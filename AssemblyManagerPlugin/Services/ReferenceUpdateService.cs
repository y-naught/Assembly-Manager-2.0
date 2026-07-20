using System.Globalization;
using AssemblyManagerPlugin.Core;
using Rhino;
using Rhino.Geometry;

namespace AssemblyManagerPlugin.Services;

public sealed class ReferenceUpdateService
{
    private readonly AssemblyRepository _repository;
    private readonly IActionHistorySink _history;

    public ReferenceUpdateService(AssemblyRepository repository, IActionHistorySink history)
    {
        _repository = repository;
        _history = history;
    }

    public int RefreshAssemblyReferences(RhinoDoc doc, string assemblyName)
    {
        var store = _repository.Load(doc);
        var assembly = store.FindAssembly(assemblyName)
            ?? throw new InvalidOperationException($"Assembly '{assemblyName}' was not found.");

        var refreshed = 0;
        foreach (var reference in assembly.GeometryReferences.Where(r => r.TargetRole == "SHOP"))
        {
            var source = doc.Objects.FindId(reference.SourceObjectId);
            var target = doc.Objects.FindId(reference.TargetObjectId);
            if (source is null || target is null)
                continue;

            var geometry = source.Geometry.Duplicate();
            geometry.Transform(reference.SourceToTargetTransform.ToTransform());
            if (ReplaceGeometry(doc, reference.TargetObjectId, geometry))
            {
                reference.UpdatedAt = DateTimeOffset.UtcNow;
                refreshed++;
            }
        }

        assembly.UpdatedAt = DateTimeOffset.UtcNow;
        _repository.Save(doc, store);
        _history.Record(doc, new ActionHistoryEntry
        {
            CommandName = "RefreshAssemblyReferences",
            AssemblyName = assemblyName,
            Summary = $"Refreshed {refreshed} generated SHOP object(s) from source geometry."
        });
        doc.Views.Redraw();
        return refreshed;
    }

    public static void AttachReferenceUserStrings(Rhino.DocObjects.ObjectAttributes attributes, Guid sourceObjectId, Transform sourceToTarget)
    {
        attributes.SetUserString(AssemblyManagerConstants.SourceObjectUserString, sourceObjectId.ToString());
        attributes.SetUserString(AssemblyManagerConstants.ReferenceIdUserString, Guid.NewGuid().ToString());
        attributes.SetUserString(AssemblyManagerConstants.ReferenceTransformUserString, SerializeTransform(sourceToTarget));
    }

    private static bool ReplaceGeometry(RhinoDoc doc, Guid objectId, GeometryBase geometry)
    {
        return geometry switch
        {
            Brep brep => doc.Objects.Replace(objectId, brep),
            Curve curve => doc.Objects.Replace(objectId, curve),
            Mesh mesh => doc.Objects.Replace(objectId, mesh),
            Extrusion extrusion => doc.Objects.Replace(objectId, extrusion),
            Surface surface => doc.Objects.Replace(objectId, surface),
            _ => false
        };
    }

    private static string SerializeTransform(Transform transform)
    {
        var values = TransformRecord.FromTransform(transform).Values;
        return string.Join(",", values.Select(v => v.ToString("R", CultureInfo.InvariantCulture)));
    }
}
