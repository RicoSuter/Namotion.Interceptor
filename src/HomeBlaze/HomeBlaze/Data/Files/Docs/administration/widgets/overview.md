---
title: Page Widgets
navTitle: Overview
position: 1
---

# Page Widgets

The widgets that ship with HomeBlaze for [markdown pages](../pages.md), by group, with one line each. [Widget Basics](basics.md) has the rules that all of them share: type names, paths, the path picker, missing values and control writes.

The reference of each widget has its arguments, its slots and its samples. Some widgets are still described in a section of [Widgets for Markdown Pages](../page-widgets.md), and their links lead there. The others have a page of their own in this folder.

---

## Layout

| Widget | Purpose |
|--------|---------|
| [FlowLayout](../page-widgets.md#flowlayout) | Lays its cells out from left to right and wraps them to as many rows as the screen needs |
| [TabLayout](../page-widgets.md#tablayout) | Shows one of several tabs at a time |
| [GridLayout](../page-widgets.md#gridlayout) | A grid with a fixed number of rows and columns |
| [CanvasLayout](../page-widgets.md#canvaslayout) | An area on which subjects are placed at any position |
| [ExpansionPanels](../page-widgets.md#expansionpanels) | Page content in panels that open and close |
| [Responsive](responsive.md) | Shows its content only on phones, tablets or desktops |

## Content

| Widget | Purpose |
|--------|---------|
| [Card](../page-widgets.md#card) | A titled box around page content |
| [Alert](../page-widgets.md#alert) | A highlighted box for a note or a warning |
| [Widget](../page-widgets.md#widget) | Shows the widget of a subject that exists somewhere else |

## Values

| Widget | Purpose |
|--------|---------|
| [ValueMap](value-map.md) | Says what text, colour and icon a value gets in the widgets that refer to it |
| [ValueList](value-list.md) | A list of rows, each with a label and a value |
| [ValueTile](value-tile.md) | A small box with a label and one large value |
| [Indicator](indicator.md) | Shows a state as a coloured chip, or as an icon alone |
| [Progress](progress.md) | A bar that shows where a number stands between a minimum and a maximum |
| [Gauge](gauge.md) | A ring that shows where a number stands between a minimum and a maximum |
| [SubjectTable](subject-table.md) | A table with a row per subject of a folder or a list and a column per property |

## Controls

| Widget | Purpose |
|--------|---------|
| [Switch](switch.md) | Turns a boolean on and off, by writing it or by invoking an operation per state |
| [Slider](slider.md) | Sets a number between a minimum and a maximum |
| [Select](select.md) | Picks one value of an enum |
| [OperationButton](operation-button.md) | Invokes an operation or a query |
| [Control](control.md) | Shows the control that fits a property or an operation |

## History

| Widget | Purpose |
|--------|---------|
| [HistoryChart](history-chart.md) | The recorded history of numbers as lines and of states as bands, with a legend of the current values |

---

## Related

- [Widget Basics](basics.md): the rules that all widgets share
- [Markdown Pages](../pages.md): the syntax of pages and blocks
- [Page Errors](../page-errors.md): every error message with its cause and its fix
