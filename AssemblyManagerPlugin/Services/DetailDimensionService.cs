using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace AssemblyManagerPlugin.Services;

public sealed class DetailDimensionService
{
    public DetailDimensionResult DimensionDetail(RhinoDoc doc, Guid detailId)
    {
        var pageView = FindDetailPage(doc, detailId, out var detail)
            ?? throw new InvalidOperationException("Selected object is not a layout detail.");

        if (detail is null)
            throw new InvalidOperationException("Selected object is not a layout detail.");

        var dimStyle = doc.DimStyles.Current
            ?? throw new InvalidOperationException("The document does not have a current annotation style.");

        var candidates = GetVisibleDimensionableObjects(doc, detail).ToList();
        if (candidates.Count == 0)
            return new DetailDimensionResult();

        var detailPageBox = detail.DetailGeometry.GetBoundingBox(true);
        var textHeight = GetTextHeight(dimStyle, detailPageBox);
        var tempBoxIds = new List<Guid>();
        var placements = new List<PlacedDimension>();
        var result = new DetailDimensionResult { ObjectCount = candidates.Count };

        try
        {
            foreach (var obj in candidates)
            {
                var modelBox = obj.Geometry.GetBoundingBox(true);
                if (!modelBox.IsValid)
                    continue;

                var width = modelBox.Max.X - modelBox.Min.X;
                var height = modelBox.Max.Y - modelBox.Min.Y;
                if (width <= doc.ModelAbsoluteTolerance || height <= doc.ModelAbsoluteTolerance)
                    continue;

                var tempBoxId = AddTemporaryWorldXyBoundingBox(doc, modelBox);
                if (tempBoxId != Guid.Empty)
                    tempBoxIds.Add(tempBoxId);

                var footprint = ProjectWorldXyBox(detail, modelBox);
                var pageBox = BoundingBoxFromPoints(footprint.AllCorners);
                if (!pageBox.IsValid)
                    continue;

                result.DimensionCount += AddBoxDimensions(
                    doc,
                    pageView,
                    detail,
                    dimStyle,
                    detailPageBox,
                    pageBox,
                    footprint,
                    textHeight,
                    placements);
            }
        }
        finally
        {
            foreach (var tempBoxId in tempBoxIds)
                DeleteTemporaryObject(doc, tempBoxId);
        }

        doc.Views.Redraw();
        return result;
    }

    private int AddBoxDimensions(
        RhinoDoc doc,
        RhinoPageView pageView,
        DetailViewObject detail,
        DimensionStyle dimStyle,
        BoundingBox detailPageBox,
        BoundingBox pageBox,
        ProjectedWorldXyBox footprint,
        double textHeight,
        List<PlacedDimension> placements)
    {
        var added = 0;
        var baseOffset = GetBaseOffset(detailPageBox, pageBox, textHeight);
        var laneSpacing = Math.Max(textHeight * 2.0, baseOffset * 0.65);
        var center = pageBox.Center;

        var horizontalEdge = PickHigherMidpoint(footprint.Bottom, footprint.Top);
        var horizontal = CreateOffsetDimensionLine(
            horizontalEdge,
            center,
            baseOffset,
            laneSpacing,
            DimensionOrientation.Horizontal,
            DimensionSide.Positive,
            placements);

        var horizontalId = AddDimension(
            doc,
            pageView,
            detail,
            dimStyle,
            horizontal.Start,
            horizontal.End,
            horizontal.LinePoint,
            DimensionOrientation.Horizontal);
        if (horizontalId != Guid.Empty)
            added++;

        var rightSpace = detailPageBox.Max.X - pageBox.Max.X;
        var leftSpace = pageBox.Min.X - detailPageBox.Min.X;
        var verticalEdge = rightSpace >= leftSpace
            ? PickHigherMidpointX(footprint.Left, footprint.Right)
            : PickLowerMidpointX(footprint.Left, footprint.Right);
        var vertical = CreateOffsetDimensionLine(
            verticalEdge,
            center,
            baseOffset,
            laneSpacing,
            DimensionOrientation.Vertical,
            rightSpace >= leftSpace ? DimensionSide.Positive : DimensionSide.Negative,
            placements);

        var verticalId = AddDimension(
            doc,
            pageView,
            detail,
            dimStyle,
            vertical.Start,
            vertical.End,
            vertical.LinePoint,
            DimensionOrientation.Vertical);
        if (verticalId != Guid.Empty)
            added++;

        return added;
    }

    private static Guid AddDimension(
        RhinoDoc doc,
        RhinoPageView pageView,
        DetailViewObject detail,
        DimensionStyle dimStyle,
        Point3d start,
        Point3d end,
        Point3d dimLinePoint,
        DimensionOrientation orientation)
    {
        var normalized = NormalizeDimensionPoints(start, end, orientation);
        var rotation = orientation == DimensionOrientation.Vertical ? Math.PI * 0.5 : 0.0;
        var dimension = LinearDimension.Create(
            AnnotationType.Rotated,
            dimStyle,
            Plane.WorldXY,
            Vector3d.XAxis,
            normalized.Start,
            normalized.End,
            dimLinePoint,
            rotation);

        if (dimension is null)
            return Guid.Empty;

        dimension.DimensionStyleId = dimStyle.Id;
        dimension.DimensionScale = 1.0;
        SetDetailDistanceScale(dimension, detail);

        var attributes = new ObjectAttributes
        {
            Space = ActiveSpace.PageSpace,
            ViewportId = pageView.MainViewport.Id,
            LayerIndex = doc.Layers.CurrentLayerIndex,
            Name = "AM DimDetail"
        };

        return doc.Objects.AddLinearDimension(dimension, attributes);
    }

    private static DimensionEdge NormalizeDimensionPoints(Point3d start, Point3d end, DimensionOrientation orientation)
    {
        if (orientation == DimensionOrientation.Vertical)
            return start.Y <= end.Y ? new DimensionEdge(start, end) : new DimensionEdge(end, start);

        return start.X <= end.X ? new DimensionEdge(start, end) : new DimensionEdge(end, start);
    }

    private Guid AddTemporaryWorldXyBoundingBox(RhinoDoc doc, BoundingBox modelBox)
    {
        var z = modelBox.Min.Z;
        var points = new[]
        {
            new Point3d(modelBox.Min.X, modelBox.Min.Y, z),
            new Point3d(modelBox.Max.X, modelBox.Min.Y, z),
            new Point3d(modelBox.Max.X, modelBox.Max.Y, z),
            new Point3d(modelBox.Min.X, modelBox.Max.Y, z),
            new Point3d(modelBox.Min.X, modelBox.Min.Y, z)
        };

        var attributes = new ObjectAttributes
        {
            Space = ActiveSpace.ModelSpace,
            LayerIndex = doc.Layers.CurrentLayerIndex,
            Mode = ObjectMode.Hidden,
            Name = "AM DimDetail temporary bounding box"
        };

        return doc.Objects.AddCurve(new PolylineCurve(points), attributes);
    }

    private static void DeleteTemporaryObject(RhinoDoc doc, Guid objectId)
    {
        var obj = doc.Objects.FindId(objectId);
        if (obj is not null)
            doc.Objects.Delete(obj, true, true);
    }

    private IEnumerable<RhinoObject> GetVisibleDimensionableObjects(RhinoDoc doc, DetailViewObject detail)
    {
        var settings = new ObjectEnumeratorSettings
        {
            ActiveObjects = true,
            NormalObjects = true,
            LockedObjects = true,
            HiddenObjects = false,
            VisibleFilter = true,
            ObjectTypeFilter = ObjectType.Brep | ObjectType.Extrusion,
            IncludeLights = false,
            IncludeGrips = false
        };

        foreach (var obj in doc.Objects.GetObjectList(settings))
        {
            if (obj.Attributes.Space != ActiveSpace.ModelSpace)
                continue;

            if (!IsPolysurfaceOrExtrusion(obj))
                continue;

            if (!IsLayerVisibleInDetail(doc, detail, obj))
                continue;

            var box = obj.Geometry.GetBoundingBox(true);
            if (box.IsValid && detail.Viewport.IsVisible(box))
                yield return obj;
        }
    }

    private static bool IsPolysurfaceOrExtrusion(RhinoObject obj)
    {
        return obj.Geometry switch
        {
            Extrusion => true,
            Brep brep => brep.Faces.Count > 1,
            _ => false
        };
    }

    private static bool IsLayerVisibleInDetail(RhinoDoc doc, DetailViewObject detail, RhinoObject obj)
    {
        var layer = doc.Layers[obj.Attributes.LayerIndex];
        return layer is not null
            && layer.IsVisible
            && layer.PerViewportIsVisible(detail.Viewport.Id);
    }

    private static RhinoPageView? FindDetailPage(RhinoDoc doc, Guid detailId, out DetailViewObject? detail)
    {
        foreach (var page in doc.Views.GetPageViews())
        {
            foreach (var candidate in page.GetDetailViews())
            {
                if (candidate.Id != detailId)
                    continue;

                detail = candidate;
                return page;
            }
        }

        detail = null;
        return null;
    }

    private static ProjectedWorldXyBox ProjectWorldXyBox(DetailViewObject detail, BoundingBox modelBox)
    {
        var z = modelBox.Min.Z;
        var lowerLeft = TransformPoint(detail.WorldToPageTransform, new Point3d(modelBox.Min.X, modelBox.Min.Y, z));
        var lowerRight = TransformPoint(detail.WorldToPageTransform, new Point3d(modelBox.Max.X, modelBox.Min.Y, z));
        var upperRight = TransformPoint(detail.WorldToPageTransform, new Point3d(modelBox.Max.X, modelBox.Max.Y, z));
        var upperLeft = TransformPoint(detail.WorldToPageTransform, new Point3d(modelBox.Min.X, modelBox.Max.Y, z));

        return new ProjectedWorldXyBox
        {
            Bottom = new DimensionEdge(lowerLeft, lowerRight),
            Right = new DimensionEdge(lowerRight, upperRight),
            Top = new DimensionEdge(upperLeft, upperRight),
            Left = new DimensionEdge(lowerLeft, upperLeft),
            AllCorners = ProjectFullBoundingBox(detail, modelBox)
        };
    }

    private static IReadOnlyList<Point3d> ProjectFullBoundingBox(DetailViewObject detail, BoundingBox modelBox)
    {
        return modelBox.GetCorners()
            .Select(point => TransformPoint(detail.WorldToPageTransform, point))
            .ToList();
    }

    private static Point3d TransformPoint(Transform transform, Point3d point)
    {
        point.Transform(transform);
        return point;
    }

    private static BoundingBox BoundingBoxFromPoints(IEnumerable<Point3d> points)
    {
        var box = BoundingBox.Empty;
        foreach (var point in points)
            box.Union(point);

        return box;
    }

    private static DimensionPlacement CreateOffsetDimensionLine(
        DimensionEdge edge,
        Point3d objectCenter,
        double baseOffset,
        double laneSpacing,
        DimensionOrientation orientation,
        DimensionSide side,
        List<PlacedDimension> placements)
    {
        var direction = edge.End - edge.Start;
        if (!direction.Unitize())
            direction = Vector3d.XAxis;

        var outward = new Vector3d(-direction.Y, direction.X, 0.0);
        if (outward * (edge.Midpoint - objectCenter) < 0)
            outward.Reverse();

        var linePoint = edge.Midpoint + outward * baseOffset;
        var span = orientation == DimensionOrientation.Horizontal
            ? new Interval(Math.Min(edge.Start.X, edge.End.X), Math.Max(edge.Start.X, edge.End.X))
            : new Interval(Math.Min(edge.Start.Y, edge.End.Y), Math.Max(edge.Start.Y, edge.End.Y));

        var coordinate = orientation == DimensionOrientation.Horizontal ? linePoint.Y : linePoint.X;
        var attempts = 0;
        while (placements.Any(existing => existing.ConflictsWith(orientation, side, span, coordinate, laneSpacing)) && attempts < 20)
        {
            linePoint += outward * laneSpacing;
            coordinate = orientation == DimensionOrientation.Horizontal ? linePoint.Y : linePoint.X;
            attempts++;
        }

        placements.Add(new PlacedDimension(orientation, side, span, coordinate));
        return new DimensionPlacement(edge.Start, edge.End, linePoint);
    }

    private static DimensionEdge PickHigherMidpoint(DimensionEdge first, DimensionEdge second)
    {
        return first.Midpoint.Y >= second.Midpoint.Y ? first : second;
    }

    private static DimensionEdge PickLowerMidpoint(DimensionEdge first, DimensionEdge second)
    {
        return first.Midpoint.Y <= second.Midpoint.Y ? first : second;
    }

    private static DimensionEdge PickHigherMidpointX(DimensionEdge first, DimensionEdge second)
    {
        return first.Midpoint.X >= second.Midpoint.X ? first : second;
    }

    private static DimensionEdge PickLowerMidpointX(DimensionEdge first, DimensionEdge second)
    {
        return first.Midpoint.X <= second.Midpoint.X ? first : second;
    }

    private static double GetBaseOffset(BoundingBox detailPageBox, BoundingBox objectPageBox, double textHeight)
    {
        var detailSpan = Math.Min(detailPageBox.Max.X - detailPageBox.Min.X, detailPageBox.Max.Y - detailPageBox.Min.Y);
        var objectSpan = Math.Min(objectPageBox.Max.X - objectPageBox.Min.X, objectPageBox.Max.Y - objectPageBox.Min.Y);
        var offset = Math.Max(textHeight * 3.0, objectSpan * 0.12);
        var upper = Math.Max(textHeight * 3.0, detailSpan * 0.08);
        return Math.Min(offset, upper);
    }

    private static double GetTextHeight(DimensionStyle dimStyle, BoundingBox detailPageBox)
    {
        if (dimStyle.TextHeight > RhinoMath.ZeroTolerance)
            return dimStyle.TextHeight;

        var detailSpan = Math.Min(detailPageBox.Max.X - detailPageBox.Min.X, detailPageBox.Max.Y - detailPageBox.Min.Y);
        return Math.Max(detailSpan * 0.015, 0.125);
    }

    private static void SetDetailDistanceScale(LinearDimension dimension, DetailViewObject detail)
    {
        var pageToModelRatio = detail.DetailGeometry.PageToModelRatio;
        if (pageToModelRatio > RhinoMath.ZeroTolerance)
            dimension.DistanceScale = 1.0 / pageToModelRatio;
    }

    private readonly record struct DimensionEdge(Point3d Start, Point3d End)
    {
        public Point3d Midpoint => new((Start.X + End.X) * 0.5, (Start.Y + End.Y) * 0.5, (Start.Z + End.Z) * 0.5);
    }

    private readonly record struct DimensionPlacement(Point3d Start, Point3d End, Point3d LinePoint);

    private sealed class ProjectedWorldXyBox
    {
        public DimensionEdge Bottom { get; init; }
        public DimensionEdge Right { get; init; }
        public DimensionEdge Top { get; init; }
        public DimensionEdge Left { get; init; }
        public IReadOnlyList<Point3d> AllCorners { get; init; } = Array.Empty<Point3d>();
    }

    private sealed class PlacedDimension
    {
        private readonly DimensionOrientation _orientation;
        private readonly DimensionSide _side;
        private readonly Interval _span;
        private readonly double _coordinate;

        public PlacedDimension(DimensionOrientation orientation, DimensionSide side, Interval span, double coordinate)
        {
            _orientation = orientation;
            _side = side;
            _span = span;
            _coordinate = coordinate;
        }

        public bool ConflictsWith(DimensionOrientation orientation, DimensionSide side, Interval span, double coordinate, double laneSpacing)
        {
            return _orientation == orientation
                && _side == side
                && IntervalsOverlap(_span, span)
                && Math.Abs(_coordinate - coordinate) < laneSpacing;
        }

        private static bool IntervalsOverlap(Interval first, Interval second)
        {
            return first.T0 <= second.T1 && second.T0 <= first.T1;
        }
    }

    private enum DimensionOrientation
    {
        Horizontal,
        Vertical
    }

    private enum DimensionSide
    {
        Negative,
        Positive
    }
}

public sealed class DetailDimensionResult
{
    public int ObjectCount { get; set; }
    public int DimensionCount { get; set; }
}
