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
| `Variant` | `Filled`, `Outlined` or `Text` | `Filled` | A filled button, a button with a border, or text alone. A tile takes its fill from `Color` and `Variant`, see [Tiles](#tiles) |
| `Kind` | `Button` or `Tile` | `Button` | `Button` is a button. `Tile` is a tile that fills its place, see [Tiles](#tiles) |
| `Subtitle` | Text | None | A second line below the label of a tile |

## Slots

| Slot | Blocks | Holds |
|------|--------|-------|
| `Badge` | One | Content over the top right corner of a tile, for example an [Indicator](indicator.md) with `Kind=Icon`. A click on it reaches the tile. Only a tile shows it |

## Samples

These samples operate the shared sample motors `Pump` and `Fan` next to this page. What they change lasts until the motors restart, see [Control Writes](basics.md#control-writes).

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

## Tiles

With `Kind=Tile` the operation is a tile in place of a button. The two tiles below stand in square cells of a [GridLayout](grid-layout.md) and show only the icon of their operation, because they have no label:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.GridLayout#buttongrid Rows=1 Columns=3 CellAspectRatio=1:1
::: Cell Row=0 Column=0
:::: HomeBlaze.Components.OperationButton Kind=Tile Path=../Pump/PowerOn Color=Default
::: Cell Row=0 Column=1
:::: HomeBlaze.Components.OperationButton Kind=Tile Path=../Pump/PowerOff Color=Default
::
~~~~

:: HomeBlaze.Components.GridLayout#buttongrid Rows=1 Columns=3 CellAspectRatio=1:1
::: Cell Row=0 Column=0
:::: HomeBlaze.Components.OperationButton Kind=Tile Path=../Pump/PowerOn Color=Default
::: Cell Row=0 Column=1
:::: HomeBlaze.Components.OperationButton Kind=Tile Path=../Pump/PowerOff Color=Default
::

Rules of a tile:

- **Fills its place**: A tile fills a grid cell, a canvas node or a flow row like a [Card](card.md) when it is their only content. Alone on a page it is as wide as its place and 96 pixels high, see [Spacing](basics.md#spacing).
- **Without a label**: A tile without `Label` shows the icon of the operation alone, and its accessible name is the title of the operation. A path that leads to no operation shows the path in place of the label, so the tile is never empty. With a `Label` it shows the label, and `Subtitle` below it.
- **The fill**: `Color` and `Variant` give the fill. `Filled` fills the tile with the color, `Outlined` draws a border in the color on the surface, and `Text` shows colored text on the surface.
- **Disabled**: A tile is disabled while its operation is not enabled or is running, as the button is, and is then dimmed with its fill kept. The two tiles above are enabled in turn, because `PowerOn` is enabled only while the pump is off and `PowerOff` only while it is powered. A device can limit its operations in the same way, so that for example the open and close tiles of a cover are disabled at the end position they move to.
- **Badge**: A badge shows over the top right corner of a tile only, and a click on it reaches the tile.

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
