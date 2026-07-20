using System.Drawing;
using System.IO;
using System.Text.Json;
using AssemblyManagerPlugin.Core;
using AssemblyManagerPlugin.Geometry;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.UI;

namespace AssemblyManagerPlugin.Services;

public sealed class AssemblyGenerationService
{
    private static readonly JsonSerializerOptions DebugJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly AssemblyRepository _repository;
    private readonly LayerService _layers;
    private readonly GeometryFingerprintService _fingerprints;
    private readonly PluginSettingsService _settings;
    private readonly IActionHistorySink _history;

    public AssemblyGenerationService(
        AssemblyRepository repository,
        LayerService layers,
        GeometryFingerprintService fingerprints,
        PluginSettingsService settings,
        IActionHistorySink history)
    {
        _repository = repository;
        _layers = layers;
        _fingerprints = fingerprints;
        _settings = settings;
        _history = history;
    }

    public CreateAssemblyResult CreateAssembly(RhinoDoc doc, IEnumerable<Guid> sourceObjectIds, CreateAssemblyOptions options)
    {
        using var progress = new CreateAssemblyProgress();
        progress.Update(0, "Starting assembly creation");
        var pluginSettings = _settings.Load();
        var assemblySettings = pluginSettings.AssemblyManager;
        options.AssemblyName = CleanName(options.AssemblyName);
        options.PartPrefix = string.IsNullOrWhiteSpace(options.PartPrefix) ? assemblySettings.DefaultPartPrefix : options.PartPrefix.Trim();
        options.ComponentPrefix = string.IsNullOrWhiteSpace(options.ComponentPrefix) ? assemblySettings.DefaultComponentPrefix : options.ComponentPrefix.Trim();

        if (string.IsNullOrWhiteSpace(options.AssemblyName))
            throw new InvalidOperationException("Assembly name is required.");

        var store = _repository.Load(doc);
        if (store.FindAssembly(options.AssemblyName) is not null)
            throw new InvalidOperationException($"Assembly '{options.AssemblyName}' already exists.");

        progress.Update(1, "Creating assembly layers");
        _layers.EnsureRootLayers(doc);
        _layers.EnsureLayer(doc, LayerService.ShopAssembly(options.AssemblyName));
        _layers.EnsureLayer(doc, LayerService.ShopComponent(options.AssemblyName, "unsorted"));
        _layers.EnsureLayer(doc, LayerService.CamAssembly(options.AssemblyName));
        _layers.EnsureLayer(doc, LayerService.DrawingsAssembly(options.AssemblyName));

        progress.Update(2, "Expanding selected groups");
        var expandedSourceObjectIds = ExpandSelectedObjectsToGroups(doc, sourceObjectIds);
        var sourceObjects = expandedSourceObjectIds
            .Select(id => doc.Objects.FindId(id))
            .Where(obj => obj is not null)
            .Cast<RhinoObject>()
            .ToList();
        var warnings = new List<string>();

        progress.Update(3, "Filtering valid manufacturing parts");
        var hardwareObjects = sourceObjects
            .Where(obj => options.SkipHardware && _fingerprints.IsHardwareObject(obj))
            .ToList();
        var hardwareObjectIds = hardwareObjects.Select(obj => obj.Id).ToHashSet();
        var hardwareCandidates = hardwareObjects
            .Select(obj => CreateHardwareCandidate(doc, obj))
            .ToList();

        var candidates = new List<PartCandidate>();
        foreach (var sourceObject in sourceObjects.Where(obj => !hardwareObjectIds.Contains(obj.Id)))
            AddPartCandidatesFromSourceObject(doc, sourceObject, candidates, warnings);

        if (candidates.Count == 0 && hardwareCandidates.Count == 0)
            throw new InvalidOperationException(BuildNoValidPartsMessage(warnings));

        progress.Update(4, $"Categorizing {candidates.Count} part object(s)");
        var selectionBox = BoundingBox.Empty;
        foreach (var candidate in candidates)
            selectionBox.Union(candidate.Geometry.GetBoundingBox(true));
        foreach (var hardware in hardwareCandidates)
            selectionBox.Union(hardware.Geometry.GetBoundingBox(true));

        var assemblyTranslation = TransformUtilities.GetTranslationToRow(
            selectionBox,
            options.TranslationMultiplier,
            options.TranslationMultiplier);

        var assembly = new AssemblyRecord
        {
            Name = options.AssemblyName,
            Description = options.Description,
            PartPrefix = options.PartPrefix,
            ComponentPrefix = options.ComponentPrefix,
            SourceDocumentId = doc.RuntimeSerialNumber.ToString()
        };

        var partCategories = BuildPartCategories(candidates);

        var partIndex = 0;
        foreach (var category in partCategories)
        {
            var partName = $"{options.PartPrefix}{partIndex + 1:00}";
            var color = PartColorForIndex(partIndex, assemblySettings.ColorizeParts);
            var partLayer = LayerService.ShopPart(options.AssemblyName, "unsorted", partName);
            var layerIndex = _layers.EnsureLayerIndex(doc, partLayer, color);

            var partRecord = new PartRecord
            {
                Name = partName,
                GeometryFingerprint = category.First().Fingerprint,
                Quantity = category.Count(),
                MaterialThickness = 0.0,
                MaterialId = category.First().MaterialId
            };

            foreach (var candidate in category)
            {
                candidate.PartName = partName;
                var sourceObject = doc.Objects.FindId(candidate.SourceObjectId);
                if (sourceObject is null)
                    continue;

                var geometry = candidate.Geometry.Duplicate();
                geometry.Transform(assemblyTranslation);
                var attributes = sourceObject.Attributes.Duplicate();
                MaterialAssignment.NormalizeToParentMaterial(attributes);
                attributes.LayerIndex = layerIndex;
                attributes.Name = string.IsNullOrWhiteSpace(attributes.Name) ? partName : attributes.Name;
                attributes.RemoveFromAllGroups();
                ReferenceUpdateService.AttachReferenceUserStrings(attributes, candidate.SourceObjectId, assemblyTranslation);
                var generatedId = doc.Objects.Add(geometry, attributes);
                candidate.GeneratedObjectId = generatedId;
                partRecord.SourceObjectIds.Add(candidate.SourceObjectId);
                partRecord.GeneratedObjectIds.Add(generatedId);
                assembly.GeometryReferences.Add(new GeometryReferenceRecord
                {
                    AssemblyName = options.AssemblyName,
                    PartName = partName,
                    SourceObjectId = candidate.SourceObjectId,
                    TargetObjectId = generatedId,
                    TargetRole = "SHOP",
                    SourceToTargetTransform = TransformRecord.FromTransform(assemblyTranslation)
                });

                if (candidate.Geometry is Brep brep)
                    partRecord.MaterialThickness = _fingerprints.GetMaterialThickness(brep);
            }

            assembly.Parts.Add(partRecord);
            partIndex++;
        }

        var debugReportPath = string.Empty;
        if (assemblySettings.DebugCategorization)
            debugReportPath = ExportPartCategorizationDebugReport(doc, options.AssemblyName, candidates, partCategories, warnings, assemblySettings);

        progress.Update(5, "Building component candidates");
        var componentCandidates = BuildComponentCandidates(doc, options, assembly, candidates, hardwareCandidates, assemblyTranslation, assemblySettings.ColorizeParts);
        progress.Update(6, "Consolidating equivalent components");
        ConsolidateComponents(doc, options, assembly, componentCandidates, assemblySettings.ColorizeParts);
        progress.Update(7, "Cleaning up temporary layers");
        CleanupLayerTreeIfEmpty(doc, LayerService.ShopComponent(options.AssemblyName, "unsorted"));

        progress.Update(8, "Saving assembly data");
        store.Assemblies.Add(assembly);
        _repository.Save(doc, store);
        var historyEntry = new ActionHistoryEntry
        {
            CommandName = "CreateAssembly",
            AssemblyName = options.AssemblyName,
            Summary = $"Created assembly with {assembly.Parts.Count} unique part(s) and {assembly.Components.Count} component type(s).",
            Data =
            {
                ["sourceParts"] = candidates.Count.ToString(),
                ["hardwareSkipped"] = hardwareObjects.Count.ToString()
            }
        };
        if (!string.IsNullOrWhiteSpace(debugReportPath))
            historyEntry.Data["debugReportPath"] = debugReportPath;

        _history.Record(doc, historyEntry);

        foreach (var warning in warnings)
            RhinoApp.WriteLine("Assembly Manager warning: {0}", warning);

        doc.Views.Redraw();
        progress.Update(9, "Assembly creation complete");
        return new CreateAssemblyResult(assembly, candidates.Count, warnings);
    }

    private List<List<PartCandidate>> BuildPartCategories(IReadOnlyList<PartCandidate> candidates)
    {
        var categories = new List<List<PartCandidate>>();
        foreach (var candidate in candidates)
        {
            var category = categories.FirstOrDefault(group => _fingerprints.AreEquivalentParts(group[0], candidate));
            if (category is null)
            {
                categories.Add(new List<PartCandidate> { candidate });
                continue;
            }

            category.Add(candidate);
        }

        return categories
            .OrderByDescending(group => group.Count)
            .ThenBy(group => CreatePartCategorySortKey(group[0]), StringComparer.Ordinal)
            .ToList();
    }

    private List<ComponentCandidate> BuildComponentCandidates(
        RhinoDoc doc,
        CreateAssemblyOptions options,
        AssemblyRecord assembly,
        IReadOnlyList<PartCandidate> candidates,
        IReadOnlyList<HardwareCandidate> hardwareCandidates,
        Transform assemblyTranslation,
        bool colorizeParts)
    {
        var partGroups = candidates
            .GroupBy(candidate => candidate.GroupIndices.FirstOrDefault(-1))
            .ToDictionary(group => group.Key, group => group.ToList());
        var hardwareGroups = hardwareCandidates
            .GroupBy(candidate => candidate.GroupIndices.FirstOrDefault(-1))
            .ToDictionary(group => group.Key, group => group.ToList());
        var groupKeys = partGroups.Keys
            .Concat(hardwareGroups.Keys)
            .Distinct()
            .OrderBy(key => key)
            .ToList();

        var components = new List<ComponentCandidate>();
        var tempIndex = 1;
        foreach (var groupKey in groupKeys)
        {
            var component = new ComponentCandidate
            {
                TemporaryName = $"TEMP_{options.ComponentPrefix}{tempIndex:00}",
                SourceGroupIndex = groupKey,
                SourceGroupName = GetGroupName(doc, groupKey)
            };

            foreach (var candidate in partGroups.GetValueOrDefault(groupKey) ?? new List<PartCandidate>())
            {
                component.Parts.Add(candidate);
                component.GeneratedObjectIds.Add(candidate.GeneratedObjectId);
                var partColor = PartColorForName(candidate.PartName, colorizeParts);
                _layers.MoveObjectToLayer(
                    doc,
                    candidate.GeneratedObjectId,
                    LayerService.ShopPart(options.AssemblyName, component.TemporaryName, candidate.PartName),
                        partColor);
            }

            foreach (var hardware in hardwareGroups.GetValueOrDefault(groupKey) ?? new List<HardwareCandidate>())
            {
                var generatedId = CopyHardwareToShopLayer(doc, options, component.TemporaryName, hardware, assemblyTranslation);
                if (generatedId == Guid.Empty)
                    continue;

                hardware.GeneratedObjectId = generatedId;
                component.Hardware.Add(hardware);
                component.GeneratedObjectIds.Add(generatedId);
                assembly.GeometryReferences.Add(new GeometryReferenceRecord
                {
                    AssemblyName = options.AssemblyName,
                    PartName = hardware.LayerName,
                    SourceObjectId = hardware.SourceObjectId,
                    TargetObjectId = generatedId,
                    TargetRole = "SHOP_HARDWARE",
                    SourceToTargetTransform = TransformRecord.FromTransform(assemblyTranslation)
                });
            }

            component.Fingerprint = _fingerprints.CreateComponentFingerprint(BuildComponentFingerprintParts(component));
            component.GeneratedGroupName = CreateGroup(doc, options.AssemblyName, component.TemporaryName, component.GeneratedObjectIds);
            components.Add(component);
            tempIndex++;
        }

        _layers.TryDeleteLayerIfEmpty(doc, LayerService.ShopComponent(options.AssemblyName, "unsorted"));
        return components;
    }

    private void ConsolidateComponents(
        RhinoDoc doc,
        CreateAssemblyOptions options,
        AssemblyRecord assembly,
        IReadOnlyList<ComponentCandidate> componentCandidates,
        bool colorizeParts)
    {
        var componentCategories = BuildComponentCategories(componentCandidates);

        var componentIndex = 0;
        foreach (var category in componentCategories)
        {
            var componentName = $"{options.ComponentPrefix}{componentIndex + 1:00}";
            var componentRecord = new ComponentRecord
            {
                Name = componentName,
                Quantity = category.Count(),
                Fingerprint = category.First().Fingerprint
            };
            var representativeComponent = category.First();
            foreach (var partGroup in representativeComponent.Parts.GroupBy(part => part.PartName, StringComparer.OrdinalIgnoreCase))
            {
                var partName = partGroup.Key;
                componentRecord.PartQuantities[partName] = partGroup.Count();
                componentRecord.RepresentativeObjectIdsByPartName[partName] = partGroup
                    .Select(part => part.GeneratedObjectId)
                    .Where(id => id != Guid.Empty)
                    .ToList();
            }
            foreach (var hardwareGroup in representativeComponent.Hardware.GroupBy(hardware => hardware.LayerName, StringComparer.OrdinalIgnoreCase))
            {
                var hardwareName = hardwareGroup.Key;
                componentRecord.PartQuantities[hardwareName] = hardwareGroup.Count();
                componentRecord.RepresentativeObjectIdsByPartName[hardwareName] = hardwareGroup
                    .Select(hardware => hardware.GeneratedObjectId)
                    .Where(id => id != Guid.Empty)
                    .ToList();
            }

            foreach (var component in category)
            {
                componentRecord.InstanceGroupNames.Add(component.GeneratedGroupName);
                componentRecord.ObjectIds.AddRange(component.GeneratedObjectIds);

                foreach (var part in component.Parts)
                {
                    if (!componentRecord.PartNames.Contains(part.PartName, StringComparer.OrdinalIgnoreCase))
                        componentRecord.PartNames.Add(part.PartName);

                    var color = PartColorForName(part.PartName, colorizeParts);
                    _layers.MoveObjectToLayer(
                        doc,
                        part.GeneratedObjectId,
                        LayerService.ShopPart(options.AssemblyName, componentName, part.PartName),
                        color);
                    var reference = assembly.GeometryReferences.FirstOrDefault(r => r.TargetObjectId == part.GeneratedObjectId);
                    if (reference is not null)
                        reference.ComponentName = componentName;
                }

                foreach (var hardware in component.Hardware)
                {
                    if (!componentRecord.PartNames.Contains(hardware.LayerName, StringComparer.OrdinalIgnoreCase))
                        componentRecord.PartNames.Add(hardware.LayerName);

                    _layers.MoveObjectToLayer(
                        doc,
                        hardware.GeneratedObjectId,
                        LayerService.ShopPart(options.AssemblyName, componentName, hardware.LayerName),
                        Color.DarkGray);
                    var reference = assembly.GeometryReferences.FirstOrDefault(r => r.TargetObjectId == hardware.GeneratedObjectId);
                    if (reference is not null)
                    {
                        reference.ComponentName = componentName;
                        reference.PartName = hardware.LayerName;
                    }

                    assembly.Hardware.Add(new HardwareRecord
                    {
                        Name = hardware.Name,
                        Description = hardware.Description,
                        SourcePath = hardware.SourcePath,
                        BlockDefinitionName = hardware.BlockDefinitionName,
                        SourceObjectId = hardware.SourceObjectId,
                        BlockInstanceId = hardware.SourceObjectId,
                        GeneratedObjectId = hardware.GeneratedObjectId,
                        ComponentName = componentName,
                        LayerName = hardware.LayerName,
                        MaterialId = hardware.MaterialId,
                        Quantity = 1
                    });
                }

                CleanupTemporaryComponentLayer(doc, options.AssemblyName, component.TemporaryName);
            }

            componentRecord.PartNames = componentRecord.PartQuantities.Keys
                .OrderBy(partName => partName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            assembly.Components.Add(componentRecord);
            componentIndex++;
        }
    }

    private List<List<ComponentCandidate>> BuildComponentCategories(IReadOnlyList<ComponentCandidate> componentCandidates)
    {
        var categories = new List<List<ComponentCandidate>>();
        foreach (var component in componentCandidates)
        {
            var componentParts = BuildComponentFingerprintParts(component);
            var category = categories.FirstOrDefault(group =>
                _fingerprints.AreEquivalentComponents(BuildComponentFingerprintParts(group[0]), componentParts));
            if (category is null)
            {
                categories.Add(new List<ComponentCandidate> { component });
                continue;
            }

            category.Add(component);
        }

        return categories
            .OrderByDescending(group => group.Count)
            .ThenBy(group => group[0].Fingerprint, StringComparer.Ordinal)
            .ToList();
    }

    private void CleanupTemporaryComponentLayer(RhinoDoc doc, string assemblyName, string temporaryName)
    {
        CleanupLayerTreeIfEmpty(doc, LayerService.ShopComponent(assemblyName, temporaryName));
    }

    private void AddPartCandidatesFromSourceObject(
        RhinoDoc doc,
        RhinoObject sourceObject,
        List<PartCandidate> candidates,
        List<string> warnings)
    {
        if (sourceObject is InstanceObject instanceObject)
        {
            var blockCandidateCount = 0;
            foreach (var blockGeometry in GetBlockPartGeometry(doc, instanceObject))
            {
                if (_fingerprints.TryCreatePartCandidate(
                    instanceObject.Id,
                    blockGeometry.Geometry,
                    instanceObject.Attributes.GetGroupList() ?? Array.Empty<int>(),
                    out var blockCandidate,
                    out var blockWarning,
                    blockGeometry.Label))
                {
                    var instanceMaterialId = MaterialAssignment.GetCategorizationMaterialId(instanceObject.Attributes);
                    blockCandidate.MaterialId = string.IsNullOrWhiteSpace(instanceMaterialId)
                        ? MaterialAssignment.GetCategorizationMaterialId(blockGeometry.Attributes)
                        : instanceMaterialId;
                    candidates.Add(blockCandidate);
                    blockCandidateCount++;
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(blockWarning))
                    warnings.Add(blockWarning);
            }

            if (blockCandidateCount == 0)
                warnings.Add($"Ignored block '{instanceObject.Name}': it did not contain closed polysurface or extrusion parts.");
            return;
        }

        if (_fingerprints.TryCreatePartCandidate(sourceObject, out var candidate, out var warning))
        {
            candidate.MaterialId = MaterialAssignment.GetCategorizationMaterialId(sourceObject.Attributes);
            candidates.Add(candidate);
            return;
        }

        if (!string.IsNullOrWhiteSpace(warning))
            warnings.Add(warning);
    }

    private HardwareCandidate CreateHardwareCandidate(RhinoDoc doc, RhinoObject hardwareObject)
    {
        if (!HardwareMetadata.TryGetFromObject(hardwareObject, out var metadata))
            metadata = HardwareMetadata.FromObject(hardwareObject);

        if (hardwareObject is InstanceObject instanceObject)
        {
            var definitionName = instanceObject.InstanceDefinition?.Name ?? metadata.BlockDefinitionName;
            metadata = metadata with
            {
                BlockDefinitionName = string.IsNullOrWhiteSpace(metadata.BlockDefinitionName) ? definitionName : metadata.BlockDefinitionName,
                Identifier = string.IsNullOrWhiteSpace(metadata.Identifier) ? definitionName : metadata.Identifier,
                Name = string.IsNullOrWhiteSpace(metadata.Name) ? definitionName : metadata.Name
            };
        }

        HardwareMetadata.EnsureObjectMarked(doc, hardwareObject, metadata);
        var geometry = hardwareObject.Geometry.Duplicate();
        var bbox = geometry.GetBoundingBox(true);
        var layerName = CleanName(string.IsNullOrWhiteSpace(metadata.Identifier) ? metadata.Name : metadata.Identifier);
        return new HardwareCandidate
        {
            SourceObjectId = hardwareObject.Id,
            Name = string.IsNullOrWhiteSpace(metadata.Name) ? layerName : metadata.Name,
            Description = metadata.Description,
            SourcePath = metadata.SourcePath,
            BlockDefinitionName = metadata.BlockDefinitionName,
            Identifier = string.IsNullOrWhiteSpace(metadata.Identifier) ? layerName : metadata.Identifier,
            LayerName = layerName,
            MaterialId = MaterialAssignment.GetCategorizationMaterialId(hardwareObject.Attributes),
            GroupIndices = hardwareObject.Attributes.GetGroupList() ?? Array.Empty<int>(),
            Centroid = bbox.IsValid ? bbox.Center : Point3d.Origin,
            Geometry = geometry
        };
    }

    private Guid CopyHardwareToShopLayer(
        RhinoDoc doc,
        CreateAssemblyOptions options,
        string componentName,
        HardwareCandidate hardware,
        Transform assemblyTranslation)
    {
        var sourceObject = doc.Objects.FindId(hardware.SourceObjectId);
        if (sourceObject is null)
            return Guid.Empty;

        var geometry = sourceObject.Geometry.Duplicate();
        geometry.Transform(assemblyTranslation);
        var attributes = sourceObject.Attributes.Duplicate();
        attributes.LayerIndex = _layers.EnsureLayerIndex(
            doc,
            LayerService.ShopPart(options.AssemblyName, componentName, hardware.LayerName),
            Color.DarkGray);
        attributes.Name = string.IsNullOrWhiteSpace(attributes.Name) ? hardware.Name : attributes.Name;
        attributes.RemoveFromAllGroups();
        HardwareMetadata.Mark(attributes, new HardwareMetadataRecord(
            hardware.Identifier,
            hardware.Name,
            hardware.Description,
            hardware.SourcePath,
            hardware.BlockDefinitionName));
        ReferenceUpdateService.AttachReferenceUserStrings(attributes, hardware.SourceObjectId, assemblyTranslation);

        return doc.Objects.Add(geometry, attributes);
    }

    private static List<PartCandidate> BuildComponentFingerprintParts(ComponentCandidate component)
    {
        var result = component.Parts.ToList();
        result.AddRange(component.Hardware.Select(ToHardwareFingerprintCandidate));
        return result;
    }

    private static PartCandidate ToHardwareFingerprintCandidate(HardwareCandidate hardware)
    {
        return new PartCandidate
        {
            SourceObjectId = hardware.SourceObjectId,
            GeneratedObjectId = hardware.GeneratedObjectId,
            PartName = hardware.LayerName,
            Fingerprint = $"hardware:{NormalizeFingerprintToken(hardware.Identifier)}",
            MaterialId = hardware.MaterialId,
            GroupIndices = hardware.GroupIndices,
            Centroid = hardware.Centroid,
            Geometry = hardware.Geometry
        };
    }

    private IEnumerable<BlockPartGeometry> GetBlockPartGeometry(RhinoDoc doc, InstanceObject instanceObject)
    {
        if (instanceObject.Geometry is not InstanceReferenceGeometry instanceReference)
            yield break;

        var definition = instanceObject.InstanceDefinition;
        if (definition is null)
            yield break;

        foreach (var blockPart in GetDefinitionGeometry(doc, definition, instanceReference.Xform, definition.Name))
            yield return blockPart;
    }

    private IEnumerable<BlockPartGeometry> GetDefinitionGeometry(
        RhinoDoc doc,
        InstanceDefinition definition,
        Transform transform,
        string labelPrefix)
    {
        foreach (var definitionObject in definition.GetObjects())
        {
            if (definitionObject.Geometry is InstanceReferenceGeometry nestedReference)
            {
                var nestedDefinition = doc.InstanceDefinitions.Find(nestedReference.ParentIdefId, true);
                if (nestedDefinition is null)
                    continue;

                var nestedTransform = transform * nestedReference.Xform;
                foreach (var nestedPart in GetDefinitionGeometry(doc, nestedDefinition, nestedTransform, $"{labelPrefix}:{nestedDefinition.Name}"))
                    yield return nestedPart;
                continue;
            }

            var geometry = definitionObject.Geometry.Duplicate();
            geometry.Transform(transform);
            var label = string.IsNullOrWhiteSpace(definitionObject.Name)
                ? $"{labelPrefix}:{definitionObject.Id}"
                : $"{labelPrefix}:{definitionObject.Name}";
            yield return new BlockPartGeometry(geometry, definitionObject.Attributes, label);
        }
    }

    private static string NormalizeFingerprintToken(string value)
    {
        return CleanName(value).ToUpperInvariant();
    }

    private string CreateGroup(RhinoDoc doc, string assemblyName, string componentName, IEnumerable<Guid> objectIds)
    {
        var groupName = MakeUniqueGroupName(doc, $"AM_{assemblyName}_{componentName}");
        var groupIndex = doc.Groups.Add(groupName);
        if (groupIndex >= 0)
        {
            foreach (var objectId in objectIds)
                doc.Groups.AddToGroup(groupIndex, objectId);
        }

        return groupName;
    }

    private List<Guid> ExpandSelectedObjectsToGroups(RhinoDoc doc, IEnumerable<Guid> sourceObjectIds)
    {
        var expanded = new List<Guid>();
        var seen = new HashSet<Guid>();

        void Add(Guid id)
        {
            if (seen.Add(id))
                expanded.Add(id);
        }

        foreach (var sourceObjectId in sourceObjectIds)
        {
            var sourceObject = doc.Objects.FindId(sourceObjectId);
            if (sourceObject is null)
                continue;

            var groupList = sourceObject.Attributes.GetGroupList();
            if (groupList is null || groupList.Length == 0)
            {
                Add(sourceObjectId);
                continue;
            }

            foreach (var groupIndex in groupList)
            {
                foreach (var groupMember in doc.Objects.FindByGroup(groupIndex))
                    Add(groupMember.Id);
            }
        }

        return expanded;
    }

    private void CleanupLayerTreeIfEmpty(RhinoDoc doc, string layerPath)
    {
        foreach (var child in _layers.GetChildLayerPaths(doc, layerPath))
            CleanupLayerTreeIfEmpty(doc, child);

        _layers.TryDeleteLayerIfEmpty(doc, layerPath);
    }

    private string MakeUniqueGroupName(RhinoDoc doc, string baseName)
    {
        var clean = CleanName(baseName);
        var name = clean;
        var index = 1;
        while (doc.Groups.FindName(name) is not null)
        {
            name = $"{clean}_{index:00}";
            index++;
        }

        return name;
    }

    private static string GetGroupName(RhinoDoc doc, int groupIndex)
    {
        if (groupIndex < 0 || groupIndex >= doc.Groups.Count)
            return string.Empty;

        return doc.Groups.GroupName(groupIndex) ?? groupIndex.ToString();
    }

    private static string CleanName(string value)
    {
        var invalid = new[] { ':', ';', '"', '\'', '<', '>', '|', '?', '*', '\r', '\n', '\t' };
        var cleaned = invalid.Aggregate(value.Trim(), (current, c) => current.Replace(c, '_'));
        return string.IsNullOrWhiteSpace(cleaned) ? "Assembly" : cleaned;
    }

    private static string BuildNoValidPartsMessage(IEnumerable<string> warnings)
    {
        var warningList = warnings.Take(6).ToList();
        if (warningList.Count == 0)
            return "No closed polysurface or extrusion parts were selected.";

        return "No closed polysurface or extrusion parts were selected.\n" + string.Join("\n", warningList);
    }

    private static string ExportPartCategorizationDebugReport(
        RhinoDoc doc,
        string assemblyName,
        IReadOnlyList<PartCandidate> candidates,
        IReadOnlyList<List<PartCandidate>> partCategories,
        IReadOnlyList<string> warnings,
        AssemblyManagerSettingsRecord settings)
    {
        var report = new PartCategorizationDebugReport
        {
            AssemblyName = assemblyName,
            CreatedAt = DateTimeOffset.UtcNow,
            RhinoDocumentPath = doc.Path ?? string.Empty,
            Settings = settings,
            Warnings = warnings.ToList(),
            Candidates = candidates
                .Select((candidate, index) => BuildCandidateDebugRecord(doc, candidate, index))
                .ToList(),
            Categories = partCategories
                .OrderBy(group => group.First().PartName, StringComparer.OrdinalIgnoreCase)
                .Select(group => new PartCategoryDebugRecord
                {
                    PartName = group.First().PartName,
                    CategoryKey = CreateDebugPartCategoryKey(group.First()),
                    GeometryFingerprint = group.First().Fingerprint,
                    MaterialId = string.IsNullOrWhiteSpace(group.First().MaterialId) ? "UNASSIGNED" : group.First().MaterialId,
                    Quantity = group.Count(),
                    SourceObjectIds = group.Select(candidate => candidate.SourceObjectId).ToList(),
                    GeneratedObjectIds = group
                        .Select(candidate => candidate.GeneratedObjectId)
                        .Where(id => id != Guid.Empty)
                        .ToList()
                })
                .ToList()
        };

        var path = WriteDebugReport(doc, assemblyName, report);
        RhinoApp.WriteLine("Assembly Manager categorization debug report: {0}", path);
        return path;
    }

    private static CandidateCategorizationDebugRecord BuildCandidateDebugRecord(RhinoDoc doc, PartCandidate candidate, int index)
    {
        var rhinoObject = doc.Objects.FindId(candidate.SourceObjectId);
        var layerName = string.Empty;
        if (rhinoObject is not null && rhinoObject.Attributes.LayerIndex >= 0 && rhinoObject.Attributes.LayerIndex < doc.Layers.Count)
            layerName = doc.Layers[rhinoObject.Attributes.LayerIndex].FullPath;

        return new CandidateCategorizationDebugRecord
        {
            Index = index + 1,
            SourceObjectId = candidate.SourceObjectId,
            GeneratedObjectId = candidate.GeneratedObjectId,
            AssignedPartNumber = candidate.PartName,
            SourceObjectName = rhinoObject?.Name ?? string.Empty,
            ObjectType = rhinoObject?.Geometry.ObjectType.ToString() ?? string.Empty,
            SourceLayer = layerName,
            GroupIndices = candidate.GroupIndices.ToList(),
            GroupNames = candidate.GroupIndices.Select(groupIndex => GetGroupName(doc, groupIndex)).ToList(),
            MaterialId = string.IsNullOrWhiteSpace(candidate.MaterialId) ? "UNASSIGNED" : candidate.MaterialId,
            CategoryKey = CreateDebugPartCategoryKey(candidate),
            GeometryFingerprint = candidate.Fingerprint,
            Centroid = PointDebugRecord.FromPoint(candidate.Centroid),
            Fingerprint = candidate.FingerprintDebug
        };
    }

    private static string WriteDebugReport(RhinoDoc doc, string assemblyName, PartCategorizationDebugReport report)
    {
        var directory = GetDebugReportDirectory(doc);
        Directory.CreateDirectory(directory);

        var fileName = $"{CleanFileName(assemblyName)}_categorization_debug_{DateTime.Now:yyyyMMdd_HHmmss}.json";
        var path = Path.Combine(directory, fileName);
        var json = JsonSerializer.Serialize(report, DebugJsonOptions);
        File.WriteAllText(path, json);
        return path;
    }

    private static string GetDebugReportDirectory(RhinoDoc doc)
    {
        if (!string.IsNullOrWhiteSpace(doc.Path))
        {
            var documentDirectory = Path.GetDirectoryName(doc.Path);
            if (!string.IsNullOrWhiteSpace(documentDirectory))
                return Path.Combine(documentDirectory, "AssemblyManagerDebugReports");
        }

        var documents = System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments);
        return Path.Combine(documents, "AssemblyManagerDebugReports");
    }

    private static string CleanFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(value.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(clean) ? "Assembly" : clean;
    }

    private static string CreateDebugPartCategoryKey(PartCandidate candidate)
    {
        var materialId = string.IsNullOrWhiteSpace(candidate.MaterialId)
            ? "UNASSIGNED"
            : MaterialAssignment.NormalizeMaterialIdForCategory(candidate.MaterialId);
        var partName = string.IsNullOrWhiteSpace(candidate.PartName) ? "UNASSIGNED_PART" : candidate.PartName;
        return $"{partName}|material:{materialId}";
    }

    private static string CreatePartCategorySortKey(PartCandidate candidate)
    {
        var materialId = string.IsNullOrWhiteSpace(candidate.MaterialId)
            ? "UNASSIGNED"
            : MaterialAssignment.NormalizeMaterialIdForCategory(candidate.MaterialId);
        if (candidate.FingerprintDebug is null)
            return $"{materialId}|{candidate.Fingerprint}";

        var debug = candidate.FingerprintDebug;
        return string.Join("|",
            materialId,
            $"edges:{debug.EdgeLengths.Count}",
            $"points:{debug.ArrangementPointCount}",
            $"v:{FormatSortDouble(debug.Volume.Raw)}",
            $"a:{FormatSortDouble(debug.Area.Raw)}",
            $"d:{string.Join(",", debug.Dimensions.Select(value => FormatSortDouble(value.Raw)))}",
            candidate.Fingerprint);
    }

    private static string FormatSortDouble(double value)
    {
        return value.ToString("0.########", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static Color PartColorForIndex(int index, bool colorizeParts)
    {
        return colorizeParts
            ? LayerService.DefaultPartColors[Math.Max(0, index) % LayerService.DefaultPartColors.Length]
            : Color.Black;
    }

    private static Color PartColorForName(string partName, bool colorizeParts)
    {
        if (!colorizeParts)
            return Color.Black;

        var digits = new string(partName.Where(char.IsDigit).ToArray());
        if (!int.TryParse(digits, out var index))
            index = 1;

        return LayerService.DefaultPartColors[(Math.Max(1, index) - 1) % LayerService.DefaultPartColors.Length];
    }

    private sealed class PartCategorizationDebugReport
    {
        public string AssemblyName { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }
        public string RhinoDocumentPath { get; set; } = string.Empty;
        public AssemblyManagerSettingsRecord Settings { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
        public List<CandidateCategorizationDebugRecord> Candidates { get; set; } = new();
        public List<PartCategoryDebugRecord> Categories { get; set; } = new();
    }

    private sealed record BlockPartGeometry(GeometryBase Geometry, ObjectAttributes Attributes, string Label);

    private sealed class CandidateCategorizationDebugRecord
    {
        public int Index { get; set; }
        public Guid SourceObjectId { get; set; }
        public Guid GeneratedObjectId { get; set; }
        public string AssignedPartNumber { get; set; } = string.Empty;
        public string SourceObjectName { get; set; } = string.Empty;
        public string ObjectType { get; set; } = string.Empty;
        public string SourceLayer { get; set; } = string.Empty;
        public List<int> GroupIndices { get; set; } = new();
        public List<string> GroupNames { get; set; } = new();
        public string MaterialId { get; set; } = string.Empty;
        public string CategoryKey { get; set; } = string.Empty;
        public string GeometryFingerprint { get; set; } = string.Empty;
        public PointDebugRecord Centroid { get; set; } = new();
        public PartFingerprintDebugRecord? Fingerprint { get; set; }
    }

    private sealed class PartCategoryDebugRecord
    {
        public string PartName { get; set; } = string.Empty;
        public string CategoryKey { get; set; } = string.Empty;
        public string GeometryFingerprint { get; set; } = string.Empty;
        public string MaterialId { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public List<Guid> SourceObjectIds { get; set; } = new();
        public List<Guid> GeneratedObjectIds { get; set; } = new();
    }

    private sealed class PointDebugRecord
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }

        public static PointDebugRecord FromPoint(Point3d point)
        {
            return new PointDebugRecord
            {
                X = point.X,
                Y = point.Y,
                Z = point.Z
            };
        }
    }

    private sealed class CreateAssemblyProgress : IDisposable
    {
        private const int MaximumStep = 9;
        private bool _disposed;

        public CreateAssemblyProgress()
        {
            StatusBar.ShowProgressMeter(0, MaximumStep, "Creating assembly", true, true);
        }

        public void Update(int step, string message)
        {
            if (_disposed)
                return;

            RhinoApp.SetCommandPrompt(message);
            RhinoApp.WriteLine("Assembly Manager: {0}", message);
            StatusBar.SetMessagePane(message);
            StatusBar.UpdateProgressMeter(Math.Clamp(step, 0, MaximumStep), true);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            StatusBar.HideProgressMeter();
            StatusBar.ClearMessagePane();
            _disposed = true;
        }
    }
}
