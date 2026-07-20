using AssemblyManagerPlugin.UI;
using Rhino;
using Rhino.Commands;
using Rhino.UI;

namespace AssemblyManagerPlugin;

public sealed class SetProjectInfoCommand : Command
{
    public override string EnglishName => "SetProjectInfo";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var dialog = new ProjectInfoDialog(doc, AssemblyManagerPlugin.Instance.Services.ProjectInfo());
        dialog.ShowModal(RhinoEtoApp.MainWindow);
        return Result.Success;
    }
}
