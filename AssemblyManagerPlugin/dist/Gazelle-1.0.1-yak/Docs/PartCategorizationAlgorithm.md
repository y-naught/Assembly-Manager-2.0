# Gazelle Part And Component Categorization Algorithm

This document explains how the Assembly Manager workflow inside Gazelle decides which selected Rhino objects are the same part, and how those part categories roll up into component categories.

## Where the code lives

- `Geometry/GeometryFingerprintService.cs`: creates part and component fingerprints.
- `Services/AssemblyGenerationService.cs`: expands selected groups, filters valid parts, groups candidates, copies geometry, and writes assembly records.
- `Services/MaterialAssignment.cs`: normalizes assigned material ids before they are included in categorization.

## High-level flow

1. The create assembly command expands the user's selection to whole Rhino groups.
2. Objects marked as Gazelle hardware are passed through without part analysis.
3. Unmarked block instances are expanded into their definition geometry and categorized like normal parts.
4. Each remaining object is converted into a manufacturable Brep candidate.
5. Each candidate receives a geometry fingerprint.
6. The part category key is `geometry fingerprint + normalized material id`.
7. Matching category keys become one unique generated part.
8. Generated parts and passed-through hardware are grouped into component candidates based on their source Rhino group.
9. Components receive their own fingerprint based on part counts, hardware identifiers, and pairwise positions.
10. Matching component fingerprints become one unique generated component.

## Object filtering

The part candidate step accepts:

- Closed polysurfaces (`Brep` with more than one face and `IsSolid == true`)
- Extrusions that can be converted to closed Breps

The part candidate step rejects:

- Curves
- Points and point clouds
- Single surfaces
- Open polysurfaces
- Object types that cannot become a closed polysurface or extrusion

Open polysurfaces are skipped with a warning so they do not silently pollute part counts.

Imported hardware is recognized only by explicit Gazelle hardware metadata. Ordinary Rhino blocks are not automatically skipped. If a block is not marked as imported hardware, Gazelle expands the block instance and analyzes its closed polysurface/extrusion contents as normal parts.

## Part fingerprint

Each part candidate still receives a fingerprint payload for debugging and record keeping, but the hash is no longer the thing that decides equivalence. Gazelle now compares raw measured values directly against the user tolerances.

The comparison data currently contains:

- Volume
- Total surface area
- Three oriented bounding dimensions, sorted smallest to largest
- All Brep edge lengths, sorted
- A topology arrangement signature made from pairwise distances between one stable feature point per Brep edge

Sorting makes the comparison independent of Rhino object orientation and internal edge order.

The oriented dimensions are calculated by:

1. Finding the largest planar face when possible.
2. Orienting that face plane to World XY.
3. Measuring the transformed bounding box dimensions.
4. Sorting those dimensions before writing them to the payload.

This means a duplicated part can be moved or rotated and should still land in the same part category.

The topology arrangement signature is the part of the comparison that prevents different internal layouts from collapsing together. Gazelle collects one stable feature point per Brep edge. Closed planar edges, such as circular hole edges, use an area centroid when possible so Rhino curve seam placement does not affect the result. Other edges use a length-sampled centroid. It then deduplicates coincident feature points, calculates every pairwise point distance, and sorts those raw distances before comparison.

This is stronger than comparing only the standard deviation of edge start points. A standard deviation can detect that a point cloud changed, but many different point arrangements can share the same average spread. Pairwise distances preserve much more of the spatial relationship between outer corners, holes, slots, and other edge features while remaining independent of where the object sits in the Rhino model.

## Tolerance handling

Part categorization uses direct tolerance comparison instead of rounded bucket equality. If the absolute difference between two compared raw values is less than or equal to that value's tolerance, that value is treated as equivalent. The current linear tolerance is:

```text
Assembly Manager Settings > Assembly Manager > Length / Edge Tolerance
```

That value is used for dimensions and edge lengths. Area, volume, and arrangement distances have separate user-editable tolerances in the same settings window. The default arrangement tolerance is `0.01`, which keeps feature-position comparison from being overly brittle while still catching practical changes in hole, slot, or cutout placement.

The default length/edge tolerance is `0.001`. Older versions rounded measured values into tolerance-sized tokens and then grouped by the token hash. That could split two nearly identical parts when their raw values were close together but landed on opposite sides of a rounding boundary, such as values near `0.375`. Older versions also included face edge counts. Those choices could split parts when:

- Rhino returned slightly different mass properties for copied or transformed geometry.
- Equivalent geometry had minor face bookkeeping differences.
- Centroid distances differed by tiny document tolerance noise.

If categorization debug mode is enabled in settings, create assembly exports a JSON report after part numbers have been assigned. The report includes each candidate object's assigned part number, category key, material id, source/generated object ids, group data, centroid, raw and tokenized volume/area/dimensions/edge lengths/topology arrangement distances, the full unhashed payload, tolerance values, and final part category groups. The tokenized payload is useful for debugging, but the assigned part number now comes from the raw tolerance comparison.

Debug reports are written to:

```text
<Rhino document folder>/AssemblyManagerDebugReports/
```

If the Rhino document has not been saved yet, the fallback is:

```text
~/Documents/AssemblyManagerDebugReports/
```

## Material handling

Materials are included after geometry matching:

```text
part category = raw geometry comparison + normalized material id
```

If two objects have identical geometry but different assigned parent materials, they become different part categories. If both are unassigned, both use `UNASSIGNED`.

The assigned stock shape or sheet size is not part of the categorization key.

## Component fingerprint

A component fingerprint contains:

- Counts of each part category in the component.
- Pairwise distances between every part centroid in that component.
- Radial distances from each part centroid to the component's average centroid.
- A per-part star signature containing that part category and its sorted distances to every other labeled part.

This prevents two components with the same part quantities but different spatial arrangements from collapsing into one component category. The pairwise distance list catches most layout changes, while the radial and star signatures preserve more of the incidence information: which distances belong to the same part, and how each part sits relative to the component as a whole.

Component centroid distances also use direct raw comparison with the arrangement tolerance, so two copied components with tiny transform noise should still match without forcing the edge-length tolerance to become coarse.

## Known tradeoffs

The algorithm is designed to be robust for fabrication parts, but it is still a fingerprint rather than a full geometric equivalence proof.

- If two different parts have the same volume, total surface area, oriented dimensions, edge length set, and topology point distance set within tolerance, they may be grouped together.
- If two visually identical parts have different topology, such as extra split edges, they may still split because their edge token sets are genuinely different.
- Mirrored parts or mirrored component layouts share the same distance-based signatures. This is usually acceptable for flat fabrication parts that can be flipped, but a future handedness/chirality token may be needed for parts or assemblies where mirror orientation matters.
- Curved or highly complex Breps may need a future secondary equivalence check that compares sampled geometry after the fingerprint pass.

## Debugging checklist

When two parts that should match are categorized differently, check these first:

1. Confirm both objects are closed polysurfaces or valid extrusions.
2. Confirm both objects have the same parent material assignment, or both are unassigned.
3. Run Rhino's edge and naked-edge checks to make sure neither object has hidden topology problems.
4. Compare edge counts and face counts. Matching visible edges can still hide split edges or split faces.
5. Enable categorization debug mode and compare the printed token lists for the parts that should match.

Future work should add a diagnostic command that prints the unhashed fingerprint payload for two selected objects so mismatched tokens can be inspected directly.
