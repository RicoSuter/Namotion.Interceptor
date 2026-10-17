---
title: Card
navTitle: Card
position: 20
---

# Card

A titled box around page content. Type name: `HomeBlaze.Components.Card`. Slot blocks: `HeaderRight`, `Footer`.

## Arguments

| Argument | Kind of value | Default | Meaning |
|----------|---------------|---------|---------|
| `Title` | Text | None | The title at the top of the card |
| `Subtitle` | Text | None | A smaller line below the title |
| `Icon` | Text | None | The [icon](basics.md#icons) next to the title |
| `IconColor` | Color | `Default` | The [color](basics.md#colors) of the icon |
| `Href` | Text | None | Where the title leads to. The title is then a link, written like a link in the text of the page, see [Links in Block Options](../pages.md#links-in-block-options) |
| `Outlined` | Yes or no | `true` | A border around the card instead of a shadow. `false` gives a card with a shadow, see `Elevation` |
| `Elevation` | Whole number | `2` | The depth of the shadow of a card that is not outlined. `0` is no shadow. An outlined card has no shadow, whatever the value is |
| `Image` | Text | None | A picture between the header and the body, 200 pixels high. Written like `Href` |
| `Height` | Whole number | None | The height of the whole card in pixels, above 0. Without it, and without `AspectRatio`, the card is as high as its content. `Height` wins when both are set. A value of 0 or less shows `Height must be above 0` in place of the card, see [Value Widget Errors](../page-errors.md#value-widget-errors) |
| `AspectRatio` | Text | None | The width and the height of the card in the form `W:H`, for example `4:3` or `1:1`, so that the height of the card follows its width. A value in another form shows `AspectRatio must look like 4:3` in place of the card, see [Value Widget Errors](../page-errors.md#value-widget-errors) |

## Slots

| Slot | Blocks | Holds |
|------|--------|-------|
| `Content` | Default slot | The body of the card: any page content |
| `HeaderRight` | One | Content at the right end of the header, for example an [Indicator](indicator.md) |
| `Footer` | One | Content below a divider at the bottom of the card |

## Samples

A card with a title, an icon and a footer. Its body is text with an expression and a widget. The head line of the footer has three colons like the widget, so it ends the widget:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.Card Title="Pump" Icon=WaterDrop
The pump runs at {{ ../Pump/CurrentSpeed }} RPM.

::: HomeBlaze.Components.Widget Path=../Pump

::: Footer
Target speed: {{ ../Pump/TargetSpeed }} RPM
::
~~~~

:: HomeBlaze.Components.Card Title="Pump" Icon=WaterDrop
The pump runs at {{ ../Pump/CurrentSpeed }} RPM.

::: HomeBlaze.Components.Widget Path=../Pump

::: Footer
Target speed: {{ ../Pump/TargetSpeed }} RPM
::

A card without arguments and without a footer. It has a border and no shadow, because a card is outlined unless `Outlined=false` is set:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.Card
This card has no title, no icon and no footer. It is a plain box around its content.
::
~~~~

:: HomeBlaze.Components.Card
This card has no title, no icon and no footer. It is a plain box around its content.
::

A card with a subtitle, a colored icon and a title that leads to the guide for pages. It is not outlined, so it has a shadow, and `Elevation=6` makes the shadow deeper than the default. The text in the corner of the header stands in the `HeaderRight` slot:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.Card Title="Pump" Subtitle="Sample motor" Icon=WaterDrop IconColor=Primary Href="../pages.md" Outlined=false Elevation=6
The pump runs at {{ ../Pump/CurrentSpeed }} RPM.

::: HeaderRight
{{ ../Pump/Status }}
::
~~~~

:: HomeBlaze.Components.Card Title="Pump" Subtitle="Sample motor" Icon=WaterDrop IconColor=Primary Href="../pages.md" Outlined=false Elevation=6
The pump runs at {{ ../Pump/CurrentSpeed }} RPM.

::: HeaderRight
{{ ../Pump/Status }}
::

Three cards in a flow layout with `AspectRatio="4:3"`. Each card is three quarters as high as it is wide, so the three cards are equally high whatever their text is, and they grow and shrink with the width of the window. A body whose text does not fit scrolls:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.FlowLayout
::: Item
:::: HomeBlaze.Components.Card Title="Pump" Icon=WaterDrop AspectRatio="4:3"
The pump runs at {{ ../Pump/CurrentSpeed }} RPM.
::: Item
:::: HomeBlaze.Components.Card Title="Fan" Icon=Air AspectRatio="4:3"
The fan runs at {{ ../Fan/CurrentSpeed }} RPM.
::: Item
:::: HomeBlaze.Components.Card Title="Notes" AspectRatio="4:3"
The pump moves the water and the fan cools the room.

Both are sample motors that run in every installation.

Their speeds change while the page is open, so the values above are live.
::
~~~~

:: HomeBlaze.Components.FlowLayout
::: Item
:::: HomeBlaze.Components.Card Title="Pump" Icon=WaterDrop AspectRatio="4:3"
The pump runs at {{ ../Pump/CurrentSpeed }} RPM.
::: Item
:::: HomeBlaze.Components.Card Title="Fan" Icon=Air AspectRatio="4:3"
The fan runs at {{ ../Fan/CurrentSpeed }} RPM.
::: Item
:::: HomeBlaze.Components.Card Title="Notes" AspectRatio="4:3"
The pump moves the water and the fan cools the room.

Both are sample motors that run in every installation.

Their speeds change while the page is open, so the values above are live.
::

A card that is 200 pixels high. The motor widget in its body is higher than the room that the header and the footer leave, so the body scrolls and the footer stays at the bottom of the card:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.Card Title="Pump" Icon=WaterDrop Height=200
::: HomeBlaze.Components.Widget Path=../Pump

::: Footer
Target speed: {{ ../Pump/TargetSpeed }} RPM
::
~~~~

:: HomeBlaze.Components.Card Title="Pump" Icon=WaterDrop Height=200
::: HomeBlaze.Components.Widget Path=../Pump

::: Footer
Target speed: {{ ../Pump/TargetSpeed }} RPM
::

## Behavior and Limits

- **The header**: The header is shown when the card has a title, a subtitle, an icon or a `HeaderRight`. The icon and the content of `HeaderRight` stand on the line of the title, also when the subtitle takes several lines.
- **A title with a link**: The title of a card with `Href` looks like the title of any card and is underlined when the pointer is on it.
- **Spacing**: [Spacing](basics.md#spacing) has the room inside a card and around it.
- **Width**: A card is as wide as the place it stands in. Put cards into a [FlowLayout](flow-layout.md) to place them next to each other.
- **A fixed height**: A card with a `Height` or an `AspectRatio` keeps the size of its header, its picture and its footer, and its body takes the rest of the height. A body that does not fit scrolls.
- **In a cell or a node**: A card that is the subject of a [GridLayout](grid-layout.md) cell or a [CanvasLayout](canvas-layout.md) node fills it and ignores `AspectRatio`. A `Height` of its own still wins. The cell or the node is the frame, so the card has no border and no shadow there, and its body scrolls like that of a card with a height.
- **Parts**: A card has no other parts than the header, the picture, the body and the footer. It has no buttons.

## Related

- [Basics](basics.md) - Type names, paths, spacing, colors and icons
- [Overview](overview.md) - All widgets by group
- [FlowLayout](flow-layout.md) - Cards next to each other that wrap to the width of the screen
- [Alert](alert.md) - A highlighted box for a note or a warning
- [Indicator](indicator.md) - A state as a chip or an icon, for example in the header of a card
