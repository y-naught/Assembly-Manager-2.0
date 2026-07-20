using AssemblyManagerPlugin.Core;
using AssemblyManagerPlugin.Services;
using Eto.Drawing;
using Eto.Forms;
using Rhino;

namespace AssemblyManagerPlugin.UI;

public sealed class MaterialSelectionDialog : Dialog<bool>
{
    private readonly RhinoDoc _doc;
    private readonly MaterialLibraryService _service;
    private readonly ListBox _materialList = new();
    private readonly Label _selection = new() { Text = "No material selected." };
    private List<MaterialDefinitionRecord> _materials = new();

    public MaterialDefinitionRecord? SelectedMaterial { get; private set; }

    public MaterialSelectionDialog(RhinoDoc doc, MaterialLibraryService service)
    {
        _doc = doc;
        _service = service;

        Title = "Assign Material";
        Padding = new Padding(12);
        Resizable = true;
        MinimumSize = new Size(420, 420);
        Size = new Size(520, 500);

        _materialList.SelectedIndexChanged += (_, _) => LoadSelectedMaterial();
        Content = BuildLayout();
        RefreshMaterials();
    }

    private Control BuildLayout()
    {
        var assignButton = new Button { Text = "Assign", Width = 110 };
        assignButton.Click += (_, _) => AssignAndClose();

        var cancelButton = new Button { Text = "Cancel", Width = 110 };
        cancelButton.Click += (_, _) => Close(false);

        var materialPane = new DynamicLayout { Spacing = new Size(6, 6), Padding = new Padding(8) };
        materialPane.AddRow(new Label { Text = "Materials" });
        materialPane.Add(_materialList, xscale: true, yscale: true);

        var root = new DynamicLayout
        {
            Spacing = new Size(10, 8),
            Padding = new Padding(8)
        };
        root.Add(BuildSection("Material", materialPane), xscale: true, yscale: true);
        root.AddRow(_selection);
        root.AddRow(null);
        root.AddRow(assignButton, cancelButton);
        return root;
    }

    private static Control BuildSection(string title, Control content)
    {
        return new GroupBox
        {
            Text = title,
            Content = content
        };
    }

    private void RefreshMaterials()
    {
        _materials = _service.GetMaterialDefinitions(_doc).ToList();
        _materialList.DataStore = _materials.Select(DisplayMaterialName).ToList();
        _materialList.SelectedIndex = _materials.Count > 0 ? 0 : -1;
        if (_materials.Count == 0)
            _selection.Text = "No materials are available. Add materials to the library first.";
    }

    private void LoadSelectedMaterial()
    {
        var index = _materialList.SelectedIndex;
        if (index < 0 || index >= _materials.Count)
        {
            SelectedMaterial = null;
            return;
        }

        SelectedMaterial = _materials[index];
        _selection.Text = $"Selected: {SelectedMaterial.Name}";
    }

    private void AssignAndClose()
    {
        if (SelectedMaterial is null)
        {
            MessageBox.Show(this, "Select a material to assign.", MessageBoxType.Warning);
            return;
        }

        Close(true);
    }

    private static string DisplayMaterialName(MaterialDefinitionRecord material)
    {
        var category = string.IsNullOrWhiteSpace(material.Category) ? "uncategorized" : material.Category;
        var density = material.DensityLbPerCubicInch > 0
            ? $" | {material.DensityLbPerCubicInch:0.####} lb/cuin"
            : string.Empty;
        return $"{material.Name} | {category}{density}";
    }
}
