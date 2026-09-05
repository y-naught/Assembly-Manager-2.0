using AssemblyManagerPlugin.Core;
using Rhino;
using Rhino.DocObjects;

namespace AssemblyManagerPlugin.Services;

public sealed class AssemblyRemovalService
{
    private readonly AssemblyRepository _repository;
    private readonly LayerService _layers;
    private readonly IActionHistorySink _history;

    public AssemblyRemovalService(AssemblyRepository repository, LayerService layers, IActionHistorySink history)
    {
        _repository = repository;
        _layers = layers;
        _history = history;
    }

    public AssemblyRemovalResult RemoveAssembly(RhinoDoc doc, string assemblyName)
    {
        using var mutation = AssemblyLinkMutationGate.Enter();
        var store = _repository.Load(doc);
        var assembly = store.FindAssembly(assemblyName);
        if (assembly is null)
            throw new InvalidOperationException($"Assembly '{assemblyName}' was not found.");

        var result = new AssemblyRemovalResult { AssemblyName = assembly.Name };
        var layerRoots = GetAssemblyLayerRoots(assembly.Name);
        var objectIds = CollectManagedObjectIds(assembly);
        foreach (var root in layerRoots)
        {
            foreach (var objectId in _layers.GetObjectIdsInLayerTree(doc, root))
                objectIds.Add(objectId);
        }

        var deletedObjectIds = new HashSet<Guid>();
        foreach (var objectId in objectIds)
        {
            if (TryDeleteObject(doc, objectId))
            {
                result.DeletedObjectCount++;
                deletedObjectIds.Add(objectId);
            }
        }

        result.DependentLinkConflictCount = MarkDependentSourcesDeleted(
            store,
            assembly,
            deletedObjectIds);

        foreach (var groupName in assembly.Components.SelectMany(component => component.InstanceGroupNames).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var group = doc.Groups.FindName(groupName);
            if (group is not null && doc.Groups.Delete(group))
                result.DeletedGroupCount++;
        }

        foreach (var root in layerRoots)
            result.DeletedLayerCount += _layers.DeleteLayerTree(doc, root);

        result.MetadataRemoved = store.Assemblies.RemoveAll(a => string.Equals(a.Name, assembly.Name, StringComparison.OrdinalIgnoreCase)) > 0;
        _repository.Save(doc, store);
        _history.Record(doc, new ActionHistoryEntry
        {
            CommandName = "RemoveAssembly",
            AssemblyName = assembly.Name,
            Summary = $"Removed assembly '{assembly.Name}'.",
            Data =
            {
                ["deletedObjects"] = result.DeletedObjectCount.ToString(),
                ["deletedLayers"] = result.DeletedLayerCount.ToString(),
                ["deletedGroups"] = result.DeletedGroupCount.ToString(),
                ["dependentLinkConflicts"] = result.DependentLinkConflictCount.ToString()
            }
        });

        doc.Views.Redraw();
        return result;
    }

    private static int MarkDependentSourcesDeleted(
        AssemblyStore store,
        AssemblyRecord removedAssembly,
        ISet<Guid> deletedObjectIds)
    {
        if (deletedObjectIds.Count == 0)
            return 0;

        const string eventKeyMetadata = "EventKey";
        var affected = 0;
        var now = DateTimeOffset.UtcNow;
        foreach (var dependentAssembly in store.Assemblies.Where(candidate => candidate.Id != removedAssembly.Id))
        {
            var assemblyChanged = false;
            foreach (var sourceNode in dependentAssembly.LinkGraph.Nodes.Where(node =>
                         string.Equals(node.Role, AssemblyLinkRoles.Source, StringComparison.OrdinalIgnoreCase) &&
                         deletedObjectIds.Contains(node.ObjectId)))
            {
                sourceNode.Status = AssemblyLinkStatuses.Deleted;
                sourceNode.UpdatedAt = now;
                foreach (var edge in dependentAssembly.LinkGraph.Edges.Where(edge => edge.ParentNodeId == sourceNode.Id))
                {
                    edge.Status = AssemblyLinkStatuses.Conflict;
                    edge.UpdatedAt = now;
                }

                var eventKey = $"removed-assembly-source:{removedAssembly.Id}:{sourceNode.Id}";
                var conflict = dependentAssembly.LinkGraph.Conflicts.FirstOrDefault(candidate =>
                    string.Equals(candidate.Status, AssemblyLinkStatuses.Open, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(candidate.ConflictType, AssemblyLinkConflictTypes.SourceDeleted, StringComparison.OrdinalIgnoreCase) &&
                    candidate.Metadata.TryGetValue(eventKeyMetadata, out var existingEventKey) &&
                    string.Equals(existingEventKey, eventKey, StringComparison.Ordinal));
                if (conflict is null)
                {
                    dependentAssembly.LinkGraph.Conflicts.Add(new LinkConflictRecord
                    {
                        ConflictType = AssemblyLinkConflictTypes.SourceDeleted,
                        Status = AssemblyLinkStatuses.Open,
                        NodeId = sourceNode.Id,
                        Message = $"This source belonged to removed assembly '{removedAssembly.Name}'. Gazelle preserved the downstream geometry, but its lineage must be relinked, detached, or removed before it can update again.",
                        CandidateObjectIds = new List<Guid> { sourceNode.ObjectId },
                        DetectedAt = now,
                        Metadata = new Dictionary<string, string>
                        {
                            [eventKeyMetadata] = eventKey,
                            ["RemovedAssemblyId"] = removedAssembly.Id.ToString("D"),
                            ["RemovedAssemblyName"] = removedAssembly.Name
                        }
                    });
                }

                affected++;
                assemblyChanged = true;
            }

            if (!assemblyChanged)
                continue;

            dependentAssembly.LinkGraph.UpdatedAt = now;
            dependentAssembly.UpdatedAt = now;
        }

        return affected;
    }

    private static HashSet<Guid> CollectManagedObjectIds(AssemblyRecord assembly)
    {
        var objectIds = new HashSet<Guid>();

        foreach (var part in assembly.Parts)
        {
            foreach (var objectId in part.GeneratedObjectIds)
                objectIds.Add(objectId);
            foreach (var objectId in part.CamObjectIds)
                objectIds.Add(objectId);
        }

        foreach (var component in assembly.Components)
        {
            foreach (var objectId in component.ObjectIds)
                objectIds.Add(objectId);

            foreach (var objectId in component.RepresentativeObjectIdsByPartName.Values.SelectMany(ids => ids))
                objectIds.Add(objectId);
        }

        foreach (var reference in assembly.GeometryReferences)
        {
            if (reference.TargetObjectId != Guid.Empty)
                objectIds.Add(reference.TargetObjectId);
        }

        foreach (var node in assembly.LinkGraph.Nodes.Where(node =>
                     !string.Equals(node.Role, AssemblyLinkRoles.Source, StringComparison.OrdinalIgnoreCase)))
        {
            if (node.ObjectId != Guid.Empty)
                objectIds.Add(node.ObjectId);
        }

        return objectIds;
    }

    private static string[] GetAssemblyLayerRoots(string assemblyName)
    {
        return new[]
        {
            LayerService.OriginalAssembly(assemblyName),
            LayerService.PartsAssembly(assemblyName),
            LayerService.CopiedComponentsAssembly(assemblyName),
            LayerService.LegacyOriginalAssembly(assemblyName),
            LayerService.LegacyPartsAssembly(assemblyName),
            LayerService.LegacyCopiedComponentsAssembly(assemblyName)
        };
    }

    private static bool TryDeleteObject(RhinoDoc doc, Guid objectId)
    {
        var rhinoObject = doc.Objects.FindId(objectId);
        return rhinoObject is not null && doc.Objects.Delete(rhinoObject, true, true);
    }
}
