---
title: Widgets for Markdown Pages
navTitle: Page Widgets
position: 5
---

# Widgets for Markdown Pages

This page is the reference of the widgets that ship with HomeBlaze and are made for [markdown pages](pages.md): `Card`, `FlowLayout`, `TabLayout`, `Widget`, `GridLayout` and `CanvasLayout`, each with its arguments, its slots, its samples and its limits. The samples on this page are live: the block below each source is that source at work. [Markdown Pages](pages.md#blocks) describes the syntax of the blocks that they are written in.

Every other type that can be configured works as a block as well, see [Widgets](pages.md#widgets) on that page.

---

## Sample Motors

Several samples need a subject to show. This page creates two sample motors for them, and the samples refer to the motors by their names `pump` and `fan`, in a `Path` and in expressions. A sample that you copy to a page of your own needs the name or the path of one of your subjects in their place.

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Samples.Motor#pump Name="Pump" TargetSpeed=1200
:: HomeBlaze.Samples.Motor#fan Name="Fan" TargetSpeed=800
::
~~~~

:: HomeBlaze.Samples.Motor#pump Name="Pump" TargetSpeed=1200
:: HomeBlaze.Samples.Motor#fan Name="Fan" TargetSpeed=800
::

The head line of the second motor ends the first motor, and the closing line ends the second one, so that the text of the page goes on below it. All samples on this page are written in this short form, see [Closing and Nesting](pages.md#closing-and-nesting). A name is unique on a page, so every other name on this page, like the name of a tab, belongs to one sample only.

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

A card with a title, an icon and a footer. Its body is text with an expression and a widget. The head line of the footer has three colons like the widget, so it ends the widget:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.Card Title="Pump" Icon=WaterDrop
The pump runs at {{ pump/CurrentSpeed }} RPM.

::: HomeBlaze.Components.Widget Path=pump

::: Footer
Target speed: {{ pump/TargetSpeed }} RPM
::
~~~~

:: HomeBlaze.Components.Card Title="Pump" Icon=WaterDrop
The pump runs at {{ pump/CurrentSpeed }} RPM.

::: HomeBlaze.Components.Widget Path=pump

::: Footer
Target speed: {{ pump/TargetSpeed }} RPM
::

A card without arguments and without a footer:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.Card
This card has no title, no icon and no footer. It is a plain box around its content.
::
~~~~

:: HomeBlaze.Components.Card
This card has no title, no icon and no footer. It is a plain box around its content.
::

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

Change the width of the browser window while you read this section, because a flow only shows what it does when its width changes.

A `FlowLayout` without arguments gives every cell at least 320 pixels. The three cells stand next to each other in a wide window, and they wrap to two rows and then to three as the window gets narrower:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.FlowLayout
::: Item
:::: HomeBlaze.Components.Card Title="Pump" Icon=WaterDrop
::::: HomeBlaze.Components.Widget Path=pump
::: Item
:::: HomeBlaze.Components.Card Title="Fan" Icon=Air
::::: HomeBlaze.Components.Widget Path=fan
::: Item
:::: HomeBlaze.Components.Card Title="Notes"
A cell can hold any page content, not only widgets.

The pump runs at {{ pump/CurrentSpeed }} RPM.
::
~~~~

:: HomeBlaze.Components.FlowLayout
::: Item
:::: HomeBlaze.Components.Card Title="Pump" Icon=WaterDrop
::::: HomeBlaze.Components.Widget Path=pump
::: Item
:::: HomeBlaze.Components.Card Title="Fan" Icon=Air
::::: HomeBlaze.Components.Widget Path=fan
::: Item
:::: HomeBlaze.Components.Card Title="Notes"
A cell can hold any page content, not only widgets.

The pump runs at {{ pump/CurrentSpeed }} RPM.
::

With `MinWidth=120` a cell may get as narrow as 120 pixels, so many cells fit into one row. The six cards stand in one row while the page is at least 800 pixels wide, and wrap below that:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.FlowLayout MinWidth=120
::: Item
:::: HomeBlaze.Components.Card Title="One"
First cell
::: Item
:::: HomeBlaze.Components.Card Title="Two"
Second cell
::: Item
:::: HomeBlaze.Components.Card Title="Three"
Third cell
::: Item
:::: HomeBlaze.Components.Card Title="Four"
Fourth cell
::: Item
:::: HomeBlaze.Components.Card Title="Five"
Fifth cell
::: Item
:::: HomeBlaze.Components.Card Title="Six"
Sixth cell
::
~~~~

:: HomeBlaze.Components.FlowLayout MinWidth=120
::: Item
:::: HomeBlaze.Components.Card Title="One"
First cell
::: Item
:::: HomeBlaze.Components.Card Title="Two"
Second cell
::: Item
:::: HomeBlaze.Components.Card Title="Three"
Third cell
::: Item
:::: HomeBlaze.Components.Card Title="Four"
Fourth cell
::: Item
:::: HomeBlaze.Components.Card Title="Five"
Fifth cell
::: Item
:::: HomeBlaze.Components.Card Title="Six"
Sixth cell
::

With `MinWidth=600` a cell is never narrower than 600 pixels, as long as the page is that wide. The two cards stand next to each other only in a wide window, and below each other in every other one:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.FlowLayout MinWidth=600
::: Item
:::: HomeBlaze.Components.Card Title="Pump" Icon=WaterDrop
The pump is {{ pump/Status }} and runs at {{ pump/CurrentSpeed }} of {{ pump/TargetSpeed }} RPM.
::: Item
:::: HomeBlaze.Components.Card Title="Fan" Icon=Air
The fan is {{ fan/Status }} and runs at {{ fan/CurrentSpeed }} of {{ fan/TargetSpeed }} RPM.
::
~~~~

:: HomeBlaze.Components.FlowLayout MinWidth=600
::: Item
:::: HomeBlaze.Components.Card Title="Pump" Icon=WaterDrop
The pump is {{ pump/Status }} and runs at {{ pump/CurrentSpeed }} of {{ pump/TargetSpeed }} RPM.
::: Item
:::: HomeBlaze.Components.Card Title="Fan" Icon=Air
The fan is {{ fan/Status }} and runs at {{ fan/CurrentSpeed }} of {{ fan/TargetSpeed }} RPM.
::

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

Two tabs with a name and an icon, and a third tab without a name and without an icon that holds plain text:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.TabLayout
::: Tab#pumptab Title="Pump" Icon=WaterDrop
The pump runs at {{ pump/CurrentSpeed }} RPM.

:::: HomeBlaze.Components.Widget Path=pump
::: Tab#fantab Title="Fan" Icon=Air
The fan runs at {{ fan/CurrentSpeed }} RPM.

:::: HomeBlaze.Components.Widget Path=fan
::: Tab Title="Notes"
A tab can hold any page content, not only widgets.
::
~~~~

:: HomeBlaze.Components.TabLayout
::: Tab#pumptab Title="Pump" Icon=WaterDrop
The pump runs at {{ pump/CurrentSpeed }} RPM.

:::: HomeBlaze.Components.Widget Path=pump
::: Tab#fantab Title="Fan" Icon=Air
The fan runs at {{ fan/CurrentSpeed }} RPM.

:::: HomeBlaze.Components.Widget Path=fan
::: Tab Title="Notes"
A tab can hold any page content, not only widgets.
::

Tabs inside tabs. The first tab holds a tab layout of its own, which has four colons, one more than the tab around it:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.TabLayout
::: Tab#motorstab Title="Motors" Icon=Settings
:::: HomeBlaze.Components.TabLayout
::::: Tab#innerpump Title="Pump" Icon=WaterDrop
:::::: HomeBlaze.Components.Widget Path=pump
::::: Tab#innerfan Title="Fan" Icon=Air
:::::: HomeBlaze.Components.Widget Path=fan
::: Tab#abouttab Title="About" Icon=Info
The inner tabs keep their selection while you switch between the outer tabs.
::
~~~~

:: HomeBlaze.Components.TabLayout
::: Tab#motorstab Title="Motors" Icon=Settings
:::: HomeBlaze.Components.TabLayout
::::: Tab#innerpump Title="Pump" Icon=WaterDrop
:::::: HomeBlaze.Components.Widget Path=pump
::::: Tab#innerfan Title="Fan" Icon=Air
:::::: HomeBlaze.Components.Widget Path=fan
::: Tab#abouttab Title="About" Icon=Info
The inner tabs keep their selection while you switch between the outer tabs.
::

Cards in a flow inside a tab. The colons count up from two to seven here: the tab layout, its tabs, the flow layout, its items, the cards and the widgets. The second `Item` ends the first item with its card and its widget, and the second `Tab` ends the first tab with everything in it:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.TabLayout
::: Tab#cardstab Title="Cards" Icon=Dashboard
:::: HomeBlaze.Components.FlowLayout MinWidth=320
::::: Item
:::::: HomeBlaze.Components.Card Title="Pump" Icon=WaterDrop
::::::: HomeBlaze.Components.Widget Path=pump
::::: Item
:::::: HomeBlaze.Components.Card Title="Fan" Icon=Air
::::::: HomeBlaze.Components.Widget Path=fan
::: Tab#texttab Title="Text" Icon=Notes
The pump runs at {{ pump/CurrentSpeed }} RPM and the fan at {{ fan/CurrentSpeed }} RPM.
::
~~~~

:: HomeBlaze.Components.TabLayout
::: Tab#cardstab Title="Cards" Icon=Dashboard
:::: HomeBlaze.Components.FlowLayout MinWidth=320
::::: Item
:::::: HomeBlaze.Components.Card Title="Pump" Icon=WaterDrop
::::::: HomeBlaze.Components.Widget Path=pump
::::: Item
:::::: HomeBlaze.Components.Card Title="Fan" Icon=Air
::::::: HomeBlaze.Components.Widget Path=fan
::: Tab#texttab Title="Text" Icon=Notes
The pump runs at {{ pump/CurrentSpeed }} RPM and the fan at {{ fan/CurrentSpeed }} RPM.
::

A tab layout inside a card. The tab layout is the body of the card, and the footer of the card stays below the tabs whichever tab is selected. The head line of the footer has three colons like the tab layout, so it ends the tab layout:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.Card Title="Motors" Icon=Settings
::: HomeBlaze.Components.TabLayout
:::: Tab#cardpump Title="Pump"
::::: HomeBlaze.Components.Widget Path=pump
:::: Tab#cardfan Title="Fan"
::::: HomeBlaze.Components.Widget Path=fan

::: Footer
Both motors are shown by name, so they are the same motors as in the samples above.
::
~~~~

:: HomeBlaze.Components.Card Title="Motors" Icon=Settings
::: HomeBlaze.Components.TabLayout
:::: Tab#cardpump Title="Pump"
::::: HomeBlaze.Components.Widget Path=pump
:::: Tab#cardfan Title="Fan"
::::: HomeBlaze.Components.Widget Path=fan

::: Footer
Both motors are shown by name, so they are the same motors as in the samples above.
::

- **All tabs are rendered**: Every tab is rendered and kept up to date, also the hidden ones. A subject in a hidden tab keeps running, and a hidden tab costs as much as a visible one.
- **The first tab is selected**: When the page opens, the first tab is shown. The selection is not saved and there is no argument for it.
- **Names keep the selection**: When the page text changes while the page is open, a tab with a name stays selected, also when tabs are added, removed or moved. A tab without a name is known by its position only, so the selection stays at the position. When the selected tab is gone, the first tab is selected.
- **Tabs only**: A `TabLayout` has no default slot. Content outside of a `Tab` block is an error.

## Widget

Shows the widget of a subject that exists somewhere else: in a file of its own, or in another block of the page. Type name: `HomeBlaze.Components.Widget`. It has no slots, so its block stays empty.

| Argument | Kind of value | Default | Meaning |
|----------|---------------|---------|---------|
| `Path` | Text | Empty | The path to the subject to show |

The three ways to refer to a subject:

- **By the name of a block**: `Path=pump` leads to the block with that `#name` on the same page.
- **By a relative path**: `Path=../Conveyor` starts at the page, so it leads to the file `Conveyor.json` next to the page.
- **By an absolute path**: `Path=/Demo/Conveyor` leads to the same subject from every page.

By the name of a block on this page, here one of the [sample motors](#sample-motors):

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.Widget Path=pump
::
~~~~

:: HomeBlaze.Components.Widget Path=pump
::

A path that leads nowhere shows a message in place of a widget. Nothing exists at this path:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.Widget Path=/Missing/Motor
::
~~~~

:: HomeBlaze.Components.Widget Path=/Missing/Motor
::

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

A grid of two rows and two columns. The two cells of the first row show the sample motors by name. The cell of the second row spans both columns and shows the pump once more:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.GridLayout#motorgrid Rows=2 Columns=2
```json
{
  "Cells": [
    { "Row": 0, "Column": 0, "Child": { "$type": "HomeBlaze.Components.Widget", "Path": "pump" } },
    { "Row": 0, "Column": 1, "Child": { "$type": "HomeBlaze.Components.Widget", "Path": "fan" } },
    { "Row": 1, "Column": 0, "ColumnSpan": 2, "Child": { "$type": "HomeBlaze.Components.Widget", "Path": "pump" } }
  ]
}
```
::
~~~~

:: HomeBlaze.Components.GridLayout#motorgrid Rows=2 Columns=2
```json
{
  "Cells": [
    { "Row": 0, "Column": 0, "Child": { "$type": "HomeBlaze.Components.Widget", "Path": "pump" } },
    { "Row": 0, "Column": 1, "Child": { "$type": "HomeBlaze.Components.Widget", "Path": "fan" } },
    { "Row": 1, "Column": 0, "ColumnSpan": 2, "Child": { "$type": "HomeBlaze.Components.Widget", "Path": "pump" } }
  ]
}
```
::

- All rows have the same height and all columns the same width, and the grid is at least 400 pixels high.
- The grid keeps its columns at every width of the screen. A cell cuts off a widget that needs more room than the cell has, which a narrow window shows in the first row of the grid above. Use a `FlowLayout` where the cells should wrap.
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

A canvas with two nodes that show the sample motors by name. A canvas does not adapt to the width of the screen: every node keeps its position and its size in pixels. The two nodes here are placed below each other, so that a narrow window shows both:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.CanvasLayout#plan MinHeight=620 IsSnapToGridEnabled=true GridSize=20
```json
{
  "Nodes": [
    { "X": 20, "Y": 20, "Width": 300, "Height": 280, "Child": { "$type": "HomeBlaze.Components.Widget", "Path": "pump" } },
    { "X": 40, "Y": 320, "Width": 300, "Height": 280, "Child": { "$type": "HomeBlaze.Components.Widget", "Path": "fan" } }
  ]
}
```
::
~~~~

:: HomeBlaze.Components.CanvasLayout#plan MinHeight=620 IsSnapToGridEnabled=true GridSize=20
```json
{
  "Nodes": [
    { "X": 20, "Y": 20, "Width": 300, "Height": 280, "Child": { "$type": "HomeBlaze.Components.Widget", "Path": "pump" } },
    { "X": 40, "Y": 320, "Width": 300, "Height": 280, "Child": { "$type": "HomeBlaze.Components.Widget", "Path": "fan" } }
  ]
}
```
::

- While the page is in edit mode, the nodes can be moved with the mouse, and the canvas has its own buttons to add, edit and remove nodes. They write the `json` block for you.
- The limits of the `GridLayout` hold here as well: a node holds a subject and no page content, the subjects are created again on every reload, and the names inside `Nodes` are not checked.

## Icons

The `Icon` of a card and of a tab is the name of a MudBlazor Material icon in its filled form, for example `Settings`, `Bolt`, `WaterDrop`, `AcUnit` or `Link`. The name is case-sensitive. An unknown name shows a default icon and no error.

---

## Related Documentation

- [Markdown Pages](pages.md) - The syntax of pages: expressions, blocks, slots and the `json` block
- [Page Errors](page-errors.md) - Every error message with its cause and its fix
- [Paths](paths.md) - Path syntax reference
