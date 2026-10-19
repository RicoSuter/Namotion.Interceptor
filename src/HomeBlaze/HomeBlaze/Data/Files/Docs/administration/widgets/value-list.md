---
title: ValueList
navTitle: ValueList
position: 31
---

# ValueList

A list of rows. Each row has an icon, a label, optional content and a value, which stands at the right. Type name: `HomeBlaze.Components.ValueList`. Slot blocks: `Value`.

## Arguments

A `ValueList` has no arguments.

## Slots

| Slot | Blocks | Holds |
|------|--------|-------|
| `Value` | Any number | One row per block: optional page content, which stands between the label and the value |

The arguments of a `Value` block:

| Argument | Kind of value | Default | Meaning |
|----------|---------------|---------|---------|
| `Path` | Path (Value) | None | The [path](basics.md#paths) of the property whose value the row shows. A row with content needs none |
| `Label` | Text | From the property, see [Labels](basics.md#labels) | The label of the row |
| `Icon` | Text | None | The [icon](basics.md#icons) at the start of the row |
| `Map` | Name or path of a `ValueMap` | None | The map that gives the value its text, color and icon, see [ValueMap](value-map.md) |

## Samples

Rows with a label and an icon. The first two rows have an icon, so the third row keeps the room of an icon, which lines up its label with theirs:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.ValueList
::: Value Path=../Pump/CurrentSpeed Label="Current speed" Icon=Speed
::: Value Path=../Pump/Temperature Label="Motor temperature" Icon=Thermostat
::: Value Path=../Pump/IsAtTargetSpeed Label="At target speed"
::
~~~~

:: HomeBlaze.Components.ValueList
::: Value Path=../Pump/CurrentSpeed Label="Current speed" Icon=Speed
::: Value Path=../Pump/Temperature Label="Motor temperature" Icon=Thermostat
::: Value Path=../Pump/IsAtTargetSpeed Label="At target speed"
::

Rows without a label, the last one with the map of [ValueMap](value-map.md). Each row takes its label from the property, see [Labels](basics.md#labels): "Speed" and "Target" are titles that the motor declares, and "Status" is the name of its property. The status is shown with the text, the color and the icon of its case:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.ValueList
::: Value Path=../Pump/CurrentSpeed
::: Value Path=../Pump/TargetSpeed
::: Value Path=../Pump/Status Map=../value-map.md/motorstate
::
~~~~

:: HomeBlaze.Components.ValueList
::: Value Path=../Pump/CurrentSpeed
::: Value Path=../Pump/TargetSpeed
::: Value Path=../Pump/Status Map=../value-map.md/motorstate
::

A row with content and without a path, and a row with a `Progress` as content. The content stands between the label and the value. The first row has no path and no value of its own: its content is a text with two expressions. The second row has a path and a bar:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.ValueList
::: Value Label="Pump" Icon=WaterDrop
{{ ../Pump/CurrentSpeed }} of {{ ../Pump/TargetSpeed }} RPM
::: Value Path=../Fan/CurrentSpeed Label="Fan" Icon=Air
:::: HomeBlaze.Components.Progress Path=../Fan/CurrentSpeed Minimum=0 Maximum=3000
::
~~~~

:: HomeBlaze.Components.ValueList
::: Value Label="Pump" Icon=WaterDrop
{{ ../Pump/CurrentSpeed }} of {{ ../Pump/TargetSpeed }} RPM
::: Value Path=../Fan/CurrentSpeed Label="Fan" Icon=Air
:::: HomeBlaze.Components.Progress Path=../Fan/CurrentSpeed Minimum=0 Maximum=3000
::

## Behavior and Limits

- **Aligned labels**: When at least one row has an icon, the rows without one keep the room of an icon, so that all labels line up. An icon that comes from a case of a map counts as well. In a card with an icon, a list in which at least one row has an icon lines up the icons of its rows with the icon of the header and its labels with the title.
- **Color and icon of a case**: A matching case gives the value text its color and the row its icon, see [ValueMap](value-map.md).
- **History**: The label of a row opens the history of a property that has one, see [Value Widgets](basics.md#value-widgets).
- **Content and value**: Content and a value can stand in the same row. The content takes the room between the label and the value: a `Progress` fills it, and a text stands at the right end of it, right before the value.

## Related

- [Basics](basics.md) - Type names, paths, value widgets, colors and icons
- [Overview](overview.md) - All widgets by group
- [ValueMap](value-map.md) - The text, color and icon of a value
- [Progress](progress.md) - A bar for a number, also as the content of a row
- [ValueTile](value-tile.md) - A small box with a label and one large value
