---
title: Progress
navTitle: Progress
position: 34
---

# Progress

A bar that shows where a number stands between a minimum and a maximum. Type name: `HomeBlaze.Components.Progress`. It has no map.

## Arguments

| Argument | Kind of value | Default | Meaning |
|----------|---------------|---------|---------|
| `Path` | Path (Number) | None | The [path](basics.md#paths) of the property whose value the bar shows |
| `Minimum` | Number with a fraction | From the property, see [Ranges](basics.md#ranges) | The value at which the bar is empty, in the unit of the property |
| `Maximum` | Number with a fraction | From the property, see [Ranges](basics.md#ranges) | The value at which the bar is full, in the unit of the property |
| `Color` | Color | `Primary` | The [color](basics.md#colors) of the bar |

## Slots

A `Progress` has no slots.

## Samples

Two bars for a speed. The highest target speed of a sample motor is 3000 RPM, so that is the end of the range. The second bar has another color:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.Progress Path=../Pump/CurrentSpeed Minimum=0 Maximum=3000
::

:: HomeBlaze.Components.Progress Path=../Fan/CurrentSpeed Minimum=0 Maximum=3000 Color=Success
::
~~~~

:: HomeBlaze.Components.Progress Path=../Pump/CurrentSpeed Minimum=0 Maximum=3000
::

:: HomeBlaze.Components.Progress Path=../Fan/CurrentSpeed Minimum=0 Maximum=3000 Color=Success
::

## Behavior and Limits

- **The range**: [Ranges](basics.md#ranges) has where the ends of the range come from and the messages for a missing or an empty range, the same as for a [Gauge](gauge.md) and a [Slider](slider.md).
- **Outside the range**: A value below the minimum gives an empty bar and a value above the maximum a full one.
- **No bar**: A [missing value](basics.md#missing-values), a path that leads nowhere and a value that is no number show the dash in place of the bar.
- **No text**: The bar has no text. Show the number with an expression, or put the bar into a row of a [ValueList](value-list.md), where the value stands next to it.
- **Width**: Alone, a bar is as wide as the place it stands in. In a row of a value list it fills the room between the label and the value, and it is at least 48 pixels wide.

## Related

- [Basics](basics.md) - Type names, paths, value widgets, colors and icons
- [Overview](overview.md) - All widgets by group
- [Gauge](gauge.md) - A number as a ring between a minimum and a maximum
- [Slider](slider.md) - Sets a number between a minimum and a maximum
- [ValueList](value-list.md) - Rows with a label and a value, with a bar as content
