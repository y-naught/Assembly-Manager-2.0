using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace AssemblyManagerPlugin.Services;

public enum DetailLabelLevel
{
    Assembly = 0,
    Component = 1
}

public sealed class DetailLabelService
{
    private readonly LayerService _layers;

    public DetailLabelService(LayerService layers)
    {
        _layers = layers;
    }

    public int LabelVisibleObjects(RhinoDoc doc, Guid detailId, DetailLabelLevel labelLevel)
    {
        var detail = FindDetail(doc, detailId);
        if (detail is null)
            throw new InvalidOperationException("Selected object is not a layout detail.");

        var targetRoot = labelLevel == DetailLabelLevel.Component ? "DRAWINGS" : "SHOP";
        var objects = GetObjectsUnderRoot(doc, targetRoot)
            .Where(obj => IsVisibleInDetail(detail, obj))
            .ToList();

        if (labelLevel == DetailLabelLevel.Component)
            return AddPartDots(doc, detail, objects);

        return AddComponentDots(doc, detail, objects);
    }

    private int AddPartDots(RhinoDoc doc, DetailViewObject detail, IReadOnlyList<RhinoObject> objects)
    {
        var count = 0;
        foreach (var obj in objects)
        {
            var point = GetObjectCenter(obj);
            point.Transform(detail.WorldToPageTransform);
            var label = LayerService.ChildName(doc.Layers[obj.Attributes.LayerIndex].FullPath);
            doc.Objects.AddTextDot(new TextDot(label, point));
            count++;
        }

        doc.Views.Redraw();
        return count;
    }

    private int AddComponentDots(RhinoDoc doc, DetailViewObject detail, IReadOnlyList<RhinoObject> objects)
    {
        var grouped = objects
            .SelectMany(obj => (obj.Attributes.GetGroupList() ?? Array.Empty<int>()).Select(group => (group, obj)))
            .GroupBy(entry => entry.group)
            .ToList();

        var count = 0;
        foreach (var group in grouped)
        {
            var groupObjects = group.Select(entry => entry.obj).Distinct().ToList();
            var bbox = BoundingBox.Empty;
            foreach (var obj in groupObjects)
                bbox.Union(obj.Geometry.GetBoundingBox(true));

            if (!bbox.IsValid)
                continue;

            var layerPath = doc.Layers[groupObjects[0].Attributes.LayerIndex].FullPath;
            var componentName = ComponentNameFromPartLayer(layerPath);
            var point = bbox.Center;
            point.Transform(detail.WorldToPageTransform);
            doc.Objects.AddTextDot(new TextDot(componentName, point));
            count++;
        }

        doc.Views.Redraw();
        return count;
    }

    private IEnumerable<RhinoObject> GetObjectsUnderRoot(RhinoDoc doc, string rootLayerName)
    {
        var settings = new ObjectEnumeratorSettings
        {
            ActiveObjects = true,
            NormalObjects = true,
            LockedObjects = true,
            HiddenObjects = false
        };

        foreach (var obj in doc.Objects.GetObjectList(settings))
        {
            var layer = doc.Layers[obj.Attributes.LayerIndex];
            if (layer is not null && layer.FullPath.StartsWith(rootLayerName + "::", StringComparison.OrdinalIgnoreCase))
                yield return obj;
        }
    }

    private static DetailViewObject? FindDetail(RhinoDoc doc, Guid detailId)
    {
        foreach (var page in doc.Views.GetPageViews())
        {
            foreach (var detail in page.GetDetailViews())
            {
                if (detail.Id == detailId)
                    return detail;
            }
        }

        return null;
    }

    private static bool IsVisibleInDetail(DetailViewObject detail, RhinoObject obj)
    {
        var box = obj.Geometry.GetBoundingBox(true);
        return box.IsValid && detail.Viewport.IsVisible(box);
    }

    private static Point3d GetObjectCenter(RhinoObject obj)
    {
        if (obj.Geometry is Brep brep)
        {
            var volume = VolumeMassProperties.Compute(brep);
            if (volume is not null)
                return volume.Centroid;
        }

        var bbox = obj.Geometry.GetBoundingBox(true);
        return bbox.IsValid ? bbox.Center : Point3d.Origin;
    }

    private static string ComponentNameFromPartLayer(string layerPath)
    {
        var parts = LayerService.SplitLayerPath(layerPath);
        return parts.Length >= 3 ? parts[^2] : LayerService.ChildName(layerPath);
    }
}
