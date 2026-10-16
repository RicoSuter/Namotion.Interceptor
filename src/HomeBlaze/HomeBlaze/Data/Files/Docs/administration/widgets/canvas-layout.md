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
| `MinHeight` | Whole number | Not set, which gives 400 | The height of the canvas in pixels |
| `IsSnapToGridEnabled` | Yes or no | `false` | Whether a node that is placed or moved snaps to a grid |
| `GridSize` | Whole number | `100` | The step of that grid in pixels |

## Slots

| Slot | Blocks | Holds |
|------|--------|-------|
| `Node` | Any number | One node per block. A `Node` block creates a subject, so it takes arguments and an optional `#name`. Its content is what the node shows: any page content |

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

## Behaviour and Limits

- While the page is in edit mode, the nodes can be moved with the mouse, each node has buttons to edit and remove it, a click on a node selects the widget in it, and a click on a free place adds a node there. The canvas itself is selected with the arrow up button of a selected widget in a node, or with the keyboard, see [Editing in the Page Editor](../pages.md#editing-in-the-page-editor).
- A canvas can be zoomed with the mouse wheel and moved by dragging its background only while the page is edited. While the page is viewed, the canvas itself cannot be zoomed or moved, the wheel scrolls the page, and the widgets in its nodes work as anywhere else on the page. While the page is edited, the widgets in the nodes do not react to the mouse, so that a node can be moved without triggering what it shows. No line can be drawn between nodes in either mode.
- A node has a fixed size and cuts off content that needs more room. Names and a configuration file work as for the cells of a [GridLayout](grid-layout.md), with the list `nodes`.
- A [Card](card.md) in a node fills the node, as in a cell of a `GridLayout`.

## Related

- [Basics](basics.md) - Type names, paths, spacing, colours and icons
- [Overview](overview.md) - Every widget by group
- [GridLayout](grid-layout.md) - Fixed rows and columns that keep their share of the width
