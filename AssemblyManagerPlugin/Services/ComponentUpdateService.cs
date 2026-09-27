using System.Drawing;
using AssemblyManagerPlugin.Core;
using AssemblyManagerPlugin.Geometry;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace AssemblyManagerPlugin.Services;

public sealed record ComponentUpdateResult(string AssemblyName, string ComponentName, int AddedObjectCount, int OccurrenceCount);

/// <summary>
/// Explicit, two-step component editing. Staging records a regrouped original or an input addition;
/// applying updates only that occurrence and its linked input/copied geometry. Input regrouping
/// may retire explicitly omitted members' managed copies; input objects themselves are preserved.
/// Ambiguous membership, dependencies, and placements fail closed.
/// </summary>
public sealed partial class ComponentUpdateService
{
    private readonly AssemblyRepository _repository;
    private readonly LayerService _layers;
    private readonly GeometryFingerprintService _fingerprints;
    private readonly AssemblyLineageService _lineage;
    private readonly Func<bool> _colorizePartsEnabled;

    public ComponentUpdateService(AssemblyRepository repository, LayerService layers,
        GeometryFingerprintService fingerprints, AssemblyLineageService lineage, Func<bool>? colorizePartsEnabled = null)
    {
        _repository = repository;
        _layers = layers;
        _fingerprints = fingerprints;
        _lineage = lineage;
        _colorizePartsEnabled = colorizePartsEnabled ?? (() => true);
    }

    public ComponentUpdateResult Stage(RhinoDoc doc, string assemblyName, Guid componentId, Guid regroupedGroupId)
    {
        var store = _repository.Load(doc);
        var assembly = store.FindAssembly(assemblyName)
                       ?? throw new InvalidOperationException($"Assembly '{assemblyName}' was not found.");
        var component = assembly.Components.SingleOrDefault(item => item.Id == componentId)
                        ?? throw new InvalidOperationException("The selected component no longer exists.");
        var group = FindGroup(doc, regroupedGroupId);
        var ids = GroupMembers(doc, group).ToHashSet();
        var instances = assembly.LinkGraph.SourceComponentInstances.Where(item => item.ComponentId == componentId).ToList();
        var donors = instances.Where(instance => OriginalNodes(assembly, instance).Any(node => ids.Contains(node.ObjectId))).ToList();
        if (donors.Count != 1)
            throw new InvalidOperationException("Select one regrouped ORIGINAL ASSEMBLIES component occurrence, with every original member retained. Do not combine occurrences.");
        var donor = donors[0];
        var expected = OriginalNodes(assembly, donor).Select(node => node.ObjectId).ToHashSet();
        if (!expected.IsSubsetOf(ids))
            throw new InvalidOperationException("The regrouped component is missing retained members. Removing, splitting, or replacing member identities is not supported by Update Component yet.");
        var added = ids.Except(expected).OrderBy(id => id).ToList();
        var previous = assembly.PendingComponentUpdates.SingleOrDefault(item => item.TemplateInstanceId == donor.Id);
        if (previous is not null && previous.AdditionOrigin != ComponentAdditionOrigins.Original)
            throw new InvalidOperationException("This occurrence already has input additions staged. Use Update Assembly before regrouping its ORIGINAL ASSEMBLIES component.");
        var pending = new PendingComponentUpdateRecord
        {
            ComponentId = componentId,
            TemplateInstanceId = donor.Id,
            RegroupedGroupId = group.Id,
            PreviousGeneratedGroupId = previous?.PreviousGeneratedGroupId ?? donor.GeneratedGroupId,
            AddedObjectIds = added,
            InstanceIds = new List<Guid> { donor.Id },
            MemberNodeIdsByInstance = new Dictionary<Guid, List<Guid>>
            {
                [donor.Id] = donor.SourceNodeIds.OrderBy(id => id).ToList()
            }
        };
        using (var plan = Preflight(doc, store, assembly, pending, staged: false))
        {
            // Preflight also proves this occurrence's copied layouts can receive the additions. No user
            // geometry, layer, or group is changed until the explicit Update Assembly action.
        }
        donor.GeneratedGroupId = group.Id;
        donor.GeneratedGroupName = group.Name;
        donor.UpdatedAt = DateTimeOffset.UtcNow;
        component.InstanceGroupNames = instances.Select(instance => instance.GeneratedGroupName)
            .Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        assembly.PendingComponentUpdates.RemoveAll(item => item.TemplateInstanceId == donor.Id);
        assembly.PendingComponentUpdates.Add(pending);
        assembly.UpdatedAt = DateTimeOffset.UtcNow;
        using var mutation = AssemblyLinkMutationGate.Enter();
        _repository.Save(doc, store);
        return new ComponentUpdateResult(assembly.Name, component.Name, added.Count, 1);
    }

    /// <summary>
    /// Adds an existing object to one registered input group and stages downstream creation.
    /// The selected source UUID, geometry, layer, and material are retained. Repeated calls
    /// accumulate additions; no generated group is rebound and no copies are made here.
    /// </summary>
    public ComponentUpdateResult StageInputAddition(RhinoDoc doc, string assemblyName, Guid sourceInstanceId, Guid addedObjectId)
    {
        var store = _repository.Load(doc);
        var assembly = store.FindAssembly(assemblyName)
            ?? throw new InvalidOperationException($"Assembly '{assemblyName}' was not found.");
        var instance = assembly.LinkGraph.SourceComponentInstances.SingleOrDefault(item => item.Id == sourceInstanceId)
            ?? throw new InvalidOperationException("The selected input component occurrence no longer exists.");
        var group = FindGroup(doc, instance.SourceGroupId);
        var component = assembly.Components.Single(item => item.Id == instance.ComponentId);
        var previous = assembly.PendingComponentUpdates.SingleOrDefault(item => item.TemplateInstanceId == instance.Id);
        if (previous is not null && previous.AdditionOrigin != ComponentAdditionOrigins.Input)
            throw new InvalidOperationException("This occurrence already has an ORIGINAL ASSEMBLIES update staged. Use Update Assembly before adding parts to its input group.");
        if (previous?.AddedObjectIds.Contains(addedObjectId) == true)
            throw new InvalidOperationException("This item is already staged for this component. Click Update Assembly to apply it.");
        var pending = new PendingComponentUpdateRecord
        {
            Id = previous?.Id ?? Guid.NewGuid(),
            CreatedAt = previous?.CreatedAt ?? DateTimeOffset.UtcNow,
            ComponentId = component.Id,
            TemplateInstanceId = instance.Id,
            AdditionOrigin = ComponentAdditionOrigins.Input,
            // Keep this INPUT identity distinct from GeneratedGroupId. Older builds that
            // do not know AdditionOrigin then reject the plan instead of inverse-copying it.
            RegroupedGroupId = group.Id,
            PreviousGeneratedGroupId = instance.GeneratedGroupId,
            PreviousSourceGroupId = previous?.PreviousSourceGroupId ?? instance.SourceGroupId,
            AddedObjectIds = (previous?.AddedObjectIds ?? new List<Guid>()).Append(addedObjectId).ToList(),
            RemovedSourceNodeIds = previous?.RemovedSourceNodeIds.ToList() ?? new List<Guid>(),
            InstanceIds = new List<Guid> { instance.Id },
            MemberNodeIdsByInstance = previous?.MemberNodeIdsByInstance ?? new Dictionary<Guid, List<Guid>>
            {
                [instance.Id] = instance.SourceNodeIds.OrderBy(id => id).ToList()
            }
        };
        // Check the prospective membership without changing Rhino or persisted metadata.
        using var plan = Preflight(doc, store, assembly, pending, staged: false, inputObjectToGroup: addedObjectId);
        var value = RequireObject(doc, addedObjectId);
        using var attributes = value.Attributes.Duplicate();
        using var mutation = AssemblyLinkMutationGate.Enter();
        try
        {
            if (!(value.Attributes.GetGroupList() ?? Array.Empty<int>()).Contains(group.Index))
                AddToGroup(doc, group.Index, addedObjectId);
            assembly.PendingComponentUpdates.RemoveAll(item => item.TemplateInstanceId == instance.Id);
            assembly.PendingComponentUpdates.Add(pending);
            assembly.UpdatedAt = DateTimeOffset.UtcNow;
            _repository.Save(doc, store);
        }
        catch
        {
            RequireModify(doc, addedObjectId, attributes);
            throw;
        }
        return new ComponentUpdateResult(assembly.Name, component.Name, pending.AddedObjectIds.Count, 1);
    }

    public IReadOnlyList<Guid> ApplyPending(RhinoDoc doc, AssemblyStore store, AssemblyRecord assembly)
    {
        EnsureInputGroupsReady(assembly);
        if (assembly.PendingComponentUpdates.Count == 0)
            return Array.Empty<Guid>();
        var plans = new List<UpdatePlan>();
        try
        {
            // Validate all staged components before applying any of them, so a stale second
            // component cannot leave a partially applied manual batch.
            foreach (var pending in assembly.PendingComponentUpdates)
                plans.Add(Preflight(doc, store, assembly, pending, staged: true));
            var allAdditions = plans.SelectMany(plan => plan.Additions).ToList();
            foreach (var addition in allAdditions)
            {
                addition.PartId = Guid.Empty;
                addition.SharedAddition = null;
            }
            AssignPartCategories(doc, assembly, allAdditions);
            var removalBatch = PrepareRemovalBatch(doc, store, assembly, plans);
            var reservedHardwareNames = assembly.Hardware.Select(hardware => hardware.LayerName)
                .Concat(allAdditions.Where(addition => addition.Hardware is not null).Select(addition => addition.LayerName))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var snapshot = AssemblyRepository.CreateAssemblySnapshot(assembly);
            var addedIds = new List<Guid>();
            var originalAttributes = new Dictionary<Guid, ObjectAttributes>();
            var createdGroups = new List<int>();
            var deletedSerialNumbers = new List<uint>();
            var existingLayers = doc.Layers.Where(layer => !layer.IsDeleted).Select(layer => layer.Id).ToHashSet();
            var sourceIds = new List<Guid>();
            using var mutation = AssemblyLinkMutationGate.Enter();
            try
            {
                foreach (var plan in plans)
                    ApplyPlan(doc, assembly, plan, addedIds, originalAttributes, createdGroups, sourceIds, reservedHardwareNames);
                ApplyRemovals(doc, assembly, removalBatch, originalAttributes, deletedSerialNumbers);
                var adoptedIds = allAdditions.Select(addition => addition.Object.Id).ToList();
                foreach (var ownerAssembly in store.Assemblies)
                    foreach (var conflict in ownerAssembly.LinkGraph.Conflicts.Where(conflict =>
                                 conflict.Status == AssemblyLinkStatuses.Open && IsAdoptableDuplicate(ownerAssembly, conflict, adoptedIds) &&
                                 IsUniqueLiveIdentity(doc, ownerAssembly, conflict.NodeId)))
                    {
                        conflict.Status = AssemblyLinkStatuses.Resolved;
                        conflict.ResolvedAt = DateTimeOffset.UtcNow;
                    }
                assembly.PendingComponentUpdates.Clear();
                assembly.LastBillOfMaterials = null;
                assembly.LastMaterialEstimate = null;
                assembly.NestingEstimates.Clear();
                assembly.UpdatedAt = DateTimeOffset.UtcNow;
                return sourceIds;
            }
            catch
            {
                // Only objects created by this attempt are deleted. The donor additions and
                // every pre-existing object retain their UUID and original attributes.
                foreach (var id in addedIds.AsEnumerable().Reverse())
                    doc.Objects.Delete(id, true);
                foreach (var serial in deletedSerialNumbers.AsEnumerable().Reverse())
                    if (!doc.Objects.Undelete(serial))
                        RhinoApp.WriteLine("Gazelle could not restore one retired object during rollback. Use Rhino Undo before continuing.");
                foreach (var (id, attributes) in originalAttributes)
                    doc.Objects.ModifyAttributes(id, attributes, true);
                foreach (var index in createdGroups.AsEnumerable().Reverse())
                    doc.Groups.Delete(index);
                foreach (var layer in doc.Layers.Where(layer => !layer.IsDeleted && !existingLayers.Contains(layer.Id))
                             .OrderByDescending(layer => layer.FullPath.Length).ToList())
                    _layers.TryDeleteLayerIfEmpty(doc, layer.FullPath);
                foreach (var property in typeof(AssemblyRecord).GetProperties().Where(property => property.CanWrite))
                    property.SetValue(assembly, property.GetValue(snapshot));
                throw;
            }
            finally
            {
                foreach (var attributes in originalAttributes.Values) attributes.Dispose();
            }
        }
        finally
        {
            foreach (var plan in plans) plan.Dispose();
        }
    }

    private UpdatePlan Preflight(RhinoDoc doc, AssemblyStore store, AssemblyRecord assembly,
        PendingComponentUpdateRecord pending, bool staged, Guid? inputObjectToGroup = null, bool allowInputRebind = false)
    {
        if (pending.AdditionOrigin is not (ComponentAdditionOrigins.Input or ComponentAdditionOrigins.Original))
            throw new InvalidOperationException("The component addition origin is unsupported. Use the same or a newer Gazelle version.");
        var fromInput = pending.AdditionOrigin == ComponentAdditionOrigins.Input;
        if ((!fromInput && pending.RemovedSourceNodeIds.Count > 0) ||
            pending.RemovedSourceNodeIds.Distinct().Count() != pending.RemovedSourceNodeIds.Count)
            throw new InvalidOperationException("The staged component removal identities are invalid.");
        var recovery = fromInput
            ? "Restore the registered input group, its retained members and staged additions, then retry AddPartToComponent or Update Assembly."
            : "Run Update Component again with the complete regrouped ORIGINAL ASSEMBLIES occurrence.";
        var component = assembly.Components.SingleOrDefault(item => item.Id == pending.ComponentId)
                        ?? throw new InvalidOperationException("A staged component no longer exists. " + recovery);
        var donor = assembly.LinkGraph.SourceComponentInstances.SingleOrDefault(item =>
                        item.Id == pending.TemplateInstanceId && item.ComponentId == component.Id)
                    ?? throw new InvalidOperationException("The selected component occurrence no longer exists. " + recovery);
        if (!pending.InstanceIds.Contains(donor.Id))
            throw new InvalidOperationException("The staged component does not include its selected occurrence. " + recovery);
        if (pending.RemovedSourceNodeIds.Except(donor.SourceNodeIds).Any() ||
            pending.RemovedSourceNodeIds.Count == donor.SourceNodeIds.Count)
            throw new InvalidOperationException("Regrouping must retain at least one original input member from exactly one component occurrence.");
        // Earlier versions saved the complete component cohort. The regrouped occurrence and
        // its saved retained-member proof are authoritative; never fan out those legacy lists.
        // Unchanged siblings need not be present, editable, or geometrically distinguishable.
        var instances = new[] { donor };
        var group = FindGroup(doc, pending.RegroupedGroupId);
        if ((fromInput && ((!allowInputRebind && donor.SourceGroupId != group.Id) || donor.GeneratedGroupId == group.Id)) ||
            (!fromInput && staged && donor.GeneratedGroupId != group.Id))
            throw new InvalidOperationException("The staged component's group identity changed. " + recovery);
        var originals = OriginalNodes(assembly, donor);
        var oldIds = originals.Select(node => node.ObjectId).ToHashSet();
        var retainedIds = fromInput
            ? assembly.LinkGraph.Nodes.Where(node => donor.SourceNodeIds.Contains(node.Id) && !pending.RemovedSourceNodeIds.Contains(node.Id)).Select(node => node.ObjectId).ToHashSet()
            : oldIds;
        var actualIds = GroupMembers(doc, group).ToHashSet();
        if (fromInput && inputObjectToGroup.HasValue) actualIds.Add(inputObjectToGroup.Value);
        if (!actualIds.SetEquals(retainedIds.Concat(pending.AddedObjectIds)))
            throw new InvalidOperationException("The staged group membership changed or retained members are missing. " + recovery);
        if (pending.AddedObjectIds.Count != pending.AddedObjectIds.Distinct().Count())
            throw new InvalidOperationException("The staged addition identities are ambiguous.");
        var reservedIds = store.Assemblies.SelectMany(owner => owner.PendingComponentUpdates
                .Where(other => owner.Id != assembly.Id || other.TemplateInstanceId != pending.TemplateInstanceId))
            .SelectMany(other => other.AddedObjectIds).ToHashSet();
        if (pending.AddedObjectIds.Any(reservedIds.Contains))
            throw new InvalidOperationException("An added object is already staged for another component. Use an independent copy for each component design.");

        var allOwnedIds = store.Assemblies.SelectMany(item => item.LinkGraph.Nodes).Select(node => node.ObjectId)
            .Concat(store.Assemblies.SelectMany(item => item.GeometryReferences).SelectMany(reference => new[] { reference.SourceObjectId, reference.TargetObjectId }))
            .Where(id => id != Guid.Empty).ToHashSet();
        var additions = new List<Addition>();
        try
        {
            foreach (var id in pending.AddedObjectIds)
            {
                var value = RequireObject(doc, id);
                if (oldIds.Contains(id) || allOwnedIds.Contains(id))
                    throw new InvalidOperationException("An added item is already linked to an assembly. Use a new, independent copy, not another managed occurrence.");
                if (fromInput && (value.Attributes.GetGroupList() ?? Array.Empty<int>()).Any(index =>
                        index != group.Index && ((doc.Groups.GroupMembers(index) ?? Array.Empty<RhinoObject>()).Any(member => allOwnedIds.Contains(member.Id)) ||
                            store.Assemblies.SelectMany(owner => owner.LinkGraph.SourceComponentInstances).Any(owner =>
                                owner.SourceGroupId == doc.Groups.FindIndex(index)?.Id || owner.GeneratedGroupId == doc.Groups.FindIndex(index)?.Id))))
                    throw new InvalidOperationException("The new item already belongs to another managed component group. Use an independent item outside that group.");
                ValidateInheritedIdentity(doc, store, value);
                var isHardware = HardwareMetadata.TryGetFromObject(value, out var hardware);
                PartCandidate? candidate = null;
                if (isHardware)
                {
                    if (value is not InstanceObject && value.Geometry is not Brep && value.Geometry is not Extrusion)
                        throw new InvalidOperationException("Added hardware must be a solid polysurface, extrusion, or marked hardware block instance.");
                }
                else
                {
                    if (!_fingerprints.TryCreatePartCandidate(value, out candidate, out var warning))
                        throw new InvalidOperationException($"The new part cannot be added: {warning}");
                    candidate.MaterialId = MaterialAssignment.GetCategorizationMaterialId(value.Attributes);
                }
                additions.Add(new Addition(value, isHardware ? hardware : null, candidate)
                {
                    LayerName = isHardware ? HardwareLayerName(assembly, hardware) : string.Empty
                });
            }
            var plans = new List<InstancePlan>();
            var tolerance = Math.Max(1e-7, doc.ModelAbsoluteTolerance);
            foreach (var instance in instances)
            {
                if (!pending.MemberNodeIdsByInstance.TryGetValue(instance.Id, out var oldNodeIds) ||
                    !instance.SourceNodeIds.ToHashSet().SetEquals(oldNodeIds))
                    throw new InvalidOperationException("A staged occurrence's retained member identities changed. " + recovery);
                var removedMembers = ValidateRemovedMembers(doc, store, assembly, instance, pending);
                var members = ValidateMembers(doc, store, assembly, instance, pending, tolerance, removedMembers);
                var allMembers = members.Concat(removedMembers).ToList();
                var generatedGroup = fromInput ? FindGroup(doc, instance.GeneratedGroupId) : group;
                var sourceGroupId = fromInput ? group.Id : instance.SourceGroupId;
                if (store.Assemblies.SelectMany(owner => owner.LinkGraph.SourceComponentInstances
                        .Where(other => owner.Id != assembly.Id || other.Id != instance.Id))
                    .Any(other => other.SourceGroupId == generatedGroup.Id || other.GeneratedGroupId == generatedGroup.Id ||
                                  (sourceGroupId != Guid.Empty &&
                                   (other.SourceGroupId == sourceGroupId || other.GeneratedGroupId == sourceGroupId)) ||
                                  (instance.SourceGroupId != Guid.Empty && other.SourceGroupId == instance.SourceGroupId)))
                    throw new InvalidOperationException("A component group is also owned by another assembly or occurrence. Structural updates to shared or downstream component groups need explicit membership support and cannot be applied safely yet.");
                var expectedIds = allMembers.Select(member => member.Original.ObjectId).ToHashSet();
                if (!fromInput) expectedIds.UnionWith(pending.AddedObjectIds);
                if (!GroupMembers(doc, generatedGroup).ToHashSet().SetEquals(expectedIds))
                    throw new InvalidOperationException("The selected component occurrence has changed group membership. Restore its retained members before applying this design.");
                var sourceGroup = sourceGroupId == Guid.Empty ? null : FindGroup(doc, sourceGroupId);
                if (sourceGroup is not null)
                {
                    var sourceIds = GroupMembers(doc, sourceGroup).ToHashSet();
                    if (fromInput && inputObjectToGroup.HasValue) sourceIds.Add(inputObjectToGroup.Value);
                    var expectedSourceIds = members.Select(member => member.Source.ObjectId).ToHashSet();
                    if (fromInput) expectedSourceIds.UnionWith(pending.AddedObjectIds);
                    if (!sourceIds.SetEquals(expectedSourceIds))
                        throw new InvalidOperationException("The input component group has changed membership. Restore it before updating the component.");
                }
                var copiedLayouts = FindCopiedLayouts(doc, assembly, allMembers, tolerance);
                foreach (var layout in copiedLayouts)
                {
                    var layoutGroupId = doc.Groups.FindIndex(layout.GroupIndex).Id;
                    if (store.Assemblies.SelectMany(owner => owner.LinkGraph.SourceComponentInstances)
                        .Any(other => other.SourceGroupId == layoutGroupId || other.GeneratedGroupId == layoutGroupId))
                        throw new InvalidOperationException("A copied-component group is also an input or original group of an assembly. Structural updates to that shared downstream group are not supported yet.");
                }
                plans.Add(new InstancePlan(instance, members, members[0].SourceToOriginal,
                    sourceGroup?.Index ?? -1, copiedLayouts) { RemovedMembers = removedMembers });
            }
            AssignPartCategories(doc, assembly, additions);
            return new UpdatePlan(component, pending, additions, plans);
        }
        catch
        {
            foreach (var addition in additions) addition.Dispose();
            throw;
        }
    }

    private List<RetainedMember> ValidateMembers(RhinoDoc doc, AssemblyStore store, AssemblyRecord assembly,
        SourceComponentInstanceRecord instance, PendingComponentUpdateRecord pending, double tolerance,
        IReadOnlyList<RetainedMember> removedMembers)
    {
        if (!string.Equals(instance.Status, AssemblyLinkStatuses.Active, StringComparison.OrdinalIgnoreCase) || instance.SourceNodeIds.Count == 0 ||
            instance.SourceNodeIds.Count != instance.SourceNodeIds.Distinct().Count())
            throw new InvalidOperationException("The selected component occurrence must have active, unambiguous retained input members.");
        var result = new List<RetainedMember>();
        foreach (var id in instance.SourceNodeIds.Except(pending.RemovedSourceNodeIds))
        {
            var source = assembly.LinkGraph.Nodes.Single(node => node.Id == id);
            if (source.Role != AssemblyLinkRoles.Source || source.SourceComponentInstanceId != instance.Id)
                throw new InvalidOperationException("The component's input member ownership is ambiguous.");
            if (store.Assemblies.SelectMany(item => item.LinkGraph.Nodes).Count(node => node.ObjectId == source.ObjectId) != 1 ||
                assembly.LinkGraph.SourceComponentInstances.Count(item => item.SourceNodeIds.Contains(source.Id)) != 1)
                throw new InvalidOperationException("A component input object is shared with another assembly or occurrence. Shared input ownership is not supported by Update Component yet.");
            var edges = assembly.LinkGraph.Edges.Where(edge => edge.ParentNodeId == source.Id &&
                assembly.LinkGraph.Nodes.Any(node => node.Id == edge.ChildNodeId && IsOriginal(node))).ToList();
            if (edges.Count != 1)
                throw new InvalidOperationException("A retained component member has no unique input-to-original link.");
            var edge = edges[0];
            var original = assembly.LinkGraph.Nodes.Single(node => node.Id == edge.ChildNodeId);
            if (original.SourceComponentInstanceId != instance.Id || original.ComponentId != instance.ComponentId ||
                assembly.LinkGraph.Edges.Count(item => item.ChildNodeId == original.Id) != 1 ||
                source.Status != AssemblyLinkStatuses.Active || original.Status != AssemblyLinkStatuses.Active || edge.Status != AssemblyLinkStatuses.Active ||
                source.Metadata.ContainsKey(AssemblyLinkMetadataKeys.Quarantined) || original.Metadata.ContainsKey(AssemblyLinkMetadataKeys.Quarantined) ||
                !edge.ParentToChildTransform.TryToTransform(out var placement) || placement.RigidType != TransformRigidType.Rigid)
                throw new InvalidOperationException("A retained component link needs review before its design can be updated.");
            var sourceObject = RequireObject(doc, source.ObjectId);
            var originalObject = RequireObject(doc, original.ObjectId);
            if ((edge.Recipe != AssemblyLinkRecipes.DirectCopy && edge.Recipe != AssemblyLinkRecipes.HardwareCopy &&
                 !(edge.Recipe == AssemblyLinkRecipes.BlockDefinitionPart && source.PartId == Guid.Empty && sourceObject is InstanceObject && originalObject is InstanceObject)) ||
                !ComponentPlacementMatcher.MatchesGeometry(sourceObject, originalObject, placement, tolerance) ||
                MaterialAssignment.GetCategorizationMaterialId(sourceObject.Attributes) != MaterialAssignment.GetCategorizationMaterialId(originalObject.Attributes))
                throw new InvalidOperationException("Retained input and ORIGINAL ASSEMBLIES geometry or materials disagree. If additions are already staged, undo or restore the later retained-member edit first, then use Update Assembly. Otherwise update existing geometry before staging this component.");
            var memberIds = new[] { source.Id, original.Id };
            if (assembly.LinkGraph.Conflicts.Any(conflict => conflict.Status == AssemblyLinkStatuses.Open &&
                    (memberIds.Contains(conflict.NodeId) || conflict.EdgeId == edge.Id) &&
                    !IsAdoptableDuplicate(assembly, conflict, pending.AddedObjectIds)))
                throw new InvalidOperationException("A retained component member has an unresolved link issue. Resolve that issue before updating its design.");
            if (result.Count > 0 && !ComponentPlacementMatcher.SameTransform(result[0].SourceToOriginal, placement, tolerance))
                throw new InvalidOperationException("The component's retained members have different input-to-original placements. A single component frame cannot be inferred safely.");
            var identity = source.PartId != Guid.Empty ? $"part:{source.PartId:D}" :
                HardwareMetadata.TryGetFromObject(sourceObject, out var metadata) ? $"hardware:{metadata.Identifier}" : string.Empty;
            if (string.IsNullOrWhiteSpace(identity))
                throw new InvalidOperationException("A retained member is neither a categorized part nor recognized hardware.");
            result.Add(new RetainedMember(source, original, placement, identity));
        }
        if (!OriginalNodes(assembly, instance).Select(node => node.Id).ToHashSet().SetEquals(result.Concat(removedMembers).Select(member => member.Original.Id)))
            throw new InvalidOperationException("The component has unaccounted original members. Resolve its membership issues first.");
        return result;
    }

    private static List<CopiedLayout> FindCopiedLayouts(RhinoDoc doc, AssemblyRecord assembly,
        IReadOnlyList<RetainedMember> members, double tolerance)
    {
        var memberIds = members.Select(member => member.Original.Id).ToHashSet();
        var edges = assembly.LinkGraph.Edges.Where(edge => memberIds.Contains(edge.ParentNodeId) &&
            assembly.LinkGraph.Nodes.Any(node => node.Id == edge.ChildNodeId && node.Role == AssemblyLinkRoles.CopiedComponent)).ToList();
        if (edges.Count == 0) return new List<CopiedLayout>();
        var copiedNodes = edges.ToDictionary(edge => edge.Id, edge => assembly.LinkGraph.Nodes.Single(node => node.Id == edge.ChildNodeId));
        var groups = copiedNodes.Values.SelectMany(node => RequireObject(doc, node.ObjectId).Attributes.GetGroupList() ?? Array.Empty<int>()).Distinct();
        var layouts = new List<CopiedLayout>();
        var covered = new HashSet<Guid>();
        foreach (var index in groups)
        {
            var group = doc.Groups.FindIndex(index);
            if (group is null || group.IsDeleted) continue;
            var ids = GroupMembers(doc, group).ToHashSet();
            var included = edges.Where(edge => ids.Contains(copiedNodes[edge.Id].ObjectId)).ToList();
            if (included.Count != members.Count || !included.Select(edge => edge.ParentNodeId).ToHashSet().SetEquals(memberIds))
                continue;
            if (assembly.LinkGraph.Nodes.Any(node => ids.Contains(node.ObjectId) && !included.Any(edge => copiedNodes[edge.Id].Id == node.Id)))
                continue;
            Transform? transform = null;
            foreach (var edge in included)
            {
                var child = copiedNodes[edge.Id];
                var parent = assembly.LinkGraph.Nodes.Single(node => node.Id == edge.ParentNodeId);
                if (edge.Status != AssemblyLinkStatuses.Active || child.Status != AssemblyLinkStatuses.Active ||
                    child.Metadata.ContainsKey(AssemblyLinkMetadataKeys.Quarantined) ||
                    !edge.ParentToChildTransform.TryToTransform(out var placement) || placement.RigidType != TransformRigidType.Rigid ||
                    (transform.HasValue && !ComponentPlacementMatcher.SameTransform(transform.Value, placement, tolerance)) ||
                    !ComponentPlacementMatcher.MatchesGeometry(RequireObject(doc, parent.ObjectId), RequireObject(doc, child.ObjectId), placement, tolerance) ||
                    assembly.LinkGraph.Conflicts.Any(conflict => conflict.Status == AssemblyLinkStatuses.Open &&
                        (conflict.NodeId == child.Id || conflict.EdgeId == edge.Id)))
                    throw new InvalidOperationException("An existing copied-component layout needs review before new members can be added.");
                transform = placement;
            }
            if (included.Any(edge => !covered.Add(edge.Id)))
                throw new InvalidOperationException("A copied component belongs to multiple possible layout groups. Keep one unambiguous component group.");
            layouts.Add(new CopiedLayout(index, transform!.Value));
        }
        if (covered.Count != edges.Count)
            throw new InvalidOperationException("An existing copied component is ungrouped or incomplete. Regroup or rebuild that copied layout before updating the component.");
        return layouts;
    }

    private void AssignPartCategories(RhinoDoc doc, AssemblyRecord assembly, List<Addition> additions)
    {
        var candidatesByPart = new Dictionary<Guid, List<PartCandidate>>();
        try
        {
            foreach (var part in assembly.Parts)
            {
                var candidates = new List<PartCandidate>();
                candidatesByPart[part.Id] = candidates;
                foreach (var id in part.SourceObjectIds.Distinct())
                {
                    var value = doc.Objects.FindId(id);
                    if (value is null || !_fingerprints.TryCreatePartCandidate(value, out var candidate))
                    {
                        foreach (var oldCandidate in candidates) oldCandidate.Geometry.Dispose();
                        candidates.Clear();
                        break;
                    }
                    candidate.MaterialId = MaterialAssignment.GetCategorizationMaterialId(value.Attributes);
                    candidates.Add(candidate);
                }
            }
            var fresh = new List<List<Addition>>();
            foreach (var addition in additions.Where(addition => addition.Candidate is not null))
            {
                var candidate = addition.Candidate!;
                var existing = assembly.Parts.Where(part => candidatesByPart[part.Id].Count > 0 &&
                    candidatesByPart[part.Id].All(other => _fingerprints.AreEquivalentParts(candidate, other))).ToList();
                var addedMatches = fresh.Where(group => group.All(other => _fingerprints.AreEquivalentParts(candidate, other.Candidate!))).ToList();
                if (existing.Count + addedMatches.Count > 1)
                    throw new InvalidOperationException("A new part matches more than one tolerated part category. Resolve the ambiguity before adding it to this component.");
                if (existing.Count == 1) addition.PartId = existing[0].Id;
                else if (addedMatches.Count == 1)
                {
                    addition.SharedAddition = addedMatches[0][0];
                    addedMatches[0].Add(addition);
                }
                else fresh.Add(new List<Addition> { addition });
            }
        }
        finally
        {
            foreach (var candidate in candidatesByPart.Values.SelectMany(items => items)) candidate.Geometry.Dispose();
        }
    }

    private void ApplyPlan(RhinoDoc doc, AssemblyRecord assembly, UpdatePlan plan, List<Guid> addedIds,
        Dictionary<Guid, ObjectAttributes> savedAttributes, List<int> createdGroups, List<Guid> sourceIds,
        IReadOnlySet<string> reservedHardwareNames)
    {
        // Historical builds saved whole hardware instances as block-leaf recipes. Preflight
        // proved their exact whole-object relationship; upgrade only those hardware edges.
        var hardwareParentIds = plan.Instances.SelectMany(instance => instance.Members)
            .Where(member => member.Source.PartId == Guid.Empty)
            .SelectMany(member => new[] { member.Source.Id, member.Original.Id }).ToHashSet();
        foreach (var edge in assembly.LinkGraph.Edges.Where(edge => hardwareParentIds.Contains(edge.ParentNodeId)))
        {
            var child = assembly.LinkGraph.Nodes.Single(node => node.Id == edge.ChildNodeId);
            if (child.PartId == Guid.Empty && child.Role is AssemblyLinkRoles.Hardware or AssemblyLinkRoles.CopiedComponent)
                edge.Recipe = AssemblyLinkRecipes.HardwareCopy;
        }
        foreach (var addition in plan.Additions.Where(addition => addition.Candidate is not null))
        {
            if (addition.PartId == Guid.Empty && addition.SharedAddition is null)
            {
                var candidate = addition.Candidate!;
                var part = new PartRecord
                {
                    Name = AllocatePartName(assembly, reservedHardwareNames), GeometryFingerprint = candidate.Fingerprint,
                    MaterialThickness = _fingerprints.GetMaterialThickness((Brep)candidate.Geometry),
                    CategorizationMaterialId = candidate.MaterialId,
                    MaterialId = MaterialAssignment.GetMaterialId(addition.Attributes)
                };
                assembly.Parts.Add(part);
                addition.PartId = part.Id;
            }
            else if (addition.SharedAddition is not null) addition.PartId = addition.SharedAddition.PartId;
        }

        foreach (var instance in plan.Instances)
        {
            var fromInput = plan.Pending.AdditionOrigin == ComponentAdditionOrigins.Input;
            var newSourceIds = new List<Guid>();
            foreach (var addition in plan.Additions)
            {
                var part = assembly.Parts.SingleOrDefault(item => item.Id == addition.PartId);
                var layerName = part?.Name ?? addition.LayerName;
                var color = part is not null ? _layers.GetOrAssignPartColor(doc, assembly, part, _colorizePartsEnabled()) : Color.DarkGray;
                var originalLayer = _layers.EnsurePartLayerIndex(doc,
                    LayerService.OriginalPart(assembly.Name, plan.Component.Name, layerName), color);
                using var originalAttributes = addition.Attributes.Duplicate();
                AssemblyLineageService.ClearLinkMetadata(originalAttributes);
                originalAttributes.LayerIndex = originalLayer;
                MaterialAssignment.NormalizeToParentMaterial(originalAttributes);
                if (addition.Hardware is not null) HardwareMetadata.Mark(originalAttributes, addition.Hardware);
                Guid sourceId;
                Guid originalId;
                using var sourceAttributes = addition.Attributes.Duplicate();
                AssemblyLineageService.ClearLinkMetadata(sourceAttributes);
                if (addition.Hardware is not null) HardwareMetadata.Mark(sourceAttributes, addition.Hardware);
                if (fromInput)
                {
                    sourceId = addition.Object.Id;
                    savedAttributes.TryAdd(sourceId, addition.Attributes.Duplicate());
                    // Adopt the real input, preserving its UUID, placement, layer, groups,
                    // and material. Only inherited linkage from an ordinary Rhino copy is cleared.
                    RequireModify(doc, sourceId, sourceAttributes);
                    originalAttributes.RemoveFromAllGroups();
                    originalId = AddTransformed(doc, RequireObject(doc, sourceId), instance.SourceToOriginal, originalAttributes);
                    addedIds.Add(originalId);
                    AddToGroup(doc, FindGroup(doc, instance.Instance.GeneratedGroupId).Index, originalId);
                }
                else
                {
                    if (!instance.SourceToOriginal.TryGetInverse(out var originalToSource))
                        throw new InvalidOperationException("A component placement became invalid while applying additions.");
                    sourceAttributes.RemoveFromAllGroups();
                    sourceAttributes.LayerIndex = RequireObject(doc, instance.Members[0].Source.ObjectId).Attributes.LayerIndex;
                    sourceId = AddTransformed(doc, addition.Object, originalToSource, sourceAttributes);
                    addedIds.Add(sourceId);
                    newSourceIds.Add(sourceId);
                    originalId = addition.Object.Id;
                    savedAttributes.TryAdd(originalId, addition.Attributes.Duplicate());
                    RequireModify(doc, originalId, originalAttributes);
                }
                sourceIds.Add(sourceId);
                var recipe = addition.Hardware is null ? AssemblyLinkRecipes.DirectCopy : AssemblyLinkRecipes.HardwareCopy;
                var role = addition.Hardware is null ? AssemblyLinkRoles.OriginalAssembly : AssemblyLinkRoles.Hardware;
                var registration = _lineage.RegisterDerived(doc, assembly, sourceId, AssemblyLinkRoles.Source, originalId,
                    role, instance.SourceToOriginal, recipe, addition.PartId, plan.Component.Id, instance.Instance.Id);
                registration.Parent.GeometryFingerprint = addition.Candidate?.Fingerprint ?? string.Empty;
                registration.Child.GeometryFingerprint = registration.Parent.GeometryFingerprint;
                instance.Instance.SourceNodeIds.Add(registration.Parent.Id);
                assembly.GeometryReferences.Add(new GeometryReferenceRecord
                {
                    Id = registration.Edge.Id, SourceObjectId = sourceId, TargetObjectId = originalId,
                    TargetRole = addition.Hardware is null ? AssemblyManagerConstants.GeneratedAssemblyReferenceRole : AssemblyManagerConstants.GeneratedHardwareReferenceRole,
                    AssemblyName = assembly.Name, ComponentName = plan.Component.Name, PartName = layerName,
                    SourceToTargetTransform = TransformRecord.FromTransform(instance.SourceToOriginal)
                });
                if (part is not null)
                {
                    part.SourceObjectIds.Add(sourceId);
                    part.GeneratedObjectIds.Add(originalId);
                    part.Quantity++;
                }
                else
                {
                    var hardware = addition.Hardware!;
                    assembly.Hardware.Add(new HardwareRecord
                    {
                        Name = hardware.Name, Description = hardware.Description, SourcePath = hardware.SourcePath,
                        BlockDefinitionName = hardware.BlockDefinitionName, SourceObjectId = sourceId,
                        BlockInstanceId = sourceId, GeneratedObjectId = originalId, ComponentName = plan.Component.Name,
                        LayerName = layerName, MaterialId = MaterialAssignment.GetMaterialId(addition.Attributes), Quantity = 1
                    });
                }
                plan.Component.ObjectIds.Add(originalId);
                foreach (var layout in instance.CopiedLayouts)
                {
                    // Build from the immutable operator snapshot, not an attributes object
                    // already passed through Rhino's native original-object modifications.
                    using var copiedAttributes = addition.Attributes.Duplicate();
                    copiedAttributes.RemoveFromAllGroups();
                    AssemblyLineageService.ClearLinkMetadata(copiedAttributes);
                    MaterialAssignment.NormalizeToParentMaterial(copiedAttributes);
                    if (addition.Hardware is not null) HardwareMetadata.Mark(copiedAttributes, addition.Hardware);
                    copiedAttributes.LayerIndex = _layers.EnsurePartLayerIndex(doc,
                        LayerService.CopiedComponentPart(assembly.Name, plan.Component.Name, layerName), color);
                    var copiedId = AddTransformed(doc, RequireObject(doc, originalId), layout.OriginalToCopy, copiedAttributes);
                    addedIds.Add(copiedId);
                    AddToGroup(doc, layout.GroupIndex, copiedId);
                    _lineage.RegisterDerived(doc, assembly, originalId, role, copiedId, AssemblyLinkRoles.CopiedComponent,
                        layout.OriginalToCopy, recipe, addition.PartId, plan.Component.Id, instance.Instance.Id);
                }
            }
            if (newSourceIds.Count > 0 && instance.SourceGroupIndex < 0)
            {
                foreach (var member in instance.Members)
                    savedAttributes.TryAdd(member.Source.ObjectId, RequireObject(doc, member.Source.ObjectId).Attributes.Duplicate());
                var index = doc.Groups.Add($"{assembly.Name}_{plan.Component.Name}_INPUT_{instance.Instance.Id:N}",
                    instance.Members.Select(member => member.Source.ObjectId).Concat(newSourceIds));
                if (index < 0) throw new InvalidOperationException("Could not create the input component group.");
                createdGroups.Add(index);
                instance.Instance.SourceGroupIndex = index;
                instance.Instance.SourceGroupId = doc.Groups.FindIndex(index).Id;
                instance.Instance.SourceGroupName = doc.Groups.GroupName(index);
            }
            else foreach (var id in newSourceIds) AddToGroup(doc, instance.SourceGroupIndex, id);
            instance.Instance.UpdatedAt = DateTimeOffset.UtcNow;
        }
        ResolveAcceptedConflicts(doc, assembly, plan.Pending);
    }

    private static void ResolveAcceptedConflicts(RhinoDoc doc, AssemblyRecord assembly, PendingComponentUpdateRecord pending)
    {
        foreach (var conflict in assembly.LinkGraph.Conflicts.Where(conflict => conflict.Status == AssemblyLinkStatuses.Open))
        {
            var acceptedGroup = conflict.ConflictType == AssemblyLinkConflictTypes.ComponentMembershipChanged &&
                conflict.Metadata.TryGetValue("GroupId", out var groupText) && Guid.TryParse(groupText, out var groupId) &&
                (groupId == pending.PreviousGeneratedGroupId || groupId == pending.PreviousSourceGroupId || groupId == pending.RegroupedGroupId) &&
                (!conflict.Metadata.TryGetValue("EventKey", out var eventKey) ||
                 eventKey.StartsWith($"group-membership:{pending.TemplateInstanceId}:", StringComparison.OrdinalIgnoreCase));
            if (!acceptedGroup && !(IsAdoptableDuplicate(assembly, conflict, pending.AddedObjectIds) &&
                                    IsUniqueLiveIdentity(doc, assembly, conflict.NodeId))) continue;
            conflict.Status = AssemblyLinkStatuses.Resolved;
            conflict.ResolvedAt = DateTimeOffset.UtcNow;
        }
    }

    private static bool IsAdoptableDuplicate(AssemblyRecord assembly, LinkConflictRecord conflict, IReadOnlyCollection<Guid> addedIds)
    {
        var owner = assembly.LinkGraph.Nodes.SingleOrDefault(node => node.Id == conflict.NodeId);
        return owner is not null && conflict.ConflictType == AssemblyLinkConflictTypes.DuplicateIdentity &&
               conflict.CandidateObjectIds.Any(addedIds.Contains) &&
               conflict.CandidateObjectIds.All(id => id == owner.ObjectId || addedIds.Contains(id));
    }

    private static bool IsUniqueLiveIdentity(RhinoDoc doc, AssemblyRecord assembly, Guid nodeId)
    {
        var owner = assembly.LinkGraph.Nodes.SingleOrDefault(node => node.Id == nodeId);
        return owner is not null && doc.Objects.FindId(owner.ObjectId) is not null &&
               !(doc.Objects.FindByUserString(AssemblyManagerConstants.LinkNodeIdUserString, nodeId.ToString("D"), false) ?? Array.Empty<RhinoObject>())
               .Any(value => !value.IsDeleted && value.Id != owner.ObjectId);
    }

    private static void ValidateInheritedIdentity(RhinoDoc doc, AssemblyStore store, RhinoObject value)
    {
        var text = value.Attributes.GetUserString(AssemblyManagerConstants.LinkNodeIdUserString);
        if (string.IsNullOrWhiteSpace(text)) return;
        if (!Guid.TryParse(text, out var nodeId))
            throw new InvalidOperationException("An added object's inherited link identity is malformed. Clear its old link metadata before adding it.");
        var owners = store.Assemblies.SelectMany(assembly => assembly.LinkGraph.Nodes).Where(node => node.Id == nodeId).ToList();
        if (owners.Count != 1 || owners[0].ObjectId == value.Id || doc.Objects.FindId(owners[0].ObjectId) is null)
            throw new InvalidOperationException("An added object has an unresolved inherited link identity. Restore the original linked object before adopting a copy.");
        var duplicates = doc.Objects.FindByUserString(AssemblyManagerConstants.LinkNodeIdUserString, text, false) ?? Array.Empty<RhinoObject>();
        if (duplicates.Count(item => item.Id != owners[0].ObjectId && item.Id != value.Id) > 0)
            throw new InvalidOperationException("Several unlinked copies share the added object's identity. Resolve the other copies before adopting this one.");
    }

    private static Guid AddTransformed(RhinoDoc doc, RhinoObject template, Transform transform, ObjectAttributes attributes)
    {
        Guid id;
        if (template is InstanceObject instance)
            id = doc.Objects.AddInstanceObject(instance.InstanceDefinition.Index, transform * instance.InstanceXform, attributes);
        else
        {
            using var geometry = template.Geometry.Duplicate();
            if (!geometry.Transform(transform)) throw new InvalidOperationException("Could not transform an added component item.");
            id = doc.Objects.Add(geometry, attributes);
        }
        if (id == Guid.Empty) throw new InvalidOperationException("Rhino could not create an added component item.");
        return id;
    }

    private static void RequireModify(RhinoDoc doc, Guid id, ObjectAttributes attributes)
    {
        if (!doc.Objects.ModifyAttributes(id, attributes, true))
            throw new InvalidOperationException("Rhino could not assign an added item to its component layer.");
    }

    private static void AddToGroup(RhinoDoc doc, int index, Guid id)
    {
        if (!doc.Groups.AddToGroup(index, id))
            throw new InvalidOperationException("Rhino could not add an item to its component group.");
    }

    private static string AllocatePartName(AssemblyRecord assembly, IReadOnlySet<string> reservedHardwareNames)
    {
        string name;
        do name = $"{assembly.PartPrefix}{assembly.NextPartSequence++:00}";
        while (reservedHardwareNames.Contains(name) ||
               assembly.Parts.Any(part => string.Equals(part.Name, name, StringComparison.OrdinalIgnoreCase)));
        return name;
    }

    private static string HardwareLayerName(AssemblyRecord assembly, HardwareMetadataRecord metadata)
    {
        var value = string.IsNullOrWhiteSpace(metadata.Identifier) ? metadata.Name : metadata.Identifier;
        var invalid = new[] { ':', ';', '"', '\'', '<', '>', '|', '?', '*', '\r', '\n', '\t' };
        var cleaned = invalid.Aggregate(value.Trim(), (current, character) => current.Replace(character, '_'));
        if (string.IsNullOrWhiteSpace(cleaned)) cleaned = "Hardware";
        bool IsPartName(string name) => assembly.Parts.Any(part => string.Equals(part.Name, name, StringComparison.OrdinalIgnoreCase));
        bool IsReservedPartNumber(string name) => name.StartsWith(assembly.PartPrefix, StringComparison.OrdinalIgnoreCase) &&
            name.Length > assembly.PartPrefix.Length && int.TryParse(name.AsSpan(assembly.PartPrefix.Length), out _);
        // Keep a safe legacy hardware label stable. New hardware cannot claim a manufacturing
        // P## layer or reserve a future part number: it receives an explicit hardware label.
        if (!IsPartName(cleaned) && assembly.Hardware.Any(hardware =>
                string.Equals(hardware.LayerName, cleaned, StringComparison.OrdinalIgnoreCase)))
            return cleaned;
        while (IsPartName(cleaned) || IsReservedPartNumber(cleaned)) cleaned = "HARDWARE - " + cleaned;
        return cleaned;
    }

    private static RhinoObject RequireObject(RhinoDoc doc, Guid id)
    {
        var value = doc.Objects.FindId(id);
        if (value is null || value.IsDeleted || value.IsReference || value.IsInstanceDefinitionGeometry || value.IsLocked)
            throw new InvalidOperationException("A component member is missing, locked, referenced, or definition-owned. Make the complete component editable before updating it.");
        return value;
    }

    private static Group FindGroup(RhinoDoc doc, Guid id)
    {
        if (id != Guid.Empty)
            for (var index = 0; index < doc.Groups.Count; index++)
            {
                var group = doc.Groups.FindIndex(index);
                if (group is not null && !group.IsDeleted && group.Id == id) return group;
            }
        throw new InvalidOperationException("A required component group is missing. Restore its complete group before updating. Use Update Component only for a regrouped ORIGINAL ASSEMBLIES occurrence.");
    }

    private static IEnumerable<Guid> GroupMembers(RhinoDoc doc, Group group) =>
        (doc.Groups.GroupMembers(group.Index) ?? Array.Empty<RhinoObject>()).Where(value => !value.IsDeleted).Select(value => value.Id);
    private static bool IsOriginal(AssemblyLinkNodeRecord node) => node.Role is AssemblyLinkRoles.OriginalAssembly or AssemblyLinkRoles.Hardware;
    private static List<AssemblyLinkNodeRecord> OriginalNodes(AssemblyRecord assembly, SourceComponentInstanceRecord instance) =>
        assembly.LinkGraph.Nodes.Where(node => node.SourceComponentInstanceId == instance.Id && IsOriginal(node)).ToList();

    private sealed record RetainedMember(AssemblyLinkNodeRecord Source, AssemblyLinkNodeRecord Original, Transform SourceToOriginal, string Identity);
    private sealed record CopiedLayout(int GroupIndex, Transform OriginalToCopy);
    private sealed class InstancePlan
    {
        public SourceComponentInstanceRecord Instance { get; }
        public IReadOnlyList<RetainedMember> Members { get; }
        public Transform SourceToOriginal { get; }
        public int SourceGroupIndex { get; }
        public List<CopiedLayout> CopiedLayouts { get; }
        public IReadOnlyList<RetainedMember> RemovedMembers { get; init; } = Array.Empty<RetainedMember>();
        public InstancePlan(SourceComponentInstanceRecord instance, IReadOnlyList<RetainedMember> members,
            Transform sourceToOriginal, int sourceGroupIndex, List<CopiedLayout> layouts)
        {
            Instance = instance; Members = members; SourceToOriginal = sourceToOriginal;
            SourceGroupIndex = sourceGroupIndex; CopiedLayouts = layouts;
        }
    }
    private sealed class Addition : IDisposable
    {
        public RhinoObject Object { get; }
        public HardwareMetadataRecord? Hardware { get; }
        public PartCandidate? Candidate { get; }
        public ObjectAttributes Attributes { get; }
        public string LayerName { get; init; } = string.Empty;
        public Guid PartId { get; set; }
        public Addition? SharedAddition { get; set; }
        public Addition(RhinoObject value, HardwareMetadataRecord? hardware, PartCandidate? candidate)
        { Object = value; Hardware = hardware; Candidate = candidate; Attributes = value.Attributes.Duplicate(); }
        public void Dispose() { Attributes.Dispose(); Candidate?.Geometry.Dispose(); }
    }
    private sealed record UpdatePlan(ComponentRecord Component, PendingComponentUpdateRecord Pending,
        List<Addition> Additions, List<InstancePlan> Instances) : IDisposable
    {
        public void Dispose()
        {
            foreach (var addition in Additions) addition.Dispose();
        }
    }
}
