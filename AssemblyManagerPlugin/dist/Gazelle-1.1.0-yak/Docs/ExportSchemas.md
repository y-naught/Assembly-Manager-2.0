# Gazelle Export Schemas

This document covers material-estimate CSV/JSON and BOM CSV written by the current plugin. Material-library exchange files have a separate schema in [MaterialLibraryFormat.md](MaterialLibraryFormat.md).

## Before Exporting

Run **Update Assembly** and review its link issues before preparing reports, especially with automatic propagation off (the default for new/unset preferences) or component additions staged. `ExportMaterialEstimate`, `PlaceBOM` (also available as `PlaceMaterialEstimate`), `GenerateBom`, and `ExportBom` recalculate the material estimate, but do **not** run the linked update workflow first. They can report the previous managed state while edits remain pending. The master **Enable linked assemblies** switch must be on and any suspension issue resolved before updates can run; producing a report does not bypass that safety check.

Exports are snapshots and overwrite the chosen file; placed estimate tables are static page-space geometry. Later assembly/library changes do not revise either. Use revision filenames and deliberately replace old placed tables. CSV numbers use a decimal point and at most three decimal places; text containing commas, quotes, or line feeds is quoted, with embedded quotes doubled. There is no estimate/BOM report importer or BOM JSON export command.

## Placed Material And Hardware Table

The Assembly Manager button is **Place BOM** and the Rhino command is `PlaceBOM`. `PlaceMaterialEstimate` remains a compatibility alias for the same workflow. It places a static grouped **Bill of Materials — &lt;assembly&gt;** table on `ANNO::Material Estimates` in the selected layout. No CSV/JSON export schema is changed.

Activate the layout page itself, outside its detail, before running the command. Otherwise it stops before the assembly picker. Choose the assembly, choose columns in the checklist and press **Confirm**, then pick two opposite page-XY corners. The table fills that width and must fit within that height.

The selectable columns are:

| Column | Contents and units |
| --- | --- |
| Item | Stock-shape name, hardware item/block name, or unaccounted part number. |
| Material | Resolved material label, with missing/unknown assignments indicated. |
| Description | Descriptive stock or hardware text where available. |
| Thickness | Stock or required part thickness, with its dimension-unit label. |
| Size | Stock sheet size or required part footprint, with its dimension-unit label. |
| Quantity | Estimated sheets, hardware occurrences, or unaccounted part quantity. |
| Unit | Quantity unit, such as `sheet` or `ea`; not the size/thickness unit. |
| Part Area | Rectangular part footprint area, in square model units where available. |
| Parts / Notes | Accounted part summaries or unaccounted reasons, where available. |
| Source | Source information, including hardware source paths when available. |

Each invocation defaults to every column except **Part Area** and **Source**; at least one must be selected. These choices change only the placed table, never the export headers below. Blank cells indicate a field that does not apply to that row, rather than an omitted quantity or hidden part.

Fitting first uses wrapped Arial body text at `0.125 in` paper height converted to page units. Text size is reduced only if the wrapped content will not fit vertically or the columns cannot fit individual characters; the chosen width stays fixed. Text is never enlarged beyond the standard height or truncated to fit. A rectangle that still cannot contain the table at `0.02 in` body-text height is rejected with guidance to enlarge it or use fewer columns. Unspecified/custom page units and invalid/tiny rectangles are rejected. This paper-unit conversion does not add stock/model-unit conversion to the material estimator.

The table has these sections:

- **Sheet Stock Estimate**: stock rows, with values shown for the chosen columns.
- **Hardware**, when present: hardware rows, with assembly quantities rather than drawing-view counts.
- **Unaccounted Objects**, when present: manufactured parts that could not be assigned to stock, including quantities and reasons when those columns are selected.

Hardware rows use the same item/description/material-ID/source-path grouping and quantity aggregation as BOM CSV. They do not count copied drawing instances as additional assembly hardware, and they do not contribute to sheet-stock area or counts. A hardware-only assembly can produce a table without sheet rows. Hardware materials come from saved hardware records and retain the caveat described below.

This combined table does **not** add hardware to `ExportMaterialEstimate` CSV or JSON. Those exports retain the material-only schemas below. Use `ExportBom` for hardware CSV data. Previously placed tables are not converted or updated automatically; place a fresh table and deliberately remove the obsolete one.

## Material Estimate CSV

Created by `ExportMaterialEstimate` when the destination extension is `.csv`.

The file has two sections:

- `materials`: stock-shape rows that Gazelle could account for.
- `unaccounted`: part rows that could not be matched to stock.

### Materials Section Header

```csv
section,material,shape,type,thickness,unit,sheet_width,sheet_height,quantity,total_part_area,nesting_efficiency,price_per_unit,price_unit,estimated_cost,parts
```

### Materials Section Fields

| Field | Meaning |
| --- | --- |
| `section` | Always `materials` for this section. |
| `material` | Parent material name, such as `MDF`. |
| `shape` | Stock shape name, such as `3/4 sheet 49x97 actual`. |
| `type` | Stock shape type, such as `sheetgood` or `plate`. |
| `thickness` | Stock shape thickness. |
| `unit` | Stock unit, usually `in`. |
| `sheet_width` | Actual sheet width used for estimating. |
| `sheet_height` | Actual sheet height used for estimating. |
| `quantity` | Estimated sheet count. |
| `total_part_area` | Sum of rectangular part footprints for this stock shape. |
| `nesting_efficiency` | Efficiency factor used to reduce usable sheet area. |
| `price_per_unit` | Price stored on the stock shape; exported as `0` when not set. |
| `price_unit` | Pricing unit stored on the stock shape. |
| `estimated_cost` | `quantity x price_per_unit` when positive; otherwise `0`. No currency or price-unit conversion. |
| `parts` | Semicolon-separated summary of part quantities. |

Example:

```csv
section,material,shape,type,thickness,unit,sheet_width,sheet_height,quantity,total_part_area,nesting_efficiency,price_per_unit,price_unit,estimated_cost,parts
materials,MDF,3/4 sheet 49x97 actual,sheetgood,0.75,in,49,97,2,6830.25,0.82,57,sheet,114,P01 x12; P02 x4
```

### Unaccounted Section Header

After a blank line, Gazelle writes this header:

```csv
section,part,quantity,material,required_width,required_height,required_thickness,reason
```

### Unaccounted Section Fields

| Field | Meaning |
| --- | --- |
| `section` | Always `unaccounted` for this section. |
| `part` | Generated part name. |
| `quantity` | Part quantity in the assembly. |
| `material` | Assigned material id/name when known, otherwise `TBD` or blank. |
| `required_width` | Required part footprint width. |
| `required_height` | Required part footprint height. |
| `required_thickness` | Detected part thickness. |
| `reason` | Why the part could not be assigned to stock. |

Example:

```csv
section,part,quantity,material,required_width,required_height,required_thickness,reason
unaccounted,P07,2,MDF,62,130,0.75,Part footprint 62 x 130 does not fit an available 0.75 thick sheet. Reoriented footprint checked: 62 x 130.
```

## How The Material Estimate Is Calculated

Gazelle does not do true polygon nesting yet.

The sheet count is:

```text
ceil(total rectangular part footprint area / (sheet width x sheet height x nesting efficiency))
```

That means the estimate is useful for early sheet counts and BOM planning, but it should still be checked against a real nest for production. It can be off when grain direction, voids, kerf, tabs, offcuts, part rotation rules, or real packing behavior matter.

For each part, Gazelle selects the smallest-area fitting sheet stock under the assigned parent material, with thickness within numeric `0.01`. It may test a reoriented footprint when the initial footprint does not fit. Assigned parts are then grouped by stock ID; positive-area stock groups have a minimum estimate of one sheet. The estimator is not a lowest-price stock optimizer and does not estimate linear stock or weight.

All comparisons use raw model and stock numbers. The stock `unit` is not a conversion instruction: use matching numeric units. `total_part_area` is in those units squared. `PriceUnit` is descriptive; enter a per-sheet price for sheet estimates. Missing geometry, missing material, missing matching thickness, or an oversized footprint appears in `unaccounted`, not as a successfully estimated row.

## Material Estimate JSON

Created by `ExportMaterialEstimate` for a `.json` destination. JSON uses camelCase property names, native numbers, GUID strings, and an ISO-8601 creation timestamp. Unlike CSV, it retains stock/material IDs and the individual per-part footprint records. Structure:

```json
{
  "id": "00000000-0000-0000-0000-000000000001",
  "createdAt": "2026-09-05T12:00:00+00:00",
  "assemblyName": "Cabinet",
  "lines": [],
  "unaccountedObjects": []
}
```

Each `lines` entry contains:

| Fields | Meaning |
| --- | --- |
| `materialId`, `materialName` | Resolved flattened stock record ID/display name. |
| `baseMaterialId`, `baseMaterialName` | Parent material identity/name. |
| `shapeId`, `shapeName`, `shapeType` | Stock shape identity, label, and type. |
| `thickness`, `unit`, `sheetWidth`, `sheetHeight` | Chosen stock thickness, unit label, and usable dimensions. |
| `nestingEfficiency`, `totalPartArea`, `estimatedSheetCount` | Inputs and result of the area estimate. |
| `pricePerUnit`, `priceUnit`, `estimatedCost` | Stored unit price/label and simple sheet-count cost; unset prices/costs are zero. |
| `parts` | Array of the per-part records described below. |

Each `parts` entry contains `partName`, `quantity`, `requiredWidth`, `requiredHeight`, `requiredThickness`, `areaEach`, and `materialId` (the resolved stock ID). `totalPartArea` sums `areaEach × quantity`.

Each `unaccountedObjects` entry contains `partName`, `quantity`, `materialId`, `materialName`, `requiredWidth`, `requiredHeight`, `requiredThickness`, and `reason`. Required dimensions may be zero when geometry could not be read. These entries must be reviewed; an empty `lines` array is not proof that the assembly needs no material.

## BOM CSV

Created by `ExportBom`.

Header:

```csv
category,item,description,quantity,unit,material_id,source
```

### BOM Fields

| Field | Meaning |
| --- | --- |
| `category` | `SheetGood` or `Hardware`. |
| `item` | Material/stock name for sheet goods, or block definition/name for hardware. |
| `description` | Sheet dimensions and efficiency for sheet goods, or hardware description for hardware. |
| `quantity` | Estimated sheet count or hardware count. |
| `unit` | `sheet` for sheet goods, `ea` for hardware. |
| `material_id` | Material or stock id when available. |
| `source` | `MaterialEstimate`, `NestingEstimate`, `Document`, or hardware source path. |

Example:

```csv
category,item,description,quantity,unit,material_id,source
SheetGood,MDF - 3/4 sheet 49x97 actual,49 x 97 x 0.75 in sheet at 82 % efficiency,2,sheet,MDF_075_49x97,MaterialEstimate
Hardware,McMaster_92196A542,Imported hardware from 92196A542.step,24,ea,,/Users/greg/Downloads/92196A542.step
```

## Where BOM Rows Come From

Sheet-good rows come from the latest material estimate. `GenerateBom` and `ExportBom` both regenerate the material estimate first.

Hardware rows come from hardware that was carried through assembly creation. Hardware is grouped by:

- Block name.
- Description.
- Material id.
- Source path.

Quantities are summed across the assembly.

Marked hardware added through **Update Component** appears after **Update Assembly** applies it. Unaccounted manufactured parts are **omitted** from the sheet-good BOM, so always review the estimate alongside the BOM. This report is a sheet-purchase/hardware summary, not a parts list with one row per P-number. Hardware material metadata is taken from saved hardware records and may be stale after direct source-material edits; verify hardware entries separately. There is no automatic currency conversion, tax, machining, labor, weight, or linear-stock cost calculation.
