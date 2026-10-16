---
title: Indicator
navTitle: Indicator
position: 33
---

# Indicator

Shows a state as a coloured chip, or as an icon alone. Type name: `HomeBlaze.Components.Indicator`.

## Arguments

| Argument | Kind of value | Default | Meaning |
|----------|---------------|---------|---------|
| `Path` | Path (Value) | None | The [path](basics.md#value-widgets) of the property whose value the indicator shows |
| `Map` | Name or path of a `ValueMap` | None | The map that gives the indicator its text, colour and icon, see [ValueMap](value-map.md) |
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

An indicator without a map, for a boolean. It has the primary colour while the value is true and the default colour otherwise. The icon comes from `Icon`:

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

## Behaviour and Limits

- **Text, colour and icon**: They come from the matching case of the map, see [ValueMap](value-map.md). `Icon` is the icon that stays when no case gives one.
- **Without a map**: A boolean that is true has the colour `Primary`, and every other value has `Default`. With a map the colour comes from the case alone.
- **No icon**: A chip without an icon shows its text only. An indicator of the kind `Icon` without an icon shows a plain circle.
- **A missing value**: The chip shows a dash. The icon form shows its icon in the default colour. Both have the path as tooltip, see [Value Widgets](basics.md#value-widgets). A chip with a value has no tooltip.
- **A small distance to its surroundings**: An indicator keeps a distance of 4 pixels to what stands next to it, so that several of them in a row do not touch.

## Related

- [Basics](basics.md) - Type names, paths, value widgets, colours and icons
- [Overview](overview.md) - Every widget by group
- [ValueMap](value-map.md) - The text, colour and icon of a value
- [Card](card.md) - A titled box with a header corner for an indicator
- [Gauge](gauge.md) - One value in a ring between a minimum and a maximum
