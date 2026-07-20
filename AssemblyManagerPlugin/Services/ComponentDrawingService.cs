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

    public ComponentDrawingService(AssemblyRepository repository, LayerService layers, IActionHistorySink history)
    {
        _repository = repository;
        _layers = layers;
        _history = history;
    }

    public int CopyAndOrientComponents(RhinoDoc doc, string assemblyName)
    {
        var store = _repository.Load(doc);
        var assembly = store.FindAssembly(assemblyName)
            ?? throw new InvalidOperationException($"Assembly '{assemblyName}' was not found.");

        _layers.EnsureRootLayers(doc);
        _layers.EnsureLayer(doc, LayerService.DrawingsAssembly(assemblyName));

        var copiedComponentCount = 0;
        var rowIndex = 0;
        foreach (var component in assembly.Components.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            var copiedIds = CopyOneComponentInstance(doc, assemblyName, component);
            if (copiedIds.Count == 0)
                continue;

            MoveToDrawingRow(doc, copiedIds, rowIndex);
            OptimizePlanRotation(doc, copiedIds);
            CreateGroup(doc, assemblyName, component.Name, copiedIds);
            copiedComponentCount++;
            rowIndex++;
        }

        _history.Record(doc, new ActionHistoryEntry
        {
            CommandName = "CopyOrientComponents",
            AssemblyName = assemblyName,
            Summary = $"Copied and oriented {copiedComponentCount} component type(s) for drawings."
        });

        doc.Views.Redraw();
        return copiedComponentCount;
    }

    private List<Guid> CopyOneComponentInstance(RhinoDoc doc, string assemblyName, ComponentRecord component)
    {
        var copiedIds = new List<Guid>();
        if (component.RepresentativeObjectIdsByPartName.Count > 0)
        {
            foreach (var (partName, objectIds) in component.RepresentativeObjectIdsByPartName)
            {
                foreach (var sourceObjectId in objectIds)
                {
                    var sourceObject = doc.Objects.FindId(sourceObjectId);
                    if (sourceObject is null)
                        continue;

                    copiedIds.Add(CopyPartObjectToDrawingLayer(doc, assemblyName, component.Name, partName, sourceObject));
                }
            }

            return copiedIds;
        }

        foreach (var partName in component.PartNames)
        {
            var shopLayer = LayerService.ShopPart(assemblyName, component.Name, partName);
            var layerIndex = _layers.FindLayerIndex(doc, shopLayer);
            if (layerIndex < 0)
                continue;

            var layer = doc.Layers[layerIndex];
            var sourceObject = doc.Objects.FindByLayer(layer).FirstOrDefault();
            if (sourceObject is null)
                continue;

            copiedIds.Add(CopyPartObjectToDrawingLayer(doc, assemblyName, component.Name, partName, sourceObject));
        }

        return copiedIds;
    }

    private Guid CopyPartObjectToDrawingLayer(
        RhinoDoc doc,
        string assemblyName,
        string componentName,
        string partName,
        RhinoObject sourceObject)
    {
        var sourceLayer = doc.Layers[sourceObject.Attributes.LayerIndex];
        var drawingLayer = LayerService.DrawingsPart(assemblyName, componentName, partName);
        var drawingLayerIndex = _layers.EnsureLayerIndex(doc, drawingLayer, sourceLayer.Color);
        var geometry = sourceObject.Geometry.Duplicate();
        var attributes = sourceObject.Attributes.Duplicate();
        attributes.LayerIndex = drawingLayerIndex;
        attributes.RemoveFromAllGroups();
        return doc.Objects.Add(geometry, attributes);
    }

    private static void MoveToDrawingRow(RhinoDoc doc, IReadOnlyList<Guid> objectIds, int rowIndex)
    {
        var bbox = TransformUtilities.GetBoundingBox(objectIds, doc);
        if (!bbox.IsValid)
            return;

        var target = new Point3d(1200.0 + rowIndex * 200.0, 0.0, 0.0);
        var translation = Transform.Translation(target - bbox.Center);
        foreach (var objectId in objectIds)
            doc.Objects.Transform(objectId, translation, true);
    }

    private static void OptimizePlanRotation(RhinoDoc doc, IReadOnlyList<Guid> objectIds)
    {
        var bbox = TransformUtilities.GetBoundingBox(objectIds, doc);
        if (!bbox.IsValid)
            return;

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
            return;

        var bestRotation = Transform.Rotation(RhinoMath.ToRadians(bestAngle), Vector3d.ZAxis, center);
        foreach (var objectId in objectIds)
            doc.Objects.Transform(objectId, bestRotation, true);
    }

    private static double PlanArea(BoundingBox bbox)
    {
        if (!bbox.IsValid)
            return double.MaxValue;

        return Math.Abs(bbox.Max.X - bbox.Min.X) * Math.Abs(bbox.Max.Y - bbox.Min.Y);
    }

    private static void CreateGroup(RhinoDoc doc, string assemblyName, string componentName, IEnumerable<Guid> objectIds)
    {
        var groupIndex = doc.Groups.Add(TruncateGroupName($"DRAWINGS_{assemblyName}_{componentName}_{Guid.NewGuid():N}"));
        if (groupIndex < 0)
            return;

        foreach (var objectId in objectIds)
            doc.Groups.AddToGroup(groupIndex, objectId);
    }

    private static string TruncateGroupName(string name)
    {
        return name.Length <= 50 ? name : name[..50];
    }
}
