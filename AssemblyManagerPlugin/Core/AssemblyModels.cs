using Rhino.Geometry;

namespace AssemblyManagerPlugin.Core;

public sealed class AssemblyStore
{
    public int SchemaVersion { get; set; } = 1;
    public List<AssemblyRecord> Assemblies { get; set; } = new();
    public List<MaterialRecord> MaterialLibraryCache { get; set; } = new();
    public List<ActionHistoryEntry> ActionHistory { get; set; } = new();

    public AssemblyRecord? FindAssembly(string name)
    {
        return Assemblies.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class AssemblyRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string SourceDocumentId { get; set; } = string.Empty;
    public string PartPrefix { get; set; } = "P";
    public string ComponentPrefix { get; set; } = "C";
    public List<ComponentRecord> Components { get; set; } = new();
    public List<PartRecord> Parts { get; set; } = new();
    public List<HardwareRecord> Hardware { get; set; } = new();
    public List<GeometryReferenceRecord> GeometryReferences { get; set; } = new();
    public List<NestingEstimateRecord> NestingEstimates { get; set; } = new();
    public MaterialEstimateReportRecord? LastMaterialEstimate { get; set; }
    public BomRecord? LastBillOfMaterials { get; set; }
}

public sealed class ComponentRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int Quantity { get; set; } = 1;
    public string Fingerprint { get; set; } = string.Empty;
    public List<string> PartNames { get; set; } = new();
    public Dictionary<string, int> PartQuantities { get; set; } = new();
    public Dictionary<string, List<Guid>> RepresentativeObjectIdsByPartName { get; set; } = new();
    public List<Guid> ObjectIds { get; set; } = new();
    public List<string> InstanceGroupNames { get; set; } = new();
}

public sealed class PartRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string GeometryFingerprint { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public double MaterialThickness { get; set; }
    public string MaterialId { get; set; } = string.Empty;
    public List<Guid> SourceObjectIds { get; set; } = new();
    public List<Guid> GeneratedObjectIds { get; set; } = new();
    public List<Guid> CamObjectIds { get; set; } = new();
    public string ReferenceId { get; set; } = string.Empty;
}

public sealed class HardwareRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public string BlockDefinitionName { get; set; } = string.Empty;
    public Guid SourceObjectId { get; set; }
    public Guid BlockInstanceId { get; set; }
    public Guid GeneratedObjectId { get; set; }
    public string ComponentName { get; set; } = string.Empty;
    public string LayerName { get; set; } = string.Empty;
    public string MaterialId { get; set; } = string.Empty;
    public int Quantity { get; set; } = 1;
}

public sealed class MaterialRecord
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string BaseMaterialId { get; set; } = string.Empty;
    public string BaseMaterialName { get; set; } = string.Empty;
    public double DensityLbPerCubicInch { get; set; }
    public string ShapeId { get; set; } = string.Empty;
    public string ShapeName { get; set; } = string.Empty;
    public string ShapeType { get; set; } = string.Empty;
    public double Thickness { get; set; }
    public string Unit { get; set; } = "in";
    public double SheetWidth { get; set; } = 48.0;
    public double SheetHeight { get; set; } = 96.0;
    public double StockLength { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public double Diameter { get; set; }
    public double WallThickness { get; set; }
    public double NestingEfficiency { get; set; } = 0.8;
    public double PricePerUnit { get; set; }
    public string PriceUnit { get; set; } = string.Empty;
    public Dictionary<string, string> Properties { get; set; } = new();
}

public sealed class MaterialDefinitionRecord
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public double DensityLbPerCubicInch { get; set; }
    public Dictionary<string, string> Properties { get; set; } = new();
    public List<MaterialShapeRecord> Shapes { get; set; } = new();
}

public sealed class MaterialShapeRecord
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ShapeType { get; set; } = "sheetgood";
    public double Thickness { get; set; }
    public string Unit { get; set; } = "in";
    public double SheetWidth { get; set; }
    public double SheetHeight { get; set; }
    public double StockLength { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public double Diameter { get; set; }
    public double WallThickness { get; set; }
    public double NestingEfficiency { get; set; } = 0.8;
    public double PricePerUnit { get; set; }
    public string PriceUnit { get; set; } = string.Empty;
    public Dictionary<string, string> Properties { get; set; } = new();
}

public sealed class GeometryReferenceRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SourceObjectId { get; set; }
    public Guid TargetObjectId { get; set; }
    public string TargetRole { get; set; } = string.Empty;
    public string AssemblyName { get; set; } = string.Empty;
    public string ComponentName { get; set; } = string.Empty;
    public string PartName { get; set; } = string.Empty;
    public TransformRecord SourceToTargetTransform { get; set; } = TransformRecord.Identity();
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class TransformRecord
{
    public double[] Values { get; set; } =
    {
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1
    };

    public static TransformRecord Identity()
    {
        return FromTransform(Transform.Identity);
    }

    public static TransformRecord FromTransform(Transform transform)
    {
        return new TransformRecord
        {
            Values =
            {
                [0] = transform.M00,
                [1] = transform.M01,
                [2] = transform.M02,
                [3] = transform.M03,
                [4] = transform.M10,
                [5] = transform.M11,
                [6] = transform.M12,
                [7] = transform.M13,
                [8] = transform.M20,
                [9] = transform.M21,
                [10] = transform.M22,
                [11] = transform.M23,
                [12] = transform.M30,
                [13] = transform.M31,
                [14] = transform.M32,
                [15] = transform.M33
            }
        };
    }

    public Transform ToTransform()
    {
        if (Values.Count() != 16)
            return Transform.Identity;

        var transform = Transform.Identity;
        transform.M00 = Values[0];
        transform.M01 = Values[1];
        transform.M02 = Values[2];
        transform.M03 = Values[3];
        transform.M10 = Values[4];
        transform.M11 = Values[5];
        transform.M12 = Values[6];
        transform.M13 = Values[7];
        transform.M20 = Values[8];
        transform.M21 = Values[9];
        transform.M22 = Values[10];
        transform.M23 = Values[11];
        transform.M30 = Values[12];
        transform.M31 = Values[13];
        transform.M32 = Values[14];
        transform.M33 = Values[15];
        return transform;
    }
}

public sealed class NestingEstimateRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string AssemblyName { get; set; } = string.Empty;
    public string MaterialId { get; set; } = string.Empty;
    public string MaterialName { get; set; } = string.Empty;
    public double SheetWidth { get; set; }
    public double SheetHeight { get; set; }
    public double NestingEfficiency { get; set; }
    public double TotalPartArea { get; set; }
    public int EstimatedSheetCount { get; set; }
    public List<NestingPartRecord> Parts { get; set; } = new();
}

public sealed class NestingPartRecord
{
    public string PartName { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public double AreaEach { get; set; }
    public string MaterialId { get; set; } = string.Empty;
}

public sealed class MaterialEstimateReportRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string AssemblyName { get; set; } = string.Empty;
    public List<MaterialEstimateLineRecord> Lines { get; set; } = new();
    public List<MaterialEstimateUnaccountedRecord> UnaccountedObjects { get; set; } = new();
}

public sealed class MaterialEstimateLineRecord
{
    public string MaterialId { get; set; } = string.Empty;
    public string MaterialName { get; set; } = string.Empty;
    public string BaseMaterialId { get; set; } = string.Empty;
    public string BaseMaterialName { get; set; } = string.Empty;
    public string ShapeId { get; set; } = string.Empty;
    public string ShapeName { get; set; } = string.Empty;
    public string ShapeType { get; set; } = string.Empty;
    public double Thickness { get; set; }
    public string Unit { get; set; } = "in";
    public double SheetWidth { get; set; }
    public double SheetHeight { get; set; }
    public double NestingEfficiency { get; set; }
    public double TotalPartArea { get; set; }
    public int EstimatedSheetCount { get; set; }
    public double PricePerUnit { get; set; }
    public string PriceUnit { get; set; } = string.Empty;
    public double EstimatedCost { get; set; }
    public List<MaterialEstimatePartRecord> Parts { get; set; } = new();
}

public sealed class MaterialEstimatePartRecord
{
    public string PartName { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public double RequiredWidth { get; set; }
    public double RequiredHeight { get; set; }
    public double RequiredThickness { get; set; }
    public double AreaEach { get; set; }
    public string MaterialId { get; set; } = string.Empty;
}

public sealed class MaterialEstimateUnaccountedRecord
{
    public string PartName { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public string MaterialId { get; set; } = string.Empty;
    public string MaterialName { get; set; } = string.Empty;
    public double RequiredWidth { get; set; }
    public double RequiredHeight { get; set; }
    public double RequiredThickness { get; set; }
    public string Reason { get; set; } = string.Empty;
}

public sealed class BomRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string AssemblyName { get; set; } = string.Empty;
    public List<BomLineRecord> Lines { get; set; } = new();
}

public sealed class BomLineRecord
{
    public string Category { get; set; } = string.Empty;
    public string Item { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public double Quantity { get; set; }
    public string Unit { get; set; } = string.Empty;
    public string MaterialId { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
}

public sealed class ActionHistoryEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public string CommandName { get; set; } = string.Empty;
    public string AssemblyName { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public Dictionary<string, string> Data { get; set; } = new();
}

public sealed class CreateAssemblyOptions
{
    public string AssemblyName { get; set; } = string.Empty;
    public string PartPrefix { get; set; } = "P";
    public string ComponentPrefix { get; set; } = "C";
    public string Description { get; set; } = "Assembly generated by Assembly Manager.";
    public double TranslationMultiplier { get; set; } = 3.0;
    public bool SkipHardware { get; set; } = true;
}

public sealed class CreateAssemblyResult
{
    public AssemblyRecord Assembly { get; }
    public int SourcePartCount { get; }
    public List<string> Warnings { get; } = new();
    public int UniquePartCount => Assembly.Parts.Count;
    public int UniqueComponentCount => Assembly.Components.Count;
    public int HardwareCount => Assembly.Hardware.Count;

    public CreateAssemblyResult(AssemblyRecord assembly, int sourcePartCount, IEnumerable<string>? warnings = null)
    {
        Assembly = assembly;
        SourcePartCount = sourcePartCount;
        if (warnings is not null)
            Warnings.AddRange(warnings);
    }
}

public sealed class AssemblyRemovalResult
{
    public string AssemblyName { get; set; } = string.Empty;
    public int DeletedObjectCount { get; set; }
    public int DeletedLayerCount { get; set; }
    public int DeletedGroupCount { get; set; }
    public bool MetadataRemoved { get; set; }
}

public sealed class PartCandidate
{
    public Guid SourceObjectId { get; init; }
    public Guid GeneratedObjectId { get; set; }
    public string PartName { get; set; } = string.Empty;
    public string Fingerprint { get; init; } = string.Empty;
    public string FingerprintDebugInfo { get; init; } = string.Empty;
    public PartFingerprintDebugRecord? FingerprintDebug { get; init; }
    public string MaterialId { get; set; } = string.Empty;
    public int[] GroupIndices { get; init; } = Array.Empty<int>();
    public Point3d Centroid { get; init; }
    public Rhino.Geometry.GeometryBase Geometry { get; init; } = default!;
}

public sealed class PartFingerprintDebugRecord
{
    public string Hash { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public CategorizationToleranceRecord Tolerances { get; set; } = new();
    public FingerprintValueRecord Volume { get; set; } = new();
    public FingerprintValueRecord Area { get; set; } = new();
    public List<FingerprintValueRecord> Dimensions { get; set; } = new();
    public List<FingerprintValueRecord> EdgeLengths { get; set; } = new();
    public int ArrangementPointCount { get; set; }
    public List<FingerprintValueRecord> ArrangementDistances { get; set; } = new();
}

public sealed class CategorizationToleranceRecord
{
    public double Length { get; set; }
    public double Area { get; set; }
    public double Volume { get; set; }
    public double Arrangement { get; set; }
}

public sealed class FingerprintValueRecord
{
    public int Index { get; set; }
    public double Raw { get; set; }
    public string Token { get; set; } = string.Empty;
}

public sealed class ComponentCandidate
{
    public string TemporaryName { get; init; } = string.Empty;
    public int SourceGroupIndex { get; init; } = -1;
    public string SourceGroupName { get; init; } = string.Empty;
    public List<PartCandidate> Parts { get; } = new();
    public List<HardwareCandidate> Hardware { get; } = new();
    public List<Guid> GeneratedObjectIds { get; } = new();
    public string Fingerprint { get; set; } = string.Empty;
    public string GeneratedGroupName { get; set; } = string.Empty;
}

public sealed class HardwareCandidate
{
    public Guid SourceObjectId { get; init; }
    public Guid GeneratedObjectId { get; set; }
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string SourcePath { get; init; } = string.Empty;
    public string BlockDefinitionName { get; init; } = string.Empty;
    public string Identifier { get; init; } = string.Empty;
    public string LayerName { get; init; } = string.Empty;
    public string MaterialId { get; init; } = string.Empty;
    public int[] GroupIndices { get; init; } = Array.Empty<int>();
    public Point3d Centroid { get; init; }
    public Rhino.Geometry.GeometryBase Geometry { get; init; } = default!;
}
