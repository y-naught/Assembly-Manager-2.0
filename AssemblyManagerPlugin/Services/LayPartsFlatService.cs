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
    private readonly AssemblyLineageService _lineage;
    private readonly ReferenceUpdateService _referenceUpdates;

    public LayPartsFlatService(
        AssemblyRepository repository,
        LayerService layers,
        GeometryFingerprintService fingerprints,
        IMaterialLibrary materials,
        PluginSettingsService settings,
        IActionHistorySink history,
        AssemblyLineageService lineage,
        ReferenceUpdateService referenceUpdates)
    {
        _repository = repository;
        _layers = layers;
        _fingerprints = fingerprints;
        _materials = materials;
        _settings = settings;
        _history = history;
        _lineage = lineage;
        _referenceUpdates = referenceUpdates;
    }

    public int LayPartsFlat(RhinoDoc doc, string assemblyName)
    {
        using var mutation = AssemblyLinkMutationGate.Enter();
        var store = _repository.Load(doc);
        var assembly = store.FindAssembly(assemblyName)
            ?? throw new InvalidOperationException($"Assembly '{assemblyName}' was not found.");

        _layers.EnsureRootLayers(doc);
        _layers.EnsureLayer(doc, LayerService.PartsAssembly(assemblyName));
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
        var outputsRequiringCascade = new HashSet<Guid>();
        var successfulRowHeaders = new List<RowHeaderItem>();

        foreach (var part in assembly.Parts.OrderBy(p => p.Name, PartNameComparer.Instance))
        {
            var sourceId = part.GeneratedObjectIds.FirstOrDefault(id => doc.Objects.FindId(id) is not null);
            if (sourceId == Guid.Empty)
                continue;

            var sourceObject = doc.Objects.FindId(sourceId);
            if (sourceObject is null || !_fingerprints.TryDuplicateBrep(sourceObject, out var brep))
                continue;

            var geometry = brep.DuplicateBrep();
            var orientTransform = TransformUtilities.OrientLargestFaceToWorldXY(geometry, _fingerprints, Point3d.Origin);
            geometry.Transform(orientTransform);
            var longAxisRotation = TransformUtilities.RotateLongDimensionToY(geometry);
            var sourceToPrepared = longAxisRotation * orientTransform;

            var bbox = geometry.GetBoundingBox(true);
            if (!bbox.IsValid)
                continue;

            var thickness = _fingerprints.GetMaterialThickness(brep);
            part.MaterialThickness = thickness;
            if (string.IsNullOrWhiteSpace(part.MaterialId))
                part.MaterialId = MaterialAssignment.GetCategorizationMaterialId(sourceObject.Attributes);

            var materialLabel = GetMaterialLabel(doc, part, sourceObject);
            preparedParts.Add(new LayFlatPartItem(
                part,
                sourceId,
                sourceObject,
                geometry,
                bbox,
                thickness,
                materialLabel,
                sourceToPrepared));
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
            var rowSucceeded = false;
            var rowHeaderAnchor = new Point3d(
                startX,
                partBottomY + rowHeight + rowHeaderOffset,
                0.0);

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
                var cam3dLayer = $"{LayerService.PartsPart(assemblyName, part.Name)}::3D";
                var partColor = _layers.FindPartLayerColor(doc, assemblyName, part.Name) ??
                                LayerService.PartColorForName(part.Name, pluginSettings.AssemblyManager.ColorizeParts);
                _layers.EnsurePartLayerIndex(doc, LayerService.PartsPart(assemblyName, part.Name), partColor);
                var camLayerIndex = _layers.EnsurePartLayerIndex(doc, cam3dLayer, partColor);
                var attributes = item.SourceObject.Attributes.Duplicate();
                attributes.LayerIndex = camLayerIndex;
                attributes.Name = part.Name;
                attributes.RemoveFromAllGroups();
                MaterialAssignment.NormalizeToParentMaterial(attributes);
                AssemblyLineageService.ClearLinkMetadata(attributes);
                if (!TryCreateOrReplaceFlatOutput(
                        doc,
                        assembly,
                        part,
                        geometry,
                        attributes,
                        out var camId,
                        out var replacedExisting))
                {
                    continue;
                }

                part.CamObjectIds.Clear();
                part.CamObjectIds.Add(camId);
                _lineage.RegisterDerived(
                    doc,
                    assembly,
                    item.SourceObjectId,
                    AssemblyLinkRoles.OriginalAssembly,
                    camId,
                    AssemblyLinkRoles.FlatPart,
                    placement * item.SourceToPreparedTransform,
                    AssemblyLinkRecipes.LayFlat,
                    partId: part.Id,
                    recipeMetadata: new Dictionary<string, string>
                    {
                        ["orientation"] = "LargestFaceToWorldXY",
                        ["longAxis"] = "Y",
                        ["layout"] = "MaterialThicknessRow",
                        [AssemblyLinkMetadataKeys.UserPlanRotationOverride] = bool.FalseString
                    });
                if (replacedExisting)
                    outputsRequiringCascade.Add(camId);

                var partLabelLayer = $"{LayerService.PartsPart(assemblyName, part.Name)}::text";
                var partLabelId = AddPartText(
                    doc,
                    assemblyName,
                    part,
                    new Point3d(bbox.Center.X, labelTopY, 0.0),
                    item.Thickness,
                    item.MaterialLabel,
                    textHeight,
                    assembly.Id,
                    camId);
                if (partLabelId != Guid.Empty)
                {
                    DeleteTextEntitiesInLayer(
                        doc,
                        partLabelLayer,
                        new HashSet<Guid> { partLabelId },
                        assembly.Id,
                        legacyPartName: part.Name);
                }

                laidFlatCount++;
                rowSucceeded = true;
                xCursor = bbox.Max.X + columnPadding;
            }

            if (rowSucceeded)
            {
                successfulRowHeaders.Add(new RowHeaderItem(
                    rowItems[0].MaterialLabel,
                    rowItems[0].Thickness,
                    rowHeaderAnchor));
            }

            yCursor = partBottomY + rowHeight + rowHeaderOffset + rowPadding;
        }

        RefreshRowHeaders(
            doc,
            assembly,
            successfulRowHeaders,
            textHeight,
            allPartsRebuilt: preparedParts.Count == assembly.Parts.Count &&
                             laidFlatCount == preparedParts.Count);

        assembly.UpdatedAt = DateTimeOffset.UtcNow;
        _repository.Save(doc, store);
        foreach (var sourceObjectId in outputsRequiringCascade)
        {
            try
            {
                _referenceUpdates.RefreshDescendantsFromSource(doc, sourceObjectId);
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine(
                    "Gazelle replaced flat output {0}, but could not cascade that change into a downstream assembly: {1}",
                    sourceObjectId,
                    ex.Message);
            }
        }

        _history.Record(doc, new ActionHistoryEntry
        {
            CommandName = "LayPartsFlat",
            AssemblyName = assemblyName,
            Summary = $"Laid flat {laidFlatCount} unique part(s)."
        });

        doc.Views.Redraw();
        return laidFlatCount;
    }

    private bool TryCreateOrReplaceFlatOutput(
        RhinoDoc doc,
        AssemblyRecord assembly,
        PartRecord part,
        Brep geometry,
        ObjectAttributes attributes,
        out Guid outputId,
        out bool replacedExisting)
    {
        outputId = Guid.Empty;
        replacedExisting = false;
        var priorIds = part.CamObjectIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        var livePriorIds = priorIds
            .Where(id => doc.Objects.FindId(id) is { IsDeleted: false })
            .ToList();
        if (livePriorIds.Count > 1)
        {
            RhinoApp.WriteLine(
                "Gazelle found {0} live flat outputs for {1}; it preserved them because choosing one replacement UUID would be ambiguous.",
                livePriorIds.Count,
                part.Name);
            return false;
        }

        if (livePriorIds.Count == 1)
        {
            outputId = livePriorIds[0];
            if (!doc.Objects.Replace(outputId, geometry))
            {
                RhinoApp.WriteLine(
                    "Gazelle could not replace the prior flat output for {0}; the existing linked object was preserved.",
                    part.Name);
                outputId = Guid.Empty;
                return false;
            }

            if (!doc.Objects.ModifyAttributes(outputId, attributes, true))
            {
                RhinoApp.WriteLine(
                    "Gazelle replaced the flat geometry for {0}, but Rhino kept its prior display attributes.",
                    part.Name);
            }

            replacedExisting = true;
        }
        else
        {
            outputId = doc.Objects.Add(geometry, attributes);
            if (outputId == Guid.Empty)
                return false;
        }

        var retainedOutputId = outputId;
        var obsoleteIds = priorIds.Where(id => id != retainedOutputId).ToHashSet();
        _lineage.DetachDerivedObjects(doc, assembly, obsoleteIds);
        return true;
    }

    private void RefreshRowHeaders(
        RhinoDoc doc,
        AssemblyRecord assembly,
        IReadOnlyList<RowHeaderItem> rowHeaders,
        double textHeight,
        bool allPartsRebuilt)
    {
        if (rowHeaders.Count == 0)
            return;

        var newHeaderIds = new HashSet<Guid>();
        var createdHeaders = new List<(Guid Id, string Text)>();
        foreach (var header in rowHeaders)
        {
            var id = AddRowHeaderText(
                doc,
                assembly.Name,
                header.MaterialLabel,
                header.Thickness,
                header.Anchor,
                textHeight,
                assembly.Id);
            if (id == Guid.Empty)
                continue;

            newHeaderIds.Add(id);
            createdHeaders.Add((id, FormatRowHeaderText(header.MaterialLabel, header.Thickness)));
        }

        var rowLabelLayer = $"{LayerService.PartsAssembly(assembly.Name)}::row labels";
        if (allPartsRebuilt && createdHeaders.Count == rowHeaders.Count)
        {
            DeleteTextEntitiesInLayer(doc, rowLabelLayer, newHeaderIds, assembly.Id);
            return;
        }

        foreach (var created in createdHeaders)
        {
            DeleteTextEntitiesInLayer(
                doc,
                rowLabelLayer,
                newHeaderIds,
                assembly.Id,
                created.Text);
        }
    }

    private void DeleteTextEntitiesInLayer(
        RhinoDoc doc,
        string layer,
        ISet<Guid> retainedIds,
        Guid assemblyId,
        string? matchingText = null,
        string? legacyPartName = null)
    {
        foreach (var objectId in _layers.GetObjectIdsInLayerTree(doc, layer))
        {
            if (retainedIds.Contains(objectId) ||
                doc.Objects.FindId(objectId) is not { Geometry: TextEntity text } obj ||
                !string.Equals(doc.Layers[obj.Attributes.LayerIndex].FullPath, layer, StringComparison.OrdinalIgnoreCase) ||
                (matchingText is not null &&
                 !string.Equals(text.PlainText, matchingText, StringComparison.Ordinal)))
            {
                continue;
            }

            var owned = FlatPartAnnotations.IsOwned(obj.Attributes, assemblyId,
                legacyPartName is null ? FlatPartAnnotations.RowHeader : FlatPartAnnotations.PartLabel);
            var legacy = string.IsNullOrWhiteSpace(obj.Attributes.GetUserString(FlatPartAnnotations.KindKey)) &&
                         (legacyPartName is null ? FlatPartAnnotations.IsLegacyRowText(text.PlainText) :
                             FlatPartAnnotations.IsLegacyPartText(text.PlainText, legacyPartName));
            if (!owned && !legacy)
                continue;

            doc.Objects.Delete(objectId, true);
        }
    }

    private Guid AddPartText(
        RhinoDoc doc,
        string assemblyName,
        PartRecord part,
        Point3d anchor,
        double thickness,
        string materialLabel,
        double textHeight,
        Guid assemblyId,
        Guid outputId)
    {
        var layer = $"{LayerService.PartsPart(assemblyName, part.Name)}::text";
        var layerIndex = _layers.EnsureLayerIndex(doc, layer, System.Drawing.Color.Black);
        var text = FlatPartAnnotations.FormatPartText(part, thickness, materialLabel);
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
        FlatPartAnnotations.Attach(attributes, assemblyId, part.Id, outputId, FlatPartAnnotations.PartLabel);
        return doc.Objects.AddText(entity, attributes);
    }

    private Guid AddRowHeaderText(
        RhinoDoc doc,
        string assemblyName,
        string materialLabel,
        double thickness,
        Point3d anchor,
        double textHeight,
        Guid assemblyId)
    {
        var layer = $"{LayerService.PartsAssembly(assemblyName)}::row labels";
        var layerIndex = _layers.EnsureLayerIndex(doc, layer, System.Drawing.Color.Black);
        var text = FormatRowHeaderText(materialLabel, thickness);
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
        FlatPartAnnotations.Attach(attributes, assemblyId, Guid.Empty, Guid.Empty, FlatPartAnnotations.RowHeader);
        return doc.Objects.AddText(entity, attributes);
    }

    private static string FormatRowHeaderText(string materialLabel, double thickness)
    {
        var label = string.IsNullOrWhiteSpace(materialLabel) ? "TBD" : materialLabel;
        return $"{label} | {Math.Round(thickness, 3):0.###}\"";
    }

    private string GetMaterialLabel(RhinoDoc doc, PartRecord part, RhinoObject sourceObject)
    {
        if (!string.IsNullOrWhiteSpace(part.MaterialId))
            return _materials.GetMaterialLabel(doc, part.MaterialId);

        var objectMaterial = MaterialAssignment.GetDisplayName(sourceObject.Attributes);
        return string.IsNullOrWhiteSpace(objectMaterial) ? "TBD" : objectMaterial;
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
    private readonly record struct RowHeaderItem(
        string MaterialLabel,
        double Thickness,
        Point3d Anchor);

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
            string materialLabel,
            Transform sourceToPreparedTransform)
        {
            Part = part;
            SourceObjectId = sourceObjectId;
            SourceObject = sourceObject;
            Geometry = geometry;
            Bounds = bounds;
            Thickness = thickness;
            MaterialLabel = string.IsNullOrWhiteSpace(materialLabel) ? "TBD" : materialLabel;
            SourceToPreparedTransform = sourceToPreparedTransform;
            _thicknessKey = Math.Round(thickness, 3, MidpointRounding.AwayFromZero);
        }

        public PartRecord Part { get; }
        public Guid SourceObjectId { get; }
        public RhinoObject SourceObject { get; }
        public Brep Geometry { get; }
        public BoundingBox Bounds { get; }
        public double Thickness { get; }
        public string MaterialLabel { get; }
        public Transform SourceToPreparedTransform { get; }
        public double Height => Math.Abs(Bounds.Max.Y - Bounds.Min.Y);
        public string GroupKey => $"{MaterialLabel}|{_thicknessKey:0.###}";
    }
}
