using AssemblyManagerPlugin.Core;
using Rhino;
using Rhino.DocObjects;

namespace AssemblyManagerPlugin.Services;

public sealed class AssemblyRemovalService
{
    private readonly AssemblyRepository _repository;
    private readonly LayerService _layers;
    private readonly IActionHistorySink _history;

    public AssemblyRemovalService(AssemblyRepository repository, LayerService layers, IActionHistorySink history)
    {
        _repository = repository;
        _layers = layers;
        _history = history;
    }

    public AssemblyRemovalResult RemoveAssembly(RhinoDoc doc, string assemblyName)
    {
        var store = _repository.Load(doc);
        var assembly = store.FindAssembly(assemblyName);
        if (assembly is null)
            throw new InvalidOperationException($"Assembly '{assemblyName}' was not found.");

        var result = new AssemblyRemovalResult { AssemblyName = assembly.Name };
        var layerRoots = GetAssemblyLayerRoots(assembly.Name);
        var objectIds = CollectManagedObjectIds(assembly);
        foreach (var root in layerRoots)
        {
            foreach (var objectId in _layers.GetObjectIdsInLayerTree(doc, root))
                objectIds.Add(objectId);
        }

        foreach (var objectId in objectIds)
        {
            if (TryDeleteObject(doc, objectId))
                result.DeletedObjectCount++;
        }

        foreach (var groupName in assembly.Components.SelectMany(component => component.InstanceGroupNames).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var group = doc.Groups.FindName(groupName);
            if (group is not null && doc.Groups.Delete(group))
                result.DeletedGroupCount++;
        }

        foreach (var root in layerRoots)
            result.DeletedLayerCount += _layers.DeleteLayerTree(doc, root);

        result.MetadataRemoved = store.Assemblies.RemoveAll(a => string.Equals(a.Name, assembly.Name, StringComparison.OrdinalIgnoreCase)) > 0;
        _repository.Save(doc, store);
        _history.Record(doc, new ActionHistoryEntry
        {
            CommandName = "RemoveAssembly",
            AssemblyName = assembly.Name,
            Summary = $"Removed assembly '{assembly.Name}'.",
            Data =
            {
                ["deletedObjects"] = result.DeletedObjectCount.ToString(),
                ["deletedLayers"] = result.DeletedLayerCount.ToString(),
                ["deletedGroups"] = result.DeletedGroupCount.ToString()
            }
        });

        doc.Views.Redraw();
        return result;
    }

    private static HashSet<Guid> CollectManagedObjectIds(AssemblyRecord assembly)
    {
        var objectIds = new HashSet<Guid>();

        foreach (var part in assembly.Parts)
        {
            foreach (var objectId in part.GeneratedObjectIds)
                objectIds.Add(objectId);
            foreach (var objectId in part.CamObjectIds)
                objectIds.Add(objectId);
        }

        foreach (var component in assembly.Components)
        {
            foreach (var objectId in component.ObjectIds)
                objectIds.Add(objectId);

            foreach (var objectId in component.RepresentativeObjectIdsByPartName.Values.SelectMany(ids => ids))
                objectIds.Add(objectId);
        }

        foreach (var reference in assembly.GeometryReferences)
        {
            if (reference.TargetObjectId != Guid.Empty)
                objectIds.Add(reference.TargetObjectId);
        }

        return objectIds;
    }

    private static string[] GetAssemblyLayerRoots(string assemblyName)
    {
        return new[]
        {
            LayerService.ShopAssembly(assemblyName),
            LayerService.CamAssembly(assemblyName),
            LayerService.DrawingsAssembly(assemblyName)
        };
    }

    private static bool TryDeleteObject(RhinoDoc doc, Guid objectId)
    {
        var rhinoObject = doc.Objects.FindId(objectId);
        return rhinoObject is not null && doc.Objects.Delete(rhinoObject, true, true);
    }
}
