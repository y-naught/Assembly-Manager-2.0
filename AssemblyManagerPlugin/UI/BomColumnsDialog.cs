using AssemblyManagerPlugin.Core;
using AssemblyManagerPlugin.Services;
using Eto.Drawing;
using Eto.Forms;

namespace AssemblyManagerPlugin.UI;

public sealed class BomColumnsDialog : Dialog<bool>
{
    private readonly List<(PlacedBomColumn Column, CheckBox CheckBox)> _choices = new();
    private readonly Button _confirm = new() { Text = "Confirm" };

    public IReadOnlyList<string> SelectedColumnIds => _choices
        .Where(choice => choice.CheckBox.Checked == true).Select(choice => choice.Column.Id).ToArray();

    public BomColumnsDialog()
    {
        Title = "Place BOM — Columns";
        Padding = new Padding(14);
        Resizable = true;
        ClientSize = new Size(390, 480);
        MinimumSize = new Size(360, 350);

        // Wrapping alone does not bound a label measured inside a scrollable.
        // Keep the short instructions outside it, and do not share grid columns
        // between the instructions, checklist, and multi-button action rows.
        var instructions = new Label
        {
            Text = "Choose columns, then click Confirm.\nNext, pick two corners on the layout sheet.",
            Wrap = WrapMode.Word,
            Width = 300,
            ToolTip = "The BOM will wrap text first, then reduce its size only if needed to fit the rectangle."
        };
        var checklist = new StackLayout
        {
            Orientation = Orientation.Vertical,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Spacing = 7,
            Padding = new Padding(4)
        };
        foreach (var column in PlacedBomService.Columns)
        {
            var checkBox = new CheckBox { Text = column.Title, Checked = column.DefaultSelected };
            checkBox.CheckedChanged += (_, _) => _confirm.Enabled = SelectedColumnIds.Count > 0;
            _choices.Add((column, checkBox));
            checklist.Items.Add(checkBox);
        }

        var selectAll = new Button { Text = "Select All" };
        selectAll.Click += (_, _) => _choices.ForEach(choice => choice.CheckBox.Checked = true);
        var clear = new Button { Text = "Clear" };
        clear.Click += (_, _) => _choices.ForEach(choice => choice.CheckBox.Checked = false);
        var selectionActions = new TableLayout
        {
            Spacing = new Size(8, 0),
            Rows = { new TableRow(selectAll, clear, null) }
        };
        var cancel = new Button { Text = "Cancel" };
        cancel.Click += (_, _) => Close(false);
        _confirm.Enabled = SelectedColumnIds.Count > 0;
        _confirm.Click += (_, _) => { if (SelectedColumnIds.Count > 0) Close(true); };
        DefaultButton = _confirm;
        AbortButton = cancel;
        var dialogActions = new TableLayout
        {
            Spacing = new Size(8, 0),
            Rows = { new TableRow(null, cancel, _confirm) }
        };
        // Only the checklist scrolls. Confirmation stays visible at small heights.
        Content = new TableLayout
        {
            Spacing = new Size(0, 10),
            Rows =
            {
                new TableRow(instructions),
                new TableRow(selectionActions),
                new TableRow(new Scrollable { Content = checklist, ExpandContentWidth = true }) { ScaleHeight = true },
                new TableRow(dialogActions)
            }
        };
    }
}
