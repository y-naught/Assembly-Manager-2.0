using AssemblyManagerPlugin.Core;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Input.Custom;

namespace AssemblyManagerPlugin;

public sealed class AddPartToComponentCommand : Command
{
    public override string EnglishName => "AddPartToComponent";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        if (doc.ActiveSpace != ActiveSpace.ModelSpace)
        {
            RhinoApp.WriteLine("AddPartToComponent must be run in model space. Activate a model view or a layout detail and run the command again.");
            return Result.Failure;
        }

        try
        {
            var services = AssemblyManagerPlugin.Instance.Services;
            using var componentPicker = new GetObject();
            componentPicker.SetCommandPrompt("Select a member of an existing assembly INPUT component group");
            componentPicker.GroupSelect = false;
            componentPicker.SubObjectSelect = false;
            componentPicker.EnablePreSelect(false, true);
            componentPicker.Get();
            if (componentPicker.CommandResult() != Result.Success)
                return componentPicker.CommandResult();

            var member = componentPicker.Object(0).Object();
            var memberGroups = (member?.GetGroupList() ?? Array.Empty<int>())
                .Select(index => doc.Groups.FindIndex(index))
                .Where(group => group is not null && !group.IsDeleted)
                .Select(group => group!.Id)
                .ToHashSet();
            var store = services.Repository.Load(doc);
            var candidates = store.Assemblies
                .SelectMany(assembly => assembly.LinkGraph.SourceComponentInstances
                    .Where(instance => memberGroups.Contains(instance.SourceGroupId))
                    .Select(instance => new InputComponent(assembly, instance)))
                .ToList();
            if (candidates.Count == 0)
            {
                RhinoApp.WriteLine("Select a recognized INPUT component group, not a group in ORIGINAL ASSEMBLIES or COPIED COMPONENTS. Finish regrouping and resolve any input-group Link Issues before adding a part.");
                return Result.Failure;
            }

            // A group shared by multiple owners is unsafe, not a choice between equivalent targets.
            if (candidates.GroupBy(candidate => candidate.Instance.SourceGroupId).Any(group => group.Count() > 1))
            {
                RhinoApp.WriteLine("The selected input group belongs to more than one assembly or component occurrence. AddPartToComponent requires uniquely owned input geometry.");
                return Result.Failure;
            }

            var selected = candidates[0];
            if (candidates.Count > 1)
            {
                var labels = candidates.Select(candidate =>
                {
                    var component = candidate.Assembly.Components.Single(item => item.Id == candidate.Instance.ComponentId);
                    return $"{candidate.Assembly.Name} / {component.Name} / {candidate.Instance.SourceGroupName} ({candidate.Instance.SourceGroupId})";
                }).ToList();
                var choice = CommandPickers.PickFromList(labels, "Input component group to receive the new part");
                if (choice is null)
                    return Result.Cancel;
                selected = candidates[labels.IndexOf(choice)];
            }

            using var partPicker = new GetObject();
            partPicker.SetCommandPrompt("Select one new closed solid, extrusion, or marked hardware object to add to the input component");
            partPicker.GroupSelect = false;
            partPicker.SubObjectSelect = false;
            partPicker.EnablePreSelect(false, true);
            partPicker.Get();
            if (partPicker.CommandResult() != Result.Success)
                return partPicker.CommandResult();

            services.LinkEvents.StageInputComponentAddition(doc, selected.Assembly.Name,
                selected.Instance.Id, partPicker.Object(0).ObjectId);
            doc.Views.Redraw();
            return Result.Success;
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine("Add part to component failed: {0}", ex.Message);
            return Result.Failure;
        }
    }

    private sealed record InputComponent(AssemblyRecord Assembly, SourceComponentInstanceRecord Instance);
}
