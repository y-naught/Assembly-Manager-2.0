# Linked Assembly Architecture

Status: working design plus the first implemented slice on the `Linking-Assemblies` branch.

This document defines a safe path from Gazelle's current manual source-reference refresh to live, persistent assembly links. The first release should establish dependable identity, event handling, transform composition, persistence, and undo behavior before it attempts structural inference such as splits, joins, or automatic group membership changes.

The implementation now includes the additive version 2 node/edge/conflict graph, immediate links for newly generated originals, component copies, and flat parts, transform tracking, optional automatic one-to-one source propagation across assembly graphs, validated inverse promotion of safe `OriginalAssembly` direct-copy edits, procedural flat-part synchronization, global-unit scaling, quarantine of copied/flat edits and unsafe original edits, manual updating through **Update Assembly**, stable live part/component recategorization for complete supported occurrences, and a post-undo/redo health check. Block-leaf locators, generalized promote/detach conflict controls, automatic copied-component representative creation, and structural propagation remain planned work.

## Automatic propagation preference and deferred work

Plugin settings schema 9 adds `AssemblyManager.AutomaticallyPropagateChangesInAssembly`, defaulting to `true` for new and existing settings. The UI label is **Automatically propagate changes in assembly**. The window's former **Refresh References** action is now **Update Assembly**; `RefreshAssemblyReferences` remains the compatible Rhino command name.

Disabling automatic propagation does not disable event capture. The idle processor still reconciles replacement UUIDs, relative placement transforms, group/structural evidence, and safety conflicts, but does not dispatch source refreshes, inverse promotion, recategorization, or flat manufacturing updates. Pending requests are persisted on source/original nodes as `PendingSourceUpdate` and `PendingOriginalUpdate` metadata. A pending original marker is written only after the first pre-edit geometry comparison proves its source relationship; subsequent paused edits retain that already-validated authority. The pre-edit material-type signature (normalized parent/category ID and parent display name) is retained as `PendingOriginalSourceMaterial` and checked against the current source before promotion, so an uncaptured source material-type change cannot be silently overwritten. Stock-shape details are not part of that signature because generated originals normally normalize stock assignments to the parent material. Simultaneous pending source/original edits, or multiple edited originals sharing one source, are quarantined rather than arbitrated.

`AssemblyLinkEventService.UpdateAssembly` drains current event facts without automatic dispatch, accepts safe pending originals in the selected assembly, and invokes one normal reference update covering its shared-input and downstream consumers, not unrelated assemblies. It does not change the preference. Re-enabling propagation resumes pending work; reopening a document or loading the plugin with an existing document queues inspection of persisted markers. Undo/redo inspection remains read-only and does not immediately replay a restored pending update, preserving Rhino's redo chain. Unresolved original-promotion conflicts stay protected even during manual rebuilding.

Changes to Gazelle's six material assignment attributes are refresh requests even without geometry replacement. Source changes propagate downstream; a safe original material edit is promoted with its geometry. Direct copied/flat material edits are quarantined for review. Material assignment/clearing, categorization, quantities, and already-created flat output are updated through the same gated pipeline.

## Goals

The linked assembly system should:

- Follow each manufacturable source BREP or extrusion into its generated `ORIGINAL ASSEMBLIES` occurrence, representative `COPIED COMPONENTS` occurrence, and laid-flat `PARTS` output.
- Propagate a supported source geometry change downstream without requiring the operator to recreate the assembly.
- Treat a supported one-to-one BREP edit on an `OriginalAssembly` occurrence as an edit to its recorded design source when the incoming `DirectCopy` relationship can be inverted without ambiguity.
- Preserve the independent model-space placement of generated component views and flat parts.
- Survive normal Rhino replacement, transform, undo/redo, save, close, and reopen operations.
- Detect structural changes, preserve the user's work, and present an explicit resolution when the correct intent is ambiguous.
- Distinguish physical part occurrences from categorized part definitions, and physical component occurrences from categorized component definitions.
- Keep the document recoverable if persisted metadata and Rhino object IDs become inconsistent.

## Initial non-goals

The first phase will not:

- Automatically infer or accept BREP splits, joins, or arbitrary replacement sets.
- Infer an upstream source from edits to copied components, flat parts, block leaves, or ambiguous generated geometry. The only generated-shape promotion in this slice is the validated one-to-one `DirectCopy` path from an `OriginalAssembly` occurrence to its recorded design source.
- Link every drawing curve, dimension, text label, or other annotation. These outputs can initially be marked stale and regenerated.
- Guess missing affine transforms from two legacy BREPs that merely appear equivalent.
- Treat copied Rhino objects as new linked occurrences unless the copy was created through a Gazelle workflow.
- Promise full linked-block or worksession support. Block-leaf provenance is part of the schema, but topology-changing definition edits require a later phase.

## Baseline before this branch

The existing code already contains the beginning of a link model:

- `AssemblyStore.SchemaVersion` is currently `1`.
- `GeometryReferenceRecord` stores a source Rhino object ID, target Rhino object ID, target role, and source-to-target transform.
- `AssemblyGenerationService` records the translation from a selected source object to the generated `ORIGINAL ASSEMBLIES` object.
- `ReferenceUpdateService` manually refreshes only those generated original-assembly objects.
- `ComponentDrawingService` creates and transforms copied component geometry, but it does not persist the copied object IDs or the final transforms.
- `LayPartsFlatService` persists the flat BREP IDs, but not the complete face-orientation, long-axis rotation, and placement transform.
- Block-derived part candidates can share the same source instance ID even though they represent different definition leaves.

These constraints make the current source-to-original references migratable, but copied-component and flat-part links must be established when those outputs are rebuilt with the new code.

## Design principles

### Source geometry is authoritative

Shape changes flow in one direction by default:

```text
design source -> original assembly occurrence -> copied component occurrence
                                      |
                                      +---------> flat part output
```

A rigid transform of a derived object is a permitted layout edit. A supported one-to-one shape edit on an `OriginalAssembly` occurrence is also a permitted authoring action when that node has one unambiguous, invertible `DirectCopy` edge from a design `Source`: Gazelle maps the edited BREP back through the inverse edge, replaces the authoritative source geometry, and then runs normal source-to-descendant propagation. This is an implicit synchronization with the source, not a permanent reversal of graph authority. Shape edits to copied-component and flat-part outputs, and any original edit that fails the promotion safety checks, remain quarantined. **Update Assembly** can rebuild quarantined copied/flat output from its source but preserves unresolved original-promotion edits; generalized `Promote to source` and `Detach` controls remain planned.

### Rhino object IDs are locators, not durable lineage

Gazelle must continue storing each Rhino object UUID, because that is the efficient way to find an object in a document. It must not use that UUID as the only logical identity:

- Replacement and some transform operations can create a new document object.
- A split is one input becoming several outputs.
- A join is several inputs becoming one output.
- Copy operations can duplicate object attributes, including Gazelle user strings.

Every linked physical object therefore receives a stable Gazelle `NodeId`. The current Rhino `ObjectId` is a mutable locator on that node. Generated objects also receive the `NodeId` in their attributes so the graph can detect changed UUIDs and duplicate copied identities. The initial source nodes remain assembly-scoped and are not given a scalar object tag because one source can participate in multiple assemblies; source-side recovery uses the stored UUID and pre-undo snapshot until a document-global source index is added.

### Occurrences and definitions are different concepts

The current `PartRecord` represents a categorized part type and also stores all source/generated occurrences. Live updates need explicit levels:

- `PartOccurrenceId`: one physical part in one source component occurrence.
- `PartDefinitionId`: a categorized manufacturing part such as `P01` shared by equivalent occurrences.
- `ComponentOccurrenceId`: one physical source group/component instance.
- `ComponentDefinitionId`: a categorized component such as `C01` shared by equivalent component occurrences.

This distinction allows one edited occurrence to leave `P01` and become a new definition without changing the identity of the unaffected `P01` occurrences.

### Use immediate edges, not a root-to-every-output star

The graph should record how the pipeline actually produced an object. A copied component is derived from an original-assembly occurrence; a flat output is derived from a representative original-assembly occurrence. It should not store independent direct links from the design source to every descendant.

Immediate edges provide three important properties:

- Repositioning a copied component updates one incoming edge without rewriting every root-relative transform.
- Transform composition remains explicit and testable.
- The graph preserves which representative and which operation produced an output.

A source object can participate in more than one assembly. The initial implementation keeps a separate source node in each assembly-scoped graph and deliberately does not write a scalar assembly ID onto the shared source object. A later document-global source index can deduplicate those logical source identities while keeping edges assembly-scoped.

## Version 2 data model

The exact C# names may change, but the persistence model should have the following shape.

```csharp
public sealed class AssemblyLinkGraphRecord
{
    public int SchemaVersion { get; set; } = 2;
    public Guid DocumentLinkId { get; set; }
    public long Revision { get; set; }
    public List<LinkNodeRecord> Nodes { get; set; } = new();
    public List<LinkEdgeRecord> Edges { get; set; } = new();
    public List<PartOccurrenceLinkRecord> PartOccurrences { get; set; } = new();
    public List<PartDefinitionLinkRecord> PartDefinitions { get; set; } = new();
    public List<ComponentOccurrenceLinkRecord> ComponentOccurrences { get; set; } = new();
    public List<ComponentDefinitionLinkRecord> ComponentDefinitions { get; set; } = new();
    public List<LinkConflictRecord> Conflicts { get; set; } = new();
    public List<LinkTombstoneRecord> Tombstones { get; set; } = new();
}
```

### Link nodes

```csharp
public sealed class LinkNodeRecord
{
    public Guid NodeId { get; set; }
    public Guid RhinoObjectId { get; set; }
    public Guid? OwnerAssemblyId { get; set; }
    public LinkNodeRole Role { get; set; }
    public SourceLocatorRecord? SourceLocator { get; set; }
    public Guid? PartOccurrenceId { get; set; }
    public Guid? PartDefinitionId { get; set; }
    public Guid? ComponentOccurrenceId { get; set; }
    public Guid? ComponentDefinitionId { get; set; }
    public uint LastGeometryCrc { get; set; }
    public string LastManufacturingFingerprint { get; set; } = string.Empty;
    public long Revision { get; set; }
    public LinkNodeState State { get; set; }
}
```

Recommended roles are:

- `DesignSource`
- `OriginalAssemblyOccurrence`
- `CopiedComponentOccurrence`
- `FlatPart`
- Later: `Hardware`, `GeneratedLinework`, `PartLabel`, `RowLabel`, and `Dimension`

`GeometryBase.DataCRC(0)` can be used as a quick indication that exact geometry data changed. The existing manufacturing fingerprint remains a separate, tolerance-aware categorization value. A CRC is not a logical identity and should not be the only equality test.

### Link edges

```csharp
public sealed class LinkEdgeRecord
{
    public Guid EdgeId { get; set; }
    public Guid AssemblyId { get; set; }
    public Guid ParentNodeId { get; set; }
    public Guid ChildNodeId { get; set; }
    public LinkEdgeKind Kind { get; set; }
    public LinkTransformPolicy TransformPolicy { get; set; }
    public TransformRecord ParentToChild { get; set; } = TransformRecord.Identity();
    public LayFlatRecipeRecord? LayFlatRecipe { get; set; }
    public long LastAppliedParentRevision { get; set; }
}
```

Recommended edge kinds are:

- `AffineClone`
- `ExtractBlockLeaf`
- `LayFlat`

Recommended transform policies are:

- `FixedAffine`: apply the recorded matrix to the current parent geometry.
- `ProceduralLayFlat`: re-evaluate the orientation recipe, preserve the layout anchor, and store the newly applied matrix.

The graph must be validated as acyclic before propagation. A cycle is quarantined as a conflict rather than partially evaluated.

### Source locators

A normal BREP can use its Rhino object ID and stable node ID. A part extracted from a block needs a richer locator, for example:

```csharp
public sealed class SourceLocatorRecord
{
    public Guid RootObjectId { get; set; }
    public List<Guid> InstanceDefinitionPath { get; set; } = new();
    public Guid? DefinitionObjectId { get; set; }
    public string FallbackFingerprint { get; set; } = string.Empty;
}
```

The current block path code records only the root instance object ID in each candidate. Without a definition path, refreshing a leaf can incorrectly substitute the entire instance or a different leaf. The first slice records a `BlockDefinitionPart` provenance edge so the limitation remains visible, but never executes it as a one-to-one affine copy; refresh preserves the existing output and creates a conflict. A later phase must add the definition path before block-leaf regeneration is enabled.

### Attribute recovery tags

The document graph is authoritative. Object attributes should duplicate only enough information to locate and recover graph membership:

- `AssemblyManager.NodeId`
- `AssemblyManager.Role`
- `AssemblyManager.IncomingEdgeId`
- `AssemblyManager.PartOccurrenceId`
- `AssemblyManager.PartDefinitionId`
- `AssemblyManager.ComponentOccurrenceId`
- `AssemblyManager.ComponentDefinitionId`
- `AssemblyManager.GraphSchema`

Source membership in multiple assemblies remains in the document graph. If a compact membership hint is needed on the object, it must support a set rather than one assembly ID.

## Transform semantics

Rhino transformation matrices act on the left of a point. Rhino's `A * B` composition applies `B` first and then `A`.

For an immediate edge from parent `p` to child `c`:

```text
G_c = E_pc * G_p
```

where `G` denotes geometry in document coordinates and `E_pc` is the stored parent-to-child transform.

For a path `source -> original -> copied`:

```text
E_source_to_copied = E_original_to_copied * E_source_to_original
```

### Source changes

When an authoritative source geometry changes, the edge remains unchanged. Descendant geometry is rebuilt from the new parent geometry and the stored edge transform. Invertible scale, shear, and orientation-reversing mirror transforms are treated as shape changes. An orientation-preserving rigid move or rotation is treated as placement: each outgoing edge is post-multiplied by the inverse so independently arranged outputs stay in place.

### Original-assembly inverse promotion

An `OriginalAssembly` occurrence is the one generated stage that can act as a safe editing surface without changing the direction of graph authority. Let `E_so` be its active `DirectCopy` edge from design source `s` to original occurrence `o`:

```text
G_o = E_so * G_s
```

After a supported one-to-one edit produces `G_o'`, Gazelle derives the revised source geometry as:

```text
G_s' = inverse(E_so) * G_o'
```

Gazelle first proves that the captured pre-edit original still equals `E_so * G_s`, then prepares and validates `G_s'` and verifies that applying `E_so` again reproduces the edited original within document tolerance. It replaces the design-source BREP under event suppression, saves the accepted graph state, and runs normal source propagation under the outer `Gazelle Auto Update` undo record. The edge remains unchanged. Propagation regenerates the original occurrence, copied-component descendants, flat-part descendants, and any downstream assembly whose source is one of those regenerated objects. This keeps the design source authoritative while allowing the operator to edit the generated assembled occurrence directly. Source replacement and descendant refresh are guarded stages of one automatic update, but they are not claimed to be one failure-atomic persistence transaction.

Inverse promotion is allowed only when the edited node has exactly one active incoming edge from a live `Source`, that edge uses `DirectCopy`, the matrix is finite and invertible, the captured pre-edit geometry proves the edge was synchronized, both object identities are unique, and the edited geometry is a supported closed BREP. A replacement chain that still proves one-to-one identity is acceptable. Gazelle leaves the edited branch unchanged and records a review item instead of promoting when it sees related split/join or delete/add structure, a block-definition leaf, duplicate identity, missing or competing incoming edges, unsupported geometry, simultaneous source and original shape edits, more than one edited original for one shared source in the same batch, or a source whose upstream ownership is itself ambiguous. A source referenced only as a `Source` by multiple assembly graphs is not ambiguous; after a successful promotion, every such graph receives the normal source refresh.

Copied-component and flat-part shape edits do not use inverse promotion. A copied component can represent a consolidated component definition, and a flat part is produced by a procedural orientation recipe; silently mapping either edit upstream could modify the wrong physical occurrence. Those edits continue to quarantine until an explicit promote, detach, or structural-resolution workflow is available.

### Derived layout transforms

If a user applies a world-space transform `D` to a derived child, the geometry already satisfies:

```text
G_c' = D * G_c
```

For a rigid move or rotation, the incoming edge is updated as:

```text
E_pc' = D * E_pc
```

Each outgoing edge from that child is updated as `E_child_to_descendant' = E_child_to_descendant * inverse(D)`, preserving independently arranged descendants in model space. Grip-owner and non-rigid transforms are shape edits, not whole-object layout changes, and must not be composed into an incoming placement edge. A qualifying `OriginalAssembly` shape edit follows the inverse-promotion path above; the same edit on a copied component or flat part is quarantined.

### Creation-time matrices

Creation services should accumulate and register exact matrices rather than infer them afterward:

- Source to original assembly: `A`, the existing assembly translation.
- Original assembly to copied component: `R * T`, where `T` moves the copied group to its row and `R` is the chosen plan rotation.
- Original assembly to flat output: `P * L * O`, where `O` orients the selected face to World XY, `L` rotates the long dimension, and `P` places the object in its material row.

`RotateLongDimensionToY` should return its transform instead of only mutating geometry. Component move/rotation helpers should likewise return or accumulate the transforms they apply.

### Procedural flat parts

A fixed historical matrix may not leave revised geometry flat if the largest face changes. A flat edge therefore stores both:

- The exact matrix last applied.
- The orientation rule, selected face signature when available, long-axis rule, row/material key, and a stable layout anchor.

For a non-structural revision, Gazelle first tries to preserve the previously accepted transform when it still leaves the dominant face parallel to World XY and satisfies the long-axis rule. A recorded user plan-rotation override is preserved even when it intentionally places the long axis along X. The implemented slice keeps the existing bounding-box-min layout anchor; if the prior transform is no longer valid, it recomputes the largest-face orientation and long-axis rotation. Persisting a selected-face signature and presenting an ambiguity review remain follow-up work.

Re-running **Lay Parts Flat** replaces the single live flat output for each part in place so its Rhino UUID remains stable, then cascades that source change into any downstream assembly graph. System-owned row and part labels are cleared and recreated. If more than one live flat output is recorded for a part, Gazelle preserves them and reports the ambiguity instead of guessing which UUID should survive.

### Global unit scaling

Changing model units with geometry scaling transforms the parent and child together. It must not be interpreted as an independent layout edit on every linked object. For global scale `S`, update stored relationships by conjugation:

```text
E' = S * E * inverse(S)
```

and do not rebuild all children a second time. The implemented reducer snapshots the UUIDs that existed before Rhino scales the document, maps replacement chains to their final UUIDs, and conjugates only edges whose two endpoints were in that snapshot. Geometry created later in the same idle batch is therefore not scaled a second time.

Part fingerprints and rounded thicknesses also use model units. The unit callback fingerprints an exactly scaled duplicate of every manufacturable linked occurrence carrying a part identity before Rhino changes the live objects. Using generated originals as well as design sources gives block-derived leaves their own category evidence even though their shared block-instance source is not itself a manufacturable BREP. Any occurrence that still matches the stored canonical category can safely advance it. If no occurrence can prove that category—for example, every occurrence was already edited—the compare-and-swap fails closed with a `CanonicalUnitScaleUnresolved` review item rather than blessing an edited shape or silently treating an old-unit fingerprint as current.

Non-affine, invalid, or singular matrices are rejected for normal affine edges. Mirrors and invertible non-uniform scales are valid, but chirality and determinant should be retained for diagnostics. The implemented `OriginalAssembly` inverse-promotion path requires one unambiguous invertible `DirectCopy` edge; generalized promotion from other derived objects remains an explicit future workflow.

## Event architecture

### Lifecycle

The plugin should register watchers during plugin load and unregister them during shutdown. Each open `RhinoDoc` gets independent queued state keyed by its runtime serial number. The implemented slice uses one plugin-owned watcher service with per-document queues and clears them on document close; a richer `DocumentLinkSession` remains a useful later refactor.

The relevant Rhino 8.0-compatible events are:

- `RhinoDoc.AddRhinoObject`
- `RhinoDoc.DeleteRhinoObject`
- `RhinoDoc.UndeleteRhinoObject`
- `RhinoDoc.PurgeRhinoObject`
- `RhinoDoc.ReplaceRhinoObject`
- `RhinoDoc.ModifyObjectAttributes`
- `RhinoDoc.BeforeTransformObjects`
- `RhinoDoc.GroupTableEvent`
- `RhinoDoc.InstanceDefinitionTableEvent`
- `RhinoDoc.UnitsChangedWithScaling`
- document open, save, and close events
- `Command.BeginCommand`, `Command.EndCommand`, and `Command.UndoRedo`
- `RhinoApp.Idle`

The implemented slice subscribes to add, delete, replace, attribute, group, before-transform, unit-scale, close, undo/redo, and idle events. Instance-definition topology, purge, explicit undelete, and open/save reconciliation remain later work. `RhinoDoc.AfterTransformObjects` was introduced after the project's pinned RhinoCommon 8.0 dependency, so this branch does not require it.

### Event handlers only capture facts

Rhino document event handlers must not run propagation or mutate the document. They enqueue small immutable event facts containing, as applicable:

- Document runtime serial number.
- Rhino object ID and recovered Gazelle node ID.
- Event kind.
- Before/after attributes or geometry CRC.
- Transform matrix, object IDs, grip-owner IDs, and `ObjectsWillBeCopied`.
- Active command and undo-record context.
- Timestamp and monotonic session sequence.

No `RhinoObject` reference is retained beyond the callback.

### Reduction and debounce

One Rhino operation can generate several callbacks. In particular, replace is followed by delete plus add during ordinary editing, or delete plus undelete during undo/redo. The reducer must treat those as one logical replacement.

The implemented slice batches callbacks and flushes them on the UI thread at idle. A future command-boundary reducer can add a short debounce for gumball or modeless-editor bursts. Within a batch:

- Sequential whole-object transforms for one derived node compose in event order.
- Repeated replacements collapse to the latest geometry revision.
- Add/delete sets are preserved for structural analysis.
- A source geometry edit and a target geometry edit in the same branch become a conflict.

The current Rhino 8.0 `BeforeTransformObjects` event supplies the transform, affected objects/grip owners, and whether objects will be copied. If `ObjectsWillBeCopied` is true, the existing node's edge is not modified. An untagged result remains ordinary detached geometry. If the added copy actually inherited an existing Gazelle node tag, the graph keeps its current object as primary and opens a duplicate-identity conflict; automatic identity repair remains planned.

McNeel documents that direct `RhinoDoc.Objects.Transform` calls do not raise `BeforeTransformObjects`; callers must use `TransformCommand.TransformObjects` to participate in that event path. Gazelle's own creation/layout services run inside an internal mutation scope and register their exact accumulated matrices directly. An uncoordinated third-party programmatic transform is conservatively treated as a replacement/shape conflict because no trustworthy rigid delta is available.

### Reentrancy

Propagation itself raises Rhino events. Each session therefore needs:

- A nestable internal `MutationScope`.
- The update transaction/revision currently being applied.
- A set of expected internal object IDs and replacements.
- A processor-active flag and a pending-after-current queue.
- A maximum propagation generation and visited-node set.

Events known to be produced by the active Gazelle transaction are acknowledged but not converted into new user changes. The implemented slice uses a nested process-wide depth because Rhino document mutations and their callbacks execute synchronously on the UI thread; a later per-document transaction identity is preferable if background/modeless mutation support is added.

### Propagation transaction

For each assembly batch:

1. Reload or validate the in-memory graph revision.
2. Reconcile Rhino object IDs from `NodeId` attribute tags.
3. Reduce events into geometry, layout, attribute, deletion, and structural changes.
4. Create conflicts for ambiguous changes and exclude those branches.
5. Recompute affected part and component categorization metadata.
6. Validate the affected DAG and topologically sort descendants.
7. Prepare and validate all replacement geometry before mutating the document.
8. Enter an internal mutation and undo scope.
9. Replace each target while preserving its target-side layer, groups, material/approved attributes, and stable tags.
10. Reconcile a changed Rhino object ID immediately from the replacement event/tag.
11. Update fingerprints, revisions, cached quantities, labels/stale flags, and conflict state.
12. Persist the graph once, record action history once, and redraw once.

An update should be atomic per assembly where practical. If a branch cannot be prepared, leave its previous output in place and record a conflict instead of deleting good geometry. Independent assemblies can succeed or fail separately.

### Undo and redo

Deferred/modeless propagation uses a named `Gazelle Auto Update` Rhino undo record. Graph metadata must undo with geometry. Because the graph is serialized into document strings, in-Rhino testing still needs to verify whether those string updates always participate in the same Rhino undo record; otherwise a custom undo event must carry before/after graph snapshots.

While Rhino is undoing or redoing:

- Suspend normal change interpretation and propagation.
- Allow Rhino to restore geometry.
- Restore/reload the matching graph snapshot.
- Reconcile object IDs and run a non-mutating health check after undo/redo completes.

The implemented health check resolves current objects from stored UUIDs, stable node tags, and pre-undo snapshot hints, detects duplicate tagged identities, then compares each supported child against its parent's expected transformed solid geometry. It reports transient health warnings without writing document data, so checking the graph cannot disturb Rhino's redo chain. Running **Update Assembly** re-runs this read-only inspection and clears the warning display when the linked geometry is healthy again.

## Change and conflict policy

This table is the target policy. In the implemented slice, safe one-to-one `OriginalAssembly` direct-copy edits use inverse promotion automatically when enabled, or during **Update Assembly**. That action also supplies Rebuild for quarantined copied/flat branches but protects unresolved original-promotion edits. Generalized promotion from copied/flat outputs, Detach, Relink, structural acceptance, and automatic duplicate repair are still planned.

| Change | Default policy |
| --- | --- |
| One-to-one shape edit on a valid design source | Automatically propagate downstream, then deterministically split, retain, or merge its part/component category from complete live evidence. Preserve the old category and record a conflict when evidence is unsupported or ambiguous. |
| Whole-object rigid transform on a design source | Treat as placement; post-multiply outgoing edges by the inverse so independent outputs stay in place. |
| Whole-object orientation-preserving rigid transform on a derived object | Treat as a layout edit; left-multiply its incoming edge and preserve independently placed descendants by updating their outgoing edges. |
| One-to-one shape replacement, grip edit, or non-rigid transform on a valid `OriginalAssembly` direct copy | Inverse-transform the edited closed BREP into its recorded design source, then run normal source propagation. Keep the incoming edge unchanged. |
| Shape edit on a copied component or flat part | Pause the branch; offer Rebuild now, with explicit Promote or Detach in a later workflow. |
| Ambiguous original edit, including split/join, block leaf, duplicate identity, simultaneous source edit, or competing candidates for one shared source | Preserve existing geometry and request review; never guess an upstream replacement. |
| Delete a design source | Mark it orphaned and preserve all generated outputs. Offer Relink, Remove lineage, or Undo. |
| Delete a derived object | Mark it missing. Do not immediately resurrect something the user may have intentionally deleted. Offer Rebuild or Detach. |
| Source becomes open, invalid, or unsupported | Preserve the last valid outputs and pause the branch. |
| Source split, one-to-many replacement | Create a structural review item; do not silently choose identities. |
| Source join, many-to-one replacement | Create a structural review item; do not silently discard descendants. |
| Unlinked BREP added to a tracked source group | Present an Add Part candidate; explicit acceptance in early phases. |
| Source group membership changes | Present component-membership deltas for review. |
| Linked object is copied | New copy is detached by default unless a Gazelle command intentionally registers it. |
| Duplicate `NodeId` is found | Keep the graph's current object as primary; assign the other object a fresh detached identity or request review. |
| Source and original shape both change in one batch | Preserve both and request resolution; never use last-writer-wins or inverse promotion. |
| Block definition topology changes | Pause affected extracted-leaf edges and request remapping. |

Generated layers remain system-owned. An unexpected move to another layer is an attribute conflict or a detach request, not an implicit change to the assembly hierarchy.

## Structural changes

### Splits and joins

Split and join command event streams can suggest lineage, but they do not prove user intent. Commands and third-party plugins can delete and add unrelated objects in one transaction. Geometry overlap, bounding boxes, volume conservation, layers, groups, and material can rank candidates; they must not silently establish production lineage in the initial implementation.

When a split is accepted:

- Tombstone the old occurrence without erasing its audit lineage.
- Give each result a new node and part-occurrence identity.
- Add a structural lineage record from the former occurrence to all results.
- Re-categorize each result and create/reuse part definitions.
- Rebuild affected original, copied-component, and flat branches while preserving unaffected layout anchors.

When a join is accepted:

- Tombstone the former occurrences.
- Give the result a new node and occurrence identity with multiple structural parents.
- Preserve one existing output layout only when the user chooses it; retain other outputs until resolution is committed.

### Adding a part

The first reliable workflow should be an explicit `Add Part to Component` action:

1. Select a source component occurrence/group.
2. Select one or more new closed BREPs/extrusions.
3. Preview the proposed material and part categorization.
4. Choose `Match existing part`, `Create new part`, `Hardware`, or `Ignore`.
5. Assign stable node/occurrence identities.
6. Create original-assembly edges and outputs using the component's assembly transform.
7. Recompute the component signature.
8. Update or split its component definition and create copied/flat outputs as required.

Automatic detection can later feed the same transaction after the user accepts a review card.

### Stable part and component names

The implemented live part pass runs after automatic source propagation, including accepted inverse promotion from `OriginalAssembly`, as well as after manual **Update Assembly**. It seeds established categories from unchanged source occurrences and assigns changed occurrences against those stable categories. It does not opportunistically merge unchanged categories. Re-clustering all unchanged occurrences with pairwise tolerance tests is unsafe because tolerance equivalence is nontransitive: it can manufacture competing clusters within an already accepted `P01` and block an unrelated divergence.

For ten `P01` occurrences, one supported edit beyond that category's tolerances therefore leaves nine `P01` occurrences and assigns the edited occurrence the next monotonically allocated part number. Existing lineage propagation then updates part/component quantities, metadata, and managed output layers. Genuine ambiguity remains a review item; missing or unsupported source evidence protects the affected category without blocking complete unrelated categories. For an assembly with linked flat output, the follow-up flat synchronizer retains stable representative UUIDs and accepted layout, creates missing supported flat categories, retires safely managed superseded representatives without dependents, and updates owned labels and material/thickness rows. It does not create the first flat layout or missing copied-component representatives; arbitrary text and unsafe/protected output remain untouched.

Re-categorization must not renumber the entire assembly:

- If an edited occurrence remains equivalent to its current definition, retain the part name.
- If only some `P01` occurrences change, the unchanged cohort retains `P01`; the changed cohort receives a new monotonically allocated number.
- If every occurrence in a definition changes together and remains mutually equivalent, retain the existing name.
- If an occurrence becomes exactly equivalent to another existing definition, propose or perform a merge according to policy; keep an alias/tombstone for the retired definition.
- Apply the same majority/stability rules to component definitions.

The existing categorizer already includes normalized material identity in part equivalence. The live system should store the categorization tolerances used so that a later document-setting change does not unexpectedly renumber parts.

### Categorized layer maintenance

Before assigning new identities, the live pass snapshots established category colors from managed layers. Existing categories keep those colors, including custom choices; their managed part-output leaves use the same color after reassignment. With coloring enabled, a new category uses the part palette while avoiding the color of the category it left, including palette wraparound. This is visual differentiation from the previous category, not a guarantee of globally unique colors. Flat-part generation resolves the established category color as well. Source-layer colors and object-level color overrides are not changed.

Cleanup is restricted to recognized original/copy component part leaves that are no longer required by current component membership or the link graph. Only empty leaves without children may be deleted; occupancy checks include hidden, locked, reference, and block-definition objects. Current and reference layers are preserved. Known empty leftovers from earlier refreshes are eligible under the same checks, but component/assembly roots, custom layers, flat-part trees, and annotations are not pruned.

### Groups

Rhino group indices are convenient locators but not sufficient logical component identities. Component occurrence IDs should live in the graph and on member-object recovery tags. A deleted/recreated group with the same linked members can be rebound. Membership changes become a reviewed delta.

An object in multiple tracked source groups is ambiguous because the current generator selects the first group index. The live workflow should require an explicit component assignment rather than silently continuing that rule.

## Persistence and migration

### Persistence

Version 2 can continue serializing JSON through `AssemblyRepository` for the first implementation, but it should:

- Keep one in-memory graph per document and serialize once per committed batch.
- Add a stable `DocumentLinkId` stored in the document. `RhinoDoc.RuntimeSerialNumber` is runtime-only and must not be used as persisted document identity.
- Maintain graph and node revisions for stale-job detection.
- Persist conflicts and tombstones so review state survives reopen.
- Retain the existing `PartRecord`, `ComponentRecord`, and ID lists as compatibility views/caches until all consumers use the new graph.

For very large models, a later schema can move or compress graph data into plugin document data. That storage change should not be mixed into phase one unless real document-size testing shows the current JSON approach is inadequate.

### Version 1 migration

The implemented slice performs a conservative additive migration when a version 1 store is loaded and persists a `LegacyMigrationIncomplete` review item. Migrated node IDs are deterministically derived from the assembly, Rhino object, and role so repeated reads of still-unsaved v1 metadata produce the same event and edge identities. Future UI can turn this into the explicit dry-run workflow below.

Migration should be an explicit `Enable Live Links` operation with a dry-run report:

1. Load and validate the existing assembly store.
2. Create a document link ID and graph revision.
3. Convert each valid source-to-original `GeometryReferenceRecord` into source/target nodes and an affine edge using its existing transform.
4. Reuse source nodes when one physical source participates in several assemblies.
5. Tag found source and target objects with stable node IDs under an undoable mutation.
6. Detect duplicate legacy `ReferenceId` user strings and do not treat them as unique node identity.
7. Report missing sources, missing targets, unsupported block leaves, and invalid transforms.
8. Keep the version 1 records available for fallback during the transition.

Copied component transforms cannot be migrated reliably because neither copied IDs nor transforms are currently persisted. Flat object IDs are stored, but their complete transforms are not. The migration UI should therefore say:

```text
Source -> Original links migrated.
Rebuild COPIED COMPONENTS and PARTS once to complete live linking.
```

Legacy outputs must not be deleted or silently adopted. The operator chooses when to replace/rebuild them.

### Open/save reconciliation

After opening a document, run a non-mutating reconciliation before enabling events:

- Index objects by `NodeId` tag.
- Repair unambiguous object-ID changes in memory.
- Mark missing, duplicate, or mismatched nodes as conflicts.
- Compare quick geometry CRCs and graph revisions.
- Do not auto-propagate a large unknown difference immediately on open.

Before save, flush a safe pending batch if Rhino is idle. If a command is active or a structural conflict is unresolved, persist dirty/conflict state and leave geometry unchanged.

## User interface

The current Assembly Manager window has a dedicated **Link Issues** section. It displays the selected assembly's open conflict count, the document-wide total, and scrollable issue details with the recorded reason, part/component names, output location, object and link IDs, and detection time where available. Undo/redo health warnings are shown separately because they are transient document-wide inspection results, not persisted open conflicts.

**Refresh Issues** reloads the saved issues and runs the read-only link-health inspection; it does not propagate geometry, renumber parts, or resolve conflicts. While the window is visible, an idle listener compares the saved document data and cached health warnings and refreshes the display only when they change. Assembly and component selections are preserved, and the listener is removed when the window or document closes.

The broader planned interface should also expose:

- `Live updates`: On, Paused, or Off.
- Status: `Up to date`, `Updating`, `Changes need review`, `Broken links`, or `Migration required`.
- A count and `Review Changes` action.
- `Update now` and `Reconcile links` actions.
- `Rebuild missing outputs` and `Detach selected` actions.

The review panel should group changes by assembly, component, and part, show source/target selections in Rhino, and preview the consequence of a structural decision. Destructive choices should state which outputs will be replaced or removed before commit.

For phase one, useful commands/actions are:

- `Enable Live Links`
- `Pause/Resume Live Links`
- `Update Linked Assembly`
- `Reconcile Linked Assembly`
- `Review Linked Changes`

Later phases add `Add Part to Component`, `Relink Source`, generalized `Promote Derived Geometry` for copied/flat or otherwise ambiguous cases, and structural split/join resolution. The narrow, validated `OriginalAssembly` inverse-promotion path does not wait for that generalized UI.

## Phased implementation

### Phase 0: event and transform spike

- Add pure transform-composition tests.
- Add an event-sequence reducer test harness.
- Verify Rhino 8.0 callback sequences for Move, Rotate, Gumball, grip edit, Replace, Split, Join, Copy, Delete, Undo, and Redo.
- Verify document-string and custom metadata undo behavior.
- Measure `DataCRC`, graph serialization, and replacement performance on representative large assemblies.

Exit condition: event sequences and undo behavior are documented with automated reducer fixtures.

### Phase 1: stable one-to-one source links

- Add version 2 node/edge/conflict records and migration.
- Add stable node recovery tags.
- Add per-document sessions, lifecycle subscriptions, event capture, reducer, debounce, and mutation scopes.
- Auto-propagate valid one-to-one source changes to `ORIGINAL ASSEMBLIES` and inverse-promote safe one-to-one `OriginalAssembly` direct-copy edits back into their design sources.
- Capture permitted whole-object layout transforms on linked derived nodes.
- Add pause/status/reconcile UI and action history.
- Persist unresolved structural events without changing descendants.

Exit condition: source-to-original updates and safe original-to-source inverse promotions survive edit, move, undo/redo, save/reopen, and object replacement without event loops or identity loss.

### Phase 2: copied components and flat parts

- Refactor component and flat creation helpers to return exact accumulated transforms.
- Register copied-component and flat nodes/edges as they are generated.
- Preserve copied/flat layout when source geometry changes.
- Implement procedural flat recipes and stable anchors.
- Track generated labels as dependent/stale auxiliary outputs.
- Provide a one-time rebuild workflow for migrated assemblies.

Exit condition: a supported source edit or safely inverse-promoted original edit updates all BREP stages while preserving approved component and flat-part placement.

### Phase 3: explicit structural editing

- Implement `Add Part to Component`, remove/reassign part, relink source, detach, and generalized promotion workflows for copied/flat and ambiguous derived edits.
- Extend the implemented stable part/component split and merge rules to structural add/remove/reassign workflows and block leaves.
- Extend the implemented quantity, component-definition, layer/reference, and BOM/nesting-cache reconciliation to structural transactions and automatic representative-output creation.
- Add a full conflict review panel.

Exit condition: operators can intentionally evolve assembly composition without rebuilding the whole assembly.

### Phase 4: inferred structural changes and advanced sources

- Rank split/join/add/group-change candidates from command batches.
- Add opt-in high-confidence auto-accept policies only after field testing.
- Implement block-definition leaf remapping and linked-block/worksession policies.
- Extend dependencies to linework, dimensions, labels, and other fabrication artifacts.

Exit condition: common Rhino structural-edit workflows are detected accurately and resolved without metadata loss.

## Test matrix

Pure/unit tests should cover:

- Matrix composition, inversion, translation, rotation about a point, mirror, and non-uniform scale.
- Source edit versus derived layout-transform semantics.
- Branching DAG propagation and cycle rejection.
- Replace/delete/add and undo/delete/undelete event reduction.
- `ObjectsWillBeCopied` and duplicate-node-tag repair.
- Stable part and component numbering during split/merge recategorization.
- Version 1 migration with missing objects and duplicate legacy reference IDs.
- JSON round-trip and graph revision conflicts.

Rhino integration tests should cover:

- Move, rotate, scale, gumball, and grip edits on source and derived objects.
- Boolean, trim, split, join, delete, copy/paste, group, ungroup, undo, and redo.
- Two assemblies sharing one source object.
- Save, close, reopen, and Save As.
- Model-unit scaling and tolerance changes.
- Locked/hidden layers and deliberately missing targets.
- Nested blocks and definition edits once that phase is enabled.
- Assemblies with hundreds or thousands of linked BREPs.

## Recommended implementation boundaries

Keeping event capture, graph evaluation, Rhino mutation, and UI separate will make the system testable:

- `Core/LinkingModels.cs`: persistence records and enums only.
- `Services/AssemblyLinkRepository.cs`: graph load/save/migration and revision checks.
- `Services/AssemblyLinkSessionManager.cs`: plugin/document lifecycle and subscriptions.
- `Services/LinkEventReducer.cs`: pure event-stream reduction.
- `Services/LinkPropagationPlanner.cs`: pure DAG planning and conflict classification.
- `Services/LinkPropagationExecutor.cs`: validated Rhino mutations and undo scope.
- `Services/LinkIdentityService.cs`: node tags, object-ID reconciliation, and collision repair.
- `Services/LinkCategorizationService.cs`: occurrence/definition split and merge rules.
- `UI/LinkedChangesDialog.cs`: review and resolution UI.

`AssemblyGenerationService`, `ComponentDrawingService`, and `LayPartsFlatService` should call a shared link-registration API. They should not each implement their own persistence or event suppression.

## Rhino API references

- [RhinoDoc.ReplaceRhinoObject](https://developer.rhino3d.com/api/RhinoCommon/html/E_Rhino_RhinoDoc_ReplaceRhinoObject.htm) documents the replace/delete/add and undo delete/undelete event sequences.
- [RhinoDoc.BeforeTransformObjects](https://developer.rhino3d.com/api/rhinocommon/rhino.rhinodoc/beforetransformobjects) provides the transform before it is applied.
- [RhinoDoc.AfterTransformObjects](https://developer.rhino3d.com/api/rhinocommon/rhino.rhinodoc/aftertransformobjects) is available only in later Rhino 8 service releases.
- [RhinoDoc.UnitsChangedWithScaling](https://developer.rhino3d.com/api/rhinocommon/rhino.rhinodoc/unitschangedwithscaling) is raised before Rhino scales document objects.
- [Transform.Multiply](https://developer.rhino3d.com/api/rhinocommon/rhino.geometry.transform/multiply) defines the `A * B` application order.
- [TransformCommand.TransformObjects](https://developer.rhino3d.com/api/rhinocommon/rhino.commands.transformcommand/transformobjects) is the command helper for programmatic transforms that participate in Rhino's transform event path.
- [RhinoCommon event watchers](https://developer.rhino3d.com/en/guides/rhinocommon/event-watchers/) documents event-handler UI-thread constraints.
- [Command.EndCommand](https://developer.rhino3d.com/api/rhinocommon/rhino.commands.command/endcommand) and [RhinoApp.Idle](https://developer.rhino3d.com/api/rhinocommon/rhino.rhinoapp/idle) provide safe batching boundaries.
- [GeometryBase.DataCRC](https://developer.rhino3d.com/api/rhinocommon/rhino.geometry.geometrybase/datacrc) provides a quick exact-data change indicator.
- [HistoryRecord](https://developer.rhino3d.com/api/RhinoCommon/html/T_Rhino_DocObjects_HistoryRecord.htm) and [Command.ReplayHistory](https://developer.rhino3d.com/api/rhinocommon/rhino.commands.command/replayhistory) are useful reference designs, but Gazelle still needs its own graph for category, group, layout, conflict, and multi-stage metadata.
