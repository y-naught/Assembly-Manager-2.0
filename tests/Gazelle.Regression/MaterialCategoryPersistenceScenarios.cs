using System.Text.Json;
using System.Text.Json.Nodes;
using AssemblyManagerPlugin.Core;
using AssemblyManagerPlugin.Services;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Runtime.InProcess;

internal static partial class Program
{
    private static void RunMaterialCategoryPersistenceScenario(RhinoCore core, RegressionServices services,
        string scenario, List<RhinoDoc> fixtureDocs)
    {
        if (scenario == "material-category-persistence-empty-standalone")
        {
            var knownEmpty = new PartRecord
            {
                Name = "P01", MaterialId = "birch-stock", CategorizationMaterialId = string.Empty
            };
            var missingIdentity = JsonSerializer.Deserialize<PartRecord>(
                "{\"Name\":\"P02\",\"MaterialId\":\"birch-stock\"}")!;
            var store = new AssemblyStore
            {
                Assemblies = new List<AssemblyRecord>
                {
                    new() { Name = "Regression-" + scenario, Parts = new List<PartRecord> { knownEmpty, missingIdentity } }
                },
                MaterialLibraryCache = new List<MaterialRecord>
                {
                    new() { Id = "birch-stock", BaseMaterialId = "birch" }
                }
            };
            AssemblyRepository.Normalize(store);
            Require(knownEmpty.CategorizationMaterialId == string.Empty,
                "Standalone normalization must preserve a known empty category instead of substituting a selected stock material.");
            Require(missingIdentity.CategorizationMaterialId == "BIRCH",
                "Standalone normalization must still initialize genuinely missing legacy identity from the cached stock parent.");
            knownEmpty.MaterialId = "aluminum-stock";
            missingIdentity.MaterialId = "aluminum-stock";
            AssemblyRepository.Normalize(store);
            Require(knownEmpty.CategorizationMaterialId == string.Empty && missingIdentity.CategorizationMaterialId == "BIRCH",
                "Neither known empty nor initialized legacy identity may follow later purchasing-material edits.");
            var roundTrip = JsonSerializer.Deserialize<AssemblyStore>(JsonSerializer.Serialize(store))!;
            AssemblyRepository.Normalize(roundTrip);
            Require(roundTrip.Assemblies[0].Parts[0].CategorizationMaterialId == string.Empty,
                "An explicit empty accepted identity must remain distinguishable from missing metadata after serialization.");
            return;
        }

        Require(scenario is "material-category-persistence-live-source" or "material-category-persistence-part-stock" or
            "material-category-persistence-legacy-missing" or "material-category-persistence-legacy-null",
            "The material-category persistence scenario must be registered explicitly.");

        var doc = RhinoDoc.Create(null);
        fixtureDocs.Add(doc);
        doc.ModelUnitSystem = UnitSystem.Inches;
        doc.ModelAbsoluteTolerance = 0.001;
        using var mutation = AssemblyLinkMutationGate.Enter();
        using var geometry = new Box(Plane.WorldXY, new Interval(0, 10), new Interval(0, 4), new Interval(0, 1)).ToBrep();
        var attributes = new ObjectAttributes();
        if (scenario == "material-category-persistence-legacy-null")
            MaterialAssignment.Set(attributes, new MaterialDefinitionRecord { Id = "birch", Name = "Birch plywood" });
        var sourceId = doc.Objects.AddBrep(geometry, attributes);
        Require(sourceId != Guid.Empty, "The isolated material persistence fixture must have a live source object.");
        var part = new PartRecord
        {
            Name = "P01", Quantity = 1, CategorizationMaterialId = string.Empty,
            SourceObjectIds = new List<Guid> { sourceId }
        };
        var assembly = new AssemblyRecord
        {
            Name = "Regression-" + scenario, Parts = new List<PartRecord> { part }
        };
        var initial = new AssemblyStore { Assemblies = new List<AssemblyRecord> { assembly } };

        if (scenario is "material-category-persistence-legacy-missing" or "material-category-persistence-legacy-null")
        {
            // Write the historical JSON directly: a normal repository save would already
            // initialize missing legacy metadata before this migration test can read it.
            var json = JsonNode.Parse(JsonSerializer.Serialize(initial))!.AsObject();
            var jsonPart = json["Assemblies"]![0]!["Parts"]![0]!.AsObject();
            if (scenario.EndsWith("missing", StringComparison.Ordinal))
                jsonPart.Remove("CategorizationMaterialId");
            else
                jsonPart["CategorizationMaterialId"] = null;
            doc.Strings.SetString(AssemblyManagerConstants.StoreSection, AssemblyManagerConstants.StoreEntry,
                json.ToJsonString());
            var legacy = services.Repository.Load(doc);
            var expectedIdentity = scenario.EndsWith("missing", StringComparison.Ordinal) ? string.Empty : "BIRCH";
            Require(legacy.Assemblies[0].Parts[0].CategorizationMaterialId == expectedIdentity,
                "Missing/null legacy identity must initialize from the live source, including an intentionally unassigned source.");
            services.Repository.Save(doc, legacy);
            var revisedAttributes = doc.Objects.FindId(sourceId).Attributes.Duplicate();
            MaterialAssignment.Set(revisedAttributes, new MaterialDefinitionRecord { Id = "aluminum", Name = "Aluminum" });
            Require(doc.Objects.ModifyAttributes(sourceId, revisedAttributes, true),
                "The material edit after legacy initialization must succeed.");
            legacy.Assemblies[0].Parts[0].MaterialId = "ALUMINUM";
            services.Repository.Save(doc, legacy);
            var reloaded = services.Repository.Load(doc);
            Require(reloaded.Assemblies[0].Parts[0].CategorizationMaterialId == expectedIdentity,
                "Legacy identity initialization must happen once, not replace the accepted identity on subsequent saves or loads.");
            return;
        }

        services.Repository.Save(doc, initial);
        var accepted = services.Repository.Load(doc);
        Require(accepted.Assemblies[0].Parts[0].CategorizationMaterialId == string.Empty,
            "The saved fixture must begin with an explicit accepted TBD category.");
        var editedAttributes = doc.Objects.FindId(sourceId).Attributes.Duplicate();
        if (scenario == "material-category-persistence-part-stock")
        {
            var stock = new MaterialRecord
            {
                Id = "birch-stock", Name = "Birch 4 x 8", BaseMaterialId = "birch", BaseMaterialName = "Birch plywood"
            };
            accepted.MaterialLibraryCache.Add(stock);
            // Match AssignMaterialToPart's pre-reconciliation record update, while
            // retaining the previously accepted category identity for the later pass.
            accepted.Assemblies[0].Parts[0].MaterialId = stock.Id;
            MaterialAssignment.Set(editedAttributes, stock);
        }
        else
        {
            MaterialAssignment.Set(editedAttributes, new MaterialDefinitionRecord { Id = "birch", Name = "Birch plywood" });
        }
        Require(doc.Objects.ModifyAttributes(sourceId, editedAttributes, true),
            "The source's new material must be applied before the repository reads pending categorization.");

        var afterSourceEdit = services.Repository.Load(doc);
        Require(afterSourceEdit.Assemblies[0].Parts[0].CategorizationMaterialId == string.Empty,
            "Loading after a live material edit must not silently replace the accepted TBD category with the new source material.");
        services.Repository.Save(doc, accepted);
        var afterSave = services.Repository.Load(doc);
        Require(afterSave.Assemblies[0].Parts[0].CategorizationMaterialId == string.Empty,
            "Saving pending source/stock material edits must preserve accepted TBD evidence for the categorization pass.");
        Require(MaterialAssignment.GetCategorizationMaterialId(doc.Objects.FindId(sourceId).Attributes) == "BIRCH",
            "Preserving accepted category evidence must not undo or rewrite the user's live material assignment.");
        if (scenario == "material-category-persistence-part-stock")
            Require(afterSave.Assemblies[0].Parts[0].MaterialId == "birch-stock",
                "Preserving category evidence must also retain the user's separate stock-shape selection.");
    }
}
