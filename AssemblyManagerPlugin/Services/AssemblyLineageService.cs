using System.Globalization;
using AssemblyManagerPlugin.Core;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace AssemblyManagerPlugin.Services;

/// <summary>
/// Creates and repairs the durable, assembly-scoped dependency graph that relates source
/// geometry to every generated occurrence. The document JSON is authoritative; object user
/// strings are a recovery index and are deliberately rewritten on every generated copy.
/// </summary>
public sealed class AssemblyLineageService
{
    public AssemblyLinkNodeRecord EnsureNode(
        AssemblyRecord assembly,
        Guid objectId,
        string role,
        Guid partId = default,
        Guid componentId = default,
        Guid sourceComponentInstanceId = default)
    {
        var graph = assembly.LinkGraph;
        var node = graph.Nodes.FirstOrDefault(candidate => candidate.ObjectId == objectId);
        if (node is null)
        {
            node = new AssemblyLinkNodeRecord
            {
                ObjectId = objectId,
                Role = role,
                PartId = partId,
                ComponentId = componentId,
                SourceComponentInstanceId = sourceComponentInstanceId
            };
            graph.Nodes.Add(node);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(node.Role))
                node.Role = role;
            node.Status = AssemblyLinkStatuses.Active;
            if (partId != Guid.Empty)
                node.PartId = partId;
            if (componentId != Guid.Empty)
                node.ComponentId = componentId;
            if (sourceComponentInstanceId != Guid.Empty)
                node.SourceComponentInstanceId = sourceComponentInstanceId;
            node.UpdatedAt = DateTimeOffset.UtcNow;
        }

        graph.UpdatedAt = DateTimeOffset.UtcNow;
        return node;
    }

    public RegisteredAssemblyLink RegisterDerived(
        RhinoDoc doc,
        AssemblyRecord assembly,
        Guid parentObjectId,
        string parentRole,
        Guid childObjectId,
        string childRole,
        Transform parentToChild,
        string recipe = AssemblyLinkRecipes.DirectCopy,
        Guid partId = default,
        Guid componentId = default,
        Guid sourceComponentInstanceId = default,
        Guid edgeId = default,
        IReadOnlyDictionary<string, string>? recipeMetadata = null)
    {
        if (parentObjectId == Guid.Empty)
            throw new ArgumentException("A linked parent object ID is required.", nameof(parentObjectId));
        if (childObjectId == Guid.Empty)
            throw new ArgumentException("A linked child object ID is required.", nameof(childObjectId));

        var graph = assembly.LinkGraph;
        var parent = EnsureNode(
            assembly,
            parentObjectId,
            parentRole,
            partId,
            componentId,
            sourceComponentInstanceId);
        var child = EnsureNode(
            assembly,
            childObjectId,
            childRole,
            partId,
            componentId,
            sourceComponentInstanceId);

        // The implemented graph is one-to-one at each derived stage. If a regenerated output
        // is intentionally rebound to a different representative parent, retire its stale
        // incoming edge rather than leaving two competing regeneration paths.
        var supersededEdgeIds = graph.Edges
            .Where(candidate =>
                candidate.ChildNodeId == child.Id &&
                candidate.ParentNodeId != parent.Id)
            .Select(candidate => candidate.Id)
            .ToHashSet();
        if (supersededEdgeIds.Count > 0)
        {
            graph.Edges.RemoveAll(candidate => supersededEdgeIds.Contains(candidate.Id));
            graph.Conflicts.RemoveAll(conflict => supersededEdgeIds.Contains(conflict.EdgeId));
        }

        var edge = graph.Edges.FirstOrDefault(candidate =>
            candidate.ParentNodeId == parent.Id && candidate.ChildNodeId == child.Id);
        if (edge is null)
        {
            edge = new AssemblyLinkEdgeRecord
            {
                Id = edgeId == Guid.Empty ? Guid.NewGuid() : edgeId,
                ParentNodeId = parent.Id,
                ChildNodeId = child.Id,
                ParentToChildTransform = TransformRecord.FromTransform(parentToChild),
                Recipe = recipe
            };
            graph.Edges.Add(edge);
        }
        else
        {
            edge.ParentToChildTransform = TransformRecord.FromTransform(parentToChild);
            edge.Recipe = recipe;
            edge.Status = AssemblyLinkStatuses.Active;
            edge.UpdatedAt = DateTimeOffset.UtcNow;
        }

        if (recipeMetadata is not null)
        {
            foreach (var (key, value) in recipeMetadata)
                edge.RecipeMetadata[key] = value;
        }

        var rebuiltAt = DateTimeOffset.UtcNow;
        child.Status = AssemblyLinkStatuses.Active;
        child.UpdatedAt = rebuiltAt;
        child.Metadata.Remove(AssemblyLinkMetadataKeys.Quarantined);
        edge.Status = AssemblyLinkStatuses.Active;
        edge.UpdatedAt = rebuiltAt;
        ResolveSuccessfulRebuildConflicts(assembly, child.Id, edge.Id, rebuiltAt);
        graph.UpdatedAt = rebuiltAt;
        ApplyObjectMetadata(doc, assembly, child, edge, parentObjectId);
        return new RegisteredAssemblyLink(parent, child, edge);
    }

    private static void ResolveSuccessfulRebuildConflicts(
        AssemblyRecord assembly,
        Guid nodeId,
        Guid edgeId,
        DateTimeOffset resolvedAt)
    {
        foreach (var conflict in assembly.LinkGraph.Conflicts.Where(conflict =>
                     string.Equals(conflict.Status, AssemblyLinkStatuses.Open, StringComparison.OrdinalIgnoreCase) &&
                     (conflict.NodeId == nodeId || conflict.EdgeId == edgeId) &&
                     (string.Equals(
                          conflict.ConflictType,
                          AssemblyLinkConflictTypes.MissingObject,
                          StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(
                          conflict.ConflictType,
                          AssemblyLinkConflictTypes.TransformUnresolved,
                          StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(
                          conflict.ConflictType,
                          AssemblyLinkConflictTypes.DerivedGeometryOutOfDate,
                          StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(
                           conflict.ConflictType,
                           AssemblyLinkConflictTypes.DerivedGeometryChanged,
                           StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(
                           conflict.ConflictType,
                           AssemblyLinkConflictTypes.OriginalAssemblyPromotionUnresolved,
                           StringComparison.OrdinalIgnoreCase))))
        {
            conflict.Status = AssemblyLinkStatuses.Resolved;
            conflict.ResolvedAt = resolvedAt;
        }
    }

    public SourceComponentInstanceRecord RegisterSourceComponentInstance(
        RhinoDoc doc,
        AssemblyRecord assembly,
        ComponentRecord component,
        int sourceGroupIndex,
        string sourceGroupName,
        IEnumerable<Guid> sourceObjectIds,
        string generatedGroupName)
    {
        var sourceIds = sourceObjectIds.Where(id => id != Guid.Empty).Distinct().ToList();
        var sourceGroupId = Guid.Empty;
        if (sourceGroupIndex >= 0 && sourceGroupIndex < doc.Groups.Count)
            sourceGroupId = doc.Groups.FindIndex(sourceGroupIndex)?.Id ?? Guid.Empty;

        var generatedGroupId = Guid.Empty;
        var generatedGroupIndex = doc.Groups.Find(generatedGroupName);
        if (generatedGroupIndex >= 0)
            generatedGroupId = doc.Groups.FindIndex(generatedGroupIndex)?.Id ?? Guid.Empty;

        var instance = new SourceComponentInstanceRecord
        {
            ComponentId = component.Id,
            SourceGroupId = sourceGroupId,
            SourceGroupIndex = sourceGroupIndex,
            SourceGroupName = sourceGroupName,
            GeneratedGroupId = generatedGroupId,
            GeneratedGroupName = generatedGroupName
        };

        foreach (var objectId in sourceIds)
        {
            var node = EnsureNode(assembly, objectId, AssemblyLinkRoles.Source, componentId: component.Id,
                sourceComponentInstanceId: instance.Id);
            instance.SourceNodeIds.Add(node.Id);
        }

        assembly.LinkGraph.SourceComponentInstances.Add(instance);
        assembly.LinkGraph.UpdatedAt = DateTimeOffset.UtcNow;
        return instance;
    }

    /// <summary>
    /// Removes generated objects from the authoritative graph without deleting their Rhino
    /// geometry. This is used when a workflow supersedes an older output but preserves the
    /// legacy object for backward-compatible command behavior.
    /// </summary>
    public void DetachDerivedObjects(RhinoDoc doc, AssemblyRecord assembly, IEnumerable<Guid> objectIds)
    {
        var ids = objectIds.Where(id => id != Guid.Empty).ToHashSet();
        if (ids.Count == 0)
            return;

        var nodes = assembly.LinkGraph.Nodes
            .Where(node => ids.Contains(node.ObjectId) &&
                           !string.Equals(node.Role, AssemblyLinkRoles.Source, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var nodeIds = nodes.Select(node => node.Id).ToHashSet();
        var edgeIds = assembly.LinkGraph.Edges
            .Where(edge => nodeIds.Contains(edge.ParentNodeId) || nodeIds.Contains(edge.ChildNodeId))
            .Select(edge => edge.Id)
            .ToHashSet();

        using var mutation = AssemblyLinkMutationGate.Enter();
        foreach (var node in nodes)
        {
            var rhinoObject = doc.Objects.FindId(node.ObjectId);
            if (rhinoObject is null)
                continue;

            var attributes = rhinoObject.Attributes.Duplicate();
            ClearLinkMetadata(attributes);
            doc.Objects.ModifyAttributes(node.ObjectId, attributes, true);
        }

        assembly.LinkGraph.Edges.RemoveAll(edge => edgeIds.Contains(edge.Id));
        assembly.LinkGraph.Nodes.RemoveAll(node => nodeIds.Contains(node.Id));
        assembly.LinkGraph.Conflicts.RemoveAll(conflict =>
            nodeIds.Contains(conflict.NodeId) || edgeIds.Contains(conflict.EdgeId));
        assembly.LinkGraph.UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void ApplyObjectMetadata(
        RhinoDoc doc,
        AssemblyRecord assembly,
        AssemblyLinkNodeRecord node,
        AssemblyLinkEdgeRecord edge,
        Guid parentObjectId)
    {
        var rhinoObject = doc.Objects.FindId(node.ObjectId);
        if (rhinoObject is null)
            return;

        var attributes = rhinoObject.Attributes.Duplicate();
        AttachObjectMetadata(attributes, assembly, node, edge, parentObjectId);
        doc.Objects.ModifyAttributes(node.ObjectId, attributes, true);
    }

    public static void AttachObjectMetadata(
        ObjectAttributes attributes,
        AssemblyRecord assembly,
        AssemblyLinkNodeRecord node,
        AssemblyLinkEdgeRecord edge,
        Guid parentObjectId)
    {
        ClearLinkMetadata(attributes);
        attributes.SetUserString(AssemblyManagerConstants.AssemblyIdUserString, assembly.Id.ToString("D"));
        attributes.SetUserString(
            AssemblyManagerConstants.LinkSchemaVersionUserString,
            AssemblyStore.CurrentSchemaVersion.ToString(CultureInfo.InvariantCulture));
        attributes.SetUserString(AssemblyManagerConstants.LinkNodeIdUserString, node.Id.ToString("D"));
        attributes.SetUserString(AssemblyManagerConstants.LinkEdgeIdUserString, edge.Id.ToString("D"));
        attributes.SetUserString(AssemblyManagerConstants.LinkRoleUserString, node.Role);
        if (node.SourceComponentInstanceId != Guid.Empty)
        {
            attributes.SetUserString(
                AssemblyManagerConstants.SourceComponentInstanceIdUserString,
                node.SourceComponentInstanceId.ToString("D"));
        }

        // Preserve the v1 recovery keys while making their IDs agree with the v2 graph.
        attributes.SetUserString(AssemblyManagerConstants.SourceObjectUserString, parentObjectId.ToString("D"));
        attributes.SetUserString(AssemblyManagerConstants.ReferenceIdUserString, edge.Id.ToString("D"));
        attributes.SetUserString(
            AssemblyManagerConstants.ReferenceTransformUserString,
            SerializeTransform(edge.ParentToChildTransform));
    }

    public static void ClearLinkMetadata(ObjectAttributes attributes)
    {
        attributes.DeleteUserString(AssemblyManagerConstants.AssemblyIdUserString);
        attributes.DeleteUserString(AssemblyManagerConstants.LinkSchemaVersionUserString);
        attributes.DeleteUserString(AssemblyManagerConstants.LinkNodeIdUserString);
        attributes.DeleteUserString(AssemblyManagerConstants.LinkEdgeIdUserString);
        attributes.DeleteUserString(AssemblyManagerConstants.LinkRoleUserString);
        attributes.DeleteUserString(AssemblyManagerConstants.SourceComponentInstanceIdUserString);
        attributes.DeleteUserString(AssemblyManagerConstants.SourceObjectUserString);
        attributes.DeleteUserString(AssemblyManagerConstants.ReferenceIdUserString);
        attributes.DeleteUserString(AssemblyManagerConstants.ReferenceTransformUserString);
    }

    private static string SerializeTransform(TransformRecord transform)
    {
        return string.Join(",", (transform.Values ?? Array.Empty<double>()).Select(value =>
            value.ToString("R", CultureInfo.InvariantCulture)));
    }
}

public readonly record struct RegisteredAssemblyLink(
    AssemblyLinkNodeRecord Parent,
    AssemblyLinkNodeRecord Child,
    AssemblyLinkEdgeRecord Edge);
