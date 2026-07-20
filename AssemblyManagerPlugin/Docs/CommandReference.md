# Gazelle Quick Command Reference

This is the fast version of the command list. For deeper notes, prompts, side effects, and settings, see [DetailedCommandReference.md](DetailedCommandReference.md).

## Main Workflow

| Command | What it does |
| --- | --- |
| `AssemblyManager` | Opens the main Assembly Manager window for creating assemblies, reviewing parts/components, laying parts flat, making drawing copies, estimating materials, and generating BOM data. |
| `CreateAssembly` | Creates a managed assembly from selected grouped model geometry. This is the command-line version of the Create Assembly button. |
| `RemoveAssembly` | Deletes a managed assembly, its generated geometry, generated groups, and managed layer trees. |
| `RefreshAssemblyReferences` | Rebuilds generated `SHOP` geometry from stored source-object references. |

## Manufacturing

| Command | What it does |
| --- | --- |
| `LayPartsFlat` | Lays one representative of each unique part onto `CAM::<assembly>`, grouped by material and thickness. |
| `EstimateMaterials` | Estimates required sheet counts by material, material thickness, and available sheet stock. |
| `PlaceMaterialEstimate` | Places the current material estimate as a grouped table in layout space. |
| `ExportMaterialEstimate` | Exports the material estimate as CSV or JSON. |
| `GenerateBom` | Generates BOM rows from the latest material estimate and assembly hardware. |
| `ExportBom` | Exports the generated BOM as CSV. |

## Hardware

| Command | What it does |
| --- | --- |
| `ImportHardware` | Imports a STEP file, creates a hardware block, marks the block definition and instance as hardware, and places it on `HARDWARE::<name>`. |

## Materials

| Command | What it does |
| --- | --- |
| `MaterialLibrary` | Opens the persistent material library editor. The window includes import and export buttons for JSON/CSV material library data. |
| `ImportMaterialLibrary` | Imports material library data from JSON or CSV. |
| `ExportMaterialLibrary` | Exports material library data to JSON or CSV. The Material Library window export button is the preferred path. |
| `AssignMaterials` | Assigns one parent material to selected Rhino objects. Sheet size is resolved later during estimating. |
| `AssignMaterialToPart` | Assigns a material to one generated part type after assembly creation. |

## Drawings And Layouts

| Command | What it does |
| --- | --- |
| `CopyOrientComponents` | Copies one representative of each component type to `DRAWINGS::<assembly>` and optimizes the plan rotation for drawing work. |
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
| `OrientToWorld` | Rotates selected objects around World Z to minimize their world bounding box. |
| `Split3Pt` | Splits polysurfaces or extrusions with a plane defined by three points, with cap-on-split enabled by default. |
