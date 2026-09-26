using AssemblyManagerPlugin.Core;
using AssemblyManagerPlugin.Services;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Runtime.InProcess;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;

internal static partial class Program
{
    private static void RunPlacementPerformanceScenario(RhinoCore core, RegressionServices services, string scenario, List<RhinoDoc> fixtureDocs)
    {
        if (scenario == "placement-repository-normalization")
        {
            RunPlacementRepositoryNormalization();
            return;
        }
        if (scenario == "placement-local-metadata")
        {
            RunPlacementMetadataScenario(core, services, fixtureDocs);
            return;
        }

        var doc = RhinoDoc.Create(null);
        fixtureDocs.Add(doc);
        doc.ModelUnitSystem = UnitSystem.Inches;
        doc.ModelAbsoluteTolerance = 0.001;
        services.AutomaticallyPropagate = true;
        var fixture = CreateComponentAdditionFixture(doc, services, scenario, ambiguous: false, singleton: true);
        PumpIdle(core, services);
        var initial = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
        var instance = initial.LinkGraph.SourceComponentInstances.Single();
        var originals = initial.LinkGraph.Nodes.Where(node => node.Role == AssemblyLinkRoles.OriginalAssembly)
            .Select(node => node.ObjectId).ToList();
        Require(doc.Groups.Delete(FindComponentFixtureGroup(doc, instance.GeneratedGroupId)), "The hardware fixture must ungroup.");
        originals.Add(AddComponentFixtureAddition(doc, services, fixture, "component-add-block-hardware", false, true));
        originals.Add(AddComponentFixtureAddition(doc, services, fixture, "component-add-block-hardware", false, true));
        var regrouped = doc.Groups.Add("Moved hardware fixture", originals);
        services.LinkEvents.StageComponentUpdate(doc, fixture.AssemblyName, fixture.ComponentId, doc.Groups.FindIndex(regrouped).Id);
        services.LinkEvents.UpdateAssembly(doc, fixture.AssemblyName);
        PumpIdle(core, services);
        var before = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
        var copied = before.LinkGraph.Nodes.Where(node => node.Role == AssemblyLinkRoles.CopiedComponent).ToArray();
        var hardware = copied.Where(node => doc.Objects.FindId(node.ObjectId) is InstanceObject).ToArray();
        Require(hardware.Length == 2 && copied.Length == 5, "The fixture must contain two block instances and three parts in its copied component.");
        var initialMatrices = hardware.ToDictionary(node => node.Id, node => ((InstanceObject)doc.Objects.FindId(node.ObjectId)).InstanceXform);
        var initialObjectIds = before.LinkGraph.Nodes.ToDictionary(node => node.Id, node => node.ObjectId);
        var initialPartCategories = before.Parts.ToDictionary(part => part.Id, part => (part.Name, part.Quantity));

        var translation = Transform.Translation(73, -29, 17);
        var rotation = Transform.Rotation(Math.PI / 3, new Vector3d(1, 2, 3), new Point3d(18, -9, 5));
        if (scenario == "placement-hardware-definition-change")
        {
            var oldObject = (InstanceObject)doc.Objects.FindId(hardware[0].ObjectId);
            var alternate = (InstanceObject)doc.Objects.FindId(hardware[1].ObjectId);
            EnqueuePlacementTransform(doc, services, new[] { oldObject.Id }, translation);
            using var changed = new InstanceReferenceGeometry(alternate.InstanceDefinition.Id, translation * oldObject.InstanceXform);
            Require(doc.Objects.Replace(oldObject.Id, changed, false), "The instance-definition replacement must succeed.");
            PumpIdle(core, services);
            var changedAssembly = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
            Require(changedAssembly.LinkGraph.Conflicts.Any(conflict => conflict.NodeId == hardware[0].Id &&
                    conflict.ConflictType == AssemblyLinkConflictTypes.DerivedGeometryChanged && conflict.Status == AssemblyLinkStatuses.Open),
                "A swapped hardware definition must remain a shape edit even when accompanied by a matching placement matrix.");
            return;
        }

        foreach (var transform in new[] { translation, rotation })
        {
            var current = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
            var ids = copied.Select(node => current.LinkGraph.Nodes.Single(candidate => candidate.Id == node.Id).ObjectId).ToArray();
            EnqueuePlacementTransform(doc, services, ids, transform);
            foreach (var id in ids)
                Require(doc.Objects.Transform(id, transform, true) != Guid.Empty, "Each copied component member must move.");
        }
        // Several placements may queue before Rhino yields idle. Both must be composed
        // in order, using each block replacement's own matrix rather than the final one.
        PumpIdle(core, services);
        var moved = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
        Require(!moved.LinkGraph.Conflicts.Any(conflict => conflict.Status == AssemblyLinkStatuses.Open),
            "Moving hardware and solids together must not quarantine the copied hardware as changed geometry.");
        Require(moved.Parts.Count == initialPartCategories.Count && moved.Parts.All(part => initialPartCategories[part.Id] == (part.Name, part.Quantity)),
            "Pure placement must not change part categories or quantities.");
        foreach (var node in hardware)
        {
            var current = moved.LinkGraph.Nodes.Single(candidate => candidate.Id == node.Id);
            AssertPlacementMatrix(rotation * translation * initialMatrices[node.Id], ((InstanceObject)doc.Objects.FindId(current.ObjectId)).InstanceXform,
                "The native block instance must reach its final translated and rotated placement.");
            var edge = moved.LinkGraph.Edges.Single(candidate => candidate.ChildNodeId == node.Id);
            var parent = moved.LinkGraph.Nodes.Single(candidate => candidate.Id == edge.ParentNodeId);
            Require(edge.ParentToChildTransform.TryToTransform(out var relation), "The moved block relationship must remain readable.");
            AssertPlacementMatrix(relation * ((InstanceObject)doc.Objects.FindId(parent.ObjectId)).InstanceXform,
                ((InstanceObject)doc.Objects.FindId(current.ObjectId)).InstanceXform,
                "The stored block relationship must include both user placement operations.");
            var storedMetadata = doc.Objects.FindId(current.ObjectId).Attributes.GetUserString(AssemblyManagerConstants.ReferenceTransformUserString)!;
            var values = storedMetadata.Split(',').Select(value => double.Parse(value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            Require(values.SequenceEqual(edge.ParentToChildTransform.Values), "Block placement recovery metadata must match the graph.");
        }

        // Change manufacturing geometry and then explicitly refresh the entire assembly,
        // exercising the same hardware reconstruction route as Update Assembly in the UI.
        var source = moved.LinkGraph.Nodes.First(node => node.Role == AssemblyLinkRoles.Source && node.PartId != Guid.Empty);
        var bounds = doc.Objects.FindId(source.ObjectId).Geometry.GetBoundingBox(true);
        using (var edited = Brep.CreateFromBox(new BoundingBox(bounds.Min, bounds.Max + new Vector3d(1, 0, 0))))
            Require(doc.Objects.Replace(source.ObjectId, edited), "The source geometry edit must succeed.");
        PumpIdle(core, services);
        services.LinkEvents.UpdateAssembly(doc, fixture.AssemblyName);
        PumpIdle(core, services);
        var refreshed = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
        foreach (var node in hardware)
        {
            var current = refreshed.LinkGraph.Nodes.Single(candidate => candidate.Id == node.Id);
            Require(doc.Objects.FindId(current.ObjectId) is InstanceObject, "Refreshing must retain hardware as block instances.");
            AssertPlacementMatrix(rotation * translation * initialMatrices[node.Id], ((InstanceObject)doc.Objects.FindId(current.ObjectId)).InstanceXform,
                "Update Assembly must preserve the final hardware placement, not its initial copied location.");
        }
        Require(refreshed.LinkGraph.Nodes.All(node => initialObjectIds[node.Id] == node.ObjectId),
            "Moving and refreshing a component must preserve linked object UUIDs.");
    }

    private static void RunPlacementMetadataScenario(RhinoCore core, RegressionServices services, List<RhinoDoc> fixtureDocs)
    {
        var doc = RhinoDoc.Create(null);
        fixtureDocs.Add(doc);
        doc.ModelUnitSystem = UnitSystem.Inches;
        doc.ModelAbsoluteTolerance = 0.001;
        services.AutomaticallyPropagate = true;
        var assembly = new AssemblyRecord { Name = "Regression-placement-local-metadata", NextPartSequence = 2 };
        var part = new PartRecord { Name = "P01", Quantity = 200, MaterialThickness = 1 };
        assembly.Parts.Add(part);
        using (AssemblyLinkMutationGate.Enter())
        {
            for (var index = 0; index < 200; index++)
            {
                AddFixtureOccurrence(doc, services, assembly, part, index * 20, 10);
                var original = part.GeneratedObjectIds[^1];
                AddLayerFixtureOutput(doc, services, assembly, original, AssemblyLinkRoles.CopiedComponent,
                    LayerService.CopiedComponentPart(assembly.Name, "C01", part.Name), Transform.Translation(0, 100, 0));
            }
            services.Repository.Save(doc, new AssemblyStore { Assemblies = new List<AssemblyRecord> { assembly } });
        }
        PumpIdle(core, services);
        var elapsed = new List<double>();
        var captureTimes = new List<double>();
        var nativeMoveTimes = new List<double>();
        var idleTimes = new List<double>();
        var metadataWrites = new List<int>();
        var selected = assembly.LinkGraph.Nodes.Where(node => node.Role == AssemblyLinkRoles.CopiedComponent).Take(8).Select(node => node.Id).ToArray();
        for (var trial = 0; trial < 4; trial++)
        {
            var current = services.Repository.Load(doc).FindAssembly(assembly.Name)!;
            var ids = selected.Select(id => current.LinkGraph.Nodes.Single(node => node.Id == id).ObjectId).ToArray();
            var writes = 0;
            EventHandler<RhinoModifyObjectAttributesEventArgs> onModified = (_, e) => { if (e.Document == doc) writes++; };
            RhinoDoc.ModifyObjectAttributes += onModified;
            var timer = Stopwatch.StartNew();
            try
            {
                EnqueuePlacementTransform(doc, services, ids, Transform.Translation(1, 2, 0));
                var captureElapsed = timer.Elapsed.TotalMilliseconds;
                captureTimes.Add(captureElapsed);
                foreach (var id in ids)
                    Require(doc.Objects.Transform(id, Transform.Translation(1, 2, 0), true) != Guid.Empty, "The timed copied-member move must succeed.");
                var nativeElapsed = timer.Elapsed.TotalMilliseconds;
                nativeMoveTimes.Add(nativeElapsed - captureElapsed);
                // Measure exactly one production idle pass, without PumpIdle's deliberate
                // message-loop waiting. This includes snapshot capture, native replacements,
                // transform classification, metadata writes and repository persistence.
                typeof(AssemblyLinkEventService).GetMethod("OnIdle", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(services.LinkEvents, new object?[] { null, EventArgs.Empty });
                idleTimes.Add(timer.Elapsed.TotalMilliseconds - nativeElapsed);
            }
            finally
            {
                timer.Stop();
                RhinoDoc.ModifyObjectAttributes -= onModified;
            }
            elapsed.Add(timer.Elapsed.TotalMilliseconds);
            metadataWrites.Add(writes);
        }
        Console.WriteLine($"Placement timing: 8 moved copies / 400 links; milliseconds=[{string.Join(", ", elapsed.Select(value => value.ToString("F1")))}]; attribute writes=[{string.Join(", ", metadataWrites)}]");
        Console.WriteLine($"Placement profile medians: pre-transform capture={captureTimes.OrderBy(value => value).ElementAt(2):F2}ms; native move/callbacks={nativeMoveTimes.OrderBy(value => value).ElementAt(2):F2}ms; idle bookkeeping={idleTimes.OrderBy(value => value).ElementAt(2):F2}ms");
        Require(metadataWrites.All(count => count == selected.Length),
            "A copied-member move must rewrite only its affected relationships, never all unrelated outputs.");
        var final = services.Repository.Load(doc).FindAssembly(assembly.Name)!;
        Require(final.Parts.Count == 1 && final.Parts[0].Quantity == 200 && final.LinkGraph.Conflicts.All(conflict => conflict.Status != AssemblyLinkStatuses.Open),
            "Optimized placement processing must preserve categories, quantities and healthy links.");
    }

    private static void EnqueuePlacementTransform(RhinoDoc doc, RegressionServices services, Guid[] objectIds, Transform transform)
    {
        var eventType = typeof(AssemblyLinkEventService);
        var objects = objectIds.Select(id => doc.Objects.FindId(id)).ToArray();
        var transformCapture = eventType.GetMethod("CaptureTransformGeometrySnapshots", BindingFlags.NonPublic | BindingFlags.Static);
        // The fallback lets the same benchmark reproduce the old build before optimizing.
        var snapshot = transformCapture is not null
            ? transformCapture.Invoke(null, new object[] { objects, transform })
            : eventType.GetMethod("CaptureGeometrySnapshots", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, new object[] { objects });
        var factType = eventType.GetNestedType("TransformFact", BindingFlags.NonPublic)!;
        var fact = Activator.CreateInstance(factType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, new object[] { objectIds.ToImmutableArray(), transform, false, snapshot!, Guid.NewGuid() }, null);
        eventType.GetMethod("Enqueue", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(services.LinkEvents, new[] { (object)doc.RuntimeSerialNumber, fact! });
    }

    private static void AssertPlacementMatrix(Transform expected, Transform actual, string message)
    {
        for (var row = 0; row < 4; row++)
            for (var column = 0; column < 4; column++)
                Require(Math.Abs(expected[row, column] - actual[row, column]) < 1e-7, message);
    }

    private static void RunPlacementRepositoryNormalization()
    {
        var assembly = new AssemblyRecord { Name = "Regression-recovery-indexes" };
        var sourceId = Guid.NewGuid();
        var placement = TransformRecord.FromTransform(Transform.Translation(12, 34, 56));
        for (var index = 0; index < 2; index++)
            assembly.GeometryReferences.Add(new GeometryReferenceRecord
            {
                SourceObjectId = sourceId, TargetObjectId = Guid.NewGuid(),
                SourceToTargetTransform = placement, PartName = "P01",
                TargetRole = AssemblyManagerConstants.GeneratedAssemblyReferenceRole
            });
        var store = new AssemblyStore { SchemaVersion = 1, Assemblies = new List<AssemblyRecord> { assembly } };
        AssemblyRepository.Normalize(store);
        Require(assembly.LinkGraph.Nodes.Count == 3 && assembly.LinkGraph.Edges.Count == 2 &&
                assembly.LinkGraph.Nodes.Count(node => node.ObjectId == sourceId) == 1,
            "Recovery indexes must reuse a source first created by an earlier legacy reference.");
        Require(assembly.GeometryReferences.All(reference => assembly.LinkGraph.Edges.Any(edge =>
                edge.Id == reference.Id && edge.ParentToChildTransform.Values.SequenceEqual(reference.SourceToTargetTransform.Values))),
            "Migration must preserve each legacy relationship identity and complete transform.");
        var normalized = System.Text.Json.JsonSerializer.Serialize(store);
        AssemblyRepository.Normalize(store);
        Require(System.Text.Json.JsonSerializer.Serialize(store) == normalized,
            "Normalizing an already migrated graph must remain idempotent.");

        assembly.GeometryReferences.Add(new GeometryReferenceRecord
        {
            SourceObjectId = sourceId, TargetObjectId = assembly.GeometryReferences[0].TargetObjectId,
            SourceToTargetTransform = TransformRecord.FromTransform(Transform.Translation(90, 0, 0)),
            TargetRole = AssemblyManagerConstants.GeneratedAssemblyReferenceRole, PartName = "P01"
        });
        AssemblyRepository.Normalize(store);
        Require(assembly.LinkGraph.Edges.Count == 2 && assembly.LinkGraph.Conflicts.Any(conflict =>
                conflict.ConflictType == AssemblyLinkConflictTypes.LegacyMigrationIncomplete && conflict.EdgeId == assembly.GeometryReferences[0].Id),
            "Conflicting duplicate recovery references must preserve the first graph edge and require review.");
        var malformed = System.Text.Json.JsonSerializer.Serialize(store);
        var duplicateNode = System.Text.Json.JsonSerializer.Deserialize<AssemblyStore>(malformed)!;
        duplicateNode.Assemblies[0].LinkGraph.Nodes.Add(new AssemblyLinkNodeRecord
        {
            Id = assembly.LinkGraph.Nodes[0].Id, ObjectId = Guid.NewGuid(), Role = AssemblyLinkRoles.Source
        });
        RequireNormalizationRejected(duplicateNode, "Duplicate node identities must not be hidden by first-match indexing.");
        var duplicateObject = System.Text.Json.JsonSerializer.Deserialize<AssemblyStore>(malformed)!;
        duplicateObject.Assemblies[0].LinkGraph.Nodes.Add(new AssemblyLinkNodeRecord
        {
            ObjectId = sourceId, Role = AssemblyLinkRoles.Source
        });
        RequireNormalizationRejected(duplicateObject, "Duplicate object ownership must still be rejected after indexing.");
        var duplicateEdge = System.Text.Json.JsonSerializer.Deserialize<AssemblyStore>(malformed)!;
        duplicateEdge.Assemblies[0].LinkGraph.Edges.Add(new AssemblyLinkEdgeRecord
        {
            Id = assembly.LinkGraph.Edges[0].Id, ParentNodeId = assembly.LinkGraph.Edges[0].ParentNodeId,
            ChildNodeId = assembly.LinkGraph.Edges[0].ChildNodeId, ParentToChildTransform = placement
        });
        RequireNormalizationRejected(duplicateEdge, "Duplicate edge identities must still be rejected after indexing.");
    }

    private static void RequireNormalizationRejected(AssemblyStore store, string message)
    {
        try { AssemblyRepository.Normalize(store); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException(message);
    }
}
