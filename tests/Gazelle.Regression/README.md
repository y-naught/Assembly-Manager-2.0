# Gazelle Rhino regression runner

This Windows console runner exercises production reference propagation and part/component reconciliation against real Rhino geometry. By default, it uses a separate, hidden RhinoCore process and disposable documents containing linked boxes; it does not open or save user models or install the RHP. The explicit inspection and supplied-model modes described below read a provided model without saving it. Rhino 8 and the .NET 7 Windows Desktop runtime must be installed.

## Build and run

From the repository root:

```powershell
dotnet build AssemblyManagerPlugin\AssemblyManagerPlugin.csproj -c Release
dotnet build tests\Gazelle.Regression\Gazelle.Regression.csproj -c Release
dotnet tests\Gazelle.Regression\bin\Release\net7.0-windows\Gazelle.Regression.dll
```

The optional first argument is the absolute path to another Gazelle RHP, for example the Debug build. The test project references the existing Release RHP for compilation and does not rebuild it itself. To override the compile-time reference, pass `-p:GazellePluginPath=<absolute-path>` when building the runner. For a nonstandard Rhino installation, set `RhinoSystemPath` during build and `GAZELLE_TEST_RHINO_SYSTEM` when running.

For a focused run, set `GAZELLE_TEST_FILTER` to comma-separated scenario prefixes, such as `input-delete-` or `regroup-material-,material-category-persistence-`. With no filter, the runner executes 173 service/storage/geometry/font cases. The four native layout cases are opt-in: run fresh processes with `GAZELLE_TEST_FILTER=bom-table-` for the three legacy table-service cases, and `GAZELLE_TEST_FILTER=bom-fitted-page` for the new fitted placement case. Rhino page initialization can stall in a hidden host, especially after other fixtures; progress markers identify the blocking call. These cases are not part of the reliable default console-host suite.

To check the Debug plugin with the same compiled runner, first build that plugin configuration and then pass its path:

```powershell
dotnet build AssemblyManagerPlugin\AssemblyManagerPlugin.csproj -c Debug
dotnet tests\Gazelle.Regression\bin\Release\net7.0-windows\Gazelle.Regression.dll "<repository path>\AssemblyManagerPlugin\bin\Debug\net7.0\Gazelle.rhp"
```

Build errors are not regression passes. The current .NET 7 target can produce an SDK end-of-support warning; do not hide actual compiler/test failures in that warning. The current case list and routing are in `Program.RunScenarios`, not generated from this README.

## Input-side component additions

Ten `input-addition-` scenarios exercise `AddPartToComponent` through its production staging wrapper: new/matching parts, adoption of a copied object with inherited link tags, block hardware and BOM quantities, three independently positioned drawing views, repeated staging and listener restart, rejected selections, stale membership, mixed input/original plans, and unsupported stored origins. They verify that the selected input UUID, layer, material, position and unrelated user text survive; no copies appear until Update Assembly; only the selected occurrence changes; existing flat outputs synchronize; and a second update does not duplicate additions.

Four `input-safety-` scenarios check native Undo of staging/application, fail-closed handling of a source plan interpreted using the legacy original-side default, and rollback after a forced failure following the first successful source adoption. Rollback verifies restored object/attribute/group/layer sets and the assembly snapshot, then retries successfully. Attribute comparisons use actual values, memberships and user strings rather than standalone 3dm archive bytes that remap group indices.

Desktop Redo is **not verified**. In this hidden host, direct `RhinoDoc.Undo()` leaves native undo recording active; a plain point/document-string control fails immediate Redo even with Gazelle listeners stopped. The tests assert Undo restoration only and explicitly report this limitation. Test command selection/cancellation and desktop Undo/Redo through the Visual Studio debugger, not by registering a second copy of Gazelle beside the installed plugin.

## Input-group recognition and removals

Sixteen `input-regroup-` scenarios exercise native group-table edits and the production idle/event path. They cover renaming, recreating an unchanged group, adding/removing/replacing members, an already deleted input member, repeated regrouping, ambiguous groups, mixed occurrences and shared inputs, multiple moved drawing views, rotated flat representatives, retirement of the last part category, hardware removal/BOM quantities, downstream dependency protection, failed-deletion rollback, and cancellation by restoring the original membership.

Assertions require recognition through retained source UUIDs, updated group identity, no downstream membership changes before Update Assembly, and changes limited to the selected occurrence. Omitted input geometry remains in the model if it still exists. Flat reparenting preserves geometry, placement and UUID; last-category retirement removes owned output and labels but retains user notes. A forced failure after the first output deletion must restore the exact output UUIDs and runtime serials, source/flat attributes, and stored assembly snapshot before a successful retry. Unsafe dependencies must be rejected before deletion. These checks do not establish general removal/relink support or failure atomicity across an entire multi-assembly refresh.

The ambiguous-group case also holds a real geometry edit from an untouched sibling without continually rescheduling idle work. Deleting only the competing group must trigger recognition of the surviving group, clear the regroup issue, and allow the held edit to finish during Update Assembly.

## TBD material merge regressions

Ten `regroup-material-` cases add shape-identical, unassigned members through native input regrouping, apply Update Assembly, and then assign the existing category's material. They cover input/original attribute edits in automatic/manual mode, one edited member of a two-member TBD category, the real `AssignMaterialToPart` service (including a distinct stock-shape ID with the same parent), and a temporary-material/reassignment recovery cycle for duplicate categories whose old TBD baseline was already overwritten. Assertions check surviving part number/color, quantities and component membership, generated layers, copied placements, synchronized flat representatives/labels, and repeated-update stability. Part-level service tests supply an in-memory library through the settings reader and do not touch saved user settings.

Five `material-category-persistence-` cases distinguish an accepted empty category from a missing/null legacy field. They check load/save and JSON round trips after source-material changes and part-level stock-record updates, one-time legacy initialization (including initialization to empty), and standalone normalization. Four direct-edit cases reproduced the merging failure against the pre-fix build before the production fix was compiled.

## Direct input deletion regressions

Fourteen `input-delete-` cases delete native input objects without regrouping. They exercise Update Assembly immediately before idle, deferred/manual mode, automatic mode still holding structural changes, final-category retirement with user-note preservation, independently moved copied views, hardware/BOM removal, shared-source and downstream dependency rejection, restoration before event processing, same-UUID replacement, staged listener restart/persistence, and deletion of the last identifying member. Two split-like delete/add batches cover both Rhino reusing the old source UUID for one remainder and entirely fresh successor UUIDs; neither may be accepted as ordinary removal or guessed replacement lineage.

Assertions cover selected-occurrence identity, removal of all owned original/copied descendants and stale graph/reference records, retained flat UUID/placement with a surviving parent, quantities and labels, and repeat-update stability. Every retained non-row-header object keeps its UUID; normal regenerated row headers are compared by content, placement, styling and count instead of requiring their UUIDs to remain constant. Tests use disposable native geometry and service callbacks, not desktop command notification/UI certification or a full save/close/reopen workflow.

Nine additional `input-delete-copyorient-` cases invoke the real Copy / Orient service after fixture normalization, without a healing Update Assembly before deletion. They check occurrence IDs in newly created part/hardware copies, independently moved views, persisted historical copies missing those IDs, and old whole-block hardware recipes. Wrong nonempty occurrence IDs, multiple incoming parents, unsupported BREP recipes, and an unowned chained copy remain rejected without deleting outputs. The earlier idealized fixtures supplied occurrence IDs themselves, and their preliminary refresh masked the producer omission; these cases cover that gap explicitly.

## Portable coverage

| Case group | Count | Files |
| --- | ---: | --- |
| Source/original propagation and protected categorization | 14 | `Program.cs` |
| Managed layer cleanup and category colors | 10 | `Program.cs` |
| Deferred/manual updates, material changes, flats, and dependencies | 26 | `AssemblyUpdateScenarios.cs`, `FlatDependencyScenarios.cs` |
| Command-line feedback | 5 | `UpdateFeedbackScenarios.cs` |
| Staged selected-occurrence component additions | 20 | `ComponentUpdateScenarios.cs`, `ComponentOccurrenceScenarios.cs` |
| Input-side additions and selection/persistence safeguards | 10 | `InputComponentAdditionScenarios.cs` |
| Input-addition Undo, legacy direction and rollback | 4 | `InputComponentAdditionSafetyScenarios.cs` |
| Input regrouping, safe removals, flat reparenting and rollback | 16 | `InputRegroupScenarios.cs` |
| Direct input deletion, real Copy/Orient ownership, downstream cleanup and protection | 23 | `InputDeletionScenarios.cs` |
| Regroup-added TBD material merges, partial cohorts and recovery | 10 | `RegroupMaterialMergeScenarios.cs` |
| Accepted empty category persistence and legacy initialization | 5 | `MaterialCategoryPersistenceScenarios.cs` |
| Copied hardware placement, movement performance and repository normalization | 4 | `PlacementPerformanceScenarios.cs` |
| Native material/hardware table placement (opt-in) | 3 | `BomTableScenarios.cs` |
| Fitted BOM columns, wrapping, paper units, bounds, validation | 6 | `PlacedBomScenarios.cs` |
| Native fitted BOM page insertion (opt-in) | 1 | `PlacedBomScenarios.cs` |
| Individual tracked component placement and preflight | 4 | `PlaceComponentScenarios.cs` |
| Multiple copied views, category merge/gap and additions | 4 | `MultiCopyReconciliationScenarios.cs` |
| Settings and master-link suspension safety | 9 | `LinkSwitchScenarios.cs` |
| Extended color allocation and persistence | 3 | `PartColorScenarios.cs` |
| Total (173 default + 4 opt-in layout) | 177 | |

The cases cover source replacement, source non-rigid scaling, original-assembly edit promotion, existing categories with non-transitive geometry tolerances, missing or unsupported source evidence, managed part layer cleanup/colors, deferred/manual updates, synchronized flat outputs, command-line update feedback, staged component additions, hardware placement, master-link suspension, and table output. The tolerance fixtures explicitly establish A matches B and B matches C while A does not match C; editing a different occurrence must still produce nine unchanged P01s plus one new part, without renumbering A/B/C. Missing or unsupported evidence in an unrelated category must not block a complete category. Evidence missing inside the affected category remains a conservative review case, preserving all quantities and existing identities.

Successful category-split cases check geometry propagation, nine-plus-one quantities, stable UUIDs, part layers/names, unchanged P01 occurrences, grouped component part listings, and idempotence on a second automatic refresh. Here, “split” means one edited occurrence receives a different part category; it does not mean Rhino's structural `Split` command is supported.

Ten dedicated layer cases verify that recategorization removes emptied original/copied component part leaves while retaining layers with other occurrences, ordinary/hidden/locked user geometry, custom child layers, or the current drawing layer. They preserve component parents and P01 layers used by other components. New categories get a color distinct from the old category, including new colors beyond the predefined palette, and that color agrees across originals, copied components, flat category parents and flat 3D outputs. Merging into P02 uses its existing custom color from another component. A whole-component merge also checks that retired C01's obsolete empty part leaves are removed while the merged outputs adopt C02's existing P02 layers/color and C01's parent remains. Repeated refresh preserves colors/layer identities; manual Update Assembly also removes recreated stale empty leaves without a new geometry edit. User input styling and explicit per-object color overrides remain unchanged.

Twenty-six assembly-update cases cover settings backward compatibility (missing automatic toggle defaults off; explicit choices survive serialization), paused input/original changes, manual update while remaining paused, automatic catch-up when re-enabled, repeated deferred original edits with an intervening move, persisted pending authority across an event-service restart (manual and automatic recovery), and conflicting deferred edits preserved for review. Material-only input and original attribute edits are tested automatically and manually, including category splits and merges. Source material changes that bypass listeners must block unsafe original promotion, whether they precede the original edit or occur while it is pending. A geometry-only original edit must preserve the input's more-specific opaque stock-material ID when the original has been normalized to that same parent material. A shared input updates both assemblies during manual promotion, with exactly one replacement of the other assembly's output. No test reads or changes the operator's saved settings; an injected local toggle and a small in-memory material library are used.

Flat-output checks use real registered lay-flat recipes, and exercise representative/non-representative splits, category merges, thickness changes, material metadata/labels/row headers, and a rotated copied-component parent frame. Every unambiguous category must have one live representative with a stored transform mapping current parent geometry to current flat geometry. Existing output and label UUIDs and label placement survive refresh; user flat moves/plan rotations and arbitrary text are preserved, obsolete managed outputs/labels retire, and repeated updates remain stable. A superseded flat output with a dependent assembly is instead preserved and flagged for review, including an inactive/conflicted dependency; its UUID and annotation remain available. Assemblies that were never laid flat must not acquire a PARTS layout merely because they are updated.

Native Rhino replacement and material-attribute callbacks populate the real event service queue. This console host does not emit `RhinoApp.Idle`, so the runner invokes the service's production idle callback by reflection to process that queue. Direct `ObjectTable.Transform` in this host emits replacement callbacks but not the command-level `BeforeTransformObjects` notification, and interactive commands are unavailable. The move/rotation cases therefore inject the identical immutable pre-transform fact using production snapshot capture, then exercise native replacement callbacks and the production transform classifier. They validate transform bookkeeping but not desktop command notification delivery. Fixture setup uses the production lineage/repository services with default categorization tolerances, not the assembly-generation UI.

Five feedback cases capture messages through the production reference-update service's injected writer. They check manual and native-event automatic batch stage ordering (including existing flat layouts), one message per stage instead of per object, elapsed-time and result summaries, silent empty/unlinked automatic requests, and pre-existing document-wide link issues directing operators to the bottom review panel. An injected history-recording failure checks that interrupted updates report the stop without also claiming completion. These tests validate the emitted messages, not their visual rendering in Rhino's command history or the Assembly Manager window's layout.

Twenty component-update cases primarily use two instances of one component type, each containing three distinct retained solids. The second occurrence is rotated and translated, and an existing copied representative has another independent placement. Tests ungroup one ORIGINAL ASSEMBLIES occurrence, add geometry, regroup it, and stage through the event-service wrapper. Staging must rebind the generated group UUID without changing the source group or adding counterparts; ordinary automatic idle and an event-service restart must leave the plan pending. Manual Update Assembly adds a new part, a matching existing part, marked solid hardware, or imported block hardware only to the selected occurrence and its existing copied representative. The untouched occurrence keeps C01 and its membership; the edited one becomes C02 with correct layers, summaries, and flat counts. Retained geometry and UUIDs survive. A non-representative edit cannot alter the old C01 copy. A singleton retains C01, and symmetric components work without inferring a sibling placement. Legacy all-occurrence plans narrow safely to their saved selected occurrence. Different occurrences can be staged independently, restaging one does not discard the other, and a design matching an existing component merges into that category.

The split fixtures verify that only the selected occurrence's existing copied descendants receive additions. They do not require a copied view for both resulting component categories; a missing-view warning can still be emitted separately from persisted graph conflicts. `PlaceComponent` adds a specific view, while rerunning Copy / Orient Components still appends another whole representative set.

Explicitly adopting an operator-created copy must clear inherited link tags without stealing the old object's identity. An unrelated original edit made while the assembly is staged must be promoted safely during the manual update, not overwritten by its old input. Hardware remains out of manufacturing categories and appears as quantity one in the BOM. Hardware with an identifier such as P01 receives a separate hardware layer and preserves the existing manufacturing part's layer color. Repeated Update Assembly is idempotent. Omitted retained members, mixing existing linked occurrences, changed staged membership, unobserved selected retained-source edits, reserving one new UUID in two pending components, and adding into a copied-output group already used as another assembly's input group must be rejected without partial additions. These are production service/geometry tests, not visual tests of the command's selection prompts.

Each case prints `PASS` or an assertion failure, followed by the total. The standalone process exits with 0 when all cases pass and 1 otherwise. It ends the isolated native host directly after flushing results because Rhino's GUI shutdown sequence can wait for a message loop or overwrite the console exit status.

The placement cases move/rotate a copied component containing two block instances and solids, then edit/update the design. They require final instance matrices, stable UUIDs, recovery transforms, and unchanged categories. A definition swap remains a review case. The performance fixture times four passes moving eight copies in a 400-edge graph and counts attribute rewrites; assertions enforce affected-edge locality, not a machine-dependent speed threshold. Reported timings include capture, native transforms/callbacks, idle bookkeeping and persistence, but exclude the helper's deliberate idle-pump waits and do not represent a production-model benchmark.

Master-switch tests inject the global setting without touching user preferences. They cover defaults/round-tripping, disabled automatic/manual/staging paths, unchanged resume, source/group edits while off, persisted suspension across service recreation, exact restoration, independently safe sibling assemblies, creation while disabled, and known new assembly creation while older suspensions remain. One test writes temporary native 3dm files and reads them with `File3dm` to verify exact suspension metadata, linked geometry/attributes, UUIDs, groups and units; changed geometry must persist beside the original safety baseline. It deletes only its own fixtures. This is a storage check, not a full Rhino document reopen/resume test: `OpenHeadless` stalled in the hidden host and was not certified.

The repository-index case checks migration with a shared source, idempotent normalization, conflict retention for inconsistent legacy references, and rejection of duplicate node/object/edge identities. Performance indexing must not bypass those validations.

Color tests allocate 121 nonrepeating colors while retaining the first 21 predefined values, verify repository persistence and repeated recreation, honor live custom colors when allocating new ones, and verify black new-part output when colorization is off. Existing split/merge tests also check saved category colors.

Table tests use native Rhino page views and text/line objects for mixed, hardware-only and no-hardware assemblies. They check quantities/aggregation matching the BOM, material labels, unaccounted parts, no sheet-stock double counting, and ownership by the selected layout. They inspect object data, not screenshots or printed page fit.

The six fitted-BOM tests use native font geometry without page initialization. They verify checklist schema/defaults and selected-column order, stock/hardware/unaccounted row semantics, long description/path wrapping before shrinking, reduced text for insufficient height, inch/millimeter paper scaling independent of model units, rejected unknown/empty columns and tiny/invalid rectangles, and empty/single-column tables. Every created text/grid entity is checked against the selected bounds. The separate `bom-fitted-page` case inserts actual geometry onto a millimeter layout with inch model units, checks reversed corners, grouping, page ownership, hardware quantity and bounds, and requires invalid placement to leave object counts unchanged.

Four PlaceComponent cases call the production service to create independent grouped views, including whole hardware blocks. They check center anchoring, first-ever placement, source and occurrence identities, direct parent transforms, independent movement followed by geometry/material propagation, and unchanged physical quantities. Disabled/unsafe linking, stale source geometry, incomplete or duplicated membership and staged additions are rejected. Four multi-copy cases preserve legitimate same-occurrence views and views retained after category merging, still detect a missing category, and apply a staged addition to three independently placed groups while retaining existing UUIDs and the untouched sibling occurrence.

For interactive command/checklist/rectangle-preview verification, use the development launch through the Visual Studio debugger. The installed Gazelle and development RHP have the same plugin UUID; do not normally load/register both together. This runner loads the selected assembly directly for service tests and does not register it as a second installed plugin.

## What these tests do not establish

The separate [BOM dialog layout check](../Gazelle.DialogLayout/README.md) verifies the column picker's real off-screen Eto/WPF layout and selection behavior without starting Rhino. It is not included in this geometry runner's scenario count and does not replace debugger-based desktop testing.

- Visual correctness of the Assembly Manager/settings window, selection prompts, or command-history rendering.
- Desktop idle scheduling, plugin loading/registration, or every command/gumball/grip/third-party event sequence.
- Complete document-string undo/redo behavior or real save/close/reopen/Save As recovery. Service-restart tests preserve an in-memory document; the separate disk-storage test verifies file contents, not desktop reopening/automatic catch-up.
- Protection against source geometry edits made while listeners are stopped/bypassed after an original edit was deferred. The suite checks the corresponding persisted material-type guard, not a durable source-geometry proof.
- General structural split/join/removal/relink/detach support beyond the recognized input-group omissions above, copied/flat upstream promotion, or block-definition topology updates.
- Hardware material-only BOM reconciliation, all same-parent stock-assignment paths, large-production-model performance, or exhaustive geometry equivalence for curved/chiral parts.
- Automatic missing copied-view rebuilding, a first automatic flat layout, or fully atomic rollback across a complete update and all its downstream assemblies. Explicit individual placement is covered separately.

No tests call the user's saved material/settings store. Passing this suite is evidence for its covered service paths, not a blanket certification that all issue types are automatically recoverable.

## Optional supplied-model inspection and historical reproduction

For the supplied `ASM TEST.3dm` reproduction (the file is not included in this repository):

```powershell
dotnet tests\Gazelle.Regression\bin\Release\net7.0-windows\Gazelle.Regression.dll --model-regression "<absolute path to ASM TEST.3dm>"
```

These three cases exercise source refresh, ORIGINAL ASSEMBLIES edit promotion, and recovery of the already-edited saved geometry through the production service entrypoints. They use disposable headless documents, never save the file, and verify its SHA-256 hash is unchanged afterward. Loaded documents do not deliver native object callbacks in this console host, so these are service-level regressions; the portable cases cover native event capture separately. The supplied-model checks require P01 quantity twelve to become eleven plus one P36, preserve every other part number/quantity and every linked object UUID, move the edited original to its P36 layer with a distinct color, remove its old empty component part leaf when safe, and remain stable on repeated refresh.

During the previous categorization fix, the preserved pre-fix plugin failed all three supplied-model cases: propagation succeeded, but an unrelated existing P01 tolerance overlap aborted categorization. The corrected build passed all three against that original fixture. The optional `--inspect "<absolute model path>"` mode prints raw stored conflicts, graph/geometry diagnostics, and P01 equivalence comparisons without modifying the document or saving the file. Both modes accept an optional plugin path as their final argument.

The supplied-model mode requires the original reproduction's stored `test assembly` and linked UUIDs. In a later historical verification, the desktop file no longer contained those saved assemblies, so the three reproduction cases were unavailable. This is not a statement about the model's current contents and this mode is not a general pass/fail check for any file named `ASM TEST.3dm`. A fixture precondition failure leaves the file unchanged; the seventy-five portable cases remain independently runnable. Use `--inspect` when the goal is to examine a different saved model's metadata.
