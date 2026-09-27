# Gazelle Quick Command Reference

This is the fast version of the command list. For deeper notes, prompts, side effects, and settings, see [DetailedCommandReference.md](DetailedCommandReference.md).

This reference covers all **33 Rhino commands** in Gazelle `1.1.1`, including `AddPartToComponent` and the retained `PlaceMaterialEstimate` compatibility alias. Input-group recognition and observed input-member deletion use the existing **Update Assembly** workflow. Button captions may contain spaces; enter the command names shown in backticks in Rhino. **Update Assembly** is a button, not a separate `UpdateAssembly` command: its command-line name remains `RefreshAssemblyReferences`. **Place BOM** runs `PlaceBOM`.

## Main Workflow

| Command | What it does |
| --- | --- |
| `AssemblyManager` | Opens the main Assembly Manager window for creating assemblies, reviewing parts/components and link issue counts/details, laying parts flat, making drawing copies, estimating materials, and generating BOM data. |
| `CreateAssembly` | Creates a managed assembly from selected grouped model geometry. This is the command-line version of the Create Assembly button. |
| `AddPartToComponent` | Selects an existing registered INPUT component group, then one new solid or marked hardware object. Adds it to that input group and stages output creation for **Update Assembly**, affecting only the selected occurrence. |
| `UpdateComponent` | Registers one regrouped ORIGINAL ASSEMBLIES component occurrence and stages its added parts/hardware. **Update Assembly** applies them only to that occurrence and updates its component category. |
| `RemoveAssembly` | Deletes a managed assembly, its generated geometry, generated groups, and managed layer trees. |
| `RefreshAssemblyReferences` | Runs **Update Assembly**: accepts safe pending original edits, rebuilds linked originals, copied components, and flat parts, and reconciles categories and quantities. Available when automatic propagation is off, provided linked assemblies are enabled and the assembly is not safety-blocked. |

The window's **Refresh Issues** button only reloads issue details and performs a read-only link-health check. It does not rebuild geometry or clear conflicts. **Update Assembly** (formerly **Refresh References**) applies pending supported geometry and material edits. **Automatically propagate changes in assembly** is **off by default** for new or unset preferences; an explicitly saved on/off choice is preserved. With it off, UUIDs, placement transforms, and pending changes continue to be tracked, while geometry propagation and recategorization wait for **Update Assembly**.

**Enable linked assemblies** is a separate master switch, **on by default**. Turning it off suspends event tracking, placement updates, automatic propagation, `AddPartToComponent`, **Update Component**, and **Update Assembly**, and prevents new tracked views through `PlaceComponent`. Use it for emergency isolation, not ordinary batching. On re-enabling, unchanged assemblies can resume; an assembly changed while tracking was off is protected from updates and gets a link issue. Restore its pre-disable tracked state or recreate it from a backup; toggling back on does not infer missed movements or edits. See the [detailed settings notes](DetailedCommandReference.md#settings).

## Manufacturing

| Command | What it does |
| --- | --- |
| `LayPartsFlat` | Lays one representative of each unique part onto `ASSEMBLY MANAGER::PARTS::<assembly>`, grouped by material and thickness. |
| `EstimateMaterials` | Estimates required sheet counts by material, material thickness, and available sheet stock. |
| `PlaceBOM` | Runs **Place BOM**: requires the layout page itself, asks for the assembly and a column checklist, then fits a static grouped stock/hardware/unaccounted table inside two opposite rectangle corners. |
| `PlaceMaterialEstimate` | Compatibility alias for the same `PlaceBOM` column-selection and rectangle-placement workflow. |
| `ExportMaterialEstimate` | Exports the material estimate as CSV or JSON. |
| `GenerateBom` | Recalculates the material estimate and generates sheet-stock and hardware BOM rows. |
| `ExportBom` | Recalculates the estimate and BOM, then exports CSV. |

Run **Update Assembly** before estimating or exporting when edits are pending. These reporting commands do not apply staged component additions or force a linked-geometry update. Estimating uses rectangular area, not production nesting, and does not convert units: stock dimensions must use the same numeric units as the model. See [ExportSchemas.md](ExportSchemas.md).

The placed table includes hardware; **Export Estimate** remains a material-estimate CSV/JSON export without hardware. Use `ExportBom` for sheet-stock and hardware CSV rows. Reported hardware materials come from saved hardware records and should be checked after material edits.

## Hardware

| Command | What it does |
| --- | --- |
| `ImportHardware` | Imports a STEP file, creates a hardware block, marks the block definition and instance as hardware, and places it on `HARDWARE::<name>`. |

## Materials

| Command | What it does |
| --- | --- |
| `MaterialLibrary` | Opens the shared material/stock editor, including create, save, delete, import/export, and Purge Library controls. |
| `ImportMaterialLibrary` | Imports material library data from JSON or CSV. |
| `ExportMaterialLibrary` | Exports material library data to JSON or CSV. The Material Library window export button is the preferred path. |
| `AssignMaterials` | Assigns one parent material to selected Rhino objects. Sheet size is resolved later during estimating. |
| `AssignMaterialToPart` | Chooses a stock record and assigns its material to the design-source objects of a part type; linked output updates through the normal update system. |

## Drawings And Layouts

| Command | What it does |
| --- | --- |
| `CopyOrientComponents` | Adds a new representative of every component type to `ASSEMBLY MANAGER::COPIED COMPONENTS::<assembly>` using a plan-rotation heuristic. Repeating it adds copies; it does not replace existing views. |
| `PlaceComponent` | Places one selected component type at a model-space center point as a fresh, independently tracked drawing group. Repeating it adds views, not physical assembly quantities. |
| `NewLayout` | Imports the saved layout template, prompting for the template file only when needed. |
| `SetProjectInfo` | Saves project fields to document string keys used by the layout template. |
| `LabelDetail` | Adds text-dot labels for components or parts visible in a selected detail. |
| `LabelPart` | Adds a page-space leader whose text is the layer name of the object under the leader tip in a detail. |
| `LabelParts` | Repeats the two-point `LabelPart` workflow until Escape or Enter twice. |
| `DimDetail` | Adds page-space dimensions around visible polysurfaces and extrusions in a selected detail. |

## Utility Geometry

| Command | What it does |
| --- | --- |
| `AssemblyManagerSettings` | Opens Gazelle settings. |
| `Regroup` | Removes old grouping from selected objects and makes a new Rhino group. |
| `MoveOrtho` | Moves selected objects along a chosen world axis with a live preview. |
| `MotionTrace` | Moves selected objects and leaves start/final edge traces plus connector lines. |
| `OrientToWorld` | Tries World-Z rotations to improve the selected objects' bounding-box alignment. |
| `Split3Pt` | Splits polysurfaces or extrusions with a plane defined by three points, with cap-on-split enabled by default. |

## Buttons And Safety Notes

- **Refresh Issues** is read-only; `AddPartToComponent` stages input-side additions and **Update Component** stages regrouped original-side additions; **Update Assembly** applies supported changes. These are different actions. `AddPartToComponent` is a Rhino command, not a new window button.
- **Purge Library**, **New/Save/Delete Material**, and **New/Save/Delete Shape** are Material Library window controls, not additional Rhino commands. Purging/deleting affects the shared library across models; export JSON first for a full backup.
- **Remove Assembly** also deletes untracked objects placed inside that assembly's managed layer trees. Keep unrelated geometry outside them.
- Input-component renames and uniquely identifiable replacement groups are recognized through retained source UUIDs, not names. Regroup with at least one retained source member to stage additions/removals, then use **Update Assembly**. Original-side regrouping remains additive and requires `UpdateComponent` explicitly.
- An observed ordinary deletion from a registered INPUT component group also stages removal without regrouping. Keep at least one existing linked member and use **Update Assembly**, even with automatic propagation on. Only that occurrence's safely owned downstream output is removed; quantities and existing flat output are reconciled. Last-member removal, shared ownership, unsafe consumers, and split/join evidence remain protected. A deletion already processed by an older build can be registered by regrouping the remaining input members before updating; arbitrary missing sources are not inferred as removals.
- New part categories use 21 predefined colors, then randomly allocated additional colors rather than cycling the palette. Assigned colors are saved and reused across matching original, copied, and flat part layers; existing/custom colors are not globally reassigned.
- **Split3Pt** replaces the input with new object IDs. Splitting linked manufacturing geometry is not an automatically supported assembly update.
- Keep each placed component view grouped and complete. Moving or rotating one view updates only its placement; geometry/category updates follow its chosen original occurrence. Supported membership edits reach every complete linked view after **Update Assembly**. Input omissions detach surviving source objects and remove only safe owned downstream objects; shared, mixed, or ambiguous groups and dependent downstream output are protected.
- Detail labels, leaders, dimensions, and placed BOM tables are static output; the linked updater does not revise them. Managed flat-part labels and row headers are updated separately by the assembly system.
