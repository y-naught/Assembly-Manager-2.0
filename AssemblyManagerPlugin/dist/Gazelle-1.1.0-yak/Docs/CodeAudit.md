# Gazelle code and documentation audit

Original audit date: September 5, 2026; implementation and release-preparation follow-up: September 26, 2026. The original review covered the `Linking-Assemblies` development implementation then carrying version `1.0.3`; the current release target is `1.1.0`. Historical audit and verification sections below retain their original scope. This audit is not evidence that a package has been published; historical packages under `dist` retain their original documentation.

## Result

The implemented functionality is now described in the [consolidated User Guide](GazelleUserGuide.md) and the focused Markdown references. The original audit accounted for all 30 command entrypoints; the later PlaceBOM/PlaceComponent implementation brings the current total to 32, including the retained PlaceMaterialEstimate alias. Documentation separates current behavior, safety restrictions, implementation gaps, and proposed future work.

The September 5 work was a code-inspection and documentation pass with the existing automated regression suite rerun; it made no implementation fixes. The September 26 changes below are a separate implementation follow-up. Passing tests do not establish that every documented command, desktop interaction, or edge case is covered.

## Version 1.1.0 release decision

The maintainer elected to defer A01 and A02 rather than expand this release's engineering scope. Both findings remain open; this is acceptance under explicit operating requirements, not a code fix, reproduced-resolution claim, or reduction of their potential impact:

- **A01:** every machine editing managed files must run the latest Gazelle release with **Enable linked assemblies** on. Collaborators should use the same current release. Automatic propagation may be off while tracking stays on. Edits made while Gazelle is unloaded, outdated, or its tracking is bypassed are not supported as fully observed history. Finish processing and updating before handing off files, and preserve backups.
- **A02:** hardware identifiers must not collide with generated manufacturing part numbers or the configured part-prefix/number namespace. Use distinct names such as `HINGE-01`, not `P01` for a `P`-prefix assembly. Initial creation still lacks a collision-proof naming guard.

A03–A06 and the remaining workflow/test limitations documented here are unchanged. In particular, placed BOMs/exports are snapshots, hardware material records can become stale after later edits, and stock/model dimensions require matching numeric units. See the [1.1.0 release notes](../CHANGELOG.md) and [operator guide](GazelleUserGuide.md) for normal workflow and requirements. Publication and final package verification are separate release steps.

## September 26 implementation follow-up

- Automatic geometry propagation/recategorization now defaults off when no preference was saved. Existing explicit choices are retained. The independent **Enable linked assemblies** master switch defaults on.
- Emergency OFF stops event capture and linked updates. [Suspension safety](https://github.com/y-naught/Assembly-Manager-2.0/blob/v1.1.0/AssemblyManagerPlugin/Services/LinkedAssemblySafetyService.cs) saves per-assembly baselines, allows proven-unchanged assemblies to resume, and blocks changed ones pending restoration/recreation. This is not a fix for every plugin-unloaded deferred-original case in A01.
- [Movement tracking](https://github.com/y-naught/Assembly-Manager-2.0/blob/v1.1.0/AssemblyManagerPlugin/Services/AssemblyLinkEventService.cs) recognizes unchanged-definition block-instance transforms, so moved hardware retains its final copied placement when an assembly is refreshed. Placement metadata is rewritten only for affected relationships. Rigid moves skip unused pre-transform mass-property snapshots without skipping replacement checks; repository recovery lookups are indexed while retaining validation.
- The initial placed **Material & Hardware Estimate** follow-up added hardware aggregated by the same code as BOM CSV. The subsequent **Place BOM** workflow below replaces its single-point command/UI placement. This does not fix the stale saved hardware-material limitation in A03.
- [Color allocation](https://github.com/y-naught/Assembly-Manager-2.0/blob/v1.1.0/AssemblyManagerPlugin/Services/LayerService.cs) retains the first 21 predefined colors and assigns randomized nonduplicate candidates beyond them. Colors are persisted on part records and reused across generated stages; existing custom colors remain authoritative. New component additions respect disabled colorization and live custom category colors.

The open A01–A06 findings below remain follow-up work unless explicitly noted otherwise. Original line-number citations describe the September 5 snapshot and can shift as implementation changes; linked files remain the primary evidence.

### Fitted BOM and individual component placement follow-up

- `PlaceBOM` requires layout/page space before prompts, selects an assembly, opens a column checklist with Confirm, and accepts two opposite corners with a rectangle preview. The Assembly Manager button is **Place BOM**; `PlaceMaterialEstimate` is retained as an alias to this workflow, not a second command implementation.
- [PlacedBomService](https://github.com/y-naught/Assembly-Manager-2.0/blob/v1.1.0/AssemblyManagerPlugin/Services/PlacedBomService.cs) computes a grouped static stock/hardware/unaccounted table with stable column meanings. Native font bounds determine wrapping and proportional column widths. It uses the full selected width, tries standard 0.125-inch paper text first, then shrinks only if needed, and rejects a fit below 0.02 inches. Page units are converted independently of model units; invalid/unspecified/custom units or insufficient bounds are rejected. This is not estimator stock-unit conversion. Insertion is bound-checked and failures roll back newly inserted table geometry.
- `PlaceComponent` uses the sole assembly automatically or prompts among multiple assemblies, then selects a component type and center placement point. [ComponentDrawingService](https://github.com/y-naught/Assembly-Manager-2.0/blob/v1.1.0/AssemblyManagerPlugin/Services/ComponentDrawingService.cs) verifies the complete current original representative before adding a grouped, independently tracked plan-oriented view. Fresh nodes link directly to the original occurrence with its source occurrence ID; extra views do not increase manufacturing/BOM quantities. Whole hardware blocks are supported; unsafe ordinary block-leaf manufacturing links are rejected.
- Component reconciliation now accepts multiple legitimate drawing views, including views of different physical occurrences whose categories merge. Missing categories still produce guidance to PlaceComponent. Existing per-occurrence staged-addition fan-out applies to all complete views at their independent final transforms.
- No release package, installed plugin registration, or user model is changed by this implementation. Interactive validation must use the Visual Studio debugger launch to avoid the installed/development plugin UUID collision.

## Review scope and evidence map

| Area reviewed | Primary implementation | Documentation |
| --- | --- | --- |
| Commands and window actions | `Commands/`, `AssemblyManagerCommand.cs`, `UI/AssemblyManagerDialog.cs` | [Quick](CommandReference.md) and [detailed](DetailedCommandReference.md) command references |
| Creation, grouping, geometry acceptance, hardware | `AssemblyGenerationService`, `GeometryFingerprintService`, `HardwareImportService`, `HardwareMetadata` | [Walkthrough](AssemblyManagerWalkthrough.md), [User Guide](GazelleUserGuide.md) |
| Part/component categorization and layer cleanup | `AssemblyCategorizationReconciliationService`, `GeometryFingerprintService`, `LayerService` | [Categorization](PartCategorizationAlgorithm.md) |
| Identity, transforms, events, pending edits, propagation, undo inspection | `AssemblyRepository`, `AssemblyLineageService`, `AssemblyLinkEventService`, `ReferenceUpdateService` | [Linked architecture](LinkedAssemblyArchitecture.md) |
| Occurrence-specific staged additions | `ComponentUpdateService`, `ComponentPlacementMatcher`, `UpdateComponentCommand` | [User Guide](GazelleUserGuide.md#adding-a-part-or-hardware-to-one-component), [linked architecture](LinkedAssemblyArchitecture.md) |
| Copied/flat output and managed annotations | `ComponentDrawingService`, `LayPartsFlatService`, `FlatPartSynchronizationService` | [User Guide](GazelleUserGuide.md#copied-views-and-flat-parts), [walkthrough](AssemblyManagerWalkthrough.md) |
| Materials, stock selection, estimates, BOM, interchange | `MaterialLibrary`, `MaterialAssignment`, `NestingEstimateService`, `BomService`, associated UI/commands | [Library format](MaterialLibraryFormat.md), [export schemas](ExportSchemas.md) |
| Project fields, layout import, drawing tools, geometry utilities | `ProjectInfoService`, `LayoutTemplateImportService`, `DetailLabelService`, `DetailDimensionService`, `UtilityGeometryService` | [Detailed commands](DetailedCommandReference.md) |
| Settings and data lifetime | `Core/PluginSettings.cs`, `PluginSettingsService`, `AssemblyRepository`, `ActionHistory` | [User Guide](GazelleUserGuide.md#settings-and-saved-data) |
| Removal and downstream dependency warnings | `AssemblyRemovalService`, `LayerService` | [Removal warning](GazelleUserGuide.md#removal-warning) |
| Existing automated checks and uncovered paths | `tests/Gazelle.Regression` | [Regression README](https://github.com/y-naught/Assembly-Manager-2.0/blob/v1.1.0/tests/Gazelle.Regression/README.md) |

## Open implementation findings

The following are code-inspection findings, not newly reproduced desktop failures. The cited paths and lines refer to this audit's source snapshot. Suggested checks are follow-up tests, not claims that those cases already pass.

### A01 — High: a restored deferred-original edit lacks a source-shape baseline

**Evidence:** [deferred request restoration](https://github.com/y-naught/Assembly-Manager-2.0/blob/v1.1.0/AssemblyManagerPlugin/Services/AssemblyLinkEventService.cs#L1046) recreates original-edit evidence with `EmptyEvidence` and `PreviouslyValidated: true`. The [original-promotion guard](https://github.com/y-naught/Assembly-Manager-2.0/blob/v1.1.0/AssemblyManagerPlugin/Services/AssemblyLinkEventService.cs#L2675) skips the pre-edit source geometry proof for previously validated evidence. A material signature is preserved and checked, but an equivalent saved source-shape baseline is not.

**Risk:** pause propagation, edit an original, then independently reshape its input while listeners are stopped/unloaded or bypassed. On resuming the deferred original edit, the guard cannot establish whether that input still has the previously validated shape. A newer input change can be overwritten. Captured simultaneous edits and persisted material conflicts have separate protections; those do not cover missing geometry history.

**Current guidance:** do not independently edit inputs while an original edit is deferred and Gazelle is unloaded. Preserve a backup and resolve the intended authority before resuming.

**1.1.0 disposition:** deferred by the maintainer, with the requirement that the latest Gazelle remains running with linking enabled on every editing machine. This requirement does not supply the missing source-shape baseline or repair files already edited outside tracking.

**Follow-up:** persist and revalidate a source-geometry baseline or conservatively reject authority restored without that evidence. Add a restart regression that edits the input while listeners are stopped and requires both competing shapes to remain intact for review.

### A02 — High: initial hardware names can overwrite manufacturing membership entries

**Evidence:** initial [part-name allocation](https://github.com/y-naught/Assembly-Manager-2.0/blob/v1.1.0/AssemblyManagerPlugin/Services/AssemblyGenerationService.cs#L121) does not reserve hardware labels. [Hardware label construction](https://github.com/y-naught/Assembly-Manager-2.0/blob/v1.1.0/AssemblyManagerPlugin/Services/AssemblyGenerationService.cs#L591) permits an identifier such as `P01`. Manufacturing entries are stored in the component's name-keyed dictionaries at [line 379](https://github.com/y-naught/Assembly-Manager-2.0/blob/v1.1.0/AssemblyManagerPlugin/Services/AssemblyGenerationService.cs#L379); hardware writes to those same keys at [line 388](https://github.com/y-naught/Assembly-Manager-2.0/blob/v1.1.0/AssemblyManagerPlugin/Services/AssemblyGenerationService.cs#L388).

**Risk:** a manufactured `P01` and hardware identifier `P01` in one component can share a layer name and overwrite the manufacturing representative/count entries. A subsequent drawing copy can omit the intended manufacturing representative. The newer `ComponentUpdateService` reserves hardware names for additions, but initial creation does not use that guard.

**Current guidance:** use hardware identifiers outside the configured part-prefix/number namespace, such as `HINGE-01`.

**1.1.0 disposition:** deferred by the maintainer on the assumption that hardware identifiers obey this naming requirement. Initial-creation collisions remain possible; no implementation fix is claimed.

**Follow-up:** use a shared collision-safe naming policy at initial creation and component addition. Add a true `CreateAssembly` fixture with colliding hardware and manufacturing names; the current hardware-collision regression exercises additions only.

### A03 — Medium: hardware material attributes and saved reporting material can diverge

**Evidence:** component evidence uses the saved [`HardwareRecord.MaterialId`](https://github.com/y-naught/Assembly-Manager-2.0/blob/v1.1.0/AssemblyManagerPlugin/Services/AssemblyCategorizationReconciliationService.cs#L655). [Legacy/reference reconciliation](https://github.com/y-naught/Assembly-Manager-2.0/blob/v1.1.0/AssemblyManagerPlugin/Services/AssemblyCategorizationReconciliationService.cs#L1002) refreshes hardware `ComponentName`, not its material. [BOM generation](https://github.com/y-naught/Assembly-Manager-2.0/blob/v1.1.0/AssemblyManagerPlugin/Services/BomService.cs#L72) and [grouping](https://github.com/y-naught/Assembly-Manager-2.0/blob/v1.1.0/AssemblyManagerPlugin/Services/BomService.cs#L116) consume the saved hardware material.

**Risk:** a material edit can propagate to hardware object attributes while component identity and BOM rows continue to use the old saved material. Regenerating the BOM alone does not repair that record.

**Current guidance:** assign hardware materials before creation and verify hardware reporting after later edits. Recreate the affected assembly from corrected input when needed pending a fix.

**Follow-up:** reconcile live supported hardware material into hardware records and invalidate/rebuild affected categories/report caches. Test solid and block hardware material-only edits, paused/manual updates, and BOM grouping afterward.

### A04 — Medium: failed output deletion does not prevent assembly metadata removal

**Evidence:** [object deletion](https://github.com/y-naught/Assembly-Manager-2.0/blob/v1.1.0/AssemblyManagerPlugin/Services/AssemblyRemovalService.cs#L38) records successes only. [Metadata removal](https://github.com/y-naught/Assembly-Manager-2.0/blob/v1.1.0/AssemblyManagerPlugin/Services/AssemblyRemovalService.cs#L62) proceeds even when some objects were not deleted, with no failed-object list in the returned summary.

**Risk:** an undeletable/protected output can remain without its assembly record. Also, the intended deletion scope includes every object under the managed/legacy assembly roots, not just graph-owned output; untracked user content there is included.

**Current guidance:** save a backup, keep independent work outside managed output trees, and inspect for leftovers after removal. Removing an assembly does not purge its imported hardware library/definitions.

**Follow-up:** preflight deletability/ownership, report failed IDs, and retain adequate metadata or roll back when a complete removal is not possible. Test a partial native deletion failure and independent user objects on managed layers.

### A05 — Medium: flat labels claim inches for raw model values

**Evidence:** [flat part/row text](https://github.com/y-naught/Assembly-Manager-2.0/blob/v1.1.0/AssemblyManagerPlugin/Services/FlatPartSynchronizationService.cs#L517) and [initial row headers](https://github.com/y-naught/Assembly-Manager-2.0/blob/v1.1.0/AssemblyManagerPlugin/Services/LayPartsFlatService.cs#L461) append an inch mark without converting the numeric thickness or checking the model units.

**Risk:** a metric part with thickness `19.05` can be labeled `19.05"`. Separately, [stock thickness matching](https://github.com/y-naught/Assembly-Manager-2.0/blob/v1.1.0/AssemblyManagerPlugin/Services/NestingEstimateService.cs#L89) compares raw stock and model numbers without conversion; the stock Unit field is not an automatic conversion instruction.

**Current guidance:** use matching model/library numeric units, and treat non-inch flat thickness labels as requiring correction. Review unit changes before fabrication.

**Follow-up:** format labels from the document's actual units and define a consistent conversion policy for material stock and estimate fields. Test physically equivalent inch/millimeter models and ensure labels and counts agree.

### A06 — Medium: material-library CSV cannot round-trip multiline fields

**Evidence:** the [CSV importer](https://github.com/y-naught/Assembly-Manager-2.0/blob/v1.1.0/AssemblyManagerPlugin/Services/MaterialLibrary.cs#L562) reads physical lines and parses each as a separate record. The exporter can quote text containing line breaks, which does not make the importer's line-by-line record splitting safe.

**Risk:** a description with embedded line breaks can be split across rows or misinterpreted on import. CSV also intentionally omits arbitrary Properties and parent materials without stock rows, and formats numeric fields to three decimals; it is not a complete backup format.

**Current guidance:** use JSON for library backup/full interchange and avoid embedded newlines in CSV imports.

**Follow-up:** use a record-aware CSV parser and add export/import round-trip tests with commas, quotes, CR/LF, Unicode, and high-precision dimensions.

## Behaviors clarified, not implemented as new features

- **Copy / Orient is append-only.** [Each invocation](https://github.com/y-naught/Assembly-Manager-2.0/blob/v1.1.0/AssemblyManagerPlugin/Services/ComponentDrawingService.cs#L40) creates representatives for all component types. It is neither a missing-only repair nor a replace-existing command. Repetition can produce overlaps/duplicates. The plan-rotation heuristic [rotates existing world bounding boxes](https://github.com/y-naught/Assembly-Manager-2.0/blob/v1.1.0/AssemblyManagerPlugin/Services/ComponentDrawingService.cs#L195), not the actual geometry for each trial, so documentation no longer promises true minimum-footprint orientation.
- **Lay Parts Flat and Update Assembly are different.** The former can reflow output; synchronization preserves supported existing anchors/plan rotations. Flat creation [changes document-wide annotation scaling](https://github.com/y-naught/Assembly-Manager-2.0/blob/v1.1.0/AssemblyManagerPlugin/Services/LayPartsFlatService.cs#L50) to enabled/12, not just Gazelle label styling. Flat output is rigidly oriented, not unfolded.
- **Materials organize rows, not nested flat material layers.** The actual paths are `<part>::3D`, `<part>::text`, and assembly `row labels`.
- **One component per clear group remains important.** Initial creation uses the [first group index](https://github.com/y-naught/Assembly-Manager-2.0/blob/v1.1.0/AssemblyManagerPlugin/Services/AssemblyGenerationService.cs#L273). Ungrouped objects share the `-1` bucket; nested/overlapping groups are not a robust component hierarchy.
- **Reports and most annotations are snapshots.** Estimating, exporting, BOM generation, detail tools, and placed tables are not an implicit linked update or a universal live-documentation system. Update, inspect, estimate, then generate/export reports.
- **Safety is scoped.** Automatic updates hold unresolved branches; staged component additions preflight and have local rollback. The entire event/reference/report workflow is not one atomic transaction. There is no general detach/relink/source-choice or split-resolution UI.
- **Persistence follows processed events.** `AssemblyLinkEventService.Start` subscribes to object/group/unit/command/idle/open/close/undo events, not a dedicated before-save queue-flush hook. Normal saves include reconciled pending metadata, not a guarantee that every queued event has been flushed synchronously before save.
- **Library edits are global.** They do not automatically rewrite every object's assignment or rebuild every document's report. Stock-only changes within the same material parent are path-sensitive; a general attribute update is not a guarantee that the part's purchasing stock ID changes. `AssignMaterialToPart` explicitly targets that part record.

An additional performance investigation is warranted around temporary native geometry lifetime in `ReferenceUpdateService` (for example candidate creation around lines 1043–1088 and validation/flat temporaries around 1169/1253). Several allocations rely on eventual finalization instead of consistently scoped disposal. This audit did not measure peak memory or establish a permanent leak; large-model profiling and explicit lifetime review should precede performance claims.

## Original September 5 verification

- Inventoried all 30 `EnglishName` command entrypoints and checked coverage in the quick reference, detailed reference, and consolidated guide.
- Built the current production Release project: **0 warnings, 0 errors**.
- Built the existing regression runner successfully. It emits the existing `NETSDK1138` warning for its `net7.0-windows` target; no framework migration was attempted.
- Ran the production services against a separate hidden RhinoCore process with disposable documents: **75/75 existing regression scenarios passed**, exit code 0.
- Checked all 11 maintained Markdown files: 99 local file/heading links, balanced code fences, and command coverage passed.
- Checked whitespace with `git diff --check` and compared source hashes to ensure this audit did not edit C# or project files.

The regression suite covers source/original changes, categorization/layers/colors, manual/deferred updates, flat synchronization, command feedback, and the additive selected-occurrence workflow. It does not cover all 30 commands end to end. It uses real native geometry/replacement/material callbacks but invokes idle processing explicitly; some transform facts are injected because the console host lacks desktop command notifications. It does not prove desktop UI rendering, Rhino startup delivery, undo gesture sequences, initial hardware-name collision handling, material-library CSV round trips, or the new findings above. No user `.3dm` was opened or modified for this audit.

## September 26 initial implementation verification

- Production Debug and Release builds succeed with zero warnings/errors. The regression runner retains only its existing .NET 7 end-of-support warning.
- **91/91 default regression cases pass against both Debug and Release**, including the existing 75 cases, hardware placement/definition guards, indexed repository validation, master-switch safety, native file-storage checks, and color behavior.
- **3/3 native BOM layout cases pass in an isolated Release run**, checking actual page-space objects, hardware quantities/material labels, unaccounted parts and selected-page ownership. Later combined runs stalled in native page initialization, so these cases are now explicitly opt-in and run separately. They do not verify screenshots or page fit.
- Full `OpenHeadless` reopening also stalled in this hidden host. The file-storage test instead writes temporary 3dm files and verifies their native geometry, attributes, identities, groups, units and suspension metadata through `File3dm`. Service-restart tests check resume/protection separately. Full desktop reopen and undo workflows remain unverified.
- A local movement fixture (eight copied objects, 400 relationships) took **58–66 ms before** and **43–48 ms in an optimized comparison run**; later warm runs were 37–46 ms. Metadata attribute writes dropped from **400 to 8**. These measurements include capture/native callbacks/idle bookkeeping/persistence but exclude deliberate idle-pump waits; they are not a prediction for every production model.
- Checked whitespace and local file links in all 11 maintained Markdown files; both JSON examples parse successfully. Temporary benchmark copies and interrupted-test files were removed. No user model was opened or modified, and no package was published.

## PlaceBOM / PlaceComponent verification

- Production Debug and Release builds succeed with zero warnings/errors. The regression runner builds with only its existing .NET 7 end-of-support warning.
- **105/105 default checks pass against both Debug and Release**, including six new native-font BOM cases, four individual-placement cases, and four multiple-view reconciliation/addition cases. Multiple views receive geometry/material updates and staged additions at their own stored placements without inflating manufacturing quantities.
- The new **native fitted-page case passes in separate Debug and Release runs**, checking actual text/grid bounds, reversed corners, millimeter paper with inch model units, grouping, page ownership, hardware quantity, and invalid-placement preservation. Literal paths, braces and RTF-like text are preserved by the renderer. These are native object assertions, not screenshot/readability certification.
- All 32 command names are present in the quick, detailed and consolidated references. Checked 11 maintained Markdown files, 96 local file links and balanced code fences; whitespace checks pass.
- No user model was opened or modified, no package was published, and no development plugin was registered alongside the installed one. Interactive checklist, mouse preview, prompt cancellation and desktop undo/save/reopen remain debugger-based manual checks.

## BOM column-picker layout refinement

The column picker now uses independent single-column sections, short width-constrained instructions, a scrolling checklist, and fixed action rows. This removes the shared-grid/scrollable preferred-width expansion without changing the placed table renderer or column selection semantics. Production Debug/Release builds pass. The separate [off-screen Eto/WPF layout check](https://github.com/y-naught/Assembly-Manager-2.0/blob/v1.1.0/tests/Gazelle.DialogLayout/README.md) verifies compact/default, tight and expanded sizes, instruction/button bounds and selection behavior, and produces rendered previews. These checks do not load/register Gazelle in Rhino or replace final debugger-based host/DPI testing.

## Documentation maintenance

- [GazelleUserGuide.md](GazelleUserGuide.md) is the consolidated reading entrypoint.
- [CommandReference.md](CommandReference.md) indexes command names; [DetailedCommandReference.md](DetailedCommandReference.md) describes prompt-level behavior and side effects.
- [AssemblyManagerWalkthrough.md](AssemblyManagerWalkthrough.md) stays task-oriented.
- [LinkedAssemblyArchitecture.md](LinkedAssemblyArchitecture.md) distinguishes implemented mechanics from future work; it must not describe planned conflict tools as shipped commands.
- [PartCategorizationAlgorithm.md](PartCategorizationAlgorithm.md), [MaterialLibraryFormat.md](MaterialLibraryFormat.md), and [ExportSchemas.md](ExportSchemas.md) document their specialized rules and limits.
- [Regression README](https://github.com/y-naught/Assembly-Manager-2.0/blob/v1.1.0/tests/Gazelle.Regression/README.md) explains how to run verification and what it cannot prove.

For future changes, update the consolidated guide and the relevant detailed reference together, add a regression covering the behavioral change, and only refresh distribution documentation when deliberately preparing a new package. The original audit pass did not commit or publish code; the 1.1.0 documentation preparation records release requirements without itself confirming a merge or publication.
