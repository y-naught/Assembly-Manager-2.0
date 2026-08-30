namespace AssemblyManagerPlugin.Core;

public static class AssemblyManagerConstants
{
    public const string StoreSection = "AssemblyManager";
    public const string StoreEntry = "Store";
    public const string PluginSettingsEntry = "PluginSettings";
    public const string ProjectInfoSection = "AssemblyManager.ProjectInfo";
    public const string ActionHistorySection = "AssemblyManager.ActionHistory";

    public const string AssemblyManagerRootLayer = "ASSEMBLY MANAGER";
    public const string OriginalAssembliesRootLayer = AssemblyManagerRootLayer + "::ORIGINAL ASSEMBLIES";
    public const string CopiedComponentsRootLayer = AssemblyManagerRootLayer + "::COPIED COMPONENTS";
    public const string PartsRootLayer = AssemblyManagerRootLayer + "::PARTS";
    public const string LegacyShopRootLayer = "SHOP";
    public const string LegacyDrawingsRootLayer = "DRAWINGS";
    public const string LegacyCamRootLayer = "CAM";
    public const string HardwareRootLayer = "HARDWARE";
    public const string AnnotationRootLayer = "ANNO";

    // These values are persisted metadata roles, not Rhino layer names. Keep them stable so
    // reference refresh continues to work in documents created by earlier Gazelle versions.
    public const string GeneratedAssemblyReferenceRole = "SHOP";
    public const string GeneratedHardwareReferenceRole = "SHOP_HARDWARE";

    public const string ObjectRoleUserString = "AssemblyManager.Role";
    public const string HardwareRole = "Hardware";
    public const string HardwareIdentifierUserString = "AssemblyManager.HardwareIdentifier";
    public const string HardwareNameUserString = "AssemblyManager.HardwareName";
    public const string HardwareDescriptionUserString = "AssemblyManager.HardwareDescription";
    public const string HardwareSourcePathUserString = "AssemblyManager.HardwareSourcePath";
    public const string HardwareBlockDefinitionUserString = "AssemblyManager.HardwareBlockDefinition";
    public const string SourceObjectUserString = "AssemblyManager.SourceObject";
    public const string ReferenceIdUserString = "AssemblyManager.ReferenceId";
    public const string ReferenceTransformUserString = "AssemblyManager.ReferenceTransform";
    public const string MaterialIdUserString = "AssemblyManager.MaterialId";
    public const string MaterialNameUserString = "AssemblyManager.MaterialName";
    public const string MaterialBaseIdUserString = "AssemblyManager.MaterialBaseId";
    public const string MaterialBaseNameUserString = "AssemblyManager.MaterialBaseName";
    public const string MaterialShapeNameUserString = "AssemblyManager.MaterialShapeName";
    public const string MaterialShapeTypeUserString = "AssemblyManager.MaterialShapeType";
}
