# Gazelle release notes

## 1.1.1 — input-side component editing and removal fixes

Release date: September 26, 2026.

- Fixed a false removal rejection for views made by **Copy / Orient Components**: new copies retain their parent occurrence ID, and existing copies missing only that ID can be removed through their proven, unambiguous parent links. Conflicting ownership and genuine downstream dependencies still block removal. A deletion already staged by the previous build can be retried with **Update Assembly** without recreating the assembly.
- Fixed direct input-member deletion: deleting a part or marked hardware from its registered input component now stages removal without requiring regrouping, provided an existing linked member remains. **Update Assembly** removes the safely owned original/copied output and reconciles quantities, categories, hardware, and existing flat output for only that occurrence, even when automatic propagation is enabled. Split/join evidence, all-members deletion, shared ownership, and unsafe downstream dependencies remain protected; unobserved or historical missing sources are not inferred as deletion intent.
- Fixed material-only recategorization of unassigned/TBD parts. An added shape can initially receive its own TBD number, then merge into an existing matching shape/material category after material assignment. The existing category's number/color is retained, quantities combine, and eligible copied/flat output follows the merge. Accepted empty material identity is no longer overwritten during repository load/save; manual-update preferences remain respected.
- `AddPartToComponent` selects a registered input component group and one new closed solid, extrusion, or marked hardware object. It adds the selected object to the input group without replacing its UUID or moving it, and stages the addition for **Update Assembly**.
- Downstream creation waits for manual updating even when automatic propagation is enabled. Only the selected physical occurrence changes; component/part categories and quantities are reconciled, and eligible existing copied views retain their individual tracked placements.
- Input-group renaming and uniquely recognizable replacement groups retain their assembly/occurrence identity. Gazelle matches retained source UUIDs, not names; at least one retained source member is required. A same-members regroup only rebinds the group. Adding or omitting members stages a structural change for **Update Assembly**, including when automatic propagation is on.
- Input regrouping can remove parts or hardware from one occurrence. Surviving excluded input objects stay in the model and lose Gazelle ownership; only safely owned originals/copied descendants are removed. Quantities, categories, and existing flat output are synchronized. A surviving part category can retain its flat representative through proven reparenting; a retired category's safe flat output and owned labels are removed, not unrelated notes.
- Further input regrouping replaces that occurrence's pending membership; restoring its original membership cancels the pending change. Mixed/shared or ambiguous groups, removal of every identifying source member, dependent output used by another assembly, unsupported geometry, and general split/join replacements remain protected. `UpdateComponent` remains the separate **additive-only** original-side workflow and cannot be mixed with a pending input plan for the same occurrence.
- Guides now list 33 commands. Historical `1.1.0` packages and their documentation are unchanged.
- Verification: production Debug and Release builds pass with zero warnings/errors, and all **173 default regression cases pass against both builds**. Four native layout cases remain separately opt-in; desktop interaction, Undo/Redo, and full save/close/reopen coverage retain the limitations documented in the audit.

The operating requirements from `1.1.0` still apply: keep the same latest Gazelle running with linking enabled on every editing machine, avoid hardware names that collide with generated part numbers, and regenerate snapshot reports after updating. The deferred source-history and hardware-name protections have not been implemented by this patch. See the [User Guide](Docs/GazelleUserGuide.md) for the 33 commands and the [Code Audit](Docs/CodeAudit.md) for evidence and remaining limits. These release notes describe the package; they are not confirmation of server publication.

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
