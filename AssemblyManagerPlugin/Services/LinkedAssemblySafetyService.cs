using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AssemblyManagerPlugin.Core;
using Rhino;
using Rhino.DocObjects;
using Rhino.FileIO;

namespace AssemblyManagerPlugin.Services;

/// <summary>
/// Emergency-off safety boundary. Turning tracking off preserves an exact, durable
/// baseline, not guessed replacement transforms. Unchanged assemblies can resume;
/// changed assemblies remain protected until restored or recreated.
/// </summary>
public sealed class LinkedAssemblySafetyService
{
    private const string SuspensionEntry = "LinkedAssemblySuspension";
    private const string SuspensionConflict = "LinkTrackingSuspended";
    private readonly AssemblyRepository _repository;
    private readonly Func<bool> _enabled;
    private readonly Dictionary<uint, SuspensionRecord> _sessions = new();
    private readonly Dictionary<uint, string> _sessionJson = new();
    private static readonly SerializationOptions GeometryOptions = CreateGeometryOptions();

    private static SerializationOptions CreateGeometryOptions()
    {
        var options = new SerializationOptions { WriteUserData = true };
        // These switches were added after Rhino 8.0. Keep compatibility with the minimum
        // SDK while omitting transient caches on newer Rhino installations.
        foreach (var property in new[] { "WriteRenderMeshes", "WriteAnalysisMeshes" })
            typeof(SerializationOptions).GetProperty(property)?.SetValue(options, false);
        return options;
    }

    public LinkedAssemblySafetyService(AssemblyRepository repository, Func<bool> enabled)
    {
        _repository = repository;
        _enabled = enabled;
    }

    public bool IsEnabled => _enabled();

    public void EnsureEnabled()
    {
        if (!IsEnabled)
            throw new InvalidOperationException("Linked assemblies are disabled in Assembly Manager Settings. Enable linked assemblies before using Update Assembly or Update Component.");
    }

    public void ApplyPreferenceToOpenDocuments()
    {
        foreach (var doc in RhinoDoc.OpenDocuments())
            ObserveDocument(doc);
    }

    public void ObserveDocument(RhinoDoc doc)
    {
        if (!IsEnabled)
        {
            Suspend(doc);
            return;
        }
        var session = ReadSession(doc);
        if (session is not null)
            VerifyResume(doc, session);
    }

    public bool IsAssemblyBlocked(RhinoDoc doc, Guid assemblyId)
    {
        var session = ReadSession(doc);
        // An assembly created while tracking was disabled has no proven baseline.
        return session is not null && (!session.ResumedAssemblyIds.Contains(assemblyId));
    }

    public void EnsureCanUpdate(RhinoDoc doc, Guid assemblyId)
    {
        EnsureEnabled();
        var session = ReadSession(doc);
        if (session is not null && !session.ResumedAssemblyIds.Contains(assemblyId))
            VerifyResume(doc, session);
        if (IsAssemblyBlocked(doc, assemblyId))
            throw new InvalidOperationException("This assembly changed while linked-assembly tracking was disabled. Its saved placements may be stale, so Gazelle has not updated any geometry. Restore the geometry, material assignments and groups to their state before disabling linking, or remove and recreate the affected assembly.");
    }

    public void ForgetDocument(RhinoDoc doc)
    {
        _sessions.Remove(doc.RuntimeSerialNumber);
        _sessionJson.Remove(doc.RuntimeSerialNumber);
    }

    /// <summary>
    /// Called only after a newly generated assembly has been saved successfully. Unlike an
    /// unknown pre-existing assembly, its freshly created links have known provenance.
    /// </summary>
    public void RegisterCreatedAssembly(RhinoDoc doc, Guid assemblyId)
    {
        var session = ReadSession(doc);
        if (session is null)
        {
            if (!IsEnabled)
                Suspend(doc);
            return;
        }
        var assembly = _repository.Load(doc).Assemblies.Single(item => item.Id == assemblyId);
        session.Snapshots[assemblyId] = CaptureSnapshot(doc, assembly);
        if (IsEnabled)
            session.ResumedAssemblyIds.Add(assemblyId);
        else
            session.ResumedAssemblyIds.Remove(assemblyId);
        WriteSession(doc, session);
    }

    private void Suspend(RhinoDoc doc)
    {
        var store = _repository.Load(doc);
        var priorSession = ReadSession(doc);
        var session = priorSession ?? new SuspensionRecord();
        foreach (var assembly in store.Assemblies)
        {
            // Never replace an unresolved older baseline with potentially stale geometry.
            if (priorSession is null || session.ResumedAssemblyIds.Contains(assembly.Id))
                session.Snapshots[assembly.Id] = CaptureSnapshot(doc, assembly);
        }
        session.ResumedAssemblyIds.Clear();
        WriteSession(doc, session);
    }

    private void VerifyResume(RhinoDoc doc, SuspensionRecord session)
    {
        var store = _repository.Load(doc);
        var storeChanged = false;
        foreach (var assembly in store.Assemblies)
        {
            if (session.ResumedAssemblyIds.Contains(assembly.Id))
                continue;
            var unchanged = session.SchemaVersion == 1 && session.Snapshots.TryGetValue(assembly.Id, out var baseline)
                && baseline.Length > 0 && baseline == CaptureSnapshot(doc, assembly);
            var conflicts = assembly.LinkGraph.Conflicts.Where(conflict => conflict.ConflictType == SuspensionConflict).ToList();
            if (unchanged)
            {
                session.ResumedAssemblyIds.Add(assembly.Id);
                foreach (var conflict in conflicts.Where(conflict => conflict.Status == AssemblyLinkStatuses.Open))
                {
                    conflict.Status = AssemblyLinkStatuses.Resolved;
                    conflict.ResolvedAt = DateTimeOffset.UtcNow;
                    storeChanged = true;
                }
            }
            else if (!conflicts.Any(conflict => conflict.Status == AssemblyLinkStatuses.Open))
            {
                assembly.LinkGraph.Conflicts.Add(new LinkConflictRecord
                {
                    ConflictType = SuspensionConflict,
                    Message = "This assembly changed while linked-assembly tracking was disabled. Updates are blocked because placements may be stale. Restore its previous geometry, material assignments and groups, or remove and recreate the affected assembly."
                });
                storeChanged = true;
            }
        }
        if (storeChanged)
            _repository.Save(doc, store);
        if (store.Assemblies.All(assembly => session.ResumedAssemblyIds.Contains(assembly.Id)))
        {
            doc.Strings.Delete(AssemblyManagerConstants.StoreSection, SuspensionEntry);
            _sessions.Remove(doc.RuntimeSerialNumber);
            _sessionJson.Remove(doc.RuntimeSerialNumber);
        }
        else
        {
            WriteSession(doc, session);
        }
    }

    private SuspensionRecord? ReadSession(RhinoDoc doc)
    {
        var json = doc.Strings.GetValue(AssemblyManagerConstants.StoreSection, SuspensionEntry) ?? string.Empty;
        if (_sessions.TryGetValue(doc.RuntimeSerialNumber, out var cached)
            && _sessionJson.GetValueOrDefault(doc.RuntimeSerialNumber) == json)
            return cached;
        if (string.IsNullOrWhiteSpace(json))
        {
            if (cached is not null)
            {
                // Undo may remove the document marker independently of this global
                // preference. Retain the baseline and require proof instead of treating
                // missing metadata as permission to use potentially stale placements.
                cached.ResumedAssemblyIds.Clear();
                _sessionJson[doc.RuntimeSerialNumber] = json;
                return cached;
            }
            return null;
        }
        try
        {
            var session = JsonSerializer.Deserialize<SuspensionRecord>(json);
            if (session is null || session.SchemaVersion != 1 || session.Snapshots is null || session.ResumedAssemblyIds is null)
                throw new InvalidOperationException("Invalid suspension record.");
            _sessions[doc.RuntimeSerialNumber] = session;
            _sessionJson[doc.RuntimeSerialNumber] = json;
            return session;
        }
        catch
        {
            // An unreadable safety record cannot authorize a geometry overwrite.
            var blocked = new SuspensionRecord { SchemaVersion = 0 };
            _sessions[doc.RuntimeSerialNumber] = blocked;
            _sessionJson[doc.RuntimeSerialNumber] = json;
            return blocked;
        }
    }

    private void WriteSession(RhinoDoc doc, SuspensionRecord session)
    {
        _sessions[doc.RuntimeSerialNumber] = session;
        var json = JsonSerializer.Serialize(session);
        _sessionJson[doc.RuntimeSerialNumber] = json;
        doc.Strings.SetString(AssemblyManagerConstants.StoreSection, SuspensionEntry, json);
    }

    private static string CaptureSnapshot(RhinoDoc doc, AssemblyRecord assembly)
    {
        try
        {
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            void Add(string value)
            {
                var bytes = Encoding.UTF8.GetBytes(value);
                digest.AppendData(BitConverter.GetBytes(bytes.Length));
                digest.AppendData(bytes);
            }
            Add(JsonSerializer.Serialize(new
            {
                doc.ModelUnitSystem, doc.ModelAbsoluteTolerance,
                assembly.LinkGraph.Nodes, assembly.LinkGraph.Edges,
                assembly.LinkGraph.SourceComponentInstances,
                assembly.GeometryReferences, assembly.PendingComponentUpdates,
                assembly.Components, assembly.Parts, assembly.Hardware
            }));
            var groupIds = new HashSet<int>();
            var definitions = new HashSet<Guid>();
            void AddObject(RhinoObject obj)
            {
                Add(obj.Id.ToString());
                Add(obj.Geometry.ToJSON(GeometryOptions));
                Add(obj.Attributes.ToJSON(GeometryOptions));
                foreach (var groupIndex in obj.Attributes.GetGroupList() ?? Array.Empty<int>())
                    groupIds.Add(groupIndex);
                if (obj is InstanceObject instance && definitions.Add(instance.InstanceDefinition.Id))
                {
                    Add(instance.InstanceDefinition.Id.ToString());
                    foreach (var member in instance.InstanceDefinition.GetObjects().OrderBy(item => item.Id))
                        AddObject(member);
                }
            }
            var objectIds = assembly.LinkGraph.Nodes.Select(node => node.ObjectId)
                .Concat(assembly.PendingComponentUpdates.SelectMany(update => update.AddedObjectIds))
                .Where(id => id != Guid.Empty).Distinct().OrderBy(id => id);
            foreach (var id in objectIds)
            {
                Add(id.ToString());
                var obj = doc.Objects.FindId(id);
                if (obj is null)
                    Add("MissingObject");
                else
                    AddObject(obj);
            }
            foreach (var instance in assembly.LinkGraph.SourceComponentInstances)
            {
                foreach (var id in new[] { instance.SourceGroupId, instance.GeneratedGroupId })
                {
                    if (id == Guid.Empty)
                        continue;
                    var group = doc.Groups.FindId(id);
                    Add(id.ToString());
                    if (group is not null)
                        groupIds.Add(group.Index);
                    else
                        Add("MissingGroup");
                }
            }
            foreach (var groupIndex in groupIds.OrderBy(index => index))
            {
                var group = doc.Groups.FindIndex(groupIndex);
                Add(group?.Id.ToString() ?? "MissingGroup");
                Add(group?.Name ?? string.Empty);
                Add(string.Join(",", (doc.Groups.GroupMembers(groupIndex) ?? Array.Empty<RhinoObject>()).Select(obj => obj.Id).OrderBy(id => id)));
            }
            return Convert.ToHexString(digest.GetHashAndReset());
        }
        catch
        {
            // No complete snapshot means no safe automatic resume.
            return string.Empty;
        }
    }

    private sealed class SuspensionRecord
    {
        public int SchemaVersion { get; set; } = 1;
        public Dictionary<Guid, string> Snapshots { get; set; } = new();
        public HashSet<Guid> ResumedAssemblyIds { get; set; } = new();
    }
}
