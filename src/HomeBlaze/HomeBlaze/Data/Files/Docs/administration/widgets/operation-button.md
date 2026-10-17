---
title: OperationButton
navTitle: OperationButton
position: 43
---

# OperationButton

A button that invokes an operation or a query of a subject. Type name: `HomeBlaze.Components.OperationButton`.

## Arguments

| Argument | Kind of value | Default | Meaning |
|----------|---------------|---------|---------|
| `Path` | Path (Operation) | None | The [path](basics.md#paths) of the operation or the query, for example `pump/EmergencyStop` |
| `Label` | Text | From the method, see [Labels](basics.md#labels) | The text of the button |
| `Icon` | Text | The icon of the method, else `PlayArrow` for an operation and `Search` for a query | The [icon](basics.md#icons) at the start of the button |
| `Color` | Color | `Primary` | The [color](basics.md#colors) of the button |
| `Variant` | `Filled`, `Outlined` or `Text` | `Filled` | A filled button, a button with a border, or text alone |

## Slots

An `OperationButton` has no slots.

## Samples

These samples operate the shared sample motors `Pump` and `Fan` next to this page. What they change lasts until the motors restart, see [Control Writes](basics.md#control-writes).

An operation that asks for confirmation. `RestartController` of the pump shows a dialog first, and stops the pump only when it is confirmed. `EmergencyStop` asks for no confirmation, because a safety action must run at once:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.OperationButton Path=../Pump/RestartController Color=Error
::
~~~~

:: HomeBlaze.Components.OperationButton Path=../Pump/RestartController Color=Error
::

Two operations that are enabled in turn. `PowerOff` is enabled while the pump is powered and `PowerOn` while it is not, so one of the two buttons is always disabled:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.OperationButton Path=../Pump/PowerOff
:: HomeBlaze.Components.OperationButton Path=../Pump/PowerOn
::
~~~~

:: HomeBlaze.Components.OperationButton Path=../Pump/PowerOff
:: HomeBlaze.Components.OperationButton Path=../Pump/PowerOn
::

A query with a label, an icon and a variant of its own. `GetDiagnostics` changes nothing and shows its result in a dialog:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.OperationButton Path=../Fan/GetDiagnostics Label="Fan diagnostics" Icon=Info Color=Info Variant=Outlined
::
~~~~

:: HomeBlaze.Components.OperationButton Path=../Fan/GetDiagnostics Label="Fan diagnostics" Icon=Info Color=Info Variant=Outlined
::

## Behavior and Limits

- **As in the property panel**: The button invokes the method the same way the property panel of a subject does. A method with parameters shows a dialog for them, a method that asks for confirmation shows the confirmation dialog, a result is shown in a dialog, and a failure shows an error dialog, also a request to a device that timed out.
- **Operations and queries**: Both work. A query shows its result.
- **Running**: While the method runs, the button is disabled and shows a spinner in place of its icon, so a second click has to wait for the first to end.
- **Enabled state**: The button is disabled while the method is not enabled, and when the path leads nowhere. A path that leads nowhere is also the tooltip of the button.
- **Leaving the page**: An operation that has started runs to its end when the page is left or reloaded. Its result dialog is not shown then.

## Related

- [Basics](basics.md) - Paths, writes, what they save, and who can operate a control
- [Overview](overview.md) - All widgets by group
- [Switch](switch.md) - Invokes an operation per state of a boolean
- [Control](control.md) - Chooses the control that fits its target
