---
title: Widget Basics
navTitle: Basics
position: 2
---

# Widget Basics

The rules that all widgets of a markdown page share. The [overview](overview.md) lists the widgets, and [Markdown Pages](../pages.md#blocks) describes the syntax of the blocks that they are written in.

---

## Type Names

A block names its type with the full type name: the namespace and the name of the class, for example `HomeBlaze.Components.Card`. The namespace is the name of the library that ships the type. The widgets of this reference are in `HomeBlaze.Components`, the sample motor is `HomeBlaze.Samples.Motor`, and a device has the namespace of its device library. The name is case-sensitive.

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

The edit dialog of a widget lets you type a path or pick it in the tree of the subjects. For a path to a property, the tree offers the properties that have a state, which are the values that the property panel of a subject shows, and the subjects on the way to them. A configuration property without a state, for example the `Name` of a motor, is not offered, so type its path. For a path to a subject, such as the `Map` of a value widget, the tree offers the subjects of the matching type.

## Missing Values

A value widget shows a dash for a value that is not set and for a path that leads nowhere, and no error box. [Value Widgets](../page-widgets.md#value-widgets) has the rules for values.

---

## Related

- [Overview](overview.md): every widget with one line
- [Markdown Pages](../pages.md): the syntax of pages and blocks
- [Paths](../paths.md): the path syntax
