---
title: GridLayout
navTitle: GridLayout
position: 12
---

# GridLayout

A grid with a fixed number of rows and columns. Type name: `HomeBlaze.Components.GridLayout`. Slot blocks: `Cell`.

## Arguments

| Argument | Kind of value | Default | Meaning |
|----------|---------------|---------|---------|
| `Rows` | Whole number | `2` | The number of rows |
| `Columns` | Whole number | `2` | The number of columns |
| `CellAspectRatio` | Ratio `W:H` | None | The shape of a cell, for example `1:1` for squares. A row is as high as a column is wide times H/W, so the grid follows the width of the page. Without it the rows share the height of the grid, which is at least 400 pixels |

## Slots

| Slot | Blocks | Holds |
|------|--------|-------|
| `Cell` | Any number | One cell per block: any page content, which the cell shows |

The arguments of a `Cell` block:

| Argument | Kind of value | Default | Meaning |
|----------|---------------|---------|---------|
| `Row` | Whole number | Automatic | The row of the cell, counted from 0. Without it the grid places the cell itself |
| `Column` | Whole number | Automatic | The column of the cell, counted from 0. Without it the grid places the cell itself |
| `RowSpan` | Whole number | `1` | How many rows the cell covers |
| `ColumnSpan` | Whole number | `1` | How many columns the cell covers |

## Samples

A grid of two rows and two columns. The two cells of the first row show the two sample motors. The cell of the second row spans both columns and holds text with expressions:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.GridLayout#motorgrid Rows=2 Columns=2
::: Cell Row=0 Column=0
:::: HomeBlaze.Components.Widget Path=../Pump
::: Cell Row=0 Column=1
:::: HomeBlaze.Components.Widget Path=../Fan
::: Cell Row=1 Column=0 ColumnSpan=2
The pump is set to **{{ ../Pump/TargetSpeed }}** and the fan to **{{ ../Fan/TargetSpeed }}**.
::
~~~~

:: HomeBlaze.Components.GridLayout#motorgrid Rows=2 Columns=2
::: Cell Row=0 Column=0
:::: HomeBlaze.Components.Widget Path=../Pump
::: Cell Row=0 Column=1
:::: HomeBlaze.Components.Widget Path=../Fan
::: Cell Row=1 Column=0 ColumnSpan=2
The pump is set to **{{ ../Pump/TargetSpeed }}** and the fan to **{{ ../Fan/TargetSpeed }}**.
::

A grid with cells that keep their shape. `CellAspectRatio=1:1` makes each cell as high as it is wide, so the cells stay square at every width of the page:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.GridLayout#squaregrid Rows=1 Columns=3 CellAspectRatio=1:1
::: Cell Row=0 Column=0
Living room
::: Cell Row=0 Column=1
Kitchen
::: Cell Row=0 Column=2
Garden
::
~~~~

:: HomeBlaze.Components.GridLayout#squaregrid Rows=1 Columns=3 CellAspectRatio=1:1
::: Cell Row=0 Column=0
Living room
::: Cell Row=0 Column=1
Kitchen
::: Cell Row=0 Column=2
Garden
::

## Behavior and Limits

- **Equal rows and columns**: All columns have the same width. Without `CellAspectRatio` all rows have the same height and the grid is at least 400 pixels high.
- **Cell aspect ratio**: With `CellAspectRatio=W:H` a row is as high as a column is wide times H/W, and follows the width of the page. The cells have no minimum height then.
- **An invalid ratio**: A `CellAspectRatio` that is not two numbers above 0 with a colon between them shows `CellAspectRatio must look like 1:1` with the value in place of the grid, see [Value Widget Errors](../page-errors.md#value-widget-errors).
- **Tiles**: A tile ([Switch](switch.md) or [OperationButton](operation-button.md) with `Kind=Tile`) in a cell fills the cell, as a card does. Square cells suit tiles.
- **Cards**: A [Card](card.md) in a cell fills the cell and ignores its `AspectRatio`, unless it has a `Height` of its own. Its body scrolls when its content does not fit.
- **Fixed columns**: The grid keeps its columns at every width of the screen. A cell cuts off content that needs more room than the cell has, which a narrow window shows in the first row of the grid above. Use a `FlowLayout` where the cells should wrap.
- **Names**: Give a cell a `#name` to keep it and everything in it when cells are added or removed, see [What an Edit of the Page Keeps](../pages.md#what-an-edit-of-the-page-keeps).
- **Edit mode**: While the page is in edit mode, each cell has buttons to edit and remove it, which show while the grid or a widget in one of its cells is selected. A click on a cell without a widget, or on a free place, selects the grid, and once the grid itself is selected, a click on the plus in a free place adds a cell there, see [Editing in the Page Editor](../pages.md#editing-in-the-page-editor).

## Related

- [Basics](basics.md) - Type names, paths, spacing, colors and icons
- [Overview](overview.md) - All widgets by group
- [FlowLayout](flow-layout.md) - Cells that wrap to the width of the screen
- [CanvasLayout](canvas-layout.md) - Content at any position in pixels
