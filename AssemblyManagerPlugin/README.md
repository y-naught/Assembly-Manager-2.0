# Gazelle

Gazelle is a Rhino 8 plugin for turning a fabrication model into organized assembly geometry, drawing geometry, material estimates, and BOM exports.

The main workflow is the Assembly Manager. It takes grouped Rhino geometry, identifies equivalent parts and components, creates a managed `ASSEMBLY MANAGER` layer structure, and then gives you tools for laying parts flat, copying component views for drawings, assigning materials, carrying hardware through the system, and exporting the information needed for fabrication.

## What Gazelle Does

- Builds a managed assembly from grouped closed polysurfaces, extrusions, and supported block instances.
- Categorizes matching parts with geometry fingerprints, material assignment, and feature-arrangement checks.
- Categorizes matching components based on the parts and hardware inside each component group.
- Passes imported hardware through without analyzing it as manufacturable sheet parts.
- Creates `ORIGINAL ASSEMBLIES`, `COPIED COMPONENTS`, and `PARTS` branches under an `ASSEMBLY MANAGER` parent, plus top-level `HARDWARE` and `ANNO` trees.
- Persists linked object identities and transforms so one-to-one source changes—and safe closed-BREP edits in `ORIGINAL ASSEMBLIES`—update generated descendants automatically, or wait for **Update Assembly** when automatic propagation is disabled.
- Lays one representative of each unique part flat for CAM or nesting review.
- Groups flat parts by assigned material and material thickness.
- Stores a persistent material library with parent materials and purchasable stock shapes.
- Assigns parent materials directly to Rhino objects before assembly generation.
- Estimates sheet counts by material, thickness, and available sheet size.
- Places material estimate tables in layout space and exports estimates as CSV or JSON.
- Generates and exports BOM rows from material estimates and hardware quantities.
- Imports a saved layout template with `NewLayout`.
- Adds detail labels, automatic detail dimensions, and layer-name leaders in layout space.

## Basic Workflow

1. Model each physical component as a Rhino group.
2. Make sure manufacturable parts are closed polysurfaces or extrusions.
3. Use `AssignMaterials` to assign parent materials before creating the assembly.
4. Use `ImportHardware` for STEP hardware that should pass through categorization.
5. Open `AssemblyManager` and create the assembly from the grouped model.
6. Use `LayPartsFlat` for CAM prep.
7. Use `CopyOrientComponents` for linked drawing copies that can be repositioned without losing their source relationship.
8. Use `EstimateMaterials`, `PlaceMaterialEstimate`, `GenerateBom`, and `ExportBom` for reporting.

9. Use `LabelDetail`, `LabelPart`, `LabelParts`, and `DimDetail` in layout space for documentation.

Supported geometry and assigned-material edits to a design source or a linked closed BREP under `ORIGINAL ASSEMBLIES` propagate through the graph. Gazelle then reconciles part and component identity: unchanged cohorts keep their existing numbers, while a genuinely divergent occurrence receives the next unused `P##`/`C##` number.

To batch edits, turn off **Automatically propagate changes in assembly** in Assembly Manager settings. Object identities and placement transforms continue to be tracked, but geometry propagation and recategorization wait for **Update Assembly** (formerly **Refresh References**). Pending supported edits are saved in the model; the existing `RefreshAssemblyReferences` command uses the same update workflow. Re-enabling the toggle resumes pending automatic work. Editing both a source and its generated original before updating is ambiguous and remains a visible link issue rather than silently choosing one edit.

## Important Model Rules

Gazelle expects one component per Rhino group. The group can contain many parts and hardware instances. Selecting one object in the group expands to the whole group during assembly creation.

Parts should be closed polysurfaces or extrusions. Curves, points, single surfaces, and open polysurfaces are ignored or warned about because they cannot be treated as reliable manufacturing parts.

Ordinary unmarked blocks are expanded and analyzed as normal part geometry. Imported hardware blocks are different: `ImportHardware` marks the block definition geometry and block instances as hardware, so Gazelle carries them through the assembly and BOM without trying to fingerprint them as sheet parts.

## Documentation

- [Quick Command Reference](Docs/CommandReference.md)
- [Detailed Command Reference](Docs/DetailedCommandReference.md)
- [Assembly Manager Walkthrough](Docs/AssemblyManagerWalkthrough.md)
- [Material Library Schema](Docs/MaterialLibraryFormat.md)
- [Export Schemas](Docs/ExportSchemas.md)
- [Part Categorization Algorithm](Docs/PartCategorizationAlgorithm.md)
- [Linked Assembly Architecture](Docs/LinkedAssemblyArchitecture.md)

## Data And Persistence

Assembly records are saved into the active Rhino document as JSON. Plugin settings, saved layout template path, and the shared material library are stored in Rhino's persistent plugin settings so they can be reused across models.

Material assignments, hardware metadata, and link recovery IDs are stored on Rhino object attributes as user strings. The authoritative version 2 link graph is stored in the Rhino document JSON and records immediate parent-to-child transforms through original assemblies, drawing copies, and flat parts.
