# Gazelle Assembly Manager Walkthrough

This guide covers the Gazelle `1.1.0` workflow, including the September 2026 update controls and material/hardware tables. For all commands, settings, support limits, and update behavior in one document, read the [consolidated User Guide](GazelleUserGuide.md). The short version is: prepare clean grouped geometry, assign materials, mark hardware, create the assembly, then use generated geometry for fabrication preparation, drawings, estimates, and BOM output.

## 1. Prep The Model

Gazelle works best when the Rhino model is structured around physical components.

Recommended setup:

- One physical component equals one Rhino group.
- Each group contains the parts and hardware that belong to that component.
- Source geometry can live on your normal modeling layers.
- Keep the model reasonably clean before running assembly creation.
- Avoid mixing unrelated components in one Rhino group.

During assembly creation, Gazelle expands selected objects to the full Rhino group. That means you do not need to select every object in every component. You can select one object from the group and Gazelle will collect the rest.

Avoid overlapping/nested component groups: candidates use their first group for component membership. Accepted ungrouped objects are collected into one shared component bucket, not automatically one component each. Save a working copy before creating or restructuring an assembly.

## 2. Use Geometry Gazelle Can Trust

Manufactured parts should be:

- Closed polysurfaces.
- Extrusions that can be converted to closed Breps.

Geometry that is not treated as a sheet/solid part:

- Curves.
- Points.
- Point clouds.
- Single surfaces.
- Open polysurfaces.

Open polysurfaces are skipped with warnings. This is intentional. If an object is open, the volume, thickness, and edge information can be unreliable, and that can poison the part count.

Unmarked Rhino blocks are allowed. Gazelle expands the block instance, reads the block definition geometry, and categorizes the closed polysurfaces/extrusions inside it. This keeps normal user-created blocks usable.

## 3. Assign Materials Before Creating The Assembly

The cleanest workflow is to assign materials before creating the assembly.

Run `AssignMaterials`, select objects, then choose a parent material from the library. Gazelle stores the material on the object attributes. It does not ask for sheet size at this point.

Why parent material only:

- A part might fit on more than one sheet size.
- Sheet size should be chosen by the estimator based on part footprint and available stock.
- The same material can have several purchasable shapes.

Example:

- Assign `MDF` to the source objects.
- Later the estimator decides whether those parts fit on `3/4 sheet 48x96`, `3/4 sheet 48x120`, or another stock shape under `MDF`.

Material assignment is part of categorization. Two pieces of identical geometry with different assigned parent materials become different part records.

Use consistent numeric units between the Rhino model and stock dimensions. Gazelle's estimator does not convert a material's Unit field into model units.

## 4. Carry Hardware Through The System

Use `ImportHardware` for STEP hardware that you already know and do not want Gazelle to analyze as a sheet part.

The command imports the STEP file, creates a block, places the block on a `HARDWARE` layer, and marks the block definition geometry and placed instance as hardware.

After import:

- You can copy the hardware block instance as needed.
- Put copied hardware instances into the same Rhino groups as the components they belong to.
- When you create the assembly, Gazelle carries the hardware into `ASSEMBLY MANAGER::ORIGINAL ASSEMBLIES`.
- Hardware contributes to component identity.
- Hardware gets counted in the BOM.

Important distinction:

- Marked hardware blocks pass through.
- Unmarked blocks are analyzed as normal part geometry.

That distinction is what lets you use blocks for both imported hardware and normal model organization.

Use hardware identifiers outside your part-number namespace, such as `HINGE-01` rather than `P01`. The current initial-creation code can confuse a hardware identifier matching a manufacturing part name. Set hardware materials before creation; later hardware-material edits are not consistently reflected in saved hardware BOM records. Review those reports or recreate from corrected inputs when necessary.

## 5. Create The Assembly

Open `AssemblyManager`.

In the left panel:

- Enter a new assembly name.
- Confirm the part prefix.
- Confirm the component prefix.
- Click Create Assembly.

Gazelle will ask you to select component groups in the model. The selection is group-aware. Pick the groups or objects in the groups that should become the assembly.

Gazelle then:

- Creates `ASSEMBLY MANAGER::ORIGINAL ASSEMBLIES::<assembly>`.
- Creates generated component layers.
- Categorizes equivalent parts.
- Categorizes equivalent components.
- Copies generated part and hardware geometry to `ASSEMBLY MANAGER::ORIGINAL ASSEMBLIES`.
- Saves assembly metadata in the Rhino document.
- Displays warnings for unsupported geometry.

## 6. Understand The Generated Layers

Gazelle creates one parent layer with three managed output branches:

- `ASSEMBLY MANAGER::ORIGINAL ASSEMBLIES`: generated assembly geometry.
- `ASSEMBLY MANAGER::COPIED COMPONENTS`: drawing-oriented component copies.
- `ASSEMBLY MANAGER::PARTS`: flat part output.

It also creates the top-level `HARDWARE` tree for imported hardware blocks and `ANNO` for annotations and generated tables.

The `ORIGINAL ASSEMBLIES` branch is a generated copy, distinct from the input model. Safe one-to-one edits to its closed BREPs can be mapped back to a healthy design source and forwarded to eligible linked original, copied, and flat output. You can also edit the input model directly. Split/join operations, block leaves, unsupported geometry, and ambiguous simultaneous edits require review; `RefreshAssemblyReferences` runs the manual **Update Assembly** workflow but is not a universal repair command.

Part and component numbers are reconciled after a supported update. For example, if two occurrences are `P01` and only one is changed beyond the categorization tolerances, the unchanged occurrence remains `P01` and the edited occurrence receives the next unused part number, such as `P02`. Its linked descendants, layer paths, quantities, component definition, legacy references, and manufacturing-report caches are updated with it.

If this assembly already has linked flat parts, Gazelle also updates their transformations, quantities, part-category layers, material/thickness row headers, and owned labels. Existing representatives keep their identities and accepted placement; new categories receive additional flat representatives, and safely managed superseded representatives are removed when no other output depends on them. User-created text and unsafe output are preserved. Use **Lay Parts Flat** for the first layout. Use `PlaceComponent` for an individual missing or additional drawing view; **Copy / Orient Components** still appends every component type.

**Automatically propagate changes in assembly** is now **off by default** for new or unset preferences; an explicitly saved on/off choice is preserved. With it off, make your supported geometry/material edits, then select the assembly and click **Update Assembly** (formerly **Refresh References**). For ten `P01` occurrences, updating after editing one produces nine `P01` occurrences and one newly numbered occurrence on its own managed part layer. Rhino's command history reports the quantity changes. An unresolved source in another part category no longer blocks this update; only the category with incomplete evidence is held for review.

While **Enable linked assemblies** remains on, Gazelle continues tracking object identities and placement transforms without rebuilding generated geometry or recategorizing on each edit. Pending supported edits survive saving and reopening the model. The existing `RefreshAssemblyReferences` command performs the same manual update. Turn automatic propagation on if you want supported edits and recategorization applied without clicking the button; enabling it resumes eligible pending work. There is no separate recategorization toggle. Avoid editing both an input source and its generated original before updating; Gazelle preserves such competing edits as link issues rather than choosing one automatically.

The separate **Enable linked assemblies** switch is **on by default**. Use it as an emergency stop for the linking feature, not as the normal batch-edit control. Turning it off stops identity/placement event tracking as well as geometry propagation, **Update Component**, and **Update Assembly**. Already captured bookkeeping is completed and a safety baseline is saved before the pause. Geometry and existing link records are retained.

On re-enabling linking, Gazelle checks that baseline. Unchanged assemblies can resume using your automatic-propagation preference. If tracked objects, attributes/materials, groups, block contents, or assembly data changed during the pause, the affected assembly stays protected with a `LinkTrackingSuspended` issue. Gazelle does not guess missed transforms or overwrite geometry. Restore the tracked state from before disabling, or recreate the affected assembly from appropriate inputs. Save a backup before disabling and editing; switching off and on again does not accept a changed state automatically.

Both update settings are global, not per assembly. Save after bookkeeping has finished: there is no dedicated before-save queue flush. The master-switch safeguard does not cover every edit made while the plugin is unloaded. Do not edit the input independently while an original edit is deferred and Gazelle is unloaded; the saved deferred-original state does not retain a complete source-shape baseline, and resuming it can overwrite an unobserved input edit. The [audit](CodeAudit.md) describes this current gap.

Established categories remain stable while changed occurrences are evaluated. Small tolerance differences among unchanged `P01` occurrences do not trigger a fresh regrouping of those parts or block the edited occurrence's new number. A changed occurrence that genuinely fits competing categories still needs review.

With part coloring enabled, a newly numbered part gets a layer color different from the category it left. If the edit instead matches an existing part, its managed output layers use that part's established color, including custom colors. Gazelle uses 21 predefined colors, then allocates additional random colors while avoiding existing category colors instead of cycling the same palette. The assigned color is saved with the category and reused for original, copied, and flat layers on later refreshes. Existing/custom colors are not globally recolored, and visually similar colors are still possible in large assemblies: use part numbers as the authoritative identifiers. Source colors and individual object color overrides stay unchanged.

An old part layer under an original or copied component is removed when that component no longer needs it and the layer is completely empty. Layers with objects (including hidden or locked objects), child layers, or unresolved linked membership are preserved, as are current and reference layers. **Update Assembly** also checks known empty leftovers from earlier edits. This cleanup does not delete flat-part trees or annotations.

During automatic and manual updates, Rhino's command line reports the current stage: linked geometry, part recategorization and quantities/materials, then existing flat PARTS geometry, placements, and labels when applicable. A final message reports elapsed update time, linked objects refreshed, new part categories, and the document-wide open link issue count. Messages are grouped by assembly and update stage, not printed for every object. An update that stops unexpectedly reports that it did not finish.

When Rhino reports link review items, open **Assembly Manager** and select the assembly. The **Link Issues** section at the bottom of the right-hand panel, below **Workflow**, shows its open issue count, the document-wide total, and each recorded reason with the affected part/component and object details when available. Undo/redo health warnings appear separately. The display updates automatically while the window is open. **Refresh Issues** reloads the issues and checks link health without changing geometry or clearing conflicts; **Update Assembly** is the geometry rebuild action.

New output always uses this hierarchy. Gazelle does not silently rename legacy `SHOP`, `DRAWINGS`, or `CAM` trees in an existing Rhino document.

### Add parts or hardware to a component

Ungroup one component in **ORIGINAL ASSEMBLIES**, add the new solid parts or marked hardware, and regroup it with **all** its existing members. Run `UpdateComponent` to choose the assembly/component type and pick the replacement group. The **Update Component** button in Assembly Manager uses the currently selected assembly and component.

This registers the new group identity and stages the additions. Click **Update Assembly** to apply them **only to the regrouped occurrence**. If five occurrences are `C01` and you change one, the other four stay `C01`; the edited occurrence receives a new component number, or joins an existing matching category. A component with only one occurrence keeps its number unless it matches another category.

Linked input counterparts, every complete grouped copied view linked to the selected occurrence, and existing flat-part output are updated where applicable. Quantities and material/category assignments are refreshed. Each copied view retains its own placement and follows the edited occurrence's category; use `PlaceComponent` if another category needs a drawing view after the split. **Copy / Orient Components** adds another complete set, including types already copied. Automatic updates for the staged assembly wait while its structural change is staged, even if the global setting is on. Staging survives saving and reopening. Plans saved by the earlier all-occurrences version now apply only to their originally selected occurrence; previously applied edits are not undone.

For this initial additive workflow, retain the old members unchanged. Removals, split replacements, mixed groups from different occurrences, and missing or uncertain source-to-original links stop with a message instead of changing unrelated geometry. Use **Update Component** again on the same occurrence to replace its staged regroup after correcting it. You can also stage different occurrences separately, including occurrences of the same component type.

If you reshape a retained member after staging, undo or restore that later edit before applying additions. Shared groups used by other assemblies are not structurally updated by this workflow. There is no dedicated cancel-plan, relink, detach, or split-resolution UI yet; preserve a backup for changes outside the supported path.

## 7. Copy And Orient Components For Drawings

Use `CopyOrientComponents` when you need geometry for shop drawing setup.

This command copies one representative of each component type to `ASSEMBLY MANAGER::COPIED COMPONENTS::<assembly>`, places the groups at fixed drawing positions, applies a bounding-box plan-rotation heuristic, and groups each copy. It is not guaranteed minimum-footprint or collision-free layout.

While linked assemblies are enabled, you can move, rotate, and isolate this geometry for presentation. Accepted movements update saved placements even with automatic propagation off; marked hardware blocks use the final copied-component placement when an assembly is refreshed. Direct reshaping is not a production-source edit. Repeating the command appends another full set at the initial drawing positions; it does not replace or fill only missing views.

For one additional view, use `PlaceComponent`:

1. Activate model space, either a model viewport or a layout detail.
2. Run `PlaceComponent`. Choose the assembly if there is more than one; a single assembly is selected automatically.
3. Choose the component type.
4. Pick the center point for the oriented component copy.

The new group includes all representative parts and hardware, uses the same plan-orientation policy as **Copy / Orient Components**, and begins tracking at your selected point. It is copied directly from its chosen original occurrence, not from another moved drawing copy, and does not require an existing drawing view. You can place the same component several times without increasing assembly/BOM quantities. Every view moves independently, receives updates from its own original occurrence, and follows that occurrence if its component category changes.

Keep each view grouped and complete so that supported **Update Component** additions can reach all linked views of the edited occurrence. Creating an ordinary Rhino copy does not enroll an additional tracked view. `PlaceComponent` stops while linking is disabled, an assembly is safety-blocked, structural additions are staged, or the representative's links/membership are unsafe. It supports direct closed-BREP/extrusion sources and whole marked-hardware blocks; manufacturing parts extracted from unmarked block definitions are not yet supported by this placement workflow. Finish **Update Assembly**, correct the reported issue, or use supported direct source geometry before placing the view; the command does not silently apply pending design changes.

Good to edit:

- The position and rotation of `COPIED COMPONENTS` views created by `CopyOrientComponents` or `PlaceComponent`.
- Closed BREP geometry under `ORIGINAL ASSEMBLIES` when you intend that change to become the revised design source.
- Layout annotations.
- Detail labels and leaders.

Do not manually reshape as production source:

- `COPIED COMPONENTS` or laid-flat `PARTS` geometry; those branches are quarantined for review if reshaped directly.
- Split, joined, open, or block-leaf `ORIGINAL ASSEMBLIES` geometry; these structural cases are not promoted automatically.
- Generated assembly metadata.
- Generated layer paths that Gazelle expects to own.

## 8. Lay Parts Flat

Run `LayPartsFlat` from the Assembly Manager window or command line.

Gazelle lays one representative of each unique part onto `ASSEMBLY MANAGER::PARTS::<assembly>`. It does not lay hardware flat.

What happens:

- The largest face is oriented to World XY.
- The long dimension is rotated into the Y direction.
- Parts are grouped into rows by material and thickness.
- Parts are placed left to right with the configured spacing.
- Each row gets a material/thickness header.
- Each part gets a label with part number, quantity, thickness, and material.

Labels are black and use text height `0.125`. The command enables model-space annotation scaling and sets the document's model-space text scale to `12`, which can affect other annotations. These are fixed, inch-oriented values: thickness labels append an inch mark to raw model values without conversion, so review/correct non-inch output. This is rigid orientation of solid representatives, not unfolding or surface development.

Rerunning **Lay Parts Flat** can reflow existing safe flat output. Use **Update Assembly** to synchronize ordinary design changes while preserving supported flat placements instead.

## 9. Use The Material Library

Open `MaterialLibrary`.

The library has two levels:

- Parent material: the general material, such as `MDF`, `Plywood`, `Steel`, or `Acrylic`.
- Stock shape: a purchasable form of that material, such as a sheet, plate, tube, pipe, or bar.

The material estimator uses sheet-like stock shapes for sheet count estimates. Sheet-like means the shape type contains `sheet`, `plate`, or `panel`.

For sheet stock, actual `Width` and `Height` matter. If actual width and height are supplied, Gazelle uses those values for fitting and estimating. If they are blank, it falls back to `SheetWidth` and `SheetHeight`.

The library can be edited manually, imported from JSON/CSV, or exported for database/spreadsheet work.

The editor supports new/save/delete operations for parent materials and stock shapes, plus library purge. These changes are shared across models and do not automatically reassign existing objects. Use JSON for a full library backup: CSV omits custom properties and materials with no stock shapes, rounds numbers, and cannot reliably import embedded line breaks. See [Material Library Format](MaterialLibraryFormat.md).

## 10. Estimate Materials

Run **Update Assembly** and inspect issues first, then run `EstimateMaterials`. Estimating does not apply pending assembly edits for you.

Gazelle checks each unique part and tries to resolve it to a stock shape:

1. Read the generated part.
2. Compute footprint dimensions.
3. Determine material thickness.
4. Find sheet-like stock under the assigned parent material.
5. Match stock thickness within `0.01`.
6. Choose the smallest sheet that fits.
7. If it does not fit, check a reoriented footprint.
8. Group all matching parts by material stock shape.
9. Estimate sheet count from total footprint area divided by usable sheet area.

Usable sheet area is:

```text
sheet width x sheet height x nesting efficiency
```

The estimate is intentionally conservative and quick, but it is not a true nesting engine. It does not know exact part outlines, grain direction, machining strategy, offcuts, tabs, or the exact efficiency of a real nest. It uses rectangular footprints, so it can overestimate for parts that nest tightly and underestimate if your real process has constraints not represented in the library.

The unaccounted list is important. It tells you when a part has no material, no matching thickness, no available sheet size, missing geometry, or a footprint that does not fit available stock.

## 11. Place A Material And Hardware Table Or Export An Estimate

Click **Place BOM** for the selected assembly, or run `PlaceBOM` to choose an assembly at the command line. The old `PlaceMaterialEstimate` command remains as an alias for the same new workflow.

1. Activate the layout sheet itself, outside any detail. If you run the command in model space or inside a detail, it stops before asking for an assembly and tells you to switch to the layout page.
2. Choose the assembly at the command line.
3. Check the columns to include in the window, then press **Confirm**. Choose at least one. The choices are **Item**, **Material**, **Description**, **Thickness**, **Size**, **Quantity**, **Unit**, **Part Area**, **Parts / Notes**, and **Source**; each invocation starts with **Part Area** and **Source** unchecked.
4. Pick two opposite corners of the rectangle that should contain the table. Width is the horizontal page-XY distance; height is the vertical limit.

Gazelle recalculates the report, fills the selected width with columns, and wraps text before reducing its size. It first tries Arial body text at `0.125 in` paper height, converted to page units. If wrapped content exceeds the available height, or the columns are too narrow for individual characters, it reduces the text just enough to fit. It never increases text size to fill extra height. If the content cannot fit at `0.02 in` body-text height, choose a larger rectangle or fewer columns; rows/text are not truncated. Use standard page units, not unspecified/custom units, and review the placed table's readability.

The static **Bill of Materials — &lt;assembly&gt;** table includes **Sheet Stock Estimate**, **Hardware**, and **Unaccounted Objects** as applicable. Hardware is not counted as sheet parts, and drawing views do not increase hardware quantities. Hardware-only assemblies are supported. Output is grouped on `ANNO::Material Estimates`. Previously placed tables do not update automatically; deliberately replace an old table after an assembly change.

Use `ExportMaterialEstimate` to export material-estimate CSV or JSON. These material-only exports do not gain hardware rows from the combined table change; use `ExportBom` for hardware CSV. CSV is good for spreadsheet review. JSON is better for automation because it preserves the full material-estimate report structure.

Exports are snapshots, not live-linked files. Regenerate reports after updates; consult [Export Schemas](ExportSchemas.md) for exact fields and cache behavior.

## 12. Generate And Export A BOM

Run `GenerateBom` to recalculate the material estimate and build BOM rows from:

- The current material estimate.
- Hardware carried through the assembly.

Run `ExportBom` to recalculate the estimate/BOM and write the BOM CSV. Neither command applies pending linked geometry changes first.

Material rows come from available estimated sheet/stock counts. Hardware rows come from saved marked-hardware records, including supported solid hardware. Rows are grouped by block/item name, description, material ID, and source path, with quantities summed. Run **Estimate Materials** after updating and before **Generate BOM**; a BOM is a purchasing summary, not a row per manufactured part, and it does not update pending geometry itself.

## 13. Label And Dimension Drawings

### Label components and parts in a detail

Use `LabelDetail` when you want quick text-dot labels for many visible objects in one detail.

- `Assembly` labels visible component groups under `ASSEMBLY MANAGER::ORIGINAL ASSEMBLIES`.
- `Component` labels visible part layers under `ASSEMBLY MANAGER::COPIED COMPONENTS`.

### Add leader labels through a detail

Use `LabelPart` from layout space when you want a leader that behaves like Rhino's leader tool but auto-fills the text.

Workflow:

1. Run `LabelPart`.
2. Click the leader tip on top of an object visible through a detail.
3. Place the rest of the leader points.
4. Gazelle fills the leader text with the leaf layer name of the object under the leader tip.

Use `LabelParts` when you want the same workflow repeated with only two points per label.

### Dimension objects in a detail

Use `DimDetail` from layout space. Select the detail, and Gazelle dimensions visible polysurfaces/extrusions by projecting World XY bounding boxes into page space. Horizontal dimensions are placed above the object, vertical dimensions are placed on the side with more room, and dimension values are left to Rhino.

## 14. Project Information And Layout Templates

Use `AssemblyManagerSettings` to save a layout template path.

Use `NewLayout` to import that saved template.

Use `SetProjectInfo` to fill the project fields used by your template. Gazelle writes those values as document string keys like `REVISION`, `PROJECT NAME`, and `CLIENT`.

Template import is not a continuing link to the template file. Detail labels, leaders, dimensions, motion traces, and estimate tables are static annotations; they do not participate in automatic flat-label refresh.

## 15. Troubleshooting

If parts are missing:

- Check that the geometry is closed.
- Check that it is not just a single surface.
- Check command history for skipped-object warnings.

If part categories are wrong:

- Open `AssemblyManagerSettings`.
- Turn on categorization debug output.
- Recreate the assembly.
- Inspect the JSON report and compare the payloads for parts that should match or should differ.

If material estimates have unaccounted parts:

- Check whether the source objects were assigned a parent material.
- Check whether that material has sheet-like stock shapes.
- Check whether stock thickness matches the part thickness within `0.01`.
- Check whether the required footprint fits any available sheet size.

If hardware is being analyzed as a part:

- Make sure it was imported with `ImportHardware`, or otherwise has Gazelle hardware metadata.
- Ordinary Rhino blocks are intentionally analyzed unless they are marked hardware.

If a source was edited while a generated-original edit was pending and Gazelle was unloaded, preserve both designs in a backup before attempting to update. Do not assume restart can reconstruct all missing edit history. General split/merge/relink resolution remains unimplemented; see [the consolidated troubleshooting table](GazelleUserGuide.md#issues-recovery-and-current-limits).

## 16. Other Tools And Preferences

`Regroup` strips selected objects from all their current groups and creates a group, but does not register component membership. `MoveOrtho` moves along a selected world axis. `MotionTrace` moves geometry and leaves static trace linework under `ANNO`. `OrientToWorld` tries a world-Z plan rotation, not full 3D orientation. `Split3Pt` splits with a three-point plane and optional capping; it does not make structural splits safe for linked propagation.

Settings include default P/C prefixes, generated-part colors (on), **Enable linked assemblies** (on), automatic propagation (off for new/unset preferences), length/edge tolerance `0.001`, area/volume/arrangement tolerances `0.01`, creation debug output (off), flat spacing `18`, and an initially empty template path. Existing explicit automatic-propagation choices are preserved. Preferences are saved in Rhino plugin settings and apply across documents. The [User Guide settings table](GazelleUserGuide.md#settings-and-saved-data) explains scope and units; changing settings does not itself rename or regenerate every assembly.

## 17. Remove An Assembly Safely

Save a backup first. **Remove Assembly** deletes recorded generated output even if moved elsewhere, and every object found in the assembly's current/legacy output layer trees—including independent user objects placed there. Input geometry is intended to survive only when outside those trees. Imported hardware library instances/block definitions are not purged merely by removing an assembly.

If another assembly uses removed output as an input, Gazelle preserves that dependent assembly's own outputs but records the missing-source issue. Individual deletion failures do not currently prevent metadata removal, so check for leftover objects. Removal is not a conflict-resolution wizard and is much broader than normal empty-part-layer cleanup.
