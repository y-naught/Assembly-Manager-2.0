using AssemblyManagerPlugin.Core;
using Rhino.DocObjects;

namespace AssemblyManagerPlugin.Services;

public static class MaterialAssignment
{
    public static void Set(ObjectAttributes attributes, MaterialDefinitionRecord material)
    {
        attributes.SetUserString(AssemblyManagerConstants.MaterialIdUserString, material.Id);
        attributes.SetUserString(AssemblyManagerConstants.MaterialNameUserString, material.Name);
        attributes.SetUserString(AssemblyManagerConstants.MaterialBaseIdUserString, material.Id);
        attributes.SetUserString(AssemblyManagerConstants.MaterialBaseNameUserString, material.Name);
        attributes.SetUserString(AssemblyManagerConstants.MaterialShapeNameUserString, string.Empty);
        attributes.SetUserString(AssemblyManagerConstants.MaterialShapeTypeUserString, string.Empty);
    }

    public static void Set(ObjectAttributes attributes, MaterialRecord material)
    {
        attributes.SetUserString(AssemblyManagerConstants.MaterialIdUserString, material.Id);
        attributes.SetUserString(AssemblyManagerConstants.MaterialNameUserString, material.Name);
        attributes.SetUserString(AssemblyManagerConstants.MaterialBaseIdUserString, material.BaseMaterialId);
        attributes.SetUserString(AssemblyManagerConstants.MaterialBaseNameUserString, material.BaseMaterialName);
        attributes.SetUserString(AssemblyManagerConstants.MaterialShapeNameUserString, material.ShapeName);
        attributes.SetUserString(AssemblyManagerConstants.MaterialShapeTypeUserString, material.ShapeType);
    }

    public static string GetMaterialId(ObjectAttributes attributes)
    {
        return attributes.GetUserString(AssemblyManagerConstants.MaterialIdUserString) ?? string.Empty;
    }

    public static string GetBaseMaterialId(ObjectAttributes attributes)
    {
        return attributes.GetUserString(AssemblyManagerConstants.MaterialBaseIdUserString) ?? string.Empty;
    }

    public static string GetBaseMaterialName(ObjectAttributes attributes)
    {
        return attributes.GetUserString(AssemblyManagerConstants.MaterialBaseNameUserString) ?? string.Empty;
    }

    public static string GetMaterialName(ObjectAttributes attributes)
    {
        return attributes.GetUserString(AssemblyManagerConstants.MaterialNameUserString) ?? string.Empty;
    }

    public static string GetCategorizationMaterialId(ObjectAttributes attributes)
    {
        var baseMaterialId = GetBaseMaterialId(attributes);
        if (!string.IsNullOrWhiteSpace(baseMaterialId))
            return NormalizeMaterialIdForCategory(baseMaterialId);

        var materialId = GetMaterialId(attributes);
        var parsedBaseId = TryParseLegacyAssignmentBaseId(materialId);
        return NormalizeMaterialIdForCategory(string.IsNullOrWhiteSpace(parsedBaseId) ? materialId : parsedBaseId);
    }

    public static string NormalizeMaterialIdForCategory(string materialId)
    {
        return string.IsNullOrWhiteSpace(materialId)
            ? string.Empty
            : materialId.Trim().ToUpperInvariant();
    }

    public static void NormalizeToParentMaterial(ObjectAttributes attributes)
    {
        var baseMaterialId = GetCategorizationMaterialId(attributes);
        if (string.IsNullOrWhiteSpace(baseMaterialId))
            return;

        var baseMaterialName = GetBaseMaterialName(attributes);
        if (string.IsNullOrWhiteSpace(baseMaterialName))
            baseMaterialName = GetMaterialName(attributes);

        attributes.SetUserString(AssemblyManagerConstants.MaterialIdUserString, baseMaterialId);
        attributes.SetUserString(AssemblyManagerConstants.MaterialNameUserString, baseMaterialName);
        attributes.SetUserString(AssemblyManagerConstants.MaterialBaseIdUserString, baseMaterialId);
        attributes.SetUserString(AssemblyManagerConstants.MaterialBaseNameUserString, baseMaterialName);
        attributes.SetUserString(AssemblyManagerConstants.MaterialShapeNameUserString, string.Empty);
        attributes.SetUserString(AssemblyManagerConstants.MaterialShapeTypeUserString, string.Empty);
    }

    public static string GetDisplayName(ObjectAttributes attributes)
    {
        var name = GetMaterialName(attributes);
        if (!string.IsNullOrWhiteSpace(name))
            return name;

        return GetMaterialId(attributes);
    }

    public static bool IsSheetLike(string shapeType)
    {
        return shapeType.Contains("sheet", StringComparison.OrdinalIgnoreCase)
            || shapeType.Contains("plate", StringComparison.OrdinalIgnoreCase)
            || shapeType.Contains("panel", StringComparison.OrdinalIgnoreCase);
    }

    private static string TryParseLegacyAssignmentBaseId(string materialId)
    {
        if (string.IsNullOrWhiteSpace(materialId) || !materialId.StartsWith("AMMAT|", StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        var parts = materialId.Split('|');
        return parts.Length > 1 ? parts[1] : string.Empty;
    }
}
