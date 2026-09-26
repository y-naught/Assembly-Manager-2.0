using AssemblyManagerPlugin.Core;
using AssemblyManagerPlugin.Services;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Runtime.InProcess;

internal static partial class Program
{
    private static void RunPlacedBomScenario(RhinoCore core, RegressionServices services, string scenario, List<RhinoDoc> fixtureDocs)
    {
        if (scenario == "bom-fitted-page")
        {
            RunFittedBomPageScenario(services, fixtureDocs);
            return;
        }
        var report = new MaterialEstimateReportRecord
        {
            AssemblyName = "BOM test",
            Lines = new List<MaterialEstimateLineRecord>
            {
                new()
                {
                    MaterialId = "BIRCH-SHEET", BaseMaterialName = "Birch plywood", ShapeName = "48 x 96",
                    ShapeType = "Sheet", Thickness = 0.75, SheetWidth = 48, SheetHeight = 96,
                    EstimatedSheetCount = 2, Unit = "in", TotalPartArea = 1024,
                    Parts = new List<MaterialEstimatePartRecord> { new() { PartName = "P01", Quantity = 9 } }
                }
            },
            UnaccountedObjects = new List<MaterialEstimateUnaccountedRecord>
            {
                new() { PartName = "P02", Quantity = 3, MaterialId = "STEEL", RequiredWidth = 10, RequiredHeight = 5,
                    RequiredThickness = 0.25, Reason = "No sheet stock is available for this part." }
            }
        };
        var hardware = new List<BomLineRecord>
        {
            new() { Category = "Hardware", Item = "Bracket block", Description = "Corner bracket", MaterialId = "ALUMINUM", Quantity = 4, Unit = "ea", Source = "Document" }
        };
        var allColumns = PlacedBomService.Columns.Select(column => column.Id).ToArray();
        var defaultColumns = PlacedBomService.Columns.Where(column => column.DefaultSelected).Select(column => column.Id).ToArray();
        string Label(string id) => id switch { "STEEL" => "Steel", "ALUMINUM" => "Aluminum", _ => id };
        PlacedBomLayout Build(double width, double height, IEnumerable<string>? selected = null, UnitSystem units = UnitSystem.Inches) =>
            PlacedBomService.BuildLayout(report, hardware, Label, selected ?? defaultColumns, width, height, units);

        if (scenario == "placed-bom-columns")
        {
            var layout = Build(8, 8, new[] { "source", "quantity", "item", "material", "quantity" });
            Require(layout.Columns.Select(column => column.Id).SequenceEqual(new[] { "item", "material", "quantity", "source" }),
                "The checklist must select stable unique columns in the declared order.");
            var data = layout.Rows.Where(row => !row.IsSection).Select(row => row.Cells.Select(cell => cell.OriginalText).ToArray()).ToList();
            Require(data.Any(row => row.SequenceEqual(new[] { "48 x 96", "Birch plywood", "2", "Material estimate" })),
                "Stock quantities must remain estimated sheet counts, not manufactured part counts.");
            Require(data.Any(row => row.SequenceEqual(new[] { "Bracket block", "Aluminum", "4", "Document" })),
                "Hardware must preserve its own material, quantity, and source under the same column meanings.");
            Require(data.Any(row => row.SequenceEqual(new[] { "P02", "Steel", "3", "" })),
                "Unaccounted objects must remain represented, including their quantity.");
            Require(layout.Rows.Count(row => row.IsSection) == 4, "Title and three BOM sections must be present.");
            AssertPlacedBomBounds(layout, 8, 8);
        }
        else if (scenario == "placed-bom-wrap-first")
        {
            hardware[0].Description = "A lengthy {bracket} description with literal \\b text that should wrap onto several separate lines before the standard text height is reduced.";
            hardware[0].Source = "C:\\Hardware\\VeryLongUnbrokenSupplierReference0123456789012345678901234567890123456789.3dm";
            report.UnaccountedObjects[0].Reason = "First explicit line\nSecond explicit line\n\nFourth line after a blank.";
            var layout = Build(5, 20, allColumns);
            Require(!layout.WasReduced && Math.Abs(layout.TextHeight - 0.125) < 1e-9,
                "A tall rectangle must keep standard paper text height and wrap first.");
            var cell = layout.Rows.SelectMany(row => row.Cells).Single(cell => cell.OriginalText == hardware[0].Source);
            Require(cell.Lines.Count > 1 && string.Concat(cell.Lines) == hardware[0].Source,
                "Long unbroken paths must wrap without truncation or lost characters.");
            var notes = layout.Rows.SelectMany(row => row.Cells).Single(cell => cell.OriginalText == report.UnaccountedObjects[0].Reason);
            Require(notes.Lines.Contains(string.Empty), "Explicit blank lines must survive wrapping.");
            AssertPlacedBomBounds(layout, 5, 20);
        }
        else if (scenario == "placed-bom-shrink")
        {
            hardware[0].Description = "A longer description of a standard corner bracket with assembly instructions.";
            var tall = Build(6, 15, allColumns);
            var available = tall.Height * 0.55;
            var fit = Build(6, available, allColumns);
            Require(fit.WasReduced && fit.TextHeight >= PlacedBomService.MinimumTextHeightInches && fit.TextHeight < tall.TextHeight,
                "The font must shrink only when the wrapped standard-height table exceeds the available height.");
            Require(fit.Height <= available && fit.Width == 6,
                "A reduced table must retain exactly the requested width and fit the available height.");
            AssertPlacedBomBounds(fit, 6, available);
        }
        else if (scenario == "placed-bom-paper-units")
        {
            var inches = Build(8, 10, allColumns);
            var millimeters = Build(8 * 25.4, 10 * 25.4, allColumns, UnitSystem.Millimeters);
            Require(Math.Abs(millimeters.StandardTextHeight - 3.175) < 1e-9,
                "The standard font must be one eighth of an inch in page units, not model units.");
            Require(Math.Abs(millimeters.Height - inches.Height * 25.4) < 1e-6,
                "Changing page units must preserve the physical size of the BOM.");
            Require(millimeters.Rows.SelectMany(row => row.Cells).SelectMany(cell => cell.Lines)
                .SequenceEqual(inches.Rows.SelectMany(row => row.Cells).SelectMany(cell => cell.Lines)),
                "Equivalent physical rectangles must use the same wraps in inches and millimeters.");
            AssertPlacedBomBounds(millimeters, 8 * 25.4, 10 * 25.4);
        }
        else if (scenario == "placed-bom-validation")
        {
            void Reject(Action action)
            {
                var rejected = false;
                try { action(); }
                catch (ArgumentException) { rejected = true; }
                catch (InvalidOperationException) { rejected = true; }
                Require(rejected, "An invalid BOM layout must reject without placing partial output.");
            }
            Reject(() => Build(8, 10, Array.Empty<string>()));
            Reject(() => Build(8, 10, new[] { "missing-column" }));
            Reject(() => Build(0, 10));
            Reject(() => Build(double.NaN, 10));
            Reject(() => Build(0.03, 0.03));
            Reject(() => Build(8, 10, units: UnitSystem.None));

            var doc = RhinoDoc.Create(null);
            fixtureDocs.Add(doc);
            var history = new DocumentActionHistorySink(services.Repository);
            var materials = new BomTableMaterialLibrary();
            var estimates = new NestingEstimateService(services.Repository, services.Layers, services.Fingerprints, materials, history);
            var placed = new PlacedBomService(services.Repository, services.Layers, estimates, materials, history);
            var before = doc.Objects.Count;
            Reject(() => placed.PlaceBom(doc, "Absent assembly", Point3d.Origin, new Point3d(10, 10, 0), defaultColumns));
            Require(doc.Objects.Count == before && services.Repository.Load(doc).Assemblies.Count == 0,
                "Model-space guard must run before estimating or changing document contents.");
        }
        else if (scenario == "placed-bom-empty-and-single-column")
        {
            report.Lines.Clear();
            report.UnaccountedObjects.Clear();
            hardware.Clear();
            var empty = Build(4, 5, new[] { "quantity" });
            Require(empty.Rows.Count == 2 && empty.Rows.All(row => row.IsSection),
                "An empty BOM must explicitly report that there are no recorded items.");
            AssertPlacedBomBounds(empty, 4, 5);
            hardware.Add(new BomLineRecord { Item = "特別な金具😀", Description = "Brácket", Quantity = 2, Unit = "ea" });
            var single = Build(2, 5, new[] { "item" });
            Require(single.Columns.Count == 1 && single.Rows.SelectMany(row => row.Cells).Any(cell => cell.OriginalText == hardware[0].Item),
                "A single selected column and Unicode descriptions must remain supported.");
            AssertPlacedBomBounds(single, 2, 5);
        }
        else
            throw new InvalidOperationException("Unknown placed BOM scenario: " + scenario);
    }

    private static void RunFittedBomPageScenario(RegressionServices services, List<RhinoDoc> fixtureDocs)
    {
        var doc = RhinoDoc.Create(null);
        fixtureDocs.Add(doc);
        doc.ModelUnitSystem = UnitSystem.Inches;
        doc.PageUnitSystem = UnitSystem.Millimeters;
        var assembly = new AssemblyRecord
        {
            Name = "Fitted table",
            Hardware = new List<HardwareRecord>
            {
                new() { Name = "Bracket", BlockDefinitionName = "Bracket", Description = "Long hardware description to wrap within a page-space BOM cell.", Quantity = 7, MaterialId = "ALUMINUM" }
            }
        };
        services.Repository.Save(doc, new AssemblyStore { Assemblies = new List<AssemblyRecord> { assembly } });
        var history = new DocumentActionHistorySink(services.Repository);
        var materials = new BomTableMaterialLibrary();
        var estimates = new NestingEstimateService(services.Repository, services.Layers, services.Fingerprints, materials, history);
        var placed = new PlacedBomService(services.Repository, services.Layers, estimates, materials, history);
        Console.WriteLine("bom-fitted-page: creating alternate page");
        Require(doc.Views.AddPageView("Other layout", 431.8, 279.4) is not null, "An alternate layout must be created.");
        Console.WriteLine("bom-fitted-page: creating target page");
        var page = doc.Views.AddPageView("BOM layout", 431.8, 279.4)
            ?? throw new InvalidOperationException("A target layout must be created.");
        doc.Views.ActiveView = page;
        page.SetPageAsActive();
        Console.WriteLine("bom-fitted-page: placing fitted snapshot");
        var result = placed.PlaceBom(doc, assembly.Name, new Point3d(185, 220, 0), new Point3d(25, 180, 0),
            new[] { "item", "description", "material", "quantity", "unit" });
        var objects = doc.Objects.GetObjectList(ObjectType.AnyObject).Where(obj => obj.Attributes.Space == ActiveSpace.PageSpace).ToArray();
        Require(objects.Length == result.ObjectCount && result.ObjectCount > 0, "All table entities must be present.");
        Require(objects.All(obj => obj.Attributes.ViewportId == page.MainViewport.Id), "The BOM must belong to only the target layout.");
        Require(objects.All(obj => obj.Attributes.GetGroupList()?.Length == 1), "The completed table must be grouped.");
        Require(objects.Select(obj => obj.Attributes.GetGroupList()![0]).Distinct().Count() == 1, "The table must form one group.");
        foreach (var obj in objects)
        {
            var box = obj.Geometry.GetBoundingBox(true);
            Require(box.IsValid && box.Min.X >= 25 - 1e-6 && box.Max.X <= 185 + 1e-6 && box.Min.Y >= 180 - 1e-6 && box.Max.Y <= 220 + 1e-6,
                "Inserted native annotations and lines must stay within the chosen corners.");
        }
        var text = objects.Select(obj => obj.Geometry).OfType<TextEntity>().ToArray();
        Require(text.Any(item => item.PlainText == "7") && text.Any(item => item.PlainText == "Aluminum"),
            "The placed hardware BOM must contain the recorded quantity and material.");
        Require(result.TextHeight <= 3.175 + 1e-8 && result.TextHeight >= 0.508,
            "The paper-size font must use millimeters independently of the model's inch unit system.");
        var objectCount = doc.Objects.Count;
        var rejected = false;
        try
        {
            placed.PlaceBom(doc, assembly.Name, new Point3d(0, 0, 0), new Point3d(0.01, 0.01, 0), new[] { "item" });
        }
        catch (ArgumentException) { rejected = true; }
        Require(rejected && doc.Objects.Count == objectCount, "Rejected tiny placement must leave the existing table untouched.");
    }

    private static void AssertPlacedBomBounds(PlacedBomLayout layout, double width, double height)
    {
        Require(Math.Abs(layout.ColumnWidths.Sum() - width) < width * 1e-9,
            "Column widths must fill the complete selected rectangle width.");
        var topLeft = new Point3d(3, 100, 0);
        var geometry = PlacedBomService.CreateGeometry(layout, topLeft);
        try
        {
            Require(geometry.Count > 0, "The fitted table must produce native Rhino geometry.");
            var expectedText = layout.Rows.SelectMany(row => row.Cells).SelectMany(cell => cell.Lines)
                .Where(line => !string.IsNullOrEmpty(line));
            Require(geometry.OfType<TextEntity>().Select(text => text.PlainText).SequenceEqual(expectedText),
                "Native rendered text must retain every literal character, including paths, braces and Unicode, without treating content as RTF controls.");
            foreach (var entity in geometry)
            {
                var bounds = entity.GetBoundingBox(true);
                Require(bounds.IsValid && bounds.Min.X >= topLeft.X - 1e-7 && bounds.Max.X <= topLeft.X + width + 1e-7
                    && bounds.Max.Y <= topLeft.Y + 1e-7 && bounds.Min.Y >= topLeft.Y - height - 1e-7,
                    $"Measured native table text and grid must remain inside the requested rectangle; got {bounds}.");
            }
        }
        finally
        {
            foreach (var entity in geometry)
                entity.Dispose();
        }
    }
}
