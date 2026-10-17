---
title: Indicator
navTitle: Indicator
---

# Indicator

Shows a state as a colored chip, or as an icon alone. Type name: `HomeBlaze.Components.Indicator`.

## Arguments

| Argument | Kind of value | Default | Meaning |
|----------|---------------|---------|---------|
| `Path` | Path (Value) | None | The [path](basics.md#paths) of the property whose value the indicator shows |
| `Map` | Name or path of a `ValueMap` | None | The map that gives the indicator its text, color and icon, see [ValueMap](value-map.md) |
| `Icon` | Text | None | The [icon](basics.md#icons) |
| `Kind` | `Chip` or `Icon` | `Chip` | `Chip` is a chip with the icon and the text. `Icon` is the icon alone, with the text as its tooltip |

## Slots

An `Indicator` has no slots.

## Samples

A chip with the map of [ValueMap](value-map.md):

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.Indicator Path=../Pump/Status Map=../value-map.md/motorstate
::
~~~~

:: HomeBlaze.Components.Indicator Path=../Pump/Status Map=../value-map.md/motorstate
::

The same status as an icon alone. The icon is the one of the matching case, and the tooltip has the text of the case:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.Indicator Path=../Pump/Status Map=../value-map.md/motorstate Kind=Icon
::
~~~~

:: HomeBlaze.Components.Indicator Path=../Pump/Status Map=../value-map.md/motorstate Kind=Icon
::

An indicator without a map, for a boolean. It has the primary color while the value is true and the default color otherwise. The icon comes from `Icon`:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.Indicator Path=../Pump/IsAtTargetSpeed Kind=Icon Icon=Speed
::
~~~~

:: HomeBlaze.Components.Indicator Path=../Pump/IsAtTargetSpeed Kind=Icon Icon=Speed
::

An indicator in the `HeaderRight` of a card:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.Card Title="Pump" Icon=WaterDrop
The pump runs at {{ ../Pump/CurrentSpeed }} RPM.

::: HeaderRight
:::: HomeBlaze.Components.Indicator Path=../Pump/Status Map=../value-map.md/motorstate
::
~~~~

:: HomeBlaze.Components.Card Title="Pump" Icon=WaterDrop
The pump runs at {{ ../Pump/CurrentSpeed }} RPM.

::: HeaderRight
:::: HomeBlaze.Components.Indicator Path=../Pump/Status Map=../value-map.md/motorstate
::

## Behavior and Limits

- **Text, color and icon**: They come from the matching case of the map, see [ValueMap](value-map.md). `Icon` is the icon that stays when no case gives one.
- **Without a map**: A boolean that is true has the color `Primary`, and every other value has `Default`. With a map the color comes from the case alone.
- **No icon**: A chip without an icon shows its text only. An indicator of the kind `Icon` without an icon shows a plain circle.
- **A missing value**: The chip shows a dash, see [Missing Values](basics.md#missing-values). The icon form shows its icon in the default color, with the path as tooltip. A chip with a value has no tooltip.
- **A small distance to its surroundings**: An indicator keeps 4 pixels to what stands next to it, the one exception of [Spacing](basics.md#spacing).

## Related

- [Basics](basics.md) - Type names, paths, value widgets, colors and icons
- [Overview](overview.md) - All widgets by group
- [ValueMap](value-map.md) - The text, color and icon of a value
- [Card](card.md) - A titled box with a header corner for an indicator
- [Gauge](gauge.md) - One value in a ring between a minimum and a maximum
