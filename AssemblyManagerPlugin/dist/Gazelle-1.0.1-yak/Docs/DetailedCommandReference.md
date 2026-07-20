# Gazelle Detailed Command Reference

This document is the fuller command reference for Gazelle. It is written for the person using the plugin in Rhino, not for someone reading the code.

## Main Assembly Commands

### `AssemblyManager`

Opens the main Assembly Manager window.

Use this for the normal workflow. The window lets you create an assembly, select existing assemblies, see component types, see the part list for a component, remove assemblies, lay parts flat, copy drawing geometry, estimate materials, place/export estimates, generate BOM data, open settings, and open the material library.

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
- Copies generated geometry to `SHOP::<assembly>`.
- Creates component and part layers under `SHOP`.
- Stores source object references so generated `SHOP` geometry can be refreshed later.
- Saves assembly metadata in the Rhino document.

Important behavior:

- Curves, points, point clouds, single surfaces, and open polysurfaces are skipped or warned about.
- Same geometry with different assigned parent materials becomes different part records.
- Imported hardware contributes to component identity, but it does not become a flat sheet part.

### `RemoveAssembly`

Deletes a managed assembly.

It removes assembly metadata, generated objects, generated groups, and the managed layer trees for the assembly. It is meant to clean up the Gazelle output, not the original source model.

### `RefreshAssemblyReferences`

Refreshes generated `SHOP` geometry from the original source objects.

Use this when source geometry has been edited after assembly creation and you want generated `SHOP` geometry updated from those stored references. This is not a replacement for every future reference/update feature, but it is the first working source-refresh path.

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

Important behavior:

- Marked hardware is carried through assembly creation and BOM generation.
- Marked hardware is copied into `SHOP::<assembly>::<component>::<hardware name>`.
- Marked hardware is copied with drawing components through `CopyOrientComponents`.
- Ordinary blocks that were not imported or marked as hardware are analyzed like regular model geometry.

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

### `ImportMaterialLibrary`

Imports JSON or CSV material library data.

Import is a merge/update operation. Matching material ids or names update existing parent material records. Matching stock-shape ids or names update existing stock-shape records.

### `ExportMaterialLibrary`

Exports the shared material library as JSON or CSV.

The Material Library window export button is the preferred way to do this. The command still exists for command-line use when Rhino registers it.

### `AssignMaterials`

Assigns one parent material to selected Rhino objects.

Workflow:

- Preselect objects or start the command and select objects.
- Choose a parent material.
- Gazelle writes material user strings to the selected object attributes.

This does not choose a sheet size. Sheet size and stock shape are resolved later during material estimating.

### `AssignMaterialToPart`

Assigns a material to one generated part type in an existing assembly.

This is useful for cleanup, but the preferred workflow is still to assign materials to source geometry before creating the assembly. Pre-assignment gives cleaner categorization and cleaner generated labels.

## Manufacturing And Estimates

### `LayPartsFlat`

Lays one representative of each unique part flat onto `CAM::<assembly>`.

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

### `PlaceMaterialEstimate`

Places the current material estimate as a grouped table in layout/page space.

This must be run from layout space. The table is drawn on `ANNO::Material Estimates`.

### `ExportMaterialEstimate`

Exports the material estimate as CSV or JSON.

The CSV includes a material section and an unaccounted section. The JSON export uses the full material estimate report structure.

### `GenerateBom`

Generates BOM rows from the assembly.

What it uses:

- Sheet/stock rows from the current material estimate.
- Hardware rows from hardware carried through the assembly.

The command regenerates the material estimate first, then builds the BOM.

### `ExportBom`

Exports the BOM as CSV.

The command regenerates the material estimate and BOM before writing the CSV, so the export reflects the current assembly record and material library.

## Drawings, Layouts, And Annotation

### `CopyOrientComponents`

Copies one representative of each component type to `DRAWINGS::<assembly>`.

Use this for drawing views and manual documentation setup. The copied geometry is intended to be moved, rotated, and edited for drawing presentation.

Do not treat `SHOP` geometry the same way. `SHOP` is Gazelle-managed output and is used by other features. If you need to change the design, edit the source model and refresh or recreate the assembly.

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

- `Assembly`: looks at `SHOP` geometry and labels visible component groups.
- `Component`: looks at `DRAWINGS` geometry and labels visible part layers.

### `LabelPart`

Adds a page-space leader in layout space.

Workflow:

- Run from layout/page space.
- Pick the leader tip on top of an object visible through a detail.
- Gazelle finds the visible model object under the leader tip.
- The leader text is the leaf layer name of that object.
- Place leader points with a live preview.
- If multiple objects are effectively tied under the tip, Gazelle asks which layer to label.

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

## Utility Geometry

### `Regroup`

Removes old grouping from selected objects and creates a new Rhino group.

### `MoveOrtho`

Moves selected objects along one chosen world axis.

Prompts for X/Y/Z, start point, and end point. The geometry previews live while the end point is being picked.

### `MotionTrace`

Moves selected objects and leaves trace linework.

The command creates hidden-style start edges, final edges, and connector lines between corresponding moved endpoints or bounding-box corners.

### `OrientToWorld`

Rotates selected objects around World Z to minimize their world bounding box.

This is useful for quickly straightening parts or imported geometry before other operations.

### `Split3Pt`

Splits selected polysurfaces or extrusions with a plane defined by three points.

Options:

- `Cap=Yes` by default. When enabled, Gazelle tries to cap planar split openings.

The cutting plane is extended to cover each selected object's bounding box before splitting.

## Settings

Open settings with `AssemblyManagerSettings` or the Settings button in the Assembly Manager window.

| Setting | Default | What it controls |
| --- | --- | --- |
| Default Part Prefix | `P` | Prefix used for generated part names, such as `P01`. |
| Default Component Prefix | `C` | Prefix used for generated component names, such as `C01`. |
| Colorize Generated Part Layers | On | Whether generated part layers receive cycling colors. When off, generated part layers are black. |
| Length / Edge Tolerance | `0.001` | Rounding tolerance for edge lengths, dimensions, and component centroid distance tokens. |
| Area Tolerance | `0.01` | Rounding tolerance for part surface area in fingerprints. |
| Volume Tolerance | `0.01` | Rounding tolerance for part volume in fingerprints. |
| Arrangement Tolerance | `0.01` | Rounding tolerance for feature-arrangement distances and component layout checks. |
| Print Part Categorization Debug Output | Off | Writes a JSON debug report showing each compared part payload and assigned part number. |
| Lay Parts Flat Part Spacing | `18.0` | Horizontal spacing between flat parts. Row spacing is derived from this and kept larger for readability. |
| Layout Template Path | blank | Saved `.3dm` template used by `NewLayout`. |

Debug reports are written next to the Rhino file in `AssemblyManagerDebugReports` when possible. If the Rhino document has no saved path, reports are written to `~/Documents/AssemblyManagerDebugReports`.
