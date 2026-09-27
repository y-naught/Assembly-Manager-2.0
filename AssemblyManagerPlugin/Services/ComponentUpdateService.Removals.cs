using AssemblyManagerPlugin.Core;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace AssemblyManagerPlugin.Services;

public sealed partial class ComponentUpdateService
{
    private static List<RetainedMember> ValidateRemovedMembers(RhinoDoc doc, AssemblyStore store,
        AssemblyRecord assembly, SourceComponentInstanceRecord instance, PendingComponentUpdateRecord pending)
    {
        var result = new List<RetainedMember>();
        foreach (var id in pending.RemovedSourceNodeIds)
        {
            var source = assembly.LinkGraph.Nodes.Single(node => node.Id == id);
            if (source.Role != AssemblyLinkRoles.Source || source.SourceComponentInstanceId != instance.Id ||
                store.Assemblies.SelectMany(owner => owner.LinkGraph.Nodes).Count(node => node.ObjectId == source.ObjectId) != 1 ||
                assembly.LinkGraph.SourceComponentInstances.Count(owner => owner.SourceNodeIds.Contains(id)) != 1)
                throw new InvalidOperationException("A removed input member has shared or ambiguous ownership. No managed copies were removed.");
            var edges = assembly.LinkGraph.Edges.Where(edge => edge.ParentNodeId == id).ToList();
            var originals = edges.Where(edge => assembly.LinkGraph.Nodes.Any(node => node.Id == edge.ChildNodeId && IsOriginal(node))).ToList();
            if (originals.Count != 1)
                throw new InvalidOperationException("A removed member has no unique generated original. Resolve its links before updating membership.");
            var edge = originals[0];
            var original = assembly.LinkGraph.Nodes.Single(node => node.Id == edge.ChildNodeId);
            var originalObject = RequireObject(doc, original.ObjectId);
            var sourceObject = doc.Objects.FindId(source.ObjectId);
            var legacyWholeHardware = edge.Recipe == AssemblyLinkRecipes.BlockDefinitionPart && source.PartId == Guid.Empty &&
                original.Role == AssemblyLinkRoles.Hardware && originalObject is InstanceObject originalInstance &&
                ((sourceObject is InstanceObject sourceInstance && sourceInstance.InstanceDefinition.Id == originalInstance.InstanceDefinition.Id) ||
                 (sourceObject is null && HardwareMetadata.HasHardwareRole(originalObject.Attributes)));
            if (original.SourceComponentInstanceId != instance.Id || original.ComponentId != instance.ComponentId ||
                original.Status != AssemblyLinkStatuses.Active || edge.Status != AssemblyLinkStatuses.Active ||
                source.Metadata.ContainsKey(AssemblyLinkMetadataKeys.Quarantined) || original.Metadata.ContainsKey(AssemblyLinkMetadataKeys.Quarantined) ||
                assembly.LinkGraph.Edges.Count(item => item.ChildNodeId == original.Id) != 1 ||
                !edge.ParentToChildTransform.TryToTransform(out var placement) || placement.RigidType != TransformRigidType.Rigid ||
                (edge.Recipe is not (AssemblyLinkRecipes.DirectCopy or AssemblyLinkRecipes.HardwareCopy) && !legacyWholeHardware))
                throw new InvalidOperationException("A removed member has an unsupported or conflicted original link. Resolve it before updating membership.");
            if (doc.Objects.FindId(source.ObjectId) is { IsDeleted: false }) RequireObject(doc, source.ObjectId);
            // Deleting an input before regrouping is an explicit omission. Other link
            // conflicts (including split/copy ambiguity) are not removal authorization.
            if (assembly.LinkGraph.Conflicts.Any(conflict => conflict.Status == AssemblyLinkStatuses.Open &&
                    (conflict.NodeId == source.Id || conflict.NodeId == original.Id || conflict.EdgeId == edge.Id) &&
                    !(conflict.NodeId == source.Id && conflict.ConflictType == AssemblyLinkConflictTypes.SourceDeleted) &&
                    !IsAdoptableDuplicate(assembly, conflict, pending.AddedObjectIds)))
                throw new InvalidOperationException("A removed member has an unresolved link issue. Resolve it before removing its managed copies.");
            result.Add(new RetainedMember(source, original, placement,
                source.PartId == Guid.Empty ? "hardware" : $"part:{source.PartId:D}"));
        }
        return result;
    }

    private RemovalBatch PrepareRemovalBatch(RhinoDoc doc, AssemblyStore store, AssemblyRecord assembly, IReadOnlyList<UpdatePlan> plans)
    {
        var graph = assembly.LinkGraph;
        var sources = plans.SelectMany(plan => plan.Instances).SelectMany(instance => instance.RemovedMembers)
            .Select(member => member.Source).ToList();
        var nodeIds = sources.Select(node => node.Id).ToHashSet();
        if (nodeIds.Count == 0) return new RemovalBatch(nodeIds, new(), new(), new());
        var sourceObjectIds = sources.Select(node => node.ObjectId).ToHashSet();
        var validatedOriginalIds = plans.SelectMany(plan => plan.Instances).SelectMany(instance => instance.RemovedMembers)
            .Select(member => member.Original.Id).ToHashSet();
        var pending = new Queue<Guid>(nodeIds);
        var affectedFlats = new HashSet<Guid>();
        while (pending.Count > 0)
        {
            var parentId = pending.Dequeue();
            var parent = graph.Nodes.Single(node => node.Id == parentId);
            foreach (var edge in graph.Edges.Where(edge => edge.ParentNodeId == parentId))
            {
                var child = graph.Nodes.Single(node => node.Id == edge.ChildNodeId);
                if (child.Role == AssemblyLinkRoles.FlatPart && edge.Recipe == AssemblyLinkRecipes.LayFlat)
                {
                    if (child.PartId == Guid.Empty || child.PartId != parent.PartId)
                        throw new InvalidOperationException("A removed member has an ambiguous flat-part relationship.");
                    affectedFlats.Add(child.Id);
                    continue;
                }
                if ((!IsOriginal(child) && child.Role != AssemblyLinkRoles.CopiedComponent) ||
                    child.Status != AssemblyLinkStatuses.Active ||
                    child.Metadata.ContainsKey(AssemblyLinkMetadataKeys.Quarantined) || edge.Status != AssemblyLinkStatuses.Active ||
                    graph.Edges.Count(item => item.ChildNodeId == child.Id) != 1)
                    throw new InvalidOperationException($"Linked object {child.ObjectId:D} is an unsupported consumer or has an unresolved copy relationship. No copies were removed.");
                if ((child.SourceComponentInstanceId == Guid.Empty || child.SourceComponentInstanceId != parent.SourceComponentInstanceId) &&
                    !IsLegacyOwnedComponentCopy(doc, parent, child, edge, validatedOriginalIds))
                    throw new InvalidOperationException($"Linked object {child.ObjectId:D} has conflicting component-occurrence ownership. No copies were removed.");
                RequireObject(doc, child.ObjectId);
                if (nodeIds.Add(child.Id)) pending.Enqueue(child.Id);
            }
        }
        var removedNodes = graph.Nodes.Where(node => nodeIds.Contains(node.Id)).ToList();
        var removedObjectIds = removedNodes.Select(node => node.ObjectId).ToHashSet();
        var accountedTargets = removedObjectIds.Concat(graph.Nodes.Where(node => affectedFlats.Contains(node.Id)).Select(node => node.ObjectId)).ToHashSet();
        if (assembly.GeometryReferences.Any(reference => removedObjectIds.Contains(reference.SourceObjectId) &&
                !accountedTargets.Contains(reference.TargetObjectId)))
            throw new InvalidOperationException("A removed member has an unsupported legacy dependent object. Resolve that dependency before changing membership.");
        if (store.Assemblies.Where(owner => owner.Id != assembly.Id).Any(owner =>
                owner.LinkGraph.Nodes.Any(node => removedObjectIds.Contains(node.ObjectId)) ||
                owner.GeometryReferences.Any(reference => removedObjectIds.Contains(reference.SourceObjectId) || removedObjectIds.Contains(reference.TargetObjectId)) ||
                owner.PendingComponentUpdates.Any(update => update.AddedObjectIds.Any(removedObjectIds.Contains))))
            throw new InvalidOperationException("A removed member or its generated copy is used by another assembly. Resolve that dependency before changing membership.");
        var reparentings = new List<FlatReparenting>();
        var retiringFlats = new List<Guid>();
        foreach (var flatId in affectedFlats)
        {
            var flat = graph.Nodes.Single(node => node.Id == flatId);
            var flatObject = RequireObject(doc, flat.ObjectId);
            var edge = graph.Edges.SingleOrDefault(item => item.ChildNodeId == flatId);
            if (flat.Status != AssemblyLinkStatuses.Active || flat.Metadata.ContainsKey(AssemblyLinkMetadataKeys.Quarantined) ||
                edge is null || edge.Status != AssemblyLinkStatuses.Active ||
                graph.Conflicts.Any(conflict => conflict.Status == AssemblyLinkStatuses.Open && (conflict.NodeId == flatId || conflict.EdgeId == edge.Id)))
                throw new InvalidOperationException("A flat representative of a removed member needs review. Resolve it before changing membership.");
            var matchingAdditions = plans.SelectMany(plan => plan.Additions.Select(addition => (Plan: plan, Addition: addition)))
                .Where(item => item.Addition.PartId == flat.PartId).ToList();
            var survivingSources = graph.Nodes.Any(node => node.Role == AssemblyLinkRoles.Source && node.PartId == flat.PartId && !nodeIds.Contains(node.Id));
            var part = assembly.Parts.Single(item => item.Id == flat.PartId);
            var legacySources = part.SourceObjectIds.Any(id => !sourceObjectIds.Contains(id) && !graph.Nodes.Any(node => node.ObjectId == id));
            if (!survivingSources && !legacySources && matchingAdditions.Count == 0)
            {
                if (graph.Edges.Any(item => item.ParentNodeId == flat.Id) ||
                    store.Assemblies.Any(owner => owner.GeometryReferences.Any(reference => reference.SourceObjectId == flat.ObjectId)) ||
                    store.Assemblies.Where(owner => owner.Id != assembly.Id).Any(owner => owner.LinkGraph.Nodes.Any(node => node.ObjectId == flat.ObjectId)))
                    throw new InvalidOperationException("The last flat representative is used by another linked object. Resolve that dependency before removing its part category.");
                // Preserve the flat node and part record until the normal flat synchronizer
                // retires its owned geometry/labels and updates row headers after recategorization.
                retiringFlats.Add(flat.Id);
                continue;
            }
            FlatReparenting? replacement = null;
            foreach (var candidate in graph.Nodes.Where(node => node.PartId == flat.PartId && IsOriginal(node) &&
                         node.Status == AssemblyLinkStatuses.Active && !nodeIds.Contains(node.Id)))
            {
                var value = doc.Objects.FindId(candidate.ObjectId);
                if (value is null || value.IsDeleted || candidate.Metadata.ContainsKey(AssemblyLinkMetadataKeys.Quarantined)) continue;
                if (!IsHealthyFlatReplacementParent(doc, assembly, candidate, value)) continue;
                if (MaterialAssignment.GetCategorizationMaterialId(value.Attributes) != MaterialAssignment.GetCategorizationMaterialId(flatObject.Attributes)) continue;
                if (ComponentFlatReparentMatcher.TryGetPlacement(value, flatObject, doc.ModelAbsoluteTolerance, _fingerprints, out var placement))
                {
                    replacement = new FlatReparenting(flat, candidate.ObjectId, false, placement);
                    break;
                }
            }
            if (replacement is null)
            {
                foreach (var (plan, addition) in matchingAdditions)
                {
                    if (!ComponentFlatReparentMatcher.TryGetPlacement(addition.Object, flatObject, doc.ModelAbsoluteTolerance, _fingerprints, out var placement)) continue;
                    var fromInput = plan.Pending.AdditionOrigin == ComponentAdditionOrigins.Input;
                    if (fromInput)
                    {
                        if (!plan.Instances[0].SourceToOriginal.TryGetInverse(out var inverse)) continue;
                        placement *= inverse;
                    }
                    replacement = new FlatReparenting(flat, addition.Object.Id, fromInput, placement);
                    break;
                }
            }
            if (replacement is null)
                throw new InvalidOperationException("Gazelle could not prove a replacement parent for an existing flat part without moving it. Restore that member or review its flat layout before updating membership.");
            reparentings.Add(replacement);
        }
        return new RemovalBatch(nodeIds, removedNodes.Where(node => node.Role != AssemblyLinkRoles.Source).Select(node => node.ObjectId).ToList(),
            reparentings, retiringFlats);
    }

    private static bool IsLegacyOwnedComponentCopy(RhinoDoc doc, AssemblyLinkNodeRecord parent,
        AssemblyLinkNodeRecord child, AssemblyLinkEdgeRecord edge, ISet<Guid> validatedOriginalIds)
    {
        // Earlier Copy / Orient Components builds omitted the child's occurrence ID.
        // Preflight's FindCopiedLayouts already proved each of these direct original
        // copies has a complete, unambiguous group, matching geometry and placement,
        // and no open link conflicts. Its authoritative parent edge is sufficient when
        // only this redundant ID is absent; never replace contradictory nonempty IDs or
        // infer ownership from a layer name, copied user string, or similar geometry.
        if (child.Role != AssemblyLinkRoles.CopiedComponent || child.SourceComponentInstanceId != Guid.Empty ||
            !validatedOriginalIds.Contains(parent.Id) || parent.SourceComponentInstanceId == Guid.Empty ||
            child.ComponentId != parent.ComponentId || child.PartId != parent.PartId)
            return false;
        if (edge.Recipe is AssemblyLinkRecipes.DirectCopy or AssemblyLinkRecipes.HardwareCopy)
            return true;
        // Historical whole hardware instances used the block-leaf recipe. Do not
        // extend this compatibility allowance to extracted block-part geometry.
        return edge.Recipe == AssemblyLinkRecipes.BlockDefinitionPart && parent.PartId == Guid.Empty &&
            doc.Objects.FindId(parent.ObjectId) is InstanceObject original &&
            doc.Objects.FindId(child.ObjectId) is InstanceObject copy &&
            original.InstanceDefinition.Id == copy.InstanceDefinition.Id;
    }

    private void ApplyRemovals(RhinoDoc doc, AssemblyRecord assembly, RemovalBatch batch,
        Dictionary<Guid, ObjectAttributes> savedAttributes, List<uint> deletedSerialNumbers)
    {
        if (batch.NodeIds.Count == 0) return;
        var graph = assembly.LinkGraph;
        foreach (var reparenting in batch.Reparentings)
        {
            var parent = graph.Nodes.Single(node => node.ObjectId == reparenting.ParentObjectId);
            if (reparenting.ParentIsAddedSource)
            {
                var parentId = parent.Id;
                parent = graph.Nodes.Single(node => IsOriginal(node) && graph.Edges.Any(edge => edge.ParentNodeId == parentId && edge.ChildNodeId == node.Id));
            }
            savedAttributes.TryAdd(reparenting.Flat.ObjectId, RequireObject(doc, reparenting.Flat.ObjectId).Attributes.Duplicate());
            _lineage.RegisterDerived(doc, assembly, parent.ObjectId, parent.Role, reparenting.Flat.ObjectId,
                AssemblyLinkRoles.FlatPart, reparenting.Placement, AssemblyLinkRecipes.LayFlat, partId: reparenting.Flat.PartId,
                recipeMetadata: new Dictionary<string, string> { ["orientation"] = "LargestFaceToWorldXY", ["longAxis"] = "Y", ["layout"] = "MaterialThicknessRow" });
            // Flat recovery references are not source-to-original migration records.
            assembly.GeometryReferences.RemoveAll(reference => reference.TargetObjectId == reparenting.Flat.ObjectId);
        }
        var removedNodes = graph.Nodes.Where(node => batch.NodeIds.Contains(node.Id)).ToList();
        var objectIds = removedNodes.Select(node => node.ObjectId).ToHashSet();
        foreach (var source in removedNodes.Where(node => node.Role == AssemblyLinkRoles.Source))
        {
            if (doc.Objects.FindId(source.ObjectId) is not { IsDeleted: false } value) continue;
            savedAttributes.TryAdd(source.ObjectId, value.Attributes.Duplicate());
            using var attributes = value.Attributes.Duplicate();
            AssemblyLineageService.ClearLinkMetadata(attributes);
            RequireModify(doc, value.Id, attributes);
        }
        foreach (var id in batch.GeneratedObjectIds)
        {
            var value = RequireObject(doc, id);
            var serial = value.RuntimeSerialNumber;
            if (!doc.Objects.Delete(value, true)) throw new InvalidOperationException("Rhino could not remove a retired component copy; the membership update was rolled back.");
            deletedSerialNumbers.Add(serial);
        }
        var retiredEdgeIds = graph.Edges.Where(edge => batch.NodeIds.Contains(edge.ParentNodeId) || batch.NodeIds.Contains(edge.ChildNodeId))
            .Select(edge => edge.Id).ToHashSet();
        graph.Edges.RemoveAll(edge => retiredEdgeIds.Contains(edge.Id));
        graph.Nodes.RemoveAll(node => batch.NodeIds.Contains(node.Id));
        graph.Conflicts.RemoveAll(conflict => batch.NodeIds.Contains(conflict.NodeId) || retiredEdgeIds.Contains(conflict.EdgeId));
        foreach (var instance in graph.SourceComponentInstances) instance.SourceNodeIds.RemoveAll(batch.NodeIds.Contains);
        assembly.GeometryReferences.RemoveAll(reference => objectIds.Contains(reference.SourceObjectId) || objectIds.Contains(reference.TargetObjectId));
        foreach (var part in assembly.Parts)
        {
            part.SourceObjectIds.RemoveAll(objectIds.Contains);
            part.GeneratedObjectIds.RemoveAll(objectIds.Contains);
            part.Quantity = part.SourceObjectIds.Count;
        }
        assembly.Hardware.RemoveAll(hardware => objectIds.Contains(hardware.SourceObjectId) || objectIds.Contains(hardware.GeneratedObjectId));
        foreach (var component in assembly.Components)
        {
            component.ObjectIds.RemoveAll(objectIds.Contains);
            foreach (var ids in component.RepresentativeObjectIdsByPartName.Values) ids.RemoveAll(objectIds.Contains);
        }
    }

    private static bool IsHealthyFlatReplacementParent(RhinoDoc doc, AssemblyRecord assembly,
        AssemblyLinkNodeRecord candidate, RhinoObject value)
    {
        var incoming = assembly.LinkGraph.Edges.Where(edge => edge.ChildNodeId == candidate.Id).ToList();
        if (incoming.Count != 1) return false;
        var edge = incoming[0];
        var source = assembly.LinkGraph.Nodes.Single(node => node.Id == edge.ParentNodeId);
        var input = doc.Objects.FindId(source.ObjectId);
        return edge.Status == AssemblyLinkStatuses.Active && edge.Recipe == AssemblyLinkRecipes.DirectCopy &&
            source.Role == AssemblyLinkRoles.Source && source.Status == AssemblyLinkStatuses.Active &&
            source.PartId == candidate.PartId && !source.Metadata.ContainsKey(AssemblyLinkMetadataKeys.Quarantined) &&
            input is { IsDeleted: false } &&
            !assembly.LinkGraph.Conflicts.Any(conflict => conflict.Status == AssemblyLinkStatuses.Open &&
                (conflict.NodeId == candidate.Id || conflict.NodeId == source.Id || conflict.EdgeId == edge.Id)) &&
            edge.ParentToChildTransform.TryToTransform(out var placement) &&
            ComponentPlacementMatcher.MatchesGeometry(input, value, placement, doc.ModelAbsoluteTolerance) &&
            MaterialAssignment.GetCategorizationMaterialId(input.Attributes) == MaterialAssignment.GetCategorizationMaterialId(value.Attributes);
    }

    private sealed record FlatReparenting(AssemblyLinkNodeRecord Flat, Guid ParentObjectId, bool ParentIsAddedSource, Transform Placement);
    private sealed record RemovalBatch(HashSet<Guid> NodeIds, List<Guid> GeneratedObjectIds,
        List<FlatReparenting> Reparentings, List<Guid> RetiringFlatNodeIds);
}
