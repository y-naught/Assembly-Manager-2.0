using System.Text.Json;
using AssemblyManagerPlugin.Core;
using Rhino;

namespace AssemblyManagerPlugin.Services;

public sealed class AssemblyRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };

    public AssemblyStore Load(RhinoDoc doc)
    {
        var json = doc.Strings.GetValue(AssemblyManagerConstants.StoreSection, AssemblyManagerConstants.StoreEntry);
        if (string.IsNullOrWhiteSpace(json))
            return new AssemblyStore();

        try
        {
            return JsonSerializer.Deserialize<AssemblyStore>(json, JsonOptions) ?? new AssemblyStore();
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine("Assembly Manager could not read stored data: {0}", ex.Message);
            return new AssemblyStore();
        }
    }

    public void Save(RhinoDoc doc, AssemblyStore store)
    {
        var json = JsonSerializer.Serialize(store, JsonOptions);
        doc.Strings.SetString(AssemblyManagerConstants.StoreSection, AssemblyManagerConstants.StoreEntry, json);
    }

    public IReadOnlyList<string> GetAssemblyNames(RhinoDoc doc)
    {
        return Load(doc).Assemblies
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .Select(a => a.Name)
            .ToList();
    }

    public void UpsertAssembly(RhinoDoc doc, AssemblyRecord assembly)
    {
        var store = Load(doc);
        var existing = store.Assemblies.FindIndex(a => string.Equals(a.Name, assembly.Name, StringComparison.OrdinalIgnoreCase));
        assembly.UpdatedAt = DateTimeOffset.UtcNow;
        if (existing >= 0)
            store.Assemblies[existing] = assembly;
        else
            store.Assemblies.Add(assembly);

        Save(doc, store);
    }

    public bool RemoveAssembly(RhinoDoc doc, string assemblyName)
    {
        var store = Load(doc);
        var removed = store.Assemblies.RemoveAll(a => string.Equals(a.Name, assemblyName, StringComparison.OrdinalIgnoreCase)) > 0;
        if (removed)
            Save(doc, store);

        return removed;
    }
}
