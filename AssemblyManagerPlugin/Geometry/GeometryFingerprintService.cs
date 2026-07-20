using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AssemblyManagerPlugin.Core;
using AssemblyManagerPlugin.Services;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace AssemblyManagerPlugin.Geometry;

public sealed class GeometryFingerprintService
{
    private const double DefaultTolerance = 0.001;
    private const double DefaultLengthCategorizationTolerance = 0.001;
    private const double DefaultAreaCategorizationTolerance = 0.01;
    private const double DefaultVolumeCategorizationTolerance = 0.01;
    private const double DefaultArrangementCategorizationTolerance = 0.01;
    private readonly PluginSettingsService? _settings;

    public GeometryFingerprintService(PluginSettingsService? settings = null)
    {
        _settings = settings;
    }

    public bool TryCreatePartCandidate(RhinoObject rhinoObject, out PartCandidate candidate)
    {
        return TryCreatePartCandidate(rhinoObject, out candidate, out _);
    }

    public bool TryCreatePartCandidate(RhinoObject rhinoObject, out PartCandidate candidate, out string warning)
    {
        candidate = default!;
        warning = string.Empty;

        if (!TryDuplicateManufacturableBrep(rhinoObject.Geometry, DescribeObject(rhinoObject), out var brep, out warning))
            return false;

        return TryCreatePartCandidate(rhinoObject.Id, brep, rhinoObject.Attributes.GetGroupList() ?? Array.Empty<int>(), out candidate);
    }

    public bool TryCreatePartCandidate(
        Guid sourceObjectId,
        GeometryBase geometry,
        int[] groupIndices,
        out PartCandidate candidate,
        out string warning,
        string label = "")
    {
        candidate = default!;
        warning = string.Empty;
        if (!TryDuplicateManufacturableBrep(geometry, string.IsNullOrWhiteSpace(label) ? sourceObjectId.ToString() : label, out var brep, out warning))
            return false;

        return TryCreatePartCandidate(sourceObjectId, brep, groupIndices, out candidate);
    }

    private bool TryCreatePartCandidate(Guid sourceObjectId, Brep brep, int[] groupIndices, out PartCandidate candidate)
    {
        var fingerprint = CreatePartFingerprint(brep, out var debugInfo, out var debugRecord);
        candidate = new PartCandidate
        {
            SourceObjectId = sourceObjectId,
            Fingerprint = fingerprint,
            FingerprintDebugInfo = debugInfo,
            FingerprintDebug = debugRecord,
            GroupIndices = groupIndices,
            Centroid = GetCentroid(brep),
            Geometry = brep
        };
        return true;
    }

    public bool TryDuplicateManufacturableBrep(RhinoObject rhinoObject, out Brep brep, out string warning)
    {
        return TryDuplicateManufacturableBrep(rhinoObject.Geometry, DescribeObject(rhinoObject), out brep, out warning);
    }

    private bool TryDuplicateManufacturableBrep(GeometryBase geometry, string label, out Brep brep, out string warning)
    {
        brep = default!;
        warning = string.Empty;

        switch (geometry)
        {
            case Brep sourceBrep:
                brep = sourceBrep.DuplicateBrep();
                break;
            case Extrusion extrusion:
                brep = extrusion.ToBrep();
                if (brep is null)
                {
                    warning = $"Ignored {label}: extrusion could not be converted to a brep.";
                    return false;
                }
                break;
            case Curve:
                warning = $"Ignored {label}: curves are not valid assembly parts.";
                return false;
            case Point:
            case PointCloud:
                warning = $"Ignored {label}: points are not valid assembly parts.";
                return false;
            case Surface:
                warning = $"Ignored {label}: single surfaces are not valid assembly parts.";
                return false;
            default:
                warning = $"Ignored {label}: object type is not a closed polysurface or extrusion.";
                return false;
        }

        if (brep.Faces.Count <= 1)
        {
            warning = $"Ignored {label}: single surfaces are not valid assembly parts.";
            return false;
        }

        if (!brep.IsSolid)
        {
            warning = $"Skipped {label}: open polysurfaces are not supported for assembly part generation.";
            return false;
        }

        return true;
    }

    public string CreatePartFingerprint(Brep brep)
    {
        return CreatePartFingerprint(brep, out _);
    }

    public string CreatePartFingerprint(Brep brep, out string debugInfo)
    {
        return CreatePartFingerprint(brep, out debugInfo, out _);
    }

    public string CreatePartFingerprint(Brep brep, out string debugInfo, out PartFingerprintDebugRecord debugRecord)
    {
        var tolerances = GetCategorizationTolerances();
        var volume = VolumeMassProperties.Compute(brep)?.Volume ?? 0.0;
        var area = AreaMassProperties.Compute(brep)?.Area ?? 0.0;
        var dimensions = GetOrientedDimensions(brep).OrderBy(v => v).ToArray();
        var arrangement = CreateTopologyArrangementSignature(brep, tolerances.Arrangement);

        var edgeRecords = brep.Edges
            .Select((edge, index) =>
            {
                var raw = edge.GetLength();
                return new FingerprintValueRecord
                {
                    Index = index,
                    Raw = raw,
                    Token = RoundToken(raw, tolerances.Length)
                };
            })
            .OrderBy(record => record.Token, StringComparer.Ordinal)
            .ThenBy(record => record.Raw)
            .ToList();

        var volumeToken = RoundToken(volume, tolerances.Volume);
        var areaToken = RoundToken(area, tolerances.Area);
        var dimensionRecords = dimensions
            .Select((dimension, index) => new FingerprintValueRecord
            {
                Index = index,
                Raw = dimension,
                Token = RoundToken(dimension, tolerances.Length)
            })
            .ToList();

        var payload = string.Join("|",
            $"ltol:{RoundToken(tolerances.Length, DefaultTolerance)}",
            $"atol:{RoundToken(tolerances.Area, DefaultTolerance)}",
            $"vtol:{RoundToken(tolerances.Volume, DefaultTolerance)}",
            $"ptol:{RoundToken(tolerances.Arrangement, DefaultTolerance)}",
            $"v:{volumeToken}",
            $"a:{areaToken}",
            $"d:{string.Join(",", dimensionRecords.Select(record => record.Token))}",
            $"e:{string.Join(",", edgeRecords.Select(record => record.Token))}",
            $"pc:{arrangement.PointCount}",
            $"pd:{string.Join(",", arrangement.PairDistances.Select(record => record.Token))}");

        var hash = Hash(payload);
        debugRecord = new PartFingerprintDebugRecord
        {
            Hash = hash,
            Payload = payload,
            Tolerances = new CategorizationToleranceRecord
            {
                Length = tolerances.Length,
                Area = tolerances.Area,
                Volume = tolerances.Volume,
                Arrangement = tolerances.Arrangement
            },
            Volume = new FingerprintValueRecord { Raw = volume, Token = volumeToken },
            Area = new FingerprintValueRecord { Raw = area, Token = areaToken },
            Dimensions = dimensionRecords,
            EdgeLengths = edgeRecords,
            ArrangementPointCount = arrangement.PointCount,
            ArrangementDistances = arrangement.PairDistances
        };
        debugInfo = BuildPartFingerprintDebugInfo(
            debugRecord);
        return hash;
    }

    public bool AreEquivalentParts(PartCandidate first, PartCandidate second)
    {
        if (!HaveSameCategorizationMaterial(first, second))
            return false;

        if (first.FingerprintDebug is null || second.FingerprintDebug is null)
            return string.Equals(first.Fingerprint, second.Fingerprint, StringComparison.Ordinal);

        var tolerances = GetCategorizationTolerances();
        var firstDebug = first.FingerprintDebug;
        var secondDebug = second.FingerprintDebug;

        return AreWithinTolerance(firstDebug.Volume.Raw, secondDebug.Volume.Raw, tolerances.Volume)
            && AreWithinTolerance(firstDebug.Area.Raw, secondDebug.Area.Raw, tolerances.Area)
            && AreValueListsEquivalent(firstDebug.Dimensions, secondDebug.Dimensions, tolerances.Length)
            && AreValueListsEquivalent(firstDebug.EdgeLengths, secondDebug.EdgeLengths, tolerances.Length)
            && firstDebug.ArrangementPointCount == secondDebug.ArrangementPointCount
            && AreValueListsEquivalent(firstDebug.ArrangementDistances, secondDebug.ArrangementDistances, tolerances.Arrangement);
    }

    public bool AreEquivalentComponents(IReadOnlyList<PartCandidate> firstParts, IReadOnlyList<PartCandidate> secondParts)
    {
        if (firstParts.Count != secondParts.Count)
            return false;

        var firstCounts = CountComponentPartTokens(firstParts);
        var secondCounts = CountComponentPartTokens(secondParts);
        if (!HaveSameCounts(firstCounts, secondCounts))
            return false;

        var arrangementTolerance = GetCategorizationTolerances().Arrangement;
        return AreDistanceGroupsEquivalent(
                CreateComponentPairDistanceGroups(firstParts),
                CreateComponentPairDistanceGroups(secondParts),
                arrangementTolerance)
            && AreDistanceGroupsEquivalent(
                CreateComponentRadialDistanceGroups(firstParts),
                CreateComponentRadialDistanceGroups(secondParts),
                arrangementTolerance)
            && AreComponentStarsEquivalent(
                CreateComponentStarRecords(firstParts),
                CreateComponentStarRecords(secondParts),
                arrangementTolerance);
    }

    public string CreateComponentFingerprint(IReadOnlyList<PartCandidate> parts)
    {
        if (parts.Count == 0)
            return string.Empty;

        var arrangementTolerance = GetCategorizationTolerances().Arrangement;
        var partCounts = parts
            .GroupBy(PartCategoryToken, StringComparer.Ordinal)
            .Select(g => $"{g.Key}:{g.Count()}")
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToArray();

        var componentCenter = AverageCentroid(parts);
        var pairDistances = new List<string>();
        var radialDistances = new List<string>();
        var starSignatures = new List<string>();
        for (var i = 0; i < parts.Count; i++)
        {
            var anchor = parts[i];
            var anchorLabel = PartCategoryToken(anchor);
            var neighbors = new List<string>();
            radialDistances.Add($"{anchorLabel}:{RoundToken(anchor.Centroid.DistanceTo(componentCenter), arrangementTolerance)}");

            for (var j = i + 1; j < parts.Count; j++)
            {
                var a = anchor;
                var b = parts[j];
                var labels = new[] { PartCategoryToken(a), PartCategoryToken(b) }.OrderBy(v => v, StringComparer.Ordinal).ToArray();
                var distance = a.Centroid.DistanceTo(b.Centroid);
                pairDistances.Add($"{labels[0]}>{labels[1]}:{RoundToken(distance, arrangementTolerance)}");
            }

            for (var j = 0; j < parts.Count; j++)
            {
                if (i == j)
                    continue;

                var neighbor = parts[j];
                neighbors.Add($"{PartCategoryToken(neighbor)}:{RoundToken(anchor.Centroid.DistanceTo(neighbor.Centroid), arrangementTolerance)}");
            }

            neighbors.Sort(StringComparer.Ordinal);
            starSignatures.Add($"{anchorLabel}>[{string.Join(",", neighbors)}]");
        }

        pairDistances.Sort(StringComparer.Ordinal);
        radialDistances.Sort(StringComparer.Ordinal);
        starSignatures.Sort(StringComparer.Ordinal);
        var payload = string.Join("|",
            $"ptol={RoundToken(arrangementTolerance, DefaultTolerance)}",
            $"parts={string.Join(",", partCounts)}",
            $"pairs={string.Join(",", pairDistances)}",
            $"radii={string.Join(",", radialDistances)}",
            $"stars={string.Join(",", starSignatures)}");
        return Hash(payload);
    }

    private static string PartCategoryToken(PartCandidate part)
    {
        var materialId = string.IsNullOrWhiteSpace(part.MaterialId)
            ? "UNASSIGNED"
            : Services.MaterialAssignment.NormalizeMaterialIdForCategory(part.MaterialId);
        var identity = string.IsNullOrWhiteSpace(part.PartName)
            ? part.Fingerprint
            : NormalizeCategoryToken(part.PartName);
        return $"{identity}|material:{materialId}";
    }

    private static Point3d AverageCentroid(IReadOnlyList<PartCandidate> parts)
    {
        var x = 0.0;
        var y = 0.0;
        var z = 0.0;
        foreach (var part in parts)
        {
            x += part.Centroid.X;
            y += part.Centroid.Y;
            z += part.Centroid.Z;
        }

        return new Point3d(x / parts.Count, y / parts.Count, z / parts.Count);
    }

    public double GetMaterialThickness(Brep brep)
    {
        if (TryGetLargestPlanarFacePlane(brep, out var dominantPlane))
        {
            var normal = dominantPlane.Normal;
            normal.Unitize();

            var points = brep.Vertices.Select(vertex => vertex.Location).ToList();
            if (points.Count == 0)
                points.AddRange(brep.GetBoundingBox(true).GetCorners());

            if (points.Count > 0)
            {
                var projections = points.Select(point => Dot(point, normal)).ToArray();
                var thickness = projections.Max() - projections.Min();
                if (thickness > DefaultTolerance)
                    return Math.Round(thickness, 3, MidpointRounding.AwayFromZero);
            }
        }

        var orientedDimensions = GetOrientedDimensions(brep);
        return orientedDimensions.Length == 0
            ? 0.0
            : Math.Round(orientedDimensions.Min(), 3, MidpointRounding.AwayFromZero);
    }

    public Point3d GetCentroid(GeometryBase geometry)
    {
        if (geometry is Brep brep)
            return GetCentroid(brep);

        var bbox = geometry.GetBoundingBox(true);
        return bbox.IsValid ? bbox.Center : Point3d.Origin;
    }

    public Point3d GetCentroid(Brep brep)
    {
        var volume = VolumeMassProperties.Compute(brep);
        if (volume is not null)
            return volume.Centroid;

        var area = AreaMassProperties.Compute(brep);
        if (area is not null)
            return area.Centroid;

        var bbox = brep.GetBoundingBox(true);
        return bbox.IsValid ? bbox.Center : Point3d.Origin;
    }

    public bool TryGetLargestFacePlane(Brep brep, out Plane plane)
    {
        if (TryGetLargestPlanarFacePlane(brep, out plane))
            return true;

        plane = Plane.WorldXY;
        BrepFace? largestFace = null;
        var largestArea = double.MinValue;

        foreach (var face in brep.Faces)
        {
            var area = AreaMassProperties.Compute(face)?.Area ?? 0.0;
            if (area > largestArea)
            {
                largestArea = area;
                largestFace = face;
            }
        }

        if (largestFace is null)
            return false;

        if (largestFace.TryGetPlane(out plane, RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? DefaultTolerance))
            return true;

        var u = largestFace.Domain(0);
        var v = largestFace.Domain(1);
        return largestFace.FrameAt(u.Mid, v.Mid, out plane);
    }

    private bool TryGetLargestPlanarFacePlane(Brep brep, out Plane plane)
    {
        plane = Plane.WorldXY;
        var tolerance = RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? DefaultTolerance;
        var largestArea = double.MinValue;
        var found = false;

        foreach (var face in brep.Faces)
        {
            if (!face.TryGetPlane(out var facePlane, tolerance))
                continue;

            var area = AreaMassProperties.Compute(face)?.Area ?? 0.0;
            if (area <= largestArea)
                continue;

            largestArea = area;
            plane = facePlane;
            found = true;
        }

        return found;
    }

    public double[] GetOrientedDimensions(Brep brep)
    {
        if (!TryGetLargestFacePlane(brep, out var plane))
        {
            var worldBox = brep.GetBoundingBox(true);
            return DimensionsFromBoundingBox(worldBox);
        }

        var transform = Transform.PlaneToPlane(plane, Plane.WorldXY);
        var bbox = BoundingBox.Empty;
        foreach (var vertex in brep.Vertices)
        {
            var point = vertex.Location;
            point.Transform(transform);
            bbox.Union(point);
        }

        if (!bbox.IsValid)
        {
            bbox = brep.GetBoundingBox(transform);
        }

        return DimensionsFromBoundingBox(bbox);
    }

    public bool TryDuplicateBrep(RhinoObject rhinoObject, out Brep brep)
    {
        brep = default!;
        switch (rhinoObject.Geometry)
        {
            case Brep sourceBrep:
                brep = sourceBrep.DuplicateBrep();
                return true;
            case Extrusion extrusion:
                brep = extrusion.ToBrep();
                return brep is not null;
            case Surface surface:
                brep = surface.ToBrep();
                return brep is not null;
            default:
                return false;
        }
    }

    private static string DescribeObject(RhinoObject rhinoObject)
    {
        return string.IsNullOrWhiteSpace(rhinoObject.Name)
            ? rhinoObject.Id.ToString()
            : $"'{rhinoObject.Name}' ({rhinoObject.Id})";
    }

    public bool IsHardwareObject(RhinoObject rhinoObject)
    {
        if (HardwareMetadata.HasHardwareRole(rhinoObject.Attributes))
            return true;

        return rhinoObject is InstanceObject instanceObject
            && HardwareMetadata.TryGetFromDefinition(instanceObject.InstanceDefinition, out _);
    }

    private static double[] DimensionsFromBoundingBox(BoundingBox bbox)
    {
        if (!bbox.IsValid)
            return Array.Empty<double>();

        return new[]
        {
            Math.Abs(bbox.Max.X - bbox.Min.X),
            Math.Abs(bbox.Max.Y - bbox.Min.Y),
            Math.Abs(bbox.Max.Z - bbox.Min.Z)
        };
    }

    private static double Dot(Point3d point, Vector3d vector)
    {
        return point.X * vector.X + point.Y * vector.Y + point.Z * vector.Z;
    }

    private static TopologyArrangementSignature CreateTopologyArrangementSignature(Brep brep, double tolerance)
    {
        var points = new List<Point3d>();

        foreach (var edge in brep.Edges)
            AddUniquePoint(points, GetEdgeFeaturePoint(edge, tolerance), tolerance);

        var distances = new List<FingerprintValueRecord>();
        var pairIndex = 0;
        for (var i = 0; i < points.Count; i++)
        {
            for (var j = i + 1; j < points.Count; j++)
            {
                var raw = points[i].DistanceTo(points[j]);
                if (raw <= Math.Max(tolerance * 0.5, RhinoMath.ZeroTolerance))
                    continue;

                distances.Add(new FingerprintValueRecord
                {
                    Index = pairIndex,
                    Raw = raw,
                    Token = RoundToken(raw, tolerance)
                });
                pairIndex++;
            }
        }

        distances = distances
            .OrderBy(record => record.Token, StringComparer.Ordinal)
            .ThenBy(record => record.Raw)
            .ToList();

        return new TopologyArrangementSignature(points.Count, distances);
    }

    private static Point3d GetEdgeFeaturePoint(BrepEdge edge, double tolerance)
    {
        if (TryGetClosedPlanarCurveCentroid(edge, tolerance, out var centroid))
            return centroid;

        return GetEdgeSampleCentroid(edge);
    }

    private static bool TryGetClosedPlanarCurveCentroid(BrepEdge edge, double tolerance, out Point3d centroid)
    {
        centroid = Point3d.Unset;
        if (!edge.IsClosed)
            return false;

        var curve = edge.DuplicateCurve();
        if (curve is null || !curve.IsClosed || !curve.IsPlanar(tolerance))
            return false;

        var properties = AreaMassProperties.Compute(curve);
        if (properties is null || !properties.Centroid.IsValid)
            return false;

        centroid = properties.Centroid;
        return true;
    }

    private static Point3d GetEdgeSampleCentroid(BrepEdge edge)
    {
        const int sampleCount = 16;
        var samplePoints = new List<Point3d>();
        for (var i = 0; i < sampleCount; i++)
        {
            var fraction = edge.IsClosed
                ? (double)i / sampleCount
                : (i + 0.5) / sampleCount;
            if (edge.NormalizedLengthParameter(fraction, out var parameter))
                samplePoints.Add(edge.PointAt(parameter));
        }

        if (samplePoints.Count == 0)
        {
            samplePoints.Add(edge.PointAtStart);
            samplePoints.Add(edge.PointAt(edge.Domain.Mid));
            samplePoints.Add(edge.PointAtEnd);
        }

        var x = 0.0;
        var y = 0.0;
        var z = 0.0;
        foreach (var point in samplePoints)
        {
            x += point.X;
            y += point.Y;
            z += point.Z;
        }

        var count = samplePoints.Count;
        return new Point3d(x / count, y / count, z / count);
    }

    private static void AddUniquePoint(List<Point3d> points, Point3d point, double tolerance)
    {
        if (!point.IsValid)
            return;

        var clusterTolerance = Math.Max(tolerance * 0.5, RhinoMath.ZeroTolerance);
        if (points.Any(existing => existing.DistanceTo(point) <= clusterTolerance))
            return;

        points.Add(point);
    }

    private (double Length, double Area, double Volume, double Arrangement) GetCategorizationTolerances()
    {
        var documentTolerance = RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? DefaultTolerance;
        var lengthFallback = Math.Max(documentTolerance, DefaultLengthCategorizationTolerance);
        var areaFallback = Math.Max(documentTolerance, DefaultAreaCategorizationTolerance);
        var volumeFallback = Math.Max(documentTolerance, DefaultVolumeCategorizationTolerance);
        var arrangementFallback = Math.Max(documentTolerance, DefaultArrangementCategorizationTolerance);
        var settings = _settings?.Load().AssemblyManager;

        return (
            SanitizeTolerance(settings?.CategorizationLengthTolerance ?? lengthFallback, lengthFallback),
            SanitizeTolerance(settings?.CategorizationAreaTolerance ?? areaFallback, areaFallback),
            SanitizeTolerance(settings?.CategorizationVolumeTolerance ?? volumeFallback, volumeFallback),
            SanitizeTolerance(settings?.CategorizationArrangementTolerance ?? arrangementFallback, arrangementFallback));
    }

    private static double SanitizeTolerance(double value, double fallback)
    {
        return value > 0.0 && !double.IsNaN(value) && !double.IsInfinity(value)
            ? value
            : fallback;
    }

    private static bool HaveSameCategorizationMaterial(PartCandidate first, PartCandidate second)
    {
        return string.Equals(
            NormalizeMaterialForComparison(first.MaterialId),
            NormalizeMaterialForComparison(second.MaterialId),
            StringComparison.Ordinal);
    }

    private static string NormalizeMaterialForComparison(string materialId)
    {
        return string.IsNullOrWhiteSpace(materialId)
            ? "UNASSIGNED"
            : Services.MaterialAssignment.NormalizeMaterialIdForCategory(materialId);
    }

    private static bool AreWithinTolerance(double first, double second, double tolerance)
    {
        return Math.Abs(first - second) <= tolerance + RhinoMath.ZeroTolerance;
    }

    private static bool AreValueListsEquivalent(
        IReadOnlyList<FingerprintValueRecord> first,
        IReadOnlyList<FingerprintValueRecord> second,
        double tolerance)
    {
        if (first.Count != second.Count)
            return false;

        var firstValues = first.Select(record => record.Raw).OrderBy(value => value).ToArray();
        var secondValues = second.Select(record => record.Raw).OrderBy(value => value).ToArray();
        for (var i = 0; i < firstValues.Length; i++)
        {
            if (!AreWithinTolerance(firstValues[i], secondValues[i], tolerance))
                return false;
        }

        return true;
    }

    private static Dictionary<string, int> CountComponentPartTokens(IReadOnlyList<PartCandidate> parts)
    {
        return parts
            .GroupBy(PartCategoryToken, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
    }

    private static bool HaveSameCounts(IReadOnlyDictionary<string, int> first, IReadOnlyDictionary<string, int> second)
    {
        if (first.Count != second.Count)
            return false;

        foreach (var entry in first)
        {
            if (!second.TryGetValue(entry.Key, out var count) || count != entry.Value)
                return false;
        }

        return true;
    }

    private static Dictionary<string, List<double>> CreateComponentPairDistanceGroups(IReadOnlyList<PartCandidate> parts)
    {
        var groups = new Dictionary<string, List<double>>(StringComparer.Ordinal);
        for (var i = 0; i < parts.Count; i++)
        {
            for (var j = i + 1; j < parts.Count; j++)
            {
                var labels = new[] { PartCategoryToken(parts[i]), PartCategoryToken(parts[j]) }
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray();
                AddDistance(groups, $"{labels[0]}>{labels[1]}", parts[i].Centroid.DistanceTo(parts[j].Centroid));
            }
        }

        return groups;
    }

    private static Dictionary<string, List<double>> CreateComponentRadialDistanceGroups(IReadOnlyList<PartCandidate> parts)
    {
        var groups = new Dictionary<string, List<double>>(StringComparer.Ordinal);
        if (parts.Count == 0)
            return groups;

        var center = AverageCentroid(parts);
        foreach (var part in parts)
            AddDistance(groups, PartCategoryToken(part), part.Centroid.DistanceTo(center));

        return groups;
    }

    private static void AddDistance(Dictionary<string, List<double>> groups, string key, double distance)
    {
        if (!groups.TryGetValue(key, out var distances))
        {
            distances = new List<double>();
            groups[key] = distances;
        }

        distances.Add(distance);
    }

    private static bool AreDistanceGroupsEquivalent(
        IReadOnlyDictionary<string, List<double>> first,
        IReadOnlyDictionary<string, List<double>> second,
        double tolerance)
    {
        if (first.Count != second.Count)
            return false;

        foreach (var entry in first)
        {
            if (!second.TryGetValue(entry.Key, out var secondDistances))
                return false;

            if (!AreDistanceListsEquivalent(entry.Value, secondDistances, tolerance))
                return false;
        }

        return true;
    }

    private static bool AreDistanceListsEquivalent(
        IReadOnlyList<double> first,
        IReadOnlyList<double> second,
        double tolerance)
    {
        if (first.Count != second.Count)
            return false;

        var firstSorted = first.OrderBy(value => value).ToArray();
        var secondSorted = second.OrderBy(value => value).ToArray();
        for (var i = 0; i < firstSorted.Length; i++)
        {
            if (!AreWithinTolerance(firstSorted[i], secondSorted[i], tolerance))
                return false;
        }

        return true;
    }

    private static List<ComponentStarRecord> CreateComponentStarRecords(IReadOnlyList<PartCandidate> parts)
    {
        var center = parts.Count == 0 ? Point3d.Origin : AverageCentroid(parts);
        var records = new List<ComponentStarRecord>();
        for (var i = 0; i < parts.Count; i++)
        {
            var anchor = parts[i];
            var neighbors = new List<ComponentNeighborDistanceRecord>();
            for (var j = 0; j < parts.Count; j++)
            {
                if (i == j)
                    continue;

                neighbors.Add(new ComponentNeighborDistanceRecord(
                    PartCategoryToken(parts[j]),
                    anchor.Centroid.DistanceTo(parts[j].Centroid)));
            }

            neighbors = neighbors
                .OrderBy(neighbor => neighbor.Token, StringComparer.Ordinal)
                .ThenBy(neighbor => neighbor.Distance)
                .ToList();

            records.Add(new ComponentStarRecord(
                PartCategoryToken(anchor),
                anchor.Centroid.DistanceTo(center),
                neighbors));
        }

        return records;
    }

    private static bool AreComponentStarsEquivalent(
        IReadOnlyList<ComponentStarRecord> first,
        IReadOnlyList<ComponentStarRecord> second,
        double tolerance)
    {
        var firstGroups = first.GroupBy(record => record.AnchorToken, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        var secondGroups = second.GroupBy(record => record.AnchorToken, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

        if (firstGroups.Count != secondGroups.Count)
            return false;

        foreach (var entry in firstGroups)
        {
            if (!secondGroups.TryGetValue(entry.Key, out var secondRecords))
                return false;

            if (!AreStarRecordListsEquivalent(entry.Value, secondRecords, tolerance))
                return false;
        }

        return true;
    }

    private static bool AreStarRecordListsEquivalent(
        IReadOnlyList<ComponentStarRecord> first,
        IReadOnlyList<ComponentStarRecord> second,
        double tolerance)
    {
        if (first.Count != second.Count)
            return false;

        var unmatched = second.ToList();
        foreach (var firstRecord in first.OrderBy(StarSortKey, StringComparer.Ordinal))
        {
            var matchIndex = unmatched.FindIndex(secondRecord => AreStarRecordsEquivalent(firstRecord, secondRecord, tolerance));
            if (matchIndex < 0)
                return false;

            unmatched.RemoveAt(matchIndex);
        }

        return true;
    }

    private static bool AreStarRecordsEquivalent(ComponentStarRecord first, ComponentStarRecord second, double tolerance)
    {
        if (!string.Equals(first.AnchorToken, second.AnchorToken, StringComparison.Ordinal)
            || !AreWithinTolerance(first.RadialDistance, second.RadialDistance, tolerance)
            || first.Neighbors.Count != second.Neighbors.Count)
            return false;

        for (var i = 0; i < first.Neighbors.Count; i++)
        {
            var firstNeighbor = first.Neighbors[i];
            var secondNeighbor = second.Neighbors[i];
            if (!string.Equals(firstNeighbor.Token, secondNeighbor.Token, StringComparison.Ordinal)
                || !AreWithinTolerance(firstNeighbor.Distance, secondNeighbor.Distance, tolerance))
                return false;
        }

        return true;
    }

    private static string StarSortKey(ComponentStarRecord record)
    {
        return string.Join("|",
            record.AnchorToken,
            FormatDouble(record.RadialDistance),
            string.Join(",", record.Neighbors.Select(neighbor => $"{neighbor.Token}:{FormatDouble(neighbor.Distance)}")));
    }

    private static string NormalizeCategoryToken(string value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToUpperInvariant();
    }

    private static string RoundToken(double value)
    {
        return RoundToken(value, DefaultTolerance);
    }

    private static string RoundToken(double value, double tolerance)
    {
        if (tolerance <= 0.0 || double.IsNaN(tolerance) || double.IsInfinity(tolerance))
            tolerance = DefaultTolerance;

        var quantized = Math.Round(value / tolerance, MidpointRounding.AwayFromZero) * tolerance;
        if (Math.Abs(quantized) < tolerance * 0.5)
            quantized = 0.0;

        return quantized.ToString("0.#####", CultureInfo.InvariantCulture);
    }

    private static string BuildPartFingerprintDebugInfo(PartFingerprintDebugRecord record)
    {
        return string.Join(System.Environment.NewLine,
            $"hash={record.Hash}",
            $"lengthTolerance={FormatDouble(record.Tolerances.Length)} areaTolerance={FormatDouble(record.Tolerances.Area)} volumeTolerance={FormatDouble(record.Tolerances.Volume)} arrangementTolerance={FormatDouble(record.Tolerances.Arrangement)}",
            $"volumeRaw={FormatDouble(record.Volume.Raw)} volumeToken={record.Volume.Token}",
            $"areaRaw={FormatDouble(record.Area.Raw)} areaToken={record.Area.Token}",
            $"dimensions={string.Join(",", record.Dimensions.Select(value => value.Token))}",
            $"edgeCount={record.EdgeLengths.Count}",
            $"edgeLengthTokens={string.Join(",", record.EdgeLengths.Select(value => value.Token))}",
            $"arrangementPointCount={record.ArrangementPointCount} arrangementDistanceCount={record.ArrangementDistances.Count}",
            $"arrangementDistanceTokens={string.Join(",", record.ArrangementDistances.Select(value => value.Token))}",
            $"payload={record.Payload}");
    }

    private static string FormatDouble(double value)
    {
        return value.ToString("0.#####", CultureInfo.InvariantCulture);
    }

    private static string Hash(string payload)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(bytes)[..16];
    }

    private sealed class TopologyArrangementSignature
    {
        public TopologyArrangementSignature(int pointCount, List<FingerprintValueRecord> pairDistances)
        {
            PointCount = pointCount;
            PairDistances = pairDistances;
        }

        public int PointCount { get; }
        public List<FingerprintValueRecord> PairDistances { get; }
    }

    private sealed record ComponentNeighborDistanceRecord(string Token, double Distance);

    private sealed record ComponentStarRecord(string AnchorToken, double RadialDistance, List<ComponentNeighborDistanceRecord> Neighbors);
}
