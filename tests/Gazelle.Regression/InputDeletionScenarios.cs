using AssemblyManagerPlugin.Core;
using AssemblyManagerPlugin.Services;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Runtime.InProcess;

internal static partial class Program
{
    private static void RunInputDeletionScenario(RhinoCore core, RegressionServices services,
        string scenario, List<RhinoDoc> fixtureDocs)
    {
        var doc = RhinoDoc.Create(null);
        fixtureDocs.Add(doc);
        doc.ModelUnitSystem = UnitSystem.Inches;
        doc.ModelAbsoluteTolerance = 0.001;
        services.EnableLinkedAssemblies = true;
        services.AutomaticallyPropagate = scenario != "input-delete-idle-manual";
        var singleton = scenario == "input-delete-last-category";
        var hardware = scenario == "input-delete-hardware" || scenario.EndsWith("-hardware", StringComparison.Ordinal);
        var copyOrient = scenario.StartsWith("input-delete-copyorient-", StringComparison.Ordinal);
        var fixture = CreateComponentAdditionFixture(doc, services, scenario, false, singleton);
        PumpIdle(core, services);
        var bomService = new BomService(services.Repository, new DocumentActionHistorySink(services.Repository));
        Guid hardwareId = Guid.Empty;
        if (hardware)
        {
            hardwareId = AddInputComponentFixtureAddition(doc, services, fixture, scenario + "-block-hardware", false, true);
            services.LinkEvents.StageInputComponentAddition(doc, fixture.AssemblyName, fixture.TemplateInstanceId, hardwareId);
            services.LinkEvents.UpdateAssembly(doc, fixture.AssemblyName);
            PumpIdle(core, services);
            Require(bomService.GenerateBom(doc, fixture.AssemblyName).Lines.Single(line => line.Category == "Hardware").Quantity == 1,
                "Direct hardware deletion must start with a tracked block included once in the BOM.");
        }
        if (scenario == "input-delete-moved-views")
        {
            var drawing = new ComponentDrawingService(services.Repository, services.Layers,
                new DocumentActionHistorySink(services.Repository), services.Lineage, services.LinkSafety);
            Require(drawing.PlaceComponent(doc, fixture.AssemblyName, fixture.ComponentId, new Point3d(630, 290, 0)) == 3,
                "The direct deletion fixture must include a second independent drawing view.");
            var placed = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
            var copies = placed.LinkGraph.Nodes.Where(node => node.Role == AssemblyLinkRoles.CopiedComponent)
                .Select(node => node.ObjectId).ToArray();
            var move = Transform.Translation(-12, 34, 8) * Transform.Rotation(Math.PI / 9, Vector3d.ZAxis, Point3d.Origin);
            EnqueuePlacementTransform(doc, services, copies, move);
            foreach (var id in copies)
                Require(doc.Objects.Transform(id, move, true) != Guid.Empty, "Copied components must move before deletion.");
            PumpIdle(core, services);
        }

        // Normalize the fixture's legacy row headers before taking UUID/semantic
        // baselines. Refresh intentionally rebuilds headers, but must retain all
        // linked geometry, individual part labels and unrelated user objects.
        services.LinkEvents.UpdateAssembly(doc, fixture.AssemblyName);
        PumpIdle(core, services);
        if (copyOrient)
            PrepareInputDeletionCopyOrientFixture(core, doc, services, fixture, scenario);
        var before = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
        var instance = before.LinkGraph.SourceComponentInstances.Single(item => item.Id == fixture.TemplateInstanceId);
        var source = before.LinkGraph.Nodes.Single(node => instance.SourceNodeIds.Contains(node.Id) &&
            (hardware ? node.ObjectId == hardwareId : node.PartId == fixture.FirstPartId));
        var original = before.LinkGraph.Nodes.Single(node => node.SourceComponentInstanceId == instance.Id &&
            (hardware ? node.Role == AssemblyLinkRoles.Hardware : node.Role == AssemblyLinkRoles.OriginalAssembly && node.PartId == source.PartId));
        var copiedNodes = before.LinkGraph.Edges.Where(edge => edge.ParentNodeId == original.Id)
            .Select(edge => before.LinkGraph.Nodes.Single(node => node.Id == edge.ChildNodeId))
            .Where(node => node.Role == AssemblyLinkRoles.CopiedComponent).ToList();
        var retiringIds = copiedNodes.Select(node => node.ObjectId).Append(original.ObjectId).ToHashSet();
        var sourceGroupIndex = FindComponentFixtureGroup(doc, instance.SourceGroupId);
        var sourceSerial = doc.Objects.FindId(source.ObjectId).RuntimeSerialNumber;
        var beforeNodeIds = before.LinkGraph.Nodes.Select(node => node.Id).ToHashSet();
        var beforeObjectIds = InputDeletionStableObjectIds(doc, before.Id);
        var beforeHeaders = InputDeletionHeaderSignatures(doc, before.Id);
        var retainedBounds = before.LinkGraph.Nodes.ToDictionary(node => node.ObjectId,
            node => doc.Objects.FindId(node.ObjectId).Geometry.GetBoundingBox(true));
        var flat = hardware ? null : before.LinkGraph.Nodes.Single(node => node.Role == AssemblyLinkRoles.FlatPart && node.PartId == source.PartId);
        using var flatGeometry = flat is null ? null : doc.Objects.FindId(flat.ObjectId).Geometry.Duplicate();
        var oldLabelIds = flat is null ? new List<Guid>() : ComponentFixtureObjects(doc).Where(obj =>
                FlatPartAnnotations.IsOwned(obj.Attributes, before.Id, FlatPartAnnotations.PartLabel) &&
                obj.Attributes.GetUserString(FlatPartAnnotations.OutputKey) == flat.ObjectId.ToString("D"))
            .Select(obj => obj.Id).ToList();
        Guid noteId = Guid.Empty;
        if (singleton)
        {
            using var mutation = AssemblyLinkMutationGate.Enter();
            using var attributes = new ObjectAttributes
            {
                LayerIndex = services.Layers.EnsureLayerIndex(doc, $"{LayerService.PartsPart(before.Name, "P01")}::text")
            };
            using var note = new TextEntity { PlainText = "Keep this operator note", Plane = Plane.WorldXY, TextHeight = 1 };
            noteId = doc.Objects.AddText(note, attributes);
            Require(noteId != Guid.Empty, "A user annotation must exist next to the category that will retire.");
        }

        if (scenario == "input-delete-replace-protection")
        {
            using var edited = ((Brep)doc.Objects.FindId(source.ObjectId).Geometry).DuplicateBrep();
            Require(edited.Transform(Transform.Scale(edited.GetBoundingBox(true).Center, 1.15)) &&
                    doc.Objects.Replace(source.ObjectId, edited), "A real same-UUID geometry replacement must succeed.");
            PumpIdle(core, services);
            services.LinkEvents.UpdateAssembly(doc, fixture.AssemblyName);
            PumpIdle(core, services);
            var replaced = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
            Require(replaced.PendingComponentUpdates.Count == 0 && replaced.LinkGraph.Nodes.Any(node => node.Id == source.Id) &&
                    replaced.LinkGraph.SourceComponentInstances.Single(item => item.Id == instance.Id).SourceNodeIds.Count == instance.SourceNodeIds.Count &&
                    doc.Objects.FindId(source.ObjectId) is { IsDeleted: false } && retiringIds.All(id => doc.Objects.FindId(id) is { IsDeleted: false }),
                "Rhino's delete callback during Replace must never be interpreted as removal of a live tracked input member.");
            Require(!replaced.LinkGraph.Conflicts.Any(conflict => conflict.Status == AssemblyLinkStatuses.Open),
                "A supported same-UUID replacement must retain its normal geometry propagation without deletion issues.");
            return;
        }

        Guid dependentAssemblyId = Guid.Empty;
        if (scenario is "input-delete-dependent" or "input-delete-shared-source")
        {
            using var mutation = AssemblyLinkMutationGate.Enter();
            var store = services.Repository.Load(doc);
            var dependent = new AssemblyRecord { Name = fixture.AssemblyName + "-dependent" };
            dependentAssemblyId = dependent.Id;
            var consumedId = scenario == "input-delete-dependent" ? copiedNodes[0].ObjectId : source.ObjectId;
            var part = new PartRecord
            {
                Name = "P01", Quantity = 1, MaterialId = "BIRCH", CategorizationMaterialId = "BIRCH",
                SourceObjectIds = new List<Guid> { consumedId }
            };
            dependent.Parts.Add(part);
            var dependentNode = services.Lineage.EnsureNode(dependent, consumedId, AssemblyLinkRoles.Source, part.Id);
            if (scenario == "input-delete-shared-source")
            {
                var component = new ComponentRecord { Name = "C01" };
                dependent.Components.Add(component);
                var shared = new SourceComponentInstanceRecord
                {
                    ComponentId = component.Id, SourceGroupId = instance.SourceGroupId,
                    SourceGroupIndex = sourceGroupIndex, SourceGroupName = instance.SourceGroupName,
                    SourceNodeIds = new List<Guid> { dependentNode.Id }
                };
                dependentNode.ComponentId = component.Id;
                dependentNode.SourceComponentInstanceId = shared.Id;
                dependent.LinkGraph.SourceComponentInstances.Add(shared);
            }
            store.Assemblies.Add(dependent);
            services.Repository.Save(doc, store);
        }

        using var deletedAttributes = doc.Objects.FindId(source.ObjectId).Attributes.Duplicate();
        var deletedBounds = doc.Objects.FindId(source.ObjectId).Geometry.GetBoundingBox(true);
        Require(doc.Objects.Delete(source.ObjectId, true), "The operator's direct input deletion must succeed without regrouping.");
        if (scenario == "input-delete-last-member-protection")
        {
            foreach (var other in before.LinkGraph.Nodes.Where(node => instance.SourceNodeIds.Contains(node.Id) && node.Id != source.Id))
                Require(doc.Objects.Delete(other.ObjectId, true), "The entire selected input occurrence must be removed for the no-anchor boundary.");
            PumpIdle(core, services);
            try { services.LinkEvents.UpdateAssembly(doc, fixture.AssemblyName); }
            catch (InvalidOperationException) { }
            PumpIdle(core, services);
            var protectedAssembly = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
            Require(protectedAssembly.LinkGraph.Nodes.Select(node => node.Id).ToHashSet().SetEquals(beforeNodeIds) &&
                    before.LinkGraph.Nodes.Where(node => node.Role != AssemblyLinkRoles.Source)
                        .All(node => doc.Objects.FindId(node.ObjectId) is { IsDeleted: false }) &&
                    protectedAssembly.LinkGraph.Conflicts.Any(conflict => conflict.Status == AssemblyLinkStatuses.Open),
                "Deleting an occurrence's final retained member must require review and must not erase an entire downstream component implicitly.");
            return;
        }
        if (scenario is "input-delete-split-protection" or "input-delete-split-new-ids")
        {
            var middle = (deletedBounds.Min.X + deletedBounds.Max.X) / 2;
            using var firstHalf = Brep.CreateFromBox(new BoundingBox(deletedBounds.Min,
                new Point3d(middle, deletedBounds.Max.Y, deletedBounds.Max.Z)));
            using var secondHalf = Brep.CreateFromBox(new BoundingBox(new Point3d(middle, deletedBounds.Min.Y, deletedBounds.Min.Z),
                deletedBounds.Max));
            if (scenario == "input-delete-split-new-ids") deletedAttributes.ObjectId = Guid.NewGuid();
            var firstId = doc.Objects.AddBrep(firstHalf, deletedAttributes);
            if (scenario == "input-delete-split-new-ids") deletedAttributes.ObjectId = Guid.NewGuid();
            var secondId = doc.Objects.AddBrep(secondHalf, deletedAttributes);
            Require(firstId != Guid.Empty && secondId != Guid.Empty, "Split-like deletion must introduce two independent successor objects in the same event batch.");
            Require(scenario == "input-delete-split-new-ids"
                    ? firstId != source.ObjectId && secondId != source.ObjectId && doc.Objects.FindId(source.ObjectId) is null
                    : firstId == source.ObjectId,
                "The split fixture must exercise its intended identity boundary: both successors fresh, or the first reusing the deleted UUID.");
            PumpIdle(core, services);
            try { services.LinkEvents.UpdateAssembly(doc, fixture.AssemblyName); }
            catch (InvalidOperationException) { }
            PumpIdle(core, services);
            var protectedAssembly = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
            Require(protectedAssembly.PendingComponentUpdates.Count == 0 &&
                    protectedAssembly.LinkGraph.Nodes.Select(node => node.Id).ToHashSet().SetEquals(beforeNodeIds) &&
                    retiringIds.All(id => doc.Objects.FindId(id) is { IsDeleted: false }) &&
                    doc.Objects.FindId(firstId) is { IsDeleted: false } && doc.Objects.FindId(secondId) is { IsDeleted: false } &&
                    protectedAssembly.LinkGraph.Conflicts.Any(conflict => conflict.Status == AssemblyLinkStatuses.Open &&
                        conflict.ConflictType == AssemblyLinkConflictTypes.SourceSplit),
                "A delete-plus-add split-like batch must require explicit review rather than silently retiring the old tracked design or adopting guessed successors.");
            return;
        }
        Require(doc.Groups[sourceGroupIndex] is { IsDeleted: false } group && group.Id == instance.SourceGroupId &&
                InputFixtureGroupIds(doc, sourceGroupIndex).Count == instance.SourceNodeIds.Count - 1,
            "Direct deletion must leave the same input group and its retained members; no Regroup helper is used.");
        if (scenario is not ("input-delete-immediate" or "input-delete-restored-before-update"))
        {
            PumpIdle(core, services);
            Require(retiringIds.All(id => doc.Objects.FindId(id) is { IsDeleted: false }),
                "Even with automatic geometry propagation enabled, direct input deletion must leave downstream structure until Update Assembly.");
        }

        if (scenario is "input-delete-dependent" or "input-delete-shared-source" or
            "input-delete-copyorient-wrong-owner" or "input-delete-copyorient-ambiguous-parent" or
            "input-delete-copyorient-legacy-unsupported-recipe" or "input-delete-copyorient-legacy-chained-copy")
        {
            try { services.LinkEvents.UpdateAssembly(doc, fixture.AssemblyName); }
            catch (InvalidOperationException) { }
            PumpIdle(core, services);
            var protectedStore = services.Repository.Load(doc);
            var protectedAssembly = protectedStore.FindAssembly(fixture.AssemblyName)!;
            Require(retiringIds.All(id => doc.Objects.FindId(id) is { IsDeleted: false }) &&
                    before.LinkGraph.Nodes.Where(node => node.Role != AssemblyLinkRoles.Source)
                        .All(node => doc.Objects.FindId(node.ObjectId) is { IsDeleted: false }) &&
                    protectedAssembly.LinkGraph.Nodes.Select(node => node.Id).ToHashSet().SetEquals(beforeNodeIds) &&
                    (dependentAssemblyId == Guid.Empty || protectedStore.Assemblies.Any(assembly => assembly.Id == dependentAssemblyId)) &&
                    protectedAssembly.LinkGraph.Conflicts.Any(conflict => conflict.Status == AssemblyLinkStatuses.Open),
                "A shared source or downstream consumer must block structural retirement, preserve all managed UUIDs, and leave a review issue.");
            return;
        }
        if (scenario == "input-delete-restored-before-update")
        {
            Require(doc.Objects.Undelete(sourceSerial), "An operator-restored input must preserve its original UUID.");
            PumpIdle(core, services);
            services.LinkEvents.UpdateAssembly(doc, fixture.AssemblyName);
            PumpIdle(core, services);
            var restored = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
            Require(InputDeletionStableObjectIds(doc, before.Id).SequenceEqual(beforeObjectIds) &&
                    InputDeletionHeaderSignatures(doc, before.Id).SequenceEqual(beforeHeaders) && restored.PendingComponentUpdates.Count == 0 &&
                    restored.LinkGraph.Nodes.Select(node => node.Id).ToHashSet().SetEquals(beforeNodeIds) &&
                    restored.Parts.Single(part => part.Id == source.PartId).Quantity == 2 &&
                    !restored.LinkGraph.Conflicts.Any(conflict => conflict.Status == AssemblyLinkStatuses.Open),
                "Restoring a deleted source before Update Assembly must cancel its staged removal and preserve the full linked design.");
            return;
        }

        if (scenario == "input-delete-staged-restart")
        {
            var pending = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!.PendingComponentUpdates.Single();
            var persisted = System.Text.Json.JsonSerializer.Deserialize<PendingComponentUpdateRecord>(
                System.Text.Json.JsonSerializer.Serialize(pending))!;
            Require(persisted.RemovedSourceNodeIds.SequenceEqual(new[] { source.Id }) && persisted.AdditionOrigin == ComponentAdditionOrigins.Input,
                "A direct deletion's manual removal plan must retain its exact input identity when serialized.");
            services.LinkEvents.Stop();
            var restarted = new RegressionServices { AutomaticallyPropagate = true };
            restarted.LinkEvents.Start();
            try
            {
                PumpIdle(core, restarted);
                Require(retiringIds.All(id => doc.Objects.FindId(id) is { IsDeleted: false }) &&
                        restarted.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!.PendingComponentUpdates.Single()
                            .RemovedSourceNodeIds.SequenceEqual(new[] { source.Id }),
                    "Restarting event listeners must keep a pending direct removal without automatically applying it.");
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
        var updatedInstance = after.LinkGraph.SourceComponentInstances.Single(item => item.Id == instance.Id);
        Require(after.PendingComponentUpdates.Count == 0 && updatedInstance.SourceGroupId == instance.SourceGroupId &&
                updatedInstance.SourceNodeIds.Count == instance.SourceNodeIds.Count - 1 && !updatedInstance.SourceNodeIds.Contains(source.Id),
            "Update Assembly must accept one directly deleted input member without changing the registered group's identity.");
        Require(retiringIds.All(id => doc.Objects.FindId(id) is null) &&
                !after.LinkGraph.Nodes.Any(node => node.Id == source.Id || retiringIds.Contains(node.ObjectId)) &&
                !after.GeometryReferences.Any(reference => reference.SourceObjectId == source.ObjectId || retiringIds.Contains(reference.TargetObjectId)) &&
                after.Parts.All(part => !part.SourceObjectIds.Contains(source.ObjectId) && !part.GeneratedObjectIds.Any(retiringIds.Contains)),
            "Direct deletion must retire every owned original/copied counterpart and remove stale link, reference and part-membership records.");
        foreach (var node in after.LinkGraph.Nodes.Where(node => retainedBounds.ContainsKey(node.ObjectId)))
        {
            var expected = retainedBounds[node.ObjectId];
            var actual = doc.Objects.FindId(node.ObjectId).Geometry.GetBoundingBox(true);
            Require(expected.Min.DistanceTo(actual.Min) < 0.001 && expected.Max.DistanceTo(actual.Max) < 0.001,
                "Direct deletion must preserve all retained input, original, copied and flat placements.");
        }
        if (!singleton && !hardware)
        {
            var siblingBefore = before.LinkGraph.SourceComponentInstances.Single(item => item.Id != instance.Id);
            var siblingAfter = after.LinkGraph.SourceComponentInstances.Single(item => item.Id == siblingBefore.Id);
            Require(siblingAfter.SourceNodeIds.SequenceEqual(siblingBefore.SourceNodeIds) && siblingAfter.ComponentId == siblingBefore.ComponentId &&
                    updatedInstance.ComponentId != siblingAfter.ComponentId,
                "Removing a part from one occurrence must recategorize only that occurrence, not its identical sibling.");
        }
        if (hardware)
        {
            Require(after.Hardware.Count == 0 && !bomService.GenerateBom(doc, fixture.AssemblyName).Lines.Any(line => line.Category == "Hardware") &&
                    after.Parts.Sum(part => part.Quantity) == before.Parts.Sum(part => part.Quantity) && after.Components.Count == 1,
                "Direct hardware deletion must remove the BOM entry while preserving manufactured quantities and matching component categories.");
        }
        else if (singleton)
        {
            Require(doc.Objects.FindId(flat!.ObjectId) is null && oldLabelIds.All(id => doc.Objects.FindId(id) is null) &&
                    doc.Objects.FindId(noteId) is { IsDeleted: false } && after.Parts.All(part => part.Id != source.PartId),
                "Deleting a category's final source must remove its flat, owned labels and empty category, but preserve user notes.");
        }
        else
        {
            var retainedFlat = after.LinkGraph.Nodes.Single(node => node.Id == flat!.Id);
            var edge = after.LinkGraph.Edges.Single(item => item.ChildNodeId == retainedFlat.Id);
            var parent = after.LinkGraph.Nodes.Single(node => node.Id == edge.ParentNodeId);
            using var expectedFlat = doc.Objects.FindId(parent.ObjectId).Geometry.Duplicate();
            Require(after.Parts.Single(part => part.Id == source.PartId).Quantity == 1 && retainedFlat.ObjectId == flat!.ObjectId &&
                    ((Brep)flatGeometry!).IsDuplicate((Brep)doc.Objects.FindId(retainedFlat.ObjectId).Geometry, 0.001) &&
                    expectedFlat.Transform(edge.ParentToChildTransform.ToTransform()) &&
                    ((Brep)expectedFlat).IsDuplicate((Brep)doc.Objects.FindId(retainedFlat.ObjectId).Geometry, 0.001),
                "A retained part must keep its flat UUID/placement, decrement quantity, and link to a surviving parent with a correct transform.");
            Require(ComponentFixtureObjects(doc).Any(obj => obj.Geometry is TextEntity text &&
                    FlatPartAnnotations.IsOwned(obj.Attributes, after.Id, FlatPartAnnotations.PartLabel) &&
                    obj.Attributes.GetUserString(FlatPartAnnotations.OutputKey) == flat!.ObjectId.ToString("D") &&
                    text.PlainText.Contains("QTY : 1", StringComparison.Ordinal)),
                "The retained flat label must show the updated quantity after direct deletion.");
        }
        Require(!after.LinkGraph.Conflicts.Any(conflict => conflict.Status == AssemblyLinkStatuses.Open),
            "A safely applied direct deletion must clear its transient source-deletion and membership issues.");
        var afterIds = InputDeletionStableObjectIds(doc, after.Id);
        var afterHeaders = InputDeletionHeaderSignatures(doc, after.Id);
        services.LinkEvents.UpdateAssembly(doc, fixture.AssemblyName);
        PumpIdle(core, services);
        Require(InputDeletionStableObjectIds(doc, after.Id).SequenceEqual(afterIds) &&
                InputDeletionHeaderSignatures(doc, after.Id).SequenceEqual(afterHeaders),
            "Repeating Update Assembly must preserve tracked/user/part-label UUIDs and row-header text, placement, styling and count.");
    }

    private static void PrepareInputDeletionCopyOrientFixture(RhinoCore core, RhinoDoc doc, RegressionServices services,
        ComponentAdditionFixture fixture, string scenario)
    {
        // Exercise the real command service as well as the hand-built linked view
        // supporting the existing flat-layout fixture. The real writer used to omit
        // occurrence ownership, which cannot be caught by idealized graph fixtures.
        var before = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
        var originalIds = before.LinkGraph.Nodes.Select(node => node.Id).ToHashSet();
        var drawing = new ComponentDrawingService(services.Repository, services.Layers,
            new DocumentActionHistorySink(services.Repository), services.Lineage, services.LinkSafety);
        Require(drawing.CopyAndOrientComponents(doc, fixture.AssemblyName) == before.Components.Count,
            "Production Copy/Orient must create every component category before testing its deletion links.");
        PumpIdle(core, services);
        var copied = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
        var newNodes = copied.LinkGraph.Nodes.Where(node => !originalIds.Contains(node.Id)).ToList();
        Require(newNodes.Count > 0 && newNodes.All(node => node.Role == AssemblyLinkRoles.CopiedComponent),
            "This regression must contain copies created by the actual production Copy/Orient path.");
        if (scenario.Contains("-new", StringComparison.Ordinal))
        {
            Require(newNodes.All(node =>
            {
                var edge = copied.LinkGraph.Edges.Single(edge => edge.ChildNodeId == node.Id);
                var parent = copied.LinkGraph.Nodes.Single(parent => parent.Id == edge.ParentNodeId);
                return node.SourceComponentInstanceId != Guid.Empty && node.SourceComponentInstanceId == parent.SourceComponentInstanceId &&
                    doc.Objects.FindId(node.ObjectId).Attributes.GetUserString(AssemblyManagerConstants.SourceComponentInstanceIdUserString) ==
                        parent.SourceComponentInstanceId.ToString("D");
            }), "Every newly copied part and hardware item must inherit its proven parent occurrence in the graph and object metadata.");
        }

        // Move this real drawing view independently, exercising repair without
        // resetting placement or borrowing a neighboring component's transform.
        var move = Transform.Translation(73, -21, 9) * Transform.Rotation(Math.PI / 7, Vector3d.ZAxis, Point3d.Origin);
        EnqueuePlacementTransform(doc, services, newNodes.Select(node => node.ObjectId).ToArray(), move);
        foreach (var node in newNodes)
            Require(doc.Objects.Transform(node.ObjectId, move, true) != Guid.Empty,
                "The real Copy/Orient view must be movable before deleting an input member.");
        PumpIdle(core, services);

        if (!scenario.Contains("-legacy", StringComparison.Ordinal) &&
            scenario is not ("input-delete-copyorient-wrong-owner" or "input-delete-copyorient-ambiguous-parent")) return;
        using var mutation = AssemblyLinkMutationGate.Enter();
        var store = services.Repository.Load(doc);
        var assembly = store.FindAssembly(fixture.AssemblyName)!;
        var producedIds = newNodes.Select(node => node.Id).ToHashSet();
        var produced = assembly.LinkGraph.Nodes.Where(node => producedIds.Contains(node.Id)).ToList();
        foreach (var node in produced)
        {
            node.SourceComponentInstanceId = Guid.Empty;
            var edge = assembly.LinkGraph.Edges.Single(edge => edge.ChildNodeId == node.Id);
            var parent = assembly.LinkGraph.Nodes.Single(parent => parent.Id == edge.ParentNodeId);
            services.Lineage.ApplyObjectMetadata(doc, assembly, node, edge, parent.ObjectId);
        }
        var target = produced.Single(node => node.PartId == fixture.FirstPartId &&
            assembly.LinkGraph.Edges.Any(edge => edge.ChildNodeId == node.Id && assembly.LinkGraph.Nodes.Any(parent =>
                parent.Id == edge.ParentNodeId && parent.SourceComponentInstanceId == fixture.TemplateInstanceId)));
        if (scenario == "input-delete-copyorient-wrong-owner")
        {
            target.SourceComponentInstanceId = assembly.LinkGraph.SourceComponentInstances.Single(instance => instance.Id != fixture.TemplateInstanceId).Id;
            var edge = assembly.LinkGraph.Edges.Single(edge => edge.ChildNodeId == target.Id);
            var parent = assembly.LinkGraph.Nodes.Single(parent => parent.Id == edge.ParentNodeId);
            services.Lineage.ApplyObjectMetadata(doc, assembly, target, edge, parent.ObjectId);
        }
        if (scenario == "input-delete-copyorient-ambiguous-parent")
        {
            var alternate = assembly.LinkGraph.Nodes.Single(node => node.Role == AssemblyLinkRoles.OriginalAssembly &&
                node.PartId == fixture.FirstPartId && node.SourceComponentInstanceId != fixture.TemplateInstanceId);
            assembly.LinkGraph.Edges.Add(new AssemblyLinkEdgeRecord
            {
                ParentNodeId = alternate.Id, ChildNodeId = target.Id, Recipe = AssemblyLinkRecipes.DirectCopy,
                ParentToChildTransform = TransformRecord.FromTransform(Transform.Identity)
            });
        }
        if (scenario == "input-delete-copyorient-legacy-unsupported-recipe")
            assembly.LinkGraph.Edges.Single(edge => edge.ChildNodeId == target.Id).Recipe = AssemblyLinkRecipes.BlockDefinitionPart;
        if (scenario == "input-delete-copyorient-legacy-wholeblock-hardware")
        {
            var hardwareCopy = produced.Single(node => node.PartId == Guid.Empty && doc.Objects.FindId(node.ObjectId) is InstanceObject);
            assembly.LinkGraph.Edges.Single(edge => edge.ChildNodeId == hardwareCopy.Id).Recipe = AssemblyLinkRecipes.BlockDefinitionPart;
        }
        if (scenario == "input-delete-copyorient-legacy-chained-copy")
        {
            var parentObject = doc.Objects.FindId(target.ObjectId);
            using var attributes = parentObject.Attributes.Duplicate();
            using var geometry = parentObject.Geometry.Duplicate();
            attributes.RemoveFromAllGroups();
            AssemblyLineageService.ClearLinkMetadata(attributes);
            var placement = Transform.Translation(400, 0, 0);
            Require(geometry.Transform(placement), "A dependent copied descendant must have a real independent placement.");
            var childId = doc.Objects.Add(geometry, attributes);
            Require(childId != Guid.Empty, "A linked copy-of-a-copy must exist for the unsupported-consumer boundary.");
            var link = services.Lineage.RegisterDerived(doc, assembly, target.ObjectId, AssemblyLinkRoles.CopiedComponent,
                childId, AssemblyLinkRoles.CopiedComponent, placement, partId: target.PartId, componentId: target.ComponentId);
            Require(link.Parent.SourceComponentInstanceId == Guid.Empty && link.Child.SourceComponentInstanceId == Guid.Empty,
                "Both generations of the unsupported copied chain must omit the legacy occurrence ID.");
        }
        services.Repository.Save(doc, store);
        // The missing field is persisted and reloaded, matching drawings made by
        // the old build rather than a transient unsaved in-memory corruption.
        var reloaded = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
        Require(reloaded.LinkGraph.Nodes.Where(node => producedIds.Contains(node.Id) && node.Id != target.Id)
                .All(node => node.SourceComponentInstanceId == Guid.Empty),
            "The legacy fixture must persist omitted copied occurrence ownership before Update Assembly repairs it.");
    }

    private static Guid[] InputDeletionStableObjectIds(RhinoDoc doc, Guid assemblyId) =>
        ComponentFixtureObjects(doc).Where(obj => !FlatPartAnnotations.IsOwned(obj.Attributes, assemblyId, FlatPartAnnotations.RowHeader))
            .Select(obj => obj.Id).OrderBy(id => id).ToArray();

    private static string[] InputDeletionHeaderSignatures(RhinoDoc doc, Guid assemblyId) =>
        ComponentFixtureObjects(doc).Where(obj => FlatPartAnnotations.IsOwned(obj.Attributes, assemblyId, FlatPartAnnotations.RowHeader))
            .Select(obj =>
            {
                var text = (TextEntity)obj.Geometry;
                return System.Text.Json.JsonSerializer.Serialize(new
                {
                    text.PlainText,
                    Plane = TransformRecord.FromTransform(Transform.PlaneToPlane(Plane.WorldXY, text.Plane)),
                    text.TextHeight, text.DimensionScale, text.Justification,
                    Layer = doc.Layers[obj.Attributes.LayerIndex].FullPath,
                    obj.Attributes.ColorSource,
                    Color = obj.Attributes.ObjectColor.ToArgb()
                });
            }).OrderBy(value => value, StringComparer.Ordinal).ToArray();
}
