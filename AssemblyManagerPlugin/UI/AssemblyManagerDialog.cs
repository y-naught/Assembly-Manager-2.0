using AssemblyManagerPlugin.Core;
using AssemblyManagerPlugin.Infrastructure;
using Eto.Drawing;
using Eto.Forms;
using Rhino;
using Rhino.DocObjects;
using Rhino.Input.Custom;
using Rhino.UI;

namespace AssemblyManagerPlugin.UI;

public sealed class AssemblyManagerDialog : Dialog<bool>
{
    private readonly RhinoDoc _doc;
    private readonly ServiceFactory _services;
    private readonly ListBox _assemblyList = new();
    private readonly ListBox _componentList = new();
    private readonly ListBox _partList = new();
    private readonly TextBox _assemblyName = new();
    private readonly TextBox _partPrefix = new() { Text = "P" };
    private readonly TextBox _componentPrefix = new() { Text = "C" };
    private readonly TextBox _assemblySummary = new() { ReadOnly = true };
    private readonly TextBox _componentQuantity = new() { ReadOnly = true };

    public AssemblyManagerDialog(RhinoDoc doc, ServiceFactory services)
    {
        _doc = doc;
        _services = services;

        Title = "Assembly Manager";
        Padding = new Padding(12);
        Resizable = true;
        MinimumSize = new Size(920, 640);
        Size = new Size(980, 700);

        _assemblyList.Activated += (_, _) => RefreshComponents();
        _assemblyList.SelectedIndexChanged += (_, _) => RefreshComponents();
        _componentList.Activated += (_, _) => RefreshParts();
        _componentList.SelectedIndexChanged += (_, _) => RefreshParts();

        ApplyDefaultSettings();
        Content = BuildLayout();
        RefreshAssemblies();
    }

    private Control BuildLayout()
    {
        var createButton = new Button { Text = "Create Assembly", Width = 145 };
        createButton.Click += (_, _) => CreateAssembly();

        var removeButton = new Button { Text = "Remove Assembly", Width = 145 };
        removeButton.Click += (_, _) => RemoveAssembly();

        var layFlatButton = new Button { Text = "Lay Parts Flat", Width = 155 };
        layFlatButton.Click += (_, _) => LayPartsFlat();

        var copyOrientButton = new Button { Text = "Copy / Orient Components", Width = 190 };
        copyOrientButton.Click += (_, _) => CopyOrientComponents();

        var refreshRefsButton = new Button { Text = "Refresh References", Width = 155 };
        refreshRefsButton.Click += (_, _) => RefreshReferences();

        var materialLibraryButton = new Button { Text = "Material Library", Width = 155 };
        materialLibraryButton.Click += (_, _) => ShowMaterialLibrary();

        var estimateMaterialsButton = new Button { Text = "Estimate Materials", Width = 155 };
        estimateMaterialsButton.Click += (_, _) => EstimateMaterials();

        var placeEstimateButton = new Button { Text = "Place Estimate", Width = 155 };
        placeEstimateButton.Click += (_, _) => PlaceMaterialEstimate();

        var exportEstimateButton = new Button { Text = "Export Estimate", Width = 155 };
        exportEstimateButton.Click += (_, _) => ExportMaterialEstimate();

        var generateBomButton = new Button { Text = "Generate BOM", Width = 155 };
        generateBomButton.Click += (_, _) => GenerateBom();

        var settingsButton = new Button { Text = "Settings", Width = 155 };
        settingsButton.Click += (_, _) => ShowSettings();

        var assemblyLayout = new DynamicLayout
        {
            Spacing = new Size(8, 8),
            Padding = new Padding(8),
            Width = 330
        };
        assemblyLayout.Add(_assemblyList, xscale: true, yscale: true);
        assemblyLayout.AddRow(new Label { Text = "Summary" }, _assemblySummary);
        assemblyLayout.AddRow(new Label { Text = "New Name" }, _assemblyName);
        assemblyLayout.AddRow(new Label { Text = "Part Prefix" }, _partPrefix);
        assemblyLayout.AddRow(new Label { Text = "Component Prefix" }, _componentPrefix);
        assemblyLayout.AddRow(createButton, removeButton);

        var contentLayout = new DynamicLayout
        {
            Spacing = new Size(10, 8),
            Padding = new Padding(10)
        };
        contentLayout.BeginHorizontal();
        contentLayout.BeginVertical(new Padding(0), new Size(8, 6));
        contentLayout.AddRow(new Label { Text = "Components" });
        contentLayout.Add(_componentList, xscale: true, yscale: true);
        contentLayout.AddRow(new Label { Text = "Selected Quantity" }, _componentQuantity);
        contentLayout.EndVertical();
        contentLayout.BeginVertical(new Padding(0), new Size(8, 6));
        contentLayout.AddRow(new Label { Text = "Parts" });
        contentLayout.Add(_partList, xscale: true, yscale: true);
        contentLayout.EndVertical();
        contentLayout.EndHorizontal();

        var workflowLayout = new DynamicLayout
        {
            Spacing = new Size(8, 8),
            Padding = new Padding(10)
        };
        workflowLayout.AddRow(new Label { Text = "Manufacturing" });
        workflowLayout.AddRow(layFlatButton, estimateMaterialsButton, placeEstimateButton);
        workflowLayout.AddRow(exportEstimateButton, generateBomButton);
        workflowLayout.AddRow(new Label { Text = "Documentation" });
        workflowLayout.AddRow(copyOrientButton, refreshRefsButton);
        workflowLayout.AddRow(new Label { Text = "Library and Setup" });
        workflowLayout.AddRow(materialLibraryButton, settingsButton);

        var rightLayout = new DynamicLayout
        {
            Spacing = new Size(10, 10),
            Padding = new Padding(0, 0, 0, 8)
        };
        rightLayout.AddRow(BuildSection("Components and Parts", contentLayout));
        rightLayout.AddRow(BuildSection("Workflow", workflowLayout));
        rightLayout.AddRow(null);

        var root = new DynamicLayout
        {
            Spacing = new Size(12, 8),
            Padding = new Padding(10)
        };
        root.BeginHorizontal();
        root.Add(BuildSection("Assemblies", assemblyLayout), xscale: false, yscale: true);
        root.Add(new Scrollable
        {
            Content = rightLayout,
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

    private void RefreshAssemblies()
    {
        var store = _services.Repository.Load(_doc);
        _assemblyList.DataStore = store.Assemblies.Select(a => a.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        if (_assemblyList.SelectedIndex < 0 && store.Assemblies.Count > 0)
            _assemblyList.SelectedIndex = 0;

        RefreshComponents();
    }

    private void RefreshComponents()
    {
        var assembly = SelectedAssembly();
        UpdateAssemblySummary(assembly);
        _componentList.DataStore = assembly?.Components.Select(c => c.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList()
            ?? new List<string>();
        if (_componentList.SelectedIndex < 0 && assembly?.Components.Count > 0)
            _componentList.SelectedIndex = 0;

        RefreshParts();
    }

    private void UpdateAssemblySummary(AssemblyRecord? assembly)
    {
        _assemblySummary.Text = assembly is null
            ? string.Empty
            : $"{assembly.Parts.Count} part(s), {assembly.Components.Count} component type(s), {assembly.Hardware.Count} hardware item(s)";
    }

    private void RefreshParts()
    {
        var assembly = SelectedAssembly();
        var component = SelectedComponent(assembly);
        _componentQuantity.Text = component?.Quantity.ToString() ?? string.Empty;
        _partList.DataStore = component?.PartNames
            .Select(partName =>
            {
                var quantity = component.PartQuantities.TryGetValue(partName, out var count) ? count : 1;
                return quantity > 1 ? $"{partName} x{quantity}" : partName;
            })
            .ToList() ?? new List<string>();
    }

    private AssemblyRecord? SelectedAssembly()
    {
        var selectedName = _assemblyList.SelectedValue as string;
        if (string.IsNullOrWhiteSpace(selectedName))
            return null;

        return _services.Repository.Load(_doc).FindAssembly(selectedName);
    }

    private ComponentRecord? SelectedComponent(AssemblyRecord? assembly)
    {
        var selectedName = _componentList.SelectedValue as string;
        if (assembly is null || string.IsNullOrWhiteSpace(selectedName))
            return null;

        return assembly.Components.FirstOrDefault(c => string.Equals(c.Name, selectedName, StringComparison.OrdinalIgnoreCase));
    }

    private void CreateAssembly()
    {
        var name = _assemblyName.Text?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show(this, "Assembly name is required.", MessageBoxType.Warning);
            return;
        }

        Visible = false;
        try
        {
            var getObject = new GetObject();
            getObject.SetCommandPrompt("Select component groups in assembly");
            getObject.GeometryFilter = ObjectType.Brep | ObjectType.Extrusion | ObjectType.Surface | ObjectType.InstanceReference;
            getObject.GroupSelect = true;
            getObject.EnablePreSelect(false, true);
            getObject.GetMultiple(1, 0);
            if (getObject.CommandResult() != Rhino.Commands.Result.Success)
                return;

            var ids = getObject.Objects().Select(reference => reference.ObjectId).ToList();
            var result = _services.AssemblyGeneration().CreateAssembly(_doc, ids, new CreateAssemblyOptions
            {
                AssemblyName = name,
                PartPrefix = _partPrefix.Text,
                ComponentPrefix = _componentPrefix.Text
            });

            RhinoApp.WriteLine("Created {0}: {1} unique part(s), {2} component type(s).",
                result.Assembly.Name,
                result.UniquePartCount,
                result.UniqueComponentCount);
            if (result.Warnings.Count > 0)
            {
                var warningText = string.Join("\n", result.Warnings.Take(8));
                if (result.Warnings.Count > 8)
                    warningText += $"\n...and {result.Warnings.Count - 8} more warning(s).";
                MessageBox.Show(this, warningText, MessageBoxType.Warning);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, MessageBoxType.Error);
        }
        finally
        {
            Visible = true;
            RefreshAssemblies();
        }
    }

    private void RemoveAssembly()
    {
        var assembly = SelectedAssembly();
        if (assembly is null)
            return;

        var confirm = MessageBox.Show(
            this,
            $"Remove '{assembly.Name}' and delete its Assembly Manager geometry and layers?",
            MessageBoxButtons.YesNo,
            MessageBoxType.Question);

        if (confirm != DialogResult.Yes)
            return;

        try
        {
            var result = _services.AssemblyRemoval().RemoveAssembly(_doc, assembly.Name);
            RhinoApp.WriteLine(
                $"Removed {result.AssemblyName}: deleted {result.DeletedObjectCount} object(s), {result.DeletedLayerCount} layer(s), and {result.DeletedGroupCount} group(s).");
            RefreshAssemblies();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, MessageBoxType.Error);
        }
    }

    private void LayPartsFlat()
    {
        var assembly = SelectedAssembly();
        if (assembly is null)
            return;

        try
        {
            var count = _services.LayPartsFlat().LayPartsFlat(_doc, assembly.Name);
            RhinoApp.WriteLine("Laid flat {0} part(s) for {1}.", count, assembly.Name);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, MessageBoxType.Error);
        }
    }

    private void CopyOrientComponents()
    {
        var assembly = SelectedAssembly();
        if (assembly is null)
            return;

        try
        {
            var count = _services.ComponentDrawing().CopyAndOrientComponents(_doc, assembly.Name);
            RhinoApp.WriteLine("Copied and oriented {0} component type(s) for {1}.", count, assembly.Name);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, MessageBoxType.Error);
        }
    }

    private void RefreshReferences()
    {
        var assembly = SelectedAssembly();
        if (assembly is null)
            return;

        try
        {
            var count = _services.ReferenceUpdate().RefreshAssemblyReferences(_doc, assembly.Name);
            RhinoApp.WriteLine("Refreshed {0} generated object(s) for {1}.", count, assembly.Name);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, MessageBoxType.Error);
        }
    }

    private void EstimateMaterials()
    {
        var assembly = SelectedAssembly();
        if (assembly is null)
            return;

        try
        {
            var report = _services.NestingEstimate().EstimateMaterials(_doc, assembly.Name);
            foreach (var line in report.Lines)
            {
                RhinoApp.WriteLine(
                    $"{line.BaseMaterialName} | {line.ShapeName}: {line.EstimatedSheetCount} sheet(s), total part area {line.TotalPartArea:0.###}, stock {line.SheetWidth:0.###} x {line.SheetHeight:0.###}");
            }

            if (report.UnaccountedObjects.Count > 0)
                RhinoApp.WriteLine("Material estimate has {0} unaccounted part type(s).", report.UnaccountedObjects.Count);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, MessageBoxType.Error);
        }
    }

    private void PlaceMaterialEstimate()
    {
        var assembly = SelectedAssembly();
        if (assembly is null)
            return;

        if (_doc.ActiveSpace != ActiveSpace.PageSpace)
        {
            MessageBox.Show(this, "Material estimate tables must be placed from layout/page space.", MessageBoxType.Warning);
            return;
        }

        Visible = false;
        try
        {
            var pointGetter = new GetPoint();
            pointGetter.SetCommandPrompt("Material estimate table insertion point");
            pointGetter.Get();
            if (pointGetter.CommandResult() != Rhino.Commands.Result.Success)
                return;

            var count = _services.NestingEstimate().PlaceMaterialEstimateTable(_doc, assembly.Name, pointGetter.Point());
            RhinoApp.WriteLine("Placed material estimate table with {0} object(s).", count);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, MessageBoxType.Error);
        }
        finally
        {
            Visible = true;
        }
    }

    private void ExportMaterialEstimate()
    {
        var assembly = SelectedAssembly();
        if (assembly is null)
            return;

        var dialog = new Eto.Forms.SaveFileDialog
        {
            Title = "Export Material Estimate",
            FileName = $"{assembly.Name}_MaterialEstimate.csv",
            Filters =
            {
                new FileFilter("CSV", ".csv"),
                new FileFilter("JSON", ".json")
            }
        };

        if (dialog.ShowDialog(RhinoEtoApp.MainWindow) != DialogResult.Ok)
            return;

        try
        {
            var service = _services.NestingEstimate();
            var report = service.EstimateMaterials(_doc, assembly.Name);
            if (Path.GetExtension(dialog.FileName).Equals(".json", StringComparison.OrdinalIgnoreCase))
                service.ExportJson(report, dialog.FileName);
            else
                service.ExportCsv(report, dialog.FileName);

            RhinoApp.WriteLine("Exported material estimate to {0}", dialog.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, MessageBoxType.Error);
        }
    }

    private void GenerateBom()
    {
        var assembly = SelectedAssembly();
        if (assembly is null)
            return;

        try
        {
            _services.NestingEstimate().EstimateMaterials(_doc, assembly.Name);
            var bom = _services.Bom().GenerateBom(_doc, assembly.Name);
            RhinoApp.WriteLine("BOM for {0}: {1} line(s)", assembly.Name, bom.Lines.Count);
            foreach (var line in bom.Lines)
                RhinoApp.WriteLine($"{line.Category} | {line.Item} | {line.Quantity:0.###} {line.Unit}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, MessageBoxType.Error);
        }
    }

    private void ShowSettings()
    {
        var dialog = new SettingsDialog(_services.PluginSettings);
        if (dialog.ShowModal(RhinoEtoApp.MainWindow))
            ApplyDefaultSettings();
    }

    private void ApplyDefaultSettings()
    {
        var settings = _services.PluginSettings.Load().AssemblyManager;
        _partPrefix.Text = settings.DefaultPartPrefix;
        _componentPrefix.Text = settings.DefaultComponentPrefix;
    }

    private void ShowMaterialLibrary()
    {
        var dialog = new MaterialLibraryDialog(_doc, _services.MaterialLibrary());
        dialog.ShowModal(RhinoEtoApp.MainWindow);
    }

}
