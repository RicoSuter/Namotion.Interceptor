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
| `Path` | Path (Enum, writable) | None | The path of the property whose value the select shows and writes |
| `Label` | Text | The title of the property | The label of the select. Without it the select shows the title that the property declares, and the name of the property when it declares none |
| `Map` | Text | None | The name or the path of a `ValueMap` that gives each value its text and icon |

## Slots

The select has no slots.

## Samples

These samples operate the shared sample motors `Pump` and `Fan` next to this page. What they change lasts until the motors restart, see [Basics](basics.md).

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

## Behaviour and Limits

- **The values**: The list holds every value of the enum by its name, in the order of the enum. A property that can hold no value as well gets a dash as first item, which clears it.
- **The map**: With `Map`, a value with a case shows the text and the icon of the case, the icon in the colour of the case. A value without a case keeps its name. A map that cannot be resolved shows `Cannot resolve map` in place of the select, see [Value Widget Errors](../page-errors.md#value-widget-errors).
- **A missing value**: A property that holds no value, and a path that leads nowhere, show a dash as the chosen value.
- **Flags enums**: An enum whose values combine as flags cannot be shown as one choice. The select shows `Cannot select a value of the flags enum` with the name of the enum in its place.
- **Disabled**: The select is disabled when the property cannot be written, holds no enum, or the path leads nowhere, and while the page is edited.
- **Saving**: A write to a configuration property saves the page or the file that holds its subject, see [Basics](basics.md#control-writes). `Mode` of the sample motors is no configuration property, so these samples save nothing.
- **Several viewers**: A change by another viewer, or by the device, changes the shown value.

## Related

- [Basics](basics.md) - Paths, writes, what they save, and who can operate a control
- [Overview](overview.md) - All widgets by group
