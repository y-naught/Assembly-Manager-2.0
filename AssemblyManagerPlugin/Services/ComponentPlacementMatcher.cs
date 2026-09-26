using Rhino.DocObjects;
using Rhino.Geometry;

namespace AssemblyManagerPlugin.Services;

/// <summary>
/// Validates stored placements against retained source/original geometry and copied layouts.
/// Component updates use the selected occurrence's recorded transform; they do not infer a
/// relative placement between otherwise equivalent component occurrences.
/// </summary>
internal static class ComponentPlacementMatcher
{
    public static bool MatchesGeometry(RhinoObject first, RhinoObject second, Transform transform, double tolerance)
    {
        if (first is InstanceObject firstInstance && second is InstanceObject secondInstance)
            return firstInstance.InstanceDefinition?.Id == secondInstance.InstanceDefinition?.Id &&
                   SameTransform(transform * firstInstance.InstanceXform, secondInstance.InstanceXform, tolerance);
        using var firstBrep = DuplicateBrep(first);
        using var secondBrep = DuplicateBrep(second);
        if (firstBrep is null || secondBrep is null ||
            firstBrep.Vertices.Count != secondBrep.Vertices.Count ||
            firstBrep.Edges.Count != secondBrep.Edges.Count ||
            firstBrep.Faces.Count != secondBrep.Faces.Count ||
            !firstBrep.Transform(transform))
            return false;

        var a = EvidencePoints(firstBrep);
        var b = EvidencePoints(secondBrep);
        if (a.Count != b.Count || a.Count == 0)
            return false;
        // A one-to-one point comparison prevents coincident/repeated samples from disguising
        // missing topology. Samples include all vertices, edge interiors, and face interiors.
        var used = new bool[b.Count];
        foreach (var point in a)
        {
            var index = -1;
            var distance = double.MaxValue;
            for (var i = 0; i < b.Count; i++)
            {
                var candidateDistance = point.DistanceTo(b[i]);
                if (!used[i] && candidateDistance <= tolerance && candidateDistance < distance)
                {
                    distance = candidateDistance;
                    index = i;
                }
            }
            if (index < 0)
                return false;
            used[index] = true;
        }
        // The recorded placement must also match Rhino's full control geometry, not just
        // sampled points, before it can be reused for additions.
        return firstBrep.IsDuplicate(secondBrep, tolerance);
    }

    public static bool SameTransform(Transform first, Transform second, double tolerance)
    {
        // Compare translation in document units and the linear basis independently. Comparing
        // only matrix entries with model tolerance accepts incorrect rotations in inch models.
        if (new Point3d(first.M03, first.M13, first.M23).DistanceTo(new Point3d(second.M03, second.M13, second.M23)) > tolerance)
            return false;
        for (var row = 0; row < 3; row++)
        for (var column = 0; column < 3; column++)
            if (Math.Abs(first[row, column] - second[row, column]) > 1e-8)
                return false;
        return true;
    }

    private static Brep? DuplicateBrep(RhinoObject value) => value.Geometry switch
    {
        Brep brep => brep.DuplicateBrep(),
        Extrusion extrusion => extrusion.ToBrep(),
        _ => null
    };

    private static List<Point3d> EvidencePoints(Brep brep)
    {
        var points = brep.Vertices.Select(vertex => vertex.Location).ToList();
        foreach (var edge in brep.Edges)
            for (var index = 0; index <= 8; index++)
                points.Add(edge.PointAt(edge.Domain.ParameterAt(index / 8.0)));
        foreach (var face in brep.Faces)
            foreach (var u in new[] { 0.25, 0.5, 0.75 })
            foreach (var v in new[] { 0.25, 0.5, 0.75 })
                points.Add(face.PointAt(face.Domain(0).ParameterAt(u), face.Domain(1).ParameterAt(v)));
        return points;
    }
}
