# Gazelle release notes

## 1.1.0 — linked assemblies and drawing workflows

Release preparation: September 26, 2026. These notes describe the 1.1.0 package target; publication to Yak is a separate step.

### Linked assemblies

- Persistent object relationships and placement transforms connect design inputs, `ORIGINAL ASSEMBLIES`, `COPIED COMPONENTS`, and flat `PARTS` output.
- Supported input edits and safe one-to-one closed-BREP original edits can propagate to linked output. Updates reconcile part/component numbers, quantities, managed layers, category colors, and existing flat geometry/labels. Divergent occurrences can become new categories; matching ones can join an existing category.
- **Update Assembly** applies pending changes, reports progress in Rhino's command line, and leaves review items visible in the Assembly Manager's bottom **Link Issues** panel. The existing `RefreshAssemblyReferences` command runs this workflow.
- **Automatically propagate changes in assembly** defaults **off**; explicitly saved preferences are retained. Tracking continues while propagation is paused. **Enable linked assemblies** defaults **on** and is an emergency stop, not the normal batching control. Re-enabling validates a suspension snapshot and blocks unsafe changed assemblies.
- Copied-component placement tracking updates affected relationships without rewriting every link. Moved hardware block instances retain their final tracked drawing placement during later updates.
- Part colors use the initial 21-color palette, then allocate randomized additional category colors. Category colors are retained across managed output stages.

### Components and drawings

- `UpdateComponent` registers an original occurrence after ungrouping, adding parts or marked hardware, and regrouping. **Update Assembly** applies the staged additions only to that occurrence and its eligible linked views. Other occurrences are not changed; the edited occurrence is recategorized as needed. Structural removal/split/join is not implemented.
- `PlaceComponent` places an additional independently tracked, plan-oriented view of a selected component. It automatically uses the sole assembly, otherwise prompts for one. Extra views preserve their own moved/rotated placements and do not increase manufacturing or BOM quantities.
- Multiple legitimate component views remain supported through category changes and staged additions. `CopyOrientComponents` still appends a complete set; use `PlaceComponent` for a specific missing or additional view.

### BOM placement

- `PlaceBOM` requires an active layout sheet, prompts for an assembly, and opens a compact column checklist with **Confirm**. Choose two opposite corners to fit the table into a rectangle.
- The placed table includes sheet-stock estimates, hardware quantities, and unaccounted-part reasons. It uses the selected width and wraps at standard 0.125-inch paper text size first. Text shrinks only if the wrapped table exceeds the available height or an extremely narrow width cannot accommodate the minimum column contents and padding. Fits requiring less than 0.02-inch text are rejected rather than truncated. Paper sizes are converted to layout units.
- `PlaceMaterialEstimate` remains a compatibility alias for the new checklist/rectangle workflow. CSV/JSON report schemas are unchanged. Placed tables and exported reports remain snapshots; update the assembly and regenerate them after changes.

### Required operating practices and known limits

- On every machine editing managed models, run the latest Gazelle release with **Enable linked assemblies** turned on; keep collaborators on the same current release. Automatic propagation can remain off. Let Rhino finish processing, run **Update Assembly**, review issues, and save before handing off a model or using fabrication reports. Keep backups.
- A deferred original edit can overwrite a newer input edit that happened while Gazelle was unloaded or tracking was bypassed. The source-shape-baseline fix is deferred, not implemented in 1.1.0. Loading Gazelle after an unobserved edit does not reconstruct the missing history.
- Hardware identifiers must avoid generated part numbers and the configured part-prefix/number namespace: for example, use `HINGE-01`, not `P01`. Initial creation does not enforce a collision-proof namespace; this fix is also deferred.
- Later hardware material edits can leave saved reporting material stale. Assign hardware materials before creation and verify reports after later edits. Stock/model dimensions must use matching numeric units; non-inch flat thickness labels need review. The estimator is not an exact nesting or toolpath system.
- General split/join propagation, member removal, relink/detach/source-choice controls, and universal live drawing/report updates are not implemented. Removal can include untracked work placed inside managed output trees; save a backup and keep independent work outside those trees.

See the [User Guide](Docs/GazelleUserGuide.md) for all 32 command entrypoints and operating steps, and the [Code Audit](Docs/CodeAudit.md) for evidence, remaining findings, and the scope of automated verification. Older packages retain their own documentation and behavior.
