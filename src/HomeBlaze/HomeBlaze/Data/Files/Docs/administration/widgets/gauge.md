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
| `Path` | Path (Number) | None | The [path](basics.md#paths) of the property whose value the gauge shows |
| `Label` | Text | From the property, see [Labels](basics.md#labels) | The text below the ring |
| `Minimum` | Number with a fraction | From the property, see [Ranges](basics.md#ranges) | The value at which the ring is empty |
| `Maximum` | Number with a fraction | From the property, see [Ranges](basics.md#ranges) | The value at which the ring is full |
| `Map` | Name or path of a `ValueMap` | None | The map that gives the text in the centre and the colour of the ring, see [ValueMap](value-map.md) |
| `Size` | `Small`, `Medium` or `Large` | `Medium` | The size of the ring: 72, 112 or 160 pixels |

## Slots

A `Gauge` has no slots.

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

- **The range**: [Ranges](basics.md#ranges) has where the ends of the range come from and the messages for a missing or an empty range.
- **Outside the range**: A value below the minimum gives an empty ring and a value above the maximum a full one. The centre still shows the value.
- **A missing value**: The dash of a [missing value](basics.md#missing-values) stands in the centre, and the ring is empty.
- **The text in the centre**: The formatted value with its unit, or the text of the matched case of the map.
- **The colour**: The colour of the matched case of the map, otherwise the primary colour of the theme.
- **Accessibility**: The gauge is a meter for screen readers, with the label as its name, the value kept inside the range and the range as its numbers, and the text in the centre as its spoken value. A gauge without a value is read as a group with its label.

## Related

- [Basics](basics.md) - Paths, value widgets, missing values and colours
- [Overview](overview.md) - All widgets by group
- [Slider](slider.md) - Sets a number with the same range rules
