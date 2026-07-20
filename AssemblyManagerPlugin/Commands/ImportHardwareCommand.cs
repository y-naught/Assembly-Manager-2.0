using Rhino;
using Rhino.Commands;
using Rhino.UI;

namespace AssemblyManagerPlugin;

public sealed class ImportHardwareCommand : Command
{
    public override string EnglishName => "ImportHardware";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select STEP file to import",
            Filter = "STEP Files (*.stp;*.step)|*.stp;*.step|All Files (*.*)|*.*||"
        };

        if (!dialog.ShowOpenDialog())
            return Result.Cancel;

        try
        {
            var record = AssemblyManagerPlugin.Instance.Services.HardwareImport().ImportStepAsHardwareBlock(doc, dialog.FileName);
            RhinoApp.WriteLine("Imported hardware block {0}.", record.BlockDefinitionName);
            return Result.Success;
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine("Import hardware failed: {0}", ex.Message);
            return Result.Failure;
        }
    }
}
