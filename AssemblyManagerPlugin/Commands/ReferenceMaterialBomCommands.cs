using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Input.Custom;
using Rhino.UI;
using AssemblyManagerPlugin.UI;

namespace AssemblyManagerPlugin;

public sealed class RefreshAssemblyReferencesCommand : Command
{
    public override string EnglishName => "RefreshAssemblyReferences";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var assemblyName = CommandPickers.PickAssembly(doc, "Assembly to refresh from source geometry");
        if (assemblyName is null)
            return Result.Cancel;

        try
        {
            var count = AssemblyManagerPlugin.Instance.Services.ReferenceUpdate().RefreshAssemblyReferences(doc, assemblyName);
            RhinoApp.WriteLine("Refreshed {0} generated object(s) for {1}.", count, assemblyName);
            return Result.Success;
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine("Refresh references failed: {0}", ex.Message);
            return Result.Failure;
        }
    }
}

public sealed class ImportMaterialLibraryCommand : Command
{
    public override string EnglishName => "ImportMaterialLibrary";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import material library",
            Filter = "Material Library (*.csv;*.json)|*.csv;*.json|All Files (*.*)|*.*||"
        };

        if (!dialog.ShowOpenDialog())
            return Result.Cancel;

        try
        {
            var stockShapes = AssemblyManagerPlugin.Instance.Services.MaterialLibrary().ImportLibrary(doc, dialog.FileName);
            RhinoApp.WriteLine("Material library now contains {0} stock shape(s).", stockShapes.Count);
            return Result.Success;
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine("Import material library failed: {0}", ex.Message);
            return Result.Failure;
        }
    }
}

public sealed class ExportMaterialLibraryCommand : Command
{
    public override string EnglishName => "ExportMaterialLibrary";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var dialog = new Eto.Forms.SaveFileDialog
        {
            Title = "Export Material Library",
            FileName = "AssemblyManager_MaterialLibrary.json",
            Filters =
            {
                new Eto.Forms.FileFilter("JSON", ".json"),
                new Eto.Forms.FileFilter("CSV", ".csv")
            }
        };

        if (dialog.ShowDialog(RhinoEtoApp.MainWindow) != Eto.Forms.DialogResult.Ok)
            return Result.Cancel;

        try
        {
            var count = AssemblyManagerPlugin.Instance.Services.MaterialLibrary().ExportLibrary(doc, dialog.FileName);
            RhinoApp.WriteLine("Exported {0} material stock shape(s) to {1}", count, dialog.FileName);
            return Result.Success;
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine("Export material library failed: {0}", ex.Message);
            return Result.Failure;
        }
    }
}

public sealed class AssignMaterialsCommand : Command
{
    public override string EnglishName => "AssignMaterials";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var getter = new GetObject();
        getter.SetCommandPrompt("Select objects to assign material");
        getter.SubObjectSelect = false;
        getter.EnablePreSelect(true, true);
        getter.GetMultiple(1, 0);
        if (getter.CommandResult() != Result.Success)
            return getter.CommandResult();

        var objectIds = getter.Objects()
            .Select(reference => reference.ObjectId)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        if (objectIds.Count == 0)
            return Result.Cancel;

        var materialLibrary = AssemblyManagerPlugin.Instance.Services.MaterialLibrary();
        if (materialLibrary.GetMaterials(doc).Count == 0)
        {
            RhinoApp.WriteLine("No material stock shapes are available. Add materials with MaterialLibrary first.");
            return Result.Cancel;
        }

        var dialog = new MaterialSelectionDialog(doc, materialLibrary);
        var accepted = dialog.ShowModal(RhinoEtoApp.MainWindow);
        if (!accepted || dialog.SelectedMaterial is null)
            return Result.Cancel;

        try
        {
            var count = materialLibrary.AssignMaterialToObjects(doc, objectIds, dialog.SelectedMaterial);
            RhinoApp.WriteLine("Assigned {0} to {1} object(s).", dialog.SelectedMaterial.Name, count);
            return count > 0 ? Result.Success : Result.Nothing;
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine("Assign materials failed: {0}", ex.Message);
            return Result.Failure;
        }
    }
}

public sealed class AssignMaterialToPartCommand : Command
{
    public override string EnglishName => "AssignMaterialToPart";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var assemblyName = CommandPickers.PickAssembly(doc, "Assembly containing part");
        if (assemblyName is null)
            return Result.Cancel;

        var partName = CommandPickers.PickPart(doc, assemblyName, "Part to assign material");
        if (partName is null)
            return Result.Cancel;

        var material = CommandPickers.PickMaterial(doc, "Material to assign");
        if (material is null)
            return Result.Cancel;

        try
        {
            AssemblyManagerPlugin.Instance.Services.MaterialLibrary().AssignMaterialToPart(doc, assemblyName, partName, material.Id);
            RhinoApp.WriteLine("Assigned {0} to {1} in {2}.", material.Name, partName, assemblyName);
            return Result.Success;
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine("Assign material failed: {0}", ex.Message);
            return Result.Failure;
        }
    }
}

public sealed class EstimateMaterialsCommand : Command
{
    public override string EnglishName => "EstimateMaterials";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var assemblyName = CommandPickers.PickAssembly(doc, "Assembly to estimate materials");
        if (assemblyName is null)
            return Result.Cancel;

        try
        {
            var report = AssemblyManagerPlugin.Instance.Services.NestingEstimate().EstimateMaterials(doc, assemblyName);
            foreach (var line in report.Lines)
            {
                RhinoApp.WriteLine(
                    $"{line.BaseMaterialName} | {line.ShapeName}: {line.EstimatedSheetCount} sheet(s), stock {line.SheetWidth:0.###} x {line.SheetHeight:0.###}, total part area {line.TotalPartArea:0.###}");
            }

            if (report.UnaccountedObjects.Count > 0)
                RhinoApp.WriteLine("Material estimate has {0} unaccounted part type(s).", report.UnaccountedObjects.Count);

            return Result.Success;
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine("Estimate materials failed: {0}", ex.Message);
            return Result.Failure;
        }
    }
}

public sealed class PlaceMaterialEstimateCommand : Command
{
    public override string EnglishName => "PlaceMaterialEstimate";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        if (doc.ActiveSpace != ActiveSpace.PageSpace)
        {
            RhinoApp.WriteLine("PlaceMaterialEstimate must be run from layout/page space.");
            return Result.Failure;
        }

        var assemblyName = CommandPickers.PickAssembly(doc, "Assembly for material estimate table");
        if (assemblyName is null)
            return Result.Cancel;

        var pointGetter = new GetPoint();
        pointGetter.SetCommandPrompt("Material estimate table insertion point");
        pointGetter.Get();
        if (pointGetter.CommandResult() != Result.Success)
            return pointGetter.CommandResult();

        try
        {
            var count = AssemblyManagerPlugin.Instance.Services.NestingEstimate().PlaceMaterialEstimateTable(doc, assemblyName, pointGetter.Point());
            RhinoApp.WriteLine("Placed material estimate table with {0} object(s).", count);
            return Result.Success;
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine("Place material estimate failed: {0}", ex.Message);
            return Result.Failure;
        }
    }
}

public sealed class ExportMaterialEstimateCommand : Command
{
    public override string EnglishName => "ExportMaterialEstimate";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var assemblyName = CommandPickers.PickAssembly(doc, "Assembly for material estimate export");
        if (assemblyName is null)
            return Result.Cancel;

        var dialog = new Eto.Forms.SaveFileDialog
        {
            Title = "Export Material Estimate",
            FileName = $"{assemblyName}_MaterialEstimate.csv",
            Filters =
            {
                new Eto.Forms.FileFilter("CSV", ".csv"),
                new Eto.Forms.FileFilter("JSON", ".json")
            }
        };

        if (dialog.ShowDialog(RhinoEtoApp.MainWindow) != Eto.Forms.DialogResult.Ok)
            return Result.Cancel;

        try
        {
            var service = AssemblyManagerPlugin.Instance.Services.NestingEstimate();
            var report = service.EstimateMaterials(doc, assemblyName);
            if (Path.GetExtension(dialog.FileName).Equals(".json", StringComparison.OrdinalIgnoreCase))
                service.ExportJson(report, dialog.FileName);
            else
                service.ExportCsv(report, dialog.FileName);

            RhinoApp.WriteLine("Exported material estimate to {0}", dialog.FileName);
            return Result.Success;
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine("Export material estimate failed: {0}", ex.Message);
            return Result.Failure;
        }
    }
}

public sealed class GenerateBomCommand : Command
{
    public override string EnglishName => "GenerateBom";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var assemblyName = CommandPickers.PickAssembly(doc, "Assembly for BOM");
        if (assemblyName is null)
            return Result.Cancel;

        try
        {
            AssemblyManagerPlugin.Instance.Services.NestingEstimate().EstimateMaterials(doc, assemblyName);
            var bom = AssemblyManagerPlugin.Instance.Services.Bom().GenerateBom(doc, assemblyName);
            RhinoApp.WriteLine("BOM for {0}: {1} line(s)", assemblyName, bom.Lines.Count);
            foreach (var line in bom.Lines)
                RhinoApp.WriteLine($"{line.Category} | {line.Item} | {line.Quantity:0.###} {line.Unit}");

            return Result.Success;
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine("Generate BOM failed: {0}", ex.Message);
            return Result.Failure;
        }
    }
}

public sealed class ExportBomCommand : Command
{
    public override string EnglishName => "ExportBom";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var assemblyName = CommandPickers.PickAssembly(doc, "Assembly for BOM export");
        if (assemblyName is null)
            return Result.Cancel;

        var dialog = new Eto.Forms.SaveFileDialog
        {
            Title = "Export BOM CSV",
            FileName = $"{assemblyName}_BOM.csv",
            Filters = { new Eto.Forms.FileFilter("CSV", ".csv") }
        };

        if (dialog.ShowDialog(Rhino.UI.RhinoEtoApp.MainWindow) != Eto.Forms.DialogResult.Ok)
            return Result.Cancel;

        try
        {
            AssemblyManagerPlugin.Instance.Services.NestingEstimate().EstimateMaterials(doc, assemblyName);
            var bom = AssemblyManagerPlugin.Instance.Services.Bom().GenerateBom(doc, assemblyName);
            AssemblyManagerPlugin.Instance.Services.Bom().ExportCsv(bom, dialog.FileName);
            RhinoApp.WriteLine("Exported BOM to {0}", dialog.FileName);
            return Result.Success;
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine("Export BOM failed: {0}", ex.Message);
            return Result.Failure;
        }
    }
}
