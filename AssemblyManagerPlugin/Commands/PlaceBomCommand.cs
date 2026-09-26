using AssemblyManagerPlugin.Infrastructure;
using AssemblyManagerPlugin.UI;
using Rhino;
using Rhino.Commands;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input.Custom;
using Rhino.UI;

namespace AssemblyManagerPlugin;

public sealed class PlaceBomCommand : Command
{
    public override string EnglishName => "PlaceBOM";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        => RunPlacement(doc, AssemblyManagerPlugin.Instance.Services);

    internal static Result RunPlacement(RhinoDoc doc, ServiceFactory services, string? assemblyName = null)
    {
        if (doc.ActiveSpace != ActiveSpace.PageSpace || doc.Views.ActiveView is not RhinoPageView page)
        {
            RhinoApp.WriteLine("PlaceBOM must be run on a layout sheet. Switch to a layout, exit any active detail, and run PlaceBOM again.");
            return Result.Failure;
        }
        var pageId = page.MainViewport.Id;
        try
        {
            assemblyName ??= CommandPickers.PickAssembly(doc, "Assembly for BOM");
            if (assemblyName is null)
                return Result.Cancel;

            using var columns = new BomColumnsDialog();
            if (!columns.ShowModal(RhinoEtoApp.MainWindow))
                return Result.Cancel;

            using var firstGetter = new GetPoint();
            firstGetter.SetCommandPrompt("First corner of the BOM rectangle on the layout sheet");
            firstGetter.Constrain(Plane.WorldXY, false);
            firstGetter.Get();
            if (firstGetter.CommandResult() != Result.Success)
                return firstGetter.CommandResult();
            var first = firstGetter.Point();
            using var oppositeGetter = new GetPoint();
            oppositeGetter.SetCommandPrompt("Opposite corner of the BOM rectangle (sets width and maximum height)");
            oppositeGetter.Constrain(Plane.WorldXY, false);
            oppositeGetter.SetBasePoint(first, true);
            oppositeGetter.DynamicDraw += (_, e) =>
            {
                if (!e.CurrentPoint.IsValid)
                    return;
                e.Display.DrawPolyline(new[]
                {
                    first, new Point3d(e.CurrentPoint.X, first.Y, 0),
                    new Point3d(e.CurrentPoint.X, e.CurrentPoint.Y, 0),
                    new Point3d(first.X, e.CurrentPoint.Y, 0), first
                }, System.Drawing.Color.DodgerBlue, 2);
            };
            oppositeGetter.Get();
            if (oppositeGetter.CommandResult() != Result.Success)
                return oppositeGetter.CommandResult();
            if (doc.ActiveSpace != ActiveSpace.PageSpace || doc.Views.ActiveView is not RhinoPageView currentPage || currentPage.MainViewport.Id != pageId)
                throw new InvalidOperationException("The active layout changed. Return to the intended sheet and run PlaceBOM again.");

            RhinoApp.WriteLine("Gazelle fitting BOM columns and wrapped text to the selected rectangle...");
            var result = services.PlacedBom().PlaceBom(doc, assemblyName, first, oppositeGetter.Point(), columns.SelectedColumnIds);
            var fitDescription = result.WasReduced
                    ? $"Text was wrapped, then reduced to {result.TextHeight:0.###} {doc.PageUnitSystem} to fit the rectangle."
                    : "Text fits at the standard size with wrapping as needed.";
            RhinoApp.WriteLine($"Placed BOM for '{assemblyName}' with {columns.SelectedColumnIds.Count} columns and {result.ObjectCount} objects. {fitDescription}");
            return Result.Success;
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine("PlaceBOM failed: {0}", ex.Message);
            return Result.Failure;
        }
    }
}
