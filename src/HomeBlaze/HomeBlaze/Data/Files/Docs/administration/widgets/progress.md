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
| `Path` | Path (Number) | None | The [path](basics.md#value-widgets) of the property whose value the bar shows |
| `Minimum` | Number with a fraction | `0` | The value at which the bar is empty |
| `Maximum` | Number with a fraction | `1` | The value at which the bar is full |
| `Color` | Colour | `Primary` | The [colour](basics.md#colours) of the bar |

## Slots

A `Progress` has no slots.

## Samples

Two bars for a speed. The highest target speed of a sample motor is 3000 RPM, so that is the end of the range. The second bar has another colour:

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

## Behaviour and Limits

- **The range**: The bar is empty at `Minimum` and full at `Maximum`. A value outside the range gives an empty or a full bar. The default range from 0 to 1 is what a property with the unit percent holds. A value from 0 to 100 needs `Maximum=100`.
- **No bar**: A value that is no number, a value that is not set and a range in which `Maximum` is not above `Minimum` show a dash in place of the bar.
- **No text**: The bar has no text. Show the number with an expression, or put the bar into a row of a [ValueList](value-list.md), where the value stands next to it.
- **Width**: Alone, a bar is as wide as the place it stands in. In a row of a value list it fills the room between the label and the value, and it is at least 48 pixels wide.

## Related

- [Basics](basics.md) - Type names, paths, value widgets, colours and icons
- [Overview](overview.md) - Every widget by group
- [Gauge](gauge.md) - A number as a ring between a minimum and a maximum
- [Slider](slider.md) - Sets a number between a minimum and a maximum
- [ValueList](value-list.md) - Rows with a label and a value, with a bar as content
