using System.Drawing;
using System.Globalization;
using System.Text;
using System.Text.Json;
using AssemblyManagerPlugin.Core;
using AssemblyManagerPlugin.Geometry;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace AssemblyManagerPlugin.Services;

public sealed class NestingEstimateService
{
    private const double ThicknessTolerance = 0.01;
    private const double TableTextHeight = 0.09;
    private const double TableRowHeight = 0.24;
    private const double TableCellPadding = 0.04;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly AssemblyRepository _repository;
    private readonly LayerService _layers;
    private readonly GeometryFingerprintService _fingerprints;
    private readonly IMaterialLibrary _materials;
    private readonly IActionHistorySink _history;

    public NestingEstimateService(
        AssemblyRepository repository,
        LayerService layers,
        GeometryFingerprintService fingerprints,
        IMaterialLibrary materials,
        IActionHistorySink history)
    {
        _repository = repository;
        _layers = layers;
        _fingerprints = fingerprints;
        _materials = materials;
        _history = history;
    }

    public MaterialEstimateReportRecord EstimateMaterials(RhinoDoc doc, string assemblyName)
    {
        var store = _repository.Load(doc);
        var assembly = store.FindAssembly(assemblyName)
            ?? throw new InvalidOperationException($"Assembly '{assemblyName}' was not found.");

        var sheetShapes = _materials.GetMaterials(doc)
            .Where(material => MaterialAssignment.IsSheetLike(material.ShapeType))
            .ToList();

        var assignments = new List<(PartRecord Part, MaterialRecord Material, double Width, double Height, double Thickness, double AreaEach)>();
        var unaccounted = new List<MaterialEstimateUnaccountedRecord>();

        foreach (var part in assembly.Parts.OrderBy(part => part.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (!TryGetPartFootprint(doc, part, out var width, out var height, out var thickness, out var reason))
            {
                AddUnaccounted(unaccounted, part, width, height, thickness, reason);
                continue;
            }

            part.MaterialThickness = thickness;
            var materialId = NormalizeMaterialIdForLookup(part.MaterialId);
            if (string.IsNullOrWhiteSpace(materialId))
            {
                AddUnaccounted(unaccounted, part, width, height, thickness, "No material is assigned.");
                continue;
            }

            var candidates = GetCandidateSheetShapes(sheetShapes, materialId);
            if (candidates.Count == 0)
            {
                AddUnaccounted(
                    unaccounted,
                    part,
                    width,
                    height,
                    thickness,
                    $"No sheet stock shapes are available for material '{_materials.GetMaterialLabel(doc, materialId)}'.");
                continue;
            }

            var thicknessMatches = candidates
                .Where(material => material.Thickness > RhinoMath.ZeroTolerance && Math.Abs(material.Thickness - thickness) <= ThicknessTolerance)
                .ToList();
            if (thicknessMatches.Count == 0)
            {
                AddUnaccounted(
                    unaccounted,
                    part,
                    width,
                    height,
                    thickness,
                    $"No sheet stock thickness matches {Format(thickness)}. Available thicknesses: {AvailableThicknesses(candidates)}.");
                continue;
            }

            var fitting = thicknessMatches
                .Where(material => FitsSheet(material, width, height))
                .OrderBy(ActualSheetArea)
                .ThenBy(material => Math.Max(ActualSheetWidth(material), ActualSheetHeight(material)))
                .ThenBy(material => material.Name, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            if (fitting is null)
            {
                var originalWidth = width;
                var originalHeight = height;
                if (TryGetReorientedPartFootprint(doc, part, out var reorientedWidth, out var reorientedHeight))
                {
                    var reorientedFit = thicknessMatches
                        .Where(material => FitsSheet(material, reorientedWidth, reorientedHeight))
                        .OrderBy(ActualSheetArea)
                        .ThenBy(material => Math.Max(ActualSheetWidth(material), ActualSheetHeight(material)))
                        .ThenBy(material => material.Name, StringComparer.OrdinalIgnoreCase)
                        .FirstOrDefault();

                    if (reorientedFit is not null)
                    {
                        assignments.Add((part, reorientedFit, reorientedWidth, reorientedHeight, thickness, reorientedWidth * reorientedHeight));
                        continue;
                    }

                    width = reorientedWidth;
                    height = reorientedHeight;
                }

                AddUnaccounted(
                    unaccounted,
                    part,
                    width,
                    height,
                    thickness,
                    $"Part footprint {Format(originalWidth)} x {Format(originalHeight)} does not fit an available {Format(thickness)} thick sheet. Reoriented footprint checked: {Format(width)} x {Format(height)}.");
                continue;
            }

            assignments.Add((part, fitting, width, height, thickness, width * height));
        }

        var report = BuildReport(assemblyName, assignments, unaccounted);
        assembly.LastMaterialEstimate = report;
        assembly.NestingEstimates = BuildLegacyNestingEstimates(report);
        assembly.UpdatedAt = DateTimeOffset.UtcNow;
        _repository.Save(doc, store);
        _history.Record(doc, new ActionHistoryEntry
        {
            CommandName = "EstimateMaterials",
            AssemblyName = assemblyName,
            Summary = $"Generated material estimate with {report.Lines.Count} stock shape line(s) and {report.UnaccountedObjects.Count} unaccounted part(s)."
        });
        return report;
    }

    public IReadOnlyList<NestingEstimateRecord> EstimateSheetCounts(RhinoDoc doc, string assemblyName)
    {
        return EstimateMaterials(doc, assemblyName)
            .Lines
            .Select(line => ToLegacyEstimate(line, assemblyName))
            .ToList();
    }

    public int PlaceMaterialEstimateTable(RhinoDoc doc, string assemblyName, Point3d origin)
    {
        if (doc.ActiveSpace != ActiveSpace.PageSpace)
            throw new InvalidOperationException("Material estimate tables must be placed in layout/page space.");

        var report = EstimateMaterials(doc, assemblyName);
        var objectIds = DrawReportTable(doc, report, origin);
        _history.Record(doc, new ActionHistoryEntry
        {
            CommandName = "PlaceMaterialEstimate",
            AssemblyName = assemblyName,
            Summary = $"Placed material estimate table with {report.Lines.Count} stock shape line(s)."
        });
        doc.Views.Redraw();
        return objectIds.Count;
    }

    public void ExportCsv(MaterialEstimateReportRecord report, string filepath)
    {
        var builder = new StringBuilder();
        builder.AppendLine("section,material,shape,type,thickness,unit,sheet_width,sheet_height,quantity,total_part_area,nesting_efficiency,price_per_unit,price_unit,estimated_cost,parts");
        foreach (var line in report.Lines)
        {
            builder.AppendLine(string.Join(",",
                Csv("materials"),
                Csv(line.BaseMaterialName),
                Csv(line.ShapeName),
                Csv(line.ShapeType),
                Format(line.Thickness),
                Csv(line.Unit),
                Format(line.SheetWidth),
                Format(line.SheetHeight),
                line.EstimatedSheetCount.ToString(CultureInfo.InvariantCulture),
                Format(line.TotalPartArea),
                Format(line.NestingEfficiency),
                Format(line.PricePerUnit),
                Csv(line.PriceUnit),
                Format(line.EstimatedCost),
                Csv(PartSummary(line.Parts))));
        }

        builder.AppendLine();
        builder.AppendLine("section,part,quantity,material,required_width,required_height,required_thickness,reason");
        foreach (var item in report.UnaccountedObjects)
        {
            builder.AppendLine(string.Join(",",
                Csv("unaccounted"),
                Csv(item.PartName),
                item.Quantity.ToString(CultureInfo.InvariantCulture),
                Csv(item.MaterialName),
                Format(item.RequiredWidth),
                Format(item.RequiredHeight),
                Format(item.RequiredThickness),
                Csv(item.Reason)));
        }

        File.WriteAllText(filepath, builder.ToString());
    }

    public void ExportJson(MaterialEstimateReportRecord report, string filepath)
    {
        File.WriteAllText(filepath, JsonSerializer.Serialize(report, JsonOptions));
    }

    private bool TryGetPartFootprint(
        RhinoDoc doc,
        PartRecord part,
        out double width,
        out double height,
        out double thickness,
        out string reason)
    {
        width = 0.0;
        height = 0.0;
        thickness = 0.0;
        reason = string.Empty;

        var objectId = part.GeneratedObjectIds.FirstOrDefault(id => doc.Objects.FindId(id) is not null);
        if (objectId == Guid.Empty)
        {
            reason = "Generated part geometry was not found.";
            return false;
        }

        var rhinoObject = doc.Objects.FindId(objectId);
        if (rhinoObject is null || !_fingerprints.TryDuplicateBrep(rhinoObject, out var brep))
        {
            reason = "Could not read generated part as Brep geometry.";
            return false;
        }

        var dimensions = _fingerprints.GetOrientedDimensions(brep)
            .OrderByDescending(value => value)
            .Take(2)
            .ToArray();
        if (dimensions.Length < 2 || dimensions.Any(value => value <= RhinoMath.ZeroTolerance))
        {
            reason = "Could not compute a valid part footprint.";
            return false;
        }

        width = dimensions[0];
        height = dimensions[1];
        thickness = part.MaterialThickness > RhinoMath.ZeroTolerance
            ? part.MaterialThickness
            : _fingerprints.GetMaterialThickness(brep);
        if (thickness <= RhinoMath.ZeroTolerance)
        {
            reason = "Could not determine material thickness.";
            return false;
        }

        return true;
    }

    private bool TryGetReorientedPartFootprint(
        RhinoDoc doc,
        PartRecord part,
        out double width,
        out double height)
    {
        width = 0.0;
        height = 0.0;

        var objectId = part.GeneratedObjectIds.FirstOrDefault(id => doc.Objects.FindId(id) is not null);
        if (objectId == Guid.Empty)
            return false;

        var rhinoObject = doc.Objects.FindId(objectId);
        if (rhinoObject is null || !_fingerprints.TryDuplicateBrep(rhinoObject, out var brep))
            return false;

        var geometry = brep.DuplicateBrep();
        geometry.Transform(TransformUtilities.OrientLargestFaceToWorldXY(geometry, _fingerprints, Point3d.Origin));
        OptimizePlanBoundingBox(geometry);
        RotateLongDimensionToY(geometry);

        var bbox = geometry.GetBoundingBox(true);
        if (!bbox.IsValid)
            return false;

        width = Math.Abs(bbox.Max.X - bbox.Min.X);
        height = Math.Abs(bbox.Max.Y - bbox.Min.Y);
        return width > RhinoMath.ZeroTolerance && height > RhinoMath.ZeroTolerance;
    }

    private List<MaterialRecord> GetCandidateSheetShapes(IEnumerable<MaterialRecord> sheetShapes, string materialId)
    {
        var exact = sheetShapes.FirstOrDefault(material => string.Equals(material.Id, materialId, StringComparison.OrdinalIgnoreCase));
        var parentId = exact is not null && !string.IsNullOrWhiteSpace(exact.BaseMaterialId)
            ? exact.BaseMaterialId
            : materialId;

        return sheetShapes
            .Where(material => string.Equals(material.BaseMaterialId, parentId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(material.Id, materialId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(material => material.Thickness)
            .ThenBy(ActualSheetArea)
            .ThenBy(material => material.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static MaterialEstimateReportRecord BuildReport(
        string assemblyName,
        IReadOnlyList<(PartRecord Part, MaterialRecord Material, double Width, double Height, double Thickness, double AreaEach)> assignments,
        IReadOnlyList<MaterialEstimateUnaccountedRecord> unaccounted)
    {
        var report = new MaterialEstimateReportRecord
        {
            AssemblyName = assemblyName,
            UnaccountedObjects = unaccounted.ToList()
        };

        foreach (var group in assignments.GroupBy(entry => entry.Material.Id, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => group.First().Material.BaseMaterialName, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(group => group.First().Material.Thickness)
                     .ThenBy(group => ActualSheetArea(group.First().Material)))
        {
            var material = group.First().Material;
            var line = new MaterialEstimateLineRecord
            {
                MaterialId = material.Id,
                MaterialName = material.Name,
                BaseMaterialId = material.BaseMaterialId,
                BaseMaterialName = string.IsNullOrWhiteSpace(material.BaseMaterialName) ? material.Name : material.BaseMaterialName,
                ShapeId = material.ShapeId,
                ShapeName = string.IsNullOrWhiteSpace(material.ShapeName) ? material.Name : material.ShapeName,
                ShapeType = material.ShapeType,
                Thickness = material.Thickness,
                Unit = material.Unit,
                SheetWidth = ActualSheetWidth(material),
                SheetHeight = ActualSheetHeight(material),
                NestingEfficiency = material.NestingEfficiency,
                PricePerUnit = material.PricePerUnit,
                PriceUnit = material.PriceUnit
            };

            foreach (var entry in group.OrderBy(entry => entry.Part.Name, StringComparer.OrdinalIgnoreCase))
            {
                line.Parts.Add(new MaterialEstimatePartRecord
                {
                    PartName = entry.Part.Name,
                    Quantity = entry.Part.Quantity,
                    RequiredWidth = entry.Width,
                    RequiredHeight = entry.Height,
                    RequiredThickness = entry.Thickness,
                    AreaEach = entry.AreaEach,
                    MaterialId = material.Id
                });
                line.TotalPartArea += entry.AreaEach * entry.Part.Quantity;
            }

            var effectiveSheetArea = line.SheetWidth * line.SheetHeight * line.NestingEfficiency;
            line.EstimatedSheetCount = effectiveSheetArea <= RhinoMath.ZeroTolerance
                ? 0
                : Math.Max(1, (int)Math.Ceiling(line.TotalPartArea / effectiveSheetArea));
            line.EstimatedCost = line.PricePerUnit > 0 ? line.EstimatedSheetCount * line.PricePerUnit : 0;
            report.Lines.Add(line);
        }

        return report;
    }

    private static List<NestingEstimateRecord> BuildLegacyNestingEstimates(MaterialEstimateReportRecord report)
    {
        return report.Lines.Select(line => ToLegacyEstimate(line, report.AssemblyName)).ToList();
    }

    private static NestingEstimateRecord ToLegacyEstimate(MaterialEstimateLineRecord line, string assemblyName = "")
    {
        return new NestingEstimateRecord
        {
            AssemblyName = assemblyName,
            MaterialId = line.MaterialId,
            MaterialName = $"{line.BaseMaterialName} - {line.ShapeName}",
            SheetWidth = line.SheetWidth,
            SheetHeight = line.SheetHeight,
            NestingEfficiency = line.NestingEfficiency,
            TotalPartArea = line.TotalPartArea,
            EstimatedSheetCount = line.EstimatedSheetCount,
            Parts = line.Parts.Select(part => new NestingPartRecord
            {
                PartName = part.PartName,
                Quantity = part.Quantity,
                Width = part.RequiredWidth,
                Height = part.RequiredHeight,
                AreaEach = part.AreaEach,
                MaterialId = line.MaterialId
            }).ToList()
        };
    }

    private List<Guid> DrawReportTable(RhinoDoc doc, MaterialEstimateReportRecord report, Point3d origin)
    {
        _layers.EnsureRootLayers(doc);
        var layerIndex = _layers.EnsureLayerIndex(doc, $"{AssemblyManagerConstants.AnnotationRootLayer}::Material Estimates", Color.Black);
        var attributes = new ObjectAttributes
        {
            LayerIndex = layerIndex,
            Space = ActiveSpace.PageSpace,
            ColorSource = ObjectColorSource.ColorFromObject,
            ObjectColor = Color.Black
        };

        var columns = new (string Header, double MinWidth)[]
        {
            ("Material", 1.45),
            ("Shape", 1.45),
            ("Thk", 0.55),
            ("Sheet", 1.05),
            ("Qty", 0.45),
            ("Area", 0.7),
            ("Parts / Notes", 2.15)
        };
        var rows = BuildTableRows(report, columns.Select(column => column.Header).ToArray());
        var columnWidths = CalculateColumnWidths(rows, columns);
        var tableWidth = columnWidths.Sum();
        var ids = new List<Guid>();
        var y = origin.Y;

        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            var rowHeight = row.IsSection ? TableRowHeight * 1.15 : TableRowHeight;
            var x = origin.X;

            ids.Add(AddLine(doc, attributes, new Point3d(origin.X, y, 0), new Point3d(origin.X + tableWidth, y, 0)));
            if (row.IsSection)
            {
                ids.Add(AddLine(doc, attributes, new Point3d(origin.X, y, 0), new Point3d(origin.X, y - rowHeight, 0)));
                ids.Add(AddLine(doc, attributes, new Point3d(origin.X + tableWidth, y, 0), new Point3d(origin.X + tableWidth, y - rowHeight, 0)));
                ids.Add(AddCellText(
                    doc,
                    attributes,
                    row.Cells[0],
                    new Point3d(origin.X + TableCellPadding, y - TableCellPadding, 0),
                    TableTextHeight * 1.1));
            }
            else
            {
                for (var columnIndex = 0; columnIndex < columns.Length; columnIndex++)
                {
                    ids.Add(AddLine(doc, attributes, new Point3d(x, y, 0), new Point3d(x, y - rowHeight, 0)));
                    if (columnIndex < row.Cells.Count && !string.IsNullOrWhiteSpace(row.Cells[columnIndex]))
                    {
                        ids.Add(AddCellText(
                            doc,
                            attributes,
                            row.Cells[columnIndex],
                            new Point3d(x + TableCellPadding, y - TableCellPadding, 0),
                            TableTextHeight));
                    }

                    x += columnWidths[columnIndex];
                }

                ids.Add(AddLine(doc, attributes, new Point3d(origin.X + tableWidth, y, 0), new Point3d(origin.X + tableWidth, y - rowHeight, 0)));
            }
            y -= rowHeight;
        }

        ids.Add(AddLine(doc, attributes, new Point3d(origin.X, y, 0), new Point3d(origin.X + tableWidth, y, 0)));

        var groupIndex = doc.Groups.Add($"MaterialEstimate_{CleanGroupName(report.AssemblyName)}_{DateTime.Now:yyyyMMdd_HHmmss}");
        if (groupIndex >= 0)
        {
            foreach (var id in ids.Where(id => id != Guid.Empty))
                doc.Groups.AddToGroup(groupIndex, id);
        }

        return ids;
    }

    private static List<TableRow> BuildTableRows(MaterialEstimateReportRecord report, string[] headers)
    {
        var rows = new List<TableRow>
        {
            TableRow.Section($"Material Estimate - {report.AssemblyName}"),
            new(headers)
        };

        foreach (var line in report.Lines)
        {
            rows.Add(new TableRow(
                line.BaseMaterialName,
                line.ShapeName,
                Format(line.Thickness),
                $"{Format(line.SheetWidth)} x {Format(line.SheetHeight)}",
                line.EstimatedSheetCount.ToString(CultureInfo.InvariantCulture),
                Format(line.TotalPartArea),
                PartSummary(line.Parts)));
        }

        if (report.UnaccountedObjects.Count > 0)
        {
            rows.Add(TableRow.Section("Unaccounted Objects"));
            rows.Add(new TableRow("Part", "Material", "Thk", "Required", "Qty", string.Empty, "Reason"));
            foreach (var item in report.UnaccountedObjects)
            {
                rows.Add(new TableRow(
                    item.PartName,
                    item.MaterialName,
                    Format(item.RequiredThickness),
                    $"{Format(item.RequiredWidth)} x {Format(item.RequiredHeight)}",
                    item.Quantity.ToString(CultureInfo.InvariantCulture),
                    string.Empty,
                    item.Reason));
            }
        }

        return rows;
    }

    private static double[] CalculateColumnWidths(List<TableRow> rows, (string Header, double MinWidth)[] columns)
    {
        var widths = columns
            .Select(column => Math.Max(column.MinWidth, EstimateTextWidth(column.Header, TableTextHeight) + (TableCellPadding * 2.0)))
            .ToArray();

        foreach (var row in rows.Where(row => !row.IsSection))
        {
            for (var i = 0; i < Math.Min(row.Cells.Count, widths.Length); i++)
            {
                var required = EstimateTextWidth(row.Cells[i], TableTextHeight) + (TableCellPadding * 2.0);
                if (required > widths[i])
                    widths[i] = required;
            }
        }

        var sectionWidth = rows
            .Where(row => row.IsSection && row.Cells.Count > 0)
            .Select(row => EstimateTextWidth(row.Cells[0], TableTextHeight * 1.1) + (TableCellPadding * 2.0))
            .DefaultIfEmpty(0.0)
            .Max();
        var tableWidth = widths.Sum();
        if (sectionWidth > tableWidth && widths.Length > 0)
            widths[^1] += sectionWidth - tableWidth;

        return widths;
    }

    private static double EstimateTextWidth(string text, double textHeight)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0.0;

        var measuredWidth = 0.0;
        foreach (var line in text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None))
        {
            if (string.IsNullOrEmpty(line))
                continue;

            var entity = new TextEntity
            {
                PlainText = line,
                Plane = Plane.WorldXY,
                TextHeight = textHeight,
                Justification = TextJustification.TopLeft
            };
            var bbox = entity.GetBoundingBox(true);
            var width = bbox.IsValid
                ? Math.Abs(bbox.Max.X - bbox.Min.X)
                : line.Length * textHeight * 0.72;
            measuredWidth = Math.Max(measuredWidth, width);
        }

        return measuredWidth;
    }

    private static Guid AddCellText(RhinoDoc doc, ObjectAttributes attributes, string text, Point3d anchor, double textHeight)
    {
        var entity = new TextEntity
        {
            PlainText = text,
            Plane = new Plane(anchor, Vector3d.ZAxis),
            TextHeight = textHeight,
            Justification = TextJustification.TopLeft
        };
        return doc.Objects.AddText(entity, attributes);
    }

    private static Guid AddLine(RhinoDoc doc, ObjectAttributes attributes, Point3d start, Point3d end)
    {
        return doc.Objects.AddCurve(new LineCurve(start, end), attributes);
    }

    private static void AddUnaccounted(
        List<MaterialEstimateUnaccountedRecord> unaccounted,
        PartRecord part,
        double width,
        double height,
        double thickness,
        string reason)
    {
        unaccounted.Add(new MaterialEstimateUnaccountedRecord
        {
            PartName = part.Name,
            Quantity = part.Quantity,
            MaterialId = part.MaterialId,
            MaterialName = string.IsNullOrWhiteSpace(part.MaterialId) ? "TBD" : part.MaterialId,
            RequiredWidth = width,
            RequiredHeight = height,
            RequiredThickness = thickness,
            Reason = reason
        });
    }

    private static bool FitsSheet(MaterialRecord material, double requiredWidth, double requiredHeight)
    {
        var sheetWidth = ActualSheetWidth(material);
        var sheetHeight = ActualSheetHeight(material);
        if (sheetWidth <= 0 || sheetHeight <= 0 || requiredWidth <= 0 || requiredHeight <= 0)
            return false;

        return (requiredWidth <= sheetWidth && requiredHeight <= sheetHeight)
            || (requiredWidth <= sheetHeight && requiredHeight <= sheetWidth);
    }

    private static void OptimizePlanBoundingBox(Brep geometry)
    {
        var bbox = geometry.GetBoundingBox(true);
        if (!bbox.IsValid)
            return;

        var center = bbox.Center;
        var bestAngle = 0.0;
        var bestArea = PlanArea(bbox);

        foreach (var angle in GetPlanRotationCandidates(geometry))
        {
            var rotation = Transform.Rotation(angle, Vector3d.ZAxis, center);
            var testBox = geometry.GetBoundingBox(rotation);
            var area = PlanArea(testBox);
            if (area >= bestArea)
                continue;

            bestArea = area;
            bestAngle = angle;
        }

        if (Math.Abs(bestAngle) <= RhinoMath.ZeroTolerance)
            return;

        geometry.Transform(Transform.Rotation(bestAngle, Vector3d.ZAxis, center));
    }

    private static IEnumerable<double> GetPlanRotationCandidates(Brep geometry)
    {
        var seen = new HashSet<int>();

        bool Add(double angle, out double normalized)
        {
            normalized = NormalizePlanAngle(angle);
            return seen.Add((int)Math.Round(normalized * 1000000.0));
        }

        if (Add(0.0, out var zero))
            yield return zero;

        foreach (var edge in geometry.Edges)
        {
            var start = edge.PointAtStart;
            var end = edge.PointAtEnd;
            var dx = end.X - start.X;
            var dy = end.Y - start.Y;
            if ((dx * dx) + (dy * dy) <= RhinoMath.ZeroTolerance)
                continue;

            var edgeAngle = Math.Atan2(dy, dx);
            if (Add(-edgeAngle, out var alignToX))
                yield return alignToX;
            if (Add((Math.PI / 2.0) - edgeAngle, out var alignToY))
                yield return alignToY;
        }

        for (var degrees = 1; degrees < 180; degrees++)
        {
            if (Add(RhinoMath.ToRadians(degrees), out var sampled))
                yield return sampled;
        }
    }

    private static double NormalizePlanAngle(double angle)
    {
        var normalized = angle % Math.PI;
        if (normalized < 0)
            normalized += Math.PI;
        return normalized;
    }

    private static void RotateLongDimensionToY(Brep geometry)
    {
        var bbox = geometry.GetBoundingBox(true);
        if (!bbox.IsValid)
            return;

        var xLength = Math.Abs(bbox.Max.X - bbox.Min.X);
        var yLength = Math.Abs(bbox.Max.Y - bbox.Min.Y);
        if (xLength <= yLength)
            return;

        geometry.Transform(Transform.Rotation(Math.PI / 2.0, Vector3d.ZAxis, bbox.Center));
    }

    private static double PlanArea(BoundingBox bbox)
    {
        if (!bbox.IsValid)
            return double.MaxValue;

        return Math.Abs(bbox.Max.X - bbox.Min.X) * Math.Abs(bbox.Max.Y - bbox.Min.Y);
    }

    private static double ActualSheetArea(MaterialRecord material)
    {
        return ActualSheetWidth(material) * ActualSheetHeight(material);
    }

    private static double ActualSheetWidth(MaterialRecord material)
    {
        return material.Width > RhinoMath.ZeroTolerance ? material.Width : material.SheetWidth;
    }

    private static double ActualSheetHeight(MaterialRecord material)
    {
        return material.Height > RhinoMath.ZeroTolerance ? material.Height : material.SheetHeight;
    }

    private static string NormalizeMaterialIdForLookup(string materialId)
    {
        if (string.IsNullOrWhiteSpace(materialId))
            return string.Empty;

        if (!materialId.StartsWith("AMMAT|", StringComparison.OrdinalIgnoreCase))
            return materialId.Trim();

        var parts = materialId.Split('|');
        return parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]) ? parts[1].Trim() : materialId.Trim();
    }

    private static string AvailableThicknesses(IEnumerable<MaterialRecord> materials)
    {
        var values = materials
            .Where(material => material.Thickness > RhinoMath.ZeroTolerance)
            .Select(material => Format(material.Thickness))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return values.Count == 0 ? "none" : string.Join(", ", values);
    }

    private static string PartSummary(IEnumerable<MaterialEstimatePartRecord> parts)
    {
        return string.Join("; ", parts.Select(part => $"{part.PartName} x{part.Quantity}"));
    }

    private static string Format(double value)
    {
        return value <= RhinoMath.ZeroTolerance ? string.Empty : value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static string Csv(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        return value.Contains(',') || value.Contains('"') || value.Contains('\n')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }

    private static string CleanGroupName(string value)
    {
        var invalid = new[] { ':', ';', '"', '\'', '<', '>', '|', '?', '*', '\r', '\n', '\t' };
        var clean = invalid.Aggregate(value.Trim(), (current, c) => current.Replace(c, '_'));
        return string.IsNullOrWhiteSpace(clean) ? "Assembly" : clean;
    }

    private sealed class TableRow
    {
        public List<string> Cells { get; }
        public bool IsSection { get; }

        public TableRow(params string[] cells)
        {
            Cells = cells.ToList();
        }

        private TableRow(string title, bool isSection)
        {
            Cells = new List<string> { title };
            IsSection = isSection;
        }

        public static TableRow Section(string title)
        {
            return new TableRow(title, true);
        }
    }
}
