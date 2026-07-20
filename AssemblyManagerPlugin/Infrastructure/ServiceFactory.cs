using AssemblyManagerPlugin.Geometry;
using AssemblyManagerPlugin.Services;

namespace AssemblyManagerPlugin.Infrastructure;

public sealed class ServiceFactory
{
    public AssemblyRepository Repository { get; } = new();
    public LayerService Layers { get; } = new();
    public PluginSettingsService PluginSettings { get; } = new();
    public GeometryFingerprintService Fingerprints { get; }

    public ServiceFactory()
    {
        Fingerprints = new GeometryFingerprintService(PluginSettings);
    }

    public IActionHistorySink History => new DocumentActionHistorySink(Repository);

    public AssemblyGenerationService AssemblyGeneration()
    {
        return new AssemblyGenerationService(Repository, Layers, Fingerprints, PluginSettings, History);
    }

    public LayPartsFlatService LayPartsFlat()
    {
        return new LayPartsFlatService(Repository, Layers, Fingerprints, MaterialLibrary(), PluginSettings, History);
    }

    public ComponentDrawingService ComponentDrawing()
    {
        return new ComponentDrawingService(Repository, Layers, History);
    }

    public HardwareImportService HardwareImport()
    {
        return new HardwareImportService(Repository, Layers, History);
    }

    public ProjectInfoService ProjectInfo()
    {
        return new ProjectInfoService();
    }

    public DetailLabelService DetailLabel()
    {
        return new DetailLabelService(Layers);
    }

    public DetailDimensionService DetailDimension()
    {
        return new DetailDimensionService();
    }

    public UtilityGeometryService UtilityGeometry()
    {
        return new UtilityGeometryService(Layers);
    }

    public ReferenceUpdateService ReferenceUpdate()
    {
        return new ReferenceUpdateService(Repository, History);
    }

    public MaterialLibraryService MaterialLibrary()
    {
        return new MaterialLibraryService(Repository, PluginSettings, History);
    }

    public NestingEstimateService NestingEstimate()
    {
        return new NestingEstimateService(Repository, Layers, Fingerprints, MaterialLibrary(), History);
    }

    public BomService Bom()
    {
        return new BomService(Repository, History);
    }

    public LayoutTemplateImportService LayoutTemplateImport()
    {
        return new LayoutTemplateImportService(PluginSettings, History);
    }

    public AssemblyRemovalService AssemblyRemoval()
    {
        return new AssemblyRemovalService(Repository, Layers, History);
    }
}
