using AssemblyManagerPlugin.Services;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Input.Custom;

namespace AssemblyManagerPlugin;

public sealed class LabelDetailCommand : Command
{
    public override string EnglishName => "LabelDetail";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var getDetail = new GetObject();
        getDetail.SetCommandPrompt("Select detail to label");
        getDetail.GeometryFilter = ObjectType.Detail;
        getDetail.EnablePreSelect(true, true);
        getDetail.Get();
        if (getDetail.CommandResult() != Result.Success)
            return getDetail.CommandResult();

        var options = new GetOption();
        options.SetCommandPrompt("Label level");
        options.AddOption("Assembly");
        options.AddOption("Component");
        options.Get();
        if (options.CommandResult() != Result.Success)
            return options.CommandResult();

        var level = options.Option().Index == 1 ? DetailLabelLevel.Assembly : DetailLabelLevel.Component;
        try
        {
            var count = AssemblyManagerPlugin.Instance.Services.DetailLabel().LabelVisibleObjects(doc, getDetail.Object(0).ObjectId, level);
            RhinoApp.WriteLine("Placed {0} label dot(s).", count);
            return Result.Success;
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine("Label detail failed: {0}", ex.Message);
            return Result.Failure;
        }
    }
}
