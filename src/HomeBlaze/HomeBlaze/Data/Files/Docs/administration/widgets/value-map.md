---
title: ValueMap
navTitle: ValueMap
position: 30
---

# ValueMap

Says what text, colour and icon a value gets in the widgets that refer to it. It shows nothing itself. Type name: `HomeBlaze.Components.ValueMap`. Slot blocks: `Case`.

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
| `Color` | Colour | `Default` | The [colour](basics.md#colours) of the value |
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

## Behaviour and Limits

- **How a widget refers to a map**: With `Map=motorstate`, the name of the block, on the same page. The pages of the other widgets refer to the map of this page as `Map=../value-map.md/motorstate`: `..` is the folder of the page, `value-map.md` the page in it and `motorstate` the name of the block. A map on another page is reached through that page. For a page `Shared.md` in the folder `Demo`, which you have made yourself and which has a map named `motorstate`, that is `Map=/Demo/Shared.md/motorstate`. This works for a map that stands directly on that page, see [Paths on Markdown Pages](../paths.md#paths-on-markdown-pages).
- **Which case is used**: A case matches when its `Value` equals the raw text of the value, without regard to upper and lower case. The raw text is what the format `raw` of an expression shows: `True` and `False` for a boolean, the name for an enum value, a number with a dot as the decimal separator. The first case that matches is used.
- **No case**: A value without a case keeps the formatted text, the default colour and the icon of the widget. A case without `Text` keeps the formatted text of the value.
- **Colour and icon**: The colour of the case is the colour of the value in a row and in a tile, and the colour of an indicator. The icon of the case replaces the icon of the row, the tile or the indicator. A `Progress` has no map.
- **A map that cannot be resolved**: A `Map` that leads nowhere, or to a subject that is no `ValueMap`, shows `Cannot resolve map: motorstat` in a small red alert in place of the value, with the name as written. This is a mistake on the page, see [Value Widget Errors](../page-errors.md#value-widget-errors). The rest of the page renders.

## Related

- [Basics](basics.md) - Type names, paths, value widgets, colours and icons
- [Overview](overview.md) - All widgets by group
- [ValueList](value-list.md) - Rows with a label and a value
- [ValueTile](value-tile.md) - A small box with a label and one large value
- [Indicator](indicator.md) - A state as a coloured chip or as an icon
- [Select](select.md) - Picks a value of an enum, with the texts of a map
