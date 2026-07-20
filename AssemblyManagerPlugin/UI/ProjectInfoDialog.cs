using AssemblyManagerPlugin.Core;
using AssemblyManagerPlugin.Services;
using Eto.Drawing;
using Eto.Forms;
using Rhino;

namespace AssemblyManagerPlugin.UI;

public sealed class ProjectInfoDialog : Dialog<bool>
{
    private readonly RhinoDoc _doc;
    private readonly ProjectInfoService _service;
    private readonly Dictionary<string, TextBox> _textBoxes = new();

    public ProjectInfoDialog(RhinoDoc doc, ProjectInfoService service)
    {
        _doc = doc;
        _service = service;
        Title = "Set Project Info";
        Padding = new Padding(15);
        Resizable = false;

        var values = _service.Load(doc);
        var layout = new DynamicLayout { Spacing = new Size(6, 6) };
        foreach (var key in ProjectInfo.Keys)
        {
            var textbox = new TextBox { Width = 260, Text = values.TryGetValue(key, out var value) ? value : string.Empty };
            _textBoxes[key] = textbox;
            layout.AddRow(new Label { Text = key }, textbox);
        }

        var update = new Button { Text = "Update" };
        update.Click += (_, _) => SaveAndClose();
        var cancel = new Button { Text = "Cancel" };
        cancel.Click += (_, _) => Close(false);

        layout.AddRow(null);
        layout.AddRow(update, cancel);
        Content = layout;
    }

    private void SaveAndClose()
    {
        _service.Save(_doc, _textBoxes.ToDictionary(pair => pair.Key, pair => pair.Value.Text ?? string.Empty));
        Close(true);
    }
}
