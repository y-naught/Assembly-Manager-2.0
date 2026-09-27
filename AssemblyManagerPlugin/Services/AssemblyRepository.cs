using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AssemblyManagerPlugin.Core;
using Rhino;
using Rhino.DocObjects;

namespace AssemblyManagerPlugin.Services;

public sealed class AssemblyRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };

    public AssemblyStore Load(RhinoDoc doc)
    {
        var json = doc.Strings.GetValue(AssemblyManagerConstants.StoreSection, AssemblyManagerConstants.StoreEntry);
        if (string.IsNullOrWhiteSpace(json))
            return new AssemblyStore();

        try
        {
            var store = JsonSerializer.Deserialize<AssemblyStore>(json, JsonOptions) ?? new AssemblyStore();
            Normalize(store, doc);
            return store;
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine("Assembly Manager could not read stored data: {0}", ex.Message);
            throw new InvalidOperationException(
                "Gazelle could not safely read this document's Assembly Manager data. The stored data was left unchanged; use the same or a newer Gazelle version, or repair the metadata before making Assembly Manager changes.",
                ex);
        }
    }

    public void Save(RhinoDoc doc, AssemblyStore store)
    {
        Normalize(store, doc);
        var json = JsonSerializer.Serialize(store, JsonOptions);
        doc.Strings.SetString(AssemblyManagerConstants.StoreSection, AssemblyManagerConstants.StoreEntry, json);
    }

    /// <summary>
    /// Applies additive schema defaults and converts the reliable part of the v1 metadata
    /// (source to generated-original references) into v2 graph edges. Copied and laid-flat
    /// legacy objects cannot be inferred safely because their transforms were never stored.
    /// </summary>
    public static void Normalize(AssemblyStore store)
    {
        Normalize(store, null);
    }

    internal static AssemblyRecord CreateAssemblySnapshot(AssemblyRecord assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var json = JsonSerializer.Serialize(assembly, JsonOptions);
        return JsonSerializer.Deserialize<AssemblyRecord>(json, JsonOptions)
               ?? throw new InvalidOperationException("Could not create an assembly reconciliation snapshot.");
    }

    private static void Normalize(AssemblyStore store, RhinoDoc? doc)
    {
        if (store.SchemaVersion > AssemblyStore.CurrentSchemaVersion)
        {
            throw new InvalidOperationException(
                $"This document uses Assembly Manager schema {store.SchemaVersion}, but this build supports schema {AssemblyStore.CurrentSchemaVersion}. Open it with the same or a newer Gazelle version to avoid data loss.");
        }

        var isLegacyStore = store.SchemaVersion < AssemblyStore.CurrentSchemaVersion;
        store.Assemblies ??= new List<AssemblyRecord>();
        store.MaterialLibraryCache ??= new List<MaterialRecord>();
        store.ActionHistory ??= new List<ActionHistoryEntry>();

        ValidateAssemblyIdentities(store.Assemblies);

        foreach (var assembly in store.Assemblies)
        {
            assembly.PartPrefix = string.IsNullOrWhiteSpace(assembly.PartPrefix) ? "P" : assembly.PartPrefix;
            assembly.ComponentPrefix = string.IsNullOrWhiteSpace(assembly.ComponentPrefix) ? "C" : assembly.ComponentPrefix;
            assembly.Components ??= new List<ComponentRecord>();
            assembly.Parts ??= new List<PartRecord>();
            assembly.Hardware ??= new List<HardwareRecord>();
            assembly.PendingComponentUpdates ??= new List<PendingComponentUpdateRecord>();
            assembly.GeometryReferences ??= new List<GeometryReferenceRecord>();
            assembly.NestingEstimates ??= new List<NestingEstimateRecord>();
            assembly.LinkGraph ??= new AssemblyLinkGraphRecord();
            if (assembly.LinkGraph.SchemaVersion > AssemblyStore.CurrentSchemaVersion)
            {
                throw new InvalidOperationException(
                    $"Assembly '{assembly.Name}' uses link schema {assembly.LinkGraph.SchemaVersion}, but this build supports schema {AssemblyStore.CurrentSchemaVersion}.");
            }
            assembly.LinkGraph.Nodes ??= new List<AssemblyLinkNodeRecord>();
            assembly.LinkGraph.Edges ??= new List<AssemblyLinkEdgeRecord>();
            assembly.LinkGraph.Conflicts ??= new List<LinkConflictRecord>();
            assembly.LinkGraph.SourceComponentInstances ??= new List<SourceComponentInstanceRecord>();
            ValidateAssemblyCollections(assembly);

            foreach (var pending in assembly.PendingComponentUpdates)
            {
                if (pending.AdditionOrigin is not (ComponentAdditionOrigins.Original or ComponentAdditionOrigins.Input))
                    throw new InvalidOperationException($"Assembly '{assembly.Name}' has an unsupported pending component addition origin. Use the same or a newer Gazelle version.");
                pending.AddedObjectIds ??= new List<Guid>();
                pending.RemovedSourceNodeIds ??= new List<Guid>();
                if (pending.AdditionOrigin != ComponentAdditionOrigins.Input && pending.RemovedSourceNodeIds.Count > 0)
                    throw new InvalidOperationException($"Assembly '{assembly.Name}' contains unsupported original-side removals.");
                pending.InstanceIds ??= new List<Guid>();
                pending.MemberNodeIdsByInstance ??= new Dictionary<Guid, List<Guid>>();
                if (pending.MemberNodeIdsByInstance.Values.Any(ids => ids is null))
                    throw new InvalidOperationException($"Assembly '{assembly.Name}' has an invalid pending component membership snapshot.");
            }

            foreach (var component in assembly.Components)
            {
                component.PartNames ??= new List<string>();
                component.PartQuantities ??= new Dictionary<string, int>();
                component.RepresentativeObjectIdsByPartName ??= new Dictionary<string, List<Guid>>();
                component.ObjectIds ??= new List<Guid>();
                component.InstanceGroupNames ??= new List<string>();
            }

            foreach (var part in assembly.Parts)
            {
                part.SourceObjectIds ??= new List<Guid>();
                part.GeneratedObjectIds ??= new List<Guid>();
                part.CamObjectIds ??= new List<Guid>();
                // An explicit empty value is the accepted TBD identity, not missing data.
                // Re-reading live material here would erase a later material-only edit
                // before reconciliation can split or merge the affected occurrences.
                if (part.CategorizationMaterialId is null)
                {
                    part.CategorizationMaterialId = ResolveCategorizationMaterialId(
                        store,
                        doc,
                        part);
                }
            }

            assembly.NextPartSequence = Math.Max(
                assembly.NextPartSequence,
                NextAvailableSequence(assembly.PartPrefix, assembly.Parts.Select(part => part.Name)));
            assembly.NextComponentSequence = Math.Max(
                assembly.NextComponentSequence,
                NextAvailableSequence(assembly.ComponentPrefix, assembly.Components.Select(component => component.Name)));

            foreach (var reference in assembly.GeometryReferences)
                reference.SourceToTargetTransform ??= InvalidTransformRecord();

            foreach (var node in assembly.LinkGraph.Nodes)
            {
                node.Metadata ??= new Dictionary<string, string>();
                node.Role = string.IsNullOrWhiteSpace(node.Role) ? AssemblyLinkRoles.Source : node.Role;
                node.Status = string.IsNullOrWhiteSpace(node.Status) ? AssemblyLinkStatuses.Active : node.Status;
            }

            foreach (var edge in assembly.LinkGraph.Edges)
            {
                edge.ParentToChildTransform ??= InvalidTransformRecord();
                edge.RecipeMetadata ??= new Dictionary<string, string>();
                edge.Recipe = string.IsNullOrWhiteSpace(edge.Recipe) ? AssemblyLinkRecipes.DirectCopy : edge.Recipe;
                edge.Status = string.IsNullOrWhiteSpace(edge.Status) ? AssemblyLinkStatuses.Active : edge.Status;
            }

            foreach (var conflict in assembly.LinkGraph.Conflicts)
            {
                conflict.CandidateObjectIds ??= new List<Guid>();
                conflict.Metadata ??= new Dictionary<string, string>();
                conflict.Status = string.IsNullOrWhiteSpace(conflict.Status) ? AssemblyLinkStatuses.Open : conflict.Status;
            }

            foreach (var instance in assembly.LinkGraph.SourceComponentInstances)
            {
                instance.SourceNodeIds ??= new List<Guid>();
                instance.Metadata ??= new Dictionary<string, string>();
                instance.Status = string.IsNullOrWhiteSpace(instance.Status)
                    ? AssemblyLinkStatuses.Active
                    : instance.Status;
            }

            MigrateLegacyReferences(assembly, doc, isLegacyStore);
            ValidateGraphIdentities(assembly);
            assembly.LinkGraph.SchemaVersion = AssemblyStore.CurrentSchemaVersion;
        }

        store.SchemaVersion = AssemblyStore.CurrentSchemaVersion;
    }

    private static int NextAvailableSequence(string prefix, IEnumerable<string> names)
    {
        var maximum = 0;
        foreach (var name in names.Where(name => !string.IsNullOrWhiteSpace(name)))
        {
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !int.TryParse(name[prefix.Length..], out var sequence) ||
                sequence <= maximum)
            {
                continue;
            }

            maximum = sequence;
        }

        return maximum + 1;
    }

    private static string ResolveCategorizationMaterialId(
        AssemblyStore store,
        RhinoDoc? doc,
        PartRecord part)
    {
        if (doc is not null)
        {
            foreach (var objectId in part.SourceObjectIds)
            {
                var sourceObject = doc.Objects.FindId(objectId);
                if (sourceObject is null || sourceObject.IsDeleted)
                    continue;

                // An empty source assignment is meaningful and must not be replaced by a
                // later stock-shape selection stored on the PartRecord.
                return MaterialAssignment.GetCategorizationMaterialId(sourceObject.Attributes);
            }
        }

        var cachedMaterial = store.MaterialLibraryCache.FirstOrDefault(material =>
            string.Equals(material.Id, part.MaterialId, StringComparison.OrdinalIgnoreCase));
        var materialId = cachedMaterial is not null && !string.IsNullOrWhiteSpace(cachedMaterial.BaseMaterialId)
            ? cachedMaterial.BaseMaterialId
            : part.MaterialId;
        return MaterialAssignment.NormalizeMaterialIdForCategory(materialId);
    }

    private static Guid CreateLegacyNodeId(Guid assemblyId, Guid objectId, string role)
    {
        var payload = Encoding.UTF8.GetBytes(
            $"Gazelle:LegacyLinkNode:{assemblyId:D}:{objectId:D}:{role.ToUpperInvariant()}");
        var hash = SHA256.HashData(payload);
        return new Guid(hash.AsSpan(0, 16));
    }

    private static TransformRecord InvalidTransformRecord()
    {
        return new TransformRecord { Values = Array.Empty<double>() };
    }

    private static void MigrateLegacyReferences(
        AssemblyRecord assembly,
        RhinoDoc? doc,
        bool isLegacyStore)
    {
        var validReferences = assembly.GeometryReferences
            .Where(reference => reference.SourceObjectId != Guid.Empty && reference.TargetObjectId != Guid.Empty)
            .ToList();
        var referencesBySource = validReferences
            .GroupBy(reference => reference.SourceObjectId)
            .ToDictionary(group => group.Key, group => group.ToList());
        // Normalization also runs for current graphs on every load/save. Index the
        // recovery records once so checking N legacy references does not repeatedly
        // scan all N nodes and edges during ordinary copied-component placement.
        // Preserve first-match behavior here; the identity validator below still
        // rejects duplicate node/edge identities instead of silently repairing them.
        var nodesByObject = assembly.LinkGraph.Nodes.GroupBy(node => node.ObjectId)
            .ToDictionary(group => group.Key, group => group.First());
        var sourceNodesByObject = assembly.LinkGraph.Nodes
            .Where(node => string.Equals(node.Role, AssemblyLinkRoles.Source, StringComparison.OrdinalIgnoreCase))
            .GroupBy(node => node.ObjectId).ToDictionary(group => group.Key, group => group.First());
        var nodesById = assembly.LinkGraph.Nodes.GroupBy(node => node.Id)
            .ToDictionary(group => group.Key, group => group.First());
        var edgesByEndpoints = assembly.LinkGraph.Edges.GroupBy(edge => (edge.ParentNodeId, edge.ChildNodeId))
            .ToDictionary(group => group.Key, group => group.First());
        var edgeIds = assembly.LinkGraph.Edges.Select(edge => edge.Id).ToHashSet();

        foreach (var reference in assembly.GeometryReferences)
        {
            if (reference.SourceObjectId == Guid.Empty || reference.TargetObjectId == Guid.Empty)
                continue;

            var partId = assembly.Parts.FirstOrDefault(part =>
                string.Equals(part.Name, reference.PartName, StringComparison.OrdinalIgnoreCase))?.Id ?? Guid.Empty;
            var componentId = assembly.Components.FirstOrDefault(component =>
                string.Equals(component.Name, reference.ComponentName, StringComparison.OrdinalIgnoreCase))?.Id ?? Guid.Empty;

            if (!sourceNodesByObject.TryGetValue(reference.SourceObjectId, out var sourceNode))
            {
                sourceNode = new AssemblyLinkNodeRecord
                {
                    Id = CreateLegacyNodeId(
                        assembly.Id,
                        reference.SourceObjectId,
                        AssemblyLinkRoles.Source),
                    ObjectId = reference.SourceObjectId,
                    Role = AssemblyLinkRoles.Source,
                    PartId = partId,
                    ComponentId = componentId,
                    SourceLocator = $"RhinoObject:{reference.SourceObjectId:D}"
                };
                assembly.LinkGraph.Nodes.Add(sourceNode);
                sourceNodesByObject.Add(sourceNode.ObjectId, sourceNode);
                nodesByObject.TryAdd(sourceNode.ObjectId, sourceNode);
                nodesById.TryAdd(sourceNode.Id, sourceNode);
            }

            if (!nodesByObject.TryGetValue(reference.TargetObjectId, out var childNode))
            {
                childNode = new AssemblyLinkNodeRecord
                {
                    Id = CreateLegacyNodeId(
                        assembly.Id,
                        reference.TargetObjectId,
                        RoleForLegacyReference(reference.TargetRole)),
                    ObjectId = reference.TargetObjectId,
                    Role = RoleForLegacyReference(reference.TargetRole),
                    PartId = partId,
                    ComponentId = componentId
                };
                assembly.LinkGraph.Nodes.Add(childNode);
                nodesByObject.Add(childNode.ObjectId, childNode);
                nodesById.TryAdd(childNode.Id, childNode);
                if (string.Equals(childNode.Role, AssemblyLinkRoles.Source, StringComparison.OrdinalIgnoreCase))
                    sourceNodesByObject.TryAdd(childNode.ObjectId, childNode);
            }

            if (edgesByEndpoints.TryGetValue((sourceNode.Id, childNode.Id), out var existingEdge))
            {
                var conflictingReferenceId = reference.Id != Guid.Empty && reference.Id != existingEdge.Id;
                if (conflictingReferenceId ||
                    !TransformRecordsEqual(existingEdge.ParentToChildTransform, reference.SourceToTargetTransform))
                {
                    AddRepositoryConflict(
                        assembly,
                        AssemblyLinkConflictTypes.LegacyMigrationIncomplete,
                        "Conflicting legacy records describe the same source and target with different identities or transforms. Gazelle preserved the existing graph edge and requires review.",
                        childNode.Id,
                        existingEdge.Id,
                        new[] { reference.SourceObjectId, reference.TargetObjectId });
                }

                continue;
            }

            var edgeId = reference.Id;
            if (edgeId == Guid.Empty || edgeIds.Contains(edgeId))
                edgeId = Guid.NewGuid();

            var sourceObject = doc?.Objects.FindId(reference.SourceObjectId);
            var targetIsHardware = string.Equals(
                reference.TargetRole,
                AssemblyManagerConstants.GeneratedHardwareReferenceRole,
                StringComparison.OrdinalIgnoreCase);
            var sourceIsKnownBlock = sourceObject is InstanceObject;
            var sourceIsAmbiguousWithoutDocument = sourceObject is null &&
                referencesBySource.TryGetValue(reference.SourceObjectId, out var sameSourceReferences) &&
                sameSourceReferences.Select(candidate => candidate.PartName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Skip(1)
                    .Any();
            var unsupportedBlockLeaf = targetIsHardware || sourceIsKnownBlock || sourceIsAmbiguousWithoutDocument;

            var edge = new AssemblyLinkEdgeRecord
            {
                Id = edgeId,
                ParentNodeId = sourceNode.Id,
                ChildNodeId = childNode.Id,
                ParentToChildTransform = reference.SourceToTargetTransform ?? TransformRecord.Identity(),
                Recipe = unsupportedBlockLeaf
                    ? AssemblyLinkRecipes.BlockDefinitionPart
                    : AssemblyLinkRecipes.DirectCopy,
                UpdatedAt = reference.UpdatedAt
            };
            assembly.LinkGraph.Edges.Add(edge);
            edgesByEndpoints.Add((sourceNode.Id, childNode.Id), edge);
            edgeIds.Add(edge.Id);

            if (unsupportedBlockLeaf)
            {
                edge.Status = AssemblyLinkStatuses.Conflict;
                childNode.Status = AssemblyLinkStatuses.Conflict;
                AddRepositoryConflict(
                    assembly,
                    AssemblyLinkConflictTypes.LegacyMigrationIncomplete,
                    "This legacy reference may represent a block-definition leaf, but the old schema did not store a unique definition path. The existing output is preserved until it is regenerated with the current link schema.",
                    childNode.Id,
                    edge.Id,
                    new[] { reference.SourceObjectId, reference.TargetObjectId });
            }
        }

        foreach (var edge in assembly.LinkGraph.Edges)
        {
            if (IsFiniteAffineInvertible(edge.ParentToChildTransform))
                continue;

            edge.Status = AssemblyLinkStatuses.Conflict;
            nodesById.TryGetValue(edge.ChildNodeId, out var childNode);
            if (childNode is not null)
                childNode.Status = AssemblyLinkStatuses.Conflict;
            AddRepositoryConflict(
                assembly,
                AssemblyLinkConflictTypes.TransformUnresolved,
                "A stored relationship transform is malformed. Gazelle will preserve the existing output instead of substituting an identity transform.",
                childNode?.Id ?? Guid.Empty,
                edge.Id,
                childNode is null ? Array.Empty<Guid>() : new[] { childNode.ObjectId });
        }

        if (isLegacyStore)
        {
            AddRepositoryConflict(
                assembly,
                AssemblyLinkConflictTypes.LegacyMigrationIncomplete,
                "Legacy source-to-original references were migrated. Earlier versions did not save copied-component, flat-part, or source-occurrence transforms; regenerate those outputs once before relying on full live propagation.",
                candidateObjectIds: assembly.Parts.SelectMany(part => part.CamObjectIds));
        }
    }

    private static string RoleForLegacyReference(string targetRole)
    {
        if (string.Equals(
                targetRole,
                AssemblyManagerConstants.GeneratedHardwareReferenceRole,
                StringComparison.OrdinalIgnoreCase))
        {
            return AssemblyLinkRoles.Hardware;
        }

        return AssemblyLinkRoles.OriginalAssembly;
    }

    private static void ValidateAssemblyIdentities(IReadOnlyCollection<AssemblyRecord> assemblies)
    {
        if (assemblies.Any(assembly => assembly is null))
            throw new InvalidOperationException("Stored Assembly Manager data contains a null assembly record.");
        if (assemblies.Any(assembly => assembly.Id == Guid.Empty))
            throw new InvalidOperationException("Stored Assembly Manager data contains an assembly with no stable identity.");

        var duplicateId = assemblies
            .GroupBy(assembly => assembly.Id)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicateId.HasValue)
            throw new InvalidOperationException($"Stored Assembly Manager data contains duplicate assembly identity {duplicateId:D}.");
    }

    private static void ValidateAssemblyCollections(AssemblyRecord assembly)
    {
        if (assembly.Components.Any(component => component is null) ||
            assembly.Parts.Any(part => part is null) ||
            assembly.Hardware.Any(hardware => hardware is null) ||
            assembly.PendingComponentUpdates.Any(pending => pending is null) ||
            assembly.GeometryReferences.Any(reference => reference is null) ||
            assembly.LinkGraph.Nodes.Any(node => node is null) ||
            assembly.LinkGraph.Edges.Any(edge => edge is null) ||
            assembly.LinkGraph.Conflicts.Any(conflict => conflict is null) ||
            assembly.LinkGraph.SourceComponentInstances.Any(instance => instance is null))
        {
            throw new InvalidOperationException(
                $"Assembly '{assembly.Name}' contains a null record and cannot be updated safely.");
        }
    }

    private static void ValidateGraphIdentities(AssemblyRecord assembly)
    {
        var graph = assembly.LinkGraph;
        ValidateStableIds(graph.Nodes.Select(node => node.Id), "node", assembly.Name);
        ValidateStableIds(graph.Edges.Select(edge => edge.Id), "edge", assembly.Name);
        ValidateStableIds(graph.Conflicts.Select(conflict => conflict.Id), "conflict", assembly.Name);
        ValidateStableIds(
            graph.SourceComponentInstances.Select(instance => instance.Id),
            "source component instance",
            assembly.Name);

        var duplicateObjectId = graph.Nodes
            .Where(node => node.ObjectId != Guid.Empty)
            .GroupBy(node => node.ObjectId)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicateObjectId.HasValue)
        {
            throw new InvalidOperationException(
                $"Assembly '{assembly.Name}' maps Rhino object {duplicateObjectId:D} to more than one link node.");
        }

        var nodeIds = graph.Nodes.Select(node => node.Id).ToHashSet();
        foreach (var edge in graph.Edges)
        {
            if (edge.ParentNodeId == edge.ChildNodeId ||
                !nodeIds.Contains(edge.ParentNodeId) ||
                !nodeIds.Contains(edge.ChildNodeId))
            {
                throw new InvalidOperationException(
                    $"Assembly '{assembly.Name}' contains link edge {edge.Id:D} with a missing or self-referential endpoint.");
            }
        }

        foreach (var instance in graph.SourceComponentInstances)
        {
            if (instance.SourceNodeIds.Any(nodeId => !nodeIds.Contains(nodeId)))
            {
                throw new InvalidOperationException(
                    $"Assembly '{assembly.Name}' contains source component instance {instance.Id:D} with a missing node.");
            }
        }

        if (GraphContainsCycle(graph.Nodes, graph.Edges))
            throw new InvalidOperationException($"Assembly '{assembly.Name}' contains a cyclic link graph.");
    }

    private static void ValidateStableIds(IEnumerable<Guid> ids, string recordType, string assemblyName)
    {
        var values = ids.ToList();
        if (values.Any(id => id == Guid.Empty))
        {
            throw new InvalidOperationException(
                $"Assembly '{assemblyName}' contains a {recordType} with no stable identity.");
        }

        var duplicateId = values.GroupBy(id => id).FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicateId.HasValue)
        {
            throw new InvalidOperationException(
                $"Assembly '{assemblyName}' contains duplicate {recordType} identity {duplicateId:D}.");
        }
    }

    private static bool GraphContainsCycle(
        IReadOnlyCollection<AssemblyLinkNodeRecord> nodes,
        IReadOnlyCollection<AssemblyLinkEdgeRecord> edges)
    {
        var indegree = nodes.ToDictionary(node => node.Id, _ => 0);
        var outgoing = new Dictionary<Guid, List<Guid>>();
        foreach (var edge in edges)
        {
            indegree[edge.ChildNodeId]++;
            if (!outgoing.TryGetValue(edge.ParentNodeId, out var children))
            {
                children = new List<Guid>();
                outgoing[edge.ParentNodeId] = children;
            }
            children.Add(edge.ChildNodeId);
        }

        var pending = new Queue<Guid>(indegree.Where(item => item.Value == 0).Select(item => item.Key));
        var visited = 0;
        while (pending.Count > 0)
        {
            var nodeId = pending.Dequeue();
            visited++;
            if (!outgoing.TryGetValue(nodeId, out var children))
                continue;

            foreach (var childId in children)
            {
                indegree[childId]--;
                if (indegree[childId] == 0)
                    pending.Enqueue(childId);
            }
        }

        return visited != nodes.Count;
    }

    private static bool TransformRecordsEqual(TransformRecord? first, TransformRecord? second)
    {
        var firstValues = first?.Values ?? Array.Empty<double>();
        var secondValues = second?.Values ?? Array.Empty<double>();
        return firstValues.Length == secondValues.Length && firstValues.SequenceEqual(secondValues);
    }

    private static bool IsFiniteAffineInvertible(TransformRecord? record)
    {
        var values = record?.Values;
        if (values is null || values.Length != 16 ||
            values.Any(value => double.IsNaN(value) || double.IsInfinity(value)))
        {
            return false;
        }

        const double affineTolerance = 1e-12;
        if (Math.Abs(values[12]) > affineTolerance ||
            Math.Abs(values[13]) > affineTolerance ||
            Math.Abs(values[14]) > affineTolerance ||
            Math.Abs(values[15] - 1.0) > affineTolerance)
        {
            return false;
        }

        var determinant =
            values[0] * (values[5] * values[10] - values[6] * values[9]) -
            values[1] * (values[4] * values[10] - values[6] * values[8]) +
            values[2] * (values[4] * values[9] - values[5] * values[8]);
        return !double.IsNaN(determinant) && !double.IsInfinity(determinant) && determinant != 0.0;
    }

    private static void AddRepositoryConflict(
        AssemblyRecord assembly,
        string conflictType,
        string message,
        Guid nodeId = default,
        Guid edgeId = default,
        IEnumerable<Guid>? candidateObjectIds = null)
    {
        var existing = assembly.LinkGraph.Conflicts.FirstOrDefault(conflict =>
            string.Equals(conflict.Status, AssemblyLinkStatuses.Open, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(conflict.ConflictType, conflictType, StringComparison.OrdinalIgnoreCase) &&
            conflict.NodeId == nodeId && conflict.EdgeId == edgeId);
        if (existing is not null)
            return;

        assembly.LinkGraph.Conflicts.Add(new LinkConflictRecord
        {
            ConflictType = conflictType,
            NodeId = nodeId,
            EdgeId = edgeId,
            Message = message,
            CandidateObjectIds = (candidateObjectIds ?? Array.Empty<Guid>())
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToList()
        });
        assembly.LinkGraph.UpdatedAt = DateTimeOffset.UtcNow;
        assembly.UpdatedAt = assembly.LinkGraph.UpdatedAt;
    }

    public IReadOnlyList<string> GetAssemblyNames(RhinoDoc doc)
    {
        return Load(doc).Assemblies
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .Select(a => a.Name)
            .ToList();
    }

    public void UpsertAssembly(RhinoDoc doc, AssemblyRecord assembly)
    {
        var store = Load(doc);
        var existing = store.Assemblies.FindIndex(a => string.Equals(a.Name, assembly.Name, StringComparison.OrdinalIgnoreCase));
        assembly.UpdatedAt = DateTimeOffset.UtcNow;
        if (existing >= 0)
            store.Assemblies[existing] = assembly;
        else
            store.Assemblies.Add(assembly);

        Save(doc, store);
    }

    public bool RemoveAssembly(RhinoDoc doc, string assemblyName)
    {
        var store = Load(doc);
        var removed = store.Assemblies.RemoveAll(a => string.Equals(a.Name, assemblyName, StringComparison.OrdinalIgnoreCase)) > 0;
        if (removed)
            Save(doc, store);

        return removed;
    }
}
