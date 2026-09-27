using AssemblyManagerPlugin.Geometry;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace AssemblyManagerPlugin.Services;

/// <summary>
/// Finds a proven placement for an existing flat representative when its old component
/// member is removed. No Rhino geometry is changed: the returned transform must reproduce
/// the complete existing output, including its operator-adjusted position and rotation.
/// </summary>
internal static class ComponentFlatReparentMatcher
{
    public static bool TryGetPlacement(RhinoObject newParent, RhinoObject existingFlat,
        double tolerance, GeometryFingerprintService fingerprints, out Transform placement)
    {
        placement = Transform.Unset;
        using var parent = DuplicateBrep(newParent);
        using var flat = DuplicateBrep(existingFlat);
        if (parent is null || flat is null || parent.Vertices.Count != flat.Vertices.Count ||
            parent.Edges.Count != flat.Edges.Count || parent.Faces.Count != flat.Faces.Count)
            return false;

        bool Accept(Transform candidate)
        {
            return candidate.IsValid && candidate.RigidType == TransformRigidType.Rigid &&
                   ComponentPlacementMatcher.MatchesGeometry(newParent, existingFlat, candidate, tolerance);
        }

        var translation = Transform.Translation(flat.GetBoundingBox(true).Center - parent.GetBoundingBox(true).Center);
        if (Accept(translation))
        {
            placement = translation;
            return true;
        }

        // Duplicated parts normally retain their vertex ordering. One well-conditioned
        // frame handles arbitrary drawing rotations without any combinatorial point search.
        // Frame correspondence is only a proposal: full control geometry must still match.
        if (TryCorrespondingFrames(parent, flat, tolerance, out var indexed) && Accept(indexed))
        {
            placement = indexed;
            return true;
        }

        // Independently generated equivalent parts may reorder their topology. Compare
        // bounded canonical-frame candidates instead of assuming matching vertex indices.
        // The inverse flat normalization restores its exact existing world placement.
        var parentToCanonical = TransformUtilities.OrientLargestFaceToWorldXY(parent, fingerprints, Point3d.Origin);
        var flatToCanonical = TransformUtilities.OrientLargestFaceToWorldXY(flat, fingerprints, Point3d.Origin);
        if (!parent.Transform(parentToCanonical) || !flat.Transform(flatToCanonical))
            return false;
        parentToCanonical = TransformUtilities.RotateLongDimensionToY(parent) * parentToCanonical;
        flatToCanonical = TransformUtilities.RotateLongDimensionToY(flat) * flatToCanonical;
        if (!flatToCanonical.TryGetInverse(out var canonicalToFlat))
            return false;

        var flatCenter = flat.GetBoundingBox(true).Center;
        for (var flip = 0; flip < 2; flip++)
        for (var quarterTurn = 0; quarterTurn < 4; quarterTurn++)
        {
            var rotation = Transform.Rotation(quarterTurn * Math.PI / 2.0, Vector3d.ZAxis, Point3d.Origin) *
                           Transform.Rotation(flip * Math.PI, Vector3d.XAxis, Point3d.Origin);
            using var candidateGeometry = parent.DuplicateBrep();
            if (!candidateGeometry.Transform(rotation))
                continue;
            var align = Transform.Translation(flatCenter - candidateGeometry.GetBoundingBox(true).Center);
            var candidate = canonicalToFlat * align * rotation * parentToCanonical;
            if (!Accept(candidate))
                continue;
            placement = candidate;
            return true;
        }
        return false;
    }

    private static bool TryCorrespondingFrames(Brep parent, Brep flat, double tolerance, out Transform placement)
    {
        placement = Transform.Unset;
        var count = Math.Min(parent.Vertices.Count, 128);
        if (count < 3)
            return false;
        var origin = parent.Vertices[0].Location;
        var axisIndex = -1;
        var longestSquared = tolerance * tolerance;
        for (var index = 1; index < count; index++)
        {
            var squared = (parent.Vertices[index].Location - origin).SquareLength;
            if (squared <= longestSquared)
                continue;
            longestSquared = squared;
            axisIndex = index;
        }
        if (axisIndex < 0)
            return false;
        var axis = parent.Vertices[axisIndex].Location - origin;
        var planeIndex = -1;
        var largestAreaSquared = longestSquared * tolerance * tolerance;
        for (var index = 1; index < count; index++)
        {
            var areaSquared = Vector3d.CrossProduct(axis, parent.Vertices[index].Location - origin).SquareLength;
            if (areaSquared <= largestAreaSquared)
                continue;
            largestAreaSquared = areaSquared;
            planeIndex = index;
        }
        if (planeIndex < 0)
            return false;
        var parentPlane = new Plane(origin, parent.Vertices[axisIndex].Location, parent.Vertices[planeIndex].Location);
        var flatPlane = new Plane(flat.Vertices[0].Location, flat.Vertices[axisIndex].Location, flat.Vertices[planeIndex].Location);
        if (!parentPlane.IsValid || !flatPlane.IsValid)
            return false;
        placement = Transform.PlaneToPlane(parentPlane, flatPlane);
        return true;
    }

    private static Brep? DuplicateBrep(RhinoObject value) => value.Geometry switch
    {
        Brep brep => brep.DuplicateBrep(),
        Extrusion extrusion => extrusion.ToBrep(),
        _ => null
    };
}
