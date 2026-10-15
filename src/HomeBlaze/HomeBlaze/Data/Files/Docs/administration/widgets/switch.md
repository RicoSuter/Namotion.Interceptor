---
title: Switch
navTitle: Switch
position: 40
---

# Switch

Turns a boolean on and off. It writes the property, or it invokes one operation for each state. Type name: `HomeBlaze.Components.Switch`.

## Arguments

| Argument | Kind of value | Default | Meaning |
|----------|---------------|---------|---------|
| `Path` | Path (Boolean) | None | The path of the property whose value the switch shows. Without `On` and `Off` the switch writes this property |
| `Label` | Text | The title of the property | The label next to the switch. Without it the switch shows the title that the property declares, and the name of the property when it declares none |
| `On` | Path (Operation) | None | The operation that turns the switch on. Set it together with `Off` |
| `Off` | Path (Operation) | None | The operation that turns the switch off. Set it together with `On` |

## Slots

The switch has no slots.

## Samples

These samples operate the shared sample motors `Pump` and `Fan` next to this page. What they change lasts until the motors restart, see [Basics](basics.md).

A switch that writes the property. `IsPowered` of the pump can be written, so the switch writes it. While the pump is off it slows down to a stop, which the indicator below the switch shows:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.Switch Path=../Pump/IsPowered
:: HomeBlaze.Components.Indicator Path=../Pump/Status
::
~~~~

:: HomeBlaze.Components.Switch Path=../Pump/IsPowered
:: HomeBlaze.Components.Indicator Path=../Pump/Status
::

A switch with an operation for each state. Turning it on invokes `PowerOn` of the fan, and turning it off invokes `PowerOff`. The label replaces the title of the property:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.Switch Path=../Fan/IsPowered Label="Fan" On=../Fan/PowerOn Off=../Fan/PowerOff
::
~~~~

:: HomeBlaze.Components.Switch Path=../Fan/IsPowered Label="Fan" On=../Fan/PowerOn Off=../Fan/PowerOff
::

## Behaviour and Limits

- **Shows the property**: The switch is on while the value is true. A value that is not set and a path that leads nowhere show the switch off.
- **Writes the property**: Without `On` and `Off`, a toggle writes true or false to the property. The switch is disabled when the property cannot be written, because it has no public setter or holds no boolean, and when the path leads nowhere.
- **Invokes an operation**: With `On` and `Off`, turning the switch on invokes `On` and turning it off invokes `Off`, and the switch does not write the property. The switch shows the state it goes to until the operation ends, and then shows the property again. A device that reports its new state later moves the switch when it does.
- **Enabled state**: With `On` and `Off`, the switch is disabled while the operation that a toggle would invoke is not enabled, for example `PowerOn` while the motor is powered, and while that operation does not exist.
- **Confirmation**: An operation that asks for confirmation shows its dialog first. Cancel leaves the switch in its state.
- **Operations without input**: `On` and `Off` are operations whose parameters need no input. One of them alone shows `Set both On and Off, or neither`, and an operation that needs input shows `On needs an operation without input` with its path, in place of the switch, see [Value Widget Errors](../page-errors.md#value-widget-errors).
- **Saving**: A write to a configuration property saves the page or the file that holds its subject, see [Basics](basics.md). `IsPowered` of the sample motors is no configuration property, so these samples save nothing.
- **Edit mode**: While the page is edited, the switch is disabled.
- **Several viewers**: A change by another viewer, or by the device, moves the switch.

## Related

- [Basics](basics.md) - Paths, writes, what they save, and who can operate a control
- [Overview](overview.md) - All widgets by group
