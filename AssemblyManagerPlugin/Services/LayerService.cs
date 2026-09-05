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
        EnsureLayer(doc, AssemblyManagerConstants.AssemblyManagerRootLayer, Color.DarkGray);
        EnsureLayer(doc, AssemblyManagerConstants.OriginalAssembliesRootLayer, Color.DarkGray);
        EnsureLayer(doc, AssemblyManagerConstants.CopiedComponentsRootLayer, Color.DarkGray);
        EnsureLayer(doc, AssemblyManagerConstants.PartsRootLayer, Color.DarkGray);
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

    public static Color PartColorForName(string partName, bool colorizeParts = true)
    {
        if (!colorizeParts)
            return Color.Black;
        var digits = new string(partName.Where(char.IsDigit).ToArray());
        if (!int.TryParse(digits, out var index))
            index = 1;
        return DefaultPartColors[(Math.Max(1, index) - 1) % DefaultPartColors.Length];
    }

    /// <summary>Existing original layers are the color authority, including user custom colors.</summary>
    public Color? FindPartLayerColor(RhinoDoc doc, string assemblyName, string partName)
    {
        var originalRoot = OriginalAssembly(assemblyName);
        var copiedRoot = CopiedComponentsAssembly(assemblyName);
        var flatPath = $"{PartsPart(assemblyName, partName)}::3D";
        return doc.Layers.Where(layer => layer is not null && !layer.IsDeleted)
            .Select(layer => new
            {
                Layer = layer,
                Priority = string.Equals(layer.Name, partName, StringComparison.OrdinalIgnoreCase) &&
                           string.Equals(ParentPath(ParentPath(layer.FullPath)), originalRoot, StringComparison.OrdinalIgnoreCase)
                    ? 0
                    : string.Equals(layer.Name, partName, StringComparison.OrdinalIgnoreCase) &&
                      string.Equals(ParentPath(ParentPath(layer.FullPath)), copiedRoot, StringComparison.OrdinalIgnoreCase)
                        ? 1
                        : string.Equals(layer.FullPath, flatPath, StringComparison.OrdinalIgnoreCase) ? 2 : 3
            })
            .Where(item => item.Priority < 3)
            .OrderBy(item => item.Priority)
            .ThenByDescending(item => doc.Objects.FindByLayer(item.Layer).Length > 0)
            .ThenBy(item => item.Layer.FullPath, StringComparer.OrdinalIgnoreCase)
            .Select(item => (Color?)item.Layer.Color)
            .FirstOrDefault();
    }

    /// <summary>Set the part leaf's color without recoloring its component or assembly parents.</summary>
    public int EnsurePartLayerIndex(RhinoDoc doc, string fullPath, Color color)
    {
        var parentPath = ParentPath(fullPath);
        if (!string.IsNullOrWhiteSpace(parentPath))
            EnsureLayer(doc, parentPath);
        var layer = EnsureLayer(doc, fullPath, color);
        if (layer.Color.ToArgb() != color.ToArgb())
        {
            layer.Color = color;
            if (doc.Layers[layer.Index].Color.ToArgb() != color.ToArgb())
                throw new InvalidOperationException($"Could not update part layer color for '{fullPath}'.");
        }
        return layer.Index;
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
        if (layer.IsReference || layerIndex == doc.Layers.CurrentLayerIndex)
            return;

        // Include objects that normal visible-object enumeration can omit. Never remove
        // operator geometry, reference objects or block-definition contents as cleanup.
        var objects = new ObjectEnumeratorSettings
        {
            NormalObjects = true,
            HiddenObjects = true,
            LockedObjects = true,
            ReferenceObjects = true,
            IdefObjects = true,
            IncludeLights = true,
            IncludeGrips = true,
            LayerIndexFilter = layerIndex
        };
        if (doc.Objects.GetObjectList(objects).Any())
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

    public static string OriginalAssembly(string assemblyName)
    {
        return $"{AssemblyManagerConstants.OriginalAssembliesRootLayer}::{assemblyName}";
    }

    public static string OriginalComponent(string assemblyName, string componentName)
    {
        return $"{OriginalAssembly(assemblyName)}::{componentName}";
    }

    public static string OriginalPart(string assemblyName, string componentName, string partName)
    {
        return $"{OriginalComponent(assemblyName, componentName)}::{partName}";
    }

    public static string PartsAssembly(string assemblyName)
    {
        return $"{AssemblyManagerConstants.PartsRootLayer}::{assemblyName}";
    }

    public static string PartsPart(string assemblyName, string partName)
    {
        return $"{PartsAssembly(assemblyName)}::{partName}";
    }

    public static string CopiedComponentsAssembly(string assemblyName)
    {
        return $"{AssemblyManagerConstants.CopiedComponentsRootLayer}::{assemblyName}";
    }

    public static string CopiedComponentPart(string assemblyName, string componentName, string partName)
    {
        return $"{CopiedComponentsAssembly(assemblyName)}::{componentName}::{partName}";
    }

    public static string LegacyOriginalAssembly(string assemblyName)
    {
        return $"{AssemblyManagerConstants.LegacyShopRootLayer}::{assemblyName}";
    }

    public static string LegacyOriginalPart(string assemblyName, string componentName, string partName)
    {
        return $"{LegacyOriginalAssembly(assemblyName)}::{componentName}::{partName}";
    }

    public static string LegacyPartsAssembly(string assemblyName)
    {
        return $"{AssemblyManagerConstants.LegacyCamRootLayer}::{assemblyName}";
    }

    public static string LegacyCopiedComponentsAssembly(string assemblyName)
    {
        return $"{AssemblyManagerConstants.LegacyDrawingsRootLayer}::{assemblyName}";
    }
}
