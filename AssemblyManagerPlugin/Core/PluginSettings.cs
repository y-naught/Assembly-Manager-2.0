using System.Text.Json;
using System.Text.Json.Serialization;

namespace AssemblyManagerPlugin.Core;

public sealed class PluginSettingsRecord
{
    public int SchemaVersion { get; set; } = 8;
    public string LayoutTemplatePath { get; set; } = string.Empty;
    public AssemblyManagerSettingsRecord AssemblyManager { get; set; } = new();
    public LayPartsFlatSettingsRecord LayPartsFlat { get; set; } = new();
    public List<MaterialDefinitionRecord> Materials { get; set; } = new();
    public List<MaterialRecord> MaterialLibrary { get; set; } = new();
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtraData { get; set; }
}

public sealed class AssemblyManagerSettingsRecord
{
    public string DefaultPartPrefix { get; set; } = "P";
    public string DefaultComponentPrefix { get; set; } = "C";
    public bool ColorizeParts { get; set; } = true;
    public double CategorizationLengthTolerance { get; set; } = 0.001;
    public double CategorizationAreaTolerance { get; set; } = 0.01;
    public double CategorizationVolumeTolerance { get; set; } = 0.01;
    public double CategorizationArrangementTolerance { get; set; } = 0.01;
    public bool DebugCategorization { get; set; }
}

public sealed class LayPartsFlatSettingsRecord
{
    public double PartSpacing { get; set; } = 18.0;
}
