using AssemblyManagerPlugin.Core;
using AssemblyManagerPlugin.Services;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Runtime.InProcess;

internal static partial class Program
{
    private static void RunFlatDependencyScenario(RhinoCore core, RegressionServices services, string scenario, List<RhinoDoc> fixtureDocs)
    {
        var doc = RhinoDoc.Create(null);
        fixtureDocs.Add(doc);
        doc.ModelUnitSystem = UnitSystem.Inches;
        doc.ModelAbsoluteTolerance = 0.001;
        services.AutomaticallyPropagate = true;
        var assembly = CreateAssemblyUpdateFixture(doc, services, scenario, true, false, true, false);
        var initial = services.Repository.Load(doc);
        assembly = initial.FindAssembly(assembly.Name)!;
        var part = assembly.Parts.Single(part => part.Name == "P01");
        var editedSource = assembly.LinkGraph.Nodes.Single(node => node.PartId == part.Id && node.Role == AssemblyLinkRoles.Source);
        var flatId = part.CamObjectIds.Single();
        var otherFlatId = assembly.Parts.Single(part => part.Name == "P02").CamObjectIds.Single();
        var noteId = AddUpdateFixtureText(doc, services, assembly, part, "Operator note: this flat supplies another assembly", false);
        Guid childId;
        Guid dependentAssemblyId;
        using (AssemblyLinkMutationGate.Enter())
        {
            var dependent = new AssemblyRecord { Name = "Dependent-" + scenario, NextPartSequence = 2 };
            dependentAssemblyId = dependent.Id;
            var dependentPart = new PartRecord { Name = "P01", Quantity = 1, MaterialThickness = 1 };
            dependent.Parts.Add(dependentPart);
            using var downstreamGeometry = doc.Objects.FindId(flatId).Geometry.Duplicate();
            var downstreamTransform = Transform.Translation(0, 100, 0);
            Require(downstreamGeometry.Transform(downstreamTransform), "The dependency fixture must create a translated downstream original.");
            using var attributes = new ObjectAttributes
            {
                LayerIndex = services.Layers.EnsureLayerIndex(doc, LayerService.OriginalPart(dependent.Name, "unsorted", dependentPart.Name)),
                Name = dependentPart.Name
            };
            childId = doc.Objects.Add(downstreamGeometry, attributes);
            Require(childId != Guid.Empty, "The downstream dependent object must be created.");
            var registration = services.Lineage.RegisterDerived(doc, dependent, flatId, AssemblyLinkRoles.Source,
                childId, AssemblyLinkRoles.OriginalAssembly, downstreamTransform, partId: dependentPart.Id);
            Require(services.Fingerprints.TryCreatePartCandidate(doc.Objects.FindId(flatId), out var sourceCandidate),
                "The flat source must have a valid dependency fingerprint.");
            registration.Parent.GeometryFingerprint = sourceCandidate.Fingerprint;
            registration.Child.GeometryFingerprint = sourceCandidate.Fingerprint;
            dependentPart.GeometryFingerprint = sourceCandidate.Fingerprint;
            sourceCandidate.Geometry.Dispose();
            dependentPart.SourceObjectIds.Add(flatId);
            dependentPart.GeneratedObjectIds.Add(childId);
            if (scenario.EndsWith("inactive", StringComparison.Ordinal))
            {
                registration.Parent.Status = AssemblyLinkStatuses.Conflict;
                registration.Edge.Status = AssemblyLinkStatuses.Conflict;
            }
            initial.Assemblies.Add(dependent);
            services.Repository.Save(doc, initial);
        }
        PumpIdle(core, services);
        ReplaceUpdateFixtureBox(doc, editedSource.ObjectId, 12);
        PumpIdle(core, services);

        var afterStore = services.Repository.Load(doc);
        var after = afterStore.FindAssembly(assembly.Name)!;
        Require(after.Parts.Count == 1 && after.Parts.Single().Name == "P02" && after.Parts.Single().Quantity == 2,
            "The source categories should merge while preserving their two occurrences.");
        Require(doc.Objects.FindId(flatId) is { IsDeleted: false } && doc.Objects.FindId(otherFlatId) is { IsDeleted: false },
            "A superseded flat with an active or inactive downstream dependency must not be deleted.");
        Require(after.Parts.Single().CamObjectIds.Contains(flatId) && after.Parts.Single().CamObjectIds.Contains(otherFlatId),
            "Both the current representative and dependent retained flat must remain discoverable in the part record.");
        var node = after.LinkGraph.Nodes.Single(node => node.ObjectId == flatId);
        Require(after.LinkGraph.Conflicts.Any(conflict => conflict.NodeId == node.Id && conflict.Status == AssemblyLinkStatuses.Open &&
                    conflict.Message.Contains("still used by another linked object", StringComparison.Ordinal)),
            "Preserving a dependent superseded output must explain the remaining duplicate as a visible link issue.");
        Require(doc.Objects.FindId(childId) is { IsDeleted: false } && afterStore.Assemblies.Single(candidate => candidate.Id == dependentAssemblyId)
                .LinkGraph.Nodes.Any(candidate => candidate.ObjectId == flatId),
            "Downstream geometry and the source reference to the retained flat UUID must survive the merge.");
        Require(doc.Objects.FindId(noteId)?.Geometry is TextEntity text && text.PlainText == "Operator note: this flat supplies another assembly",
            "User annotations on a retired part's text layer must remain untouched.");
        var labels = services.Layers.GetObjectIdsInLayerTree(doc, LayerService.PartsAssembly(after.Name))
            .Select(doc.Objects.FindId).Where(obj => obj?.Geometry is TextEntity &&
                obj.Attributes.GetUserString(FlatPartAnnotations.OutputKey) == flatId.ToString("D")).ToList();
        Require(labels.Count == 1 && ((TextEntity)labels.Single()!.Geometry).PlainText.Replace("\r", string.Empty).StartsWith("P02\nQTY : 2\n", StringComparison.Ordinal),
            "The retained dependent flat must still receive its merged category name and current quantity label.");
    }
}
