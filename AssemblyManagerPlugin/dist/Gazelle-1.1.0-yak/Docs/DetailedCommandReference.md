# Gazelle Detailed Command Reference

This document is the fuller command reference for Gazelle. It is written for the person using the plugin in Rhino, not for someone reading the code.

It describes Gazelle `1.1.0`, including all 32 implemented Rhino commands (the retained `PlaceMaterialEstimate` compatibility alias is included). See [CommandReference.md](CommandReference.md) for the short inventory, [MaterialLibraryFormat.md](MaterialLibraryFormat.md) for library files, and [ExportSchemas.md](ExportSchemas.md) for report formats. Settings and the material library are shared plugin preferences; assembly records, links, pending edits, and project fields are saved with the Rhino model.

Button names are not always command names. In particular, **Update Assembly** runs `RefreshAssemblyReferences`; there is no separate `UpdateAssembly` command. **Place BOM** runs `PlaceBOM`, with `PlaceMaterialEstimate` retained as an alias for the same workflow. Saving a model preserves pending work but does not apply it.

## Main Assembly Commands

### `AssemblyManager`

Opens the main Assembly Manager window.

Use this for the normal workflow. The window lets you create an assembly, select existing assemblies, see component types, see the part list for a component, remove assemblies, lay parts flat, copy drawing geometry, estimate materials, place/export estimates, generate BOM data, open settings, and open the material library.

The **Link Issues** section at the bottom of the window shows the selected assembly's open issue count, the document-wide total, and detailed reasons with part/component, location, and object/link IDs when available. Undo/redo health warnings are listed separately. The display updates automatically as saved assembly data changes and retains the selected assembly and component. **Refresh Issues** reloads this information and checks link health without modifying geometry or resolving conflicts; it is different from **Update Assembly**. Issue counts are review records, not necessarily a count of distinct objects. There is no general-purpose relink, detach, or split-resolution editor in this window yet.

The command name is still `AssemblyManager` because that is the name of the workflow inside Gazelle.

### `CreateAssembly`

Creates a managed assembly from selected model geometry.

Prompts:

- Assembly name.
- Part prefix. The default comes from settings and is usually `P`.
- Component prefix. The default comes from settings and is usually `C`.
- Component groups in the model.

What it does:

- Expands selected objects to their whole Rhino group.
- Treats each Rhino group as one component instance.
- Accepts closed polysurfaces and extrusions as manufacturable parts.
- Expands unmarked block instances and categorizes their closed polysurface/extrusion contents.
- Passes marked hardware through without analyzing it as sheet parts.
- Creates unique part records from geometry fingerprints plus assigned material.
- Creates component records from the parts and hardware in each group.
- Copies generated geometry to `ASSEMBLY MANAGER::ORIGINAL ASSEMBLIES::<assembly>`.
- Creates component and part layers under `ASSEMBLY MANAGER::ORIGINAL ASSEMBLIES`.
- Stores a versioned link graph connecting source objects, generated originals, copied components, and laid-flat parts.
- Saves assembly metadata in the Rhino document.

Important behavior:

- Curves, points, point clouds, single surfaces, and open polysurfaces are skipped or warned about.
- Same geometry with different assigned parent materials becomes different part records.
- Imported hardware contributes to component identity, but it does not become a flat sheet part.

Choose a unique assembly name; an existing name is rejected rather than overwritten. Keep component grouping simple and non-overlapping. Membership is assigned using the first group index recorded on each object; overlapping groups are not treated as a nested component hierarchy. Ungrouped selected objects share the ungrouped component bucket. Source geometry is retained, and generated originals are translated away from the selection. Creation prepares the managed root layers but does not produce the first copied drawing or flat layout.

### `UpdateComponent`

Stages additions to one selected component occurrence without immediately propagating geometry. Other occurrences of the same component type are left unchanged.

1. Ungroup one component occurrence in `ORIGINAL ASSEMBLIES`.
2. Keep all its existing members, add closed solid parts or marked hardware, and regroup the complete component.
3. Run `UpdateComponent`, choose the assembly and component type, then pick a member of the new group. If it belongs to multiple groups, choose the complete replacement group explicitly. Alternatively, select the assembly/component in Assembly Manager and click **Update Component**.
4. Click **Update Assembly** to apply the additions only to the regrouped occurrence and recategorize it.

The new Rhino group ID replaces the selected occurrence's generated-group ID; the input group's identity is separate. Pending additions are saved in the model. Automatic geometry updates for the staged assembly wait for **Update Assembly**, regardless of the automatic-update setting. The assembly summary and issue details identify pending updates. Running **Update Component** again for the same occurrence replaces its staged selection; different occurrences can be staged separately, even when they currently have the same component number. Pending plans saved by the earlier all-occurrences version are also limited to their originally selected occurrence when applied.

If five occurrences are `C01` and one receives an addition, the other four remain `C01`. The edited occurrence gets the next unused component number, or joins an existing component category if it matches one. An edited singleton retains its existing number unless it matches another category. Part categories and quantities are refreshed too.

Applying the update creates linked input counterparts for the selected occurrence's additions, extends every complete grouped copied view linked to that occurrence, and refreshes materials and existing flat-part output. Each copied view follows its resulting component category and retains its individual placement. A split can leave another category without a drawing representative; use `PlaceComponent` to add just that category when needed. **Copy / Orient Components** still adds a fresh representative for every type, including types already copied. Marked hardware stays hardware and contributes to hardware/BOM records; it does not become a flat sheet part. Existing managed objects retain their identities and placements.

This first version is additive: removing existing members, split/join replacements, unmarked blocks, or uncertain/missing links are not accepted. The selected occurrence must have a valid stored source-to-original placement so its new input counterparts can be positioned safely. No placement is inferred between repeated occurrences, so symmetry between them does not prevent this update. If the staged group changes before applying, correct it and run **Update Component** again. No first-time drawing or flat layout is created by this command.

If an existing retained member is reshaped after staging, undo or restore that later edit before applying the additions; finish **Update Assembly** before making further geometry edits. Shared component groups that also serve as another assembly's input are not structurally updated by this first version.

### `RemoveAssembly`

Deletes a managed assembly.

It removes assembly metadata, generated objects, generated groups, and the managed layer trees for the assembly. It is meant to clean up the Gazelle output, not the original source model.

The command asks for confirmation. Removal includes **every object inside the assembly's managed layer trees**, even untracked geometry or notes placed there manually, plus recorded generated objects moved elsewhere. Keep unrelated work outside these trees and save a backup before removing an assembly. Input objects outside these managed outputs are retained. This is not a material-library purge or a delete-all-hardware operation.

Check the reported deletion counts and remaining model contents. Removal is not all-or-nothing: if Rhino refuses some object/layer deletions, the assembly metadata can still be removed while residual objects remain.

If another assembly uses one of those generated objects as a source, Gazelle preserves that downstream assembly's geometry and records its source and outgoing links as needing review instead of leaving them falsely active.

### `RefreshAssemblyReferences`

Runs the window's **Update Assembly** action. The command name is retained for existing aliases. It first applies pending link and placement bookkeeping and accepts safe edits to generated originals, then updates linked descendants, including copied component views and laid-flat parts created with the current link schema.

Source replacements, assigned-material changes, and safe one-to-one closed-BREP edits under `ORIGINAL ASSEMBLIES` are detected while Gazelle is loaded and **Enable linked assemblies** is on. **Automatically propagate changes in assembly** now defaults to **off** for new or unset preferences; an existing explicitly saved choice is preserved. With automatic propagation off, identities, transforms, safety checks, and pending changes are still saved, but generated geometry and recategorization wait for **Update Assembly**. Enable automatic propagation to resume supported pending updates automatically. Manual updating does not change that preference.

The separate **Enable linked assemblies** master switch defaults to on. Turning it off also stops identity/placement event tracking and disables both update actions. Re-enabling it verifies a saved safety baseline: unchanged assemblies resume, while assemblies changed during the pause are blocked with a link issue rather than rebuilt using stale transforms. Restore their tracked state or recreate the affected assembly. See [Settings](#settings) before using this emergency pause.

Edits to both a source and its original before updating, or to several originals sharing one source, are preserved for review. **Update Assembly** does not silently discard an unresolved original-promotion edit. Direct edits to copied or flat output can still be rebuilt from their authoritative source. Legacy assemblies can migrate their stored source-to-original links, but legacy copied and laid-flat geometry must be regenerated once because older versions did not save those transforms.

After geometry propagation finishes, Gazelle re-evaluates supported live design-source BREPs. If one occurrence no longer matches the other `P01` occurrences, the unchanged cohort keeps `P01` and the changed occurrence receives the next unused part number. If all occurrences change together, the existing part number remains stable. An unambiguous match to another existing part category merges into that category instead of creating a duplicate. The same stable split/merge rule is applied to complete component occurrences, and cached nesting, estimate, and BOM results are invalidated.

The same recategorization runs after automatic input edits and accepted `ORIGINAL ASSEMBLIES` edits. It keeps unchanged part categories as stable references while evaluating changed occurrences, so tolerance differences within an established category do not prevent a separate edited occurrence from receiving a new number and managed layer. Unchanged categories are not merged opportunistically.

For an assembly that already has linked flat output, updating also keeps one representative per supported part category: existing representatives retain their UUIDs and accepted layout, newly split categories receive additional representatives, and safely managed superseded representatives are retired when nothing depends on them. Source-to-flat transforms, quantities, part layers, material/thickness labels, and generated row headers are updated together. Arbitrary user text and unsafe or dependency-bearing output are preserved. Updating does not lay out an assembly for the first time; use **Lay Parts Flat** for that.

Copied-component output remains preserved by lineage. Multiple drawing views of a component are valid, including views of different occurrences whose categories later merge; they do not increase physical quantities. A category without a drawing view is reported so you can use `PlaceComponent` if needed. Missing, unsupported, block-backed, or tolerance-ambiguous source evidence remains unchanged and is recorded for review rather than being guessed.

The command line reports when updating starts, its stages and completion counts, and remaining review items. Completion does not guarantee all links are healthy: check the bottom **Link Issues** section and any separate health warnings. Matching part types share their category color across generated layers. When colorization is enabled, new categories use the 21 predefined colors first, then additional randomly allocated colors chosen to avoid existing category colors. The assigned color is saved, not rerolled on each refresh; merges use the destination category's established/custom color. Empty obsolete managed part layers are cleaned up only when safe; a layer containing other geometry is retained.

## Hardware

### `ImportHardware`

Imports a STEP file as known hardware.

What it does:

- Imports the STEP file.
- Moves imported geometry to `HARDWARE::<file name>`.
- Creates a block definition.
- Marks both the block definition geometry and the placed block instance as Gazelle hardware.
- Stores hardware name, description, source path, and block definition metadata on object attributes.

After import, you can copy the block instance in Rhino. Copies remain recognizable as hardware because the block definition geometry is marked.

Import alone does not enroll the block in an existing assembly or its BOM. Include it in a component before **Create Assembly**, or add/regroup it in an existing original occurrence and complete **Update Component → Update Assembly**. Reimporting the same filename uses unique layer/block names; it does not refresh the old block from its STEP file.

Important behavior:

- Marked hardware is carried through assembly creation and BOM generation.
- Marked hardware is copied into `ASSEMBLY MANAGER::ORIGINAL ASSEMBLIES::<assembly>::<component>::<hardware name>`.
- Marked hardware is copied with drawing components through `CopyOrientComponents`.
- Ordinary blocks that were not imported or marked as hardware are analyzed like regular model geometry.

Use hardware identifiers distinct from manufacturing part numbers. In particular, do not import hardware with an identifier such as `P01` when the assembly uses the `P` prefix: initial assembly creation does not fully protect against that name collision. The additive component-update path has a separate collision guard, but it does not repair earlier creation-time collisions.

Hardware count/identity is tracked separately from manufacturable parts. After a hardware material edit, verify the BOM's material field; saved hardware metadata is not fully synchronized by every source-attribute update. This importer is not a hardware catalog browser, supplier lookup, or generalized assembly block editor.

## Materials

### `MaterialLibrary`

Opens the material library editor.

The material library is persistent plugin data, so it can be reused across Rhino models. It stores parent materials, such as `MDF` or `Steel`, and stock shapes under each material, such as `3/4 sheet 48x96`, `3/4 sheet 60x144`, or `2x2 tube 120`.

Editable parent material fields:

- Name.
- Category.
- Density in lb/cuin.
- Description.

Editable stock shape fields:

- Name.
- Shape type.
- Thickness.
- Unit.
- Sheet size.
- Stock length.
- Actual width.
- Actual height.
- Diameter.
- Wall thickness.
- Nesting efficiency.
- Price per unit.
- Price unit.

The window includes import and export buttons for JSON and CSV library data.

Use **New Material** / **Save Material** and **New Shape** / **Save Shape** to commit editor changes; merely closing the editor is not a substitute for the Save buttons. **Delete Material** deletes that material and all its shapes; **Delete Shape** removes only the selected stock record. **Purge Library** clears the entire shared library after confirmation. These are window controls, not separate Rhino commands. Existing object material strings are not removed and may no longer resolve after deletion/purge. Export JSON before destructive library maintenance.

Stock types such as tube and pipe can be stored, but automatic estimating currently uses sheet-like shapes only. Density is stored, not used for a calculated weight report. Library price/stock edits do not update previously placed tables or exported files; regenerate those reports.

### `ImportMaterialLibrary`

Imports JSON or CSV material library data.

Import is a merge/update operation. Matching material ids or names update existing parent material records. Matching stock-shape ids or names update existing stock-shape records.

### `ExportMaterialLibrary`

Exports the shared material library as JSON or CSV.

The Material Library window export button and this registered command use the same export service. JSON preserves hierarchy and custom properties; CSV is a stock-row exchange format, not a full-fidelity backup. See the format reference for its limitations.

### `AssignMaterials`

Assigns one parent material to selected Rhino objects.

Workflow:

- Preselect objects or start the command and select objects.
- Choose a parent material.
- Gazelle writes material user strings to the selected object attributes.

This does not choose a sheet size. Sheet size and stock shape are resolved later during material estimating.

For linked assemblies, assign on the design input or use **Assign Material To Part**. The normal propagation setting controls when originals, copies, flat labels, and categories are rebuilt. A Rhino display/render material or layer color is not the same as Gazelle's material assignment user strings. Assigning only a downstream drawing copy is not a reliable design-material edit.

### `AssignMaterialToPart`

Prompts for an assembly, a part type, and an available material stock record. It writes that assignment to the part type's design-source objects, invalidates estimate/BOM caches, and lets the linked update pipeline distribute the change. The stock picker differs from `AssignMaterials`, whose dialog picks a parent material. Categorization still uses the parent material identity; estimating can choose another suitable sheet shape under that parent.

This is useful for cleanup, but the preferred workflow is still to assign materials to source geometry before creating the assembly. Pre-assignment gives cleaner categorization and cleaner generated labels.

## Manufacturing And Estimates

### `LayPartsFlat`

Lays one representative of each unique part flat onto `ASSEMBLY MANAGER::PARTS::<assembly>`.

What it does:

- Reads the generated part geometry from the assembly.
- Orients the largest face to World XY.
- Rotates the part so the long dimension is in the Y direction.
- Groups rows by material label and thickness.
- Places parts left to right with the configured spacing.
- Adds row labels by material/thickness.
- Adds part labels with part name, quantity, thickness, and material.
- Sets model-space annotation scaling on and model-space text scale to `12`.
- Uses text height `0.125`.

Hardware is not laid flat. This command is for manufactured parts.

The geometry layer is `ASSEMBLY MANAGER::PARTS::<assembly>::<part>::3D`; the owned label layer is the sibling `text` layer. Material/thickness grouping describes the physical rows and row headers, not extra material/thickness layer levels. Layout dimensions and text sizes are raw model-unit values, not automatically scaled from inches.

Thickness text currently appends an inch mark to raw model values, so non-inch models receive incorrectly unit-labeled thicknesses. The annotation-scaling change is document-wide and may affect other model-space annotations. Review those side effects before using the output for fabrication.

Running this command again deliberately recalculates the whole flat layout and resets accepted manual plan placement. It attempts to reuse safe existing representative UUIDs and replaces its owned labels/headers; missing or unsafe output may be skipped and reported. Use **Update Assembly** for normal linked edits when you want to preserve accepted layout placement.

### `EstimateMaterials`

Creates a material estimate for an assembly.

Under the hood:

- Reads each unique generated part.
- Computes an oriented footprint and material thickness.
- Looks up the assigned parent material.
- Finds sheet-like stock shapes under that material.
- Filters by thickness using a `0.01` thickness tolerance.
- Picks the smallest actual sheet size that fits the part.
- If a part does not fit as initially measured, Gazelle checks a reoriented footprint.
- Groups the result by material stock shape.
- Estimates sheet count from footprint area, sheet area, and nesting efficiency.
- Stores unaccounted objects with reasons.

This is an estimate, not a true nesting solver. It does not pack the exact part outlines. It uses rectangular footprints and nesting efficiency, so it can be wrong when parts nest very efficiently, nest very poorly, have grain direction requirements, have large internal voids, or need manufacturing spacing/kerf rules that are not modeled yet.

Stock `Unit` is a label: the estimator does **not** convert between the Rhino model units and stock units. Enter dimensions in matching numeric units, including the `0.01` thickness check. The smallest fitting stock area wins; it is not a least-cost, grain-aware, tube/bar, or weight optimizer. Reported costs multiply the sheet count by the entered unit price without currency or price-unit conversion.

Run **Update Assembly** first when geometry or component additions are pending. Estimating reads the current managed records and generated geometry; it does not apply staged changes itself. Review the unaccounted list before using quantities downstream.

### `PlaceBOM`

Runs the Assembly Manager window's **Place BOM** action. Recalculates the material estimate and places a grouped **Bill of Materials — &lt;assembly&gt;** table in a user-defined layout/page-space rectangle. The window uses its selected assembly; the standalone command asks you to choose one.

Workflow:

1. Activate the layout page itself, outside its detail view, and run `PlaceBOM`. In model space or an active detail, the command stops and asks you to switch to the layout sheet before running it again; it does not proceed to assembly selection.
2. Choose the assembly at the command line.
3. In the column checklist, select the fields to include and press **Confirm**. At least one column is required.
4. Pick two opposite corners of the desired rectangle in page XY. Their horizontal distance sets the table width; their vertical distance limits its height.

Available columns are **Item**, **Material**, **Description**, **Thickness**, **Size**, **Quantity**, **Unit**, **Part Area**, **Parts / Notes**, and **Source**. Each invocation starts with all except **Part Area** and **Source** selected. Column choices affect only this placed table, not the CSV or JSON export formats.

The table uses the full selected width. Gazelle first allocates column widths and wraps words or long tokens in Arial at a standard body-text paper height of `0.125 in` (converted into the document's page units). If the wrapped table is too tall, or the columns are too narrow even for individual characters, it reduces text height to fit the rectangle. It does not enlarge text to fill excess height. If the table cannot fit at the minimum `0.02 in` body-text height, placement stops with guidance to use a larger rectangle or fewer columns; it does not truncate rows or text. Zero/tiny rectangles and unspecified/custom page units are rejected. Review readability before issuing drawings. The table is drawn on `ANNO::Material Estimates`.

The table contains **Sheet Stock Estimate**, a **Hardware** section when hardware is present, and **Unaccounted Objects** when manufactured parts could not be estimated. Selected hardware fields show item/block name, description, material label (or `TBD`), unit, assembly quantity, and source. They use the same aggregation as the BOM: item, description, material ID, and source path. Drawing copies do not increase hardware counts, and hardware is not counted again as sheet stock. Hardware-only assemblies can produce a table without any sheet-stock rows. An assembly with no report rows receives an explicit empty-report message.

Hardware material labels still depend on saved hardware records, which can be stale after direct source-material changes. Review them separately. This combined table does not change material-estimate CSV/JSON schemas; use `ExportBom` to export hardware rows.

The placed table is static text and linework, not a live link to the report. Repeating the command adds another table. Replace obsolete tables deliberately after an assembly or library change; **Update Assembly** does not rewrite them.

### `PlaceMaterialEstimate`

Compatibility alias for `PlaceBOM`. Existing command aliases now open the same column checklist and two-corner placement workflow, with the same page-space requirement and report content. There is no separate old one-point placement workflow through this command.

### `ExportMaterialEstimate`

Exports the material estimate as CSV or JSON.

The CSV includes a material section and an unaccounted section. The JSON export uses the full material estimate report structure.

The command recalculates the estimate before writing. It does not update linked geometry or pending component additions first. A `.json` destination selects JSON; otherwise the report is written as CSV. Existing export files at the chosen path are overwritten by the write operation; choose a new revision filename to retain prior output.

### `GenerateBom`

Generates BOM rows from the assembly.

What it uses:

- Sheet/stock rows from the current material estimate.
- Hardware rows from hardware carried through the assembly.

The command regenerates the material estimate first, then builds the BOM.

This is a purchasing summary, not a separate row for every manufactured P-number. Unaccounted manufactured parts have no sheet-good BOM row; inspect the estimate before relying on the BOM. Hardware is grouped by item name, description, material ID, and source path across the assembly. Staged additions must be applied before their quantities can be reported.

### `ExportBom`

Exports the BOM as CSV.

The command regenerates the material estimate and BOM before writing the CSV, so the export reflects the current assembly record and material library.

## Drawings, Layouts, And Annotation

### `CopyOrientComponents`

Copies one representative of each component type to `ASSEMBLY MANAGER::COPIED COMPONENTS::<assembly>`.

Each invocation **adds** a new grouped representative for every component type; existing copied views are neither removed nor reused. The initial centers are placed at World X `1200 + 200 × type index`, Y/Z `0`, in model units, and a coarse World-Z plan rotation is evaluated. This is a starting drawing arrangement, not automatic detail/layout creation or collision-free packing.

Use this for drawing views and manual documentation setup. While linked assemblies are enabled, copied geometry can be moved and rotated; Gazelle updates its stored placement matrix even when automatic propagation is off. Marked hardware blocks retain their accepted final copied-component placement when an assembly is refreshed. Direct shape edits are held for review so the plugin does not silently overwrite the source or guess which version should win.

`ORIGINAL ASSEMBLIES` is linked manufacturing geometry, not drawing-only output. A safe one-to-one edit to a closed BREP there is mapped back to its design source, then Gazelle updates the linked original, copied component, and flat part. Split/join operations, block-definition leaves, open or unsupported geometry, and ambiguous concurrent edits remain review items instead of being guessed. You can also edit the design source directly. Use **Update Assembly** with the default manual-update setting, or enable **Automatically propagate changes in assembly** for supported automatic updates. Both require the master linked-assembly switch to remain on.

### `PlaceComponent`

Places one additional drawing view of a selected component type without duplicating the physical component in the assembly or changing BOM quantities.

1. Work in model space (a model viewport or an activated layout detail) and run `PlaceComponent`. The layout page itself is not a valid placement space.
2. Choose the assembly when the document has more than one. With exactly one assembly, Gazelle selects it automatically.
3. Choose the component type.
4. Pick the model-space point for the center of the oriented copy's bounding box.

Gazelle creates a fresh complete group under `ASSEMBLY MANAGER::COPIED COMPONENTS::<assembly>::<component>`, including repeated part members and marked hardware. It uses the same coarse World-Z plan-orientation policy as **Copy / Orient Components**, then centers the result at your point. It copies the representative original occurrence, not an arbitrarily moved or modified drawing view. It can create the first view of a category without rerunning the all-components command.

Each placed object receives a fresh identity and a direct stored transform from its original part or hardware object. The new group can be moved and rotated independently of other views, and its placement is tracked while linked assemblies are enabled—even when automatic propagation is off. Repeating `PlaceComponent` adds more independently tracked views; it does not increase part, component, or hardware quantities.

Views follow the particular original occurrence used for their placement. If that occurrence later changes category, its linked views follow it; unrelated occurrences and their views retain their own lineage. Supported additions through **Update Component → Update Assembly** reach every complete grouped view of the edited occurrence. Keep these groups complete and unambiguous if they need to accept future additions. Ordinary Rhino copies are not automatically enrolled as additional tracked views; use this command instead.

Placement is refused while the master linked-assembly switch is off, when the assembly is safety-blocked, when component additions are staged, or when its representative/membership links cannot be validated. Supported representatives use direct closed-BREP/extrusion links or whole marked-hardware blocks. Manufacturing parts extracted from unmarked block-definition leaves cannot yet be placed by this command because their regeneration path cannot be validated safely. Complete **Update Assembly** and resolve the reported issue before placing a new view; for an unsupported source structure, use supported direct source geometry. The command does not silently apply pending design changes or guess missing member placements.

### `NewLayout`

Imports the saved layout template.

If no valid template path is saved, Gazelle prompts for a `.3dm` file and stores that path in settings. After that, the command can import the template without asking for the file again.

### `SetProjectInfo`

Opens the project info editor.

Gazelle writes these fields as bare Rhino document string keys so layout templates can reference them directly:

- `PROJECT NAME`
- `PROJECT #`
- `CLIENT`
- `DELIVERABLE`
- `DELIVERABLE #`
- `REVISION`
- `STATUS`
- `PROJECT MANAGER NAME`
- `DESIGNER NAME`
- `MISC.`

### `LabelDetail`

Adds text-dot labels for objects visible in a selected detail.

Options:

- `Assembly`: looks at `ASSEMBLY MANAGER::ORIGINAL ASSEMBLIES` geometry and labels visible component groups.
- `Component`: looks at `ASSEMBLY MANAGER::COPIED COMPONENTS` geometry and labels visible part layers.

Labels are newly created text dots based on the detail's current visibility and geometry. They are not registered as linked flat labels, and repeating the command adds another set. Review their placement and text after geometry, layer-name, or view changes; do not expect **Update Assembly** to keep them synchronized.

### `LabelPart`

Adds a page-space leader in layout space.

Workflow:

- Run from layout/page space.
- Pick the leader tip on top of an object visible through a detail.
- Gazelle finds the visible model object under the leader tip.
- The leader text is the leaf layer name of that object.
- Place leader points with a live preview.
- If multiple objects are effectively tied under the tip, Gazelle asks which layer to label.

This creates a static leader, not an associative object label. Its text is the leaf layer name, so picking a flat object on a `3D` layer yields `3D`, not automatically its parent P-number. Review/update leaders after recategorization.

### `LabelParts`

Repeatedly runs a simple two-point `LabelPart` workflow.

Use this when you want to label a lot of parts quickly. Pick a tip point, pick a text point, and repeat. Press Escape to cancel or press Enter twice at the tip prompt to finish cleanly.

### `DimDetail`

Adds page-space dimensions around visible objects in a selected detail.

What it does:

- Requires layout/page space.
- Prompts for a detail.
- Finds visible polysurfaces and extrusions in that detail.
- Computes World XY bounding boxes.
- Projects bounding-box corners into page space.
- Places horizontal dimensions above objects.
- Places vertical dimensions on the side with more room.
- Uses the current document annotation style.
- Does not override the dimension text value.

Activate the layout page itself, not the model space inside its detail. A perspective detail produces a warning because these are World-XY measurements, not general view-aligned manufacturing dimensions. Created dimensions use the detail scale at creation and are not part of the assembly link graph; review or recreate them after geometry/detail-scale changes. Repeating the command adds dimensions again.

## Utility Geometry

### `Regroup`

Removes old grouping from selected objects and creates a new Rhino group.

Only selected objects lose their group memberships; it is not a recursive deletion of all old groups or their other members. This generic helper does not reassign Gazelle's stored component group ID. Use `UpdateComponent` after a supported additive regrouping.

### `MoveOrtho`

Moves selected objects along one chosen world axis.

Prompts for X/Y/Z, start point, and end point. The geometry previews live while the end point is being picked.

### `MotionTrace`

Moves selected objects and leaves trace linework.

The command creates hidden-style start edges, final edges, and connector lines between corresponding moved endpoints or bounding-box corners.

Trace linework is grouped under `ANNO::MOVE_TRACE_<timestamp>` with separate start, final, and connector sublayers. It is a static snapshot and is not linked to later assembly changes. The selected geometry is moved, not copied.

### `OrientToWorld`

Rotates selected objects around World Z to minimize their world bounding box.

This is useful for quickly straightening parts or imported geometry before other operations.

The whole selection is rotated together around its bounding box's bottom center. It does not orient arbitrary faces to World XY or rotate each selected part independently.

### `Split3Pt`

Splits selected polysurfaces or extrusions with a plane defined by three points.

Options:

- `Cap=Yes` by default. When enabled, Gazelle tries to cap planar split openings.

The cutting plane is extended to cover each selected object's bounding box before splitting.

When a split produces multiple pieces, the original object is deleted and the pieces receive new object IDs with copied attributes. Capping is attempted, not guaranteed. This is not an assembly-aware structural edit: splitting an existing linked member can create duplicate identity and missing-source review items and is outside the additive `UpdateComponent` workflow.

## Settings

Open settings with `AssemblyManagerSettings` or the Settings button in the Assembly Manager window.

Preferences are shared across documents. Length, spacing, area, and volume tolerances use raw model units (and their squared/cubed equivalents), not unit-converted physical values. Changing tolerances affects future comparisons; it is not a promise to renumber every existing category immediately. The automatic-update toggle is global, not an independent setting on each assembly.

| Setting | Default | What it controls |
| --- | --- | --- |
| Default Part Prefix | `P` | Prefix used for generated part names, such as `P01`. |
| Default Component Prefix | `C` | Prefix used for generated component names, such as `C01`. |
| Colorize Generated Part Layers | On | Assign 21 predefined colors, then additional random non-repeating allocations. Category colors are saved and shared by matching output layers. Off allocates black to new categories; existing/custom colors are preserved. |
| Enable Linked Assemblies | On | Emergency master switch. Off stops event tracking and linked updates, including the manual update actions and new `PlaceComponent` placements. Re-enabling requires the saved state to be unchanged before an assembly can resume. |
| Automatically Propagate Changes In Assembly | Off | Defer geometry, material propagation, and recategorization until **Update Assembly**. While linked assemblies are enabled, identities and placement transforms continue to be tracked, and pending edits are saved in the model. Existing saved on/off choices are preserved. |
| Length / Edge Tolerance | `0.001` | Rounding tolerance for edge lengths, dimensions, and component centroid distance tokens. |
| Area Tolerance | `0.01` | Rounding tolerance for part surface area in fingerprints. |
| Volume Tolerance | `0.01` | Rounding tolerance for part volume in fingerprints. |
| Arrangement Tolerance | `0.01` | Rounding tolerance for feature-arrangement distances and component layout checks. |
| Print Part Categorization Debug Output | Off | Writes a JSON debug report showing each compared part payload and assigned part number. |
| Lay Parts Flat Part Spacing | `18.0` | Horizontal spacing between flat parts. Row spacing is derived from this and kept larger for readability. |
| Layout Template Path | blank | Saved `.3dm` template used by `NewLayout`. |

For ordinary batch editing, leave **Enable linked assemblies** on and automatic propagation off. They are not interchangeable switches, and there is no separate automatic-recategorization preference: recategorization runs as part of the chosen automatic/manual update.

Use the master switch only when you need to isolate the linking system. Gazelle finishes already captured bookkeeping before saving a safety baseline, then stops listening for new linked-object changes. It retains geometry, links, and staged plans rather than deleting them. On re-enabling, it compares tracked geometry, object attributes, block contents, groups, and assembly data with that baseline. Unchanged assemblies resume according to the automatic-propagation setting. A changed or unverifiable assembly is protected from both automatic and manual updates and shows a `LinkTrackingSuspended` issue; restoring the pre-disable state allows rechecking, otherwise recreate it from appropriate inputs. A backup is strongly recommended before disabling and editing. Do not use repeated off/on toggles as a way to accept unknown changes.

The explicit master-switch safeguard does not replace the separate audit warning about edits made while the plugin is unloaded; there is no complete edit history for such an interval. Save after pending bookkeeping has completed and review [CodeAudit.md](CodeAudit.md) for current recovery limits.

Debug reports are written next to the Rhino file in `AssemblyManagerDebugReports` when possible. If the Rhino document has no saved path, reports are written to `~/Documents/AssemblyManagerDebugReports`.
