using AssemblyManagerPlugin.Core;
using Rhino;
using Rhino.DocObjects;

namespace AssemblyManagerPlugin.Services;

public sealed partial class ComponentUpdateService
{
    internal static bool HasInputRegroupIssue(AssemblyRecord assembly) => assembly.LinkGraph.Conflicts.Any(conflict =>
        conflict.Status == AssemblyLinkStatuses.Open && conflict.ConflictType == AssemblyLinkConflictTypes.ComponentMembershipChanged &&
        conflict.Metadata.TryGetValue("EventKey", out var key) && key.StartsWith("source-regroup:", StringComparison.Ordinal));

    internal static void EnsureInputGroupsReady(AssemblyRecord assembly)
    {
        if (HasInputRegroupIssue(assembly))
            throw new InvalidOperationException("An input component group needs review. Complete or correct its regrouping and resolve its Link Issues before using Update Assembly.");
    }

    /// <summary>
    /// Reconciles completed input-group edits/deletions using surviving source UUIDs, never names.
    /// Changes only the supplied store: downstream objects wait for Update Assembly.
    /// Call only for group edits or observed source deletions, after the command completes.
    /// </summary>
    public bool ReconcileInputGroups(RhinoDoc doc, AssemblyStore store,
        IReadOnlySet<Guid> eligibleAssemblyIds, IReadOnlyCollection<Guid> changedGroupIds)
    {
        var touched = changedGroupIds.Where(id => id != Guid.Empty).ToHashSet();
        if (touched.Count == 0) return false;

        var groupCache = new Dictionary<Guid, InputGroupSnapshot?>();
        InputGroupSnapshot? ReadGroup(Guid id)
        {
            if (id == Guid.Empty) return null;
            if (groupCache.TryGetValue(id, out var cached)) return cached;
            var group = doc.Groups.FindId(id);
            var value = group is null || group.IsDeleted ? null
                : new InputGroupSnapshot(group, GroupMembers(doc, group).ToHashSet());
            groupCache[id] = value;
            return value;
        }

        // Restrict native lookups to changed groups and groups attached to the affected
        // source objects. Moving a copied component never enters this reconciliation.
        var touchedGroups = touched.Select(ReadGroup).Where(group => group is not null)
            .Cast<InputGroupSnapshot>().ToList();
        var changed = false;
        foreach (var assembly in store.Assemblies.Where(item => eligibleAssemblyIds.Contains(item.Id)))
        {
            var nodesById = assembly.LinkGraph.Nodes.ToDictionary(node => node.Id);
            var unresolvedRegroups = assembly.LinkGraph.Conflicts
                .Where(conflict => conflict.Status == AssemblyLinkStatuses.Open &&
                    conflict.ConflictType == AssemblyLinkConflictTypes.ComponentMembershipChanged &&
                    conflict.Metadata.ContainsKey("EventKey"))
                .Select(conflict => conflict.Metadata["EventKey"]).ToHashSet(StringComparer.Ordinal);
            foreach (var instance in assembly.LinkGraph.SourceComponentInstances)
            {
                var sourceNodes = instance.SourceNodeIds.Where(nodesById.ContainsKey)
                    .Select(id => nodesById[id]).Where(node => node.Role == AssemblyLinkRoles.Source).ToList();
                var sourceIds = sourceNodes.Select(node => node.ObjectId).Where(id => id != Guid.Empty).ToHashSet();
                // Deleting a competing group can resolve ambiguity even though its
                // members can no longer be read from the native group table. Revisit
                // unresolved occurrences on group batches, never ordinary move batches.
                if (!unresolvedRegroups.Contains($"source-regroup:{instance.Id}") &&
                    !touched.Contains(instance.SourceGroupId) &&
                    !touchedGroups.Any(group => group.Members.Overlaps(sourceIds))) continue;

                var current = ReadGroup(instance.SourceGroupId);
                if (current is not null)
                    changed |= UpdateInputGroupIdentity(instance, current.Group);

                var previous = assembly.PendingComponentUpdates.SingleOrDefault(update => update.TemplateInstanceId == instance.Id);
                var acceptedIds = sourceIds.ToHashSet();
                if (previous?.AdditionOrigin == ComponentAdditionOrigins.Input)
                {
                    acceptedIds.ExceptWith(sourceNodes.Where(node => previous.RemovedSourceNodeIds.Contains(node.Id))
                        .Select(node => node.ObjectId));
                    acceptedIds.UnionWith(previous.AddedObjectIds);
                }

                // Another ordinary/nested group is not a replacement while the registered
                // group still has its accepted membership. This also protects a pending
                // regroup from being stolen by subsequently grouping its removed leftovers.
                if (current is not null && current.Members.SetEquals(acceptedIds))
                {
                    changed |= ResolveInputGroupConflicts(assembly, instance.Id,
                        new[] { instance.SourceGroupId }, resolveRegroupIssue: true);
                    continue;
                }

                var replacements = new Dictionary<Guid, InputGroupSnapshot>();
                foreach (var objectId in sourceIds)
                {
                    var value = doc.Objects.FindId(objectId);
                    if (value is null || value.IsDeleted) continue;
                    foreach (var index in value.Attributes.GetGroupList() ?? Array.Empty<int>())
                    {
                        var group = doc.Groups.FindIndex(index);
                        if (group is null || group.IsDeleted || group.Id == instance.SourceGroupId) continue;
                        // A previous regroup can leave excluded objects in the former
                        // group. Such an untouched leftover is not a fresh replacement
                        // when the currently registered group is being edited in place.
                        if (current is not null && current.Members.Count > 0 && !touched.Contains(group.Id)) continue;
                        var candidate = ReadGroup(group.Id);
                        if (candidate is not null && candidate.Members.Overlaps(sourceIds))
                            replacements[group.Id] = candidate;
                    }
                }

                InputGroupSnapshot? selected = null;
                string? problem = null;
                if (replacements.Count > 1)
                    problem = "More than one group contains this component's retained input members. Keep one unambiguous component group before using Update Assembly.";
                else if (replacements.Count == 1)
                    selected = replacements.Values.Single();
                else if (current is not null && current.Members.Overlaps(sourceIds))
                    selected = current;
                else
                    problem = "The input component is ungrouped or has no retained linked member. Regroup at least one of its existing input parts with the desired members before using Update Assembly.";

                if (problem is null && previous is not null && previous.AdditionOrigin != ComponentAdditionOrigins.Input)
                    problem = "This occurrence has an ORIGINAL ASSEMBLIES update staged. Apply or restore that update before regrouping its input component.";

                if (problem is null && selected is not null)
                {
                    // Preflight checks every object's ownership, active links, geometry,
                    // material, placement and copied views before any identity is rebound.
                    var pending = new PendingComponentUpdateRecord
                    {
                        Id = previous?.Id ?? Guid.NewGuid(),
                        CreatedAt = previous?.CreatedAt ?? DateTimeOffset.UtcNow,
                        ComponentId = instance.ComponentId,
                        TemplateInstanceId = instance.Id,
                        AdditionOrigin = ComponentAdditionOrigins.Input,
                        RegroupedGroupId = selected.Group.Id,
                        PreviousSourceGroupId = previous?.PreviousSourceGroupId is { } originalId && originalId != Guid.Empty
                            ? originalId : instance.SourceGroupId,
                        PreviousGeneratedGroupId = instance.GeneratedGroupId,
                        AddedObjectIds = selected.Members.Except(sourceIds).OrderBy(id => id).ToList(),
                        RemovedSourceNodeIds = sourceNodes.Where(node => !selected.Members.Contains(node.ObjectId))
                            .Select(node => node.Id).OrderBy(id => id).ToList(),
                        InstanceIds = new List<Guid> { instance.Id },
                        MemberNodeIdsByInstance = previous?.MemberNodeIdsByInstance ?? new Dictionary<Guid, List<Guid>>
                        {
                            [instance.Id] = instance.SourceNodeIds.OrderBy(id => id).ToList()
                        }
                    };
                    try
                    {
                        using var plan = Preflight(doc, store, assembly, pending, staged: false, allowInputRebind: true);
                        var oldGroupId = instance.SourceGroupId;
                        changed |= UpdateInputGroupIdentity(instance, selected.Group);
                        assembly.PendingComponentUpdates.RemoveAll(update => update.TemplateInstanceId == instance.Id);
                        // Renaming or recreating an otherwise identical group needs no
                        // geometry update, and restoring the original membership cancels
                        // a pending structural change without touching any Rhino object.
                        if (pending.AddedObjectIds.Count > 0 || pending.RemovedSourceNodeIds.Count > 0)
                            assembly.PendingComponentUpdates.Add(pending);
                        changed = true;
                        assembly.UpdatedAt = DateTimeOffset.UtcNow;
                        changed |= ResolveInputGroupConflicts(assembly, instance.Id,
                            new[] { oldGroupId, pending.PreviousSourceGroupId, selected.Group.Id }, resolveRegroupIssue: true);
                        var componentName = assembly.Components.Single(component => component.Id == instance.ComponentId).Name;
                        if (pending.AddedObjectIds.Count > 0 || pending.RemovedSourceNodeIds.Count > 0)
                            RhinoApp.WriteLine($"Gazelle recognized input component membership changes for {componentName} in '{assembly.Name}': " +
                                $"{pending.AddedObjectIds.Count} added, {pending.RemovedSourceNodeIds.Count} removed member(s) staged. Click Update Assembly to apply; other occurrences remain unchanged.");
                        else
                            RhinoApp.WriteLine("Gazelle retained the input link for regrouped component {0} in '{1}'; its membership is unchanged.", componentName, assembly.Name);
                    }
                    catch (InvalidOperationException exception)
                    {
                        problem = exception.Message;
                    }
                }

                if (problem is not null)
                    changed |= RecordInputRegroupIssue(assembly, instance, sourceIds,
                        "Gazelle could not accept the regrouped input component: " + problem);
            }
        }
        return changed;
    }

    private static bool UpdateInputGroupIdentity(SourceComponentInstanceRecord instance, Group group)
    {
        if (instance.SourceGroupId == group.Id && instance.SourceGroupIndex == group.Index &&
            string.Equals(instance.SourceGroupName, group.Name, StringComparison.Ordinal)) return false;
        instance.SourceGroupId = group.Id;
        instance.SourceGroupIndex = group.Index;
        instance.SourceGroupName = group.Name;
        instance.UpdatedAt = DateTimeOffset.UtcNow;
        return true;
    }

    private static bool RecordInputRegroupIssue(AssemblyRecord assembly, SourceComponentInstanceRecord instance,
        IEnumerable<Guid> sourceIds, string message)
    {
        var key = $"source-regroup:{instance.Id}";
        var existing = assembly.LinkGraph.Conflicts.FirstOrDefault(conflict =>
            conflict.Status == AssemblyLinkStatuses.Open &&
            conflict.Metadata.TryGetValue("EventKey", out var eventKey) && eventKey == key);
        var candidates = sourceIds.OrderBy(id => id).ToList();
        if (existing is not null)
        {
            if (existing.Message == message && existing.CandidateObjectIds.SequenceEqual(candidates)) return false;
            existing.Message = message;
            existing.CandidateObjectIds = candidates;
            existing.Metadata["GroupId"] = instance.SourceGroupId.ToString();
            existing.Metadata["GroupName"] = instance.SourceGroupName;
            return true;
        }
        assembly.LinkGraph.Conflicts.Add(new LinkConflictRecord
        {
            ConflictType = AssemblyLinkConflictTypes.ComponentMembershipChanged,
            Message = message,
            CandidateObjectIds = candidates,
            Metadata = new Dictionary<string, string>
            {
                ["EventKey"] = key,
                ["GroupId"] = instance.SourceGroupId.ToString(),
                ["GroupName"] = instance.SourceGroupName
            }
        });
        return true;
    }

    private static bool ResolveInputGroupConflicts(AssemblyRecord assembly, Guid instanceId,
        IEnumerable<Guid> acceptedGroupIds, bool resolveRegroupIssue)
    {
        var ids = acceptedGroupIds.Where(id => id != Guid.Empty).ToHashSet();
        var changed = false;
        foreach (var conflict in assembly.LinkGraph.Conflicts.Where(conflict =>
                     conflict.Status == AssemblyLinkStatuses.Open &&
                     conflict.ConflictType == AssemblyLinkConflictTypes.ComponentMembershipChanged))
        {
            if (!conflict.Metadata.TryGetValue("EventKey", out var key)) continue;
            var matchedRegroup = resolveRegroupIssue && key == $"source-regroup:{instanceId}";
            var matchedGroup = key.StartsWith($"group-membership:{instanceId}:", StringComparison.OrdinalIgnoreCase) &&
                conflict.Metadata.TryGetValue("GroupId", out var groupText) && Guid.TryParse(groupText, out var groupId) && ids.Contains(groupId);
            if (!matchedRegroup && !matchedGroup) continue;
            conflict.Status = AssemblyLinkStatuses.Resolved;
            conflict.ResolvedAt = DateTimeOffset.UtcNow;
            changed = true;
        }
        return changed;
    }

    private sealed record InputGroupSnapshot(Group Group, HashSet<Guid> Members);
}
