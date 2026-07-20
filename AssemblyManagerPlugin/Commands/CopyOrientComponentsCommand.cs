using Rhino;
using Rhino.Commands;

namespace AssemblyManagerPlugin;

public sealed class CopyOrientComponentsCommand : Command
{
    public override string EnglishName => "CopyOrientComponents";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var assemblyName = LayPartsFlatCommand.GetAssemblyName(doc, "Assembly to copy/orient for drawings");
        if (string.IsNullOrWhiteSpace(assemblyName))
            return Result.Cancel;

        try
        {
            var count = AssemblyManagerPlugin.Instance.Services.ComponentDrawing().CopyAndOrientComponents(doc, assemblyName);
            RhinoApp.WriteLine("Copied and oriented {0} component type(s) for {1}.", count, assemblyName);
            return Result.Success;
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine("Copy / Orient Components failed: {0}", ex.Message);
            return Result.Failure;
        }
    }
}
