using AssemblyManagerPlugin.Core;
using AssemblyManagerPlugin.Geometry;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace AssemblyManagerPlugin.Services;

/// <summary>
/// Rebuilds assembly part/component definitions from live design-source evidence after linked
/// geometry changes. Derived nodes inherit categorization from their source lineage and never
/// vote on equivalence. Existing names are retained for stable cohorts; true splits receive the
/// next monotonic assembly-local number.
/// </summary>
public sealed class AssemblyCategorizationReconciliationService
{
    private readonly GeometryFingerprintService _fingerprints;
    private readonly LayerService _layers;
    private readonly AssemblyLineageService _lineage;
    private readonly PluginSettingsService? _settings;

    public AssemblyCategorizationReconciliationService(
        GeometryFingerprintService fingerprints,
        LayerService layers,
        AssemblyLineageService lineage,
        PluginSettingsService? settings = null)
    {
        _fingerprints = fingerprints;
        _layers = layers;
        _lineage = lineage;
        _settings = settings;
    }

    public CategorizationReconciliationResult Reconcile(RhinoDoc doc, AssemblyRecord assembly)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(assembly);

        using var mutation = AssemblyLinkMutationGate.Enter();
        InvalidateManufacturingCaches(assembly);
        var oldPartNameByObjectId = assembly.LinkGraph.Nodes
            .Where(node => node.ObjectId != Guid.Empty)
            .Select(node =>
            {
                var partName = assembly.Parts.FirstOrDefault(part => part.Id == node.PartId)?.Name;
                return (node.ObjectId, PartName: partName);
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.PartName))
            .GroupBy(item => item.ObjectId)
            .ToDictionary(group => group.Key, group => group.First().PartName!);

        var evidence = CapturePartEvidence(doc, assembly, out var protectedPartIds);
        var hasHardwareSources = assembly.Hardware.Any(hardware => assembly.LinkGraph.Nodes.Any(node =>
            string.Equals(node.Role, AssemblyLinkRoles.Source, StringComparison.OrdinalIgnoreCase) &&
            (node.ObjectId == hardware.SourceObjectId || node.ObjectId == hardware.BlockInstanceId)));
        // A component can consist entirely of hardware. It still needs membership,
        // quantities and category reconciliation even without manufacturable part evidence.
        if (evidence.Count == 0 && (protectedPartIds.Count > 0 || !hasHardwareSources))
        {
            assembly.UpdatedAt = DateTimeOffset.UtcNow;
            assembly.LinkGraph.UpdatedAt = assembly.UpdatedAt;
            if (protectedPartIds.Count > 0)
            {
                RhinoApp.WriteLine(
                    "Gazelle preserved {0} part category or categories in '{1}' because their source evidence needs review. No complete supported part category was available to recategorize.",
                    protectedPartIds.Count,
                    assembly.Name);
            }
            else
            {
                RhinoApp.WriteLine(
                    "Gazelle found no supported part source occurrences in '{0}' to recategorize.",
                    assembly.Name);
            }
            return CategorizationReconciliationResult.InvalidatedOnly;
        }

        var clusters = BuildPartClusters(assembly, evidence, out var hasAmbiguousPartMatch);
        if (hasAmbiguousPartMatch)
        {
            assembly.UpdatedAt = DateTimeOffset.UtcNow;
            assembly.LinkGraph.UpdatedAt = assembly.UpdatedAt;
            return CategorizationReconciliationResult.InvalidatedOnly;
        }

        var colorizeParts = _settings?.Load().AssemblyManager.ColorizeParts ?? true;
        // Capture category colors before any nodes/layers move. Otherwise a newly split
        // category inherits the edited object's old layer color, or a merge loses the
        // existing destination category's custom color.
        var colorsByPartId = assembly.Parts.ToDictionary(part => part.Id, part =>
            _layers.GetOrAssignPartColor(doc, assembly, part, colorizeParts));
        var knownPartNames = assembly.Parts.Select(part => part.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var previousComponentNames = assembly.Components.Select(component => component.Name).ToList();
        var assignments = AssignPartDefinitions(assembly, clusters);
        AssignNewPartColors(assignments, colorsByPartId, colorizeParts);
        var reassignedSources = ApplyPartAssignments(assembly, assignments, protectedPartIds);
        RebuildPartMembership(
            doc,
            assembly,
            assignments.Values.Select(assignment => assignment.Part.Id)
                .Concat(evidence.Select(item => item.OldPart.Id))
                .ToHashSet());

        var componentResult = ReconcileComponents(doc, assembly, protectedPartIds, out var protectedComponentIds);
        UpdateLegacyReferences(assembly, protectedPartIds);
        MoveManagedObjectsToCategorizedLayers(doc, assembly, oldPartNameByObjectId, protectedPartIds, colorsByPartId);
        ResolveSuccessfulPartConflicts(assembly, assignments.SelectMany(pair => pair.Key.Members));

        var activePartIds = assembly.LinkGraph.Nodes
            .Select(node => node.PartId)
            .Where(id => id != Guid.Empty)
            .ToHashSet();
        var retiredPartCount = assembly.Parts.RemoveAll(part =>
            !protectedPartIds.Contains(part.Id) &&
            !activePartIds.Contains(part.Id) && part.SourceObjectIds.Count == 0);
        var activeComponentIds = assembly.LinkGraph.Nodes
            .Select(node => node.ComponentId)
            .Concat(assembly.LinkGraph.SourceComponentInstances.Select(instance => instance.ComponentId))
            .Where(id => id != Guid.Empty)
            .ToHashSet();
        var retiredComponentCount = componentResult.Reconciled
            ? assembly.Components.RemoveAll(component =>
                !protectedComponentIds.Contains(component.Id) && !activeComponentIds.Contains(component.Id))
            : 0;

        // Retired components still carry their old membership until removed above. Do not
        // let that stale membership preserve empty leaves after a component-category merge.
        RemoveObsoleteComponentPartLayers(doc, assembly, previousComponentNames, knownPartNames);

        // Every complete supported source category was re-evaluated. Even when stable
        // category IDs were retained, geometry/material evidence may have changed.
        const bool changed = true;

        var liveFlatNodes = assembly.LinkGraph.Nodes.Where(node =>
                string.Equals(node.Role, AssemblyLinkRoles.FlatPart, StringComparison.OrdinalIgnoreCase) &&
                doc.Objects.FindId(node.ObjectId) is { IsDeleted: false })
            .ToList();
        var requiresFlatRebuild = liveFlatNodes.Count > 0 && assembly.Parts
            .Where(part => part.Quantity > 0 && !protectedPartIds.Contains(part.Id))
            .Any(part => liveFlatNodes.Count(node => node.PartId == part.Id) != 1);
        var liveCopiedNodes = assembly.LinkGraph.Nodes.Where(node =>
                string.Equals(node.Role, AssemblyLinkRoles.CopiedComponent, StringComparison.OrdinalIgnoreCase) &&
                doc.Objects.FindId(node.ObjectId) is { IsDeleted: false })
            .ToList();
        var requiresCopiedComponentRebuild = componentResult.Reconciled &&
                                               liveCopiedNodes.Count > 0 &&
                                               assembly.Components
            .Where(component => !protectedComponentIds.Contains(component.Id))
            // Drawing views do not vote on category quantity. Multiple views of one
            // occurrence, or views of different occurrences whose categories merged,
            // remain valid independently tracked output. Only missing coverage needs
            // another placement; never ask the operator to delete valid extra views.
            .Any(component => !liveCopiedNodes.Any(node =>
                node.ComponentId == component.Id && node.SourceComponentInstanceId != Guid.Empty));

        return new CategorizationReconciliationResult(
            assignments.Values.Count(part => part.WasCreated),
            retiredPartCount,
            reassignedSources,
            componentResult.CreatedComponents,
            retiredComponentCount,
            componentResult.ChangedInstances,
            requiresFlatRebuild,
            requiresCopiedComponentRebuild,
            changed)
        {
            PartsReconciled = true,
            ProtectedPartIds = protectedPartIds
        };
    }

    private static void InvalidateManufacturingCaches(AssemblyRecord assembly)
    {
        assembly.NestingEstimates.Clear();
        assembly.LastMaterialEstimate = null;
        assembly.LastBillOfMaterials = null;
        assembly.UpdatedAt = DateTimeOffset.UtcNow;
        assembly.LinkGraph.UpdatedAt = assembly.UpdatedAt;
    }

    private List<SourcePartEvidence> CapturePartEvidence(
        RhinoDoc doc,
        AssemblyRecord assembly,
        out HashSet<Guid> protectedPartIds)
    {
        var partsById = assembly.Parts.ToDictionary(part => part.Id);
        var nodesById = assembly.LinkGraph.Nodes.ToDictionary(node => node.Id);
        var blockBackedPartIds = assembly.LinkGraph.Edges
            .Where(edge => string.Equals(
                edge.Recipe,
                AssemblyLinkRecipes.BlockDefinitionPart,
                StringComparison.OrdinalIgnoreCase))
            .SelectMany(edge => new[]
            {
                nodesById.GetValueOrDefault(edge.ParentNodeId)?.PartId ?? Guid.Empty,
                nodesById.GetValueOrDefault(edge.ChildNodeId)?.PartId ?? Guid.Empty
            })
            .Where(partId => partId != Guid.Empty)
            .ToHashSet();
        protectedPartIds = new HashSet<Guid>(blockBackedPartIds);
        foreach (var partId in blockBackedPartIds)
        {
            var categoryNodes = assembly.LinkGraph.Nodes.Where(node => node.PartId == partId).ToList();
            var categoryName = partsById.GetValueOrDefault(partId)?.Name ?? partId.ToString();
            AddOrUpdateConflict(
                assembly,
                AssemblyLinkConflictTypes.PartCategorizationChanged,
                categoryNodes.FirstOrDefault()?.Id ?? Guid.Empty,
                $"Part category '{categoryName}' contains block-definition geometry without independent source occurrence identities. Gazelle preserved this category for review; other complete categories can still recategorize.",
                categoryNodes.Select(node => node.ObjectId));
        }
        var result = new List<SourcePartEvidence>();
        foreach (var node in assembly.LinkGraph.Nodes
                     .Where(node => node.PartId != Guid.Empty &&
                                    string.Equals(node.Role, AssemblyLinkRoles.Source, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(node => partsById.GetValueOrDefault(node.PartId)?.Name, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(node => node.Id))
        {
            // A block instance can represent several definition leaves through one Rhino UUID.
            // Until those leaves have independent occurrence identities, preserve the complete
            // category rather than collapsing quantities while rebuilding membership.
            if (blockBackedPartIds.Contains(node.PartId))
            {
                continue;
            }

            if (!partsById.TryGetValue(node.PartId, out var oldPart))
            {
                protectedPartIds.Add(node.PartId);
                AddOrUpdateConflict(
                    assembly,
                    AssemblyLinkConflictTypes.PartCategorizationChanged,
                    node.Id,
                    "This source refers to a missing part definition. Gazelle preserved the existing categorization for review.",
                    new[] { node.ObjectId });
                continue;
            }

            var rhinoObject = doc.Objects.FindId(node.ObjectId);
            if (rhinoObject is null || rhinoObject.IsDeleted)
            {
                protectedPartIds.Add(node.PartId);
                AddOrUpdateConflict(
                    assembly,
                    AssemblyLinkConflictTypes.PartCategorizationChanged,
                    node.Id,
                    $"Source geometry for '{oldPart.Name}' cannot be safely recategorized because the Rhino object is missing.",
                    new[] { node.ObjectId });
                continue;
            }

            if (!_fingerprints.TryCreatePartCandidate(rhinoObject, out var candidate, out var warning))
            {
                protectedPartIds.Add(node.PartId);
                AddOrUpdateConflict(
                    assembly,
                    AssemblyLinkConflictTypes.PartCategorizationChanged,
                    node.Id,
                    string.IsNullOrWhiteSpace(warning)
                        ? $"Source geometry for '{oldPart.Name}' cannot be safely recategorized."
                        : $"Source geometry for '{oldPart.Name}' cannot be safely recategorized: {warning}",
                    new[] { node.ObjectId });
                continue;
            }

            candidate.MaterialId = MaterialAssignment.GetCategorizationMaterialId(rhinoObject.Attributes);
            result.Add(new SourcePartEvidence(node, oldPart, candidate, node.GeometryFingerprint,
                MaterialAssignment.GetMaterialId(rhinoObject.Attributes)));
        }

        // One uncertain occurrence protects its entire existing category: using only its live
        // siblings could lose quantities or give the old number to the wrong cohort. Unrelated
        // complete categories still participate, so they can split without waiting for repair.
        var excludedPartIds = protectedPartIds;
        result.RemoveAll(item => excludedPartIds.Contains(item.OldPart.Id));
        return result;
    }

    private List<PartCluster> BuildPartClusters(
        AssemblyRecord assembly,
        IReadOnlyList<SourcePartEvidence> evidence,
        out bool hasAmbiguousMatch)
    {
        hasAmbiguousMatch = false;
        // Creation compares each occurrence with a category representative, so tolerated
        // differences need not be transitive between every pair of its members. Re-clustering
        // unchanged occurrences can therefore split an existing category (or report an
        // ambiguity) merely because a different member happens to be visited first. Their
        // stored identities are the anchors; only changed occurrences need a new decision.
        var unchangedNodeIds = evidence
            .Where(item => PreservesStoredPartIdentity(item, item.OldPart))
            .Select(item => item.Node.Id)
            .ToHashSet();
        var clusters = evidence
            .Where(item => unchangedNodeIds.Contains(item.Node.Id))
            .GroupBy(item => item.OldPart.Id)
            .Select(group =>
            {
                var cluster = new PartCluster(group.First());
                cluster.Members.AddRange(group.Skip(1));
                return cluster;
            })
            .ToList();
        foreach (var item in evidence.Where(item => !unchangedNodeIds.Contains(item.Node.Id)))
        {
            var matches = clusters.Where(cluster =>
                    cluster.Members.All(member =>
                        _fingerprints.AreEquivalentParts(member.Candidate, item.Candidate)))
                .ToList();
            if (matches.Count == 1)
            {
                matches[0].Members.Add(item);
                continue;
            }

            if (matches.Count > 1)
            {
                hasAmbiguousMatch = true;
                AddOrUpdateConflict(
                    assembly,
                    AssemblyLinkConflictTypes.PartCategorizationChanged,
                    item.Node.Id,
                    "This part falls within tolerance of more than one live category. Gazelle preserved its existing part number for review instead of making a non-transitive merge.",
                    new[] { item.Node.ObjectId });
                continue;
            }

            clusters.Add(new PartCluster(item));
        }

        return clusters;
    }

    private Dictionary<PartCluster, AssignedPart> AssignPartDefinitions(
        AssemblyRecord assembly,
        IReadOnlyList<PartCluster> clusters)
    {
        var preferredParts = clusters.ToDictionary(cluster => cluster, _ => new List<PartRecord>());
        foreach (var part in assembly.Parts)
        {
            var occupiedClusters = clusters
                .Where(cluster => cluster.Members.Any(member => member.OldPart.Id == part.Id))
                .ToList();
            if (occupiedClusters.Count == 0)
                continue;

            var retained = occupiedClusters
                .OrderByDescending(cluster => cluster.Members.Count(member =>
                    member.OldPart.Id == part.Id &&
                    PreservesStoredPartIdentity(member, part)))
                .ThenByDescending(cluster => cluster.Members.Count(member => member.OldPart.Id == part.Id))
                .ThenBy(cluster => cluster.SortKey, StringComparer.Ordinal)
                .First();
            preferredParts[retained].Add(part);
        }

        var assignments = new Dictionary<PartCluster, AssignedPart>();
        foreach (var cluster in clusters.OrderBy(cluster => cluster.SortKey, StringComparer.Ordinal))
        {
            var preferred = preferredParts[cluster]
                .OrderByDescending(part => cluster.Members.Count(member =>
                    member.OldPart.Id == part.Id &&
                    PreservesStoredPartIdentity(member, part)))
                .ThenByDescending(part => cluster.Members.Count(member => member.OldPart.Id == part.Id))
                .ThenBy(part => part.Name, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            var created = preferred is null;
            var dominantOldPart = cluster.Members
                .GroupBy(member => member.OldPart)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key.Name, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Key)
                .First();
            var assignedPart = preferred ?? new PartRecord
            {
                Name = AllocatePartName(assembly),
                Description = dominantOldPart.Description,
                ReferenceId = dominantOldPart.ReferenceId,
                CategorizationMaterialId = dominantOldPart.CategorizationMaterialId,
                MaterialId = dominantOldPart.MaterialId
            };
            if (created)
                assembly.Parts.Add(assignedPart);

            var representative = cluster.Members
                .OrderByDescending(member => member.OldPart.Id == assignedPart.Id)
                .ThenByDescending(member => string.Equals(
                    member.Candidate.Fingerprint,
                    assignedPart.GeometryFingerprint,
                    StringComparison.Ordinal))
                .ThenBy(member => member.Node.Id)
                .First();
            assignedPart.GeometryFingerprint = representative.Candidate.Fingerprint;
            var previousCategorizationMaterialId = MaterialAssignment.NormalizeMaterialIdForCategory(
                assignedPart.CategorizationMaterialId ?? assignedPart.MaterialId);
            var currentCategorizationMaterialId = MaterialAssignment.NormalizeMaterialIdForCategory(
                representative.Candidate.MaterialId);
            if (!string.Equals(
                    previousCategorizationMaterialId,
                    currentCategorizationMaterialId,
                    StringComparison.Ordinal) &&
                !string.Equals(MaterialAssignment.GetParentIdForStoredAssignment(assignedPart.MaterialId),
                    currentCategorizationMaterialId, StringComparison.Ordinal))
            {
                // A stock shape selected for the previous parent material must not carry into a
                // different material category. Geometry-only splits retain the existing choice.
                // Candidate.MaterialId already supplies the authoritative parent identity.
                // Stock IDs are arbitrary library IDs, not necessarily legacy AMMAT tokens.
                assignedPart.MaterialId = string.IsNullOrWhiteSpace(representative.AssignedMaterialId)
                    ? currentCategorizationMaterialId : representative.AssignedMaterialId;
            }
            assignedPart.CategorizationMaterialId = currentCategorizationMaterialId;
            if (representative.Candidate.Geometry is Brep brep)
                assignedPart.MaterialThickness = _fingerprints.GetMaterialThickness(brep);
            assignments[cluster] = new AssignedPart(assignedPart, created);
        }

        return assignments;
    }

    private int ApplyPartAssignments(
        AssemblyRecord assembly,
        IReadOnlyDictionary<PartCluster, AssignedPart> assignments,
        IReadOnlySet<Guid> protectedPartIds)
    {
        var changedSources = 0;
        var desiredPartsByNodeId = new Dictionary<Guid, HashSet<Guid>>();
        var nodesById = assembly.LinkGraph.Nodes.ToDictionary(node => node.Id);
        var outgoing = assembly.LinkGraph.Edges
            .Where(edge => string.Equals(
                edge.Status,
                AssemblyLinkStatuses.Active,
                StringComparison.OrdinalIgnoreCase))
            .GroupBy(edge => edge.ParentNodeId)
            .ToDictionary(group => group.Key, group => group.ToList());

        foreach (var (cluster, assigned) in assignments)
        {
            foreach (var evidence in cluster.Members)
            {
                if (evidence.Node.PartId != assigned.Part.Id)
                    changedSources++;
                evidence.Node.PartId = assigned.Part.Id;
                evidence.Node.GeometryFingerprint = evidence.Candidate.Fingerprint;
                evidence.Node.UpdatedAt = DateTimeOffset.UtcNow;

                var pending = new Queue<Guid>();
                var visited = new HashSet<Guid>();
                pending.Enqueue(evidence.Node.Id);
                while (pending.Count > 0)
                {
                    var parentId = pending.Dequeue();
                    if (!visited.Add(parentId))
                        continue;
                    if (!outgoing.TryGetValue(parentId, out var edges))
                        continue;

                    foreach (var edge in edges)
                    {
                        if (!nodesById.TryGetValue(edge.ChildNodeId, out var child) ||
                            protectedPartIds.Contains(child.PartId) ||
                            !string.Equals(
                                child.Status,
                                AssemblyLinkStatuses.Active,
                                StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(child.Role, AssemblyLinkRoles.Source, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (!desiredPartsByNodeId.TryGetValue(child.Id, out var desiredPartIds))
                        {
                            desiredPartIds = new HashSet<Guid>();
                            desiredPartsByNodeId[child.Id] = desiredPartIds;
                        }
                        desiredPartIds.Add(assigned.Part.Id);
                        pending.Enqueue(child.Id);
                    }
                }
            }
        }

        foreach (var (nodeId, partIds) in desiredPartsByNodeId)
        {
            var node = nodesById[nodeId];
            if (partIds.Count != 1)
            {
                AddOrUpdateConflict(
                    assembly,
                    AssemblyLinkConflictTypes.PartCategorizationChanged,
                    node.Id,
                    "A derived object is reachable from source occurrences assigned to different part categories. Gazelle preserved the existing derived assignment for review.",
                    new[] { node.ObjectId });
                continue;
            }

            node.PartId = partIds.Single();
            node.UpdatedAt = DateTimeOffset.UtcNow;
        }

        return changedSources;
    }

    private static void RebuildPartMembership(
        RhinoDoc doc,
        AssemblyRecord assembly,
        IReadOnlySet<Guid> reconciledPartIds)
    {
        var trackedObjectIds = assembly.LinkGraph.Nodes.Select(node => node.ObjectId).ToHashSet();
        foreach (var part in assembly.Parts)
        {
            if (!reconciledPartIds.Contains(part.Id))
                continue;

            var legacySources = part.SourceObjectIds.Where(id =>
                !trackedObjectIds.Contains(id) && doc.Objects.FindId(id) is { IsDeleted: false });
            var legacyGenerated = part.GeneratedObjectIds.Where(id =>
                !trackedObjectIds.Contains(id) && doc.Objects.FindId(id) is { IsDeleted: false });
            var legacyFlat = part.CamObjectIds.Where(id =>
                !trackedObjectIds.Contains(id) && doc.Objects.FindId(id) is { IsDeleted: false });

            part.SourceObjectIds = assembly.LinkGraph.Nodes
                .Where(node => node.PartId == part.Id &&
                               string.Equals(node.Role, AssemblyLinkRoles.Source, StringComparison.OrdinalIgnoreCase))
                .Select(node => node.ObjectId)
                .Concat(legacySources)
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToList();
            part.GeneratedObjectIds = assembly.LinkGraph.Nodes
                .Where(node => node.PartId == part.Id &&
                               string.Equals(node.Role, AssemblyLinkRoles.OriginalAssembly, StringComparison.OrdinalIgnoreCase))
                .Select(node => node.ObjectId)
                .Concat(legacyGenerated)
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToList();
            part.CamObjectIds = assembly.LinkGraph.Nodes
                .Where(node => node.PartId == part.Id &&
                               string.Equals(node.Role, AssemblyLinkRoles.FlatPart, StringComparison.OrdinalIgnoreCase))
                .Select(node => node.ObjectId)
                .Concat(legacyFlat)
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToList();
            part.Quantity = part.SourceObjectIds.Count;
        }
    }

    private ComponentReconciliationResult ReconcileComponents(
        RhinoDoc doc,
        AssemblyRecord assembly,
        IReadOnlySet<Guid> protectedPartIds,
        out HashSet<Guid> protectedComponentIds)
    {
        var evidence = CaptureComponentEvidence(doc, assembly, protectedPartIds, out protectedComponentIds);
        if (evidence.Count == 0)
            return ComponentReconciliationResult.Empty;

        var clusters = BuildComponentClusters(assembly, evidence, out var hasAmbiguousMatch);
        if (hasAmbiguousMatch)
            return ComponentReconciliationResult.Empty;

        var assignments = AssignComponentDefinitions(assembly, clusters);
        var changedInstances = ApplyComponentAssignments(assembly, assignments, protectedPartIds, protectedComponentIds);
        RebuildComponentRecords(assembly, assignments);
        ResolveSuccessfulComponentConflicts(
            assembly,
            evidence.SelectMany(item => item.SourceNodes).Select(node => node.Id));
        return new ComponentReconciliationResult(
            assignments.Values.Count(component => component.WasCreated),
            changedInstances,
            Reconciled: true);
    }

    private List<ComponentInstanceEvidence> CaptureComponentEvidence(
        RhinoDoc doc,
        AssemblyRecord assembly,
        IReadOnlySet<Guid> protectedPartIds,
        out HashSet<Guid> protectedComponentIds)
    {
        var nodesById = assembly.LinkGraph.Nodes.ToDictionary(node => node.Id);
        var partsById = assembly.Parts.ToDictionary(part => part.Id);
        var componentsById = assembly.Components.ToDictionary(component => component.Id);
        var componentIdsWithOccurrences = assembly.LinkGraph.SourceComponentInstances
            .Select(instance => instance.ComponentId)
            .ToHashSet();
        protectedComponentIds = componentsById.Keys
            .Where(componentId => !componentIdsWithOccurrences.Contains(componentId))
            .ToHashSet();
        if (protectedComponentIds.Count > 0)
        {
            var preservedNames = protectedComponentIds.Select(componentId => componentsById[componentId].Name)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase);
            AddOrUpdateConflict(
                assembly,
                AssemblyLinkConflictTypes.ComponentCategorizationChanged,
                Guid.Empty,
                $"Component categories '{string.Join(", ", preservedNames)}' have no recorded source occurrences. Gazelle preserved these component numbers and quantities for review.",
                Array.Empty<Guid>());
        }
        var result = new List<ComponentInstanceEvidence>();
        foreach (var instance in assembly.LinkGraph.SourceComponentInstances.OrderBy(instance => instance.Id))
        {
            var candidates = new List<PartCandidate>();
            var sourceNodes = instance.SourceNodeIds
                .Select(id => nodesById.GetValueOrDefault(id))
                .Where(node => node is not null)
                .Cast<AssemblyLinkNodeRecord>()
                .ToList();
            var instanceIsComplete = sourceNodes.Count > 0 &&
                                     sourceNodes.Count == instance.SourceNodeIds.Distinct().Count();
            foreach (var sourceNode in sourceNodes)
            {
                if (protectedPartIds.Contains(sourceNode.PartId))
                {
                    instanceIsComplete = false;
                    continue;
                }
                var rhinoObject = doc.Objects.FindId(sourceNode.ObjectId);
                if (rhinoObject is null || rhinoObject.IsDeleted)
                {
                    instanceIsComplete = false;
                    continue;
                }

                if (partsById.TryGetValue(sourceNode.PartId, out var part) &&
                    _fingerprints.TryCreatePartCandidate(rhinoObject, out var candidate, out _))
                {
                    candidate.PartName = part.Name;
                    candidate.MaterialId = MaterialAssignment.NormalizeMaterialIdForCategory(
                        part.CategorizationMaterialId ?? part.MaterialId);
                    candidates.Add(candidate);
                    continue;
                }

                var hardware = assembly.Hardware.FirstOrDefault(item =>
                    item.SourceObjectId == sourceNode.ObjectId || item.BlockInstanceId == sourceNode.ObjectId);
                if (hardware is null)
                {
                    instanceIsComplete = false;
                    continue;
                }

                var bounds = rhinoObject.Geometry.GetBoundingBox(true);
                candidates.Add(new PartCandidate
                {
                    SourceObjectId = sourceNode.ObjectId,
                    PartName = hardware.LayerName,
                    Fingerprint = $"hardware:{hardware.LayerName}",
                    MaterialId = hardware.MaterialId,
                    Centroid = bounds.IsValid ? bounds.Center : Point3d.Origin,
                    Geometry = rhinoObject.Geometry
                });
            }

            if (!instanceIsComplete || candidates.Count != sourceNodes.Count)
            {
                if (instance.ComponentId != Guid.Empty)
                    protectedComponentIds.Add(instance.ComponentId);
                AddOrUpdateConflict(
                    assembly,
                    AssemblyLinkConflictTypes.ComponentCategorizationChanged,
                    sourceNodes.FirstOrDefault()?.Id ?? Guid.Empty,
                    $"Component occurrence '{instance.SourceGroupName}' has incomplete source evidence or belongs to a part category under review. Gazelle preserved its entire existing component category; other complete component categories can still recategorize.",
                    sourceNodes.Select(node => node.ObjectId));
                continue;
            }

            if (candidates.Count == 0)
                continue;

            componentsById.TryGetValue(instance.ComponentId, out var oldComponent);
            result.Add(new ComponentInstanceEvidence(
                instance,
                oldComponent,
                sourceNodes,
                candidates,
                _fingerprints.CreateComponentFingerprint(candidates)));
        }

        // Component quantities also depend on complete occurrence cohorts. Preserve every
        // instance of an uncertain old category without blocking unrelated component types.
        var excludedComponentIds = protectedComponentIds;
        result.RemoveAll(item => excludedComponentIds.Contains(item.Instance.ComponentId));
        return result;
    }

    private List<ComponentCluster> BuildComponentClusters(
        AssemblyRecord assembly,
        IReadOnlyList<ComponentInstanceEvidence> evidence,
        out bool hasAmbiguousMatch)
    {
        hasAmbiguousMatch = false;
        var clusters = new List<ComponentCluster>();
        foreach (var item in evidence)
        {
            var matches = clusters.Where(candidate =>
                    candidate.Members.All(member =>
                        _fingerprints.AreEquivalentComponents(member.Candidates, item.Candidates)))
                .ToList();
            if (matches.Count == 1)
            {
                matches[0].Members.Add(item);
            }
            else if (matches.Count == 0)
            {
                clusters.Add(new ComponentCluster(item));
            }
            else
            {
                hasAmbiguousMatch = true;
                AddOrUpdateConflict(
                    assembly,
                    AssemblyLinkConflictTypes.ComponentCategorizationChanged,
                    item.SourceNodes.FirstOrDefault()?.Id ?? Guid.Empty,
                    "This component occurrence falls within tolerance of more than one live component category. Gazelle preserved the existing component numbers for review.",
                    item.SourceNodes.Select(node => node.ObjectId));
            }
        }

        return clusters;
    }

    private Dictionary<ComponentCluster, AssignedComponent> AssignComponentDefinitions(
        AssemblyRecord assembly,
        IReadOnlyList<ComponentCluster> clusters)
    {
        var preferredComponents = clusters.ToDictionary(cluster => cluster, _ => new List<ComponentRecord>());
        foreach (var component in assembly.Components)
        {
            var occupied = clusters
                .Where(cluster => cluster.Members.Any(member => member.OldComponent?.Id == component.Id))
                .ToList();
            if (occupied.Count == 0)
                continue;

            var retained = occupied
                .OrderByDescending(cluster => cluster.Members.Count(member =>
                    member.OldComponent?.Id == component.Id &&
                    string.Equals(member.Fingerprint, component.Fingerprint, StringComparison.Ordinal)))
                .ThenByDescending(cluster => cluster.Members.Count(member => member.OldComponent?.Id == component.Id))
                .ThenBy(cluster => cluster.SortKey, StringComparer.Ordinal)
                .First();
            preferredComponents[retained].Add(component);
        }

        var assignments = new Dictionary<ComponentCluster, AssignedComponent>();
        foreach (var cluster in clusters.OrderBy(cluster => cluster.SortKey, StringComparer.Ordinal))
        {
            var preferred = preferredComponents[cluster]
                .OrderByDescending(component => cluster.Members.Count(member =>
                    member.OldComponent?.Id == component.Id &&
                    string.Equals(member.Fingerprint, component.Fingerprint, StringComparison.Ordinal)))
                .ThenByDescending(component => cluster.Members.Count(member => member.OldComponent?.Id == component.Id))
                .ThenBy(component => component.Name, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            var created = preferred is null;
            var dominantOldComponent = cluster.Members
                .Where(member => member.OldComponent is not null)
                .GroupBy(member => member.OldComponent!)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key.Name, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Key)
                .FirstOrDefault();
            var assigned = preferred ?? new ComponentRecord
            {
                Name = AllocateComponentName(assembly),
                Description = dominantOldComponent?.Description ?? string.Empty
            };
            if (created)
                assembly.Components.Add(assigned);
            assignments[cluster] = new AssignedComponent(assigned, created);
        }

        return assignments;
    }

    private static int ApplyComponentAssignments(
        AssemblyRecord assembly,
        IReadOnlyDictionary<ComponentCluster, AssignedComponent> assignments,
        IReadOnlySet<Guid> protectedPartIds,
        IReadOnlySet<Guid> protectedComponentIds)
    {
        var changed = 0;
        var nodesById = assembly.LinkGraph.Nodes.ToDictionary(node => node.Id);
        var outgoing = assembly.LinkGraph.Edges
            .Where(edge => string.Equals(
                edge.Status,
                AssemblyLinkStatuses.Active,
                StringComparison.OrdinalIgnoreCase))
            .GroupBy(edge => edge.ParentNodeId)
            .ToDictionary(group => group.Key, group => group.ToList());
        var desiredAssignmentsByNodeId = new Dictionary<Guid, HashSet<(Guid ComponentId, Guid InstanceId)>>();
        foreach (var (cluster, assigned) in assignments)
        {
            foreach (var member in cluster.Members)
            {
                if (member.Instance.ComponentId != assigned.Component.Id)
                    changed++;
                member.Instance.ComponentId = assigned.Component.Id;
                member.Instance.UpdatedAt = DateTimeOffset.UtcNow;

                foreach (var sourceNode in member.SourceNodes)
                {
                    var pending = new Queue<Guid>();
                    var visited = new HashSet<Guid>();
                    pending.Enqueue(sourceNode.Id);
                    while (pending.Count > 0)
                    {
                        var nodeId = pending.Dequeue();
                        if (!visited.Add(nodeId) || !nodesById.TryGetValue(nodeId, out var node))
                            continue;
                        if (protectedPartIds.Contains(node.PartId) || protectedComponentIds.Contains(node.ComponentId))
                            continue;

                        if (!desiredAssignmentsByNodeId.TryGetValue(node.Id, out var desiredAssignments))
                        {
                            desiredAssignments = new HashSet<(Guid ComponentId, Guid InstanceId)>();
                            desiredAssignmentsByNodeId[node.Id] = desiredAssignments;
                        }
                        desiredAssignments.Add((assigned.Component.Id, member.Instance.Id));
                        if (!outgoing.TryGetValue(nodeId, out var edges))
                            continue;

                        foreach (var edge in edges)
                        {
                            if (nodesById.TryGetValue(edge.ChildNodeId, out var child) &&
                                string.Equals(
                                    child.Status,
                                    AssemblyLinkStatuses.Active,
                                    StringComparison.OrdinalIgnoreCase) &&
                                !string.Equals(child.Role, AssemblyLinkRoles.Source, StringComparison.OrdinalIgnoreCase))
                            {
                                pending.Enqueue(child.Id);
                            }
                        }
                    }
                }
            }
        }

        foreach (var (nodeId, desiredAssignments) in desiredAssignmentsByNodeId)
        {
            var node = nodesById[nodeId];
            if (desiredAssignments.Count != 1)
            {
                AddOrUpdateConflict(
                    assembly,
                    AssemblyLinkConflictTypes.ComponentMembershipChanged,
                    node.Id,
                    "A linked object is reachable from more than one component occurrence. Gazelle preserved its existing component assignment for review.",
                    new[] { node.ObjectId });
                continue;
            }

            var desired = desiredAssignments.Single();
            node.ComponentId = desired.ComponentId;
            node.SourceComponentInstanceId = desired.InstanceId;
            node.UpdatedAt = DateTimeOffset.UtcNow;
        }

        return changed;
    }

    private static void RebuildComponentRecords(
        AssemblyRecord assembly,
        IReadOnlyDictionary<ComponentCluster, AssignedComponent> assignments)
    {
        var nodesById = assembly.LinkGraph.Nodes.ToDictionary(node => node.Id);
        var outgoing = assembly.LinkGraph.Edges
            .GroupBy(edge => edge.ParentNodeId)
            .ToDictionary(group => group.Key, group => group.ToList());
        var partsById = assembly.Parts.ToDictionary(part => part.Id);

        bool HasCopiedDescendant(Guid startingNodeId)
        {
            var pending = new Queue<Guid>();
            var visited = new HashSet<Guid>();
            pending.Enqueue(startingNodeId);
            while (pending.Count > 0)
            {
                var nodeId = pending.Dequeue();
                if (!visited.Add(nodeId) || !outgoing.TryGetValue(nodeId, out var edges))
                    continue;
                foreach (var edge in edges)
                {
                    if (!nodesById.TryGetValue(edge.ChildNodeId, out var child))
                        continue;
                    if (string.Equals(child.Role, AssemblyLinkRoles.CopiedComponent, StringComparison.OrdinalIgnoreCase))
                        return true;
                    pending.Enqueue(child.Id);
                }
            }

            return false;
        }

        foreach (var (cluster, assigned) in assignments)
        {
            var component = assigned.Component;
            var representative = cluster.Members
                .OrderByDescending(member => member.SourceNodes.Any(source => HasCopiedDescendant(source.Id)))
                .ThenBy(member => member.Instance.Id)
                .First();

            component.Quantity = cluster.Members.Count;
            component.Fingerprint = representative.Fingerprint;
            component.InstanceGroupNames = cluster.Members
                .Select(member => member.Instance.GeneratedGroupName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            component.ObjectIds = cluster.Members
                .SelectMany(member => assembly.LinkGraph.Nodes.Where(node =>
                    node.SourceComponentInstanceId == member.Instance.Id &&
                    node.ComponentId == component.Id &&
                    (string.Equals(node.Role, AssemblyLinkRoles.OriginalAssembly, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(node.Role, AssemblyLinkRoles.Hardware, StringComparison.OrdinalIgnoreCase))))
                .Select(node => node.ObjectId)
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToList();
            component.PartQuantities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            component.RepresentativeObjectIdsByPartName =
                new Dictionary<string, List<Guid>>(StringComparer.OrdinalIgnoreCase);

            foreach (var sourceNode in representative.SourceNodes)
            {
                var partName = partsById.GetValueOrDefault(sourceNode.PartId)?.Name;
                if (string.IsNullOrWhiteSpace(partName))
                {
                    partName = assembly.Hardware.FirstOrDefault(hardware =>
                        hardware.SourceObjectId == sourceNode.ObjectId ||
                        hardware.BlockInstanceId == sourceNode.ObjectId)?.LayerName;
                }
                if (string.IsNullOrWhiteSpace(partName))
                    continue;

                component.PartQuantities[partName] = component.PartQuantities.GetValueOrDefault(partName) + 1;
                if (!component.RepresentativeObjectIdsByPartName.TryGetValue(partName, out var ids))
                {
                    ids = new List<Guid>();
                    component.RepresentativeObjectIdsByPartName[partName] = ids;
                }

                if (!outgoing.TryGetValue(sourceNode.Id, out var edges))
                    continue;
                ids.AddRange(edges
                    .Select(edge => nodesById.GetValueOrDefault(edge.ChildNodeId))
                    .Where(node => node is not null &&
                                   node.ComponentId == component.Id &&
                                   string.Equals(
                                       node.Status,
                                       AssemblyLinkStatuses.Active,
                                       StringComparison.OrdinalIgnoreCase) &&
                                   (string.Equals(node.Role, AssemblyLinkRoles.OriginalAssembly, StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(node.Role, AssemblyLinkRoles.Hardware, StringComparison.OrdinalIgnoreCase)))
                    .Select(node => node!.ObjectId)
                    .Where(id => id != Guid.Empty));
            }

            foreach (var ids in component.RepresentativeObjectIdsByPartName.Values)
            {
                var distinct = ids.Distinct().ToList();
                ids.Clear();
                ids.AddRange(distinct);
            }
            component.PartNames = component.PartQuantities.Keys
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    private static void UpdateLegacyReferences(
        AssemblyRecord assembly,
        IReadOnlySet<Guid> protectedPartIds)
    {
        var nodesByObjectId = assembly.LinkGraph.Nodes
            .Where(node => node.ObjectId != Guid.Empty)
            .GroupBy(node => node.ObjectId)
            .ToDictionary(group => group.Key, group => group.First());
        var partsById = assembly.Parts.ToDictionary(part => part.Id);
        var componentsById = assembly.Components.ToDictionary(component => component.Id);
        foreach (var reference in assembly.GeometryReferences)
        {
            if (!nodesByObjectId.TryGetValue(reference.TargetObjectId, out var node))
                continue;
            if (protectedPartIds.Contains(node.PartId))
                continue;
            if (partsById.TryGetValue(node.PartId, out var part))
                reference.PartName = part.Name;
            if (componentsById.TryGetValue(node.ComponentId, out var component))
                reference.ComponentName = component.Name;
            reference.UpdatedAt = DateTimeOffset.UtcNow;
        }

        foreach (var hardware in assembly.Hardware)
        {
            var node = nodesByObjectId.GetValueOrDefault(hardware.GeneratedObjectId) ??
                       nodesByObjectId.GetValueOrDefault(hardware.SourceObjectId);
            if (node is not null && componentsById.TryGetValue(node.ComponentId, out var component))
                hardware.ComponentName = component.Name;
        }
    }

    private void MoveManagedObjectsToCategorizedLayers(
        RhinoDoc doc,
        AssemblyRecord assembly,
        IReadOnlyDictionary<Guid, string> oldPartNameByObjectId,
        IReadOnlySet<Guid> protectedPartIds,
        IReadOnlyDictionary<Guid, System.Drawing.Color> colorsByPartId)
    {
        var partsById = assembly.Parts.ToDictionary(part => part.Id);
        var componentsById = assembly.Components.ToDictionary(component => component.Id);
        var nodesById = assembly.LinkGraph.Nodes.ToDictionary(node => node.Id);
        foreach (var node in assembly.LinkGraph.Nodes)
        {
            if (protectedPartIds.Contains(node.PartId))
                continue;
            try
            {
                var rhinoObject = doc.Objects.FindId(node.ObjectId);
                if (rhinoObject is null || rhinoObject.IsDeleted)
                    continue;

                partsById.TryGetValue(node.PartId, out var part);
                componentsById.TryGetValue(node.ComponentId, out var component);
                string? targetLayer = null;
                if (string.Equals(node.Role, AssemblyLinkRoles.OriginalAssembly, StringComparison.OrdinalIgnoreCase) &&
                    part is not null)
                {
                    targetLayer = LayerService.OriginalPart(
                        assembly.Name,
                        component?.Name ?? "unsorted",
                        part.Name);
                }
                else if (string.Equals(node.Role, AssemblyLinkRoles.Hardware, StringComparison.OrdinalIgnoreCase))
                {
                    var hardwareName = assembly.Hardware.FirstOrDefault(hardware =>
                        hardware.GeneratedObjectId == node.ObjectId)?.LayerName;
                    if (!string.IsNullOrWhiteSpace(hardwareName))
                    {
                        targetLayer = LayerService.OriginalPart(
                            assembly.Name,
                            component?.Name ?? "unsorted",
                            hardwareName);
                    }
                }
                else if (string.Equals(node.Role, AssemblyLinkRoles.CopiedComponent, StringComparison.OrdinalIgnoreCase))
                {
                    var copiedPartName = part?.Name;
                    if (string.IsNullOrWhiteSpace(copiedPartName))
                    {
                        copiedPartName = assembly.GeometryReferences.FirstOrDefault(reference =>
                            reference.TargetObjectId == node.ObjectId)?.PartName;
                    }

                    if (string.IsNullOrWhiteSpace(copiedPartName))
                    {
                        var copiedIncomingEdge = assembly.LinkGraph.Edges.FirstOrDefault(edge =>
                            edge.ChildNodeId == node.Id);
                        var parentNode = copiedIncomingEdge is null
                            ? null
                            : nodesById.GetValueOrDefault(copiedIncomingEdge.ParentNodeId);
                        copiedPartName = parentNode is null
                            ? string.Empty
                            : assembly.Hardware.FirstOrDefault(hardware =>
                                hardware.GeneratedObjectId == parentNode.ObjectId)?.LayerName;
                    }

                    if (!string.IsNullOrWhiteSpace(copiedPartName))
                    {
                        targetLayer = LayerService.CopiedComponentPart(
                            assembly.Name,
                            component?.Name ?? "unsorted",
                            copiedPartName);
                    }
                }
                else if (string.Equals(node.Role, AssemblyLinkRoles.FlatPart, StringComparison.OrdinalIgnoreCase) &&
                         part is not null)
                {
                    targetLayer = $"{LayerService.PartsPart(assembly.Name, part.Name)}::3D";
                }

                if (!string.IsNullOrWhiteSpace(targetLayer))
                {
                    var currentLayer = rhinoObject.Attributes.LayerIndex >= 0 &&
                                       rhinoObject.Attributes.LayerIndex < doc.Layers.Count
                        ? doc.Layers[rhinoObject.Attributes.LayerIndex]
                        : null;
                    var attributes = rhinoObject.Attributes.Duplicate();
                    if (part is not null &&
                        string.Equals(node.Role, AssemblyLinkRoles.FlatPart, StringComparison.OrdinalIgnoreCase) &&
                        colorsByPartId.TryGetValue(part.Id, out var flatColor))
                    {
                        _layers.EnsurePartLayerIndex(doc, LayerService.PartsPart(assembly.Name, part.Name), flatColor);
                    }
                    attributes.LayerIndex = part is not null && colorsByPartId.TryGetValue(part.Id, out var color)
                        ? _layers.EnsurePartLayerIndex(doc, targetLayer, color)
                        : _layers.EnsureLayerIndex(doc, targetLayer, currentLayer?.Color);
                    if (part is not null &&
                        oldPartNameByObjectId.TryGetValue(node.ObjectId, out var oldPartName) &&
                        (string.IsNullOrWhiteSpace(attributes.Name) ||
                         string.Equals(attributes.Name, oldPartName, StringComparison.OrdinalIgnoreCase)))
                    {
                        attributes.Name = part.Name;
                    }
                    if (!doc.Objects.ModifyAttributes(node.ObjectId, attributes, true))
                    {
                        AddOrUpdateConflict(
                            assembly,
                            AssemblyLinkConflictTypes.PartCategorizationChanged,
                            node.Id,
                            "Rhino would not move this linked object to its recategorized layer. The object may be locked or reference-only.",
                            new[] { node.ObjectId });
                    }
                }

                var incomingEdge = assembly.LinkGraph.Edges.FirstOrDefault(edge => edge.ChildNodeId == node.Id);
                if (incomingEdge is not null && nodesById.TryGetValue(incomingEdge.ParentNodeId, out var parent))
                    _lineage.ApplyObjectMetadata(doc, assembly, node, incomingEdge, parent.ObjectId);
            }
            catch (Exception ex)
            {
                AddOrUpdateConflict(
                    assembly,
                    AssemblyLinkConflictTypes.PartCategorizationChanged,
                    node.Id,
                    $"Gazelle recategorized this linked object but could not finish its layer or recovery metadata update: {ex.Message}",
                    new[] { node.ObjectId });
            }
        }
    }

    private static void AssignNewPartColors(
        IReadOnlyDictionary<PartCluster, AssignedPart> assignments,
        IDictionary<Guid, System.Drawing.Color> colorsByPartId,
        bool colorizeParts)
    {
        foreach (var assignment in assignments.Values.Where(value => value.WasCreated)
                     .OrderBy(value => value.Part.Name, StringComparer.OrdinalIgnoreCase))
        {
            // Include all old and new categories so a split cannot inherit its predecessor
            // color, even after exhausting the predefined palette.
            var selected = LayerService.ChoosePartColor(assignment.Part.Name, colorizeParts, colorsByPartId.Values);
            colorsByPartId[assignment.Part.Id] = selected;
            assignment.Part.LayerColorArgb = selected.ToArgb();
        }
    }

    private void RemoveObsoleteComponentPartLayers(
        RhinoDoc doc,
        AssemblyRecord assembly,
        IEnumerable<string> previousComponentNames,
        HashSet<string> knownPartNames)
    {
        var partsById = assembly.Parts.ToDictionary(part => part.Id);
        var componentsById = assembly.Components.ToDictionary(component => component.Id);
        knownPartNames.UnionWith(assembly.Parts.Select(part => part.Name));
        var requiredPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void RequirePartLayers(string componentName, string partName)
        {
            requiredPaths.Add(LayerService.OriginalPart(assembly.Name, componentName, partName));
            requiredPaths.Add(LayerService.CopiedComponentPart(assembly.Name, componentName, partName));
        }

        // Membership matters even if an occurrence is missing, protected, quarantined, or
        // currently on another layer. An empty layer alone is not evidence of an obsolete part.
        foreach (var component in assembly.Components)
        {
            foreach (var partName in component.PartNames.Concat(component.PartQuantities
                         .Where(pair => pair.Value > 0).Select(pair => pair.Key)))
                RequirePartLayers(component.Name, partName);
        }
        foreach (var node in assembly.LinkGraph.Nodes)
        {
            if (partsById.TryGetValue(node.PartId, out var part))
                RequirePartLayers(componentsById.GetValueOrDefault(node.ComponentId)?.Name ?? "unsorted", part.Name);
        }

        var removed = 0;
        var componentNames = previousComponentNames.Concat(assembly.Components.Select(component => component.Name))
            .Append("unsorted").Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var componentName in componentNames)
        {
            foreach (var parent in new[]
                     {
                         LayerService.OriginalComponent(assembly.Name, componentName),
                         $"{LayerService.CopiedComponentsAssembly(assembly.Name)}::{componentName}"
                     })
            {
                // Only immediate, known part leaves under this assembly's known components.
                // This also repairs leftovers from earlier refreshes, without sweeping user
                // layers, component parents, flat output trees or annotations.
                foreach (var path in _layers.GetChildLayerPaths(doc, parent))
                {
                    if (requiredPaths.Contains(path) || !knownPartNames.Contains(LayerService.ChildName(path)))
                        continue;
                    _layers.TryDeleteLayerIfEmpty(doc, path);
                    if (_layers.FindLayerIndex(doc, path) < 0)
                        removed++;
                }
            }
        }
        if (removed > 0)
            RhinoApp.WriteLine("Gazelle removed {0} obsolete empty component part layer(s) in '{1}'.", removed, assembly.Name);
    }

    private static void ResolveSuccessfulPartConflicts(
        AssemblyRecord assembly,
        IEnumerable<SourcePartEvidence> evidence)
    {
        var resolvedNodeIds = evidence.Select(item => item.Node.Id).ToHashSet();
        var now = DateTimeOffset.UtcNow;
        foreach (var conflict in assembly.LinkGraph.Conflicts.Where(conflict =>
                     resolvedNodeIds.Contains(conflict.NodeId) &&
                     string.Equals(conflict.Status, AssemblyLinkStatuses.Open, StringComparison.OrdinalIgnoreCase) &&
                     string.Equals(
                         conflict.ConflictType,
                         AssemblyLinkConflictTypes.PartCategorizationChanged,
                         StringComparison.OrdinalIgnoreCase)))
        {
            conflict.Status = AssemblyLinkStatuses.Resolved;
            conflict.ResolvedAt = now;
        }
    }

    private static void ResolveSuccessfulComponentConflicts(
        AssemblyRecord assembly,
        IEnumerable<Guid> sourceNodeIds)
    {
        var resolvedNodeIds = sourceNodeIds.ToHashSet();
        var now = DateTimeOffset.UtcNow;
        foreach (var conflict in assembly.LinkGraph.Conflicts.Where(conflict =>
                     resolvedNodeIds.Contains(conflict.NodeId) &&
                     string.Equals(conflict.Status, AssemblyLinkStatuses.Open, StringComparison.OrdinalIgnoreCase) &&
                     string.Equals(
                         conflict.ConflictType,
                         AssemblyLinkConflictTypes.ComponentCategorizationChanged,
                         StringComparison.OrdinalIgnoreCase)))
        {
            conflict.Status = AssemblyLinkStatuses.Resolved;
            conflict.ResolvedAt = now;
        }
    }

    private static void AddOrUpdateConflict(
        AssemblyRecord assembly,
        string conflictType,
        Guid nodeId,
        string message,
        IEnumerable<Guid> objectIds)
    {
        var existing = assembly.LinkGraph.Conflicts.FirstOrDefault(conflict =>
            conflict.NodeId == nodeId &&
            string.Equals(conflict.ConflictType, conflictType, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(conflict.Status, AssemblyLinkStatuses.Open, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            existing.Message = message;
            existing.CandidateObjectIds = objectIds.Where(id => id != Guid.Empty).Distinct().ToList();
            existing.DetectedAt = DateTimeOffset.UtcNow;
            return;
        }

        assembly.LinkGraph.Conflicts.Add(new LinkConflictRecord
        {
            ConflictType = conflictType,
            NodeId = nodeId,
            Message = message,
            CandidateObjectIds = objectIds.Where(id => id != Guid.Empty).Distinct().ToList()
        });
    }

    private static string AllocatePartName(AssemblyRecord assembly)
    {
        var existing = assembly.Parts.Select(part => part.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            var name = $"{assembly.PartPrefix}{assembly.NextPartSequence++:00}";
            if (!existing.Contains(name))
                return name;
        }
    }

    private static bool PreservesStoredPartIdentity(SourcePartEvidence evidence, PartRecord part)
    {
        var candidateMaterialId = MaterialAssignment.NormalizeMaterialIdForCategory(evidence.Candidate.MaterialId);
        var storedMaterialId = MaterialAssignment.NormalizeMaterialIdForCategory(
            part.CategorizationMaterialId ?? part.MaterialId);
        if (!string.Equals(candidateMaterialId, storedMaterialId, StringComparison.Ordinal))
            return false;

        return (!string.IsNullOrWhiteSpace(evidence.OriginalNodeFingerprint) &&
                string.Equals(
                    evidence.Candidate.Fingerprint,
                    evidence.OriginalNodeFingerprint,
                    StringComparison.Ordinal)) ||
               (!string.IsNullOrWhiteSpace(part.GeometryFingerprint) &&
                string.Equals(
                    evidence.Candidate.Fingerprint,
                    part.GeometryFingerprint,
                    StringComparison.Ordinal));
    }

    private static string AllocateComponentName(AssemblyRecord assembly)
    {
        var existing = assembly.Components.Select(component => component.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            var name = $"{assembly.ComponentPrefix}{assembly.NextComponentSequence++:00}";
            if (!existing.Contains(name))
                return name;
        }
    }

    private sealed record SourcePartEvidence(
        AssemblyLinkNodeRecord Node,
        PartRecord OldPart,
        PartCandidate Candidate,
        string OriginalNodeFingerprint,
        string AssignedMaterialId);

    private sealed class PartCluster
    {
        public PartCluster(SourcePartEvidence first)
        {
            Members.Add(first);
        }

        public List<SourcePartEvidence> Members { get; } = new();
        public string SortKey => string.Join(
            ":",
            Members.Select(member => member.Node.Id.ToString("N")).OrderBy(value => value, StringComparer.Ordinal));
    }

    private sealed record AssignedPart(PartRecord Part, bool WasCreated);

    private sealed record ComponentInstanceEvidence(
        SourceComponentInstanceRecord Instance,
        ComponentRecord? OldComponent,
        IReadOnlyList<AssemblyLinkNodeRecord> SourceNodes,
        IReadOnlyList<PartCandidate> Candidates,
        string Fingerprint);

    private sealed class ComponentCluster
    {
        public ComponentCluster(ComponentInstanceEvidence first)
        {
            Members.Add(first);
        }

        public List<ComponentInstanceEvidence> Members { get; } = new();
        public string SortKey => string.Join(
            ":",
            Members.Select(member => member.Instance.Id.ToString("N")).OrderBy(value => value, StringComparer.Ordinal));
    }

    private sealed record AssignedComponent(ComponentRecord Component, bool WasCreated);
    private readonly record struct ComponentReconciliationResult(
        int CreatedComponents,
        int ChangedInstances,
        bool Reconciled)
    {
        public static ComponentReconciliationResult Empty => new(0, 0, false);
    }
}

public readonly record struct CategorizationReconciliationResult(
    int CreatedParts,
    int RetiredParts,
    int ReassignedSources,
    int CreatedComponents,
    int RetiredComponents,
    int ReassignedComponentInstances,
    bool RequiresFlatPartRebuild,
    bool RequiresCopiedComponentRebuild,
    bool Changed)
{
    public bool PartsReconciled { get; init; }
    public IReadOnlySet<Guid> ProtectedPartIds { get; init; } = new HashSet<Guid>();
    public static CategorizationReconciliationResult Empty => new(0, 0, 0, 0, 0, 0, false, false, false);
    public static CategorizationReconciliationResult InvalidatedOnly => new(0, 0, 0, 0, 0, 0, false, false, true);
}
