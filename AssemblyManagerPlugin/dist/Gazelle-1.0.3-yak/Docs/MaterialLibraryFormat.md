# Gazelle Material Library Schema

Gazelle stores the shared material library as parent material records with one or more purchasable stock shapes under each material.

The JSON format is the preferred format for import/export because different stock shapes need different fields. CSV is also supported for spreadsheet editing and database exports.

## How Import Works

Import is a merge/update operation.

- A material with a matching `Id` updates the existing material.
- If no id matches, a material with a matching normalized `Name` updates the existing material.
- A stock shape with a matching `Id` updates the existing shape under that material.
- If no shape id matches, a matching normalized `Name` updates the existing shape.
- New materials and shapes are added.

This lets a database export refresh dimensions, densities, prices, and sheet sizes without creating duplicates.

## How Export Works

Use the Export button in `MaterialLibrary`, or run `ExportMaterialLibrary`.

- `.json` exports the hierarchical JSON structure shown below.
- `.csv` exports one row per stock shape.

## JSON Structure

Gazelle accepts either:

- A root object with a `Materials` property.
- A root array of material records.

Property names are case-insensitive on import. Export writes a root `Materials` property.

```json
{
  "Materials": [
    {
      "Id": "MDF",
      "Name": "MDF",
      "Category": "composite",
      "Description": "Medium density fiberboard sheet stock.",
      "DensityLbPerCubicInch": 0.026,
      "Properties": {
        "vendor": "Example Vendor"
      },
      "Shapes": [
        {
          "Id": "MDF_075_49x97",
          "Name": "3/4 sheet 49x97 actual",
          "ShapeType": "sheetgood",
          "Thickness": 0.75,
          "Unit": "in",
          "SheetWidth": 48,
          "SheetHeight": 96,
          "Width": 49,
          "Height": 97,
          "NestingEfficiency": 0.82,
          "PricePerUnit": 57.0,
          "PriceUnit": "sheet",
          "Properties": {
            "sku": "MDF-075-49x97"
          }
        }
      ]
    }
  ]
}
```

## Material Fields

| Field | Type | Required | Notes |
| --- | --- | --- | --- |
| `Id` | string | Recommended | Stable material id. If blank, Gazelle generates one from `Name`. |
| `Name` | string | Yes | Parent material name, for example `MDF`, `Steel`, `Acrylic`, or `Plywood`. |
| `Category` | string | No | Broad family, such as `wood`, `composite`, `metal`, `plastic`, `hardware`, or `other`. |
| `Description` | string | No | Human-readable notes. |
| `DensityLbPerCubicInch` | number | No | Parent material density in pounds per cubic inch. This is stored now for future weight/BOM workflows. |
| `Properties` | object | No | Extra string key/value metadata for database ids, vendor ids, finish, grade, etc. |
| `Shapes` | array | Yes | Purchasable stock shapes under this material. |

## Stock Shape Fields

| Field | Type | Required | Notes |
| --- | --- | --- | --- |
| `Id` | string | Recommended | Stable stock-shape id. If blank, Gazelle generates one from material id and shape name. |
| `Name` | string | Recommended | Shape display name, such as `3/4 sheet 48x96` or `2x2 tube 120`. |
| `ShapeType` | string | Yes | Examples: `sheetgood`, `plate`, `panel`, `round stock`, `square stock`, `tube`, `pipe`, `hardware`, `other`. |
| `Thickness` | number | Shape-specific | Sheet or plate thickness in `Unit`. |
| `Unit` | string | Recommended | Usually `in`. Defaults to `in` when blank. |
| `SheetWidth` | number | Sheet-like shapes | Nominal sheet width. |
| `SheetHeight` | number | Sheet-like shapes | Nominal sheet height. |
| `Width` | number | Shape-specific | Actual usable width. For sheet-like shapes, this overrides `SheetWidth` for fitting and estimates. |
| `Height` | number | Shape-specific | Actual usable height. For sheet-like shapes, this overrides `SheetHeight` for fitting and estimates. |
| `StockLength` | number | Linear stock | Purchasable length for tube, pipe, bar, etc. |
| `Diameter` | number | Round stock/pipe | Outside diameter. |
| `WallThickness` | number | Tube/pipe | Wall thickness. |
| `NestingEfficiency` | number | Sheet-like shapes | Decimal from 0 to 1. Defaults to `0.8` when blank or invalid. |
| `PricePerUnit` | number | No | Cost for one `PriceUnit`. |
| `PriceUnit` | string | No | Pricing basis, such as `sheet`, `length`, `linear_ft`, or `each`. |
| `Properties` | object | No | Extra string key/value metadata. |

## Sheet Size Rules

For sheet-like stock, Gazelle treats `Width` and `Height` as actual usable stock dimensions. If `Width` and `Height` are blank, Gazelle mirrors `SheetWidth` and `SheetHeight` into those fields.

This matters because some materials come oversized and others come exact size. The material estimate uses the actual dimensions when deciding whether a part fits on a sheet.

Gazelle currently treats these shape types as sheet-like:

- Any shape type containing `sheet`.
- Any shape type containing `plate`.
- Any shape type containing `panel`.

## CSV Material Library Format

CSV import/export is row-based. Each row represents one stock shape. Rows with the same `material_id` merge under the same parent material.

Recommended headers:

```csv
material_id,material_name,material_category,description,density_lb_per_cubic_inch,shape_id,shape_name,shape_type,thickness,unit,sheet_width,sheet_height,sheetsize,stock_length,width,height,diameter,wall_thickness,nesting_efficiency,price_per_unit,price_unit
```

Example:

```csv
material_id,material_name,material_category,description,density_lb_per_cubic_inch,shape_id,shape_name,shape_type,thickness,unit,sheet_width,sheet_height,sheetsize,stock_length,width,height,diameter,wall_thickness,nesting_efficiency,price_per_unit,price_unit
MDF,MDF,composite,Medium density fiberboard,0.026,MDF_075_49x97,3/4 sheet 49x97 actual,sheetgood,0.75,in,48,96,48x96,,49,97,,,0.82,57.00,sheet
STEEL,Steel,metal,Mild steel stock,0.283,STEEL_TUBE_2x2x120,2x2 tube 120,tube,,in,,,,120,2,2,,0.125,0.8,64.00,length
```

Accepted CSV aliases include:

| Canonical field | Accepted aliases |
| --- | --- |
| `material_id` | `base_material_id` |
| `material_name` | `material`, `base_material`, `base`, legacy `name` |
| `description` | `material_description`, `notes` |
| `density_lb_per_cubic_inch` | `density`, `density_lb_cuin`, `density_lb_in_cubed` |
| `shape_id` | `stock_id`, legacy `id` |
| `shape_name` | `shape`, `stock_name` |
| `shape_type` | `stock_type`, legacy `category` |
| `sheetsize` | `sheet` |
| `sheet_width`, `sheet_height` | `sheetwidth`, `sheetheight` |
| `stock_length` | `length` |
| `width` | `actual_width`, `usable_width`, `stock_width` |
| `height` | `actual_height`, `usable_height`, `stock_height` |
| `diameter` | `od` |
| `wall_thickness` | `wall` |
| `price_per_unit` | `unit_price`, `price`, `cost`, `cost_per_unit` |
| `price_unit` | `pricing_unit`, `cost_unit` |

## Object Assignment Data

`AssignMaterials` stores parent material data directly on Rhino object attributes.

| User string key | Value |
| --- | --- |
| `AssemblyManager.MaterialId` | Assigned parent material id. |
| `AssemblyManager.MaterialName` | Parent material name. |
| `AssemblyManager.MaterialBaseId` | Parent material id. |
| `AssemblyManager.MaterialBaseName` | Parent material name. |
| `AssemblyManager.MaterialShapeName` | Blank for parent-material assignments. |
| `AssemblyManager.MaterialShapeType` | Blank for parent-material assignments. |

During assembly creation, these assignments are copied to generated geometry and included in part categorization. That means identical geometry with different parent materials is treated as different parts.
