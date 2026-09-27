# Gazelle Material Library Schema

Gazelle stores the shared material library as parent material records with one or more purchasable stock shapes under each material.

The library lives in persistent Gazelle plugin settings and is shared across Rhino models. Assembly/object assignments live in each model. A material can be saved before it has shapes, but estimating requires suitable stock under it. Library changes do not rewrite previously exported files or placed tables.

The JSON format is the preferred format for import/export because different stock shapes need different fields. CSV is also supported for spreadsheet editing and database exports.

## How Import Works

Import is a merge/update operation.

- A material with a matching `Id` updates the existing material.
- If no id matches, a material with a matching normalized `Name` updates the existing material.
- A stock shape with a matching `Id` updates the existing shape under that material.
- If no shape id matches, a matching normalized `Name` updates the existing shape.
- New materials and shapes are added.

This lets a database export refresh dimensions, densities, prices, and sheet sizes without creating duplicates.

IDs compare case-insensitively; name matching trims and collapses spaces and ignores case. The existing matching material/shape ID is retained. A matching material's descriptive fields and `Properties` are replaced by the import, and a matching shape is replaced with the normalized imported record. Blank/default fields can therefore clear previous values; this is not a patch-only import. Materials/shapes omitted from the file are retained, not deleted. Export a JSON backup before bulk imports.

## How Export Works

Use the Export button in `MaterialLibrary`, or run `ExportMaterialLibrary`.

- `.json` exports the hierarchical JSON structure shown below.
- `.csv` exports one row per stock shape.

## JSON Structure

Gazelle accepts either:

- A root object with a `Materials` property.
- A root array of material records.

Property names are case-insensitive on import. Export writes a root `Materials` property.

An older flat array of stock records is also accepted for migration. Do not mix flat stock records and parent-with-`Shapes` records in one array: the first object determines the parser used for the entire file. Use numeric JSON values rather than quoted numeric strings, and use empty objects/arrays rather than `null` for `Properties` and `Shapes`.

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
| `Name` | string | Recommended | Parent material name, for example `MDF`, `Steel`, `Acrylic`, or `Plywood`. Blank names fall back to the id. |
| `Category` | string | No | Broad family, such as `wood`, `composite`, `metal`, `plastic`, `hardware`, or `other`. |
| `Description` | string | No | Human-readable notes. |
| `DensityLbPerCubicInch` | number | No | Parent material density in pounds per cubic inch. This is stored now for future weight/BOM workflows. |
| `Properties` | object | No | Extra string key/value metadata for database ids, vendor ids, finish, grade, etc. |
| `Shapes` | array | Include for hierarchical format | Purchasable stock shapes under this material; may be empty when defining a parent before adding stock. |

## Stock Shape Fields

| Field | Type | Required | Notes |
| --- | --- | --- | --- |
| `Id` | string | Recommended | Stable stock-shape id. If blank, Gazelle generates one from material id and shape name. |
| `Name` | string | Recommended | Shape display name, such as `3/4 sheet 48x96` or `2x2 tube 120`. |
| `ShapeType` | string | Recommended | Defaults to `sheetgood` when blank. Examples: `sheetgood`, `plate`, `panel`, `round stock`, `square stock`, `tube`, `pipe`, `hardware`, `other`. |
| `Thickness` | number | Shape-specific | Sheet or plate thickness in `Unit`. |
| `Unit` | string | Recommended | Usually `in`. Defaults to `in` when blank. |
| `SheetWidth` | number | Sheet-like shapes | Sheet width; normalized to `Width` when both actual dimensions are supplied. |
| `SheetHeight` | number | Sheet-like shapes | Sheet height; normalized to `Height` when both actual dimensions are supplied. |
| `Width` | number | Shape-specific | Actual usable width. For sheet-like shapes, this overrides `SheetWidth` for fitting and estimates. |
| `Height` | number | Shape-specific | Actual usable height. For sheet-like shapes, this overrides `SheetHeight` for fitting and estimates. |
| `StockLength` | number | Linear stock | Purchasable length for tube, pipe, bar, etc. |
| `Diameter` | number | Round stock/pipe | Outside diameter. |
| `WallThickness` | number | Tube/pipe | Wall thickness. |
| `NestingEfficiency` | number | Sheet-like shapes | Decimal greater than 0 and at most 1. Defaults to `0.8` when omitted, nonpositive, or greater than 1. |
| `PricePerUnit` | number | No | Cost for one `PriceUnit`. |
| `PriceUnit` | string | No | Pricing basis, such as `sheet`, `length`, `linear_ft`, or `each`. |
| `Properties` | object | No | Extra string key/value metadata. |

## Sheet Size Rules

For sheet-like stock, Gazelle treats `Width` and `Height` as actual usable stock dimensions. If `Width` and `Height` are blank, Gazelle mirrors `SheetWidth` and `SheetHeight` into those fields.

When both actual dimensions are positive, normalization also overwrites `SheetWidth` and `SheetHeight` with those actual dimensions. The library therefore does **not** preserve a separate nominal 48×96 and actual 49×97 pair in those numeric fields; retain nominal information in the name or JSON `Properties` if needed. Missing/nonpositive sheet dimensions fall back to numeric `48` and `96`. Always enter real stock sizes deliberately.

This matters because some materials come oversized and others come exact size. The material estimate uses the actual dimensions when deciding whether a part fits on a sheet.

Gazelle currently treats these shape types as sheet-like:

- Any shape type containing `sheet`.
- Any shape type containing `plate`.
- Any shape type containing `panel`.

## Units, Prices, And Current Scope

The `Unit` field is stored and displayed but does not trigger model-to-stock conversion. Geometry is measured in the Rhino model's numeric units. Match all stock dimensions to those units before estimating; a millimeter model with inch-valued stock will not estimate correctly. Thickness matching uses a fixed numeric tolerance of `0.01`.

Tube, pipe, bar, and other shapes can be catalogued, but the current estimator only calculates sheet-like stock. It does not calculate stock-length purchases or material weight. Prices have no currency field and no pricing-unit conversion: estimated sheet cost is sheet quantity × `PricePerUnit`, regardless of the text in `PriceUnit`. Use a price per sheet for sheet estimates. Negative density/price values are normalized to zero.

## CSV Material Library Format

CSV import/export is row-based. Each row represents one stock shape. Rows with the same `material_id` merge under the same parent material.

Use a header row, decimal-point numeric values, and one physical line per stock row. Headers ignore case and punctuation. Quoted commas and doubled quotes are supported, but multiline quoted cells are **not** correctly supported by the line-based importer; remove embedded line breaks or use JSON. Blank numeric cells receive defaults, not a request to retain an existing value. When repeated rows supply different parent descriptions/names, the first row's parent fields are normally used; density may be filled by a later positive value.

CSV does not export/import custom `Properties`, does not export parent materials with no shapes, and formats numeric exports to at most three decimal places (zero/nonpositive values are blank). Use JSON for full-fidelity backup and higher-precision values. A valid `sheetsize` value overrides separate `sheet_width` / `sheet_height` values before actual-dimension normalization.

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
| `material_category` | `family` |
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

`AssignMaterialToPart` instead selects a stock record, assigns it to all saved design-source objects of that P-number, and lets normal propagation refresh output. Category comparison normalizes back to the parent ID, so two selected stock shapes under the same parent are not automatically distinct part materials. Stock size is resolved again at estimating time.

Supported manufacturing material edits propagate automatically when the global toggle is enabled, or through **Update Assembly** when it is off. Existing flat material text and quantity labels are updated as part of that process. This is distinct from changing a Rhino render material or display color. Hardware BOM material metadata currently needs manual verification after a source-material edit; not every hardware metadata change is synchronized into saved hardware records.

## Library Maintenance

The Material Library window has **New / Save / Delete Material**, **New / Save / Delete Shape**, **Import CSV / JSON**, **Export CSV / JSON**, and **Purge Library** controls. Save the edited record explicitly. Deleting a material deletes its shapes; purging clears the shared library. Neither operation removes material user strings already stored on Rhino objects. Those assignments may become unresolved until matching records are restored. Back up with JSON before deletion or purge.
