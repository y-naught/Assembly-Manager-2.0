using Rhino;
using Rhino.Commands;

namespace AssemblyManagerPlugin;

public sealed class UpdateComponentCommand : Command
{
    public override string EnglishName => "UpdateComponent";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var assemblyName = CommandPickers.PickAssembly(doc, "Assembly containing the component to update");
        if (assemblyName is null)
            return Result.Cancel;
        var services = AssemblyManagerPlugin.Instance.Services;
        try
        {
            var assembly = services.Repository.Load(doc).FindAssembly(assemblyName)!;
            var componentName = CommandPickers.PickFromList(
                assembly.Components.Select(component => component.Name).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList(),
                "Current component type (only the regrouped occurrence will be updated)");
            if (componentName is null)
                return Result.Cancel;
            var component = assembly.Components.Single(item => item.Name == componentName);
            var groupId = CommandPickers.PickRegroupedComponent(doc);
            if (!groupId.HasValue)
                return Result.Cancel;
            services.LinkEvents.StageComponentUpdate(doc, assemblyName, component.Id, groupId.Value);
            return Result.Success;
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine("Update component failed: {0}", ex.Message);
            return Result.Failure;
        }
    }
}
