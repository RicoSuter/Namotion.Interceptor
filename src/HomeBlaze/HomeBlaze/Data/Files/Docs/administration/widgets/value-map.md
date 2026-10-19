---
title: ValueMap
navTitle: ValueMap
position: 30
---

# ValueMap

Says what text, color and icon a value gets in the widgets that refer to it. It shows nothing itself. Type name: `HomeBlaze.Components.ValueMap`. Slot blocks: `Case`.

## Arguments

A `ValueMap` has no arguments.

## Slots

| Slot | Blocks | Holds |
|------|--------|-------|
| `Case` | Any number | One case per block. It holds no content |

The arguments of a `Case` block:

| Argument | Kind of value | Default | Meaning |
|----------|---------------|---------|---------|
| `Value` | Text | None | The value that the case is for, as raw text, see below |
| `Text` | Text | None | The text to show. Without it the formatted text of the value stays |
| `Color` | Color | `Default` | The [color](basics.md#colors) of the value: a theme color such as `Success`, or a CSS color such as `"#ffffff80"` |
| `Icon` | Text | None | The [icon](basics.md#icons) to show. It replaces the icon of the widget |

## Samples

A map for the status of a motor. The cases are the values of `MotorStatus`. The block below the source shows nothing, because a map is only read by other widgets. The other widget pages refer to it from their folder as `Map=../value-map.md/motorstate`:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.ValueMap#motorstate
::: Case Value=Stopped Text="Off" Icon=StopCircle
::: Case Value=Starting Text="Starting up" Color=Warning Icon=PlayCircle
::: Case Value=Running Text="Running" Color=Success Icon=CheckCircle
::: Case Value=Stopping Text="Shutting down" Color=Warning Icon=PauseCircle
::: Case Value=Error Text="Fault" Color=Error Icon=Error
::
~~~~

:: HomeBlaze.Components.ValueMap#motorstate
::: Case Value=Stopped Text="Off" Icon=StopCircle
::: Case Value=Starting Text="Starting up" Color=Warning Icon=PlayCircle
::: Case Value=Running Text="Running" Color=Success Icon=CheckCircle
::: Case Value=Stopping Text="Shutting down" Color=Warning Icon=PauseCircle
::: Case Value=Error Text="Fault" Color=Error Icon=Error
::

A map with CSS colors for a badge on a tile that is always filled. The switch tile of the fan is filled with the warning color while the fan runs and with a dark CSS color while it is off. The speed icon in its badge is white while the fan is at its target speed and half transparent white otherwise:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.ValueMap#fanfill
::: Case Value=True Text="Running" Color=Warning Icon=Bolt
::: Case Value=False Text="Off" Color="#37474f" Icon=PowerOff
::

:: HomeBlaze.Components.ValueMap#speedbadge
::: Case Value=True Text="At target speed" Color="#ffffff"
::: Case Value=False Text="Below target speed" Color="#ffffff80"
::

:: HomeBlaze.Components.GridLayout#cssgrid Rows=1 Columns=3 CellAspectRatio=1:1
::: Cell Row=0 Column=0
:::: HomeBlaze.Components.Switch Kind=Tile Path=../Fan/IsPowered On=../Fan/PowerOn Off=../Fan/PowerOff Map=fanfill Label="Fan"
::::: Badge
:::::: HomeBlaze.Components.Indicator Path=../Fan/IsAtTargetSpeed Map=speedbadge Kind=Icon Icon=Speed
::
~~~~

:: HomeBlaze.Components.ValueMap#fanfill
::: Case Value=True Text="Running" Color=Warning Icon=Bolt
::: Case Value=False Text="Off" Color="#37474f" Icon=PowerOff
::

:: HomeBlaze.Components.ValueMap#speedbadge
::: Case Value=True Text="At target speed" Color="#ffffff"
::: Case Value=False Text="Below target speed" Color="#ffffff80"
::

:: HomeBlaze.Components.GridLayout#cssgrid Rows=1 Columns=3 CellAspectRatio=1:1
::: Cell Row=0 Column=0
:::: HomeBlaze.Components.Switch Kind=Tile Path=../Fan/IsPowered On=../Fan/PowerOn Off=../Fan/PowerOff Map=fanfill Label="Fan"
::::: Badge
:::::: HomeBlaze.Components.Indicator Path=../Fan/IsAtTargetSpeed Map=speedbadge Kind=Icon Icon=Speed
::

## Behavior and Limits

- **How a widget refers to a map**: With `Map=motorstate`, the name of the block, on the same page. The pages of the other widgets refer to the map of this page as `Map=../value-map.md/motorstate`: `..` is the folder of the page, `value-map.md` the page in it and `motorstate` the name of the block. A map on another page is reached through that page. For a page `Shared.md` in the folder `Demo`, which you have made yourself and which has a map named `motorstate`, that is `Map=/Demo/Shared.md/motorstate`. This works for a map that stands directly on that page, see [Paths on Markdown Pages](../paths.md#paths-on-markdown-pages).
- **Which case is used**: A case matches when its `Value` equals the raw text of the value, without regard to upper and lower case. The raw text is what the format `raw` of an expression shows: `True` and `False` for a boolean, the name for an enum value, a number with a dot as the decimal separator. The first case that matches is used.
- **No case**: A value without a case keeps the formatted text, the default color and the icon of the widget. A case without `Text` keeps the formatted text of the value.
- **Color and icon**: What a case changes depends on the widget, see its page. In a row of a `ValueList` and in a `ValueTile` the case gives the value its text and color and replaces the icon of the row or the tile. An `Indicator` takes text, color and icon. A `Gauge` takes the text in the center and the color of the ring. A `Select` shows the text and the icon of the case for each value, the icon in the color of the case. A column of a `SubjectTable` gives its cells the text and color. A `Control` hands its map to the select, the indicator or the value row that it chooses.
- **CSS colors**: A [CSS color](basics.md#colors) does what a theme color does in the same place. It colors the value text of a `ValueList` row, of a `ValueTile` box and of a `SubjectTable` cell, the icon of an `Indicator` with `Kind=Icon`, the icon of a value in a `Select`, the ring of a `Gauge` and a toggle `Switch`. It fills the chip of an `Indicator`, a `ValueTile` with `Kind=Tile`, a `Switch` tile and a `DoorLock` tile, whose badge takes the text color of the fill. The text on a fill is black or white, whichever stands out more from the color, without regard to its transparency. On a fill with a `var()` color the text has the text color of the theme.
- **A color that is no color**: A `Color` that is neither a theme color nor a CSS color of a form that [Colors](basics.md#colors) lists shows `Unknown color 'purple': use a theme color like Success or a CSS color like #ffffff80` in a small red alert in place of every widget that refers to the map, with the color as written, also while no value matches that case. See [Value Widget Errors](../page-errors.md#value-widget-errors).
- **A map that cannot be resolved**: A `Map` that leads nowhere, or to a subject that is no `ValueMap`, shows `Cannot resolve map: motorstat` in a small red alert in place of the widget, with the name as written. A row of a `ValueList` shows it in place of its value, and a `SubjectTable` shows it once per such column in place of the table. This is a mistake on the page, see [Value Widget Errors](../page-errors.md#value-widget-errors). The rest of the page renders.

## Related

- [Basics](basics.md) - Type names, paths, value widgets, colors and icons
- [Overview](overview.md) - All widgets by group
- [ValueList](value-list.md) - Rows with a label and a value
- [ValueTile](value-tile.md) - A small box with a label and one large value
- [Indicator](indicator.md) - A state as a colored chip or as an icon
- [Select](select.md) - Picks a value of an enum, with the texts of a map
