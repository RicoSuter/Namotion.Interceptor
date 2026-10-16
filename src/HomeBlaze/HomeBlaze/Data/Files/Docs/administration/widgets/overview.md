---
title: Widgets for Markdown Pages
navTitle: Overview
position: 1
---

# Widgets for Markdown Pages

These widgets ship with HomeBlaze and are made for [markdown pages](../pages.md). Each one has a page of its own with its arguments, its slots, live samples and its limits. [Basics](basics.md) has the rules that all of them share: type names, paths, the sample motors, value widgets, picker filters, control writes, colours, icons and spacing. [Markdown Pages](../pages.md#blocks) describes the syntax of the blocks that they are written in.

Every other type that can be configured works as a block as well, see [Widgets](../pages.md#widgets) on that page.

## Layout

| Widget | Type name | What it does |
|--------|-----------|--------------|
| [FlowLayout](flow-layout.md) | `HomeBlaze.Components.FlowLayout` | Cells of equal width that wrap to as many rows as the screen needs |
| [TabLayout](tab-layout.md) | `HomeBlaze.Components.TabLayout` | One of several tabs at a time |
| [GridLayout](grid-layout.md) | `HomeBlaze.Components.GridLayout` | A grid with a fixed number of rows and columns and page content in each cell |
| [CanvasLayout](canvas-layout.md) | `HomeBlaze.Components.CanvasLayout` | An area on which page content is placed at any position |
| [ExpansionPanels](expansion-panels.md) | `HomeBlaze.Components.ExpansionPanels` | Page content in panels that open and close |
| [Responsive](responsive.md) | `HomeBlaze.Components.Responsive` | Page content that is shown only at some widths of the screen |

## Content

| Widget | Type name | What it does |
|--------|-----------|--------------|
| [Card](card.md) | `HomeBlaze.Components.Card` | A titled box around page content |
| [Alert](alert.md) | `HomeBlaze.Components.Alert` | A highlighted box for a note or a warning |
| [Widget](widget.md) | `HomeBlaze.Components.Widget` | The widget of a subject that exists somewhere else |

## Values

| Widget | Type name | What it does |
|--------|-----------|--------------|
| [ValueMap](value-map.md) | `HomeBlaze.Components.ValueMap` | The text, colour and icon that a value gets in other widgets |
| [ValueList](value-list.md) | `HomeBlaze.Components.ValueList` | Rows with an icon, a label, optional content and a value |
| [ValueTile](value-tile.md) | `HomeBlaze.Components.ValueTile` | A small box with a label and one large value |
| [Indicator](indicator.md) | `HomeBlaze.Components.Indicator` | A state as a coloured chip or as an icon |
| [Progress](progress.md) | `HomeBlaze.Components.Progress` | A bar that shows where a number stands between a minimum and a maximum |
| [Gauge](gauge.md) | `HomeBlaze.Components.Gauge` | A ring with a number between a minimum and a maximum |
| [SubjectTable](subject-table.md) | `HomeBlaze.Components.SubjectTable` | A table with one row per subject and one column per property |

## Controls

| Widget | Type name | What it does |
|--------|-----------|--------------|
| [Switch](switch.md) | `HomeBlaze.Components.Switch` | Turns a boolean on and off, by writing it or through two operations |
| [Slider](slider.md) | `HomeBlaze.Components.Slider` | Sets a number within a range |
| [Select](select.md) | `HomeBlaze.Components.Select` | Picks a value of an enum |
| [OperationButton](operation-button.md) | `HomeBlaze.Components.OperationButton` | Runs an operation or a query |
| [Control](control.md) | `HomeBlaze.Components.Control` | Shows the matching widget for what its path leads to |

## History

| Widget | Type name | What it does |
|--------|-----------|--------------|
| [HistoryChart](history-chart.md) | `HomeBlaze.History.Blazor.HistoryChart` | The recorded history of numbers as lines and of states as bands |

## Related Documentation

- [Markdown Pages](../pages.md) - The syntax of pages: expressions, blocks, slots and the `json` block
- [Page Errors](../page-errors.md) - Every error message with its cause and its fix
- [Paths](../paths.md) - Path syntax reference
