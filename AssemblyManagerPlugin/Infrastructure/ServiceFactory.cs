using AssemblyManagerPlugin.Geometry;
using AssemblyManagerPlugin.Services;

namespace AssemblyManagerPlugin.Infrastructure;

public sealed class ServiceFactory
{
    public static ServiceFactory Instance { get; } = new();

    public AssemblyRepository Repository { get; } = new();
    public LayerService Layers { get; } = new();
    public PluginSettingsService PluginSettings { get; } = new();
    public GeometryFingerprintService Fingerprints { get; }
    public AssemblyLineageService Lineage { get; } = new();
    public AssemblyCategorizationReconciliationService CategorizationReconciliation { get; }
    public IActionHistorySink History { get; }
    public ReferenceUpdateService ReferenceUpdates { get; }
    public FlatPartSynchronizationService FlatPartSynchronization { get; }
    public AssemblyLinkEventService LinkEvents { get; }
    public ComponentUpdateService ComponentUpdates { get; }
    public LinkedAssemblySafetyService LinkSafety { get; }

    private ServiceFactory()
    {
        History = new DocumentActionHistorySink(Repository);
        LinkSafety = new LinkedAssemblySafetyService(Repository, () => PluginSettings.EnableLinkedAssemblies);
        Fingerprints = new GeometryFingerprintService(PluginSettings);
        CategorizationReconciliation = new AssemblyCategorizationReconciliationService(
            Fingerprints,
            Layers,
            Lineage,
            PluginSettings);
        FlatPartSynchronization = new FlatPartSynchronizationService(
            Layers, Fingerprints, MaterialLibrary(), Lineage, PluginSettings);
        ComponentUpdates = new ComponentUpdateService(Repository, Layers, Fingerprints, Lineage,
            () => PluginSettings.Load().AssemblyManager.ColorizeParts);
        ReferenceUpdates = new ReferenceUpdateService(
            Repository,
            History,
            Lineage,
            Fingerprints,
            CategorizationReconciliation,
            FlatPartSynchronization,
            componentUpdates: ComponentUpdates,
            linkSafety: LinkSafety);
        LinkEvents = new AssemblyLinkEventService(Repository, ReferenceUpdates, Lineage, Fingerprints,
            () => PluginSettings.AutomaticallyPropagateChangesInAssembly, ComponentUpdates, LinkSafety);
        PluginSettings.LinkingPreferenceChanged += (_, _) => LinkEvents.RefreshLinkingPreference();
    }

    public AssemblyGenerationService AssemblyGeneration()
    {
        return new AssemblyGenerationService(Repository, Layers, Fingerprints, PluginSettings, History, Lineage, LinkSafety);
    }

    public LayPartsFlatService LayPartsFlat()
    {
        return new LayPartsFlatService(
            Repository,
            Layers,
            Fingerprints,
            MaterialLibrary(),
            PluginSettings,
            History,
            Lineage,
            ReferenceUpdates);
    }

    public ComponentDrawingService ComponentDrawing()
    {
        return new ComponentDrawingService(Repository, Layers, History, Lineage, LinkSafety);
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
        return ReferenceUpdates;
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

    public PlacedBomService PlacedBom()
    {
        return new PlacedBomService(Repository, Layers, NestingEstimate(), MaterialLibrary(), History);
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
