using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Input.Custom;

namespace AssemblyManagerPlugin;

public sealed class PlaceComponentCommand : Command
{
    public override string EnglishName => "PlaceComponent";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        if (doc.ActiveSpace != ActiveSpace.ModelSpace)
        {
            RhinoApp.WriteLine("PlaceComponent must be run in model space. Activate a model view or a layout detail and run the command again.");
            return Result.Failure;
        }
        try
        {
            var services = AssemblyManagerPlugin.Instance.Services;
            var assemblies = services.Repository.GetAssemblyNames(doc);
            var assemblyName = assemblies.Count == 1 ? assemblies[0] :
                CommandPickers.PickFromList(assemblies, "Assembly containing the component to place");
            if (assemblyName is null)
                return Result.Cancel;
            var assembly = services.Repository.Load(doc).FindAssembly(assemblyName)!;
            var componentName = CommandPickers.PickFromList(assembly.Components.Select(component => component.Name)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList(), "Component to place as an additional linked drawing view");
            if (componentName is null)
                return Result.Cancel;
            using var point = new GetPoint();
            point.SetCommandPrompt("Component placement point (center of its plan-oriented bounds)");
            point.Get();
            if (point.CommandResult() != Result.Success)
                return point.CommandResult();
            var count = services.ComponentDrawing().PlaceComponent(doc, assemblyName,
                assembly.Components.Single(component => component.Name == componentName).Id, point.Point());
            RhinoApp.WriteLine("Placed a linked {0} drawing view with {1} member(s). Assembly quantities are unchanged.", componentName, count);
            return Result.Success;
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine("Place component failed: {0}", ex.Message);
            return Result.Failure;
        }
    }
}
