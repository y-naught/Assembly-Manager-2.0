using AssemblyManagerPlugin.Core;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Input.Custom;

namespace AssemblyManagerPlugin;

public sealed class CreateAssemblyCommand : Command
{
    public override string EnglishName => "CreateAssembly";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var settings = AssemblyManagerPlugin.Instance.Services.PluginSettings.Load().AssemblyManager;

        var nameGetter = new GetString();
        nameGetter.SetCommandPrompt("Assembly name");
        nameGetter.SetDefaultString("Assembly01");
        nameGetter.Get();
        if (nameGetter.CommandResult() != Result.Success)
            return nameGetter.CommandResult();

        var partPrefixGetter = new GetString();
        partPrefixGetter.SetCommandPrompt("Part prefix");
        partPrefixGetter.SetDefaultString(settings.DefaultPartPrefix);
        partPrefixGetter.Get();
        if (partPrefixGetter.CommandResult() != Result.Success)
            return partPrefixGetter.CommandResult();

        var componentPrefixGetter = new GetString();
        componentPrefixGetter.SetCommandPrompt("Component prefix");
        componentPrefixGetter.SetDefaultString(settings.DefaultComponentPrefix);
        componentPrefixGetter.Get();
        if (componentPrefixGetter.CommandResult() != Result.Success)
            return componentPrefixGetter.CommandResult();

        var objectGetter = new GetObject();
        objectGetter.SetCommandPrompt("Select component groups in assembly");
        objectGetter.GeometryFilter = ObjectType.Brep | ObjectType.Extrusion | ObjectType.Surface | ObjectType.InstanceReference;
        objectGetter.GroupSelect = true;
        objectGetter.EnablePreSelect(false, true);
        objectGetter.GetMultiple(1, 0);
        if (objectGetter.CommandResult() != Result.Success)
            return objectGetter.CommandResult();

        try
        {
            var ids = objectGetter.Objects().Select(reference => reference.ObjectId).ToList();
            var result = AssemblyManagerPlugin.Instance.Services.AssemblyGeneration().CreateAssembly(doc, ids, new CreateAssemblyOptions
            {
                AssemblyName = nameGetter.StringResult(),
                PartPrefix = partPrefixGetter.StringResult(),
                ComponentPrefix = componentPrefixGetter.StringResult()
            });

            RhinoApp.WriteLine(
                $"Created assembly {result.Assembly.Name}: {result.SourcePartCount} selected parts, {result.UniquePartCount} unique parts, {result.UniqueComponentCount} component types.");
            foreach (var warning in result.Warnings)
                RhinoApp.WriteLine("Assembly Manager warning: {0}", warning);
            return Result.Success;
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine("Assembly Manager failed: {0}", ex.Message);
            return Result.Failure;
        }
    }
}
