using System.Drawing;
using System.Globalization;
using System.Text.RegularExpressions;
using AssemblyManagerPlugin.Core;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace AssemblyManagerPlugin.Services;

/// <summary>Places a static material/hardware BOM snapshot inside a paper-space rectangle.</summary>
public sealed class PlacedBomService
{
    public const double StandardTextHeightInches = 0.125;
    // Reject an unusably small region instead of creating effectively invisible annotations.
    public const double MinimumTextHeightInches = 0.02;
    private const double PaddingRatio = 0.24;
    private const string FontFace = "Arial";

    public static IReadOnlyList<PlacedBomColumn> Columns { get; } = Array.AsReadOnly(new[]
    {
        new PlacedBomColumn("item", "Item", true),
        new PlacedBomColumn("material", "Material", true),
        new PlacedBomColumn("description", "Description", true),
        new PlacedBomColumn("thickness", "Thickness", true),
        new PlacedBomColumn("size", "Size", true),
        new PlacedBomColumn("quantity", "Quantity", true),
        new PlacedBomColumn("unit", "Unit", true),
        new PlacedBomColumn("area", "Part Area", false),
        new PlacedBomColumn("notes", "Parts / Notes", true),
        new PlacedBomColumn("source", "Source", false)
    });

    private readonly AssemblyRepository _repository;
    private readonly LayerService _layers;
    private readonly NestingEstimateService _estimates;
    private readonly IMaterialLibrary _materials;
    private readonly IActionHistorySink _history;

    public PlacedBomService(AssemblyRepository repository, LayerService layers,
        NestingEstimateService estimates, IMaterialLibrary materials, IActionHistorySink history)
    {
        _repository = repository;
        _layers = layers;
        _estimates = estimates;
        _materials = materials;
        _history = history;
    }

    public PlacedBomResult PlaceBom(RhinoDoc doc, string assemblyName, Point3d firstCorner,
        Point3d oppositeCorner, IEnumerable<string> selectedColumnIds)
    {
        if (doc.ActiveSpace != ActiveSpace.PageSpace || doc.Views.ActiveView is not RhinoPageView page)
            throw new InvalidOperationException("Move to a layout sheet (outside any detail) before running PlaceBOM.");
        var columns = SelectColumns(selectedColumnIds);
        if (!firstCorner.IsValid || !oppositeCorner.IsValid)
            throw new ArgumentException("Choose two valid opposite corners of the BOM rectangle.");
        var left = Math.Min(firstCorner.X, oppositeCorner.X);
        var top = Math.Max(firstCorner.Y, oppositeCorner.Y);
        var width = Math.Abs(firstCorner.X - oppositeCorner.X);
        var height = Math.Abs(firstCorner.Y - oppositeCorner.Y);
        ValidateDimensions(width, height, doc.PageUnitSystem);

        var report = _estimates.EstimateMaterials(doc, assemblyName);
        var assembly = _repository.Load(doc).FindAssembly(assemblyName)
            ?? throw new InvalidOperationException($"Assembly '{assemblyName}' was not found.");
        var hardware = BomService.BuildHardwareLines(assembly.Hardware);
        var layout = BuildLayout(report, hardware, id => _materials.GetMaterialLabel(doc, NormalizeMaterialId(id)),
            columns.Select(column => column.Id), width, height, doc.PageUnitSystem, doc.ModelUnitSystem);

        // Construct and validate every entity before adding any document object.
        var geometry = CreateGeometry(layout, new Point3d(left, top, 0));
        var ids = new List<Guid>();
        var groupIndex = -1;
        try
        {
            var layerIndex = _layers.EnsureLayerIndex(doc,
                $"{AssemblyManagerConstants.AnnotationRootLayer}::Material Estimates", Color.Black);
            var attributes = new ObjectAttributes
            {
                LayerIndex = layerIndex,
                Space = ActiveSpace.PageSpace,
                ViewportId = page.MainViewport.Id,
                ColorSource = ObjectColorSource.ColorFromObject,
                ObjectColor = Color.Black
            };
            foreach (var entity in geometry)
            {
                var id = entity is TextEntity text
                    ? doc.Objects.AddText(text, attributes)
                    : doc.Objects.AddCurve((Curve)entity, attributes);
                if (id == Guid.Empty)
                    throw new InvalidOperationException("Rhino could not add a BOM table object. The partial table was removed.");
                ids.Add(id);
                // Rhino may resolve document annotation settings at insertion time.
                // Check that this has not changed the measured paper-space geometry.
                var actual = doc.Objects.FindId(id)?.Geometry.GetBoundingBox(true) ?? BoundingBox.Unset;
                if (!IsWithin(actual, left, top, width, height))
                    throw new InvalidOperationException("The document's text settings would put BOM text outside the rectangle. The partial table was removed.");
            }

            groupIndex = doc.Groups.Add($"BOM_{Guid.NewGuid():N}");
            if (groupIndex < 0 || !doc.Groups.AddToGroup(groupIndex, ids))
                throw new InvalidOperationException("Rhino could not group the BOM table. The partial table was removed.");
            _history.Record(doc, new ActionHistoryEntry
            {
                CommandName = "PlaceBOM",
                AssemblyName = assemblyName,
                Summary = $"Placed BOM snapshot with {report.Lines.Count} stock line(s), {hardware.Count} hardware line(s), and {report.UnaccountedObjects.Count} unaccounted part(s).",
                Data = new Dictionary<string, string>
                {
                    ["Columns"] = string.Join(",", columns.Select(column => column.Id)),
                    ["TextHeight"] = layout.TextHeight.ToString("R", CultureInfo.InvariantCulture),
                    ["PageUnitSystem"] = doc.PageUnitSystem.ToString()
                }
            });
        }
        catch (Exception failure)
        {
            var retained = new List<Guid>();
            foreach (var id in ids)
            {
                var added = doc.Objects.FindId(id);
                // These are exclusively objects created by this invocation. A locked
                // annotation layer must not prevent rolling its partial table back.
                if (added is not null && !doc.Objects.Delete(added, true, true))
                    retained.Add(id);
            }
            if (groupIndex >= 0 && retained.Count == 0)
                doc.Groups.Delete(groupIndex);
            if (retained.Count > 0)
                throw new InvalidOperationException($"BOM placement failed and Rhino could not remove {retained.Count} partial table object(s). Undo this placement before retrying.", failure);
            throw;
        }
        finally
        {
            foreach (var entity in geometry)
                entity.Dispose();
        }

        doc.Views.Redraw();
        return new PlacedBomResult { ObjectCount = ids.Count, TextHeight = layout.TextHeight, WasReduced = layout.WasReduced };
    }

    /// <summary>Uses Rhino's actual font extents, but requires no page view or document writes.</summary>
    public static PlacedBomLayout BuildLayout(MaterialEstimateReportRecord report,
        IReadOnlyList<BomLineRecord> hardware, Func<string, string> materialLabel,
        IEnumerable<string> selectedColumnIds, double width, double maximumHeight,
        UnitSystem pageUnits, UnitSystem modelUnits = UnitSystem.Inches)
    {
        var columns = SelectColumns(selectedColumnIds);
        var units = ValidateDimensions(width, maximumHeight, pageUnits);
        var rows = BuildRows(report, hardware, materialLabel, columns, modelUnits);
        var metrics = new TextMetrics();
        var standardHeight = StandardTextHeightInches * units;
        var minimumHeight = MinimumTextHeightInches * units;
        var weights = new double[columns.Count];
        var minimumContentWidths = new double[columns.Count];
        for (var i = 0; i < columns.Count; i++)
        {
            var values = rows.Where(row => !row.IsSection).Select(row => row.Cells[i]).ToArray();
            // Square-root weighting lets longer prose/paths wrap without consuming
            // the whole table. A column's minimum fits its widest single glyph.
            weights[i] = Math.Sqrt(Math.Max(2.0, Math.Min(40.0, values.Select(metrics.NaturalWidth).DefaultIfEmpty(2).Max())));
            minimumContentWidths[i] = Math.Max(0.5, values.Select(metrics.WidestGlyph).DefaultIfEmpty(0.5).Max());
        }

        PlacedBomLayout? TryLayout(double textHeight)
        {
            var padding = textHeight * PaddingRatio;
            var minimums = minimumContentWidths.Select(value => value * textHeight * 1.002 + padding * 2).ToArray();
            var available = width - minimums.Sum();
            if (available <= 0)
                return null;
            var weightSum = weights.Sum();
            var widths = minimums.Select((minimum, i) => minimum + available * weights[i] / weightSum).ToArray();
            // Ensure floating-point accumulation lands exactly on the selected right edge.
            widths[^1] = width - widths.Take(widths.Length - 1).Sum();
            var output = new List<PlacedBomLayoutRow>();
            var y = 0.0;
            foreach (var row in rows)
            {
                var fontHeight = textHeight * (row.IsSection ? 1.1 : 1.0);
                var cellWidths = row.IsSection ? new[] { width } : widths;
                var cells = new List<PlacedBomLayoutCell>();
                var x = 0.0;
                var lineCount = 1;
                var lineHeight = fontHeight * 1.4;
                for (var i = 0; i < row.Cells.Length; i++)
                {
                    var lines = Wrap(row.Cells[i], (cellWidths[i] - padding * 2) / fontHeight, metrics);
                    if (lines is null)
                        return null;
                    lineCount = Math.Max(lineCount, lines.Count);
                    foreach (var line in lines)
                        lineHeight = Math.Max(lineHeight, metrics.Bounds(line).Diagonal.Y * fontHeight * 1.08);
                    cells.Add(new PlacedBomLayoutCell { Left = x, Width = cellWidths[i], OriginalText = row.Cells[i], Lines = lines });
                    x += cellWidths[i];
                }
                var rowHeight = lineCount * lineHeight + padding * 2;
                output.Add(new PlacedBomLayoutRow
                {
                    IsSection = row.IsSection, Top = y, Height = rowHeight, TextHeight = fontHeight,
                    Padding = padding, LineHeight = lineHeight, Cells = cells
                });
                y += rowHeight;
                if (y > maximumHeight)
                    return null;
            }
            return new PlacedBomLayout
            {
                Columns = columns, ColumnWidths = widths, Rows = output, Width = width, Height = y,
                TextHeight = textHeight, StandardTextHeight = standardHeight
            };
        }

        // Wrapping is always tried at the standard paper text height before shrinking.
        var standard = TryLayout(standardHeight);
        if (standard is not null)
            return standard;
        var best = TryLayout(minimumHeight)
            ?? throw new InvalidOperationException("The BOM cannot fit legibly in that rectangle. Choose a larger rectangle or fewer columns.");
        var low = minimumHeight;
        var high = standardHeight;
        for (var i = 0; i < 28; i++)
        {
            var middle = (low + high) / 2;
            var candidate = TryLayout(middle);
            if (candidate is null)
                high = middle;
            else
            {
                low = middle;
                best = candidate;
            }
        }
        return best;
    }

    /// <summary>Creates caller-owned measured entities without changing a Rhino document.</summary>
    public static IReadOnlyList<GeometryBase> CreateGeometry(PlacedBomLayout layout, Point3d topLeft)
    {
        var geometry = new List<GeometryBase>();
        try
        {
            foreach (var row in layout.Rows)
            {
                var y = topLeft.Y - row.Top;
                geometry.Add(new LineCurve(new Point3d(topLeft.X, y, 0), new Point3d(topLeft.X + layout.Width, y, 0)));
                foreach (var cell in row.Cells)
                {
                    var x = topLeft.X + cell.Left;
                    geometry.Add(new LineCurve(new Point3d(x, y, 0), new Point3d(x, y - row.Height, 0)));
                    for (var lineIndex = 0; lineIndex < cell.Lines.Count; lineIndex++)
                    {
                        var line = cell.Lines[lineIndex];
                        if (string.IsNullOrEmpty(line))
                            continue;
                        var text = CreateText(line, row.TextHeight);
                        geometry.Add(text);
                        var bounds = text.GetBoundingBox(true);
                        if (!bounds.IsValid)
                            throw new InvalidOperationException("Rhino could not measure BOM text.");
                        var anchor = new Point3d(x + row.Padding,
                            y - row.Padding - lineIndex * row.LineHeight, 0);
                        text.Transform(Transform.Translation(anchor.X - bounds.Min.X, anchor.Y - bounds.Max.Y, 0));
                        var actual = text.GetBoundingBox(true);
                        if (!IsWithin(actual, x, y, cell.Width, row.Height))
                            throw new InvalidOperationException("BOM text could not be fitted to its cell. Choose a larger rectangle.");
                    }
                }
                geometry.Add(new LineCurve(new Point3d(topLeft.X + layout.Width, y, 0),
                    new Point3d(topLeft.X + layout.Width, y - row.Height, 0)));
            }
            geometry.Add(new LineCurve(new Point3d(topLeft.X, topLeft.Y - layout.Height, 0),
                new Point3d(topLeft.X + layout.Width, topLeft.Y - layout.Height, 0)));
            return geometry;
        }
        catch
        {
            foreach (var entity in geometry)
                entity.Dispose();
            throw;
        }
    }

    private static IReadOnlyList<PlacedBomColumn> SelectColumns(IEnumerable<string> selectedColumnIds)
    {
        if (selectedColumnIds is null)
            throw new ArgumentNullException(nameof(selectedColumnIds));
        var ids = selectedColumnIds.ToHashSet(StringComparer.Ordinal);
        if (ids.Count == 0)
            throw new ArgumentException("Select at least one BOM column.", nameof(selectedColumnIds));
        if (ids.Any(id => !Columns.Any(column => column.Id == id)))
            throw new ArgumentException("An unrecognized BOM column was selected.", nameof(selectedColumnIds));
        return Columns.Where(column => ids.Contains(column.Id)).ToArray();
    }

    private static double ValidateDimensions(double width, double height, UnitSystem units)
    {
        if (units is UnitSystem.None or UnitSystem.Unset or UnitSystem.CustomUnits)
            throw new InvalidOperationException("Set the layout to a standard paper unit system before placing a BOM.");
        var unitScale = RhinoMath.UnitScale(UnitSystem.Inches, units);
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= MinimumTextHeightInches * unitScale
            || height <= MinimumTextHeightInches * unitScale || !double.IsFinite(unitScale) || unitScale <= 0)
            throw new ArgumentException("Choose a larger, non-zero BOM rectangle.");
        return unitScale;
    }

    private static List<SourceRow> BuildRows(MaterialEstimateReportRecord report, IReadOnlyList<BomLineRecord> hardware,
        Func<string, string> materialLabel, IReadOnlyList<PlacedBomColumn> columns, UnitSystem modelUnits)
    {
        var rows = new List<SourceRow> { SourceRow.Section($"Bill of Materials — {report.AssemblyName}") };
        var headers = columns.Select(column => column.Title).ToArray();
        void Add(IReadOnlyDictionary<string, string> values) => rows.Add(new SourceRow(false,
            columns.Select(column => values.TryGetValue(column.Id, out var value) ? value : string.Empty).ToArray()));

        if (report.Lines.Count > 0)
        {
            rows.Add(SourceRow.Section("Sheet Stock Estimate"));
            rows.Add(new SourceRow(false, headers));
            foreach (var line in report.Lines)
                Add(new Dictionary<string, string>
                {
                    ["item"] = line.ShapeName, ["material"] = line.BaseMaterialName,
                    ["description"] = line.ShapeType, ["thickness"] = Dimension(line.Thickness, line.Unit),
                    ["size"] = Size(line.SheetWidth, line.SheetHeight, line.Unit),
                    ["quantity"] = line.EstimatedSheetCount.ToString(CultureInfo.InvariantCulture), ["unit"] = "sheet",
                    ["area"] = Dimension(line.TotalPartArea, UnitLabel(modelUnits) + "²"),
                    ["notes"] = string.Join("; ", line.Parts.Select(part => $"{part.PartName} x{part.Quantity}")),
                    ["source"] = "Material estimate"
                });
        }
        if (hardware.Count > 0)
        {
            rows.Add(SourceRow.Section("Hardware"));
            rows.Add(new SourceRow(false, headers));
            foreach (var line in hardware)
            {
                var label = materialLabel(line.MaterialId);
                Add(new Dictionary<string, string>
                {
                    ["item"] = line.Item, ["material"] = string.IsNullOrWhiteSpace(label) ? "TBD" : label,
                    ["description"] = line.Description, ["quantity"] = Format(line.Quantity),
                    ["unit"] = line.Unit, ["source"] = line.Source
                });
            }
        }
        if (report.UnaccountedObjects.Count > 0)
        {
            rows.Add(SourceRow.Section("Unaccounted Objects"));
            rows.Add(new SourceRow(false, headers));
            foreach (var item in report.UnaccountedObjects)
            {
                var label = materialLabel(item.MaterialId);
                Add(new Dictionary<string, string>
                {
                    ["item"] = item.PartName, ["material"] = string.IsNullOrWhiteSpace(label) ? "TBD" : label,
                    ["thickness"] = Dimension(item.RequiredThickness, UnitLabel(modelUnits)),
                    ["size"] = Size(item.RequiredWidth, item.RequiredHeight, UnitLabel(modelUnits)),
                    ["quantity"] = item.Quantity.ToString(CultureInfo.InvariantCulture), ["unit"] = "ea", ["notes"] = item.Reason
                });
            }
        }
        if (rows.Count == 1)
            rows.Add(SourceRow.Section("No material or hardware items are recorded."));
        return rows;
    }

    private static IReadOnlyList<string>? Wrap(string value, double maximumWidth, TextMetrics metrics)
    {
        if (maximumWidth <= 0)
            return null;
        var output = new List<string>();
        foreach (var paragraph in (value ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var words = Regex.Matches(paragraph, @"\S+").Select(match => match.Value).ToArray();
            var current = string.Empty;
            foreach (var word in words)
            {
                var combined = current.Length == 0 ? word : current + " " + word;
                if (metrics.Width(combined) <= maximumWidth)
                {
                    current = combined;
                    continue;
                }
                if (current.Length > 0)
                {
                    output.Add(current);
                    current = string.Empty;
                }
                var remaining = word;
                while (metrics.Width(remaining) > maximumWidth)
                {
                    var starts = StringInfo.ParseCombiningCharacters(remaining);
                    var low = 0;
                    var high = starts.Length;
                    while (low < high)
                    {
                        var middle = (low + high + 1) / 2;
                        var end = middle == starts.Length ? remaining.Length : starts[middle];
                        if (metrics.Width(remaining[..end]) <= maximumWidth)
                            low = middle;
                        else
                            high = middle - 1;
                    }
                    if (low == 0)
                        return null;
                    var split = low == starts.Length ? remaining.Length : starts[low];
                    output.Add(remaining[..split]);
                    remaining = remaining[split..];
                }
                current = remaining;
            }
            output.Add(current);
        }
        return output;
    }

    private static TextEntity CreateText(string value, double height)
    {
        // Rhino's annotation parser recognizes RTF controls even through PlainText's
        // setter. Escape paths/braces as literal text before measuring or drawing.
        var richText = AnnotationBase.PlainTextToRtf(value);
        return new TextEntity
        {
            RichText = richText, Plane = Plane.WorldXY, Font = new Rhino.DocObjects.Font(FontFace),
            TextHeight = height, DimensionScale = 1.0, Justification = TextJustification.TopLeft
        };
    }

    private static bool IsWithin(BoundingBox bounds, double left, double top, double width, double height)
    {
        var tolerance = Math.Max(width, height) * 1e-8;
        return bounds.IsValid && bounds.Min.X >= left - tolerance && bounds.Max.X <= left + width + tolerance
            && bounds.Max.Y <= top + tolerance && bounds.Min.Y >= top - height - tolerance;
    }

    private static string Format(double number) => number.ToString("0.###", CultureInfo.InvariantCulture);
    private static string Dimension(double number, string unit) => number > RhinoMath.ZeroTolerance ? $"{Format(number)} {unit}".Trim() : string.Empty;
    private static string Size(double width, double height, string unit) => width > RhinoMath.ZeroTolerance && height > RhinoMath.ZeroTolerance
        ? $"{Format(width)} x {Format(height)} {unit}".Trim() : string.Empty;
    private static string UnitLabel(UnitSystem units) => units switch
    {
        UnitSystem.Inches => "in", UnitSystem.Feet => "ft", UnitSystem.Millimeters => "mm", UnitSystem.Centimeters => "cm",
        UnitSystem.Meters => "m", _ => units.ToString()
    };
    private static string NormalizeMaterialId(string id)
    {
        var parts = id.Split('|');
        return parts.Length > 1 && parts[0].Equals("AMMAT", StringComparison.OrdinalIgnoreCase) ? parts[1].Trim() : id.Trim();
    }

    private sealed record SourceRow(bool IsSection, string[] Cells)
    {
        public static SourceRow Section(string text) => new(true, new[] { text });
    }

    private sealed class TextMetrics
    {
        private readonly Dictionary<string, BoundingBox> _bounds = new(StringComparer.Ordinal);
        public BoundingBox Bounds(string value)
        {
            if (string.IsNullOrEmpty(value))
                return new BoundingBox(Point3d.Origin, Point3d.Origin);
            if (_bounds.TryGetValue(value, out var cached))
                return cached;
            using var text = CreateText(value, 1.0);
            var bounds = text.GetBoundingBox(true);
            if (!bounds.IsValid)
                throw new InvalidOperationException("Rhino could not measure BOM text using the table font.");
            return _bounds[value] = bounds;
        }
        // A small horizontal allowance absorbs font rounding between unit-height
        // measurement and final paper-height text without allowing clipped glyphs.
        public double Width(string value) => Bounds(value).Diagonal.X * 1.002;
        public double NaturalWidth(string value) => value.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(Width).DefaultIfEmpty(0).Max();
        public double WidestGlyph(string value)
        {
            var enumerator = StringInfo.GetTextElementEnumerator(value);
            var maximum = 0.0;
            while (enumerator.MoveNext())
            {
                var glyph = enumerator.GetTextElement();
                if (!string.IsNullOrWhiteSpace(glyph))
                    maximum = Math.Max(maximum, Width(glyph));
            }
            return maximum;
        }
    }
}
