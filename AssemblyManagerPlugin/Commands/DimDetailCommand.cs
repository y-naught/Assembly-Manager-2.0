using Rhino;
using Rhino.Commands;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Input.Custom;

namespace AssemblyManagerPlugin;

public sealed class DimDetailCommand : Command
{
    public override string EnglishName => "DimDetail";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        if (!TryGetActivePage(doc, out var pageView))
            return Result.Failure;

        var getDetail = new GetObject();
        getDetail.SetCommandPrompt("Select detail to dimension");
        getDetail.GeometryFilter = ObjectType.Detail;
        getDetail.EnablePreSelect(true, true);
        getDetail.Get();
        if (getDetail.CommandResult() != Result.Success)
            return getDetail.CommandResult();

        try
        {
            var detailId = getDetail.Object(0).ObjectId;
            var detail = FindDetail(pageView!, detailId);
            if (detail is not null && detail.Viewport.IsPerspectiveProjection)
                RhinoApp.WriteLine("DimDetail warning: selected detail is perspective; world XY dimensions may not align with the view.");

            var result = AssemblyManagerPlugin.Instance.Services.DetailDimension().DimensionDetail(doc, detailId);
            RhinoApp.WriteLine("DimDetail added {0} dimension(s) around {1} object(s).", result.DimensionCount, result.ObjectCount);
            return Result.Success;
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine("DimDetail failed: {0}", ex.Message);
            return Result.Failure;
        }
    }

    private static bool TryGetActivePage(RhinoDoc doc, out RhinoPageView? pageView)
    {
        pageView = doc.Views.ActiveView as RhinoPageView;
        if (pageView is null || doc.ActiveSpace != ActiveSpace.PageSpace)
        {
            RhinoApp.WriteLine("DimDetail must be run from layout space.");
            return false;
        }

        if (!pageView.PageIsActive)
        {
            RhinoApp.WriteLine("DimDetail must be run with the layout page active, not inside an active detail.");
            return false;
        }

        return true;
    }

    private static DetailViewObject? FindDetail(RhinoPageView pageView, Guid detailId)
    {
        return pageView.GetDetailViews().FirstOrDefault(detail => detail.Id == detailId);
    }
}
