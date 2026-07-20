using System.Text.Json;
using AssemblyManagerPlugin.Core;
using Rhino;

namespace AssemblyManagerPlugin.Services;

public sealed class PluginSettingsService
{
    private const int CurrentSchemaVersion = 8;
    private const double DefaultLengthTolerance = 0.001;
    private static readonly double[] PreviousDefaultLengthTolerances = { 0.01, 0.005 };
    private const double DefaultAreaTolerance = 0.01;
    private const double DefaultVolumeTolerance = 0.01;
    private const double DefaultArrangementTolerance = 0.01;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };

    public PluginSettingsRecord Load()
    {
        var settings = global::AssemblyManagerPlugin.AssemblyManagerPlugin.Instance.Settings;
        var json = settings.GetString(AssemblyManagerConstants.PluginSettingsEntry, string.Empty);
        if (string.IsNullOrWhiteSpace(json))
            return Normalize(new PluginSettingsRecord());

        try
        {
            return Normalize(JsonSerializer.Deserialize<PluginSettingsRecord>(json, JsonOptions) ?? new PluginSettingsRecord());
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine("Assembly Manager could not read plugin settings: {0}", ex.Message);
            return Normalize(new PluginSettingsRecord());
        }
    }

    public void Save(PluginSettingsRecord record)
    {
        record.UpdatedAt = DateTimeOffset.UtcNow;
        var json = JsonSerializer.Serialize(record, JsonOptions);
        var plugin = global::AssemblyManagerPlugin.AssemblyManagerPlugin.Instance;
        plugin.Settings.SetString(AssemblyManagerConstants.PluginSettingsEntry, json);
        plugin.SaveSettings();
    }

    private static PluginSettingsRecord Normalize(PluginSettingsRecord record)
    {
        var originalSchemaVersion = record.SchemaVersion;
        record.AssemblyManager ??= new AssemblyManagerSettingsRecord();
        record.LayPartsFlat ??= new LayPartsFlatSettingsRecord();
        record.SchemaVersion = Math.Max(record.SchemaVersion, CurrentSchemaVersion);

        if (string.IsNullOrWhiteSpace(record.AssemblyManager.DefaultPartPrefix))
            record.AssemblyManager.DefaultPartPrefix = "P";
        if (string.IsNullOrWhiteSpace(record.AssemblyManager.DefaultComponentPrefix))
            record.AssemblyManager.DefaultComponentPrefix = "C";

        if (originalSchemaVersion < CurrentSchemaVersion
            && PreviousDefaultLengthTolerances.Any(value => Math.Abs(record.AssemblyManager.CategorizationLengthTolerance - value) < 0.0000001))
        {
            record.AssemblyManager.CategorizationLengthTolerance = DefaultLengthTolerance;
        }

        if (record.AssemblyManager.CategorizationLengthTolerance <= 0.0)
            record.AssemblyManager.CategorizationLengthTolerance = DefaultLengthTolerance;
        if (record.AssemblyManager.CategorizationAreaTolerance <= 0.0)
            record.AssemblyManager.CategorizationAreaTolerance = DefaultAreaTolerance;
        if (record.AssemblyManager.CategorizationVolumeTolerance <= 0.0)
            record.AssemblyManager.CategorizationVolumeTolerance = DefaultVolumeTolerance;
        if (record.AssemblyManager.CategorizationArrangementTolerance <= 0.0)
            record.AssemblyManager.CategorizationArrangementTolerance = DefaultArrangementTolerance;
        if (record.LayPartsFlat.PartSpacing <= 0.0)
            record.LayPartsFlat.PartSpacing = 18.0;

        record.AssemblyManager.DefaultPartPrefix = record.AssemblyManager.DefaultPartPrefix.Trim();
        record.AssemblyManager.DefaultComponentPrefix = record.AssemblyManager.DefaultComponentPrefix.Trim();
        return record;
    }
}
