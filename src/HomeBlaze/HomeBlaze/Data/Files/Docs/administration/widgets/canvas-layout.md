---
title: CanvasLayout
navTitle: CanvasLayout
position: 13
---

# CanvasLayout

An area on which content is placed at any position. Type name: `HomeBlaze.Components.CanvasLayout`. Slot blocks: `Node`.

## Arguments

| Argument | Kind of value | Default | Meaning |
|----------|---------------|---------|---------|
| `MinHeight` | Whole number | None | The height of the canvas in pixels. Without it the canvas is 400 pixels high |
| `IsSnapToGridEnabled` | Yes or no | `false` | Whether a node that is placed or moved snaps to a grid |
| `GridSize` | Whole number | `100` | The step of that grid in pixels |
| `Scale` | `None` or `FitWidth` | `None` | `None` draws the canvas in pixels. `FitWidth` draws a viewed canvas at the factor of the width of its place to its `Width` |
| `Width` | Whole number above 0 | The right edge of the rightmost node | The design width of the canvas in pixels, which `FitWidth` fits to the width of the page |

## Slots

| Slot | Blocks | Holds |
|------|--------|-------|
| `Node` | Any number | One node per block: any page content, which the node shows |

The arguments of a `Node` block:

| Argument | Kind of value | Default | Meaning |
|----------|---------------|---------|---------|
| `X` | Whole number | `0` | The distance from the left edge in pixels |
| `Y` | Whole number | `0` | The distance from the top edge in pixels |
| `Width` | Whole number | `200` | The width in pixels |
| `Height` | Whole number | `150` | The height in pixels |

## Samples

A canvas with two nodes that show the two sample motors. A canvas does not adapt to the width of the screen: every node keeps its position and its size in pixels. The two nodes here are placed below each other, so that a narrow window shows both:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.CanvasLayout#plan MinHeight=620 IsSnapToGridEnabled=true GridSize=20
::: Node X=20 Y=20 Width=300 Height=280
:::: HomeBlaze.Components.Widget Path=../Pump
::: Node X=40 Y=320 Width=300 Height=280
:::: HomeBlaze.Components.Widget Path=../Fan
::
~~~~

:: HomeBlaze.Components.CanvasLayout#plan MinHeight=620 IsSnapToGridEnabled=true GridSize=20
::: Node X=20 Y=20 Width=300 Height=280
:::: HomeBlaze.Components.Widget Path=../Pump
::: Node X=40 Y=320 Width=300 Height=280
:::: HomeBlaze.Components.Widget Path=../Fan
::

A canvas that fits the width. `Width=420` is the width the two nodes were designed in, so the canvas is drawn at the factor of the width of its place to 420, larger or smaller, and its height follows. Change the width of the window to see it:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.CanvasLayout#fitplan MinHeight=200 Width=420 Scale=FitWidth
::: Node X=0 Y=0 Width=200 Height=200
Pump
::: Node X=220 Y=0 Width=200 Height=200
Fan
::
~~~~

:: HomeBlaze.Components.CanvasLayout#fitplan MinHeight=200 Width=420 Scale=FitWidth
::: Node X=0 Y=0 Width=200 Height=200
Pump
::: Node X=220 Y=0 Width=200 Height=200
Fan
::

## Behavior and Limits

- **Edit mode**: While the page is in edit mode, the nodes can be moved with the mouse, a click on a node selects the widget in it, or the canvas when the node has no widget, and a click on a free place adds a node there. Each node has buttons to edit and remove it, which show while the canvas or a widget in one of its nodes is selected. The canvas is also selected with the arrow up button of a selected widget in a node, or with the keyboard, see [Editing in the Page Editor](../pages.md#editing-in-the-page-editor).
- **Zoom and move**: A canvas can be zoomed with the mouse wheel and moved by dragging its background only while the page is edited. While the page is viewed, the canvas itself cannot be zoomed or moved, the wheel scrolls the page, and the widgets in its nodes work as anywhere else on the page. While the page is edited, the widgets in the nodes do not react to the mouse, so that a node can be moved without triggering what it shows. No line can be drawn between nodes in either mode.
- **Fits the width**: A viewed canvas with `Scale=FitWidth` is drawn at the factor of the width of its place to its `Width`, also larger than 1, and its height is `MinHeight` times that factor.
- **Unscaled while edited**: While the page is edited, a `FitWidth` canvas is drawn unscaled, so that nodes move in canvas pixels. It scrolls horizontally when it is wider than the page.
- **An invalid width**: A `Width` of 0 or less shows `Width must be above 0` with the value in place of the canvas, see [Value Widget Errors](../page-errors.md#value-widget-errors).
- **Fixed size**: A node has a fixed size and cuts off content that needs more room.
- **Names**: Give a node a `#name` to keep it and everything in it when nodes are added or removed, as for the cells of a [GridLayout](grid-layout.md).
- **Cards and tiles**: A [Card](card.md) in a node fills the node, as in a cell of a `GridLayout`, and so does a tile ([Switch](switch.md) or [OperationButton](operation-button.md) with `Kind=Tile`).

## Related

- [Basics](basics.md) - Type names, paths, spacing, colors and icons
- [Overview](overview.md) - All widgets by group
- [GridLayout](grid-layout.md) - Fixed rows and columns that keep their share of the width
