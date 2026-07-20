using System.IO;
using System.Globalization;
using AssemblyManagerPlugin.Core;
using AssemblyManagerPlugin.Services;
using Eto.Drawing;
using Eto.Forms;

namespace AssemblyManagerPlugin.UI;

public sealed class SettingsDialog : Dialog<bool>
{
    private readonly PluginSettingsService _settings;
    private readonly TextBox _layoutTemplatePath = new() { Width = 460 };
    private readonly TextBox _defaultPartPrefix = new() { Width = 120 };
    private readonly TextBox _defaultComponentPrefix = new() { Width = 120 };
    private readonly CheckBox _colorizeParts = new() { Text = "Colorize generated part layers" };
    private readonly TextBox _lengthTolerance = new() { Width = 120 };
    private readonly TextBox _areaTolerance = new() { Width = 120 };
    private readonly TextBox _volumeTolerance = new() { Width = 120 };
    private readonly TextBox _arrangementTolerance = new() { Width = 120 };
    private readonly CheckBox _debugCategorization = new() { Text = "Print part categorization debug output" };
    private readonly TextBox _layFlatSpacing = new() { Width = 120 };

    public SettingsDialog(PluginSettingsService settings)
    {
        _settings = settings;
        Title = "Assembly Manager Settings";
        Padding = new Padding(15);
        Resizable = true;
        MinimumSize = new Size(720, 520);

        var saved = _settings.Load();
        _layoutTemplatePath.Text = saved.LayoutTemplatePath;
        _defaultPartPrefix.Text = saved.AssemblyManager.DefaultPartPrefix;
        _defaultComponentPrefix.Text = saved.AssemblyManager.DefaultComponentPrefix;
        _colorizeParts.Checked = saved.AssemblyManager.ColorizeParts;
        _lengthTolerance.Text = FormatDouble(saved.AssemblyManager.CategorizationLengthTolerance);
        _areaTolerance.Text = FormatDouble(saved.AssemblyManager.CategorizationAreaTolerance);
        _volumeTolerance.Text = FormatDouble(saved.AssemblyManager.CategorizationVolumeTolerance);
        _arrangementTolerance.Text = FormatDouble(saved.AssemblyManager.CategorizationArrangementTolerance);
        _debugCategorization.Checked = saved.AssemblyManager.DebugCategorization;
        _layFlatSpacing.Text = FormatDouble(saved.LayPartsFlat.PartSpacing);
        Content = BuildLayout();
    }

    private Control BuildLayout()
    {
        var browseButton = new Button { Text = "Browse" };
        browseButton.Click += (_, _) => BrowseForLayoutTemplate();

        var saveButton = new Button { Text = "Save" };
        saveButton.Click += (_, _) => SaveAndClose();

        var cancelButton = new Button { Text = "Cancel" };
        cancelButton.Click += (_, _) => Close(false);

        var layout = new DynamicLayout
        {
            Spacing = new Size(8, 8),
            Padding = new Padding(10)
        };

        AddSectionHeader(layout, "Assembly Manager");
        layout.AddRow(new Label { Text = "Default Part Prefix" }, _defaultPartPrefix);
        layout.AddRow(new Label { Text = "Default Component Prefix" }, _defaultComponentPrefix);
        layout.AddRow(new Label { Text = string.Empty }, _colorizeParts);
        layout.AddRow(new Label { Text = "Length / Edge Tolerance" }, _lengthTolerance);
        layout.AddRow(new Label { Text = "Area Tolerance" }, _areaTolerance);
        layout.AddRow(new Label { Text = "Volume Tolerance" }, _volumeTolerance);
        layout.AddRow(new Label { Text = "Arrangement Tolerance" }, _arrangementTolerance);
        layout.AddRow(new Label { Text = string.Empty }, _debugCategorization);

        AddSectionHeader(layout, "Lay Parts Flat");
        layout.AddRow(new Label { Text = "Part Spacing" }, _layFlatSpacing);

        AddSectionHeader(layout, "Layout Template");
        layout.AddRow(_layoutTemplatePath, browseButton);
        layout.AddRow(null);
        layout.AddRow(saveButton, cancelButton);

        return new Scrollable
        {
            Content = layout,
            ExpandContentWidth = true,
            ExpandContentHeight = false
        };
    }

    private void BrowseForLayoutTemplate()
    {
        var dialog = new Rhino.UI.OpenFileDialog
        {
            Title = "Select layout template",
            Filter = "Rhino Models (*.3dm)|*.3dm|All Files (*.*)|*.*||"
        };

        if (dialog.ShowOpenDialog())
            _layoutTemplatePath.Text = dialog.FileName;
    }

    private void SaveAndClose()
    {
        var path = _layoutTemplatePath.Text?.Trim() ?? string.Empty;
        if (!TryReadPositiveDouble(_lengthTolerance.Text, "Length / edge tolerance", out var lengthTolerance)
            || !TryReadPositiveDouble(_areaTolerance.Text, "Area tolerance", out var areaTolerance)
            || !TryReadPositiveDouble(_volumeTolerance.Text, "Volume tolerance", out var volumeTolerance)
            || !TryReadPositiveDouble(_arrangementTolerance.Text, "Arrangement tolerance", out var arrangementTolerance)
            || !TryReadPositiveDouble(_layFlatSpacing.Text, "Lay parts flat spacing", out var layFlatSpacing))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(path) && !File.Exists(path))
        {
            var confirm = MessageBox.Show(
                this,
                "The layout template path does not currently point to an existing file. Save it anyway?",
                MessageBoxButtons.YesNo,
                MessageBoxType.Question);

            if (confirm != DialogResult.Yes)
                return;
        }

        var saved = _settings.Load();
        saved.LayoutTemplatePath = path;
        saved.AssemblyManager.DefaultPartPrefix = string.IsNullOrWhiteSpace(_defaultPartPrefix.Text) ? "P" : _defaultPartPrefix.Text.Trim();
        saved.AssemblyManager.DefaultComponentPrefix = string.IsNullOrWhiteSpace(_defaultComponentPrefix.Text) ? "C" : _defaultComponentPrefix.Text.Trim();
        saved.AssemblyManager.ColorizeParts = _colorizeParts.Checked == true;
        saved.AssemblyManager.CategorizationLengthTolerance = lengthTolerance;
        saved.AssemblyManager.CategorizationAreaTolerance = areaTolerance;
        saved.AssemblyManager.CategorizationVolumeTolerance = volumeTolerance;
        saved.AssemblyManager.CategorizationArrangementTolerance = arrangementTolerance;
        saved.AssemblyManager.DebugCategorization = _debugCategorization.Checked == true;
        saved.LayPartsFlat.PartSpacing = layFlatSpacing;
        _settings.Save(saved);
        Close(true);
    }

    private static void AddSectionHeader(DynamicLayout layout, string text)
    {
        layout.AddRow(null);
        layout.AddRow(new Label
        {
            Text = text
        });
    }

    private bool TryReadPositiveDouble(string? input, string label, out double value)
    {
        var raw = input?.Trim() ?? string.Empty;
        if ((double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                || double.TryParse(raw, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
            && value > 0.0
            && !double.IsNaN(value)
            && !double.IsInfinity(value))
        {
            return true;
        }

        MessageBox.Show(this, $"{label} must be a positive number.", MessageBoxType.Warning);
        return false;
    }

    private static string FormatDouble(double value)
    {
        return value.ToString("0.#####", CultureInfo.InvariantCulture);
    }
}
