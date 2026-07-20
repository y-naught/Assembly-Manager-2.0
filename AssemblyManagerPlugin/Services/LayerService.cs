using System.Drawing;
using AssemblyManagerPlugin.Core;
using Rhino;
using Rhino.DocObjects;

namespace AssemblyManagerPlugin.Services;

public sealed class LayerService
{
    public static readonly Color[] DefaultPartColors =
    {
        Color.FromArgb(230, 25, 75),
        Color.FromArgb(60, 180, 75),
        Color.FromArgb(255, 225, 25),
        Color.FromArgb(0, 130, 200),
        Color.FromArgb(245, 130, 48),
        Color.FromArgb(145, 30, 180),
        Color.FromArgb(70, 240, 240),
        Color.FromArgb(240, 50, 230),
        Color.FromArgb(210, 245, 60),
        Color.FromArgb(250, 190, 212),
        Color.FromArgb(0, 128, 128),
        Color.FromArgb(220, 190, 255),
        Color.FromArgb(170, 110, 40),
        Color.FromArgb(255, 250, 200),
        Color.FromArgb(128, 0, 0),
        Color.FromArgb(170, 255, 195),
        Color.FromArgb(128, 128, 0),
        Color.FromArgb(255, 215, 180),
        Color.FromArgb(0, 0, 128),
        Color.FromArgb(128, 128, 128),
        Color.FromArgb(255, 0, 0)
    };

    public void EnsureRootLayers(RhinoDoc doc)
    {
        EnsureLayer(doc, AssemblyManagerConstants.ShopRootLayer, Color.DarkGray);
        EnsureLayer(doc, AssemblyManagerConstants.CamRootLayer, Color.DarkGray);
        EnsureLayer(doc, AssemblyManagerConstants.DrawingsRootLayer, Color.DarkGray);
        EnsureLayer(doc, AssemblyManagerConstants.HardwareRootLayer, Color.DarkGray);
        EnsureLayer(doc, AssemblyManagerConstants.AnnotationRootLayer, Color.DarkGray);
    }

    public Layer EnsureLayer(RhinoDoc doc, string fullPath, Color? color = null)
    {
        var pathParts = SplitLayerPath(fullPath);
        if (pathParts.Length == 0)
            throw new ArgumentException("Layer path cannot be empty.", nameof(fullPath));

        Layer? parent = null;
        Layer? currentLayer = null;
        var currentPath = string.Empty;

        foreach (var pathPart in pathParts)
        {
            currentPath = string.IsNullOrEmpty(currentPath) ? pathPart : $"{currentPath}::{pathPart}";
            var existingIndex = FindLayerIndex(doc, currentPath);
            if (existingIndex >= 0)
            {
                currentLayer = doc.Layers[existingIndex];
                parent = currentLayer;
                continue;
            }

            var newLayer = new Layer
            {
                Name = pathPart,
                Color = color ?? Color.Black
            };

            if (parent is not null)
                newLayer.ParentLayerId = parent.Id;

            var index = doc.Layers.Add(newLayer);
            if (index < 0)
                throw new InvalidOperationException($"Could not create Rhino layer '{currentPath}'.");

            currentLayer = doc.Layers[index];
            parent = currentLayer;
        }

        return currentLayer!;
    }

    public int EnsureLayerIndex(RhinoDoc doc, string fullPath, Color? color = null)
    {
        return EnsureLayer(doc, fullPath, color).Index;
    }

    public int FindLayerIndex(RhinoDoc doc, string fullPath)
    {
        for (var i = 0; i < doc.Layers.Count; i++)
        {
            var layer = doc.Layers[i];
            if (layer is null || layer.IsDeleted)
                continue;

            if (string.Equals(layer.FullPath, fullPath, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    public string[] GetChildLayerPaths(RhinoDoc doc, string parentFullPath)
    {
        var parentIndex = FindLayerIndex(doc, parentFullPath);
        if (parentIndex < 0)
            return Array.Empty<string>();

        var parentId = doc.Layers[parentIndex].Id;
        return doc.Layers
            .Where(layer => layer is not null && !layer.IsDeleted && layer.ParentLayerId == parentId)
            .Select(layer => layer.FullPath)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<string> GetLayerTreePaths(RhinoDoc doc, string rootFullPath)
    {
        if (FindLayerIndex(doc, rootFullPath) < 0)
            return Array.Empty<string>();

        var paths = new List<string>();

        void Visit(string path)
        {
            paths.Add(path);
            foreach (var child in GetChildLayerPaths(doc, path))
                Visit(child);
        }

        Visit(rootFullPath);
        return paths;
    }

    public IReadOnlyList<Guid> GetObjectIdsInLayerTree(RhinoDoc doc, string rootFullPath)
    {
        return GetLayerTreePaths(doc, rootFullPath)
            .SelectMany(path =>
            {
                var layerIndex = FindLayerIndex(doc, path);
                if (layerIndex < 0)
                    return Enumerable.Empty<Guid>();

                return doc.Objects.FindByLayer(doc.Layers[layerIndex]).Select(obj => obj.Id);
            })
            .Distinct()
            .ToList();
    }

    public void MoveObjectToLayer(RhinoDoc doc, Guid objectId, string fullPath, Color? color = null)
    {
        var rhinoObject = doc.Objects.FindId(objectId);
        if (rhinoObject is null)
            return;

        var attributes = rhinoObject.Attributes.Duplicate();
        attributes.LayerIndex = EnsureLayerIndex(doc, fullPath, color);
        doc.Objects.ModifyAttributes(rhinoObject, attributes, true);
    }

    public void TryDeleteLayerIfEmpty(RhinoDoc doc, string fullPath)
    {
        var layerIndex = FindLayerIndex(doc, fullPath);
        if (layerIndex < 0)
            return;

        var layer = doc.Layers[layerIndex];
        if (doc.Objects.FindByLayer(layer).Length > 0)
            return;

        var hasChildren = doc.Layers.Any(child => child is not null && !child.IsDeleted && child.ParentLayerId == layer.Id);
        if (hasChildren)
            return;

        doc.Layers.Delete(layerIndex, true);
    }

    public int DeleteLayerTree(RhinoDoc doc, string rootFullPath)
    {
        var paths = GetLayerTreePaths(doc, rootFullPath);
        if (paths.Count == 0)
            return 0;

        MoveCurrentLayerOutsideTree(doc, paths);

        var deletedCount = 0;
        foreach (var path in paths.OrderByDescending(path => SplitLayerPath(path).Length))
        {
            var layerIndex = FindLayerIndex(doc, path);
            if (layerIndex < 0)
                continue;

            if (doc.Layers.Delete(layerIndex, true))
                deletedCount++;
        }

        return deletedCount;
    }

    private void MoveCurrentLayerOutsideTree(RhinoDoc doc, IReadOnlyCollection<string> treePaths)
    {
        var currentIndex = doc.Layers.CurrentLayerIndex;
        if (currentIndex < 0 || currentIndex >= doc.Layers.Count)
            return;

        var currentLayer = doc.Layers[currentIndex];
        if (currentLayer is null || !treePaths.Contains(currentLayer.FullPath, StringComparer.OrdinalIgnoreCase))
            return;

        for (var i = 0; i < doc.Layers.Count; i++)
        {
            var candidate = doc.Layers[i];
            if (candidate is null || candidate.IsDeleted)
                continue;

            if (treePaths.Contains(candidate.FullPath, StringComparer.OrdinalIgnoreCase))
                continue;

            doc.Layers.SetCurrentLayerIndex(i, true);
            return;
        }
    }

    public static string ChildName(string fullPath)
    {
        var parts = SplitLayerPath(fullPath);
        return parts.Length == 0 ? fullPath : parts[^1];
    }

    public static string ParentPath(string fullPath)
    {
        var parts = SplitLayerPath(fullPath);
        return parts.Length <= 1 ? string.Empty : string.Join("::", parts.Take(parts.Length - 1));
    }

    public static string[] SplitLayerPath(string fullPath)
    {
        return fullPath
            .Split(new[] { "::" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public static string ShopAssembly(string assemblyName)
    {
        return $"{AssemblyManagerConstants.ShopRootLayer}::{assemblyName}";
    }

    public static string ShopComponent(string assemblyName, string componentName)
    {
        return $"{ShopAssembly(assemblyName)}::{componentName}";
    }

    public static string ShopPart(string assemblyName, string componentName, string partName)
    {
        return $"{ShopComponent(assemblyName, componentName)}::{partName}";
    }

    public static string CamAssembly(string assemblyName)
    {
        return $"{AssemblyManagerConstants.CamRootLayer}::{assemblyName}";
    }

    public static string CamPart(string assemblyName, string partName)
    {
        return $"{CamAssembly(assemblyName)}::{partName}";
    }

    public static string DrawingsAssembly(string assemblyName)
    {
        return $"{AssemblyManagerConstants.DrawingsRootLayer}::{assemblyName}";
    }

    public static string DrawingsPart(string assemblyName, string componentName, string partName)
    {
        return $"{DrawingsAssembly(assemblyName)}::{componentName}::{partName}";
    }
}
