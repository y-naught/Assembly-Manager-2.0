using System.IO;
using AssemblyManagerPlugin.Core;
using Rhino;
using Rhino.UI;

namespace AssemblyManagerPlugin.Services;

public sealed class LayoutTemplateImportService
{
    private readonly PluginSettingsService _settings;
    private readonly IActionHistorySink _history;

    public LayoutTemplateImportService(PluginSettingsService settings, IActionHistorySink history)
    {
        _settings = settings;
        _history = history;
    }

    public LayoutTemplateImportResult? ImportSavedOrPromptedLayout(RhinoDoc doc)
    {
        var settings = _settings.Load();
        var templatePath = settings.LayoutTemplatePath;
        var prompted = false;

        if (!IsUsablePath(templatePath))
        {
            templatePath = PromptForTemplatePath();
            if (string.IsNullOrWhiteSpace(templatePath))
                return null;

            settings.LayoutTemplatePath = templatePath;
            _settings.Save(settings);
            prompted = true;
        }

        return ImportLayout(doc, templatePath, prompted);
    }

    public LayoutTemplateImportResult ImportLayout(RhinoDoc doc, string templatePath, bool promptedForPath = false)
    {
        if (!IsUsablePath(templatePath))
            throw new FileNotFoundException("Layout template file was not found.", templatePath);

        var pageCountBefore = doc.Views.GetPageViews().Length;
        var escapedPath = templatePath.Replace("\\", "/").Replace("\"", "\\\"");
        if (!RhinoApp.RunScript($"_-ImportLayout \"{escapedPath}\"", false))
            throw new InvalidOperationException($"Rhino ImportLayout command failed.\n{RhinoApp.CommandHistoryWindowText}");

        var pageCountAfter = doc.Views.GetPageViews().Length;
        var result = new LayoutTemplateImportResult
        {
            TemplatePath = templatePath,
            PromptedForPath = promptedForPath,
            LayoutCountBefore = pageCountBefore,
            LayoutCountAfter = pageCountAfter
        };

        _history.Record(doc, new ActionHistoryEntry
        {
            CommandName = "NewLayout",
            Summary = $"Imported layout template '{Path.GetFileName(templatePath)}'.",
            Data =
            {
                ["path"] = templatePath,
                ["importedLayouts"] = result.ImportedLayoutCount.ToString()
            }
        });

        doc.Views.Redraw();
        return result;
    }

    private static bool IsUsablePath(string? path)
    {
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
    }

    private static string? PromptForTemplatePath()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select layout template",
            Filter = "Rhino Models (*.3dm)|*.3dm|All Files (*.*)|*.*||"
        };

        return dialog.ShowOpenDialog() ? dialog.FileName : null;
    }
}

public sealed class LayoutTemplateImportResult
{
    public string TemplatePath { get; set; } = string.Empty;
    public bool PromptedForPath { get; set; }
    public int LayoutCountBefore { get; set; }
    public int LayoutCountAfter { get; set; }
    public int ImportedLayoutCount => Math.Max(0, LayoutCountAfter - LayoutCountBefore);
}
