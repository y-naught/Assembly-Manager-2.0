using AssemblyManagerPlugin.Services;
using Eto.Drawing;
using Eto.Forms;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input.Custom;
using Rhino.UI;
using RhinoCommand = Rhino.Commands.Command;

namespace AssemblyManagerPlugin;

public sealed class RegroupCommand : RhinoCommand
{
    public override string EnglishName => "Regroup";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var getter = new GetObject();
        getter.SetCommandPrompt("Select objects to regroup");
        getter.GeometryFilter = ObjectType.AnyObject;
        getter.GetMultiple(1, 0);
        if (getter.CommandResult() != Result.Success)
            return getter.CommandResult();

        var groupName = AssemblyManagerPlugin.Instance.Services.UtilityGeometry().Regroup(doc, getter.Objects().Select(o => o.ObjectId));
        RhinoApp.WriteLine("Created group {0}.", groupName);
        return Result.Success;
    }
}

public sealed class MoveOrthoCommand : RhinoCommand
{
    public override string EnglishName => "MoveOrtho";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var getter = new GetObject();
        getter.SetCommandPrompt("Select objects to move orthogonally");
        getter.GeometryFilter = ObjectType.AnyObject;
        getter.GetMultiple(1, 0);
        if (getter.CommandResult() != Result.Success)
            return getter.CommandResult();

        var axisGetter = new GetOption();
        axisGetter.SetCommandPrompt("Orthogonal axis");
        axisGetter.AddOption("X");
        axisGetter.AddOption("Y");
        axisGetter.AddOption("Z");
        axisGetter.Get();
        if (axisGetter.CommandResult() != Result.Success)
            return axisGetter.CommandResult();

        var pointGetter = new GetPoint();
        pointGetter.SetCommandPrompt("Start point");
        pointGetter.Get();
        if (pointGetter.CommandResult() != Result.Success)
            return pointGetter.CommandResult();
        var start = pointGetter.Point();

        var axis = axisGetter.Option().Index switch
        {
            1 => Vector3d.XAxis,
            2 => Vector3d.YAxis,
            _ => Vector3d.ZAxis
        };

        var endGetter = new OrthogonalMovePointGetter(doc, getter.Objects().Select(o => o.ObjectId), axis, start);
        endGetter.SetCommandPrompt("End point");
        endGetter.SetBasePoint(start, true);
        endGetter.DrawLineFromPoint(start, false);
        endGetter.Get();
        if (endGetter.CommandResult() != Result.Success)
            return endGetter.CommandResult();

        AssemblyManagerPlugin.Instance.Services.UtilityGeometry().MoveOrtho(doc, getter.Objects().Select(o => o.ObjectId), start, endGetter.Point(), axis);
        return Result.Success;
    }
}

public sealed class MotionTraceCommand : RhinoCommand
{
    public override string EnglishName => "MotionTrace";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var getter = new GetObject();
        getter.SetCommandPrompt("Select objects to move with trace");
        getter.GeometryFilter = ObjectType.Brep | ObjectType.Extrusion | ObjectType.Surface;
        getter.GetMultiple(1, 0);
        if (getter.CommandResult() != Result.Success)
            return getter.CommandResult();

        var startGetter = new GetPoint();
        startGetter.SetCommandPrompt("Start point");
        startGetter.Get();
        if (startGetter.CommandResult() != Result.Success)
            return startGetter.CommandResult();

        var endGetter = new GetPoint();
        endGetter.SetCommandPrompt("End point");
        endGetter.SetBasePoint(startGetter.Point(), true);
        endGetter.DrawLineFromPoint(startGetter.Point(), true);
        endGetter.Get();
        if (endGetter.CommandResult() != Result.Success)
            return endGetter.CommandResult();

        var moved = AssemblyManagerPlugin.Instance.Services.UtilityGeometry().MotionTrace(
            doc,
            getter.Objects().Select(o => o.ObjectId),
            startGetter.Point(),
            endGetter.Point());
        RhinoApp.WriteLine("Moved {0} object(s) and created trace linework.", moved);
        return Result.Success;
    }
}

public sealed class OrientToWorldCommand : RhinoCommand
{
    public override string EnglishName => "OrientToWorld";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var getter = new GetObject();
        getter.SetCommandPrompt("Select objects to orient to world");
        getter.GeometryFilter = ObjectType.AnyObject;
        getter.GetMultiple(1, 0);
        if (getter.CommandResult() != Result.Success)
            return getter.CommandResult();

        var count = AssemblyManagerPlugin.Instance.Services.UtilityGeometry().OrientToWorld(doc, getter.Objects().Select(o => o.ObjectId));
        RhinoApp.WriteLine("Oriented {0} object(s).", count);
        return Result.Success;
    }
}

public sealed class Split3PtCommand : RhinoCommand
{
    public override string EnglishName => "Split3Pt";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var getter = new GetObject();
        getter.SetCommandPrompt("Select polysurfaces or extrusions to split");
        getter.GeometryFilter = ObjectType.Brep | ObjectType.Extrusion;
        var capToggle = new OptionToggle(true, "No", "Yes");
        getter.AddOptionToggle("Cap", ref capToggle);

        while (true)
        {
            var result = getter.GetMultiple(1, 0);
            if (result == Rhino.Input.GetResult.Option)
                continue;

            break;
        }

        if (getter.CommandResult() != Result.Success)
            return getter.CommandResult();

        var points = new List<Point3d>();
        for (var i = 1; i <= 3; i++)
        {
            var pointGetter = new GetPoint();
            pointGetter.SetCommandPrompt($"Plane point {i}");
            pointGetter.Get();
            if (pointGetter.CommandResult() != Result.Success)
                return pointGetter.CommandResult();

            points.Add(pointGetter.Point());
        }

        try
        {
            var pieces = AssemblyManagerPlugin.Instance.Services.UtilityGeometry().SplitByThreePointPlane(
                doc,
                getter.Objects().Select(o => o.ObjectId),
                points[0],
                points[1],
                points[2],
                capToggle.CurrentValue);
            RhinoApp.WriteLine("Created {0} split piece(s).", pieces);
            return Result.Success;
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine("Split3Pt failed: {0}", ex.Message);
            return Result.Failure;
        }
    }
}

internal sealed class OrthogonalMovePointGetter : GetPoint
{
    private static readonly System.Drawing.Color PreviewColor = System.Drawing.Color.FromArgb(40, 120, 255);
    private readonly RhinoDoc _doc;
    private readonly List<RhinoObject> _objects = new();
    private readonly Vector3d _axis;
    private readonly Point3d _basePoint;

    public OrthogonalMovePointGetter(RhinoDoc doc, IEnumerable<Guid> objectIds, Vector3d axis, Point3d basePoint)
    {
        _doc = doc;
        _axis = axis;
        _axis.Unitize();
        _basePoint = basePoint;

        foreach (var objectId in objectIds)
        {
            var rhinoObject = doc.Objects.FindId(objectId);
            if (rhinoObject is not null)
                _objects.Add(rhinoObject);
        }
    }

    protected override void OnDynamicDraw(GetPointDrawEventArgs e)
    {
        base.OnDynamicDraw(e);
        var transform = Transform.Translation(ConstrainedVector(e.CurrentPoint));
        var previewPoint = _basePoint;
        previewPoint.Transform(transform);
        e.Display.DrawLine(_basePoint, previewPoint, PreviewColor, 2);

        foreach (var rhinoObject in _objects)
            DrawTransformedPreview(e, rhinoObject, transform);
    }

    private Vector3d ConstrainedVector(Point3d target)
    {
        var delta = target - _basePoint;
        return _axis * (delta * _axis);
    }

    private static void DrawTransformedPreview(GetPointDrawEventArgs e, RhinoObject rhinoObject, Transform transform)
    {
        var geometry = rhinoObject.Geometry.Duplicate();
        if (geometry is null)
            return;

        geometry.Transform(transform);
        switch (geometry)
        {
            case Curve curve:
                e.Display.DrawCurve(curve, PreviewColor, 2);
                break;
            case Brep brep:
                e.Display.DrawBrepWires(brep, PreviewColor, 1);
                break;
            case Extrusion extrusion:
                var extrusionBrep = extrusion.ToBrep();
                if (extrusionBrep is not null)
                    e.Display.DrawBrepWires(extrusionBrep, PreviewColor, 1);
                break;
            case Mesh mesh:
                e.Display.DrawMeshWires(mesh, PreviewColor);
                break;
            case Rhino.Geometry.Point point:
                e.Display.DrawPoint(point.Location, PreviewColor);
                break;
        }
    }
}

public sealed class LabelPartCommand : RhinoCommand
{
    public override string EnglishName => "LabelPart";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        if (doc.ActiveSpace != ActiveSpace.PageSpace)
        {
            RhinoApp.WriteLine("LabelPart must be run from layout/page space.");
            return Result.Failure;
        }

        try
        {
            var tipResult = GetLeaderTip("Leader tip point on layout detail object", acceptNothing: false, out var tipPoint);
            if (tipResult != Result.Success)
                return tipResult;

            var candidate = ResolveLabelCandidate(doc, tipPoint);
            if (candidate is null)
                return Result.Cancel;

            var leaderPoints = CollectLeaderPoints(tipPoint, candidate.Label);
            if (leaderPoints is null)
                return Result.Cancel;

            var result = AssemblyManagerPlugin.Instance.Services.UtilityGeometry().AddLayerLeaderForObject(
                doc,
                leaderPoints,
                candidate.ObjectId,
                candidate.ProjectedDistance);
            RhinoApp.WriteLine("Created LabelPart leader '{0}'.", result.Label);
            doc.Objects.Select(result.LeaderId);
            return Result.Success;
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine("LabelPart failed: {0}", ex.Message);
            return Result.Failure;
        }
    }

    private static List<Point3d>? CollectLeaderPoints(Point3d tipPoint, string label)
    {
        const int maxPoints = 20;
        var points = new List<Point3d> { tipPoint };
        var previous = tipPoint;
        while (points.Count < maxPoints)
        {
            var getter = new LeaderPreviewPointGetter(points, label);
            getter.SetCommandPrompt(points.Count == 1
                ? "Leader elbow or text point"
                : "Next leader point or press Enter to finish");
            getter.SetBasePoint(previous, true);
            getter.AcceptNothing(points.Count >= 2);
            var result = getter.Get();
            if (result == Rhino.Input.GetResult.Point)
            {
                var point = getter.Point();
                if (point.DistanceTo(previous) > RhinoMath.ZeroTolerance)
                    points.Add(point);
                previous = point;
                continue;
            }

            if (result == Rhino.Input.GetResult.Nothing && points.Count >= 2)
                break;

            return null;
        }

        return points.Count >= 2 ? points : null;
    }

    internal static Result GetLeaderTip(string prompt, bool acceptNothing, out Point3d point)
    {
        var tipGetter = new GetPoint();
        tipGetter.SetCommandPrompt(prompt);
        tipGetter.AcceptNothing(acceptNothing);
        var result = tipGetter.Get();
        if (result == Rhino.Input.GetResult.Nothing)
        {
            point = Point3d.Unset;
            return Result.Nothing;
        }

        if (tipGetter.CommandResult() != Result.Success)
        {
            point = Point3d.Unset;
            return tipGetter.CommandResult();
        }

        point = tipGetter.Point();
        return Result.Success;
    }

    internal static LabelPartCandidate? ResolveLabelCandidate(RhinoDoc doc, Point3d tipPoint)
    {
        var candidates = AssemblyManagerPlugin.Instance.Services.UtilityGeometry().GetLabelPartCandidates(doc, tipPoint);
        if (candidates.Count == 0)
            throw new InvalidOperationException("No visible model object was found under the leader tip point.");
        if (!ShouldPromptForCandidate(doc, candidates))
            return candidates[0];

        var dialog = new LabelPartCandidateDialog(candidates);
        return dialog.ShowModal(RhinoEtoApp.MainWindow);
    }

    private static bool ShouldPromptForCandidate(RhinoDoc doc, IReadOnlyList<LabelPartCandidate> candidates)
    {
        if (candidates.Count < 2)
            return false;

        var first = candidates[0];
        var second = candidates[1];
        var projectedClose = Math.Abs(first.ProjectedDistance - second.ProjectedDistance) <= 0.03;
        var depthClose = Math.Abs(first.ViewDepth - second.ViewDepth) <= Math.Max(doc.ModelAbsoluteTolerance * 10.0, 0.01);
        return projectedClose && depthClose;
    }
}

public sealed class LabelPartsCommand : RhinoCommand
{
    public override string EnglishName => "LabelParts";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        if (doc.ActiveSpace != ActiveSpace.PageSpace)
        {
            RhinoApp.WriteLine("LabelParts must be run from layout/page space.");
            return Result.Failure;
        }

        var created = 0;
        var emptyPresses = 0;
        while (true)
        {
            try
            {
                var tipResult = LabelPartCommand.GetLeaderTip(
                    created == 0
                        ? "Leader tip point on layout detail object or press Enter twice to finish"
                        : "Next leader tip point or press Enter twice to finish",
                    acceptNothing: true,
                    out var tipPoint);

                if (tipResult == Result.Nothing)
                {
                    emptyPresses++;
                    if (emptyPresses >= 2)
                        break;

                    continue;
                }

                if (tipResult != Result.Success)
                    return tipResult;

                emptyPresses = 0;
                var candidate = LabelPartCommand.ResolveLabelCandidate(doc, tipPoint);
                if (candidate is null)
                    continue;

                var endGetter = new LeaderPreviewPointGetter(new[] { tipPoint }, candidate.Label);
                endGetter.SetCommandPrompt("Leader text point");
                endGetter.SetBasePoint(tipPoint, true);
                endGetter.AcceptNothing(false);
                var endResult = endGetter.Get();
                if (endResult != Rhino.Input.GetResult.Point)
                    return endGetter.CommandResult() == Result.Success ? Result.Cancel : endGetter.CommandResult();

                var result = AssemblyManagerPlugin.Instance.Services.UtilityGeometry().AddLayerLeaderForObject(
                    doc,
                    new[] { tipPoint, endGetter.Point() },
                    candidate.ObjectId,
                    candidate.ProjectedDistance);
                RhinoApp.WriteLine("Created LabelParts leader '{0}'.", result.Label);
                created++;
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine("LabelParts failed: {0}", ex.Message);
                return Result.Failure;
            }
        }

        RhinoApp.WriteLine("Created {0} leader label(s).", created);
        return Result.Success;
    }
}

internal sealed class LeaderPreviewPointGetter : GetPoint
{
    private static readonly System.Drawing.Color LineColor = System.Drawing.Color.Black;
    private static readonly System.Drawing.Color DotColor = System.Drawing.Color.White;
    private readonly List<Point3d> _fixedPoints;
    private readonly string _label;

    public LeaderPreviewPointGetter(IEnumerable<Point3d> fixedPoints, string label)
    {
        _fixedPoints = fixedPoints.ToList();
        _label = label;
    }

    protected override void OnDynamicDraw(GetPointDrawEventArgs e)
    {
        base.OnDynamicDraw(e);
        if (_fixedPoints.Count == 0)
            return;

        var points = _fixedPoints.Concat(new[] { e.CurrentPoint }).ToList();
        for (var i = 0; i < points.Count - 1; i++)
            e.Display.DrawLine(points[i], points[i + 1], LineColor, 2);

        if (!string.IsNullOrWhiteSpace(_label))
            e.Display.DrawDot(e.CurrentPoint, _label, DotColor, LineColor);
    }
}

internal sealed class LabelPartCandidateDialog : Dialog<LabelPartCandidate?>
{
    private readonly IReadOnlyList<LabelPartCandidate> _candidates;
    private readonly ListBox _list = new();

    public LabelPartCandidateDialog(IReadOnlyList<LabelPartCandidate> candidates)
    {
        _candidates = candidates;
        Title = "Choose Part Label";
        Padding = new Padding(12);
        Resizable = true;
        Size = new Size(520, 320);

        _list.DataStore = _candidates
            .Take(12)
            .Select(candidate => $"{candidate.Label} | {candidate.LayerPath}")
            .ToList();
        _list.SelectedIndex = 0;

        var useButton = new Button { Text = "Use Selected" };
        useButton.Click += (_, _) => Close(SelectedCandidate());

        var cancelButton = new Button { Text = "Cancel" };
        cancelButton.Click += (_, _) => Close(null);

        var layout = new DynamicLayout { Spacing = new Size(8, 8) };
        layout.AddRow(new Label { Text = "Multiple objects are close to that leader tip. Choose the layer to label." });
        layout.Add(_list, xscale: true, yscale: true);
        layout.AddRow(null, cancelButton, useButton);
        Content = layout;
    }

    private LabelPartCandidate? SelectedCandidate()
    {
        var index = _list.SelectedIndex;
        return index >= 0 && index < _candidates.Count ? _candidates[index] : null;
    }
}
