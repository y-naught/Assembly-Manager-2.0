using AssemblyManagerPlugin.UI;
using Rhino;
using Rhino.Commands;
using Rhino.UI;

namespace AssemblyManagerPlugin;

public sealed class AssemblyManagerSettingsCommand : Command
{
    public override string EnglishName => "AssemblyManagerSettings";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var dialog = new SettingsDialog(AssemblyManagerPlugin.Instance.Services.PluginSettings);
        dialog.ShowModal(RhinoEtoApp.MainWindow);
        return Result.Success;
    }
}

[CommandStyle(Style.ScriptRunner)]
public sealed class NewLayoutCommand : Command
{
    public override string EnglishName => "NewLayout";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        try
        {
            var result = AssemblyManagerPlugin.Instance.Services.LayoutTemplateImport().ImportSavedOrPromptedLayout(doc);
            if (result is null)
                return Result.Cancel;

            RhinoApp.WriteLine("Imported {0} layout(s) from {1}.", result.ImportedLayoutCount, result.TemplatePath);
            return Result.Success;
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine("Import layout template failed: {0}", ex.Message);
            return Result.Failure;
        }
    }
}
