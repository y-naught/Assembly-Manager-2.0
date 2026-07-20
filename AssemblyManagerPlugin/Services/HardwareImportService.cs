using System.IO;
using AssemblyManagerPlugin.Core;
using Rhino;
using Rhino.DocObjects;
using Rhino.FileIO;
using Rhino.Geometry;

namespace AssemblyManagerPlugin.Services;

public sealed class HardwareImportService
{
    private readonly AssemblyRepository _repository;
    private readonly LayerService _layers;
    private readonly IActionHistorySink _history;

    public HardwareImportService(AssemblyRepository repository, LayerService layers, IActionHistorySink history)
    {
        _repository = repository;
        _layers = layers;
        _history = history;
    }

    public HardwareRecord ImportStepAsHardwareBlock(RhinoDoc doc, string filepath, string? assemblyName = null)
    {
        if (string.IsNullOrWhiteSpace(filepath) || !File.Exists(filepath))
            throw new FileNotFoundException("STEP file was not found.", filepath);

        _layers.EnsureRootLayers(doc);

        var baseName = Path.GetFileNameWithoutExtension(filepath);
        if (string.IsNullOrWhiteSpace(baseName))
            baseName = "Imported_STEP";

        var layerPath = $"{AssemblyManagerConstants.HardwareRootLayer}::{GetUniqueLayerChildName(doc, AssemblyManagerConstants.HardwareRootLayer, baseName)}";
        var layerIndex = _layers.EnsureLayerIndex(doc, layerPath);
        var description = $"Imported hardware from {Path.GetFileName(filepath)}";
        var before = GetDocumentObjectIds(doc);

        if (!ImportHardwareFile(doc, filepath))
        {
            var history = RhinoApp.CommandHistoryWindowText;
            var detail = string.IsNullOrWhiteSpace(history) ? string.Empty : $"\n\nRhino command history:\n{history}";
            throw new InvalidOperationException($"Rhino could not import '{Path.GetFileName(filepath)}'.{detail}");
        }

        var after = GetDocumentObjectIds(doc);
        var importedIds = after.Except(before).ToList();
        if (importedIds.Count == 0)
            throw new InvalidOperationException("No geometry was imported from the STEP file.");

        foreach (var objectId in importedIds)
            _layers.MoveObjectToLayer(doc, objectId, layerPath);

        var importedObjects = importedIds
            .Select(id => doc.Objects.FindId(id))
            .Where(obj => obj is not null)
            .Cast<RhinoObject>()
            .ToList();

        var blockName = GetUniqueBlockName(doc, baseName);
        var metadata = new HardwareMetadataRecord(blockName, baseName, description, filepath, blockName);

        var geometries = importedObjects.Select(obj => obj.Geometry.Duplicate()).ToList();
        var attributes = importedObjects.Select(obj =>
        {
            var attrs = obj.Attributes.Duplicate();
            attrs.LayerIndex = layerIndex;
            if (string.IsNullOrWhiteSpace(attrs.Name))
                attrs.Name = blockName;
            HardwareMetadata.Mark(attrs, metadata);
            return attrs;
        }).ToList();

        var definitionIndex = doc.InstanceDefinitions.Add(blockName, description, Point3d.Origin, geometries, attributes);
        if (definitionIndex < 0)
            throw new InvalidOperationException("Could not create a block definition from imported hardware.");

        foreach (var importedId in importedIds)
            doc.Objects.Delete(importedId, true);

        var instanceAttributes = new ObjectAttributes
        {
            LayerIndex = layerIndex,
            Name = blockName
        };
        HardwareMetadata.Mark(instanceAttributes, metadata);

        var instanceId = doc.Objects.AddInstanceObject(definitionIndex, Transform.Identity, instanceAttributes);
        var record = new HardwareRecord
        {
            Name = baseName,
            Description = description,
            SourcePath = filepath,
            BlockDefinitionName = blockName,
            BlockInstanceId = instanceId,
            SourceObjectId = instanceId,
            LayerName = blockName,
            Quantity = 1
        };

        if (!string.IsNullOrWhiteSpace(assemblyName))
        {
            var store = _repository.Load(doc);
            var assembly = store.FindAssembly(assemblyName);
            if (assembly is not null)
            {
                assembly.Hardware.Add(record);
                _repository.Save(doc, store);
            }
        }

        _history.Record(doc, new ActionHistoryEntry
        {
            CommandName = "ImportHardware",
            AssemblyName = assemblyName ?? string.Empty,
            Summary = $"Imported hardware '{baseName}' as a block.",
            Data =
            {
                ["path"] = filepath,
                ["block"] = blockName
            }
        });

        doc.Views.Redraw();
        return record;
    }

    private static HashSet<Guid> GetDocumentObjectIds(RhinoDoc doc)
    {
        var settings = new ObjectEnumeratorSettings
        {
            ActiveObjects = true,
            HiddenObjects = true,
            LockedObjects = true,
            IncludeLights = false,
            IncludeGrips = false,
            NormalObjects = true
        };

        return doc.Objects.GetObjectList(settings).Select(obj => obj.Id).ToHashSet();
    }

    private static bool ImportHardwareFile(RhinoDoc doc, string filepath)
    {
        var extension = Path.GetExtension(filepath);
        if (string.Equals(extension, ".stp", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".step", StringComparison.OrdinalIgnoreCase))
        {
            var options = new FileStpReadOptions
            {
                JoinSurfaces = true,
                LimitFaces = false
            };
            return doc.Import(filepath, options.ToDictionary());
        }

        return doc.Import(filepath);
    }

    private string GetUniqueLayerChildName(RhinoDoc doc, string parentLayer, string baseName)
    {
        var child = CleanName(baseName);
        var index = 1;
        while (_layers.FindLayerIndex(doc, $"{parentLayer}::{child}") >= 0)
        {
            child = $"{CleanName(baseName)}_{index:00}";
            index++;
        }

        return child;
    }

    private static string GetUniqueBlockName(RhinoDoc doc, string baseName)
    {
        var clean = CleanName(baseName);
        var blockName = clean;
        var index = 1;
        while (doc.InstanceDefinitions.Find(blockName) is not null)
        {
            blockName = $"{clean}_{index:00}";
            index++;
        }

        return blockName;
    }

    private static string CleanName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(new[] { ':', ';', '"', '\'', '<', '>', '|', '?', '*', '\r', '\n', '\t' });
        var cleaned = invalid.Aggregate(value.Trim(), (current, c) => current.Replace(c, '_'));
        return string.IsNullOrWhiteSpace(cleaned) ? "Imported_STEP" : cleaned;
    }
}
