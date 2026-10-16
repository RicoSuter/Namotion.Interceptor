---
title: Control
navTitle: Control
position: 44
---

# Control

Shows the control that fits its target: a button, a switch, an indicator, a select, a slider or a row with the value. Type name: `HomeBlaze.Components.Control`.

## Arguments

| Argument | Kind of value | Default | Meaning |
|----------|---------------|---------|---------|
| `Path` | Path (Value or Operation) | None | The path of the property or the operation that the control is for |
| `Label` | Text | The title of the property or of the method | The label of the control |
| `Minimum` | Number with a fraction | See [Slider](slider.md) | The value at the left end of a slider |
| `Maximum` | Number with a fraction | See [Slider](slider.md) | The value at the right end of a slider |
| `Step` | Number with a fraction | See [Slider](slider.md) | The distance between two positions of a slider |
| `Map` | Name or path of a `ValueMap` | None | The map for a select, an indicator or a value row, see [ValueMap](value-map.md) |

The control passes on the arguments that the chosen widget takes and ignores the others.

| Target | Widget |
|--------|--------|
| An operation or a query | [OperationButton](operation-button.md) |
| A boolean that can be written, also one that can hold no value | [Switch](switch.md) |
| A boolean that cannot be written | A row with the label and an indicator |
| An enum that can be written and whose values do not combine as flags | [Select](select.md) |
| A number that can be written and has a range, from the arguments, from the attributes `Minimum` and `Maximum` or from the unit percent | [Slider](slider.md) |
| Anything else, and a path that leads nowhere | A row with the label and the value |

## Slots

The control has no slots.

## Samples

These samples operate the shared sample motors `Pump` and `Fan` next to this page. What they change lasts until the motors restart, see [Basics](basics.md#control-writes).

One control for each kind of target of the pump, one below the other. `IsPowered` gets a switch, `Mode` a select, `Load` a slider because it is a percent, `IsAtTargetSpeed` an indicator because it cannot be written, `CurrentSpeed` a value row because it has no range, and `EmergencyStop` a button:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.Control Path=../Pump/IsPowered
:: HomeBlaze.Components.Control Path=../Pump/Mode
:: HomeBlaze.Components.Control Path=../Pump/Load
:: HomeBlaze.Components.Control Path=../Pump/IsAtTargetSpeed
:: HomeBlaze.Components.Control Path=../Pump/CurrentSpeed
:: HomeBlaze.Components.Control Path=../Pump/EmergencyStop
::
~~~~

:: HomeBlaze.Components.Control Path=../Pump/IsPowered
:: HomeBlaze.Components.Control Path=../Pump/Mode
:: HomeBlaze.Components.Control Path=../Pump/Load
:: HomeBlaze.Components.Control Path=../Pump/IsAtTargetSpeed
:: HomeBlaze.Components.Control Path=../Pump/CurrentSpeed
:: HomeBlaze.Components.Control Path=../Pump/EmergencyStop
::

A control with a label and a range of its own, which gives the load of the fan a slider from 20 to 80:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.Control Path=../Fan/Load Label="Fan load" Minimum=0.2 Maximum=0.8
::
~~~~

:: HomeBlaze.Components.Control Path=../Fan/Load Label="Fan load" Minimum=0.2 Maximum=0.8
::

## Behaviour and Limits

- **Chosen on every render**: The control decides on its widget each time it renders, so a target that a device adds later gets its widget when it appears.
- **As the widget it renders**: A chosen widget behaves exactly as on its own page, with its rules for writes, saving, confirmation, enabled state and messages, see [Basics](basics.md#control-writes).
- **No range, no slider**: A number without a range gets a value row and no message. Set `Minimum` and `Maximum` for a slider.
- **Flags enums**: An enum whose values combine as flags gets a value row.
- **Edit mode**: While the page is edited, the chosen control is disabled.

## Related

- [Basics](basics.md) - Paths, writes, what they save, and who can operate a control
- [Overview](overview.md) - All widgets by group
- [Switch](switch.md), [Slider](slider.md), [Select](select.md) and [OperationButton](operation-button.md) - The widgets that the control chooses from
