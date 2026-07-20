using AssemblyManagerPlugin.Core;
using Rhino;

namespace AssemblyManagerPlugin.Services;

public sealed class ProjectInfoService
{
    public Dictionary<string, string> Load(RhinoDoc doc)
    {
        return ProjectInfo.Keys.ToDictionary(
            key => key,
            key => doc.Strings.GetValue(key)
                ?? doc.Strings.GetValue(AssemblyManagerConstants.ProjectInfoSection, key)
                ?? string.Empty);
    }

    public void Save(RhinoDoc doc, IDictionary<string, string> values)
    {
        foreach (var key in ProjectInfo.Keys)
        {
            values.TryGetValue(key, out var value);
            doc.Strings.SetString(key, value ?? string.Empty);
            doc.Strings.Delete(AssemblyManagerConstants.ProjectInfoSection, key);
        }
    }
}
