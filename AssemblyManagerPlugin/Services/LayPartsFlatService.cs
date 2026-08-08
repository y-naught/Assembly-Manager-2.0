using AssemblyManagerPlugin.Core;
using AssemblyManagerPlugin.Geometry;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace AssemblyManagerPlugin.Services;

public sealed class LayPartsFlatService
{
    private readonly AssemblyRepository _repository;
    private readonly LayerService _layers;
    private readonly GeometryFingerprintService _fingerprints;
    private readonly IMaterialLibrary _materials;
    private readonly PluginSettingsService _settings;
    private readonly IActionHistorySink _history;

    public LayPartsFlatService(
        AssemblyRepository repository,
        LayerService layers,
        GeometryFingerprintService fingerprints,
        IMaterialLibrary materials,
        PluginSettingsService settings,
        IActionHistorySink history)
    {
        _repository = repository;
        _layers = layers;
        _fingerprints = fingerprints;
        _materials = materials;
        _settings = settings;
        _history = history;
    }

    public int LayPartsFlat(RhinoDoc doc, string assemblyName)
    {
        var store = _repository.Load(doc);
        var assembly = store.FindAssembly(assemblyName)
            ?? throw new InvalidOperationException($"Assembly '{assemblyName}' was not found.");

        _layers.EnsureRootLayers(doc);
        _layers.EnsureLayer(doc, LayerService.CamAssembly(assemblyName));
        var pluginSettings = _settings.Load();
        doc.ModelSpaceAnnotationScalingEnabled = true;
        doc.ModelSpaceTextScale = 12.0;

        const double startX = 200.0;
        var columnPadding = pluginSettings.LayPartsFlat.PartSpacing;
        var rowPadding = Math.Max(columnPadding * 2.0, 24.0);
        const double labelBandHeight = 4.0;
        const double rowHeaderOffset = 2.0;
        const double textHeight = 0.125;
        var yCursor = 4.0;
        var laidFlatCount = 0;
        var preparedParts = new List<LayFlatPartItem>();

        foreach (var part in assembly.Parts.OrderBy(p => p.Name, PartNameComparer.Instance))
        {
            part.CamObjectIds.Clear();
            var sourceId = part.GeneratedObjectIds.FirstOrDefault(id => doc.Objects.FindId(id) is not null);
            if (sourceId == Guid.Empty)
                continue;

            var sourceObject = doc.Objects.FindId(sourceId);
            if (sourceObject is null || !_fingerprints.TryDuplicateBrep(sourceObject, out var brep))
                continue;

            var geometry = brep.DuplicateBrep();
            var orientTransform = TransformUtilities.OrientLargestFaceToWorldXY(geometry, _fingerprints, Point3d.Origin);
            geometry.Transform(orientTransform);
            RotateLongDimensionToY(geometry);

            var bbox = geometry.GetBoundingBox(true);
            if (!bbox.IsValid)
                continue;

            var thickness = _fingerprints.GetMaterialThickness(brep);
            part.MaterialThickness = thickness;
            if (string.IsNullOrWhiteSpace(part.MaterialId))
                part.MaterialId = MaterialAssignment.GetCategorizationMaterialId(sourceObject.Attributes);

            var materialLabel = GetMaterialLabel(doc, part, sourceObject);
            preparedParts.Add(new LayFlatPartItem(part, sourceId, sourceObject, geometry, bbox, thickness, materialLabel));
        }

        var rows = preparedParts
            .GroupBy(item => item.GroupKey, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.First().MaterialLabel, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.First().Thickness)
            .ToList();

        foreach (var row in rows)
        {
            var rowItems = row
                .OrderBy(item => item.Part.Name, PartNameComparer.Instance)
                .ToList();
            var rowHeight = rowItems.Max(item => item.Height);
            var labelTopY = yCursor;
            var partBottomY = labelTopY + labelBandHeight;
            var xCursor = startX;

            AddRowHeaderText(
                doc,
                assemblyName,
                rowItems[0].MaterialLabel,
                rowItems[0].Thickness,
                new Point3d(startX, partBottomY + rowHeight + rowHeaderOffset, 0.0),
                textHeight);

            foreach (var item in rowItems)
            {
                var geometry = item.Geometry.DuplicateBrep();
                var placement = Transform.Translation(
                    xCursor - item.Bounds.Min.X,
                    partBottomY - item.Bounds.Min.Y,
                    -item.Bounds.Min.Z);
                geometry.Transform(placement);
                var bbox = geometry.GetBoundingBox(true);
                if (!bbox.IsValid)
                    continue;

                var part = item.Part;
                var cam3dLayer = $"{LayerService.CamPart(assemblyName, part.Name)}::3D";
                var camLayerIndex = _layers.EnsureLayerIndex(doc, cam3dLayer, LayerColorForPart(part.Name, pluginSettings.AssemblyManager.ColorizeParts));
                var attributes = item.SourceObject.Attributes.Duplicate();
                attributes.LayerIndex = camLayerIndex;
                attributes.Name = part.Name;
                attributes.RemoveFromAllGroups();
                MaterialAssignment.NormalizeToParentMaterial(attributes);
                attributes.SetUserString(AssemblyManagerConstants.SourceObjectUserString, item.SourceObjectId.ToString());
                var camId = doc.Objects.Add(geometry, attributes);
                part.CamObjectIds.Add(camId);

                AddPartText(doc, assemblyName, part, new Point3d(bbox.Center.X, labelTopY, 0.0), item.Thickness, item.MaterialLabel, textHeight);
                laidFlatCount++;
                xCursor = bbox.Max.X + columnPadding;
            }

            yCursor = partBottomY + rowHeight + rowHeaderOffset + rowPadding;
        }

        assembly.UpdatedAt = DateTimeOffset.UtcNow;
        _repository.Save(doc, store);
        _history.Record(doc, new ActionHistoryEntry
        {
            CommandName = "LayPartsFlat",
            AssemblyName = assemblyName,
            Summary = $"Laid flat {laidFlatCount} unique part(s)."
        });

        doc.Views.Redraw();
        return laidFlatCount;
    }

    private void AddPartText(
        RhinoDoc doc,
        string assemblyName,
        PartRecord part,
        Point3d anchor,
        double thickness,
        string materialLabel,
        double textHeight)
    {
        var layer = $"{LayerService.CamPart(assemblyName, part.Name)}::text";
        var layerIndex = _layers.EnsureLayerIndex(doc, layer, System.Drawing.Color.Black);
        var text = $"{part.Name}\nQTY : {part.Quantity}\n{Math.Round(thickness, 3):0.###}\" | {materialLabel}";
        var entity = new TextEntity
        {
            PlainText = text,
            Plane = new Plane(anchor, Vector3d.ZAxis),
            TextHeight = textHeight,
            DimensionScale = 12.0,
            Justification = TextJustification.TopCenter
        };
        var attributes = new ObjectAttributes
        {
            LayerIndex = layerIndex,
            ColorSource = ObjectColorSource.ColorFromObject,
            ObjectColor = System.Drawing.Color.Black
        };
        doc.Objects.AddText(entity, attributes);
    }

    private void AddRowHeaderText(
        RhinoDoc doc,
        string assemblyName,
        string materialLabel,
        double thickness,
        Point3d anchor,
        double textHeight)
    {
        var layer = $"{LayerService.CamAssembly(assemblyName)}::row labels";
        var layerIndex = _layers.EnsureLayerIndex(doc, layer, System.Drawing.Color.Black);
        var label = string.IsNullOrWhiteSpace(materialLabel) ? "TBD" : materialLabel;
        var text = $"{label} | {Math.Round(thickness, 3):0.###}\"";
        var entity = new TextEntity
        {
            PlainText = text,
            Plane = new Plane(anchor, Vector3d.ZAxis),
            TextHeight = textHeight,
            DimensionScale = 12.0,
            Justification = TextJustification.TopLeft
        };
        var attributes = new ObjectAttributes
        {
            LayerIndex = layerIndex,
            ColorSource = ObjectColorSource.ColorFromObject,
            ObjectColor = System.Drawing.Color.Black
        };
        doc.Objects.AddText(entity, attributes);
    }

    private string GetMaterialLabel(RhinoDoc doc, PartRecord part, RhinoObject sourceObject)
    {
        if (!string.IsNullOrWhiteSpace(part.MaterialId))
            return _materials.GetMaterialLabel(doc, part.MaterialId);

        var objectMaterial = MaterialAssignment.GetDisplayName(sourceObject.Attributes);
        return string.IsNullOrWhiteSpace(objectMaterial) ? "TBD" : objectMaterial;
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

    private static System.Drawing.Color LayerColorForPart(string partName, bool colorizeParts)
    {
        if (!colorizeParts)
            return System.Drawing.Color.Black;

        var digits = new string(partName.Where(char.IsDigit).ToArray());
        if (!int.TryParse(digits, out var index))
            index = 1;

        return LayerService.DefaultPartColors[(Math.Max(1, index) - 1) % LayerService.DefaultPartColors.Length];
    }

    private sealed class PartNameComparer : IComparer<string>
    {
        public static readonly PartNameComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            if (string.Equals(x, y, StringComparison.OrdinalIgnoreCase))
                return 0;
            if (string.IsNullOrWhiteSpace(x))
                return -1;
            if (string.IsNullOrWhiteSpace(y))
                return 1;

            var xToken = ParsePartName(x);
            var yToken = ParsePartName(y);
            var prefixCompare = string.Compare(xToken.Prefix, yToken.Prefix, StringComparison.OrdinalIgnoreCase);
            if (prefixCompare != 0)
                return prefixCompare;

            if (xToken.Number.HasValue && yToken.Number.HasValue)
            {
                var numberCompare = xToken.Number.Value.CompareTo(yToken.Number.Value);
                if (numberCompare != 0)
                    return numberCompare;
            }
            else if (xToken.Number.HasValue)
            {
                return -1;
            }
            else if (yToken.Number.HasValue)
            {
                return 1;
            }

            return string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
        }

        private static PartNameToken ParsePartName(string value)
        {
            var trimmed = value.Trim();
            var digitStart = trimmed.Length;
            while (digitStart > 0 && char.IsDigit(trimmed[digitStart - 1]))
                digitStart--;

            var prefix = trimmed[..digitStart];
            if (digitStart < trimmed.Length
                && int.TryParse(trimmed[digitStart..], out var number))
            {
                return new PartNameToken(prefix, number);
            }

            return new PartNameToken(trimmed, null);
        }
    }

    private readonly record struct PartNameToken(string Prefix, int? Number);

    private sealed class LayFlatPartItem
    {
        private readonly double _thicknessKey;

        public LayFlatPartItem(
            PartRecord part,
            Guid sourceObjectId,
            RhinoObject sourceObject,
            Brep geometry,
            BoundingBox bounds,
            double thickness,
            string materialLabel)
        {
            Part = part;
            SourceObjectId = sourceObjectId;
            SourceObject = sourceObject;
            Geometry = geometry;
            Bounds = bounds;
            Thickness = thickness;
            MaterialLabel = string.IsNullOrWhiteSpace(materialLabel) ? "TBD" : materialLabel;
            _thicknessKey = Math.Round(thickness, 3, MidpointRounding.AwayFromZero);
        }

        public PartRecord Part { get; }
        public Guid SourceObjectId { get; }
        public RhinoObject SourceObject { get; }
        public Brep Geometry { get; }
        public BoundingBox Bounds { get; }
        public double Thickness { get; }
        public string MaterialLabel { get; }
        public double Height => Math.Abs(Bounds.Max.Y - Bounds.Min.Y);
        public string GroupKey => $"{MaterialLabel}|{_thicknessKey:0.###}";
    }
}
