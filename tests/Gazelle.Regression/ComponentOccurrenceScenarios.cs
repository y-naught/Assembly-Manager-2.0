using AssemblyManagerPlugin.Core;
using AssemblyManagerPlugin.Services;
using Rhino;
using Rhino.Geometry;
using Rhino.Runtime.InProcess;

internal static partial class Program
{
    private static void RunComponentOccurrenceScenario(RhinoCore core, RegressionServices services, string scenario, List<RhinoDoc> fixtureDocs)
    {
        var doc = RhinoDoc.Create(null);
        fixtureDocs.Add(doc);
        doc.ModelUnitSystem = UnitSystem.Inches;
        doc.ModelAbsoluteTolerance = 0.001;
        services.AutomaticallyPropagate = true;
        var fixture = CreateComponentAdditionFixture(doc, services, scenario, false);
        PumpIdle(core, services);
        var before = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
        var sibling = before.LinkGraph.SourceComponentInstances.Single(instance => instance.Id != fixture.TemplateInstanceId);
        var firstAdditionId = RegroupComponentOccurrence(core, doc, services, fixture, fixture.TemplateInstanceId, scenario);

        if (scenario.EndsWith("legacy-cohort", StringComparison.Ordinal))
        {
            // Simulate a model saved by the previous all-occurrence implementation. Its
            // extra cohort entries must not authorize additions to the unchanged sibling.
            using (AssemblyLinkMutationGate.Enter())
            {
                var store = services.Repository.Load(doc);
                var assembly = store.FindAssembly(fixture.AssemblyName)!;
                var pending = assembly.PendingComponentUpdates.Single();
                pending.InstanceIds = assembly.LinkGraph.SourceComponentInstances.Select(instance => instance.Id).ToList();
                pending.MemberNodeIdsByInstance = assembly.LinkGraph.SourceComponentInstances.ToDictionary(
                    instance => instance.Id, instance => instance.SourceNodeIds.ToList());
                services.Repository.Save(doc, store);
            }
            services.LinkEvents.UpdateAssembly(doc, fixture.AssemblyName);
            PumpIdle(core, services);
            var after = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
            var oldComponent = after.Components.Single(component => component.Id == fixture.ComponentId);
            var edited = after.LinkGraph.SourceComponentInstances.Single(instance => instance.Id == fixture.TemplateInstanceId);
            var unchanged = after.LinkGraph.SourceComponentInstances.Single(instance => instance.Id == sibling.Id);
            Require(after.PendingComponentUpdates.Count == 0 && after.Components.Count == 2 && oldComponent.Name == "C01" && oldComponent.Quantity == 1,
                "A legacy cohort plan must consume once and preserve C01 for the unchanged occurrence.");
            Require(edited.ComponentId != fixture.ComponentId && edited.SourceNodeIds.Count == 4 &&
                    unchanged.ComponentId == fixture.ComponentId && unchanged.SourceNodeIds.SequenceEqual(sibling.SourceNodeIds) &&
                    unchanged.GeneratedGroupId == sibling.GeneratedGroupId && unchanged.SourceGroupId == sibling.SourceGroupId,
                "Only the legacy plan's saved template may gain membership or change component category/group identity.");
            var previousNodeIds = before.LinkGraph.Nodes.Select(node => node.Id).ToHashSet();
            var addedSourceNodes = after.LinkGraph.Nodes.Where(node => !previousNodeIds.Contains(node.Id) && node.Role == AssemblyLinkRoles.Source).ToList();
            var addedOriginals = after.LinkGraph.Nodes.Where(node => !previousNodeIds.Contains(node.Id) && node.Role == AssemblyLinkRoles.OriginalAssembly).ToList();
            Require(addedSourceNodes.Count == 1 && addedSourceNodes.Single().SourceComponentInstanceId == fixture.TemplateInstanceId &&
                    addedOriginals.Count == 1 && addedOriginals.Single().ObjectId == firstAdditionId,
                "An old all-occurrence plan must create one input and adopt one selected original, never a sibling duplicate.");
            Require(after.Parts.Sum(part => part.Quantity) == 7 &&
                    after.Parts.Single(part => part.Id == addedSourceNodes.Single().PartId).Quantity == 1,
                "Legacy narrowing must count the addition once, not once per saved cohort entry.");
            AssertComponentOccurrenceAdditionPlacement(doc, fixture, after, firstAdditionId, fixture.TemplateInstanceId);
            AssertSynchronizedFlatOutputs(doc, services, after);
            return;
        }

        if (scenario.EndsWith("independent-occurrences", StringComparison.Ordinal))
        {
            var secondAdditionId = RegroupComponentOccurrence(core, doc, services, fixture, sibling.Id, scenario);
            var staged = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
            Require(staged.PendingComponentUpdates.Count == 2 &&
                    staged.PendingComponentUpdates.Select(pending => pending.TemplateInstanceId).ToHashSet()
                        .SetEquals(new[] { fixture.TemplateInstanceId, sibling.Id }),
                "Two separately regrouped occurrences of C01 must keep independent staged plans.");
            var firstPending = staged.PendingComponentUpdates.Single(pending => pending.TemplateInstanceId == fixture.TemplateInstanceId);
            var originalGroupBeforeRestage = firstPending.PreviousGeneratedGroupId;
            services.LinkEvents.StageComponentUpdate(doc, fixture.AssemblyName, fixture.ComponentId, firstPending.RegroupedGroupId);
            var restaged = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
            Require(restaged.PendingComponentUpdates.Count == 2 && restaged.PendingComponentUpdates.All(pending =>
                        pending.InstanceIds.SequenceEqual(new[] { pending.TemplateInstanceId }) &&
                        pending.MemberNodeIdsByInstance.Count == 1 && pending.MemberNodeIdsByInstance.ContainsKey(pending.TemplateInstanceId)),
                "Restaging one C01 occurrence must replace only its plan and keep both donor-only membership proofs.");
            Require(restaged.PendingComponentUpdates.Single(pending => pending.TemplateInstanceId == fixture.TemplateInstanceId)
                        .PreviousGeneratedGroupId == originalGroupBeforeRestage &&
                    restaged.PendingComponentUpdates.Single(pending => pending.TemplateInstanceId == sibling.Id)
                        .AddedObjectIds.SequenceEqual(new[] { secondAdditionId }),
                "Restaging must preserve the first occurrence's original rebind history and the sibling's independent addition.");
            PumpIdle(core, services);
            Require(services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!.LinkGraph.Nodes.Count == before.LinkGraph.Nodes.Count,
                "Automatic idle processing must leave both independent structural additions staged.");
            services.LinkEvents.UpdateAssembly(doc, fixture.AssemblyName);
            PumpIdle(core, services);
            var after = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
            Require(after.PendingComponentUpdates.Count == 0 && after.Components.Count == 1 && after.Components.Single().Quantity == 2 &&
                    after.LinkGraph.SourceComponentInstances.All(instance => instance.SourceNodeIds.Count == 4),
                "Two independently staged equivalent designs must each apply once and then form one component category of quantity two.");
            Require(after.Parts.Count == 4 && after.Parts.Sum(part => part.Quantity) == 8,
                "Independent plans sharing a new part design must allocate one part category with exactly two added members.");
            var firstSource = AssertComponentOccurrenceAdditionPlacement(doc, fixture, after, firstAdditionId, fixture.TemplateInstanceId);
            var secondSource = AssertComponentOccurrenceAdditionPlacement(doc, fixture, after, secondAdditionId, sibling.Id);
            Require(firstSource.PartId == secondSource.PartId && after.Parts.Single(part => part.Id == firstSource.PartId).Quantity == 2,
                "Both independent additions must share the matching new part category without duplicating either occurrence.");
            AssertSynchronizedFlatOutputs(doc, services, after);
            return;
        }

        Require(scenario.EndsWith("existing-component", StringComparison.Ordinal), "The occurrence regression scenario must be recognized.");
        services.LinkEvents.UpdateAssembly(doc, fixture.AssemblyName);
        PumpIdle(core, services);
        var split = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
        var editedComponentId = split.LinkGraph.SourceComponentInstances.Single(instance => instance.Id == fixture.TemplateInstanceId).ComponentId;
        Require(split.Components.Count == 2 && editedComponentId != fixture.ComponentId &&
                split.Components.Single(component => component.Id == editedComponentId).Name == "C02" &&
                split.Components.Single(component => component.Id == fixture.ComponentId).Quantity == 1,
            "The first selected occurrence must split into C02 while the unchanged occurrence remains C01.");
        var matchingAdditionId = RegroupComponentOccurrence(core, doc, services, fixture, sibling.Id, scenario);
        services.LinkEvents.UpdateAssembly(doc, fixture.AssemblyName);
        PumpIdle(core, services);
        var merged = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
        Require(merged.PendingComponentUpdates.Count == 0 && merged.Components.Count == 1 &&
                merged.Components.Single().Id == editedComponentId && merged.Components.Single().Name == "C02" &&
                merged.Components.Single().Quantity == 2 &&
                merged.LinkGraph.SourceComponentInstances.All(instance => instance.ComponentId == editedComponentId && instance.SourceNodeIds.Count == 4),
            "A later selected occurrence matching the existing C02 design must reuse C02 and retire the now-empty C01 definition.");
        var existingSource = AssertComponentOccurrenceAdditionPlacement(doc, fixture, merged, firstAdditionId, fixture.TemplateInstanceId);
        var matchingSource = AssertComponentOccurrenceAdditionPlacement(doc, fixture, merged, matchingAdditionId, sibling.Id);
        Require(existingSource.PartId == matchingSource.PartId && merged.Parts.Count == 4 &&
                merged.Parts.Single(part => part.Id == matchingSource.PartId).Quantity == 2,
            "A component merge must reuse the existing added-part category and count one addition from each occurrence.");
        AssertSynchronizedFlatOutputs(doc, services, merged);
    }

    private static Guid RegroupComponentOccurrence(RhinoCore core, RhinoDoc doc, RegressionServices services,
        ComponentAdditionFixture fixture, Guid instanceId, string scenario)
    {
        var assembly = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
        var instance = assembly.LinkGraph.SourceComponentInstances.Single(item => item.Id == instanceId);
        var members = assembly.LinkGraph.Nodes.Where(node => node.SourceComponentInstanceId == instanceId &&
                node.Role is AssemblyLinkRoles.OriginalAssembly or AssemblyLinkRoles.Hardware)
            .Select(node => node.ObjectId).ToList();
        Require(doc.Groups.Delete(FindComponentFixtureGroup(doc, instance.GeneratedGroupId)),
            "The selected occurrence must ungroup without changing its retained object identities.");
        var addedId = AddComponentFixtureAddition(doc, services, fixture, scenario, false, false);
        if (instanceId != fixture.TemplateInstanceId)
        {
            Require(fixture.SourceToOriginal.TryGetInverse(out var originalToSource),
                "The fixture input-to-original frame must be invertible.");
            Require(fixture.SourceFrames[fixture.TemplateInstanceId].TryGetInverse(out var templateToBase),
                "The template source frame must be invertible.");
            var placement = fixture.SourceToOriginal * fixture.SourceFrames[instanceId] * templateToBase * originalToSource;
            using var geometry = (Brep)doc.Objects.FindId(addedId).Geometry.Duplicate();
            Require(geometry.Transform(placement) && doc.Objects.Replace(addedId, geometry),
                "The independently added item must move from the template into this occurrence's rotated original frame.");
        }
        members.Add(addedId);
        var index = doc.Groups.Add(fixture.AssemblyName + "-regrouped-" + instanceId.ToString("N"), members);
        Require(index >= 0, "The selected occurrence's replacement group must be created.");
        PumpIdle(core, services);
        services.LinkEvents.StageComponentUpdate(doc, fixture.AssemblyName, instance.ComponentId, doc.Groups.FindIndex(index).Id);
        return addedId;
    }

    private static AssemblyLinkNodeRecord AssertComponentOccurrenceAdditionPlacement(RhinoDoc doc,
        ComponentAdditionFixture fixture, AssemblyRecord assembly, Guid originalId, Guid instanceId)
    {
        var original = assembly.LinkGraph.Nodes.Single(node => node.ObjectId == originalId && node.Role == AssemblyLinkRoles.OriginalAssembly);
        var edge = assembly.LinkGraph.Edges.Single(item => item.ChildNodeId == original.Id);
        var source = assembly.LinkGraph.Nodes.Single(node => node.Id == edge.ParentNodeId);
        Require(original.SourceComponentInstanceId == instanceId && source.SourceComponentInstanceId == instanceId &&
                assembly.LinkGraph.SourceComponentInstances.Single(instance => instance.Id == instanceId).SourceNodeIds.Contains(source.Id),
            "Each adopted original and new input must belong only to its selected occurrence.");
        Require(fixture.SourceToOriginal.TryGetInverse(out var originalToSource), "The stored source placement must be invertible.");
        using var expectedSource = doc.Objects.FindId(originalId).Geometry.Duplicate();
        Require(expectedSource.Transform(originalToSource), "The addition must map back to its own input frame.");
        AssertComponentFixtureGeometryBounds(expectedSource, doc.Objects.FindId(source.ObjectId).Geometry,
            "The added input must use the selected occurrence's stored source-to-original transform without a sibling placement inference.");
        return source;
    }
}
