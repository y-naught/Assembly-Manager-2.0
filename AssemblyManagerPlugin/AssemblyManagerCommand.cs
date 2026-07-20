using AssemblyManagerPlugin.UI;
using Rhino;
using Rhino.Commands;
using Rhino.UI;

namespace AssemblyManagerPlugin;

public sealed class AssemblyManagerCommand : Command
{
    public override string EnglishName => "AssemblyManager";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var dialog = new AssemblyManagerDialog(doc, AssemblyManagerPlugin.Instance.Services);
        dialog.ShowModal(RhinoEtoApp.MainWindow);
        return Result.Success;
    }
}
