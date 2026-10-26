---
title: ValueTile
navTitle: ValueTile
---

# ValueTile

A box or a tile with a label and one large value. Type name: `HomeBlaze.Components.ValueTile`.

## Arguments

| Argument | Kind of value | Default | Meaning |
|----------|---------------|---------|---------|
| `Path` | Path (Value) | None | The [path](basics.md#paths) of the property whose value the tile shows, or with a `Map` of a subject whose states the cases read, see [ValueMap](value-map.md#conditions) |
| `Label` | Text with values | From the property, see [Labels](basics.md#labels) | The small text above the value of a box and below the value of a tile, see [Text with Values](basics.md#text-with-values) |
| `Subtitle` | Text with values | None | A second, smaller line for what the tile stands for, such as the device that the value comes from: below the label of a box, and below the label as a third line of a tile. It never replaces the text of a matched case, which is the value, see [Text with Values](basics.md#text-with-values) |
| `Outlined` | `true` or `false` | `false` | A thin border around the tile, as an outlined [Card](card.md) has, whatever its fill, see [Tile Borders](basics.md#tile-borders) |
| `Icon` | Text | None | The [icon](basics.md#icons) next to the label of a box and above the value of a tile. The icon of a matched case replaces it |
| `Map` | Name or path of a `ValueMap` | None | The map that gives the value its text, color and icon, see [ValueMap](value-map.md). A box takes the color of the matched case for the value, a tile is filled with it |
| `Kind` | `Box` or `Tile` | `Box` | `Box` is a raised box with the label above the value. `Tile` is a tile that fills its place, with the value as its large line, see [Tiles](#tiles) |

## Slots

A `ValueTile` has no slots.

## Samples

Three tiles in a `FlowLayout`. The first has a label of its own and an icon, the second takes its label from the property, and the third shows the status through the map of [ValueMap](value-map.md):

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.FlowLayout MinWidth=140
::: Item
:::: HomeBlaze.Components.ValueTile Path=../Pump/CurrentSpeed Label="Pump speed" Icon=Speed
::: Item
:::: HomeBlaze.Components.ValueTile Path=../Pump/Temperature
::: Item
:::: HomeBlaze.Components.ValueTile Path=../Pump/Status Map=../value-map.md/motorstate
::
~~~~

:: HomeBlaze.Components.FlowLayout MinWidth=140
::: Item
:::: HomeBlaze.Components.ValueTile Path=../Pump/CurrentSpeed Label="Pump speed" Icon=Speed
::: Item
:::: HomeBlaze.Components.ValueTile Path=../Pump/Temperature
::: Item
:::: HomeBlaze.Components.ValueTile Path=../Pump/Status Map=../value-map.md/motorstate
::

## Tiles

With `Kind=Tile` the value stands on a tile like the tiles of a [Switch](switch.md#tiles) and an [OperationButton](operation-button.md#tiles), so that a value fits into a wall of tiles. From top to bottom the tile shows the icon, the value as its large line and the label as its small line. The tiles below stand in square cells of a [GridLayout](grid-layout.md). The first shows the speed of the pump, the second its status through the map of [ValueMap](value-map.md), and is filled with the color of the matched case. The third shows the target speed of the pump in its label.

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.GridLayout Rows=1 Columns=3 CellAspectRatio=1:1
::: Cell Row=0 Column=0
:::: HomeBlaze.Components.ValueTile Kind=Tile Path=../Pump/CurrentSpeed Label="Pump speed" Icon=Speed
::: Cell Row=0 Column=1
:::: HomeBlaze.Components.ValueTile Kind=Tile Path=../Pump/Status Map=../value-map.md/motorstate Label="Pump"
::: Cell Row=0 Column=2
:::: HomeBlaze.Components.ValueTile Kind=Tile Path=../Pump/CurrentSpeed Label="Target {{ subject/TargetSpeed }}" Icon=Speed
::
~~~~

:: HomeBlaze.Components.GridLayout Rows=1 Columns=3 CellAspectRatio=1:1
::: Cell Row=0 Column=0
:::: HomeBlaze.Components.ValueTile Kind=Tile Path=../Pump/CurrentSpeed Label="Pump speed" Icon=Speed
::: Cell Row=0 Column=1
:::: HomeBlaze.Components.ValueTile Kind=Tile Path=../Pump/Status Map=../value-map.md/motorstate Label="Pump"
::: Cell Row=0 Column=2
:::: HomeBlaze.Components.ValueTile Kind=Tile Path=../Pump/CurrentSpeed Label="Target {{ subject/TargetSpeed }}" Icon=Speed
::

Rules of a tile:

- **Fills its place**: A tile fills a grid cell, a canvas node or a flow row like a [Card](card.md) when it is their only content, and draws no frame of its own there. Alone on a page it is as wide as its place and 96 pixels high, see [Spacing](basics.md#spacing).
- **Size of the content**: In a grid cell or a canvas node the icon and the text grow with a large place, and a place that is too low for the icon leaves it out, see [Spacing](basics.md#spacing).
- **The fill**: A tile takes the color of the matched case of its map, and its text the color that goes with it. A [CSS color](value-map.md#behavior-and-limits) fills it with a text color that stands out from it, and the `TextColor` of the case replaces that text color, see [Text colors](value-map.md#behavior-and-limits). Without a map, without a matching case or for a case without a `Color` it stays on the surface.
- **A subtitle**: `Subtitle` adds a third, smaller line below the label, such as `Subtitle="Backup Interface (M1 + M2)"` under a label `House`. A tile lower than 150 pixels leaves out its icon to make room for it.
- **A missing value**: The tile shows the dash of a [missing value](basics.md#missing-values) as its value, with the path as tooltip.
- **A click**: A tile only shows its value. When the property has a [history](basics.md#value-widgets), the whole tile is a button that opens it.

## Behavior and Limits

- **Box and tile**: The default `Kind=Box` is a raised box with the icon and the label on its first line and the value below, in the color of the matched case. It keeps its frame in a cell or a node. A tile has no frame and fills its place, see [Tiles](#tiles).
- **Color and icon of a case**: A matching case gives the value its color, or fills the tile with it, and gives the value its icon, see [ValueMap](value-map.md).
- **History**: The label of a box, and the whole of a tile, open the history of a property that has one, see [Value Widgets](basics.md#value-widgets).
- **Spacing**: A value tile adds no distance around itself and is as wide as its place, see [Spacing](basics.md#spacing). It is meant to stand in a layout such as `FlowLayout`, which does the spacing.

## Related

- [Basics](basics.md) - Type names, paths, value widgets, colors and icons
- [Overview](overview.md) - All widgets by group
- [ValueMap](value-map.md) - The text, color and icon of a value
- [Gauge](gauge.md) - One value in a ring between a minimum and a maximum
