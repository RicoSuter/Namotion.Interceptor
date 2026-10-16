---
title: Gauge
navTitle: Gauge
position: 35
---

# Gauge

A ring that shows where a number stands between a minimum and a maximum, with the value in its centre and a label below it. Type name: `HomeBlaze.Components.Gauge`.

## Arguments

| Argument | Kind of value | Default | Meaning |
|----------|---------------|---------|---------|
| `Path` | Path (Number) | None | The path of the property whose value the gauge shows. The picker offers numbers only |
| `Label` | Text | The title of the property, then its name | The text below the ring. Without it the gauge shows the title that the property declares, the name of the property when it declares none, and the path when the path leads nowhere |
| `Minimum` | Number with a fraction | From the property | The value at which the ring is empty |
| `Maximum` | Number with a fraction | From the property | The value at which the ring is full |
| `Map` | Name or path of a `ValueMap` | None | The map that gives the text in the centre and the colour of the ring, see [ValueMap](value-map.md) |
| `Size` | `Small`, `Medium` or `Large` | `Medium` | The size of the ring: 72, 112 or 160 pixels |

## Slots

The gauge has no slots.

## Samples

The current speed of the sample pump. Its range is given, because the current speed has no range of its own:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.Gauge Path=../Pump/CurrentSpeed Minimum=0 Maximum=3000 Label="Pump speed"
::
~~~~

:: HomeBlaze.Components.Gauge Path=../Pump/CurrentSpeed Minimum=0 Maximum=3000 Label="Pump speed"
::

The target speed of a sample motor has the attributes `Minimum` and `Maximum`, so the gauge takes its range from them:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.Gauge Path=../Pump/TargetSpeed
::
~~~~

:: HomeBlaze.Components.Gauge Path=../Pump/TargetSpeed
::

The load of a sample motor has the unit percent, so its range is 0 to 1 without any argument:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.Gauge Path=../Pump/Load
::
~~~~

:: HomeBlaze.Components.Gauge Path=../Pump/Load
::

A map on the same page gives the text in the centre and the colour of the ring for one value:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.ValueMap#gaugetarget
::: Case Value=800 Text="Normal" Color=Success
:: HomeBlaze.Components.Gauge Path=../Fan/TargetSpeed Map=gaugetarget Label="Fan target"
::
~~~~

:: HomeBlaze.Components.ValueMap#gaugetarget
::: Case Value=800 Text="Normal" Color=Success
:: HomeBlaze.Components.Gauge Path=../Fan/TargetSpeed Map=gaugetarget Label="Fan target"
::

The three sizes next to each other in a [FlowLayout](flow-layout.md), whose columns fit the large ring, see [Gauge Rows](#gauge-rows):

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.FlowLayout MinWidth=168
::: Item
:::: HomeBlaze.Components.Gauge Path=../Fan/CurrentSpeed Minimum=0 Maximum=3000 Size=Small
::: Item
:::: HomeBlaze.Components.Gauge Path=../Fan/CurrentSpeed Minimum=0 Maximum=3000
::: Item
:::: HomeBlaze.Components.Gauge Path=../Fan/CurrentSpeed Minimum=0 Maximum=3000 Size=Large
::
~~~~

:: HomeBlaze.Components.FlowLayout MinWidth=168
::: Item
:::: HomeBlaze.Components.Gauge Path=../Fan/CurrentSpeed Minimum=0 Maximum=3000 Size=Small
::: Item
:::: HomeBlaze.Components.Gauge Path=../Fan/CurrentSpeed Minimum=0 Maximum=3000
::: Item
:::: HomeBlaze.Components.Gauge Path=../Fan/CurrentSpeed Minimum=0 Maximum=3000 Size=Large
::

## Gauge Rows

A gauge has a fixed size and stands at the start of its column, so a row of gauges in a [FlowLayout](flow-layout.md) looks even when the columns are only a little wider than the rings. The rings are 72 pixels (`Small`), 112 pixels (`Medium`) and 160 pixels (`Large`) wide; a `MinWidth` of the ring size plus 8 pixels fits them: 80 for `Small`, 120 for `Medium` and 168 for `Large`, and the size of the largest ring for a row of mixed sizes. The gap of 16 pixels between the columns comes on top. A wider column leaves more room after each gauge than between the gauges. A label that is wider than the ring needs a wider column.

## Behaviour and Limits

- **The range**: `Minimum` and `Maximum` come first. Without them the gauge takes the `Minimum` and `Maximum` attributes of the property when they are numbers, and for a property with the unit percent the range 0 to 1. A property without any of them shows the error "Needs a range: set Minimum and Maximum" in place of the gauge, and a `Maximum` that is not above `Minimum` shows "Maximum must be above Minimum" with both ends, see [Value Widget Errors](../page-errors.md#value-widget-errors). The same rules hold for [Slider](slider.md).
- **Outside the range**: A value below the minimum gives an empty ring and a value above the maximum a full one. The centre still shows the value.
- **A missing value**: A value that is not set and a path that leads nowhere show a dash in the centre and an empty ring, with the path as tooltip, as every value widget does, see [Basics](basics.md).
- **The text in the centre**: The formatted value with its unit, or the text of the matched case of the map.
- **The colour**: The colour of the matched case of the map, otherwise the primary colour of the theme.
- **Accessibility**: The gauge is a meter for screen readers, with the label as its name, the value kept inside the range and the range as its numbers, and the text in the centre as its spoken value. A gauge without a value is read as a group with its label.
- **Live**: The ring and the text change on the page when the property changes.

## Related

- [Basics](basics.md) - Paths, value widgets, missing values and colours
- [Overview](overview.md) - All widgets
- [Slider](slider.md) - Sets a number with the same range rules
