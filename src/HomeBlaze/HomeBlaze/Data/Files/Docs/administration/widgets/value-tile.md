---
title: ValueTile
navTitle: ValueTile
position: 32
---

# ValueTile

A small box with a label and one large value. Type name: `HomeBlaze.Components.ValueTile`.

## Arguments

| Argument | Kind of value | Default | Meaning |
|----------|---------------|---------|---------|
| `Path` | Path (Value) | None | The [path](basics.md#paths) of the property whose value the tile shows |
| `Label` | Text | From the property, see [Labels](basics.md#labels) | The small text above the value |
| `Icon` | Text | None | The [icon](basics.md#icons) next to the label |
| `Map` | Name or path of a `ValueMap` | None | The map that gives the value its text, colour and icon, see [ValueMap](value-map.md) |

## Slots

A `ValueTile` has no slots.

## Samples

Three tiles in a `FlowLayout`. The first has a label of its own and an icon, the second takes its label from the property, and the third shows the status through the map of [ValueMap](value-map.md):

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.FlowLayout MinWidth=140
::: Item
:::: HomeBlaze.Components.ValueTile Path=../Pump/CurrentSpeed Label="Pump speed" Icon=Speed
::: Item
:::: HomeBlaze.Components.ValueTile Path=../Pump/Temperature
::: Item
:::: HomeBlaze.Components.ValueTile Path=../Pump/Status Map=../value-map.md/motorstate
::
~~~~

:: HomeBlaze.Components.FlowLayout MinWidth=140
::: Item
:::: HomeBlaze.Components.ValueTile Path=../Pump/CurrentSpeed Label="Pump speed" Icon=Speed
::: Item
:::: HomeBlaze.Components.ValueTile Path=../Pump/Temperature
::: Item
:::: HomeBlaze.Components.ValueTile Path=../Pump/Status Map=../value-map.md/motorstate
::

## Behaviour and Limits

- **Colour and icon of a case**: A matching case gives the value its colour and the tile its icon, see [ValueMap](value-map.md).
- **Spacing**: A tile adds no distance around itself and is as wide as its place, see [Spacing](basics.md#spacing). It is meant to stand in a layout such as `FlowLayout`, which does the spacing.

## Related

- [Basics](basics.md) - Type names, paths, value widgets, colours and icons
- [Overview](overview.md) - All widgets by group
- [ValueMap](value-map.md) - The text, colour and icon of a value
- [Gauge](gauge.md) - One value in a ring between a minimum and a maximum
