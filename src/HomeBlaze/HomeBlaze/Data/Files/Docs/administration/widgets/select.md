---
title: Select
navTitle: Select
position: 42
---

# Select

Picks one value of an enum from a list. Type name: `HomeBlaze.Components.Select`.

## Arguments

| Argument | Kind of value | Default | Meaning |
|----------|---------------|---------|---------|
| `Path` | Path (Enum, writable) | None | The [path](basics.md#paths) of the property whose value the select shows and writes |
| `Label` | Text | From the property, see [Labels](basics.md#labels) | The label of the select |
| `Map` | Name or path of a `ValueMap` | None | The map that gives each value its text and icon, see [ValueMap](value-map.md) |

## Slots

A `Select` has no slots.

## Samples

These samples operate the shared sample motors `Pump` and `Fan` next to this page. What they change lasts until the motors restart, see [Control Writes](basics.md#control-writes).

A select for the mode of the fan. The list holds the names of the values: `Eco`, `Normal` and `Boost`. The mode changes the speed that the fan aims at, which the list below the select shows:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.Select Path=../Fan/Mode Label="Fan mode"
:: HomeBlaze.Components.ValueList
::: Value Path=../Fan/CurrentSpeed Label="Fan speed"
::
~~~~

:: HomeBlaze.Components.Select Path=../Fan/Mode Label="Fan mode"
:: HomeBlaze.Components.ValueList
::: Value Path=../Fan/CurrentSpeed Label="Fan speed"
::

A select with a map. Each value in the list gets the text and the icon of its case, and the select shows the text of the chosen value:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.ValueMap#modes
::: Case Value=Eco Text="Saving" Color=Success Icon=EnergySavingsLeaf
::: Case Value=Normal Text="Normal" Icon=Speed
::: Case Value=Boost Text="Full power" Color=Warning Icon=RocketLaunch
:: HomeBlaze.Components.Select Path=../Pump/Mode Map=modes
::
~~~~

:: HomeBlaze.Components.ValueMap#modes
::: Case Value=Eco Text="Saving" Color=Success Icon=EnergySavingsLeaf
::: Case Value=Normal Text="Normal" Icon=Speed
::: Case Value=Boost Text="Full power" Color=Warning Icon=RocketLaunch
:: HomeBlaze.Components.Select Path=../Pump/Mode Map=modes
::

## Behavior and Limits

- **The values**: The list holds every value of the enum by its name, in the order of the enum. A property that can hold no value as well gets a dash as first item, which clears it.
- **The map**: With `Map`, a value with a case shows the text and the icon of the case, the icon in the color of the case. A value without a case keeps its name. A map that cannot be resolved shows `Cannot resolve map` in place of the select, see [Value Widget Errors](../page-errors.md#value-widget-errors).
- **A missing value**: A property that holds no value, and a path that leads nowhere, show a dash as the chosen value.
- **Flags enums**: An enum whose values combine as flags cannot be shown as one choice. The select shows `Cannot select a value of the flags enum` with the name of the enum in its place, see [Value Widget Errors](../page-errors.md#value-widget-errors).
- **Disabled**: Besides the cases of [Control Writes](basics.md#control-writes), the select is disabled when the property holds no enum.

## Related

- [Basics](basics.md) - Paths, writes, what they save, and who can operate a control
- [Overview](overview.md) - All widgets by group
- [Control](control.md) - Chooses the control that fits its target
