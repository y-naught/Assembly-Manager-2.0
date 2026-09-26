using System.Text.Json;
using AssemblyManagerPlugin.Core;
using Rhino;

namespace AssemblyManagerPlugin.Services;

public sealed class PluginSettingsService
{
    private const int CurrentSchemaVersion = 10;
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
    private bool? _automaticPropagationEnabled;
    private bool? _linkedAssembliesEnabled;

    public event EventHandler? LinkingPreferenceChanged;

    public bool EnableLinkedAssemblies =>
        _linkedAssembliesEnabled ??= Load().AssemblyManager.EnableLinkedAssemblies;

    // Rhino can raise Idle frequently. Do not deserialize the complete material library and
    // settings record on every idle tick merely to read this one preference.
    public bool AutomaticallyPropagateChangesInAssembly =>
        _automaticPropagationEnabled ??= Load().AssemblyManager.AutomaticallyPropagateChangesInAssembly;

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
        var linkingWasEnabled = EnableLinkedAssemblies;
        var automaticWasEnabled = AutomaticallyPropagateChangesInAssembly;
        var plugin = global::AssemblyManagerPlugin.AssemblyManagerPlugin.Instance;
        var previousJson = plugin.Settings.GetString(AssemblyManagerConstants.PluginSettingsEntry, string.Empty);
        _linkedAssembliesEnabled = record.AssemblyManager.EnableLinkedAssemblies;
        _automaticPropagationEnabled = record.AssemblyManager.AutomaticallyPropagateChangesInAssembly;
        record.UpdatedAt = DateTimeOffset.UtcNow;
        var json = JsonSerializer.Serialize(record, JsonOptions);
        var linkingChanged = linkingWasEnabled != _linkedAssembliesEnabled;
        try
        {
            // Establish the durable safety baseline before committing an emergency-off
            // preference. A failed flush/snapshot must not leave tracking off without it.
            if (linkingChanged)
                LinkingPreferenceChanged?.Invoke(this, EventArgs.Empty);
            plugin.Settings.SetString(AssemblyManagerConstants.PluginSettingsEntry, json);
            plugin.SaveSettings();
        }
        catch
        {
            _linkedAssembliesEnabled = linkingWasEnabled;
            _automaticPropagationEnabled = automaticWasEnabled;
            plugin.Settings.SetString(AssemblyManagerConstants.PluginSettingsEntry, previousJson);
            if (linkingChanged)
            {
                try { LinkingPreferenceChanged?.Invoke(this, EventArgs.Empty); }
                catch { /* Preserve the original failure; persisted safety records stay fail-closed. */ }
            }
            throw;
        }
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

        if (originalSchemaVersion < 8
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
