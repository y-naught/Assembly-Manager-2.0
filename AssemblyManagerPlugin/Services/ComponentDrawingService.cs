using AssemblyManagerPlugin.Core;
using AssemblyManagerPlugin.Geometry;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace AssemblyManagerPlugin.Services;

public sealed class ComponentDrawingService
{
    private readonly AssemblyRepository _repository;
    private readonly LayerService _layers;
    private readonly IActionHistorySink _history;
    private readonly AssemblyLineageService _lineage;

    public ComponentDrawingService(
        AssemblyRepository repository,
        LayerService layers,
        IActionHistorySink history,
        AssemblyLineageService lineage)
    {
        _repository = repository;
        _layers = layers;
        _history = history;
        _lineage = lineage;
    }

    public int CopyAndOrientComponents(RhinoDoc doc, string assemblyName)
    {
        using var mutation = AssemblyLinkMutationGate.Enter();
        var store = _repository.Load(doc);
        var assembly = store.FindAssembly(assemblyName)
            ?? throw new InvalidOperationException($"Assembly '{assemblyName}' was not found.");

        _layers.EnsureRootLayers(doc);
        _layers.EnsureLayer(doc, LayerService.CopiedComponentsAssembly(assemblyName));

        var copiedComponentCount = 0;
        var rowIndex = 0;
        foreach (var component in assembly.Components.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            var copiedObjects = CopyOneComponentInstance(doc, assemblyName, component);
            if (copiedObjects.Count == 0)
                continue;

            var rowTranslation = MoveToDrawingRow(doc, copiedObjects, rowIndex);
            var planRotation = OptimizePlanRotation(doc, copiedObjects);
            var parentToChild = planRotation * rowTranslation;
            foreach (var copiedObject in copiedObjects)
            {
                var partId = assembly.Parts.FirstOrDefault(part =>
                    string.Equals(part.Name, copiedObject.PartName, StringComparison.OrdinalIgnoreCase))?.Id ?? Guid.Empty;
                _lineage.RegisterDerived(
                    doc,
                    assembly,
                    copiedObject.ParentObjectId,
                    AssemblyLinkRoles.OriginalAssembly,
                    copiedObject.TargetObjectId,
                    AssemblyLinkRoles.CopiedComponent,
                    parentToChild,
                    partId: partId,
                    componentId: component.Id);
            }

            var copiedIds = copiedObjects.Select(item => item.TargetObjectId).ToList();
            CreateGroup(doc, assemblyName, component.Name, copiedIds);
            copiedComponentCount++;
            rowIndex++;
        }

        assembly.UpdatedAt = DateTimeOffset.UtcNow;
        _repository.Save(doc, store);
        _history.Record(doc, new ActionHistoryEntry
        {
            CommandName = "CopyOrientComponents",
            AssemblyName = assemblyName,
            Summary = $"Copied and oriented {copiedComponentCount} component type(s) for drawings."
        });

        doc.Views.Redraw();
        return copiedComponentCount;
    }

    private List<CopiedObject> CopyOneComponentInstance(RhinoDoc doc, string assemblyName, ComponentRecord component)
    {
        var copiedObjects = new List<CopiedObject>();
        if (component.RepresentativeObjectIdsByPartName.Count > 0)
        {
            foreach (var (partName, objectIds) in component.RepresentativeObjectIdsByPartName)
            {
                foreach (var sourceObjectId in objectIds)
                {
                    var sourceObject = doc.Objects.FindId(sourceObjectId);
                    if (sourceObject is null)
                        continue;

                    var targetObjectId = CopyPartObjectToDrawingLayer(
                        doc,
                        assemblyName,
                        component.Name,
                        partName,
                        sourceObject);
                    if (targetObjectId != Guid.Empty)
                        copiedObjects.Add(new CopiedObject(sourceObjectId, targetObjectId, partName));
                }
            }

            return copiedObjects;
        }

        foreach (var partName in component.PartNames)
        {
            var originalLayer = LayerService.OriginalPart(assemblyName, component.Name, partName);
            var layerIndex = _layers.FindLayerIndex(doc, originalLayer);
            if (layerIndex < 0)
                layerIndex = _layers.FindLayerIndex(doc, LayerService.LegacyOriginalPart(assemblyName, component.Name, partName));
            if (layerIndex < 0)
                continue;

            var layer = doc.Layers[layerIndex];
            var sourceObject = doc.Objects.FindByLayer(layer).FirstOrDefault();
            if (sourceObject is null)
                continue;

            var targetObjectId = CopyPartObjectToDrawingLayer(
                doc,
                assemblyName,
                component.Name,
                partName,
                sourceObject);
            if (targetObjectId != Guid.Empty)
                copiedObjects.Add(new CopiedObject(sourceObject.Id, targetObjectId, partName));
        }

        return copiedObjects;
    }

    private Guid CopyPartObjectToDrawingLayer(
        RhinoDoc doc,
        string assemblyName,
        string componentName,
        string partName,
        RhinoObject sourceObject)
    {
        var sourceLayer = doc.Layers[sourceObject.Attributes.LayerIndex];
        var drawingLayer = LayerService.CopiedComponentPart(assemblyName, componentName, partName);
        var color = _layers.FindPartLayerColor(doc, assemblyName, partName) ?? sourceLayer.Color;
        var drawingLayerIndex = _layers.EnsurePartLayerIndex(doc, drawingLayer, color);
        var geometry = sourceObject.Geometry.Duplicate();
        var attributes = sourceObject.Attributes.Duplicate();
        attributes.LayerIndex = drawingLayerIndex;
        attributes.RemoveFromAllGroups();
        AssemblyLineageService.ClearLinkMetadata(attributes);
        return doc.Objects.Add(geometry, attributes);
    }

    private static Transform MoveToDrawingRow(RhinoDoc doc, IReadOnlyList<CopiedObject> copiedObjects, int rowIndex)
    {
        var objectIds = copiedObjects.Select(item => item.TargetObjectId).ToList();
        var bbox = TransformUtilities.GetBoundingBox(objectIds, doc);
        if (!bbox.IsValid)
            return Transform.Identity;

        var target = new Point3d(1200.0 + rowIndex * 200.0, 0.0, 0.0);
        var translation = Transform.Translation(target - bbox.Center);
        ApplyTransform(doc, copiedObjects, translation);

        return translation;
    }

    private static Transform OptimizePlanRotation(RhinoDoc doc, IReadOnlyList<CopiedObject> copiedObjects)
    {
        var objectIds = copiedObjects.Select(item => item.TargetObjectId).ToList();
        var bbox = TransformUtilities.GetBoundingBox(objectIds, doc);
        if (!bbox.IsValid)
            return Transform.Identity;

        var center = bbox.Center;
        var bestAngle = 0.0;
        var bestArea = PlanArea(bbox);

        for (var angle = 15.0; angle < 180.0; angle += 15.0)
        {
            var rotation = Transform.Rotation(RhinoMath.ToRadians(angle), Vector3d.ZAxis, center);
            var testBox = BoundingBox.Empty;
            foreach (var objectId in objectIds)
            {
                var rhinoObject = doc.Objects.FindId(objectId);
                if (rhinoObject is null)
                    continue;

                var objectBox = rhinoObject.Geometry.GetBoundingBox(true);
                objectBox.Transform(rotation);
                testBox.Union(objectBox);
            }

            var area = PlanArea(testBox);
            if (area < bestArea)
            {
                bestArea = area;
                bestAngle = angle;
            }
        }

        if (Math.Abs(bestAngle) < RhinoMath.ZeroTolerance)
            return Transform.Identity;

        var bestRotation = Transform.Rotation(RhinoMath.ToRadians(bestAngle), Vector3d.ZAxis, center);
        ApplyTransform(doc, copiedObjects, bestRotation);

        return bestRotation;
    }

    private static void ApplyTransform(
        RhinoDoc doc,
        IReadOnlyList<CopiedObject> copiedObjects,
        Transform transform)
    {
        foreach (var copiedObject in copiedObjects)
        {
            var transformedId = doc.Objects.Transform(copiedObject.TargetObjectId, transform, true);
            if (transformedId != Guid.Empty)
                copiedObject.TargetObjectId = transformedId;
        }
    }

    private static double PlanArea(BoundingBox bbox)
    {
        if (!bbox.IsValid)
            return double.MaxValue;

        return Math.Abs(bbox.Max.X - bbox.Min.X) * Math.Abs(bbox.Max.Y - bbox.Min.Y);
    }

    private static void CreateGroup(RhinoDoc doc, string assemblyName, string componentName, IEnumerable<Guid> objectIds)
    {
        var groupIndex = doc.Groups.Add(CreateGroupName(assemblyName, componentName));
        if (groupIndex < 0)
            return;

        foreach (var objectId in objectIds)
            doc.Groups.AddToGroup(groupIndex, objectId);
    }

    private static string CreateGroupName(string assemblyName, string componentName)
    {
        const int maximumLength = 50;
        var uniqueSuffix = $"_{Guid.NewGuid():N}"[..13];
        var readableName = $"COPIED_COMPONENTS_{assemblyName}_{componentName}";
        var maximumReadableLength = maximumLength - uniqueSuffix.Length;
        if (readableName.Length > maximumReadableLength)
            readableName = readableName[..maximumReadableLength];

        return readableName + uniqueSuffix;
    }

    private sealed class CopiedObject
    {
        public CopiedObject(Guid parentObjectId, Guid targetObjectId, string partName)
        {
            ParentObjectId = parentObjectId;
            TargetObjectId = targetObjectId;
            PartName = partName;
        }

        public Guid ParentObjectId { get; }
        public Guid TargetObjectId { get; set; }
        public string PartName { get; }
    }
}
