using System.Text.Json;
using AssemblyManagerPlugin.Core;
using AssemblyManagerPlugin.Services;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Runtime.InProcess;

internal static partial class Program
{
    private static void RunInputComponentAdditionScenario(RhinoCore core, RegressionServices services,
        string scenario, List<RhinoDoc> fixtureDocs)
    {
        if (scenario == "input-addition-rejections")
        {
            RunInputComponentAdditionRejections(core, services, scenario, fixtureDocs);
            return;
        }

        var doc = RhinoDoc.Create(null);
        fixtureDocs.Add(doc);
        doc.ModelUnitSystem = UnitSystem.Inches;
        doc.ModelAbsoluteTolerance = 0.001;
        services.AutomaticallyPropagate = true;
        services.EnableLinkedAssemblies = true;
        var fixture = CreateComponentAdditionFixture(doc, services, scenario, false);
        PumpIdle(core, services);
        var multiView = scenario == "input-addition-multi-view";
        var repeated = scenario == "input-addition-staged-restart";
        var hardware = scenario == "input-addition-block-hardware";
        var matching = scenario is "input-addition-existing-part" or "input-addition-inherited-copy";
        if (multiView)
        {
            var drawing = new ComponentDrawingService(services.Repository, services.Layers,
                new DocumentActionHistorySink(services.Repository), services.Lineage, services.LinkSafety);
            Require(drawing.PlaceComponent(doc, fixture.AssemblyName, fixture.ComponentId, new Point3d(500, 200, 30)) == 3 &&
                    drawing.PlaceComponent(doc, fixture.AssemblyName, fixture.ComponentId, new Point3d(700, 400, 60)) == 3,
                "Input addition coverage must include the original copied view and two independently placed views.");
            var movedAssembly = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
            var movedGroups = movedAssembly.LinkGraph.Nodes.Where(node => node.Role == AssemblyLinkRoles.CopiedComponent)
                .SelectMany(node => doc.Objects.FindId(node.ObjectId).Attributes.GetGroupList() ?? Array.Empty<int>()).Distinct().ToArray();
            var movedIds = doc.Groups.GroupMembers(movedGroups[1]).Select(obj => obj.Id).ToArray();
            var move = Transform.Translation(-17, 43, 8) * Transform.Rotation(Math.PI / 3, Vector3d.ZAxis, Point3d.Origin);
            EnqueuePlacementTransform(doc, services, movedIds, move);
            foreach (var id in movedIds)
                Require(doc.Objects.Transform(id, move, true) != Guid.Empty, "An independent copied view must move before the input addition.");
            PumpIdle(core, services);
        }

        var before = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
        var donor = before.LinkGraph.SourceComponentInstances.Single(instance => instance.Id == fixture.TemplateInstanceId);
        var sibling = before.LinkGraph.SourceComponentInstances.Single(instance => instance.Id != donor.Id);
        var sourceGroupIndex = FindComponentFixtureGroup(doc, donor.SourceGroupId);
        var originalGroupIndex = FindComponentFixtureGroup(doc, donor.GeneratedGroupId);
        var retainedObjects = before.LinkGraph.Nodes.ToDictionary(node => node.Id, node => node.ObjectId);
        var retainedBounds = retainedObjects.Values.ToDictionary(id => id, id => doc.Objects.FindId(id).Geometry.GetBoundingBox(true));
        var groupPlacements = before.LinkGraph.Nodes.Where(node => node.Role == AssemblyLinkRoles.CopiedComponent)
            .SelectMany(node => doc.Objects.FindId(node.ObjectId).Attributes.GetGroupList() ?? Array.Empty<int>()).Distinct()
            .ToDictionary(index => index, index =>
            {
                var member = doc.Groups.GroupMembers(index)[0];
                var child = before.LinkGraph.Nodes.Single(node => node.ObjectId == member.Id);
                return before.LinkGraph.Edges.Single(edge => edge.ChildNodeId == child.Id).ParentToChildTransform.ToTransform();
            });
        var addedIds = new List<Guid> { AddInputComponentFixtureAddition(doc, services, fixture, scenario, matching, hardware) };

        if (scenario == "input-addition-mixed-origin")
        {
            RegroupComponentOccurrence(core, doc, services, fixture, donor.Id, scenario);
            var stagedBefore = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
            var groupBefore = InputFixtureGroupIds(doc, sourceGroupIndex);
            var objectCount = ComponentFixtureObjects(doc).Count;
            ExpectComponentUpdateRejection(() => services.LinkEvents.StageInputComponentAddition(doc, fixture.AssemblyName, donor.Id, addedIds[0]),
                "A component with a pending original-side update must reject an input-side addition.");
            var rejected = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
            Require(InputFixtureGroupIds(doc, sourceGroupIndex).SetEquals(groupBefore) && ComponentFixtureObjects(doc).Count == objectCount &&
                    JsonSerializer.Serialize(rejected.PendingComponentUpdates) == JsonSerializer.Serialize(stagedBefore.PendingComponentUpdates),
                "Mixed-origin rejection must preserve the original pending plan and leave the input part outside its group.");
            return;
        }

        using var originalAttributes = doc.Objects.FindId(addedIds[0]).Attributes.Duplicate();
        using var originalGeometry = doc.Objects.FindId(addedIds[0]).Geometry.Duplicate();
        var countBeforeStage = ComponentFixtureObjects(doc).Count;
        services.LinkEvents.StageInputComponentAddition(doc, fixture.AssemblyName, donor.Id, addedIds[0]);
        var staged = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
        var pending = staged.PendingComponentUpdates.Single();
        Require(pending.AdditionOrigin == ComponentAdditionOrigins.Input && pending.TemplateInstanceId == donor.Id &&
                pending.RegroupedGroupId == donor.SourceGroupId && pending.AddedObjectIds.SequenceEqual(addedIds) &&
                pending.InstanceIds.SequenceEqual(new[] { donor.Id }),
            "The input plan must remember its origin, existing input group, selected occurrence and actual operator UUID.");
        Require(InputFixtureGroupIds(doc, sourceGroupIndex).Contains(addedIds[0]) &&
                !InputFixtureGroupIds(doc, originalGroupIndex).Contains(addedIds[0]) &&
                staged.LinkGraph.SourceComponentInstances.Single(instance => instance.Id == donor.Id).GeneratedGroupId == donor.GeneratedGroupId,
            "Staging must add the selected object only to the existing input group without rebinding the original group.");
        Require(staged.LinkGraph.Nodes.Count == before.LinkGraph.Nodes.Count && ComponentFixtureObjects(doc).Count == countBeforeStage,
            "Staging an input addition must not create or register any downstream counterpart.");
        AssertInputAdditionPreserved(doc, addedIds[0], originalAttributes, originalGeometry);
        PumpIdle(core, services);
        var idle = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
        Require(idle.PendingComponentUpdates.Count == 1 && idle.LinkGraph.Nodes.Count == before.LinkGraph.Nodes.Count &&
                ComponentFixtureObjects(doc).Count == countBeforeStage,
            "Even with automatic propagation on, the input addition must wait for an explicit Update Assembly.");

        if (scenario is "input-addition-stale-membership" or "input-addition-unknown-origin")
        {
            var validStoreJson = doc.Strings.GetValue(AssemblyManagerConstants.StoreSection, AssemblyManagerConstants.StoreEntry);
            try
            {
                using (AssemblyLinkMutationGate.Enter())
                {
                    if (scenario.EndsWith("stale-membership", StringComparison.Ordinal))
                    {
                        var unexpectedId = AddInputComponentFixtureAddition(doc, services, fixture, scenario, false, false);
                        Require(doc.Groups.AddToGroup(sourceGroupIndex, unexpectedId), "An unapproved later input-group addition must be created.");
                    }
                    else
                    {
                        var store = services.Repository.Load(doc);
                        store.FindAssembly(fixture.AssemblyName)!.PendingComponentUpdates.Single().AdditionOrigin = "UnknownFutureOrigin";
                        // Deliberately model unsupported persisted metadata: production Save
                        // correctly refuses this origin before it can reach the document.
                        doc.Strings.SetString(AssemblyManagerConstants.StoreSection, AssemblyManagerConstants.StoreEntry,
                            JsonSerializer.Serialize(store));
                    }
                }
                var countBeforeFailure = ComponentFixtureObjects(doc).Count;
                ExpectComponentUpdateRejection(() => services.LinkEvents.UpdateAssembly(doc, fixture.AssemblyName),
                    "A changed source membership or unknown input-plan origin must fail closed at apply time.");
                var failed = JsonSerializer.Deserialize<AssemblyStore>(doc.Strings.GetValue(
                    AssemblyManagerConstants.StoreSection, AssemblyManagerConstants.StoreEntry),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!.FindAssembly(fixture.AssemblyName)!;
                Require(failed.PendingComponentUpdates.Count == 1 && failed.LinkGraph.Nodes.Count == before.LinkGraph.Nodes.Count &&
                        ComponentFixtureObjects(doc).Count == countBeforeFailure,
                    "Failed input-plan validation must keep the pending addition without creating partial counterparts.");
            }
            finally
            {
                // Fixture documents remain open for the entire run. Leave subsequent
                // cross-document event/safety checks a readable store, even on failure.
                if (scenario == "input-addition-unknown-origin")
                {
                    using var mutation = AssemblyLinkMutationGate.Enter();
                    doc.Strings.SetString(AssemblyManagerConstants.StoreSection, AssemblyManagerConstants.StoreEntry,
                        validStoreJson);
                }
            }
            return;
        }

        if (repeated)
        {
            addedIds.Add(AddInputComponentFixtureAddition(doc, services, fixture, scenario, false, false));
            services.LinkEvents.StageInputComponentAddition(doc, fixture.AssemblyName, donor.Id, addedIds[1]);
            var secondStage = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
            Require(secondStage.PendingComponentUpdates.Count == 1 &&
                    secondStage.PendingComponentUpdates.Single().AddedObjectIds.ToHashSet().SetEquals(addedIds),
                "Repeated AddPartToComponent calls must accumulate additions in one pending occurrence plan.");
            var restored = JsonSerializer.Deserialize<PendingComponentUpdateRecord>(JsonSerializer.Serialize(secondStage.PendingComponentUpdates.Single()))!;
            Require(restored.AdditionOrigin == ComponentAdditionOrigins.Input && restored.AddedObjectIds.ToHashSet().SetEquals(addedIds),
                "The staged origin and all selected source UUIDs must survive metadata serialization.");
            services.LinkEvents.Stop();
            var restarted = new RegressionServices { AutomaticallyPropagate = true };
            restarted.LinkEvents.Start();
            try
            {
                PumpIdle(core, restarted);
                Require(restarted.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!.PendingComponentUpdates.Single()
                            .AddedObjectIds.ToHashSet().SetEquals(addedIds) &&
                        restarted.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!.LinkGraph.Nodes.Count == before.LinkGraph.Nodes.Count,
                    "Restarting listeners must retain input additions without automatically committing them.");
                restarted.LinkEvents.UpdateAssembly(doc, fixture.AssemblyName);
                PumpIdle(core, restarted);
            }
            finally
            {
                restarted.LinkEvents.Stop();
                services.LinkEvents.Start();
            }
        }
        else
        {
            services.LinkEvents.UpdateAssembly(doc, fixture.AssemblyName);
            PumpIdle(core, services);
        }

        var after = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
        var updatedDonor = after.LinkGraph.SourceComponentInstances.Single(instance => instance.Id == donor.Id);
        var updatedSibling = after.LinkGraph.SourceComponentInstances.Single(instance => instance.Id == sibling.Id);
        Require(after.PendingComponentUpdates.Count == 0 && after.Components.Count == 2 &&
                updatedDonor.ComponentId != fixture.ComponentId && updatedSibling.ComponentId == fixture.ComponentId &&
                updatedSibling.SourceNodeIds.SequenceEqual(sibling.SourceNodeIds) && after.Components.All(component => component.Quantity == 1),
            "Only the edited physical input occurrence must recategorize; its identical sibling must remain unchanged.");
        Require(updatedDonor.SourceGroupId == donor.SourceGroupId && updatedDonor.GeneratedGroupId == donor.GeneratedGroupId &&
                updatedDonor.SourceNodeIds.Count == donor.SourceNodeIds.Count + addedIds.Count,
            "An input addition must preserve both existing group identities and register each new source once.");
        foreach (var (nodeId, objectId) in retainedObjects)
        {
            Require(after.LinkGraph.Nodes.Single(node => node.Id == nodeId).ObjectId == objectId,
                "Adding input parts must preserve every retained linked object's UUID.");
            var bounds = doc.Objects.FindId(objectId).Geometry.GetBoundingBox(true);
            Require(bounds.Min.DistanceTo(retainedBounds[objectId].Min) < 0.001 && bounds.Max.DistanceTo(retainedBounds[objectId].Max) < 0.001,
                "An input addition must leave all retained source, original, drawing and flat geometry in place.");
        }
        AssertInputAdditionPreserved(doc, addedIds[0], originalAttributes, originalGeometry);
        var addedSources = after.LinkGraph.Nodes.Where(node => addedIds.Contains(node.ObjectId) && node.Role == AssemblyLinkRoles.Source).ToList();
        Require(addedSources.Count == addedIds.Count && addedSources.All(node => node.SourceComponentInstanceId == donor.Id &&
                    node.ComponentId == updatedDonor.ComponentId && updatedDonor.SourceNodeIds.Contains(node.Id)),
            "The actual user-selected input UUIDs, not replacement copies, must become the new registered sources.");
        foreach (var source in addedSources)
        {
            var originalEdge = after.LinkGraph.Edges.Single(edge => edge.ParentNodeId == source.Id &&
                after.LinkGraph.Nodes.Any(node => node.Id == edge.ChildNodeId && node.Role is AssemblyLinkRoles.OriginalAssembly or AssemblyLinkRoles.Hardware));
            var original = after.LinkGraph.Nodes.Single(node => node.Id == originalEdge.ChildNodeId);
            Require(InputFixtureGroupIds(doc, originalGroupIndex).Contains(original.ObjectId),
                "Each new generated original must join the existing original component group.");
            AssertPlacementMatrix(fixture.SourceToOriginal, originalEdge.ParentToChildTransform.ToTransform(),
                "New original geometry must use the selected occurrence's recorded forward input placement.");
            using var expectedOriginal = doc.Objects.FindId(source.ObjectId).Geometry.Duplicate();
            Require(expectedOriginal.Transform(fixture.SourceToOriginal), "The recorded input placement must transform the new part.");
            AssertComponentFixtureGeometryBounds(expectedOriginal, doc.Objects.FindId(original.ObjectId).Geometry,
                "New original geometry must land in the selected assembly occurrence's existing frame.");
            var copies = after.LinkGraph.Edges.Where(edge => edge.ParentNodeId == original.Id)
                .Select(edge => after.LinkGraph.Nodes.Single(node => node.Id == edge.ChildNodeId))
                .Where(node => node.Role == AssemblyLinkRoles.CopiedComponent).ToList();
            Require(copies.Count == groupPlacements.Count, "Every existing copied drawing view must receive exactly one new counterpart.");
            foreach (var (groupIndex, placement) in groupPlacements)
            {
                var members = doc.Groups.GroupMembers(groupIndex);
                var copy = copies.Single(node => members.Any(obj => obj.Id == node.ObjectId));
                var edge = after.LinkGraph.Edges.Single(item => item.ChildNodeId == copy.Id);
                AssertPlacementMatrix(placement, edge.ParentToChildTransform.ToTransform(),
                    "The new counterpart must retain the independent placement of its own copied view.");
                using var expectedCopy = expectedOriginal.Duplicate();
                Require(expectedCopy.Transform(placement), "The copied view's current placement must transform the new original.");
                AssertComponentFixtureGeometryBounds(expectedCopy, doc.Objects.FindId(copy.ObjectId).Geometry,
                    "A new input part must reach each drawing view at its final moved and rotated location.");
            }
            foreach (var node in new[] { source, original }.Concat(copies))
                Require(MaterialAssignment.GetCategorizationMaterialId(doc.Objects.FindId(node.ObjectId).Attributes) == (hardware ? "STEEL" : "BIRCH"),
                    "New linked source, original and copied geometry must carry the selected part's material.");
            if (hardware)
                Require(new[] { source, original }.Concat(copies).All(node => doc.Objects.FindId(node.ObjectId) is InstanceObject &&
                            HardwareMetadata.HasHardwareRole(doc.Objects.FindId(node.ObjectId).Attributes)),
                    "Input hardware additions must remain marked whole block instances through every generated view.");
        }
        if (hardware)
        {
            var record = after.Hardware.Single();
            Require(record.SourceObjectId == addedIds[0] && record.Quantity == 1 && after.Parts.Count == before.Parts.Count &&
                    after.Parts.Sum(part => part.Quantity) == before.Parts.Sum(part => part.Quantity),
                "Hardware must record its real input UUID once without inflating manufacturing part quantities.");
            var bom = new BomService(services.Repository, new DocumentActionHistorySink(services.Repository)).GenerateBom(doc, fixture.AssemblyName);
            Require(bom.Lines.Single(line => line.Category == "Hardware").Quantity == 1,
                "The added hardware must appear exactly once in the BOM, regardless of drawing copies.");
        }
        else
        {
            var category = after.Parts.Single(part => part.Id == addedSources[0].PartId);
            Require(after.Parts.Sum(part => part.Quantity) == before.Parts.Sum(part => part.Quantity) + addedIds.Count &&
                    after.Parts.Count == before.Parts.Count + (matching ? 0 : 1) &&
                    category.Quantity == (matching ? 3 : addedIds.Count) && (!matching || category.Id == fixture.FirstPartId),
                "Input additions must reuse matching part categories and count actual physical parts, never drawing views.");
        }
        foreach (var groupIndex in groupPlacements.Keys)
            Require(doc.Groups.GroupMembers(groupIndex).Length == donor.SourceNodeIds.Count + addedIds.Count,
                "All original copied groups must retain their IDs and gain exactly the approved input members.");
        Require(after.LinkGraph.Conflicts.All(conflict => conflict.Status != AssemblyLinkStatuses.Open),
            "Approved input membership additions must complete without stale group-change or link issues.");
        AssertSynchronizedFlatOutputs(doc, services, after);
        var completedIds = after.LinkGraph.Nodes.Select(node => node.ObjectId).OrderBy(id => id).ToArray();
        var completedQuantities = after.Parts.ToDictionary(part => part.Id, part => part.Quantity);
        services.LinkEvents.UpdateAssembly(doc, fixture.AssemblyName);
        PumpIdle(core, services);
        var again = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
        Require(again.LinkGraph.Nodes.Select(node => node.ObjectId).OrderBy(id => id).SequenceEqual(completedIds) &&
                again.Parts.All(part => completedQuantities.GetValueOrDefault(part.Id) == part.Quantity) && again.Hardware.Count == after.Hardware.Count,
            "A repeated manual update must not duplicate any approved input addition or alter its quantities.");
    }

    private static void RunInputComponentAdditionRejections(RhinoCore core, RegressionServices services,
        string scenario, List<RhinoDoc> fixtureDocs)
    {
        foreach (var invalid in new[] { "source", "original", "curve", "other-input-group", "shared-input-group", "disabled" })
        {
            var doc = RhinoDoc.Create(null);
            fixtureDocs.Add(doc);
            doc.ModelUnitSystem = UnitSystem.Inches;
            doc.ModelAbsoluteTolerance = 0.001;
            services.EnableLinkedAssemblies = true;
            services.AutomaticallyPropagate = true;
            var fixture = CreateComponentAdditionFixture(doc, services, scenario + "-" + invalid, false);
            PumpIdle(core, services);
            var store = services.Repository.Load(doc);
            var before = store.FindAssembly(fixture.AssemblyName)!;
            var donor = before.LinkGraph.SourceComponentInstances.Single(instance => instance.Id == fixture.TemplateInstanceId);
            var sibling = before.LinkGraph.SourceComponentInstances.Single(instance => instance.Id != donor.Id);
            var groupIndex = FindComponentFixtureGroup(doc, donor.SourceGroupId);
            Guid selectedId;
            using (AssemblyLinkMutationGate.Enter())
            {
                selectedId = invalid switch
                {
                    "source" => before.LinkGraph.Nodes.First(node => node.Role == AssemblyLinkRoles.Source).ObjectId,
                    "original" => before.LinkGraph.Nodes.First(node => node.Role == AssemblyLinkRoles.OriginalAssembly).ObjectId,
                    "curve" => doc.Objects.AddLine(Point3d.Origin, new Point3d(2, 3, 4)),
                    _ => AddInputComponentFixtureAddition(doc, services, fixture, scenario, false, false)
                };
                if (invalid == "other-input-group")
                    Require(doc.Groups.AddToGroup(FindComponentFixtureGroup(doc, sibling.SourceGroupId), selectedId),
                        "The rejected input part must belong to another managed input group.");
                if (invalid == "shared-input-group")
                {
                    sibling.SourceGroupId = donor.SourceGroupId;
                    sibling.SourceGroupIndex = donor.SourceGroupIndex;
                    services.Repository.Save(doc, store);
                }
            }
            Require(selectedId != Guid.Empty, "The rejected selection fixture must exist.");
            var groupBefore = InputFixtureGroupIds(doc, groupIndex);
            var selectedGroupsBefore = (doc.Objects.FindId(selectedId).Attributes.GetGroupList() ?? Array.Empty<int>()).OrderBy(index => index).ToArray();
            var countBefore = ComponentFixtureObjects(doc).Count;
            if (invalid == "disabled")
                services.EnableLinkedAssemblies = false;
            try
            {
                ExpectComponentUpdateRejection(() => services.LinkEvents.StageInputComponentAddition(doc, fixture.AssemblyName, donor.Id, selectedId),
                    "Unsafe or disabled input addition must fail before mutation: " + invalid);
            }
            finally
            {
                services.EnableLinkedAssemblies = true;
            }
            var after = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
            Require(after.PendingComponentUpdates.Count == 0 && after.LinkGraph.Nodes.Count == before.LinkGraph.Nodes.Count &&
                    InputFixtureGroupIds(doc, groupIndex).SetEquals(groupBefore) && ComponentFixtureObjects(doc).Count == countBefore &&
                    (doc.Objects.FindId(selectedId).Attributes.GetGroupList() ?? Array.Empty<int>()).OrderBy(index => index).SequenceEqual(selectedGroupsBefore),
                "A rejected input addition must preserve geometry, group memberships and registered graph: " + invalid);
        }
    }

    private static Guid AddInputComponentFixtureAddition(RhinoDoc doc, RegressionServices services,
        ComponentAdditionFixture fixture, string scenario, bool matching, bool hardware)
    {
        using var mutation = AssemblyLinkMutationGate.Enter();
        var originalSpaceId = AddComponentFixtureAddition(doc, services, fixture,
            scenario == "input-addition-inherited-copy" ? scenario + "-copied-member" : scenario, matching, hardware);
        Require(fixture.SourceToOriginal.TryGetInverse(out var toInput), "Fixture original placement must be invertible.");
        var inputId = doc.Objects.Transform(originalSpaceId, toInput, true);
        Require(inputId != Guid.Empty, "The input addition must be placed in the existing input group's coordinate frame.");
        using var attributes = doc.Objects.FindId(inputId).Attributes.Duplicate();
        attributes.Name = "Operator input addition";
        attributes.SetUserString("InputAdditionRegression", "Retain this operator value");
        Require(doc.Objects.ModifyAttributes(inputId, attributes, true), "Input addition attributes must be assigned.");
        return inputId;
    }

    private static HashSet<Guid> InputFixtureGroupIds(RhinoDoc doc, int groupIndex) =>
        (doc.Groups.GroupMembers(groupIndex) ?? Array.Empty<RhinoObject>()).Select(obj => obj.Id).ToHashSet();

    private static void AssertInputAdditionPreserved(RhinoDoc doc, Guid id, ObjectAttributes attributes, GeometryBase geometry)
    {
        var obj = doc.Objects.FindId(id);
        Require(obj is { IsDeleted: false } && obj.Attributes.LayerIndex == attributes.LayerIndex && obj.Attributes.Name == attributes.Name &&
                obj.Attributes.GetUserString("InputAdditionRegression") == attributes.GetUserString("InputAdditionRegression"),
            "Adding an existing input part must preserve its UUID, user layer, name and unrelated user metadata.");
        AssertComponentFixtureGeometryBounds(geometry, obj.Geometry, "Adding an input part must preserve its operator-defined geometry and position.");
    }
}
