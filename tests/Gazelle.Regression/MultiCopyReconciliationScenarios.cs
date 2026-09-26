using AssemblyManagerPlugin.Core;
using AssemblyManagerPlugin.Services;
using Rhino;
using Rhino.Geometry;
using Rhino.Runtime.InProcess;

internal static partial class Program
{
    private static void RunMultiCopyReconciliationScenario(RhinoCore core, RegressionServices services,
        string scenario, List<RhinoDoc> fixtureDocs)
    {
        if (scenario == "multi-copy-component-addition")
        {
            RunMultiCopyComponentAdditionScenario(core, services, scenario, fixtureDocs);
            return;
        }
        var doc = RhinoDoc.Create(null);
        fixtureDocs.Add(doc);
        doc.ModelUnitSystem = UnitSystem.Inches;
        doc.ModelAbsoluteTolerance = 0.001;
        services.AutomaticallyPropagate = true;
        services.EnableLinkedAssemblies = true;
        var repeated = scenario == "multi-copy-same-occurrence";
        var merged = scenario == "multi-copy-category-merge";
        Require(repeated || merged || scenario == "multi-copy-missing-category",
            "The multi-copy reconciliation scenario must be recognized.");
        var assembly = new AssemblyRecord { Name = "Regression-" + scenario, NextPartSequence = 3, NextComponentSequence = 3 };
        var firstPart = new PartRecord { Name = "P01", Quantity = 1, MaterialThickness = 1 };
        var secondPart = new PartRecord { Name = "P02", Quantity = 1, MaterialThickness = 1 };
        var copyIds = new List<Guid>();
        Guid firstSource;
        using (AssemblyLinkMutationGate.Enter())
        {
            assembly.Parts.Add(firstPart);
            firstSource = AddFixtureOccurrence(doc, services, assembly, firstPart, 0, 10);
            AddLayerFixtureComponent(doc, services, assembly, "C01", new[] { firstSource });
            if (!repeated)
            {
                assembly.Parts.Add(secondPart);
                var secondSource = AddFixtureOccurrence(doc, services, assembly, secondPart, 40, 15);
                AddLayerFixtureComponent(doc, services, assembly, "C02", new[] { secondSource });
            }
            foreach (var original in assembly.LinkGraph.Nodes.Where(node => node.Role == AssemblyLinkRoles.OriginalAssembly).ToList())
            {
                if (!merged && original.PartId != firstPart.Id)
                    continue;
                var part = assembly.Parts.Single(item => item.Id == original.PartId);
                var component = assembly.Components.Single(item => item.Id == original.ComponentId);
                for (var view = 0; view < (repeated ? 2 : 1); view++)
                {
                    var id = AddLayerFixtureOutput(doc, services, assembly, original.ObjectId, AssemblyLinkRoles.CopiedComponent,
                        LayerService.CopiedComponentPart(assembly.Name, component.Name, part.Name),
                        Transform.Translation(view * 100, 100, 0));
                    copyIds.Add(id);
                    Require(doc.Groups.Add($"{assembly.Name}-{component.Name}-view-{view}", new[] { id }) >= 0,
                        "Each fixture drawing view must have its own distinct group.");
                }
            }
            services.Repository.Save(doc, new AssemblyStore { Assemblies = new List<AssemblyRecord> { assembly } });
        }
        PumpIdle(core, services);
        if (merged)
        {
            using var replacement = BoxAt(0, 15);
            Require(doc.Objects.Replace(firstSource, replacement), "The first design source must change to the second component's design.");
            PumpIdle(core, services);
        }
        var after = services.Repository.Load(doc).FindAssembly(assembly.Name)!;
        var result = new AssemblyCategorizationReconciliationService(services.Fingerprints, services.Layers, services.Lineage)
            .Reconcile(doc, after);
        Require(result.RequiresCopiedComponentRebuild == (!repeated && !merged),
            "Copied output coverage must warn only for a missing component category, not valid extra views.");
        Require(copyIds.All(id => doc.Objects.FindId(id) is { IsDeleted: false }) &&
                after.LinkGraph.Nodes.Count(node => node.Role == AssemblyLinkRoles.CopiedComponent) == copyIds.Count,
            "Reconciliation must preserve every existing drawing object and its independent link.");
        if (merged || repeated)
        {
            Require(after.Components.Count == 1 && after.Components.Single().Quantity == (merged ? 2 : 1),
                "Drawing views must not inflate the count of physical component occurrences.");
            var copies = after.LinkGraph.Nodes.Where(node => node.Role == AssemblyLinkRoles.CopiedComponent).ToList();
            Require(copies.Count == 2 && copies.Select(node => node.SourceComponentInstanceId).Distinct().Count() == (merged ? 2 : 1),
                "Same-occurrence views and category-merged occurrence views must retain their own source lineage.");
            Require(copies.All(node => node.ComponentId == after.Components.Single().Id),
                "All copied views must inherit the final component category without retiring their identities.");
        }
    }

    private static void RunMultiCopyComponentAdditionScenario(RhinoCore core, RegressionServices services,
        string scenario, List<RhinoDoc> fixtureDocs)
    {
        var doc = RhinoDoc.Create(null);
        fixtureDocs.Add(doc);
        doc.ModelUnitSystem = UnitSystem.Inches;
        doc.ModelAbsoluteTolerance = 0.001;
        services.AutomaticallyPropagate = true;
        services.EnableLinkedAssemblies = true;
        var fixture = CreateComponentAdditionFixture(doc, services, scenario, false);
        PumpIdle(core, services);
        var drawing = new ComponentDrawingService(services.Repository, services.Layers,
            new DocumentActionHistorySink(services.Repository), services.Lineage, services.LinkSafety);
        Require(drawing.PlaceComponent(doc, fixture.AssemblyName, fixture.ComponentId, new Point3d(500, 200, 30)) == 3 &&
                drawing.PlaceComponent(doc, fixture.AssemblyName, fixture.ComponentId, new Point3d(700, 400, 60)) == 3,
            "The additive scenario must begin with two new complete views of the representative occurrence.");
        var beforeMove = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
        var existingCopies = beforeMove.LinkGraph.Nodes.Where(node => node.Role == AssemblyLinkRoles.CopiedComponent).ToList();
        var groups = existingCopies.SelectMany(node => doc.Objects.FindId(node.ObjectId).Attributes.GetGroupList() ?? Array.Empty<int>())
            .Distinct().ToArray();
        Require(groups.Length == 3, "The existing view and two placements must have three independent groups.");
        var movedIds = (doc.Groups.GroupMembers(groups[1]) ?? Array.Empty<Rhino.DocObjects.RhinoObject>()).Select(obj => obj.Id).ToArray();
        var move = Transform.Translation(-17, 43, 8) * Transform.Rotation(Math.PI / 3, Vector3d.ZAxis, Point3d.Origin);
        EnqueuePlacementTransform(doc, services, movedIds, move);
        foreach (var id in movedIds)
            Require(doc.Objects.Transform(id, move, true) != Guid.Empty, "One complete component view must move independently before staging.");
        PumpIdle(core, services);
        var before = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
        var sibling = before.LinkGraph.SourceComponentInstances.Single(instance => instance.Id != fixture.TemplateInstanceId);
        var preservedCopyIds = before.LinkGraph.Nodes.Where(node => node.Role == AssemblyLinkRoles.CopiedComponent)
            .ToDictionary(node => node.Id, node => node.ObjectId);
        var groupPlacements = groups.ToDictionary(index => index, index =>
        {
            var member = doc.Groups.GroupMembers(index)[0];
            var child = before.LinkGraph.Nodes.Single(node => node.ObjectId == member.Id);
            return before.LinkGraph.Edges.Single(edge => edge.ChildNodeId == child.Id).ParentToChildTransform.ToTransform();
        });
        var originalAddition = RegroupComponentOccurrence(core, doc, services, fixture, fixture.TemplateInstanceId, scenario);
        services.LinkEvents.UpdateAssembly(doc, fixture.AssemblyName);
        PumpIdle(core, services);
        var updated = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
        var editedInstance = updated.LinkGraph.SourceComponentInstances.Single(instance => instance.Id == fixture.TemplateInstanceId);
        var unchangedInstance = updated.LinkGraph.SourceComponentInstances.Single(instance => instance.Id == sibling.Id);
        Require(updated.PendingComponentUpdates.Count == 0 && updated.Components.Count == 2 &&
                editedInstance.ComponentId != fixture.ComponentId && unchangedInstance.ComponentId == fixture.ComponentId &&
                unchangedInstance.SourceNodeIds.SequenceEqual(sibling.SourceNodeIds) &&
                editedInstance.SourceNodeIds.Count == 4,
            "An addition must split only its physical occurrence while leaving its identical sibling unchanged.");
        Require(updated.Components.All(component => component.Quantity == 1) && updated.Parts.Sum(part => part.Quantity) == 7,
            "Three drawing views must not multiply the new physical part or component quantity.");
        foreach (var (nodeId, objectId) in preservedCopyIds)
            Require(updated.LinkGraph.Nodes.Single(node => node.Id == nodeId).ObjectId == objectId,
                "Staged additions must preserve all existing copied-object identities across saved reconciliation.");
        var addedOriginal = updated.LinkGraph.Nodes.Single(node => node.ObjectId == originalAddition);
        var addedCopies = updated.LinkGraph.Edges.Where(edge => edge.ParentNodeId == addedOriginal.Id)
            .Select(edge => updated.LinkGraph.Nodes.Single(node => node.Id == edge.ChildNodeId))
            .Where(node => node.Role == AssemblyLinkRoles.CopiedComponent).ToList();
        Require(addedCopies.Count == 3, "Exactly one new member must reach each of the three complete drawing views.");
        foreach (var groupIndex in groups)
        {
            var members = doc.Groups.GroupMembers(groupIndex) ?? Array.Empty<Rhino.DocObjects.RhinoObject>();
            Require(members.Length == 4, "Every existing view group must retain its group ID and gain exactly the one new member.");
            var newObject = members.Single(obj => addedCopies.Any(node => node.ObjectId == obj.Id));
            var newNode = addedCopies.Single(node => node.ObjectId == newObject.Id);
            var edge = updated.LinkGraph.Edges.Single(item => item.ChildNodeId == newNode.Id);
            AssertPlacementMatrix(groupPlacements[groupIndex], edge.ParentToChildTransform.ToTransform(),
                "The added member must inherit this view's own saved placement, including its independent manual move.");
            using var expected = doc.Objects.FindId(originalAddition).Geometry.Duplicate();
            Require(expected.Transform(groupPlacements[groupIndex]), "The added member's expected drawing placement must be valid.");
            AssertComponentFixtureGeometryBounds(expected, newObject.Geometry,
                "The new member must land in each independently placed component frame.");
            Require(members.All(obj => updated.LinkGraph.Nodes.Any(node => node.ObjectId == obj.Id &&
                    node.ComponentId == editedInstance.ComponentId && node.SourceComponentInstanceId == fixture.TemplateInstanceId)),
                "All members of each view must follow the edited original occurrence's new component category.");
        }
    }
}
