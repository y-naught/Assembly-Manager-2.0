using AssemblyManagerPlugin.UI;
using Rhino;
using Rhino.Commands;
using Rhino.UI;

namespace AssemblyManagerPlugin;

public sealed class MaterialLibraryCommand : Command
{
    public override string EnglishName => "MaterialLibrary";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var dialog = new MaterialLibraryDialog(doc, AssemblyManagerPlugin.Instance.Services.MaterialLibrary());
        dialog.ShowModal(RhinoEtoApp.MainWindow);
        return Result.Success;
    }
}
