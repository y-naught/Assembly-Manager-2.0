using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AssemblyManagerPlugin.Core;
using Rhino;

namespace AssemblyManagerPlugin.Services;

public interface IMaterialLibrary
{
    IReadOnlyList<MaterialRecord> GetMaterials(RhinoDoc? doc = null);
    MaterialRecord? FindById(RhinoDoc? doc, string materialId);
    MaterialRecord? ResolveForEstimate(RhinoDoc? doc, string materialId, double requiredWidth, double requiredHeight, double requiredThickness);
    string GetMaterialLabel(RhinoDoc? doc, string materialId);
}

public sealed class MaterialLibraryService : IMaterialLibrary
{
    private readonly AssemblyRepository _repository;
    private readonly PluginSettingsService _settings;
    private readonly IActionHistorySink _history;

    public MaterialLibraryService(AssemblyRepository repository, PluginSettingsService settings, IActionHistorySink history)
    {
        _repository = repository;
        _settings = settings;
        _history = history;
    }

    public IReadOnlyList<MaterialDefinitionRecord> GetMaterialDefinitions(RhinoDoc? doc = null)
    {
        var settings = LoadAndMigrateSettings(doc);
        return settings.Materials
            .Select(CloneAndNormalizeDefinition)
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<MaterialRecord> GetMaterials(RhinoDoc? doc = null)
    {
        return FlattenDefinitions(GetMaterialDefinitions(doc))
            .OrderBy(m => m.BaseMaterialName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.ShapeType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.Thickness)
            .ThenBy(m => m.SheetWidth)
            .ThenBy(m => m.SheetHeight)
            .ThenBy(m => m.ShapeName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public MaterialRecord? FindById(RhinoDoc? doc, string materialId)
    {
        if (string.IsNullOrWhiteSpace(materialId))
            return null;

        return GetMaterials(doc).FirstOrDefault(m => string.Equals(m.Id, materialId, StringComparison.OrdinalIgnoreCase));
    }

    public MaterialDefinitionRecord? FindMaterialDefinitionById(RhinoDoc? doc, string materialId)
    {
        if (string.IsNullOrWhiteSpace(materialId))
            return null;

        return GetMaterialDefinitions(doc)
            .FirstOrDefault(material => string.Equals(material.Id, materialId, StringComparison.OrdinalIgnoreCase));
    }

    public string GetMaterialLabel(RhinoDoc? doc, string materialId)
    {
        if (string.IsNullOrWhiteSpace(materialId))
            return string.Empty;

        var materialDefinition = FindMaterialDefinitionById(doc, materialId);
        if (materialDefinition is not null)
            return materialDefinition.Name;

        var exact = FindById(doc, materialId);
        if (exact is not null)
            return exact.Name;

        return materialId;
    }

    public MaterialRecord? ResolveForEstimate(RhinoDoc? doc, string materialId, double requiredWidth, double requiredHeight, double requiredThickness)
    {
        if (string.IsNullOrWhiteSpace(materialId))
            return null;

        var exact = FindById(doc, materialId);
        if (exact is not null)
            return exact;

        var parentMaterialId = NormalizeMaterialIdForLookup(materialId);
        var candidates = GetMaterials(doc)
            .Where(material => string.Equals(material.BaseMaterialId, parentMaterialId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (candidates.Count == 0)
            return null;

        var sheetLike = candidates
            .Where(material => MaterialAssignment.IsSheetLike(material.ShapeType))
            .ToList();
        if (sheetLike.Count == 0)
            return candidates
                .OrderBy(material => material.StockLength <= 0 ? double.MaxValue : material.StockLength)
                .ThenBy(material => material.Name, StringComparer.OrdinalIgnoreCase)
                .First();

        var thicknessMatches = FindThicknessMatches(sheetLike, requiredThickness);
        if (thicknessMatches.Count > 0)
            sheetLike = thicknessMatches;

        var fitting = sheetLike
            .Where(material => FitsSheet(material, requiredWidth, requiredHeight))
            .OrderBy(ActualSheetArea)
            .ThenBy(material => material.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (fitting is not null)
            return fitting;

        return sheetLike
            .OrderByDescending(ActualSheetArea)
            .ThenBy(material => material.Name, StringComparer.OrdinalIgnoreCase)
            .First();
    }

    public IReadOnlyList<MaterialDefinitionRecord> SaveMaterialDefinition(RhinoDoc? doc, MaterialDefinitionRecord material)
    {
        var settings = LoadAndMigrateSettings(doc);
        var normalized = CloneAndNormalizeDefinition(material);
        var existing = settings.Materials.FirstOrDefault(m => string.Equals(m.Id, normalized.Id, StringComparison.OrdinalIgnoreCase));
        if (existing is not null && normalized.Shapes.Count == 0)
            normalized.Shapes = existing.Shapes.Select(shape => CloneAndNormalizeShape(shape, normalized.Id)).ToList();

        UpsertDefinition(settings.Materials, normalized);
        SaveSettingsAndMirror(doc, settings);
        return GetMaterialDefinitions(doc);
    }

    public IReadOnlyList<MaterialDefinitionRecord> DeleteMaterialDefinition(RhinoDoc? doc, string materialId)
    {
        var settings = LoadAndMigrateSettings(doc);
        settings.Materials.RemoveAll(m => string.Equals(m.Id, materialId, StringComparison.OrdinalIgnoreCase));
        SaveSettingsAndMirror(doc, settings);
        return GetMaterialDefinitions(doc);
    }

    public IReadOnlyList<MaterialDefinitionRecord> SaveShape(RhinoDoc? doc, string materialId, MaterialShapeRecord shape)
    {
        var settings = LoadAndMigrateSettings(doc);
        var material = settings.Materials.FirstOrDefault(m => string.Equals(m.Id, materialId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Select or save a material before adding stock shapes.");

        var normalized = CloneAndNormalizeShape(shape, material.Id);
        UpsertShape(material.Shapes, normalized);
        SaveSettingsAndMirror(doc, settings);
        return GetMaterialDefinitions(doc);
    }

    public IReadOnlyList<MaterialDefinitionRecord> DeleteShape(RhinoDoc? doc, string materialId, string shapeId)
    {
        var settings = LoadAndMigrateSettings(doc);
        var material = settings.Materials.FirstOrDefault(m => string.Equals(m.Id, materialId, StringComparison.OrdinalIgnoreCase));
        if (material is not null)
            material.Shapes.RemoveAll(s => string.Equals(s.Id, shapeId, StringComparison.OrdinalIgnoreCase));

        SaveSettingsAndMirror(doc, settings);
        return GetMaterialDefinitions(doc);
    }

    public IReadOnlyList<MaterialRecord> ImportLibrary(RhinoDoc doc, string filepath)
    {
        if (!File.Exists(filepath))
            throw new FileNotFoundException("Material library file was not found.", filepath);

        var imported = Path.GetExtension(filepath).Equals(".json", StringComparison.OrdinalIgnoreCase)
            ? LoadJson(filepath)
            : LoadCsv(filepath);

        var settings = LoadAndMigrateSettings(doc);
        foreach (var material in imported.Select(CloneAndNormalizeDefinition))
            MergeDefinition(settings.Materials, material);

        SaveSettingsAndMirror(doc, settings);
        _history.Record(doc, new ActionHistoryEntry
        {
            CommandName = "ImportMaterialLibrary",
            Summary = $"Imported {imported.Sum(m => m.Shapes.Count)} stock shape(s) across {imported.Count} material(s).",
            Data = { ["path"] = filepath }
        });

        return GetMaterials(doc);
    }

    public int ExportLibrary(RhinoDoc doc, string filepath)
    {
        if (string.IsNullOrWhiteSpace(filepath))
            throw new ArgumentException("Choose a destination file path.", nameof(filepath));

        var materials = GetMaterialDefinitions(doc).ToList();
        var directory = Path.GetDirectoryName(filepath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        if (Path.GetExtension(filepath).Equals(".json", StringComparison.OrdinalIgnoreCase))
            ExportJson(filepath, materials);
        else
            ExportCsv(filepath, materials);

        _history.Record(doc, new ActionHistoryEntry
        {
            CommandName = "ExportMaterialLibrary",
            Summary = $"Exported {materials.Sum(material => material.Shapes.Count)} stock shape(s) across {materials.Count} material(s).",
            Data =
            {
                ["path"] = filepath,
                ["format"] = Path.GetExtension(filepath).Equals(".json", StringComparison.OrdinalIgnoreCase) ? "json" : "csv"
            }
        });

        return materials.Sum(material => material.Shapes.Count);
    }

    public int PurgeLibrary(RhinoDoc doc)
    {
        var settings = LoadAndMigrateSettings(doc);
        var removedShapeCount = settings.Materials.Sum(material => material.Shapes.Count);
        var removedMaterialCount = settings.Materials.Count;

        settings.Materials.Clear();
        SaveSettingsAndMirror(doc, settings);

        _history.Record(doc, new ActionHistoryEntry
        {
            CommandName = "PurgeMaterialLibrary",
            Summary = $"Purged {removedShapeCount} stock shape(s) across {removedMaterialCount} material(s) from the shared material library."
        });

        return removedShapeCount;
    }

    public void AssignMaterialToPart(RhinoDoc doc, string assemblyName, string partName, string materialId)
    {
        var store = _repository.Load(doc);
        var assembly = store.FindAssembly(assemblyName)
            ?? throw new InvalidOperationException($"Assembly '{assemblyName}' was not found.");

        var material = FindById(doc, materialId)
            ?? throw new InvalidOperationException($"Material shape '{materialId}' was not found.");

        var part = assembly.Parts.FirstOrDefault(p => string.Equals(p.Name, partName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Part '{partName}' was not found.");

        UpsertFlatMaterial(store.MaterialLibraryCache, material);
        part.MaterialId = material.Id;
        part.MaterialThickness = material.Thickness > 0 ? material.Thickness : part.MaterialThickness;
        ApplyMaterialToObjects(doc, part.GeneratedObjectIds.Concat(part.CamObjectIds), material);
        assembly.UpdatedAt = DateTimeOffset.UtcNow;
        _repository.Save(doc, store);
        _history.Record(doc, new ActionHistoryEntry
        {
            CommandName = "AssignMaterialToPart",
            AssemblyName = assemblyName,
            Summary = $"Assigned {material.Name} to {partName}.",
            Data =
            {
                ["part"] = partName,
                ["materialShape"] = material.Id
            }
        });
    }

    public int AssignMaterialToObjects(RhinoDoc doc, IEnumerable<Guid> objectIds, string materialId)
    {
        var material = FindById(doc, materialId)
            ?? throw new InvalidOperationException($"Material shape '{materialId}' was not found.");

        var count = ApplyMaterialToObjects(doc, objectIds, material);
        _history.Record(doc, new ActionHistoryEntry
        {
            CommandName = "AssignMaterials",
            Summary = $"Assigned {material.Name} to {count} object(s).",
            Data =
            {
                ["materialShape"] = material.Id,
                ["objectCount"] = count.ToString()
            }
        });
        doc.Views.Redraw();
        return count;
    }

    public int AssignMaterialToObjects(RhinoDoc doc, IEnumerable<Guid> objectIds, MaterialDefinitionRecord material)
    {
        var normalized = CloneAndNormalizeDefinition(material);
        var count = ApplyMaterialToObjects(doc, objectIds, normalized);
        _history.Record(doc, new ActionHistoryEntry
        {
            CommandName = "AssignMaterials",
            Summary = $"Assigned {normalized.Name} to {count} object(s).",
            Data =
            {
                ["material"] = normalized.Id,
                ["objectCount"] = count.ToString()
            }
        });
        doc.Views.Redraw();
        return count;
    }

    private static int ApplyMaterialToObjects(RhinoDoc doc, IEnumerable<Guid> objectIds, MaterialRecord material)
    {
        var count = 0;
        var seen = new HashSet<Guid>();
        foreach (var objectId in objectIds)
        {
            if (!seen.Add(objectId))
                continue;

            var rhinoObject = doc.Objects.FindId(objectId);
            if (rhinoObject is null)
                continue;

            var attributes = rhinoObject.Attributes.Duplicate();
            MaterialAssignment.Set(attributes, material);
            if (doc.Objects.ModifyAttributes(rhinoObject, attributes, true))
                count++;
        }

        return count;
    }

    private static int ApplyMaterialToObjects(RhinoDoc doc, IEnumerable<Guid> objectIds, MaterialDefinitionRecord material)
    {
        var count = 0;
        var seen = new HashSet<Guid>();
        foreach (var objectId in objectIds)
        {
            if (!seen.Add(objectId))
                continue;

            var rhinoObject = doc.Objects.FindId(objectId);
            if (rhinoObject is null)
                continue;

            var attributes = rhinoObject.Attributes.Duplicate();
            MaterialAssignment.Set(attributes, material);
            if (doc.Objects.ModifyAttributes(rhinoObject, attributes, true))
                count++;
        }

        return count;
    }

    public static bool TryParseSheetSize(string value, out double width, out double height)
    {
        width = 0;
        height = 0;
        var matches = Regex.Matches(value ?? string.Empty, @"[-+]?\d*\.?\d+");
        if (matches.Count < 2)
            return false;

        return double.TryParse(matches[0].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out width)
            && double.TryParse(matches[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out height)
            && width > 0
            && height > 0;
    }

    public static string FormatSheetSize(MaterialShapeRecord shape)
    {
        if (shape.SheetWidth <= 0 || shape.SheetHeight <= 0)
            return string.Empty;

        return $"{shape.SheetWidth:0.###}x{shape.SheetHeight:0.###}";
    }

    public static string FormatSheetSize(MaterialRecord material)
    {
        if (material.SheetWidth <= 0 || material.SheetHeight <= 0)
            return string.Empty;

        return $"{material.SheetWidth:0.###}x{material.SheetHeight:0.###}";
    }

    public static string MakeMaterialId(string name)
    {
        var clean = new string((name ?? string.Empty).Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray());
        return string.IsNullOrWhiteSpace(clean) ? Guid.NewGuid().ToString("N") : clean;
    }

    private PluginSettingsRecord LoadAndMigrateSettings(RhinoDoc? doc)
    {
        var settings = _settings.Load();
        var changed = false;

        if (settings.Materials.Count == 0)
        {
            var flatMaterials = settings.MaterialLibrary;
            if (flatMaterials.Count == 0 && doc is not null)
                flatMaterials = _repository.Load(doc).MaterialLibraryCache;

            if (flatMaterials.Count > 0)
            {
                settings.Materials = ConvertFlatMaterials(flatMaterials);
                changed = true;
            }
        }

        if (changed)
            SaveSettingsAndMirror(doc, settings);

        return settings;
    }

    private void SaveSettingsAndMirror(RhinoDoc? doc, PluginSettingsRecord settings)
    {
        settings.SchemaVersion = Math.Max(settings.SchemaVersion, 4);
        settings.Materials = settings.Materials.Select(CloneAndNormalizeDefinition).ToList();
        settings.MaterialLibrary = FlattenDefinitions(settings.Materials).ToList();
        _settings.Save(settings);
        MirrorToDocumentCache(doc, settings.Materials);
    }

    private void MirrorToDocumentCache(RhinoDoc? doc, IEnumerable<MaterialDefinitionRecord> materials)
    {
        if (doc is null)
            return;

        var store = _repository.Load(doc);
        store.MaterialLibraryCache = FlattenDefinitions(materials).ToList();
        _repository.Save(doc, store);
    }

    private static IEnumerable<MaterialRecord> FlattenDefinitions(IEnumerable<MaterialDefinitionRecord> materials)
    {
        foreach (var material in materials.Select(CloneAndNormalizeDefinition))
        {
            foreach (var shape in material.Shapes.Select(shape => CloneAndNormalizeShape(shape, material.Id)))
            {
                yield return new MaterialRecord
                {
                    Id = shape.Id,
                    Name = $"{material.Name} - {shape.Name}",
                    Category = shape.ShapeType,
                    BaseMaterialId = material.Id,
                    BaseMaterialName = material.Name,
                    DensityLbPerCubicInch = material.DensityLbPerCubicInch,
                    ShapeId = shape.Id,
                    ShapeName = shape.Name,
                    ShapeType = shape.ShapeType,
                    Thickness = shape.Thickness,
                    Unit = shape.Unit,
                    SheetWidth = shape.SheetWidth,
                    SheetHeight = shape.SheetHeight,
                    StockLength = shape.StockLength,
                    Width = shape.Width,
                    Height = shape.Height,
                    Diameter = shape.Diameter,
                    WallThickness = shape.WallThickness,
                    NestingEfficiency = shape.NestingEfficiency,
                    PricePerUnit = shape.PricePerUnit,
                    PriceUnit = shape.PriceUnit,
                    Properties = new Dictionary<string, string>(shape.Properties, StringComparer.OrdinalIgnoreCase)
                };
            }
        }
    }

    private static List<MaterialDefinitionRecord> ConvertFlatMaterials(IEnumerable<MaterialRecord> materials)
    {
        var result = new List<MaterialDefinitionRecord>();
        foreach (var flat in materials.Select(CloneAndNormalizeFlatMaterial))
        {
            var materialName = string.IsNullOrWhiteSpace(flat.BaseMaterialName) ? flat.Name : flat.BaseMaterialName;
            var materialId = string.IsNullOrWhiteSpace(flat.BaseMaterialId) ? MakeMaterialId(materialName) : flat.BaseMaterialId;
            var material = result.FirstOrDefault(m => string.Equals(m.Id, materialId, StringComparison.OrdinalIgnoreCase));
            if (material is null)
            {
                material = new MaterialDefinitionRecord
                {
                    Id = materialId,
                    Name = materialName,
                    Category = flat.Properties.TryGetValue("materialCategory", out var category) ? category : string.Empty,
                    DensityLbPerCubicInch = flat.DensityLbPerCubicInch
                };
                result.Add(material);
            }
            else if (material.DensityLbPerCubicInch <= 0 && flat.DensityLbPerCubicInch > 0)
            {
                material.DensityLbPerCubicInch = flat.DensityLbPerCubicInch;
            }

            var shape = new MaterialShapeRecord
            {
                Id = string.IsNullOrWhiteSpace(flat.ShapeId) ? flat.Id : flat.ShapeId,
                Name = string.IsNullOrWhiteSpace(flat.ShapeName) || string.Equals(flat.ShapeName, flat.Name, StringComparison.OrdinalIgnoreCase)
                    ? BuildShapeName(flat.Category, flat.Thickness, flat.SheetWidth, flat.SheetHeight, flat.Unit)
                    : flat.ShapeName,
                ShapeType = string.IsNullOrWhiteSpace(flat.ShapeType) ? flat.Category : flat.ShapeType,
                Thickness = flat.Thickness,
                Unit = flat.Unit,
                SheetWidth = flat.SheetWidth,
                SheetHeight = flat.SheetHeight,
                StockLength = flat.StockLength,
                Width = flat.Width,
                Height = flat.Height,
                Diameter = flat.Diameter,
                WallThickness = flat.WallThickness,
                NestingEfficiency = flat.NestingEfficiency,
                PricePerUnit = flat.PricePerUnit,
                PriceUnit = flat.PriceUnit,
                Properties = new Dictionary<string, string>(flat.Properties, StringComparer.OrdinalIgnoreCase)
            };
            UpsertShape(material.Shapes, CloneAndNormalizeShape(shape, material.Id));
        }

        return result;
    }

    private static List<MaterialDefinitionRecord> LoadJson(string filepath)
    {
        var json = File.ReadAllText(filepath);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (root.ValueKind == JsonValueKind.Object)
        {
            var materialsProperty = root
                .EnumerateObject()
                .FirstOrDefault(property => string.Equals(property.Name, "materials", StringComparison.OrdinalIgnoreCase));
            if (materialsProperty.Value.ValueKind != JsonValueKind.Undefined)
                root = materialsProperty.Value;
        }

        if (root.ValueKind != JsonValueKind.Array)
            return new List<MaterialDefinitionRecord>();

        var firstObject = root.EnumerateArray().FirstOrDefault(element => element.ValueKind == JsonValueKind.Object);
        var hasShapes = firstObject.ValueKind == JsonValueKind.Object
            && firstObject.EnumerateObject().Any(property => string.Equals(property.Name, "shapes", StringComparison.OrdinalIgnoreCase));

        if (hasShapes)
        {
            return JsonSerializer.Deserialize<List<MaterialDefinitionRecord>>(root.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? new List<MaterialDefinitionRecord>();
        }

        var flat = JsonSerializer.Deserialize<List<MaterialRecord>>(root.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? new List<MaterialRecord>();
        return ConvertFlatMaterials(flat);
    }

    private static List<MaterialDefinitionRecord> LoadCsv(string filepath)
    {
        var lines = File.ReadAllLines(filepath).Where(line => !string.IsNullOrWhiteSpace(line)).ToList();
        if (lines.Count < 2)
            return new List<MaterialDefinitionRecord>();

        var headers = SplitCsvLine(lines[0]).Select(NormalizeHeader).ToList();
        var result = new List<MaterialDefinitionRecord>();
        foreach (var line in lines.Skip(1))
        {
            var cells = SplitCsvLine(line);
            string Cell(params string[] names)
            {
                foreach (var name in names.Select(NormalizeHeader))
                {
                    var index = headers.IndexOf(name);
                    if (index >= 0 && index < cells.Count && !string.IsNullOrWhiteSpace(cells[index]))
                        return cells[index].Trim();
                }

                return string.Empty;
            }

            var legacyName = Cell("name");
            var materialName = Cell("materialname", "material", "basematerial", "base");
            if (string.IsNullOrWhiteSpace(materialName))
                materialName = legacyName;

            var materialId = Cell("materialid", "basematerialid");
            if (string.IsNullOrWhiteSpace(materialId))
                materialId = MakeMaterialId(materialName);

            var material = result.FirstOrDefault(m => string.Equals(m.Id, materialId, StringComparison.OrdinalIgnoreCase));
            if (material is null)
            {
                material = new MaterialDefinitionRecord
                {
                    Id = materialId,
                    Name = materialName,
                    Category = Cell("materialcategory", "family"),
                    Description = Cell("description", "materialdescription", "notes")
                };
                if (double.TryParse(Cell("densitylbpercubicinch", "density", "densitylbcuin", "densitylbincubed"), NumberStyles.Float, CultureInfo.InvariantCulture, out var density))
                    material.DensityLbPerCubicInch = density;
                result.Add(material);
            }
            else if (material.DensityLbPerCubicInch <= 0
                && double.TryParse(Cell("densitylbpercubicinch", "density", "densitylbcuin", "densitylbincubed"), NumberStyles.Float, CultureInfo.InvariantCulture, out var density))
            {
                material.DensityLbPerCubicInch = density;
            }

            var shapeType = Cell("shapetype", "stocktype", "category");
            var shapeName = Cell("shapename", "shape", "stockname");
            var unit = string.IsNullOrWhiteSpace(Cell("unit")) ? "in" : Cell("unit");
            var shape = new MaterialShapeRecord
            {
                Id = Cell("shapeid", "stockid", "id"),
                Name = shapeName,
                ShapeType = string.IsNullOrWhiteSpace(shapeType) ? "sheetgood" : shapeType,
                Unit = unit
            };

            if (double.TryParse(Cell("thickness"), NumberStyles.Float, CultureInfo.InvariantCulture, out var thickness))
                shape.Thickness = thickness;
            if (double.TryParse(Cell("sheetwidth"), NumberStyles.Float, CultureInfo.InvariantCulture, out var sheetWidth))
                shape.SheetWidth = sheetWidth;
            if (double.TryParse(Cell("sheetheight"), NumberStyles.Float, CultureInfo.InvariantCulture, out var sheetHeight))
                shape.SheetHeight = sheetHeight;
            if (TryParseSheetSize(Cell("sheetsize", "sheet"), out var parsedWidth, out var parsedHeight))
            {
                shape.SheetWidth = parsedWidth;
                shape.SheetHeight = parsedHeight;
            }
            if (double.TryParse(Cell("length", "stocklength"), NumberStyles.Float, CultureInfo.InvariantCulture, out var length))
                shape.StockLength = length;
            if (double.TryParse(Cell("width", "actualwidth", "usablewidth", "stockwidth"), NumberStyles.Float, CultureInfo.InvariantCulture, out var width))
                shape.Width = width;
            if (double.TryParse(Cell("height", "actualheight", "usableheight", "stockheight"), NumberStyles.Float, CultureInfo.InvariantCulture, out var height))
                shape.Height = height;
            if (double.TryParse(Cell("diameter", "od"), NumberStyles.Float, CultureInfo.InvariantCulture, out var diameter))
                shape.Diameter = diameter;
            if (double.TryParse(Cell("wallthickness", "wall"), NumberStyles.Float, CultureInfo.InvariantCulture, out var wall))
                shape.WallThickness = wall;
            if (double.TryParse(Cell("nestingefficiency"), NumberStyles.Float, CultureInfo.InvariantCulture, out var efficiency))
                shape.NestingEfficiency = efficiency;
            if (double.TryParse(Cell("priceperunit", "unitprice", "price", "cost", "costperunit"), NumberStyles.Float, CultureInfo.InvariantCulture, out var price))
                shape.PricePerUnit = price;
            shape.PriceUnit = Cell("priceunit", "pricingunit", "costunit");

            if (string.IsNullOrWhiteSpace(shape.Name))
                shape.Name = BuildShapeName(shape.ShapeType, shape.Thickness, shape.SheetWidth, shape.SheetHeight, shape.Unit);

            UpsertShape(material.Shapes, CloneAndNormalizeShape(shape, material.Id));
        }

        return result;
    }

    private static void ExportJson(string filepath, IReadOnlyList<MaterialDefinitionRecord> materials)
    {
        var json = JsonSerializer.Serialize(
            new { Materials = materials },
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(filepath, json);
    }

    private static void ExportCsv(string filepath, IReadOnlyList<MaterialDefinitionRecord> materials)
    {
        var builder = new StringBuilder();
        builder.AppendLine("material_id,material_name,material_category,description,density_lb_per_cubic_inch,shape_id,shape_name,shape_type,thickness,unit,sheet_width,sheet_height,sheetsize,stock_length,width,height,diameter,wall_thickness,nesting_efficiency,price_per_unit,price_unit");

        foreach (var material in materials)
        {
            foreach (var shape in material.Shapes)
            {
                var cells = new[]
                {
                    material.Id,
                    material.Name,
                    material.Category,
                    material.Description,
                    FormatNumber(material.DensityLbPerCubicInch),
                    shape.Id,
                    shape.Name,
                    shape.ShapeType,
                    FormatNumber(shape.Thickness),
                    shape.Unit,
                    FormatNumber(shape.SheetWidth),
                    FormatNumber(shape.SheetHeight),
                    FormatSheetSize(shape),
                    FormatNumber(shape.StockLength),
                    FormatNumber(shape.Width),
                    FormatNumber(shape.Height),
                    FormatNumber(shape.Diameter),
                    FormatNumber(shape.WallThickness),
                    FormatNumber(shape.NestingEfficiency),
                    FormatNumber(shape.PricePerUnit),
                    shape.PriceUnit
                };
                builder.AppendLine(string.Join(",", cells.Select(EscapeCsv)));
            }

        }

        File.WriteAllText(filepath, builder.ToString());
    }

    private static string FormatNumber(double value)
    {
        return value > 0 ? value.ToString("0.###", CultureInfo.InvariantCulture) : string.Empty;
    }

    private static string EscapeCsv(string value)
    {
        value ??= string.Empty;
        return value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
    }

    private static MaterialDefinitionRecord CloneAndNormalizeDefinition(MaterialDefinitionRecord material)
    {
        var clone = new MaterialDefinitionRecord
        {
            Id = material.Id,
            Name = material.Name,
            Category = material.Category,
            Description = material.Description,
            DensityLbPerCubicInch = material.DensityLbPerCubicInch,
            Properties = new Dictionary<string, string>(material.Properties, StringComparer.OrdinalIgnoreCase),
            Shapes = material.Shapes.Select(shape => CloneAndNormalizeShape(shape, material.Id)).ToList()
        };

        if (string.IsNullOrWhiteSpace(clone.Id))
            clone.Id = MakeMaterialId(clone.Name);
        if (string.IsNullOrWhiteSpace(clone.Name))
            clone.Name = clone.Id;
        if (clone.DensityLbPerCubicInch < 0)
            clone.DensityLbPerCubicInch = 0;

        return clone;
    }

    private static MaterialShapeRecord CloneAndNormalizeShape(MaterialShapeRecord shape, string materialId = "")
    {
        var clone = new MaterialShapeRecord
        {
            Id = shape.Id,
            Name = shape.Name,
            ShapeType = string.IsNullOrWhiteSpace(shape.ShapeType) ? "sheetgood" : shape.ShapeType,
            Thickness = shape.Thickness,
            Unit = string.IsNullOrWhiteSpace(shape.Unit) ? "in" : shape.Unit,
            SheetWidth = shape.SheetWidth,
            SheetHeight = shape.SheetHeight,
            StockLength = shape.StockLength,
            Width = shape.Width,
            Height = shape.Height,
            Diameter = shape.Diameter,
            WallThickness = shape.WallThickness,
            NestingEfficiency = shape.NestingEfficiency,
            PricePerUnit = shape.PricePerUnit,
            PriceUnit = shape.PriceUnit,
            Properties = new Dictionary<string, string>(shape.Properties, StringComparer.OrdinalIgnoreCase)
        };

        if (IsSheetLike(clone.ShapeType))
        {
            if (clone.Width > 0 && clone.Height > 0)
            {
                clone.SheetWidth = clone.Width;
                clone.SheetHeight = clone.Height;
            }
            if (clone.SheetWidth <= 0)
                clone.SheetWidth = 48.0;
            if (clone.SheetHeight <= 0)
                clone.SheetHeight = 96.0;
            if (clone.Width <= 0)
                clone.Width = clone.SheetWidth;
            if (clone.Height <= 0)
                clone.Height = clone.SheetHeight;
        }

        if (clone.NestingEfficiency <= 0 || clone.NestingEfficiency > 1.0)
            clone.NestingEfficiency = 0.8;
        if (clone.PricePerUnit < 0)
            clone.PricePerUnit = 0;
        if (string.IsNullOrWhiteSpace(clone.Name))
            clone.Name = BuildShapeName(clone.ShapeType, clone.Thickness, clone.SheetWidth, clone.SheetHeight, clone.Unit);
        if (string.IsNullOrWhiteSpace(clone.Id))
            clone.Id = MakeShapeId(materialId, clone.Name);

        return clone;
    }

    private static MaterialRecord CloneAndNormalizeFlatMaterial(MaterialRecord material)
    {
        var clone = new MaterialRecord
        {
            Id = material.Id,
            Name = material.Name,
            Category = material.Category,
            BaseMaterialId = material.BaseMaterialId,
            BaseMaterialName = material.BaseMaterialName,
            DensityLbPerCubicInch = material.DensityLbPerCubicInch,
            ShapeId = material.ShapeId,
            ShapeName = material.ShapeName,
            ShapeType = material.ShapeType,
            Thickness = material.Thickness,
            Unit = string.IsNullOrWhiteSpace(material.Unit) ? "in" : material.Unit,
            SheetWidth = material.SheetWidth,
            SheetHeight = material.SheetHeight,
            StockLength = material.StockLength,
            Width = material.Width,
            Height = material.Height,
            Diameter = material.Diameter,
            WallThickness = material.WallThickness,
            NestingEfficiency = material.NestingEfficiency,
            PricePerUnit = material.PricePerUnit,
            PriceUnit = material.PriceUnit,
            Properties = new Dictionary<string, string>(material.Properties, StringComparer.OrdinalIgnoreCase)
        };

        if (string.IsNullOrWhiteSpace(clone.Id))
            clone.Id = MakeMaterialId(clone.Name);
        if (string.IsNullOrWhiteSpace(clone.Name))
            clone.Name = clone.Id;
        if (string.IsNullOrWhiteSpace(clone.Category))
            clone.Category = "sheetgood";
        if (string.IsNullOrWhiteSpace(clone.ShapeType))
            clone.ShapeType = clone.Category;
        if (clone.SheetWidth <= 0)
            clone.SheetWidth = 48.0;
        if (clone.SheetHeight <= 0)
            clone.SheetHeight = 96.0;
        if (IsSheetLike(clone.ShapeType))
        {
            if (clone.Width > 0 && clone.Height > 0)
            {
                clone.SheetWidth = clone.Width;
                clone.SheetHeight = clone.Height;
            }
            if (clone.Width <= 0)
                clone.Width = clone.SheetWidth;
            if (clone.Height <= 0)
                clone.Height = clone.SheetHeight;
        }
        if (clone.NestingEfficiency <= 0 || clone.NestingEfficiency > 1.0)
            clone.NestingEfficiency = 0.8;
        if (clone.PricePerUnit < 0)
            clone.PricePerUnit = 0;

        return clone;
    }

    private static void MergeDefinition(List<MaterialDefinitionRecord> materials, MaterialDefinitionRecord material)
    {
        var existingIndex = FindDefinitionIndex(materials, material);
        if (existingIndex < 0)
        {
            materials.Add(material);
            return;
        }

        var existing = materials[existingIndex];
        existing.Name = material.Name;
        existing.Category = material.Category;
        existing.Description = material.Description;
        existing.DensityLbPerCubicInch = material.DensityLbPerCubicInch;
        existing.Properties = new Dictionary<string, string>(material.Properties, StringComparer.OrdinalIgnoreCase);
        foreach (var shape in material.Shapes)
        {
            var normalizedShape = CloneAndNormalizeShape(shape, existing.Id);
            var existingShapeIndex = FindShapeIndex(existing.Shapes, normalizedShape);
            if (existingShapeIndex >= 0)
                normalizedShape.Id = existing.Shapes[existingShapeIndex].Id;

            UpsertShape(existing.Shapes, normalizedShape);
        }
    }

    private static void UpsertDefinition(List<MaterialDefinitionRecord> materials, MaterialDefinitionRecord material)
    {
        var existing = FindDefinitionIndex(materials, material);
        if (existing >= 0)
            materials[existing] = material;
        else
            materials.Add(material);
    }

    private static void UpsertShape(List<MaterialShapeRecord> shapes, MaterialShapeRecord shape)
    {
        var existing = FindShapeIndex(shapes, shape);
        if (existing >= 0)
        {
            if (string.IsNullOrWhiteSpace(shape.Id))
                shape.Id = shapes[existing].Id;

            shapes[existing] = shape;
        }
        else
        {
            shapes.Add(shape);
        }
    }

    private static int FindDefinitionIndex(List<MaterialDefinitionRecord> materials, MaterialDefinitionRecord material)
    {
        var byId = materials.FindIndex(m => string.Equals(m.Id, material.Id, StringComparison.OrdinalIgnoreCase));
        if (byId >= 0)
            return byId;

        return materials.FindIndex(m => string.Equals(NormalizeName(m.Name), NormalizeName(material.Name), StringComparison.OrdinalIgnoreCase));
    }

    private static int FindShapeIndex(List<MaterialShapeRecord> shapes, MaterialShapeRecord shape)
    {
        var byId = shapes.FindIndex(s => string.Equals(s.Id, shape.Id, StringComparison.OrdinalIgnoreCase));
        if (byId >= 0)
            return byId;

        return shapes.FindIndex(s => string.Equals(NormalizeName(s.Name), NormalizeName(shape.Name), StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeName(string value)
    {
        return string.Join(" ", (value ?? string.Empty).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static void UpsertFlatMaterial(List<MaterialRecord> materials, MaterialRecord material)
    {
        var existing = materials.FindIndex(m => string.Equals(m.Id, material.Id, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0)
            materials[existing] = material;
        else
            materials.Add(material);
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

    private static string NormalizeMaterialIdForLookup(string materialId)
    {
        if (!materialId.StartsWith("AMMAT|", StringComparison.OrdinalIgnoreCase))
            return materialId;

        var parts = materialId.Split('|');
        return parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]) ? parts[1] : materialId;
    }

    private static List<MaterialRecord> FindThicknessMatches(IEnumerable<MaterialRecord> materials, double requiredThickness)
    {
        if (requiredThickness <= 0)
            return new List<MaterialRecord>();

        const double tolerance = 0.01;
        return materials
            .Where(material => material.Thickness > 0 && Math.Abs(material.Thickness - requiredThickness) <= tolerance)
            .ToList();
    }

    private static double ActualSheetArea(MaterialRecord material)
    {
        return ActualSheetWidth(material) * ActualSheetHeight(material);
    }

    private static double ActualSheetWidth(MaterialRecord material)
    {
        return material.Width > 0 ? material.Width : material.SheetWidth;
    }

    private static double ActualSheetHeight(MaterialRecord material)
    {
        return material.Height > 0 ? material.Height : material.SheetHeight;
    }

    private static string MakeShapeId(string materialId, string shapeName)
    {
        var prefix = string.IsNullOrWhiteSpace(materialId) ? string.Empty : MakeMaterialId(materialId) + "_";
        return prefix + MakeMaterialId(shapeName);
    }

    private static string BuildShapeName(string shapeType, double thickness, double sheetWidth, double sheetHeight, string unit)
    {
        var parts = new List<string>();
        if (thickness > 0)
            parts.Add($"{thickness:0.###} {unit}");
        if (!string.IsNullOrWhiteSpace(shapeType))
            parts.Add(shapeType);
        if (sheetWidth > 0 && sheetHeight > 0)
            parts.Add($"{sheetWidth:0.###}x{sheetHeight:0.###}");

        return parts.Count == 0 ? "Stock Shape" : string.Join(" ", parts);
    }

    private static bool IsSheetLike(string shapeType)
    {
        return shapeType.Contains("sheet", StringComparison.OrdinalIgnoreCase)
            || shapeType.Contains("plate", StringComparison.OrdinalIgnoreCase)
            || shapeType.Contains("panel", StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> SplitCsvLine(string line)
    {
        var cells = new List<string>();
        var current = new List<char>();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Add('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == ',' && !inQuotes)
            {
                cells.Add(new string(current.ToArray()).Trim());
                current.Clear();
            }
            else
            {
                current.Add(c);
            }
        }

        cells.Add(new string(current.ToArray()).Trim());
        return cells;
    }

    private static string NormalizeHeader(string value)
    {
        return new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
    }
}
