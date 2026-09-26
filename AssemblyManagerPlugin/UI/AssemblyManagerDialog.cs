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
    private readonly Label _linkIssueCount = new() { Font = SystemFonts.Bold(), Wrap = WrapMode.Word };
    private readonly Label _documentIssueCount = new() { Wrap = WrapMode.Word };
    private readonly TextArea _linkIssueDetails = new() { ReadOnly = true, Wrap = true, Height = 150 };
    private readonly Label _undoHealthCount = new() { Wrap = WrapMode.Word };
    private readonly TextArea _undoHealthDetails = new() { ReadOnly = true, Wrap = true, Height = 75 };
    private AssemblyStore _displayedStore = new();
    private string? _lastStoreJson;
    private string _lastUndoHealthSignature = string.Empty;
    private bool _refreshingView;
    private bool _observingChanges;

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
        Shown += (_, _) => StartObservingChanges();
        Closed += (_, _) => StopObservingChanges();
        UnLoad += (_, _) => StopObservingChanges();
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

        var placeComponentButton = new Button
        {
            Text = "Place Component", Width = 155,
            ToolTip = "Place another independently tracked drawing view of the selected component, without increasing assembly quantities."
        };
        placeComponentButton.Click += (_, _) => PlaceComponent();

        var refreshRefsButton = new Button
        {
            Text = "Update Assembly",
            Width = 155,
            ToolTip = "Apply pending linked edits and update this assembly, including its flat parts, quantities, and materials."
        };
        refreshRefsButton.Click += (_, _) => UpdateAssembly();

        var updateComponentButton = new Button
        {
            Text = "Update Component",
            Width = 155,
            ToolTip = "Register additions to one regrouped occurrence of the selected component. Update Assembly applies them and assigns its component category; other occurrences remain unchanged."
        };
        updateComponentButton.Click += (_, _) => UpdateComponent();

        var materialLibraryButton = new Button { Text = "Material Library", Width = 155 };
        materialLibraryButton.Click += (_, _) => ShowMaterialLibrary();

        var estimateMaterialsButton = new Button { Text = "Estimate Materials", Width = 155 };
        estimateMaterialsButton.Click += (_, _) => EstimateMaterials();

        var placeEstimateButton = new Button
        {
            Text = "Place BOM",
            Width = 155,
            ToolTip = "Choose BOM columns and fit the material/hardware table inside a two-corner rectangle on the layout sheet."
        };
        placeEstimateButton.Click += (_, _) => PlaceBom();

        var exportEstimateButton = new Button { Text = "Export Estimate", Width = 155 };
        exportEstimateButton.Click += (_, _) => ExportMaterialEstimate();

        var generateBomButton = new Button { Text = "Generate BOM", Width = 155 };
        generateBomButton.Click += (_, _) => GenerateBom();

        var settingsButton = new Button { Text = "Settings", Width = 155 };
        settingsButton.Click += (_, _) => ShowSettings();

        var refreshIssuesButton = new Button
        {
            Text = "Refresh Issues",
            ToolTip = "Reload link issues and check link health without changing geometry."
        };
        refreshIssuesButton.Click += (_, _) => RefreshIssues();

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
        workflowLayout.AddRow(copyOrientButton, placeComponentButton);
        workflowLayout.AddRow(updateComponentButton, refreshRefsButton);
        workflowLayout.AddRow(new Label { Text = "Library and Setup" });
        workflowLayout.AddRow(materialLibraryButton, settingsButton);

        var linkIssuesLayout = new DynamicLayout
        {
            Spacing = new Size(8, 6),
            Padding = new Padding(10)
        };
        linkIssuesLayout.AddRow(_linkIssueCount, refreshIssuesButton);
        linkIssuesLayout.AddRow(_documentIssueCount);
        linkIssuesLayout.AddRow(_linkIssueDetails);
        linkIssuesLayout.AddRow(_undoHealthCount);
        linkIssuesLayout.AddRow(_undoHealthDetails);

        var rightLayout = new DynamicLayout
        {
            Spacing = new Size(10, 10),
            Padding = new Padding(0, 0, 0, 8)
        };
        rightLayout.AddRow(BuildSection("Components and Parts", contentLayout));
        rightLayout.AddRow(BuildSection("Workflow", workflowLayout));
        rightLayout.AddRow(BuildSection("Link Issues", linkIssuesLayout));
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
        if (_refreshingView)
            return;

        var selectedAssemblyName = _assemblyList.SelectedValue as string;
        var selectedComponentName = _componentList.SelectedValue as string;
        var selectedPart = _partList.SelectedValue as string;
        _refreshingView = true;
        try
        {
            _displayedStore = _services.Repository.Load(_doc);
            _lastStoreJson = ReadStoreJson();
            SetListItems(_assemblyList,
                _displayedStore.Assemblies.Select(a => a.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase),
                selectedAssemblyName);
            var assembly = SelectedAssembly();
            SetListItems(_componentList,
                assembly?.Components.Select(c => c.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    ?? Enumerable.Empty<string>(),
                selectedComponentName);
            UpdateAssemblySummary(assembly);
            UpdateLinkIssues(assembly);
        }
        finally
        {
            _refreshingView = false;
        }

        RefreshParts(selectedPart);
    }

    private void RefreshComponents()
    {
        if (_refreshingView)
            return;

        var assembly = SelectedAssembly();
        UpdateAssemblySummary(assembly);
        UpdateLinkIssues(assembly);
        _refreshingView = true;
        try
        {
            SetListItems(_componentList,
                assembly?.Components.Select(c => c.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    ?? Enumerable.Empty<string>(),
                _componentList.SelectedValue as string);
        }
        finally
        {
            _refreshingView = false;
        }

        RefreshParts();
    }

    private void UpdateAssemblySummary(AssemblyRecord? assembly)
    {
        if (assembly is null)
        {
            _assemblySummary.Text = string.Empty;
            _assemblySummary.ToolTip = string.Empty;
            return;
        }

        var linkedOutputCount = assembly.LinkGraph.Nodes.Count(node =>
            !string.Equals(node.Role, AssemblyLinkRoles.Source, StringComparison.OrdinalIgnoreCase));
        _assemblySummary.Text =
            $"{assembly.Parts.Count} part(s), {assembly.Components.Count} component type(s), " +
            $"{assembly.Hardware.Count} hardware item(s), {linkedOutputCount} linked output(s)";
        if (assembly.PendingComponentUpdates.Count > 0)
            _assemblySummary.Text += $"; {assembly.PendingComponentUpdates.Count} component update(s) pending";
        _assemblySummary.ToolTip = _assemblySummary.Text;
    }

    private void RefreshParts(string? selectedPart = null)
    {
        if (_refreshingView)
            return;

        var assembly = SelectedAssembly();
        var component = SelectedComponent(assembly);
        _componentQuantity.Text = component?.Quantity.ToString() ?? string.Empty;
        SetListItems(_partList, component?.PartNames
            .Select(partName =>
            {
                var quantity = component.PartQuantities.TryGetValue(partName, out var count) ? count : 1;
                return quantity > 1 ? $"{partName} x{quantity}" : partName;
            })
            ?? Enumerable.Empty<string>(), selectedPart ?? _partList.SelectedValue as string);
    }

    private AssemblyRecord? SelectedAssembly()
    {
        var selectedName = _assemblyList.SelectedValue as string;
        if (string.IsNullOrWhiteSpace(selectedName))
            return null;

        return _displayedStore.FindAssembly(selectedName);
    }

    private static void SetListItems(ListBox list, IEnumerable<string> items, string? preferredSelection)
    {
        var values = items.ToList();
        var selectedIndex = values.FindIndex(value =>
            string.Equals(value, preferredSelection, StringComparison.OrdinalIgnoreCase));
        list.DataStore = values;
        list.SelectedIndex = selectedIndex >= 0 ? selectedIndex : values.Count > 0 ? 0 : -1;
    }

    private void UpdateLinkIssues(AssemblyRecord? assembly)
    {
        var conflicts = assembly?.LinkGraph.Conflicts
            .Where(IsOpenConflict)
            .OrderBy(conflict => conflict.DetectedAt)
            .ToList() ?? new List<LinkConflictRecord>();
        var documentCount = _displayedStore.Assemblies.Sum(item => item.LinkGraph.Conflicts.Count(IsOpenConflict));
        _linkIssueCount.Text = assembly is null
            ? "Select an assembly to review its link issues."
            : $"{conflicts.Count} open link issue(s) — {assembly.Name}";
        _linkIssueCount.TextColor = conflicts.Count > 0 ? Colors.DarkRed : SystemColors.ControlText;
        _documentIssueCount.Text = $"{documentCount} open link issue(s) across this document. Details below are for the selected assembly.";
        _linkIssueDetails.Text = assembly is null
            ? "No assembly selected."
            : conflicts.Count == 0
                ? "No open link issues for this assembly."
                : string.Join("\n\n", conflicts.Select((conflict, index) => DescribeConflict(assembly, conflict, index + 1)));
        if (assembly?.PendingComponentUpdates.Count > 0)
            _linkIssueDetails.Text = $"{assembly.PendingComponentUpdates.Count} component occurrence update(s) staged. Click Update Assembly to apply additions only to the selected occurrences and update their component categories. Automatic updates for this assembly are held until then.\n\n" + _linkIssueDetails.Text;

        var undoIssues = _services.LinkEvents.GetUndoHealthIssues(_doc);
        _lastUndoHealthSignature = string.Join("\n", undoIssues);
        _undoHealthCount.Text = $"{undoIssues.Count} undo / redo health warning(s) — document-wide, separate from link issues";
        _undoHealthDetails.Text = undoIssues.Count == 0
            ? "No undo / redo health warnings."
            : string.Join("\n\n", undoIssues.Select((issue, index) => $"{index + 1}. {issue}"));
        _undoHealthDetails.Visible = undoIssues.Count > 0;
    }

    private static bool IsOpenConflict(LinkConflictRecord conflict)
    {
        return string.Equals(conflict.Status, AssemblyLinkStatuses.Open, StringComparison.OrdinalIgnoreCase);
    }

    private static string DescribeConflict(AssemblyRecord assembly, LinkConflictRecord conflict, int number)
    {
        var edge = assembly.LinkGraph.Edges.FirstOrDefault(item => item.Id == conflict.EdgeId);
        var nodeId = conflict.NodeId != Guid.Empty ? conflict.NodeId : edge?.ChildNodeId ?? Guid.Empty;
        var node = assembly.LinkGraph.Nodes.FirstOrDefault(item => item.Id == nodeId);
        var part = node is null ? null : assembly.Parts.FirstOrDefault(item => item.Id == node.PartId);
        var component = node is null ? null : assembly.Components.FirstOrDefault(item => item.Id == node.ComponentId);
        var type = System.Text.RegularExpressions.Regex.Replace(conflict.ConflictType, "(?<=[a-z])(?=[A-Z])", " ");
        var lines = new List<string>
        {
            $"{number}. {type}",
            string.IsNullOrWhiteSpace(conflict.Message) ? "No further description was recorded." : conflict.Message
        };
        if (part is not null)
            lines.Add($"Part: {part.Name}");
        if (component is not null)
            lines.Add($"Component: {component.Name}");
        if (node is not null)
        {
            var location = node.Role switch
            {
                AssemblyLinkRoles.Source => "Input geometry",
                AssemblyLinkRoles.OriginalAssembly => "ORIGINAL ASSEMBLIES",
                AssemblyLinkRoles.CopiedComponent => "COPIED COMPONENTS",
                AssemblyLinkRoles.FlatPart => "PARTS",
                _ => node.Role
            };
            lines.Add($"Location: {location} | Link status: {node.Status}");
            if (node.ObjectId != Guid.Empty)
                lines.Add($"Object ID: {node.ObjectId:D}");
        }
        if (nodeId != Guid.Empty)
            lines.Add($"Link node: {nodeId:D}");
        if (conflict.EdgeId != Guid.Empty)
            lines.Add($"Link edge: {conflict.EdgeId:D}");
        if (conflict.CandidateObjectIds.Count > 0)
            lines.Add($"Candidate object IDs: {string.Join(", ", conflict.CandidateObjectIds)}");
        lines.Add($"Detected: {conflict.DetectedAt.ToLocalTime():g}");
        return string.Join("\n", lines);
    }

    private void RefreshIssues()
    {
        try
        {
            _services.LinkEvents.ValidateLinks(_doc);
            RefreshAssemblies();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, MessageBoxType.Error);
        }
    }

    private string? ReadStoreJson()
    {
        return _doc.Strings.GetValue(AssemblyManagerConstants.StoreSection, AssemblyManagerConstants.StoreEntry);
    }

    private void StartObservingChanges()
    {
        if (_observingChanges)
            return;

        RhinoApp.Idle += OnRhinoIdle;
        RhinoDoc.CloseDocument += OnDocumentClosed;
        _observingChanges = true;
    }

    private void StopObservingChanges()
    {
        if (!_observingChanges)
            return;

        RhinoApp.Idle -= OnRhinoIdle;
        RhinoDoc.CloseDocument -= OnDocumentClosed;
        _observingChanges = false;
    }

    private void OnDocumentClosed(object? sender, DocumentEventArgs e)
    {
        if ((e.Document?.RuntimeSerialNumber ?? e.DocumentSerialNumber) == _doc.RuntimeSerialNumber)
            StopObservingChanges();
    }

    private void OnRhinoIdle(object? sender, EventArgs e)
    {
        if (!Visible || _refreshingView)
            return;

        var json = _lastStoreJson;
        try
        {
            // Only deserialize and rebuild the lists after the saved document data changes.
            json = ReadStoreJson();
            if (!string.Equals(json, _lastStoreJson, StringComparison.Ordinal))
            {
                RefreshAssemblies();
                return;
            }

            var undoSignature = string.Join("\n", _services.LinkEvents.GetUndoHealthIssues(_doc));
            if (!string.Equals(undoSignature, _lastUndoHealthSignature, StringComparison.Ordinal))
                UpdateLinkIssues(SelectedAssembly());
        }
        catch (Exception ex)
        {
            // Avoid repeated dialogs on every idle tick if stored data cannot be read.
            _lastStoreJson = json;
            _linkIssueCount.Text = "Link issue status could not be refreshed.";
            _linkIssueCount.TextColor = Colors.DarkRed;
            _linkIssueDetails.Text = ex.Message;
        }
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
                $"Removed {result.AssemblyName}: deleted {result.DeletedObjectCount} object(s), {result.DeletedLayerCount} layer(s), and {result.DeletedGroupCount} group(s); marked {result.DependentLinkConflictCount} downstream source link(s) for review.");
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

    private void UpdateComponent()
    {
        var assembly = SelectedAssembly();
        var component = SelectedComponent(assembly);
        if (assembly is null || component is null)
        {
            MessageBox.Show(this, "Select an assembly and component type first.", MessageBoxType.Information);
            return;
        }
        Visible = false;
        try
        {
            var groupId = CommandPickers.PickRegroupedComponent(_doc);
            if (groupId.HasValue)
                _services.LinkEvents.StageComponentUpdate(_doc, assembly.Name, component.Id, groupId.Value);
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine("Update component failed: {0}", ex.Message);
            MessageBox.Show(this, ex.Message, MessageBoxType.Error);
        }
        finally
        {
            Visible = true;
            RefreshAssemblies();
        }
    }

    private void PlaceComponent()
    {
        var assembly = SelectedAssembly();
        var component = SelectedComponent(assembly);
        if (assembly is null || component is null)
        {
            MessageBox.Show(this, "Select an assembly and component type first.", MessageBoxType.Information);
            return;
        }
        if (_doc.ActiveSpace == ActiveSpace.PageSpace)
        {
            MessageBox.Show(this, "Place component drawing geometry in model space, then display it through a layout detail.", MessageBoxType.Information);
            return;
        }
        Visible = false;
        try
        {
            _services.LinkSafety.EnsureCanUpdate(_doc, assembly.Id);
            using var getter = new GetPoint();
            getter.SetCommandPrompt($"Location for {component.Name} drawing view (component center)");
            getter.Get();
            if (getter.CommandResult() != Rhino.Commands.Result.Success)
                return;
            var count = _services.ComponentDrawing().PlaceComponent(_doc, assembly.Name, component.Id, getter.Point());
            RhinoApp.WriteLine("Placed a tracked {0} drawing view with {1} objects; assembly quantities are unchanged.", component.Name, count);
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine("PlaceComponent failed: {0}", ex.Message);
            MessageBox.Show(this, ex.Message, MessageBoxType.Error);
        }
        finally
        {
            Visible = true;
            RefreshAssemblies();
        }
    }

    private void UpdateAssembly()
    {
        var assembly = SelectedAssembly();
        if (assembly is null)
            return;

        try
        {
            _services.LinkEvents.UpdateAssembly(_doc, assembly.Name);
            var healthIssueCount = _services.LinkEvents.ValidateLinks(_doc).Count;
            if (healthIssueCount == 0)
                RhinoApp.WriteLine("Gazelle link health is clean after the update.");
            else
                RhinoApp.WriteLine("Gazelle still reports {0} link health warning(s) after the update.", healthIssueCount);
            RefreshAssemblies();
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine("Update assembly failed: {0}", ex.Message);
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

    private void PlaceBom()
    {
        var assembly = SelectedAssembly();
        if (assembly is null)
            return;

        if (_doc.ActiveSpace != ActiveSpace.PageSpace)
        {
            MessageBox.Show(this, "Move to a layout sheet and exit any active detail before using Place BOM.", MessageBoxType.Warning);
            return;
        }

        Visible = false;
        try
        {
            PlaceBomCommand.RunPlacement(_doc, _services, assembly.Name);
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
