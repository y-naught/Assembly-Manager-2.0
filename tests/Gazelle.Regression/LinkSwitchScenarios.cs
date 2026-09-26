using System.Text.Json;
using AssemblyManagerPlugin.Core;
using AssemblyManagerPlugin.Services;
using Rhino;
using Rhino.Geometry;
using Rhino.Runtime.InProcess;

internal static partial class Program
{
    private static void RunLinkSwitchScenario(RhinoCore core, RegressionServices services, string scenario, List<RhinoDoc> fixtureDocs)
    {
        if (scenario == "link-master-file-roundtrip")
        {
            RunLinkSuspensionFileRoundtrip(services, scenario, fixtureDocs);
            return;
        }
        if (scenario is "link-master-created-off" or "link-master-recreated")
        {
            RunNewLinkedAssemblySafetyScenario(core, services, scenario, fixtureDocs);
            return;
        }
        if (scenario == "link-settings-defaults")
        {
            var defaults = new PluginSettingsRecord();
            Require(defaults.AssemblyManager.EnableLinkedAssemblies, "The master linked-assembly feature must default on.");
            Require(!defaults.AssemblyManager.AutomaticallyPropagateChangesInAssembly, "Automatic updates/recategorization must default off.");
            var old = JsonSerializer.Deserialize<PluginSettingsRecord>("{\"SchemaVersion\":9,\"AssemblyManager\":{\"AutomaticallyPropagateChangesInAssembly\":true}}")!;
            Require(old.AssemblyManager.EnableLinkedAssemblies && old.AssemblyManager.AutomaticallyPropagateChangesInAssembly,
                "Existing explicit automatic opt-in must survive; absent master setting must default on.");
            foreach (var enabled in new[] { true, false })
            foreach (var automatic in new[] { true, false })
            {
                defaults.AssemblyManager.EnableLinkedAssemblies = enabled;
                defaults.AssemblyManager.AutomaticallyPropagateChangesInAssembly = automatic;
                var restored = JsonSerializer.Deserialize<PluginSettingsRecord>(JsonSerializer.Serialize(defaults))!;
                Require(restored.AssemblyManager.EnableLinkedAssemblies == enabled
                        && restored.AssemblyManager.AutomaticallyPropagateChangesInAssembly == automatic,
                    "The two independent preferences must round-trip in every combination.");
            }
            return;
        }

        var doc = RhinoDoc.Create(null);
        fixtureDocs.Add(doc);
        doc.ModelUnitSystem = UnitSystem.Inches;
        doc.ModelAbsoluteTolerance = 0.001;
        services.AutomaticallyPropagate = true;
        var created = CreateFixture(doc, services, scenario + "-grouped", out var sources);
        PumpIdle(core, services);
        var before = services.Repository.Load(doc).FindAssembly(created.Name)!;
        var target = before.LinkGraph.Nodes.First(node => node.Role == AssemblyLinkRoles.OriginalAssembly);
        using var originalSource = doc.Objects.FindId(sources[0]).Geometry.Duplicate();
        var targetBounds = doc.Objects.FindId(target.ObjectId).Geometry.GetBoundingBox(true);
        var beforeGraph = JsonSerializer.Serialize(before.LinkGraph);
        Guid unaffectedId = Guid.Empty;
        if (scenario == "link-master-unrelated-assembly")
        {
            using var mutation = AssemblyLinkMutationGate.Enter();
            var store = services.Repository.Load(doc);
            var other = new AssemblyRecord { Name = created.Name + "-unaffected", NextPartSequence = 2 };
            var part = new PartRecord { Name = "P01", Quantity = 1, MaterialThickness = 1 };
            other.Parts.Add(part);
            AddFixtureOccurrence(doc, services, other, part, 1000, 10);
            store.Assemblies.Add(other);
            services.Repository.Save(doc, store);
            unaffectedId = other.Id;
        }

        var addedGroupId = Guid.Empty;
        var sourceChanged = scenario is "link-master-source-edit" or "link-master-restart" or "link-master-unrelated-assembly";
        try
        {
            services.EnableLinkedAssemblies = false;
            services.LinkEvents.RefreshLinkingPreference();
            Require(!services.LinkSafety.IsEnabled, "The master switch must stop linking.");
            ExpectLinkUpdateBlocked(() => services.LinkEvents.UpdateAssembly(doc, created.Name), "Event/manual updates must reject while disabled.");
            ExpectLinkUpdateBlocked(() => services.LinkEvents.StageComponentUpdate(doc, created.Name,
                    before.Components[0].Id, before.LinkGraph.SourceComponentInstances[0].GeneratedGroupId),
                "Component staging must reject while the master switch is disabled.");
            ExpectLinkUpdateBlocked(() => services.ReferenceUpdates.RefreshAssemblyReferences(doc, created.Name), "Direct manual refresh must reject while disabled.");
            Require(services.ReferenceUpdates.RefreshDescendantsFromSources(doc, sources) == 0, "Background refresh must do no work while disabled.");
            if (sourceChanged)
            {
                using var revised = BoxAt(0, 12);
                Require(doc.Objects.Replace(sources[0], revised), "The disabled-mode source edit must succeed.");
            }
            else if (scenario == "link-master-group-change")
            {
                using var extra = BoxAt(600, 4);
                addedGroupId = doc.Objects.AddBrep(extra);
                var group = doc.Groups.FindId(before.LinkGraph.SourceComponentInstances[0].SourceGroupId);
                Require(doc.Groups.AddToGroup(group.Index, addedGroupId), "The disabled-mode group addition must succeed.");
            }
            PumpIdle(core, services);
            Require(JsonSerializer.Serialize(services.Repository.Load(doc).FindAssembly(created.Name)!.LinkGraph) == beforeGraph,
                "The master switch must prevent event-driven metadata changes while off.");
            Require(doc.Objects.FindId(target.ObjectId).Geometry.GetBoundingBox(true).Equals(targetBounds), "Disabled tracking must not move or regenerate descendants.");

            services.EnableLinkedAssemblies = true;
            if (scenario == "link-master-restart")
            {
                var restarted = new LinkedAssemblySafetyService(services.Repository, () => true);
                restarted.ObserveDocument(doc);
                Require(restarted.IsAssemblyBlocked(doc, created.Id), "A fresh service must read the persisted disabled-mode baseline and protect changed assemblies.");
                ExpectLinkUpdateBlocked(() => restarted.EnsureCanUpdate(doc, created.Id), "A restart must not authorize stale placements.");
            }
            services.LinkEvents.RefreshLinkingPreference();
            Require(services.AutomaticallyPropagate, "The master switch must not overwrite the separate automatic preference.");
            if (scenario == "link-master-unchanged")
            {
                Require(!services.LinkSafety.IsAssemblyBlocked(doc, created.Id), "An unchanged assembly must resume without being recreated.");
                services.LinkSafety.EnsureCanUpdate(doc, created.Id);
            }
            else
            {
                Require(services.LinkSafety.IsAssemblyBlocked(doc, created.Id), "An assembly changed while off must remain protected after reenable.");
                ExpectLinkUpdateBlocked(() => services.LinkEvents.UpdateAssembly(doc, created.Name), "Manual update must not overwrite a changed suspended assembly.");
                ExpectLinkUpdateBlocked(() => services.ReferenceUpdates.RefreshAssemblyReferences(doc, created.Name), "Direct refresh must preserve changed suspended geometry.");
                if (unaffectedId != Guid.Empty)
                {
                    Require(!services.LinkSafety.IsAssemblyBlocked(doc, unaffectedId), "An unchanged assembly in the same document must resume independently.");
                    services.LinkSafety.EnsureCanUpdate(doc, unaffectedId);
                }
                Require(services.Repository.Load(doc).FindAssembly(created.Name)!.LinkGraph.Conflicts.Any(conflict => conflict.ConflictType == "LinkTrackingSuspended" && conflict.Status == AssemblyLinkStatuses.Open),
                    "The Assembly Manager issue count must expose the suspension guard.");
                using (AssemblyLinkMutationGate.Enter())
                {
                    if (sourceChanged)
                        Require(doc.Objects.Replace(sources[0], (Brep)originalSource), "Restore the source geometry for safe resume.");
                    if (addedGroupId != Guid.Empty)
                        Require(doc.Objects.Delete(addedGroupId, true), "Remove the disabled-mode group addition to restore the baseline.");
                }
                services.LinkSafety.EnsureCanUpdate(doc, created.Id);
                Require(!services.LinkSafety.IsAssemblyBlocked(doc, created.Id), "Restoring the exact baseline must safely release the suspension guard.");
            }
        }
        finally
        {
            services.EnableLinkedAssemblies = true;
            services.LinkEvents.RefreshLinkingPreference();
        }
    }

    private static void ExpectLinkUpdateBlocked(Action action, string message)
    {
        try
        {
            action();
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("disabled", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        throw new InvalidOperationException(message);
    }

    private static void RunNewLinkedAssemblySafetyScenario(RhinoCore core, RegressionServices services, string scenario, List<RhinoDoc> fixtureDocs)
    {
        var doc = RhinoDoc.Create(null);
        fixtureDocs.Add(doc);
        doc.ModelUnitSystem = UnitSystem.Inches;
        doc.ModelAbsoluteTolerance = 0.001;
        (AssemblyRecord Assembly, Guid SourceId) CreateKnownAssembly(string name, double x)
        {
            using var mutation = AssemblyLinkMutationGate.Enter();
            var store = services.Repository.Load(doc);
            var assembly = new AssemblyRecord { Name = name, NextPartSequence = 2 };
            var part = new PartRecord { Name = "P01", Quantity = 1, MaterialThickness = 1 };
            assembly.Parts.Add(part);
            var sourceId = AddFixtureOccurrence(doc, services, assembly, part, x, 10);
            store.Assemblies.Add(assembly);
            services.Repository.Save(doc, store);
            services.LinkSafety.RegisterCreatedAssembly(doc, assembly.Id);
            return (assembly, sourceId);
        }
        var createdOff = scenario == "link-master-created-off";
        (AssemblyRecord Assembly, Guid SourceId) first = default;
        try
        {
            if (!createdOff)
                first = CreateKnownAssembly(scenario + "-old", 0);
            services.EnableLinkedAssemblies = false;
            services.LinkEvents.RefreshLinkingPreference();
            if (createdOff)
                first = CreateKnownAssembly(scenario + "-new", 0);
            using var original = doc.Objects.FindId(first.SourceId).Geometry.Duplicate();
            using (var changed = BoxAt(0, 12))
                Require(doc.Objects.Replace(first.SourceId, changed), "Edit the known assembly while tracking is disabled.");
            PumpIdle(core, services);
            services.EnableLinkedAssemblies = true;
            services.LinkEvents.RefreshLinkingPreference();
            Require(services.LinkSafety.IsAssemblyBlocked(doc, first.Assembly!.Id), "An assembly created while off must not bypass protection for its subsequent untracked edits.");
            if (!createdOff)
            {
                var recreated = CreateKnownAssembly(scenario + "-replacement", 300);
                Require(!services.LinkSafety.IsAssemblyBlocked(doc, recreated.Assembly.Id), "A newly generated assembly may resume despite an unrelated unresolved suspension record.");
                services.LinkSafety.EnsureCanUpdate(doc, recreated.Assembly.Id);
                services.LinkEvents.UpdateAssembly(doc, recreated.Assembly.Name);
                Require(services.LinkSafety.IsAssemblyBlocked(doc, first.Assembly.Id), "Registering a known new assembly must not release older unsafe baselines.");
            }
            using (AssemblyLinkMutationGate.Enter())
                Require(doc.Objects.Replace(first.SourceId, (Brep)original), "Restore the original source for cleanup.");
            services.LinkSafety.EnsureCanUpdate(doc, first.Assembly.Id);
        }
        finally
        {
            services.EnableLinkedAssemblies = true;
            services.LinkEvents.RefreshLinkingPreference();
        }
    }

    private static void RunLinkSuspensionFileRoundtrip(RegressionServices services, string scenario, List<RhinoDoc> fixtureDocs)
    {
        var directory = Path.Combine(Path.GetTempPath(), "GazelleLinkRoundtrip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var unchangedPath = Path.Combine(directory, "unchanged.3dm");
        var changedPath = Path.Combine(directory, "changed.3dm");
        services.LinkEvents.Stop();
        var doc = RhinoDoc.Create(null);
        fixtureDocs.Add(doc);
        doc.ModelUnitSystem = UnitSystem.Inches;
        doc.ModelAbsoluteTolerance = 0.001;
        var created = CreateFixture(doc, services, scenario + "-grouped", out var sources);
        using var original = doc.Objects.FindId(sources[0]).Geometry.Duplicate();
        try
        {
            var suspended = new LinkedAssemblySafetyService(services.Repository, () => false);
            suspended.ObserveDocument(doc);
            var baseline = doc.Strings.GetValue(AssemblyManagerConstants.StoreSection, "LinkedAssemblySuspension");
            Require(!string.IsNullOrWhiteSpace(baseline), "The live fixture must contain a suspension snapshot before saving.");
            var serialization = new Rhino.FileIO.SerializationOptions { WriteUserData = true };
            foreach (var property in new[] { "WriteRenderMeshes", "WriteAnalysisMeshes" })
                typeof(Rhino.FileIO.SerializationOptions).GetProperty(property)?.SetValue(serialization, false);
            var sourceGeometryBefore = doc.Objects.FindId(sources[0]).Geometry.ToJSON(serialization);
            var options = new Rhino.FileIO.FileWriteOptions
            {
                FileVersion = 8,
                WriteUserData = true,
                UpdateDocumentPath = false,
                SuppressDialogBoxes = true,
                SuppressAllInput = true,
                IncludeRenderMeshes = false,
                IncludePreviewImage = false
            };
            Console.WriteLine("link-master-file-roundtrip: saving unchanged fixture");
            Require(doc.Write3dmFile(unchangedPath, options), "Write the disposable unchanged suspension fixture as a native 3dm.");
            Console.WriteLine("link-master-file-roundtrip: reading unchanged file storage");
            using (var file = Rhino.FileIO.File3dm.Read(unchangedPath)
                   ?? throw new InvalidOperationException("The disposable unchanged 3dm must be readable."))
            {
                Require(file.Strings.GetValue(AssemblyManagerConstants.StoreSection, "LinkedAssemblySuspension") == baseline,
                    "Native 3dm storage must preserve the exact suspension baseline JSON.");
                Require(file.Settings.ModelUnitSystem == doc.ModelUnitSystem && file.Settings.ModelAbsoluteTolerance == doc.ModelAbsoluteTolerance,
                    "Native 3dm storage must preserve the snapshot's unit and tolerance inputs.");
                foreach (var id in created.LinkGraph.Nodes.Select(node => node.ObjectId).Distinct())
                {
                    var live = doc.Objects.FindId(id);
                    var stored = file.Objects.Single(obj => obj.Attributes.ObjectId == id);
                    Require(stored.Geometry.ToJSON(serialization) == live.Geometry.ToJSON(serialization),
                        "Each linked geometry serialization must round-trip unchanged in native 3dm storage.");
                    Require(stored.Attributes.ToJSON(serialization) == live.Attributes.ToJSON(serialization),
                        "Each linked object's UUID, attributes and recovery tags must round-trip unchanged.");
                }
                foreach (var groupId in created.LinkGraph.SourceComponentInstances.SelectMany(instance =>
                             new[] { instance.SourceGroupId, instance.GeneratedGroupId }).Where(id => id != Guid.Empty).Distinct())
                {
                    var liveGroup = doc.Groups.FindId(groupId);
                    var storedGroup = file.AllGroups.FindIndex(liveGroup.Index);
                    Require(storedGroup is not null && storedGroup.Id == liveGroup.Id && storedGroup.Name == liveGroup.Name,
                        "Native 3dm storage must preserve tracked group identities and names.");
                    Require(file.AllGroups.GroupMembers(liveGroup.Index).Select(obj => obj.Attributes.ObjectId).OrderBy(id => id)
                            .SequenceEqual(doc.Groups.GroupMembers(liveGroup.Index).Select(obj => obj.Id).OrderBy(id => id)),
                        "Native 3dm storage must preserve exact tracked group membership.");
                }
            }

            using (var revised = BoxAt(0, 12))
                Require(doc.Objects.Replace(sources[0], revised), "Edit the source while linking is disabled in the fixture.");
            Console.WriteLine("link-master-file-roundtrip: saving changed fixture");
            Require(doc.Write3dmFile(changedPath, options), "Write the disposable changed suspension fixture as a native 3dm.");
            Console.WriteLine("link-master-file-roundtrip: reading changed file storage");
            using (var file = Rhino.FileIO.File3dm.Read(changedPath)
                   ?? throw new InvalidOperationException("The disposable changed 3dm must be readable."))
            {
                Require(file.Strings.GetValue(AssemblyManagerConstants.StoreSection, "LinkedAssemblySuspension") == baseline,
                    "Saving a disabled-mode edit must preserve the original suspension baseline, not replace it with edited geometry.");
                var stored = file.Objects.Single(obj => obj.Attributes.ObjectId == sources[0]);
                Require(stored.Geometry.ToJSON(serialization) != sourceGeometryBefore
                        && stored.Geometry.ToJSON(serialization) == doc.Objects.FindId(sources[0]).Geometry.ToJSON(serialization),
                    "The edited source must persist as changed geometry beside the unchanged safety baseline.");
            }
        }
        finally
        {
            using (AssemblyLinkMutationGate.Enter())
                doc.Objects.Replace(sources[0], (Brep)original);
            services.LinkEvents.Start();
            // Only these two files and this unique, owned directory are disposable.
            if (File.Exists(unchangedPath)) File.Delete(unchangedPath);
            if (File.Exists(changedPath)) File.Delete(changedPath);
            if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }
    }
}
