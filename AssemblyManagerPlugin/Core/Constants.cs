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
    public const string AssemblyIdUserString = "AssemblyManager.AssemblyId";
    public const string LinkSchemaVersionUserString = "AssemblyManager.LinkSchemaVersion";
    public const string LinkNodeIdUserString = "AssemblyManager.LinkNodeId";
    public const string LinkEdgeIdUserString = "AssemblyManager.LinkEdgeId";
    public const string LinkRoleUserString = "AssemblyManager.LinkRole";
    public const string SourceComponentInstanceIdUserString = "AssemblyManager.SourceComponentInstanceId";
    public const string MaterialIdUserString = "AssemblyManager.MaterialId";
    public const string MaterialNameUserString = "AssemblyManager.MaterialName";
    public const string MaterialBaseIdUserString = "AssemblyManager.MaterialBaseId";
    public const string MaterialBaseNameUserString = "AssemblyManager.MaterialBaseName";
    public const string MaterialShapeNameUserString = "AssemblyManager.MaterialShapeName";
    public const string MaterialShapeTypeUserString = "AssemblyManager.MaterialShapeType";
}

// These values are persisted in Rhino object attributes and document JSON. Keep them stable
// across releases; display labels can be changed independently in the UI.
public static class AssemblyLinkRoles
{
    public const string Source = "Source";
    public const string OriginalAssembly = "OriginalAssembly";
    public const string CopiedComponent = "CopiedComponent";
    public const string FlatPart = "FlatPart";
    public const string Hardware = "Hardware";
}

public static class AssemblyLinkStatuses
{
    public const string Active = "Active";
    public const string Open = "Open";
    public const string Missing = "Missing";
    public const string Deleted = "Deleted";
    public const string Unlinked = "Unlinked";
    public const string Conflict = "Conflict";
    public const string Resolved = "Resolved";
    public const string Ignored = "Ignored";
}

public static class AssemblyLinkMetadataKeys
{
    /// <summary>
    /// Marks a generated node whose edit could not be promoted safely. Safe one-to-one edits to
    /// ORIGINAL ASSEMBLIES are promoted automatically; other branches require Refresh References
    /// or later operator-directed conflict handling.
    /// </summary>
    public const string Quarantined = "Quarantined";
    public const string UserPlanRotationOverride = "UserPlanRotationOverride";
}

public static class AssemblyLinkRecipes
{
    public const string DirectCopy = "DirectCopy";
    public const string LayFlat = "LayFlat";
    public const string BlockDefinitionPart = "BlockDefinitionPart";
    public const string ManualRelink = "ManualRelink";
}

public static class AssemblyLinkConflictTypes
{
    public const string MissingObject = "MissingObject";
    public const string DuplicateIdentity = "DuplicateIdentity";
    public const string SourceDeleted = "SourceDeleted";
    public const string SourceSplit = "SourceSplit";
    public const string SourceAdded = "SourceAdded";
    public const string DerivedGeometryChanged = "DerivedGeometryChanged";
    public const string OriginalAssemblyPromotionUnresolved = "OriginalAssemblyPromotionUnresolved";
    public const string PartCategorizationChanged = "PartCategorizationChanged";
    public const string LegacyMigrationIncomplete = "LegacyMigrationIncomplete";
    public const string DerivedGeometryOutOfDate = "DerivedGeometryOutOfDate";
    public const string TransformUnresolved = "TransformUnresolved";
    public const string CanonicalUnitScaleUnresolved = "CanonicalUnitScaleUnresolved";
    public const string ComponentMembershipChanged = "ComponentMembershipChanged";
    public const string ComponentCategorizationChanged = "ComponentCategorizationChanged";
}
