# Gazelle Assembly Manager Walkthrough

This guide covers the intended Assembly Manager workflow inside Gazelle. The short version is this: prep clean grouped geometry, assign materials, mark known hardware, create the assembly, then use Gazelle's generated geometry for CAM, drawings, estimates, and BOM output.

## 1. Prep The Model

Gazelle works best when the Rhino model is structured around physical components.

Recommended setup:

- One physical component equals one Rhino group.
- Each group contains the parts and hardware that belong to that component.
- Source geometry can live on your normal modeling layers.
- Keep the model reasonably clean before running assembly creation.
- Avoid mixing unrelated components in one Rhino group.

During assembly creation, Gazelle expands selected objects to the full Rhino group. That means you do not need to select every object in every component. You can select one object from the group and Gazelle will collect the rest.

## 2. Use Geometry Gazelle Can Trust

Manufactured parts should be:

- Closed polysurfaces.
- Extrusions that can be converted to closed Breps.

Geometry that is not treated as a sheet/solid part:

- Curves.
- Points.
- Point clouds.
- Single surfaces.
- Open polysurfaces.

Open polysurfaces are skipped with warnings. This is intentional. If an object is open, the volume, thickness, and edge information can be unreliable, and that can poison the part count.

Unmarked Rhino blocks are allowed. Gazelle expands the block instance, reads the block definition geometry, and categorizes the closed polysurfaces/extrusions inside it. This keeps normal user-created blocks usable.

## 3. Assign Materials Before Creating The Assembly

The cleanest workflow is to assign materials before creating the assembly.

Run `AssignMaterials`, select objects, then choose a parent material from the library. Gazelle stores the material on the object attributes. It does not ask for sheet size at this point.

Why parent material only:

- A part might fit on more than one sheet size.
- Sheet size should be chosen by the estimator based on part footprint and available stock.
- The same material can have several purchasable shapes.

Example:

- Assign `MDF` to the source objects.
- Later the estimator decides whether those parts fit on `3/4 sheet 48x96`, `3/4 sheet 48x120`, or another stock shape under `MDF`.

Material assignment is part of categorization. Two pieces of identical geometry with different assigned parent materials become different part records.

## 4. Carry Hardware Through The System

Use `ImportHardware` for STEP hardware that you already know and do not want Gazelle to analyze as a sheet part.

The command imports the STEP file, creates a block, places the block on a `HARDWARE` layer, and marks the block definition geometry and placed instance as hardware.

After import:

- You can copy the hardware block instance as needed.
- Put copied hardware instances into the same Rhino groups as the components they belong to.
- When you create the assembly, Gazelle carries the hardware into `ASSEMBLY MANAGER::ORIGINAL ASSEMBLIES`.
- Hardware contributes to component identity.
- Hardware gets counted in the BOM.

Important distinction:

- Marked hardware blocks pass through.
- Unmarked blocks are analyzed as normal part geometry.

That distinction is what lets you use blocks for both imported hardware and normal model organization.

## 5. Create The Assembly

Open `AssemblyManager`.

In the left panel:

- Enter a new assembly name.
- Confirm the part prefix.
- Confirm the component prefix.
- Click Create Assembly.

Gazelle will ask you to select component groups in the model. The selection is group-aware. Pick the groups or objects in the groups that should become the assembly.

Gazelle then:

- Creates `ASSEMBLY MANAGER::ORIGINAL ASSEMBLIES::<assembly>`.
- Creates generated component layers.
- Categorizes equivalent parts.
- Categorizes equivalent components.
- Copies generated part and hardware geometry to `ASSEMBLY MANAGER::ORIGINAL ASSEMBLIES`.
- Saves assembly metadata in the Rhino document.
- Displays warnings for unsupported geometry.

## 6. Understand The Generated Layers

Gazelle creates one parent layer with three managed output branches:

- `ASSEMBLY MANAGER::ORIGINAL ASSEMBLIES`: generated assembly geometry.
- `ASSEMBLY MANAGER::COPIED COMPONENTS`: drawing-oriented component copies.
- `ASSEMBLY MANAGER::PARTS`: flat part output.

It also creates the top-level `HARDWARE` tree for imported hardware blocks and `ANNO` for annotations and generated tables.

The `ORIGINAL ASSEMBLIES` branch is managed, linked manufacturing geometry. It is not a drawing-only playground, but safe one-to-one edits to its closed BREPs are supported: Gazelle maps the edit back to the design source and rebuilds every linked original, copied component, and flat part. You can also edit the source model directly. Split/join operations, block leaves, unsupported geometry, and ambiguous simultaneous edits are preserved for review instead of being guessed; `RefreshAssemblyReferences` remains the manual rebuild fallback.

Part and component numbers are reconciled after a supported update. For example, if two occurrences are `P01` and only one is changed beyond the categorization tolerances, the unchanged occurrence remains `P01` and the edited occurrence receives the next unused part number, such as `P02`. Its linked descendants, layer paths, quantities, component definition, legacy references, and manufacturing-report caches are updated with it.

If this assembly already has linked flat parts, Gazelle also updates their transformations, quantities, material/thickness layers, and owned labels. Existing representatives keep their identities and accepted placement; new categories receive additional flat representatives, and safely managed superseded representatives are removed when no other output depends on them. User-created text and unsafe output are preserved. Use **Lay Parts Flat** for the first layout, and review reported copied-component gaps before running **Copy / Orient Components** again.

With **Automatically propagate changes in assembly** enabled in Settings (the default), this happens after a supported input-geometry or assigned-material edit; no button click is required. For ten `P01` occurrences, editing one produces nine `P01` occurrences and one newly numbered occurrence on its own managed part layer. Rhino's command history reports the quantity changes. An unresolved source in another part category no longer blocks this update; only the category with incomplete evidence is held for review.

For larger models, turn that setting off while making several edits, then select the assembly and click **Update Assembly** (formerly **Refresh References**). Gazelle continues tracking object identities and placement transforms without rebuilding generated geometry or recategorizing on each edit. Pending supported edits survive saving and reopening the model. The existing `RefreshAssemblyReferences` command performs the same update, and switching automatic propagation back on resumes pending work. Avoid editing both an input source and its generated original before updating; Gazelle preserves such competing edits as link issues rather than choosing one automatically.

Established categories remain stable while changed occurrences are evaluated. Small tolerance differences among unchanged `P01` occurrences do not trigger a fresh regrouping of those parts or block the edited occurrence's new number. A changed occurrence that genuinely fits competing categories still needs review.

With part coloring enabled, a newly numbered part gets a layer color different from the category it left. If the edit instead matches an existing part, its managed output layers use that part's established color, including custom colors. The palette can repeat across an assembly, so use part numbers to identify categories. Source colors and individual object color overrides stay unchanged.

An old part layer under an original or copied component is removed when that component no longer needs it and the layer is completely empty. Layers with objects (including hidden or locked objects), child layers, or unresolved linked membership are preserved, as are current and reference layers. **Update Assembly** also checks known empty leftovers from earlier edits. This cleanup does not delete flat-part trees or annotations.

During automatic and manual updates, Rhino's command line reports the current stage: linked geometry, part recategorization and quantities/materials, then existing flat PARTS geometry, placements, and labels when applicable. A final message reports elapsed update time, linked objects refreshed, new part categories, and the document-wide open link issue count. Messages are grouped by assembly and update stage, not printed for every object. An update that stops unexpectedly reports that it did not finish.

When Rhino reports link review items, open **Assembly Manager** and select the assembly. The **Link Issues** section at the bottom of the right-hand panel, below **Workflow**, shows its open issue count, the document-wide total, and each recorded reason with the affected part/component and object details when available. Undo/redo health warnings appear separately. The display updates automatically while the window is open. **Refresh Issues** reloads the issues and checks link health without changing geometry or clearing conflicts; **Update Assembly** is the geometry rebuild action.

New output always uses this hierarchy. Gazelle does not silently rename legacy `SHOP`, `DRAWINGS`, or `CAM` trees in an existing Rhino document.

## 7. Copy And Orient Components For Drawings

Use `CopyOrientComponents` when you need geometry for shop drawing setup.

This command copies one representative of each component type to `ASSEMBLY MANAGER::COPIED COMPONENTS::<assembly>`, lays those component copies out in rows, optimizes the plan rotation, and groups each drawing copy.

This is the geometry you can move, rotate, isolate, and edit for drawing presentation.

Good to edit:

- The position and rotation of `COPIED COMPONENTS` copies created by `CopyOrientComponents`.
- Closed BREP geometry under `ORIGINAL ASSEMBLIES` when you intend that change to become the revised design source.
- Layout annotations.
- Detail labels and leaders.

Do not manually reshape as production source:

- `COPIED COMPONENTS` or laid-flat `PARTS` geometry; those branches are quarantined for review if reshaped directly.
- Split, joined, open, or block-leaf `ORIGINAL ASSEMBLIES` geometry; these structural cases are not promoted automatically.
- Generated assembly metadata.
- Generated layer paths that Gazelle expects to own.

## 8. Lay Parts Flat

Run `LayPartsFlat` from the Assembly Manager window or command line.

Gazelle lays one representative of each unique part onto `ASSEMBLY MANAGER::PARTS::<assembly>`. It does not lay hardware flat.

What happens:

- The largest face is oriented to World XY.
- The long dimension is rotated into the Y direction.
- Parts are grouped into rows by material and thickness.
- Parts are placed left to right with the configured spacing.
- Each row gets a material/thickness header.
- Each part gets a label with part number, quantity, thickness, and material.

The labels are black, use text height `0.125`, and use model-space scale `12` so they read correctly in paperspace.

## 9. Use The Material Library

Open `MaterialLibrary`.

The library has two levels:

- Parent material: the general material, such as `MDF`, `Plywood`, `Steel`, or `Acrylic`.
- Stock shape: a purchasable form of that material, such as a sheet, plate, tube, pipe, or bar.

The material estimator uses sheet-like stock shapes for sheet count estimates. Sheet-like means the shape type contains `sheet`, `plate`, or `panel`.

For sheet stock, actual `Width` and `Height` matter. If actual width and height are supplied, Gazelle uses those values for fitting and estimating. If they are blank, it falls back to `SheetWidth` and `SheetHeight`.

The library can be edited manually, imported from JSON/CSV, or exported for database/spreadsheet work.

## 10. Estimate Materials

Run `EstimateMaterials` after an assembly exists and materials have been assigned.

Gazelle checks each unique part and tries to resolve it to a stock shape:

1. Read the generated part.
2. Compute footprint dimensions.
3. Determine material thickness.
4. Find sheet-like stock under the assigned parent material.
5. Match stock thickness within `0.01`.
6. Choose the smallest sheet that fits.
7. If it does not fit, check a reoriented footprint.
8. Group all matching parts by material stock shape.
9. Estimate sheet count from total footprint area divided by usable sheet area.

Usable sheet area is:

```text
sheet width x sheet height x nesting efficiency
```

The estimate is intentionally conservative and quick, but it is not a true nesting engine. It does not know exact part outlines, grain direction, machining strategy, offcuts, tabs, or the exact efficiency of a real nest. It uses rectangular footprints, so it can overestimate for parts that nest tightly and underestimate if your real process has constraints not represented in the library.

The unaccounted list is important. It tells you when a part has no material, no matching thickness, no available sheet size, missing geometry, or a footprint that does not fit available stock.

## 11. Place Or Export A Material Estimate

Use `PlaceMaterialEstimate` from layout space to place a table on the page.

Use `ExportMaterialEstimate` to export CSV or JSON. CSV is good for spreadsheet review. JSON is better for automation because it preserves the full report structure.

## 12. Generate And Export A BOM

Run `GenerateBom` to build BOM rows from:

- The current material estimate.
- Hardware carried through the assembly.

Run `ExportBom` to write the BOM CSV.

Material rows come from estimated sheet/stock counts. Hardware rows come from marked hardware block instances in the assembly. Hardware rows are grouped by block name, description, material id, and source path, with quantities summed.

## 13. Label And Dimension Drawings

### Label components and parts in a detail

Use `LabelDetail` when you want quick text-dot labels for many visible objects in one detail.

- `Assembly` labels visible component groups under `ASSEMBLY MANAGER::ORIGINAL ASSEMBLIES`.
- `Component` labels visible part layers under `ASSEMBLY MANAGER::COPIED COMPONENTS`.

### Add leader labels through a detail

Use `LabelPart` from layout space when you want a leader that behaves like Rhino's leader tool but auto-fills the text.

Workflow:

1. Run `LabelPart`.
2. Click the leader tip on top of an object visible through a detail.
3. Place the rest of the leader points.
4. Gazelle fills the leader text with the leaf layer name of the object under the leader tip.

Use `LabelParts` when you want the same workflow repeated with only two points per label.

### Dimension objects in a detail

Use `DimDetail` from layout space. Select the detail, and Gazelle dimensions visible polysurfaces/extrusions by projecting World XY bounding boxes into page space. Horizontal dimensions are placed above the object, vertical dimensions are placed on the side with more room, and dimension values are left to Rhino.

## 14. Project Information And Layout Templates

Use `AssemblyManagerSettings` to save a layout template path.

Use `NewLayout` to import that saved template.

Use `SetProjectInfo` to fill the project fields used by your template. Gazelle writes those values as document string keys like `REVISION`, `PROJECT NAME`, and `CLIENT`.

## 15. Troubleshooting

If parts are missing:

- Check that the geometry is closed.
- Check that it is not just a single surface.
- Check command history for skipped-object warnings.

If part categories are wrong:

- Open `AssemblyManagerSettings`.
- Turn on categorization debug output.
- Recreate the assembly.
- Inspect the JSON report and compare the payloads for parts that should match or should differ.

If material estimates have unaccounted parts:

- Check whether the source objects were assigned a parent material.
- Check whether that material has sheet-like stock shapes.
- Check whether stock thickness matches the part thickness within `0.01`.
- Check whether the required footprint fits any available sheet size.

If hardware is being analyzed as a part:

- Make sure it was imported with `ImportHardware`, or otherwise has Gazelle hardware metadata.
- Ordinary Rhino blocks are intentionally analyzed unless they are marked hardware.
