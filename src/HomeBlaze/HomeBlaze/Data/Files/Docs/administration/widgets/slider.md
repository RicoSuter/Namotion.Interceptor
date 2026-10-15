---
title: Slider
navTitle: Slider
position: 41
---

# Slider

Sets a number by moving a thumb between a minimum and a maximum. Type name: `HomeBlaze.Components.Slider`.

## Arguments

| Argument | Kind of value | Default | Meaning |
|----------|---------------|---------|---------|
| `Path` | Path (Number, writable) | None | The path of the property whose value the slider shows and writes |
| `Label` | Text | The title of the property | The label above the slider. Without it the slider shows the title that the property declares, and the name of the property when it declares none |
| `Minimum` | Number with a fraction | The `Minimum` attribute of the property, otherwise `0` for a percent | The value at the left end, in the unit of the property |
| `Maximum` | Number with a fraction | The `Maximum` attribute of the property, otherwise `1` for a percent | The value at the right end, in the unit of the property |
| `Step` | Number with a fraction | `1` for a property that holds whole numbers, otherwise a hundredth of the range | The distance between two positions, in the unit of the property |

## Slots

The slider has no slots.

## Samples

These samples operate the shared sample motors `Pump` and `Fan` next to this page. What they change lasts until the motors restart, see [Basics](basics.md).

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

## Behaviour and Limits

- **The range**: `Minimum` and `Maximum` of the block come first. Without them the slider takes the attributes `Minimum` and `Maximum` of the property when they are numbers, and a property with the unit percent runs from 0 to 1. Without any of these the slider shows `Needs a range: set Minimum and Maximum` in its place, see [Value Widget Errors](../page-errors.md#value-widget-errors). A `Maximum` that is not above `Minimum` shows `Maximum must be above Minimum` with both ends.
- **Percent**: A property with the unit percent is shown and entered as 0 to 100 and written as 0 to 1. `Minimum`, `Maximum` and `Step` stay in the unit of the property.
- **Writes on release**: Dragging the thumb writes once, when it is released. Each press of an arrow key writes once. While the thumb is dragged, the text next to the label shows the value under it.
- **Rounding**: The value is rounded to the nearest step from `Minimum` and written in the type of the property. For a property that holds whole numbers the value is then rounded to a whole number, half away from zero, and kept between the whole numbers inside the range, so `Minimum=0.5 Maximum=10.5` writes 1 to 10. A value that the type cannot hold is not written and shows a message.
- **The step**: A `Step` of 0 or less, and a `Step` with a fraction for a property that holds whole numbers, show a message in place of the slider.
- **A missing value**: A value that is not set shows the thumb at `Minimum` and a dash as text. Releasing the thumb there writes nothing; to write `Minimum`, move the thumb away and back.
- **Disabled**: The slider is disabled when the property cannot be written or holds no number, when the path leads nowhere and the block sets `Minimum` and `Maximum` (without them the slider shows `Needs a range` instead), and while the page is edited.
- **Saving**: A write to a configuration property saves the page or the file that holds its subject, see [Basics](basics.md#control-writes). For example `Path=motor/TargetSpeed` on a page of your own takes the range 0 to 3000 from the attributes of the motor and saves the motor, one second after the last move, so a series of arrow keys gives one save. `Load` of the sample motors is no configuration property, so these samples save nothing.
- **Several viewers**: A change by another viewer, or by the device, moves the thumb.

## Related

- [Basics](basics.md) - Paths, writes, what they save, and who can operate a control
- [Overview](overview.md) - All widgets by group
- [Control](control.md) - Chooses the control that fits its target
