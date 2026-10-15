---
title: OperationButton
navTitle: Operation Button
position: 43
---

# OperationButton

A button that invokes an operation or a query of a subject. Type name: `HomeBlaze.Components.OperationButton`.

## Arguments

| Argument | Kind of value | Default | Meaning |
|----------|---------------|---------|---------|
| `Path` | Path (Operation) | None | The path of the operation or the query, for example `pump/EmergencyStop` |
| `Label` | Text | The title of the method | The text of the button |
| `Icon` | Text | The icon of the method | The icon at the start of the button, the name of a MudBlazor Material icon in its filled form |
| `Color` | Colour | `Primary` | The colour of the button |
| `Variant` | `Filled`, `Outlined` or `Text` | `Filled` | A filled button, a button with a border, or text alone |

## Slots

The button has no slots.

## Samples

These samples operate the shared sample motors `Pump` and `Fan` next to this page. What they change lasts until the motors restart, see [Basics](basics.md).

An operation that asks for confirmation. `EmergencyStop` of the pump shows a dialog first, and stops the pump only when it is confirmed:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.OperationButton Path=../Pump/EmergencyStop Color=Error
::
~~~~

:: HomeBlaze.Components.OperationButton Path=../Pump/EmergencyStop Color=Error
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

## Behaviour and Limits

- **As in the property panel**: The button invokes the method the same way the property panel of a subject does. A method with parameters shows a dialog for them, a method that asks for confirmation shows the confirmation dialog, a result is shown in a dialog, and a failure shows an error dialog.
- **Operations and queries**: Both work. A query shows its result.
- **Running**: While the method runs, the button is disabled and shows a spinner in place of its icon, so a second click has to wait for the first to end.
- **Enabled state**: The button is disabled while the method is not enabled, and when the path leads nowhere. Then the button shows the path as text unless `Label` is set, and the path as tooltip.
- **Leaving the page**: An operation that has started runs to its end when the page is left or reloaded. Its result dialog is not shown then.
- **Who can operate**: Every viewer of the page can press the button, see [Basics](basics.md#control-writes).
- **Edit mode**: While the page is edited, the button is disabled.

## Related

- [Basics](basics.md) - Paths, writes, what they save, and who can operate a control
- [Overview](overview.md) - All widgets by group
- [Switch](switch.md) - Invokes an operation per state of a boolean
