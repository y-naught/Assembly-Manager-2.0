using System.Drawing;
using System.Text.RegularExpressions;
using AssemblyManagerPlugin.Core;
using AssemblyManagerPlugin.Geometry;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace AssemblyManagerPlugin.Services;

/// <summary>
/// Maintains an already-created PARTS layout without rerunning the layout command. Existing
/// representatives keep their UUID and placement; newly split categories are appended in free
/// space. Only graph-owned geometry and Gazelle-owned annotations may be retired.
/// </summary>
public sealed class FlatPartSynchronizationService
{
    private readonly LayerService _layers;
    private readonly GeometryFingerprintService _fingerprints;
    private readonly IMaterialLibrary _materials;
    private readonly AssemblyLineageService _lineage;
    private readonly PluginSettingsService? _settings;

    public FlatPartSynchronizationService(
        LayerService layers,
        GeometryFingerprintService fingerprints,
        IMaterialLibrary materials,
        AssemblyLineageService lineage,
        PluginSettingsService? settings = null)
    {
        _layers = layers;
        _fingerprints = fingerprints;
        _materials = materials;
        _lineage = lineage;
        _settings = settings;
    }

    public FlatPartSynchronizationResult Synchronize(
        RhinoDoc doc,
        AssemblyStore store,
        AssemblyRecord assembly,
        AssemblyRecord? previousAssembly = null,
        IReadOnlySet<Guid>? protectedPartIds = null)
    {
        var graph = assembly.LinkGraph;
        var existingNodes = graph.Nodes.Where(node => IsFlat(node) &&
            doc.Objects.FindId(node.ObjectId) is { IsDeleted: false }).ToList();
        if (existingNodes.Count == 0)
            return FlatPartSynchronizationResult.Empty;

        using var mutation = AssemblyLinkMutationGate.Enter();
        previousAssembly ??= assembly;
        var previousHeaders = CapturePreviousHeaderTexts(doc, assembly, previousAssembly, existingNodes);
        var settings = _settings?.Load() ?? new PluginSettingsRecord();
        var spacing = Math.Max(settings.LayPartsFlat.PartSpacing, 1.0);
        var layoutBounds = BoundingBox.Empty;
        foreach (var node in existingNodes)
            layoutBounds.Union(doc.Objects.FindId(node.ObjectId)!.Geometry.GetBoundingBox(true));
        var xCursor = layoutBounds.IsValid ? layoutBounds.Max.X + spacing : 200.0;
        var yAnchor = layoutBounds.IsValid ? layoutBounds.Min.Y : 8.0;
        var changedIds = new HashSet<Guid>();
        var retainedIds = new HashSet<Guid>();
        var completedParts = new HashSet<Guid>();
        var added = 0;
        var removed = 0;
        var review = false;
        var rows = new List<FlatRowItem>();

        foreach (var part in assembly.Parts.Where(part => part.Quantity > 0).OrderBy(part => part.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (protectedPartIds?.Contains(part.Id) == true)
            {
                review = true;
                continue;
            }
            var candidates = existingNodes.Where(node => node.PartId == part.Id)
                .OrderByDescending(node => previousAssembly.LinkGraph.Nodes.Any(previous =>
                    previous.ObjectId == node.ObjectId && previous.PartId == part.Id))
                .ThenBy(node => node.Id).ToList();
            var output = candidates.FirstOrDefault(node => IsActive(node.Status));
            if (output is null && candidates.Count > 0)
            {
                AddReview(assembly, candidates[0], $"The flat output for {part.Name} is inactive or needs review. Gazelle preserved it instead of replacing an operator edit.");
                review = true;
                continue; // Do not silently replace a quarantined/operator-edited output.
            }

            var incomingEdges = output is null ? new List<AssemblyLinkEdgeRecord>() : graph.Edges.Where(edge =>
                edge.ChildNodeId == output.Id && IsActive(edge.Status)).ToList();
            if (incomingEdges.Count > 1)
            {
                AddReview(assembly, output!, "This flat representative has multiple active source links. Gazelle preserved it instead of choosing an ambiguous rebuild source.");
                review = true;
                continue;
            }
            var incoming = incomingEdges.SingleOrDefault();
            if (output is not null && (incoming is null ||
                !string.Equals(incoming.Recipe, AssemblyLinkRecipes.LayFlat, StringComparison.OrdinalIgnoreCase)))
            {
                AddReview(assembly, output, "This flat representative has no active supported lay-flat recipe. Gazelle preserved its geometry and placement for review.");
                review = true;
                continue;
            }
            // A valid flat output can descend from either an original or a copied component.
            // Its saved transform belongs to that exact parent frame, not an interchangeable
            // occurrence of the same part elsewhere in the model.
            var parent = incoming is null ? graph.Nodes.FirstOrDefault(node => node.PartId == part.Id && IsOriginal(node) &&
                IsActive(node.Status) && doc.Objects.FindId(node.ObjectId) is { IsDeleted: false }) :
                graph.Nodes.FirstOrDefault(node => node.Id == incoming.ParentNodeId && node.PartId == part.Id && IsActive(node.Status));
            var source = parent is null ? null : doc.Objects.FindId(parent.ObjectId);
            if (source is null || !_fingerprints.TryDuplicateManufacturableBrep(source, out var sourceBrep, out _))
            {
                AddReview(assembly, output ?? parent, $"Gazelle could not find a supported live representative for flat part {part.Name}; its existing output was preserved.");
                review = true;
                continue;
            }
            using var ownedSourceGeometry = sourceBrep;
            if (incoming is not null && !incoming.ParentToChildTransform.TryToTransform(out _))
            {
                AddReview(assembly, output, $"The saved lay-flat transform for {part.Name} is invalid; Gazelle preserved its placement for review.");
                review = true;
                continue;
            }

            var currentObject = output is null ? null : doc.Objects.FindId(output.ObjectId);
            if (!TryPrepareGeometry(sourceBrep, currentObject, incoming, doc.ModelAbsoluteTolerance,
                    new Point3d(xCursor, yAnchor, 0.0), out var geometry, out var transform))
            {
                geometry.Dispose();
                AddReview(assembly, output ?? parent, $"Gazelle could not safely flatten the revised geometry for {part.Name}; its prior output was preserved.");
                review = true;
                continue;
            }
            using var ownedFlatGeometry = geometry;

            var color = _layers.GetOrAssignPartColor(doc, assembly, part, settings.AssemblyManager.ColorizeParts);
            var partPath = LayerService.PartsPart(assembly.Name, part.Name);
            _layers.EnsurePartLayerIndex(doc, partPath, color);
            using var attributes = currentObject?.Attributes.Duplicate() ?? source.Attributes.Duplicate();
            attributes.LayerIndex = _layers.EnsurePartLayerIndex(doc, $"{partPath}::3D", color);
            attributes.Name = part.Name;
            if (currentObject is null)
                attributes.RemoveFromAllGroups();
            MaterialAssignment.Copy(source.Attributes, attributes);
            MaterialAssignment.NormalizeToParentMaterial(attributes);

            Guid outputId;
            if (currentObject is null)
            {
                AssemblyLineageService.ClearLinkMetadata(attributes);
                outputId = doc.Objects.AddBrep(geometry, attributes);
                if (outputId == Guid.Empty)
                {
                    AddReview(assembly, parent, $"Rhino could not create the new flat representative for {part.Name}.");
                    review = true;
                    continue;
                }
                added++;
                xCursor = geometry.GetBoundingBox(true).Max.X + spacing;
            }
            else
            {
                outputId = currentObject.Id;
                var geometryChanged = !GeometryBase.GeometryEquals(currentObject.Geometry, geometry);
                var materialChanged = MaterialAssignment.GetMaterialId(currentObject.Attributes) != MaterialAssignment.GetMaterialId(attributes) ||
                                      MaterialAssignment.GetDisplayName(currentObject.Attributes) != MaterialAssignment.GetDisplayName(attributes);
                if ((geometryChanged && !doc.Objects.Replace(outputId, geometry)) || !doc.Objects.ModifyAttributes(outputId, attributes, true))
                {
                    AddReview(assembly, output, $"Rhino could not finish updating the flat geometry or attributes for {part.Name}.");
                    review = true;
                    continue;
                }
                if (geometryChanged || materialChanged)
                    changedIds.Add(outputId);
            }

            var registration = _lineage.RegisterDerived(doc, assembly, source.Id, parent!.Role,
                outputId, AssemblyLinkRoles.FlatPart, transform, AssemblyLinkRecipes.LayFlat, partId: part.Id,
                recipeMetadata: new Dictionary<string, string>
                {
                    ["orientation"] = "LargestFaceToWorldXY",
                    ["longAxis"] = "Y",
                    ["layout"] = "MaterialThicknessRow"
                });
            ResolveReview(assembly, registration.Child.Id);
            ResolveReview(assembly, registration.Parent.Id);
            registration.Child.GeometryFingerprint = _fingerprints.CreatePartFingerprint(geometry);
            part.MaterialThickness = _fingerprints.GetMaterialThickness(sourceBrep);
            part.CamObjectIds = new List<Guid> { outputId };
            retainedIds.Add(outputId);
            completedParts.Add(part.Id);
            if (currentObject is null)
                changedIds.Add(outputId);

            var materialLabel = GetMaterialLabel(doc, part, source);
            var bounds = geometry.GetBoundingBox(true);
            if (!SynchronizePartLabel(doc, assembly, previousAssembly, part, outputId, bounds, materialLabel))
            {
                AddReview(assembly, registration.Child, $"The flat geometry for {part.Name} updated, but Rhino could not finish updating its quantity/material label.");
                review = true;
            }
            foreach (var reference in assembly.GeometryReferences.Where(reference => reference.TargetObjectId == outputId))
            {
                reference.SourceObjectId = source.Id;
                reference.PartName = part.Name;
                reference.SourceToTargetTransform = TransformRecord.FromTransform(transform);
                reference.UpdatedAt = DateTimeOffset.UtcNow;
            }
            rows.Add(new FlatRowItem(materialLabel, part.MaterialThickness, bounds));
        }

        foreach (var node in existingNodes.Where(node => !retainedIds.Contains(node.ObjectId)))
        {
            if (protectedPartIds?.Contains(node.PartId) == true)
                continue;
            var livePart = assembly.Parts.FirstOrDefault(part => part.Id == node.PartId && part.Quantity > 0);
            if ((livePart is not null && !completedParts.Contains(livePart.Id)) || !IsActive(node.Status))
            {
                if (livePart is not null && !livePart.CamObjectIds.Contains(node.ObjectId))
                    livePart.CamObjectIds.Add(node.ObjectId);
                continue;
            }
            if (HasDependents(store, assembly, node))
            {
                AddReview(assembly, node, "This superseded flat representative is still used by another linked object. Gazelle preserved it; review that dependency before removing it.");
                if (livePart is not null && !livePart.CamObjectIds.Contains(node.ObjectId))
                    livePart.CamObjectIds.Add(node.ObjectId);
                if (livePart is not null && doc.Objects.FindId(node.ObjectId) is { } retainedObject)
                    SynchronizePartLabel(doc, assembly, previousAssembly, livePart, node.ObjectId,
                        retainedObject.Geometry.GetBoundingBox(true), GetMaterialLabel(doc, livePart, retainedObject));
                review = true;
                continue;
            }

            var oldPart = FindPreviousPart(previousAssembly, node.ObjectId);
            if (doc.Objects.Delete(node.ObjectId, true))
            {
                _lineage.DetachDerivedObjects(doc, assembly, new[] { node.ObjectId });
                assembly.GeometryReferences.RemoveAll(reference => reference.TargetObjectId == node.ObjectId);
                DeleteOwnedPartLabels(doc, assembly, oldPart, node.ObjectId);
                removed++;
            }
            else
            {
                AddReview(assembly, node, "Rhino could not remove a superseded flat representative. Its linked geometry was preserved for review.");
                review = true;
            }
        }

        // Header text may no longer describe a single material after a category/material split.
        // Rebuild only our headers; part geometry and hand-placed/user annotations stay put.
        if (completedParts.Count == assembly.Parts.Count(part => part.Quantity > 0))
        {
            if (!SynchronizeRowHeaders(doc, assembly, previousHeaders, rows))
            {
                AddReview(assembly, null, "Rhino could not finish updating the flat-layout material row headings. The prior headings were preserved.");
                review = true;
            }
        }
        CleanupRetiredLayers(doc, assembly, previousAssembly);
        return new FlatPartSynchronizationResult(changedIds.ToArray(), added, removed, review);
    }

    private bool TryPrepareGeometry(Brep source, RhinoObject? currentObject, AssemblyLinkEdgeRecord? edge,
        double tolerance, Point3d newAnchor, out Brep geometry, out Transform transform)
    {
        geometry = source.DuplicateBrep();
        transform = Transform.Identity;
        var anchor = currentObject?.Geometry.GetBoundingBox(true).Min ?? newAnchor;
        if (edge is not null && edge.ParentToChildTransform.TryToTransform(out var prior))
        {
            if (geometry.Transform(prior) && IsFlat(geometry, tolerance))
            {
                var bounds = geometry.GetBoundingBox(true);
                var placement = Transform.Translation(anchor - bounds.Min);
                if (geometry.Transform(placement))
                {
                    transform = placement * prior;
                    return true;
                }
            }
            geometry.Dispose();
            geometry = source.DuplicateBrep();
        }

        var orientation = TransformUtilities.OrientLargestFaceToWorldXY(geometry, _fingerprints, Point3d.Origin);
        if (!geometry.Transform(orientation))
            return false;
        var rotation = TransformUtilities.RotateLongDimensionToY(geometry);
        var preparedBounds = geometry.GetBoundingBox(true);
        if (!preparedBounds.IsValid)
            return false;
        var translation = Transform.Translation(anchor - preparedBounds.Min);
        transform = translation * rotation * orientation;
        return geometry.Transform(translation);
    }

    private bool IsFlat(Brep geometry, double tolerance)
    {
        var bounds = geometry.GetBoundingBox(true);
        if (!bounds.IsValid || !_fingerprints.TryGetLargestFacePlane(geometry, out var plane))
            return false;
        var normal = plane.Normal;
        return normal.Unitize() && Math.Abs(normal.Z) >= Math.Cos(Math.PI / 1800.0) &&
               Math.Abs((bounds.Max.Z - bounds.Min.Z) - _fingerprints.GetMaterialThickness(geometry)) <= Math.Max(tolerance, RhinoMath.ZeroTolerance);
    }

    private bool SynchronizePartLabel(RhinoDoc doc, AssemblyRecord assembly, AssemblyRecord previousAssembly,
        PartRecord part, Guid outputId, BoundingBox bounds, string materialLabel)
    {
        var oldPart = FindPreviousPart(previousAssembly, outputId);
        var labels = FindPartLabels(doc, assembly, oldPart, outputId).ToList();
        var layer = _layers.EnsureLayerIndex(doc, $"{LayerService.PartsPart(assembly.Name, part.Name)}::text", Color.Black);
        var text = FlatPartAnnotations.FormatPartText(part, part.MaterialThickness, materialLabel);
        if (labels.Count > 0)
        {
            var succeeded = true;
            foreach (var label in labels)
            {
                using var attributes = label.Attributes.Duplicate();
                attributes.LayerIndex = layer;
                FlatPartAnnotations.Attach(attributes, assembly.Id, part.Id, outputId, FlatPartAnnotations.PartLabel);
                if (!string.Equals(((TextEntity)label.Geometry).PlainText.Replace("\r", string.Empty), text, StringComparison.Ordinal))
                {
                    using var entity = (TextEntity)label.Geometry.Duplicate();
                    entity.PlainText = text;
                    if (!doc.Objects.Replace(label.Id, entity))
                    {
                        succeeded = false;
                        continue;
                    }
                }
                succeeded &= doc.Objects.ModifyAttributes(label.Id, attributes, true);
            }
            return succeeded;
        }

        using var newText = new TextEntity
        {
            PlainText = text,
            Plane = new Plane(new Point3d(bounds.Center.X, bounds.Min.Y - 4.0, bounds.Min.Z), Vector3d.ZAxis),
            TextHeight = 0.125,
            DimensionScale = 12.0,
            Justification = TextJustification.TopCenter
        };
        using var newAttributes = new ObjectAttributes { LayerIndex = layer, ColorSource = ObjectColorSource.ColorFromObject, ObjectColor = Color.Black };
        FlatPartAnnotations.Attach(newAttributes, assembly.Id, part.Id, outputId, FlatPartAnnotations.PartLabel);
        return doc.Objects.AddText(newText, newAttributes) != Guid.Empty;
    }

    private IEnumerable<RhinoObject> FindPartLabels(RhinoDoc doc, AssemblyRecord assembly, PartRecord? oldPart, Guid outputId)
    {
        var objects = _layers.GetObjectIdsInLayerTree(doc, LayerService.PartsAssembly(assembly.Name))
            .Select(doc.Objects.FindId).Where(obj => obj?.Geometry is TextEntity).Cast<RhinoObject>().ToList();
        var owned = objects.Where(obj => FlatPartAnnotations.IsOwned(obj.Attributes, assembly.Id, FlatPartAnnotations.PartLabel) &&
                                        obj.Attributes.GetUserString(FlatPartAnnotations.OutputKey) == outputId.ToString("D")).ToList();
        if (owned.Count > 0 || oldPart is null)
            return owned;
        var priorLayer = $"{LayerService.PartsPart(assembly.Name, oldPart.Name)}::text";
        // Older Gazelle releases did not tag labels. Adopt only the recognizable generated
        // label on its exact managed layer, never arbitrary text beneath that layer tree.
        return objects.Where(obj => string.IsNullOrWhiteSpace(obj.Attributes.GetUserString(FlatPartAnnotations.KindKey)) &&
            string.Equals(doc.Layers[obj.Attributes.LayerIndex].FullPath, priorLayer, StringComparison.OrdinalIgnoreCase) &&
            FlatPartAnnotations.IsLegacyPartText(((TextEntity)obj.Geometry).PlainText, oldPart.Name)).Take(1);
    }

    private void DeleteOwnedPartLabels(RhinoDoc doc, AssemblyRecord assembly, PartRecord? oldPart, Guid outputId)
    {
        foreach (var obj in FindPartLabels(doc, assembly, oldPart, outputId).ToList())
            doc.Objects.Delete(obj.Id, true);
    }

    private HashSet<string> CapturePreviousHeaderTexts(RhinoDoc doc, AssemblyRecord assembly,
        AssemblyRecord previousAssembly, IEnumerable<AssemblyLinkNodeRecord> outputs)
    {
        var previousHeaders = previousAssembly.Parts.Select(part => FlatPartAnnotations.FormatRowText(
            string.IsNullOrWhiteSpace(part.MaterialId) ? "TBD" : _materials.GetMaterialLabel(doc, part.MaterialId), part.MaterialThickness)).ToHashSet(StringComparer.Ordinal);
        foreach (var output in outputs)
        {
            var oldPart = FindPreviousPart(previousAssembly, output.ObjectId);
            foreach (var label in FindPartLabels(doc, assembly, oldPart, output.ObjectId))
            {
                var lines = ((TextEntity)label.Geometry).PlainText.Replace("\r", string.Empty).Split('\n');
                if (lines.Length != 3)
                    continue;
                var delimiter = lines[2].IndexOf("\" | ", StringComparison.Ordinal);
                if (delimiter >= 0)
                    previousHeaders.Add($"{lines[2][(delimiter + 4)..]} | {lines[2][..delimiter]}\"");
            }
        }
        return previousHeaders;
    }

    private bool SynchronizeRowHeaders(RhinoDoc doc, AssemblyRecord assembly, HashSet<string> previousHeaders, List<FlatRowItem> rows)
    {
        var path = $"{LayerService.PartsAssembly(assembly.Name)}::row labels";
        var oldIds = _layers.GetObjectIdsInLayerTree(doc, path).Where(id =>
        {
            var obj = doc.Objects.FindId(id);
            return obj?.Geometry is TextEntity entity &&
                   string.Equals(doc.Layers[obj.Attributes.LayerIndex].FullPath, path, StringComparison.OrdinalIgnoreCase) &&
                   (FlatPartAnnotations.IsOwned(obj.Attributes, assembly.Id, FlatPartAnnotations.RowHeader) ||
                    (string.IsNullOrWhiteSpace(obj.Attributes.GetUserString(FlatPartAnnotations.KindKey)) && previousHeaders.Contains(entity.PlainText)));
        }).ToList();
        var createdAll = true;
        foreach (var group in rows.GroupBy(row => $"{row.Material}|{Math.Round(row.Thickness, 3):0.###}|{Math.Round(row.Bounds.Min.Y, 3):0.###}", StringComparer.OrdinalIgnoreCase))
        {
            var bounds = BoundingBox.Empty;
            foreach (var row in group)
                bounds.Union(row.Bounds);
            var first = group.First();
            using var entity = new TextEntity
            {
                PlainText = FlatPartAnnotations.FormatRowText(first.Material, first.Thickness),
                Plane = new Plane(new Point3d(bounds.Min.X, bounds.Max.Y + 2.0, bounds.Min.Z), Vector3d.ZAxis),
                TextHeight = 0.125,
                DimensionScale = 12.0,
                Justification = TextJustification.TopLeft
            };
            using var attributes = new ObjectAttributes { LayerIndex = _layers.EnsureLayerIndex(doc, path, Color.Black), ColorSource = ObjectColorSource.ColorFromObject, ObjectColor = Color.Black };
            FlatPartAnnotations.Attach(attributes, assembly.Id, Guid.Empty, Guid.Empty, FlatPartAnnotations.RowHeader);
            createdAll &= doc.Objects.AddText(entity, attributes) != Guid.Empty;
        }
        if (createdAll)
            foreach (var id in oldIds)
                createdAll &= doc.Objects.Delete(id, true);
        return createdAll;
    }

    private void CleanupRetiredLayers(RhinoDoc doc, AssemblyRecord assembly, AssemblyRecord previousAssembly)
    {
        foreach (var part in previousAssembly.Parts)
        {
            var path = LayerService.PartsPart(assembly.Name, part.Name);
            _layers.TryDeleteLayerIfEmpty(doc, $"{path}::3D");
            _layers.TryDeleteLayerIfEmpty(doc, $"{path}::text");
            _layers.TryDeleteLayerIfEmpty(doc, path);
        }
    }

    private string GetMaterialLabel(RhinoDoc doc, PartRecord part, RhinoObject original)
    {
        if (!string.IsNullOrWhiteSpace(part.MaterialId))
            return _materials.GetMaterialLabel(doc, part.MaterialId);
        var label = MaterialAssignment.GetDisplayName(original.Attributes);
        return string.IsNullOrWhiteSpace(label) ? "TBD" : label;
    }

    private static PartRecord? FindPreviousPart(AssemblyRecord previous, Guid outputId)
    {
        var node = previous.LinkGraph.Nodes.FirstOrDefault(node => node.ObjectId == outputId && IsFlat(node));
        return previous.Parts.FirstOrDefault(part => part.Id == node?.PartId || part.CamObjectIds.Contains(outputId));
    }

    private static bool HasDependents(AssemblyStore store, AssemblyRecord assembly, AssemblyLinkNodeRecord node) =>
        assembly.LinkGraph.Edges.Any(edge => edge.ParentNodeId == node.Id) ||
        store.Assemblies.Any(other => other.GeometryReferences.Any(reference => reference.SourceObjectId == node.ObjectId)) ||
        store.Assemblies.Where(other => other.Id != assembly.Id).Any(other => other.LinkGraph.Nodes.Any(candidate =>
            candidate.ObjectId == node.ObjectId));

    private static void AddReview(AssemblyRecord assembly, AssemblyLinkNodeRecord? node, string message)
    {
        var nodeId = node?.Id ?? Guid.Empty;
        if (assembly.LinkGraph.Conflicts.Any(conflict => conflict.NodeId == nodeId && conflict.Message == message && conflict.Status == AssemblyLinkStatuses.Open))
            return;
        assembly.LinkGraph.Conflicts.Add(new LinkConflictRecord
        {
            ConflictType = AssemblyLinkConflictTypes.DerivedGeometryOutOfDate,
            NodeId = nodeId,
            Message = message,
            Metadata = new Dictionary<string, string> { ["FlatPartSynchronization"] = bool.TrueString }
        });
    }

    private static void ResolveReview(AssemblyRecord assembly, Guid nodeId)
    {
        foreach (var conflict in assembly.LinkGraph.Conflicts.Where(conflict => conflict.NodeId == nodeId &&
                     conflict.Status == AssemblyLinkStatuses.Open && conflict.Metadata.ContainsKey("FlatPartSynchronization")))
        {
            conflict.Status = AssemblyLinkStatuses.Resolved;
            conflict.ResolvedAt = DateTimeOffset.UtcNow;
        }
    }

    private static bool IsFlat(AssemblyLinkNodeRecord node) => string.Equals(node.Role, AssemblyLinkRoles.FlatPart, StringComparison.OrdinalIgnoreCase);
    private static bool IsOriginal(AssemblyLinkNodeRecord node) => string.Equals(node.Role, AssemblyLinkRoles.OriginalAssembly, StringComparison.OrdinalIgnoreCase);
    private static bool IsActive(string status) => string.Equals(status, AssemblyLinkStatuses.Active, StringComparison.OrdinalIgnoreCase);
    private sealed record FlatRowItem(string Material, double Thickness, BoundingBox Bounds);
}

public sealed record FlatPartSynchronizationResult(IReadOnlyList<Guid> UpdatedObjectIds, int AddedOutputs, int RemovedOutputs, bool RequiresReview)
{
    public static readonly FlatPartSynchronizationResult Empty = new(Array.Empty<Guid>(), 0, 0, false);
}

public static class FlatPartAnnotations
{
    public const string AssemblyKey = "Gazelle.FlatAnnotation.AssemblyId";
    public const string PartKey = "Gazelle.FlatAnnotation.PartId";
    public const string OutputKey = "Gazelle.FlatAnnotation.OutputId";
    public const string KindKey = "Gazelle.FlatAnnotation.Kind";
    public const string PartLabel = "PartLabel";
    public const string RowHeader = "RowHeader";

    public static void Attach(ObjectAttributes attributes, Guid assemblyId, Guid partId, Guid outputId, string kind)
    {
        attributes.SetUserString(AssemblyKey, assemblyId.ToString("D"));
        attributes.SetUserString(PartKey, partId.ToString("D"));
        attributes.SetUserString(OutputKey, outputId.ToString("D"));
        attributes.SetUserString(KindKey, kind);
    }

    public static bool IsOwned(ObjectAttributes attributes, Guid assemblyId, string kind) =>
        attributes.GetUserString(AssemblyKey) == assemblyId.ToString("D") && attributes.GetUserString(KindKey) == kind;

    public static string FormatPartText(PartRecord part, double thickness, string material) =>
        $"{part.Name}\nQTY : {part.Quantity}\n{Math.Round(thickness, 3):0.###}\" | {material}";

    public static string FormatRowText(string material, double thickness) =>
        $"{(string.IsNullOrWhiteSpace(material) ? "TBD" : material)} | {Math.Round(thickness, 3):0.###}\"";

    public static bool IsLegacyPartText(string text, string partName) => Regex.IsMatch(text.Replace("\r", string.Empty),
        $"^{Regex.Escape(partName)}\\nQTY : \\d+\\n[0-9.,]+\\\" \\| [^\\n]+$", RegexOptions.CultureInvariant);

    public static bool IsLegacyRowText(string text) => Regex.IsMatch(text,
        "^[^\\r\\n]+ \\| [0-9.,]+\\\"$", RegexOptions.CultureInvariant);
}
