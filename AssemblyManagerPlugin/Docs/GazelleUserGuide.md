# Gazelle: functions and linked assembly updates

This is the consolidated operator guide for Gazelle `1.1.0`, also known as Assembly Manager 2.0, prepared September 26, 2026. It describes this version's functionality, not every previously published Yak package. See the [release notes](../CHANGELOG.md) for the update summary. This guide does not itself confirm publication to Yak.

Gazelle turns a grouped Rhino fabrication model into numbered parts and components, linked assembly and drawing geometry, flat-part layouts, material estimates, and a purchasing BOM. The main window is opened with `AssemblyManager`. Commands below are the exact English Rhino command names; window buttons sometimes use shorter names.

## Contents

1. [Model concepts and layers](#model-concepts-and-layers)
2. [First assembly: recommended workflow](#first-assembly-recommended-workflow)
3. [All available commands](#all-available-commands)
4. [The Assembly Manager window](#the-assembly-manager-window)
5. [How linked updating works](#how-linked-updating-works)
6. [Adding a part or hardware to one component](#adding-a-part-or-hardware-to-one-component)
7. [Copied views and flat parts](#copied-views-and-flat-parts)
8. [Materials, estimates, and BOMs](#materials-estimates-and-boms)
9. [Settings and saved data](#settings-and-saved-data)
10. [Issues, recovery, and current limits](#issues-recovery-and-current-limits)
11. [Further reading](#further-reading)

## Model concepts and layers

An **assembly** is a named collection managed in one Rhino document. A **component occurrence** is one physical grouped instance, such as one cabinet. A **component type**, such as `C01`, describes equivalent occurrences. A **part type**, such as `P01`, describes equivalent manufactured pieces across the assembly. A **hardware item** is explicitly marked hardware, not a manufactured sheet part.

Component quantity counts occurrences. Part quantity counts manufacturing source occurrences across the assembly, not the extra geometry generated for drawings or flat layouts. A copied representative or a flat representative is a view of the design, not an extra item to purchase.

Use one unambiguous Rhino group per component. Avoid overlapping/nested component groups: creation expands selection through groups, but assigns a candidate using its first recorded group. All ungrouped accepted objects share one component bucket, so they do not automatically become separate components. Manufacturing geometry should be closed BREPs/polysurfaces or extrusions convertible to closed BREPs. Open polysurfaces, single surfaces, curves, and points are not accepted as reliable manufacturing parts.

Unmarked blocks can be expanded during initial creation. That does **not** mean later block-definition or block-leaf edits support the same automatic update workflow as directly linked closed solids. Imported, marked hardware is handled separately.

The input remains on your modeling layers. It is distinct from Gazelle's generated `ORIGINAL ASSEMBLIES` copy:

```text
Your modeling layers                      original input/design sources
ASSEMBLY MANAGER
  ORIGINAL ASSEMBLIES
    <assembly>
      C01
        P01                               generated assembly occurrences
        <hardware identifier>
  COPIED COMPONENTS
    <assembly>
      C01
        P01                               drawing representatives
  PARTS
    <assembly>
      P01
        3D                                flat solid representative
        text                              managed part label
      row labels                          material/thickness row headers
HARDWARE                                  imported hardware instances
ANNO                                      drawing annotations/tables/traces
```

Material and thickness organize flat **rows and labels**, not an extra material/thickness layer hierarchy. New managed output uses these layer names. Legacy `SHOP`, `DRAWINGS`, and `CAM` paths have compatibility handling but are not silently renamed. Do not manually rename managed layer paths or modify Gazelle's object user strings.

## First assembly: recommended workflow

Before editing a managed model on any machine, make sure the latest Gazelle release is running and **Enable linked assemblies** is on. All collaborators should use the same current release. Automatic propagation may be off; that is the supported way to batch edits while retaining tracking. Merely having Gazelle installed, or loading it only after editing, is not equivalent to recording those edits. Keep a model backup and finish processing and **Update Assembly** before handing off a file or generating fabrication reports.

1. Save a working copy of the Rhino model. Check that model dimensions and material-library stock dimensions use the same numeric units.
2. Prepare closed parts and one group per physical component. Resolve overlapping groups and unsupported geometry first.
3. Open `MaterialLibrary` to set up parent materials and stock shapes. Use `AssignMaterials` on input objects to assign parent materials.
4. Use `ImportHardware` for STEP hardware. Copy its marked instances into the appropriate groups. Use identifiers such as `HINGE-01`, not `P01`, to avoid the current initial-creation naming collision described below.
5. Open `AssemblyManager` or run `CreateAssembly`. Supply an assembly name and part/component prefixes, then select the intended component groups. Review the creation warnings.
6. Run `CopyOrientComponents` if you need drawing representatives. Run `LayPartsFlat` for the first flat layout. Neither is a toolpath generator.
7. Make supported design changes using the update workflow below. Finish any staged component additions with **Update Assembly** and review outstanding issues.
8. Run `EstimateMaterials`, inspect unaccounted parts, then `GenerateBom`. Export only after checking that the report represents the updated assembly.
9. Create/import a layout with `NewLayout`, fill `SetProjectInfo`, and add labels, dimensions, or estimate tables as needed.

## All available commands

These are all 32 implemented command entrypoints, including the retained placement alias. See the [detailed reference](DetailedCommandReference.md) for prompts and options.

### Assemblies and generated geometry

| Command | Function and important behavior |
| --- | --- |
| `AssemblyManager` | Opens the assembly/component browser and workflow window. |
| `CreateAssembly` | Copies accepted grouped input, categorizes parts/components, creates managed layers/groups, and records links. An existing assembly name is rejected. |
| `UpdateComponent` | Registers one regrouped original occurrence and stages added parts or marked hardware; **Update Assembly** applies them later. |
| `RefreshAssemblyReferences` | Runs the window's **Update Assembly** workflow. The older command name is retained; there is no separate `UpdateAssembly` command. |
| `CopyOrientComponents` | Appends one drawing representative for every component type. Repeating it adds another set; it is not a missing-only or replace-existing operation. |
| `PlaceComponent` | Selects one component type and places an additional independently tracked drawing view at a chosen point. Manufacturing quantities do not increase. |
| `LayPartsFlat` | Creates/reflows one flat representative per manufacturing part category, with quantity/material labels. Existing safe managed outputs can be replaced while keeping their IDs. |
| `RemoveAssembly` | Removes assembly metadata and generated output, including objects found anywhere in its current and legacy output layer trees. Read the deletion warning below. |

### Materials, hardware, and reporting

| Command | Function and important behavior |
| --- | --- |
| `ImportHardware` | Imports a STEP file, creates a marked hardware block and placed instance, and records descriptive/source-file metadata. It does not watch the STEP file for later changes. |
| `MaterialLibrary` | Opens the persistent parent-material and stock-shape editor, including import/export and deletion/purge controls. |
| `ImportMaterialLibrary` | Imports supported JSON or CSV library records. Review matching IDs and import behavior before updating a shared library. |
| `ExportMaterialLibrary` | Exports JSON or CSV. Use JSON as the fuller library backup/interchange format. |
| `AssignMaterials` | Assigns a selected parent material to selected Rhino objects; this is the normal pre-assembly input workflow. |
| `AssignMaterialToPart` | Assigns material to an existing named part record and its recorded input sources; generated output follows the update pipeline. It is category-wide, not an edit to just one physical occurrence. |
| `EstimateMaterials` | Recalculates sheet-stock estimates and the unaccounted-part list from current assembly data. Does not first update pending assembly geometry. |
| `PlaceBOM` | Selects an assembly and columns, then fits a static stock/hardware/unaccounted table into two chosen layout corners. Wraps text before reducing its size. |
| `PlaceMaterialEstimate` | Compatibility alias for the same **Place BOM** workflow, including column selection and two-corner placement. |
| `ExportMaterialEstimate` | Recalculates and exports estimate CSV or JSON; does not first apply pending linked assembly edits. |
| `GenerateBom` | Recalculates the material estimate, then builds sheet-good rows and hardware rows from assembly records. It is not a manufacturing cut-list generator. |
| `ExportBom` | Recalculates the estimate/BOM and exports CSV. Complete updating and inspect unaccounted parts before relying on its quantities. |

### Drawings and project setup

| Command | Function and important behavior |
| --- | --- |
| `AssemblyManagerSettings` | Opens global preferences, including the linked-assembly master switch, automatic propagation, categorization tolerances, flat spacing, and the layout-template path. |
| `SetProjectInfo` | Writes project/title-block fields into the current document's strings. |
| `NewLayout` | Imports the saved `.3dm` layout template into the active document. It is an import, not a continuing link to the template. |
| `LabelDetail` | Adds text-dot labels for visible assembly components or copied-component part layers in a chosen detail. |
| `LabelPart` | Creates a multi-point layout leader using the leaf layer name beneath its tip. Offers a choice when several objects are candidates. |
| `LabelParts` | Repeats the two-point leader-label workflow until ended. |
| `DimDetail` | Places dimensions from visible solid/extrusion World-XY bounding boxes projected through a detail; it is not an arbitrary-view feature-dimensioning engine. |

### General geometry utilities

| Command | Function and important behavior |
| --- | --- |
| `Regroup` | Removes selected objects from all their existing groups and creates a new group. It does not register an assembly membership change; use `UpdateComponent` for that. |
| `MoveOrtho` | Moves selected geometry along the chosen world X, Y, or Z axis. |
| `MotionTrace` | Moves selected supported geometry and creates start/final edges and movement connectors under `ANNO`. The trace is static. |
| `OrientToWorld` | Tries world-Z plan rotations to improve the selection's bounding-box alignment. It is not a full 3D face-to-world orientation. |
| `Split3Pt` | Splits supported solids with a plane defined by three points; capping defaults on. This utility does not provide linked one-to-many part propagation. |

Utility commands are not exceptions to the linking rules. For example, splitting a tracked part still creates a structural review case even when the split was made by Gazelle's own utility.

## The Assembly Manager window

Select an assembly on the left, then a component type to see its quantity and part list. The lists and quantity fields describe the saved assembly; they are not a free-form editor for IDs, quantities, or membership. New Name and prefix fields are for creation, not renaming an existing assembly.

The workflow controls include creation/removal, **Copy / Orient Components**, **Place Component**, flat parts, **Update Component**, **Update Assembly**, material-library access, estimates, **Place BOM**, estimate export, BOM generation, and settings. The Place Component button uses the selected assembly/component; Place BOM uses the selected assembly and still opens the column checklist. Some commands, including BOM export and drawing utilities, are accessed from Rhino's command line.

The **Link Issues** area is at the bottom of the right-hand panel, below **Workflow**. It shows:

- Open issues for the selected assembly and a separate document-wide total.
- Recorded reasons and available part/component/object details.
- Pending component additions waiting for manual application.
- Separate undo/redo health warnings; these are not included in the persisted link-issue count.

The display refreshes while the window is open. **Refresh Issues** reloads details and performs a read-only health inspection. It does not rebuild geometry, resolve conflicts, or apply additions. **Update Assembly** performs updating.

## How linked updating works

### The relationship between objects

Gazelle records stable link identities alongside Rhino object UUIDs and saves a placement transform for each parent-to-child relationship. A UUID identifies an object; a transform records where a copy belongs relative to its parent. The saved document graph, not a matching layer name alone, is the authority for new linked output.

```text
Input/design source
  -> generated ORIGINAL ASSEMBLIES occurrence
       -> COPIED COMPONENTS representative, when created
       -> PARTS flat representative, when created
```

A flat output can also be linked through a copied parent. Its placement is calculated from its actual recorded parent. Gazelle does not require all outputs to be in the original creation positions.

Moving or rotating linked output normally changes its stored placement, including hardware block instances moved with copied components. A later update uses their final translated/rotated placement, not the initial Copy / Orient location. Reshaping a supported input changes the design. A supported one-to-one closed-BREP edit to an original assembly occurrence can be mapped back through its transform to the input, then propagated forward. This reverse direction requires a healthy, unambiguous relationship; it is not unrestricted two-way editing of every output branch.

### Automatic or manual

**Automatically propagate changes in assembly** defaults **off** for new settings and older records without an explicit value. Existing saved on/off choices remain unchanged. This controls geometry propagation and recategorization together; it is not a separate category-only switch. With it on, Gazelle records object and group changes and processes supported changes after Rhino returns to an appropriate idle state. It does not rebuild everything inside each object callback.

To batch work:

1. Open `AssemblyManagerSettings` and turn automatic propagation off.
2. Make supported edits. Gazelle still records identities, placement changes, pending edits, and safety issues.
3. Select the assembly and click **Update Assembly**, or run `RefreshAssemblyReferences`.
4. Check command history and the issue panel. The setting remains off until you turn it on again.

The preference is global to this plugin installation, not a checkbox stored separately on each assembly. Re-enabling it allows eligible deferred work to resume. An assembly with staged component additions remains held until an explicit **Update Assembly**, even if the preference is on.

After queued tracking is reconciled, pending markers and staged additions are part of the document data and survive a normal save/reopen. Let Rhino finish processing before saving/closing. The current implementation does not have a dedicated before-save queue-flush hook. Do not treat plugin-unloaded or externally modified geometry as fully observed edit history. In particular, if an original edit is pending and its input is independently changed while Gazelle is not observing it, a later update can overwrite that unobserved input change. This remains an accepted limitation for 1.1.0, not a fixed or automatically detected case; the operating requirement is to keep the latest Gazelle running with linking enabled on every editing machine.

### Emergency master switch

**Enable linked assemblies** defaults **on**. Leave it on for normal manual or automatic updating. Turning it off stops link-event capture and prevents **Update Assembly**, **Update Component**, **Place Component**, and automatic propagation. Existing geometry and saved links remain; creation, reporting, and unrelated drawing tools are not uninstalled or disabled.

Disabling drains already-captured placement bookkeeping without propagating geometry, then saves an exact per-assembly suspension snapshot. On re-enabling, assemblies unchanged since that snapshot can resume. Changed geometry, materials, group membership, block definitions, or saved link state cause that assembly to remain blocked and show a link issue. This prevents a stale transform from overwriting work done while tracking was off. Restore the affected state or, after preserving your input/independent work, remove and recreate the affected assembly. Review the [removal warning](#removal-warning) first. Toggling off/on again does not approve the changed state. These safeguards persist with the saved model.

Use the automatic-propagation checkbox—not the master switch—to batch everyday edits while keeping placements tracked. Changes made while the plugin is unloaded remain subject to the separate limitations below; the master switch is not a general history-reconstruction tool.

### What an update does

1. Reconciles captured IDs, placements, and pending edit evidence.
2. Validates and applies staged component additions, if any, before accepting further original edits.
3. Accepts safe original-to-input edits and refreshes eligible linked descendants.
4. Re-evaluates supported part and complete component categories, quantities, references, and managed layer assignments.
5. Synchronizes existing flat output and owned labels; it does not create the first flat layout or automatically create missing copied-component views.
6. Invalidates cached manufacturing reports as appropriate and records remaining review items.

Updates can also reach dependent assemblies that use a changed managed object as input. This is not an instruction to rewrite unrelated assemblies, and unresolved dependencies can block retirement of an old output.

Rhino's command line reports preparation, geometry refresh, categorization/quantities/materials, and flat-output stages when applicable, followed by an elapsed-time/result summary. Updates with review items are not proof that every branch completed successfully. Unexpected failures report that the update stopped.

### Part and component numbers after an edit

Equivalence is based on supported geometry, material category, and categorization checks—not just a name, UUID, or bounding box.

- If one of ten `P01` occurrences becomes a different part, the nine unchanged occurrences retain `P01`; the edited occurrence receives the next unused number, for example `P02`.
- If it unambiguously matches an existing category, it joins that category instead of creating a duplicate number.
- If every occurrence changes together and remains equivalent, the category can keep its number.
- Complete component occurrences follow the same split/merge principle. Editing one of five `C01`s can leave four `C01`s and one new component type.
- Incomplete evidence or competing tolerance matches can preserve the existing category for review. Increasing tolerances is not a general repair for a broken link.

New part categories get a distinct color from the category they left when coloring is enabled. Initial categories use the 21 predefined colors; later categories receive randomized colors chosen to avoid exact duplicates and improve separation from used colors. The assigned color is saved, not rerandomized on refresh. Original, copied, and flat output share it; a merge uses the destination's existing color, including customization. Existing duplicate/custom colors are not globally recolored, and similar-looking colors remain possible; part numbers are the identifiers. Input styling and explicit per-object color overrides are preserved.

Obsolete original/copied component part-layer leaves are removed only when the component no longer needs them and the leaves are safely empty. Objects, child layers, current/reference layers, or unresolved membership prevent cleanup. This differs from the broader deletion performed by **Remove Assembly**.

### Which edits are supported?

| Action | Current result |
| --- | --- |
| One-to-one supported solid edit on linked input | Refreshes eligible descendants and recategorizes automatically, or on manual update while paused. |
| Healthy one-to-one closed-BREP edit on ORIGINAL ASSEMBLIES | Can promote back to input, then update descendants and categories. |
| Supported manufacturing-source/original material assignment change | Can update categories and flat material/quantity labels; review hardware-specific limitation below. |
| Move/rotate linked output | Updates placement bookkeeping when the transform is observed and supported; flat plan rotation and anchor are preserved during synchronization. |
| Directly reshape COPIED COMPONENTS or PARTS | Marks the branch for review. It is not promoted to the design input; manual updating may rebuild it from the authoritative source. |
| Add a member by regrouping | Not inferred as an approved design addition. Use **Update Component**, then **Update Assembly**. |
| Split, join, delete, or replace one identity with several | Structural review; automatic one-to-many/many-to-one propagation is not implemented. |
| Edit input and its original before updating | Competing authority is preserved for review when captured; do not rely on automatic conflict resolution. |
| Edit block-definition manufacturing leaves | Current automatic leaf-update support is limited; initial block expansion is not a live block-definition synchronization promise. |
| Undo/redo | Stops mutation during replay and inspects restored links read-only. It avoids immediately replaying a restored pending update and breaking redo. |

## Adding a part or hardware to one component

This is an **additive, occurrence-specific** workflow:

1. In `ORIGINAL ASSEMBLIES`, ungroup one occurrence of the component to change.
2. Leave all retained members and their geometry/material relationship unchanged. Add closed solid parts or explicitly marked hardware.
3. Regroup the complete occurrence, including every old member and the additions. Do not include another occurrence's linked objects.
4. Run `UpdateComponent`; choose the assembly, its current component type, and the regrouped occurrence. If a picked object belongs to multiple groups, choose the complete replacement group. The window button uses the selected assembly/component type.
5. Click **Update Assembly** to apply the staged change.

The new group identity replaces the selected occurrence's generated-original group identity. Its input group is a separate identity. On application, new linked input counterparts are placed using that occurrence's existing source-to-original transform. The user's added original objects retain their UUIDs; existing source/original members are retained. Only copied layouts actually linked to the selected occurrence receive added members.

Other occurrences do not receive additions. A changed occurrence gets a new component number if it diverges, or joins an existing matching type. A singleton can keep its component number. Part and hardware quantities reflect only the occurrences actually changed.

Different occurrences can be staged independently. Restaging the same occurrence replaces its plan without discarding another occurrence's plan. Old pending plans saved by the earlier all-occurrences implementation now apply only to their saved selected occurrence; this does not undo edits already applied by an older build.

Current restrictions:

- No automatic removal, split/join replacement, or unmarked-block addition through this command.
- Required selected members, groups, and linked copied layouts must be present and safely editable, with valid stored placement evidence.
- Shared groups that are also another assembly's input/generated group are rejected for structural changes.
- A copied managed object with a genuinely new UUID may be adopted only when its inherited identity can be proved unambiguous; an existing managed occurrence cannot be stolen as an addition.
- Changing the staged group requires correction and restaging. Changing a retained member after staging can block application; undo/restore that later edit and finish applying additions before further reshaping.
- There is no dedicated cancel-plan, relink, detach, or split-resolution interface yet. Restore the model through an appropriate Rhino undo/backup when a plan cannot safely be completed.

## Copied views and flat parts

`CopyOrientComponents` creates one representative of each component type per invocation, with groups and linked output layers. Its initial layout uses fixed positions and a plan-rotation heuristic; it is not guaranteed minimum-footprint or collision-free packing. Reposition/rotate the resulting groups for drawing presentation.

**Repeating Copy / Orient appends another full set, including types already represented.** It does not replace views or make only a missing type. For a specific additional or missing view, use **Place Component**:

1. Run `PlaceComponent` in model space (an activated layout detail also counts). If exactly one assembly exists, it is selected automatically; otherwise choose an assembly.
2. Choose a component type, such as `C01`.
3. Pick the center location for its plan-oriented bounding box.

Gazelle copies the complete, current representative occurrence in `ORIGINAL ASSEMBLIES`, using the Copy / Orient plan-orientation rule. It can create the first drawing view of a type; no earlier Copy / Orient run is required. It creates an independent Rhino group and fresh link identities directly from that original occurrence, not from another drawing view. Move or rotate each view as needed. Subsequent updates retain their independent placements, propagate geometry/material changes to all eligible views, and do not count extra drawings as manufacturing quantities.

When an occurrence changes category, its views follow that occurrence. Several views remain valid, even when previously different component categories merge; they are not a duplicate-copy error. If a split leaves a different category without a view, use `PlaceComponent` for that category. Added members registered through Update Component reach every complete copied group linked to the changed occurrence.

Place Component requires linking to be enabled and the source/original representative to be complete and current. Finish staged changes and **Update Assembly** first; resolve reported link issues instead of duplicating stale output. Whole marked hardware blocks are supported, but ordinary expanded block-definition manufacturing leaves are conservatively rejected. The command does not replace or detach existing views. Do not blindly delete copies that other assemblies or flat outputs use as sources.

`LayPartsFlat` initially lays out one representative per manufacturing category, oriented using a large face and a long-axis-to-Y rotation. Hardware is excluded. Parts are arranged in material/thickness rows with part number, quantity, thickness, material, and row-header labels.

Rerunning **Lay Parts Flat** is a re-layout action, not the same as **Update Assembly**. It can move/reflow existing safe flat representatives. Normal update synchronization preserves supported user anchors/plan rotations, refreshes transforms and labels, creates additional flat categories only when a flat layout already exists, and safely retires obsolete output where there are no protected dependencies.

Flat creation enables model-space annotation scaling and sets document `ModelSpaceTextScale` to `12`; generated label height is `0.125`. These are current code constants, not unit-adaptive preferences, and the document-wide scaling change can affect other annotations. Thickness labels also append an inch mark to raw model values without conversion, so they are incorrect for non-inch models until manually corrected or the implementation is fixed. Keep independent text separate from managed labels. Detail labels/leaders, dimensions, motion traces, and placed estimate tables are static outputs, not part of the automatic flat-label maintenance system.

## Materials, estimates, and BOMs

The library separates a parent material (for example plywood or steel) from purchasable stock shapes. The editor can create/save/delete parent materials and shapes, import/export records, and purge the library. Deleting library entries does not automatically reassign objects in existing models. Export a JSON backup before a bulk change.

Use `AssignMaterials` for parent material assignments on model objects. Use `AssignMaterialToPart` deliberately: it targets an entire named part category, not just one instance. Parent material identity participates in manufacturing categorization; a selected stock-shape ID may carry more specific purchasing information.

Sheet estimation uses shapes whose type contains `sheet`, `plate`, or `panel`. It checks thickness within `0.01`, chooses a smallest fitting stock footprint, and estimates sheets from total rectangular part footprint area divided by usable sheet area:

```text
usable sheet area = stock width × stock height × nesting efficiency
```

Actual stock Width/Height override the fallback SheetWidth/SheetHeight when supplied. The result is an area-based estimate, not an exact nest. It does not account for all grain, machining, offcut, clamp, or packing constraints. Review the unaccounted list for missing material, thickness, geometry, or fitting stock.

**Model dimensions and stock dimensions must use the same numeric units.** The current estimator reports the library's Unit text but does not convert stock dimensions to Rhino model units. For example, millimeter model geometry will not automatically match inch stock entries.

After a design/material change, use this reporting order:

1. **Update Assembly** and inspect issues.
2. **Estimate Materials** and inspect unaccounted parts.
3. **Generate BOM**.
4. Export or place the needed report.

Updating can invalidate cached estimates/BOMs but does not automatically regenerate purchasing reports or rewrite previously exported files or placed tables. Report placement/export and BOM commands recalculate the material estimate, but are not an implicit **Update Assembly**. Explicitly estimating first gives you a chance to inspect the unaccounted list. BOM material rows are sheet-good purchasing rows; hardware rows are grouped by recorded item/description/material/source information. A BOM is not a row per manufactured `P##` occurrence, and unaccounted manufactured parts are omitted from its sheet-good rows.

Current hardware cautions: initial assembly creation can collide with hardware identifiers such as `P01`, so identifiers must stay outside the assembly's configured part-prefix/number namespace (for example `HINGE-01`). This naming rule is a release requirement; a code-level collision fix is deferred. Later hardware material edits can change object attributes without refreshing the saved hardware material used by component categorization/BOM generation. Verify those records after such edits; set hardware materials before creation and recreate the affected assembly from corrected inputs if necessary until that gap is fixed.

### Place a fitted BOM on a layout

1. Activate the layout sheet itself, exiting any active detail. Run `PlaceBOM`; it stops before any other prompts if you are not in page space.
2. Choose the assembly.
3. Check the desired columns and press **Confirm**. At least one is required. Available columns are Item, Material, Description, Thickness, Size, Quantity, Unit, Part Area, Parts / Notes, and Source. All except Part Area and Source are selected by default each time. The compact dialog keeps instructions and Confirm/Cancel visible; only the checklist scrolls when the window is shortened.
4. Pick two opposite corners of the rectangle. Either corner order works. The preview shows the requested bounds; cancelling before completion adds no table.

The **Bill of Materials** table contains sheet-stock estimates, hardware aggregated using the same grouping as BOM CSV, and unaccounted-part reasons. Columns retain consistent meanings across sections; fields that do not apply stay blank. Hardware is not counted as sheet stock. The table fills the selected width and uses only the height needed, up to the rectangle's maximum height. It wraps words and long identifiers at the standard 0.125-inch paper text size (converted to layout units), then shrinks the font only if the table does not fit. It rejects rectangles requiring text below 0.02 inches rather than truncating content; choose a larger rectangle or fewer columns. Unspecified/custom layout units are rejected. This paper-unit handling does not change the estimator's model/stock unit limitation.

The result is grouped text and grid geometry on the active sheet under `ANNO::Material Estimates`. A failed insertion removes its newly added table objects. `PlaceMaterialEstimate` remains a compatibility alias with this same checklist/rectangle workflow. The Assembly Manager **Place BOM** button uses its selected assembly. Material-estimate CSV/JSON remains a sheet-stock report; use BOM CSV for exported hardware rows. Placed tables are snapshots and must be placed again after changes.

Library CSV is not a full-fidelity backup: custom Properties and parent materials with no stock shapes are not fully represented, and embedded line breaks do not safely round-trip through the current line-based importer. Use JSON for these cases. Exact file fields are described in [Material Library Format](MaterialLibraryFormat.md) and [Export Schemas](ExportSchemas.md).

## Settings and saved data

### Defaults

| Setting | Default | Scope / meaning |
| --- | --- | --- |
| Default Part Prefix | `P` | Used for new assemblies; does not rename existing parts. |
| Default Component Prefix | `C` | Used for new assemblies; does not rename existing components. |
| Colorize generated part layers | On | Part-category layer styling; per-object overrides can still control display. |
| Enable linked assemblies | On | Emergency master switch; off stops capture and linked updating. Re-enable validates a suspension snapshot. |
| Automatically propagate changes in assembly | Off | Global geometry/categorization preference; tracking continues while propagation is paused if linking is enabled. Saved explicit choices are retained. |
| Length / Edge Tolerance | `0.001` | Categorization comparison tolerance in model length units. |
| Area Tolerance | `0.01` | Categorization area tolerance in squared model units. |
| Volume Tolerance | `0.01` | Categorization volume tolerance in cubed model units. |
| Arrangement Tolerance | `0.01` | Feature-arrangement comparison tolerance; see algorithm reference for normalization. |
| Print part categorization debug output | Off | Creation diagnostics and a detailed categorization report. |
| Part Spacing | `18` | Flat-layout spacing in model units. |
| Layout template path | Empty | Saved `.3dm` source for `NewLayout`. |

Positive numeric values are required in the settings dialog. Defaults shown are for a new settings record; an existing installation can retain its saved preferences. Changing a tolerance or library entry is not itself a blanket recategorization of every document.

### What is saved where?

| Location | Data |
| --- | --- |
| Current `.3dm` document strings | Assembly records, component/part/hardware records, link graph and transforms, conflicts, reconciled pending edits, staged component plans, report caches, local action history, project information. |
| Rhino object attributes/user strings | Material and hardware metadata plus link/recovery identities. These complement, not replace, the document graph. |
| Rhino persistent plugin settings | Shared material library, automatic-update preference, other settings, layout-template path. These are not bundled into each model as the authoritative shared library. |
| Exported files / imported resources | Explicit snapshots or imports. JSON/CSV, STEP hardware sources, and layout templates are not watched for live changes. |

`SetProjectInfo` writes `PROJECT NAME`, `PROJECT #`, `CLIENT`, `DELIVERABLE`, `DELIVERABLE #`, `REVISION`, `STATUS`, `PROJECT MANAGER NAME`, `DESIGNER NAME`, and `MISC.` to document strings for use by title-block fields.

Unreadable or newer unsupported assembly metadata is rejected with an error rather than replaced by an empty store. Legacy source-to-original references can migrate when there is reliable evidence; older copied/flat geometry without saved transforms cannot be reliably reconstructed as links merely from its layer name. Keep a model backup before migration or regeneration.

## Issues, recovery, and current limits

An issue count describes saved review records, not a count of changed parts or a guarantee that all model geometry has been checked. A zero count does not validate purchases, unsupported edits, or every drawing annotation.

| Situation | Operator action |
| --- | --- |
| A supported edit has not appeared downstream | Check **Enable linked assemblies**, whether automatic propagation is off, and staged additions. With linking enabled, run **Update Assembly** and inspect both command history and issues. |
| Updates blocked after re-enabling linking | Review the suspension issue. Restore the pre-disable assembly state or carefully recreate that assembly; do not expect a second toggle to infer missed placements. Unchanged assemblies can resume independently. |
| Part/component number did not change | Check actual equivalence/material assignment and tolerance settings. Incomplete source evidence in the affected category can prevent safe recategorization. |
| Unexpected component membership issue after regrouping | Keep every retained member and run **Update Component** on the complete replacement group. Ordinary regrouping is not approval of a structural change. |
| Copied/flat geometry was reshaped accidentally | Use Undo to restore it, or manually update from the authoritative input if that is the intended design. Do not expect that branch's shape to be promoted. |
| Input and original were both edited | Preserve a backup and choose the intended design by restoring the conflicting side before proceeding. No general source-choice conflict wizard exists. |
| An object was split/joined/deleted, or its identity is ambiguous | Restore the prior supported structure through Undo/backup or recreate the affected assembly. Refresh Issues alone does not repair the mapping. |
| A component split leaves a copied-view gap | Use `PlaceComponent` for the missing type after updating. Existing views remain tied to their original occurrences; a Copy / Orient run still appends all types. |
| The assembly was edited while Gazelle was unloaded | This is outside the 1.1.0 operating requirement: keep the latest Gazelle running with linking enabled on every editing machine. Preserve a backup before updating. A previously deferred original edit does not retain a full source-geometry baseline across restart; a separate unobserved input edit can be overwritten. Resolve which geometry to keep before updating. |
| Undo/redo health warning | Inspect the restored geometry and links. The check is intentionally read-only; decide whether a subsequent manual update is appropriate. |
| A material estimate looks wrong | Check model/library units, thickness, assigned parent, stock dimensions/efficiency, and the unaccounted list. Regenerate after updates. |

### Removal warning

**Remove Assembly is broader than safe empty-layer cleanup.** It gathers tracked generated objects, even if they have been moved outside the output trees, and every object under the selected assembly's current and legacy output trees, including untracked user objects placed there. Keep independent design geometry and valuable annotations outside those trees; moving tracked output elsewhere does not detach or protect it. Normal input objects outside the output trees are not intended deletion targets.

If another assembly uses removed output as its input, that input disappears; the dependent assembly's own outputs are preserved and marked for review. Deletion failures do not currently prevent metadata removal, so inspect for leftovers and save a backup before removing an assembly. Do not use removal as an automatic conflict-repair shortcut.

### What is not implemented

- General one-to-many split propagation, merge/join resolution, or structural member removal.
- General relink/detach/source-choice conflict controls or a dedicated staged-plan cancellation UI.
- Live update of every expanded block-definition leaf.
- Automatic missing-view generation or replace-existing copied-component generation; individual views can be added explicitly with `PlaceComponent`.
- Live maintenance of all detail labels, leaders, dimensions, estimate tables, or exported files.
- Exact nesting/toolpath generation or automatic material-unit conversion.
- A guarantee of fully observed edits while the plugin is unloaded, or atomic rollback of every multi-service update failure.

The [code audit](CodeAudit.md) separates verified functionality, known implementation gaps, and test coverage. The unobserved-input/deferred-original and initial hardware-name findings remain open and are accepted for 1.1.0 under the operating requirements above. Other documented gaps also remain; this guide does not imply they were fixed.

## Further reading

- [Release Notes](../CHANGELOG.md): version 1.1.0 changes and operating requirements.
- [Assembly Manager Walkthrough](AssemblyManagerWalkthrough.md): guided day-to-day sequence.
- [Quick Command Reference](CommandReference.md): compact command index.
- [Detailed Command Reference](DetailedCommandReference.md): prompts, options, and side effects.
- [Part Categorization Algorithm](PartCategorizationAlgorithm.md): comparison and numbering rules.
- [Linked Assembly Architecture](LinkedAssemblyArchitecture.md): implementation and remaining design work.
- [Material Library Format](MaterialLibraryFormat.md) and [Export Schemas](ExportSchemas.md): data interchange.
- [Code Audit](CodeAudit.md) and [regression runner](../../tests/Gazelle.Regression/README.md): review findings and verification limits.
