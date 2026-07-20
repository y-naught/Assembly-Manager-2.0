using AssemblyManagerPlugin.Core;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace AssemblyManagerPlugin.Services;

public sealed class UtilityGeometryService
{
    private readonly LayerService _layers;

    public UtilityGeometryService(LayerService layers)
    {
        _layers = layers;
    }

    public string Regroup(RhinoDoc doc, IEnumerable<Guid> objectIds)
    {
        var ids = objectIds.ToList();
        if (ids.Count == 0)
            throw new InvalidOperationException("No objects were selected.");

        foreach (var id in ids)
        {
            var obj = doc.Objects.FindId(id);
            if (obj is null)
                continue;

            var attrs = obj.Attributes.Duplicate();
            attrs.RemoveFromAllGroups();
            doc.Objects.ModifyAttributes(obj, attrs, true);
        }

        var groupName = $"AM_Regroup_{Guid.NewGuid():N}"[..30];
        var groupIndex = doc.Groups.Add(groupName);
        foreach (var id in ids)
            doc.Groups.AddToGroup(groupIndex, id);

        doc.Views.Redraw();
        return groupName;
    }

    public void MoveOrtho(RhinoDoc doc, IEnumerable<Guid> objectIds, Point3d start, Point3d end, Vector3d axis)
    {
        var delta = end - start;
        axis.Unitize();
        var constrained = axis * (delta * axis);
        var transform = Transform.Translation(constrained);
        foreach (var id in objectIds)
            doc.Objects.Transform(id, transform, true);

        doc.Views.Redraw();
    }

    public int MotionTrace(RhinoDoc doc, IEnumerable<Guid> objectIds, Point3d start, Point3d end)
    {
        _layers.EnsureLayer(doc, AssemblyManagerConstants.AnnotationRootLayer);
        var traceLayer = $"{AssemblyManagerConstants.AnnotationRootLayer}::MOVE_TRACE_{DateTime.Now:yyyyMMdd_HHmmss}";
        var startLayerIndex = _layers.EnsureLayerIndex(doc, $"{traceLayer}::START_EDGES_HIDDEN", System.Drawing.Color.FromArgb(130, 130, 130));
        var finalLayerIndex = _layers.EnsureLayerIndex(doc, $"{traceLayer}::FINAL_EDGES_HIDDEN", System.Drawing.Color.FromArgb(90, 90, 90));
        var connectorLayerIndex = _layers.EnsureLayerIndex(doc, $"{traceLayer}::MOTION_CONNECTORS_HIDDEN", System.Drawing.Color.FromArgb(70, 130, 190));
        var hiddenLinetypeIndex = GetOrCreateHiddenLinetypeIndex(doc);
        var moved = 0;
        var translation = Transform.Translation(end - start);
        var addedTraceObjectIds = new List<Guid>();
        var usedConnectorKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var id in objectIds)
        {
            var obj = doc.Objects.FindId(id);
            if (obj is null)
                continue;

            var startGeometry = DuplicateGeometry(obj);
            if (startGeometry is null)
                continue;

            var finalGeometry = startGeometry.Duplicate();
            finalGeometry.Transform(translation);
            var objectColor = GetObjectDisplayColor(doc, obj);
            var startCurves = EdgeCurvesFromGeometry(startGeometry, doc.ModelAbsoluteTolerance);
            var finalCurves = EdgeCurvesFromGeometry(finalGeometry, doc.ModelAbsoluteTolerance);
            if (doc.Objects.Transform(id, translation, true) == Guid.Empty)
                continue;

            var perObjectTraceIds = new List<Guid>();
            perObjectTraceIds.AddRange(AddCurvesToDoc(doc, startCurves, startLayerIndex, hiddenLinetypeIndex, objectColor));
            perObjectTraceIds.AddRange(AddCurvesToDoc(doc, finalCurves, finalLayerIndex, hiddenLinetypeIndex, objectColor));
            var connectors = AddEdgeEndpointConnectors(
                doc,
                startCurves,
                finalCurves,
                connectorLayerIndex,
                hiddenLinetypeIndex,
                usedConnectorKeys,
                objectColor);
            if (connectors.Count < 2)
            {
                connectors.AddRange(AddBoundingBoxConnectors(
                    doc,
                    startGeometry,
                    finalGeometry,
                    connectorLayerIndex,
                    hiddenLinetypeIndex,
                    usedConnectorKeys,
                    objectColor));
            }

            perObjectTraceIds.AddRange(connectors);
            addedTraceObjectIds.AddRange(perObjectTraceIds);
            moved++;
        }

        CreateGroup(doc, $"ANNO_MOVE_TRACE_{DateTime.Now:yyyyMMdd_HHmmss}", addedTraceObjectIds);
        doc.Views.Redraw();
        return moved;
    }

    public int OrientToWorld(RhinoDoc doc, IEnumerable<Guid> objectIds)
    {
        var ids = objectIds.ToList();
        if (ids.Count == 0)
            return 0;

        var bbox = GetBoundingBox(doc, ids);
        if (!bbox.IsValid)
            return 0;

        var rotationPoint = GetBottomCenter(bbox);
        var currentMeasure = BoundingBoxMeasure(bbox);
        var bestAngle = FindBestWorldZRotation(doc, ids, rotationPoint, currentMeasure);

        if (Math.Abs(bestAngle) < RhinoMath.ToRadians(0.01))
            return 0;

        var bestRotation = Transform.Rotation(bestAngle, Vector3d.ZAxis, rotationPoint);
        foreach (var id in ids)
            doc.Objects.Transform(id, bestRotation, true);

        doc.Views.Redraw();
        return ids.Count;
    }

    public int SplitByThreePointPlane(RhinoDoc doc, IEnumerable<Guid> objectIds, Point3d a, Point3d b, Point3d c, bool capSplitFaces = true)
    {
        if (!Plane.FitPlaneToPoints(new[] { a, b, c }, out var plane).Equals(PlaneFitResult.Success))
            throw new InvalidOperationException("The selected points do not define a valid plane.");

        var addedPieces = 0;
        var tolerance = doc.ModelAbsoluteTolerance;
        foreach (var id in objectIds)
        {
            var obj = doc.Objects.FindId(id);
            if (obj is null || !TryGetBrep(obj.Geometry, out var brep))
                continue;

            var bbox = brep.GetBoundingBox(true);
            if (!bbox.IsValid)
                continue;

            var cutter = CreatePlaneCutter(plane, bbox, tolerance);
            var pieces = brep.Split(cutter, tolerance);
            if (pieces is null || pieces.Length <= 1)
                continue;

            var attrs = obj.Attributes.Duplicate();
            foreach (var piece in pieces)
            {
                var outputPiece = capSplitFaces
                    ? piece.CapPlanarHoles(tolerance) ?? piece
                    : piece;
                doc.Objects.AddBrep(outputPiece, attrs);
                addedPieces++;
            }

            doc.Objects.Delete(id, true);
        }

        doc.Views.Redraw();
        return addedPieces;
    }

    private static bool TryGetBrep(GeometryBase geometry, out Brep brep)
    {
        switch (geometry)
        {
            case Brep sourceBrep:
                brep = sourceBrep.DuplicateBrep();
                return true;
            case Extrusion extrusion:
                brep = extrusion.ToBrep();
                return brep is not null;
            default:
                brep = default!;
                return false;
        }
    }

    private static Brep CreatePlaneCutter(Plane plane, BoundingBox bbox, double tolerance)
    {
        var minU = double.MaxValue;
        var maxU = double.MinValue;
        var minV = double.MaxValue;
        var maxV = double.MinValue;

        foreach (var corner in bbox.GetCorners())
        {
            if (!plane.ClosestParameter(corner, out var u, out var v))
                continue;

            minU = Math.Min(minU, u);
            maxU = Math.Max(maxU, u);
            minV = Math.Min(minV, v);
            maxV = Math.Max(maxV, v);
        }

        var margin = Math.Max(bbox.Diagonal.Length, tolerance * 100.0);
        if (minU == double.MaxValue || minV == double.MaxValue)
        {
            minU = minV = -margin;
            maxU = maxV = margin;
        }
        else
        {
            minU -= margin;
            maxU += margin;
            minV -= margin;
            maxV += margin;
        }

        return new PlaneSurface(plane, new Interval(minU, maxU), new Interval(minV, maxV)).ToBrep();
    }

    public Guid AddLayerTextDot(RhinoDoc doc, Guid objectId, Point3d point)
    {
        var obj = doc.Objects.FindId(objectId);
        if (obj is null)
            return Guid.Empty;

        var layerName = doc.Layers[obj.Attributes.LayerIndex].Name;
        var id = doc.Objects.AddTextDot(new TextDot(layerName, point));
        doc.Views.Redraw();
        return id;
    }

    public LabelPartLeaderResult AddLayerLeaderFromLayoutPoint(RhinoDoc doc, IReadOnlyList<Point3d> leaderPoints)
    {
        if (doc.ActiveSpace != ActiveSpace.PageSpace)
            throw new InvalidOperationException("LabelPart must be run from layout/page space.");
        if (leaderPoints.Count < 2)
            throw new InvalidOperationException("At least two leader points are required.");

        var tipPoint = leaderPoints[0];
        var match = FindObjectCandidatesAtLayoutPoint(doc, tipPoint).FirstOrDefault()
            ?? throw new InvalidOperationException("No visible model object was found under the leader tip point.");

        return AddLayerLeaderForObject(doc, leaderPoints, match.Object.Id, match.Distance);
    }

    public IReadOnlyList<LabelPartCandidate> GetLabelPartCandidates(RhinoDoc doc, Point3d tipPoint)
    {
        return FindObjectCandidatesAtLayoutPoint(doc, tipPoint)
            .Select(match => new LabelPartCandidate(
                match.Object.Id,
                GetLeafLayerName(doc, match.Object),
                GetLayerPath(doc, match.Object),
                match.Distance,
                match.ViewDepth))
            .ToList();
    }

    public LabelPartLeaderResult AddLayerLeaderForObject(
        RhinoDoc doc,
        IReadOnlyList<Point3d> leaderPoints,
        Guid sourceObjectId,
        double pickDistance = 0.0)
    {
        if (doc.ActiveSpace != ActiveSpace.PageSpace)
            throw new InvalidOperationException("LabelPart must be run from layout/page space.");
        if (leaderPoints.Count < 2)
            throw new InvalidOperationException("At least two leader points are required.");

        var pageView = doc.Views.ActiveView as RhinoPageView
            ?? throw new InvalidOperationException("The active view is not a layout page.");
        var sourceObject = doc.Objects.FindId(sourceObjectId)
            ?? throw new InvalidOperationException("The selected label source object was not found.");

        var label = GetLeafLayerName(doc, sourceObject);
        var dimStyle = doc.DimStyles.Current
            ?? throw new InvalidOperationException("The document does not have a current annotation style.");
        var leader = Leader.Create(label, Plane.WorldXY, dimStyle, leaderPoints.ToArray())
            ?? throw new InvalidOperationException("Rhino could not create the leader annotation.");
        leader.DimensionStyleId = dimStyle.Id;
        leader.DimensionScale = 1.0;

        var layerIndex = _layers.EnsureLayerIndex(doc, "LEADERS", System.Drawing.Color.Black);
        var attributes = new ObjectAttributes
        {
            Space = ActiveSpace.PageSpace,
            ViewportId = pageView.MainViewport.Id,
            LayerIndex = layerIndex,
            ColorSource = ObjectColorSource.ColorFromLayer,
            Name = $"Layer Leader - {label}"
        };
        var leaderId = doc.Objects.AddLeader(leader, attributes);
        if (leaderId == Guid.Empty)
            throw new InvalidOperationException("Rhino could not add the leader annotation to the document.");

        doc.Views.Redraw();
        return new LabelPartLeaderResult(leaderId, label, sourceObject.Id, pickDistance);
    }

    private List<ObjectPageMatch> FindObjectCandidatesAtLayoutPoint(RhinoDoc doc, Point3d tipPoint)
    {
        if (doc.ActiveSpace != ActiveSpace.PageSpace)
            throw new InvalidOperationException("LabelPart must be run from layout/page space.");

        var pageView = doc.Views.ActiveView as RhinoPageView
            ?? throw new InvalidOperationException("The active view is not a layout page.");
        var detail = FindDetailAtPagePoint(pageView, tipPoint)
            ?? throw new InvalidOperationException("No detail view was found under the leader tip point.");

        return FindObjectsUnderDetailPoint(doc, detail, tipPoint);
    }

    private static DetailViewObject? FindDetailAtPagePoint(RhinoPageView pageView, Point3d pagePoint)
    {
        return pageView.GetDetailViews()
            .Where(detail =>
            {
                var bbox = detail.DetailGeometry.GetBoundingBox(true);
                return bbox.IsValid
                    && pagePoint.X >= bbox.Min.X
                    && pagePoint.X <= bbox.Max.X
                    && pagePoint.Y >= bbox.Min.Y
                    && pagePoint.Y <= bbox.Max.Y;
            })
            .OrderBy(detail =>
            {
                var bbox = detail.DetailGeometry.GetBoundingBox(true);
                return Math.Abs((bbox.Max.X - bbox.Min.X) * (bbox.Max.Y - bbox.Min.Y));
            })
            .FirstOrDefault();
    }

    private List<ObjectPageMatch> FindObjectsUnderDetailPoint(RhinoDoc doc, DetailViewObject detail, Point3d pagePoint)
    {
        var tolerance = GetPagePickTolerance(detail);
        var matches = new List<ObjectPageMatch>();

        var settings = new ObjectEnumeratorSettings
        {
            ActiveObjects = true,
            NormalObjects = true,
            LockedObjects = true,
            HiddenObjects = false,
            VisibleFilter = true,
            IncludeLights = false,
            IncludeGrips = false
        };

        foreach (var obj in doc.Objects.GetObjectList(settings))
        {
            if (obj.Attributes.Space != ActiveSpace.ModelSpace)
                continue;
            if (obj.ObjectType == ObjectType.Detail || obj.ObjectType == ObjectType.Annotation)
                continue;
            if (!IsLayerVisibleInDetail(doc, detail, obj))
                continue;

            var bbox = obj.Geometry.GetBoundingBox(true);
            if (!bbox.IsValid || !detail.Viewport.IsVisible(bbox))
                continue;

            var hit = ProjectedHitTestObject(detail, obj, pagePoint, tolerance);
            if (hit.Distance > tolerance)
                continue;

            matches.Add(new ObjectPageMatch(obj, hit.Distance, hit.ViewDepth, hit.FaceHit));
        }

        return matches
            .OrderBy(match => match.FaceHit ? 0 : 1)
            .ThenBy(match => match.FaceHit ? match.ViewDepth : 0.0)
            .ThenBy(match => match.Distance)
            .ThenBy(match => match.ViewDepth)
            .ThenBy(match => match.Object.Id)
            .ToList();
    }

    private static ObjectPageHit ProjectedHitTestObject(DetailViewObject detail, RhinoObject obj, Point3d pagePoint, double tolerance)
    {
        var geometry = obj.Geometry;
        var bbox = geometry.GetBoundingBox(true);
        var bestDistance = double.MaxValue;
        var bestDepth = bbox.IsValid ? ViewDepth(detail, bbox.Center) : double.MaxValue;

        if (TryProjectedMeshHit(detail, geometry, pagePoint, tolerance, out var faceDepth))
            return new ObjectPageHit(0.0, faceDepth, true);

        foreach (var curve in EdgeCurvesFromGeometry(geometry, RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? RhinoMath.ZeroTolerance))
        {
            var points = SampleCurveToPagePoints(curve, detail.WorldToPageTransform);
            var distance = PolylineDistance2d(pagePoint, points);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestDepth = ViewDepth(detail, curve.PointAtNormalizedLength(0.5));
            }
        }

        if (bbox.IsValid)
        {
            if (ProjectedBoundingBoxContains(pagePoint, bbox, detail.WorldToPageTransform, tolerance))
                bestDistance = Math.Min(bestDistance, tolerance * 0.5);
            else
                bestDistance = Math.Min(bestDistance, BoundingBoxPageDistance(pagePoint, bbox, detail.WorldToPageTransform));
        }

        return new ObjectPageHit(bestDistance, bestDepth, false);
    }

    private static bool TryProjectedMeshHit(
        DetailViewObject detail,
        GeometryBase geometry,
        Point3d pagePoint,
        double tolerance,
        out double viewDepth)
    {
        viewDepth = double.MaxValue;
        var hit = false;

        foreach (var mesh in MeshesFromGeometry(geometry))
        {
            for (var i = 0; i < mesh.Faces.Count; i++)
            {
                var face = mesh.Faces[i];
                var a = mesh.Vertices[face.A];
                var b = mesh.Vertices[face.B];
                var c = mesh.Vertices[face.C];
                if (ProjectedTriangleContains(detail, pagePoint, a, b, c, tolerance))
                {
                    viewDepth = Math.Min(viewDepth, ViewDepth(detail, AveragePoint(a, b, c)));
                    hit = true;
                }

                if (face.IsQuad)
                {
                    var d = mesh.Vertices[face.D];
                    if (ProjectedTriangleContains(detail, pagePoint, a, c, d, tolerance))
                    {
                        viewDepth = Math.Min(viewDepth, ViewDepth(detail, AveragePoint(a, c, d)));
                        hit = true;
                    }
                }
            }
        }

        return hit;
    }

    private static IEnumerable<Mesh> MeshesFromGeometry(GeometryBase geometry)
    {
        switch (geometry)
        {
            case Mesh mesh:
                yield return mesh;
                break;
            case Brep brep:
                foreach (var mesh in Mesh.CreateFromBrep(brep, MeshingParameters.Default) ?? Array.Empty<Mesh>())
                    yield return mesh;
                break;
            case Extrusion extrusion:
                var extrusionBrep = extrusion.ToBrep();
                if (extrusionBrep is not null)
                {
                    foreach (var mesh in Mesh.CreateFromBrep(extrusionBrep, MeshingParameters.Default) ?? Array.Empty<Mesh>())
                        yield return mesh;
                }
                break;
            case Surface surface:
                var surfaceBrep = surface.ToBrep();
                if (surfaceBrep is not null)
                {
                    foreach (var mesh in Mesh.CreateFromBrep(surfaceBrep, MeshingParameters.Default) ?? Array.Empty<Mesh>())
                        yield return mesh;
                }
                break;
        }
    }

    private static bool ProjectedTriangleContains(
        DetailViewObject detail,
        Point3d pagePoint,
        Point3d a,
        Point3d b,
        Point3d c,
        double tolerance)
    {
        var pa = TransformPoint(detail.WorldToPageTransform, a);
        var pb = TransformPoint(detail.WorldToPageTransform, b);
        var pc = TransformPoint(detail.WorldToPageTransform, c);
        if (PointInTriangle2d(pagePoint, pa, pb, pc))
            return true;

        return SegmentDistance2d(pagePoint, pa, pb) <= tolerance
            || SegmentDistance2d(pagePoint, pb, pc) <= tolerance
            || SegmentDistance2d(pagePoint, pc, pa) <= tolerance;
    }

    private static bool PointInTriangle2d(Point3d point, Point3d a, Point3d b, Point3d c)
    {
        var d1 = TriangleSign(point, a, b);
        var d2 = TriangleSign(point, b, c);
        var d3 = TriangleSign(point, c, a);
        var hasNegative = d1 < 0 || d2 < 0 || d3 < 0;
        var hasPositive = d1 > 0 || d2 > 0 || d3 > 0;
        return !(hasNegative && hasPositive);
    }

    private static double TriangleSign(Point3d p1, Point3d p2, Point3d p3)
    {
        return ((p1.X - p3.X) * (p2.Y - p3.Y)) - ((p2.X - p3.X) * (p1.Y - p3.Y));
    }

    private static Point3d TransformPoint(Transform transform, Point3d point)
    {
        point.Transform(transform);
        return point;
    }

    private static Point3d AveragePoint(Point3d a, Point3d b, Point3d c)
    {
        return new Point3d((a.X + b.X + c.X) / 3.0, (a.Y + b.Y + c.Y) / 3.0, (a.Z + b.Z + c.Z) / 3.0);
    }

    private static double ViewDepth(DetailViewObject detail, Point3d point)
    {
        var direction = detail.Viewport.CameraDirection;
        if (!direction.Unitize())
            return point.DistanceTo(detail.Viewport.CameraLocation);

        return (point - detail.Viewport.CameraLocation) * direction;
    }

    private static bool ProjectedBoundingBoxContains(Point3d pagePoint, BoundingBox modelBox, Transform worldToPage, double tolerance)
    {
        var pageBox = BoundingBox.Empty;
        foreach (var corner in modelBox.GetCorners())
        {
            var pageCorner = corner;
            pageCorner.Transform(worldToPage);
            pageBox.Union(pageCorner);
        }

        return pageBox.IsValid
            && pagePoint.X >= pageBox.Min.X - tolerance
            && pagePoint.X <= pageBox.Max.X + tolerance
            && pagePoint.Y >= pageBox.Min.Y - tolerance
            && pagePoint.Y <= pageBox.Max.Y + tolerance;
    }

    private static IReadOnlyList<Point3d> SampleCurveToPagePoints(Curve curve, Transform worldToPage)
    {
        const int sampleCount = 32;
        var points = new List<Point3d>();
        var domain = curve.Domain;
        for (var i = 0; i <= sampleCount; i++)
        {
            var t = domain.T0 + ((domain.T1 - domain.T0) * i / sampleCount);
            var point = curve.PointAt(t);
            point.Transform(worldToPage);
            points.Add(point);
        }

        return points;
    }

    private static double BoundingBoxPageDistance(Point3d pagePoint, BoundingBox modelBox, Transform worldToPage)
    {
        var corners = modelBox.GetCorners()
            .Select(point =>
            {
                point.Transform(worldToPage);
                return point;
            })
            .ToArray();
        var pairs = new[]
        {
            (0, 1), (1, 2), (2, 3), (3, 0),
            (4, 5), (5, 6), (6, 7), (7, 4),
            (0, 4), (1, 5), (2, 6), (3, 7)
        };

        var best = double.MaxValue;
        foreach (var (a, b) in pairs)
            best = Math.Min(best, SegmentDistance2d(pagePoint, corners[a], corners[b]));

        return best;
    }

    private static double PolylineDistance2d(Point3d pagePoint, IReadOnlyList<Point3d> points)
    {
        if (points.Count == 0)
            return double.MaxValue;
        if (points.Count == 1)
            return Distance2d(pagePoint, points[0]);

        var best = double.MaxValue;
        for (var i = 0; i < points.Count - 1; i++)
            best = Math.Min(best, SegmentDistance2d(pagePoint, points[i], points[i + 1]));

        return best;
    }

    private static double SegmentDistance2d(Point3d point, Point3d start, Point3d end)
    {
        var vx = end.X - start.X;
        var vy = end.Y - start.Y;
        var wx = point.X - start.X;
        var wy = point.Y - start.Y;
        var lengthSquared = (vx * vx) + (vy * vy);
        if (lengthSquared <= RhinoMath.ZeroTolerance)
            return Distance2d(point, start);

        var t = Math.Clamp(((wx * vx) + (wy * vy)) / lengthSquared, 0.0, 1.0);
        return Distance2d(point, new Point3d(start.X + (t * vx), start.Y + (t * vy), 0.0));
    }

    private static double Distance2d(Point3d a, Point3d b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private static double GetPagePickTolerance(DetailViewObject detail)
    {
        var bbox = detail.DetailGeometry.GetBoundingBox(true);
        if (!bbox.IsValid)
            return 0.1;

        var span = Math.Min(Math.Abs(bbox.Max.X - bbox.Min.X), Math.Abs(bbox.Max.Y - bbox.Min.Y));
        return Math.Clamp(span * 0.01, 0.06, 0.25);
    }

    private static bool IsLayerVisibleInDetail(RhinoDoc doc, DetailViewObject detail, RhinoObject obj)
    {
        var layer = doc.Layers[obj.Attributes.LayerIndex];
        return layer is not null
            && layer.IsVisible
            && layer.PerViewportIsVisible(detail.Viewport.Id);
    }

    private static string GetLeafLayerName(RhinoDoc doc, RhinoObject obj)
    {
        var layerIndex = obj.Attributes.LayerIndex;
        if (layerIndex < 0 || layerIndex >= doc.Layers.Count)
            return "UNLAYERED";

        var layer = doc.Layers[layerIndex];
        if (layer is null)
            return "UNLAYERED";

        var parts = LayerService.SplitLayerPath(layer.FullPath);
        return parts.Length == 0 ? layer.Name : parts[^1];
    }

    private static string GetLayerPath(RhinoDoc doc, RhinoObject obj)
    {
        var layerIndex = obj.Attributes.LayerIndex;
        if (layerIndex < 0 || layerIndex >= doc.Layers.Count)
            return "UNLAYERED";

        return doc.Layers[layerIndex]?.FullPath ?? "UNLAYERED";
    }

    private static GeometryBase? DuplicateGeometry(RhinoObject obj)
    {
        return obj.Geometry.Duplicate();
    }

    private static List<Guid> AddCurvesToDoc(
        RhinoDoc doc,
        IEnumerable<Curve> curves,
        int layerIndex,
        int linetypeIndex,
        System.Drawing.Color color)
    {
        var ids = new List<Guid>();
        var attrs = CreateTraceAttributes(layerIndex, linetypeIndex, color);
        foreach (var curve in curves)
        {
            if (!curve.IsValid)
                continue;

            var id = doc.Objects.AddCurve(curve, attrs);
            if (id != Guid.Empty)
                ids.Add(id);
        }

        return ids;
    }

    private static ObjectAttributes CreateTraceAttributes(int layerIndex, int linetypeIndex, System.Drawing.Color color)
    {
        var attrs = new ObjectAttributes
        {
            LayerIndex = layerIndex,
            ColorSource = ObjectColorSource.ColorFromObject,
            ObjectColor = color
        };

        if (linetypeIndex >= 0)
        {
            attrs.LinetypeSource = ObjectLinetypeSource.LinetypeFromObject;
            attrs.LinetypeIndex = linetypeIndex;
        }

        return attrs;
    }

    private static List<Curve> EdgeCurvesFromGeometry(GeometryBase geometry, double tolerance)
    {
        switch (geometry)
        {
            case Curve curve:
                return new List<Curve> { curve.DuplicateCurve() };
            case Brep brep:
                var edgeCurves = brep.DuplicateEdgeCurves(false)?.Where(curve => curve is not null && curve.IsValid).ToList();
                if (edgeCurves is not null && edgeCurves.Count > 0)
                    return edgeCurves;
                break;
            case Extrusion extrusion:
                var extrusionBrep = extrusion.ToBrep();
                if (extrusionBrep is not null)
                    return EdgeCurvesFromGeometry(extrusionBrep, tolerance);
                break;
            case Surface surface:
                var surfaceBrep = surface.ToBrep();
                if (surfaceBrep is not null)
                    return EdgeCurvesFromGeometry(surfaceBrep, tolerance);
                break;
            case Mesh mesh:
                var meshCurves = MeshEdgeCurves(mesh, tolerance);
                if (meshCurves.Count > 0)
                    return meshCurves;
                break;
        }

        return BoundingBoxEdgeCurves(geometry, tolerance);
    }

    private static List<Curve> MeshEdgeCurves(Mesh mesh, double tolerance)
    {
        var curves = new List<Curve>();
        var topologyEdges = mesh.TopologyEdges;
        for (var i = 0; i < topologyEdges.Count; i++)
        {
            var line = topologyEdges.EdgeLine(i);
            if (line.IsValid && line.Length > tolerance)
                curves.Add(new LineCurve(line));
        }

        return curves;
    }

    private static List<Curve> BoundingBoxEdgeCurves(GeometryBase geometry, double tolerance)
    {
        var bbox = geometry.GetBoundingBox(true);
        if (!bbox.IsValid)
            return new List<Curve>();

        var corners = bbox.GetCorners();
        var pairs = new[]
        {
            (0, 1), (1, 2), (2, 3), (3, 0),
            (4, 5), (5, 6), (6, 7), (7, 4),
            (0, 4), (1, 5), (2, 6), (3, 7)
        };
        var curves = new List<Curve>();
        foreach (var (a, b) in pairs)
        {
            var line = new Line(corners[a], corners[b]);
            if (line.IsValid && line.Length > tolerance)
                curves.Add(new LineCurve(line));
        }

        return curves;
    }

    private static List<Guid> AddEdgeEndpointConnectors(
        RhinoDoc doc,
        IReadOnlyList<Curve> startCurves,
        IReadOnlyList<Curve> finalCurves,
        int layerIndex,
        int linetypeIndex,
        HashSet<string> usedConnectorKeys,
        System.Drawing.Color color)
    {
        var added = new List<Guid>();
        if (startCurves.Count != finalCurves.Count || startCurves.Count * 2 > 80)
            return added;

        for (var i = 0; i < startCurves.Count; i++)
        {
            added.AddRange(AddConnectorPair(doc, startCurves[i].PointAtStart, finalCurves[i].PointAtStart, layerIndex, linetypeIndex, usedConnectorKeys, color));
            added.AddRange(AddConnectorPair(doc, startCurves[i].PointAtEnd, finalCurves[i].PointAtEnd, layerIndex, linetypeIndex, usedConnectorKeys, color));
        }

        return added;
    }

    private static List<Guid> AddBoundingBoxConnectors(
        RhinoDoc doc,
        GeometryBase startGeometry,
        GeometryBase finalGeometry,
        int layerIndex,
        int linetypeIndex,
        HashSet<string> usedConnectorKeys,
        System.Drawing.Color color)
    {
        var added = new List<Guid>();
        var startBox = startGeometry.GetBoundingBox(true);
        var finalBox = finalGeometry.GetBoundingBox(true);
        if (!startBox.IsValid || !finalBox.IsValid)
            return added;

        var startCorners = startBox.GetCorners();
        var finalCorners = finalBox.GetCorners();
        for (var i = 0; i < Math.Min(startCorners.Length, finalCorners.Length); i++)
            added.AddRange(AddConnectorPair(doc, startCorners[i], finalCorners[i], layerIndex, linetypeIndex, usedConnectorKeys, color));

        return added;
    }

    private static List<Guid> AddConnectorPair(
        RhinoDoc doc,
        Point3d start,
        Point3d end,
        int layerIndex,
        int linetypeIndex,
        HashSet<string> usedConnectorKeys,
        System.Drawing.Color color)
    {
        var key = $"{PointKey(start, doc.ModelAbsoluteTolerance)}>{PointKey(end, doc.ModelAbsoluteTolerance)}";
        if (!usedConnectorKeys.Add(key) || start.DistanceTo(end) <= doc.ModelAbsoluteTolerance)
            return new List<Guid>();

        var line = new Line(start, end);
        if (!line.IsValid)
            return new List<Guid>();

        var attrs = CreateTraceAttributes(layerIndex, linetypeIndex, color);
        var id = doc.Objects.AddCurve(new LineCurve(line), attrs);
        return id == Guid.Empty ? new List<Guid>() : new List<Guid> { id };
    }

    private static string PointKey(Point3d point, double tolerance)
    {
        var keyTolerance = Math.Max(tolerance * 10.0, 0.001);
        return $"{(int)Math.Round(point.X / keyTolerance)}:{(int)Math.Round(point.Y / keyTolerance)}:{(int)Math.Round(point.Z / keyTolerance)}";
    }

    private static int GetOrCreateHiddenLinetypeIndex(RhinoDoc doc)
    {
        foreach (var name in new[] { "Hidden", "Dashed", "Center" })
        {
            var index = doc.Linetypes.Find(name);
            if (index >= 0)
                return index;
        }

        var linetype = new Linetype { Name = "ANNO_Hidden_Dashed" };
        linetype.AppendSegment(0.25, true);
        linetype.AppendSegment(0.125, false);
        return doc.Linetypes.Add(linetype);
    }

    private static System.Drawing.Color GetObjectDisplayColor(RhinoDoc doc, RhinoObject obj)
    {
        try
        {
            return obj.Attributes.DrawColor(doc);
        }
        catch
        {
            var layerIndex = obj.Attributes.LayerIndex;
            if (layerIndex >= 0 && layerIndex < doc.Layers.Count)
                return doc.Layers[layerIndex].Color;
        }

        return System.Drawing.Color.FromArgb(120, 120, 120);
    }

    private static void CreateGroup(RhinoDoc doc, string groupName, IEnumerable<Guid> objectIds)
    {
        var ids = objectIds.Where(id => id != Guid.Empty).Distinct().ToList();
        if (ids.Count == 0)
            return;

        var cleanName = groupName;
        var suffix = 1;
        while (doc.Groups.FindName(cleanName) is not null)
        {
            cleanName = $"{groupName}_{suffix:00}";
            suffix++;
        }

        var groupIndex = doc.Groups.Add(cleanName);
        foreach (var id in ids)
            doc.Groups.AddToGroup(groupIndex, id);
    }

    private static BoundingBox GetBoundingBox(RhinoDoc doc, IEnumerable<Guid> objectIds)
    {
        var bbox = BoundingBox.Empty;
        foreach (var id in objectIds)
        {
            var obj = doc.Objects.FindId(id);
            if (obj is not null)
                bbox.Union(obj.Geometry.GetBoundingBox(true));
        }

        return bbox;
    }

    private static Point3d GetBottomCenter(BoundingBox bbox)
    {
        var corners = bbox.GetCorners();
        if (corners.Length < 4)
            return bbox.Center;

        var x = 0.0;
        var y = 0.0;
        var z = 0.0;
        for (var i = 0; i < 4; i++)
        {
            x += corners[i].X;
            y += corners[i].Y;
            z += corners[i].Z;
        }

        return new Point3d(x / 4.0, y / 4.0, z / 4.0);
    }

    private static double FindBestWorldZRotation(RhinoDoc doc, IReadOnlyList<Guid> objectIds, Point3d rotationPoint, double currentMeasure)
    {
        var bestAngle = 0.0;
        var bestMeasure = currentMeasure;

        for (var degrees = 0.0; degrees < 180.0; degrees += 5.0)
            TestAngle(RhinoMath.ToRadians(degrees));

        var coarseBest = bestAngle;
        for (var degrees = RhinoMath.ToDegrees(coarseBest) - 5.0; degrees <= RhinoMath.ToDegrees(coarseBest) + 5.0; degrees += 0.25)
            TestAngle(RhinoMath.ToRadians(degrees));

        return NormalizeHalfTurn(bestAngle);

        void TestAngle(double angle)
        {
            var measure = BoundingMeasureAfterRotation(doc, objectIds, rotationPoint, angle);
            if (measure < bestMeasure)
            {
                bestMeasure = measure;
                bestAngle = angle;
            }
        }
    }

    private static double BoundingMeasureAfterRotation(RhinoDoc doc, IEnumerable<Guid> objectIds, Point3d rotationPoint, double angle)
    {
        var rotation = Transform.Rotation(angle, Vector3d.ZAxis, rotationPoint);
        var bbox = BoundingBox.Empty;
        foreach (var id in objectIds)
        {
            var obj = doc.Objects.FindId(id);
            if (obj is null)
                continue;

            bbox.Union(obj.Geometry.GetBoundingBox(rotation));
        }

        return BoundingBoxMeasure(bbox);
    }

    private static double NormalizeHalfTurn(double angle)
    {
        var normalized = angle % Math.PI;
        if (normalized < 0)
            normalized += Math.PI;

        if (normalized > Math.PI / 2.0)
            normalized -= Math.PI;

        return normalized;
    }

    private static double BoundingBoxMeasure(BoundingBox bbox)
    {
        if (!bbox.IsValid)
            return double.MaxValue;

        var dimensions = new[]
        {
            Math.Abs(bbox.Max.X - bbox.Min.X),
            Math.Abs(bbox.Max.Y - bbox.Min.Y),
            Math.Abs(bbox.Max.Z - bbox.Min.Z)
        };
        var volume = dimensions[0] * dimensions[1] * dimensions[2];
        if (volume > RhinoMath.ZeroTolerance)
            return volume;

        Array.Sort(dimensions);
        return dimensions[1] * dimensions[2];
    }

    private sealed record ObjectPageHit(double Distance, double ViewDepth, bool FaceHit);

    private sealed record ObjectPageMatch(RhinoObject Object, double Distance, double ViewDepth, bool FaceHit);
}

public sealed record LabelPartLeaderResult(Guid LeaderId, string Label, Guid SourceObjectId, double PickDistance);

public sealed record LabelPartCandidate(Guid ObjectId, string Label, string LayerPath, double ProjectedDistance, double ViewDepth);
