using AssemblyManagerPlugin.Core;
using Rhino;
using Rhino.DocObjects;

namespace AssemblyManagerPlugin.Services;

public sealed record HardwareMetadataRecord(
    string Identifier,
    string Name,
    string Description,
    string SourcePath,
    string BlockDefinitionName);

public static class HardwareMetadata
{
    public static bool HasHardwareRole(ObjectAttributes attributes)
    {
        var role = attributes.GetUserString(AssemblyManagerConstants.ObjectRoleUserString);
        return string.Equals(role, AssemblyManagerConstants.HardwareRole, StringComparison.OrdinalIgnoreCase);
    }

    public static void Mark(ObjectAttributes attributes, HardwareMetadataRecord metadata)
    {
        attributes.SetUserString(AssemblyManagerConstants.ObjectRoleUserString, AssemblyManagerConstants.HardwareRole);
        attributes.SetUserString(AssemblyManagerConstants.HardwareIdentifierUserString, metadata.Identifier);
        attributes.SetUserString(AssemblyManagerConstants.HardwareNameUserString, metadata.Name);
        attributes.SetUserString(AssemblyManagerConstants.HardwareDescriptionUserString, metadata.Description);
        attributes.SetUserString(AssemblyManagerConstants.HardwareSourcePathUserString, metadata.SourcePath);
        attributes.SetUserString(AssemblyManagerConstants.HardwareBlockDefinitionUserString, metadata.BlockDefinitionName);
    }

    public static HardwareMetadataRecord FromObject(RhinoObject rhinoObject)
    {
        var definitionName = rhinoObject is InstanceObject instanceObject
            ? instanceObject.InstanceDefinition?.Name ?? string.Empty
            : string.Empty;
        return FromAttributes(rhinoObject.Attributes, rhinoObject.Name, definitionName);
    }

    public static HardwareMetadataRecord FromAttributes(ObjectAttributes attributes, string fallbackName, string blockDefinitionName = "")
    {
        var name = FirstNonEmpty(
            attributes.GetUserString(AssemblyManagerConstants.HardwareNameUserString),
            fallbackName,
            blockDefinitionName,
            "Hardware");
        var definitionName = FirstNonEmpty(
            attributes.GetUserString(AssemblyManagerConstants.HardwareBlockDefinitionUserString),
            blockDefinitionName,
            name);
        var identifier = FirstNonEmpty(
            attributes.GetUserString(AssemblyManagerConstants.HardwareIdentifierUserString),
            definitionName,
            name);

        return new HardwareMetadataRecord(
            identifier,
            name,
            attributes.GetUserString(AssemblyManagerConstants.HardwareDescriptionUserString) ?? string.Empty,
            attributes.GetUserString(AssemblyManagerConstants.HardwareSourcePathUserString) ?? string.Empty,
            definitionName);
    }

    public static bool TryGetFromObject(RhinoObject rhinoObject, out HardwareMetadataRecord metadata)
    {
        if (HasHardwareRole(rhinoObject.Attributes))
        {
            metadata = FromObject(rhinoObject);
            return true;
        }

        if (rhinoObject is InstanceObject instanceObject)
            return TryGetFromDefinition(instanceObject.InstanceDefinition, out metadata);

        metadata = Empty();
        return false;
    }

    public static bool TryGetFromDefinition(InstanceDefinition? definition, out HardwareMetadataRecord metadata)
    {
        if (definition is null)
        {
            metadata = Empty();
            return false;
        }

        foreach (var definitionObject in definition.GetObjects())
        {
            if (!HasHardwareRole(definitionObject.Attributes))
                continue;

            var fromDefinitionObject = FromAttributes(definitionObject.Attributes, definitionObject.Name, definition.Name);
            metadata = fromDefinitionObject with
            {
                BlockDefinitionName = FirstNonEmpty(fromDefinitionObject.BlockDefinitionName, definition.Name),
                Identifier = FirstNonEmpty(fromDefinitionObject.Identifier, definition.Name),
                Name = FirstNonEmpty(fromDefinitionObject.Name, definition.Name)
            };
            return true;
        }

        metadata = Empty();
        return false;
    }

    public static bool EnsureObjectMarked(RhinoDoc doc, RhinoObject rhinoObject, HardwareMetadataRecord metadata)
    {
        if (HasHardwareRole(rhinoObject.Attributes))
            return false;

        var attributes = rhinoObject.Attributes.Duplicate();
        Mark(attributes, metadata);
        if (string.IsNullOrWhiteSpace(attributes.Name))
            attributes.Name = metadata.Name;

        return doc.Objects.ModifyAttributes(rhinoObject, attributes, true);
    }

    private static HardwareMetadataRecord Empty()
    {
        return new HardwareMetadataRecord(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return string.Empty;
    }
}
