---
title: Widget Basics
navTitle: Basics
position: 2
---

# Widget Basics

The rules that all widgets of a markdown page share. The [overview](overview.md) lists the widgets, and [Markdown Pages](../pages.md#blocks) describes the syntax of the blocks that they are written in.

---

## Type Names

A block names its type with the full type name: the namespace and the name of the class, for example `HomeBlaze.Components.Card`. The namespace is the name of the library that ships the type. The widgets of this reference are in `HomeBlaze.Components`, except `HomeBlaze.History.Blazor.HistoryChart`, which ships with the history library. The sample motor is `HomeBlaze.Samples.Motor`, and a device has the namespace of its device library. The name is case-sensitive.

A block that fills a slot of a widget is written with the name of the slot instead, for example `Tab` inside a `TabLayout` or `Case` inside a `ValueMap`. Such a slot block is only allowed directly inside its widget, see [Slot Blocks](#slot-blocks).

## Sample Motors

The samples of these pages need subjects to show. They use two sample motors: the subject files `Pump.json` and `Fan.json` in this folder, of the type `HomeBlaze.Samples.Motor`, with the names "Pump" and "Fan" and the target speeds 1200 and 800 RPM. The documentation is copied into every installation, so the two motors run there as well and the samples show live values.

A sample refers to a motor relative to its page, for example `../Pump/CurrentSpeed` for the current speed of the pump. A sample that you copy to a page of your own needs the path of one of your subjects in its place.

All samples on these pages are written in the short form, where the closing line of the last block ends the sample and the text of the page goes on below it, see [Closing and Nesting](../pages.md#closing-and-nesting). A name is unique on a page, so every name on a page, like the name of a tab, belongs to one sample only.

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.Card Title="Pump" Icon=WaterDrop
The pump runs at {{ ../Pump/CurrentSpeed }} of {{ ../Pump/TargetSpeed }} RPM.
::
~~~~

:: HomeBlaze.Components.Card Title="Pump" Icon=WaterDrop
The pump runs at {{ ../Pump/CurrentSpeed }} of {{ ../Pump/TargetSpeed }} RPM.
::

## Paths

A widget refers to a subject, a property or an operation by its path. The path is written like the path of an [expression](../pages.md#expression-paths): relative to the page, where the first segment can be the name of a block, or absolute. A relative path starts at the page, wherever the block stands on the page, see [Paths on Markdown Pages](../paths.md#paths-on-markdown-pages).

| Path | Leads to |
|------|----------|
| `motor/CurrentSpeed` | A property of the block named `motor` on the same page |
| `../Pump/CurrentSpeed` | A property of the subject file `Pump.json` in the folder of the page. `../` is the folder that contains the page |
| `/Docs/administration/widgets/Pump/CurrentSpeed` | The same property from every page, from the root |

A subject file is a segment without its extension, so `Pump.json` is `Pump`. A page keeps its extension, for example `/Docs/home.md`.

## The Path Picker

A widget's edit dialog lets you type a path or pick it in the tree of the subjects. Open it in the Inline or Split mode of the page, where a click on the widget selects it and "Edit" in the bar above it opens the dialog, or with the "Edit" button of the page editor for its block, as [Editing in the Page Editor](../pages.md#editing-in-the-page-editor) describes. For a path to a property, the tree offers the properties that have a state, which are the values that the property panel of a subject shows, and the subjects on the way to them. A configuration property without a state, for example the `Name` of a motor, is not offered, so type its path. A typed path is not checked.

Each path argument says what it can lead to, and the arguments table of each widget names it in brackets, for example `Path (Boolean)` or `Path (Number, writable)`:

| Filter | The tree lets you select |
|--------|--------------------------|
| `Boolean` | A property that holds true or false |
| `Number` | A property that holds a whole number, a number with a fraction or a decimal |
| `Enum` | A property that holds one of a fixed set of names, such as the `Mode` of a motor |
| `Value` | Any value: a boolean, a number, an enum, a text, a date and so on, but no operation |
| `Operation` | An operation or a query of a subject, such as `EmergencyStop` of a motor |
| `writable` | Only a property that a widget can write, combined with the filters above |

A property that does not match is shown greyed out and cannot be selected. Operations are listed only for an argument that takes one. For a path to a subject, such as the `Map` of a value widget, the tree offers the subjects of the matching type.

## Slot Blocks

The slots of a widget are filled as [Content and Slots](../pages.md#content-and-slots) describes. The table under Slots on the page of each widget lists them: `Default slot` for the slot that takes the loose content of the block, `One` for a slot that takes one block and `Any number` for a slot that takes a list of blocks.

- **Blocks that create a subject**: A slot block whose arguments its widget page lists, like `Tab` or `Case`, creates a subject. It takes those arguments, an optional `#name` and a `json` block, like a widget block. A name keeps the subject when the page is edited, see [What an Edit of the Page Keeps](../pages.md#what-an-edit-of-the-page-keeps). Every other slot block, like `Item` or `Footer`, holds content only and takes no arguments, no `#name` and no `json` block.
- **No default slot**: A widget whose table has no `Default slot` takes content only inside its slot blocks. Content outside of them is an error, see [Slot and Name Errors](../page-errors.md#slot-and-name-errors).
- **Configuration file**: In a configuration file of its own, a widget has the blocks of a slot that takes any number of them as a list, named like the slot property in camel case, such as `cells` of a `GridLayout`, `nodes` of a `CanvasLayout`, `columns` of a `SubjectTable` and `series` of a `HistoryChart`. An entry of a block that creates a subject has the arguments of the block in camel case, and the content of a `Cell` or a `Node` is its subject `child`. On a page they are always slot blocks, and neither an argument nor the [`json` block](../pages.md#the-json-block) can set them.

## Labels

A widget with a `Label` argument shows the label that is set. Without it, a widget that reads a property shows the title that the property declares, and the name of the property when it declares none. When the path leads nowhere:

- **Controls, values and chart series**: A [Switch](switch.md), a [Slider](slider.md), a [Select](select.md), a [Control](control.md), a [Gauge](gauge.md), a row of a [ValueList](value-list.md), a [ValueTile](value-tile.md) and a series of a [HistoryChart](history-chart.md) show the path.
- **Operations**: An [OperationButton](operation-button.md), and a `Control` for an operation, show the title of the method, and the path when the path leads nowhere.
- **Table columns**: A column of a [SubjectTable](subject-table.md) without `Label` takes the title of the property from the first row that has the property, and the name of the property when no row has it.

## Value Widgets

`ValueList`, `ValueTile`, `Indicator`, `Progress`, `Gauge` and `SubjectTable` show values of properties, and `ValueMap` says what text, colour and icon a value gets. The pages of these widgets refer to the rules in this section.

- **The formatted value**: A value is shown in the [formatted form of an expression](../pages.md#expression-formats). A map can replace the text.
- **Live**: A value changes on the page when the property changes.

## Missing Values

A value widget shows a dash in place of a value that is not set and for a path that leads nowhere, with the path as tooltip. This is no error box, because a device that is offline or still starting is normal. A misspelled path shows the same dash, so check the spelling when a dash stays. The pages of [Indicator](indicator.md), [Progress](progress.md), [Gauge](gauge.md) and [SubjectTable](subject-table.md) say what they show in its place. A control shows a missing value as its page describes.

## Ranges

A [Slider](slider.md), a [Gauge](gauge.md) and a [Progress](progress.md), and a [Control](control.md) that chooses a slider, show a number within a range. Each end of the range comes from the first of these that gives one:

1. The `Minimum` or `Maximum` argument of the block.
2. The attribute `Minimum` or `Maximum` of the property, when its value is a number. A type declares them for its properties, see [Range Attributes](../../development/building-subjects.md#range-attributes).
3. For a property with the unit percent, 0 and 1.

Rules for all ranges:

- **No range**: A widget that finds no end of the range of a number shows `Needs a range: set Minimum and Maximum` in its place, see [Value Widget Errors](../page-errors.md#value-widget-errors). Only a number needs a range: a path that leads nowhere, or one to a property that holds no number, shows no message. A `Gauge` then shows an empty ring with the dash of a [missing value](#missing-values) or the value as text, a `Progress` shows the dash, a `Slider` is disabled, and a `Control` chooses a value row.
- **An empty range**: A `Maximum` that is not above `Minimum` shows `Maximum must be above Minimum` with both ends in place of the widget.
- **The unit of the property**: `Minimum` and `Maximum` are in the unit of the property, so a percent runs from 0 to 1.

## Colours

A colour is one of `Default`, `Primary`, `Secondary`, `Tertiary`, `Info`, `Success`, `Warning` and `Error`. They are the colours of the theme, so they follow it. `MudBlazor.Color` has a few more names that are accepted, but these are the ones meant for pages. The `IconColor` of a card, the `Color` of a case of a map, the `Color` of a progress bar and the `Color` of an operation button take one.

## Icons

The `Icon` of a card, a tab, a panel, a row, a tile, an indicator, an alert, an operation button and a case of a map is the name of a MudBlazor Material icon in its filled form, for example `Settings`, `Bolt`, `WaterDrop`, `AcUnit` or `Link`. The name is case-sensitive. An unknown name shows a default icon and no error.

## Control Writes

The control widgets ([Switch](switch.md) and the others of the group Controls in the [overview](overview.md)) write properties and invoke operations. They follow the same rules as the property panel of a subject.

- **Who can operate**: Every viewer of a page can operate its controls. Every viewer of this documentation can operate the two sample motors through the control samples.
- **Disabled**: A control is disabled when its path leads nowhere, when the property cannot be written because it has no public setter, and while the page is edited.
- **Live**: A control follows its property, so a change by another viewer or by the device moves it.
- **Writes**: A value is converted to the type of the property. A value that the type cannot hold, or that the property refuses, shows an error message, and nothing is rolled back.
- **Runtime values**: A property that is no configuration property, such as `IsPowered`, `Mode` and `Load` of the sample motors, changes in memory only and keeps its value until the subject restarts. The control samples of these pages write only such properties, so they change no file.
- **Saving**: A write to a configuration property saves the subject that owns the configuration, one second after the last write to it, so a series of arrow keys gives one save. A subject defined on a page saves its block of that page and leaves the rest of the page as it is. A subject of a `*.json` file saves that file.
- **Not saved**: When nothing can save the value, for example for a subject that a device discovered at runtime and that belongs to no file, a warning says that the value is not saved, and the value stays in memory. Such a subject warns at each save.
- **Devices**: A write to a property that a connector sends to a device can fail later on the device side, without a message on the page.
- **The text editor**: A page that is open in the Split mode text editor does not take what a control saved, and saving the editor afterwards overwrites it.
- **Several viewers**: Two viewers who change two subjects of one page at the same time both keep their change: the saves of one page run one after the other. A value that was not saved stays in memory when the page is reloaded, as long as the block of its subject is not edited. Saving a block inside a layout counts as an edit of that layout: a cell that was added to the layout or removed from it and not saved yet is gone after it, while the configuration of the layout and its other cells keep what was not saved.

## Spacing

Only containers create space: the page, a card, a tab, an expansion panel and the cells of a layout. A widget has no outer margin of its own, so the same widget looks the same wherever it stands, and the space around it comes from where it stands. The one exception is the [Indicator](indicator.md), which keeps 4 pixels to what stands next to it, so that several indicators in a row do not touch.

- **Between blocks of a page**: 16 pixels between two blocks and between a block and the text before or after it. Paragraphs of text keep 12 pixels between each other, and headings their own space.
- **Inside a container**: The blocks and paragraphs of the content are 16 pixels apart, and a heading is 8 pixels above what follows it. The first and the last of them add nothing at the edge of the container, also when a [Responsive](responsive.md) block that the screen hides stands first or last. A widget that shows nothing, such as a `ValueMap`, takes no space.
- **Containers**: A [card](card.md) has 12 pixels of room at every edge of its header, body and footer, and 4 pixels between its title and a body right below it. A tab has 16 pixels between its tab bar and its content and nothing at the other edges, unless its tab bar is at a side: then its content has 4 pixels at the opposite edge and at the bottom, so that the shadow of a card in it is not cut off. An expansion panel keeps the room that its header and its frame have. A cell of a [FlowLayout](flow-layout.md) adds no room: a card in it brings its own. A cell of a [GridLayout](grid-layout.md) and a node of a [CanvasLayout](canvas-layout.md) draw a frame, so their content has the 12 pixels of a card body at every edge, unless it is a single card, which fills them.
- **Layouts**: The cells of a `FlowLayout` and of a `GridLayout` are 16 pixels apart in both directions.
- **Fluid and fixed widgets**: A card, a tile, a list, a table and a chart are as wide as their place. A card that is the only content of a `FlowLayout` cell is as high as the highest cell of its row, so the cards of a row end on one line, and its footer stays at the bottom. A widget of a fixed size, such as a [Gauge](gauge.md) or a button, stands at the start of its place; [Gauge Rows](gauge.md#gauge-rows) shows how to choose the column width for it.
- **Card footers**: The blocks of a footer, such as two buttons, stand in a row with 8 pixels between them and wrap when the card is too narrow.
- **Controls**: A control is aligned by its visible edges: the track of a [Switch](switch.md), the box of a [Select](select.md) and the label of a [Slider](slider.md) start at the edge where text starts. Inside a widget, its own parts keep their room, such as the 16 pixels between the rows of a value list or the small margin around a chip; the first and the last row of a value list add nothing at its edges.

---

## Related

- [Overview](overview.md) - All widgets by group
- [Markdown Pages](../pages.md) - The syntax of pages and blocks
- [Paths](../paths.md) - The path syntax
