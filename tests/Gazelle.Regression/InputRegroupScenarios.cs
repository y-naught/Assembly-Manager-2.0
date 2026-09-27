using System.Text.Json;
using AssemblyManagerPlugin.Core;
using AssemblyManagerPlugin.Services;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Runtime.InProcess;

internal static partial class Program
{
    private static void RunInputRegroupScenario(RhinoCore core, RegressionServices services,
        string scenario, List<RhinoDoc> fixtureDocs)
    {
        if (scenario is "input-regroup-remove-hardware" or "input-regroup-dependent-removal" or
            "input-regroup-removal-rollback" or "input-regroup-cancel-removal")
        {
            RunInputRegroupSafetyScenario(core, services, scenario, fixtureDocs);
            return;
        }
        var doc = RhinoDoc.Create(null);
        fixtureDocs.Add(doc);
        doc.ModelUnitSystem = UnitSystem.Inches;
        doc.ModelAbsoluteTolerance = 0.001;
        services.AutomaticallyPropagate = true;
        services.EnableLinkedAssemblies = true;
        var singleton = scenario == "input-regroup-last-category";
        var fixture = CreateComponentAdditionFixture(doc, services, scenario, false, singleton);
        PumpIdle(core, services);

        if (scenario == "input-regroup-multi-view-flat")
        {
            var drawing = new ComponentDrawingService(services.Repository, services.Layers,
                new DocumentActionHistorySink(services.Repository), services.Lineage, services.LinkSafety);
            Require(drawing.PlaceComponent(doc, fixture.AssemblyName, fixture.ComponentId, new Point3d(620, 300, 0)) == 3,
                "Regroup removal coverage must include another independent copied component.");
            var placed = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
            var groups = placed.LinkGraph.Nodes.Where(node => node.Role == AssemblyLinkRoles.CopiedComponent)
                .SelectMany(node => doc.Objects.FindId(node.ObjectId).Attributes.GetGroupList() ?? Array.Empty<int>()).Distinct().ToArray();
            var ids = InputFixtureGroupIds(doc, groups.Last()).ToArray();
            var move = Transform.Translation(-11, 27, 9) * Transform.Rotation(Math.PI / 7, Vector3d.ZAxis, Point3d.Origin);
            EnqueuePlacementTransform(doc, services, ids, move);
            foreach (var id in ids)
                Require(doc.Objects.Transform(id, move, true) != Guid.Empty, "A second copied view must move before regrouping.");
            PumpIdle(core, services);
            var flat = placed.LinkGraph.Nodes.Single(node => node.PartId == fixture.FirstPartId && node.Role == AssemblyLinkRoles.FlatPart);
            var flatMove = Transform.Translation(32, -14, 0) * Transform.Rotation(23 * Math.PI / 180, Vector3d.ZAxis,
                doc.Objects.FindId(flat.ObjectId).Geometry.GetBoundingBox(true).Center);
            EnqueuePlacementTransform(doc, services, new[] { flat.ObjectId }, flatMove);
            Require(doc.Objects.Transform(flat.ObjectId, flatMove, true) != Guid.Empty, "The existing flat must have an arbitrary operator rotation.");
            PumpIdle(core, services);
        }

        var before = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
        var donor = before.LinkGraph.SourceComponentInstances.Single(instance => instance.Id == fixture.TemplateInstanceId);
        var sourceNodes = before.LinkGraph.Nodes.Where(node => donor.SourceNodeIds.Contains(node.Id)).ToList();
        var sourceIds = sourceNodes.Select(node => node.ObjectId).ToList();
        var oldGroupIndex = FindComponentFixtureGroup(doc, donor.SourceGroupId);
        var removedSource = sourceNodes.Single(node => node.PartId == fixture.FirstPartId);
        var removedOriginal = before.LinkGraph.Nodes.Single(node => node.SourceComponentInstanceId == donor.Id &&
            node.PartId == fixture.FirstPartId && node.Role == AssemblyLinkRoles.OriginalAssembly);
        var removedCopies = before.LinkGraph.Edges.Where(edge => edge.ParentNodeId == removedOriginal.Id)
            .Select(edge => before.LinkGraph.Nodes.Single(node => node.Id == edge.ChildNodeId))
            .Where(node => node.Role == AssemblyLinkRoles.CopiedComponent).ToList();
        var oldFlat = before.LinkGraph.Nodes.Single(node => node.PartId == fixture.FirstPartId && node.Role == AssemblyLinkRoles.FlatPart);
        using var flatGeometry = doc.Objects.FindId(oldFlat.ObjectId).Geometry.Duplicate();
        using var removedGeometry = doc.Objects.FindId(removedSource.ObjectId).Geometry.Duplicate();
        using var removedAttributes = doc.Objects.FindId(removedSource.ObjectId).Attributes.Duplicate();
        var originalObjectIds = before.LinkGraph.Nodes.Select(node => node.ObjectId).ToHashSet();
        var retainedBounds = before.LinkGraph.Nodes.ToDictionary(node => node.Id,
            node => doc.Objects.FindId(node.ObjectId).Geometry.GetBoundingBox(true));
        var oldNodeIds = before.LinkGraph.Nodes.Select(node => node.Id).ToHashSet();
        var oldOriginalGroupId = donor.GeneratedGroupId;

        if (scenario == "input-regroup-rename")
        {
            var name = "Operator renamed input " + Guid.NewGuid().ToString("N");
            Require(doc.Groups.ChangeGroupName(oldGroupIndex, name), "The input group must be renamed natively.");
            PumpIdle(core, services);
            var renamed = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
            var instance = renamed.LinkGraph.SourceComponentInstances.Single(item => item.Id == donor.Id);
            Require(instance.SourceGroupId == donor.SourceGroupId && instance.SourceGroupName == name &&
                    renamed.PendingComponentUpdates.Count == 0 && renamed.LinkGraph.Nodes.Select(node => node.Id).ToHashSet().SetEquals(oldNodeIds),
                "A name-only edit must update the cached input name without changing identity or creating a structural plan.");
            return;
        }

        if (scenario == "input-regroup-shared-input")
        {
            using var mutation = AssemblyLinkMutationGate.Enter();
            var store = services.Repository.Load(doc);
            var other = new AssemblyRecord { Name = fixture.AssemblyName + "-dependent" };
            var component = new ComponentRecord { Name = "C01" };
            other.Components.Add(component);
            var shared = new SourceComponentInstanceRecord
            {
                ComponentId = component.Id, SourceGroupId = donor.SourceGroupId,
                SourceGroupIndex = oldGroupIndex, SourceGroupName = donor.SourceGroupName
            };
            other.LinkGraph.SourceComponentInstances.Add(shared);
            foreach (var id in sourceIds)
                shared.SourceNodeIds.Add(services.Lineage.EnsureNode(other, id, AssemblyLinkRoles.Source,
                    componentId: component.Id, sourceComponentInstanceId: shared.Id).Id);
            store.Assemblies.Add(other);
            services.Repository.Save(doc, store);
        }

        var removes = scenario is "input-regroup-removal" or "input-regroup-deleted-removal" or
            "input-regroup-add-remove" or "input-regroup-multi-view-flat" or "input-regroup-last-category";
        var adds = scenario is "input-regroup-addition" or "input-regroup-add-remove" or "input-regroup-repeated" or
            "input-regroup-ambiguous" or "input-regroup-mixed-occurrences" or "input-regroup-shared-input";
        var selectedIds = sourceIds.Where(id => !removes || id != removedSource.ObjectId).ToList();
        Guid addedId = Guid.Empty;
        if (adds)
        {
            addedId = AddInputComponentFixtureAddition(doc, services, fixture, scenario, false, false);
            selectedIds.Add(addedId);
        }
        if (scenario == "input-regroup-mixed-occurrences")
            selectedIds.Add(before.LinkGraph.Nodes.First(node => node.Role == AssemblyLinkRoles.Source &&
                node.SourceComponentInstanceId != donor.Id).ObjectId);

        var removedLabelIds = ComponentFixtureObjects(doc).Where(obj =>
            FlatPartAnnotations.IsOwned(obj.Attributes, before.Id, FlatPartAnnotations.PartLabel) &&
            obj.Attributes.GetUserString(FlatPartAnnotations.OutputKey) == oldFlat.ObjectId.ToString("D"))
            .Select(obj => obj.Id).ToList();
        Guid userNoteId = Guid.Empty;
        if (singleton)
        {
            using var mutation = AssemblyLinkMutationGate.Enter();
            using var attributes = new ObjectAttributes
            {
                LayerIndex = services.Layers.EnsureLayerIndex(doc, $"{LayerService.PartsPart(before.Name, "P01")}::text")
            };
            using var text = new TextEntity { PlainText = "Operator note: preserve this", Plane = Plane.WorldXY, TextHeight = 1 };
            userNoteId = doc.Objects.AddText(text, attributes);
            Require(userNoteId != Guid.Empty, "An unrelated operator annotation must exist beside the retiring flat label.");
        }

        Require(doc.Groups.Delete(oldGroupIndex), "The input occurrence must be ungrouped before its replacement group is created.");
        if (scenario == "input-regroup-deleted-removal")
            Require(doc.Objects.Delete(removedSource.ObjectId, true), "The removed input must be deleted as an explicit operator edit.");
        var newName = new UtilityGeometryService(services.Layers).Regroup(doc, selectedIds);
        var newGroup = doc.Groups.FindName(newName)!;
        Require(newGroup is not null && newGroup.Id != donor.SourceGroupId, "Regrouping must create a different Rhino group identity.");
        if (scenario == "input-regroup-ambiguous")
            Require(doc.Groups.Add("Competing input group", selectedIds) >= 0, "Two equally plausible groups must coexist before the event drain.");
        PumpIdle(core, services);

        if (scenario is "input-regroup-ambiguous" or "input-regroup-mixed-occurrences" or "input-regroup-shared-input")
        {
            var rejected = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
            Require(rejected.PendingComponentUpdates.Count == 0 &&
                    rejected.LinkGraph.Nodes.Select(node => node.Id).ToHashSet().SetEquals(oldNodeIds) &&
                    originalObjectIds.All(id => doc.Objects.FindId(id) is { IsDeleted: false }),
                "Ambiguous or shared regrouping must not stage membership changes, delete output, or register new counterparts.");
            Require(rejected.LinkGraph.Conflicts.Any(conflict => conflict.Status == AssemblyLinkStatuses.Open),
                "Ambiguous or shared regrouping must remain visible as a link review issue.");
            if (scenario == "input-regroup-ambiguous")
            {
                var deferredSource = rejected.LinkGraph.Nodes.First(node => node.Role == AssemblyLinkRoles.Source &&
                    node.SourceComponentInstanceId != donor.Id && node.PartId == fixture.FirstPartId);
                var deferredEdge = rejected.LinkGraph.Edges.Single(edge => edge.ParentNodeId == deferredSource.Id &&
                    rejected.LinkGraph.Nodes.Any(node => node.Id == edge.ChildNodeId && node.Role == AssemblyLinkRoles.OriginalAssembly));
                var deferredOriginal = rejected.LinkGraph.Nodes.Single(node => node.Id == deferredEdge.ChildNodeId);
                using var originalBeforeRepair = doc.Objects.FindId(deferredOriginal.ObjectId).Geometry.Duplicate();
                using var editedSource = ((Brep)doc.Objects.FindId(deferredSource.ObjectId).Geometry).DuplicateBrep();
                Require(editedSource.Transform(Transform.Scale(editedSource.GetBoundingBox(true).Center, 1.15)) &&
                    doc.Objects.Replace(deferredSource.ObjectId, editedSource),
                    "A different occurrence must receive a genuine geometry edit while regrouping is ambiguous.");
                PumpIdle(core, services);

                var deferredDocuments = (System.Collections.Concurrent.ConcurrentDictionary<uint, bool>)
                    typeof(AssemblyLinkEventService).GetField("_deferredDocuments",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                        .GetValue(services.LinkEvents)!;
                var held = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
                Require(held.PendingComponentUpdates.Count == 0 && held.LinkGraph.Nodes.Single(node => node.Id == deferredSource.Id)
                        .Metadata.ContainsKey("PendingSourceUpdate") && !deferredDocuments.ContainsKey(doc.RuntimeSerialNumber),
                    "An unresolved regroup must retain pending geometry work without scheduling an endless empty-idle retry.");
                Require(GeometryBase.GeometryEquals(originalBeforeRepair, doc.Objects.FindId(deferredOriginal.ObjectId).Geometry),
                    "Geometry updates must remain held while the input group is ambiguous.");
                PumpIdle(core, services);
                Require(!deferredDocuments.ContainsKey(doc.RuntimeSerialNumber),
                    "Repeated idle callbacks must not requeue work blocked by an input regroup issue.");

                Require(doc.Groups.Delete(doc.Groups.FindName("Competing input group").Index),
                    "Deleting only the competing group must repair the regroup ambiguity.");
                PumpIdle(core, services);
                var repaired = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
                Require(repaired.PendingComponentUpdates.Count == 1 &&
                        repaired.LinkGraph.SourceComponentInstances.Single(instance => instance.Id == donor.Id).SourceGroupId == newGroup!.Id &&
                        !repaired.LinkGraph.Conflicts.Any(conflict => conflict.Status == AssemblyLinkStatuses.Open &&
                            conflict.Metadata.TryGetValue("EventKey", out var key) && key.StartsWith("source-regroup:", StringComparison.Ordinal)),
                    "Removing a competing group must wake regroup reconciliation and stage the remaining unambiguous design.");
                services.LinkEvents.UpdateAssembly(doc, fixture.AssemblyName);
                PumpIdle(core, services);
                var completed = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
                Require(completed.PendingComponentUpdates.Count == 0 &&
                        !completed.LinkGraph.Nodes.Single(node => node.Id == deferredSource.Id).Metadata.ContainsKey("PendingSourceUpdate") &&
                        !GeometryBase.GeometryEquals(originalBeforeRepair, doc.Objects.FindId(deferredOriginal.ObjectId).Geometry),
                    "Once the regroup is repaired, Update Assembly must apply membership and resume the previously held geometry edit.");
            }
            return;
        }

        var staged = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
        var rebound = staged.LinkGraph.SourceComponentInstances.Single(instance => instance.Id == donor.Id);
        Require(rebound.SourceGroupId == newGroup!.Id && rebound.SourceGroupName == newName &&
                rebound.GeneratedGroupId == oldOriginalGroupId && rebound.SourceNodeIds.ToHashSet().SetEquals(donor.SourceNodeIds),
            "Retained source UUIDs must rebind the same occurrence to its new input group without changing its original group or registered membership yet.");
        if (scenario == "input-regroup-same-members")
        {
            Require(staged.PendingComponentUpdates.Count == 0 && staged.LinkGraph.Nodes.Select(node => node.Id).ToHashSet().SetEquals(oldNodeIds),
                "Regrouping unchanged input members must not require a structural update or recategorize the component.");
            services.LinkEvents.UpdateAssembly(doc, fixture.AssemblyName);
            PumpIdle(core, services);
            Require(services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!.Components.Count == 1,
                "An unchanged regroup must remain the same component category after Update Assembly.");
            return;
        }

        var pending = staged.PendingComponentUpdates.Single();
        Require(pending.AdditionOrigin == ComponentAdditionOrigins.Input && pending.TemplateInstanceId == donor.Id &&
                pending.RegroupedGroupId == newGroup.Id && pending.AddedObjectIds.ToHashSet().SetEquals(adds ? new[] { addedId } : Array.Empty<Guid>()) &&
                pending.RemovedSourceNodeIds.ToHashSet().SetEquals(removes ? new[] { removedSource.Id } : Array.Empty<Guid>()),
            "Automatic regroup recognition must persist the exact input additions and removals for only the selected occurrence.");
        Require(staged.LinkGraph.Nodes.Select(node => node.Id).ToHashSet().SetEquals(oldNodeIds) &&
                doc.Objects.FindId(removedOriginal.ObjectId) is not null && removedCopies.All(node => doc.Objects.FindId(node.ObjectId) is not null),
            "Recognizing a regroup must leave all downstream geometry unchanged until Update Assembly, even when automatic propagation is enabled.");

        Guid abandonedAddition = Guid.Empty;
        if (scenario == "input-regroup-repeated")
        {
            abandonedAddition = addedId;
            addedId = AddInputComponentFixtureAddition(doc, services, fixture, scenario, false, false);
            selectedIds = sourceIds.Append(addedId).ToList();
            Require(doc.Groups.Delete(newGroup.Index), "A staged input group must be replaceable before applying it.");
            newName = new UtilityGeometryService(services.Layers).Regroup(doc, selectedIds);
            newGroup = doc.Groups.FindName(newName)!;
            PumpIdle(core, services);
            staged = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
            Require(staged.PendingComponentUpdates.Single().AddedObjectIds.SequenceEqual(new[] { addedId }) &&
                    staged.PendingComponentUpdates.Single().RegroupedGroupId == newGroup.Id &&
                    doc.Objects.FindId(abandonedAddition) is { IsDeleted: false },
                "A later unambiguous regroup must replace the pending design and preserve an omitted, never-adopted addition.");
        }

        services.LinkEvents.UpdateAssembly(doc, fixture.AssemblyName);
        PumpIdle(core, services);
        var after = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
        var changed = after.LinkGraph.SourceComponentInstances.Single(instance => instance.Id == donor.Id);
        Require(after.PendingComponentUpdates.Count == 0 && changed.SourceGroupId == newGroup.Id && changed.GeneratedGroupId == oldOriginalGroupId &&
                changed.SourceNodeIds.Count == donor.SourceNodeIds.Count + (adds ? 1 : 0) - (removes ? 1 : 0),
            "Update Assembly must consume the latest regroup exactly once while retaining occurrence and group identities.");
        if (!singleton)
        {
            var siblingBefore = before.LinkGraph.SourceComponentInstances.Single(instance => instance.Id != donor.Id);
            var sibling = after.LinkGraph.SourceComponentInstances.Single(instance => instance.Id == siblingBefore.Id);
            Require(sibling.SourceNodeIds.SequenceEqual(siblingBefore.SourceNodeIds) && sibling.ComponentId == siblingBefore.ComponentId &&
                    changed.ComponentId != sibling.ComponentId && after.Components.Count == 2,
                "Changing one regrouped design must recategorize only that occurrence and preserve the identical sibling.");
        }
        if (adds)
        {
            var newSource = after.LinkGraph.Nodes.Single(node => node.ObjectId == addedId && node.Role == AssemblyLinkRoles.Source);
            Require(changed.SourceNodeIds.Contains(newSource.Id) && newSource.SourceComponentInstanceId == donor.Id,
                "The actual selected input UUID must be adopted once, not replaced by a new input copy.");
        }
        if (abandonedAddition != Guid.Empty)
            Require(doc.Objects.FindId(abandonedAddition) is not null && !after.LinkGraph.Nodes.Any(node => node.ObjectId == abandonedAddition),
                "An abandoned pending addition must remain ordinary unlinked input geometry.");
        foreach (var node in after.LinkGraph.Nodes.Where(node => retainedBounds.ContainsKey(node.Id)))
        {
            var expected = retainedBounds[node.Id];
            var current = doc.Objects.FindId(node.ObjectId).Geometry.GetBoundingBox(true);
            Require(expected.Min.DistanceTo(current.Min) < 0.001 && expected.Max.DistanceTo(current.Max) < 0.001,
                "Retained input, original, copied and flat members must keep their geometry and independent final placements after regrouping.");
        }
        if (removes)
        {
            Require(!after.LinkGraph.Nodes.Any(node => node.Id == removedSource.Id || node.Id == removedOriginal.Id ||
                        removedCopies.Any(copy => copy.Id == node.Id)) && doc.Objects.FindId(removedOriginal.ObjectId) is null &&
                    removedCopies.All(node => doc.Objects.FindId(node.ObjectId) is null),
                "A removed member must retire its owned original and every copied view while removing their authoritative link records.");
            Require(after.Parts.All(part => !part.SourceObjectIds.Contains(removedSource.ObjectId) &&
                        !part.GeneratedObjectIds.Contains(removedOriginal.ObjectId)) &&
                    !after.GeometryReferences.Any(reference => reference.SourceObjectId == removedSource.ObjectId || reference.TargetObjectId == removedOriginal.ObjectId),
                "Removed inputs and originals must not survive as stale legacy membership or quantity records.");
            if (scenario != "input-regroup-deleted-removal")
            {
                var retained = doc.Objects.FindId(removedSource.ObjectId);
                Require(retained is not null && retained.Attributes.LayerIndex == removedAttributes.LayerIndex &&
                        string.IsNullOrWhiteSpace(retained.Attributes.GetUserString(AssemblyManagerConstants.LinkNodeIdUserString)),
                    "An excluded live input must keep its UUID and layer and lose only its obsolete Gazelle identity.");
                AssertComponentFixtureGeometryBounds(removedGeometry, retained!.Geometry, "The excluded input geometry must remain unchanged.");
            }
            if (singleton)
            {
                Require(doc.Objects.FindId(oldFlat.ObjectId) is null && removedLabelIds.All(id => doc.Objects.FindId(id) is null) &&
                        doc.Objects.FindId(userNoteId) is not null && after.Parts.All(part => part.Id != fixture.FirstPartId || part.Quantity == 0),
                    "Removing a category's last occurrence must retire its flat and owned label, zero its quantity, and preserve operator notes.");
            }
            else
            {
                var flat = after.LinkGraph.Nodes.Single(node => node.Id == oldFlat.Id);
                var edge = after.LinkGraph.Edges.Single(item => item.ChildNodeId == flat.Id);
                var parent = after.LinkGraph.Nodes.Single(node => node.Id == edge.ParentNodeId);
                Require(flat.ObjectId == oldFlat.ObjectId && ((Brep)flatGeometry).IsDuplicate((Brep)doc.Objects.FindId(flat.ObjectId).Geometry, 0.001) &&
                        after.Parts.Single(part => part.Id == fixture.FirstPartId).Quantity == 1,
                    "A still-used part must retain the exact flat UUID and operator placement while its quantity decreases.");
                using var expected = doc.Objects.FindId(parent.ObjectId).Geometry.Duplicate();
                Require(expected.Transform(edge.ParentToChildTransform.ToTransform()) &&
                        ((Brep)expected).IsDuplicate((Brep)doc.Objects.FindId(flat.ObjectId).Geometry, 0.001),
                    "A flat whose prior parent was removed must receive a surviving parent with a correct replacement transform.");
                var labels = ComponentFixtureObjects(doc).Where(obj => obj.Geometry is TextEntity &&
                    FlatPartAnnotations.IsOwned(obj.Attributes, after.Id, FlatPartAnnotations.PartLabel) &&
                    obj.Attributes.GetUserString(FlatPartAnnotations.OutputKey) == flat.ObjectId.ToString("D"));
                Require(labels.Any(obj => ((TextEntity)obj.Geometry).PlainText.Contains("QTY : 1", StringComparison.Ordinal)),
                    "The retained flat label must show the reduced manufacturing quantity.");
            }
        }
        Require(!after.LinkGraph.Conflicts.Any(conflict => conflict.Status == AssemblyLinkStatuses.Open),
            "A successfully accepted unambiguous input regroup must clear its temporary membership/deletion issues.");
        var countAfter = ComponentFixtureObjects(doc).Count;
        services.LinkEvents.UpdateAssembly(doc, fixture.AssemblyName);
        PumpIdle(core, services);
        Require(ComponentFixtureObjects(doc).Count == countAfter &&
                services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!.PendingComponentUpdates.Count == 0,
            "Repeating Update Assembly must not duplicate additions, repeat removals, or move the completed layout.");
    }

    private static void RunInputRegroupSafetyScenario(RhinoCore core, RegressionServices services,
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
        var hardware = scenario == "input-regroup-remove-hardware";
        Guid hardwareId = Guid.Empty;
        var bomService = new BomService(services.Repository, new DocumentActionHistorySink(services.Repository));
        if (hardware)
        {
            hardwareId = AddInputComponentFixtureAddition(doc, services, fixture, scenario + "-block-hardware", false, true);
            services.LinkEvents.StageInputComponentAddition(doc, fixture.AssemblyName, fixture.TemplateInstanceId, hardwareId);
            services.LinkEvents.UpdateAssembly(doc, fixture.AssemblyName);
            PumpIdle(core, services);
            var bom = bomService.GenerateBom(doc, fixture.AssemblyName);
            Require(bom.Lines.Count(line => line.Category == "Hardware") == 1 &&
                    bom.Lines.Single(line => line.Category == "Hardware").Quantity == 1,
                "Hardware-removal coverage must begin with one registered block counted once by the BOM.");
        }
        var before = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
        var donor = before.LinkGraph.SourceComponentInstances.Single(instance => instance.Id == fixture.TemplateInstanceId);
        var sources = before.LinkGraph.Nodes.Where(node => donor.SourceNodeIds.Contains(node.Id)).ToList();
        var source = sources.Single(node => hardware ? node.ObjectId == hardwareId : node.PartId == fixture.FirstPartId);
        var sourceLinkTagBefore = doc.Objects.FindId(source.ObjectId).Attributes.GetUserString(AssemblyManagerConstants.LinkNodeIdUserString);
        var original = before.LinkGraph.Nodes.Single(node => node.SourceComponentInstanceId == donor.Id &&
            (hardware ? node.Role == AssemblyLinkRoles.Hardware : node.Role == AssemblyLinkRoles.OriginalAssembly && node.PartId == fixture.FirstPartId));
        var copies = before.LinkGraph.Edges.Where(edge => edge.ParentNodeId == original.Id)
            .Select(edge => before.LinkGraph.Nodes.Single(node => node.Id == edge.ChildNodeId))
            .Where(node => node.Role == AssemblyLinkRoles.CopiedComponent).ToList();
        var retiringIds = copies.Select(node => node.ObjectId).Append(original.ObjectId).ToHashSet();
        var allIds = InputSafetyObjectIds(doc);
        var retainedBounds = before.LinkGraph.Nodes.ToDictionary(node => node.ObjectId,
            node => doc.Objects.FindId(node.ObjectId).Geometry.GetBoundingBox(true));
        var priorNodeIds = before.LinkGraph.Nodes.Select(node => node.Id).ToHashSet();
        Guid dependentAssemblyId = Guid.Empty;
        if (scenario == "input-regroup-dependent-removal")
        {
            using var mutation = AssemblyLinkMutationGate.Enter();
            var store = services.Repository.Load(doc);
            var dependent = new AssemblyRecord { Name = fixture.AssemblyName + "-consumer" };
            dependentAssemblyId = dependent.Id;
            var part = new PartRecord
            {
                Name = "P01", Quantity = 1, MaterialId = "BIRCH", CategorizationMaterialId = "BIRCH",
                SourceObjectIds = new List<Guid> { copies[0].ObjectId }
            };
            dependent.Parts.Add(part);
            services.Lineage.EnsureNode(dependent, copies[0].ObjectId, AssemblyLinkRoles.Source, part.Id);
            store.Assemblies.Add(dependent);
            services.Repository.Save(doc, store);
        }

        Require(doc.Groups.Delete(FindComponentFixtureGroup(doc, donor.SourceGroupId)), "The safety fixture input must ungroup.");
        var regroupName = new UtilityGeometryService(services.Layers).Regroup(doc,
            sources.Where(node => node.Id != source.Id).Select(node => node.ObjectId));
        PumpIdle(core, services);
        var staged = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
        if (scenario == "input-regroup-dependent-removal")
        {
            if (staged.PendingComponentUpdates.Count > 0)
                ExpectComponentUpdateRejection(() => services.LinkEvents.UpdateAssembly(doc, fixture.AssemblyName),
                    "A generated member consumed by another assembly must reject removal before any geometry is deleted.");
            else
                Require(staged.LinkGraph.Conflicts.Any(conflict => conflict.Status == AssemblyLinkStatuses.Open),
                    "If staging rejects a dependent member, the dependency must remain visible for review.");
            var result = services.Repository.Load(doc);
            var unchanged = result.FindAssembly(fixture.AssemblyName)!;
            Require(InputSafetyObjectIds(doc).SequenceEqual(allIds) &&
                    unchanged.LinkGraph.Nodes.Select(node => node.Id).ToHashSet().SetEquals(priorNodeIds) &&
                    result.Assemblies.Single(assembly => assembly.Id == dependentAssemblyId).LinkGraph.Nodes.Single().ObjectId == copies[0].ObjectId &&
                    doc.Objects.FindId(source.ObjectId).Attributes.GetUserString(AssemblyManagerConstants.LinkNodeIdUserString) == sourceLinkTagBefore,
                "Dependency rejection must preserve every UUID, original/source identity, consumer record and copied output.");
            return;
        }
        Require(staged.PendingComponentUpdates.Single().RemovedSourceNodeIds.SequenceEqual(new[] { source.Id }),
            "The safety fixture must stage exactly the omitted input member for removal.");

        if (scenario == "input-regroup-cancel-removal")
        {
            Require(doc.Groups.Delete(doc.Groups.FindName(regroupName).Index), "The staged group must be replaceable before apply.");
            var restoredName = new UtilityGeometryService(services.Layers).Regroup(doc, sources.Select(node => node.ObjectId));
            PumpIdle(core, services);
            var restored = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
            Require(restored.PendingComponentUpdates.Count == 0 && InputSafetyObjectIds(doc).SequenceEqual(allIds) &&
                    restored.LinkGraph.SourceComponentInstances.Single(instance => instance.Id == donor.Id).SourceGroupId == doc.Groups.FindName(restoredName).Id &&
                    restored.LinkGraph.Nodes.Select(node => node.Id).ToHashSet().SetEquals(priorNodeIds),
                "Restoring all registered input members must cancel the pending removal without deleting or recreating any geometry.");
            services.LinkEvents.UpdateAssembly(doc, fixture.AssemblyName);
            PumpIdle(core, services);
            restored = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
            Require(restored.Components.Count == 1 && restored.Parts.Single(part => part.Id == fixture.FirstPartId).Quantity == 2 &&
                    !restored.LinkGraph.Conflicts.Any(conflict => conflict.Status == AssemblyLinkStatuses.Open),
                "Cancelled removal must retain the original component category and quantities without leftover membership issues.");
            return;
        }

        if (scenario == "input-regroup-removal-rollback")
        {
            var store = services.Repository.Load(doc);
            var assembly = store.FindAssembly(fixture.AssemblyName)!;
            var beforeJson = JsonSerializer.Serialize(assembly);
            var sourceAttributes = InputSafetyAttributes(doc, source.ObjectId);
            var flat = assembly.LinkGraph.Nodes.Single(node => node.PartId == source.PartId && node.Role == AssemblyLinkRoles.FlatPart);
            var flatAttributes = InputSafetyAttributes(doc, flat.ObjectId);
            var serials = retiringIds.ToDictionary(id => id, id => doc.Objects.FindId(id).RuntimeSerialNumber);
            Guid firstDeleted = Guid.Empty;
            Guid lockedId = Guid.Empty;
            var lockedSuccessfully = false;
            void ForceMidRemovalFailure(object? sender, RhinoObjectEventArgs args)
            {
                if (firstDeleted != Guid.Empty || !retiringIds.Contains(args.ObjectId)) return;
                firstDeleted = args.ObjectId;
                lockedId = retiringIds.First(id => id != args.ObjectId && doc.Objects.FindId(id) is { IsDeleted: false });
                // Mimic a competing document callback changing the next output after
                // preflight has passed but after the first owned copy was already deleted.
                lockedSuccessfully = doc.Objects.Lock(lockedId, true);
            }
            RhinoDoc.DeleteRhinoObject += ForceMidRemovalFailure;
            try
            {
                ExpectComponentUpdateRejection(() => services.ComponentUpdates.ApplyPending(doc, store, assembly),
                    "A generated output locked during removal must trigger rollback instead of a partially applied design.");
                Require(firstDeleted != Guid.Empty && lockedSuccessfully,
                    "The rollback test must actually delete an initial object and lock another output during the delete callback.");
                Require(InputSafetyObjectIds(doc).SequenceEqual(allIds) && retiringIds.All(id =>
                            doc.Objects.FindId(id) is { IsDeleted: false } value && value.RuntimeSerialNumber == serials[id]) &&
                        InputSafetyAttributes(doc, source.ObjectId) == sourceAttributes &&
                        InputSafetyAttributes(doc, flat.ObjectId) == flatAttributes && JsonSerializer.Serialize(assembly) == beforeJson,
                    "Removal rollback must undelete the exact generated objects and restore input/flat metadata and the pending graph snapshot.");
            }
            finally
            {
                RhinoDoc.DeleteRhinoObject -= ForceMidRemovalFailure;
                if (lockedSuccessfully)
                {
                    using var mutation = AssemblyLinkMutationGate.Enter();
                    Require(doc.Objects.Unlock(lockedId, true), "The test's external lock must be released before retry.");
                }
            }
        }

        services.LinkEvents.UpdateAssembly(doc, fixture.AssemblyName);
        PumpIdle(core, services);
        var after = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
        Require(after.PendingComponentUpdates.Count == 0 && retiringIds.All(id => doc.Objects.FindId(id) is null) &&
                doc.Objects.FindId(source.ObjectId) is { IsDeleted: false } &&
                string.IsNullOrWhiteSpace(doc.Objects.FindId(source.ObjectId).Attributes.GetUserString(AssemblyManagerConstants.LinkNodeIdUserString)),
            "A validated removal or rollback retry must remove owned copies once while preserving the detached live input.");
        foreach (var node in after.LinkGraph.Nodes.Where(node => retainedBounds.ContainsKey(node.ObjectId)))
        {
            var expected = retainedBounds[node.ObjectId];
            var bounds = doc.Objects.FindId(node.ObjectId).Geometry.GetBoundingBox(true);
            Require(bounds.Min.DistanceTo(expected.Min) < 0.001 && bounds.Max.DistanceTo(expected.Max) < 0.001,
                "Removing hardware or retrying a rolled-back removal must preserve every retained member's placement.");
        }
        if (hardware)
        {
            Require(after.Hardware.Count == 0 && after.Parts.Sum(part => part.Quantity) == before.Parts.Sum(part => part.Quantity) &&
                    after.Components.Count == 1 && doc.Objects.FindId(hardwareId) is InstanceObject &&
                    HardwareMetadata.HasHardwareRole(doc.Objects.FindId(hardwareId).Attributes) &&
                    !bomService.GenerateBom(doc, fixture.AssemblyName).Lines.Any(line => line.Category == "Hardware"),
                "Hardware removal must update component categories and BOM counts while preserving manufactured quantities and the excluded hardware block.");
        }
        Require(!after.LinkGraph.Conflicts.Any(conflict => conflict.Status == AssemblyLinkStatuses.Open),
            "A successful hardware removal or rollback retry must not leave stale link issues.");
    }
}
