using System.Globalization;
using AssemblyManagerPlugin.Core;
using AssemblyManagerPlugin.Services;
using Eto.Drawing;
using Eto.Forms;
using Rhino;

namespace AssemblyManagerPlugin.UI;

public sealed class MaterialLibraryDialog : Dialog<bool>
{
    private static readonly string[] MaterialCategoryPresets =
    {
        "wood",
        "composite",
        "metal",
        "plastic",
        "hardware",
        "other"
    };

    private static readonly string[] ShapeTypePresets =
    {
        "sheetgood",
        "plate",
        "round stock",
        "square stock",
        "tube",
        "pipe",
        "hardware",
        "other"
    };

    private readonly RhinoDoc _doc;
    private readonly MaterialLibraryService _service;
    private readonly ListBox _materialList = new();
    private readonly ListBox _shapeList = new();

    private readonly TextBox _materialName = new() { Width = 260 };
    private readonly TextBox _materialCategory = new() { Width = 180 };
    private readonly TextBox _materialDescription = new() { Width = 500 };
    private readonly TextBox _materialDensity = new() { Width = 120 };

    private readonly TextBox _shapeName = new() { Width = 260 };
    private readonly TextBox _shapeType = new() { Text = "sheetgood", Width = 180 };
    private readonly TextBox _sheetSize = new() { Text = "48x96", Width = 160 };
    private readonly TextBox _thickness = new() { Width = 120 };
    private readonly TextBox _unit = new() { Text = "in", Width = 80 };
    private readonly TextBox _stockLength = new() { Width = 120 };
    private readonly TextBox _width = new() { Width = 120 };
    private readonly TextBox _height = new() { Width = 120 };
    private readonly TextBox _diameter = new() { Width = 120 };
    private readonly TextBox _wallThickness = new() { Width = 120 };
    private readonly TextBox _nestingEfficiency = new() { Text = "0.8", Width = 120 };
    private readonly TextBox _pricePerUnit = new() { Width = 120 };
    private readonly TextBox _priceUnit = new() { Width = 120 };

    private string _currentMaterialId = string.Empty;
    private string _currentShapeId = string.Empty;
    private List<MaterialDefinitionRecord> _materials = new();
    private List<MaterialShapeRecord> _shapes = new();
    private bool _isRefreshing;

    public MaterialLibraryDialog(RhinoDoc doc, MaterialLibraryService service)
    {
        _doc = doc;
        _service = service;

        Title = "Material Library";
        Padding = new Padding(12);
        Resizable = true;
        MinimumSize = new Size(920, 620);
        Size = new Size(1040, 720);

        _materialList.SelectedIndexChanged += (_, _) => LoadSelectedMaterial();
        _shapeList.SelectedIndexChanged += (_, _) => LoadSelectedShape();
        Content = BuildLayout();
        RefreshMaterials();
    }

    private Control BuildLayout()
    {
        var importButton = new Button { Text = "Import CSV / JSON" };
        importButton.Click += (_, _) => ImportLibrary();

        var exportButton = new Button { Text = "Export CSV / JSON" };
        exportButton.Click += (_, _) => ExportLibrary();

        var purgeButton = new Button { Text = "Purge Library" };
        purgeButton.Click += (_, _) => PurgeLibrary();

        var newMaterialButton = new Button { Text = "New Material", Width = 130 };
        newMaterialButton.Click += (_, _) => ClearMaterialEditor();

        var saveMaterialButton = new Button { Text = "Save Material", Width = 130 };
        saveMaterialButton.Click += (_, _) => SaveMaterial();

        var deleteMaterialButton = new Button { Text = "Delete Material", Width = 130 };
        deleteMaterialButton.Click += (_, _) => DeleteSelectedMaterial();

        var newShapeButton = new Button { Text = "New Shape", Width = 130 };
        newShapeButton.Click += (_, _) => ClearShapeEditor();

        var saveShapeButton = new Button { Text = "Save Shape", Width = 130 };
        saveShapeButton.Click += (_, _) => SaveShape();

        var deleteShapeButton = new Button { Text = "Delete Shape", Width = 130 };
        deleteShapeButton.Click += (_, _) => DeleteSelectedShape();

        var closeButton = new Button { Text = "Close" };
        closeButton.Click += (_, _) => Close(true);

        var materialCategoryMenu = new DropDown { DataStore = MaterialCategoryPresets, Width = 180 };
        materialCategoryMenu.SelectedIndexChanged += (_, _) =>
        {
            if (materialCategoryMenu.SelectedValue is string value)
                _materialCategory.Text = value;
        };

        var shapeTypeMenu = new DropDown { DataStore = ShapeTypePresets, Width = 180 };
        shapeTypeMenu.SelectedIndexChanged += (_, _) =>
        {
            if (shapeTypeMenu.SelectedValue is string value)
            {
                _shapeType.Text = value;
                if (IsSheetLike(value))
                {
                    if (string.IsNullOrWhiteSpace(_sheetSize.Text))
                        _sheetSize.Text = "48x96";
                }
                else
                {
                    _sheetSize.Text = string.Empty;
                }
            }
        };

        var materialButtons = new DynamicLayout { Spacing = new Size(6, 6) };
        materialButtons.AddRow(newMaterialButton, deleteMaterialButton);

        var shapeButtons = new DynamicLayout { Spacing = new Size(6, 6) };
        shapeButtons.AddRow(newShapeButton, deleteShapeButton);

        var libraryLayout = new DynamicLayout
        {
            Spacing = new Size(8, 8),
            Padding = new Padding(8),
            Width = 330
        };
        libraryLayout.AddRow(new Label { Text = "Materials" });
        libraryLayout.Add(_materialList, xscale: true, yscale: true);
        libraryLayout.AddRow(materialButtons);
        libraryLayout.AddRow(new Label { Text = "Stock Shapes" });
        libraryLayout.Add(_shapeList, xscale: true, yscale: true);
        libraryLayout.AddRow(shapeButtons);
        libraryLayout.AddRow(importButton, exportButton);
        libraryLayout.AddRow(purgeButton);

        var materialEditor = new DynamicLayout
        {
            Spacing = new Size(8, 8),
            Padding = new Padding(10)
        };
        materialEditor.AddRow(new Label { Text = "Name" }, _materialName);
        materialEditor.AddRow(new Label { Text = "Category" }, _materialCategory, new Label { Text = "Preset" }, materialCategoryMenu);
        materialEditor.AddRow(new Label { Text = "Density" }, _materialDensity, new Label { Text = "Units" }, new Label { Text = "lb/cuin" });
        materialEditor.AddRow(new Label { Text = "Description" }, _materialDescription);
        materialEditor.AddRow(null);
        materialEditor.AddRow(saveMaterialButton);

        var shapeEditor = new DynamicLayout
        {
            Spacing = new Size(8, 8),
            Padding = new Padding(10)
        };
        shapeEditor.AddRow(new Label { Text = "Name" }, _shapeName);
        shapeEditor.AddRow(new Label { Text = "Type" }, _shapeType, new Label { Text = "Preset" }, shapeTypeMenu);
        shapeEditor.AddRow(new Label { Text = "Thickness" }, _thickness, new Label { Text = "Unit" }, _unit);
        shapeEditor.AddRow(new Label { Text = "Sheet Size" }, _sheetSize, new Label { Text = "Nesting Efficiency" }, _nestingEfficiency);
        shapeEditor.AddRow(new Label { Text = "Stock Length" }, _stockLength, new Label { Text = "Actual Width" }, _width);
        shapeEditor.AddRow(new Label { Text = "Actual Height" }, _height, new Label { Text = "Diameter" }, _diameter);
        shapeEditor.AddRow(new Label { Text = "Wall Thickness" }, _wallThickness);
        shapeEditor.AddRow(new Label { Text = "Price / Unit" }, _pricePerUnit, new Label { Text = "Price Unit" }, _priceUnit);
        shapeEditor.AddRow(null);
        shapeEditor.AddRow(saveShapeButton);

        var editorLayout = new DynamicLayout
        {
            Spacing = new Size(10, 10),
            Padding = new Padding(0, 0, 0, 8)
        };
        editorLayout.AddRow(BuildSection("Material Details", materialEditor));
        editorLayout.AddRow(BuildSection("Stock Shape Details", shapeEditor));
        editorLayout.AddRow(null);
        editorLayout.AddRow(closeButton);

        var root = new DynamicLayout
        {
            Spacing = new Size(12, 8),
            Padding = new Padding(10)
        };
        root.BeginHorizontal();
        root.Add(BuildSection("Library", libraryLayout), xscale: false, yscale: true);
        root.Add(new Scrollable
        {
            Content = editorLayout,
            ExpandContentWidth = true,
            ExpandContentHeight = false
        }, xscale: true, yscale: true);
        root.EndHorizontal();

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

    private void RefreshMaterials(string? selectedMaterialId = null, string? selectedShapeId = null)
    {
        _isRefreshing = true;
        _materials = _service.GetMaterialDefinitions(_doc).ToList();
        _materialList.DataStore = _materials.Select(DisplayMaterialName).ToList();

        var selectedIndex = -1;
        var desiredMaterialId = string.IsNullOrWhiteSpace(selectedMaterialId) ? _currentMaterialId : selectedMaterialId;
        if (!string.IsNullOrWhiteSpace(desiredMaterialId))
            selectedIndex = _materials.FindIndex(m => string.Equals(m.Id, desiredMaterialId, StringComparison.OrdinalIgnoreCase));

        if (selectedIndex < 0 && _materials.Count > 0)
            selectedIndex = 0;

        _isRefreshing = false;
        _materialList.SelectedIndex = selectedIndex;

        if (selectedIndex >= 0)
            LoadSelectedMaterial(selectedShapeId);
        else
            ClearMaterialEditor();
    }

    private void LoadSelectedMaterial(string? selectedShapeId = null)
    {
        if (_isRefreshing)
            return;

        var index = _materialList.SelectedIndex;
        if (index < 0 || index >= _materials.Count)
            return;

        var material = _materials[index];
        _currentMaterialId = material.Id;
        _materialName.Text = material.Name;
        _materialCategory.Text = material.Category;
        _materialDescription.Text = material.Description;
        _materialDensity.Text = FormatOptionalDouble(material.DensityLbPerCubicInch);

        _shapes = material.Shapes
            .OrderBy(s => s.ShapeType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Thickness)
            .ThenBy(s => s.SheetWidth)
            .ThenBy(s => s.SheetHeight)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _shapeList.DataStore = _shapes.Select(DisplayShapeName).ToList();

        var desiredShapeId = string.IsNullOrWhiteSpace(selectedShapeId) ? _currentShapeId : selectedShapeId;
        var selectedIndex = -1;
        if (!string.IsNullOrWhiteSpace(desiredShapeId))
            selectedIndex = _shapes.FindIndex(s => string.Equals(s.Id, desiredShapeId, StringComparison.OrdinalIgnoreCase));

        if (selectedIndex < 0 && _shapes.Count > 0)
            selectedIndex = 0;

        _shapeList.SelectedIndex = selectedIndex;
        if (selectedIndex >= 0)
            LoadSelectedShape();
        else
            ClearShapeEditor(keepSelection: true);
    }

    private void LoadSelectedShape()
    {
        if (_isRefreshing)
            return;

        var index = _shapeList.SelectedIndex;
        if (index < 0 || index >= _shapes.Count)
            return;

        var shape = _shapes[index];
        _currentShapeId = shape.Id;
        _shapeName.Text = shape.Name;
        _shapeType.Text = string.IsNullOrWhiteSpace(shape.ShapeType) ? "sheetgood" : shape.ShapeType;
        _sheetSize.Text = MaterialLibraryService.FormatSheetSize(shape);
        _thickness.Text = FormatOptionalDouble(shape.Thickness);
        _unit.Text = string.IsNullOrWhiteSpace(shape.Unit) ? "in" : shape.Unit;
        _stockLength.Text = FormatOptionalDouble(shape.StockLength);
        _width.Text = FormatOptionalDouble(shape.Width);
        _height.Text = FormatOptionalDouble(shape.Height);
        _diameter.Text = FormatOptionalDouble(shape.Diameter);
        _wallThickness.Text = FormatOptionalDouble(shape.WallThickness);
        _nestingEfficiency.Text = shape.NestingEfficiency.ToString("0.###", CultureInfo.InvariantCulture);
        _pricePerUnit.Text = FormatOptionalDouble(shape.PricePerUnit);
        _priceUnit.Text = shape.PriceUnit;
    }

    private void ClearMaterialEditor()
    {
        _currentMaterialId = string.Empty;
        _currentShapeId = string.Empty;
        _materialName.Text = string.Empty;
        _materialCategory.Text = "wood";
        _materialDescription.Text = string.Empty;
        _materialDensity.Text = string.Empty;
        _shapes = new List<MaterialShapeRecord>();
        _shapeList.DataStore = Array.Empty<string>();
        _materialList.SelectedIndex = -1;
        ClearShapeEditor(keepSelection: true);
    }

    private void ClearShapeEditor(bool keepSelection = false)
    {
        _currentShapeId = string.Empty;
        _shapeName.Text = string.Empty;
        _shapeType.Text = "sheetgood";
        _sheetSize.Text = "48x96";
        _thickness.Text = string.Empty;
        _unit.Text = "in";
        _stockLength.Text = string.Empty;
        _width.Text = string.Empty;
        _height.Text = string.Empty;
        _diameter.Text = string.Empty;
        _wallThickness.Text = string.Empty;
        _nestingEfficiency.Text = "0.8";
        _pricePerUnit.Text = string.Empty;
        _priceUnit.Text = string.Empty;
        if (!keepSelection)
            _shapeList.SelectedIndex = -1;
    }

    private void SaveMaterial()
    {
        var name = _materialName.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show(this, "Material name is required.", MessageBoxType.Warning);
            return;
        }

        if (!TryParseOptionalDouble(_materialDensity.Text, out var density))
        {
            MessageBox.Show(this, "Density should be a number in lb/cuin.", MessageBoxType.Warning);
            return;
        }
        if (density < 0)
        {
            MessageBox.Show(this, "Density cannot be negative.", MessageBoxType.Warning);
            return;
        }

        var material = new MaterialDefinitionRecord
        {
            Id = string.IsNullOrWhiteSpace(_currentMaterialId) ? MaterialLibraryService.MakeMaterialId(name) : _currentMaterialId,
            Name = name,
            Category = string.IsNullOrWhiteSpace(_materialCategory.Text) ? "other" : _materialCategory.Text.Trim(),
            Description = _materialDescription.Text?.Trim() ?? string.Empty,
            DensityLbPerCubicInch = density
        };

        _service.SaveMaterialDefinition(_doc, material);
        RefreshMaterials(material.Id, _currentShapeId);
    }

    private void DeleteSelectedMaterial()
    {
        if (string.IsNullOrWhiteSpace(_currentMaterialId))
            return;

        var confirm = MessageBox.Show(
            this,
            $"Delete '{_materialName.Text}' and all of its stock shapes from the shared material library?",
            MessageBoxButtons.YesNo,
            MessageBoxType.Question);

        if (confirm != DialogResult.Yes)
            return;

        _service.DeleteMaterialDefinition(_doc, _currentMaterialId);
        _currentMaterialId = string.Empty;
        _currentShapeId = string.Empty;
        RefreshMaterials();
    }

    private void SaveShape()
    {
        if (string.IsNullOrWhiteSpace(_currentMaterialId))
        {
            MessageBox.Show(this, "Save or select a material before adding stock shapes.", MessageBoxType.Warning);
            return;
        }

        var shapeName = _shapeName.Text?.Trim() ?? string.Empty;
        var shapeType = string.IsNullOrWhiteSpace(_shapeType.Text) ? "sheetgood" : _shapeType.Text.Trim();
        var shape = new MaterialShapeRecord
        {
            Id = _currentShapeId,
            Name = shapeName,
            ShapeType = shapeType,
            Unit = string.IsNullOrWhiteSpace(_unit.Text) ? "in" : _unit.Text.Trim()
        };

        if (!TryParseOptionalDouble(_thickness.Text, out var thickness))
        {
            MessageBox.Show(this, "Thickness should be a number.", MessageBoxType.Warning);
            return;
        }

        if (!TryParseOptionalDouble(_stockLength.Text, out var stockLength))
        {
            MessageBox.Show(this, "Stock length should be a number.", MessageBoxType.Warning);
            return;
        }

        if (!TryParseOptionalDouble(_width.Text, out var width))
        {
            MessageBox.Show(this, "Width should be a number.", MessageBoxType.Warning);
            return;
        }

        if (!TryParseOptionalDouble(_height.Text, out var height))
        {
            MessageBox.Show(this, "Height should be a number.", MessageBoxType.Warning);
            return;
        }

        if (!TryParseOptionalDouble(_diameter.Text, out var diameter))
        {
            MessageBox.Show(this, "Diameter should be a number.", MessageBoxType.Warning);
            return;
        }

        if (!TryParseOptionalDouble(_wallThickness.Text, out var wallThickness))
        {
            MessageBox.Show(this, "Wall thickness should be a number.", MessageBoxType.Warning);
            return;
        }

        if (!TryParseOptionalDouble(_nestingEfficiency.Text, out var efficiency))
        {
            MessageBox.Show(this, "Nesting efficiency should be a number between 0 and 1.", MessageBoxType.Warning);
            return;
        }
        if (efficiency > 1)
        {
            MessageBox.Show(this, "Nesting efficiency should be a number between 0 and 1.", MessageBoxType.Warning);
            return;
        }

        if (!TryParseOptionalDouble(_pricePerUnit.Text, out var pricePerUnit))
        {
            MessageBox.Show(this, "Price per unit should be a number.", MessageBoxType.Warning);
            return;
        }
        if (pricePerUnit < 0)
        {
            MessageBox.Show(this, "Price per unit cannot be negative.", MessageBoxType.Warning);
            return;
        }

        if (IsSheetLike(shapeType) && !string.IsNullOrWhiteSpace(_sheetSize.Text))
        {
            if (!MaterialLibraryService.TryParseSheetSize(_sheetSize.Text, out var sheetWidth, out var sheetHeight))
            {
                MessageBox.Show(this, "Sheet size should look like 48x96.", MessageBoxType.Warning);
                return;
            }

            shape.SheetWidth = sheetWidth;
            shape.SheetHeight = sheetHeight;
        }

        shape.Thickness = thickness;
        shape.StockLength = stockLength;
        shape.Width = width;
        shape.Height = height;
        shape.Diameter = diameter;
        shape.WallThickness = wallThickness;
        shape.NestingEfficiency = efficiency <= 0 ? 0.8 : efficiency;
        shape.PricePerUnit = pricePerUnit;
        shape.PriceUnit = _priceUnit.Text?.Trim() ?? string.Empty;

        var materials = _service.SaveShape(_doc, _currentMaterialId, shape);
        var selectedShapeId = ResolveSavedShapeId(materials, _currentMaterialId, shape);
        RefreshMaterials(_currentMaterialId, selectedShapeId);
    }

    private void DeleteSelectedShape()
    {
        if (string.IsNullOrWhiteSpace(_currentMaterialId) || string.IsNullOrWhiteSpace(_currentShapeId))
            return;

        var confirm = MessageBox.Show(
            this,
            $"Delete stock shape '{_shapeName.Text}' from '{_materialName.Text}'?",
            MessageBoxButtons.YesNo,
            MessageBoxType.Question);

        if (confirm != DialogResult.Yes)
            return;

        _service.DeleteShape(_doc, _currentMaterialId, _currentShapeId);
        _currentShapeId = string.Empty;
        RefreshMaterials(_currentMaterialId);
    }

    private void ImportLibrary()
    {
        var dialog = new Rhino.UI.OpenFileDialog
        {
            Title = "Import material library",
            Filter = "Material Library (*.csv;*.json)|*.csv;*.json|All Files (*.*)|*.*||"
        };

        if (!dialog.ShowOpenDialog())
            return;

        try
        {
            var stockShapes = _service.ImportLibrary(_doc, dialog.FileName);
            RefreshMaterials();
            RhinoApp.WriteLine("Material library now contains {0} stock shape(s).", stockShapes.Count);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, MessageBoxType.Error);
        }
    }

    private void ExportLibrary()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export material library",
            FileName = "AssemblyManager_MaterialLibrary.json",
            Filters =
            {
                new FileFilter("JSON", ".json"),
                new FileFilter("CSV", ".csv")
            }
        };

        if (dialog.ShowDialog(this) != DialogResult.Ok)
            return;

        try
        {
            var count = _service.ExportLibrary(_doc, dialog.FileName);
            RhinoApp.WriteLine("Exported {0} material stock shape(s) to {1}", count, dialog.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, MessageBoxType.Error);
        }
    }

    private void PurgeLibrary()
    {
        var materialCount = _materials.Count;
        var stockShapeCount = _materials.Sum(material => material.Shapes.Count);
        if (materialCount == 0)
        {
            MessageBox.Show(this, "The material library is already empty.", MessageBoxType.Information);
            return;
        }

        var confirm = MessageBox.Show(
            this,
            $"Purge {materialCount} material(s) and {stockShapeCount} stock shape(s) from the shared material library?\n\nExisting object material assignments will not be removed, but they may resolve as TBD until materials are added again.",
            MessageBoxButtons.YesNo,
            MessageBoxType.Warning);

        if (confirm != DialogResult.Yes)
            return;

        try
        {
            var removedCount = _service.PurgeLibrary(_doc);
            _currentMaterialId = string.Empty;
            _currentShapeId = string.Empty;
            RefreshMaterials();
            RhinoApp.WriteLine("Purged {0} material stock shape(s) from the material library.", removedCount);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, MessageBoxType.Error);
        }
    }

    private static bool TryParseOptionalDouble(string? value, out double number)
    {
        number = 0;
        return string.IsNullOrWhiteSpace(value)
            || double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out number);
    }

    private static string FormatOptionalDouble(double value)
    {
        return value > 0 ? value.ToString("0.###", CultureInfo.InvariantCulture) : string.Empty;
    }

    private static string DisplayMaterialName(MaterialDefinitionRecord material)
    {
        var category = string.IsNullOrWhiteSpace(material.Category) ? "uncategorized" : material.Category;
        var density = material.DensityLbPerCubicInch > 0 ? $" | {material.DensityLbPerCubicInch:0.####} lb/cuin" : string.Empty;
        return $"{material.Name} | {category}{density} | {material.Shapes.Count} shape(s)";
    }

    private static string DisplayShapeName(MaterialShapeRecord shape)
    {
        var detail = new List<string>();
        if (!string.IsNullOrWhiteSpace(shape.ShapeType))
            detail.Add(shape.ShapeType);
        if (shape.Thickness > 0)
            detail.Add($"{shape.Thickness:0.###} {shape.Unit}");
        var sheetSize = MaterialLibraryService.FormatSheetSize(shape);
        if (!string.IsNullOrWhiteSpace(sheetSize))
            detail.Add(sheetSize);
        if (shape.StockLength > 0)
            detail.Add($"{shape.StockLength:0.###} long");
        if (shape.Width > 0
            && shape.Height > 0
            && (!IsSheetLike(shape.ShapeType)
                || !NearlyEqual(shape.Width, shape.SheetWidth)
                || !NearlyEqual(shape.Height, shape.SheetHeight)))
            detail.Add($"{shape.Width:0.###}x{shape.Height:0.###}");
        if (shape.Diameter > 0)
            detail.Add($"{shape.Diameter:0.###} dia");
        if (shape.PricePerUnit > 0)
        {
            var priceUnit = string.IsNullOrWhiteSpace(shape.PriceUnit) ? "unit" : shape.PriceUnit;
            detail.Add($"{shape.PricePerUnit:0.##}/{priceUnit}");
        }

        return detail.Count == 0 ? shape.Name : $"{shape.Name} | {string.Join(" | ", detail)}";
    }

    private static string ResolveSavedShapeId(
        IReadOnlyList<MaterialDefinitionRecord> materials,
        string materialId,
        MaterialShapeRecord shape)
    {
        if (!string.IsNullOrWhiteSpace(shape.Id))
            return shape.Id;

        var material = materials.FirstOrDefault(m => string.Equals(m.Id, materialId, StringComparison.OrdinalIgnoreCase));
        if (material is null)
            return string.Empty;

        var match = material.Shapes.LastOrDefault(saved =>
            string.Equals(saved.Name, shape.Name, StringComparison.OrdinalIgnoreCase)
            && string.Equals(saved.ShapeType, shape.ShapeType, StringComparison.OrdinalIgnoreCase)
            && NearlyEqual(saved.Thickness, shape.Thickness)
            && NearlyEqual(saved.SheetWidth, shape.SheetWidth)
            && NearlyEqual(saved.SheetHeight, shape.SheetHeight)
            && NearlyEqual(saved.StockLength, shape.StockLength)
            && NearlyEqual(saved.Width, shape.Width)
            && NearlyEqual(saved.Height, shape.Height)
            && NearlyEqual(saved.Diameter, shape.Diameter)
            && NearlyEqual(saved.WallThickness, shape.WallThickness)
            && NearlyEqual(saved.PricePerUnit, shape.PricePerUnit)
            && string.Equals(saved.PriceUnit, shape.PriceUnit, StringComparison.OrdinalIgnoreCase));

        return match?.Id ?? material.Shapes.LastOrDefault()?.Id ?? string.Empty;
    }

    private static bool NearlyEqual(double left, double right)
    {
        return Math.Abs(left - right) <= 0.000001;
    }

    private static bool IsSheetLike(string shapeType)
    {
        return shapeType.Contains("sheet", StringComparison.OrdinalIgnoreCase)
            || shapeType.Contains("plate", StringComparison.OrdinalIgnoreCase)
            || shapeType.Contains("panel", StringComparison.OrdinalIgnoreCase);
    }
}
