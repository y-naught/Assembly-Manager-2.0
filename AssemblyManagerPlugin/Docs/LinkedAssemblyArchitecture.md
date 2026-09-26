# Linked Assembly Architecture

This document describes the Gazelle `1.1.0` implementation, not the contents of older packages under `dist`. Planned work is listed separately at the end. For operator instructions, see [Assembly Manager Walkthrough](AssemblyManagerWalkthrough.md); for numbering rules, see [Part Categorization Algorithm](PartCategorizationAlgorithm.md).

## Current support

| Operation | Implemented behavior |
| --- | --- |
| Edit a linked input solid | Refresh supported descendants and recategorize parts/components, automatically when enabled or through **Update Assembly**. |
| Edit a supported solid in ORIGINAL ASSEMBLIES | Inverse-promote a proven one-to-one `DirectCopy` edit to its design input, then refresh descendants. Unsafe or competing edits are preserved for review. |
| Move/rotate a linked object | Capture an orientation-preserving whole-object rigid transform and adjust relationships so separately placed descendants stay put. This requires the expected Rhino transform events. |
| Edit COPIED COMPONENTS or PARTS geometry/material | Quarantine the edited branch. **Update Assembly** can rebuild those live outputs from their authoritative parents; it is not a command to promote their edits upstream. |
| Change a part's assigned material | Refresh assignment attributes, recategorize by parent material, and update existing flat labels and quantities. Stock size is separate from category identity. |
| Add a part or marked hardware to a component | Explicitly stage one regrouped ORIGINAL ASSEMBLIES occurrence with **Update Component**, then apply through **Update Assembly**. Sibling occurrences are not modified. |
| Split/join/remove retained members or arbitrarily change group membership | Record review evidence; do not infer new production lineage. The additive command rejects these changes. |
| Refresh existing flat layouts | Preserve usable representative identities/placement, append newly required categories, retire safe superseded representatives, and maintain owned part labels/row headers. |
| Create the first flat or copied layout | Still an explicit output command, not an automatic consequence of updating. |
| Refresh extracted leaves of an ordinary block | Preserve the old output and report unsupported provenance. Unique nested definition-leaf locators are not implemented. |
| General relink, detach, promote-derived, or split/join resolution | No dedicated resolution UI/command is implemented. A status message suggesting review does not imply such a control exists. |

Hardware is not a manufacturing part category. Marked whole hardware can be copied with a `HardwareCopy` recipe, including block instances. This is different from automatically tracking definition edits inside ordinary unmarked blocks.

## Layers and ownership

The managed roots are:

```text
ASSEMBLY MANAGER
  ORIGINAL ASSEMBLIES
  COPIED COMPONENTS
  PARTS
```

Original and copied geometry is organized by assembly, component number, and part/hardware name. Flat output is organized by assembly and part number, including `3D` and `text` children. `LayerService` and `Core/Constants.cs` define the full paths.

Legacy `SHOP` and `SHOP_HARDWARE` values still appear in persisted reference-role metadata. They are compatibility identifiers, not the current layer names; changing these strings as if they were paths would break old references.

The graph is the authority for object membership. A user moving an object to another layer does not create a new component or part identity. Managed geometry may be returned to its categorized layer on update. Arbitrary user geometry or annotations on managed layers are not automatically adopted into the graph.

## Persistence model actually in use

`Core/AssemblyModels.cs` defines the current schema. Both `AssemblyStore` and `AssemblyLinkGraphRecord` use schema version 2. The store is serialized by `AssemblyRepository` to Rhino document strings at section `AssemblyManager`, entry `Store`.

| Record | Purpose |
| --- | --- |
| `AssemblyRecord` | Assembly identity, part/component/hardware records, legacy references, link graph, staged component updates, and cached manufacturing reports. |
| `AssemblyLinkNodeRecord` | Stable logical `Id`; mutable Rhino `ObjectId`; role/status; `PartId`, `ComponentId`, `SourceComponentInstanceId`; fingerprint, timestamp, and metadata. |
| `AssemblyLinkEdgeRecord` | Parent/child node IDs, exact `ParentToChildTransform`, recipe, status, timestamp, and recipe metadata. |
| `SourceComponentInstanceRecord` | One physical component occurrence; input/generated group UUIDs and names, input group index hint, retained source node IDs, component-category ID, and status. |
| `PartRecord` / `ComponentRecord` | Categorized definitions and compatibility membership/quantity/representative caches. |
| `LinkConflictRecord` | Persisted issue type, status, reason, affected node/edge, candidate object IDs, timestamps, and context metadata. |
| `PendingComponentUpdateRecord` | Selected occurrence, replacement group, added object IDs, prior group identity, and retained-member identity proof awaiting manual application. |

Roles are `Source`, `OriginalAssembly`, `CopiedComponent`, `FlatPart`, and `Hardware`. Supported regeneration recipes are `DirectCopy`, `HardwareCopy`, and `LayFlat`; `BlockDefinitionPart` records unsupported extracted-leaf provenance. `ManualRelink` is a reserved recipe name, not an implemented relink command.

There are no separate persisted part-occurrence/definition tables, graph/node revision counters, tombstone collections, or document-global source index. Occurrence identity is represented by link nodes plus `SourceComponentInstanceRecord`; category identity is represented by `PartRecord.Id` and `ComponentRecord.Id`.

### Recovery tags

Generated object attributes carry stable link metadata such as `AssemblyManager.LinkNodeId`, `LinkEdgeId`, `LinkRole`, `AssemblyId`, `LinkSchemaVersion`, and `SourceComponentInstanceId`. They also carry compatible source/reference/transform metadata. Exact keys are in `Core/Constants.cs`.

Input nodes remain assembly-scoped and do not receive one scalar generated-object link tag: the same physical input UUID may feed several assembly graphs. Generated object tags help recover replacement UUIDs and detect copied duplicate identities. A copied tag does not grant a new object ownership of the original node.

### Validation and migration

Repository load/save normalizes missing additive collections, advances part/component sequence counters, and checks graph identities and edges. Unsupported future schemas, duplicate graph identities/object mappings, missing graph endpoints, and internal graph cycles cause a read failure rather than silently replacing the document store. A failed load leaves the stored JSON unchanged.

Version 1 source-to-original references can be migrated additively using their recorded transforms. Migrated node identities are deterministic, and a `LegacyMigrationIncomplete` issue describes gaps. Loading normalizes an in-memory store; a later save persists that normalized state. Legacy copied output lacks complete IDs/transforms, and legacy flat output lacks complete placement recipes, so neither is safely inferred merely from visual similarity.

Migration is not an `Enable Live Links` wizard. There is no automatic full legacy-output replacement operation. Preserve a backup and inspect existing output before generating replacements. In particular, **Copy / Orient Components** appends another representative set for every component; it is not a missing-only repair command and can duplicate an existing layout.

## Authority and transform semantics

Normal shape authority flows from an input to an original, then to immediate copied/flat descendants:

```text
design input -> original occurrence -> copied component
                          |                    |
                          +-------> flat <-----+
```

A flat edge can have an original or a copied parent. Its transform belongs to that exact parent frame, not to an interchangeable occurrence with the same part number.

For an edge from parent `p` to child `c`:

```text
child geometry = E_pc * parent geometry
E_source_to_copy = E_original_to_copy * E_source_to_original
```

Matrices operate on the left; `A * B` applies `B` first. `TransformRecord` stores 16 values and rejects nonfinite, non-affine, or noninvertible matrices.

### Shape changes versus placement

- A supported source shape change rebuilds descendants using the saved edge/recipe.
- For a captured rigid source move/rotation `D`, outgoing edges become `E * inverse(D)`, preserving independent output placement.
- For a captured rigid derived-object move/rotation, its incoming edge becomes `D * E`; its outgoing edges become `E_out * inverse(D)`.
- Block-instance move evidence includes the unchanged definition UUID and exact cumulative instance matrix. This preserves hardware's final copied-component placement through refresh; a definition swap is still a shape edit, not a rigid move.
- Grip-owner edits and non-rigid operations are not treated as whole-object layout changes. Scale, shear, and orientation-reversing mirror operations follow shape-edit safety rules.
- Global model-unit scaling uses conjugation `E' = S * E * inverse(S)` for endpoints present in the pre-scale snapshot. Category fingerprints/thicknesses are advanced only with corroborating canonical evidence; failures create `CanonicalUnitScaleUnresolved` issues.

Creation/layout services register their accumulated matrices directly under event suppression. A third-party operation that changes objects without the expected transform notification may be classified as a shape replacement instead of a layout move. The regression host does not verify every desktop command's event delivery.

### Safe ORIGINAL ASSEMBLIES promotion

For an original edited through one active source-to-original `DirectCopy` edge `E`, the candidate input becomes `inverse(E) * edited original`.

Before accepting it, the event path checks the captured pre-edit geometry against the current source, checks material-type consistency and identity/ownership, and rejects competing edits. The update service validates the inverse-transformed supported solid and its forward round trip. It then replaces the input and refreshes all shared-input/downstream consumers.

Missing or ambiguous relationships, unsupported/block-leaf geometry, duplicate identity, split/join evidence, concurrent input/original edits, and multiple edited originals for one source are review cases. **Update Assembly** does not discard an unresolved original-promotion edit.

Source promotion, material copying, descendant updates, and metadata saves are guarded stages, not one failure-atomic document transaction. A reported failure may follow successful earlier mutations; inspect the resulting issues rather than assuming nothing changed.

## Automatic preference, manual update, and pending work

Settings schema 10 includes `AssemblyManager.AutomaticallyPropagateChangesInAssembly`, displayed as **Automatically propagate changes in assembly**, default **false**. Missing older values use the new default; explicitly saved true/false choices are retained. Geometry propagation and recategorization share this preference.

With the toggle off, Gazelle still captures replacement IDs, placement matrices, material changes, groups, and conflict evidence. It defers expensive geometry propagation, safe original promotion, categorization, and flat synchronization. It is not a zero-overhead or listener-off mode.

Deferred input/original requests are saved in node metadata:

- `PendingSourceUpdate`
- `PendingOriginalUpdate`
- `PendingOriginalSourceMaterial`

The last field records a normalized parent-material ID/name signature. It excludes stock-shape details because originals normally normalize assignments to the parent material. A geometry-only original edit preserves an input's more-specific stock choice when both still have the same parent material.

**Update Assembly** drains queued bookkeeping, validates/applies selected staged membership, promotes safe pending originals, and performs one normal full reference refresh. The selected assembly's shared input and downstream consumers may also update; unrelated assemblies do not. This does not re-enable the preference. The compatible Rhino command remains `RefreshAssemblyReferences`; `UpdateAssembly` is not a newly registered Rhino command.

Re-enabling automatic propagation resumes pending requests. Plugin start and document-open events queue a pending-marker scan. This is not a complete geometry comparison of everything changed while Gazelle was absent.

Important safety limit: a deferred original stores that its initial geometry proof passed, not a persistent exact copy/fingerprint of the then-current source geometry. Later deferred passes skip that original geometry proof while rechecking the saved material signature. Geometry edited on the input while capture is disabled or bypassed may therefore escape the concurrent-edit checks. Do not edit linked inputs through an inactive/uncoordinated plugin while an original edit is pending.

### Emergency linking switch

`AssemblyManager.EnableLinkedAssemblies` defaults **true**. OFF suppresses event capture/idle propagation and rejects manual **Update Assembly**, direct reference refresh, and **Update Component** staging. Ordinary creation and reporting remain available. This is separate from the manual-update preference, which continues tracking.

`LinkedAssemblySafetyService` saves a per-assembly SHA-256 suspension snapshot in document strings (`AssemblyManager` section, `LinkedAssemblySuspension` entry). Before disabling, queued bookkeeping is drained without geometry propagation. The snapshot includes current linked geometry and attributes, relevant group membership, recursive hardware block definitions, and stored category/graph/pending state. Readable snapshots are required for safe resume; missing evidence is not permission to infer transforms. It is a baseline hash, not a geometry backup.

Re-enabling proves each suspended assembly unchanged before allowing work. Changed assemblies receive `LinkTrackingSuspended` issues and remain blocked; unaffected assemblies can resume independently. Exact restoration or controlled removal/recreation is required for affected assemblies. Newly generated assemblies explicitly register their known provenance; an assembly created while OFF still has subsequent untracked edits checked. Repeated toggling does not replace an unresolved baseline with the changed state. Stored marker changes are checked against the cache, including undo-related changes. A shared-source original promotion cannot bypass an unresolved suspended consumer.

This boundary protects edits during the explicit master-OFF workflow. It does not solve every edit made while Gazelle was unloaded, nor replace the deferred-original limitation above. Save a backup before disabling or recreating an assembly. Settings-save failure restores the prior preference rather than silently leaving tracking disabled without a prepared baseline.

### Processing and feedback

The plugin-owned `AssemblyLinkEventService` subscribes to before-transform, replace/delete/add, material/link attribute, group, unit-scale, document-close/open, command begin/end, undo/redo, and idle events. It does not subscribe to instance-definition topology, purge, explicit undelete, or save-time flush events.

Callbacks capture immutable facts; the main reducer processes them at idle. Replacement chains, command-batch context, copy flags, grip owners, and structural evidence are reduced before updates. `AssemblyLinkMutationGate` suppresses recursively generated events; a processing flag prevents nested updates. State is keyed by document runtime serial number and cleared on close.

Placement metadata synchronization is limited to relationships incident on moved/replaced nodes, including an independently placed flat child. Unrelated generated objects are not rewritten. A placement-only pass reuses its reconciled store instead of loading and normalizing it a second time. Rigid whole-object moves skip unused pre-transform mass-property snapshots; actual replacement comparisons, subobject/grip evidence and non-rigid snapshots remain intact. Repository recovery-reference checks use indexed node/edge lookups while retaining duplicate-identity validation. Persistence and graph safety checks still apply; the handler is not a no-work transform cache.

`ReferenceUpdateService` traverses immediate edges, updates successful targets, then reconciles affected assemblies. It follows regenerated objects that are inputs to other assemblies. Flat synchronization can start further bounded waves; repeated dependencies stop with a review item. Per-assembly cycles are rejected by repository validation. The implementation does not prepare all document replacements in a single all-or-nothing plan.

Command-line feedback reports preparation, linked-geometry updates, categorization/quantity/material work, existing flat-output synchronization, elapsed completion time, counts, and remaining issues. No-op unlinked automatic requests are silent. Counts describe updated linked objects, not a guarantee that every output is ready for fabrication.

### Undo, save, and reopen

Automatic/modeless work uses named Rhino undo records; explicit actions use their own records when not already inside one. During undo/redo the service suspends normal propagation, inspects restored links without writing metadata, and exposes transient health warnings. It deliberately does not immediately replay a restored pending update and disturb redo.

The graph and pending records are serialized in document strings, so normal Rhino saving is required to retain them on disk. There is no save callback guaranteeing that an event queue has drained before a save. Save after updates have completed; reopening only resumes requests already recorded in the saved store.

The regression suite exercises native geometry events and pending-marker restart behavior, but does not certify desktop undo/redo document-string restoration, save/close/reopen command sequences, or all third-party event streams.

## Explicit additive component editing

1. Ungroup **one** occurrence in ORIGINAL ASSEMBLIES.
2. Keep retained members unchanged, add new supported closed parts or explicitly marked hardware, then regroup.
3. Run `UpdateComponent` or use **Update Component**, choose the assembly/component, and select the complete replacement group.
4. Click **Update Assembly** to apply the staged membership and recategorize.

Staging reassigns the selected occurrence's generated-group UUID and records identities, not a geometry snapshot. It does not create new inputs/copies/flats. Any staged plan holds ordinary automatic propagation for that assembly until the explicit update, even if the automatic toggle is on.

Preflight verifies retained members, source/original geometry/material agreement, one usable rigid source-to-original placement, group ownership, independent additions, and any selected occurrence's copied layouts. It is repeated at apply time. The addition's input counterpart is created with that occurrence's stored inverse transform; the operator's original addition retains its UUID. Existing copied descendants of that selected occurrence receive matching additions.

Unchanged siblings do not receive additions or require inferred alignment. A changed C01 occurrence becomes a new component number when needed, while unchanged C01 occurrences retain their identity. A matching existing category is reused; a singleton can retain its number. Staging the same occurrence replaces only its plan; other occurrences can be staged independently. Older all-occurrence pending lists authorize only their saved selected occurrence in this build.

The additive apply preflights the complete staged batch and attempts rollback of its own created objects/groups/layers and modified attributes if application fails. Subsequent reference propagation/categorization is a separate stage, so the entire manual workflow is not failure-atomic.

Rejected operations include retained-member removal/split/replacement, mixed linked occurrences, unmarked blocks as new parts, shared input ownership, stale membership/geometry, and copied groups already used as another assembly's input/original group. There is no dedicated cancel-staged-plan button. Restore a safe state/undo as appropriate or restage the complete selected group; do not keep changing retained members under a staged plan.

## Categorization and manufacturing output

Only complete live source evidence votes on part/component categories. Generated outputs inherit identities through their lineage. Part comparison uses raw geometry measures and normalized parent material, with stable unchanged category seeds; see [Part Categorization Algorithm](PartCategorizationAlgorithm.md).

Successful reconciliation updates source/generated membership, category names, quantities, reference names, managed layers, and relevant cache invalidation. BOM/material-estimate/nesting caches are invalidated, not automatically regenerated or exported.

Part colors are stored as nullable `PartRecord.LayerColorArgb` for legacy compatibility. Live category layers take precedence over saved colors. Initial creation uses 21 predefined colors then randomized nonduplicate candidates; subsequent output/refresh reuses the saved choice. New categories consider existing live/custom colors, and merges reuse their destination color. Existing duplicate colors are not forcibly changed.

Protected missing/unsupported source categories keep their prior quantities/identities. Complete unrelated categories may proceed. A genuinely ambiguous changed part assignment currently aborts the whole part-assignment pass for that assembly; it is not localized in the same way as missing evidence. Hardware material metadata in saved hardware/BOM records is not fully refreshed by material-only linked edits; verify hardware reports after such changes.

### Existing flat layouts

`FlatPartSynchronizationService` runs only when at least one live graph-managed flat output exists. It:

- Retains an active representative's UUID and current bounding-box-min anchor.
- Reuses a usable orientation; when necessary, recalculates largest-face flattening and records the new exact transform.
- Appends a newly required supported category to the right of the existing layout; it does not reflow everything into newly sorted material rows.
- Updates quantity, material, thickness, part name, category color, and owned label content.
- Preserves existing part-label UUIDs and planes. A label does not automatically follow a part moved separately from that label.
- Updates generated row headers; row-header UUIDs are not promised stable.
- Retires only safely managed superseded representatives. Active or inactive graph/reference/downstream ownership protects a dependent representative from deletion.

Generated flat annotations use `Gazelle.FlatAnnotation.*` ownership tags. Recognizable untagged legacy labels on exact managed layers may be adopted; arbitrary text is retained. This recognition is not a universal way to distinguish a user-created exact imitation of old generated text.

Inactive, unsupported, ambiguous, or protected output is preserved with issues. A missing original/copied object is not generically recreated by **Update Assembly**. Flat synchronization can fill supported representative gaps within an existing live flat layout, but it is not a complete missing-link repair tool.

**Copy / Orient Components** appends new representatives for every category on each run. It does not replace existing copies or fill only the missing category. Use **Place Component** for an individual additional/missing drawing view. It validates a complete current representative original group and source-to-original geometry/material parity, then creates fresh nodes and direct original/hardware-to-copy edges with the source occurrence ID. Independent groups carry their own transforms, use the same plan-orientation policy, and are centered at the picked point. No manufactured occurrence is added.

Multiple copies of an occurrence and copies inherited from merged component categories are legitimate views. Reconciliation reports only a current category with no copied representation when copied output already exists; it no longer treats multiple represented occurrences as a rebuild reason. Each view follows its actual original occurrence through recategorization. Staged additions fan out to every complete copied group linked to that occurrence, including individually repositioned views. Place Component blocks staged/unsafe/stale representatives rather than implicitly updating the assembly; the master switch must be on. Whole marked hardware is supported, but manufacturing block-leaf recipes without safe source matching are rejected.

**Place BOM** is a static reporting workflow, separate from this link graph. `PlacedBomService` lays out selected columns from current material estimates, grouped hardware, and unaccounted rows in paper units. It measures native text, allocates the selected width, wraps at 0.125-inch paper height first, then reduces height only if necessary (minimum 0.02 inches). Its text/grid geometry belongs to the chosen page and one fresh group. It validates fit before insertion and removes its newly created partial table if insertion fails. `PlaceMaterialEstimate` routes to the same command/UI workflow; existing estimate/BOM export schemas are unchanged.

### Colors and narrow cleanup

Existing categories keep their established layer colors, including custom colors. New part categories use a color different from their predecessor when part coloring is enabled; colors are not globally unique. New categories use black when coloring is off. Input styling and explicit per-object overrides are preserved.

Obsolete original/copied component part leaves are removed only when no current membership/link requires them, no objects or child layers remain, and they are not current/reference layers. Hidden, locked, reference, and block-definition geometry count as contents. Component/assembly roots and custom layers are not broadly pruned. Flat synchronization separately cleans its own safe retired output/label layers.

## Link Issues and recovery boundaries

**Link Issues** is at the bottom of Assembly Manager, below Workflow. It shows selected-assembly and document-wide open counts, persisted reasons and available object/component/part context. Transient read-only link-health warnings are shown separately.

**Refresh Issues** reloads persisted issues and performs read-only inspection. It does not propagate, renumber, rebind, or resolve issues. The visible window refreshes its display when saved store data/health warnings change.

Choose recovery based on the issue:

- For intended input edits or proven original edits, use **Update Assembly** after fixing the stated obstruction.
- For unwanted copied/flat edits, preserve anything needed separately before using **Update Assembly**, which may overwrite them from their parents.
- For conflicting original/input edits, deleted sources, structural replacements, duplicate identities, or unsupported block provenance, preserve the model and inspect the exact IDs. There is no generalized automatic merge/relink/detach tool.
- For an output gap, inspect existing outputs before invoking creation commands; they have different append/replace behavior.
- If metadata cannot be read safely, use a compatible build or recover/repair from a backed-up model. An empty issues panel is not proof that unreadable data is healthy.

## Code map and verification

| Area | Implementation |
| --- | --- |
| Persistence/constants | `Core/AssemblyModels.cs`, `Core/Constants.cs`, `Services/AssemblyRepository.cs` |
| Link registration and ownership tags | `Services/AssemblyLineageService.cs` |
| Event capture, deferred updates, undo inspection | `Services/AssemblyLinkEventService.cs` |
| Propagation, original promotion, feedback | `Services/ReferenceUpdateService.cs` |
| Part/component reconciliation | `Services/AssemblyCategorizationReconciliationService.cs` |
| Staged membership and placement proof | `Services/ComponentUpdateService.cs`, `Services/ComponentPlacementMatcher.cs` |
| Existing flat-output maintenance | `Services/FlatPartSynchronizationService.cs` |
| Output creation | `Services/AssemblyGenerationService.cs`, `ComponentDrawingService.cs`, `LayPartsFlatService.cs` |
| Fitted static BOM placement | `Services/PlacedBomService.cs`, `Core/PlacedBomModels.cs`, `Commands/PlaceBomCommand.cs`, `UI/BomColumnsDialog.cs` |
| Window/settings composition | `UI/AssemblyManagerDialog.cs`, `UI/SettingsDialog.cs`, `Infrastructure/ServiceFactory.cs` |
| Regression scope and execution | [Regression runner README](../../tests/Gazelle.Regression/README.md) |

The current runner has 109 cases: 105 default native service/storage/geometry/font cases and four opt-in native BOM layout cases. New cases cover independent component views, split/merge/addition fan-out, font wrapping/shrinking, column selection, bounds and paper units. Earlier cases include moved hardware blocks, repository normalization, master suspension/resume, temporary-file persistence and extended colors. Layout cases run separately because page initialization can stall in a hidden console host. Tests use disposable documents and injected settings/material libraries. Idle is invoked through the production callback, and move/rotation cases inject the pre-transform fact that the console host does not emit. Passing these tests does not substitute for visual UI testing, exhaustive desktop command/undo/save/reopen testing, advanced block scenarios, or large-production-model performance testing. See the [runner README](../../tests/Gazelle.Regression/README.md) for timing scope and coverage limits.

## Planned work, not shipped controls

- Explicit cancel/preview/removal/reassignment and split/join component workflows.
- General relink/detach/promote-derived resolution with previews and clear destructive consequences.
- Unique nested block-leaf locators and definition/worksession policies.
- Complete open/save reconciliation and stronger durable deferred-source geometry proof.
- Automatic missing-view reconstruction, replace-existing copied-view workflows, and complete generated-annotation/linework dependencies. Explicit individual placement is available through Place Component.
- More atomic mutation/persistence, document-global ownership/revision support if needed, and desktop undo/reopen certification.
- Large-model event/performance measurement and geometry-equivalence checks for chirality/complex curved solids.

These are future design directions. No `Enable Live Links`, `Review Linked Changes`, `Relink Source`, or generalized `Promote Derived Geometry` command should be inferred from this roadmap.
