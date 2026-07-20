using System.Globalization;
using System.Text;
using AssemblyManagerPlugin.Core;
using Rhino;

namespace AssemblyManagerPlugin.Services;

public sealed class BomService
{
    private readonly AssemblyRepository _repository;
    private readonly IActionHistorySink _history;

    public BomService(AssemblyRepository repository, IActionHistorySink history)
    {
        _repository = repository;
        _history = history;
    }

    public BomRecord GenerateBom(RhinoDoc doc, string assemblyName)
    {
        var store = _repository.Load(doc);
        var assembly = store.FindAssembly(assemblyName)
            ?? throw new InvalidOperationException($"Assembly '{assemblyName}' was not found.");

        var bom = new BomRecord { AssemblyName = assemblyName };

        if (assembly.LastMaterialEstimate is not null)
        {
            foreach (var line in assembly.LastMaterialEstimate.Lines)
            {
                bom.Lines.Add(new BomLineRecord
                {
                    Category = "SheetGood",
                    Item = $"{line.BaseMaterialName} - {line.ShapeName}",
                    Description = $"{line.SheetWidth:0.###} x {line.SheetHeight:0.###} x {line.Thickness:0.###} {line.Unit} sheet at {line.NestingEfficiency:P0} efficiency",
                    Quantity = line.EstimatedSheetCount,
                    Unit = "sheet",
                    MaterialId = line.MaterialId,
                    Source = "MaterialEstimate"
                });
            }
        }
        else
        {
            foreach (var estimate in assembly.NestingEstimates)
            {
                bom.Lines.Add(new BomLineRecord
                {
                    Category = "SheetGood",
                    Item = estimate.MaterialName,
                    Description = $"{estimate.SheetWidth:0.###} x {estimate.SheetHeight:0.###} sheet at {estimate.NestingEfficiency:P0} efficiency",
                    Quantity = estimate.EstimatedSheetCount,
                    Unit = "sheet",
                    MaterialId = estimate.MaterialId,
                    Source = "NestingEstimate"
                });
            }
        }

        foreach (var hardwareGroup in assembly.Hardware
            .GroupBy(HardwareBomKey)
            .OrderBy(group => group.Key.Item, StringComparer.OrdinalIgnoreCase))
        {
            var hardware = hardwareGroup.First();
            bom.Lines.Add(new BomLineRecord
            {
                Category = "Hardware",
                Item = string.IsNullOrWhiteSpace(hardware.BlockDefinitionName) ? hardware.Name : hardware.BlockDefinitionName,
                Description = string.IsNullOrWhiteSpace(hardware.Description) ? hardware.Name : hardware.Description,
                Quantity = hardwareGroup.Sum(item => Math.Max(1, item.Quantity)),
                Unit = "ea",
                MaterialId = hardware.MaterialId,
                Source = string.IsNullOrWhiteSpace(hardware.SourcePath) ? "Document" : hardware.SourcePath
            });
        }

        assembly.LastBillOfMaterials = bom;
        assembly.UpdatedAt = DateTimeOffset.UtcNow;
        _repository.Save(doc, store);
        _history.Record(doc, new ActionHistoryEntry
        {
            CommandName = "GenerateBom",
            AssemblyName = assemblyName,
            Summary = $"Generated BOM with {bom.Lines.Count} line(s)."
        });
        return bom;
    }

    public void ExportCsv(BomRecord bom, string filepath)
    {
        var builder = new StringBuilder();
        builder.AppendLine("category,item,description,quantity,unit,material_id,source");
        foreach (var line in bom.Lines)
        {
            builder.AppendLine(string.Join(",",
                Csv(line.Category),
                Csv(line.Item),
                Csv(line.Description),
                line.Quantity.ToString("0.###", CultureInfo.InvariantCulture),
                Csv(line.Unit),
                Csv(line.MaterialId),
                Csv(line.Source)));
        }

        File.WriteAllText(filepath, builder.ToString());
    }

    private static string Csv(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";

        return value;
    }

    private static (string Item, string Description, string MaterialId, string SourcePath) HardwareBomKey(HardwareRecord hardware)
    {
        var item = string.IsNullOrWhiteSpace(hardware.BlockDefinitionName) ? hardware.Name : hardware.BlockDefinitionName;
        var description = string.IsNullOrWhiteSpace(hardware.Description) ? hardware.Name : hardware.Description;
        return (item, description, hardware.MaterialId, hardware.SourcePath);
    }
}
