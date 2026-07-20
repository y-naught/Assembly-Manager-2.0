using Rhino;
using Rhino.Commands;
using Rhino.Input.Custom;

namespace AssemblyManagerPlugin;

public sealed class RemoveAssemblyCommand : Command
{
    public override string EnglishName => "RemoveAssembly";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var assemblyName = CommandPickers.PickAssembly(doc, "Assembly to remove");
        if (assemblyName is null)
            return Result.Cancel;

        if (!ConfirmRemove(assemblyName))
            return Result.Cancel;

        try
        {
            var result = AssemblyManagerPlugin.Instance.Services.AssemblyRemoval().RemoveAssembly(doc, assemblyName);
            RhinoApp.WriteLine(
                $"Removed {result.AssemblyName}: deleted {result.DeletedObjectCount} object(s), {result.DeletedLayerCount} layer(s), and {result.DeletedGroupCount} group(s).");
            return Result.Success;
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine("Remove assembly failed: {0}", ex.Message);
            return Result.Failure;
        }
    }

    private static bool ConfirmRemove(string assemblyName)
    {
        var getter = new GetOption();
        getter.SetCommandPrompt($"Remove '{assemblyName}' and delete its Assembly Manager geometry and layers?");
        var yesIndex = getter.AddOption("Yes");
        getter.AddOption("No");
        getter.Get();

        return getter.CommandResult() == Result.Success && getter.Option().Index == yesIndex;
    }
}
