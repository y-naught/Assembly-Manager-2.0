using System.Diagnostics;
using System.Globalization;
using AssemblyManagerPlugin.Core;
using AssemblyManagerPlugin.Geometry;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace AssemblyManagerPlugin.Services;

public sealed class ReferenceUpdateService
{
    private readonly AssemblyRepository _repository;
    private readonly IActionHistorySink _history;
    private readonly AssemblyLineageService _lineage;
    private readonly GeometryFingerprintService _fingerprints;
    private readonly AssemblyCategorizationReconciliationService _categorization;
    private readonly FlatPartSynchronizationService? _flatParts;
    private readonly Action<string> _updateFeedback;
    private readonly ComponentUpdateService? _componentUpdates;
    private readonly LinkedAssemblySafetyService? _linkSafety;

    public ReferenceUpdateService(
        AssemblyRepository repository,
        IActionHistorySink history,
        AssemblyLineageService lineage,
        GeometryFingerprintService fingerprints,
        AssemblyCategorizationReconciliationService categorization,
        FlatPartSynchronizationService? flatParts = null,
        Action<string>? updateFeedback = null,
        ComponentUpdateService? componentUpdates = null,
        LinkedAssemblySafetyService? linkSafety = null)
    {
        _repository = repository;
        _history = history;
        _lineage = lineage;
        _fingerprints = fingerprints;
        _categorization = categorization;
        _flatParts = flatParts;
        _updateFeedback = updateFeedback ?? (message => RhinoApp.WriteLine(message));
        _componentUpdates = componentUpdates;
        _linkSafety = linkSafety;
    }

    public int RefreshAssemblyReferences(RhinoDoc doc, string assemblyName)
    {
        _linkSafety?.EnsureEnabled();
        var store = _repository.Load(doc);
        var assembly = store.FindAssembly(assemblyName)
            ?? throw new InvalidOperationException($"Assembly '{assemblyName}' was not found.");
        ComponentUpdateService.EnsureInputGroupsReady(assembly);
        var suspensionMayResolve = _linkSafety?.IsAssemblyBlocked(doc, assembly.Id) == true;
        _linkSafety?.EnsureCanUpdate(doc, assembly.Id);
        if (suspensionMayResolve)
        {
            // A restored baseline resolves its safety issue before this refresh. Do not
            // save the pre-validation snapshot and inadvertently reopen that issue.
            store = _repository.Load(doc);
            assembly = store.FindAssembly(assemblyName)!;
        }
        using var feedback = new UpdateFeedbackScope(_updateFeedback,
            $"Gazelle updating assembly '{assemblyName}'...");
        if (assembly.PendingComponentUpdates.Count > 0)
        {
            if (_componentUpdates is null)
                throw new InvalidOperationException("Component update support is unavailable. Load the current Gazelle plugin before updating this assembly.");
            _updateFeedback($"Gazelle applying staged component membership changes in '{assemblyName}'...");
            using var mutation = AssemblyLinkMutationGate.Enter();
            _componentUpdates.ApplyPending(doc, store, assembly);
            // Save the complete structural registration before normal refresh. This makes a
            // retry idempotent if categorization or a downstream output subsequently fails.
            _repository.Save(doc, store);
        }
        var openConflictsBefore = assembly.LinkGraph.Conflicts.Count(conflict =>
            string.Equals(conflict.Status, AssemblyLinkStatuses.Open, StringComparison.OrdinalIgnoreCase));
        var previousIssueIds = OpenIssueIds(store);

        var selectedSourceIds = assembly.LinkGraph.Nodes
            .Where(node => string.Equals(node.Role, AssemblyLinkRoles.Source, StringComparison.OrdinalIgnoreCase))
            .Select(node => node.ObjectId).ToHashSet();
        // One physical input can feed several assemblies. Updating it through a selected
        // assembly must not leave sibling consumers stale after accepting an original edit.
        var initialSeeds = store.Assemblies
            .Where(linkedAssembly => _linkSafety?.IsAssemblyBlocked(doc, linkedAssembly.Id) != true)
            .SelectMany(linkedAssembly => linkedAssembly.LinkGraph.Nodes
            .Where(node => selectedSourceIds.Contains(node.ObjectId) &&
                           string.Equals(node.Role, AssemblyLinkRoles.Source, StringComparison.OrdinalIgnoreCase))
            .Select(node => new PropagationSeed(linkedAssembly.Id, node.Id, RebuildQuarantined: true)))
            .ToList();
        // The event service prepares deferred input/original edits before the UI calls here.
        // Unsupported or competing ORIGINAL ASSEMBLIES edits remain protected for review.
        var refreshResult = RefreshStoreFromSeeds(doc, store, initialSeeds);
        var refreshed = refreshResult.RefreshedObjects;
        // Reconciliation can restore a post-propagation snapshot after an unexpected failure,
        // which replaces the AssemblyRecord instance held by the store.
        assembly = store.FindAssembly(assemblyName) ?? assembly;

        assembly.UpdatedAt = DateTimeOffset.UtcNow;
        _repository.Save(doc, store);
        _history.Record(doc, new ActionHistoryEntry
        {
            CommandName = "RefreshAssemblyReferences",
            AssemblyName = assemblyName,
            Summary = $"Refreshed {refreshed} linked object(s) from the assembly source graph and created {refreshResult.CreatedPartCategories} new part categorization(s)."
        });
        ReportCategorizationOutcomes(refreshResult.CategorizationOutcomes);
        var newConflictCount = assembly.LinkGraph.Conflicts.Count(conflict =>
            string.Equals(conflict.Status, AssemblyLinkStatuses.Open, StringComparison.OrdinalIgnoreCase)) - openConflictsBefore;
        if (newConflictCount > 0)
        {
            RhinoApp.WriteLine(
                "Gazelle preserved existing output and recorded {0} link issue(s) for '{1}'.",
                newConflictCount,
                assemblyName);
        }
        ReportNewIssueDetails(store, previousIssueIds);
        doc.Views.Redraw();
        feedback.Complete(refreshResult, CountOpenConflicts(store));
        return refreshed;
    }

    /// <summary>
    /// Propagates a one-to-one source geometry replacement through all descendants in every
    /// assembly graph that references that Rhino object. Intended for the idle event processor.
    /// Structural operations are deliberately excluded and become conflicts instead.
    /// </summary>
    public int RefreshDescendantsFromSource(RhinoDoc doc, Guid sourceObjectId)
    {
        return RefreshDescendantsFromSources(doc, new[] { sourceObjectId });
    }

    /// <summary>
    /// Propagates a batch of source edits through one store snapshot, then reconciles each
    /// affected assembly exactly once after the complete cascade has drained.
    /// </summary>
    public int RefreshDescendantsFromSources(RhinoDoc doc, IEnumerable<Guid> sourceObjectIds)
    {
        if (_linkSafety?.IsEnabled == false)
            return 0;
        var sourceIds = sourceObjectIds.Where(id => id != Guid.Empty).Distinct().ToHashSet();
        if (sourceIds.Count == 0)
            return 0;

        var store = _repository.Load(doc);
        var openConflictsBefore = CountOpenConflicts(store);
        var previousIssueIds = OpenIssueIds(store);
        var initialSeeds = store.Assemblies
            .Where(assembly => assembly.PendingComponentUpdates.Count == 0 && !ComponentUpdateService.HasInputRegroupIssue(assembly) &&
                _linkSafety?.IsAssemblyBlocked(doc, assembly.Id) != true)
            .SelectMany(assembly => assembly.LinkGraph.Nodes
                .Where(node => sourceIds.Contains(node.ObjectId) &&
                               string.Equals(node.Role, AssemblyLinkRoles.Source, StringComparison.OrdinalIgnoreCase))
                .Select(node => new PropagationSeed(assembly.Id, node.Id, RebuildQuarantined: false)))
            .ToList();
        // Ordinary document events can include objects outside any assembly. Keep those
        // no-op requests silent, and report once per batch rather than once per object.
        if (initialSeeds.Count == 0)
            return 0;
        using var feedback = new UpdateFeedbackScope(_updateFeedback,
            $"Gazelle updating linked assemblies from {sourceIds.Count} changed source object(s)...");
        var refreshResult = RefreshStoreFromSeeds(doc, store, initialSeeds);
        var refreshed = refreshResult.RefreshedObjects;

        _repository.Save(doc, store);
        doc.Views.Redraw();

        ReportCategorizationOutcomes(refreshResult.CategorizationOutcomes);

        var newConflictCount = CountOpenConflicts(store) - openConflictsBefore;
        if (newConflictCount > 0)
        {
            RhinoApp.WriteLine(
                $"Gazelle refreshed {refreshed} linked object(s) from {sourceIds.Count} source object(s); {newConflictCount} additional categorization or link review item(s) are open.");
        }
        ReportNewIssueDetails(store, previousIssueIds);
        feedback.Complete(refreshResult, CountOpenConflicts(store));

        return refreshed;
    }

    /// <summary>
    /// Accepts a supported one-to-one edit made to generated ORIGINAL ASSEMBLIES geometry,
    /// maps that geometry back through the stored source-to-original transform, and then uses
    /// the normal source refresh path so every downstream occurrence remains coherent.
    /// </summary>
    public int PromoteOriginalAssemblyEdits(
        RhinoDoc doc,
        IEnumerable<(Guid AssemblyId, Guid NodeId)> requestedNodes,
        bool refreshDescendants = true)
    {
        if (_linkSafety?.IsEnabled == false)
            return 0;
        var requests = requestedNodes
            .Where(request => request.AssemblyId != Guid.Empty && request.NodeId != Guid.Empty)
            .Distinct()
            .ToList();
        if (requests.Count == 0)
            return 0;

        var store = _repository.Load(doc);
        var assembliesById = store.Assemblies.ToDictionary(assembly => assembly.Id);
        var prepared = new List<OriginalAssemblyPromotion>();
        var storeChanged = false;

        foreach (var request in requests)
        {
            if (!assembliesById.TryGetValue(request.AssemblyId, out var assembly)
                || _linkSafety?.IsAssemblyBlocked(doc, assembly.Id) == true)
                continue;

            var originalNode = assembly.LinkGraph.Nodes.FirstOrDefault(node => node.Id == request.NodeId);
            if (originalNode is null ||
                !string.Equals(
                    originalNode.Role,
                    AssemblyLinkRoles.OriginalAssembly,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (TryPrepareOriginalAssemblyPromotion(
                    doc,
                    store,
                    assembly,
                    originalNode,
                    out var promotion,
                    out var failureMessage))
            {
                prepared.Add(promotion);
                continue;
            }

            storeChanged |= MarkOriginalPromotionUnresolved(
                assembly,
                originalNode,
                incomingEdge: assembly.LinkGraph.Edges.FirstOrDefault(edge => edge.ChildNodeId == originalNode.Id),
                failureMessage);
        }

        var accepted = new List<OriginalAssemblyPromotion>();
        foreach (var sourceGroup in prepared.GroupBy(candidate => candidate.SourceNode.ObjectId))
        {
            var distinctOriginals = sourceGroup
                .Select(candidate => (candidate.Assembly.Id, candidate.OriginalNode.Id))
                .Distinct()
                .Count();
            if (distinctOriginals == 1)
            {
                accepted.Add(sourceGroup.First());
                continue;
            }

            foreach (var candidate in sourceGroup)
            {
                storeChanged |= MarkOriginalPromotionUnresolved(
                    candidate.Assembly,
                    candidate.OriginalNode,
                    candidate.IncomingEdge,
                    "More than one edited ORIGINAL ASSEMBLIES occurrence maps to the same source in this update batch. Gazelle preserved every edit for review instead of choosing one as authoritative.");
            }
        }

        var promotedSourceIds = new HashSet<Guid>();
        using (AssemblyLinkMutationGate.Enter())
        {
            foreach (var candidate in accepted)
            {
                if (!ReplaceGeometry(doc, candidate.SourceNode.ObjectId, candidate.PromotedSourceGeometry))
                {
                    storeChanged |= MarkOriginalPromotionUnresolved(
                        candidate.Assembly,
                        candidate.OriginalNode,
                        candidate.IncomingEdge,
                        "Rhino would not replace the linked design source. It may be locked, reference-only, or on a locked layer; the edited assembly occurrence was preserved for review.");
                    continue;
                }

                if (!CopyMaterialAttributes(doc, candidate.OriginalNode.ObjectId, candidate.SourceNode.ObjectId,
                        preserveSourceStockFromNormalizedOriginal: true))
                {
                    storeChanged |= MarkOriginalPromotionUnresolved(
                        candidate.Assembly, candidate.OriginalNode, candidate.IncomingEdge,
                        "The geometry was promoted, but Rhino could not copy the edited assembly material to its design source. Review the locked or reference-only object before updating again.");
                    continue;
                }

                var now = DateTimeOffset.UtcNow;
                foreach (var sourceAssembly in store.Assemblies)
                {
                    foreach (var matchingSourceNode in sourceAssembly.LinkGraph.Nodes.Where(node =>
                                 node.ObjectId == candidate.SourceNode.ObjectId &&
                                 string.Equals(node.Role, AssemblyLinkRoles.Source, StringComparison.OrdinalIgnoreCase)))
                    {
                        matchingSourceNode.Status = AssemblyLinkStatuses.Active;
                        matchingSourceNode.UpdatedAt = now;
                        ResolveSourceDeletedConflicts(sourceAssembly, matchingSourceNode.Id, now);
                        sourceAssembly.LinkGraph.UpdatedAt = now;
                        sourceAssembly.UpdatedAt = now;
                    }
                }

                candidate.OriginalNode.Status = AssemblyLinkStatuses.Active;
                candidate.OriginalNode.UpdatedAt = now;
                candidate.OriginalNode.GeometryFingerprint = candidate.OriginalFingerprint;
                candidate.OriginalNode.Metadata.Remove(AssemblyLinkMetadataKeys.Quarantined);
                candidate.IncomingEdge.Status = AssemblyLinkStatuses.Active;
                candidate.IncomingEdge.UpdatedAt = now;
                ResolveOriginalPromotionConflicts(
                    candidate.Assembly,
                    candidate.OriginalNode.Id,
                    candidate.IncomingEdge.Id,
                    now);
                candidate.Assembly.LinkGraph.UpdatedAt = now;
                candidate.Assembly.UpdatedAt = now;
                promotedSourceIds.Add(candidate.SourceNode.ObjectId);
                storeChanged = true;
            }

            if (storeChanged)
                _repository.Save(doc, store);
        }

        var refreshed = 0;
        if (promotedSourceIds.Count > 0 && refreshDescendants)
        {
            try
            {
                refreshed = RefreshDescendantsFromSources(doc, promotedSourceIds);
            }
            catch (Exception ex)
            {
                foreach (var sourceObjectId in promotedSourceIds)
                    RecordPromotionRefreshFailure(doc, sourceObjectId, ex.Message);
            }
        }

        if (promotedSourceIds.Count > 0)
        {
            if (refreshDescendants)
                RhinoApp.WriteLine(
                    "Gazelle accepted {0} ORIGINAL ASSEMBLIES edit(s) and refreshed {1} linked downstream object(s).",
                    promotedSourceIds.Count, refreshed);
            else
                RhinoApp.WriteLine("Gazelle accepted {0} ORIGINAL ASSEMBLIES edit(s) for this assembly update.", promotedSourceIds.Count);
        }

        return refreshed;
    }

    private void RecordPromotionRefreshFailure(
        RhinoDoc doc,
        Guid sourceObjectId,
        string errorMessage)
    {
        var store = _repository.Load(doc);
        var changed = false;
        foreach (var assembly in store.Assemblies)
        {
            var assemblyChanged = false;
            var nodesById = assembly.LinkGraph.Nodes.ToDictionary(node => node.Id);
            foreach (var sourceNode in assembly.LinkGraph.Nodes.Where(node =>
                         node.ObjectId == sourceObjectId &&
                         string.Equals(node.Role, AssemblyLinkRoles.Source, StringComparison.OrdinalIgnoreCase)))
            {
                foreach (var edge in assembly.LinkGraph.Edges.Where(edge => edge.ParentNodeId == sourceNode.Id))
                {
                    if (!nodesById.TryGetValue(edge.ChildNodeId, out var childNode))
                        continue;

                    childNode.Status = AssemblyLinkStatuses.Conflict;
                    childNode.UpdatedAt = DateTimeOffset.UtcNow;
                    edge.Status = AssemblyLinkStatuses.Conflict;
                    edge.UpdatedAt = childNode.UpdatedAt;
                    AddConflict(
                        assembly,
                        AssemblyLinkConflictTypes.DerivedGeometryOutOfDate,
                        $"The design source accepted an ORIGINAL ASSEMBLIES edit, but automatic descendant refresh stopped unexpectedly: {errorMessage}",
                        childNode.Id,
                        edge.Id);
                    assemblyChanged = true;
                }
            }

            if (assemblyChanged)
            {
                assembly.LinkGraph.UpdatedAt = DateTimeOffset.UtcNow;
                assembly.UpdatedAt = assembly.LinkGraph.UpdatedAt;
                changed = true;
            }
        }

        if (changed)
        {
            using (AssemblyLinkMutationGate.Enter())
                _repository.Save(doc, store);
        }

        RhinoApp.WriteLine(
            "Gazelle accepted the source update for {0}, but descendant refresh needs review: {1}",
            sourceObjectId,
            errorMessage);
    }

    private bool TryPrepareOriginalAssemblyPromotion(
        RhinoDoc doc,
        AssemblyStore store,
        AssemblyRecord assembly,
        AssemblyLinkNodeRecord originalNode,
        out OriginalAssemblyPromotion promotion,
        out string failureMessage)
    {
        promotion = default!;
        failureMessage = string.Empty;

        if (assembly.LinkGraph.Conflicts.Any(conflict =>
                conflict.NodeId == originalNode.Id &&
                string.Equals(conflict.Status, AssemblyLinkStatuses.Open, StringComparison.OrdinalIgnoreCase) &&
                (string.Equals(
                     conflict.ConflictType,
                     AssemblyLinkConflictTypes.DuplicateIdentity,
                     StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(
                     conflict.ConflictType,
                     AssemblyLinkConflictTypes.SourceSplit,
                     StringComparison.OrdinalIgnoreCase))))
        {
            failureMessage = "The edited object has duplicate or structural lineage evidence, so Gazelle cannot prove that this was a one-to-one replacement.";
            return false;
        }

        var taggedOriginals = (doc.Objects.FindByUserString(
                                   AssemblyManagerConstants.LinkNodeIdUserString,
                                   originalNode.Id.ToString("D"),
                                   false) ?? Array.Empty<RhinoObject>())
            .Where(candidate => !candidate.IsDeleted)
            .Select(candidate => candidate.Id)
            .Where(objectId => objectId != Guid.Empty)
            .Distinct()
            .ToArray();
        if (taggedOriginals.Length != 1 || taggedOriginals[0] != originalNode.ObjectId)
        {
            failureMessage = "The edited assembly occurrence does not have one unique live recovery identity, so Gazelle cannot prove which Rhino object is authoritative.";
            return false;
        }

        var incomingEdges = assembly.LinkGraph.Edges
            .Where(edge => edge.ChildNodeId == originalNode.Id)
            .ToList();
        if (incomingEdges.Count != 1)
        {
            failureMessage = "The edited assembly object does not have exactly one incoming source relationship.";
            return false;
        }

        var incomingEdge = incomingEdges[0];
        var sourceNode = assembly.LinkGraph.Nodes.FirstOrDefault(node => node.Id == incomingEdge.ParentNodeId);
        if (sourceNode is null ||
            !string.Equals(sourceNode.Role, AssemblyLinkRoles.Source, StringComparison.OrdinalIgnoreCase))
        {
            failureMessage = "The edited assembly object is not linked directly to a design source.";
            return false;
        }

        var sharedSourceHasBlockingConflict = store.Assemblies.Any(sourceAssembly =>
            sourceAssembly.LinkGraph.Nodes.Any(candidateSourceNode =>
                candidateSourceNode.ObjectId == sourceNode.ObjectId &&
                (_linkSafety?.IsAssemblyBlocked(doc, sourceAssembly.Id) == true ||
                 (string.Equals(candidateSourceNode.Role, AssemblyLinkRoles.Source, StringComparison.OrdinalIgnoreCase) &&
                  sourceAssembly.LinkGraph.Conflicts.Any(conflict =>
                    conflict.NodeId == candidateSourceNode.Id &&
                    string.Equals(conflict.Status, AssemblyLinkStatuses.Open, StringComparison.OrdinalIgnoreCase) &&
                    (string.Equals(conflict.ConflictType, AssemblyLinkConflictTypes.DuplicateIdentity, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(conflict.ConflictType, AssemblyLinkConflictTypes.SourceSplit, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(conflict.ConflictType, AssemblyLinkConflictTypes.TransformUnresolved, StringComparison.OrdinalIgnoreCase)))))));
        if (sharedSourceHasBlockingConflict)
        {
            failureMessage = "The design source has an unresolved identity, structural, or transform conflict in at least one assembly graph, so it cannot receive an automatic promotion.";
            return false;
        }

        if (!string.Equals(
                incomingEdge.Recipe,
                AssemblyLinkRecipes.DirectCopy,
                StringComparison.OrdinalIgnoreCase))
        {
            failureMessage = "Block-definition and procedural source relationships cannot be promoted automatically yet.";
            return false;
        }

        if (!incomingEdge.ParentToChildTransform.TryToTransform(out var sourceToOriginal) ||
            !sourceToOriginal.TryGetInverse(out var originalToSource))
        {
            failureMessage = "The stored source-to-assembly transform is invalid or non-invertible.";
            return false;
        }

        var originalObject = doc.Objects.FindId(originalNode.ObjectId);
        if (originalObject is null)
        {
            failureMessage = "The edited assembly object is missing or is not a supported closed BREP.";
            return false;
        }

        if (!_fingerprints.TryCreatePartCandidate(originalObject, out var originalCandidate, out var originalWarning) ||
            originalCandidate.Geometry is not Brep editedOriginal)
        {
            failureMessage = string.IsNullOrWhiteSpace(originalWarning)
                ? "The edited assembly object is not a supported closed BREP."
                : originalWarning;
            return false;
        }

        var sourceObject = doc.Objects.FindId(sourceNode.ObjectId);
        if (sourceObject is null || sourceObject is InstanceObject)
        {
            failureMessage = "The linked design source is missing or cannot accept a BREP promotion.";
            return false;
        }

        if (!_fingerprints.TryCreatePartCandidate(sourceObject, out _, out var sourceWarning))
        {
            failureMessage = string.IsNullOrWhiteSpace(sourceWarning)
                ? "The linked design source cannot accept a BREP promotion."
                : sourceWarning;
            return false;
        }

        var sourceIsDerivedElsewhere = store.Assemblies.Any(candidateAssembly =>
            candidateAssembly.LinkGraph.Nodes.Any(node =>
                node.ObjectId == sourceNode.ObjectId &&
                !string.Equals(node.Role, AssemblyLinkRoles.Source, StringComparison.OrdinalIgnoreCase)));
        if (sourceIsDerivedElsewhere)
        {
            failureMessage = "The linked source is itself generated by another assembly. Recursive upstream promotion is ambiguous, so Gazelle preserved the edit for review.";
            return false;
        }

        var promotedSource = editedOriginal.DuplicateBrep();
        if (!promotedSource.Transform(originalToSource) ||
            !promotedSource.IsValid ||
            !promotedSource.IsSolid)
        {
            failureMessage = "The edited assembly BREP could not be mapped safely back into source coordinates.";
            return false;
        }

        var roundTrip = promotedSource.DuplicateBrep();
        if (!roundTrip.Transform(sourceToOriginal) ||
            !GeometryRoundTrips(roundTrip, editedOriginal, doc.ModelAbsoluteTolerance))
        {
            failureMessage = "The edited geometry did not round-trip through its stored relationship transform within model tolerance.";
            return false;
        }

        promotion = new OriginalAssemblyPromotion(
            assembly,
            sourceNode,
            originalNode,
            incomingEdge,
            promotedSource,
            originalCandidate.Fingerprint);
        return true;
    }

    private static bool GeometryRoundTrips(Brep expected, Brep actual, double modelTolerance)
    {
        if (expected.Faces.Count != actual.Faces.Count ||
            expected.Edges.Count != actual.Edges.Count ||
            expected.Vertices.Count != actual.Vertices.Count)
        {
            return false;
        }

        var bounds = actual.GetBoundingBox(true);
        var coordinateMagnitude = bounds.IsValid
            ? new[]
            {
                Math.Abs(bounds.Min.X), Math.Abs(bounds.Min.Y), Math.Abs(bounds.Min.Z),
                Math.Abs(bounds.Max.X), Math.Abs(bounds.Max.Y), Math.Abs(bounds.Max.Z)
            }.Max()
            : 1.0;
        var tolerance = Math.Max(
            Math.Max(modelTolerance, RhinoMath.ZeroTolerance) * 1e-3,
            Math.Max(1.0, coordinateMagnitude) * 1e-10);
        for (var index = 0; index < expected.Vertices.Count; index++)
        {
            if (expected.Vertices[index].Location.DistanceTo(actual.Vertices[index].Location) > tolerance)
                return false;
        }

        var expectedVolume = VolumeMassProperties.Compute(expected)?.Volume ?? double.NaN;
        var actualVolume = VolumeMassProperties.Compute(actual)?.Volume ?? double.NaN;
        var expectedArea = AreaMassProperties.Compute(expected)?.Area ?? double.NaN;
        var actualArea = AreaMassProperties.Compute(actual)?.Area ?? double.NaN;
        return MassValuesMatch(expectedVolume, actualVolume, tolerance) &&
               MassValuesMatch(expectedArea, actualArea, tolerance);
    }

    private static bool MassValuesMatch(double expected, double actual, double absoluteTolerance)
    {
        if (double.IsNaN(expected) || double.IsNaN(actual))
            return double.IsNaN(expected) && double.IsNaN(actual);

        return Math.Abs(expected - actual) <=
               Math.Max(absoluteTolerance, Math.Abs(expected) * 1e-9);
    }

    private static bool MarkOriginalPromotionUnresolved(
        AssemblyRecord assembly,
        AssemblyLinkNodeRecord originalNode,
        AssemblyLinkEdgeRecord? incomingEdge,
        string message)
    {
        if (!originalNode.Metadata.TryGetValue(AssemblyLinkMetadataKeys.Quarantined, out var quarantined) ||
            !string.Equals(quarantined, bool.TrueString, StringComparison.OrdinalIgnoreCase))
        {
            originalNode.Metadata[AssemblyLinkMetadataKeys.Quarantined] = bool.TrueString;
        }

        if (!string.Equals(originalNode.Status, AssemblyLinkStatuses.Conflict, StringComparison.OrdinalIgnoreCase))
        {
            originalNode.Status = AssemblyLinkStatuses.Conflict;
        }

        if (incomingEdge is not null &&
            !string.Equals(incomingEdge.Status, AssemblyLinkStatuses.Conflict, StringComparison.OrdinalIgnoreCase))
        {
            incomingEdge.Status = AssemblyLinkStatuses.Conflict;
            incomingEdge.UpdatedAt = DateTimeOffset.UtcNow;
        }

        AddConflict(
            assembly,
            AssemblyLinkConflictTypes.OriginalAssemblyPromotionUnresolved,
            message,
            originalNode.Id,
            incomingEdge?.Id ?? Guid.Empty);
        var now = DateTimeOffset.UtcNow;
        originalNode.UpdatedAt = now;
        assembly.LinkGraph.UpdatedAt = now;
        assembly.UpdatedAt = now;
        return true;
    }

    private static void ResolveOriginalPromotionConflicts(
        AssemblyRecord assembly,
        Guid nodeId,
        Guid edgeId,
        DateTimeOffset resolvedAt)
    {
        foreach (var conflict in assembly.LinkGraph.Conflicts.Where(conflict =>
                     string.Equals(conflict.Status, AssemblyLinkStatuses.Open, StringComparison.OrdinalIgnoreCase) &&
                     (conflict.NodeId == nodeId || conflict.EdgeId == edgeId) &&
                     (string.Equals(
                          conflict.ConflictType,
                          AssemblyLinkConflictTypes.OriginalAssemblyPromotionUnresolved,
                          StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(
                           conflict.ConflictType,
                           AssemblyLinkConflictTypes.DerivedGeometryChanged,
                           StringComparison.OrdinalIgnoreCase))))
        {
            conflict.Status = AssemblyLinkStatuses.Resolved;
            conflict.ResolvedAt = resolvedAt;
        }
    }

    private RefreshStoreResult RefreshStoreFromSeeds(
        RhinoDoc doc,
        AssemblyStore store,
        IEnumerable<PropagationSeed> initialSeeds)
    {
        var pending = initialSeeds.Distinct().ToList();
        var outcomes = new List<AssemblyCategorizationOutcome>();
        var refreshed = 0;
        // Geometry refreshed by final flat-output synchronization may itself be an input
        // to another assembly. Finish those downstream updates in bounded waves. This also
        // prevents malformed cross-assembly dependency cycles from running indefinitely.
        var waveLimit = Math.Max(2, store.Assemblies.Count + 1);
        for (var wave = 0; pending.Count > 0 && wave < waveLimit; wave++)
        {
            var result = RefreshStoreWave(doc, store, pending);
            refreshed += result.RefreshedObjects;
            outcomes.AddRange(result.CategorizationOutcomes);
            var changedFlats = result.UpdatedFlatObjectIds.ToHashSet();
            pending = store.Assemblies.SelectMany(assembly => assembly.LinkGraph.Nodes
                .Where(node => changedFlats.Contains(node.ObjectId) &&
                               string.Equals(node.Role, AssemblyLinkRoles.Source, StringComparison.OrdinalIgnoreCase))
                .Select(node => new PropagationSeed(assembly.Id, node.Id, RebuildQuarantined: false)))
                .Distinct().ToList();
        }
        foreach (var seed in pending)
        {
            var assembly = store.Assemblies.FirstOrDefault(item => item.Id == seed.AssemblyId);
            if (assembly is not null)
                AddConflict(assembly, AssemblyLinkConflictTypes.DerivedGeometryOutOfDate,
                    "Flat-output propagation reached a repeated assembly dependency. Gazelle stopped the cascade; review the linked assembly cycle before updating again.", seed.SourceNodeId);
        }
        return new RefreshStoreResult(refreshed, outcomes);
    }

    private RefreshStoreResult RefreshStoreWave(
        RhinoDoc doc,
        AssemblyStore store,
        IEnumerable<PropagationSeed> initialSeeds)
    {
        var assembliesById = store.Assemblies.ToDictionary(assembly => assembly.Id);
        var pending = new Queue<PropagationSeed>(initialSeeds);
        var visitedSourceNodes = new HashSet<(Guid AssemblyId, Guid NodeId)>();
        var visitedEdges = new HashSet<(Guid AssemblyId, Guid EdgeId)>();
        var affectedAssemblyIds = new HashSet<Guid>();
        var refreshed = 0;

        while (pending.Count > 0)
        {
            var seed = pending.Dequeue();
            if (!visitedSourceNodes.Add((seed.AssemblyId, seed.SourceNodeId)) ||
                !assembliesById.TryGetValue(seed.AssemblyId, out var assembly) ||
                assembly.PendingComponentUpdates.Count > 0 ||
                ComponentUpdateService.HasInputRegroupIssue(assembly) ||
                _linkSafety?.IsAssemblyBlocked(doc, assembly.Id) == true)
            {
                continue;
            }
            if (affectedAssemblyIds.Add(assembly.Id))
                _updateFeedback($"Gazelle updating linked geometry for '{assembly.Name}'...");

            var regeneratedObjectIds = new HashSet<Guid>();
            refreshed += RefreshGraphDescendants(
                doc,
                assembly,
                new[] { seed.SourceNodeId },
                seed.RebuildQuarantined,
                visitedEdges,
                regeneratedObjectIds);
            assembly.UpdatedAt = DateTimeOffset.UtcNow;

            foreach (var regeneratedObjectId in regeneratedObjectIds)
            {
                foreach (var candidateAssembly in store.Assemblies)
                {
                    foreach (var sourceNode in candidateAssembly.LinkGraph.Nodes.Where(node =>
                                 node.ObjectId == regeneratedObjectId &&
                                 string.Equals(
                                     node.Role,
                                     AssemblyLinkRoles.Source,
                                     StringComparison.OrdinalIgnoreCase)))
                    {
                        pending.Enqueue(new PropagationSeed(
                            candidateAssembly.Id,
                            sourceNode.Id,
                            RebuildQuarantined: false));
                    }
                }
            }
        }

        var categorizationOutcomes = new List<AssemblyCategorizationOutcome>();
        var updatedFlatObjectIds = new HashSet<Guid>();
        foreach (var assemblyId in affectedAssemblyIds.OrderBy(id => id))
        {
            if (!assembliesById.TryGetValue(assemblyId, out var assembly))
                continue;

            var assemblyName = assembly.Name;
            var assemblyIndex = store.Assemblies.FindIndex(candidate => candidate.Id == assemblyId);
            AssemblyRecord? reconciliationSnapshot = null;
            try
            {
                _updateFeedback($"Gazelle recategorizing parts and updating quantities/materials for '{assemblyName}'...");
                reconciliationSnapshot = AssemblyRepository.CreateAssemblySnapshot(assembly);
                var result = _categorization.Reconcile(doc, assembly);
                if (result.PartsReconciled && _flatParts is not null)
                {
                    try
                    {
                        if (assembly.LinkGraph.Nodes.Any(node =>
                                string.Equals(node.Role, AssemblyLinkRoles.FlatPart, StringComparison.OrdinalIgnoreCase) &&
                                doc.Objects.FindId(node.ObjectId) is not null))
                            _updateFeedback($"Gazelle updating flat PARTS geometry, placements, and labels for '{assemblyName}'...");
                        var flatResult = _flatParts.Synchronize(doc, store, assembly, reconciliationSnapshot, result.ProtectedPartIds);
                        updatedFlatObjectIds.UnionWith(flatResult.UpdatedObjectIds);
                        result = result with { RequiresFlatPartRebuild = flatResult.RequiresReview };
                        if (flatResult.AddedOutputs > 0 || flatResult.RemovedOutputs > 0)
                            RhinoApp.WriteLine("Gazelle updated PARTS in '{0}': {1} new representative(s), {2} superseded representative(s) removed.",
                                assembly.Name, flatResult.AddedOutputs, flatResult.RemovedOutputs);
                    }
                    catch (Exception ex)
                    {
                        // Keep successful category changes and any completed flat operations.
                        // Restoring only JSON here could orphan geometry created or retired by
                        // synchronization. The remaining work is an explicit review item.
                        var nodeId = assembly.LinkGraph.Nodes.FirstOrDefault(node =>
                            string.Equals(node.Role, AssemblyLinkRoles.FlatPart, StringComparison.OrdinalIgnoreCase))?.Id ?? Guid.Empty;
                        AddConflict(assembly, AssemblyLinkConflictTypes.DerivedGeometryOutOfDate,
                            $"Assembly categories updated, but flat-part synchronization needs review: {ex.Message}", nodeId);
                        result = result with { RequiresFlatPartRebuild = true };
                    }
                }
                categorizationOutcomes.Add(new AssemblyCategorizationOutcome(
                    assemblyName,
                    result,
                    ErrorMessage: string.Empty,
                    QuantitySummary: DescribePartQuantityChanges(reconciliationSnapshot, assembly)));
            }
            catch (Exception ex)
            {
                if (reconciliationSnapshot is not null && assemblyIndex >= 0)
                {
                    assembly = reconciliationSnapshot;
                    store.Assemblies[assemblyIndex] = assembly;
                    assembliesById[assemblyId] = assembly;
                }

                var sourceNodeId = assembly.LinkGraph.Nodes.FirstOrDefault(node =>
                    string.Equals(node.Role, AssemblyLinkRoles.Source, StringComparison.OrdinalIgnoreCase))?.Id ??
                                   Guid.Empty;
                AddConflict(
                    assembly,
                    AssemblyLinkConflictTypes.PartCategorizationChanged,
                    $"Linked geometry refreshed, but automatic part recategorization failed: {ex.Message}",
                    sourceNodeId,
                    metadata: new Dictionary<string, string>
                    {
                        ["ReconciliationError"] = ex.Message
                    });
                assembly.UpdatedAt = DateTimeOffset.UtcNow;
                assembly.LinkGraph.UpdatedAt = assembly.UpdatedAt;
                categorizationOutcomes.Add(new AssemblyCategorizationOutcome(
                    assemblyName,
                    CategorizationReconciliationResult.Empty,
                    ex.Message));
            }
        }

        return new RefreshStoreResult(refreshed, categorizationOutcomes)
        {
            UpdatedFlatObjectIds = updatedFlatObjectIds.ToArray()
        };
    }

    private static string DescribePartQuantityChanges(AssemblyRecord before, AssemblyRecord after)
    {
        var previousQuantities = before.Parts
            .GroupBy(part => part.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Sum(part => part.Quantity), StringComparer.OrdinalIgnoreCase);
        var currentQuantities = after.Parts
            .GroupBy(part => part.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Sum(part => part.Quantity), StringComparer.OrdinalIgnoreCase);
        return string.Join(", ", previousQuantities.Keys.Concat(currentQuantities.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Where(name => previousQuantities.GetValueOrDefault(name) != currentQuantities.GetValueOrDefault(name))
            .Select(name => $"{name}: {previousQuantities.GetValueOrDefault(name)} -> {currentQuantities.GetValueOrDefault(name)}"));
    }

    private static void ReportCategorizationOutcomes(
        IEnumerable<AssemblyCategorizationOutcome> outcomes)
    {
        foreach (var outcome in outcomes)
        {
            if (!string.IsNullOrWhiteSpace(outcome.ErrorMessage))
            {
                RhinoApp.WriteLine(
                    "Gazelle refreshed linked geometry for '{0}', but part recategorization needs review: {1}",
                    outcome.AssemblyName,
                    outcome.ErrorMessage);
                continue;
            }

            var result = outcome.Result;
            if (result.CreatedParts > 0 || result.RetiredParts > 0 || result.ReassignedSources > 0 ||
                result.CreatedComponents > 0 || result.RetiredComponents > 0 ||
                result.ReassignedComponentInstances > 0)
            {
                RhinoApp.WriteLine(
                    $"Gazelle recategorized '{outcome.AssemblyName}': {result.ReassignedSources} source occurrence(s) reassigned, {result.CreatedParts} new part category(s), and {result.CreatedComponents} new component category(s).");
            }

            if (!string.IsNullOrWhiteSpace(outcome.QuantitySummary))
            {
                RhinoApp.WriteLine(
                    "Gazelle part quantities in '{0}': {1}.",
                    outcome.AssemblyName,
                    outcome.QuantitySummary);
            }

            if (result.RequiresFlatPartRebuild)
            {
                RhinoApp.WriteLine(
                    "Some PARTS output in '{0}' could not be updated safely. See Link Issues in Assembly Manager before rebuilding or removing an output.",
                    outcome.AssemblyName);
            }

            if (result.RequiresCopiedComponentRebuild)
            {
                RhinoApp.WriteLine(
                    "Some component categories in '{0}' have no COPIED COMPONENTS view. Use PlaceComponent to add the missing views when needed. Existing views remain tracked, including multiple views of the same component.",
                    outcome.AssemblyName);
            }
        }
    }

    private static int CountOpenConflicts(AssemblyStore store)
    {
        return store.Assemblies.Sum(assembly => assembly.LinkGraph.Conflicts.Count(conflict =>
            string.Equals(conflict.Status, AssemblyLinkStatuses.Open, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Stage-level feedback only; does not pump UI events or mutate the document.</summary>
    private sealed class UpdateFeedbackScope : IDisposable
    {
        private readonly Action<string> _write;
        private readonly Stopwatch _elapsed = Stopwatch.StartNew();
        private bool _completed;

        public UpdateFeedbackScope(Action<string> write, string startMessage)
        {
            _write = write;
            _write(startMessage);
        }

        public void Complete(RefreshStoreResult result, int openDocumentIssues)
        {
            var issueSummary = openDocumentIssues == 0
                ? "No open link issues in the document."
                : $"{openDocumentIssues} open link issue(s) in the document; see Link Issues at the bottom of Assembly Manager.";
            _write($"Gazelle assembly update finished in {_elapsed.Elapsed.TotalSeconds:0.0} s: " +
                   $"{result.RefreshedObjects} linked object(s) refreshed; {result.CreatedPartCategories} new part category(s). {issueSummary}");
            _completed = true;
        }

        public void Dispose()
        {
            if (!_completed)
                _write($"Gazelle assembly update stopped before completion after {_elapsed.Elapsed.TotalSeconds:0.0} s. " +
                       "See the error or Link Issues in Assembly Manager.");
        }
    }

    private static HashSet<Guid> OpenIssueIds(AssemblyStore store) => store.Assemblies
        .SelectMany(assembly => assembly.LinkGraph.Conflicts)
        .Where(conflict => string.Equals(conflict.Status, AssemblyLinkStatuses.Open, StringComparison.OrdinalIgnoreCase))
        .Select(conflict => conflict.Id)
        .ToHashSet();

    private static void ReportNewIssueDetails(AssemblyStore store, IReadOnlySet<Guid> previousIssueIds)
    {
        var newIssues = store.Assemblies.SelectMany(assembly => assembly.LinkGraph.Conflicts
            .Where(conflict => !previousIssueIds.Contains(conflict.Id) &&
                               string.Equals(conflict.Status, AssemblyLinkStatuses.Open, StringComparison.OrdinalIgnoreCase))
            .Select(conflict => (AssemblyName: assembly.Name, conflict.Message)))
            .ToList();
        foreach (var issue in newIssues.Take(10))
            RhinoApp.WriteLine("Gazelle review for '{0}': {1}", issue.AssemblyName, issue.Message);
        if (newIssues.Count > 10)
            RhinoApp.WriteLine("See Link Issues in Assembly Manager for {0} more review item(s).", newIssues.Count - 10);
    }

    public static void AttachReferenceUserStrings(
        Rhino.DocObjects.ObjectAttributes attributes,
        Guid sourceObjectId,
        Transform sourceToTarget,
        Guid referenceId = default)
    {
        attributes.SetUserString(AssemblyManagerConstants.SourceObjectUserString, sourceObjectId.ToString());
        attributes.SetUserString(
            AssemblyManagerConstants.ReferenceIdUserString,
            (referenceId == Guid.Empty ? Guid.NewGuid() : referenceId).ToString());
        attributes.SetUserString(AssemblyManagerConstants.ReferenceTransformUserString, SerializeTransform(sourceToTarget));
    }

    private int RefreshGraphDescendants(
        RhinoDoc doc,
        AssemblyRecord assembly,
        IEnumerable<Guid> startingNodeIds,
        bool rebuildQuarantined,
        ISet<(Guid AssemblyId, Guid EdgeId)> visitedEdges,
        ISet<Guid> regeneratedObjectIds)
    {
        var nodesById = assembly.LinkGraph.Nodes.ToDictionary(node => node.Id);
        var outgoingEdges = assembly.LinkGraph.Edges
            .GroupBy(edge => edge.ParentNodeId)
            .ToDictionary(group => group.Key, group => group.ToList());
        var startingIds = startingNodeIds.Distinct().ToList();
        var pending = new Queue<Guid>();
        var refreshed = 0;

        using var mutation = AssemblyLinkMutationGate.Enter();
        foreach (var sourceNodeId in startingIds)
        {
            if (!nodesById.TryGetValue(sourceNodeId, out var sourceNode))
                continue;

            if (rebuildQuarantined &&
                string.Equals(sourceNode.Role, AssemblyLinkRoles.Source, StringComparison.OrdinalIgnoreCase) &&
                doc.Objects.FindId(sourceNode.ObjectId) is not null)
            {
                var now = DateTimeOffset.UtcNow;
                sourceNode.Status = AssemblyLinkStatuses.Active;
                sourceNode.UpdatedAt = now;
                ResolveSourceDeletedConflicts(assembly, sourceNode.Id, now);
            }

            if (ValidatePartSourceForPropagation(doc, assembly, sourceNode))
            {
                pending.Enqueue(sourceNodeId);
            }
        }

        while (pending.Count > 0)
        {
            var parentNodeId = pending.Dequeue();
            if (!outgoingEdges.TryGetValue(parentNodeId, out var edges))
                continue;

            foreach (var edge in edges)
            {
                if (!visitedEdges.Add((assembly.Id, edge.Id)))
                    continue;
                if (!nodesById.TryGetValue(edge.ParentNodeId, out var parentNode) ||
                    !nodesById.TryGetValue(edge.ChildNodeId, out var childNode))
                {
                    AddConflict(
                        assembly,
                        AssemblyLinkConflictTypes.MissingObject,
                        "A link edge refers to a node that is not present in the graph.",
                        edgeId: edge.Id);
                    continue;
                }

                if (IsQuarantined(assembly, childNode) && !rebuildQuarantined)
                {
                    childNode.Metadata[AssemblyLinkMetadataKeys.Quarantined] = bool.TrueString;
                    childNode.Status = AssemblyLinkStatuses.Conflict;
                    edge.Status = AssemblyLinkStatuses.Conflict;
                    continue;
                }

                if (string.Equals(childNode.Role, AssemblyLinkRoles.OriginalAssembly, StringComparison.OrdinalIgnoreCase) &&
                    assembly.LinkGraph.Conflicts.Any(conflict => conflict.NodeId == childNode.Id &&
                        string.Equals(conflict.Status, AssemblyLinkStatuses.Open, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(conflict.ConflictType, AssemblyLinkConflictTypes.OriginalAssemblyPromotionUnresolved, StringComparison.OrdinalIgnoreCase)))
                {
                    // Update Assembly is not permission to discard an unresolved original
                    // edit, especially when source and original were edited while paused.
                    childNode.Status = AssemblyLinkStatuses.Conflict;
                    edge.Status = AssemblyLinkStatuses.Conflict;
                    continue;
                }

                var parentObject = doc.Objects.FindId(parentNode.ObjectId);
                var childObject = doc.Objects.FindId(childNode.ObjectId);
                if (parentObject is null || childObject is null)
                {
                    parentNode.Status = parentObject is null ? AssemblyLinkStatuses.Missing : parentNode.Status;
                    childNode.Status = childObject is null ? AssemblyLinkStatuses.Missing : childNode.Status;
                    edge.Status = AssemblyLinkStatuses.Conflict;
                    AddConflict(
                        assembly,
                        AssemblyLinkConflictTypes.MissingObject,
                        $"Could not refresh link because {(parentObject is null ? "parent" : "child")} object is missing.",
                        childNode.Id,
                        edge.Id);
                    continue;
                }

                if (!TryRegenerateChild(
                        parentObject,
                        childObject,
                        edge,
                        doc.ModelAbsoluteTolerance,
                        out var geometry,
                        out var effectiveTransform,
                        out var failureMessage) ||
                    !ReplaceGeometry(doc, childNode.ObjectId, geometry))
                {
                    childNode.Status = AssemblyLinkStatuses.Conflict;
                    edge.Status = AssemblyLinkStatuses.Conflict;
                    AddConflict(
                        assembly,
                        AssemblyLinkConflictTypes.TransformUnresolved,
                        string.IsNullOrWhiteSpace(failureMessage)
                            ? "Gazelle could not regenerate the linked child with its stored recipe and transform."
                            : failureMessage,
                        childNode.Id,
                        edge.Id);
                    continue;
                }

                if (!CopyMaterialAttributes(doc, parentNode.ObjectId, childNode.ObjectId))
                {
                    childNode.Status = AssemblyLinkStatuses.Conflict;
                    edge.Status = AssemblyLinkStatuses.Conflict;
                    AddConflict(assembly, AssemblyLinkConflictTypes.DerivedGeometryOutOfDate,
                        "Linked geometry refreshed, but Rhino could not update its material attributes. Review the locked or reference-only object.", childNode.Id, edge.Id);
                    continue;
                }

                var now = DateTimeOffset.UtcNow;
                edge.ParentToChildTransform = TransformRecord.FromTransform(effectiveTransform);
                childNode.Status = AssemblyLinkStatuses.Active;
                childNode.UpdatedAt = now;
                edge.Status = AssemblyLinkStatuses.Active;
                edge.UpdatedAt = now;
                childNode.Metadata.Remove(AssemblyLinkMetadataKeys.Quarantined);
                ResolveRefreshConflicts(assembly, childNode.Id, edge.Id, now);
                _lineage.ApplyObjectMetadata(doc, assembly, childNode, edge, parentNode.ObjectId);
                if (doc.Objects.FindId(childNode.ObjectId) is { } refreshedChild &&
                    _fingerprints.TryCreatePartCandidate(refreshedChild, out var refreshedCandidate, out _))
                {
                    childNode.GeometryFingerprint = refreshedCandidate.Fingerprint;
                }
                pending.Enqueue(childNode.Id);
                regeneratedObjectIds.Add(childNode.ObjectId);
                refreshed++;
            }
        }

        assembly.LinkGraph.UpdatedAt = DateTimeOffset.UtcNow;
        return refreshed;
    }

    private static bool CopyMaterialAttributes(RhinoDoc doc, Guid sourceId, Guid targetId,
        bool preserveSourceStockFromNormalizedOriginal = false)
    {
        var source = doc.Objects.FindId(sourceId);
        var target = doc.Objects.FindId(targetId);
        if (source is null || target is null)
            return false;
        if (preserveSourceStockFromNormalizedOriginal &&
            string.Equals(MaterialAssignment.GetCategorizationMaterialId(source.Attributes),
                MaterialAssignment.GetCategorizationMaterialId(target.Attributes), StringComparison.Ordinal) &&
            string.Equals(MaterialAssignment.GetCategorizationMaterialId(source.Attributes),
                MaterialAssignment.NormalizeMaterialIdForCategory(MaterialAssignment.GetMaterialId(source.Attributes)), StringComparison.Ordinal) &&
            !string.Equals(MaterialAssignment.GetCategorizationMaterialId(target.Attributes),
                MaterialAssignment.NormalizeMaterialIdForCategory(MaterialAssignment.GetMaterialId(target.Attributes)), StringComparison.Ordinal))
        {
            // Creation normalizes generated originals to their parent material. A geometry
            // edit of that original must not erase a stock-shape choice retained on input.
            return true;
        }
        var attributes = target.Attributes.Duplicate();
        return !MaterialAssignment.Copy(source.Attributes, attributes) ||
               doc.Objects.ModifyAttributes(targetId, attributes, true);
    }

    private sealed record RefreshStoreResult(
        int RefreshedObjects,
        IReadOnlyList<AssemblyCategorizationOutcome> CategorizationOutcomes)
    {
        public int CreatedPartCategories => CategorizationOutcomes.Sum(outcome => outcome.Result.CreatedParts);
        public IReadOnlyList<Guid> UpdatedFlatObjectIds { get; init; } = Array.Empty<Guid>();
    }

    private readonly record struct AssemblyCategorizationOutcome(
        string AssemblyName,
        CategorizationReconciliationResult Result,
        string ErrorMessage,
        string QuantitySummary = "");

    private readonly record struct PropagationSeed(
        Guid AssemblyId,
        Guid SourceNodeId,
        bool RebuildQuarantined);

    private sealed record OriginalAssemblyPromotion(
        AssemblyRecord Assembly,
        AssemblyLinkNodeRecord SourceNode,
        AssemblyLinkNodeRecord OriginalNode,
        AssemblyLinkEdgeRecord IncomingEdge,
        Brep PromotedSourceGeometry,
        string OriginalFingerprint);

    private bool ValidatePartSourceForPropagation(
        RhinoDoc doc,
        AssemblyRecord assembly,
        AssemblyLinkNodeRecord sourceNode)
    {
        if (!string.Equals(sourceNode.Role, AssemblyLinkRoles.Source, StringComparison.OrdinalIgnoreCase) ||
            sourceNode.PartId == Guid.Empty ||
            assembly.LinkGraph.Edges.Any(edge =>
                edge.ParentNodeId == sourceNode.Id &&
                string.Equals(edge.Recipe, AssemblyLinkRecipes.BlockDefinitionPart, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var part = assembly.Parts.FirstOrDefault(candidate => candidate.Id == sourceNode.PartId);
        var sourceObject = doc.Objects.FindId(sourceNode.ObjectId);
        if (part is null || sourceObject is null)
            return true;

        if (!_fingerprints.TryCreatePartCandidate(sourceObject, out _, out var warning))
        {
            AddConflict(
                assembly,
                AssemblyLinkConflictTypes.PartCategorizationChanged,
                string.IsNullOrWhiteSpace(warning)
                    ? $"Source geometry for '{part.Name}' is no longer a supported solid part; quantities and representative outputs require review."
                    : $"Source geometry for '{part.Name}' changed categorization: {warning}",
                sourceNode.Id,
                metadata: new Dictionary<string, string>
                {
                    ["PartId"] = part.Id.ToString("D"),
                    ["PartName"] = part.Name,
                    ["CanonicalFingerprint"] = part.GeometryFingerprint,
                    ["CurrentFingerprint"] = string.Empty
                });
            return false;
        }

        return true;
    }

    private bool TryRegenerateChild(
        RhinoObject parentObject,
        RhinoObject childObject,
        AssemblyLinkEdgeRecord edge,
        double modelTolerance,
        out GeometryBase geometry,
        out Transform effectiveTransform,
        out string failureMessage)
    {
        geometry = default!;
        effectiveTransform = Transform.Identity;
        failureMessage = string.Empty;

        if (string.Equals(edge.Recipe, AssemblyLinkRecipes.HardwareCopy, StringComparison.OrdinalIgnoreCase))
        {
            if (!HardwareMetadata.TryGetFromObject(parentObject, out _) ||
                !HardwareMetadata.TryGetFromObject(childObject, out _))
            {
                failureMessage = "A whole-hardware link has lost its hardware identity. Review it before updating.";
                return false;
            }
            if (!edge.ParentToChildTransform.TryToTransform(out effectiveTransform))
            {
                failureMessage = "The stored hardware placement is invalid.";
                return false;
            }
            geometry = parentObject.Geometry.Duplicate();
            if (geometry is not null && geometry.Transform(effectiveTransform))
                return true;
            geometry?.Dispose();
            geometry = default!;
            failureMessage = "Rhino could not duplicate the linked hardware at its stored placement.";
            return false;
        }

        if (string.Equals(edge.Recipe, AssemblyLinkRecipes.BlockDefinitionPart, StringComparison.OrdinalIgnoreCase))
        {
            failureMessage = "This linked output came from a block-definition leaf. Gazelle preserved it because the stored link does not yet identify a unique definition path for safe regeneration.";
            return false;
        }

        if (string.Equals(edge.Recipe, AssemblyLinkRecipes.LayFlat, StringComparison.OrdinalIgnoreCase))
        {
            if (!_fingerprints.TryDuplicateManufacturableBrep(parentObject, out var sourceBrep, out var warning))
            {
                failureMessage = string.IsNullOrWhiteSpace(warning)
                    ? "Gazelle could not create a manufacturable solid for the stored lay-flat recipe."
                    : warning;
                return false;
            }

            var currentBounds = childObject.Geometry.GetBoundingBox(true);
            if (!currentBounds.IsValid)
            {
                failureMessage = "Gazelle could not preserve the laid-flat part's current layout anchor.";
                return false;
            }

            // Prefer the previously accepted orientation when it still leaves the revised solid
            // flat. This avoids gratuitously flipping a symmetric part to its opposite face.
            if (edge.ParentToChildTransform.TryToTransform(out var previousTransform))
            {
                var stableBrep = sourceBrep.DuplicateBrep();
                var preserveUserPlanRotation = edge.RecipeMetadata.TryGetValue(
                                                   AssemblyLinkMetadataKeys.UserPlanRotationOverride,
                                                   out var overrideValue) &&
                                               bool.TryParse(overrideValue, out var hasOverride) &&
                                               hasOverride;
                if (stableBrep.Transform(previousTransform) &&
                    IsFlatOnWorldXY(stableBrep, modelTolerance) &&
                    (preserveUserPlanRotation || LongAxisIsY(stableBrep, modelTolerance)))
                {
                    var stableBounds = stableBrep.GetBoundingBox(true);
                    if (stableBounds.IsValid)
                    {
                        var stablePlacement = Transform.Translation(
                            currentBounds.Min.X - stableBounds.Min.X,
                            currentBounds.Min.Y - stableBounds.Min.Y,
                            currentBounds.Min.Z - stableBounds.Min.Z);
                        if (stableBrep.Transform(stablePlacement))
                        {
                            geometry = stableBrep;
                            effectiveTransform = stablePlacement * previousTransform;
                            return true;
                        }
                    }
                }
            }

            var flatBrep = sourceBrep.DuplicateBrep();
            var orientation = TransformUtilities.OrientLargestFaceToWorldXY(
                flatBrep,
                _fingerprints,
                Point3d.Origin);
            if (!flatBrep.Transform(orientation))
            {
                failureMessage = "Gazelle could not orient the revised part onto the World XY plane.";
                return false;
            }

            var longAxisRotation = TransformUtilities.RotateLongDimensionToY(flatBrep);
            var revisedBounds = flatBrep.GetBoundingBox(true);
            if (!revisedBounds.IsValid)
            {
                failureMessage = "Gazelle could not preserve the laid-flat part's current layout anchor.";
                return false;
            }

            var placement = Transform.Translation(
                currentBounds.Min.X - revisedBounds.Min.X,
                currentBounds.Min.Y - revisedBounds.Min.Y,
                currentBounds.Min.Z - revisedBounds.Min.Z);
            if (!flatBrep.Transform(placement))
            {
                failureMessage = "Gazelle could not return the revised flat part to its existing layout location.";
                return false;
            }

            geometry = flatBrep;
            effectiveTransform = placement * longAxisRotation * orientation;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(edge.Recipe) &&
            !string.Equals(edge.Recipe, AssemblyLinkRecipes.DirectCopy, StringComparison.OrdinalIgnoreCase))
        {
            failureMessage = $"Gazelle preserved this output because regeneration recipe '{edge.Recipe}' is not supported automatically.";
            return false;
        }

        if (!edge.ParentToChildTransform.TryToTransform(out effectiveTransform))
        {
            failureMessage = "The stored parent-to-child transform is malformed. Gazelle preserved the existing child instead of substituting an identity transform.";
            return false;
        }

        if (!_fingerprints.TryDuplicateManufacturableBrep(parentObject, out var directCopyBrep, out var directCopyWarning))
        {
            failureMessage = string.IsNullOrWhiteSpace(directCopyWarning)
                ? "Gazelle could not create a manufacturable BREP for this direct-copy link."
                : directCopyWarning;
            return false;
        }

        geometry = directCopyBrep;
        if (!geometry.Transform(effectiveTransform))
        {
            failureMessage = "Gazelle could not apply the stored parent-to-child transform.";
            return false;
        }

        return true;
    }

    private bool IsFlatOnWorldXY(Brep brep, double modelTolerance)
    {
        var bounds = brep.GetBoundingBox(true);
        if (!bounds.IsValid || !_fingerprints.TryGetLargestFacePlane(brep, out var dominantPlane))
            return false;

        var normal = dominantPlane.Normal;
        if (!normal.Unitize())
            return false;

        // A small Z extent alone is insufficient: a thin plate tilted in space can still have
        // a smaller Z bounding-box dimension than X or Y. The dominant manufacturing face must
        // actually be parallel to World XY.
        const double angularToleranceRadians = Math.PI / 1800.0; // 0.1 degree
        if (Math.Abs(normal.Z) < Math.Cos(angularToleranceRadians))
            return false;

        var z = Math.Abs(bounds.Max.Z - bounds.Min.Z);
        var expectedThickness = _fingerprints.GetMaterialThickness(brep);
        var tolerance = Math.Max(modelTolerance, RhinoMath.ZeroTolerance);
        return expectedThickness > tolerance &&
               Math.Abs(z - expectedThickness) <= Math.Max(tolerance * 10.0, expectedThickness * 1e-6);
    }

    private static bool LongAxisIsY(Brep brep, double modelTolerance)
    {
        var bounds = brep.GetBoundingBox(true);
        if (!bounds.IsValid)
            return false;

        var x = Math.Abs(bounds.Max.X - bounds.Min.X);
        var y = Math.Abs(bounds.Max.Y - bounds.Min.Y);
        return x <= y + Math.Max(modelTolerance, RhinoMath.ZeroTolerance);
    }

    private static bool IsQuarantined(AssemblyRecord assembly, AssemblyLinkNodeRecord node)
    {
        if (node.Metadata.TryGetValue(AssemblyLinkMetadataKeys.Quarantined, out var value) &&
            bool.TryParse(value, out var quarantined) && quarantined)
        {
            return true;
        }

        return assembly.LinkGraph.Conflicts.Any(conflict =>
            conflict.NodeId == node.Id &&
            string.Equals(conflict.Status, AssemblyLinkStatuses.Open, StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(
                 conflict.ConflictType,
                 AssemblyLinkConflictTypes.DerivedGeometryChanged,
                 StringComparison.OrdinalIgnoreCase) ||
             string.Equals(
                 conflict.ConflictType,
                 AssemblyLinkConflictTypes.OriginalAssemblyPromotionUnresolved,
                 StringComparison.OrdinalIgnoreCase)));
    }

    private static void ResolveSourceDeletedConflicts(
        AssemblyRecord assembly,
        Guid nodeId,
        DateTimeOffset resolvedAt)
    {
        foreach (var conflict in assembly.LinkGraph.Conflicts.Where(conflict =>
                     conflict.NodeId == nodeId &&
                     string.Equals(conflict.Status, AssemblyLinkStatuses.Open, StringComparison.OrdinalIgnoreCase) &&
                     string.Equals(
                         conflict.ConflictType,
                         AssemblyLinkConflictTypes.SourceDeleted,
                         StringComparison.OrdinalIgnoreCase)))
        {
            conflict.Status = AssemblyLinkStatuses.Resolved;
            conflict.ResolvedAt = resolvedAt;
        }
    }

    private static void ResolveRefreshConflicts(
        AssemblyRecord assembly,
        Guid nodeId,
        Guid edgeId,
        DateTimeOffset resolvedAt)
    {
        foreach (var conflict in assembly.LinkGraph.Conflicts.Where(conflict =>
                     string.Equals(conflict.Status, AssemblyLinkStatuses.Open, StringComparison.OrdinalIgnoreCase) &&
                     (conflict.NodeId == nodeId || conflict.EdgeId == edgeId) &&
                     (string.Equals(
                          conflict.ConflictType,
                          AssemblyLinkConflictTypes.MissingObject,
                          StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(
                          conflict.ConflictType,
                          AssemblyLinkConflictTypes.TransformUnresolved,
                          StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(
                          conflict.ConflictType,
                          AssemblyLinkConflictTypes.DerivedGeometryOutOfDate,
                          StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(
                           conflict.ConflictType,
                           AssemblyLinkConflictTypes.DerivedGeometryChanged,
                           StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(
                           conflict.ConflictType,
                           AssemblyLinkConflictTypes.OriginalAssemblyPromotionUnresolved,
                           StringComparison.OrdinalIgnoreCase))))
        {
            conflict.Status = AssemblyLinkStatuses.Resolved;
            conflict.ResolvedAt = resolvedAt;
        }
    }

    private static void AddConflict(
        AssemblyRecord assembly,
        string conflictType,
        string message,
        Guid nodeId = default,
        Guid edgeId = default,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        var existing = assembly.LinkGraph.Conflicts.FirstOrDefault(conflict =>
            string.Equals(conflict.Status, AssemblyLinkStatuses.Open, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(conflict.ConflictType, conflictType, StringComparison.OrdinalIgnoreCase) &&
            conflict.NodeId == nodeId && conflict.EdgeId == edgeId);
        if (existing is not null)
        {
            existing.Message = message;
            existing.DetectedAt = DateTimeOffset.UtcNow;
            if (metadata is not null)
            {
                foreach (var (key, value) in metadata)
                    existing.Metadata[key] = value;
            }
            return;
        }

        assembly.LinkGraph.Conflicts.Add(new LinkConflictRecord
        {
            ConflictType = conflictType,
            NodeId = nodeId,
            EdgeId = edgeId,
            Message = message,
            Metadata = metadata is null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string>(metadata)
        });
    }

    private static bool ReplaceGeometry(RhinoDoc doc, Guid objectId, GeometryBase geometry)
    {
        return geometry switch
        {
            Brep brep => doc.Objects.Replace(objectId, brep),
            Curve curve => doc.Objects.Replace(objectId, curve),
            Mesh mesh => doc.Objects.Replace(objectId, mesh),
            Extrusion extrusion => doc.Objects.Replace(objectId, extrusion),
            Surface surface => doc.Objects.Replace(objectId, surface),
            InstanceReferenceGeometry instance => doc.Objects.Replace(objectId, instance, false),
            _ => false
        };
    }

    private static string SerializeTransform(Transform transform)
    {
        var values = TransformRecord.FromTransform(transform).Values;
        return string.Join(",", values.Select(v => v.ToString("R", CultureInfo.InvariantCulture)));
    }
}
