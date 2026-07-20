using Rhino.Geometry;

namespace AssemblyManagerPlugin.Geometry;

public static class TransformUtilities
{
    public static Transform GetTranslationToRow(BoundingBox bbox, double xMultiplier, double yMultiplier)
    {
        if (!bbox.IsValid)
            return Transform.Identity;

        var dims = new[]
        {
            Math.Abs(bbox.Max.X - bbox.Min.X),
            Math.Abs(bbox.Max.Y - bbox.Min.Y),
            Math.Abs(bbox.Max.Z - bbox.Min.Z)
        };
        var maxDim = dims.Max();
        return Transform.Translation(maxDim * xMultiplier, maxDim * yMultiplier, 0.0);
    }

    public static Transform OrientLargestFaceToWorldXY(Brep brep, GeometryFingerprintService fingerprints, Point3d targetOrigin)
    {
        if (!fingerprints.TryGetLargestFacePlane(brep, out var sourcePlane))
            return Transform.Translation(targetOrigin - brep.GetBoundingBox(true).Center);

        var targetPlane = new Plane(targetOrigin, Vector3d.XAxis, Vector3d.YAxis);
        return Transform.PlaneToPlane(sourcePlane, targetPlane);
    }

    public static BoundingBox GetBoundingBox(IEnumerable<Guid> objectIds, Rhino.RhinoDoc doc)
    {
        var bbox = BoundingBox.Empty;
        foreach (var objectId in objectIds)
        {
            var rhinoObject = doc.Objects.FindId(objectId);
            if (rhinoObject is null)
                continue;

            bbox.Union(rhinoObject.Geometry.GetBoundingBox(true));
        }

        return bbox;
    }

    public static Point3d Average(IEnumerable<Point3d> points)
    {
        var list = points.ToList();
        if (list.Count == 0)
            return Point3d.Origin;

        var x = list.Sum(p => p.X) / list.Count;
        var y = list.Sum(p => p.Y) / list.Count;
        var z = list.Sum(p => p.Z) / list.Count;
        return new Point3d(x, y, z);
    }
}
