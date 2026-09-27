using System.Text.Json;
using System.Text.Json.Nodes;
using AssemblyManagerPlugin.Core;
using AssemblyManagerPlugin.Services;
using Rhino;
using Rhino.Geometry;
using Rhino.Runtime.InProcess;

internal static partial class Program
{
    private static void RunInputComponentAdditionSafetyScenario(
        RhinoCore core, RegressionServices services, string scenario, List<RhinoDoc> fixtureDocs)
    {
        var doc = RhinoDoc.Create(null);
        fixtureDocs.Add(doc);
        doc.ModelUnitSystem = UnitSystem.Inches;
        doc.ModelAbsoluteTolerance = 0.001;
        services.AutomaticallyPropagate = true;
        var fixture = CreateComponentAdditionFixture(doc, services, scenario, false);
        var additionId = CreateInputSafetyAddition(doc, services, fixture, scenario);
        PumpIdle(core, services);
        var before = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
        var sourceGroupIndex = FindComponentFixtureGroup(doc,
            before.LinkGraph.SourceComponentInstances.Single(instance => instance.Id == fixture.TemplateInstanceId).SourceGroupId);

        if (scenario == "input-safety-undo-stage")
        {
            doc.UndoRecordingEnabled = true;
            Require(doc.UndoRecordingEnabled && !doc.UndoRecordingIsActive,
                "The native fixture must support a fresh undo record; this test must not silently skip undo.");
            doc.ClearUndoRecords(true);
            var originalStore = InputSafetyStoredJson(doc);
            var originalAttributes = InputSafetyAttributes(doc, additionId);
            var objectIds = InputSafetyObjectIds(doc);
            services.LinkEvents.StageInputComponentAddition(doc, fixture.AssemblyName, fixture.TemplateInstanceId, additionId);
            PumpIdle(core, services);
            Require(services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!.PendingComponentUpdates.Count == 1 &&
                doc.Objects.FindId(additionId).Attributes.IsInGroup(sourceGroupIndex),
                "Staging must persist its plan and add the existing UUID to the input group.");

            Require(doc.Undo(), "Rhino must undo the complete Add Part To Component staging record.");
            PumpIdle(core, services);
            Require(InputSafetyStoredJson(doc) == originalStore && InputSafetyAttributes(doc, additionId) == originalAttributes &&
                InputSafetyObjectIds(doc).SequenceEqual(objectIds),
                "Undo staging must restore both document metadata and the added object's original membership, without deleting its UUID.");
            // Direct RhinoDoc.Undo in this hidden host leaves UndoActive and recording
            // active. An independent point/string control with Gazelle stopped also
            // cannot Redo. Do not close native records by hand or claim desktop redo QA.
            Console.WriteLine($"{scenario}: Native Undo restored the complete stage. Redo is not verified in the hidden host; interactive Visual Studio testing is required.");
            return;
        }

        services.LinkEvents.StageInputComponentAddition(doc, fixture.AssemblyName, fixture.TemplateInstanceId, additionId);
        PumpIdle(core, services);
        if (scenario == "input-safety-undo-apply")
        {
            doc.UndoRecordingEnabled = true;
            Require(doc.UndoRecordingEnabled && !doc.UndoRecordingIsActive,
                "The native fixture must support a fresh undo record for Update Assembly.");
            doc.ClearUndoRecords(true);
            var stagedStore = InputSafetyStoredJson(doc);
            var stagedAttributes = InputSafetyAttributes(doc, additionId);
            var stagedObjectIds = InputSafetyObjectIds(doc);
            services.LinkEvents.UpdateAssembly(doc, fixture.AssemblyName);
            PumpIdle(core, services);
            var appliedObjectIds = InputSafetyObjectIds(doc);
            var applied = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
            Require(applied.PendingComponentUpdates.Count == 0 && applied.LinkGraph.Nodes.Any(node =>
                    node.ObjectId == additionId && node.Role == AssemblyLinkRoles.Source) &&
                appliedObjectIds.Length > stagedObjectIds.Length,
                "The undo fixture must first apply the real source addition and create downstream output.");

            Require(doc.Undo(), "Rhino must undo the complete input addition application record.");
            PumpIdle(core, services);
            Require(InputSafetyStoredJson(doc) == stagedStore && InputSafetyObjectIds(doc).SequenceEqual(stagedObjectIds) &&
                InputSafetyAttributes(doc, additionId) == stagedAttributes,
                "Undo Update Assembly must remove created output, retain the input UUID, and restore the pending plan and source attributes.");
            Console.WriteLine($"{scenario}: Native Undo restored the pending addition and removed its output. Redo is not verified in the hidden host; interactive Visual Studio testing is required.");
            return;
        }

        if (scenario == "input-safety-legacy-direction")
        {
            var store = services.Repository.Load(doc);
            var assembly = store.FindAssembly(fixture.AssemblyName)!;
            var pendingJson = JsonNode.Parse(JsonSerializer.Serialize(assembly.PendingComponentUpdates.Single()))!.AsObject();
            Require(pendingJson.Remove(nameof(PendingComponentUpdateRecord.AdditionOrigin)),
                "The compatibility fixture must actually remove the direction discriminator.");
            var legacyPending = pendingJson.Deserialize<PendingComponentUpdateRecord>()!;
            Require(legacyPending.AdditionOrigin == ComponentAdditionOrigins.Original,
                "Older pending records without AdditionOrigin must retain the established original-side default.");
            assembly.PendingComponentUpdates[0] = legacyPending;
            var objectIds = InputSafetyObjectIds(doc);
            var sourceAttributes = InputSafetyAttributes(doc, additionId);
            var savedJson = InputSafetyStoredJson(doc);
            ExpectComponentUpdateRejection(() => services.ComponentUpdates.ApplyPending(doc, store, assembly),
                "An input plan interpreted using the older original-side default must fail closed, not inverse-copy or adopt the wrong object.");
            Require(InputSafetyObjectIds(doc).SequenceEqual(objectIds) && InputSafetyAttributes(doc, additionId) == sourceAttributes &&
                InputSafetyStoredJson(doc) == savedJson && assembly.LinkGraph.Nodes.Count == before.LinkGraph.Nodes.Count &&
                assembly.PendingComponentUpdates.Count == 1,
                "Missing-direction rejection must preserve geometry, input attributes, the pending plan, and stored metadata.");
            return;
        }

        Require(scenario == "input-safety-rollback-adoption", "The input safety scenario must be recognized.");
        var secondAdditionId = CreateInputSafetyAddition(doc, services, fixture, scenario + "-second");
        services.LinkEvents.StageInputComponentAddition(doc, fixture.AssemblyName, fixture.TemplateInstanceId, secondAdditionId);
        PumpIdle(core, services);
        var staged = services.Repository.Load(doc);
        var stagedAssembly = staged.FindAssembly(fixture.AssemblyName)!;
        var assemblyJson = JsonSerializer.Serialize(stagedAssembly);
        var storedJson = InputSafetyStoredJson(doc);
        var stagedIds = InputSafetyObjectIds(doc);
        var attributesBefore = new[] { additionId, secondAdditionId }.ToDictionary(id => id, id => InputSafetyAttributes(doc, id));
        var layersBefore = doc.Layers.Where(layer => !layer.IsDeleted).Select(layer => layer.Id).OrderBy(id => id).ToArray();
        var groupsBefore = doc.Groups.GroupNames(true).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        var preferenceReadCount = 0;
        var witnessedAdoption = false;
        var failingService = new ComponentUpdateService(services.Repository, services.Layers, services.Fingerprints, services.Lineage,
            () =>
            {
                if (++preferenceReadCount == 2)
                {
                    var source = stagedAssembly.LinkGraph.Nodes.SingleOrDefault(node => node.ObjectId == additionId &&
                        node.Role == AssemblyLinkRoles.Source);
                    witnessedAdoption = source is not null && stagedAssembly.LinkGraph.Edges.Any(edge => edge.ParentNodeId == source.Id) &&
                        InputSafetyObjectIds(doc).Length > stagedIds.Length;
                    throw new InvalidOperationException("Intentional second-addition preference failure after first source adoption.");
                }
                return true;
            });
        ExpectComponentUpdateRejection(() => failingService.ApplyPending(doc, staged, stagedAssembly),
            "A failure after the first source adoption must escape rather than report a successful partial update.");
        Require(witnessedAdoption && preferenceReadCount == 2,
            "The rollback fixture must fail after materializing the first addition, not during preflight.");
        Require(JsonSerializer.Serialize(stagedAssembly) == assemblyJson && InputSafetyStoredJson(doc) == storedJson &&
            InputSafetyObjectIds(doc).SequenceEqual(stagedIds) &&
            attributesBefore.All(item => InputSafetyAttributes(doc, item.Key) == item.Value),
            "Failed application must restore the complete assembly snapshot and both source attributes without deleting either adopted input UUID.");
        Require(doc.Layers.Where(layer => !layer.IsDeleted).Select(layer => layer.Id).OrderBy(id => id).SequenceEqual(layersBefore) &&
            doc.Groups.GroupNames(true).OrderBy(name => name, StringComparer.Ordinal).SequenceEqual(groupsBefore),
            "Failed application must remove its new empty output layers and preserve existing groups.");
        services.LinkEvents.UpdateAssembly(doc, fixture.AssemblyName);
        PumpIdle(core, services);
        var recovered = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
        Require(recovered.PendingComponentUpdates.Count == 0 && new[] { additionId, secondAdditionId }.All(id =>
                recovered.LinkGraph.Nodes.Count(node => node.ObjectId == id && node.Role == AssemblyLinkRoles.Source) == 1) &&
            recovered.Parts.Sum(part => part.Quantity) == before.Parts.Sum(part => part.Quantity) + 2,
            "A normal retry after rollback must adopt each existing source once and count exactly two added parts.");
    }

    private static Guid CreateInputSafetyAddition(RhinoDoc doc, RegressionServices services,
        ComponentAdditionFixture fixture, string scenario)
    {
        using var mutation = AssemblyLinkMutationGate.Enter();
        var id = AddComponentFixtureAddition(doc, services, fixture, scenario, false, false);
        Require(fixture.SourceToOriginal.TryGetInverse(out var originalToSource), "The safety fixture placement must be invertible.");
        using var geometry = (Brep)doc.Objects.FindId(id).Geometry.Duplicate();
        Require(geometry.Transform(originalToSource) && doc.Objects.Replace(id, geometry),
            "The safety addition must occupy input space without changing its UUID.");
        using var attributes = doc.Objects.FindId(id).Attributes.Duplicate();
        attributes.SetUserString("RegressionInputAnnotation", "Preserve the operator's source metadata");
        Require(doc.Objects.ModifyAttributes(id, attributes, true), "The source metadata fixture must be assigned.");
        return id;
    }

    private static string InputSafetyStoredJson(RhinoDoc doc) =>
        doc.Strings.GetValue(AssemblyManagerConstants.StoreSection, AssemblyManagerConstants.StoreEntry);

    private static string InputSafetyAttributes(RhinoDoc doc, Guid id)
    {
        var attributes = doc.Objects.FindId(id).Attributes;
        // A stand-alone 3dm attributes archive remaps group indices. Compare actual
        // attribute values, group membership and user strings, not opaque archive bytes.
        return JsonSerializer.Serialize(new
        {
            attributes.ObjectId, attributes.Name, attributes.Url, attributes.LayerIndex,
            attributes.Mode, attributes.Visible, attributes.IsInstanceDefinitionObject,
            attributes.MaterialIndex, attributes.MaterialSource,
            attributes.ColorSource, Color = attributes.ObjectColor.ToArgb(),
            attributes.PlotColorSource, PlotColor = attributes.PlotColor.ToArgb(),
            attributes.PlotWeightSource, attributes.PlotWeight,
            attributes.LinetypeSource, attributes.LinetypeIndex,
            attributes.WireDensity, attributes.ObjectDecoration, attributes.DisplayOrder,
            attributes.ViewportId, attributes.Space, attributes.CastsShadows, attributes.ReceivesShadows,
            Groups = (attributes.GetGroupList() ?? Array.Empty<int>()).OrderBy(index => index),
            UserStrings = attributes.GetUserStrings().AllKeys.OrderBy(key => key, StringComparer.Ordinal)
                .Select(key => new { Key = key, Value = attributes.GetUserString(key) })
        });
    }

    private static Guid[] InputSafetyObjectIds(RhinoDoc doc) =>
        ComponentFixtureObjects(doc).Select(obj => obj.Id).OrderBy(id => id).ToArray();
}
