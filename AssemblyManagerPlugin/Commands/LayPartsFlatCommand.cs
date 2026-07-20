using Rhino;
using Rhino.Commands;
using Rhino.Input.Custom;

namespace AssemblyManagerPlugin;

public sealed class LayPartsFlatCommand : Command
{
    public override string EnglishName => "LayPartsFlat";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var assemblyName = GetAssemblyName(doc, "Assembly to lay flat");
        if (string.IsNullOrWhiteSpace(assemblyName))
            return Result.Cancel;

        try
        {
            var count = AssemblyManagerPlugin.Instance.Services.LayPartsFlat().LayPartsFlat(doc, assemblyName);
            RhinoApp.WriteLine("Laid flat {0} unique part(s) for {1}.", count, assemblyName);
            return Result.Success;
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine("Lay Parts Flat failed: {0}", ex.Message);
            return Result.Failure;
        }
    }

    internal static string? GetAssemblyName(RhinoDoc doc, string prompt)
    {
        var names = AssemblyManagerPlugin.Instance.Services.Repository.GetAssemblyNames(doc);
        if (names.Count == 0)
        {
            RhinoApp.WriteLine("No Assembly Manager assemblies are stored in this document.");
            return null;
        }

        var getter = new GetOption();
        getter.SetCommandPrompt(prompt);
        foreach (var name in names)
            getter.AddOption(name);

        getter.Get();
        if (getter.CommandResult() != Result.Success)
            return null;

        var index = getter.Option().Index - 1;
        return index >= 0 && index < names.Count ? names[index] : null;
    }
}
