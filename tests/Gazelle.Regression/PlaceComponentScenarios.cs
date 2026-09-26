using AssemblyManagerPlugin.Core;
using AssemblyManagerPlugin.Services;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Runtime.InProcess;

internal static partial class Program
{
    private static void RunPlaceComponentScenario(RhinoCore core, RegressionServices services, string scenario, List<RhinoDoc> fixtureDocs)
    {
        var doc = RhinoDoc.Create(null);
        fixtureDocs.Add(doc);
        doc.ModelUnitSystem = UnitSystem.Inches;
        doc.ModelAbsoluteTolerance = 0.001;
        services.AutomaticallyPropagate = true;
        services.EnableLinkedAssemblies = true;
        var firstView = scenario == "place-component-first-view";
        var fixture = CreateComponentAdditionFixture(doc, services, scenario, ambiguous: firstView, singleton: true);
        PumpIdle(core, services);
        var drawing = new ComponentDrawingService(services.Repository, services.Layers,
            new DocumentActionHistorySink(services.Repository), services.Lineage, services.LinkSafety);
        if (scenario == "place-component-validation")
        {
            RunPlaceComponentValidation(doc, services, fixture, drawing);
            return;
        }
        if (scenario == "place-component-safety")
        {
            RunPlaceComponentSafety(doc, services, fixture, drawing);
            return;
        }
        if (!firstView)
        {
            var assembly = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
            var instance = assembly.LinkGraph.SourceComponentInstances.Single();
            var originals = assembly.LinkGraph.Nodes.Where(node => node.Role == AssemblyLinkRoles.OriginalAssembly)
                .Select(node => node.ObjectId).ToList();
            Require(doc.Groups.Delete(FindComponentFixtureGroup(doc, instance.GeneratedGroupId)), "The placement fixture must ungroup.");
            originals.Add(AddComponentFixtureAddition(doc, services, fixture, "component-add-block-hardware", false, true));
            var regrouped = doc.Groups.Add("PlaceComponent hardware fixture", originals);
            services.LinkEvents.StageComponentUpdate(doc, fixture.AssemblyName, fixture.ComponentId, doc.Groups.FindIndex(regrouped).Id);
            services.LinkEvents.UpdateAssembly(doc, fixture.AssemblyName);
            PumpIdle(core, services);
        }
        var before = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
        var beforeIds = before.LinkGraph.Nodes.Select(node => node.Id).ToHashSet();
        var beforeParts = before.Parts.ToDictionary(part => part.Id, part => part.Quantity);
        var beforeHardware = before.Hardware.Sum(hardware => hardware.Quantity);
        var component = before.Components.Single();
        var memberCount = component.PartQuantities.Values.Sum();
        var anchors = new[] { new Point3d(20, 400, 12), new Point3d(110, 650, 23) };
        foreach (var anchor in anchors)
        {
            var oldIds = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!.LinkGraph.Nodes.Select(node => node.Id).ToHashSet();
            Require(drawing.PlaceComponent(doc, fixture.AssemblyName, component.Id, anchor) == memberCount,
                "PlaceComponent must copy every member of exactly one original occurrence.");
            var placed = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
            var view = placed.LinkGraph.Nodes.Where(node => !oldIds.Contains(node.Id)).ToArray();
            Require(view.Length == memberCount && view.All(node => node.Role == AssemblyLinkRoles.CopiedComponent &&
                    node.SourceComponentInstanceId == fixture.TemplateInstanceId && !beforeIds.Contains(node.Id)),
                "Every new object must receive its own copied-component lineage node tied to the representative source occurrence.");
            var bounds = BoundingBox.Empty;
            foreach (var node in view)
                bounds.Union(doc.Objects.FindId(node.ObjectId).Geometry.GetBoundingBox(true));
            Require(bounds.Center.DistanceTo(anchor) < 0.001, "The final oriented component bounds must be centered at the selected point.");
            var groups = view.SelectMany(node => doc.Objects.FindId(node.ObjectId).Attributes.GetGroupList() ?? Array.Empty<int>()).Distinct().ToArray();
            Require(groups.Length == 1 && (doc.Groups.GroupMembers(groups[0]) ?? Array.Empty<RhinoObject>()).Select(obj => obj.Id)
                    .ToHashSet().SetEquals(view.Select(node => node.ObjectId)),
                "Each placement must create one independent, complete Rhino group with no source or other-view members.");
            foreach (var node in view)
            {
                var edge = placed.LinkGraph.Edges.Single(item => item.ChildNodeId == node.Id);
                var parent = placed.LinkGraph.Nodes.Single(item => item.Id == edge.ParentNodeId);
                Require(parent.Role is AssemblyLinkRoles.OriginalAssembly or AssemblyLinkRoles.Hardware && parent.PartId == node.PartId,
                    "New copies must derive directly from original members, retaining manufacturing versus hardware identity.");
                Require(doc.Objects.FindId(node.ObjectId).Attributes.GetUserString(AssemblyManagerConstants.LinkNodeIdUserString) == node.Id.ToString("D"),
                    "Recovery metadata must refer to the fresh node, never an existing copied view.");
            }
        }
        var copies = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
        var newNodes = copies.LinkGraph.Nodes.Where(node => !beforeIds.Contains(node.Id)).ToArray();
        Require(copies.Components.Single().Quantity == component.Quantity && copies.LinkGraph.SourceComponentInstances.Count == before.LinkGraph.SourceComponentInstances.Count &&
                copies.Parts.All(part => beforeParts[part.Id] == part.Quantity) && copies.Hardware.Sum(hardware => hardware.Quantity) == beforeHardware,
            "Additional drawing views must not add source occurrences or increase manufacturing/BOM quantities.");
        if (firstView)
        {
            Require(before.LinkGraph.Nodes.All(node => node.Role != AssemblyLinkRoles.CopiedComponent) && newNodes.Length == 2 * memberCount,
                "PlaceComponent must also work before Copy / Orient Components has ever been run.");
            return;
        }

        var movedNodes = newNodes.Take(memberCount).ToArray();
        var move = Transform.Translation(41, -26, 8) * Transform.Rotation(Math.PI / 4, Vector3d.ZAxis, anchors[0]);
        EnqueuePlacementTransform(doc, services, movedNodes.Select(node => node.ObjectId).ToArray(), move);
        foreach (var node in movedNodes)
            Require(doc.Objects.Transform(node.ObjectId, move, true) != Guid.Empty, "The entire independently placed view must move.");
        PumpIdle(core, services);
        var moved = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
        var expectedHardware = moved.LinkGraph.Nodes.Where(node => node.Role == AssemblyLinkRoles.CopiedComponent &&
            doc.Objects.FindId(node.ObjectId) is InstanceObject).ToDictionary(node => node.Id,
            node => ((InstanceObject)doc.Objects.FindId(node.ObjectId)).InstanceXform);
        Require(expectedHardware.Count == 3, "The original drawing plus two placements must each contain the block hardware.");
        var copiedRelations = moved.LinkGraph.Edges.Where(edge => moved.LinkGraph.Nodes.Any(node => node.Id == edge.ChildNodeId &&
            node.Role == AssemblyLinkRoles.CopiedComponent)).ToDictionary(edge => edge.Id, edge => edge.ParentToChildTransform.ToTransform());
        services.AutomaticallyPropagate = false;
        var source = moved.LinkGraph.Nodes.First(node => node.Role == AssemblyLinkRoles.Source && node.PartId == fixture.FirstPartId);
        using (var edited = doc.Objects.FindId(source.ObjectId).Geometry.Duplicate())
        {
            Require(edited.Transform(Transform.Scale(Plane.WorldXY, 1.2, 1, 1)), "The input design change must be valid.");
            Require(doc.Objects.Replace(source.ObjectId, (Brep)edited), "The input design change must be applied.");
        }
        using (var material = doc.Objects.FindId(source.ObjectId).Attributes.Duplicate())
        {
            MaterialAssignment.Set(material, new MaterialDefinitionRecord { Id = "MAPLE", Name = "Maple" });
            Require(doc.Objects.ModifyAttributes(source.ObjectId, material, true), "The input material change must be applied.");
        }
        services.LinkEvents.UpdateAssembly(doc, fixture.AssemblyName);
        PumpIdle(core, services);
        var updated = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
        foreach (var (edgeId, relation) in copiedRelations)
        {
            var edge = updated.LinkGraph.Edges.Single(item => item.Id == edgeId);
            var parent = updated.LinkGraph.Nodes.Single(item => item.Id == edge.ParentNodeId);
            var child = updated.LinkGraph.Nodes.Single(item => item.Id == edge.ChildNodeId);
            AssertPlacementMatrix(relation, edge.ParentToChildTransform.ToTransform(), "Updating must preserve every view's independent placement relation.");
            using var expected = doc.Objects.FindId(parent.ObjectId).Geometry.Duplicate();
            Require(expected.Transform(relation), "Expected descendant placement must be transformable.");
            AssertComponentFixtureGeometryBounds(expected, doc.Objects.FindId(child.ObjectId).Geometry,
                "Every drawing view must reflect the edited input at its own saved placement.");
            if (child.PartId != Guid.Empty && parent.PartId == updated.LinkGraph.Nodes.Single(node => node.Id == source.Id).PartId)
                Require(MaterialAssignment.GetCategorizationMaterialId(doc.Objects.FindId(child.ObjectId).Attributes) == "MAPLE",
                    "Material edits must reach the original drawing and each additional placed view.");
        }
        foreach (var (id, matrix) in expectedHardware)
        {
            var node = updated.LinkGraph.Nodes.Single(item => item.Id == id);
            AssertPlacementMatrix(matrix, ((InstanceObject)doc.Objects.FindId(node.ObjectId)).InstanceXform,
                "Hardware in each placed view must preserve its final moved placement after updates.");
        }
        Require(updated.LinkGraph.Nodes.Count == copies.LinkGraph.Nodes.Count &&
                updated.Parts.Sum(part => part.Quantity) == before.Parts.Sum(part => part.Quantity) &&
                updated.Hardware.Sum(hardware => hardware.Quantity) == beforeHardware,
            "Refresh must neither duplicate placed views nor count drawing copies as manufactured parts.");
    }

    private static void RunPlaceComponentValidation(RhinoDoc doc, RegressionServices services, ComponentAdditionFixture fixture, ComponentDrawingService drawing)
    {
        var baseline = services.Repository.Load(doc);
        var originalJson = System.Text.Json.JsonSerializer.Serialize(baseline);
        void Reject(Action<AssemblyRecord> corrupt, string message)
        {
            var store = System.Text.Json.JsonSerializer.Deserialize<AssemblyStore>(originalJson)!;
            corrupt(store.Assemblies.Single());
            services.Repository.Save(doc, store);
            var count = ComponentFixtureObjects(doc).Count;
            var nodes = store.Assemblies.Single().LinkGraph.Nodes.Count;
            ExpectComponentUpdateRejection(() => drawing.PlaceComponent(doc, fixture.AssemblyName, fixture.ComponentId, new Point3d(300, 300, 0)), message);
            Require(ComponentFixtureObjects(doc).Count == count && services.Repository.Load(doc).Assemblies.Single().LinkGraph.Nodes.Count == nodes,
                "Rejected placement must not leave partial copies or graph entries.");
        }
        Reject(assembly => assembly.Components.Single().RepresentativeObjectIdsByPartName.Remove("P02"),
            "Incomplete representative mappings must be rejected rather than silently omit a part.");
        Reject(assembly => assembly.Components.Single().RepresentativeObjectIdsByPartName["P02"] =
            assembly.Components.Single().RepresentativeObjectIdsByPartName["P01"].ToList(),
            "Repeated representative UUIDs must be rejected rather than duplicate the wrong part.");
        Reject(assembly => assembly.PendingComponentUpdates.Add(new PendingComponentUpdateRecord { ComponentId = fixture.ComponentId }),
            "Staged additions must require Update Assembly before generating another drawing view.");
        services.Repository.Save(doc, baseline);
        var source = baseline.Assemblies.Single().LinkGraph.Nodes.First(node => node.Role == AssemblyLinkRoles.Source);
        using (AssemblyLinkMutationGate.Enter())
        using (var changed = doc.Objects.FindId(source.ObjectId).Geometry.Duplicate())
        {
            Require(changed.Transform(Transform.Scale(Point3d.Origin, 1.2)), "The stale-input test change must succeed.");
            Require(doc.Objects.Replace(source.ObjectId, (Brep)changed), "The stale-input test replacement must succeed.");
        }
        var priorCount = ComponentFixtureObjects(doc).Count;
        ExpectComponentUpdateRejection(() => drawing.PlaceComponent(doc, fixture.AssemblyName, fixture.ComponentId, new Point3d(300, 300, 0)),
            "Unobserved input edits must be rejected rather than copy stale original geometry.");
        Require(ComponentFixtureObjects(doc).Count == priorCount, "Stale-input rejection must leave all geometry unchanged.");
    }

    private static void RunPlaceComponentSafety(RhinoDoc doc, RegressionServices services, ComponentAdditionFixture fixture, ComponentDrawingService drawing)
    {
        var count = ComponentFixtureObjects(doc).Count;
        try
        {
            services.EnableLinkedAssemblies = false;
            services.LinkSafety.ObserveDocument(doc);
            ExpectComponentUpdateRejection(() => drawing.PlaceComponent(doc, fixture.AssemblyName, fixture.ComponentId, new Point3d(300, 300, 0)),
                "The emergency linking switch must block placement of new tracked copies.");
            Require(ComponentFixtureObjects(doc).Count == count, "Disabled placement must not add any objects.");
            services.EnableLinkedAssemblies = true;
            Require(drawing.PlaceComponent(doc, fixture.AssemblyName, fixture.ComponentId, new Point3d(300, 300, 0)) == 3,
                "An unchanged assembly must resume and allow placement after linking is re-enabled.");
            services.EnableLinkedAssemblies = false;
            services.LinkSafety.ObserveDocument(doc);
            var assembly = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
            var original = assembly.LinkGraph.Nodes.First(node => node.Role == AssemblyLinkRoles.OriginalAssembly);
            using (AssemblyLinkMutationGate.Enter())
                Require(doc.Objects.Transform(original.ObjectId, Transform.Translation(1, 0, 0), true) != Guid.Empty,
                    "The changed-while-disabled fixture must move.");
            services.EnableLinkedAssemblies = true;
            var changedCount = ComponentFixtureObjects(doc).Count;
            ExpectComponentUpdateRejection(() => drawing.PlaceComponent(doc, fixture.AssemblyName, fixture.ComponentId, new Point3d(600, 600, 0)),
                "An assembly changed during disabled tracking must not acquire new potentially stale views.");
            Require(ComponentFixtureObjects(doc).Count == changedCount, "Suspended placement must leave geometry untouched.");
        }
        finally
        {
            services.EnableLinkedAssemblies = true;
        }
    }
}
