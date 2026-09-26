using System.Collections.Concurrent;
using System.Collections.Immutable;
using AssemblyManagerPlugin.Core;
using AssemblyManagerPlugin.Geometry;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.DocObjects.Tables;
using Rhino.Geometry;

namespace AssemblyManagerPlugin.Services;

/// <summary>
/// Coordinates Rhino document events with the persisted assembly-link graph.
/// Event callbacks only capture immutable facts. All document metadata changes and
/// descendant refresh requests are deferred until Rhino reaches its idle state.
/// </summary>
public sealed class AssemblyLinkEventService : IDisposable
{
    private const string ConflictEventKey = "EventKey";
    private const string PendingSourceUpdateKey = "PendingSourceUpdate";
    private const string PendingOriginalUpdateKey = "PendingOriginalUpdate";
    private const string PendingOriginalMaterialKey = "PendingOriginalSourceMaterial";
    private static readonly string[] MaterialAttributeKeys =
    {
        AssemblyManagerConstants.MaterialIdUserString,
        AssemblyManagerConstants.MaterialNameUserString,
        AssemblyManagerConstants.MaterialBaseIdUserString,
        AssemblyManagerConstants.MaterialBaseNameUserString,
        AssemblyManagerConstants.MaterialShapeNameUserString,
        AssemblyManagerConstants.MaterialShapeTypeUserString
    };
    private readonly AssemblyRepository _repository;
    private readonly ReferenceUpdateService _referenceUpdates;
    private readonly AssemblyLineageService _lineage;
    private readonly GeometryFingerprintService _fingerprints;
    private readonly Func<bool> _automaticPropagationEnabled;
    private readonly ComponentUpdateService? _componentUpdates;
    private readonly LinkedAssemblySafetyService? _linkSafety;
    private readonly ConcurrentDictionary<uint, bool> _deferredDocuments = new();
    private readonly ConcurrentDictionary<uint, ConcurrentQueue<LinkEventFact>> _queues = new();
    private readonly ConcurrentDictionary<uint, ConcurrentStack<Guid>> _activeCommandBatches = new();
    private readonly ConcurrentDictionary<uint, UndoSnapshot> _undoSnapshots = new();
    private readonly ConcurrentDictionary<uint, ImmutableArray<string>> _undoHealthIssues = new();
    private int _started;
    private int _processingIdle;

    public AssemblyLinkEventService(
        AssemblyRepository repository,
        ReferenceUpdateService referenceUpdates,
        AssemblyLineageService lineage,
        GeometryFingerprintService fingerprints,
        Func<bool>? automaticPropagationEnabled = null,
        ComponentUpdateService? componentUpdates = null,
        LinkedAssemblySafetyService? linkSafety = null)
    {
        _repository = repository;
        _referenceUpdates = referenceUpdates;
        _lineage = lineage;
        _fingerprints = fingerprints;
        _automaticPropagationEnabled = automaticPropagationEnabled ?? (() => true);
        _componentUpdates = componentUpdates;
        _linkSafety = linkSafety;
    }

    public bool IsStarted => Volatile.Read(ref _started) != 0;

    /// <summary>
    /// Finish already captured placement bookkeeping before suspending the link system.
    /// The durable safety snapshot then covers the exact point tracking stopped.
    /// </summary>
    public void RefreshLinkingPreference()
    {
        if (_linkSafety is null)
            return;
        if (Interlocked.Exchange(ref _processingIdle, 1) != 0)
            throw new InvalidOperationException("Wait for the current assembly update before changing linked assembly settings.");
        try
        {
            foreach (var entry in _queues.ToArray())
            {
                if (RhinoDoc.FromRuntimeSerialNumber(entry.Key) is not { } doc)
                    continue;
                var facts = Drain(entry.Value);
                if (facts.Count > 0)
                    ProcessFacts(doc, facts, propagateChanges: false);
            }
            _linkSafety.ApplyPreferenceToOpenDocuments();
            if (!_linkSafety.IsEnabled)
            {
                _queues.Clear();
                _deferredDocuments.Clear();
                _activeCommandBatches.Clear();
                _undoSnapshots.Clear();
            }
            else
            {
                foreach (var doc in RhinoDoc.OpenDocuments())
                    Enqueue(doc.RuntimeSerialNumber, new PendingUpdateFact());
            }
        }
        finally
        {
            Volatile.Write(ref _processingIdle, 0);
        }
    }

    /// <summary>Registers an explicit component regroup without propagating geometry.</summary>
    public ComponentUpdateResult StageComponentUpdate(
        RhinoDoc doc, string assemblyName, Guid componentId, Guid regroupedGroupId)
    {
        ArgumentNullException.ThrowIfNull(doc);
        _linkSafety?.EnsureEnabled();
        if (_componentUpdates is null)
            throw new InvalidOperationException("Component updates are not available in this service instance.");
        var assembly = _repository.Load(doc).FindAssembly(assemblyName)
            ?? throw new InvalidOperationException($"Assembly '{assemblyName}' was not found.");
        _linkSafety?.EnsureCanUpdate(doc, assembly.Id);
        if (UndoOrRedoIsActive(doc))
            throw new InvalidOperationException("Wait for undo or redo to finish before updating a component.");
        if (Interlocked.Exchange(ref _processingIdle, 1) != 0)
            throw new InvalidOperationException("An assembly update is already in progress.");
        var undoRecord = doc.UndoRecordingEnabled && !doc.UndoRecordingIsActive
            ? doc.BeginUndoRecord("Gazelle Update Component") : 0u;
        try
        {
            var facts = _queues.TryGetValue(doc.RuntimeSerialNumber, out var queue)
                ? Drain(queue) : new List<LinkEventFact>();
            ProcessFacts(doc, facts, propagateChanges: false);
            using var mutation = AssemblyLinkMutationGate.Enter();
            var result = _componentUpdates.Stage(doc, assemblyName, componentId, regroupedGroupId);
            RhinoApp.WriteLine($"Gazelle staged {result.AddedObjectCount} added item(s) for the selected occurrence " +
                $"of {result.ComponentName} in '{result.AssemblyName}'. Click Update Assembly to apply them and update its component category; other occurrences remain unchanged.");
            return result;
        }
        finally
        {
            if (undoRecord != 0)
                doc.EndUndoRecord(undoRecord);
            Volatile.Write(ref _processingIdle, 0);
        }
    }

    /// <summary>
    /// Applies queued event bookkeeping first, then accepts safe deferred ORIGINAL ASSEMBLIES
    /// edits before rebuilding the selected assembly. This remains available while automatic
    /// propagation is disabled and does not change the operator's preference.
    /// </summary>
    public int UpdateAssembly(RhinoDoc doc, string assemblyName)
    {
        ArgumentNullException.ThrowIfNull(doc);
        _linkSafety?.EnsureEnabled();
        if (UndoOrRedoIsActive(doc))
            throw new InvalidOperationException("Wait for undo or redo to finish before updating an assembly.");
        var assembly = _repository.Load(doc).FindAssembly(assemblyName)
            ?? throw new InvalidOperationException($"Assembly '{assemblyName}' was not found.");
        _linkSafety?.EnsureCanUpdate(doc, assembly.Id);
        if (Interlocked.Exchange(ref _processingIdle, 1) != 0)
            throw new InvalidOperationException("An assembly update is already in progress.");

        var undoRecord = doc.UndoRecordingEnabled && !doc.UndoRecordingIsActive
            ? doc.BeginUndoRecord("Gazelle Update Assembly")
            : 0u;
        try
        {
            RhinoApp.WriteLine("Gazelle preparing assembly update for '{0}': checking pending edits and links...", assemblyName);
            var facts = _queues.TryGetValue(doc.RuntimeSerialNumber, out var queue)
                ? Drain(queue)
                : new List<LinkEventFact>();
            // Other assemblies still receive UUID/placement bookkeeping. The reference
            // updater includes shared-input/downstream consumers, not unrelated assemblies.
            ProcessFacts(doc, facts, propagateChanges: false);
            var pendingStore = _repository.Load(doc);
            var pendingAssembly = pendingStore.FindAssembly(assemblyName)!;
            if (pendingAssembly.PendingComponentUpdates.Count > 0)
            {
                if (_componentUpdates is null)
                    throw new InvalidOperationException("Component updates are not available in this service instance.");
                RhinoApp.WriteLine("Gazelle applying staged component additions in '{0}'...", assemblyName);
                using var mutation = AssemblyLinkMutationGate.Enter();
                _componentUpdates.ApplyPending(doc, pendingStore, pendingAssembly);
                _repository.Save(doc, pendingStore);
            }
            // Validate/materialize staged membership before promoting any deferred edits.
            // The hold is now removed, so unrelated originals edited while staged can be
            // accepted safely instead of being overwritten by the final source refresh.
            ProcessFacts(doc, Array.Empty<LinkEventFact>(), propagateChanges: true, assembly.Id,
                prepareForManualUpdate: true);
            return _referenceUpdates.RefreshAssemblyReferences(doc, assemblyName);
        }
        finally
        {
            if (undoRecord != 0)
                doc.EndUndoRecord(undoRecord);
            Volatile.Write(ref _processingIdle, 0);
        }
    }

    public IReadOnlyList<string> GetUndoHealthIssues(RhinoDoc doc)
    {
        return _undoHealthIssues.TryGetValue(doc.RuntimeSerialNumber, out var issues)
            ? issues
            : Array.Empty<string>();
    }

    /// <summary>
    /// Re-runs the same read-only consistency inspection used after undo/redo.
    /// This intentionally does not persist UUID repairs or conflict records so it
    /// cannot add an unexpected document mutation to the user's undo/redo chain.
    /// </summary>
    public IReadOnlyList<string> ValidateLinks(RhinoDoc doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var store = _repository.Load(doc);
        InspectAfterUndoRedo(doc, store, CaptureUndoSnapshot(store));
        return GetUndoHealthIssues(doc);
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            return;

        RhinoDoc.BeforeTransformObjects += OnBeforeTransformObjects;
        RhinoDoc.ReplaceRhinoObject += OnReplaceRhinoObject;
        RhinoDoc.DeleteRhinoObject += OnDeleteRhinoObject;
        RhinoDoc.AddRhinoObject += OnAddRhinoObject;
        RhinoDoc.ModifyObjectAttributes += OnModifyObjectAttributes;
        RhinoDoc.GroupTableEvent += OnGroupTableEvent;
        RhinoDoc.UnitsChangedWithScaling += OnUnitsChangedWithScaling;
        RhinoDoc.CloseDocument += OnCloseDocument;
        RhinoDoc.EndOpenDocument += OnEndOpenDocument;
        Command.BeginCommand += OnBeginCommand;
        Command.EndCommand += OnEndCommand;
        Command.UndoRedo += OnUndoRedo;
        RhinoApp.Idle += OnIdle;
        // Rhino may load the plugin on demand after a model is already open.
        foreach (var doc in RhinoDoc.OpenDocuments())
        {
            _linkSafety?.ObserveDocument(doc);
            if (_linkSafety?.IsEnabled != false)
                Enqueue(doc.RuntimeSerialNumber, new PendingUpdateFact());
        }
    }

    public void Stop()
    {
        if (Interlocked.Exchange(ref _started, 0) == 0)
            return;

        RhinoApp.Idle -= OnIdle;
        Command.UndoRedo -= OnUndoRedo;
        Command.EndCommand -= OnEndCommand;
        Command.BeginCommand -= OnBeginCommand;
        RhinoDoc.CloseDocument -= OnCloseDocument;
        RhinoDoc.EndOpenDocument -= OnEndOpenDocument;
        RhinoDoc.UnitsChangedWithScaling -= OnUnitsChangedWithScaling;
        RhinoDoc.GroupTableEvent -= OnGroupTableEvent;
        RhinoDoc.ModifyObjectAttributes -= OnModifyObjectAttributes;
        RhinoDoc.AddRhinoObject -= OnAddRhinoObject;
        RhinoDoc.DeleteRhinoObject -= OnDeleteRhinoObject;
        RhinoDoc.ReplaceRhinoObject -= OnReplaceRhinoObject;
        RhinoDoc.BeforeTransformObjects -= OnBeforeTransformObjects;
        _queues.Clear();
        _activeCommandBatches.Clear();
        _undoSnapshots.Clear();
        _undoHealthIssues.Clear();
        _deferredDocuments.Clear();
    }

    /// <summary>
    /// Suppresses events caused by geometry changes made by Assembly Manager itself.
    /// The scope is process-wide because Rhino document events are static.
    /// </summary>
    public void Dispose()
    {
        Stop();
    }

    private void OnBeforeTransformObjects(object? sender, RhinoTransformObjectsEventArgs e)
    {
        if (!ShouldCapture())
            return;

        var captureId = Guid.NewGuid();
        var objectsByDocument = e.Objects
            .Where(obj => obj?.Document is not null)
            .GroupBy(obj => obj.Document.RuntimeSerialNumber);

        foreach (var group in objectsByDocument)
        {
            var doc = group.First().Document;
            if (UndoOrRedoIsActive(doc))
                continue;

            var groupedObjects = group.ToList();
            var originalGeometryByObjectId = CaptureTransformGeometrySnapshots(groupedObjects, e.Transform);
            var geometryChangedIds = groupedObjects
                .Where(HasSelectedSubObjects)
                .Select(obj => obj.Id)
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToImmutableArray();
            if (!geometryChangedIds.IsDefaultOrEmpty)
                Enqueue(
                    group.Key,
                    new GeometryChangedFact(
                        geometryChangedIds,
                        originalGeometryByObjectId,
                        captureId));

            var objectIds = groupedObjects
                .Where(obj => !HasSelectedSubObjects(obj))
                .Select(obj => obj.Id)
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToImmutableArray();
            if (!objectIds.IsDefaultOrEmpty)
                Enqueue(
                    group.Key,
                    new TransformFact(
                        objectIds,
                        e.Transform,
                        e.ObjectsWillBeCopied,
                        originalGeometryByObjectId,
                        captureId));
        }

        // Grip edits deform geometry and therefore are refresh requests, not rigid placement
        // transforms. Applying the grip matrix to graph edges would corrupt their placement.
        var gripOwnersByDocument = e.GripOwners
            .Where(obj => obj?.Document is not null)
            .GroupBy(obj => obj.Document.RuntimeSerialNumber);

        foreach (var group in gripOwnersByDocument)
        {
            var doc = group.First().Document;
            if (UndoOrRedoIsActive(doc))
                continue;

            var groupedObjects = group.ToList();
            var objectIds = groupedObjects.Select(obj => obj.Id).Where(id => id != Guid.Empty).Distinct().ToImmutableArray();
            if (!objectIds.IsDefaultOrEmpty)
                Enqueue(
                    group.Key,
                    new GeometryChangedFact(
                        objectIds,
                        CaptureGeometrySnapshots(groupedObjects),
                        captureId));
        }
    }

    private void OnReplaceRhinoObject(object? sender, RhinoReplaceObjectEventArgs e)
    {
        var doc = e.Document;
        if (!ShouldCapture(doc))
            return;

        var oldObjectId = e.OldRhinoObject?.Id ?? Guid.Empty;
        if (oldObjectId == Guid.Empty)
            oldObjectId = e.ObjectId;
        var newObjectId = e.NewRhinoObject?.Id ?? Guid.Empty;
        var newObjectIdWasUnavailable = newObjectId == Guid.Empty;
        if (newObjectIdWasUnavailable)
            newObjectId = e.ObjectId;
        var linkNodeId = e.OldRhinoObject?.Attributes.GetUserString(
                             AssemblyManagerConstants.LinkNodeIdUserString) ??
                         e.NewRhinoObject?.Attributes.GetUserString(
                             AssemblyManagerConstants.LinkNodeIdUserString) ??
                         string.Empty;
        Enqueue(
            doc.RuntimeSerialNumber,
            new ReplaceFact(
                oldObjectId,
                newObjectId,
                newObjectIdWasUnavailable,
                linkNodeId,
                ScaleGeometrySnapshot.Capture(e.OldRhinoObject?.Geometry, e.OldRhinoObject?.Attributes),
                ScaleGeometrySnapshot.Capture(e.NewRhinoObject?.Geometry, e.NewRhinoObject?.Attributes)));
    }

    private static bool HasSelectedSubObjects(RhinoObject obj)
    {
        try
        {
            return obj.GetSelectedSubObjects()?.Length > 0;
        }
        catch
        {
            // Some custom Rhino object types do not expose their subobject selection state.
            // The geometry-snapshot classifier still verifies any later replacement.
            return false;
        }
    }

    private static ImmutableDictionary<Guid, ScaleGeometrySnapshot> CaptureGeometrySnapshots(
        IEnumerable<RhinoObject> objects)
    {
        return objects
            .Where(obj => obj.Id != Guid.Empty)
            .GroupBy(obj => obj.Id)
            .ToImmutableDictionary(
                group => group.Key,
                group => ScaleGeometrySnapshot.Capture(group.Last().Geometry, group.Last().Attributes));
    }

    private static ImmutableDictionary<Guid, ScaleGeometrySnapshot> CaptureTransformGeometrySnapshots(
        IEnumerable<RhinoObject> objects, Transform transform)
    {
        // Rigid whole-object placement only needs the matrix. Any replacement is still
        // independently verified with full old/new geometry captured by OnReplace, so a
        // misleading rigid Gumball notification cannot hide a shape edit. Keep pre-edit
        // evidence for actual subobjects and non-rigid transforms, whose promotion paths
        // need it. This avoids an unused mass-properties pass on every moved solid.
        return CaptureGeometrySnapshots(transform.RigidType == TransformRigidType.Rigid
            ? objects.Where(HasSelectedSubObjects)
            : objects);
    }

    private void OnDeleteRhinoObject(object? sender, RhinoObjectEventArgs e)
    {
        var doc = e.TheObject?.Document;
        if (!ShouldCapture(doc))
            return;

        Enqueue(
            doc!.RuntimeSerialNumber,
            new DeleteFact(
                e.ObjectId,
                e.TheObject?.Attributes.GetUserString(AssemblyManagerConstants.LinkNodeIdUserString) ?? string.Empty,
                (e.TheObject?.Attributes.GetGroupList() ?? Array.Empty<int>()).ToImmutableArray()));
    }

    private void OnAddRhinoObject(object? sender, RhinoObjectEventArgs e)
    {
        var rhinoObject = e.TheObject;
        var doc = rhinoObject?.Document;
        if (!ShouldCapture(doc))
            return;

        var attributes = rhinoObject!.Attributes;
        Enqueue(
            doc!.RuntimeSerialNumber,
            new AddedFact(
                e.ObjectId,
                attributes.GetUserString(AssemblyManagerConstants.LinkNodeIdUserString) ?? string.Empty,
                (attributes.GetGroupList() ?? Array.Empty<int>()).ToImmutableArray(),
                ScaleGeometrySnapshot.Capture(rhinoObject.Geometry)));
    }

    private void OnModifyObjectAttributes(object? sender, RhinoModifyObjectAttributesEventArgs e)
    {
        var doc = e.Document;
        if (!ShouldCapture(doc))
            return;

        CaptureGroupMembershipChanges(doc, e);

        if (MaterialAttributeKeys.Any(key => !string.Equals(
                e.OldAttributes?.GetUserString(key) ?? string.Empty,
                e.NewAttributes?.GetUserString(key) ?? string.Empty,
                StringComparison.Ordinal)))
        {
            Enqueue(doc.RuntimeSerialNumber, new MaterialChangedFact(
                e.RhinoObject.Id,
                ScaleGeometrySnapshot.Capture(e.RhinoObject.Geometry, e.OldAttributes)));
        }

        var oldNodeId = e.OldAttributes?.GetUserString(AssemblyManagerConstants.LinkNodeIdUserString) ?? string.Empty;
        var newNodeId = e.NewAttributes?.GetUserString(AssemblyManagerConstants.LinkNodeIdUserString) ?? string.Empty;
        var oldAssemblyId = e.OldAttributes?.GetUserString(AssemblyManagerConstants.AssemblyIdUserString) ?? string.Empty;
        var newAssemblyId = e.NewAttributes?.GetUserString(AssemblyManagerConstants.AssemblyIdUserString) ?? string.Empty;
        var oldRole = e.OldAttributes?.GetUserString(AssemblyManagerConstants.LinkRoleUserString) ?? string.Empty;
        var newRole = e.NewAttributes?.GetUserString(AssemblyManagerConstants.LinkRoleUserString) ?? string.Empty;

        if (string.Equals(oldNodeId, newNodeId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(oldAssemblyId, newAssemblyId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(oldRole, newRole, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Enqueue(doc.RuntimeSerialNumber, new MetadataChangedFact(e.RhinoObject.Id, oldNodeId, newNodeId));
    }

    private void CaptureGroupMembershipChanges(RhinoDoc doc, RhinoModifyObjectAttributesEventArgs e)
    {
        var oldGroups = (e.OldAttributes?.GetGroupList() ?? Array.Empty<int>()).ToHashSet();
        var newGroups = (e.NewAttributes?.GetGroupList() ?? Array.Empty<int>()).ToHashSet();
        if (oldGroups.SetEquals(newGroups))
            return;

        foreach (var groupIndex in oldGroups.Concat(newGroups).Distinct())
        {
            var group = doc.Groups.FindIndex(groupIndex);
            var members = (doc.Objects.FindByGroup(groupIndex) ?? Array.Empty<RhinoObject>())
                .Select(member => member.Id)
                .Where(id => id != Guid.Empty && id != e.RhinoObject.Id)
                .ToHashSet();
            if (newGroups.Contains(groupIndex))
                members.Add(e.RhinoObject.Id);

            Enqueue(
                doc.RuntimeSerialNumber,
                new GroupChangedFact(
                    group?.Id ?? Guid.Empty,
                    groupIndex,
                    group?.Name ?? string.Empty,
                    "MembershipModified",
                    members.ToImmutableArray()));
        }
    }

    private void OnGroupTableEvent(object? sender, GroupTableEventArgs e)
    {
        var doc = e.Document;
        if (!ShouldCapture(doc))
            return;

        var state = e.NewState ?? e.OldState;
        var groupId = state?.Id ?? Guid.Empty;
        var groupName = state?.Name ?? string.Empty;
        var members = e.GroupIndex >= 0
            ? (doc.Objects.FindByGroup(e.GroupIndex) ?? Array.Empty<RhinoObject>())
                .Select(obj => obj.Id)
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToImmutableArray()
            : ImmutableArray<Guid>.Empty;

        Enqueue(
            doc.RuntimeSerialNumber,
            new GroupChangedFact(groupId, e.GroupIndex, groupName, e.EventType.ToString(), members));
    }

    private void OnUnitsChangedWithScaling(object? sender, UnitsChangedWithScalingEventArgs e)
    {
        var documentSerialNumber = e.DocumentSerialNumber;
        var doc = e.Document ?? RhinoDoc.FromRuntimeSerialNumber(documentSerialNumber);
        if (documentSerialNumber == 0 || !ShouldCapture() ||
            (doc is not null && UndoOrRedoIsActive(doc)))
        {
            return;
        }

        var linkedObjectIds = ImmutableHashSet<Guid>.Empty;
        var linkedNodeKeys = ImmutableHashSet<GraphItemKey>.Empty;
        var partFingerprintSeeds = ImmutableDictionary<Guid, UnitScaleFingerprintSeed>.Empty;
        if (doc is not null)
        {
            try
            {
                var nodeEntries = _repository.Load(doc).Assemblies
                    .SelectMany(assembly => assembly.LinkGraph.Nodes.Select(node =>
                        (AssemblyId: assembly.Id, Node: node)))
                    .ToList();
                linkedObjectIds = nodeEntries
                    .Select(entry => entry.Node.ObjectId)
                    .Where(objectId => objectId != Guid.Empty)
                    .Distinct()
                    .Where(objectId => doc.Objects.FindId(objectId) is { IsDeleted: false })
                    .ToImmutableHashSet();
                linkedNodeKeys = nodeEntries
                    .Select(entry => new GraphItemKey(entry.AssemblyId, entry.Node.Id))
                    .ToImmutableHashSet();

                if (IsUsableGlobalScale(e.Scale))
                {
                    var seedBuilder = ImmutableDictionary.CreateBuilder<Guid, UnitScaleFingerprintSeed>();
                    foreach (var objectId in nodeEntries
                                 .Where(entry =>
                                     entry.Node.PartId != Guid.Empty &&
                                     linkedObjectIds.Contains(entry.Node.ObjectId))
                                 .Select(entry => entry.Node.ObjectId)
                                 .Distinct())
                    {
                        var sourceObject = doc.Objects.FindId(objectId);
                        if (sourceObject is null ||
                            !_fingerprints.TryCreatePartCandidate(sourceObject, out var candidate, out _) ||
                            candidate.Geometry is not Brep sourceBrep)
                        {
                            continue;
                        }

                        var scaledBrep = sourceBrep.DuplicateBrep();
                        if (!scaledBrep.Transform(Transform.Scale(Point3d.Origin, e.Scale)))
                            continue;

                        seedBuilder[objectId] = new UnitScaleFingerprintSeed(
                            candidate.Fingerprint,
                            _fingerprints.CreatePartFingerprint(scaledBrep),
                            _fingerprints.GetMaterialThickness(sourceBrep),
                            _fingerprints.GetMaterialThickness(scaledBrep));
                    }

                    partFingerprintSeeds = seedBuilder.ToImmutable();
                }
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine("Gazelle could not snapshot linked objects for a unit change: {0}", ex.Message);
            }
        }

        Enqueue(
            documentSerialNumber,
            new GlobalScaleFact(e.Scale, linkedObjectIds, linkedNodeKeys, partFingerprintSeeds));
    }

    private void OnCloseDocument(object? sender, DocumentEventArgs e)
    {
        if (e.Document is { } doc)
            _linkSafety?.ForgetDocument(doc);
        var serialNumber = e.Document?.RuntimeSerialNumber ?? e.DocumentSerialNumber;
        _queues.TryRemove(serialNumber, out _);
        _activeCommandBatches.TryRemove(serialNumber, out _);
        _undoSnapshots.TryRemove(serialNumber, out _);
        _undoHealthIssues.TryRemove(serialNumber, out _);
        _deferredDocuments.TryRemove(serialNumber, out _);
    }

    private void OnEndOpenDocument(object? sender, DocumentOpenEventArgs e)
    {
        if (RhinoDoc.FromRuntimeSerialNumber(e.DocumentSerialNumber) is { } doc)
            _linkSafety?.ObserveDocument(doc);
        if (_linkSafety?.IsEnabled != false)
            Enqueue(e.DocumentSerialNumber, new PendingUpdateFact());
    }

    private void OnBeginCommand(object? sender, CommandEventArgs e)
    {
        if (_linkSafety?.IsEnabled == false)
            return;
        var serialNumber = e.DocumentRuntimeSerialNumber;
        if (serialNumber != 0)
            _activeCommandBatches.GetOrAdd(serialNumber, _ => new ConcurrentStack<Guid>()).Push(Guid.NewGuid());
    }

    private void OnEndCommand(object? sender, CommandEventArgs e)
    {
        var serialNumber = e.DocumentRuntimeSerialNumber;
        if (serialNumber == 0 || !_activeCommandBatches.TryGetValue(serialNumber, out var batches))
            return;

        batches.TryPop(out _);
        if (batches.IsEmpty)
            _activeCommandBatches.TryRemove(serialNumber, out _);
    }

    private void OnUndoRedo(object? sender, UndoRedoEventArgs e)
    {
        if (_linkSafety?.IsEnabled == false)
            return;
        if (e.IsBeginUndo || e.IsBeginRedo)
        {
            var affectedDocuments = RhinoDoc.OpenDocuments(true)
                .Where(UndoOrRedoIsActive)
                .ToList();
            if (affectedDocuments.Count == 0 && RhinoDoc.ActiveDoc is { } activeDoc)
                affectedDocuments.Add(activeDoc);

            foreach (var doc in affectedDocuments)
            {
                _queues.TryRemove(doc.RuntimeSerialNumber, out _);
                try
                {
                    _undoSnapshots[doc.RuntimeSerialNumber] = CaptureUndoSnapshot(_repository.Load(doc));
                }
                catch (Exception ex)
                {
                    RhinoApp.WriteLine("Gazelle could not snapshot links before undo/redo: {0}", ex.Message);
                }
            }

            return;
        }

        if (!e.IsEndUndo && !e.IsEndRedo)
            return;

        foreach (var entry in _undoSnapshots.ToArray())
        {
            _undoSnapshots.TryRemove(entry.Key, out _);
            if (RhinoDoc.FromRuntimeSerialNumber(entry.Key) is { } doc)
                Enqueue(doc.RuntimeSerialNumber, new ReconcileFact(entry.Value));
        }
    }

    private void OnIdle(object? sender, EventArgs e)
    {
        if (!IsStarted || !ShouldCapture() || Interlocked.Exchange(ref _processingIdle, 1) != 0)
            return;

        try
        {
            var propagateChanges = _automaticPropagationEnabled();
            foreach (var entry in _queues.ToArray())
            {
                var doc = RhinoDoc.FromRuntimeSerialNumber(entry.Key);
                if (doc is null)
                {
                    _queues.TryRemove(entry.Key, out _);
                    _deferredDocuments.TryRemove(entry.Key, out _);
                    continue;
                }

                if (UndoOrRedoIsActive(doc))
                    continue;

                var facts = Drain(entry.Value);
                if (facts.Count > 0 ||
                    (propagateChanges && _deferredDocuments.ContainsKey(entry.Key)))
                {
                    ProcessFacts(doc, facts, propagateChanges);
                }
            }
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine("Assembly Manager link-event processing failed: {0}", ex.Message);
        }
        finally
        {
            Volatile.Write(ref _processingIdle, 0);
        }
    }

    private void ProcessFacts(
        RhinoDoc doc,
        IReadOnlyList<LinkEventFact> facts,
        bool propagateChanges,
        Guid? selectedAssemblyId = null,
        bool prepareForManualUpdate = false)
    {
        facts = ResolveUnavailableReplacementIds(doc, facts);
        var fullStore = _repository.Load(doc);
        // Suspended assemblies with untracked changes must not have their stale matrices
        // advanced or their geometry refreshed. Keep the complete persisted store intact;
        // this working view shares only the safe assembly records with it.
        var store = _linkSafety is null ? fullStore : new AssemblyStore
        {
            SchemaVersion = fullStore.SchemaVersion,
            Assemblies = fullStore.Assemblies.Where(assembly => !_linkSafety.IsAssemblyBlocked(doc, assembly.Id)).ToList(),
            MaterialLibraryCache = fullStore.MaterialLibraryCache,
            ActionHistory = fullStore.ActionHistory
        };
        if (facts.Count > 0 && facts.All(fact => fact is ReconcileFact))
        {
            foreach (var fact in facts.OfType<ReconcileFact>())
                InspectAfterUndoRedo(doc, store, fact.Snapshot);
            // Restoring a pending marker with Undo must not immediately replay the update
            // and destroy Rhino's redo chain. A new edit or Update Assembly can resume it.
            _deferredDocuments.TryRemove(doc.RuntimeSerialNumber, out _);
            return;
        }
        var openConflictsBefore = CountOpenConflicts(store);
        var replacementMap = BuildReplacementMap(facts);
        var replacementCompanionIndexes = FindReplacementCompanionIndexes(doc, store, facts);
        var replacementObjectIds = replacementMap.Keys
            .Concat(replacementMap.Values)
            .Where(id => id != Guid.Empty)
            .ToHashSet();
        var transformReplacementClassification = ClassifyTransformProducedReplacements(facts);
        var transformProducedReplacementIndexes = transformReplacementClassification.ReplacementIndexes;
        var globalScaleValues = facts.OfType<GlobalScaleFact>()
            .Select(fact => fact.Scale)
            .Where(IsUsableGlobalScale)
            .ToList();
        var globalScaleTransformIndexes = FindGlobalScaleTransformIndexes(facts);
        var globalScaleReplacementIndexes = FindGlobalScaleReplacementIndexes(facts);
        var canonicalFingerprintExclusions = facts.Select((fact, index) => (fact, index))
            .Where(item => item.fact is ReplaceFact &&
                           !globalScaleReplacementIndexes.Contains(item.index) &&
                           !transformProducedReplacementIndexes.Contains(item.index))
            .SelectMany(item =>
            {
                var replacement = (ReplaceFact)item.fact;
                return new[] { replacement.OldObjectId, replacement.NewObjectId };
            })
            .Concat(facts.OfType<GeometryChangedFact>().SelectMany(fact => fact.ObjectIds))
            .Concat(facts.Select((fact, index) => (fact, index))
                .Where(item => item.fact is TransformFact transformFact &&
                               !globalScaleTransformIndexes.Contains(item.index) &&
                               !transformFact.ObjectsWillBeCopied &&
                               transformFact.Transform.RigidType != TransformRigidType.Rigid)
                .SelectMany(item => ((TransformFact)item.fact).ObjectIds))
            .Select(objectId => ResolveReplacementId(objectId, replacementMap))
            .Where(objectId => objectId != Guid.Empty)
            .ToHashSet();

        var refreshRequests = new HashSet<Guid>();
        var originalAssemblyEditRequests = new Dictionary<GraphItemKey, OriginalAssemblyEditEvidence>();
        RestoreDeferredRequests(store, refreshRequests, originalAssemblyEditRequests);
        var changed = false;

        // A newly created linked object already receives its final edge transform from the
        // creation service. Ignore transform notifications captured earlier in that command,
        // otherwise the same placement would be composed into the edge twice.
        var newlyLinkedObjectIds = facts.Select((fact, index) => (fact, index))
            .Where(item =>
                item.fact is AddedFact addedFact &&
                Guid.TryParse(addedFact.LinkNodeId, out _) &&
                !replacementCompanionIndexes.Contains(item.index) &&
                !replacementObjectIds.Contains(addedFact.ObjectId))
            .Select(item => ResolveReplacementId(((AddedFact)item.fact).ObjectId, replacementMap))
            .Concat(facts.OfType<MetadataChangedFact>()
                .Where(fact => !Guid.TryParse(fact.OldLinkNodeId, out _) && Guid.TryParse(fact.NewLinkNodeId, out _))
                .Select(fact => ResolveReplacementId(fact.ObjectId, replacementMap)))
            .ToHashSet();
        var geometryChangedObjectIdsByCapture = facts.OfType<GeometryChangedFact>()
            .GroupBy(fact => fact.CaptureId)
            .ToDictionary(
                group => group.Key,
                group => group.SelectMany(fact => fact.ObjectIds)
                    .Select(id => ResolveReplacementId(id, replacementMap))
                    .Where(id => id != Guid.Empty)
                    .ToHashSet());

        // Establish the final UUID mapping first. Rhino can emit A->B->C replacement chains,
        // followed by transforms or grip edits that name any point in that chain.
        for (var index = 0; index < facts.Count; index++)
        {
            if (facts[index] is ReplaceFact replaceFact)
            {
                changed |= ApplyReplaceFact(
                    store,
                    replaceFact,
                    transformProducedReplacementIndexes.Contains(index) ||
                    globalScaleReplacementIndexes.Contains(index),
                    refreshRequests,
                    originalAssemblyEditRequests,
                    index);
            }
        }

        for (var index = 0; index < facts.Count; index++)
        {
            if (replacementCompanionIndexes.Contains(index))
                continue;

            switch (facts[index])
            {
                case TransformFact transformFact when !globalScaleTransformIndexes.Contains(index):
                    {
                        transformReplacementClassification.ShapeObjectIdsByTransformIndex.TryGetValue(
                            index,
                            out var shapeObjectIds);
                        geometryChangedObjectIdsByCapture.TryGetValue(
                            transformFact.CaptureId,
                            out var geometryChangedObjectIds);
                        var existingObjectIds = transformFact.ObjectIds
                            .Where(id => shapeObjectIds is null || !shapeObjectIds.Contains(id))
                            .Select(id => ResolveReplacementId(id, replacementMap))
                            .Where(id => geometryChangedObjectIds is null || !geometryChangedObjectIds.Contains(id))
                            .Where(id => !newlyLinkedObjectIds.Contains(id))
                            .Distinct()
                            .ToImmutableArray();
                        if (!existingObjectIds.IsDefaultOrEmpty)
                        {
                            var originalGeometryByObjectId = transformFact.OriginalGeometryByObjectId
                                .GroupBy(entry => ResolveReplacementId(entry.Key, replacementMap))
                                .Where(group => group.Key != Guid.Empty)
                                .ToImmutableDictionary(group => group.Key, group => group.Last().Value);
                            changed |= ApplyTransformFact(
                                store,
                                transformFact with
                                {
                                    ObjectIds = existingObjectIds,
                                    OriginalGeometryByObjectId = originalGeometryByObjectId
                                },
                                refreshRequests,
                                originalAssemblyEditRequests,
                                index);
                        }

                        break;
                    }
                case GeometryChangedFact geometryChangedFact:
                    {
                        var objectIds = geometryChangedFact.ObjectIds
                            .Select(id => ResolveReplacementId(id, replacementMap))
                            .Distinct()
                            .ToImmutableArray();
                        var originalGeometryByObjectId = geometryChangedFact.OriginalGeometryByObjectId
                            .GroupBy(entry => ResolveReplacementId(entry.Key, replacementMap))
                            .Where(group => group.Key != Guid.Empty)
                            .ToImmutableDictionary(group => group.Key, group => group.Last().Value);
                        changed |= ApplyGeometryChangedFact(
                            store,
                            geometryChangedFact with
                            {
                                ObjectIds = objectIds,
                                OriginalGeometryByObjectId = originalGeometryByObjectId
                            },
                            refreshRequests,
                            originalAssemblyEditRequests,
                            index);
                        break;
                    }
                case GlobalScaleFact globalScaleFact:
                    {
                        var scaledObjectIds = globalScaleFact.LinkedObjectIds
                            .Select(id => ResolveReplacementId(id, replacementMap))
                            .Where(id => id != Guid.Empty)
                            .ToHashSet();
                        var scaledFingerprintSeeds = globalScaleFact.PartFingerprintSeeds
                            .GroupBy(entry => ResolveReplacementId(entry.Key, replacementMap))
                            .Where(group => group.Key != Guid.Empty)
                            .ToDictionary(group => group.Key, group => group.Last().Value);
                        changed |= ApplyGlobalScaleFact(
                            doc,
                            store,
                            globalScaleFact,
                            scaledObjectIds,
                            globalScaleFact.LinkedNodeKeys,
                            scaledFingerprintSeeds,
                            canonicalFingerprintExclusions);
                        break;
                    }
                case DeleteFact deleteFact:
                    changed |= ApplyDeleteFact(
                        doc,
                        store,
                        deleteFact with { ObjectId = ResolveReplacementId(deleteFact.ObjectId, replacementMap) });
                    break;
                case AddedFact addedFact:
                    changed |= ApplyAddedFact(store, addedFact);
                    break;
                case MetadataChangedFact metadataChangedFact:
                    changed |= ApplyMetadataChangedFact(
                        doc,
                        store,
                        metadataChangedFact with
                        {
                            ObjectId = ResolveReplacementId(metadataChangedFact.ObjectId, replacementMap)
                        });
                    break;
                case MaterialChangedFact materialChangedFact:
                    changed |= ApplyMaterialChangedFact(
                        store,
                        materialChangedFact with
                        {
                            ObjectId = ResolveReplacementId(materialChangedFact.ObjectId, replacementMap)
                        },
                        refreshRequests,
                        originalAssemblyEditRequests,
                        index);
                    break;
                case ReconcileFact reconcileFact:
                    InspectAfterUndoRedo(doc, store, reconcileFact.Snapshot);
                    break;
            }
        }

        // Group creation and membership assignment can emit several table events in one
        // command. Only the final immutable snapshot represents the state to reconcile.
        foreach (var fact in facts.OfType<GroupChangedFact>()
                     .GroupBy(GroupFactKey)
                     .Select(group => group.Last()))
            changed |= ApplyGroupChangedFact(store, fact);

        changed |= RejectStructuralGeometryBatches(
            doc,
            facts,
            replacementCompanionIndexes,
            replacementMap,
            store,
            refreshRequests,
            originalAssemblyEditRequests);
        changed |= RejectConcurrentSourceAndOriginalEdits(
            store,
            refreshRequests,
            originalAssemblyEditRequests);
        changed |= RejectConcurrentOriginalAssemblyEdits(store, originalAssemblyEditRequests);
        changed |= RejectOutOfSyncOriginalAssemblyEdits(
            doc,
            store,
            originalAssemblyEditRequests);

        var reconciliationOnly = facts.Count > 0 && facts.All(fact => fact is ReconcileFact);
        // An accepted structural edit requires the explicit Update Assembly action even
        // when automatic geometry updates are enabled. Keep tracking facts while staged.
        var stagedAssemblyIds = store.Assemblies.Where(assembly => assembly.PendingComponentUpdates.Count > 0)
            .Select(assembly => assembly.Id).ToHashSet();
        var dispatchOriginals = propagateChanges && !reconciliationOnly
            ? originalAssemblyEditRequests.Keys
                .Where(key => !stagedAssemblyIds.Contains(key.AssemblyId) &&
                    (!selectedAssemblyId.HasValue || key.AssemblyId == selectedAssemblyId.Value))
                .ToHashSet()
            : new HashSet<GraphItemKey>();
        var dispatchSources = propagateChanges && !reconciliationOnly
            ? store.Assemblies
                .Where(assembly => !stagedAssemblyIds.Contains(assembly.Id) &&
                    (!selectedAssemblyId.HasValue || assembly.Id == selectedAssemblyId.Value))
                .SelectMany(assembly => assembly.LinkGraph.Nodes)
                .Where(node => string.Equals(node.Role, AssemblyLinkRoles.Source, StringComparison.OrdinalIgnoreCase)
                    && refreshRequests.Contains(node.ObjectId))
                .Select(node => node.ObjectId)
                .ToHashSet()
            : new HashSet<Guid>();
        if (!reconciliationOnly)
        {
            changed |= PersistDeferredRequests(
                store,
                refreshRequests.Except(dispatchSources).ToHashSet(),
                originalAssemblyEditRequests
                    .Where(entry => !dispatchOriginals.Contains(entry.Key))
                    .ToDictionary(entry => entry.Key, entry => entry.Value));
        }

        var undoRecord = 0u;
        if (!reconciliationOnly &&
            (changed || refreshRequests.Count > 0 || originalAssemblyEditRequests.Count > 0) &&
            doc.UndoRecordingEnabled &&
            !doc.UndoRecordingIsActive)
        {
            undoRecord = doc.BeginUndoRecord(propagateChanges ? "Gazelle Auto Update" : "Gazelle Track Assembly Changes");
        }

        try
        {
            if (changed)
            {
                using var mutation = AssemblyLinkMutationGate.Enter();
                SynchronizeTransformMetadata(
                    doc,
                    store,
                    facts.OfType<TransformFact>()
                        .Where(fact => !fact.ObjectsWillBeCopied)
                        .SelectMany(fact => fact.ObjectIds)
                        .Concat(facts.OfType<ReplaceFact>().SelectMany(fact => new[] { fact.OldObjectId, fact.NewObjectId }))
                        .Select(id => ResolveReplacementId(id, replacementMap))
                        .ToHashSet(),
                    globalScaleValues.Count > 0);
                _repository.Save(doc, fullStore);
            }

            DispatchOriginalAssemblyEditRequests(doc, dispatchOriginals, refreshDescendants: !prepareForManualUpdate);
            if (!prepareForManualUpdate)
                DispatchRefreshRequests(doc, dispatchSources);

            // Placement-only batches do not dispatch geometry work. Keep the already
            // normalized store instead of deserializing the entire assembly graph again.
            var finalStore = dispatchOriginals.Count > 0 || (!prepareForManualUpdate && dispatchSources.Count > 0)
                ? _repository.Load(doc)
                : store;
            var activeFinalAssemblies = finalStore.Assemblies
                .Where(assembly => _linkSafety?.IsAssemblyBlocked(doc, assembly.Id) != true).ToList();
            if (activeFinalAssemblies.Where(assembly => assembly.PendingComponentUpdates.Count == 0)
                .SelectMany(assembly => assembly.LinkGraph.Nodes).Any(node =>
                    HasPendingUpdate(node, PendingSourceUpdateKey) || HasPendingUpdate(node, PendingOriginalUpdateKey)))
            {
                _deferredDocuments[doc.RuntimeSerialNumber] = true;
            }
            else
            {
                _deferredDocuments.TryRemove(doc.RuntimeSerialNumber, out _);
            }
            var newConflictCount = CountOpenConflicts(new AssemblyStore { Assemblies = activeFinalAssemblies }) - openConflictsBefore;
            if (newConflictCount > 0)
            {
                RhinoApp.WriteLine(
                    "Gazelle detected {0} linked-assembly change(s) that need review. Open Assembly Manager to see the link issue count.",
                    newConflictCount);
            }
        }
        finally
        {
            if (undoRecord != 0)
                doc.EndUndoRecord(undoRecord);
        }
    }

    private void SynchronizeTransformMetadata(
        RhinoDoc doc,
        AssemblyStore store,
        ISet<Guid> changedObjectIds,
        bool synchronizeAll)
    {
        if (!synchronizeAll && changedObjectIds.Count == 0)
            return;

        foreach (var assembly in store.Assemblies)
        {
            var nodesById = assembly.LinkGraph.Nodes.ToDictionary(node => node.Id);
            foreach (var edge in assembly.LinkGraph.Edges)
            {
                if (!nodesById.TryGetValue(edge.ParentNodeId, out var parent) ||
                    !nodesById.TryGetValue(edge.ChildNodeId, out var child))
                {
                    continue;
                }

                // Moving one copied component changes only edges incident to its members
                // (including edges to flat children). Do not rewrite every unrelated
                // object's attributes and grow the undo record for an otherwise small move.
                if (!synchronizeAll && !changedObjectIds.Contains(parent.ObjectId) &&
                    !changedObjectIds.Contains(child.ObjectId))
                    continue;

                _lineage.ApplyObjectMetadata(doc, assembly, child, edge, parent.ObjectId);
            }
        }
    }

    private static bool HasPendingUpdate(AssemblyLinkNodeRecord node, string key)
    {
        return node.Metadata.TryGetValue(key, out var value)
            && bool.TryParse(value, out var pending)
            && pending;
    }

    private static string CaptureMaterialSignature(ObjectAttributes attributes)
    {
        // Generated originals normalize stock assignments to the parent material. Compare
        // material type, not stock-shape metadata, so that normal normalization is not
        // mistaken for a stale source material. All six attributes still trigger events.
        var categoryId = MaterialAssignment.GetCategorizationMaterialId(attributes);
        var name = MaterialAssignment.GetBaseMaterialName(attributes);
        if (string.IsNullOrWhiteSpace(name))
            name = MaterialAssignment.GetMaterialName(attributes);
        // Length-prefix values so user-supplied names cannot create delimiter collisions.
        return $"{categoryId.Length}:{categoryId}{name.Length}:{name}";
    }

    private static void RestoreDeferredRequests(
        AssemblyStore store,
        ISet<Guid> sourceRequests,
        IDictionary<GraphItemKey, OriginalAssemblyEditEvidence> originalRequests)
    {
        foreach (var assembly in store.Assemblies)
        {
            foreach (var node in assembly.LinkGraph.Nodes)
            {
                if (string.Equals(node.Role, AssemblyLinkRoles.Source, StringComparison.OrdinalIgnoreCase)
                    && HasPendingUpdate(node, PendingSourceUpdateKey))
                {
                    sourceRequests.Add(node.ObjectId);
                }
                else if (string.Equals(node.Role, AssemblyLinkRoles.OriginalAssembly, StringComparison.OrdinalIgnoreCase)
                    && HasPendingUpdate(node, PendingOriginalUpdateKey))
                {
                    // A marker is written only after validating the first edit against its
                    // pre-edit geometry. Later edits while paused must not compare against
                    // the intentionally unchanged source as if this were a fresh edit.
                    originalRequests[new GraphItemKey(assembly.Id, node.Id)] = new OriginalAssemblyEditEvidence(
                        ScaleGeometrySnapshot.EmptyEvidence() with
                        {
                            MaterialSignature = node.Metadata.GetValueOrDefault(PendingOriginalMaterialKey)
                        }, -1, PreviouslyValidated: true);
                }
            }
        }
    }

    private static bool PersistDeferredRequests(
        AssemblyStore store,
        ISet<Guid> sourceRequests,
        IReadOnlyDictionary<GraphItemKey, OriginalAssemblyEditEvidence> originalRequests)
    {
        var changed = false;
        foreach (var assembly in store.Assemblies)
        {
            foreach (var node in assembly.LinkGraph.Nodes)
            {
                changed |= SetPendingUpdate(node, PendingSourceUpdateKey,
                    string.Equals(node.Role, AssemblyLinkRoles.Source, StringComparison.OrdinalIgnoreCase)
                    && sourceRequests.Contains(node.ObjectId));
                var originalPending = string.Equals(node.Role, AssemblyLinkRoles.OriginalAssembly, StringComparison.OrdinalIgnoreCase)
                    && originalRequests.TryGetValue(new GraphItemKey(assembly.Id, node.Id), out _);
                changed |= SetPendingUpdate(node, PendingOriginalUpdateKey, originalPending);
                if (originalPending && originalRequests[new GraphItemKey(assembly.Id, node.Id)].PreEditGeometry.MaterialSignature is { } signature)
                {
                    if (!node.Metadata.TryGetValue(PendingOriginalMaterialKey, out var existingSignature)
                        || !string.Equals(existingSignature, signature, StringComparison.Ordinal))
                    {
                        node.Metadata[PendingOriginalMaterialKey] = signature;
                        changed = true;
                    }
                }
                else if (!originalPending)
                {
                    changed |= node.Metadata.Remove(PendingOriginalMaterialKey);
                }
            }
        }
        return changed;
    }

    private static bool SetPendingUpdate(AssemblyLinkNodeRecord node, string key, bool pending)
    {
        if (!pending)
            return node.Metadata.Remove(key);
        if (HasPendingUpdate(node, key))
            return false;
        node.Metadata[key] = bool.TrueString;
        return true;
    }

    private static bool ApplyMaterialChangedFact(
        AssemblyStore store,
        MaterialChangedFact fact,
        ISet<Guid> sourceRequests,
        IDictionary<GraphItemKey, OriginalAssemblyEditEvidence> originalRequests,
        int factIndex)
    {
        var changed = false;
        foreach (var assembly in store.Assemblies)
        {
            foreach (var node in assembly.LinkGraph.Nodes.Where(node => node.ObjectId == fact.ObjectId))
            {
                if (string.Equals(node.Role, AssemblyLinkRoles.Source, StringComparison.OrdinalIgnoreCase))
                {
                    sourceRequests.Add(node.ObjectId);
                }
                else if (string.Equals(node.Role, AssemblyLinkRoles.OriginalAssembly, StringComparison.OrdinalIgnoreCase))
                {
                    QueueOriginalAssemblyEdit(originalRequests, new GraphItemKey(assembly.Id, node.Id),
                        fact.Geometry, factIndex);
                }
                else
                {
                    changed |= QuarantineNode(assembly, node);
                    changed |= AddConflict(assembly, AssemblyLinkConflictTypes.DerivedGeometryChanged, node.Id,
                        "A generated copied or flat object's material was edited directly. Use Update Assembly to rebuild its material from the source; edit the design source or ORIGINAL ASSEMBLIES to change the part material.",
                        new[] { node.ObjectId }, $"derived-geometry:{node.Id}");
                }
                node.UpdatedAt = DateTimeOffset.UtcNow;
                assembly.LinkGraph.UpdatedAt = node.UpdatedAt;
                assembly.UpdatedAt = node.UpdatedAt;
                changed = true;
            }
        }
        return changed;
    }

    private static int CountOpenConflicts(AssemblyStore store)
    {
        return store.Assemblies.Sum(assembly => assembly.LinkGraph.Conflicts.Count(conflict =>
            string.Equals(conflict.Status, AssemblyLinkStatuses.Open, StringComparison.OrdinalIgnoreCase)));
    }

    private static IReadOnlyList<LinkEventFact> ResolveUnavailableReplacementIds(
        RhinoDoc doc,
        IReadOnlyList<LinkEventFact> facts)
    {
        List<LinkEventFact>? normalized = null;
        for (var index = 0; index < facts.Count; index++)
        {
            if (facts[index] is not ReplaceFact replacement ||
                !replacement.NewObjectIdWasUnavailable)
            {
                continue;
            }

            var resolved = replacement;
            if (Guid.TryParse(replacement.LinkNodeId, out _))
            {
                var metadataRestoredObjectIds = facts.OfType<MetadataChangedFact>()
                    .Where(metadata => string.Equals(
                        metadata.NewLinkNodeId,
                        replacement.LinkNodeId,
                        StringComparison.OrdinalIgnoreCase))
                    .Select(metadata => metadata.ObjectId)
                    .ToHashSet();
                var sawExpectedDelete = false;
                var expectedDeleteWasCommandless = false;
                for (var candidateIndex = index + 1; candidateIndex < facts.Count; candidateIndex++)
                {
                    switch (facts[candidateIndex])
                    {
                        case DeleteFact deleteFact when !sawExpectedDelete &&
                                                        deleteFact.ObjectId == replacement.OldObjectId:
                            sawExpectedDelete = true;
                            expectedDeleteWasCommandless = deleteFact.CommandBatchId == Guid.Empty;
                            break;
                        case AddedFact addedFact when sawExpectedDelete:
                            {
                                var metadataMatches = string.Equals(
                                                          addedFact.LinkNodeId,
                                                          replacement.LinkNodeId,
                                                          StringComparison.OrdinalIgnoreCase) ||
                                                      metadataRestoredObjectIds.Contains(addedFact.ObjectId);
                                var sameCommand = replacement.CommandBatchId != Guid.Empty &&
                                                  addedFact.CommandBatchId == replacement.CommandBatchId;
                                var commandlessDocumentedSequence = replacement.CommandBatchId == Guid.Empty &&
                                                                    expectedDeleteWasCommandless &&
                                                                    addedFact.CommandBatchId == Guid.Empty;
                                if (addedFact.ObjectId != Guid.Empty &&
                                    (metadataMatches || sameCommand || commandlessDocumentedSequence))
                                {
                                    resolved = resolved with
                                    {
                                        NewObjectId = addedFact.ObjectId,
                                        NewGeometry = addedFact.Geometry
                                    };
                                }

                                // Rhino documents Replace -> Delete old -> Add new. For commandless
                                // callers, accept only this first structural Add after the exact Delete;
                                // never scan past another structural mutation looking for a match.
                                candidateIndex = facts.Count;
                                break;
                            }
                        case DeleteFact:
                        case AddedFact:
                        case ReplaceFact:
                        case ReconcileFact:
                            candidateIndex = facts.Count;
                            break;
                    }
                }
            }

            if (!resolved.NewGeometry.HasGeometryEvidence &&
                doc.Objects.FindId(resolved.NewObjectId) is { } finalObject)
            {
                resolved = resolved with
                {
                    NewGeometry = ScaleGeometrySnapshot.Capture(finalObject.Geometry)
                };
            }

            normalized ??= facts.ToList();
            normalized[index] = resolved;
        }

        return normalized ?? facts;
    }

    private static Dictionary<Guid, Guid> BuildReplacementMap(IReadOnlyList<LinkEventFact> facts)
    {
        var result = new Dictionary<Guid, Guid>();
        foreach (var fact in facts.OfType<ReplaceFact>())
        {
            if (fact.OldObjectId == Guid.Empty || fact.NewObjectId == Guid.Empty ||
                fact.OldObjectId == fact.NewObjectId)
            {
                continue;
            }

            result[fact.OldObjectId] = fact.NewObjectId;
        }

        foreach (var oldObjectId in result.Keys.ToList())
            result[oldObjectId] = ResolveReplacementId(result[oldObjectId], result);
        return result;
    }

    private static Guid ResolveReplacementId(Guid objectId, IReadOnlyDictionary<Guid, Guid> replacements)
    {
        var visited = new HashSet<Guid>();
        while (objectId != Guid.Empty && replacements.TryGetValue(objectId, out var replacement) &&
               replacement != Guid.Empty && replacement != objectId && visited.Add(objectId))
        {
            objectId = replacement;
        }

        return objectId;
    }

    private static HashSet<int> FindReplacementCompanionIndexes(
        RhinoDoc doc,
        AssemblyStore store,
        IReadOnlyList<LinkEventFact> facts)
    {
        var companions = new HashSet<int>();
        var storedObjectIds = store.Assemblies
            .SelectMany(assembly => assembly.LinkGraph.Nodes)
            .Select(node => node.ObjectId)
            .Where(objectId => objectId != Guid.Empty)
            .ToHashSet();
        for (var index = 0; index < facts.Count; index++)
        {
            if (facts[index] is not ReplaceFact replacement)
                continue;

            var foundDelete = false;
            var foundAdd = replacement.NewObjectId == Guid.Empty;
            var replacementStartsFromStoredNode = ResolveObjectIdsBeforeIndex(
                    facts,
                    storedObjectIds,
                    index)
                .Contains(replacement.OldObjectId);
            if (replacementStartsFromStoredNode && replacement.CommandBatchId != Guid.Empty)
            {
                for (var candidateIndex = index - 1; candidateIndex >= 0; candidateIndex--)
                {
                    if (facts[candidateIndex].CommandBatchId != replacement.CommandBatchId ||
                        facts[candidateIndex] is ReplaceFact or ReconcileFact)
                    {
                        break;
                    }

                    if (facts[candidateIndex] is AddedFact leadingAdd &&
                        (leadingAdd.ObjectId == replacement.NewObjectId ||
                         doc.Objects.FindId(leadingAdd.ObjectId) is null))
                    {
                        // History replay can publish a transient Add before its Replace/Delete/Add
                        // sequence. Ignore only an add tied to the persisted object's current
                        // replacement lineage; an unrelated copied duplicate remains a conflict.
                        companions.Add(candidateIndex);
                    }
                }
            }

            for (var candidateIndex = index + 1; candidateIndex < facts.Count; candidateIndex++)
            {
                switch (facts[candidateIndex])
                {
                    case DeleteFact deleteFact when !foundDelete && deleteFact.ObjectId == replacement.OldObjectId:
                        companions.Add(candidateIndex);
                        foundDelete = true;
                        break;
                    case AddedFact addedFact when !foundAdd && addedFact.ObjectId == replacement.NewObjectId:
                        companions.Add(candidateIndex);
                        foundAdd = true;
                        break;
                    case DeleteFact:
                    case AddedFact:
                        break;
                    default:
                        candidateIndex = facts.Count;
                        break;
                }

                if (foundDelete && foundAdd)
                    break;
            }
        }

        return companions;
    }

    private static TransformReplacementClassification ClassifyTransformProducedReplacements(
        IReadOnlyList<LinkEventFact> facts)
    {
        var replacementIndexes = new HashSet<int>();
        var shapeObjectIdsByTransformIndex = new Dictionary<int, HashSet<Guid>>();
        var pendingTransforms = new Dictionary<
            Guid,
            (int FactIndex, Transform Transform, Guid CommandBatchId, bool TransformAlreadyProven)>();
        for (var index = 0; index < facts.Count; index++)
        {
            switch (facts[index])
            {
                case TransformFact transformFact when !transformFact.ObjectsWillBeCopied:
                    foreach (var objectId in transformFact.ObjectIds.Where(objectId => objectId != Guid.Empty))
                        pendingTransforms[objectId] = (
                            index,
                            transformFact.Transform,
                            transformFact.CommandBatchId,
                            false);
                    break;
                case ReplaceFact replaceFact:
                    if (!pendingTransforms.Remove(replaceFact.OldObjectId, out var pendingTransform))
                        break;
                    if (pendingTransform.CommandBatchId != Guid.Empty &&
                        replaceFact.CommandBatchId != Guid.Empty &&
                        pendingTransform.CommandBatchId != replaceFact.CommandBatchId)
                    {
                        break;
                    }

                    // BeforeTransformObjects is necessary but not sufficient evidence of a
                    // whole-object placement edit: subobject Gumball operations can raise the
                    // same event for their owner. Prove that every captured geometry sample was
                    // transformed by the reported matrix before changing relationship edges.
                    if (replaceFact.OldGeometry.MatchesTransformed(
                            replaceFact.NewGeometry,
                            pendingTransform.Transform))
                    {
                        replacementIndexes.Add(index);
                        if (replaceFact.NewObjectId != Guid.Empty)
                        {
                            // Rhino history and API callers can implement one placement as an
                            // A->B->C replacement chain. Once the transform is proven on the first
                            // hop, require later identity hops in that captured lineage to preserve
                            // the transformed geometry rather than treating them as shape edits.
                            pendingTransforms[replaceFact.NewObjectId] = (
                                pendingTransform.FactIndex,
                                Transform.Identity,
                                pendingTransform.CommandBatchId,
                                true);
                        }
                    }
                    else
                    {
                        // A later replacement can be a real shape edit after an already-proven
                        // placement transform. Route that replacement as shape, but do not
                        // retroactively suppress the legitimate relationship-matrix update.
                        if (pendingTransform.TransformAlreadyProven)
                            break;

                        if (!shapeObjectIdsByTransformIndex.TryGetValue(
                                pendingTransform.FactIndex,
                                out var shapeObjectIds))
                        {
                            shapeObjectIds = new HashSet<Guid>();
                            shapeObjectIdsByTransformIndex[pendingTransform.FactIndex] = shapeObjectIds;
                        }

                        shapeObjectIds.Add(replaceFact.OldObjectId);
                    }
                    break;
            }
        }

        return new TransformReplacementClassification(
            replacementIndexes,
            shapeObjectIdsByTransformIndex);
    }

    private static HashSet<int> FindGlobalScaleReplacementIndexes(IReadOnlyList<LinkEventFact> facts)
    {
        var result = new HashSet<int>();
        for (var globalScaleIndex = 0; globalScaleIndex < facts.Count; globalScaleIndex++)
        {
            if (facts[globalScaleIndex] is not GlobalScaleFact globalScaleFact ||
                !IsUsableGlobalScale(globalScaleFact.Scale) ||
                globalScaleFact.LinkedObjectIds.Count == 0)
            {
                continue;
            }

            var pendingObjectIds = ResolveObjectIdsBeforeIndex(
                facts,
                globalScaleFact.LinkedObjectIds,
                globalScaleIndex);
            var operationEnd = NextGlobalScaleIndex(facts, globalScaleIndex + 1);
            for (var index = globalScaleIndex + 1; index < operationEnd && pendingObjectIds.Count > 0; index++)
            {
                if (facts[index] is not ReplaceFact replaceFact ||
                    !pendingObjectIds.Contains(replaceFact.OldObjectId) ||
                    !replaceFact.OldGeometry.MatchesScaled(replaceFact.NewGeometry, globalScaleFact.Scale))
                {
                    continue;
                }

                // The unit event is raised before Rhino scales document objects. Attribute only
                // the first scale-matching replacement for each linked UUID after that event;
                // a later same-factor scripted transform in the idle batch remains independent.
                result.Add(index);
                pendingObjectIds.Remove(replaceFact.OldObjectId);
            }
        }

        return result;
    }

    private static HashSet<int> FindGlobalScaleTransformIndexes(IReadOnlyList<LinkEventFact> facts)
    {
        var result = new HashSet<int>();
        for (var globalScaleIndex = 0; globalScaleIndex < facts.Count; globalScaleIndex++)
        {
            if (facts[globalScaleIndex] is not GlobalScaleFact globalScaleFact ||
                !IsUsableGlobalScale(globalScaleFact.Scale) ||
                globalScaleFact.LinkedObjectIds.Count == 0)
            {
                continue;
            }

            var unitObjectIds = ResolveObjectIdsBeforeIndex(
                facts,
                globalScaleFact.LinkedObjectIds,
                globalScaleIndex);
            if (unitObjectIds.Count == 0)
                continue;

            var operationEnd = NextGlobalScaleIndex(facts, globalScaleIndex + 1);
            for (var index = globalScaleIndex + 1; index < operationEnd; index++)
            {
                // If linked replacements have started, a later transform belongs to a separate
                // operation even when it happens to use the same factor.
                if (facts[index] is ReplaceFact replaceFact &&
                    unitObjectIds.Contains(replaceFact.OldObjectId))
                {
                    break;
                }

                if (facts[index] is not TransformFact transformFact)
                    continue;

                var affectedObjectIds = transformFact.ObjectIds.ToHashSet();
                if (!transformFact.ObjectsWillBeCopied &&
                    unitObjectIds.IsSubsetOf(affectedObjectIds) &&
                    TransformIsOriginScale(transformFact.Transform, globalScaleFact.Scale))
                {
                    result.Add(index);
                }
                break;
            }
        }

        return result;
    }

    private static HashSet<Guid> ResolveObjectIdsBeforeIndex(
        IReadOnlyList<LinkEventFact> facts,
        IEnumerable<Guid> objectIds,
        int exclusiveEndIndex)
    {
        var result = objectIds.Where(objectId => objectId != Guid.Empty).ToHashSet();
        for (var index = 0; index < exclusiveEndIndex; index++)
        {
            if (facts[index] is not ReplaceFact replacement || !result.Remove(replacement.OldObjectId))
                continue;

            if (replacement.NewObjectId != Guid.Empty)
                result.Add(replacement.NewObjectId);
        }

        return result;
    }

    private static int NextGlobalScaleIndex(IReadOnlyList<LinkEventFact> facts, int startIndex)
    {
        for (var index = startIndex; index < facts.Count; index++)
        {
            if (facts[index] is GlobalScaleFact)
                return index;
        }

        return facts.Count;
    }

    private static bool TransformIsOriginScale(Transform transform, double scale)
    {
        var tolerance = Math.Max(Math.Abs(scale), 1.0) * 1e-10;
        return Math.Abs(transform.M00 - scale) <= tolerance &&
               Math.Abs(transform.M11 - scale) <= tolerance &&
               Math.Abs(transform.M22 - scale) <= tolerance &&
               Math.Abs(transform.M01) <= tolerance &&
               Math.Abs(transform.M02) <= tolerance &&
               Math.Abs(transform.M03) <= tolerance &&
               Math.Abs(transform.M10) <= tolerance &&
               Math.Abs(transform.M12) <= tolerance &&
               Math.Abs(transform.M13) <= tolerance &&
               Math.Abs(transform.M20) <= tolerance &&
               Math.Abs(transform.M21) <= tolerance &&
               Math.Abs(transform.M23) <= tolerance &&
               Math.Abs(transform.M30) <= tolerance &&
               Math.Abs(transform.M31) <= tolerance &&
               Math.Abs(transform.M32) <= tolerance &&
               Math.Abs(transform.M33 - 1.0) <= tolerance;
    }

    private static bool IsUsableGlobalScale(double scale)
    {
        return !double.IsNaN(scale) && !double.IsInfinity(scale) && scale > 0.0 &&
               Math.Abs(scale - 1.0) > RhinoMath.ZeroTolerance;
    }

    private bool ApplyGlobalScaleFact(
        RhinoDoc doc,
        AssemblyStore store,
        GlobalScaleFact fact,
        ISet<Guid> scaledObjectIds,
        ISet<GraphItemKey> scaledNodeKeys,
        IReadOnlyDictionary<Guid, UnitScaleFingerprintSeed> scaledFingerprintSeeds,
        ISet<Guid> canonicalFingerprintExclusions)
    {
        if (!IsUsableGlobalScale(fact.Scale))
        {
            return false;
        }

        var scale = Transform.Scale(Point3d.Origin, fact.Scale);
        if (!scale.TryGetInverse(out var inverse))
            return false;

        var changed = false;
        foreach (var assembly in store.Assemblies)
        {
            var assemblyChanged = false;
            var nodesById = assembly.LinkGraph.Nodes.ToDictionary(node => node.Id);
            var scaledNodeIds = scaledNodeKeys
                .Where(key => key.AssemblyId == assembly.Id)
                .Select(key => key.ItemId)
                .ToHashSet();
            var originalPartFingerprints = assembly.Parts.ToDictionary(part => part.Id, part => part.GeometryFingerprint);
            var originalPartThicknesses = assembly.Parts.ToDictionary(part => part.Id, part => part.MaterialThickness);
            foreach (var edge in assembly.LinkGraph.Edges)
            {
                if (!nodesById.TryGetValue(edge.ParentNodeId, out var parentNode) ||
                    !nodesById.TryGetValue(edge.ChildNodeId, out var childNode) ||
                    !scaledNodeIds.Contains(parentNode.Id) ||
                    !scaledNodeIds.Contains(childNode.Id))
                {
                    continue;
                }

                if (!edge.ParentToChildTransform.TryToTransform(out var current))
                {
                    edge.Status = AssemblyLinkStatuses.Conflict;
                    assemblyChanged |= AddConflict(
                        assembly,
                        AssemblyLinkConflictTypes.TransformUnresolved,
                        childNode.Id,
                        "A stored relationship transform is malformed and could not be adjusted for the document unit scale.",
                        new[] { childNode.ObjectId },
                        $"unit-scale-transform:{edge.Id}",
                        edgeId: edge.Id);
                    assemblyChanged = true;
                    continue;
                }

                edge.ParentToChildTransform = TransformRecord.FromTransform(scale * current * inverse);
                edge.UpdatedAt = DateTimeOffset.UtcNow;
                assemblyChanged = true;
            }

            // Part fingerprints and rounded thicknesses are unit-dependent. Advance each
            // category from an occurrence whose exact pre-scale BREP still matches the stored
            // canonical record. This also covers categories already in a shape conflict before
            // the unit event. If no occurrence proves the old canonical shape, fail closed.
            var scaledPartGroups = assembly.LinkGraph.Nodes
                .Where(node =>
                    node.PartId != Guid.Empty &&
                    scaledNodeIds.Contains(node.Id))
                .GroupBy(node => node.PartId);
            foreach (var partGroup in scaledPartGroups)
            {
                var partNodes = partGroup.ToList();
                var part = assembly.Parts.FirstOrDefault(candidate => candidate.Id == partGroup.Key);
                if (part is null ||
                    !originalPartFingerprints.TryGetValue(part.Id, out var originalCanonicalFingerprint) ||
                    !originalPartThicknesses.TryGetValue(part.Id, out var originalCanonicalThickness))
                {
                    continue;
                }

                var canonicalSeeds = partNodes
                    .Select(node => scaledFingerprintSeeds.GetValueOrDefault(node.ObjectId))
                    .Where(seed => seed is not null &&
                                   string.Equals(
                                       originalCanonicalFingerprint,
                                       seed.OriginalFingerprint,
                                       StringComparison.Ordinal) &&
                                   Math.Abs(originalCanonicalThickness - seed.OriginalThickness) <=
                                   RhinoMath.ZeroTolerance)
                    .Cast<UnitScaleFingerprintSeed>()
                    .ToList();
                var scaledFingerprints = canonicalSeeds
                    .Select(seed => seed.ScaledFingerprint)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                var scaledThicknessesAgree = canonicalSeeds.Count > 0 &&
                                             canonicalSeeds.Max(seed => seed.ScaledThickness) -
                                             canonicalSeeds.Min(seed => seed.ScaledThickness) <=
                                             RhinoMath.ZeroTolerance;
                if (scaledFingerprints.Count != 1 || !scaledThicknessesAgree)
                {
                    foreach (var node in partNodes)
                    {
                        assemblyChanged |= AddConflict(
                            assembly,
                            AssemblyLinkConflictTypes.CanonicalUnitScaleUnresolved,
                            node.Id,
                            "Document units changed while no source occurrence could safely prove this part's stored canonical category. Gazelle preserved the existing canonical category instead of guessing its new-unit fingerprint. Recreate or manually recategorize this assembly before relying on quantities.",
                            partNodes.Select(candidate => candidate.ObjectId),
                            $"canonical-unit-scale:{node.Id}");
                    }

                    continue;
                }

                var canonicalSeed = canonicalSeeds[0];
                if (!string.Equals(part.GeometryFingerprint, canonicalSeed.ScaledFingerprint, StringComparison.Ordinal))
                {
                    part.GeometryFingerprint = canonicalSeed.ScaledFingerprint;
                    assemblyChanged = true;
                }

                if (Math.Abs(part.MaterialThickness - canonicalSeed.ScaledThickness) > RhinoMath.ZeroTolerance)
                {
                    part.MaterialThickness = canonicalSeed.ScaledThickness;
                    assemblyChanged = true;
                }

                foreach (var node in partNodes)
                {
                    if (scaledFingerprintSeeds.TryGetValue(node.ObjectId, out var nodeSeed) &&
                        string.Equals(nodeSeed.OriginalFingerprint, originalCanonicalFingerprint, StringComparison.Ordinal) &&
                        string.Equals(nodeSeed.ScaledFingerprint, canonicalSeed.ScaledFingerprint, StringComparison.Ordinal) &&
                        string.Equals(node.GeometryFingerprint, nodeSeed.OriginalFingerprint, StringComparison.Ordinal))
                    {
                        node.GeometryFingerprint = nodeSeed.ScaledFingerprint;
                        assemblyChanged = true;
                    }

                    assemblyChanged |= ResolveConflictByEventKey(
                        assembly,
                        $"canonical-unit-scale:{node.Id}");
                }
            }

            // Refresh non-conflicting source-node fingerprints from the final scaled document.
            // Part categories were advanced above from the exact pre-scale snapshots, so a live
            // source that was already edited cannot be mistaken for the canonical category.
            foreach (var node in assembly.LinkGraph.Nodes.Where(node =>
                         string.Equals(node.Role, AssemblyLinkRoles.Source, StringComparison.OrdinalIgnoreCase) &&
                         scaledNodeIds.Contains(node.Id) &&
                         !canonicalFingerprintExclusions.Contains(node.ObjectId)))
            {
                var sourceObject = doc.Objects.FindId(node.ObjectId);
                if (sourceObject is null ||
                    !_fingerprints.TryCreatePartCandidate(sourceObject, out var candidate, out _))
                {
                    continue;
                }

                var previousFingerprint = node.GeometryFingerprint;
                if (!string.Equals(previousFingerprint, candidate.Fingerprint, StringComparison.Ordinal))
                {
                    node.GeometryFingerprint = candidate.Fingerprint;
                    assemblyChanged = true;
                }
            }

            foreach (var reference in assembly.GeometryReferences)
            {
                if (scaledNodeIds.Count == 0 ||
                    !scaledObjectIds.Contains(reference.SourceObjectId) ||
                    !scaledObjectIds.Contains(reference.TargetObjectId))
                {
                    continue;
                }

                if (!reference.SourceToTargetTransform.TryToTransform(out var current))
                    continue;

                reference.SourceToTargetTransform = TransformRecord.FromTransform(scale * current * inverse);
                reference.UpdatedAt = DateTimeOffset.UtcNow;
                assemblyChanged = true;
            }

            if (!assemblyChanged)
                continue;

            var now = DateTimeOffset.UtcNow;
            foreach (var node in assembly.LinkGraph.Nodes.Where(node => scaledNodeIds.Contains(node.Id)))
                node.UpdatedAt = now;
            assembly.LinkGraph.UpdatedAt = now;
            assembly.UpdatedAt = now;
            changed = true;
        }

        return changed;
    }

    private static bool ApplyTransformFact(
        AssemblyStore store,
        TransformFact fact,
        ISet<Guid> refreshRequests,
        IDictionary<GraphItemKey, OriginalAssemblyEditEvidence> originalAssemblyEditRequests,
        int factIndex)
    {
        if (fact.ObjectIds.IsDefaultOrEmpty)
            return false;

        var selectedObjectIds = fact.ObjectIds.ToHashSet();
        var changed = false;

        if (fact.ObjectsWillBeCopied)
        {
            // BeforeTransformObjects only tells us that a copy operation was requested. The
            // resulting object may intentionally omit Assembly Manager user strings. AddedFact
            // is the authoritative evidence: it opens a conflict only when the new object
            // actually carries an existing link-node identity.
            return false;
        }

        if (!fact.Transform.TryGetInverse(out var inverse))
        {
            foreach (var assembly in store.Assemblies)
            {
                foreach (var node in assembly.LinkGraph.Nodes.Where(node => selectedObjectIds.Contains(node.ObjectId)))
                {
                    if (!string.Equals(node.Role, AssemblyLinkRoles.Source, StringComparison.OrdinalIgnoreCase))
                        changed |= QuarantineNode(assembly, node);

                    changed |= AddConflict(
                        assembly,
                        AssemblyLinkConflictTypes.TransformUnresolved,
                        node.Id,
                        "A linked object received a non-invertible transform; its relationship matrix was not changed.",
                        new[] { node.ObjectId },
                        $"non-invertible:{node.Id}");
                }
            }

            return changed;
        }

        // Only orientation-preserving rigid moves/rotations are layout edits. Mirrors reverse
        // chirality and therefore follow the same shape-change path as scale and shear.
        if (fact.Transform.RigidType != TransformRigidType.Rigid)
        {
            foreach (var assembly in store.Assemblies)
            {
                foreach (var node in assembly.LinkGraph.Nodes.Where(node => selectedObjectIds.Contains(node.ObjectId)))
                {
                    node.UpdatedAt = DateTimeOffset.UtcNow;
                    assembly.LinkGraph.UpdatedAt = node.UpdatedAt;
                    assembly.UpdatedAt = node.UpdatedAt;
                    if (string.Equals(node.Role, AssemblyLinkRoles.Source, StringComparison.OrdinalIgnoreCase))
                    {
                        refreshRequests.Add(node.ObjectId);
                    }
                    else if (string.Equals(
                                 node.Role,
                                 AssemblyLinkRoles.OriginalAssembly,
                                 StringComparison.OrdinalIgnoreCase))
                    {
                        QueueOriginalAssemblyEdit(
                            originalAssemblyEditRequests,
                            new GraphItemKey(assembly.Id, node.Id),
                            fact.OriginalGeometryByObjectId.TryGetValue(node.ObjectId, out var originalGeometry)
                                ? originalGeometry
                                : ScaleGeometrySnapshot.EmptyEvidence(),
                            factIndex);
                    }
                    else
                    {
                        changed |= QuarantineNode(assembly, node);
                        changed |= AddConflict(
                            assembly,
                            AssemblyLinkConflictTypes.DerivedGeometryChanged,
                            node.Id,
                            "A generated copied or flat object received a non-rigid transform and was quarantined as a shape edit. Use Update Assembly to rebuild it from its source; only safe ORIGINAL ASSEMBLIES edits can promote to the source.",
                            new[] { node.ObjectId },
                            $"derived-geometry:{node.Id}");
                    }

                    changed = true;
                }
            }

            return changed;
        }

        foreach (var assembly in store.Assemblies)
        {
            var selectedNodeIds = assembly.LinkGraph.Nodes
                .Where(node => selectedObjectIds.Contains(node.ObjectId))
                .Select(node => node.Id)
                .ToHashSet();

            foreach (var edge in assembly.LinkGraph.Edges)
            {
                var childMoved = selectedNodeIds.Contains(edge.ChildNodeId);
                var parentMoved = selectedNodeIds.Contains(edge.ParentNodeId);
                if (!childMoved && !parentMoved)
                    continue;

                if (!edge.ParentToChildTransform.TryToTransform(out var updated))
                {
                    if (assembly.LinkGraph.Nodes.FirstOrDefault(node => node.Id == edge.ChildNodeId) is { } childNode)
                    {
                        if (!string.Equals(childNode.Status, AssemblyLinkStatuses.Conflict, StringComparison.OrdinalIgnoreCase))
                        {
                            childNode.Status = AssemblyLinkStatuses.Conflict;
                            changed = true;
                        }

                        changed |= AddConflict(
                            assembly,
                            AssemblyLinkConflictTypes.TransformUnresolved,
                            childNode.Id,
                            "A stored relationship transform is malformed. Gazelle left the relationship unchanged instead of substituting an identity transform.",
                            new[] { childNode.ObjectId },
                            $"stored-transform:{edge.Id}",
                            edgeId: edge.Id);
                    }

                    if (!string.Equals(edge.Status, AssemblyLinkStatuses.Conflict, StringComparison.OrdinalIgnoreCase))
                    {
                        edge.Status = AssemblyLinkStatuses.Conflict;
                        changed = true;
                    }
                    continue;
                }

                if (childMoved)
                {
                    updated = fact.Transform * updated;
                    if (string.Equals(edge.Recipe, AssemblyLinkRecipes.LayFlat, StringComparison.OrdinalIgnoreCase) &&
                        HasPlanRotation(fact.Transform))
                    {
                        edge.RecipeMetadata[AssemblyLinkMetadataKeys.UserPlanRotationOverride] = bool.TrueString;
                    }
                }
                if (parentMoved)
                    updated *= inverse;

                edge.ParentToChildTransform = TransformRecord.FromTransform(updated);
                edge.UpdatedAt = DateTimeOffset.UtcNow;
                changed = true;
            }

            // Keep v1 direct references consistent while old documents migrate to the graph.
            foreach (var reference in assembly.GeometryReferences)
            {
                var targetMoved = selectedObjectIds.Contains(reference.TargetObjectId);
                var sourceMoved = selectedObjectIds.Contains(reference.SourceObjectId);
                if (!targetMoved && !sourceMoved)
                    continue;

                if (!reference.SourceToTargetTransform.TryToTransform(out var updated))
                {
                    var targetNode = assembly.LinkGraph.Nodes.FirstOrDefault(node => node.ObjectId == reference.TargetObjectId);
                    changed |= AddConflict(
                        assembly,
                        AssemblyLinkConflictTypes.TransformUnresolved,
                        targetNode?.Id ?? Guid.Empty,
                        "A legacy relationship transform is malformed. Gazelle left it unchanged instead of substituting an identity transform.",
                        new[] { reference.TargetObjectId },
                        $"legacy-transform:{reference.Id}");
                    continue;
                }

                if (targetMoved)
                    updated = fact.Transform * updated;
                if (sourceMoved)
                    updated *= inverse;

                reference.SourceToTargetTransform = TransformRecord.FromTransform(updated);
                reference.UpdatedAt = DateTimeOffset.UtcNow;
                changed = true;
            }

            if (selectedNodeIds.Count > 0)
            {
                var timestamp = DateTimeOffset.UtcNow;
                foreach (var node in assembly.LinkGraph.Nodes.Where(node => selectedNodeIds.Contains(node.Id)))
                {
                    node.UpdatedAt = timestamp;
                    if (!string.Equals(
                            node.Role,
                            AssemblyLinkRoles.OriginalAssembly,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var requestKey = new GraphItemKey(assembly.Id, node.Id);
                    if (originalAssemblyEditRequests.TryGetValue(requestKey, out var evidence) &&
                        factIndex > evidence.FirstShapeFactIndex)
                    {
                        originalAssemblyEditRequests[requestKey] = evidence with
                        {
                            PreEditGeometry = evidence.PreEditGeometry.TransformEvidence(fact.Transform)
                        };
                    }
                }
                assembly.LinkGraph.UpdatedAt = timestamp;
                assembly.UpdatedAt = timestamp;
            }
        }

        return changed;
    }

    private static bool HasPlanRotation(Transform transform)
    {
        const double tolerance = 1e-10;
        return Math.Abs(transform.M00 - 1.0) > tolerance ||
               Math.Abs(transform.M01) > tolerance ||
               Math.Abs(transform.M10) > tolerance ||
               Math.Abs(transform.M11 - 1.0) > tolerance;
    }

    private static bool ApplyReplaceFact(
        AssemblyStore store,
        ReplaceFact fact,
        bool producedByTransform,
        ISet<Guid> refreshRequests,
        IDictionary<GraphItemKey, OriginalAssemblyEditEvidence> originalAssemblyEditRequests,
        int factIndex)
    {
        var changed = false;
        foreach (var assembly in store.Assemblies)
        {
            if (fact.NewObjectId != Guid.Empty && fact.NewObjectId != fact.OldObjectId)
            {
                changed |= ReplaceObjectIdInAssemblyRecords(
                    assembly,
                    fact.OldObjectId,
                    fact.NewObjectId);
            }

            foreach (var node in assembly.LinkGraph.Nodes.Where(node => node.ObjectId == fact.OldObjectId))
            {
                if (fact.NewObjectId != Guid.Empty && fact.NewObjectId != fact.OldObjectId)
                {
                    node.ObjectId = fact.NewObjectId;
                    if (refreshRequests.Remove(fact.OldObjectId))
                        refreshRequests.Add(fact.NewObjectId);
                }

                node.Status = AssemblyLinkStatuses.Active;
                node.UpdatedAt = DateTimeOffset.UtcNow;
                assembly.LinkGraph.UpdatedAt = node.UpdatedAt;
                assembly.UpdatedAt = node.UpdatedAt;
                changed = true;

                if (string.Equals(node.Role, AssemblyLinkRoles.Source, StringComparison.OrdinalIgnoreCase)
                    && !producedByTransform)
                {
                    refreshRequests.Add(node.ObjectId);
                }
                else if (string.Equals(
                             node.Role,
                             AssemblyLinkRoles.OriginalAssembly,
                             StringComparison.OrdinalIgnoreCase) &&
                         !producedByTransform)
                {
                    QueueOriginalAssemblyEdit(
                        originalAssemblyEditRequests,
                        new GraphItemKey(assembly.Id, node.Id),
                        fact.OldGeometry,
                        factIndex);
                }
                else if (!string.Equals(node.Role, AssemblyLinkRoles.Source, StringComparison.OrdinalIgnoreCase)
                          && !producedByTransform)
                {
                    changed |= QuarantineNode(assembly, node);
                    changed |= AddConflict(
                        assembly,
                        AssemblyLinkConflictTypes.DerivedGeometryChanged,
                        node.Id,
                        "Generated copied or flat geometry was edited directly. Use Update Assembly to rebuild this quarantined branch from its source; only safe ORIGINAL ASSEMBLIES edits can promote to the source.",
                        new[] { node.ObjectId },
                        $"derived-geometry:{node.Id}");
                }
            }
        }

        return changed;
    }

    private static bool ApplyGeometryChangedFact(
        AssemblyStore store,
        GeometryChangedFact fact,
        ISet<Guid> refreshRequests,
        IDictionary<GraphItemKey, OriginalAssemblyEditEvidence> originalAssemblyEditRequests,
        int factIndex)
    {
        var matched = false;
        var changedObjectIds = fact.ObjectIds.ToHashSet();
        foreach (var assembly in store.Assemblies)
        {
            foreach (var node in assembly.LinkGraph.Nodes.Where(node => changedObjectIds.Contains(node.ObjectId)))
            {
                node.UpdatedAt = DateTimeOffset.UtcNow;
                assembly.LinkGraph.UpdatedAt = node.UpdatedAt;
                assembly.UpdatedAt = node.UpdatedAt;
                if (string.Equals(node.Role, AssemblyLinkRoles.Source, StringComparison.OrdinalIgnoreCase))
                {
                    refreshRequests.Add(node.ObjectId);
                }
                else if (string.Equals(
                             node.Role,
                             AssemblyLinkRoles.OriginalAssembly,
                             StringComparison.OrdinalIgnoreCase))
                {
                    QueueOriginalAssemblyEdit(
                        originalAssemblyEditRequests,
                        new GraphItemKey(assembly.Id, node.Id),
                        fact.OriginalGeometryByObjectId.TryGetValue(node.ObjectId, out var originalGeometry)
                            ? originalGeometry
                            : ScaleGeometrySnapshot.EmptyEvidence(),
                        factIndex);
                }
                else
                {
                    matched |= QuarantineNode(assembly, node);
                    matched |= AddConflict(
                        assembly,
                        AssemblyLinkConflictTypes.DerivedGeometryChanged,
                        node.Id,
                        "Generated copied or flat geometry was reshaped with grips. Use Update Assembly to rebuild this quarantined branch from its source; only safe ORIGINAL ASSEMBLIES edits can promote to the source.",
                        new[] { node.ObjectId },
                        $"derived-geometry:{node.Id}");
                }

                matched = true;
            }
        }

        return matched;
    }

    private static bool ApplyDeleteFact(RhinoDoc doc, AssemblyStore store, DeleteFact fact)
    {
        var changed = false;
        foreach (var assembly in store.Assemblies)
        {
            foreach (var node in assembly.LinkGraph.Nodes.Where(node => node.ObjectId == fact.ObjectId))
            {
                node.Status = AssemblyLinkStatuses.Deleted;
                node.UpdatedAt = DateTimeOffset.UtcNow;
                var isSource = string.Equals(node.Role, AssemblyLinkRoles.Source, StringComparison.OrdinalIgnoreCase);
                changed |= AddConflict(
                    assembly,
                    isSource ? AssemblyLinkConflictTypes.SourceDeleted : AssemblyLinkConflictTypes.MissingObject,
                    node.Id,
                    isSource
                        ? "A linked source object was deleted. Split, join, and replacement intent require review."
                        : "A generated linked object was deleted. It was not recreated automatically.",
                    new[] { fact.ObjectId },
                    $"delete-node:{node.Id}");
                assembly.LinkGraph.UpdatedAt = node.UpdatedAt;
                assembly.UpdatedAt = node.UpdatedAt;
                changed = true;
            }
        }

        if (Guid.TryParse(fact.LinkNodeId, out var linkedNodeId))
            changed |= ResolveDuplicateIdentityIfUnique(doc, store, linkedNodeId);

        return changed;
    }

    private static bool ApplyAddedFact(AssemblyStore store, AddedFact fact)
    {
        if (!Guid.TryParse(fact.LinkNodeId, out var linkedNodeId))
            return false;

        foreach (var assembly in store.Assemblies)
        {
            var node = assembly.LinkGraph.Nodes.FirstOrDefault(candidate => candidate.Id == linkedNodeId);
            if (node is null || node.ObjectId == fact.ObjectId)
                continue;

            return AddConflict(
                assembly,
                AssemblyLinkConflictTypes.DuplicateIdentity,
                node.Id,
                "Two Rhino objects carry the same Assembly Manager link-node identity. The new object was not linked automatically.",
                new[] { node.ObjectId, fact.ObjectId },
                $"copy-node:{node.Id}");
        }

        return false;
    }

    private static bool ReplaceObjectIdInAssemblyRecords(
        AssemblyRecord assembly,
        Guid oldObjectId,
        Guid newObjectId)
    {
        if (oldObjectId == Guid.Empty || newObjectId == Guid.Empty || oldObjectId == newObjectId)
            return false;

        var changed = false;
        var oldLocator = $"RhinoObject:{oldObjectId:D}";
        foreach (var node in assembly.LinkGraph.Nodes.Where(node =>
                     string.Equals(node.SourceLocator, oldLocator, StringComparison.OrdinalIgnoreCase)))
        {
            node.SourceLocator = $"RhinoObject:{newObjectId:D}";
            changed = true;
        }

        foreach (var part in assembly.Parts)
        {
            changed |= ReplaceObjectId(part.SourceObjectIds, oldObjectId, newObjectId);
            changed |= ReplaceObjectId(part.GeneratedObjectIds, oldObjectId, newObjectId);
            changed |= ReplaceObjectId(part.CamObjectIds, oldObjectId, newObjectId);
        }

        foreach (var component in assembly.Components)
        {
            changed |= ReplaceObjectId(component.ObjectIds, oldObjectId, newObjectId);
            foreach (var objectIds in component.RepresentativeObjectIdsByPartName.Values)
                changed |= ReplaceObjectId(objectIds, oldObjectId, newObjectId);
        }

        foreach (var hardware in assembly.Hardware)
        {
            if (hardware.SourceObjectId == oldObjectId)
            {
                hardware.SourceObjectId = newObjectId;
                changed = true;
            }

            if (hardware.BlockInstanceId == oldObjectId)
            {
                hardware.BlockInstanceId = newObjectId;
                changed = true;
            }

            if (hardware.GeneratedObjectId == oldObjectId)
            {
                hardware.GeneratedObjectId = newObjectId;
                changed = true;
            }
        }

        foreach (var reference in assembly.GeometryReferences)
        {
            if (reference.SourceObjectId == oldObjectId)
            {
                reference.SourceObjectId = newObjectId;
                changed = true;
            }

            if (reference.TargetObjectId == oldObjectId)
            {
                reference.TargetObjectId = newObjectId;
                changed = true;
            }
        }

        return changed;
    }

    private static bool ReplaceObjectId(IList<Guid> objectIds, Guid oldObjectId, Guid newObjectId)
    {
        var changed = false;
        for (var index = 0; index < objectIds.Count; index++)
        {
            if (objectIds[index] != oldObjectId)
                continue;

            objectIds[index] = newObjectId;
            changed = true;
        }

        return changed;
    }

    private static bool ApplyMetadataChangedFact(
        RhinoDoc doc,
        AssemblyStore store,
        MetadataChangedFact fact)
    {
        var changed = false;
        if (Guid.TryParse(fact.OldLinkNodeId, out var oldNodeId)
            && !string.Equals(fact.OldLinkNodeId, fact.NewLinkNodeId, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var assembly in store.Assemblies)
            {
                var oldNode = assembly.LinkGraph.Nodes.FirstOrDefault(node => node.Id == oldNodeId && node.ObjectId == fact.ObjectId);
                if (oldNode is null)
                    continue;

                changed |= AddConflict(
                    assembly,
                    AssemblyLinkConflictTypes.MissingObject,
                    oldNode.Id,
                    "A linked object's identity metadata was removed or changed. Its lineage was not reassigned automatically.",
                    new[] { fact.ObjectId },
                    $"metadata-removed:{oldNode.Id}");
            }

            changed |= ResolveDuplicateIdentityIfUnique(doc, store, oldNodeId);
        }

        if (!Guid.TryParse(fact.NewLinkNodeId, out var newNodeId))
            return changed;

        foreach (var assembly in store.Assemblies)
        {
            var node = assembly.LinkGraph.Nodes.FirstOrDefault(candidate => candidate.Id == newNodeId);
            if (node is null || node.ObjectId == fact.ObjectId)
                continue;

            changed |= AddConflict(
                assembly,
                AssemblyLinkConflictTypes.DuplicateIdentity,
                node.Id,
                "Link identity metadata was assigned to a second Rhino object. The duplicate was not linked automatically.",
                new[] { node.ObjectId, fact.ObjectId },
                $"copy-node:{node.Id}");
        }

        return changed;
    }

    private static bool ResolveDuplicateIdentityIfUnique(
        RhinoDoc doc,
        AssemblyStore store,
        Guid linkNodeId)
    {
        var changed = false;
        foreach (var assembly in store.Assemblies)
        {
            var node = assembly.LinkGraph.Nodes.FirstOrDefault(candidate => candidate.Id == linkNodeId);
            if (node is null || doc.Objects.FindId(node.ObjectId) is null)
                continue;

            var taggedObjectIds = (doc.Objects.FindByUserString(
                                       AssemblyManagerConstants.LinkNodeIdUserString,
                                       node.Id.ToString("D"),
                                       false) ?? Array.Empty<RhinoObject>())
                .Select(candidate => candidate.Id)
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToHashSet();
            taggedObjectIds.Remove(node.ObjectId);
            if (taggedObjectIds.Count == 0)
                changed |= ResolveConflictByEventKey(assembly, $"copy-node:{node.Id}");
        }

        return changed;
    }

    private static bool ApplyGroupChangedFact(AssemblyStore store, GroupChangedFact fact)
    {
        var changed = false;
        var actualMembers = fact.MemberObjectIds.ToHashSet();

        foreach (var assembly in store.Assemblies)
        {
            foreach (var instance in assembly.LinkGraph.SourceComponentInstances)
            {
                var isSourceGroup = fact.GroupId != Guid.Empty
                    ? instance.SourceGroupId == fact.GroupId
                    : instance.SourceGroupId == Guid.Empty && instance.SourceGroupIndex == fact.GroupIndex;
                var isGeneratedGroup = fact.GroupId != Guid.Empty && instance.GeneratedGroupId == fact.GroupId;
                if (!isSourceGroup && !isGeneratedGroup)
                    continue;

                var expectedMembers = isSourceGroup
                    ? instance.SourceNodeIds
                        .Select(nodeId => assembly.LinkGraph.Nodes.FirstOrDefault(node => node.Id == nodeId)?.ObjectId ?? Guid.Empty)
                        .Where(id => id != Guid.Empty)
                        .ToHashSet()
                    : assembly.LinkGraph.Nodes
                        .Where(node => node.SourceComponentInstanceId == instance.Id
                                       && (string.Equals(node.Role, AssemblyLinkRoles.OriginalAssembly, StringComparison.OrdinalIgnoreCase)
                                           || string.Equals(node.Role, AssemblyLinkRoles.Hardware, StringComparison.OrdinalIgnoreCase)))
                        .Select(node => node.ObjectId)
                        .Where(id => id != Guid.Empty)
                        .ToHashSet();

                var groupIdentity = fact.GroupId != Guid.Empty ? fact.GroupId.ToString() : $"index:{fact.GroupIndex}";
                var conflictEventKey = $"group-membership:{instance.Id}:{groupIdentity}";

                if (expectedMembers.SetEquals(actualMembers))
                {
                    changed |= ResolveConflictByEventKey(assembly, conflictEventKey);
                    continue;
                }

                var added = actualMembers.Except(expectedMembers).OrderBy(id => id).ToArray();
                var removed = expectedMembers.Except(actualMembers).OrderBy(id => id).ToArray();
                var metadata = new Dictionary<string, string>
                {
                    ["GroupId"] = fact.GroupId.ToString(),
                    ["GroupIndex"] = fact.GroupIndex.ToString(),
                    ["GroupName"] = fact.GroupName,
                    ["GroupEventType"] = fact.EventType,
                    ["AddedObjectIds"] = string.Join(",", added),
                    ["RemovedObjectIds"] = string.Join(",", removed)
                };

                changed |= AddConflict(
                    assembly,
                    AssemblyLinkConflictTypes.ComponentMembershipChanged,
                    Guid.Empty,
                    $"The linked component group '{fact.GroupName}' changed membership. Added and removed parts require review.",
                    added.Concat(removed),
                    conflictEventKey,
                    metadata);
            }
        }

        return changed;
    }

    private static UndoSnapshot CaptureUndoSnapshot(AssemblyStore store)
    {
        var nodeObjectIds = ImmutableDictionary.CreateBuilder<GraphItemKey, Guid>();
        foreach (var assembly in store.Assemblies)
        {
            foreach (var node in assembly.LinkGraph.Nodes)
                nodeObjectIds[new GraphItemKey(assembly.Id, node.Id)] = node.ObjectId;
        }

        return new UndoSnapshot(nodeObjectIds.ToImmutable());
    }

    private void InspectAfterUndoRedo(RhinoDoc doc, AssemblyStore store, UndoSnapshot snapshot)
    {
        var issues = ImmutableArray.CreateBuilder<string>();
        foreach (var assembly in store.Assemblies)
        {
            var objectsByNodeId = new Dictionary<Guid, RhinoObject>();
            foreach (var node in assembly.LinkGraph.Nodes)
            {
                var currentObject = doc.Objects.FindId(node.ObjectId);
                var candidates = new Dictionary<Guid, RhinoObject>();
                if (currentObject is not null)
                    candidates[currentObject.Id] = currentObject;
                var tagged = doc.Objects.FindByUserString(
                    AssemblyManagerConstants.LinkNodeIdUserString,
                    node.Id.ToString("D"),
                    false) ?? Array.Empty<RhinoObject>();
                foreach (var candidate in tagged)
                    candidates[candidate.Id] = candidate;

                if (snapshot.NodeObjectIds.TryGetValue(new GraphItemKey(assembly.Id, node.Id), out var previousObjectId) &&
                    doc.Objects.FindId(previousObjectId) is { } previousObject)
                {
                    candidates[previousObject.Id] = previousObject;
                }

                if (currentObject is not null)
                {
                    objectsByNodeId[node.Id] = currentObject;
                    var duplicateIds = candidates.Keys.Where(id => id != currentObject.Id).ToArray();
                    if (duplicateIds.Length > 0)
                    {
                        issues.Add(
                            $"{assembly.Name}: linked node {node.Id:D} is duplicated on Rhino object(s) {string.Join(", ", duplicateIds.Select(id => id.ToString("D")))}.");
                    }
                    continue;
                }

                if (candidates.Count == 1)
                {
                    var candidate = candidates.Values.Single();
                    objectsByNodeId[node.Id] = candidate;
                    issues.Add(
                        $"{assembly.Name}: linked node {node.Id:D} now resolves to Rhino object {candidate.Id:D} instead of stored object {node.ObjectId:D}.");
                }
                else if (candidates.Count > 1)
                {
                    issues.Add($"{assembly.Name}: linked node {node.Id:D} has multiple possible Rhino objects after undo/redo.");
                }
                else
                {
                    issues.Add($"{assembly.Name}: linked Rhino object {node.ObjectId:D} is missing after undo/redo.");
                }
            }

            var nodesById = assembly.LinkGraph.Nodes.ToDictionary(node => node.Id);
            foreach (var edge in assembly.LinkGraph.Edges)
            {
                if (string.Equals(edge.Recipe, AssemblyLinkRecipes.BlockDefinitionPart, StringComparison.OrdinalIgnoreCase) ||
                    !nodesById.TryGetValue(edge.ParentNodeId, out var parentNode) ||
                    !nodesById.TryGetValue(edge.ChildNodeId, out var childNode) ||
                    IsNodeQuarantined(assembly, parentNode) ||
                    IsNodeQuarantined(assembly, childNode) ||
                    !objectsByNodeId.TryGetValue(parentNode.Id, out var parentObject) ||
                    !objectsByNodeId.TryGetValue(childNode.Id, out var childObject))
                {
                    continue;
                }

                if (!edge.ParentToChildTransform.TryToTransform(out var parentToChild))
                {
                    issues.Add($"{assembly.Name}: link edge {edge.Id:D} has an invalid stored transform after undo/redo.");
                    continue;
                }

                if (!TryCreateComparableBrep(parentObject, parentToChild, out var expected) ||
                    !_fingerprints.TryDuplicateManufacturableBrep(childObject, out var actual, out _))
                {
                    continue;
                }

                if (!GeometryMatches(expected, actual, doc.ModelAbsoluteTolerance))
                {
                    issues.Add(
                        $"{assembly.Name}: linked output {childObject.Id:D} is out of sync with parent {parentObject.Id:D} after undo/redo.");
                }
            }
        }

        if (issues.Count == 0)
        {
            _undoHealthIssues.TryRemove(doc.RuntimeSerialNumber, out _);
            return;
        }

        var result = issues.Distinct(StringComparer.Ordinal).ToImmutableArray();
        _undoHealthIssues[doc.RuntimeSerialNumber] = result;
        RhinoApp.WriteLine(
            "Gazelle found {0} linked-assembly health warning(s) after undo/redo. Existing geometry and the redo chain were left unchanged; use Update Assembly or inspect Assembly Manager before continuing.",
            result.Length);
    }

    private bool TryCreateComparableBrep(
        RhinoObject parentObject,
        Transform parentToChild,
        out Brep expected)
    {
        expected = default!;
        if (!_fingerprints.TryDuplicateManufacturableBrep(parentObject, out var parentBrep, out _))
            return false;

        if (!parentBrep.Transform(parentToChild))
            return false;

        expected = parentBrep;
        return true;
    }

    private static bool GeometryMatches(Brep expected, Brep actual, double modelTolerance)
    {
        if (expected.DataCRC(0) == actual.DataCRC(0))
            return true;
        if (expected.Faces.Count != actual.Faces.Count ||
            expected.Edges.Count != actual.Edges.Count ||
            expected.Vertices.Count != actual.Vertices.Count)
        {
            return false;
        }

        var tolerance = Math.Max(modelTolerance * 1e-6, 1e-10);
        for (var index = 0; index < expected.Vertices.Count; index++)
        {
            if (expected.Vertices[index].Location.DistanceTo(actual.Vertices[index].Location) > tolerance)
                return false;
        }

        for (var index = 0; index < expected.Edges.Count; index++)
        {
            if (Math.Abs(expected.Edges[index].GetLength() - actual.Edges[index].GetLength()) > tolerance)
                return false;
        }

        var expectedVolume = VolumeMassProperties.Compute(expected)?.Volume ?? double.NaN;
        var actualVolume = VolumeMassProperties.Compute(actual)?.Volume ?? double.NaN;
        var expectedArea = AreaMassProperties.Compute(expected)?.Area ?? double.NaN;
        var actualArea = AreaMassProperties.Compute(actual)?.Area ?? double.NaN;
        return !double.IsNaN(expectedVolume) && !double.IsNaN(actualVolume) &&
               !double.IsNaN(expectedArea) && !double.IsNaN(actualArea) &&
               Math.Abs(expectedVolume - actualVolume) <= tolerance &&
               Math.Abs(expectedArea - actualArea) <= tolerance;
    }

    private static bool QuarantineNode(AssemblyRecord assembly, AssemblyLinkNodeRecord node)
    {
        var changed = false;
        if (!node.Metadata.TryGetValue(AssemblyLinkMetadataKeys.Quarantined, out var current) ||
            !string.Equals(current, bool.TrueString, StringComparison.OrdinalIgnoreCase))
        {
            node.Metadata[AssemblyLinkMetadataKeys.Quarantined] = bool.TrueString;
            changed = true;
        }

        if (!string.Equals(node.Status, AssemblyLinkStatuses.Conflict, StringComparison.OrdinalIgnoreCase))
        {
            node.Status = AssemblyLinkStatuses.Conflict;
            changed = true;
        }

        foreach (var edge in assembly.LinkGraph.Edges.Where(edge => edge.ChildNodeId == node.Id))
        {
            if (string.Equals(edge.Status, AssemblyLinkStatuses.Conflict, StringComparison.OrdinalIgnoreCase))
                continue;

            edge.Status = AssemblyLinkStatuses.Conflict;
            edge.UpdatedAt = DateTimeOffset.UtcNow;
            changed = true;
        }

        if (changed)
        {
            node.UpdatedAt = DateTimeOffset.UtcNow;
            assembly.LinkGraph.UpdatedAt = node.UpdatedAt;
            assembly.UpdatedAt = node.UpdatedAt;
        }

        return changed;
    }

    private static bool IsNodeQuarantined(AssemblyRecord assembly, AssemblyLinkNodeRecord node)
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

    private static bool ResolveConflictByEventKey(AssemblyRecord assembly, string eventKey)
    {
        var resolved = false;
        foreach (var conflict in assembly.LinkGraph.Conflicts.Where(conflict =>
                     string.Equals(conflict.Status, AssemblyLinkStatuses.Open, StringComparison.OrdinalIgnoreCase) &&
                     conflict.Metadata.TryGetValue(ConflictEventKey, out var existingKey) &&
                     string.Equals(existingKey, eventKey, StringComparison.Ordinal)))
        {
            conflict.Status = AssemblyLinkStatuses.Resolved;
            conflict.ResolvedAt = DateTimeOffset.UtcNow;
            resolved = true;
        }

        return resolved;
    }

    private static bool RejectConcurrentSourceAndOriginalEdits(
        AssemblyStore store,
        ISet<Guid> sourceRefreshRequests,
        IDictionary<GraphItemKey, OriginalAssemblyEditEvidence> originalAssemblyEditRequests)
    {
        var changed = false;
        var assembliesById = store.Assemblies.ToDictionary(assembly => assembly.Id);
        foreach (var request in originalAssemblyEditRequests.Keys.ToList())
        {
            if (!assembliesById.TryGetValue(request.AssemblyId, out var assembly))
                continue;

            var originalNode = assembly.LinkGraph.Nodes.FirstOrDefault(node => node.Id == request.ItemId);
            var incomingEdge = originalNode is null
                ? null
                : assembly.LinkGraph.Edges.FirstOrDefault(edge => edge.ChildNodeId == originalNode.Id);
            var sourceNode = incomingEdge is null
                ? null
                : assembly.LinkGraph.Nodes.FirstOrDefault(node => node.Id == incomingEdge.ParentNodeId);
            if (originalNode is null || sourceNode is null ||
                !sourceRefreshRequests.Contains(sourceNode.ObjectId))
            {
                continue;
            }

            originalAssemblyEditRequests.Remove(request);
            changed |= QuarantineNode(assembly, originalNode);
            changed |= AddConflict(
                assembly,
                AssemblyLinkConflictTypes.OriginalAssemblyPromotionUnresolved,
                originalNode.Id,
                "The design source and its ORIGINAL ASSEMBLIES occurrence were both reshaped in the same update batch. Gazelle preserved both edits for review instead of choosing one as authoritative.",
                new[] { sourceNode.ObjectId, originalNode.ObjectId },
                $"original-promotion-concurrent:{originalNode.Id}",
                edgeId: incomingEdge?.Id ?? Guid.Empty);
        }

        return changed;
    }

    private bool RejectOutOfSyncOriginalAssemblyEdits(
        RhinoDoc doc,
        AssemblyStore store,
        IDictionary<GraphItemKey, OriginalAssemblyEditEvidence> originalAssemblyEditRequests)
    {
        var changed = false;
        var assembliesById = store.Assemblies.ToDictionary(assembly => assembly.Id);
        foreach (var (request, evidence) in originalAssemblyEditRequests.ToList())
        {
            if (!assembliesById.TryGetValue(request.AssemblyId, out var assembly) ||
                assembly.LinkGraph.Nodes.FirstOrDefault(node => node.Id == request.ItemId) is not { } originalNode)
            {
                originalAssemblyEditRequests.Remove(request);
                continue;
            }

            var incomingEdges = assembly.LinkGraph.Edges
                .Where(edge => edge.ChildNodeId == originalNode.Id)
                .ToList();
            var incomingEdge = incomingEdges.Count == 1 ? incomingEdges[0] : null;
            var sourceNode = incomingEdge is null
                ? null
                : assembly.LinkGraph.Nodes.FirstOrDefault(node => node.Id == incomingEdge.ParentNodeId);
            var failureMessage = string.Empty;
            if (!evidence.PreviouslyValidated && !evidence.PreEditGeometry.HasGeometryEvidence)
            {
                failureMessage = "Gazelle could not capture the assembly occurrence before it changed, so it cannot prove that inverse promotion is safe.";
            }
            else if (IsNodeQuarantined(assembly, originalNode) ||
                     incomingEdge is null ||
                     !string.Equals(incomingEdge.Status, AssemblyLinkStatuses.Active, StringComparison.OrdinalIgnoreCase) ||
                     sourceNode is null ||
                     !string.Equals(sourceNode.Role, AssemblyLinkRoles.Source, StringComparison.OrdinalIgnoreCase))
            {
                failureMessage = "The assembly occurrence was already quarantined or its incoming source relationship was not healthy before this edit.";
            }
            else if (!evidence.PreviouslyValidated &&
                     (!incomingEdge.ParentToChildTransform.TryToTransform(out var sourceToOriginal) ||
                     doc.Objects.FindId(sourceNode.ObjectId) is not { } sourceObject ||
                     !_fingerprints.TryDuplicateManufacturableBrep(sourceObject, out var expectedOriginal, out _) ||
                     !expectedOriginal.Transform(sourceToOriginal) ||
                     !ScaleGeometrySnapshot.Capture(expectedOriginal).MatchesTransformed(
                         evidence.PreEditGeometry,
                         Transform.Identity)))
            {
                failureMessage = "The occurrence did not match its linked source immediately before the edit. Gazelle preserved the edit instead of overwriting the source through a stale relationship.";
            }
            else if (evidence.PreEditGeometry.MaterialSignature is { } expectedMaterial &&
                     doc.Objects.FindId(sourceNode.ObjectId) is { } materialSource &&
                     !string.Equals(expectedMaterial, CaptureMaterialSignature(materialSource.Attributes), StringComparison.Ordinal))
            {
                failureMessage = "The original occurrence's material before editing did not match its linked source. Gazelle preserved the edit instead of overwriting a different source material through a stale relationship.";
            }

            if (string.IsNullOrWhiteSpace(failureMessage))
                continue;

            originalAssemblyEditRequests.Remove(request);
            changed |= QuarantineNode(assembly, originalNode);
            changed |= AddConflict(
                assembly,
                AssemblyLinkConflictTypes.OriginalAssemblyPromotionUnresolved,
                originalNode.Id,
                failureMessage,
                new[] { originalNode.ObjectId, sourceNode?.ObjectId ?? Guid.Empty },
                $"original-promotion-precondition:{originalNode.Id}",
                edgeId: incomingEdge?.Id ?? Guid.Empty);
        }

        return changed;
    }

    private static bool RejectConcurrentOriginalAssemblyEdits(
        AssemblyStore store,
        IDictionary<GraphItemKey, OriginalAssemblyEditEvidence> requests)
    {
        var candidates = store.Assemblies.SelectMany(assembly => assembly.LinkGraph.Nodes
            .Where(node => requests.ContainsKey(new GraphItemKey(assembly.Id, node.Id)))
            .Select(node =>
            {
                var edge = assembly.LinkGraph.Edges.FirstOrDefault(candidate => candidate.ChildNodeId == node.Id);
                var source = edge is null ? null : assembly.LinkGraph.Nodes.FirstOrDefault(candidate => candidate.Id == edge.ParentNodeId);
                return (Assembly: assembly, Node: node, Edge: edge, Source: source);
            }))
            .Where(candidate => candidate.Source is not null)
            .GroupBy(candidate => candidate.Source!.ObjectId)
            .Where(group => group.Count() > 1);
        var changed = false;
        foreach (var group in candidates)
        {
            foreach (var candidate in group)
            {
                requests.Remove(new GraphItemKey(candidate.Assembly.Id, candidate.Node.Id));
                changed |= QuarantineNode(candidate.Assembly, candidate.Node);
                changed |= AddConflict(candidate.Assembly,
                    AssemblyLinkConflictTypes.OriginalAssemblyPromotionUnresolved, candidate.Node.Id,
                    "More than one edited ORIGINAL ASSEMBLIES occurrence maps to the same source before the assembly update. Gazelle preserved every edit for review instead of choosing one as authoritative.",
                    new[] { candidate.Node.ObjectId, candidate.Source!.ObjectId },
                    $"original-promotion-concurrent:{candidate.Node.Id}", edgeId: candidate.Edge?.Id ?? Guid.Empty);
            }
        }
        return changed;
    }

    private static void QueueOriginalAssemblyEdit(
        IDictionary<GraphItemKey, OriginalAssemblyEditEvidence> requests,
        GraphItemKey key,
        ScaleGeometrySnapshot preEditGeometry,
        int factIndex)
    {
        var candidate = new OriginalAssemblyEditEvidence(preEditGeometry, factIndex);
        if (!requests.TryGetValue(key, out var existing))
        {
            requests[key] = candidate;
            return;
        }

        if (factIndex < existing.FirstShapeFactIndex ||
            (factIndex == existing.FirstShapeFactIndex &&
             !existing.PreEditGeometry.HasGeometryEvidence &&
             preEditGeometry.HasGeometryEvidence))
        {
            requests[key] = candidate;
        }
    }

    private static bool RejectStructuralGeometryBatches(
        RhinoDoc doc,
        IReadOnlyList<LinkEventFact> facts,
        ISet<int> replacementCompanionIndexes,
        IReadOnlyDictionary<Guid, Guid> replacementMap,
        AssemblyStore store,
        ISet<Guid> sourceRefreshRequests,
        IDictionary<GraphItemKey, OriginalAssemblyEditEvidence> originalAssemblyEditRequests)
    {
        var unmatchedMutations = facts
            .Select((fact, index) => (fact, index))
            .Where(item => !replacementCompanionIndexes.Contains(item.index))
            .Select(item => item.fact switch
            {
                AddedFact added => new StructuralMutationEvidence(
                    added.ObjectId,
                    added.LinkNodeId,
                    added.GroupIndices,
                    item.fact.CommandBatchId),
                DeleteFact deleted => new StructuralMutationEvidence(
                    deleted.ObjectId,
                    deleted.LinkNodeId,
                    deleted.GroupIndices,
                    item.fact.CommandBatchId),
                _ => null
            })
            .Where(evidence => evidence is not null && evidence.ObjectId != Guid.Empty)
            .Cast<StructuralMutationEvidence>()
            .ToArray();
        if (unmatchedMutations.Length == 0 ||
            (sourceRefreshRequests.Count == 0 && originalAssemblyEditRequests.Count == 0))
        {
            return false;
        }

        var changed = false;
        var assembliesById = store.Assemblies.ToDictionary(assembly => assembly.Id);
        foreach (var request in originalAssemblyEditRequests.Keys.ToList())
        {
            if (!assembliesById.TryGetValue(request.AssemblyId, out var assembly) ||
                assembly.LinkGraph.Nodes.FirstOrDefault(node => node.Id == request.ItemId) is not { } node)
            {
                continue;
            }

            var relatedObjectIds = GetRelatedStructuralObjectIds(
                doc,
                assembly,
                node,
                unmatchedMutations,
                GetShapeChangeCommandBatchIds(facts, replacementMap, node.ObjectId));
            if (relatedObjectIds.Count == 0)
                continue;

            originalAssemblyEditRequests.Remove(request);
            changed |= QuarantineNode(assembly, node);
            changed |= AddConflict(
                assembly,
                AssemblyLinkConflictTypes.OriginalAssemblyPromotionUnresolved,
                node.Id,
                "This geometry-change batch also added or removed an unmatched Rhino object, so it may be a split, join, or new-part operation. Gazelle preserved the result for review instead of treating one remainder as a one-to-one edit.",
                relatedObjectIds.Append(node.ObjectId),
                $"original-promotion-structural:{node.Id}");
        }

        foreach (var sourceObjectId in sourceRefreshRequests.ToList())
        {
            var affectedSources = new List<(AssemblyRecord Assembly, AssemblyLinkNodeRecord Node, Guid[] RelatedIds)>();
            foreach (var assembly in store.Assemblies)
            {
                foreach (var node in assembly.LinkGraph.Nodes.Where(node =>
                             node.ObjectId == sourceObjectId &&
                             string.Equals(node.Role, AssemblyLinkRoles.Source, StringComparison.OrdinalIgnoreCase)))
                {
                    var relatedObjectIds = GetRelatedStructuralObjectIds(
                        doc,
                        assembly,
                        node,
                        unmatchedMutations,
                        GetShapeChangeCommandBatchIds(facts, replacementMap, node.ObjectId));
                    if (relatedObjectIds.Count > 0)
                        affectedSources.Add((assembly, node, relatedObjectIds.ToArray()));
                }
            }

            foreach (var affected in affectedSources)
            {
                var assembly = affected.Assembly;
                var node = affected.Node;
                if (!string.Equals(node.Status, AssemblyLinkStatuses.Conflict, StringComparison.OrdinalIgnoreCase))
                {
                    node.Status = AssemblyLinkStatuses.Conflict;
                    changed = true;
                }

                changed |= AddConflict(
                    assembly,
                    AssemblyLinkConflictTypes.SourceSplit,
                    node.Id,
                    "This source geometry-change batch also added or removed an unmatched Rhino object, so it may be a split, join, or new-part operation. Linked descendants were preserved for review.",
                    affected.RelatedIds.Append(sourceObjectId),
                    $"source-structural:{node.Id}");
            }

            if (affectedSources.Count > 0)
                sourceRefreshRequests.Remove(sourceObjectId);
        }

        return changed;
    }

    private static IReadOnlyCollection<Guid> GetRelatedStructuralObjectIds(
        RhinoDoc doc,
        AssemblyRecord assembly,
        AssemblyLinkNodeRecord changedNode,
        IEnumerable<StructuralMutationEvidence> unmatchedMutations,
        ISet<Guid> shapeChangeCommandBatchIds)
    {
        var changedGroups = (doc.Objects.FindId(changedNode.ObjectId)?.Attributes.GetGroupList() ??
                             Array.Empty<int>()).ToHashSet();
        var nodesById = assembly.LinkGraph.Nodes.ToDictionary(node => node.Id);
        return unmatchedMutations
            .Where(evidence =>
            {
                if (evidence.CommandBatchId != Guid.Empty &&
                    shapeChangeCommandBatchIds.Contains(evidence.CommandBatchId))
                {
                    return true;
                }
                if (evidence.GroupIndices.Any(changedGroups.Contains))
                    return true;
                if (!Guid.TryParse(evidence.LinkNodeId, out var evidenceNodeId) ||
                    !nodesById.TryGetValue(evidenceNodeId, out var evidenceNode))
                {
                    return false;
                }

                if (evidenceNode.Id == changedNode.Id)
                    return true;
                if (changedNode.SourceComponentInstanceId != Guid.Empty &&
                    evidenceNode.SourceComponentInstanceId == changedNode.SourceComponentInstanceId)
                {
                    return true;
                }

                return changedNode.ComponentId != Guid.Empty &&
                       evidenceNode.ComponentId == changedNode.ComponentId;
            })
            .Select(evidence => evidence.ObjectId)
            .Distinct()
            .ToArray();
    }

    private static HashSet<Guid> GetShapeChangeCommandBatchIds(
        IReadOnlyList<LinkEventFact> facts,
        IReadOnlyDictionary<Guid, Guid> replacementMap,
        Guid finalObjectId)
    {
        return facts.Where(fact => fact.CommandBatchId != Guid.Empty && fact switch
        {
            ReplaceFact replacement =>
                ResolveReplacementId(replacement.OldObjectId, replacementMap) == finalObjectId ||
                ResolveReplacementId(replacement.NewObjectId, replacementMap) == finalObjectId,
            GeometryChangedFact geometryChanged => geometryChanged.ObjectIds.Any(objectId =>
                ResolveReplacementId(objectId, replacementMap) == finalObjectId),
            TransformFact transform =>
                !transform.ObjectsWillBeCopied &&
                transform.Transform.RigidType != TransformRigidType.Rigid &&
                transform.ObjectIds.Any(objectId =>
                    ResolveReplacementId(objectId, replacementMap) == finalObjectId),
            _ => false
        })
            .Select(fact => fact.CommandBatchId)
            .ToHashSet();
    }

    private void DispatchOriginalAssemblyEditRequests(
        RhinoDoc doc,
        IEnumerable<GraphItemKey> requestedNodes,
        bool refreshDescendants = true)
    {
        var requests = requestedNodes.Distinct().ToList();
        if (requests.Count == 0)
            return;

        try
        {
            _referenceUpdates.PromoteOriginalAssemblyEdits(
                doc,
                requests.Select(request => (request.AssemblyId, request.ItemId)),
                refreshDescendants);
        }
        catch (Exception ex)
        {
            RecordOriginalPromotionFailure(doc, requests, ex.Message);
        }
    }

    private void RecordOriginalPromotionFailure(
        RhinoDoc doc,
        IEnumerable<GraphItemKey> requests,
        string errorMessage)
    {
        var store = _repository.Load(doc);
        var assembliesById = store.Assemblies.ToDictionary(assembly => assembly.Id);
        var changed = false;
        foreach (var request in requests)
        {
            if (!assembliesById.TryGetValue(request.AssemblyId, out var assembly) ||
                assembly.LinkGraph.Nodes.FirstOrDefault(node => node.Id == request.ItemId) is not { } node)
            {
                continue;
            }

            changed |= QuarantineNode(assembly, node);
            changed |= AddConflict(
                assembly,
                AssemblyLinkConflictTypes.OriginalAssemblyPromotionUnresolved,
                node.Id,
                $"Automatic promotion of the ORIGINAL ASSEMBLIES edit failed: {errorMessage}",
                new[] { node.ObjectId },
                $"original-promotion-failed:{node.Id}");
        }

        if (!changed)
            return;

        using (AssemblyLinkMutationGate.Enter())
            _repository.Save(doc, store);
    }

    private void DispatchRefreshRequests(RhinoDoc doc, IEnumerable<Guid> sourceObjectIds)
    {
        var sourceIds = sourceObjectIds.Where(id => id != Guid.Empty).Distinct().ToList();
        if (sourceIds.Count == 0)
            return;

        try
        {
            using (AssemblyLinkMutationGate.Enter())
                _referenceUpdates.RefreshDescendantsFromSources(doc, sourceIds);
        }
        catch (Exception ex)
        {
            foreach (var sourceObjectId in sourceIds)
                RecordRefreshFailure(doc, sourceObjectId, ex.Message);
        }
    }

    private void RecordRefreshFailure(RhinoDoc doc, Guid sourceObjectId, string errorMessage)
    {
        var store = _repository.Load(doc);
        var changed = false;
        foreach (var assembly in store.Assemblies)
        {
            foreach (var node in assembly.LinkGraph.Nodes.Where(candidate =>
                         candidate.ObjectId == sourceObjectId
                         && string.Equals(candidate.Role, AssemblyLinkRoles.Source, StringComparison.OrdinalIgnoreCase)))
            {
                changed |= AddConflict(
                    assembly,
                    AssemblyLinkConflictTypes.TransformUnresolved,
                    node.Id,
                    $"Automatic descendant refresh failed: {errorMessage}",
                    new[] { sourceObjectId },
                    $"refresh-failed:{node.Id}");
            }
        }

        if (changed)
        {
            using var mutation = AssemblyLinkMutationGate.Enter();
            _repository.Save(doc, store);
        }

        RhinoApp.WriteLine(
            "Assembly Manager could not refresh linked descendants for source '{0}': {1}",
            sourceObjectId,
            errorMessage);
    }

    private static bool AddConflict(
        AssemblyRecord assembly,
        string conflictType,
        Guid nodeId,
        string message,
        IEnumerable<Guid> candidateObjectIds,
        string eventKey,
        IReadOnlyDictionary<string, string>? metadata = null,
        Guid edgeId = default)
    {
        var existing = assembly.LinkGraph.Conflicts.FirstOrDefault(conflict =>
            string.Equals(conflict.Status, AssemblyLinkStatuses.Open, StringComparison.OrdinalIgnoreCase)
            && string.Equals(conflict.ConflictType, conflictType, StringComparison.OrdinalIgnoreCase)
            && conflict.Metadata.TryGetValue(ConflictEventKey, out var existingKey)
            && string.Equals(existingKey, eventKey, StringComparison.Ordinal));

        var candidates = candidateObjectIds.Where(id => id != Guid.Empty).Distinct().ToArray();
        if (existing is not null)
        {
            var changed = false;
            var newCandidates = candidates.Except(existing.CandidateObjectIds).ToArray();
            if (newCandidates.Length > 0)
            {
                existing.CandidateObjectIds.AddRange(newCandidates);
                changed = true;
            }

            if (!string.Equals(existing.Message, message, StringComparison.Ordinal))
            {
                existing.Message = message;
                changed = true;
            }

            if (metadata is not null)
            {
                foreach (var (key, value) in metadata)
                {
                    if (!existing.Metadata.TryGetValue(key, out var existingValue)
                        || !string.Equals(existingValue, value, StringComparison.Ordinal))
                    {
                        existing.Metadata[key] = value;
                        changed = true;
                    }
                }
            }

            if (edgeId != Guid.Empty && existing.EdgeId != edgeId)
            {
                existing.EdgeId = edgeId;
                changed = true;
            }

            return changed;
        }

        var conflictMetadata = metadata is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(metadata);
        conflictMetadata[ConflictEventKey] = eventKey;
        assembly.LinkGraph.Conflicts.Add(new LinkConflictRecord
        {
            ConflictType = conflictType,
            NodeId = nodeId,
            EdgeId = edgeId,
            Message = message,
            CandidateObjectIds = candidates.ToList(),
            Metadata = conflictMetadata
        });
        assembly.LinkGraph.UpdatedAt = DateTimeOffset.UtcNow;
        assembly.UpdatedAt = assembly.LinkGraph.UpdatedAt;
        return true;
    }

    private static string GroupFactKey(GroupChangedFact fact)
    {
        return fact.GroupId != Guid.Empty ? fact.GroupId.ToString("D") : $"index:{fact.GroupIndex}";
    }

    private void Enqueue(uint documentSerialNumber, LinkEventFact fact)
    {
        if (documentSerialNumber == 0)
            return;

        var commandBatchId = _activeCommandBatches.TryGetValue(documentSerialNumber, out var activeBatches) &&
                             activeBatches.TryPeek(out var activeBatch)
            ? activeBatch
            : Guid.Empty;
        _queues.GetOrAdd(documentSerialNumber, _ => new ConcurrentQueue<LinkEventFact>())
            .Enqueue(fact with { CommandBatchId = commandBatchId });
    }

    private static List<LinkEventFact> Drain(ConcurrentQueue<LinkEventFact> queue)
    {
        var facts = new List<LinkEventFact>();
        while (queue.TryDequeue(out var fact))
            facts.Add(fact);
        return facts;
    }

    private bool ShouldCapture()
    {
        return _linkSafety?.IsEnabled != false && !AssemblyLinkMutationGate.IsSuppressed;
    }

    private bool ShouldCapture(RhinoDoc? doc)
    {
        return doc is not null && ShouldCapture() && !UndoOrRedoIsActive(doc);
    }

    private static bool UndoOrRedoIsActive(RhinoDoc doc)
    {
        return doc.UndoActive || doc.RedoActive;
    }

    private sealed record OriginalAssemblyEditEvidence(
        ScaleGeometrySnapshot PreEditGeometry,
        int FirstShapeFactIndex,
        bool PreviouslyValidated = false);

    private sealed record ScaleGeometrySnapshot(
        string GeometryKind,
        BoundingBox Bounds,
        int FaceCount,
        ImmutableArray<Point3d> Vertices,
        ImmutableArray<Point3d> EdgeSamples,
        ImmutableArray<double> EdgeLengths,
        double Area,
        double Volume,
        string? MaterialSignature = null,
        Guid InstanceDefinitionId = default,
        Transform? InstanceTransform = null)
    {
        public bool HasGeometryEvidence =>
            !string.IsNullOrWhiteSpace(GeometryKind) &&
            (Vertices.Length > 0 || EdgeSamples.Length > 0 || Bounds.IsValid || InstanceTransform.HasValue);

        public static ScaleGeometrySnapshot EmptyEvidence()
        {
            return Empty();
        }

        public static ScaleGeometrySnapshot Capture(GeometryBase? geometry, ObjectAttributes? attributes = null)
        {
            if (geometry is null)
                return Empty();

            if (geometry is InstanceReferenceGeometry instance)
            {
                return new ScaleGeometrySnapshot(
                    nameof(InstanceReferenceGeometry), geometry.GetBoundingBox(true), 0,
                    ImmutableArray<Point3d>.Empty, ImmutableArray<Point3d>.Empty,
                    ImmutableArray<double>.Empty, double.NaN, double.NaN,
                    attributes is null ? null : CaptureMaterialSignature(attributes),
                    instance.ParentIdefId, instance.Xform);
            }

            var brep = geometry switch
            {
                Brep sourceBrep => sourceBrep,
                Extrusion extrusion => extrusion.ToBrep(),
                Surface surface => surface.ToBrep(),
                _ => null
            };
            if (brep is null)
            {
                return new ScaleGeometrySnapshot(
                    geometry.GetType().FullName ?? geometry.GetType().Name,
                    geometry.GetBoundingBox(true),
                    0,
                    ImmutableArray<Point3d>.Empty,
                    ImmutableArray<Point3d>.Empty,
                    ImmutableArray<double>.Empty,
                    double.NaN,
                    double.NaN,
                    attributes is null ? null : CaptureMaterialSignature(attributes));
            }

            return new ScaleGeometrySnapshot(
                "Brep",
                brep.GetBoundingBox(true),
                brep.Faces.Count,
                brep.Vertices.Select(vertex => vertex.Location).ToImmutableArray(),
                brep.Edges.Select(edge => edge.PointAt(edge.Domain.Mid)).ToImmutableArray(),
                brep.Edges.Select(edge => edge.GetLength()).OrderBy(length => length).ToImmutableArray(),
                AreaMassProperties.Compute(brep)?.Area ?? double.NaN,
                VolumeMassProperties.Compute(brep)?.Volume ?? double.NaN,
                attributes is null ? null : CaptureMaterialSignature(attributes));
        }

        public ScaleGeometrySnapshot TransformEvidence(Transform transform)
        {
            if (!HasGeometryEvidence || !IsAffine(transform))
                return Empty();

            var transformedVertices = TransformPoints(Vertices, transform);
            var transformedEdgeSamples = TransformPoints(EdgeSamples, transform);
            var transformedBounds = Bounds.IsValid
                ? new BoundingBox(Bounds.GetCorners(), transform)
                : BoundingBox.Empty;
            return this with
            {
                Bounds = transformedBounds,
                Vertices = transformedVertices,
                EdgeSamples = transformedEdgeSamples,
                InstanceTransform = InstanceTransform.HasValue ? transform * InstanceTransform.Value : null
            };
        }

        public bool MatchesTransformed(ScaleGeometrySnapshot revised, Transform transform)
        {
            // Rhino moves block instances by replacing their instance-reference geometry,
            // just as it does BREPs. Definition identity and the complete instance matrix
            // prove that replacement is a placement edit; bounding boxes cannot prove it
            // for rotated or symmetric hardware. Never accept a swapped block definition.
            if (InstanceTransform.HasValue || revised.InstanceTransform.HasValue)
            {
                if (InstanceDefinitionId == Guid.Empty || InstanceDefinitionId != revised.InstanceDefinitionId ||
                    !InstanceTransform.HasValue || !revised.InstanceTransform.HasValue || !IsAffine(transform))
                    return false;
                var expected = transform * InstanceTransform.Value;
                var actual = revised.InstanceTransform.Value;
                for (var row = 0; row < 4; row++)
                {
                    for (var column = 0; column < 4; column++)
                    {
                        var tolerance = Math.Max(1e-10, Math.Abs(expected[row, column]) * 1e-9);
                        if (!NearlyEqual(expected[row, column], actual[row, column], tolerance))
                            return false;
                    }
                }
                return true;
            }

            if (!string.Equals(GeometryKind, "Brep", StringComparison.Ordinal) ||
                !string.Equals(GeometryKind, revised.GeometryKind, StringComparison.Ordinal) ||
                FaceCount != revised.FaceCount ||
                Vertices.Length != revised.Vertices.Length ||
                EdgeSamples.Length != revised.EdgeSamples.Length ||
                EdgeLengths.Length != revised.EdgeLengths.Length ||
                !IsAffine(transform))
            {
                return false;
            }

            var coordinateMagnitude = new[]
            {
                Math.Abs(Bounds.Min.X), Math.Abs(Bounds.Min.Y), Math.Abs(Bounds.Min.Z),
                Math.Abs(Bounds.Max.X), Math.Abs(Bounds.Max.Y), Math.Abs(Bounds.Max.Z),
                Math.Abs(revised.Bounds.Min.X), Math.Abs(revised.Bounds.Min.Y), Math.Abs(revised.Bounds.Min.Z),
                Math.Abs(revised.Bounds.Max.X), Math.Abs(revised.Bounds.Max.Y), Math.Abs(revised.Bounds.Max.Z),
                Math.Abs(transform.M03), Math.Abs(transform.M13), Math.Abs(transform.M23)
            }.Where(value => !double.IsNaN(value) && !double.IsInfinity(value)).DefaultIfEmpty(1.0).Max();
            var coordinateTolerance = Math.Max(1e-8, Math.Max(1.0, coordinateMagnitude) * 1e-9);

            for (var index = 0; index < Vertices.Length; index++)
            {
                var expected = Vertices[index];
                expected.Transform(transform);
                if (expected.DistanceTo(revised.Vertices[index]) > coordinateTolerance)
                    return false;
            }

            for (var index = 0; index < EdgeSamples.Length; index++)
            {
                var expected = EdgeSamples[index];
                expected.Transform(transform);
                if (expected.DistanceTo(revised.EdgeSamples[index]) > coordinateTolerance)
                    return false;
            }

            var determinant = LinearDeterminant(transform);
            if (double.IsNaN(determinant) ||
                !MassValueMatches(Volume, revised.Volume, Math.Abs(determinant)))
            {
                return false;
            }

            if (transform.RigidType == TransformRigidType.Rigid)
            {
                var lengthTolerance = Math.Max(
                    1e-8,
                    EdgeLengths.DefaultIfEmpty(0.0).Select(Math.Abs).DefaultIfEmpty(0.0).Max() * 1e-9);
                for (var index = 0; index < EdgeLengths.Length; index++)
                {
                    if (!NearlyEqual(revised.EdgeLengths[index], EdgeLengths[index], lengthTolerance))
                        return false;
                }

                if (!MassValueMatches(Area, revised.Area, 1.0))
                    return false;
            }

            return true;
        }

        public bool MatchesScaled(ScaleGeometrySnapshot revised, double scale)
        {
            if (!IsUsableGlobalScale(scale) ||
                !string.Equals(GeometryKind, revised.GeometryKind, StringComparison.Ordinal) ||
                !Bounds.IsValid || !revised.Bounds.IsValid)
            {
                return false;
            }

            var coordinateMagnitude = new[]
            {
                Math.Abs(Bounds.Min.X * scale), Math.Abs(Bounds.Min.Y * scale), Math.Abs(Bounds.Min.Z * scale),
                Math.Abs(Bounds.Max.X * scale), Math.Abs(Bounds.Max.Y * scale), Math.Abs(Bounds.Max.Z * scale)
            }.Max();
            var coordinateTolerance = Math.Max(1e-8, coordinateMagnitude * 1e-9);
            if (!NearlyEqual(revised.Bounds.Min.X, Bounds.Min.X * scale, coordinateTolerance) ||
                !NearlyEqual(revised.Bounds.Min.Y, Bounds.Min.Y * scale, coordinateTolerance) ||
                !NearlyEqual(revised.Bounds.Min.Z, Bounds.Min.Z * scale, coordinateTolerance) ||
                !NearlyEqual(revised.Bounds.Max.X, Bounds.Max.X * scale, coordinateTolerance) ||
                !NearlyEqual(revised.Bounds.Max.Y, Bounds.Max.Y * scale, coordinateTolerance) ||
                !NearlyEqual(revised.Bounds.Max.Z, Bounds.Max.Z * scale, coordinateTolerance))
            {
                return false;
            }

            // Linked manufacturing geometry is normalized to a BREP snapshot. Matching every
            // vertex, sorted edge length, area, and volume makes the exemption specific to the
            // unit scale instead of trusting unrelated replacements in the same idle batch.
            if (!string.Equals(GeometryKind, "Brep", StringComparison.Ordinal))
                return true;
            if (FaceCount != revised.FaceCount ||
                Vertices.Length != revised.Vertices.Length ||
                EdgeLengths.Length != revised.EdgeLengths.Length)
            {
                return false;
            }

            for (var index = 0; index < Vertices.Length; index++)
            {
                var expected = new Point3d(
                    Vertices[index].X * scale,
                    Vertices[index].Y * scale,
                    Vertices[index].Z * scale);
                if (expected.DistanceTo(revised.Vertices[index]) > coordinateTolerance)
                    return false;
            }

            var lengthTolerance = Math.Max(1e-8, EdgeLengths.DefaultIfEmpty(0.0).Max() * scale * 1e-9);
            for (var index = 0; index < EdgeLengths.Length; index++)
            {
                if (!NearlyEqual(revised.EdgeLengths[index], EdgeLengths[index] * scale, lengthTolerance))
                    return false;
            }

            if (!MassValueMatches(Area, revised.Area, scale * scale) ||
                !MassValueMatches(Volume, revised.Volume, scale * scale * scale))
            {
                return false;
            }

            return true;
        }

        private static bool MassValueMatches(double original, double revised, double factor)
        {
            if (double.IsNaN(original) || double.IsNaN(revised))
                return double.IsNaN(original) && double.IsNaN(revised);

            var expected = original * factor;
            var tolerance = Math.Max(1e-8, Math.Abs(expected) * 1e-8);
            return NearlyEqual(revised, expected, tolerance);
        }

        private static bool NearlyEqual(double first, double second, double tolerance)
        {
            return Math.Abs(first - second) <= tolerance;
        }

        private static bool IsAffine(Transform transform)
        {
            const double tolerance = 1e-12;
            return Math.Abs(transform.M30) <= tolerance &&
                   Math.Abs(transform.M31) <= tolerance &&
                   Math.Abs(transform.M32) <= tolerance &&
                   Math.Abs(transform.M33 - 1.0) <= tolerance;
        }

        private static ImmutableArray<Point3d> TransformPoints(
            ImmutableArray<Point3d> points,
            Transform transform)
        {
            if (points.IsDefaultOrEmpty)
                return ImmutableArray<Point3d>.Empty;

            return points.Select(point =>
            {
                var transformed = point;
                transformed.Transform(transform);
                return transformed;
            }).ToImmutableArray();
        }

        private static double LinearDeterminant(Transform transform)
        {
            return transform.M00 * (transform.M11 * transform.M22 - transform.M12 * transform.M21) -
                   transform.M01 * (transform.M10 * transform.M22 - transform.M12 * transform.M20) +
                   transform.M02 * (transform.M10 * transform.M21 - transform.M11 * transform.M20);
        }

        private static ScaleGeometrySnapshot Empty()
        {
            return new ScaleGeometrySnapshot(
                string.Empty,
                BoundingBox.Empty,
                0,
                ImmutableArray<Point3d>.Empty,
                ImmutableArray<Point3d>.Empty,
                ImmutableArray<double>.Empty,
                double.NaN,
                double.NaN);
        }
    }

    private abstract record LinkEventFact
    {
        public Guid CommandBatchId { get; init; }
    }
    private sealed record TransformFact(
        ImmutableArray<Guid> ObjectIds,
        Transform Transform,
        bool ObjectsWillBeCopied,
        ImmutableDictionary<Guid, ScaleGeometrySnapshot> OriginalGeometryByObjectId,
        Guid CaptureId) : LinkEventFact;
    private sealed record GeometryChangedFact(
        ImmutableArray<Guid> ObjectIds,
        ImmutableDictionary<Guid, ScaleGeometrySnapshot> OriginalGeometryByObjectId,
        Guid CaptureId) : LinkEventFact;
    private sealed record GlobalScaleFact(
        double Scale,
        ImmutableHashSet<Guid> LinkedObjectIds,
        ImmutableHashSet<GraphItemKey> LinkedNodeKeys,
        ImmutableDictionary<Guid, UnitScaleFingerprintSeed> PartFingerprintSeeds) : LinkEventFact;
    private sealed record UnitScaleFingerprintSeed(
        string OriginalFingerprint,
        string ScaledFingerprint,
        double OriginalThickness,
        double ScaledThickness);
    private sealed record ReplaceFact(
        Guid OldObjectId,
        Guid NewObjectId,
        bool NewObjectIdWasUnavailable,
        string LinkNodeId,
        ScaleGeometrySnapshot OldGeometry,
        ScaleGeometrySnapshot NewGeometry) : LinkEventFact;
    private sealed record TransformReplacementClassification(
        HashSet<int> ReplacementIndexes,
        IReadOnlyDictionary<int, HashSet<Guid>> ShapeObjectIdsByTransformIndex);
    private sealed record DeleteFact(
        Guid ObjectId,
        string LinkNodeId,
        ImmutableArray<int> GroupIndices) : LinkEventFact;
    private sealed record AddedFact(
        Guid ObjectId,
        string LinkNodeId,
        ImmutableArray<int> GroupIndices,
        ScaleGeometrySnapshot Geometry) : LinkEventFact;
    private sealed record MetadataChangedFact(Guid ObjectId, string OldLinkNodeId, string NewLinkNodeId) : LinkEventFact;
    private sealed record MaterialChangedFact(Guid ObjectId, ScaleGeometrySnapshot Geometry) : LinkEventFact;
    private sealed record PendingUpdateFact : LinkEventFact;
    private sealed record ReconcileFact(UndoSnapshot Snapshot) : LinkEventFact;
    private sealed record GroupChangedFact(
        Guid GroupId,
        int GroupIndex,
        string GroupName,
        string EventType,
        ImmutableArray<Guid> MemberObjectIds) : LinkEventFact;
    private sealed record StructuralMutationEvidence(
        Guid ObjectId,
        string LinkNodeId,
        ImmutableArray<int> GroupIndices,
        Guid CommandBatchId);
    private readonly record struct GraphItemKey(Guid AssemblyId, Guid ItemId);
    private sealed record UndoSnapshot(ImmutableDictionary<GraphItemKey, Guid> NodeObjectIds);

}
