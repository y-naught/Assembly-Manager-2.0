using AssemblyManagerPlugin.Core;
using AssemblyManagerPlugin.Geometry;
using AssemblyManagerPlugin.Services;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Runtime.InProcess;
using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;

internal static partial class Program
{
    private static void RunAssemblyUpdateScenario(RhinoCore core, RegressionServices services, string scenario, List<RhinoDoc> fixtureDocs)
    {
        if (scenario.StartsWith("update-feedback-", StringComparison.Ordinal))
        {
            RunUpdateFeedbackScenario(core, services, scenario, fixtureDocs);
            return;
        }
        if (scenario.StartsWith("update-flat-dependent-", StringComparison.Ordinal))
        {
            RunFlatDependencyScenario(core, services, scenario, fixtureDocs);
            return;
        }
        if (scenario == "update-paused-stock-original-edit")
        {
            RunStockNormalizedOriginalUpdate(core, services, scenario, fixtureDocs);
            return;
        }
        if (scenario == "update-paused-shared-source")
        {
            RunSharedSourceManualUpdate(core, services, scenario, fixtureDocs);
            return;
        }
        if (scenario == "update-settings-compatibility")
        {
            var legacy = JsonSerializer.Deserialize<PluginSettingsRecord>("{\"SchemaVersion\":8,\"AssemblyManager\":{}}")!;
            Require(legacy.AssemblyManager.AutomaticallyPropagateChangesInAssembly, "Existing settings without the toggle must retain automatic updates.");
            var current = new PluginSettingsRecord();
            Require(current.SchemaVersion >= 9 && current.AssemblyManager.AutomaticallyPropagateChangesInAssembly,
                "New settings must default to automatic propagation with the new schema.");
            current.AssemblyManager.AutomaticallyPropagateChangesInAssembly = false;
            Require(!JsonSerializer.Deserialize<PluginSettingsRecord>(JsonSerializer.Serialize(current))!.AssemblyManager.AutomaticallyPropagateChangesInAssembly,
                "An explicitly disabled toggle must survive serialization.");
            return;
        }
        if (scenario is "update-paused-multiple-original-edits" or "update-paused-service-restart" or "update-paused-competing-edits"
            or "update-original-source-material-conflict-before-edit" or "update-paused-source-material-conflict-after-edit"
            or "update-paused-service-restart-reenable")
        {
            RunAdvancedPausedUpdateScenario(core, services, scenario, fixtureDocs);
            return;
        }
        var doc = RhinoDoc.Create(null);
        fixtureDocs.Add(doc);
        doc.ModelUnitSystem = UnitSystem.Inches;
        doc.ModelAbsoluteTolerance = 0.001;
        services.AutomaticallyPropagate = true;
        var flatScenario = scenario.Contains("flat", StringComparison.Ordinal) || scenario.Contains("material", StringComparison.Ordinal);
        var materialScenario = scenario.Contains("material", StringComparison.Ordinal);
        var mergeScenario = scenario.EndsWith("merge", StringComparison.Ordinal);
        var movedFlat = scenario.EndsWith("move-rotate", StringComparison.Ordinal);
        var singleSource = movedFlat || scenario.EndsWith("thickness", StringComparison.Ordinal) || scenario.EndsWith("copied-parent", StringComparison.Ordinal);
        var created = CreateAssemblyUpdateFixture(doc, services, scenario, flatScenario, materialScenario, mergeScenario, singleSource);
        PumpIdle(core, services);
        var before = services.Repository.Load(doc).FindAssembly(created.Name)!;
        var editedSource = before.LinkGraph.Nodes.Where(node => node.Role == AssemblyLinkRoles.Source)
            .OrderBy(node => doc.Objects.FindId(node.ObjectId).Geometry.GetBoundingBox(true).Min.X)
            .ElementAt(scenario.EndsWith("nonrepresentative", StringComparison.Ordinal) ? 1 : 0);
        var editedOriginal = before.LinkGraph.Nodes.Single(node => node.Id == before.LinkGraph.Edges
            .Single(edge => edge.ParentNodeId == editedSource.Id && before.LinkGraph.Nodes.Single(n => n.Id == edge.ChildNodeId).Role == AssemblyLinkRoles.OriginalAssembly).ChildNodeId);
        var originalSourceId = editedSource.ObjectId;
        var originalNodeId = editedOriginal.Id;
        var priorFlat = before.LinkGraph.Nodes.FirstOrDefault(node => node.Role == AssemblyLinkRoles.FlatPart);
        var priorFlatId = priorFlat?.ObjectId ?? Guid.Empty;
        var priorLabel = flatScenario ? FindFixturePartLabel(doc, services, before, before.Parts[0]) : null;
        var priorLabelId = priorLabel?.Id ?? Guid.Empty;
        var priorLabelPlane = (priorLabel?.Geometry as TextEntity)?.Plane;
        var userTextId = flatScenario ? AddUpdateFixtureText(doc, services, before, before.Parts[0], "Operator note: keep this annotation", false) : Guid.Empty;
        Point3d? movedAnchor = null;
        Transform? movedPlacement = null;

        if (movedFlat)
        {
            var flat = doc.Objects.FindId(priorFlatId);
            var center = flat.Geometry.GetBoundingBox(true).Center;
            ApplyFixtureLayoutTransform(doc, services, flat.Id,
                Transform.Translation(35, -18, 0) * Transform.Rotation(Math.PI / 6, Vector3d.ZAxis, center));
            PumpIdle(core, services);
            before = services.Repository.Load(doc).FindAssembly(created.Name)!;
            priorFlat = before.LinkGraph.Nodes.Single(node => node.Id == priorFlat!.Id);
            priorFlatId = priorFlat.ObjectId;
            movedAnchor = doc.Objects.FindId(priorFlatId).Geometry.GetBoundingBox(true).Min;
            var movedEdge = before.LinkGraph.Edges.Single(edge => edge.ChildNodeId == priorFlat.Id);
            Require(movedEdge.ParentToChildTransform.TryToTransform(out var placement), "Moved flat transform must remain readable.");
            movedPlacement = placement;
            Require(movedEdge.RecipeMetadata.GetValueOrDefault(AssemblyLinkMetadataKeys.UserPlanRotationOverride) == bool.TrueString,
                "User flat rotation must be remembered before a later geometry edit.");
        }

        var paused = scenario.Contains("paused", StringComparison.Ordinal);
        var editOriginal = scenario.Contains("original", StringComparison.Ordinal);
        services.AutomaticallyPropagate = !paused;
        if (scenario.EndsWith("move-original", StringComparison.Ordinal))
        {
            ApplyFixtureLayoutTransform(doc, services, editedOriginal.ObjectId, Transform.Translation(7, 9, 0));
            PumpIdle(core, services);
            before = services.Repository.Load(doc).FindAssembly(created.Name)!;
            editedOriginal = before.LinkGraph.Nodes.Single(node => node.Id == originalNodeId);
            Require(doc.Objects.FindId(originalSourceId).Geometry.GetBoundingBox(true).Min.DistanceTo(new Point3d(0, 0, 0)) < 0.001,
                "Moving an original while paused must not relocate its input geometry.");
        }
        var editedObjectId = editOriginal ? editedOriginal.ObjectId : editedSource.ObjectId;
        var originalBounds = doc.Objects.FindId(editedObjectId).Geometry.GetBoundingBox(true);
        if (materialScenario)
        {
            var attributes = doc.Objects.FindId(editedObjectId).Attributes.Duplicate();
            MaterialAssignment.Set(attributes, new MaterialDefinitionRecord { Id = "ALUMINUM", Name = "Aluminum" });
            Require(doc.Objects.ModifyAttributes(editedObjectId, attributes, true), "A material-only attribute edit must succeed.");
        }
        else
        {
            var size = scenario.EndsWith("thickness", StringComparison.Ordinal) ? new Vector3d(10, 5, 2) : new Vector3d(12, 5, 1);
            using var replacement = Brep.CreateFromBox(new BoundingBox(originalBounds.Min, originalBounds.Min + size));
            Require(doc.Objects.Replace(editedObjectId, replacement), "The source/original geometry edit must succeed.");
        }
        PumpIdle(core, services);
        if (paused)
        {
            var deferred = services.Repository.Load(doc).FindAssembly(created.Name)!;
            var unchangedTargetId = editOriginal ? originalSourceId : deferred.LinkGraph.Nodes.Single(node => node.Id == originalNodeId).ObjectId;
            Require(Math.Abs(doc.Objects.FindId(unchangedTargetId).Geometry.GetBoundingBox(true).Diagonal.X - 10) < 0.001,
                "Paused updates must not propagate source/original geometry to the opposite side.");
            Require(deferred.Parts.Count == 1 && deferred.Parts[0].Quantity == 2,
                "Paused updates must not recategorize the edited occurrence.");
            if (materialScenario)
                Require(MaterialAssignment.GetCategorizationMaterialId(doc.Objects.FindId(unchangedTargetId).Attributes) == "BIRCH",
                    "Paused material changes must leave downstream material unchanged.");
            if (scenario.EndsWith("reenable", StringComparison.Ordinal))
            {
                services.AutomaticallyPropagate = true;
                PumpIdle(core, services);
            }
            else
            {
                services.LinkEvents.UpdateAssembly(doc, created.Name);
                PumpIdle(core, services);
                Require(!services.AutomaticallyPropagate, "Manual update must not silently re-enable automatic propagation.");
            }
        }

        var after = services.Repository.Load(doc).FindAssembly(created.Name)!;
        var revisedSource = after.LinkGraph.Nodes.Single(node => node.Id == editedSource.Id);
        var revisedOriginal = after.LinkGraph.Nodes.Single(node => node.Id == originalNodeId);
        Require(after.Parts.Sum(part => part.Quantity) == (singleSource ? 1 : 2), "An update must preserve total occurrence quantity.");
        if (materialScenario)
        {
            Require(MaterialAssignment.GetCategorizationMaterialId(doc.Objects.FindId(revisedSource.ObjectId).Attributes) == "ALUMINUM",
                "Material edits must retain/promote the assigned type on the input source.");
            Require(MaterialAssignment.GetCategorizationMaterialId(doc.Objects.FindId(revisedOriginal.ObjectId).Attributes) == "ALUMINUM",
                "Material-only source changes must propagate to the generated original.");
            Require(after.Parts.Single(part => part.Id == revisedSource.PartId).CategorizationMaterialId == "ALUMINUM",
                "Material-only source changes must update the category material.");
        }
        else
        {
            Require(Math.Abs(doc.Objects.FindId(revisedSource.ObjectId).Geometry.GetBoundingBox(true).Diagonal.X - (scenario.EndsWith("thickness", StringComparison.Ordinal) ? 10 : 12)) < 0.001,
                "The revised input geometry must be retained/promoted.");
            Require(Math.Abs(doc.Objects.FindId(revisedOriginal.ObjectId).Geometry.GetBoundingBox(true).Diagonal.X - (scenario.EndsWith("thickness", StringComparison.Ordinal) ? 10 : 12)) < 0.001,
                "The generated original must receive the revised geometry.");
        }
        if (!singleSource)
        {
            Require(after.Parts.Count == (mergeScenario ? 1 : 2), "The changed occurrence must split or merge its category as appropriate.");
            if (mergeScenario)
                Require(after.Parts.Single().Name == "P02" && after.Parts.Single().Quantity == 2,
                    "A merge must retain the existing matching P02 category and combine quantity.");
            else
                Require(after.Parts.All(part => part.Quantity == 1), "A split must produce one occurrence per category.");
        }
        if (flatScenario)
        {
            AssertSynchronizedFlatOutputs(doc, services, after);
            Require(doc.Objects.FindId(userTextId) != null, "Updating managed annotations must preserve ordinary user text.");
            if (mergeScenario)
            {
                Require(doc.Objects.FindId(priorFlatId) == null, "The obsolete managed flat output must retire after its category merges.");
                Require(!doc.Objects.GetObjectList(ObjectType.Annotation).Any(obj => obj.Geometry is TextEntity text && text.PlainText.Replace("\r", string.Empty).StartsWith("P01\nQTY :", StringComparison.Ordinal)),
                    "The obsolete managed P01 label must retire after its category merges.");
            }
            else
            {
                Require(after.LinkGraph.Nodes.Any(node => node.ObjectId == priorFlatId && node.Role == AssemblyLinkRoles.FlatPart),
                    "A surviving existing flat output must retain its UUID.");
                Require(doc.Objects.FindId(priorLabelId)?.Geometry is TextEntity,
                    "Updating a surviving flat's generated label must preserve its UUID.");
                var currentLabelPlane = ((TextEntity)doc.Objects.FindId(priorLabelId).Geometry).Plane;
                Require(currentLabelPlane.Origin.DistanceTo(priorLabelPlane!.Value.Origin) < 0.001 &&
                    (currentLabelPlane.XAxis - priorLabelPlane.Value.XAxis).Length < 0.001,
                    "Updating a generated label must preserve its existing text placement.");
            }
            if (movedFlat)
            {
                var flatNode = after.LinkGraph.Nodes.Single(node => node.ObjectId == priorFlatId);
                var flatObject = doc.Objects.FindId(priorFlatId);
                Require(flatObject.Geometry.GetBoundingBox(true).Min.DistanceTo(movedAnchor!.Value) < 0.001,
                    "Geometry update must retain the user's moved flat layout anchor.");
                var edge = after.LinkGraph.Edges.Single(edge => edge.ChildNodeId == flatNode.Id);
                Require(edge.ParentToChildTransform.TryToTransform(out var transform), "Refreshed flat transform must be readable.");
                Require((transform * Vector3d.XAxis - movedPlacement!.Value * Vector3d.XAxis).Length < 0.001,
                    "Geometry update must retain the user's plan rotation.");
            }
            var objectIds = after.Parts.SelectMany(part => part.CamObjectIds).OrderBy(id => id).ToArray();
            services.ReferenceUpdates.RefreshAssemblyReferences(doc, created.Name);
            PumpIdle(core, services);
            var repeated = services.Repository.Load(doc).FindAssembly(created.Name)!;
            AssertSynchronizedFlatOutputs(doc, services, repeated);
            Require(repeated.Parts.SelectMany(part => part.CamObjectIds).OrderBy(id => id).SequenceEqual(objectIds),
                "A repeated update must not duplicate or replace stable flat output UUIDs.");
        }
        else
            Require(!after.LinkGraph.Nodes.Any(node => node.Role == AssemblyLinkRoles.FlatPart),
                "Updating an assembly that was never laid flat must not create an unsolicited PARTS layout.");
        services.AutomaticallyPropagate = true;
    }

    private static void RunStockNormalizedOriginalUpdate(RhinoCore core, RegressionServices services, string scenario, List<RhinoDoc> fixtureDocs)
    {
        var doc = RhinoDoc.Create(null);
        fixtureDocs.Add(doc);
        doc.ModelUnitSystem = UnitSystem.Inches;
        doc.ModelAbsoluteTolerance = 0.001;
        services.AutomaticallyPropagate = false;
        var created = CreateAssemblyUpdateFixture(doc, services, scenario, false, true, false, false);
        var before = services.Repository.Load(doc).FindAssembly(created.Name)!;
        var source = before.LinkGraph.Nodes.First(node => node.Role == AssemblyLinkRoles.Source);
        var original = before.LinkGraph.Nodes.Single(node => node.Id == before.LinkGraph.Edges.Single(edge => edge.ParentNodeId == source.Id).ChildNodeId);
        const string stockId = "birch-sheet-48x96-stock-id";
        using (AssemblyLinkMutationGate.Enter())
        {
            var attributes = doc.Objects.FindId(source.ObjectId).Attributes.Duplicate();
            MaterialAssignment.Set(attributes, new MaterialRecord
            {
                Id = stockId,
                Name = "Birch plywood 48 x 96",
                BaseMaterialId = "BIRCH",
                BaseMaterialName = "Birch plywood",
                ShapeName = "48 x 96",
                ShapeType = "Sheet"
            });
            Require(doc.Objects.ModifyAttributes(source.ObjectId, attributes, true), "The design input's stock assignment must be recorded.");
        }
        PumpIdle(core, services);
        ReplaceUpdateFixtureBox(doc, original.ObjectId, 12);
        PumpIdle(core, services);
        services.LinkEvents.UpdateAssembly(doc, created.Name);
        PumpIdle(core, services);
        var after = services.Repository.Load(doc).FindAssembly(created.Name)!;
        Require(Math.Abs(doc.Objects.FindId(source.ObjectId).Geometry.GetBoundingBox(true).Diagonal.X - 12) < 0.001,
            "A base-normalized original must safely promote a shape edit to an input assigned a stock size of that same material.");
        Require(MaterialAssignment.GetMaterialId(doc.Objects.FindId(source.ObjectId).Attributes) == stockId,
            "A shape-only original edit must preserve the input's more-specific stock material assignment.");
        Require(MaterialAssignment.GetCategorizationMaterialId(doc.Objects.FindId(original.ObjectId).Attributes) == "BIRCH",
            "The original must retain the shared parent material category.");
        Require(!after.LinkGraph.Conflicts.Any(conflict => conflict.Status == AssemblyLinkStatuses.Open &&
            conflict.ConflictType == AssemblyLinkConflictTypes.OriginalAssemblyPromotionUnresolved),
            "Normal stock-to-parent material normalization must not look like a stale material conflict.");
        Require(after.Parts.Count == 2 && after.Parts.All(part => part.Quantity == 1),
            "The stock-assigned source shape edit must still recategorize its occurrence.");
        services.AutomaticallyPropagate = true;
    }

    private static void RunSharedSourceManualUpdate(RhinoCore core, RegressionServices services, string scenario, List<RhinoDoc> fixtureDocs)
    {
        var doc = RhinoDoc.Create(null);
        fixtureDocs.Add(doc);
        doc.ModelUnitSystem = UnitSystem.Inches;
        doc.ModelAbsoluteTolerance = 0.001;
        services.AutomaticallyPropagate = false;
        var created = CreateAssemblyUpdateFixture(doc, services, scenario, false, false, false, false);
        var store = services.Repository.Load(doc);
        var first = store.FindAssembly(created.Name)!;
        var source = first.LinkGraph.Nodes.First(node => node.Role == AssemblyLinkRoles.Source);
        var original = first.LinkGraph.Nodes.Single(node => node.Id == first.LinkGraph.Edges.Single(edge => edge.ParentNodeId == source.Id).ChildNodeId);
        var shared = new AssemblyRecord { Name = created.Name + "-shared", NextPartSequence = 2 };
        var part = new PartRecord
        {
            Name = "P01",
            Quantity = 1,
            MaterialThickness = 1,
            GeometryFingerprint = first.Parts[0].GeometryFingerprint,
            SourceObjectIds = new List<Guid> { source.ObjectId }
        };
        shared.Parts.Add(part);
        Guid sharedOriginalId;
        using (AssemblyLinkMutationGate.Enter())
        {
            var transform = Transform.Translation(0, 120, 0);
            using var geometry = doc.Objects.FindId(source.ObjectId).Geometry.Duplicate();
            Require(geometry.Transform(transform), "The second assembly fixture transform must succeed.");
            var attributes = new ObjectAttributes
            { Name = part.Name, LayerIndex = services.Layers.EnsureLayerIndex(doc, LayerService.OriginalPart(shared.Name, "unsorted", part.Name)) };
            var referenceId = Guid.NewGuid();
            ReferenceUpdateService.AttachReferenceUserStrings(attributes, source.ObjectId, transform, referenceId);
            sharedOriginalId = doc.Objects.Add(geometry, attributes);
            Require(sharedOriginalId != Guid.Empty, "The second assembly original must be created.");
            part.GeneratedObjectIds.Add(sharedOriginalId);
            var link = services.Lineage.RegisterDerived(doc, shared, source.ObjectId, AssemblyLinkRoles.Source,
                sharedOriginalId, AssemblyLinkRoles.OriginalAssembly, transform, partId: part.Id, edgeId: referenceId);
            link.Parent.GeometryFingerprint = part.GeometryFingerprint;
            link.Child.GeometryFingerprint = part.GeometryFingerprint;
            shared.GeometryReferences.Add(new GeometryReferenceRecord
            {
                Id = referenceId,
                AssemblyName = shared.Name,
                PartName = part.Name,
                SourceObjectId = source.ObjectId,
                TargetObjectId = sharedOriginalId,
                TargetRole = AssemblyManagerConstants.GeneratedAssemblyReferenceRole,
                SourceToTargetTransform = TransformRecord.FromTransform(transform)
            });
            store.Assemblies.Add(shared);
            services.Repository.Save(doc, store);
        }
        PumpIdle(core, services);
        ReplaceUpdateFixtureBox(doc, original.ObjectId, 12);
        PumpIdle(core, services);
        Require(Math.Abs(doc.Objects.FindId(sharedOriginalId).Geometry.GetBoundingBox(true).Diagonal.X - 10) < 0.001,
            "A shared input's other assembly must stay unchanged while automatic updates are paused.");
        var sharedRefreshes = 0;
        void CountSharedReplacement(object? sender, RhinoReplaceObjectEventArgs args)
        {
            if (args.ObjectId == sharedOriginalId)
                sharedRefreshes++;
        }
        RhinoDoc.ReplaceRhinoObject += CountSharedReplacement;
        try
        {
            services.LinkEvents.UpdateAssembly(doc, created.Name);
            PumpIdle(core, services);
        }
        finally
        {
            RhinoDoc.ReplaceRhinoObject -= CountSharedReplacement;
        }
        var after = services.Repository.Load(doc);
        Require(Math.Abs(doc.Objects.FindId(source.ObjectId).Geometry.GetBoundingBox(true).Diagonal.X - 12) < 0.001 &&
            Math.Abs(doc.Objects.FindId(sharedOriginalId).Geometry.GetBoundingBox(true).Diagonal.X - 12) < 0.001,
            "Manual promotion must update the shared input and every assembly depending on that same source.");
        Require(sharedRefreshes == 1, $"The other assembly's shared-source output must refresh exactly once, got {sharedRefreshes}.");
        Require(after.FindAssembly(created.Name)!.Parts.Count == 2 && after.FindAssembly(shared.Name)!.Parts.Sum(candidate => candidate.Quantity) == 1,
            "Shared-source recategorization must preserve both assemblies' independent quantities.");
        Require(!services.AutomaticallyPropagate, "Updating a shared source must not re-enable automatic propagation.");
        services.AutomaticallyPropagate = true;
    }

    private static void RunAdvancedPausedUpdateScenario(RhinoCore core, RegressionServices services, string scenario, List<RhinoDoc> fixtureDocs)
    {
        var doc = RhinoDoc.Create(null);
        fixtureDocs.Add(doc);
        doc.ModelUnitSystem = UnitSystem.Inches;
        doc.ModelAbsoluteTolerance = 0.001;
        services.AutomaticallyPropagate = false;
        var materialConflict = scenario.Contains("material-conflict", StringComparison.Ordinal);
        var created = CreateAssemblyUpdateFixture(doc, services, scenario, false, materialConflict, false, false);
        PumpIdle(core, services);
        var before = services.Repository.Load(doc).FindAssembly(created.Name)!;
        var source = before.LinkGraph.Nodes.First(node => node.Role == AssemblyLinkRoles.Source);
        var original = before.LinkGraph.Nodes.Single(node => node.Id == before.LinkGraph.Edges.Single(edge => edge.ParentNodeId == source.Id).ChildNodeId);
        var competing = scenario.EndsWith("competing-edits", StringComparison.Ordinal);
        if (materialConflict && scenario.EndsWith("before-edit", StringComparison.Ordinal))
            ChangeUnobservedFixtureMaterial(doc, source.ObjectId);
        ReplaceUpdateFixtureBox(doc, competing ? source.ObjectId : original.ObjectId, 12);
        PumpIdle(core, services);
        Require(Math.Abs(doc.Objects.FindId(competing ? original.ObjectId : source.ObjectId).Geometry.GetBoundingBox(true).Diagonal.X - 10) < 0.001,
            "The first paused edit must leave the opposite side unchanged.");
        if (materialConflict)
        {
            if (scenario.EndsWith("after-edit", StringComparison.Ordinal))
                ChangeUnobservedFixtureMaterial(doc, source.ObjectId);
        }
        else if (scenario.Contains("service-restart", StringComparison.Ordinal))
        {
            var pending = services.Repository.Load(doc).FindAssembly(created.Name)!.LinkGraph.Nodes.Single(node => node.Id == original.Id);
            Require(pending.Metadata.GetValueOrDefault("PendingOriginalUpdate") == bool.TrueString,
                "Paused original authority must be persisted before event-service restart.");
            services.LinkEvents.Stop();
            services.LinkEvents.Start();
        }
        else
        {
            var currentOriginal = services.Repository.Load(doc).FindAssembly(created.Name)!.LinkGraph.Nodes.Single(node => node.Id == original.Id);
            if (!competing)
            {
                ApplyFixtureLayoutTransform(doc, services, currentOriginal.ObjectId, Transform.Translation(7, 9, 0));
                PumpIdle(core, services);
                currentOriginal = services.Repository.Load(doc).FindAssembly(created.Name)!.LinkGraph.Nodes.Single(node => node.Id == original.Id);
            }
            ReplaceUpdateFixtureBox(doc, currentOriginal.ObjectId, 14);
            PumpIdle(core, services);
        }
        if (scenario.EndsWith("restart-reenable", StringComparison.Ordinal))
            services.AutomaticallyPropagate = true;
        else
            services.LinkEvents.UpdateAssembly(doc, created.Name);
        PumpIdle(core, services);
        var after = services.Repository.Load(doc).FindAssembly(created.Name)!;
        var currentSource = after.LinkGraph.Nodes.Single(node => node.Id == source.Id);
        var updatedOriginal = after.LinkGraph.Nodes.Single(node => node.Id == original.Id);
        var sourceLength = doc.Objects.FindId(currentSource.ObjectId).Geometry.GetBoundingBox(true).Diagonal.X;
        var originalLength = doc.Objects.FindId(updatedOriginal.ObjectId).Geometry.GetBoundingBox(true).Diagonal.X;
        if (materialConflict)
        {
            Require(Math.Abs(sourceLength - 10) < 0.001 && Math.Abs(originalLength - 12) < 0.001,
                "Unobserved source-material changes must block original promotion without losing either shape.");
            Require(MaterialAssignment.GetCategorizationMaterialId(doc.Objects.FindId(currentSource.ObjectId).Attributes) == "ALUMINUM" &&
                MaterialAssignment.GetCategorizationMaterialId(doc.Objects.FindId(updatedOriginal.ObjectId).Attributes) == "BIRCH",
                "Conflicting source/original material assignments must both be preserved for review.");
            Require(after.LinkGraph.Conflicts.Any(conflict => conflict.Status == AssemblyLinkStatuses.Open &&
                conflict.ConflictType == AssemblyLinkConflictTypes.OriginalAssemblyPromotionUnresolved),
                "A source-material provenance mismatch must report an original-promotion review issue.");
        }
        else if (competing)
        {
            Require(Math.Abs(sourceLength - 12) < 0.001 && Math.Abs(originalLength - 14) < 0.001,
                "Competing deferred edits must preserve both shapes, not arbitrarily overwrite either side.");
            Require(after.LinkGraph.Conflicts.Any(conflict => conflict.Status == AssemblyLinkStatuses.Open),
                "Competing deferred authority must appear as an open link issue.");
        }
        else
        {
            var expected = scenario.Contains("service-restart", StringComparison.Ordinal) ? 12 : 14;
            Require(Math.Abs(sourceLength - expected) < 0.001 && Math.Abs(originalLength - expected) < 0.001,
                "Manual update must apply the latest deferred original shape after repeated edits or a service restart.");
            Require(after.Parts.Count == 2 && after.Parts.All(part => part.Quantity == 1),
                "Deferred original updates must recategorize only the changed occurrence.");
            Require(!currentSource.Metadata.ContainsKey("PendingSourceUpdate")
                && !updatedOriginal.Metadata.ContainsKey("PendingOriginalUpdate"),
                "Successfully applied pending markers must clear.");
        }
        services.AutomaticallyPropagate = true;
    }

    private static void ChangeUnobservedFixtureMaterial(RhinoDoc doc, Guid objectId)
    {
        using var mutation = AssemblyLinkMutationGate.Enter();
        var attributes = doc.Objects.FindId(objectId).Attributes.Duplicate();
        MaterialAssignment.Set(attributes, new MaterialDefinitionRecord { Id = "ALUMINUM", Name = "Aluminum" });
        Require(doc.Objects.ModifyAttributes(objectId, attributes, true), "The unobserved material edit must succeed.");
    }

    private static void ReplaceUpdateFixtureBox(RhinoDoc doc, Guid objectId, double length)
    {
        var bounds = doc.Objects.FindId(objectId).Geometry.GetBoundingBox(true);
        using var replacement = Brep.CreateFromBox(new BoundingBox(bounds.Min, bounds.Min + new Vector3d(length, 5, 1)));
        Require(doc.Objects.Replace(objectId, replacement), "The disposable deferred edit must replace successfully.");
    }

    private static void ApplyFixtureLayoutTransform(RhinoDoc doc, RegressionServices services, Guid objectId, Transform transform)
    {
        // ObjectTable.Transform emits native replacement callbacks, but not the command-level
        // BeforeTransformObjects notification. RhinoCore also cannot run interactive commands
        // from this standalone console host. Supply the identical immutable pre-transform fact
        // then let native replacements and the production idle classifier handle the change.
        var eventType = typeof(AssemblyLinkEventService);
        var snapshot = eventType.GetMethod("CaptureGeometrySnapshots", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { new[] { doc.Objects.FindId(objectId) } });
        var factType = eventType.GetNestedType("TransformFact", BindingFlags.NonPublic)!;
        var fact = Activator.CreateInstance(factType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, new object[] { ImmutableArray.Create(objectId), transform, false, snapshot!, Guid.NewGuid() }, null);
        eventType.GetMethod("Enqueue", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(services.LinkEvents, new[] { (object)doc.RuntimeSerialNumber, fact! });
        Require(doc.Objects.Transform(objectId, transform, true) != Guid.Empty, "The fixture layout transform must succeed.");
    }

    private static AssemblyRecord CreateAssemblyUpdateFixture(RhinoDoc doc, RegressionServices services, string scenario,
        bool flats, bool materials, bool merge, bool singleSource)
    {
        using var mutation = AssemblyLinkMutationGate.Enter();
        var assembly = new AssemblyRecord { Name = "Regression-" + scenario, NextPartSequence = merge ? 3 : 2 };
        var part = new PartRecord { Name = "P01", Quantity = singleSource || merge ? 1 : 2, MaterialThickness = 1 };
        assembly.Parts.Add(part);
        AddFixtureOccurrence(doc, services, assembly, part, 0, 10);
        if (merge)
        {
            var second = new PartRecord { Name = "P02", Quantity = 1, MaterialThickness = 1 };
            assembly.Parts.Add(second);
            AddFixtureOccurrence(doc, services, assembly, second, 30, materials ? 10 : 12);
        }
        else if (!singleSource)
            AddFixtureOccurrence(doc, services, assembly, part, 30, 10);
        if (materials)
        {
            foreach (var category in assembly.Parts)
            {
                var id = category.Name == "P02" ? "ALUMINUM" : "BIRCH";
                category.MaterialId = id;
                category.CategorizationMaterialId = id;
                foreach (var objectId in category.SourceObjectIds.Concat(category.GeneratedObjectIds))
                {
                    var attributes = doc.Objects.FindId(objectId).Attributes.Duplicate();
                    MaterialAssignment.Set(attributes, new MaterialDefinitionRecord { Id = id, Name = id == "BIRCH" ? "Birch plywood" : "Aluminum" });
                    Require(doc.Objects.ModifyAttributes(objectId, attributes, true), "Fixture material must be assigned.");
                }
            }
        }
        if (flats)
        {
            foreach (var category in assembly.Parts)
            {
                var parentId = scenario.EndsWith("copied-parent", StringComparison.Ordinal)
                    ? AddLayerFixtureOutput(doc, services, assembly, category.GeneratedObjectIds[0], AssemblyLinkRoles.CopiedComponent,
                        LayerService.CopiedComponentPart(assembly.Name, "unsorted", category.Name),
                        Transform.Translation(80, 0, 0) * Transform.Rotation(Math.PI / 4, Vector3d.ZAxis, Point3d.Origin))
                    : category.GeneratedObjectIds[0];
                AddAssemblyUpdateFlat(doc, services, assembly, category, 200 + assembly.Parts.IndexOf(category) * 40, parentId);
                AddAssemblyUpdateRowHeader(doc, services, assembly, category);
            }
        }
        services.Repository.Save(doc, new AssemblyStore { Assemblies = new List<AssemblyRecord> { assembly } });
        return assembly;
    }

    private static void AddAssemblyUpdateFlat(RhinoDoc doc, RegressionServices services, AssemblyRecord assembly, PartRecord part, double x, Guid originalId)
    {
        var original = doc.Objects.FindId(originalId);
        using var geometry = ((Brep)original.Geometry).DuplicateBrep();
        var orientation = TransformUtilities.OrientLargestFaceToWorldXY(geometry, services.Fingerprints, Point3d.Origin);
        Require(geometry.Transform(orientation), "Flat fixture orientation must succeed.");
        var longAxis = TransformUtilities.RotateLongDimensionToY(geometry);
        var bounds = geometry.GetBoundingBox(true);
        var placement = Transform.Translation(x - bounds.Min.X, 8 - bounds.Min.Y, -bounds.Min.Z);
        Require(geometry.Transform(placement), "Flat fixture placement must succeed.");
        var attributes = original.Attributes.Duplicate();
        AssemblyLineageService.ClearLinkMetadata(attributes);
        attributes.LayerIndex = services.Layers.EnsureLayerIndex(doc, LayerService.PartsPart(assembly.Name, part.Name) + "::3D");
        attributes.Name = part.Name;
        var flatId = doc.Objects.AddBrep(geometry, attributes);
        Require(flatId != Guid.Empty, "Flat fixture geometry must be created.");
        var parentRole = assembly.LinkGraph.Nodes.Single(node => node.ObjectId == originalId).Role;
        var link = services.Lineage.RegisterDerived(doc, assembly, originalId, parentRole,
            flatId, AssemblyLinkRoles.FlatPart, placement * longAxis * orientation, AssemblyLinkRecipes.LayFlat,
            partId: part.Id, recipeMetadata: new Dictionary<string, string>
            {
                ["orientation"] = "LargestFaceToWorldXY",
                ["longAxis"] = "Y",
                ["layout"] = "MaterialThicknessRow",
                [AssemblyLinkMetadataKeys.UserPlanRotationOverride] = bool.FalseString
            });
        link.Child.GeometryFingerprint = part.GeometryFingerprint;
        part.CamObjectIds.Add(flatId);
        var material = part.MaterialId == "BIRCH" ? "Birch plywood" : part.MaterialId == "ALUMINUM" ? "Aluminum" : "TBD";
        AddUpdateFixtureText(doc, services, assembly, part, $"{part.Name}\nQTY : {part.Quantity}\n1\" | {material}", true);
    }

    private static RhinoObject? FindFixturePartLabel(RhinoDoc doc, RegressionServices services, AssemblyRecord assembly, PartRecord part)
    {
        var labelLayer = doc.Layers.FirstOrDefault(layer => !layer.IsDeleted && layer.FullPath == LayerService.PartsPart(assembly.Name, part.Name) + "::text");
        return labelLayer == null ? null : (doc.Objects.FindByLayer(labelLayer) ?? Array.Empty<RhinoObject>())
            .SingleOrDefault(obj => obj.Geometry is TextEntity text && text.PlainText.Replace("\r", string.Empty).StartsWith(part.Name + "\nQTY :", StringComparison.Ordinal));
    }

    private static void AddAssemblyUpdateRowHeader(RhinoDoc doc, RegressionServices services, AssemblyRecord assembly, PartRecord part)
    {
        var material = part.MaterialId == "BIRCH" ? "Birch plywood" : part.MaterialId == "ALUMINUM" ? "Aluminum" : "TBD";
        using var text = new TextEntity
        {
            PlainText = $"{material} | 1\"",
            Plane = new Plane(new Point3d(200 + assembly.Parts.IndexOf(part) * 40, 20, 0), Vector3d.ZAxis),
            TextHeight = 0.125,
            DimensionScale = 12,
            Justification = TextJustification.TopLeft
        };
        Require(doc.Objects.AddText(text, new ObjectAttributes
        { LayerIndex = services.Layers.EnsureLayerIndex(doc, LayerService.PartsAssembly(assembly.Name) + "::row labels") }) != Guid.Empty,
            "The legacy material row header must be created.");
    }

    private static Guid AddUpdateFixtureText(RhinoDoc doc, RegressionServices services, AssemblyRecord assembly, PartRecord part, string value, bool generated)
    {
        using var text = new TextEntity
        {
            PlainText = value,
            Plane = new Plane(new Point3d(205 + assembly.Parts.IndexOf(part) * 40, generated ? 4 : -2, 0), Vector3d.ZAxis),
            TextHeight = 0.125,
            DimensionScale = 12,
            Justification = TextJustification.TopCenter
        };
        return doc.Objects.AddText(text, new ObjectAttributes
        {
            LayerIndex = services.Layers.EnsureLayerIndex(doc, LayerService.PartsPart(assembly.Name, part.Name) + "::text")
        });
    }

    private static void AssertSynchronizedFlatOutputs(RhinoDoc doc, RegressionServices services, AssemblyRecord assembly)
    {
        foreach (var part in assembly.Parts)
        {
            var flatIds = part.CamObjectIds.Where(id => doc.Objects.FindId(id) != null).ToList();
            Require(flatIds.Count == 1, $"{part.Name} must have exactly one live flat representative, found {flatIds.Count}.");
            var flat = doc.Objects.FindId(flatIds.Single());
            var flatNode = assembly.LinkGraph.Nodes.Single(node => node.ObjectId == flat.Id && node.Role == AssemblyLinkRoles.FlatPart);
            var edge = assembly.LinkGraph.Edges.Single(edge => edge.ChildNodeId == flatNode.Id);
            var parent = assembly.LinkGraph.Nodes.Single(node => node.Id == edge.ParentNodeId);
            Require(flatNode.PartId == part.Id && parent.PartId == part.Id, "The flat representative and its incoming source must agree on category.");
            Require(edge.ParentToChildTransform.TryToTransform(out var transform), "Every synchronized flat must have a readable source transform.");
            using var expected = doc.Objects.FindId(parent.ObjectId).Geometry.Duplicate();
            Require(expected.Transform(transform), "The stored flat transform must apply successfully.");
            var expectedBounds = expected.GetBoundingBox(true);
            var actualBounds = flat.Geometry.GetBoundingBox(true);
            Require(expectedBounds.Min.DistanceTo(actualBounds.Min) < 0.001 && expectedBounds.Max.DistanceTo(actualBounds.Max) < 0.001,
                "The flat's persisted transformation must map current parent geometry to current flat geometry.");
            Require(Math.Abs(actualBounds.Diagonal.Z - part.MaterialThickness) < 0.001,
                "Flat output thickness must agree with the updated part definition.");
            var layerPath = doc.Layers[flat.Attributes.LayerIndex].FullPath;
            Require(layerPath == LayerService.PartsPart(assembly.Name, part.Name) + "::3D", "Flat output must be on its current category's 3D layer.");
            var textLayer = doc.Layers.FirstOrDefault(layer => !layer.IsDeleted && layer.FullPath == LayerService.PartsPart(assembly.Name, part.Name) + "::text");
            var texts = (textLayer == null ? Array.Empty<RhinoObject>() : doc.Objects.FindByLayer(textLayer) ?? Array.Empty<RhinoObject>())
                .Where(obj => obj.Geometry is TextEntity text && text.PlainText.Replace("\r", string.Empty).StartsWith(part.Name + "\nQTY :", StringComparison.Ordinal))
                .Select(obj => ((TextEntity)obj.Geometry).PlainText.Replace("\r", string.Empty)).ToList();
            Require(texts.Count == 1 && texts.Single().Contains("QTY : " + part.Quantity + "\n", StringComparison.Ordinal),
                $"{part.Name} must have exactly one current quantity label, got [{string.Join(" | ", texts)}].");
            if (part.CategorizationMaterialId == "ALUMINUM")
            {
                Require(MaterialAssignment.GetCategorizationMaterialId(flat.Attributes) == "ALUMINUM", "Flat material metadata must match updated category material.");
                Require(texts.Single().Contains("Aluminum", StringComparison.OrdinalIgnoreCase), "Flat label must describe the updated material type.");
            }
        }
        var rowLayer = doc.Layers.FirstOrDefault(layer => !layer.IsDeleted && layer.FullPath == LayerService.PartsAssembly(assembly.Name) + "::row labels");
        var rowTexts = (rowLayer == null ? Array.Empty<RhinoObject>() : doc.Objects.FindByLayer(rowLayer) ?? Array.Empty<RhinoObject>())
            .Where(obj => obj.Geometry is TextEntity).Select(obj => ((TextEntity)obj.Geometry).PlainText).ToArray();
        if (assembly.Parts.All(part => part.CategorizationMaterialId == "ALUMINUM"))
            Require(rowTexts.Any(text => text.StartsWith("Aluminum |", StringComparison.Ordinal)) &&
                rowTexts.All(text => !text.StartsWith("Birch plywood |", StringComparison.Ordinal)),
                "Material row headers must update after all categories merge into the new material type.");
    }

    private sealed class RegressionMaterialLibrary : IMaterialLibrary
    {
        public IReadOnlyList<MaterialRecord> GetMaterials(RhinoDoc? doc = null) => Array.Empty<MaterialRecord>();
        public MaterialRecord? FindById(RhinoDoc? doc, string materialId) => null;
        public MaterialRecord? ResolveForEstimate(RhinoDoc? doc, string materialId, double requiredWidth, double requiredHeight, double requiredThickness) => null;
        public string GetMaterialLabel(RhinoDoc? doc, string materialId) => materialId.ToUpperInvariant() switch
        {
            "BIRCH" => "Birch plywood",
            "ALUMINUM" => "Aluminum",
            "" => "TBD",
            _ => materialId
        };
    }
}
