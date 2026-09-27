using AssemblyManagerPlugin.Core;
using AssemblyManagerPlugin.Services;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Runtime.InProcess;

internal static partial class Program
{
    private static void RunComponentUpdateScenario(RhinoCore core, RegressionServices services, string scenario, List<RhinoDoc> fixtureDocs)
    {
        if (scenario.EndsWith("unrelated-original", StringComparison.Ordinal))
        {
            RunComponentUpdateWithUnrelatedOriginalEdit(core, services, scenario, fixtureDocs);
            return;
        }
        if (scenario.EndsWith("reserved-addition", StringComparison.Ordinal))
        {
            RunComponentReservedAdditionRejection(core, services, scenario, fixtureDocs);
            return;
        }
        if (scenario.EndsWith("shared-copied-group", StringComparison.Ordinal))
        {
            RunComponentSharedCopiedGroupRejection(core, services, scenario, fixtureDocs);
            return;
        }
        var doc = RhinoDoc.Create(null);
        fixtureDocs.Add(doc);
        doc.ModelUnitSystem = UnitSystem.Inches;
        doc.ModelAbsoluteTolerance = 0.001;
        services.AutomaticallyPropagate = true;
        var symmetric = scenario.EndsWith("symmetric-occurrence", StringComparison.Ordinal);
        var singleton = scenario.EndsWith("singleton", StringComparison.Ordinal);
        var noRepresentative = scenario.EndsWith("nonrepresentative", StringComparison.Ordinal);
        var fixture = CreateComponentAdditionFixture(doc, services, scenario, symmetric, singleton);
        if (noRepresentative)
            fixture = fixture with { TemplateInstanceId = fixture.SourceFrames.Keys.Single(id => id != fixture.TemplateInstanceId) };
        PumpIdle(core, services);
        var before = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
        var retainedIds = before.LinkGraph.Nodes.Select(node => node.ObjectId).ToHashSet();
        var retainedBounds = retainedIds.ToDictionary(id => id, id => doc.Objects.FindId(id).Geometry.GetBoundingBox(true));
        var firstPartColor = services.Layers.FindPartLayerColor(doc, before.Name, "P01");
        var template = before.LinkGraph.SourceComponentInstances.Single(instance => instance.Id == fixture.TemplateInstanceId);
        var oldSourceGroupId = template.SourceGroupId;
        var oldOriginalGroupId = template.GeneratedGroupId;
        var originalMembers = before.LinkGraph.Nodes.Where(node => node.SourceComponentInstanceId == template.Id &&
            (node.Role == AssemblyLinkRoles.OriginalAssembly || node.Role == AssemblyLinkRoles.Hardware)).Select(node => node.ObjectId).ToList();
        var oldGroupIndex = FindComponentFixtureGroup(doc, oldOriginalGroupId);
        Require(doc.Groups.Delete(oldGroupIndex), "Ungrouping the chosen original component must succeed.");

        var hardware = scenario.Contains("hardware", StringComparison.Ordinal);
        var existingPart = scenario.EndsWith("existing-part", StringComparison.Ordinal) || scenario.EndsWith("copied-member", StringComparison.Ordinal);
        var addedId = AddComponentFixtureAddition(doc, services, fixture, scenario, existingPart, hardware);
        if (noRepresentative)
        {
            Require(fixture.SourceToOriginal.TryGetInverse(out var undoPlacement), "The fixture original placement must be invertible.");
            var placement = fixture.SourceToOriginal * fixture.SourceFrames[template.Id] * undoPlacement;
            using var placedAddition = (Brep)doc.Objects.FindId(addedId).Geometry.Duplicate();
            Require(placedAddition.Transform(placement) && doc.Objects.Replace(addedId, placedAddition),
                "The addition must move to the selected non-representative occurrence while retaining its UUID.");
        }
        var chosenMembers = originalMembers.ToList();
        chosenMembers.Add(addedId);
        if (scenario.EndsWith("omitted-member", StringComparison.Ordinal))
            chosenMembers.Remove(originalMembers.Last());
        if (scenario.EndsWith("linked-member", StringComparison.Ordinal))
            chosenMembers.Add(before.LinkGraph.Nodes.First(node => node.Role == AssemblyLinkRoles.OriginalAssembly &&
                node.SourceComponentInstanceId != template.Id).ObjectId);
        var newGroupIndex = doc.Groups.Add(fixture.AssemblyName + "-operator-regrouped", chosenMembers);
        Require(newGroupIndex >= 0, "The operator's replacement component group must be created.");
        var newGroupId = doc.Groups.FindIndex(newGroupIndex).Id;
        PumpIdle(core, services);
        Require(services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!.LinkGraph.Nodes.Count == before.LinkGraph.Nodes.Count,
            "Merely regrouping must not infer structural membership or create linked counterparts.");

        var rejectedAtStage = scenario.Contains("omitted-member", StringComparison.Ordinal) ||
            scenario.Contains("linked-member", StringComparison.Ordinal);
        if (rejectedAtStage)
        {
            var objectCount = ComponentFixtureObjects(doc).Count;
            ExpectComponentUpdateRejection(() => services.LinkEvents.StageComponentUpdate(doc, fixture.AssemblyName, fixture.ComponentId, newGroupId),
                "Unsafe regrouping must be rejected before any linked geometry is added.");
            var rejected = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
            Require(rejected.PendingComponentUpdates.Count == 0, "Rejected regrouping must not leave a staged plan.");
            Require(ComponentFixtureObjects(doc).Count == objectCount && retainedIds.All(id => doc.Objects.FindId(id) != null),
                "Rejected regrouping must preserve all current user geometry.");
            return;
        }

        var countBeforeStage = ComponentFixtureObjects(doc).Count;
        services.LinkEvents.StageComponentUpdate(doc, fixture.AssemblyName, fixture.ComponentId, newGroupId);
        var staged = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
        Require(staged.PendingComponentUpdates.Count == 1, "Update Component must persist one pending structural update.");
        var pending = staged.PendingComponentUpdates.Single();
        Require(pending.ComponentId == fixture.ComponentId && pending.TemplateInstanceId == template.Id &&
            pending.RegroupedGroupId == newGroupId && pending.AddedObjectIds.SequenceEqual(new[] { addedId }) &&
            pending.InstanceIds.SequenceEqual(new[] { template.Id }) && pending.MemberNodeIdsByInstance.Keys.SequenceEqual(new[] { template.Id }),
            "The pending plan must remember the replacement group and addition for only the selected occurrence.");
        var rebound = staged.LinkGraph.SourceComponentInstances.Single(instance => instance.Id == template.Id);
        Require(rebound.GeneratedGroupId == newGroupId && rebound.GeneratedGroupId != oldOriginalGroupId && rebound.SourceGroupId == oldSourceGroupId,
            "Update Component must rebind only the ORIGINAL ASSEMBLIES group identity, preserving input group identity.");
        Require(ComponentFixtureObjects(doc).Count == countBeforeStage && staged.LinkGraph.Nodes.Count == before.LinkGraph.Nodes.Count,
            "Staging must not create source, sibling-original, copied, or flat geometry.");
        PumpIdle(core, services);
        var idle = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
        Require(idle.PendingComponentUpdates.Count == 1 && idle.LinkGraph.Nodes.Count == before.LinkGraph.Nodes.Count,
            "Automatic propagation must leave structural additions staged until Update Assembly is explicitly requested.");

        if (scenario.Contains("stale-", StringComparison.Ordinal))
        {
            if (scenario.EndsWith("stale-group", StringComparison.Ordinal))
            {
                using var unexpected = Brep.CreateFromBox(new BoundingBox(new Point3d(40, 120, 0), new Point3d(42, 122, 1)));
                var unexpectedId = doc.Objects.AddBrep(unexpected);
                Require(doc.Groups.AddToGroup(newGroupIndex, unexpectedId), "An unexpected later group addition must succeed.");
            }
            else
            {
                // Bypass callbacks deliberately: the persisted stage must be revalidated from
                // live retained geometry, not trusted only because no event was captured.
                using var mutation = AssemblyLinkMutationGate.Enter();
                var sourceId = before.LinkGraph.Nodes.First(node => node.Role == AssemblyLinkRoles.Source &&
                    node.SourceComponentInstanceId == template.Id).ObjectId;
                var bounds = doc.Objects.FindId(sourceId).Geometry.GetBoundingBox(true);
                using var changed = Brep.CreateFromBox(new BoundingBox(bounds.Min, bounds.Max + new Vector3d(0, 0, 2)));
                Require(doc.Objects.Replace(sourceId, changed), "An unobserved retained-source edit must succeed.");
            }
            var countBeforeFailure = ComponentFixtureObjects(doc).Count;
            ExpectComponentUpdateRejection(() => services.LinkEvents.UpdateAssembly(doc, fixture.AssemblyName),
                "A staged plan whose membership or geometry changed must be rejected when applied.");
            var rejected = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
            Require(rejected.PendingComponentUpdates.Count == 1 && rejected.LinkGraph.Nodes.Count == before.LinkGraph.Nodes.Count &&
                ComponentFixtureObjects(doc).Count == countBeforeFailure,
                "Failed revalidation must retain the pending plan without partially creating linked additions.");
            return;
        }

        if (scenario.EndsWith("restart-manual", StringComparison.Ordinal))
        {
            services.LinkEvents.Stop();
            var restarted = new RegressionServices();
            restarted.LinkEvents.Start();
            try
            {
                PumpIdle(core, restarted);
                Require(restarted.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!.PendingComponentUpdates.Count == 1 &&
                    ComponentFixtureObjects(doc).Count == countBeforeStage,
                    "Restarting listeners with automatic updates on must preserve the staged plan without applying it.");
                restarted.LinkEvents.UpdateAssembly(doc, fixture.AssemblyName);
                PumpIdle(core, restarted);
            }
            finally
            {
                restarted.LinkEvents.Stop();
                services.LinkEvents.Start();
            }
        }
        else
        {
            services.LinkEvents.UpdateAssembly(doc, fixture.AssemblyName);
            PumpIdle(core, services);
        }

        var after = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
        Require(after.PendingComponentUpdates.Count == 0, "A successful manual update must consume the staged addition exactly once.");
        Require(retainedIds.All(id => after.LinkGraph.Nodes.Any(node => node.ObjectId == id) && doc.Objects.FindId(id) != null),
            "Adding a member must retain every old linked UUID, including copied and flat representatives.");
        foreach (var id in retainedIds)
        {
            var actual = doc.Objects.FindId(id).Geometry.GetBoundingBox(true);
            Require(actual.Min.DistanceTo(retainedBounds[id].Min) < 0.001 && actual.Max.DistanceTo(retainedBounds[id].Max) < 0.001,
                "Adding a member must preserve the placement and geometry of all retained objects.");
        }
        var updatedInstance = after.LinkGraph.SourceComponentInstances.Single(instance => instance.Id == template.Id);
        var updatedComponent = after.Components.Single(component => component.Id == updatedInstance.ComponentId);
        Require(updatedComponent.Quantity == 1, "Only the selected occurrence must belong to the edited design.");
        if (singleton)
            Require(after.Components.Count == 1 && updatedComponent.Id == fixture.ComponentId && updatedComponent.Name == "C01",
                "A singleton component may retain its original component identity and number.");
        else
            Require(after.Components.Count == 2 && updatedComponent.Id != fixture.ComponentId && updatedComponent.Name == "C02" &&
                after.Components.Single(component => component.Id == fixture.ComponentId) is { Name: "C01", Quantity: 1 } &&
                after.LinkGraph.SourceComponentInstances.Single(instance => instance.Id != template.Id).ComponentId == fixture.ComponentId,
                "The unchanged occurrence must retain C01 and the selected edited occurrence must become C02.");
        var addedOriginals = after.LinkGraph.Nodes.Where(node => !retainedIds.Contains(node.ObjectId) &&
            (node.Role == AssemblyLinkRoles.OriginalAssembly || node.Role == AssemblyLinkRoles.Hardware)).ToList();
        var addedSources = after.LinkGraph.Nodes.Where(node => !retainedIds.Contains(node.ObjectId) && node.Role == AssemblyLinkRoles.Source).ToList();
        var addedCopies = after.LinkGraph.Nodes.Where(node => !retainedIds.Contains(node.ObjectId) && node.Role == AssemblyLinkRoles.CopiedComponent).ToList();
        Require(addedOriginals.Count == 1 && addedSources.Count == 1 && addedCopies.Count == (symmetric || noRepresentative ? 0 : 1),
            "An addition must create one input counterpart, adopt the selected original, and extend only its existing copied representative.");
        Require(addedOriginals.Any(node => node.ObjectId == addedId), "The user's selected added original object must retain its UUID.");
        foreach (var node in after.LinkGraph.Nodes.Where(node => node.Role is AssemblyLinkRoles.OriginalAssembly or
                     AssemblyLinkRoles.Hardware or AssemblyLinkRoles.CopiedComponent))
        {
            var component = after.Components.Single(component => component.Id == node.ComponentId);
            var partName = after.Parts.SingleOrDefault(part => part.Id == node.PartId)?.Name ?? after.Hardware.Single(item =>
                after.LinkGraph.Nodes.Any(source => source.Id == after.LinkGraph.Edges.Single(edge => edge.ChildNodeId == node.Id).ParentNodeId &&
                    (source.ObjectId == item.SourceObjectId || source.ObjectId == item.GeneratedObjectId))).LayerName;
            var expectedPath = node.Role == AssemblyLinkRoles.CopiedComponent
                ? LayerService.CopiedComponentPart(after.Name, component.Name, partName)
                : LayerService.OriginalPart(after.Name, component.Name, partName);
            Require(doc.Layers[doc.Objects.FindId(node.ObjectId).Attributes.LayerIndex].FullPath == expectedPath,
                "Original and copied members must follow the correct old or newly assigned component layer.");
        }
        Require(updatedComponent.PartQuantities.Values.Sum() == before.LinkGraph.SourceComponentInstances
                .Single(instance => instance.Id == template.Id).SourceNodeIds.Count + 1,
            "The edited component's membership summary must include its added part or hardware.");
        if (!singleton)
            Require(after.Components.Single(component => component.Id == fixture.ComponentId).PartQuantities
                .OrderBy(pair => pair.Key).SequenceEqual(before.Components.Single(component => component.Id == fixture.ComponentId)
                    .PartQuantities.OrderBy(pair => pair.Key)),
                "The unchanged component type must preserve its original part/hardware summary.");
        foreach (var instance in after.LinkGraph.SourceComponentInstances)
        {
            var previousInstance = before.LinkGraph.SourceComponentInstances.Single(previous => previous.Id == instance.Id);
            if (instance.Id != template.Id)
            {
                Require(instance.SourceNodeIds.SequenceEqual(previousInstance.SourceNodeIds) &&
                    instance.SourceGroupId == previousInstance.SourceGroupId && instance.GeneratedGroupId == previousInstance.GeneratedGroupId &&
                    !addedSources.Concat(addedOriginals).Concat(addedCopies).Any(node => node.SourceComponentInstanceId == instance.Id),
                    "The untouched occurrence must preserve its membership, group identities, and receive no additions.");
                var unchangedOriginalIds = before.LinkGraph.Nodes.Where(node => node.SourceComponentInstanceId == instance.Id &&
                    node.Role == AssemblyLinkRoles.OriginalAssembly).Select(node => node.ObjectId).OrderBy(id => id);
                Require((doc.Objects.FindByGroup(FindComponentFixtureGroup(doc, instance.GeneratedGroupId)) ?? Array.Empty<RhinoObject>())
                    .Select(obj => obj.Id).OrderBy(id => id).SequenceEqual(unchangedOriginalIds),
                    "The unchanged original group must still contain exactly its old members.");
                continue;
            }
            Require(instance.SourceNodeIds.Count == previousInstance.SourceNodeIds.Count + 1,
                "Only the selected occurrence must record an additional source member.");
            var source = addedSources.Single(node => node.SourceComponentInstanceId == instance.Id);
            Require(instance.SourceNodeIds.Contains(source.Id), "The newly linked source must belong to its matching occurrence.");
            var sourceGroupIndex = FindComponentFixtureGroup(doc, instance.SourceGroupId);
            Require((doc.Objects.FindByGroup(sourceGroupIndex) ?? Array.Empty<RhinoObject>()).Any(obj => obj.Id == source.ObjectId),
                "Input counterpart additions must join the existing input group.");
            var generated = addedOriginals.Single(node => node.SourceComponentInstanceId == instance.Id);
            var originalGroupIndex = FindComponentFixtureGroup(doc, instance.GeneratedGroupId);
            Require((doc.Objects.FindByGroup(originalGroupIndex) ?? Array.Empty<RhinoObject>()).Any(obj => obj.Id == generated.ObjectId),
                "The selected addition must join its occurrence's current original group.");
            using var expectedSource = doc.Objects.FindId(addedId).Geometry.Duplicate();
            Require(fixture.SourceToOriginal.TryGetInverse(out var undoOriginal), "Fixture original placement must be invertible.");
            Require(expectedSource.Transform(undoOriginal), "Expected addition must map from the selected original to its own input.");
            AssertComponentFixtureGeometryBounds(expectedSource, doc.Objects.FindId(source.ObjectId).Geometry,
                "The added input member must follow its occurrence's rigid rotation and translation.");
        }
        foreach (var node in addedOriginals.Concat(addedCopies))
        {
            var edge = after.LinkGraph.Edges.Single(edge => edge.ChildNodeId == node.Id);
            var parent = after.LinkGraph.Nodes.Single(parent => parent.Id == edge.ParentNodeId);
            using var expected = doc.Objects.FindId(parent.ObjectId).Geometry.Duplicate();
            Require(edge.ParentToChildTransform.TryToTransform(out var transform) && expected.Transform(transform),
                "New generated output must store a usable parent-to-child placement.");
            AssertComponentFixtureGeometryBounds(expected, doc.Objects.FindId(node.ObjectId).Geometry,
                "Stored output placement must map its current parent to the generated addition.");
        }
        if (hardware)
        {
            if (scenario.EndsWith("name-collision", StringComparison.Ordinal))
            {
                Require(after.Hardware.All(item => !after.Parts.Any(part => part.Name == item.LayerName)),
                    "A hardware identifier matching P01 must not share a manufacturing part layer/name.");
                Require(services.Layers.FindPartLayerColor(doc, after.Name, "P01") == firstPartColor,
                    "Adding hardware named P01 must preserve the existing manufacturing part's color.");
            }
            Require(after.Parts.Count == 3 && after.Parts.Sum(part => part.Quantity) == 6 && after.Hardware.Count == 1,
                "Hardware must count only in the selected occurrence without entering manufacturing part categories.");
            Require(after.Hardware.All(item => item.Quantity == 1 && item.ComponentName == updatedComponent.Name &&
                addedSources.Any(node => node.ObjectId == item.SourceObjectId) && addedOriginals.Any(node => node.ObjectId == item.GeneratedObjectId)),
                "Hardware BOM records must bind the correct per-occurrence source and original IDs.");
            foreach (var node in addedSources.Concat(addedOriginals).Concat(addedCopies))
            {
                var obj = doc.Objects.FindId(node.ObjectId);
                Console.WriteLine($"{scenario} hardware node {node.ObjectId}: role={node.Role}; part={node.PartId}; type={obj?.GetType().Name}; geometry={obj?.Geometry.GetType().Name}; marker={obj?.Attributes.GetUserString(AssemblyManagerConstants.ObjectRoleUserString)}; identifier={obj?.Attributes.GetUserString(AssemblyManagerConstants.HardwareIdentifierUserString)}; name={obj?.Attributes.GetUserString(AssemblyManagerConstants.HardwareNameUserString)}; linkRole={obj?.Attributes.GetUserString(AssemblyManagerConstants.LinkRoleUserString)}");
            }
            foreach (var conflict in after.LinkGraph.Conflicts.Where(conflict => conflict.Status == AssemblyLinkStatuses.Open))
                Console.WriteLine($"{scenario} OPEN {conflict.ConflictType}: {conflict.Message}");
            Require(addedSources.Concat(addedOriginals).Concat(addedCopies).All(node =>
                HardwareMetadata.HasHardwareRole(doc.Objects.FindId(node.ObjectId).Attributes)),
                "Hardware metadata must survive on all input, original and copied instances.");
            if (scenario.Contains("block-hardware", StringComparison.Ordinal))
                Require(addedSources.Concat(addedOriginals).Concat(addedCopies).All(node => doc.Objects.FindId(node.ObjectId) is InstanceObject),
                    "Added imported block hardware must remain whole block instances at every stage.");
            var bom = new BomService(services.Repository, new DocumentActionHistorySink(services.Repository)).GenerateBom(doc, fixture.AssemblyName);
            Require(bom.Lines.Count(line => line.Category == "Hardware") == 1 && bom.Lines.Single(line => line.Category == "Hardware").Quantity == 1,
                "The BOM must count the added hardware only once, in the selected occurrence.");
        }
        else
        {
            Require(after.Parts.Sum(part => part.Quantity) == before.Parts.Sum(part => part.Quantity) + 1 &&
                after.Parts.Count == before.Parts.Count + (existingPart ? 0 : 1),
                "Part additions must count only once and merge into an existing category only when matching.");
            var newPartIds = addedSources.Select(node => node.PartId).Distinct().ToArray();
            Require(newPartIds.Length == 1 && newPartIds[0] != Guid.Empty, "The added source must have one valid part category.");
            var additionPart = after.Parts.Single(part => part.Id == newPartIds[0]);
            Require(existingPart ? additionPart.Id == fixture.FirstPartId && additionPart.Quantity == 3 : additionPart.Quantity == 1,
                "A matching P01 addition must produce three P01s; a new category must contain one part.");
        }
        if (!symmetric)
            AssertSynchronizedFlatOutputs(doc, services, after);
        if (noRepresentative)
            Require(after.LinkGraph.Nodes.Where(node => node.Role == AssemblyLinkRoles.CopiedComponent)
                .All(node => node.ComponentId == fixture.ComponentId && node.SourceComponentInstanceId != template.Id),
                "An edited non-representative occurrence must not steal or alter the old component's copied representative.");
        Require(after.LinkGraph.Conflicts.All(conflict => conflict.Status != AssemblyLinkStatuses.Open),
            "A complete, unambiguous additive component update must leave no open membership or geometry issues.");

        var completedObjectIds = after.LinkGraph.Nodes.Select(node => node.ObjectId).OrderBy(id => id).ToArray();
        var completedPartQuantities = after.Parts.ToDictionary(part => part.Id, part => part.Quantity);
        services.LinkEvents.UpdateAssembly(doc, fixture.AssemblyName);
        PumpIdle(core, services);
        var repeated = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
        Require(repeated.LinkGraph.Nodes.Select(node => node.ObjectId).OrderBy(id => id).SequenceEqual(completedObjectIds) &&
            repeated.Parts.All(part => completedPartQuantities.GetValueOrDefault(part.Id) == part.Quantity) && repeated.Hardware.Count == after.Hardware.Count,
            "Repeated Update Assembly must not duplicate additions or change stable part quantities/UUIDs.");
    }

    private static void RunComponentUpdateWithUnrelatedOriginalEdit(RhinoCore core, RegressionServices services, string scenario, List<RhinoDoc> fixtureDocs)
    {
        var doc = RhinoDoc.Create(null);
        fixtureDocs.Add(doc);
        doc.ModelUnitSystem = UnitSystem.Inches;
        doc.ModelAbsoluteTolerance = 0.001;
        services.AutomaticallyPropagate = true;
        var fixture = CreateComponentAdditionFixture(doc, services, scenario, false);
        Guid unrelatedSourceId;
        Guid unrelatedOriginalId;
        using (AssemblyLinkMutationGate.Enter())
        {
            var store = services.Repository.Load(doc);
            var assembly = store.FindAssembly(fixture.AssemblyName)!;
            var part = new PartRecord { Name = "P04", Quantity = 1, MaterialThickness = 1 };
            assembly.Parts.Add(part);
            assembly.NextPartSequence = 5;
            assembly.NextComponentSequence = 3;
            unrelatedSourceId = AddFixtureOccurrence(doc, services, assembly, part, 170, 13);
            unrelatedOriginalId = part.GeneratedObjectIds.Single();
            AddLayerFixtureComponent(doc, services, assembly, "C02", new[] { unrelatedSourceId });
            services.Repository.Save(doc, store);
        }
        PumpIdle(core, services);
        var before = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
        var template = before.LinkGraph.SourceComponentInstances.Single(instance => instance.Id == fixture.TemplateInstanceId);
        var members = before.LinkGraph.Nodes.Where(node => node.SourceComponentInstanceId == template.Id && node.Role == AssemblyLinkRoles.OriginalAssembly)
            .Select(node => node.ObjectId).ToList();
        Require(doc.Groups.Delete(FindComponentFixtureGroup(doc, template.GeneratedGroupId)), "The C01 template must ungroup.");
        members.Add(AddComponentFixtureAddition(doc, services, fixture, scenario, false, false));
        var groupIndex = doc.Groups.Add(fixture.AssemblyName + "-regrouped", members);
        Require(groupIndex >= 0, "The C01 replacement group must exist.");
        services.LinkEvents.StageComponentUpdate(doc, fixture.AssemblyName, fixture.ComponentId, doc.Groups.FindIndex(groupIndex).Id);
        ReplaceUpdateFixtureBox(doc, unrelatedOriginalId, 17);
        PumpIdle(core, services);
        Require(Math.Abs(doc.Objects.FindId(unrelatedSourceId).Geometry.GetBoundingBox(true).Diagonal.X - 13) < 0.001 &&
            Math.Abs(doc.Objects.FindId(unrelatedOriginalId).Geometry.GetBoundingBox(true).Diagonal.X - 17) < 0.001,
            "Staging C01 must defer an unrelated C02 original edit without discarding it.");
        services.LinkEvents.UpdateAssembly(doc, fixture.AssemblyName);
        PumpIdle(core, services);
        var after = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
        Require(after.PendingComponentUpdates.Count == 0 && after.Components.Single(component => component.Id == fixture.ComponentId).Quantity == 1,
            "Manual update must preserve one unchanged C01 occurrence.");
        Require(after.LinkGraph.SourceComponentInstances.Single(instance => instance.Id == template.Id).SourceNodeIds.Count == 4 &&
            after.LinkGraph.SourceComponentInstances.Single(instance => instance.ComponentId == fixture.ComponentId).SourceNodeIds.Count == 3,
            "Only the selected occurrence must gain a fourth source member.");
        Require(Math.Abs(doc.Objects.FindId(unrelatedSourceId).Geometry.GetBoundingBox(true).Diagonal.X - 17) < 0.001 &&
            Math.Abs(doc.Objects.FindId(unrelatedOriginalId).Geometry.GetBoundingBox(true).Diagonal.X - 17) < 0.001,
            "Manual structural update must promote the unrelated C02 original edit before source-to-original refresh, never overwrite it.");
        var unrelatedNode = after.LinkGraph.Nodes.Single(node => node.ObjectId == unrelatedOriginalId);
        Require(!unrelatedNode.Metadata.ContainsKey("PendingOriginalUpdate") &&
            !after.LinkGraph.Nodes.Single(node => node.ObjectId == unrelatedSourceId).Metadata.ContainsKey("PendingSourceUpdate"),
            "Successfully processed unrelated deferred edits must not leave stale pending authority markers.");
    }

    private static void RunComponentReservedAdditionRejection(RhinoCore core, RegressionServices services, string scenario, List<RhinoDoc> fixtureDocs)
    {
        var doc = RhinoDoc.Create(null);
        fixtureDocs.Add(doc);
        doc.ModelUnitSystem = UnitSystem.Inches;
        doc.ModelAbsoluteTolerance = 0.001;
        services.AutomaticallyPropagate = true;
        var fixture = CreateComponentAdditionFixture(doc, services, scenario, false);
        Guid otherComponentId;
        Guid otherOriginalId;
        Guid otherGroupId;
        using (AssemblyLinkMutationGate.Enter())
        {
            var store = services.Repository.Load(doc);
            var assembly = store.FindAssembly(fixture.AssemblyName)!;
            var part = new PartRecord { Name = "P04", Quantity = 1, MaterialThickness = 1 };
            assembly.Parts.Add(part);
            assembly.NextPartSequence = 5;
            assembly.NextComponentSequence = 3;
            var sourceId = AddFixtureOccurrence(doc, services, assembly, part, 170, 13);
            AddLayerFixtureComponent(doc, services, assembly, "C02", new[] { sourceId });
            otherComponentId = assembly.Components.Single(component => component.Name == "C02").Id;
            otherOriginalId = part.GeneratedObjectIds.Single();
            otherGroupId = assembly.LinkGraph.SourceComponentInstances.Single(instance => instance.ComponentId == otherComponentId).GeneratedGroupId;
            services.Repository.Save(doc, store);
        }
        PumpIdle(core, services);
        var before = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
        var template = before.LinkGraph.SourceComponentInstances.Single(instance => instance.Id == fixture.TemplateInstanceId);
        var members = before.LinkGraph.Nodes.Where(node => node.SourceComponentInstanceId == template.Id && node.Role == AssemblyLinkRoles.OriginalAssembly)
            .Select(node => node.ObjectId).ToList();
        Require(doc.Groups.Delete(FindComponentFixtureGroup(doc, template.GeneratedGroupId)), "The first template must ungroup.");
        var additionId = AddComponentFixtureAddition(doc, services, fixture, scenario, false, false);
        members.Add(additionId);
        var firstGroup = doc.Groups.Add(fixture.AssemblyName + "-regrouped-C01", members);
        Require(firstGroup >= 0, "The first replacement group must exist.");
        services.LinkEvents.StageComponentUpdate(doc, fixture.AssemblyName, fixture.ComponentId, doc.Groups.FindIndex(firstGroup).Id);
        Require(doc.Groups.Delete(FindComponentFixtureGroup(doc, otherGroupId)), "The second component must ungroup.");
        var secondGroup = doc.Groups.Add(fixture.AssemblyName + "-regrouped-C02", new[] { otherOriginalId, additionId });
        Require(secondGroup >= 0, "The second group sharing the staged addition must exist.");
        var countBeforeFailure = ComponentFixtureObjects(doc).Count;
        ExpectComponentUpdateRejection(() => services.LinkEvents.StageComponentUpdate(doc, fixture.AssemblyName,
                otherComponentId, doc.Groups.FindIndex(secondGroup).Id),
            "One operator object must not be reserved as the new original in two pending component types.");
        var after = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
        Require(after.PendingComponentUpdates.Count == 1 && after.PendingComponentUpdates.Single().ComponentId == fixture.ComponentId &&
            ComponentFixtureObjects(doc).Count == countBeforeFailure && after.LinkGraph.Nodes.Count == before.LinkGraph.Nodes.Count,
            "Rejecting a competing reservation must preserve the first pending plan and all user geometry without creating counterparts.");
    }

    private static void RunComponentSharedCopiedGroupRejection(RhinoCore core, RegressionServices services, string scenario, List<RhinoDoc> fixtureDocs)
    {
        var doc = RhinoDoc.Create(null);
        fixtureDocs.Add(doc);
        doc.ModelUnitSystem = UnitSystem.Inches;
        doc.ModelAbsoluteTolerance = 0.001;
        services.AutomaticallyPropagate = true;
        var fixture = CreateComponentAdditionFixture(doc, services, scenario, false);
        Guid dependentAssemblyId;
        int sharedGroupIndex;
        Guid[] copiedIds;
        using (AssemblyLinkMutationGate.Enter())
        {
            var store = services.Repository.Load(doc);
            var assembly = store.FindAssembly(fixture.AssemblyName)!;
            var copiedNodes = assembly.LinkGraph.Nodes.Where(node => node.Role == AssemblyLinkRoles.CopiedComponent).ToList();
            copiedIds = copiedNodes.Select(node => node.ObjectId).OrderBy(id => id).ToArray();
            sharedGroupIndex = doc.Objects.FindId(copiedIds[0]).Attributes.GetGroupList()!
                .Single(index => doc.Groups.FindIndex(index) is { IsDeleted: false });
            var sharedGroup = doc.Groups.FindIndex(sharedGroupIndex);
            var dependent = new AssemblyRecord { Name = "Dependent-" + scenario };
            dependentAssemblyId = dependent.Id;
            var component = new ComponentRecord { Name = "C01", Quantity = 1 };
            dependent.Components.Add(component);
            var instance = new SourceComponentInstanceRecord
            {
                ComponentId = component.Id, SourceGroupId = sharedGroup.Id,
                SourceGroupIndex = sharedGroupIndex, SourceGroupName = sharedGroup.Name
            };
            dependent.LinkGraph.SourceComponentInstances.Add(instance);
            foreach (var copied in copiedNodes)
            {
                var originalPart = assembly.Parts.Single(part => part.Id == copied.PartId);
                var part = new PartRecord
                {
                    Name = originalPart.Name, Quantity = 1, GeometryFingerprint = originalPart.GeometryFingerprint,
                    MaterialThickness = originalPart.MaterialThickness, MaterialId = originalPart.MaterialId,
                    CategorizationMaterialId = originalPart.CategorizationMaterialId,
                    SourceObjectIds = new List<Guid> { copied.ObjectId }
                };
                dependent.Parts.Add(part);
                var source = services.Lineage.EnsureNode(dependent, copied.ObjectId, AssemblyLinkRoles.Source,
                    part.Id, component.Id, instance.Id);
                source.GeometryFingerprint = copied.GeometryFingerprint;
                instance.SourceNodeIds.Add(source.Id);
            }
            store.Assemblies.Add(dependent);
            services.Repository.Save(doc, store);
        }
        PumpIdle(core, services);
        var before = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
        var template = before.LinkGraph.SourceComponentInstances.Single(instance => instance.Id == fixture.TemplateInstanceId);
        var members = before.LinkGraph.Nodes.Where(node => node.SourceComponentInstanceId == template.Id && node.Role == AssemblyLinkRoles.OriginalAssembly)
            .Select(node => node.ObjectId).ToList();
        Require(doc.Groups.Delete(FindComponentFixtureGroup(doc, template.GeneratedGroupId)), "The original template must ungroup.");
        members.Add(AddComponentFixtureAddition(doc, services, fixture, scenario, false, false));
        var replacementGroup = doc.Groups.Add(fixture.AssemblyName + "-operator-regrouped", members);
        Require(replacementGroup >= 0, "The template's replacement group must exist.");
        var countBeforeStage = ComponentFixtureObjects(doc).Count;
        ExpectComponentUpdateRejection(() => services.LinkEvents.StageComponentUpdate(doc, fixture.AssemblyName,
                fixture.ComponentId, doc.Groups.FindIndex(replacementGroup).Id),
            "A copied output group that is another assembly's input group must not acquire additions through suppressed mutations.");
        var afterStore = services.Repository.Load(doc);
        var after = afterStore.FindAssembly(fixture.AssemblyName)!;
        var dependentAfter = afterStore.Assemblies.Single(assembly => assembly.Id == dependentAssemblyId);
        Require(after.PendingComponentUpdates.Count == 0 && after.LinkGraph.Nodes.Count == before.LinkGraph.Nodes.Count &&
            ComponentFixtureObjects(doc).Count == countBeforeStage,
            "Rejecting a shared copied-group target must leave no staged plan or partially added counterparts.");
        Require((doc.Objects.FindByGroup(sharedGroupIndex) ?? Array.Empty<RhinoObject>()).Select(obj => obj.Id).OrderBy(id => id).SequenceEqual(copiedIds) &&
            dependentAfter.LinkGraph.SourceComponentInstances.Single().SourceNodeIds.Count == copiedIds.Length,
            "The dependent assembly's copied-source group and recorded source membership must remain unchanged.");
    }

    private static ComponentAdditionFixture CreateComponentAdditionFixture(RhinoDoc doc, RegressionServices services, string scenario, bool ambiguous, bool singleton = false)
    {
        using var mutation = AssemblyLinkMutationGate.Enter();
        var assembly = new AssemblyRecord { Name = "Regression-" + scenario, NextPartSequence = ambiguous ? 2 : 4, NextComponentSequence = 2 };
        var occurrenceCount = singleton ? 1 : 2;
        var component = new ComponentRecord { Name = "C01", Quantity = occurrenceCount };
        assembly.Components.Add(component);
        var boxes = new[]
        {
            new BoundingBox(new Point3d(0, 0, 0), new Point3d(8, 3, 1)),
            new BoundingBox(new Point3d(15, 2, 3), new Point3d(20, 4, 4)),
            new BoundingBox(new Point3d(3, 13, 7), new Point3d(7, 16, 9))
        }.Take(ambiguous ? 1 : 3).ToArray();
        foreach (var (bounds, index) in boxes.Select((bounds, index) => (bounds, index)))
        {
            using var geometry = Brep.CreateFromBox(bounds);
            var part = new PartRecord
            {
                Name = $"P{index + 1:00}", Quantity = occurrenceCount, GeometryFingerprint = services.Fingerprints.CreatePartFingerprint(geometry),
                MaterialThickness = services.Fingerprints.GetMaterialThickness(geometry), MaterialId = "BIRCH", CategorizationMaterialId = "BIRCH"
            };
            assembly.Parts.Add(part);
            component.PartNames.Add(part.Name);
            component.PartQuantities[part.Name] = 1;
        }
        var sourceToOriginal = Transform.Translation(0, 100, 0);
        var frames = new[] { Transform.Identity, Transform.Translation(80, 25, 4) * Transform.Rotation(Math.PI / 2, Vector3d.ZAxis, Point3d.Origin) };
        var sourceFrames = new Dictionary<Guid, Transform>();
        Guid templateInstanceId = Guid.Empty;
        for (var occurrence = 0; occurrence < occurrenceCount; occurrence++)
        {
            var sources = new List<Guid>();
            var originals = new List<Guid>();
            foreach (var (bounds, index) in boxes.Select((bounds, index) => (bounds, index)))
            {
                var part = assembly.Parts[index];
                using var geometry = Brep.CreateFromBox(bounds);
                Require(geometry.Transform(frames[occurrence]), "Retained source fixture must follow its occurrence frame.");
                using var attributes = new ObjectAttributes { Name = part.Name + " input" };
                MaterialAssignment.Set(attributes, new MaterialDefinitionRecord { Id = "BIRCH", Name = "Birch plywood" });
                var sourceId = doc.Objects.AddBrep(geometry, attributes);
                Require(sourceId != Guid.Empty, "Retained source fixture must be created.");
                sources.Add(sourceId);
                part.SourceObjectIds.Add(sourceId);
                Require(geometry.Transform(sourceToOriginal), "Original fixture placement must succeed.");
                attributes.LayerIndex = services.Layers.EnsureLayerIndex(doc, LayerService.OriginalPart(assembly.Name, component.Name, part.Name));
                attributes.Name = part.Name;
                var originalId = doc.Objects.AddBrep(geometry, attributes);
                Require(originalId != Guid.Empty, "Retained original fixture must be created.");
                originals.Add(originalId);
                part.GeneratedObjectIds.Add(originalId);
                component.ObjectIds.Add(originalId);
                if (occurrence == 0)
                    component.RepresentativeObjectIdsByPartName[part.Name] = new List<Guid> { originalId };
            }
            var sourceGroupName = assembly.Name + "-input-" + occurrence;
            var originalGroupName = assembly.Name + "-original-" + occurrence;
            var sourceGroupIndex = doc.Groups.Add(sourceGroupName, sources);
            Require(doc.Groups.Add(originalGroupName, originals) >= 0 && sourceGroupIndex >= 0, "Retained component groups must be created.");
            component.InstanceGroupNames.Add(originalGroupName);
            var instance = services.Lineage.RegisterSourceComponentInstance(doc, assembly, component, sourceGroupIndex, sourceGroupName, sources, originalGroupName);
            sourceFrames[instance.Id] = frames[occurrence];
            if (occurrence == 0)
                templateInstanceId = instance.Id;
            for (var index = 0; index < sources.Count; index++)
            {
                var part = assembly.Parts[index];
                var link = services.Lineage.RegisterDerived(doc, assembly, sources[index], AssemblyLinkRoles.Source,
                    originals[index], AssemblyLinkRoles.OriginalAssembly, sourceToOriginal,
                    partId: part.Id, componentId: component.Id, sourceComponentInstanceId: instance.Id);
                link.Parent.GeometryFingerprint = part.GeometryFingerprint;
                link.Child.GeometryFingerprint = part.GeometryFingerprint;
                assembly.GeometryReferences.Add(new GeometryReferenceRecord
                {
                    Id = link.Edge.Id, AssemblyName = assembly.Name, ComponentName = component.Name, PartName = part.Name,
                    SourceObjectId = sources[index], TargetObjectId = originals[index], TargetRole = AssemblyManagerConstants.GeneratedAssemblyReferenceRole,
                    SourceToTargetTransform = TransformRecord.FromTransform(sourceToOriginal)
                });
            }
        }
        var componentCandidates = assembly.Parts.Select(part =>
        {
            Require(services.Fingerprints.TryCreatePartCandidate(doc.Objects.FindId(part.SourceObjectIds[0]), out var candidate), "Retained part fingerprint must be readable.");
            candidate.PartName = part.Name;
            candidate.MaterialId = part.CategorizationMaterialId ?? MaterialAssignment.NormalizeMaterialIdForCategory(part.MaterialId);
            return candidate;
        }).ToList();
        component.Fingerprint = services.Fingerprints.CreateComponentFingerprint(componentCandidates);
        componentCandidates.ForEach(candidate => candidate.Geometry.Dispose());
        if (!ambiguous)
        {
            var copiedIds = new List<Guid>();
            var copyTransform = Transform.Translation(300, -80, 0) * Transform.Rotation(Math.PI / 6, Vector3d.ZAxis, Point3d.Origin);
            foreach (var part in assembly.Parts)
            {
                var originalId = part.GeneratedObjectIds[0];
                var copyId = AddLayerFixtureOutput(doc, services, assembly, originalId, AssemblyLinkRoles.CopiedComponent,
                    LayerService.CopiedComponentPart(assembly.Name, component.Name, part.Name), copyTransform);
                // Real copied components retain assigned material metadata on their copies.
                using var attributes = doc.Objects.FindId(copyId).Attributes.Duplicate();
                MaterialAssignment.Copy(doc.Objects.FindId(originalId).Attributes, attributes);
                Require(doc.Objects.ModifyAttributes(copyId, attributes, true), "Copied fixture material must be assigned.");
                copiedIds.Add(copyId);
                AddAssemblyUpdateFlat(doc, services, assembly, part, 450 + assembly.Parts.IndexOf(part) * 45, copyId);
                AddAssemblyUpdateRowHeader(doc, services, assembly, part);
            }
            Require(doc.Groups.Add(assembly.Name + "-copied-C01", copiedIds) >= 0, "The copied representative must be grouped.");
        }
        services.Repository.Save(doc, new AssemblyStore { Assemblies = new List<AssemblyRecord> { assembly } });
        return new ComponentAdditionFixture(assembly.Name, component.Id, templateInstanceId, assembly.Parts[0].Id, sourceToOriginal, sourceFrames);
    }

    private static Guid AddComponentFixtureAddition(RhinoDoc doc, RegressionServices services, ComponentAdditionFixture fixture,
        string scenario, bool existingPart, bool hardware)
    {
        if (scenario.EndsWith("copied-member", StringComparison.Ordinal))
        {
            var assembly = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
            var original = assembly.LinkGraph.Nodes.Single(node => node.SourceComponentInstanceId == fixture.TemplateInstanceId &&
                node.PartId == fixture.FirstPartId && node.Role == AssemblyLinkRoles.OriginalAssembly);
            using var copiedGeometry = doc.Objects.FindId(original.ObjectId).Geometry.Duplicate();
            using var copiedAttributes = doc.Objects.FindId(original.ObjectId).Attributes.Duplicate();
            copiedAttributes.RemoveFromAllGroups();
            // Model Rhino Copy: a new UUID inherits link user strings from an existing
            // managed object. Explicit regroup adoption may clear those inherited strings,
            // but must neither steal nor remap the old object identity.
            Require(copiedGeometry.Transform(Transform.Translation(25, 23, 5)), "The operator's copied member must move into place.");
            var copiedId = doc.Objects.Add(copiedGeometry, copiedAttributes);
            Require(copiedId != Guid.Empty && copiedId != original.ObjectId, "A copied member must receive a new independent UUID.");
            return copiedId;
        }
        using var attributes = new ObjectAttributes { Name = hardware ? "Test bracket" : "Operator added part" };
        if (hardware)
        {
            var identifier = scenario.EndsWith("name-collision", StringComparison.Ordinal) ? "P01" : "regression-bracket";
            HardwareMetadata.Mark(attributes, new HardwareMetadataRecord(identifier, "Test bracket", "Bracket regression fixture", "", "RegressionBracket"));
            MaterialAssignment.Set(attributes, new MaterialDefinitionRecord { Id = "STEEL", Name = "Steel" });
        }
        else
            MaterialAssignment.Set(attributes, new MaterialDefinitionRecord { Id = "BIRCH", Name = "Birch plywood" });
        var corner = new Point3d(25, 23, 5);
        var size = hardware ? new Vector3d(2, 1, 1) : existingPart ? new Vector3d(8, 3, 1) : new Vector3d(9, 4, 1);
        Guid objectId;
        if (scenario.Contains("block-hardware", StringComparison.Ordinal))
        {
            using var definitionGeometry = Brep.CreateFromBox(new BoundingBox(Point3d.Origin, Point3d.Origin + size));
            var definitionIndex = doc.InstanceDefinitions.Add("RegressionBracket-" + Guid.NewGuid().ToString("N"),
                "Disposable hardware fixture", Point3d.Origin, new GeometryBase[] { definitionGeometry }, new[] { attributes });
            Require(definitionIndex >= 0, "The fixture hardware definition must be created.");
            objectId = doc.Objects.AddInstanceObject(definitionIndex, fixture.SourceToOriginal * Transform.Translation(corner - Point3d.Origin), attributes);
        }
        else
        {
            using var geometry = Brep.CreateFromBox(new BoundingBox(corner, corner + size));
            Require(geometry.Transform(fixture.SourceToOriginal), "The operator's addition must be placed in the template original frame.");
            objectId = doc.Objects.AddBrep(geometry, attributes);
        }
        Require(objectId != Guid.Empty, "The operator's new component member must be created.");
        return objectId;
    }

    private static void AssertComponentFixtureGeometryBounds(GeometryBase expected, GeometryBase actual, string message)
    {
        var expectedBounds = expected.GetBoundingBox(true);
        var actualBounds = actual.GetBoundingBox(true);
        Require(expectedBounds.Min.DistanceTo(actualBounds.Min) < 0.001 && expectedBounds.Max.DistanceTo(actualBounds.Max) < 0.001, message);
    }

    private static int FindComponentFixtureGroup(RhinoDoc doc, Guid groupId)
    {
        for (var index = 0; index < doc.Groups.Count; index++)
        {
            var group = doc.Groups.FindIndex(index);
            if (group is { IsDeleted: false } && group.Id == groupId)
                return index;
        }
        throw new InvalidOperationException("The expected live component group must exist.");
    }

    private static List<RhinoObject> ComponentFixtureObjects(RhinoDoc doc) => doc.Objects.GetObjectList(ObjectType.AnyObject)
        .Where(obj => !obj.IsDeleted && !obj.IsInstanceDefinitionGeometry).ToList();

    private static void ExpectComponentUpdateRejection(Action action, string message)
    {
        try
        {
            action();
        }
        catch (InvalidOperationException)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }

    private sealed record ComponentAdditionFixture(string AssemblyName, Guid ComponentId, Guid TemplateInstanceId,
        Guid FirstPartId, Transform SourceToOriginal, Dictionary<Guid, Transform> SourceFrames);
}
