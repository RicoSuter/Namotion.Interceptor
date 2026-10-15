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

A block that fills a slot of a widget is written with the name of the slot instead, for example `Tab` inside a `TabLayout` or `Case` inside a `ValueMap`. Such a slot block is only allowed directly inside its widget.

## Sample Motors

The samples of these pages need subjects to show. They use two sample motors: the subject files `Pump.json` and `Fan.json` in this folder, of the type `HomeBlaze.Samples.Motor`, with the names "Pump" and "Fan" and the target speeds 1200 and 800 RPM. The documentation is copied into every installation, so the two motors run there as well and the samples show live values.

A sample refers to a motor relative to its page, for example `../Pump/CurrentSpeed` for the current speed of the pump. A sample that you copy to a page of your own needs the path of one of your subjects in its place.

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

A widget refers to a subject or to a property by its path. A relative path starts at the page, wherever the block stands on the page, see [Paths on Markdown Pages](../paths.md#paths-on-markdown-pages).

| Path | Leads to |
|------|----------|
| `motor/CurrentSpeed` | A property of the block named `motor` on the same page |
| `../Pump/CurrentSpeed` | A property of the subject file `Pump.json` in the folder of the page. `../` is the folder that contains the page |
| `/Demo/Conveyor/CurrentSpeed` | The same property from every page, from the root |

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

## Missing Values

A value widget shows a dash for a value that is not set and for a path that leads nowhere, and no error box. [Value Widgets](../page-widgets.md#value-widgets) has the rules for values.

## Control Writes

The control widgets ([Switch](switch.md) and the others of the group Controls in the [overview](overview.md)) write properties and invoke operations. They follow the same rules as the property panel of a subject.

- **Who can operate**: Every viewer of a page can operate its controls. Every viewer of this documentation can operate the two sample motors through the control samples.
- **Disabled**: A control is disabled when its path leads nowhere, when the property cannot be written because it has no public setter, and while the page is edited.
- **Writes**: A value is converted to the type of the property. A value that the type cannot hold, or that the property refuses, shows an error message, and nothing is rolled back.
- **Runtime values**: A property that is no configuration property, such as `IsPowered`, `Mode` and `Load` of the sample motors, changes in memory only and keeps its value until the subject restarts. The control samples of these pages write only such properties, so they change no file.
- **Saving**: A write to a configuration property saves the subject that owns the configuration, one second after the last write to it, so a series of arrow keys gives one save. A subject defined on a page saves its block of that page and leaves the rest of the page as it is. A subject of a `*.json` file saves that file.
- **Not saved**: When nothing can save the value, for example for a subject that a device discovered at runtime and that belongs to no file, a warning says that the value is not saved, and the value stays in memory. Such a subject warns at each save.
- **Devices**: A write to a property that a connector sends to a device can fail later on the device side, without a message on the page.
- **The text editor**: A page that is open in the Split mode text editor does not take what a control saved, and saving the editor afterwards overwrites it.

---

## Related

- [Overview](overview.md): every widget with one line
- [Markdown Pages](../pages.md): the syntax of pages and blocks
- [Paths](../paths.md): the path syntax
