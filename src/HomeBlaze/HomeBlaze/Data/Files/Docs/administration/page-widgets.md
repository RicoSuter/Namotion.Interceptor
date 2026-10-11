---
title: Widgets for Markdown Pages
navTitle: Page Widgets
position: 5
---

# Widgets for Markdown Pages

This page is the reference of the widgets that ship with HomeBlaze and are made for [markdown pages](pages.md): `Card`, `FlowLayout`, `TabLayout`, `Widget`, `GridLayout` and `CanvasLayout`, each with its arguments, its slots, a sample and its limits. The samples are page source. [Markdown Pages](pages.md#blocks) describes the syntax of the blocks that they are written in.

Every other type that can be configured works as a block as well, see [Widgets](pages.md#widgets) on that page.

---

## Card

A titled box around page content. Type name: `HomeBlaze.Components.Card`.

| Argument | Kind of value | Default | Meaning |
|----------|---------------|---------|---------|
| `Title` | Text | None | The title at the top of the card |
| `Icon` | Text | None | The [icon](#icons) next to the title |

| Slot | Blocks | Holds |
|------|--------|-------|
| `Content` | Default slot | The body of the card: any page content |
| `Footer` | One | Content below a divider at the bottom of the card |

~~~~markdown
:: HomeBlaze.Components.Card Title="Conveyor Belt" Icon=Settings
The conveyor runs at {{ ../Conveyor/CurrentSpeed }} RPM.

::: HomeBlaze.Components.Widget Path=../Conveyor
:::

::: Footer
Target speed: {{ ../Conveyor/TargetSpeed }} RPM
:::
::

:: HomeBlaze.Components.Card
This card has no title, no icon and no footer. It is a plain box around its content.
::
~~~~

- The title row is shown when the card has a title or an icon.
- A card is as wide as the place it stands in. Put cards into a `FlowLayout` to place them next to each other.
- A card has one footer and no other parts, such as buttons or a subtitle.

## FlowLayout

Lays its cells out from left to right and wraps them to as many rows as the width of the screen needs. All cells are equally wide. This gives one column on a phone and several on a desktop. Type name: `HomeBlaze.Components.FlowLayout`.

| Argument | Kind of value | Default | Meaning |
|----------|---------------|---------|---------|
| `MinWidth` | Whole number | `320` | The narrowest a cell may get, in pixels. A value below 50 counts as 50 |

| Slot | Blocks | Holds |
|------|--------|-------|
| `Item` | Any number | One cell per block: any page content |

~~~~markdown
:: HomeBlaze.Components.FlowLayout MinWidth=320
::: Item
:::: HomeBlaze.Components.Card Title="Compressor" Icon=Bolt
::::: HomeBlaze.Components.Widget Path=../Compressor
:::::
::::
:::
::: Item
:::: HomeBlaze.Components.Card Title="Water Pump" Icon=WaterDrop
::::: HomeBlaze.Components.Widget Path=../WaterPump
:::::
::::
:::
::: Item
:::: HomeBlaze.Components.Card Title="Notes"
A cell can hold any page content, not only widgets.

The compressor runs at {{ ../Compressor/CurrentSpeed }} RPM.
::::
:::
::
~~~~

How `MinWidth` works and how to choose it:

- **A number of pixels**: `MinWidth` is a plain number, without a unit. The layout puts as many columns into a row as fit with at least that width each, and then makes all columns equally wide to fill the row. There are 16 pixels between two cells.
- **Smaller means more columns**: With `MinWidth=120` many cells fit into one row. With `MinWidth=600` two cells stand next to each other only on a wide screen.
- **At least as wide as the widest widget**: A cell can get as narrow as `MinWidth`, and a widget that needs more room then paints over the next cell. The sample motor widget needs 280 pixels, and 312 pixels inside a card, so 320 fits it.
- **Narrow screens**: On a screen that is narrower than `MinWidth` there is one column, as wide as the screen.

Limits:

- All cells have the same width. A cell cannot span two columns.
- A row is not stretched: with fewer cells than columns fit, the rest of the row stays empty.
- An `Item` holds content only. It takes no arguments and no `#name`.
- A `FlowLayout` has no default slot. Content outside of an `Item` block is an error.

## TabLayout

Shows one of several tabs at a time. Type name: `HomeBlaze.Components.TabLayout`. It has no arguments.

| Slot | Blocks | Holds |
|------|--------|-------|
| `Tab` | Any number | One tab per block. A `Tab` block creates a subject, so it takes arguments, an optional `#name` and any page content |

The arguments of a `Tab` block:

| Argument | Kind of value | Default | Meaning |
|----------|---------------|---------|---------|
| `Title` | Text | None | The label of the tab |
| `Icon` | Text | None | The [icon](#icons) on the tab |

~~~~markdown
:: HomeBlaze.Components.TabLayout
::: Tab#cooling Title="Cooling Fan" Icon=AcUnit
The cooling fan has a temperature of {{ ../CoolingFan/Temperature }} °C.

:::: HomeBlaze.Components.Widget Path=../CoolingFan
::::
:::
::: Tab#exhaust Title="Exhaust Fan" Icon=Air
The exhaust fan runs at {{ ../ExhaustFan/CurrentSpeed }} RPM.

:::: HomeBlaze.Components.Widget Path=../ExhaustFan
::::
:::
::: Tab Title="Notes"
A tab without a name and without an icon.
:::
::
~~~~

- **All tabs are rendered**: Every tab is rendered and kept up to date, also the hidden ones. A subject in a hidden tab keeps running, and a hidden tab costs as much as a visible one.
- **The first tab is selected**: When the page opens, the first tab is shown. The selection is not saved and there is no argument for it.
- **Names keep the selection**: When the page text changes while the page is open, a tab with a name stays selected, also when tabs are added, removed or moved. A tab without a name is known by its position only, so the selection stays at the position. When the selected tab is gone, the first tab is selected.
- **Tabs only**: A `TabLayout` has no default slot. Content outside of a `Tab` block is an error.

## Widget

Shows the widget of a subject that exists somewhere else: in a file of its own, or in another block of the page. Type name: `HomeBlaze.Components.Widget`. It has no slots, so its block stays empty.

| Argument | Kind of value | Default | Meaning |
|----------|---------------|---------|---------|
| `Path` | Text | Empty | The path to the subject to show |

~~~~markdown
:: HomeBlaze.Samples.Motor#localmotor Name="Local Motor" TargetSpeed=1500
::

:: HomeBlaze.Components.Card Title="Three Ways to Refer to a Subject" Icon=Link
By the name of a block on this page:

::: HomeBlaze.Components.Widget Path=localmotor
:::

By a relative path, which starts at the page. This is the file `Conveyor.json` next to the page:

::: HomeBlaze.Components.Widget Path=../Conveyor
:::

By an absolute path, which leads to the same subject from every page:

::: HomeBlaze.Components.Widget Path=/Demo/Conveyor
:::
::
~~~~

- **Not a copy**: The widget shows the subject itself. A motor that is shown twice is one motor.
- **A path that leads nowhere**: The widget shows the message "Cannot resolve path" with the path. This is no error box, and a `Widget` without a `Path` shows it too.
- **A path that leads back**: A `Widget` that refers to itself or to a block that contains it shows the message "Cannot show path" in place of the content. Two blocks that show each other end with the same message.
- **A subject without a widget**: When the type of the subject has no widget, the notice "No Widget component for" with the type name is shown.

## GridLayout

A grid with a fixed number of rows and columns. Type name: `HomeBlaze.Components.GridLayout`. It has no slots: its cells are configuration and stand in the `json` block, and each cell holds one subject.

| Argument | Kind of value | Default | Meaning |
|----------|---------------|---------|---------|
| `Rows` | Whole number | `2` | The number of rows |
| `Columns` | Whole number | `2` | The number of columns |
| `Cells` | List, in the `json` block | Empty | The cells of the grid |

One cell in `Cells`:

| Name | Kind of value | Default | Meaning |
|------|---------------|---------|---------|
| `Row` | Whole number | Not set | The row of the cell, counted from 0. Without it the grid places the cell itself |
| `Column` | Whole number | Not set | The column of the cell, counted from 0. Without it the grid places the cell itself |
| `RowSpan` | Whole number | `1` | How many rows the cell covers |
| `ColumnSpan` | Whole number | `1` | How many columns the cell covers |
| `Child` | Subject with `$type` | Not set | The subject that the cell shows |

~~~~markdown
:: HomeBlaze.Components.GridLayout#motorgrid Rows=2 Columns=2
```json
{
  "Cells": [
    { "Row": 0, "Column": 0, "Child": { "$type": "HomeBlaze.Components.Widget", "Path": "../Compressor" } },
    { "Row": 0, "Column": 1, "Child": { "$type": "HomeBlaze.Components.Widget", "Path": "../WaterPump" } },
    { "Row": 1, "Column": 0, "ColumnSpan": 2, "Child": { "$type": "HomeBlaze.Samples.Motor", "Name": "Grid Motor", "TargetSpeed": 900 } }
  ]
}
```
::
~~~~

- All rows have the same height and all columns the same width, and the grid is at least 400 pixels high.
- A cell holds a subject, not page content. Text, expressions and blocks cannot stand in a cell. Use a `FlowLayout` with cards for that.
- The subjects in the cells are created again on every reload of the page, see [What an Edit of the Page Keeps](pages.md#what-an-edit-of-the-page-keeps). Show a subject that must keep running through a `Widget` in the cell.
- The names inside `Cells` are not checked, see [The json Block](pages.md#the-json-block).
- While the page is in edit mode, the grid has its own buttons to add, edit and remove cells. They write the `json` block for you.

## CanvasLayout

An area on which subjects are placed at any position. Type name: `HomeBlaze.Components.CanvasLayout`. It has no slots: its nodes are configuration and stand in the `json` block.

| Argument | Kind of value | Default | Meaning |
|----------|---------------|---------|---------|
| `MinHeight` | Whole number | Not set, which gives 400 | The height of the canvas in pixels |
| `IsSnapToGridEnabled` | Yes or no | `false` | Whether a node that is placed or moved snaps to a grid |
| `GridSize` | Whole number | `100` | The step of that grid in pixels |
| `Nodes` | List, in the `json` block | Empty | The nodes on the canvas |

One node in `Nodes`:

| Name | Kind of value | Default | Meaning |
|------|---------------|---------|---------|
| `X` | Whole number | `0` | The distance from the left edge in pixels |
| `Y` | Whole number | `0` | The distance from the top edge in pixels |
| `Width` | Whole number | `200` | The width in pixels |
| `Height` | Whole number | `150` | The height in pixels |
| `Child` | Subject with `$type` | Not set | The subject that the node shows |

~~~~markdown
:: HomeBlaze.Components.CanvasLayout#plan MinHeight=320 IsSnapToGridEnabled=true GridSize=20
```json
{
  "Nodes": [
    { "X": 20, "Y": 20, "Width": 320, "Height": 260, "Child": { "$type": "HomeBlaze.Components.Widget", "Path": "../Conveyor" } }
  ]
}
```
::
~~~~

- While the page is in edit mode, the nodes can be moved with the mouse, and the canvas has its own buttons to add, edit and remove nodes. They write the `json` block for you.
- The limits of the `GridLayout` hold here as well: a node holds a subject and no page content, the subjects are created again on every reload, and the names inside `Nodes` are not checked.

## Icons

The `Icon` of a card and of a tab is the name of a MudBlazor Material icon in its filled form, for example `Settings`, `Bolt`, `WaterDrop`, `AcUnit` or `Link`. The name is case-sensitive. An unknown name shows a default icon and no error.

---

## Related Documentation

- [Markdown Pages](pages.md) - The syntax of pages: expressions, blocks, slots and the `json` block
- [Page Errors](page-errors.md) - Every error message with its cause and its fix
- [Paths](paths.md) - Path syntax reference
