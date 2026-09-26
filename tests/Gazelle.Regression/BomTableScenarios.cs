using AssemblyManagerPlugin.Core;
using AssemblyManagerPlugin.Services;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Runtime.InProcess;

internal static partial class Program
{
    private static void RunBomTableScenario(RhinoCore core, RegressionServices services, string scenario, List<RhinoDoc> fixtureDocs)
    {
        var doc = RhinoDoc.Create(null);
        fixtureDocs.Add(doc);
        doc.ModelUnitSystem = UnitSystem.Inches;
        doc.PageUnitSystem = UnitSystem.Inches;
        doc.ModelAbsoluteTolerance = 0.001;
        var assembly = new AssemblyRecord { Name = scenario };
        var hasParts = scenario != "bom-table-hardware-only";
        var hasHardware = scenario != "bom-table-no-hardware";

        if (hasParts)
        {
            using var partGeometry = Brep.CreateFromBox(new BoundingBox(Point3d.Origin, new Point3d(8, 3, 1)));
            var partId = doc.Objects.AddBrep(partGeometry);
            assembly.Parts.Add(new PartRecord
            {
                Name = "P01", Quantity = 3, MaterialId = "BIRCH", MaterialThickness = 1,
                GeneratedObjectIds = new List<Guid> { partId }
            });
            // Keep the material estimate's explicit missing/unaccounted information.
            assembly.Parts.Add(new PartRecord { Name = "P02", Quantity = 5 });
        }
        if (hasHardware)
        {
            assembly.Hardware.AddRange(new[]
            {
                new HardwareRecord { Name = "Bracket", BlockDefinitionName = "Bracket block", Description = "Corner bracket", MaterialId = "ALUMINUM", Quantity = 2 },
                new HardwareRecord { Name = "Bracket", BlockDefinitionName = "Bracket block", Description = "Corner bracket", MaterialId = "ALUMINUM", Quantity = 1 },
                new HardwareRecord { Name = "Bracket", BlockDefinitionName = "Bracket block", Description = "Corner bracket", MaterialId = "STEEL", Quantity = 4 },
                new HardwareRecord { Name = "Washer", Description = "Solid washer", Quantity = 2 }
            });
        }

        services.Repository.Save(doc, new AssemblyStore { Assemblies = new List<AssemblyRecord> { assembly } });
        var history = new DocumentActionHistorySink(services.Repository);
        var estimates = new NestingEstimateService(services.Repository, services.Layers, services.Fingerprints,
            new BomTableMaterialLibrary(), history);

        // Native layout placement, not a mocked row builder. The table must belong
        // only to the selected page even when another page exists in the document.
        Console.WriteLine($"{scenario}: creating alternate page");
        Require(doc.Views.AddPageView("Other page", 17, 11) != null, "The alternate page must be created.");
        Console.WriteLine($"{scenario}: creating BOM page");
        var page = doc.Views.AddPageView("BOM page", 17, 11)
            ?? throw new InvalidOperationException("The BOM page must be created.");
        Console.WriteLine($"{scenario}: activating BOM page");
        doc.Views.ActiveView = page;
        page.SetPageAsActive();
        Require(doc.ActiveSpace == ActiveSpace.PageSpace, "The fixture must be in page space.");
        Console.WriteLine($"{scenario}: placing table");
        var count = estimates.PlaceMaterialEstimateTable(doc, assembly.Name, new Point3d(1, 9, 0));
        Console.WriteLine($"{scenario}: reading {count} table objects");
        var tableObjects = doc.Objects.GetObjectList(ObjectType.AnyObject)
            .Where(obj => obj.Attributes.Space == ActiveSpace.PageSpace).ToList();
        Require(count > 0 && tableObjects.Count == count, "Every reported table object must exist in page space.");
        Require(tableObjects.All(obj => obj.Attributes.ViewportId == page.MainViewport.Id),
            "Every line and label must be assigned to the selected page only.");
        var rows = tableObjects.Select(obj => obj.Geometry).OfType<TextEntity>()
            .GroupBy(text => Math.Round(text.Plane.Origin.Y, 6))
            .OrderByDescending(group => group.Key)
            .Select(group => group.OrderBy(text => text.Plane.Origin.X).Select(text => text.PlainText).ToArray())
            .ToList();
        Require(rows.Any(row => row.SequenceEqual(new[] { "Material & Hardware Estimate - " + assembly.Name })),
            "The placed report must make its combined material/hardware scope clear.");

        var after = services.Repository.Load(doc).FindAssembly(assembly.Name)!;
        var report = after.LastMaterialEstimate!;
        var bom = new BomService(services.Repository, history).GenerateBom(doc, assembly.Name);
        Require(report.Lines.Count == (hasParts ? 1 : 0) && bom.Lines.Count(line => line.Category == "SheetGood") == report.Lines.Count,
            "Sheet stock must remain a single estimate line rather than being counted again as individual parts.");
        Require(report.UnaccountedObjects.Count == (hasParts ? 1 : 0), "Unaccounted parts must remain in the estimate.");
        if (hasParts)
        {
            Require(report.Lines.Single().EstimatedSheetCount == 1 && report.Lines.Single().Parts.Single().Quantity == 3,
                "Hardware must not contribute to the sheet-stock estimate or manufactured-part quantities.");
            Require(rows.Any(row => row.Contains("P01 x3")) && rows.Any(row => row.Contains("P02") && row.Contains("5")),
                "The placed table must retain estimated part summaries and unaccounted-part quantities.");
            Require(rows.Any(row => row.SequenceEqual(new[] { "Unaccounted Objects" })),
                "The placed table must retain the unaccounted section.");
        }
        if (hasHardware)
        {
            Require(bom.Lines.Count(line => line.Category == "Hardware") == 3,
                "Matching hardware must aggregate across component occurrences; different materials must stay separate.");
            Require(rows.Any(row => row.SequenceEqual(new[] { "Bracket block", "Corner bracket", "Aluminum", "ea", "3", "Document" })),
                "The placed table must include matching block hardware with the same quantity/description/material as the BOM.");
            Require(rows.Any(row => row.SequenceEqual(new[] { "Bracket block", "Corner bracket", "Steel", "ea", "4", "Document" })),
                "Hardware with a different material must have its own table row.");
            Require(rows.Any(row => row.SequenceEqual(new[] { "Washer", "Solid washer", "TBD", "ea", "2", "Document" })),
                "Solid hardware and its unassigned material must be visible as hardware, not as sheet stock.");
            Require(rows.Count(row => row.SequenceEqual(new[] { "Hardware" })) == 1,
                "The placed table must have exactly one hardware section.");
        }
        else
        {
            Require(!rows.Any(row => row.SequenceEqual(new[] { "Hardware" })) && bom.Lines.All(line => line.Category != "Hardware"),
                "Assemblies without hardware must not get fabricated hardware rows.");
        }
    }

    private sealed class BomTableMaterialLibrary : IMaterialLibrary
    {
        private readonly MaterialRecord _sheet = new()
        {
            Id = "BIRCH-SHEET", Name = "Birch sheet", BaseMaterialId = "BIRCH", BaseMaterialName = "Birch plywood",
            ShapeType = "Sheet", ShapeName = "48 x 96", Thickness = 1, SheetWidth = 48, SheetHeight = 96,
            Width = 48, Height = 96, NestingEfficiency = 0.8
        };

        public IReadOnlyList<MaterialRecord> GetMaterials(RhinoDoc? doc = null) => new[] { _sheet };
        public MaterialRecord? FindById(RhinoDoc? doc, string materialId) => materialId == _sheet.Id ? _sheet : null;
        public MaterialRecord? ResolveForEstimate(RhinoDoc? doc, string materialId, double requiredWidth, double requiredHeight, double requiredThickness) => _sheet;
        public string GetMaterialLabel(RhinoDoc? doc, string materialId) => materialId switch
        {
            "BIRCH" => "Birch plywood",
            "ALUMINUM" => "Aluminum",
            "STEEL" => "Steel",
            _ => materialId
        };
    }
}
