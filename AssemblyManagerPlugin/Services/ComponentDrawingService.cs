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
    private readonly LinkedAssemblySafetyService? _linkSafety;

    public ComponentDrawingService(
        AssemblyRepository repository,
        LayerService layers,
        IActionHistorySink history,
        AssemblyLineageService lineage,
        LinkedAssemblySafetyService? linkSafety = null)
    {
        _repository = repository;
        _layers = layers;
        _history = history;
        _lineage = lineage;
        _linkSafety = linkSafety;
    }

    /// <summary>
    /// Places an additional drawing view, not another manufacturing occurrence. Each member
    /// is linked directly to the complete representative ORIGINAL ASSEMBLIES occurrence.
    /// </summary>
    public int PlaceComponent(RhinoDoc doc, string assemblyName, Guid componentId, Point3d location)
    {
        if (doc.ActiveSpace != ActiveSpace.ModelSpace)
            throw new InvalidOperationException("PlaceComponent must be run in model space. Activate a model view or layout detail before placing a component.");
        if (!location.IsValid)
            throw new ArgumentException("A valid component placement point is required.", nameof(location));
        _linkSafety?.EnsureEnabled();
        var store = _repository.Load(doc);
        var assembly = store.FindAssembly(assemblyName)
            ?? throw new InvalidOperationException($"Assembly '{assemblyName}' was not found.");
        _linkSafety?.EnsureCanUpdate(doc, assembly.Id);
        // Safety verification can resolve suspension conflicts in the persisted store.
        store = _repository.Load(doc);
        assembly = store.FindAssembly(assemblyName)!;
        var component = assembly.Components.SingleOrDefault(item => item.Id == componentId)
            ?? throw new InvalidOperationException("The selected component no longer exists. Select its current component number.");
        if (assembly.PendingComponentUpdates.Count > 0)
            throw new InvalidOperationException("This assembly has a staged component change. Run Update Assembly before placing another component view.");
        var sources = ValidatePlacementSources(doc, assembly, component);
        var rotation = FindPlanRotation(doc, sources.Select(item => item.Object.Id).ToList());
        var prepared = new List<(PlacementSource Source, GeometryBase Geometry)>();
        var addedIds = new List<Guid>();
        var groupIndex = -1;
        var saved = false;
        using var mutation = AssemblyLinkMutationGate.Enter();
        try
        {
            var bounds = BoundingBox.Empty;
            foreach (var source in sources)
            {
                var geometry = source.Object.Geometry.Duplicate();
                prepared.Add((source, geometry));
                if (!geometry.Transform(rotation))
                    throw new InvalidOperationException("A component member could not be oriented. No component view was placed.");
                bounds.Union(geometry.GetBoundingBox(true));
            }
            if (!bounds.IsValid)
                throw new InvalidOperationException("The component has no valid placement bounds.");
            var translation = Transform.Translation(location - bounds.Center);
            var parentToChild = translation * rotation;
            foreach (var (source, geometry) in prepared)
            {
                if (!geometry.Transform(translation))
                    throw new InvalidOperationException("A component member could not be positioned. No component view was placed.");
                using var attributes = source.Object.Attributes.Duplicate();
                attributes.RemoveFromAllGroups();
                AssemblyLineageService.ClearLinkMetadata(attributes);
                attributes.Space = ActiveSpace.ModelSpace;
                attributes.ViewportId = Guid.Empty;
                var color = _layers.FindPartLayerColor(doc, assemblyName, source.PartName)
                    ?? doc.Layers[source.Object.Attributes.LayerIndex].Color;
                attributes.LayerIndex = _layers.EnsurePartLayerIndex(doc,
                    LayerService.CopiedComponentPart(assemblyName, component.Name, source.PartName), color);
                var id = doc.Objects.Add(geometry, attributes);
                if (id == Guid.Empty)
                    throw new InvalidOperationException("Rhino could not add every component member. No component view was placed.");
                addedIds.Add(id);
                var link = _lineage.RegisterDerived(doc, assembly, source.Object.Id, source.Node.Role,
                    id, AssemblyLinkRoles.CopiedComponent, parentToChild,
                    recipe: source.IsHardware ? AssemblyLinkRecipes.HardwareCopy : AssemblyLinkRecipes.DirectCopy,
                    partId: source.Node.PartId, componentId: component.Id,
                    sourceComponentInstanceId: source.Node.SourceComponentInstanceId);
                link.Child.GeometryFingerprint = source.Node.GeometryFingerprint;
                if (doc.Objects.FindId(id)?.Attributes.GetUserString(AssemblyManagerConstants.LinkNodeIdUserString) != link.Child.Id.ToString("D"))
                    throw new InvalidOperationException("Rhino could not store the new component's tracking metadata.");
            }
            groupIndex = doc.Groups.Add(CreateGroupName(assemblyName, component.Name), addedIds);
            if (groupIndex < 0 || (doc.Groups.GroupMembers(groupIndex)?.Length ?? 0) != addedIds.Count)
                throw new InvalidOperationException("Rhino could not group every member of the new component view.");
            assembly.UpdatedAt = DateTimeOffset.UtcNow;
            _repository.Save(doc, store);
            saved = true;
        }
        catch
        {
            if (!saved)
            {
                if (groupIndex >= 0)
                    doc.Groups.Delete(groupIndex);
                foreach (var id in addedIds)
                {
                    var added = doc.Objects.FindId(id);
                    if (added is not null)
                        doc.Objects.Delete(added, true, true);
                }
            }
            throw;
        }
        finally
        {
            foreach (var (_, geometry) in prepared)
                geometry.Dispose();
        }
        _history.Record(doc, new ActionHistoryEntry
        {
            CommandName = "PlaceComponent", AssemblyName = assemblyName,
            Summary = $"Placed an additional linked drawing view of {component.Name} ({addedIds.Count} members); manufacturing quantities are unchanged."
        });
        doc.Views.Redraw();
        return addedIds.Count;
    }

    private static List<PlacementSource> ValidatePlacementSources(RhinoDoc doc, AssemblyRecord assembly, ComponentRecord component)
    {
        const string review = "The component's representative is incomplete or needs link review. Run Update Assembly and resolve its link issues before using PlaceComponent.";
        var mapping = component.RepresentativeObjectIdsByPartName;
        if (mapping.Count == 0 || mapping.Count != component.PartQuantities.Count ||
            component.PartQuantities.Any(pair => pair.Value <= 0 || !mapping.TryGetValue(pair.Key, out var ids) || ids.Count != pair.Value))
            throw new InvalidOperationException(review);
        var sources = new List<PlacementSource>();
        var idsSeen = new HashSet<Guid>();
        foreach (var (partName, ids) in mapping)
        foreach (var id in ids)
        {
            var obj = doc.Objects.FindId(id);
            var node = assembly.LinkGraph.Nodes.SingleOrDefault(item => item.ObjectId == id);
            if (!idsSeen.Add(id) || obj is null || obj.Attributes.Space != ActiveSpace.ModelSpace || node is null ||
                node.ComponentId != component.Id || node.SourceComponentInstanceId == Guid.Empty ||
                node.Role is not (AssemblyLinkRoles.OriginalAssembly or AssemblyLinkRoles.Hardware) ||
                node.Status != AssemblyLinkStatuses.Active || node.Metadata.ContainsKey(AssemblyLinkMetadataKeys.Quarantined))
                throw new InvalidOperationException(review);
            var hardware = HardwareMetadata.TryGetFromObject(obj, out _);
            if (hardware ? node.PartId != Guid.Empty :
                !assembly.Parts.Any(part => part.Id == node.PartId && string.Equals(part.Name, partName, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException(review);
            var incoming = assembly.LinkGraph.Edges.Where(edge => edge.ChildNodeId == node.Id).ToList();
            var edge = incoming.Count == 1 ? incoming[0] : null;
            if (edge?.Recipe == AssemblyLinkRecipes.BlockDefinitionPart)
                throw new InvalidOperationException("This component contains manufacturing parts extracted from a block definition. Their source links do not yet identify a safe regeneration path. Recreate those manufacturing inputs as individual solids before placing tracked component views; whole hardware blocks are supported.");
            var input = edge is null ? null : assembly.LinkGraph.Nodes.SingleOrDefault(item => item.Id == edge.ParentNodeId);
            var inputObject = input is null ? null : doc.Objects.FindId(input.ObjectId);
            if (edge is null || input is null || inputObject is null || input.Role != AssemblyLinkRoles.Source ||
                input.Status != AssemblyLinkStatuses.Active || edge.Status != AssemblyLinkStatuses.Active ||
                input.SourceComponentInstanceId != node.SourceComponentInstanceId ||
                input.Metadata.ContainsKey(AssemblyLinkMetadataKeys.Quarantined) ||
                !edge.ParentToChildTransform.TryToTransform(out var placement) ||
                !ComponentPlacementMatcher.MatchesGeometry(inputObject, obj, placement, doc.ModelAbsoluteTolerance) ||
                MaterialAssignment.GetCategorizationMaterialId(inputObject.Attributes) != MaterialAssignment.GetCategorizationMaterialId(obj.Attributes) ||
                assembly.LinkGraph.Conflicts.Any(conflict => conflict.Status == AssemblyLinkStatuses.Open &&
                    (conflict.NodeId == node.Id || conflict.NodeId == input.Id || conflict.EdgeId == edge.Id)))
                throw new InvalidOperationException(review);
            sources.Add(new PlacementSource(obj, node, partName, hardware));
        }
        var instances = sources.Select(item => item.Node.SourceComponentInstanceId).Distinct().ToArray();
        if (instances.Length != 1)
            throw new InvalidOperationException(review);
        var instance = assembly.LinkGraph.SourceComponentInstances.SingleOrDefault(item => item.Id == instances[0]);
        if (instance is null || instance.ComponentId != component.Id || instance.Status != AssemblyLinkStatuses.Active ||
            !assembly.LinkGraph.Nodes.Where(node => node.SourceComponentInstanceId == instance.Id &&
                node.Role is AssemblyLinkRoles.OriginalAssembly or AssemblyLinkRoles.Hardware).Select(node => node.ObjectId).ToHashSet().SetEquals(idsSeen))
            throw new InvalidOperationException(review);
        var group = doc.Groups.FindId(instance.GeneratedGroupId);
        if (group is null || group.IsDeleted ||
            !(doc.Groups.GroupMembers(group.Index) ?? Array.Empty<RhinoObject>()).Select(obj => obj.Id).ToHashSet().SetEquals(idsSeen))
            throw new InvalidOperationException("The representative component is ungrouped or has unregistered additions. Regroup it, run Update Component and Update Assembly before placing another view.");
        return sources;
    }

    private sealed record PlacementSource(RhinoObject Object, AssemblyLinkNodeRecord Node, string PartName, bool IsHardware);

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
                    recipe: doc.Objects.FindId(copiedObject.ParentObjectId) is { } parent &&
                        HardwareMetadata.TryGetFromObject(parent, out _)
                        ? AssemblyLinkRecipes.HardwareCopy : AssemblyLinkRecipes.DirectCopy,
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
        var rotation = FindPlanRotation(doc, objectIds);
        if (rotation != Transform.Identity)
            ApplyTransform(doc, copiedObjects, rotation);
        return rotation;
    }

    private static Transform FindPlanRotation(RhinoDoc doc, IReadOnlyList<Guid> objectIds)
    {
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

        return Transform.Rotation(RhinoMath.ToRadians(bestAngle), Vector3d.ZAxis, center);
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
