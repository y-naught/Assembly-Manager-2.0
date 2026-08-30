# Gazelle Export Schemas

This document covers the CSV files Gazelle writes for material estimates and BOM exports.

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
| `price_per_unit` | Price stored on the stock shape. Blank when not set. |
| `price_unit` | Pricing unit stored on the stock shape. |
| `estimated_cost` | `quantity x price_per_unit` when price is set. |
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
