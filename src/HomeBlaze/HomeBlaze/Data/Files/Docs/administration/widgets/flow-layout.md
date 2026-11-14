---
title: FlowLayout
navTitle: FlowLayout
---

# FlowLayout

Lays its cells out from left to right and wraps them to as many rows as the width of the screen needs. All cells are equally wide. This gives one column on a phone and several on a desktop. Type name: `HomeBlaze.Components.FlowLayout`. Slot blocks: `Item`.

## Arguments

| Argument | Kind of value | Default | Meaning |
|----------|---------------|---------|---------|
| `MinWidth` | Whole number | `320` | The narrowest a cell may get, in pixels. A value below 50 counts as 50 |

## Slots

| Slot | Blocks | Holds |
|------|--------|-------|
| `Item` | Any number | One cell per block: any page content |

## Samples

Change the width of the browser window while you read this page, because a flow only shows what it does when its width changes.

A `FlowLayout` without arguments gives every cell at least 320 pixels. The three cells stand next to each other in a wide window, and they wrap to two rows and then to three as the window gets narrower:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.FlowLayout
::: Item
:::: HomeBlaze.Components.Card Title="Pump" Icon=WaterDrop
::::: HomeBlaze.Components.Widget Path=../Pump
::: Item
:::: HomeBlaze.Components.Card Title="Fan" Icon=Air
::::: HomeBlaze.Components.Widget Path=../Fan
::: Item
:::: HomeBlaze.Components.Card Title="Notes"
A cell can hold any page content, not only widgets.

The pump runs at {{ ../Pump/CurrentSpeed }} RPM.
::
~~~~

:: HomeBlaze.Components.FlowLayout
::: Item
:::: HomeBlaze.Components.Card Title="Pump" Icon=WaterDrop
::::: HomeBlaze.Components.Widget Path=../Pump
::: Item
:::: HomeBlaze.Components.Card Title="Fan" Icon=Air
::::: HomeBlaze.Components.Widget Path=../Fan
::: Item
:::: HomeBlaze.Components.Card Title="Notes"
A cell can hold any page content, not only widgets.

The pump runs at {{ ../Pump/CurrentSpeed }} RPM.
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
The pump is {{ ../Pump/Status }} and runs at {{ ../Pump/CurrentSpeed }} of {{ ../Pump/TargetSpeed }} RPM.
::: Item
:::: HomeBlaze.Components.Card Title="Fan" Icon=Air
The fan is {{ ../Fan/Status }} and runs at {{ ../Fan/CurrentSpeed }} of {{ ../Fan/TargetSpeed }} RPM.
::
~~~~

:: HomeBlaze.Components.FlowLayout MinWidth=600
::: Item
:::: HomeBlaze.Components.Card Title="Pump" Icon=WaterDrop
The pump is {{ ../Pump/Status }} and runs at {{ ../Pump/CurrentSpeed }} of {{ ../Pump/TargetSpeed }} RPM.
::: Item
:::: HomeBlaze.Components.Card Title="Fan" Icon=Air
The fan is {{ ../Fan/Status }} and runs at {{ ../Fan/CurrentSpeed }} of {{ ../Fan/TargetSpeed }} RPM.
::

## Behavior and Limits

- **A number of pixels**: `MinWidth` is a plain number, without a unit. The layout puts as many columns into a row as fit with at least that width each, and then makes all columns equally wide to fill the row. The room between two cells is in [Spacing](basics.md#spacing).
- **Smaller means more columns**: With `MinWidth=120` many cells fit into one row. With `MinWidth=600` two cells stand next to each other only on a wide screen.
- **At least as wide as the widest widget**: A cell can get as narrow as `MinWidth`, and a widget that needs more room then paints over the next cell. The sample motor widget needs 280 pixels, and 304 pixels inside a card, so 320 fits it.
- **Narrow screens**: On a screen that is narrower than `MinWidth` there is one column, as wide as the screen.
- **Cards and gauges**: [Spacing](basics.md#spacing) has the height of the cards of a row, and [Gauge Rows](gauge.md#gauge-rows) the `MinWidth` for a row of gauges.
- **Equal widths**: All cells have the same width. A cell cannot span two columns.
- **No stretched rows**: With fewer cells than columns fit, the rest of the row stays empty.

## Related

- [Basics](basics.md) - Type names, paths, spacing, colors and icons
- [Overview](overview.md) - All widgets by group
- [Card](card.md) - A titled box, the usual content of a cell
- [GridLayout](grid-layout.md) - Fixed rows and columns, with cells that span several
- [Responsive](responsive.md) - Content that is shown only at some widths of the screen
