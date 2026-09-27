# Gazelle Part And Component Categorization Algorithm

This document explains how the Assembly Manager workflow inside Gazelle decides which selected Rhino objects are the same part, and how those part categories roll up into component categories.

It describes the Gazelle `1.1.1` implementation. Older packaged documentation under `dist` may describe earlier behavior. Categorization is a manufacturing comparison, not a proof that two solids are geometrically identical.

## Where the code lives

- `Geometry/GeometryFingerprintService.cs`: creates part and component fingerprints.
- `Services/AssemblyGenerationService.cs`: expands selected groups, filters valid parts, groups candidates, copies geometry, and writes assembly records.
- `Services/AssemblyCategorizationReconciliationService.cs`: rebuilds part/component definitions from live source evidence after linked geometry refresh.
- `Services/MaterialAssignment.cs`: normalizes assigned material ids before they are included in categorization.
- `Services/ComponentUpdateService.cs`: stages and applies additive membership for one selected component occurrence before normal recategorization.
- `Services/FlatPartSynchronizationService.cs`: maintains an existing flat layout after categories change.

## High-level flow

1. The create assembly command expands the user's selection to whole Rhino groups.
2. Objects marked as Gazelle hardware are passed through without part analysis.
3. Unmarked block instances are expanded into their definition geometry and categorized like normal parts.
4. Each remaining object is converted into a manufacturable Brep candidate.
5. Each candidate receives a geometry fingerprint.
6. Candidate equivalence compares raw geometry measurements within the configured tolerances and requires the same normalized parent material.
7. Matching candidates share one part definition and part number; their physical occurrences remain separate objects.
8. Generated parts and passed-through hardware are grouped into component candidates based on their source Rhino group.
9. Components receive their own fingerprint based on part counts, hardware identifiers, and pairwise positions.
10. Equivalent component candidates share one component definition/number. Raw labeled-distance comparisons, not just fingerprint-hash equality, decide equivalence.

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

This block expansion belongs to assembly creation. Live replacement of an extracted block leaf is not supported because its stored source locator does not identify a unique definition path. `UpdateComponent` and `AddPartToComponent` also reject newly added unmarked blocks; use supported standalone parts or explicitly marked hardware for those additive workflows.

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

The defaults are length `0.001`, area `0.01`, volume `0.01`, and arrangement `0.01`. They compare measurements in model units (squared units for area and cubed units for volume). These are current plugin settings, not an immutable per-assembly tolerance policy. Changing them can affect later categorization and stored fingerprints; review the resulting numbers before using manufacturing output.

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

The assigned stock shape or sheet size is not part of the categorization key. `PartRecord.CategorizationMaterialId` tracks the parent category separately from the purchasing/nesting stock selection in `PartRecord.MaterialId`. Do not expect selecting a different sheet size of the same parent material to create a new part number.

An explicitly empty `CategorizationMaterialId` records an accepted unassigned/TBD category. It must remain empty across repository loads/saves until categorization accepts a material change; neither live source attributes nor a new stock selection may replace that baseline early. Only a missing/null legacy field is initialized during normalization. Thus a regroup-added TBD part can later merge into the existing matching geometry/material category, while untouched TBD occurrences retain their own number and quantity.

## Live recategorization and stable numbering

After a supported linked geometry or material update, only live design-source occurrences vote on categorization. This runs automatically after an input edit or an accepted `ORIGINAL ASSEMBLIES` edit unless **Automatically propagate changes in assembly** is off or a structural component update is staged. **Update Assembly** applies pending edits manually without changing the automatic preference. `RefreshAssemblyReferences` remains the actual Rhino command name for that action; there is no newly registered `UpdateAssembly` command. Generated originals, copied components, and flat outputs inherit the winning identity through their lineage; they do not vote while they may still contain earlier geometry.

- An occurrence still equivalent to its category keeps its part number.
- If only part of a category changes, the unchanged cohort keeps the old number and the divergent cohort receives the next monotonically allocated number.
- If the entire cohort changes together and remains equivalent, it keeps the old number.
- If a changed cohort unambiguously matches an existing category, it merges into that category.
- Complete component occurrences use the same stable rules after their part identities are settled.

The live pass starts from established, unchanged part categories and then evaluates changed source occurrences. It does not rebuild every unchanged category from scratch or merge two unchanged categories merely because their current measurements overlap. This matters because tolerance comparison is not transitive: A can match B and B can match C while A does not match C. Re-clustering those unchanged occurrences could otherwise create an artificial ambiguity and prevent a separate edited part from receiving its new number.

A changed occurrence must have an unambiguous category assignment. If it genuinely matches competing categories, Gazelle records a review item and currently defers the whole assembly's part-assignment pass, not just that occurrence. Missing, unsupported, or block-backed source evidence is handled differently: it protects its entire existing part category, including its quantities and outputs, while complete unrelated categories can proceed. For example, a missing `P10` source must not prevent ten valid `P01` occurrences from becoming nine `P01` occurrences and one newly numbered occurrence. Incomplete component evidence likewise protects the existing component category; an ambiguous component match defers component reassignment. Structural one-to-many edits such as Split and Join are not inferred by these passes.

The new identity updates managed output layers and quantities through the existing lineage. For an assembly with existing linked flat output, the update then synchronizes flat representatives and owned labels: retain existing identities and accepted placement, add representatives for new supported categories, and retire safely managed superseded representatives without downstream dependencies. BOM, material-estimate, and nesting caches are invalidated; their reports/exports are not automatically regenerated.

Updating does not create the first flat layout or automatically create missing copied-component representatives. **Copy / Orient Components** appends another representative set for every component on each run; it does not replace old copies or fill only a missing category. Use **Place Component** for a specific additional or missing drawing view. Every view follows its recorded original occurrence, without adding manufacturing quantity. Multiple valid views, including views whose original categories merge, do not by themselves require a rebuild. The **Link Issues** section at the bottom of Assembly Manager shows review reasons and affected part/component/object context. **Refresh Issues** is read-only and does not recategorize or fix those issues.

### Changing members of one component occurrence

`UpdateComponent` accepts one regrouped ORIGINAL ASSEMBLIES occurrence with its retained members unchanged and supported new parts or marked hardware added. It rebinds that occurrence's generated group and stages the additions. **Update Assembly** creates linked input counterparts, extends only that occurrence's existing copied descendants, and then performs the category pass.

`AddPartToComponent` starts from an existing registered input group instead: it adds the selected new object to that group and stages the change without replacing the input UUID or rebinding the original group. **Update Assembly** forward-creates its original counterpart, extends that occurrence's copied views, and runs the same category pass. Repeated calls accumulate additions. Both workflows require the explicit update even with automatic propagation enabled; input- and original-side plans cannot be mixed on the same occurrence before applying.

Input group recognition also accepts a uniquely identifiable replacement group with additions or omissions, provided at least one unchanged linked source UUID remains. A name change or same-members regroup only updates group bookkeeping; membership changes are staged for **Update Assembly**. Applying detaches surviving excluded inputs without deleting them and removes only their safely owned original/copied descendants. Part/component signatures and quantities then reflect the retained and added membership. Sibling occurrences keep their established identities. Restoring the original input membership cancels its pending structural plan.

An observed ordinary deletion of a source from its registered input component also stages removal without regrouping, provided at least one linked member remains. **Update Assembly** applies the same ownership-checked removal before category, manufacturing/hardware quantity, and existing flat-output reconciliation. This remains manual even when automatic propagation is enabled. Missing geometry alone does not change membership: unobserved deletions and structural split/join evidence remain protected, as do shared ownership and unsafe downstream dependencies.

If five occurrences share C01 and one gains a genuinely different member, four remain C01 and the edited occurrence receives a new component number, or joins another existing matching category. It does not add the new member to all five occurrences. A sole occurrence can retain its existing number. New manufacturing members receive a new part category or join an existing one using the same material/tolerance comparison; hardware remains outside the part list.

Different occurrences may be staged independently, and restaging one replaces only its plan. The geometry/placement of all retained members is revalidated before application. Original-side removals, general split replacements, mixed-occurrence groups, and unresolved retained-member edits are not accepted. Input omissions also stop for ambiguous/shared identity or downstream assembly dependencies. If a surviving category's flat parent is removed, only a strictly proven replacement parent preserves its representative's UUID/placement; otherwise the operation stops for review. A retired category's safe flat output and owned labels are handled by normal flat synchronization. See [Linked Assembly Architecture](LinkedAssemblyArchitecture.md) for ownership and rollback boundaries.

### Layer colors and obsolete part layers

Recategorization captures existing category colors before moving managed objects. An occurrence that joins an existing category uses that category's established layer color, including a custom color, across original, copied, and flat output. With coloring enabled, initial categories use the 21 predefined colors. Beyond that palette, newly numbered categories receive randomized additional colors, avoiding exact duplicates and favoring well-separated sampled candidates. The color is persisted as nullable `PartRecord.LayerColorArgb` and reused when output is rebuilt. Existing layer colors take precedence, including custom colors and legacy categories without a stored value. New component additions consider those live colors too. A new category does not inherit its predecessor's color; disabling colorization gives newly allocated categories black. Existing/custom duplicates are not globally recolored. Part numbers—not colors—remain authoritative. Input colors and per-object overrides are preserved.

When **Colorize Parts** is disabled, new categories use black; established category colors are still preserved.

After updating membership and moving geometry, Gazelle removes an obsolete part leaf under a managed original or copied component only when no current membership or link requires it, it contains no objects, and it has no child layers. Hidden, locked, reference, and block-definition geometry count as contents. Current and reference layers remain untouched. The same narrow cleanup can remove known empty leftovers from earlier recategorizations during **Update Assembly**; it does not prune component roots, unknown custom layers, flat-part trees, or annotations. Flat synchronization manages only its own safely identified representatives and generated labels.

## Component fingerprint

A component fingerprint contains:

- Counts of each part category in the component.
- Pairwise distances between every part centroid in that component.
- Radial distances from each part centroid to the component's average centroid.
- A per-part star signature containing that part category and its sorted distances to every other labeled part.

This distinguishes many components with the same part quantities but different spatial arrangements. The pairwise distance list catches most layout changes, while the radial and star signatures preserve more incidence information: which distances belong to the same part, and how each part sits relative to the component as a whole. It is still a distance-based signature, so it is not a universal equivalence proof.

Hardware contributes its recorded hardware label/material token and centroid placement, not a manufacturing-part fingerprint. Current hardware material-only edits do not fully update the saved hardware material record used by component/BOM calculations; verify those records/reports separately after such edits.

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
5. Check the current tolerances and the command-line update summary. An issue can leave geometry refreshed while numbering is deliberately held for review.
6. Enable categorization debug mode before creating a diagnostic assembly and compare raw measured values as well as token lists in the exported report. Matching or differing hash tokens alone do not decide raw-tolerance equivalence.

Future work should add a diagnostic command that prints the unhashed fingerprint payload for two selected objects so mismatched tokens can be inspected directly.
