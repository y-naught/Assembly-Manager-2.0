using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using AssemblyManagerPlugin.Core;
using AssemblyManagerPlugin.Geometry;
using AssemblyManagerPlugin.Services;
using Rhino;
using Rhino.Geometry;
using Rhino.DocObjects;
using Rhino.Runtime.InProcess;

internal static partial class Program
{
    private static string _rhinoSystemPath = string.Empty;
    private static string _pluginPath = string.Empty;
    private static string? _inspectionPath;
    private static string? _modelRegressionPath;
    private static int _replacementEventCount;
    private static int _idleEventCount;

    [STAThread]
    private static int Main(string[] args)
    {
        _rhinoSystemPath = System.Environment.GetEnvironmentVariable("GAZELLE_TEST_RHINO_SYSTEM")
            ?? @"C:\Program Files\Rhino 8\System";
        if (args.FirstOrDefault() == "--inspect")
        {
            _inspectionPath = Path.GetFullPath(args[1]);
            args = args.Skip(2).ToArray();
        }
        if (args.FirstOrDefault() == "--model-regression")
        {
            _modelRegressionPath = Path.GetFullPath(args[1]);
            args = args.Skip(2).ToArray();
        }
        _pluginPath = Path.GetFullPath(args.FirstOrDefault()
            ?? Path.Combine(AppContext.BaseDirectory, "../../../../../AssemblyManagerPlugin/bin/Release/net7.0/Gazelle.rhp"));
        if (!File.Exists(_pluginPath))
        {
            Console.Error.WriteLine($"Plugin not found: {_pluginPath}");
            return 2;
        }

        SetDllDirectory(_rhinoSystemPath);
        AssemblyLoadContext.Default.Resolving += ResolveAssembly;
        try
        {
            return Run();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static Assembly? ResolveAssembly(AssemblyLoadContext context, AssemblyName name)
    {
        var path = name.Name == "Gazelle" ? _pluginPath : Path.Combine(_rhinoSystemPath, name.Name + ".dll");
        return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Run()
    {
        Console.WriteLine($"Starting isolated RhinoCore; plugin {_pluginPath}");
        using var core = new RhinoCore(new[] { "/NOSPLASH", "/NOTEMPLATE" }, WindowStyle.Hidden);
        var result = _inspectionPath != null ? InspectModel(_inspectionPath)
            : _modelRegressionPath != null ? RunModelRegression(core, _modelRegressionPath)
            : RunScenarios(core);
        Console.Out.Flush();
        Console.Error.Flush();
        // RhinoCore owns a native window loop even when hidden; Dispose can wait for a
        // GUI shutdown acknowledgement in a console host. This executable contains only
        // unsaved disposable fixtures, so end its process with the assertion exit code.
        TerminateProcess(GetCurrentProcess(), (uint)result);
        return result;
    }

    private static int InspectModel(string path)
    {
        using var file = Rhino.FileIO.File3dm.Read(path) ?? throw new InvalidOperationException("The supplied model must be readable.");
        var json = file.Strings.GetValue(AssemblyManagerConstants.StoreSection, AssemblyManagerConstants.StoreEntry);
        Require(!string.IsNullOrWhiteSpace(json), "The supplied model must contain Assembly Manager data.");
        var raw = JsonSerializer.Deserialize<AssemblyStore>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Console.WriteLine($"Read-only raw inspection: {path}; schema {raw.SchemaVersion}; {raw.Assemblies.Count} assemblies");
        foreach (var assembly in raw.Assemblies)
        {
            Console.WriteLine($"Assembly {assembly.Name}: {assembly.Parts.Count} parts, {assembly.Components.Count} components, {assembly.LinkGraph.Nodes.Count} nodes, {assembly.LinkGraph.Edges.Count} edges");
            foreach (var conflict in assembly.LinkGraph.Conflicts.Where(conflict => conflict.Status == AssemblyLinkStatuses.Open))
                Console.WriteLine($"OPEN {JsonSerializer.Serialize(conflict)}");
            foreach (var part in assembly.Parts)
                Console.WriteLine($"PART {part.Name}: id={part.Id} quantity={part.Quantity} sources={part.SourceObjectIds.Count} originals={part.GeneratedObjectIds.Count} fingerprint={part.GeometryFingerprint}");
        }
        using var doc = RhinoDoc.OpenHeadless(path) ?? throw new InvalidOperationException("The supplied model must open for read-only geometry inspection.");
        var fingerprints = new GeometryFingerprintService();
        foreach (var assembly in raw.Assemblies)
        {
            foreach (var node in assembly.LinkGraph.Nodes)
            {
                var obj = doc.Objects.FindId(node.ObjectId);
                var part = assembly.Parts.FirstOrDefault(part => part.Id == node.PartId);
                var supported = obj != null && fingerprints.TryCreatePartCandidate(obj, out _);
                var currentFingerprint = obj?.Geometry is Brep brep ? fingerprints.CreatePartFingerprint(brep) : string.Empty;
                Console.WriteLine($"NODE {node.Id}: role={node.Role} part={part?.Name ?? "-"} object={node.ObjectId} present={obj != null} supported={supported} storedPartMatch={currentFingerprint == part?.GeometryFingerprint} nodeFingerprintMatch={currentFingerprint == node.GeometryFingerprint} layer={(obj == null ? "-" : doc.Layers[obj.Attributes.LayerIndex].FullPath)}");
                if (obj?.Geometry is Brep candidateBrep)
                    Console.WriteLine($"  BREP closed={candidateBrep.IsSolid} valid={candidateBrep.IsValid} faces={candidateBrep.Faces.Count} bounds={candidateBrep.GetBoundingBox(true)} fingerprint={currentFingerprint}");
            }
            foreach (var edge in assembly.LinkGraph.Edges)
                Console.WriteLine($"EDGE {JsonSerializer.Serialize(edge)}");
            var candidates = assembly.LinkGraph.Nodes.Where(node => node.Role == AssemblyLinkRoles.Source)
                .Select(node => (Node: node, Object: doc.Objects.FindId(node.ObjectId)))
                .Where(item => item.Object != null)
                .Select(item => (item.Node, Candidate: fingerprints.TryCreatePartCandidate(item.Object, out var candidate) ? candidate : null))
                .Where(item => item.Candidate != null).ToList();
            foreach (var item in candidates.Where(item => assembly.Parts.Single(part => part.Id == item.Node.PartId).Name == "P01"))
            {
                var matches = candidates.Where(other => other.Node.Id != item.Node.Id
                    && fingerprints.AreEquivalentParts(item.Candidate!, other.Candidate!));
                Console.WriteLine($"MATCHES {item.Node.ObjectId} fingerprint={item.Candidate!.Fingerprint}: {string.Join(", ", matches.Select(other => $"{assembly.Parts.Single(part => part.Id == other.Node.PartId).Name}/{other.Node.ObjectId}/{other.Candidate!.Fingerprint}"))}");
            }
            foreach (var item in candidates)
                item.Candidate!.Geometry.Dispose();
        }
        doc.Modified = false;
        return 0;
    }

    private static int RunModelRegression(RhinoCore core, string path)
    {
        var diskHashBefore = HashFile(path);
        var failures = 0;
        foreach (var mode in new[] { "source", "original", "saved-shape-recovery" })
        {
            var editOriginal = mode == "original";
            var scenario = "supplied-model-" + mode;
            try
            {
                // The file is only read. All replacement/metadata updates below exist solely
                // in this disposable headless document. No save or write-file API is called.
                // Loaded documents in this console host do not deliver native callbacks, so
                // these supplied-model cases invoke the same production service entrypoints
                // used by the listeners. The portable fixtures test native event capture.
                using var doc = RhinoDoc.OpenHeadless(path) ?? throw new InvalidOperationException("The supplied model must open in the isolated host.");
                var services = new RegressionServices();
                var before = services.Repository.Load(doc).FindAssembly("test assembly")
                    ?? throw new InvalidOperationException("This saved model no longer contains the original 'test assembly' regression fixture. The file may have been edited since it was supplied; no geometry was modified.");
                var sourceId = Guid.Parse("58f80081-809b-4ebf-8523-b3446ec7152b");
                var originalId = Guid.Parse("8c640e9d-8989-4cf8-9e21-cb4df6c9b771");
                var unchangedSourceId = Guid.Parse("79aad77b-90ac-4146-b909-04b7e0d90393");
                var originalObject = doc.Objects.FindId(originalId)
                    ?? throw new InvalidOperationException("The original saved-model regression object is absent. Use the original reproduction file; no geometry was modified.");
                var oldOriginalLayer = doc.Layers[originalObject.Attributes.LayerIndex];
                var oldOriginalLayerPath = oldOriginalLayer.FullPath;
                var oldOriginalColor = oldOriginalLayer.Color.ToArgb();
                var oldOriginalWasOnlyObject = doc.Objects.FindByLayer(oldOriginalLayer).All(obj => obj.Id == originalId);
                var initialPartId = before.Parts.Single(part => part.Name == "P01").Id;
                var initialTotal = before.Parts.Sum(part => part.Quantity);
                var initialObjectIds = before.LinkGraph.Nodes.Select(node => node.ObjectId).OrderBy(id => id).ToArray();
                var editedId = editOriginal ? originalId : sourceId;
                try
                {
                    if (mode == "saved-shape-recovery")
                        services.ReferenceUpdates.RefreshAssemblyReferences(doc, before.Name);
                    else
                    {
                        using var replacement = ((Brep)doc.Objects.FindId(editedId).Geometry).DuplicateBrep();
                        var bounds = replacement.GetBoundingBox(true);
                        Require(replacement.Transform(Transform.Scale(new Plane(bounds.Min, Vector3d.ZAxis), 1, 1, 1.05)),
                            "The supplied-model disposable edit must transform successfully.");
                        Require(doc.Objects.Replace(editedId, replacement), "The supplied-model disposable edit must replace successfully.");
                        if (editOriginal)
                            services.ReferenceUpdates.PromoteOriginalAssemblyEdits(doc,
                                new[] { (before.Id, before.LinkGraph.Nodes.Single(node => node.ObjectId == originalId).Id) });
                        else
                            services.ReferenceUpdates.RefreshDescendantsFromSource(doc, sourceId);
                    }
                    Console.WriteLine($"{scenario}: production service entrypoint; sourceHeight={doc.Objects.FindId(sourceId).Geometry.GetBoundingBox(true).Diagonal.Z}; originalHeight={doc.Objects.FindId(originalId).Geometry.GetBoundingBox(true).Diagonal.Z}");
                    var after = services.Repository.Load(doc).FindAssembly(before.Name)!;
                    var changedSource = after.LinkGraph.Nodes.Single(node => node.ObjectId == sourceId && node.Role == AssemblyLinkRoles.Source);
                    var changedOriginal = after.LinkGraph.Nodes.Single(node => node.ObjectId == originalId && node.Role == AssemblyLinkRoles.OriginalAssembly);
                    Console.WriteLine($"{scenario}: before P01={before.Parts.Single(part => part.Id == initialPartId).Quantity}; after {string.Join(", ", after.Parts.Select(part => $"{part.Name}={part.Quantity}"))}");
                    var openConflicts = after.LinkGraph.Conflicts.Where(conflict => conflict.Status == AssemblyLinkStatuses.Open).ToList();
                    Console.WriteLine($"{scenario}: open link issues={openConflicts.Count}");
                    foreach (var conflict in openConflicts)
                        Console.WriteLine($"  conflict {conflict.ConflictType}: {conflict.Message}");
                    Require(changedSource.PartId != initialPartId && changedOriginal.PartId == changedSource.PartId,
                        "The edited source and its original must receive a new shared category despite unrelated tolerance ambiguity.");
                    var changedPart = after.Parts.Single(part => part.Id == changedSource.PartId);
                    Require(changedPart.Quantity == 1, "The edited supplied-model part must have quantity one.");
                    Require(openConflicts.Count == 0, "The saved tolerance ambiguity must resolve without creating additional issues in this model.");
                    Require(after.LinkGraph.Nodes.Single(node => node.ObjectId == unchangedSourceId && node.Role == AssemblyLinkRoles.Source).PartId == initialPartId,
                        "An unchanged canonical P01 occurrence must retain P01 despite overlap with another category.");
                    Require(after.Parts.Count == before.Parts.Count + 1 && after.Parts.Single(part => part.Id == initialPartId).Quantity == 11,
                        "The supplied-model edit must produce exactly eleven P01s and one new part category.");
                    Require(before.Parts.Where(part => part.Id != initialPartId).All(part =>
                        after.Parts.Any(current => current.Id == part.Id && current.Name == part.Name && current.Quantity == part.Quantity)),
                        "Every unrelated supplied-model part must retain its identity, number and quantity.");
                    Require(before.LinkGraph.Nodes.Where(node => node.ObjectId != sourceId && node.ObjectId != originalId).All(node =>
                        after.LinkGraph.Nodes.Single(current => current.Id == node.Id).PartId == node.PartId),
                        "Every unchanged supplied-model occurrence must retain its existing part category.");
                    Require(doc.Layers[doc.Objects.FindId(originalId).Attributes.LayerIndex].FullPath.EndsWith("::" + changedPart.Name, StringComparison.Ordinal),
                        "The edited original must move to its newly numbered part layer.");
                    Require(doc.Layers[doc.Objects.FindId(originalId).Attributes.LayerIndex].Color.ToArgb() != oldOriginalColor,
                        "The supplied-model new part must receive a different layer color from P01.");
                    if (oldOriginalWasOnlyObject && doc.Layers.CurrentLayerIndex != oldOriginalLayer.Index
                        && services.Layers.GetChildLayerPaths(doc, oldOriginalLayerPath).Length == 0)
                        Require(services.Layers.FindLayerIndex(doc, oldOriginalLayerPath) < 0,
                            "The supplied-model empty old component part layer must be removed.");
                    Require(after.Parts.Sum(part => part.Quantity) == initialTotal,
                        "Supplied-model recategorization must preserve total assembly quantity.");
                    Require(initialObjectIds.SequenceEqual(after.LinkGraph.Nodes.Select(node => node.ObjectId).OrderBy(id => id)),
                        "Supplied-model recategorization must preserve every linked Rhino UUID.");
                    Require(Math.Abs(doc.Objects.FindId(sourceId).Geometry.GetBoundingBox(true).Diagonal.Z
                        - doc.Objects.FindId(originalId).Geometry.GetBoundingBox(true).Diagonal.Z) < 0.001,
                        "The supplied-model edit must propagate between source and original.");
                    var expectedParts = after.Parts.ToDictionary(part => part.Id, part => (part.Name, part.Quantity));
                    services.ReferenceUpdates.RefreshDescendantsFromSource(doc, sourceId);
                    var repeated = services.Repository.Load(doc).FindAssembly(before.Name)!;
                    Require(repeated.Parts.Count == expectedParts.Count && repeated.Parts.All(part => expectedParts.TryGetValue(part.Id, out var expected)
                        && expected.Name == part.Name && expected.Quantity == part.Quantity),
                        "Repeated supplied-model refresh must preserve category identities and quantities.");
                    Require(repeated.LinkGraph.Conflicts.All(conflict => conflict.Status != AssemblyLinkStatuses.Open),
                        "Repeated supplied-model refresh must retain zero open link issues.");
                    Console.WriteLine($"PASS {scenario}: edited original is now {changedPart.Name}, quantity {changedPart.Quantity}.");
                }
                finally
                {
                    doc.Modified = false;
                }
            }
            catch (Exception exception)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {scenario}: {exception}");
            }
        }
        Require(HashFile(path) == diskHashBefore, "The supplied model on disk must remain byte-for-byte unchanged.");
        Console.WriteLine($"Supplied-model regression result: {3 - failures}/3 passed; original file hash unchanged ({diskHashBefore}).");
        return failures == 0 ? 0 : 1;
    }

    private static string HashFile(string path)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunScenarios(RhinoCore core)
    {
        // Construct production services with default tolerances, without installing/loading
        // the RHP or touching the operator's plug-in settings. Startup is not under test.
        var services = new RegressionServices();
        RhinoDoc.ReplaceRhinoObject += (_, _) => _replacementEventCount++;
        RhinoApp.Idle += (_, _) => _idleEventCount++;
        services.LinkEvents.Start();
        Require(services.LinkEvents.IsStarted, "Automatic link listeners must be started.");

        var failures = 0;
        var fixtureDocs = new List<RhinoDoc>();
        var scenarios = new[]
        {
            "source-replace", "source-scale", "original-replace",
            "source-replace-tolerance-overlap", "original-replace-tolerance-overlap",
            "source-replace-unrelated-missing", "source-replace-unrelated-unsupported",
            "source-replace-grouped-unrelated-missing", "source-replace-grouped-unrelated-unsupported",
            "source-replace-grouped-same-category-missing", "source-replace-grouped-same-category-unsupported",
            "original-replace-grouped-same-category-missing", "original-replace-grouped-same-category-unsupported",
            "source-replace-grouped-no-unchanged-witness-missing",
            "layer-split-empty", "layer-split-sibling", "layer-split-user-geometry",
            "layer-split-hidden-user-geometry", "layer-split-locked-user-geometry", "layer-split-child-layer",
            "layer-split-current-layer", "layer-split-palette-wrap", "layer-merge-custom-color", "layer-component-merge",
            "update-paused-source-manual", "update-paused-original-manual",
            "update-paused-source-reenable", "update-paused-original-reenable", "update-paused-move-original",
            "update-paused-material-manual", "update-flat-material", "update-flat-material-merge", "update-flat-original-material",
            "update-flat-split-representative", "update-flat-split-nonrepresentative",
            "update-flat-move-rotate", "update-flat-thickness", "update-flat-merge", "update-flat-copied-parent",
            "update-settings-compatibility", "update-paused-multiple-original-edits",
            "update-paused-service-restart", "update-paused-competing-edits",
            "update-original-source-material-conflict-before-edit", "update-paused-source-material-conflict-after-edit",
            "update-paused-shared-source", "update-paused-service-restart-reenable", "update-paused-stock-original-edit",
            "update-flat-dependent-active", "update-flat-dependent-inactive",
            "update-feedback-manual-flat", "update-feedback-auto-batch-flat",
            "update-feedback-noop", "update-feedback-document-issues", "update-feedback-failure"
        };
        foreach (var scenario in scenarios)
        {
            try
            {
                if (scenario.StartsWith("update-", StringComparison.Ordinal))
                    RunAssemblyUpdateScenario(core, services, scenario, fixtureDocs);
                else if (scenario.StartsWith("layer-", StringComparison.Ordinal))
                    RunLayerScenario(core, services, scenario, fixtureDocs);
                else
                    RunSplitScenario(core, services, scenario, fixtureDocs);
                Console.WriteLine($"PASS {scenario}");
            }
            catch (Exception exception)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {scenario}: {exception}");
            }
        }

        services.LinkEvents.Stop();
        foreach (var document in fixtureDocs)
            document.Modified = false;
        Console.WriteLine($"Regression result: {scenarios.Length - failures}/{scenarios.Length} passed.");
        return failures == 0 ? 0 : 1;
    }

    private static void RunSplitScenario(RhinoCore core, RegressionServices services, string scenario, List<RhinoDoc> fixtureDocs)
    {
        var doc = RhinoDoc.Create(null);
        fixtureDocs.Add(doc);
        doc.ModelUnitSystem = UnitSystem.Inches;
        doc.ModelAbsoluteTolerance = 0.001;
        var created = CreateFixture(doc, services, scenario, out var sources);
        PumpIdle(core, services);
        var before = services.Repository.Load(doc).FindAssembly(created.Name)!;
        var expectedInitialQuantity = scenario.Contains("no-unchanged-witness", StringComparison.Ordinal) ? 2 : 10;
        Require(before.Parts[0].Name == "P01" && before.Parts[0].Quantity == expectedInitialQuantity,
            $"Fixture must begin with {expectedInitialQuantity} P01 occurrences.");
        var originalPartId = before.Parts[0].Id;
        var editedSource = before.LinkGraph.Nodes.First(node => node.ObjectId == sources[0] && node.Role == AssemblyLinkRoles.Source);
        var originalEdge = before.LinkGraph.Edges.Single(edge => edge.ParentNodeId == editedSource.Id);
        var original = before.LinkGraph.Nodes.Single(node => node.Id == originalEdge.ChildNodeId);
        var editedObjectId = scenario.StartsWith("original-replace", StringComparison.Ordinal) ? original.ObjectId : editedSource.ObjectId;
        var oldBounds = doc.Objects.FindId(editedObjectId).Geometry.GetBoundingBox(true);
        var replacementEventsBefore = _replacementEventCount;
        if (scenario == "source-scale")
        {
            var scaling = Transform.Scale(new Plane(oldBounds.Min, Vector3d.ZAxis), 1.2, 1, 1);
            Require(doc.Objects.Transform(editedObjectId, scaling, true) != Guid.Empty, "Input non-rigid scale must succeed.");
        }
        else
        {
            using var replacement = Brep.CreateFromBox(new BoundingBox(oldBounds.Min, oldBounds.Min + new Vector3d(12, 5, 1)));
            Require(doc.Objects.Replace(editedObjectId, replacement), "Edited object replacement must succeed.");
        }
        PumpIdle(core, services);
        Require(_replacementEventCount > replacementEventsBefore, "The fixture must emit real native Rhino object replacement events.");
        Require(Math.Abs(doc.Objects.FindId(original.ObjectId).Geometry.GetBoundingBox(true).Diagonal.X - 12) < 0.001,
            "Automatic propagation must update the original geometry before recategorization is checked.");

        var after = services.Repository.Load(doc).FindAssembly(created.Name)!;
        Console.WriteLine($"{scenario} categories: {string.Join(", ", after.Parts.Select(part => $"{part.Name}={part.Quantity}"))}");
        foreach (var conflict in after.LinkGraph.Conflicts.Where(conflict => conflict.Status == AssemblyLinkStatuses.Open))
            Console.WriteLine($"  conflict {conflict.ConflictType}: {conflict.Message}");
        if (scenario.Contains("no-unchanged-witness", StringComparison.Ordinal) || scenario.Contains("same-category", StringComparison.Ordinal))
        {
            Require(after.Parts.Count == before.Parts.Count && after.Parts.Single(part => part.Id == originalPartId).Quantity == expectedInitialQuantity,
                "An incomplete P01 category must conservatively retain its identity and all occurrences.");
            Require(after.LinkGraph.Nodes.Where(node => node.Role == AssemblyLinkRoles.OriginalAssembly && node.PartId == originalPartId).Count() == expectedInitialQuantity,
                "An incomplete category must not silently reassign or lose original occurrences.");
            Require(after.LinkGraph.Conflicts.Any(conflict => conflict.Status == AssemblyLinkStatuses.Open
                    && conflict.ConflictType == AssemblyLinkConflictTypes.PartCategorizationChanged),
                "An incomplete category must remain explicitly flagged for review.");
            return;
        }
        var relevantParts = after.Parts.Where(part => part.Name != "P10").ToList();
        Require(relevantParts.Count == 2, "Editing one occurrence must produce two part categories automatically.");
        var unchanged = after.Parts.Single(part => part.Id == originalPartId);
        var changed = relevantParts.Single(part => part.Id != originalPartId);
        Require(unchanged.Name == "P01" && unchanged.Quantity == 9, "Nine unchanged occurrences must retain P01.");
        var nextPartName = before.Parts.Any(part => part.Name == "P10") ? "P11" : "P02";
        Require(changed.Name == nextPartName && changed.Quantity == 1, $"The one edited occurrence must become {nextPartName}.");
        var editedOriginal = after.LinkGraph.Nodes.Single(node => node.Id == original.Id);
        Require(editedOriginal.PartId == changed.Id, "The edited original's stable graph node must point to the new category.");
        Require(editedOriginal.ObjectId == original.ObjectId, "Recategorization must preserve the original object's UUID.");
        var changedObject = doc.Objects.FindId(editedOriginal.ObjectId);
        var changedLayer = doc.Layers[changedObject.Attributes.LayerIndex].FullPath;
        Require(changedLayer.StartsWith("ASSEMBLY MANAGER::ORIGINAL ASSEMBLIES::", StringComparison.Ordinal)
            && changedLayer.EndsWith("::" + nextPartName, StringComparison.Ordinal), $"Edited original must move to a {nextPartName} layer, got {changedLayer}.");
        Require(changedObject.Attributes.Name.Contains(nextPartName, StringComparison.Ordinal), $"Edited original object name must include {nextPartName}.");
        var unchangedOriginals = after.LinkGraph.Nodes.Where(node => node.Role == AssemblyLinkRoles.OriginalAssembly && node.PartId == originalPartId).ToList();
        Require(unchangedOriginals.Count == 9, "Exactly nine original occurrences must still belong to P01.");
        Require(unchangedOriginals.All(node => doc.Layers[doc.Objects.FindId(node.ObjectId).Attributes.LayerIndex].FullPath.EndsWith("::P01", StringComparison.Ordinal)),
            "All nine unchanged originals must stay on P01 layers.");
        if (scenario.Contains("unrelated", StringComparison.Ordinal))
        {
            var oldProtectedPart = before.Parts.Single(part => part.Name == "P10");
            var protectedPart = after.Parts.Single(part => part.Name == "P10");
            Require(protectedPart.Id == oldProtectedPart.Id && protectedPart.Quantity == oldProtectedPart.Quantity,
                "Unrelated incomplete P10 must preserve its identity and quantity.");
            Require(protectedPart.GeneratedObjectIds.OrderBy(id => id).SequenceEqual(oldProtectedPart.GeneratedObjectIds.OrderBy(id => id))
                && protectedPart.GeneratedObjectIds.All(id => doc.Layers[doc.Objects.FindId(id).Attributes.LayerIndex].FullPath.EndsWith("::P10", StringComparison.Ordinal)),
                "Unrelated incomplete P10 must preserve its generated UUIDs and P10 layers.");
        }
        if (scenario.Contains("grouped", StringComparison.Ordinal))
        {
            var changedComponent = after.Components.Single(component => component.Name == "C01");
            Require(changedComponent.PartQuantities.Count == 2 && changedComponent.PartQuantities.GetValueOrDefault("P01") == 9
                && changedComponent.PartQuantities.GetValueOrDefault(nextPartName) == 1,
                "C01 must update its part listing to nine P01 and one new part, retaining the unresolved occurrence.");
            var protectedComponent = after.Components.Single(component => component.Name == "C10");
            var oldProtectedComponent = before.Components.Single(component => component.Name == "C10");
            Require(protectedComponent.Id == oldProtectedComponent.Id && protectedComponent.Quantity == oldProtectedComponent.Quantity
                && protectedComponent.PartQuantities.Count == 1 && protectedComponent.PartQuantities.GetValueOrDefault("P10") == 1,
                "Unrelated incomplete C10 must retain its identity, quantity and P10 listing.");
        }
        var expectedIds = after.Parts.ToDictionary(part => part.Name, part => part.Id);
        var currentSourceObjectId = after.LinkGraph.Nodes.Single(node => node.Id == editedSource.Id).ObjectId;
        services.ReferenceUpdates.RefreshDescendantsFromSource(doc, currentSourceObjectId);
        PumpIdle(core, services);
        var repeated = services.Repository.Load(doc).FindAssembly(created.Name)!;
        Require(repeated.Parts.Count == expectedIds.Count && repeated.Parts.All(part => expectedIds[part.Name] == part.Id),
            "A second automatic refresh must preserve both category names and IDs.");
        Require(repeated.Parts.Single(part => part.Name == "P01").Quantity == 9
            && repeated.Parts.Single(part => part.Name == nextPartName).Quantity == 1,
            "A second automatic refresh must preserve 9/1 quantities.");
    }

    private static void RunLayerScenario(RhinoCore core, RegressionServices services, string scenario, List<RhinoDoc> fixtureDocs)
    {
        var doc = RhinoDoc.Create(null);
        fixtureDocs.Add(doc);
        doc.ModelUnitSystem = UnitSystem.Inches;
        doc.ModelAbsoluteTolerance = 0.001;
        var componentMerge = scenario == "layer-component-merge";
        var assembly = new AssemblyRecord
        {
            Name = "Regression-" + scenario,
            NextPartSequence = scenario.EndsWith("palette-wrap", StringComparison.Ordinal) ? 22 : 3,
            NextComponentSequence = componentMerge ? 4 : 3
        };
        var part = new PartRecord { Name = "P01", Quantity = scenario.EndsWith("sibling", StringComparison.Ordinal) ? 3 : 2, MaterialThickness = 1 };
        var existingPart = new PartRecord { Name = "P02", Quantity = componentMerge ? 2 : 1, MaterialThickness = 1 };
        assembly.Parts.AddRange(new[] { part, existingPart });
        var oldColor = System.Drawing.Color.FromArgb(230, 25, 75);
        var existingColor = System.Drawing.Color.FromArgb(17, 101, 173);
        Guid sourceId;
        Guid originalId;
        Guid copiedId;
        Guid flatId;
        Guid userObjectId = Guid.Empty;
        var oldOriginalPath = LayerService.OriginalPart(assembly.Name, "C01", "P01");
        var oldCopiedPath = LayerService.CopiedComponentPart(assembly.Name, "C01", "P01");
        using (AssemblyLinkMutationGate.Enter())
        {
            sourceId = AddFixtureOccurrence(doc, services, assembly, part, 0, 10);
            var unchangedSource = AddFixtureOccurrence(doc, services, assembly, part, 20, 10);
            var existingSource = AddFixtureOccurrence(doc, services, assembly, existingPart, 50, 15);
            var firstComponentSources = new List<Guid> { sourceId };
            if (scenario.EndsWith("sibling", StringComparison.Ordinal))
                firstComponentSources.Add(AddFixtureOccurrence(doc, services, assembly, part, 80, 10));
            AddLayerFixtureComponent(doc, services, assembly, "C01", firstComponentSources);
            if (componentMerge)
            {
                AddLayerFixtureComponent(doc, services, assembly, "C02", new[] { existingSource });
                var extraExistingSource = AddFixtureOccurrence(doc, services, assembly, existingPart, 80, 15);
                AddLayerFixtureComponent(doc, services, assembly, "C03", new[] { unchangedSource, extraExistingSource });
            }
            else
                AddLayerFixtureComponent(doc, services, assembly, "C02", new[] { unchangedSource, existingSource });
            originalId = assembly.LinkGraph.Nodes.Single(node => node.Role == AssemblyLinkRoles.OriginalAssembly
                && assembly.LinkGraph.Edges.Any(edge => edge.ChildNodeId == node.Id
                    && assembly.LinkGraph.Nodes.Single(parent => parent.Id == edge.ParentNodeId).ObjectId == sourceId)).ObjectId;
            copiedId = AddLayerFixtureOutput(doc, services, assembly, originalId, AssemblyLinkRoles.CopiedComponent,
                oldCopiedPath, Transform.Translation(0, 100, 0));
            flatId = AddLayerFixtureOutput(doc, services, assembly, copiedId, AssemblyLinkRoles.FlatPart,
                LayerService.PartsPart(assembly.Name, "P01") + "::3D", Transform.Translation(0, 100, 0));
            foreach (var original in assembly.LinkGraph.Nodes.Where(node => node.Role == AssemblyLinkRoles.OriginalAssembly
                         && node.ObjectId != originalId).ToList())
            {
                var component = assembly.Components.Single(component => component.Id == original.ComponentId);
                var category = assembly.Parts.Single(category => category.Id == original.PartId);
                var copied = AddLayerFixtureOutput(doc, services, assembly, original.ObjectId, AssemblyLinkRoles.CopiedComponent,
                    LayerService.CopiedComponentPart(assembly.Name, component.Name, category.Name), Transform.Translation(0, 100, 0));
                AddLayerFixtureOutput(doc, services, assembly, copied, AssemblyLinkRoles.FlatPart,
                    LayerService.PartsPart(assembly.Name, category.Name) + "::3D", Transform.Translation(0, 100, 0));
            }
            foreach (var layer in doc.Layers.Where(layer => !layer.IsDeleted && layer.FullPath.StartsWith("ASSEMBLY MANAGER::", StringComparison.Ordinal)).ToList())
            {
                var categoryColor = layer.FullPath.EndsWith("::P02", StringComparison.Ordinal)
                    || layer.FullPath.Contains("::P02::", StringComparison.Ordinal) ? existingColor : oldColor;
                layer.Color = categoryColor;
                Require(doc.Layers[layer.Index].Color.ToArgb() == categoryColor.ToArgb(), "Fixture custom category colors must be applied.");
            }
            if (scenario.EndsWith("user-geometry", StringComparison.Ordinal))
                userObjectId = doc.Objects.AddPoint(new Point3d(0, 75, 0), new ObjectAttributes
                {
                    LayerIndex = services.Layers.FindLayerIndex(doc, oldOriginalPath),
                    Mode = scenario.Contains("hidden", StringComparison.Ordinal) ? ObjectMode.Hidden
                        : scenario.Contains("locked", StringComparison.Ordinal) ? ObjectMode.Locked : ObjectMode.Normal
                });
            if (scenario.EndsWith("child-layer", StringComparison.Ordinal))
                services.Layers.EnsureLayer(doc, oldOriginalPath + "::Operator notes");
            if (scenario.EndsWith("current-layer", StringComparison.Ordinal))
                Require(doc.Layers.SetCurrentLayerIndex(services.Layers.FindLayerIndex(doc, oldOriginalPath), true),
                    "Fixture old part layer must become the current drawing layer.");
            if (scenario == "layer-split-empty")
            {
                var attributes = doc.Objects.FindId(originalId).Attributes.Duplicate();
                attributes.ColorSource = ObjectColorSource.ColorFromObject;
                attributes.ObjectColor = System.Drawing.Color.MediumPurple;
                Require(doc.Objects.ModifyAttributes(originalId, attributes, true), "Fixture object color override must be applied.");
            }
            services.Repository.Save(doc, new AssemblyStore { Assemblies = new List<AssemblyRecord> { assembly } });
        }
        PumpIdle(core, services);
        var originalSourceLayer = doc.Objects.FindId(sourceId).Attributes.LayerIndex;
        var originalSourceColorMode = doc.Objects.FindId(sourceId).Attributes.ColorSource;
        var originalSourceColor = doc.Objects.FindId(sourceId).Attributes.ObjectColor.ToArgb();
        var isMerge = scenario.Contains("merge", StringComparison.Ordinal);
        var replacementEventsBefore = _replacementEventCount;
        using (var replacement = BoxAt(0, isMerge ? 15 : 12))
            Require(doc.Objects.Replace(sourceId, replacement), "Layer fixture source edit must succeed.");
        PumpIdle(core, services);
        Require(_replacementEventCount > replacementEventsBefore, "Layer fixture must traverse native replacement listeners.");
        var after = services.Repository.Load(doc).FindAssembly(assembly.Name)!;
        var changedSource = after.LinkGraph.Nodes.Single(node => node.ObjectId == sourceId);
        var changedPart = after.Parts.Single(category => category.Id == changedSource.PartId);
        var expectedPartName = isMerge ? "P02" : scenario.EndsWith("palette-wrap", StringComparison.Ordinal) ? "P22" : "P03";
        Require(changedPart.Name == expectedPartName, "Layer fixture must recategorize to the expected new or existing category.");
        Require(changedPart.Quantity == (componentMerge ? 3 : isMerge ? 2 : 1), "Layer fixture must preserve correct recategorized quantity.");
        var changedObjectIds = new[] { originalId, copiedId, flatId };
        var changedLayers = changedObjectIds.Select(id => doc.Layers[doc.Objects.FindId(id).Attributes.LayerIndex]).ToList();
        Require(changedLayers.All(layer => layer.FullPath.EndsWith("::" + changedPart.Name, StringComparison.Ordinal)
            || layer.FullPath.EndsWith("::" + changedPart.Name + "::3D", StringComparison.Ordinal)),
            "Original, copied component and flat output must all move to the correct part layer.");
        var changedColor = changedLayers[0].Color.ToArgb();
        Require(changedColor != oldColor.ToArgb(), "An edited part moving into a new or different category must not inherit the old part color.");
        Require(changedLayers.All(layer => layer.Color.ToArgb() == changedColor), "All managed stages of the changed category must share its color.");
        var flatParentLayerIndex = services.Layers.FindLayerIndex(doc, LayerService.PartsPart(assembly.Name, changedPart.Name));
        Require(flatParentLayerIndex >= 0 && doc.Layers[flatParentLayerIndex].Color.ToArgb() == changedColor,
            "The flat part category parent and its 3D leaf must display the same category color.");
        var updatedSourceAttributes = doc.Objects.FindId(sourceId).Attributes;
        Require(updatedSourceAttributes.LayerIndex == originalSourceLayer && updatedSourceAttributes.ColorSource == originalSourceColorMode
            && updatedSourceAttributes.ObjectColor.ToArgb() == originalSourceColor,
            "Managed category layer styling must not change input geometry layer or color attributes.");
        if (scenario == "layer-split-empty")
            Require(doc.Objects.FindId(originalId).Attributes.ColorSource == ObjectColorSource.ColorFromObject
                && doc.Objects.FindId(originalId).Attributes.ObjectColor.ToArgb() == System.Drawing.Color.MediumPurple.ToArgb(),
                "Changing a category layer color must preserve explicit user object color overrides.");
        if (isMerge)
        {
            Require(changedColor == existingColor.ToArgb(), "A merge must reuse the existing category's custom layer color.");
            Require(after.LinkGraph.Nodes.Where(node => node.PartId == existingPart.Id && node.Role != AssemblyLinkRoles.Source)
                .All(node => doc.Layers[doc.Objects.FindId(node.ObjectId).Attributes.LayerIndex].Color.ToArgb() == existingColor.ToArgb()),
                "Existing category occurrences must retain their custom color after a merge.");
        }
        if (componentMerge)
        {
            Require(after.Components.All(component => component.Name != "C01")
                && after.Components.Single(component => component.Name == "C02").Quantity == 2,
                "Matching the existing component must retire C01 and retain two C02 occurrences.");
            Require(changedSource.ComponentId == after.Components.Single(component => component.Name == "C02").Id,
                "The edited occurrence must inherit the existing C02 component identity.");
            Require(changedLayers[0].FullPath == LayerService.OriginalPart(assembly.Name, "C02", "P02")
                && changedLayers[1].FullPath == LayerService.CopiedComponentPart(assembly.Name, "C02", "P02"),
                "Merged originals and copied outputs must move to C02's existing P02 layers.");
        }
        var retainOriginal = scenario.EndsWith("sibling", StringComparison.Ordinal)
            || scenario.EndsWith("user-geometry", StringComparison.Ordinal) || scenario.EndsWith("child-layer", StringComparison.Ordinal)
            || scenario.EndsWith("current-layer", StringComparison.Ordinal);
        Require((services.Layers.FindLayerIndex(doc, oldOriginalPath) >= 0) == retainOriginal,
            retainOriginal ? "An old original layer containing a sibling, user geometry or child layer must be retained."
                : "The emptied old original part leaf must be removed from its component.");
        Require((services.Layers.FindLayerIndex(doc, oldCopiedPath) >= 0) == scenario.EndsWith("sibling", StringComparison.Ordinal),
            "An emptied copied part leaf must be removed, but a leaf with another P01 must remain.");
        Require(services.Layers.FindLayerIndex(doc, LayerService.OriginalComponent(assembly.Name, "C01")) >= 0,
            "Empty part cleanup must not remove the component parent, even if that component category retires.");
        Require(services.Layers.FindLayerIndex(doc, LayerService.OriginalPart(assembly.Name, componentMerge ? "C03" : "C02", "P01")) >= 0,
            "P01 in a different component must retain its layer.");
        if (userObjectId != Guid.Empty)
            Require(doc.Objects.FindId(userObjectId) != null
                && doc.Layers[doc.Objects.FindId(userObjectId).Attributes.LayerIndex].FullPath == oldOriginalPath,
                "Cleanup must preserve user geometry and its original layer assignment.");
        if (scenario.EndsWith("child-layer", StringComparison.Ordinal))
            Require(services.Layers.FindLayerIndex(doc, oldOriginalPath + "::Operator notes") >= 0,
                "Cleanup must preserve custom child layers.");
        if (scenario.EndsWith("current-layer", StringComparison.Ordinal))
            Require(doc.Layers.CurrentLayer.FullPath == oldOriginalPath, "Cleanup must preserve the current drawing layer selection.");
        var expectedLayerIds = changedLayers.Select(layer => layer.Id).ToArray();
        services.ReferenceUpdates.RefreshDescendantsFromSource(doc, sourceId);
        PumpIdle(core, services);
        Require(changedObjectIds.Select(id => doc.Layers[doc.Objects.FindId(id).Attributes.LayerIndex].Id).SequenceEqual(expectedLayerIds),
            "Repeated refresh must preserve the recategorized layer identities.");
        Require(changedObjectIds.All(id => doc.Layers[doc.Objects.FindId(id).Attributes.LayerIndex].Color.ToArgb() == changedColor),
            "Repeated refresh must not change category colors.");
        if (scenario == "layer-split-empty")
        {
            // Recreate a legacy empty leaf after categorization is already complete. This
            // models stale layers left by older versions without another geometry edit.
            using (AssemblyLinkMutationGate.Enter())
            {
                services.Layers.EnsureLayer(doc, oldOriginalPath, oldColor);
                services.Layers.EnsureLayer(doc, oldCopiedPath, oldColor);
            }
            services.ReferenceUpdates.RefreshAssemblyReferences(doc, assembly.Name);
            PumpIdle(core, services);
            Require(services.Layers.FindLayerIndex(doc, oldOriginalPath) < 0 && services.Layers.FindLayerIndex(doc, oldCopiedPath) < 0,
                "Refresh References must also clean stale empty managed part leaves from a previously completed recategorization.");
        }
    }

    private static void AddLayerFixtureComponent(RhinoDoc doc, RegressionServices services, AssemblyRecord assembly,
        string name, IEnumerable<Guid> sourceObjects)
    {
        var sourceIds = sourceObjects.ToList();
        var sourceNodes = assembly.LinkGraph.Nodes.Where(node => node.Role == AssemblyLinkRoles.Source && sourceIds.Contains(node.ObjectId)).ToList();
        var originalNodes = sourceNodes.Select(source => assembly.LinkGraph.Nodes.Single(node =>
            assembly.LinkGraph.Edges.Any(edge => edge.ParentNodeId == source.Id && edge.ChildNodeId == node.Id))).ToList();
        var quantities = sourceNodes.GroupBy(node => assembly.Parts.Single(part => part.Id == node.PartId).Name)
            .ToDictionary(group => group.Key, group => group.Count());
        var component = new ComponentRecord
        {
            Name = name,
            Quantity = 1,
            PartNames = quantities.Keys.ToList(),
            PartQuantities = quantities,
            ObjectIds = originalNodes.Select(node => node.ObjectId).ToList(),
            RepresentativeObjectIdsByPartName = originalNodes.GroupBy(node => assembly.Parts.Single(part => part.Id == node.PartId).Name)
                .ToDictionary(group => group.Key, group => group.Select(node => node.ObjectId).ToList())
        };
        var sourceGroupName = assembly.Name + "-source-" + name;
        var originalGroupName = assembly.Name + "-original-" + name;
        var sourceGroupIndex = doc.Groups.Add(sourceGroupName, sourceIds);
        doc.Groups.Add(originalGroupName, component.ObjectIds);
        component.InstanceGroupNames.Add(originalGroupName);
        assembly.Components.Add(component);
        var instance = services.Lineage.RegisterSourceComponentInstance(doc, assembly, component, sourceGroupIndex,
            sourceGroupName, sourceIds, originalGroupName);
        var candidates = new List<PartCandidate>();
        foreach (var source in sourceNodes)
        {
            Require(services.Fingerprints.TryCreatePartCandidate(doc.Objects.FindId(source.ObjectId), out var candidate), "Layer component source must be supported.");
            candidate.PartName = assembly.Parts.Single(part => part.Id == source.PartId).Name;
            candidates.Add(candidate);
        }
        component.Fingerprint = services.Fingerprints.CreateComponentFingerprint(candidates);
        foreach (var candidate in candidates)
            candidate.Geometry.Dispose();
        foreach (var node in sourceNodes.Concat(originalNodes))
        {
            node.ComponentId = component.Id;
            node.SourceComponentInstanceId = instance.Id;
            if (node.Role != AssemblyLinkRoles.OriginalAssembly)
                continue;
            var category = assembly.Parts.Single(part => part.Id == node.PartId);
            services.Layers.MoveObjectToLayer(doc, node.ObjectId, LayerService.OriginalPart(assembly.Name, name, category.Name));
            assembly.GeometryReferences.Single(reference => reference.TargetObjectId == node.ObjectId).ComponentName = name;
        }
    }

    private static Guid AddLayerFixtureOutput(RhinoDoc doc, RegressionServices services, AssemblyRecord assembly,
        Guid parentId, string role, string layerPath, Transform transform)
    {
        var parent = assembly.LinkGraph.Nodes.Single(node => node.ObjectId == parentId);
        var part = assembly.Parts.Single(part => part.Id == parent.PartId);
        using var geometry = doc.Objects.FindId(parentId).Geometry.Duplicate();
        Require(geometry.Transform(transform), "Downstream layer fixture transform must succeed.");
        var objectId = doc.Objects.Add(geometry, new ObjectAttributes
        { Name = part.Name, LayerIndex = services.Layers.EnsureLayerIndex(doc, layerPath) });
        Require(objectId != Guid.Empty, "Downstream layer fixture object must be created.");
        var link = services.Lineage.RegisterDerived(doc, assembly, parentId, parent.Role, objectId, role, transform,
            partId: part.Id, componentId: parent.ComponentId, sourceComponentInstanceId: parent.SourceComponentInstanceId);
        link.Child.GeometryFingerprint = part.GeometryFingerprint;
        if (role == AssemblyLinkRoles.FlatPart)
            part.CamObjectIds.Add(objectId);
        return objectId;
    }

    private static AssemblyRecord CreateFixture(RhinoDoc doc, RegressionServices services, string scenario, out Guid[] sources)
    {
        using var gate = AssemblyLinkMutationGate.Enter();
        var assembly = new AssemblyRecord { Name = "Regression-" + scenario, NextPartSequence = 2 };
        var initialQuantity = scenario.Contains("no-unchanged-witness", StringComparison.Ordinal) ? 2 : 10;
        var part = new PartRecord { Name = "P01", Quantity = initialQuantity, MaterialThickness = 1 };
        assembly.Parts.Add(part);
        var sourceIds = new List<Guid>();
        for (var index = 0; index < initialQuantity; index++)
        {
            var length = scenario.Contains("tolerance-overlap", StringComparison.Ordinal)
                ? index == 2 ? 10.0012 : index == 3 ? 10.0006 : 10
                : 10;
            sourceIds.Add(AddFixtureOccurrence(doc, services, assembly, part, index * 20, length));
        }
        sources = sourceIds.ToArray();
        if (scenario.Contains("tolerance-overlap", StringComparison.Ordinal))
        {
            for (var index = 0; index < sources.Length; index++)
            {
                var sourceId = sources[index];
                var node = assembly.LinkGraph.Nodes.Single(node => node.ObjectId == sourceId && node.Role == AssemblyLinkRoles.Source);
                var edge = assembly.LinkGraph.Edges.Single(edge => edge.ParentNodeId == node.Id);
                node.Id = Guid.Parse($"{index + 1:x8}-0000-0000-0000-000000000000");
                edge.ParentNodeId = node.Id;
                var attributes = doc.Objects.FindId(sourceId).Attributes.Duplicate();
                attributes.SetUserString(AssemblyManagerConstants.LinkNodeIdUserString, node.Id.ToString());
                Require(doc.Objects.ModifyAttributes(sourceId, attributes, true), "Deterministic fixture source metadata must be applied.");
            }
            Require(services.Fingerprints.TryCreatePartCandidate(doc.Objects.FindId(sources[1]), out var candidateA), "Tolerance fixture A must be valid.");
            Require(services.Fingerprints.TryCreatePartCandidate(doc.Objects.FindId(sources[2]), out var candidateC), "Tolerance fixture C must be valid.");
            Require(services.Fingerprints.TryCreatePartCandidate(doc.Objects.FindId(sources[3]), out var candidateB), "Tolerance fixture B must be valid.");
            Require(services.Fingerprints.AreEquivalentParts(candidateA, candidateB)
                && services.Fingerprints.AreEquivalentParts(candidateB, candidateC)
                && !services.Fingerprints.AreEquivalentParts(candidateA, candidateC),
                "Tolerance fixture must prove A matches B and B matches C while A does not match C.");
            candidateA.Geometry.Dispose();
            candidateB.Geometry.Dispose();
            candidateC.Geometry.Dispose();
        }
        if (scenario.Contains("grouped", StringComparison.Ordinal))
            AddFixtureComponent(doc, services, assembly, part, "C01");
        if (scenario.Contains("unrelated", StringComparison.Ordinal)
            || scenario.Contains("same-category", StringComparison.Ordinal)
            || scenario.Contains("no-unchanged-witness", StringComparison.Ordinal))
        {
            var unrelated = new PartRecord { Name = "P10", Quantity = 1, MaterialThickness = 1 };
            assembly.Parts.Add(unrelated);
            var unrelatedSource = AddFixtureOccurrence(doc, services, assembly, unrelated, 300, 15);
            if (scenario.Contains("grouped", StringComparison.Ordinal))
                AddFixtureComponent(doc, services, assembly, unrelated, "C10");
            assembly.NextPartSequence = 11;
            var unresolvedSource = scenario.Contains("unrelated", StringComparison.Ordinal) ? unrelatedSource : sources[1];
            var unresolvedX = scenario.Contains("unrelated", StringComparison.Ordinal) ? 300 : 20;
            if (scenario.EndsWith("missing", StringComparison.Ordinal))
                Require(doc.Objects.Delete(unresolvedSource, true), "One source must be removed for the incomplete-evidence scenario.");
            else
            {
                using var openBrep = Brep.CreateFromCornerPoints(new Point3d(unresolvedX, 0, 0), new Point3d(unresolvedX + 10, 0, 0), new Point3d(unresolvedX + 10, 5, 0), new Point3d(unresolvedX, 5, 0), 0.001);
                Require(doc.Objects.Replace(unresolvedSource, openBrep), "One source must become unsupported for the incomplete-evidence scenario.");
            }
        }
        services.Repository.Save(doc, new AssemblyStore { Assemblies = new List<AssemblyRecord> { assembly } });
        return assembly;
    }

    private static Guid AddFixtureOccurrence(RhinoDoc doc, RegressionServices services, AssemblyRecord assembly, PartRecord part, double x, double length)
    {
        using var geometry = BoxAt(x, length);
        part.GeometryFingerprint = services.Fingerprints.CreatePartFingerprint(geometry);
        var sourceId = doc.Objects.AddBrep(geometry);
        var transform = Transform.Translation(0, 50, 0);
        using var translated = geometry.DuplicateBrep();
        Require(translated.Transform(transform), "Fixture geometry translation must succeed.");
        var attributes = new ObjectAttributes
        {
            Name = part.Name,
            LayerIndex = services.Layers.EnsureLayerIndex(doc, LayerService.OriginalPart(assembly.Name, "unsorted", part.Name))
        };
        var referenceId = Guid.NewGuid();
        ReferenceUpdateService.AttachReferenceUserStrings(attributes, sourceId, transform, referenceId);
        var originalId = doc.Objects.AddBrep(translated, attributes);
        Require(sourceId != Guid.Empty && originalId != Guid.Empty, "Fixture occurrences must be created.");
        part.SourceObjectIds.Add(sourceId);
        part.GeneratedObjectIds.Add(originalId);
        assembly.GeometryReferences.Add(new GeometryReferenceRecord
        {
            Id = referenceId,
            AssemblyName = assembly.Name,
            PartName = part.Name,
            SourceObjectId = sourceId,
            TargetObjectId = originalId,
            TargetRole = AssemblyManagerConstants.GeneratedAssemblyReferenceRole,
            SourceToTargetTransform = TransformRecord.FromTransform(transform)
        });
        var link = services.Lineage.RegisterDerived(doc, assembly, sourceId, AssemblyLinkRoles.Source,
            originalId, AssemblyLinkRoles.OriginalAssembly, transform, partId: part.Id, edgeId: referenceId);
        link.Parent.GeometryFingerprint = part.GeometryFingerprint;
        link.Child.GeometryFingerprint = part.GeometryFingerprint;
        return sourceId;
    }

    private static void AddFixtureComponent(RhinoDoc doc, RegressionServices services, AssemblyRecord assembly, PartRecord part, string name)
    {
        var component = new ComponentRecord
        {
            Name = name,
            Quantity = 1,
            PartNames = new List<string> { part.Name },
            PartQuantities = new Dictionary<string, int> { [part.Name] = part.Quantity },
            ObjectIds = part.GeneratedObjectIds.ToList(),
            RepresentativeObjectIdsByPartName = new Dictionary<string, List<Guid>> { [part.Name] = part.GeneratedObjectIds.ToList() }
        };
        var sourceGroupName = assembly.Name + "-source-" + name;
        var generatedGroupName = assembly.Name + "-original-" + name;
        var sourceGroupIndex = doc.Groups.Add(sourceGroupName, part.SourceObjectIds);
        doc.Groups.Add(generatedGroupName, part.GeneratedObjectIds);
        component.InstanceGroupNames.Add(generatedGroupName);
        assembly.Components.Add(component);
        var instance = services.Lineage.RegisterSourceComponentInstance(doc, assembly, component, sourceGroupIndex,
            sourceGroupName, part.SourceObjectIds, generatedGroupName);
        var candidates = new List<PartCandidate>();
        foreach (var sourceId in part.SourceObjectIds)
        {
            Require(services.Fingerprints.TryCreatePartCandidate(doc.Objects.FindId(sourceId), out var candidate), "Component fixture needs valid source candidates.");
            candidate.PartName = part.Name;
            candidates.Add(candidate);
        }
        component.Fingerprint = services.Fingerprints.CreateComponentFingerprint(candidates);
        foreach (var node in assembly.LinkGraph.Nodes.Where(node => node.PartId == part.Id))
        {
            node.ComponentId = component.Id;
            node.SourceComponentInstanceId = instance.Id;
            if (node.Role == AssemblyLinkRoles.OriginalAssembly)
            {
                var attributes = doc.Objects.FindId(node.ObjectId).Attributes.Duplicate();
                attributes.LayerIndex = services.Layers.EnsureLayerIndex(doc, LayerService.OriginalPart(assembly.Name, name, part.Name));
                Require(doc.Objects.ModifyAttributes(node.ObjectId, attributes, true), "Component fixture layer update must succeed.");
            }
        }
        foreach (var reference in assembly.GeometryReferences.Where(reference => reference.PartName == part.Name))
            reference.ComponentName = name;
    }

    private static Brep BoxAt(double x, double length) =>
        Brep.CreateFromBox(new BoundingBox(new Point3d(x, 0, 0), new Point3d(x + length, 5, 1)));

    private static void PumpIdle(RhinoCore core, RegressionServices services)
    {
        for (var index = 0; index < 3; index++)
        {
            core.RaiseIdle();
            core.DoIdle();
            // RhinoCore's standalone console host does not deliver RhinoApp.Idle. Drain
            // the real native object-event queue through the identical production callback.
            typeof(AssemblyLinkEventService).GetMethod("OnIdle", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(services.LinkEvents, new object?[] { null, EventArgs.Empty });
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDllDirectory(string path);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    private sealed class RegressionServices
    {
        public AssemblyRepository Repository { get; } = new();
        public LayerService Layers { get; } = new();
        public GeometryFingerprintService Fingerprints { get; } = new();
        public AssemblyLineageService Lineage { get; } = new();
        public ReferenceUpdateService ReferenceUpdates { get; }
        public AssemblyLinkEventService LinkEvents { get; }
        public bool AutomaticallyPropagate { get; set; } = true;
        public List<string> UpdateFeedback { get; } = new();

        public RegressionServices()
        {
            var categorization = new AssemblyCategorizationReconciliationService(Fingerprints, Layers, Lineage);
            var flatParts = new FlatPartSynchronizationService(Layers, Fingerprints, new RegressionMaterialLibrary(), Lineage);
            ReferenceUpdates = new ReferenceUpdateService(Repository, new DocumentActionHistorySink(Repository), Lineage, Fingerprints, categorization, flatParts,
                updateFeedback: UpdateFeedback.Add);
            LinkEvents = new AssemblyLinkEventService(Repository, ReferenceUpdates, Lineage, Fingerprints,
                automaticPropagationEnabled: () => AutomaticallyPropagate);
        }
    }
}
