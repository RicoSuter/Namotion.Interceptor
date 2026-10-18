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
| `Path` | Path (Boolean) | None | The [path](basics.md#paths) of the property whose value the switch shows. Without `On` and `Off` the switch writes this property |
| `Label` | Text | From the property, see [Labels](basics.md#labels) | The label next to the switch |
| `On` | Path (Operation) | None | The operation that turns the switch on. Set it together with `Off` |
| `Off` | Path (Operation) | None | The operation that turns the switch off. Set it together with `On` |
| `Kind` | `Switch` or `Tile` | `Switch` | `Switch` is a toggle switch with its label next to it. `Tile` is a tile that fills its place and is filled by the state, see [Tiles](#tiles) |
| `Map` | Name or path of a `ValueMap` | None | The map that gives a state its color, icon and text, see [ValueMap](value-map.md). A tile is filled with the color of the matched case, a toggle switch takes it as its color |
| `Subtitle` | Text | None | A second line below the label of a tile. Without it, a tile with a map shows the text of the value |
| `Icon` | Text | None | The [icon](basics.md#icons) of a tile. The icon of a matched case replaces it |

## Slots

| Slot | Blocks | Holds |
|------|--------|-------|
| `Badge` | One | Content over the top right corner of a tile, for example an [Indicator](indicator.md) with `Kind=Icon`. A click on it reaches the tile. Only a tile shows it |

## Samples

These samples operate the shared sample motors `Pump` and `Fan` next to this page. What they change lasts until the motors restart, see [Control Writes](basics.md#control-writes).

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

## Tiles

With `Kind=Tile` the switch is a tile in place of a toggle switch: the icon, the label and a subtitle on a filled box that toggles the state with one click. A map gives each state its color, icon and text. The tile below stands in a square cell of a [GridLayout](grid-layout.md) and shows the fan. It is filled with the color of the matched case, and the badge shows whether the fan has reached its target speed:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.ValueMap#tilepower
::: Case Value=True Text="Running" Color=Warning Icon=Bolt
::: Case Value=False Text="Off" Icon=PowerOff
:: HomeBlaze.Components.GridLayout#tilegrid Rows=1 Columns=3 CellAspectRatio=1:1
::: Cell Row=0 Column=0
:::: HomeBlaze.Components.Switch Kind=Tile Path=../Fan/IsPowered On=../Fan/PowerOn Off=../Fan/PowerOff Map=tilepower Label="Fan"
::::: Badge
:::::: HomeBlaze.Components.Indicator Path=../Fan/IsAtTargetSpeed Kind=Icon Icon=Speed
::
~~~~

:: HomeBlaze.Components.ValueMap#tilepower
::: Case Value=True Text="Running" Color=Warning Icon=Bolt
::: Case Value=False Text="Off" Icon=PowerOff
:: HomeBlaze.Components.GridLayout#tilegrid Rows=1 Columns=3 CellAspectRatio=1:1
::: Cell Row=0 Column=0
:::: HomeBlaze.Components.Switch Kind=Tile Path=../Fan/IsPowered On=../Fan/PowerOn Off=../Fan/PowerOff Map=tilepower Label="Fan"
::::: Badge
:::::: HomeBlaze.Components.Indicator Path=../Fan/IsAtTargetSpeed Kind=Icon Icon=Speed
::

Rules of a tile:

- **Fills its place**: A tile fills a grid cell, a canvas node or a flow row like a [Card](card.md) when it is their only content. Alone on a page it is as wide as its place and 96 pixels high, see [Spacing](basics.md#spacing).
- **Size of the content**: In a grid cell or a canvas node the icon and the text grow with a large place, and a place that is too low for the icon leaves it out, see [Spacing](basics.md#spacing).
- **One click target**: The whole tile toggles the state, also where the badge stands. A click on the badge reaches the tile.
- **Room for the badge**: The content of a tile with a badge starts below the badge, so the label never runs under it. A badge keeps its own size, such as that of an indicator, while the icon and the text grow with the place. Such a tile leaves out its icon in a slightly higher place than one without a badge, and a text that does not fit runs out at the bottom.
- **The fill**: A tile takes the color of the matched case of its map. Without a map, or without a matching case, it has the primary color while the value is true and the color of the surface while it is false. A matched case without a `Color` stays on the surface.
- **The text**: The subtitle is `Subtitle`. Without it, a tile with a map shows the text of the matched case, else the formatted value.
- **A missing value**: A value that is not set shows a `?` in place of the icon. The tile is disabled only when there is nothing to write or invoke, such as a path that leads nowhere. A writable `bool?` without a value, or a switch whose `On` operation is enabled, can still be turned on.
- **Disabled**: A tile is disabled while the page is edited, while a toggle is pending, and when the write or the operation is not possible, for the same reasons as the toggle switch. A disabled tile keeps the fill of its state and is dimmed, its icon and text more than its fill, so a light that is on stays yellow while it cannot be switched. A progress circle at the bottom right shows a toggle that is pending, and a pending tile shows the state it goes to (fill, icon and subtitle) until the operation ends.
- **Name**: The accessible name of a tile is its label.

## Behavior and Limits

- **Shows the property**: The switch is on while the value is true. A value that is not set and a path that leads nowhere show the switch off.
- **Writes the property**: Without `On` and `Off`, a toggle writes true or false to the property. Besides the cases of [Control Writes](basics.md#control-writes), the switch is disabled when the property holds no boolean.
- **Invokes an operation**: With `On` and `Off`, turning the switch on invokes `On` and turning it off invokes `Off`, and the switch does not write the property. The switch shows the state it goes to until the operation ends, and then shows the property again. A device that reports its new state later moves the switch when it does.
- **Enabled state**: With `On` and `Off`, the switch is disabled while the operation that a toggle would invoke is not enabled, for example `PowerOn` while the motor is powered, and while that operation does not exist.
- **Confirmation**: An operation that asks for confirmation shows its dialog first. Cancel leaves the switch in its state.
- **Toggle switch with a map**: A `Switch` with `Map` and without `Kind=Tile` takes the color of the matched case as its color, and `Primary` when no case matches. Subtitle, icon and badge are for tiles.
- **Operations without input**: `On` and `Off` are operations whose parameters need no input. One of them alone shows `Set both On and Off, or neither`, and an operation that needs input shows `On needs an operation without input` with its path, in place of the switch, see [Value Widget Errors](../page-errors.md#value-widget-errors).

## Related

- [Basics](basics.md) - Paths, writes, what they save, and who can operate a control
- [Overview](overview.md) - All widgets by group
- [Control](control.md) - Chooses the control that fits its target
