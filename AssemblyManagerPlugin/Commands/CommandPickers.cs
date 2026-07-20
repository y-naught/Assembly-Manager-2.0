using AssemblyManagerPlugin.Core;
using Rhino;
using Rhino.Commands;
using Rhino.Input.Custom;

namespace AssemblyManagerPlugin;

internal static class CommandPickers
{
    public static string? PickAssembly(RhinoDoc doc, string prompt)
    {
        var names = AssemblyManagerPlugin.Instance.Services.Repository.GetAssemblyNames(doc);
        return PickFromList(names, prompt);
    }

    public static string? PickPart(RhinoDoc doc, string assemblyName, string prompt)
    {
        var assembly = AssemblyManagerPlugin.Instance.Services.Repository.Load(doc).FindAssembly(assemblyName);
        return assembly is null ? null : PickFromList(assembly.Parts.Select(p => p.Name).ToList(), prompt);
    }

    public static MaterialRecord? PickMaterial(RhinoDoc doc, string prompt)
    {
        var materials = AssemblyManagerPlugin.Instance.Services.MaterialLibrary()
            .GetMaterials(doc)
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var labels = materials.Select(m => $"{m.Name} ({m.Category})").ToList();
        var selected = PickFromList(labels, prompt);
        if (selected is null)
            return null;

        var index = labels.IndexOf(selected);
        return index >= 0 && index < materials.Count ? materials[index] : null;
    }

    public static string? PickFromList(IReadOnlyList<string> values, string prompt)
    {
        if (values.Count == 0)
        {
            RhinoApp.WriteLine("No options are available for: {0}", prompt);
            return null;
        }

        var getter = new GetOption();
        getter.SetCommandPrompt(prompt);
        var keys = new List<string>();
        foreach (var value in values)
        {
            var key = MakeOptionKey(value, keys);
            keys.Add(key);
            getter.AddOption(key);
        }

        getter.Get();
        if (getter.CommandResult() != Result.Success)
            return null;

        var index = getter.Option().Index - 1;
        return index >= 0 && index < values.Count ? values[index] : null;
    }

    private static string MakeOptionKey(string value, IReadOnlyCollection<string> existing)
    {
        var clean = new string(value.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        if (string.IsNullOrWhiteSpace(clean) || !char.IsLetter(clean[0]))
            clean = "Option_" + clean;

        var result = clean;
        var index = 1;
        while (existing.Contains(result, StringComparer.OrdinalIgnoreCase))
        {
            result = $"{clean}_{index:00}";
            index++;
        }

        return result;
    }
}
