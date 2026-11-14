---
title: Slider
navTitle: Slider
---

# Slider

Sets a number by moving a thumb between a minimum and a maximum. Type name: `HomeBlaze.Components.Slider`.

## Arguments

| Argument | Kind of value | Default | Meaning |
|----------|---------------|---------|---------|
| `Path` | Path (Number, writable) | None | The [path](basics.md#paths) of the property whose value the slider shows and writes |
| `Label` | Text | From the property, see [Labels](basics.md#labels) | The label above the slider |
| `Minimum` | Number with a fraction | From the property, see [Ranges](basics.md#ranges) | The value at the left end, in the unit of the property |
| `Maximum` | Number with a fraction | From the property, see [Ranges](basics.md#ranges) | The value at the right end, in the unit of the property |
| `Step` | Number with a fraction | `1` for a property that holds whole numbers, otherwise a hundredth of the range | The distance between two positions, in the unit of the property |

## Slots

A `Slider` has no slots.

## Samples

These samples operate the shared sample motors `Pump` and `Fan` next to this page. What they change lasts until the motors restart, see [Control Writes](basics.md#control-writes).

A slider for a percent. `Load` of the pump has the unit percent and no range of its own, so it runs from 0 to 1, which the slider shows as 0 to 100. A higher load makes the motor warmer, which the list below the slider shows:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.Slider Path=../Pump/Load
:: HomeBlaze.Components.ValueList
::: Value Path=../Pump/Load
::: Value Path=../Pump/Temperature
::
~~~~

:: HomeBlaze.Components.Slider Path=../Pump/Load
:: HomeBlaze.Components.ValueList
::: Value Path=../Pump/Load
::: Value Path=../Pump/Temperature
::

A slider with a range and a step of its own. They are in the unit of the property, so `Minimum=0.2` is 20 on the slider and `Step=0.1` moves it by 10:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.Slider Path=../Fan/Load Label="Fan load" Minimum=0.2 Maximum=0.8 Step=0.1
::
~~~~

:: HomeBlaze.Components.Slider Path=../Fan/Load Label="Fan load" Minimum=0.2 Maximum=0.8 Step=0.1
::

## Behavior and Limits

- **The range**: [Ranges](basics.md#ranges) has where the ends of the range come from and the messages for a missing or an empty range.
- **Percent**: A property with the unit percent is shown and entered as 0 to 100 and written as 0 to 1. `Minimum`, `Maximum` and `Step` stay in the unit of the property.
- **Writes on release**: Dragging the thumb writes once, when it is released. Each press of an arrow key writes once. While the thumb is dragged, the text next to the label shows the value under it.
- **Rounding**: The value is rounded to the nearest step from `Minimum` and written in the type of the property. For a property that holds whole numbers the value is then rounded to a whole number, half away from zero, and kept between the whole numbers inside the range, so `Minimum=0.5 Maximum=10.5` writes 1 to 10. A value that the type cannot hold is not written and shows a message.
- **The step**: A `Step` of 0 or less shows `Step must be above 0`, and a `Step` with a fraction for a property that holds whole numbers shows `Step must be a whole number for a whole number property`, in place of the slider, see [Value Widget Errors](../page-errors.md#value-widget-errors).
- **A missing value**: A value that is not set shows the thumb at `Minimum` and a dash as text. Releasing the thumb there writes nothing; to write `Minimum`, move the thumb away and back.
- **Disabled**: Besides the cases of [Control Writes](basics.md#control-writes), the slider is disabled when the path leads nowhere or the property holds no number. Such a slider needs no range, and for a path that leads nowhere it shows a dash as text, see [Ranges](basics.md#ranges).
- **Saving**: For example `Path=motor/TargetSpeed` on a page of your own takes the range 0 to 3000 from the attributes of the motor and saves the motor as [Control Writes](basics.md#control-writes) describes. `Load` of the sample motors is no configuration property, so these samples save nothing.

## Related

- [Basics](basics.md) - Paths, writes, what they save, and who can operate a control
- [Overview](overview.md) - All widgets by group
- [Control](control.md) - Chooses the control that fits its target
