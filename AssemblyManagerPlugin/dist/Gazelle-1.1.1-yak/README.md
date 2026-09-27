# Gazelle

Gazelle is a Rhino 8 plugin for turning a fabrication model into organized assembly geometry, drawing geometry, material estimates, and BOM exports.

Start with the [consolidated Gazelle User Guide](Docs/GazelleUserGuide.md) for all 33 commands, settings, operating steps, and the linked-update system. This documentation covers Gazelle `1.1.1`, including input-component additions, regrouping, and safe member removal. See the [release notes](CHANGELOG.md) for the update summary; historical packages under `dist` retain their original documentation.

The main workflow is the Assembly Manager. It takes grouped Rhino geometry, identifies equivalent parts and components, creates a managed `ASSEMBLY MANAGER` layer structure, and then gives you tools for laying parts flat, copying component views for drawings, assigning materials, carrying hardware through the system, and exporting the information needed for fabrication.

## What Gazelle Does

- Builds a managed assembly from grouped closed polysurfaces, extrusions, and supported block instances.
- Categorizes matching parts with geometry fingerprints, material assignment, and feature-arrangement checks.
- Categorizes matching components based on the parts and hardware inside each component group.
- Passes imported hardware through without analyzing it as manufacturable sheet parts.
- Creates `ORIGINAL ASSEMBLIES`, `COPIED COMPONENTS`, and `PARTS` branches under an `ASSEMBLY MANAGER` parent, plus top-level `HARDWARE` and `ANNO` trees.
- Persists linked object identities and transforms so one-to-one source changes—and safe closed-BREP edits in `ORIGINAL ASSEMBLIES`—update generated descendants automatically, or wait for **Update Assembly** when automatic propagation is disabled.
- Lays one representative of each unique part flat for CAM or nesting review.
- Groups flat parts into rows by assigned material and material thickness, with labels under each part's layer tree.
- Stores a persistent material library with parent materials and purchasable stock shapes.
- Assigns parent materials directly to Rhino objects before assembly generation.
- Estimates sheet counts by material, thickness, and available sheet size.
- Places combined material/hardware BOM tables in a selected layout rectangle, with selectable columns and wrap-first text fitting, and exports material estimates as CSV or JSON.
- Places additional individually tracked drawing views with `PlaceComponent`, without increasing manufacturing quantities.
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
7. Use `CopyOrientComponents` for a full set of linked drawing views, or `PlaceComponent` for an individual additional view. Each can be repositioned without losing its source relationship.
8. Finish **Update Assembly** and review issues before recalculating with `EstimateMaterials`, placing a rectangle-fitted table with `PlaceBOM`, or using `GenerateBom` and `ExportBom` for reporting.
9. Use `LabelDetail`, `LabelPart`, `LabelParts`, and `DimDetail` in layout space for documentation.

Supported geometry and assigned-material edits to a design source or a linked closed BREP under `ORIGINAL ASSEMBLIES` propagate through the graph. Gazelle then reconciles part and component identity: unchanged cohorts keep their existing numbers, while a genuinely divergent occurrence receives the next unused `P##`/`C##` number.

**Automatically propagate changes in assembly** now defaults off (an explicitly saved preference is retained). Object identities and placement transforms continue to be tracked, but geometry propagation and recategorization wait for **Update Assembly** (formerly **Refresh References**). Pending supported edits are saved in the model; the existing `RefreshAssemblyReferences` command uses the same update workflow. Enabling the toggle resumes eligible pending automatic work. Editing both a source and its generated original before updating is ambiguous and remains a visible link issue rather than silently choosing one edit.

The separate **Enable linked assemblies** setting defaults on. Turning it off is an emergency stop for tracking and automatic/manual linked updates, not the routine batching control. Re-enabling checks a saved suspension snapshot: unchanged assemblies resume, while changed ones remain protected from stale-transform overwrites until restored or recreated. See the User Guide before using this switch.

Copied-component moves retain both solid and hardware-block placement for later updates. Part colors keep the first 21 predefined colors, then allocate random additional colors once per category; saved colors remain consistent across original, copied, and flat output. **Place BOM** (`PlaceBOM`) includes hardware quantities alongside stock estimates and unaccounted parts. Choose columns, then two opposite layout corners: the table fills the width, wraps text at standard paper size first, and reduces the text only when needed to fit. `PlaceMaterialEstimate` remains an alias for this same workflow.

To add one part or marked hardware directly to an existing **input component group**, run `AddPartToComponent`, select a member of that registered input group, then select the new object. The object joins the input group without being moved or replaced; **Update Assembly** later creates its linked output and refreshes categories and quantities.

Alternatively, ungroup and regroup that **input component** with the desired members, retaining at least one original linked source member. Gazelle recognizes a uniquely identifiable replacement group by those objects' UUIDs, not its name. A rename or same-members replacement updates the group identity without a production change; additions and omissions are staged for **Update Assembly**. Excluded input objects may remain in the model: updating detaches them from Gazelle and removes only their safely owned downstream output. Do not mix component occurrences or use this to split/join existing members. The separate **ORIGINAL ASSEMBLIES → Update Component** workflow remains additive and still requires explicit registration.

You can also delete an individual part or hardware object directly from the registered **input group** while linking is enabled. Keep at least one existing linked member in that group, then click **Update Assembly**. No regrouping is required: Gazelle stages the observed deletion and removes that occurrence's safely owned original/copied output, with quantities and existing flat output reconciled. Deleting every identifying member, split/join operations, shared ownership, and unsafe downstream dependencies remain protected. A previously missing source is not automatically treated as removal intent; for a deletion already processed by an older build, regroup the remaining input members and update.

Only the edited physical occurrence changes: other matching components retain their number, while a divergent occurrence receives a new number or joins an existing matching category. An edited singleton keeps its number unless it matches another category. Structural edits wait for manual updating even when automatic propagation is enabled. See the walkthrough for ownership, removal, and copied-output restrictions.

## Important Model Rules

Every machine editing a managed model must run the latest Gazelle release with **Enable linked assemblies** on. Keep the same current release across collaborators; installing the plugin without loading/running it does not observe edits. Automatic propagation may remain off for normal editing, provided tracking stays enabled and **Update Assembly** is run before reporting or fabrication. Let Rhino finish processing before saving, and keep backups. Gazelle cannot reconstruct all changes made while it was unloaded or tracking was bypassed.

Gazelle expects one component per Rhino group. The group can contain many parts and hardware instances. Selecting one object in the group expands to the whole group during assembly creation.

Keep component groups unambiguous: a candidate is assigned by its first group, and all accepted ungrouped objects share one component bucket. Separate intended components should be explicitly grouped.

Parts should be closed polysurfaces or extrusions. Curves, points, single surfaces, and open polysurfaces are ignored or warned about because they cannot be treated as reliable manufacturing parts.

Ordinary unmarked blocks are expanded and analyzed as normal part geometry. Imported hardware blocks are different: `ImportHardware` marks the block definition geometry and block instances as hardware, so Gazelle carries them through the assembly and BOM without trying to fingerprint them as sheet parts.

Hardware identifiers must stay outside the assembly's configured manufacturing-part namespace: use `HINGE-01`, not `P01` when the part prefix is `P`. Initial creation does not enforce a collision-safe namespace.

## Important Current Limits

- Input regrouping can stage additions/removals only when retained source UUIDs prove one unique component occurrence. Observed ordinary deletions from its registered input group also stage removals without regrouping. Keep at least one retained member, avoid mixed/shared or ambiguous groups, and apply with **Update Assembly**. `AddPartToComponent` and original-side `UpdateComponent` remain additive commands. General split/join replacement and removing an entire occurrence through an empty group are not supported.
- Repeating `CopyOrientComponents` appends another set for every component type. Use `PlaceComponent` for a specific additional or missing view; it uses the current representative occurrence, not an arbitrary edited drawing copy. Rerunning `LayPartsFlat` reflows safe flat output, whereas **Update Assembly** preserves supported existing flat placement.
- Material estimates compare model and stock dimensions without unit conversion. Flat thickness labels currently append inch marks to raw model values. Use matching units and review non-inch output carefully.
- Reports, placed tables, detail annotations, and exported files are not all live-linked. Update the assembly, then regenerate the needed reports.
- `RemoveAssembly` also deletes untracked objects under its managed/legacy output layer trees. Keep independent work outside those trees and save a backup first.
- Initial hardware identifiers can collide with `P##` part names, and later hardware material changes can leave saved BOM material stale. Avoid part-number-like hardware identifiers and verify hardware reporting after edits.
- Deferred original edits do not retain a complete source-shape baseline across listener restart; independently changing input while Gazelle is unloaded can make resuming that original edit unsafe.

The deferred-edit and hardware-name limitations remain open, accepted for this release under the operating rules above; they have not been fixed. See the [code audit](Docs/CodeAudit.md) for evidence, the release decision, other current limitations, and verification coverage.

## Documentation

- [Consolidated User Guide — start here](Docs/GazelleUserGuide.md)
- [Release Notes](CHANGELOG.md)
- [Quick Command Reference](Docs/CommandReference.md)
- [Detailed Command Reference](Docs/DetailedCommandReference.md)
- [Assembly Manager Walkthrough](Docs/AssemblyManagerWalkthrough.md)
- [Material Library Schema](Docs/MaterialLibraryFormat.md)
- [Export Schemas](Docs/ExportSchemas.md)
- [Part Categorization Algorithm](Docs/PartCategorizationAlgorithm.md)
- [Linked Assembly Architecture](Docs/LinkedAssemblyArchitecture.md)
- [Code Audit and Verification](Docs/CodeAudit.md)
- [Regression Runner](https://github.com/y-naught/Assembly-Manager-2.0/blob/v1.1.1/tests/Gazelle.Regression/README.md)

## Data And Persistence

Assembly records are saved into the active Rhino document as JSON. Plugin settings, saved layout template path, and the shared material library are stored in Rhino's persistent plugin settings so they can be reused across models.

Material assignments, hardware metadata, and link recovery IDs are stored on Rhino object attributes as user strings. The authoritative version 2 link graph is stored in the Rhino document JSON and records immediate parent-to-child transforms through original assemblies, drawing copies, and flat parts.

Pending changes are persisted after event bookkeeping is processed; there is no dedicated before-save event-queue flush. Let Rhino finish processing before saving/closing. Plugin settings/materials are shared across models, while the assembly graph and project fields belong to the model. STEP sources, layout templates, and exported reports are not watched for live changes.
