---
title: Widgets for Markdown Pages
navTitle: Page Widgets
position: 5
---

# Widgets for Markdown Pages

This page is the reference of the widgets that ship with HomeBlaze and are made for [markdown pages](pages.md): `Card`, `FlowLayout`, `TabLayout`, `Widget`, `GridLayout`, `CanvasLayout`, `ValueMap`, `ValueList`, `ValueTile`, `Indicator`, `Progress`, `ExpansionPanels` and `Alert`, each with its arguments, its slots, its samples and its limits. The samples on this page are live: the block below each source is that source at work. [Markdown Pages](pages.md#blocks) describes the syntax of the blocks that they are written in.

Every other type that can be configured works as a block as well, see [Widgets](pages.md#widgets) on that page.

---

## Sample Motors

Several samples need a subject to show. They use the two [sample motors](widgets/basics.md#sample-motors) of the widget pages, the subject files `Pump.json` and `Fan.json` in the folder `widgets` next to this page. A path that starts with `../` starts at the folder of this page, so the samples refer to the motors as `../widgets/Pump` and `../widgets/Fan`, in a `Path` and in expressions. A sample that you copy to a page of your own needs the path of one of your subjects in their place.

All samples on this page are written in the short form, where the closing line of the last block ends the sample and the text of the page goes on below it, see [Closing and Nesting](pages.md#closing-and-nesting). A name is unique on a page, so every name on this page, like the name of a tab, belongs to one sample only.

## Card

A titled box around page content. Type name: `HomeBlaze.Components.Card`.

| Argument | Kind of value | Default | Meaning |
|----------|---------------|---------|---------|
| `Title` | Text | None | The title at the top of the card |
| `Subtitle` | Text | None | A smaller line below the title |
| `Icon` | Text | None | The [icon](#icons) next to the title |
| `IconColor` | Colour | `Default` | The [colour](#colours) of the icon |
| `Href` | Text | None | Where the title leads to. The title is then a link, written like a link in the text of the page, see [Links in Block Options](pages.md#links-in-block-options) |
| `Outlined` | Yes or no | `true` | A border around the card instead of a shadow. `false` gives a card with a shadow, see `Elevation` |
| `Elevation` | Whole number | `2` | The depth of the shadow of a card that is not outlined. `0` is no shadow. An outlined card has no shadow, whatever the value is |
| `Image` | Text | None | A picture between the header and the body, 200 pixels high. Written like `Href` |

| Slot | Blocks | Holds |
|------|--------|-------|
| `Content` | Default slot | The body of the card: any page content |
| `HeaderRight` | One | Content at the right end of the header, for example an [Indicator](#indicator) |
| `Footer` | One | Content below a divider at the bottom of the card |

A card with a title, an icon and a footer. Its body is text with an expression and a widget. The head line of the footer has three colons like the widget, so it ends the widget:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.Card Title="Pump" Icon=WaterDrop
The pump runs at {{ ../widgets/Pump/CurrentSpeed }} RPM.

::: HomeBlaze.Components.Widget Path=../widgets/Pump

::: Footer
Target speed: {{ ../widgets/Pump/TargetSpeed }} RPM
::
~~~~

:: HomeBlaze.Components.Card Title="Pump" Icon=WaterDrop
The pump runs at {{ ../widgets/Pump/CurrentSpeed }} RPM.

::: HomeBlaze.Components.Widget Path=../widgets/Pump

::: Footer
Target speed: {{ ../widgets/Pump/TargetSpeed }} RPM
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

A card with a subtitle, a coloured icon and a title that leads to the guide for pages. It is not outlined, so it has a shadow, and `Elevation=6` makes the shadow deeper than the default. The text in the corner of the header stands in the `HeaderRight` slot:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.Card Title="Pump" Subtitle="Sample motor" Icon=WaterDrop IconColor=Primary Href="pages.md" Outlined=false Elevation=6
The pump runs at {{ ../widgets/Pump/CurrentSpeed }} RPM.

::: HeaderRight
{{ ../widgets/Pump/Status }}
::
~~~~

:: HomeBlaze.Components.Card Title="Pump" Subtitle="Sample motor" Icon=WaterDrop IconColor=Primary Href="pages.md" Outlined=false Elevation=6
The pump runs at {{ ../widgets/Pump/CurrentSpeed }} RPM.

::: HeaderRight
{{ ../widgets/Pump/Status }}
::

- The header is shown when the card has a title, a subtitle, an icon or a `HeaderRight`.
- The icon and the content of `HeaderRight` stand on the line of the title, also when the subtitle takes several lines.
- The title of a card with `Href` looks like the title of any card and is underlined when the pointer is on it.
- A card is as wide as the place it stands in. Put cards into a `FlowLayout` to place them next to each other.
- A card has no other parts than the header, the picture, the body and the footer. It has no buttons.

## FlowLayout

Lays its cells out from left to right and wraps them to as many rows as the width of the screen needs. All cells are equally wide. This gives one column on a phone and several on a desktop. Type name: `HomeBlaze.Components.FlowLayout`.

| Argument | Kind of value | Default | Meaning |
|----------|---------------|---------|---------|
| `MinWidth` | Whole number | `320` | The narrowest a cell may get, in pixels. A value below 50 counts as 50 |

| Slot | Blocks | Holds |
|------|--------|-------|
| `Item` | Any number | One cell per block: any page content |

Change the width of the browser window while you read this section, because a flow only shows what it does when its width changes.

A `FlowLayout` without arguments gives every cell at least 320 pixels. The three cells stand next to each other in a wide window, and they wrap to two rows and then to three as the window gets narrower:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.FlowLayout
::: Item
:::: HomeBlaze.Components.Card Title="Pump" Icon=WaterDrop
::::: HomeBlaze.Components.Widget Path=../widgets/Pump
::: Item
:::: HomeBlaze.Components.Card Title="Fan" Icon=Air
::::: HomeBlaze.Components.Widget Path=../widgets/Fan
::: Item
:::: HomeBlaze.Components.Card Title="Notes"
A cell can hold any page content, not only widgets.

The pump runs at {{ ../widgets/Pump/CurrentSpeed }} RPM.
::
~~~~

:: HomeBlaze.Components.FlowLayout
::: Item
:::: HomeBlaze.Components.Card Title="Pump" Icon=WaterDrop
::::: HomeBlaze.Components.Widget Path=../widgets/Pump
::: Item
:::: HomeBlaze.Components.Card Title="Fan" Icon=Air
::::: HomeBlaze.Components.Widget Path=../widgets/Fan
::: Item
:::: HomeBlaze.Components.Card Title="Notes"
A cell can hold any page content, not only widgets.

The pump runs at {{ ../widgets/Pump/CurrentSpeed }} RPM.
::

With `MinWidth=120` a cell may get as narrow as 120 pixels, so many cells fit into one row. The six cards stand in one row while the page is at least 800 pixels wide, and wrap below that:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.FlowLayout MinWidth=120
::: Item
:::: HomeBlaze.Components.Card Title="One"
First cell
::: Item
:::: HomeBlaze.Components.Card Title="Two"
Second cell
::: Item
:::: HomeBlaze.Components.Card Title="Three"
Third cell
::: Item
:::: HomeBlaze.Components.Card Title="Four"
Fourth cell
::: Item
:::: HomeBlaze.Components.Card Title="Five"
Fifth cell
::: Item
:::: HomeBlaze.Components.Card Title="Six"
Sixth cell
::
~~~~

:: HomeBlaze.Components.FlowLayout MinWidth=120
::: Item
:::: HomeBlaze.Components.Card Title="One"
First cell
::: Item
:::: HomeBlaze.Components.Card Title="Two"
Second cell
::: Item
:::: HomeBlaze.Components.Card Title="Three"
Third cell
::: Item
:::: HomeBlaze.Components.Card Title="Four"
Fourth cell
::: Item
:::: HomeBlaze.Components.Card Title="Five"
Fifth cell
::: Item
:::: HomeBlaze.Components.Card Title="Six"
Sixth cell
::

With `MinWidth=600` a cell is never narrower than 600 pixels, as long as the page is that wide. The two cards stand next to each other only in a wide window, and below each other in every other one:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.FlowLayout MinWidth=600
::: Item
:::: HomeBlaze.Components.Card Title="Pump" Icon=WaterDrop
The pump is {{ ../widgets/Pump/Status }} and runs at {{ ../widgets/Pump/CurrentSpeed }} of {{ ../widgets/Pump/TargetSpeed }} RPM.
::: Item
:::: HomeBlaze.Components.Card Title="Fan" Icon=Air
The fan is {{ ../widgets/Fan/Status }} and runs at {{ ../widgets/Fan/CurrentSpeed }} of {{ ../widgets/Fan/TargetSpeed }} RPM.
::
~~~~

:: HomeBlaze.Components.FlowLayout MinWidth=600
::: Item
:::: HomeBlaze.Components.Card Title="Pump" Icon=WaterDrop
The pump is {{ ../widgets/Pump/Status }} and runs at {{ ../widgets/Pump/CurrentSpeed }} of {{ ../widgets/Pump/TargetSpeed }} RPM.
::: Item
:::: HomeBlaze.Components.Card Title="Fan" Icon=Air
The fan is {{ ../widgets/Fan/Status }} and runs at {{ ../widgets/Fan/CurrentSpeed }} of {{ ../widgets/Fan/TargetSpeed }} RPM.
::

How `MinWidth` works and how to choose it:

- **A number of pixels**: `MinWidth` is a plain number, without a unit. The layout puts as many columns into a row as fit with at least that width each, and then makes all columns equally wide to fill the row. There are 16 pixels between two cells.
- **Smaller means more columns**: With `MinWidth=120` many cells fit into one row. With `MinWidth=600` two cells stand next to each other only on a wide screen.
- **At least as wide as the widest widget**: A cell can get as narrow as `MinWidth`, and a widget that needs more room then paints over the next cell. The sample motor widget needs 280 pixels, and 312 pixels inside a card, so 320 fits it.
- **Narrow screens**: On a screen that is narrower than `MinWidth` there is one column, as wide as the screen.

Limits:

- All cells have the same width. A cell cannot span two columns.
- A row is not stretched: with fewer cells than columns fit, the rest of the row stays empty.
- An `Item` holds content only. It takes no arguments and no `#name`.
- A `FlowLayout` has no default slot. Content outside of an `Item` block is an error.

## TabLayout

Shows one of several tabs at a time. Type name: `HomeBlaze.Components.TabLayout`.

| Argument | Kind of value | Default | Meaning |
|----------|---------------|---------|---------|
| `Position` | `Top`, `Left`, `Right` or `Bottom` | `Top` | The side of the content that the tab bar is at. At `Left` and `Right` the tabs stand below each other |

| Slot | Blocks | Holds |
|------|--------|-------|
| `Tab` | Any number | One tab per block. A `Tab` block creates a subject, so it takes arguments, an optional `#name` and any page content |

The arguments of a `Tab` block:

| Argument | Kind of value | Default | Meaning |
|----------|---------------|---------|---------|
| `Title` | Text | None | The label of the tab |
| `Icon` | Text | None | The [icon](#icons) on the tab |

Two tabs with a name and an icon, and a third tab without a name and without an icon that holds plain text:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.TabLayout
::: Tab#pumptab Title="Pump" Icon=WaterDrop
The pump runs at {{ ../widgets/Pump/CurrentSpeed }} RPM.

:::: HomeBlaze.Components.Widget Path=../widgets/Pump
::: Tab#fantab Title="Fan" Icon=Air
The fan runs at {{ ../widgets/Fan/CurrentSpeed }} RPM.

:::: HomeBlaze.Components.Widget Path=../widgets/Fan
::: Tab Title="Notes"
A tab can hold any page content, not only widgets.
::
~~~~

:: HomeBlaze.Components.TabLayout
::: Tab#pumptab Title="Pump" Icon=WaterDrop
The pump runs at {{ ../widgets/Pump/CurrentSpeed }} RPM.

:::: HomeBlaze.Components.Widget Path=../widgets/Pump
::: Tab#fantab Title="Fan" Icon=Air
The fan runs at {{ ../widgets/Fan/CurrentSpeed }} RPM.

:::: HomeBlaze.Components.Widget Path=../widgets/Fan
::: Tab Title="Notes"
A tab can hold any page content, not only widgets.
::

Tabs inside tabs. The first tab holds a tab layout of its own, which has four colons, one more than the tab around it:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.TabLayout
::: Tab#motorstab Title="Motors" Icon=Settings
:::: HomeBlaze.Components.TabLayout
::::: Tab#innerpump Title="Pump" Icon=WaterDrop
:::::: HomeBlaze.Components.Widget Path=../widgets/Pump
::::: Tab#innerfan Title="Fan" Icon=Air
:::::: HomeBlaze.Components.Widget Path=../widgets/Fan
::: Tab#abouttab Title="About" Icon=Info
The inner tabs keep their selection while you switch between the outer tabs.
::
~~~~

:: HomeBlaze.Components.TabLayout
::: Tab#motorstab Title="Motors" Icon=Settings
:::: HomeBlaze.Components.TabLayout
::::: Tab#innerpump Title="Pump" Icon=WaterDrop
:::::: HomeBlaze.Components.Widget Path=../widgets/Pump
::::: Tab#innerfan Title="Fan" Icon=Air
:::::: HomeBlaze.Components.Widget Path=../widgets/Fan
::: Tab#abouttab Title="About" Icon=Info
The inner tabs keep their selection while you switch between the outer tabs.
::

Cards in a flow inside a tab. The colons count up from two to seven here: the tab layout, its tabs, the flow layout, its items, the cards and the widgets. The second `Item` ends the first item with its card and its widget, and the second `Tab` ends the first tab with everything in it:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.TabLayout
::: Tab#cardstab Title="Cards" Icon=Dashboard
:::: HomeBlaze.Components.FlowLayout MinWidth=320
::::: Item
:::::: HomeBlaze.Components.Card Title="Pump" Icon=WaterDrop
::::::: HomeBlaze.Components.Widget Path=../widgets/Pump
::::: Item
:::::: HomeBlaze.Components.Card Title="Fan" Icon=Air
::::::: HomeBlaze.Components.Widget Path=../widgets/Fan
::: Tab#texttab Title="Text" Icon=Notes
The pump runs at {{ ../widgets/Pump/CurrentSpeed }} RPM and the fan at {{ ../widgets/Fan/CurrentSpeed }} RPM.
::
~~~~

:: HomeBlaze.Components.TabLayout
::: Tab#cardstab Title="Cards" Icon=Dashboard
:::: HomeBlaze.Components.FlowLayout MinWidth=320
::::: Item
:::::: HomeBlaze.Components.Card Title="Pump" Icon=WaterDrop
::::::: HomeBlaze.Components.Widget Path=../widgets/Pump
::::: Item
:::::: HomeBlaze.Components.Card Title="Fan" Icon=Air
::::::: HomeBlaze.Components.Widget Path=../widgets/Fan
::: Tab#texttab Title="Text" Icon=Notes
The pump runs at {{ ../widgets/Pump/CurrentSpeed }} RPM and the fan at {{ ../widgets/Fan/CurrentSpeed }} RPM.
::

A tab layout inside a card. The tab layout is the body of the card, and the footer of the card stays below the tabs whichever tab is selected. The head line of the footer has three colons like the tab layout, so it ends the tab layout:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.Card Title="Motors" Icon=Settings
::: HomeBlaze.Components.TabLayout
:::: Tab#cardpump Title="Pump"
::::: HomeBlaze.Components.Widget Path=../widgets/Pump
:::: Tab#cardfan Title="Fan"
::::: HomeBlaze.Components.Widget Path=../widgets/Fan

::: Footer
Both tabs show the sample motors, the same motors as in the samples above.
::
~~~~

:: HomeBlaze.Components.Card Title="Motors" Icon=Settings
::: HomeBlaze.Components.TabLayout
:::: Tab#cardpump Title="Pump"
::::: HomeBlaze.Components.Widget Path=../widgets/Pump
:::: Tab#cardfan Title="Fan"
::::: HomeBlaze.Components.Widget Path=../widgets/Fan

::: Footer
Both tabs show the sample motors, the same motors as in the samples above.
::

The tab bar at the left of the content:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.TabLayout Position=Left
::: Tab#sidepump Title="Pump" Icon=WaterDrop
:::: HomeBlaze.Components.Widget Path=../widgets/Pump
::: Tab#sidefan Title="Fan" Icon=Air
:::: HomeBlaze.Components.Widget Path=../widgets/Fan
::
~~~~

:: HomeBlaze.Components.TabLayout Position=Left
::: Tab#sidepump Title="Pump" Icon=WaterDrop
:::: HomeBlaze.Components.Widget Path=../widgets/Pump
::: Tab#sidefan Title="Fan" Icon=Air
:::: HomeBlaze.Components.Widget Path=../widgets/Fan
::

- **Tabs at a side need a wide window**: With `Position=Left` or `Position=Right` the tab bar is at the top while the browser window is narrower than 600 pixels, because the content would have too little room beside it. The width of the window counts, not that of the place the layout stands in. Content that cannot get as narrow as the room beside the tab bar, like a wide table, scrolls inside the tab.
- **All tabs are rendered**: Every tab is rendered and kept up to date, also the hidden ones. A subject in a hidden tab keeps running, and a hidden tab costs as much as a visible one.
- **The first tab is selected**: When the page opens, the first tab is shown. The selection is not saved and there is no argument for it.
- **Names keep the selection**: When the page text changes while the page is open, a tab with a name stays selected, also when tabs are added, removed or moved. A tab without a name is known by its position only, so the selection stays at the position. When the selected tab is gone, the first tab is selected.
- **Tabs only**: A `TabLayout` has no default slot. Content outside of a `Tab` block is an error.

## Widget

Shows the widget of a subject that exists somewhere else: in a file of its own, or in another block of the page. Type name: `HomeBlaze.Components.Widget`. It has no slots, so its block stays empty.

| Argument | Kind of value | Default | Meaning |
|----------|---------------|---------|---------|
| `Path` | Text | Empty | The path to the subject to show |

The three ways to refer to a subject:

- **By the name of a block**: `Path=mymotor` leads to the block with the name `#mymotor` on the same page.
- **By a relative path**: `Path=../Conveyor` starts at the page, so it leads to the file `Conveyor.json` next to the page.
- **By an absolute path**: `Path=/Demo/Conveyor` leads to the same subject from every page.

By a relative path, here to one of the [sample motors](#sample-motors):

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.Widget Path=../widgets/Pump
::
~~~~

:: HomeBlaze.Components.Widget Path=../widgets/Pump
::

A path that leads nowhere shows a message in place of a widget. Nothing exists at this path:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.Widget Path=/Missing/Motor
::
~~~~

:: HomeBlaze.Components.Widget Path=/Missing/Motor
::

- **Not a copy**: The widget shows the subject itself. A motor that is shown twice is one motor.
- **A path that leads nowhere**: The widget shows the message "Cannot resolve path" with the path. This is no error box, and a `Widget` without a `Path` shows it too.
- **A path that leads back**: A `Widget` that refers to itself or to a block that contains it shows the message "Cannot show path" in place of the content. Two blocks that show each other end with the same message.
- **A subject without a widget**: When the type of the subject has no widget, the notice "No Widget component for" with the type name is shown.

## GridLayout

A grid with a fixed number of rows and columns. Type name: `HomeBlaze.Components.GridLayout`.

| Argument | Kind of value | Default | Meaning |
|----------|---------------|---------|---------|
| `Rows` | Whole number | `2` | The number of rows |
| `Columns` | Whole number | `2` | The number of columns |

| Slot | Blocks | Holds |
|------|--------|-------|
| `Cell` | Any number | One cell per block. A `Cell` block creates a subject, so it takes arguments and an optional `#name`. Its content is what the cell shows: any page content |

The arguments of a `Cell` block:

| Argument | Kind of value | Default | Meaning |
|----------|---------------|---------|---------|
| `Row` | Whole number | Not set | The row of the cell, counted from 0. Without it the grid places the cell itself |
| `Column` | Whole number | Not set | The column of the cell, counted from 0. Without it the grid places the cell itself |
| `RowSpan` | Whole number | `1` | How many rows the cell covers |
| `ColumnSpan` | Whole number | `1` | How many columns the cell covers |

A grid of two rows and two columns. The two cells of the first row show the two sample motors. The cell of the second row spans both columns and holds text with expressions:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.GridLayout#motorgrid Rows=2 Columns=2
::: Cell Row=0 Column=0
:::: HomeBlaze.Components.Widget Path=../widgets/Pump
::: Cell Row=0 Column=1
:::: HomeBlaze.Components.Widget Path=../widgets/Fan
::: Cell Row=1 Column=0 ColumnSpan=2
The pump is set to **{{ ../widgets/Pump/TargetSpeed }}** and the fan to **{{ ../widgets/Fan/TargetSpeed }}**.
::
~~~~

:: HomeBlaze.Components.GridLayout#motorgrid Rows=2 Columns=2
::: Cell Row=0 Column=0
:::: HomeBlaze.Components.Widget Path=../widgets/Pump
::: Cell Row=0 Column=1
:::: HomeBlaze.Components.Widget Path=../widgets/Fan
::: Cell Row=1 Column=0 ColumnSpan=2
The pump is set to **{{ ../widgets/Pump/TargetSpeed }}** and the fan to **{{ ../widgets/Fan/TargetSpeed }}**.
::

- All rows have the same height and all columns the same width, and the grid is at least 400 pixels high.
- The grid keeps its columns at every width of the screen. A cell cuts off content that needs more room than the cell has, which a narrow window shows in the first row of the grid above. Use a `FlowLayout` where the cells should wrap.
- Give a cell a `#name` to keep it and everything in it when cells are added or removed, see [What an Edit of the Page Keeps](pages.md#what-an-edit-of-the-page-keeps).
- In a configuration file of its own, a `GridLayout` has its cells as the list `cells`, each with one subject as `child`. On a page they are always `Cell` blocks as above, and the [`json` block](pages.md#the-json-block) cannot set them.
- While the page is in edit mode, each cell has buttons to edit and remove it, and once the grid itself is selected, a click on the plus in a free place adds a cell there, see [Editing in the Page Editor](pages.md#editing-in-the-page-editor).

## CanvasLayout

An area on which content is placed at any position. Type name: `HomeBlaze.Components.CanvasLayout`.

| Argument | Kind of value | Default | Meaning |
|----------|---------------|---------|---------|
| `MinHeight` | Whole number | Not set, which gives 400 | The height of the canvas in pixels |
| `IsSnapToGridEnabled` | Yes or no | `false` | Whether a node that is placed or moved snaps to a grid |
| `GridSize` | Whole number | `100` | The step of that grid in pixels |

| Slot | Blocks | Holds |
|------|--------|-------|
| `Node` | Any number | One node per block. A `Node` block creates a subject, so it takes arguments and an optional `#name`. Its content is what the node shows: any page content |

The arguments of a `Node` block:

| Argument | Kind of value | Default | Meaning |
|----------|---------------|---------|---------|
| `X` | Whole number | `0` | The distance from the left edge in pixels |
| `Y` | Whole number | `0` | The distance from the top edge in pixels |
| `Width` | Whole number | `200` | The width in pixels |
| `Height` | Whole number | `150` | The height in pixels |

A canvas with two nodes that show the two sample motors. A canvas does not adapt to the width of the screen: every node keeps its position and its size in pixels. The two nodes here are placed below each other, so that a narrow window shows both:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.CanvasLayout#plan MinHeight=620 IsSnapToGridEnabled=true GridSize=20
::: Node X=20 Y=20 Width=300 Height=280
:::: HomeBlaze.Components.Widget Path=../widgets/Pump
::: Node X=40 Y=320 Width=300 Height=280
:::: HomeBlaze.Components.Widget Path=../widgets/Fan
::
~~~~

:: HomeBlaze.Components.CanvasLayout#plan MinHeight=620 IsSnapToGridEnabled=true GridSize=20
::: Node X=20 Y=20 Width=300 Height=280
:::: HomeBlaze.Components.Widget Path=../widgets/Pump
::: Node X=40 Y=320 Width=300 Height=280
:::: HomeBlaze.Components.Widget Path=../widgets/Fan
::

- While the page is in edit mode, the nodes can be moved with the mouse, each node has buttons to edit and remove it, a click on a node selects the widget in it, and a click on a free place adds a node there. The canvas itself is selected with the arrow up button of a selected widget in a node, or with the keyboard, see [Editing in the Page Editor](pages.md#editing-in-the-page-editor).
- A canvas can be zoomed with the mouse wheel and moved by dragging its background only while the page is edited. While the page is viewed, the canvas itself cannot be zoomed or moved, the wheel scrolls the page, and the widgets in its nodes work as anywhere else on the page. While the page is edited, the widgets in the nodes do not react to the mouse, so that a node can be moved without triggering what it shows. No line can be drawn between nodes in either mode.
- A node has a fixed size and cuts off content that needs more room. Names and a configuration file work as for the cells of a `GridLayout`, with the list `nodes`.

## Value Widgets

`ValueList`, `ValueTile`, `Indicator` and `Progress` show values of properties, and `ValueMap` says what text, colour and icon a value gets. The sections for them refer to the rules in this section.

- **The path**: `Path` is the path of a property. It is written like the path of an [expression](pages.md#expression-paths): relative to the page, where the first segment can be the name of a block, or absolute.
- **The formatted value**: A value is shown in the [formatted form of an expression](pages.md#expression-formats). A map can replace the text.
- **A missing value**: A value that is not set and a path that leads nowhere show a dash in place of the value, with the path as tooltip. This is no error box, because a device that is offline or still starting is normal. A misspelled path shows the same dash, so check the spelling when a dash stays. A `Progress` shows the dash for a value that is no number as well.
- **Live**: A value changes on the page when the property changes.

## ValueMap

Says what text, colour and icon a value gets in the widgets that refer to it. It shows nothing itself. Type name: `HomeBlaze.Components.ValueMap`. It has no arguments.

| Slot | Blocks | Holds |
|------|--------|-------|
| `Case` | Any number | One case per block. A `Case` block creates a subject, so it takes arguments and an optional `#name`. It holds no content |

The arguments of a `Case` block:

| Argument | Kind of value | Default | Meaning |
|----------|---------------|---------|---------|
| `Value` | Text | None | The value that the case is for, as raw text, see below |
| `Text` | Text | None | The text to show. Without it the formatted text of the value stays |
| `Color` | Colour | `Default` | The [colour](#colours) of the value |
| `Icon` | Text | None | The [icon](#icons) to show. It replaces the icon of the widget |

A map for the status of a motor. The cases are the values of `MotorStatus`. The block below the source shows nothing, because a map is only read by other widgets. The samples in the next sections refer to it by its name `motorstate`:

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

- **How a widget refers to a map**: With `Map=motorstate`, the name of the block, on the same page. A map on another page is reached through that page. For a page `Shared.md` in the folder `Demo`, which you have made yourself and which has a map named `motorstate`, that is `Map=/Demo/Shared.md/motorstate`. This works for a map that stands directly on that page, see [Paths on Markdown Pages](paths.md#paths-on-markdown-pages).
- **Which case is used**: A case matches when its `Value` equals the raw text of the value, without regard to upper and lower case. The raw text is what the format `raw` of an expression shows: `True` and `False` for a boolean, the name for an enum value, a number with a dot as the decimal separator. The first case that matches is used.
- **No case**: A value without a case keeps the formatted text, the default colour and the icon of the widget. A case without `Text` keeps the formatted text of the value.
- **Colour and icon**: The colour of the case is the colour of the value in a row and in a tile, and the colour of an indicator. The icon of the case replaces the icon of the row, the tile or the indicator. A `Progress` has no map.
- **A map that cannot be resolved**: A `Map` that leads nowhere, or to a subject that is no `ValueMap`, shows `Cannot resolve map: motorstat` in a small red alert in place of the value, with the name as written. This is a mistake on the page, see [Page Errors](page-errors.md#value-widget-errors). The rest of the page renders.
- **Cases only**: A `ValueMap` has no default slot. Content outside of a `Case` block is an error.

## ValueList

A list of rows. Each row has an icon, a label, optional content and a value, which stands at the right. Type name: `HomeBlaze.Components.ValueList`. It has no arguments.

| Slot | Blocks | Holds |
|------|--------|-------|
| `Value` | Any number | One row per block. A `Value` block creates a subject, so it takes arguments, an optional `#name` and any page content |

The arguments of a `Value` block:

| Argument | Kind of value | Default | Meaning |
|----------|---------------|---------|---------|
| `Path` | Text | None | The [path](#value-widgets) of the property whose value the row shows. A row with content needs none |
| `Label` | Text | The title of the property | The label of the row. Without it the row shows the title that the property declares, and the name of the property when it declares none |
| `Icon` | Text | None | The [icon](#icons) at the start of the row |
| `Map` | Text | None | The name or the path of a [ValueMap](#valuemap) |

Rows with a label and an icon. The first two rows have an icon, so the third row keeps the room of an icon, which lines up its label with theirs:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.ValueList
::: Value Path=../widgets/Pump/CurrentSpeed Label="Current speed" Icon=Speed
::: Value Path=../widgets/Pump/Temperature Label="Motor temperature" Icon=Thermostat
::: Value Path=../widgets/Pump/IsAtTargetSpeed Label="At target speed"
::
~~~~

:: HomeBlaze.Components.ValueList
::: Value Path=../widgets/Pump/CurrentSpeed Label="Current speed" Icon=Speed
::: Value Path=../widgets/Pump/Temperature Label="Motor temperature" Icon=Thermostat
::: Value Path=../widgets/Pump/IsAtTargetSpeed Label="At target speed"
::

Rows without a label, the last one with a map. A label is the title that the property declares, or its name when it declares none: "Speed" and "Target" are titles that the motor declares, and "Status" is the name of its property. The status is shown with the text, the colour and the icon of its case:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.ValueList
::: Value Path=../widgets/Pump/CurrentSpeed
::: Value Path=../widgets/Pump/TargetSpeed
::: Value Path=../widgets/Pump/Status Map=motorstate
::
~~~~

:: HomeBlaze.Components.ValueList
::: Value Path=../widgets/Pump/CurrentSpeed
::: Value Path=../widgets/Pump/TargetSpeed
::: Value Path=../widgets/Pump/Status Map=motorstate
::

A row with content and without a path, and a row with a `Progress` as content. The content stands between the label and the value. The first row has no path and no value of its own: its content is a text with two expressions. The second row has a path and a bar:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.ValueList
::: Value Label="Pump" Icon=WaterDrop
{{ ../widgets/Pump/CurrentSpeed }} of {{ ../widgets/Pump/TargetSpeed }} RPM
::: Value Path=../widgets/Fan/CurrentSpeed Label="Fan" Icon=Air
:::: HomeBlaze.Components.Progress Path=../widgets/Fan/CurrentSpeed Minimum=0 Maximum=3000
::
~~~~

:: HomeBlaze.Components.ValueList
::: Value Label="Pump" Icon=WaterDrop
{{ ../widgets/Pump/CurrentSpeed }} of {{ ../widgets/Pump/TargetSpeed }} RPM
::: Value Path=../widgets/Fan/CurrentSpeed Label="Fan" Icon=Air
:::: HomeBlaze.Components.Progress Path=../widgets/Fan/CurrentSpeed Minimum=0 Maximum=3000
::

- **Aligned labels**: When at least one row has an icon, the rows without one keep the room of an icon, so that all labels line up. An icon that comes from a case of a map counts as well. In a card with an icon, a list in which at least one row has an icon lines up the icons of its rows with the icon of the header and its labels with the title.
- **Colour and icon of a case**: A matching case gives the value text its colour and the row its icon, see [ValueMap](#valuemap).
- **Content and value**: Content and a value can stand in the same row. The content takes the room between the label and the value: a `Progress` fills it, and a text stands at the right end of it, right before the value.
- **Rows only**: A `ValueList` has no default slot. Content outside of a `Value` block is an error.

## ValueTile

A small box with a label and one large value. Type name: `HomeBlaze.Components.ValueTile`. It has no slots.

| Argument | Kind of value | Default | Meaning |
|----------|---------------|---------|---------|
| `Path` | Text | None | The [path](#value-widgets) of the property whose value the tile shows |
| `Label` | Text | The title of the property | The small text above the value. Without it the tile shows the title that the property declares, and the name of the property when it declares none |
| `Icon` | Text | None | The [icon](#icons) next to the label |
| `Map` | Text | None | The name or the path of a [ValueMap](#valuemap) |

Three tiles in a `FlowLayout`. The first has a label of its own and an icon, the second takes its label from the property, and the third shows the status through the map:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.FlowLayout MinWidth=140
::: Item
:::: HomeBlaze.Components.ValueTile Path=../widgets/Pump/CurrentSpeed Label="Pump speed" Icon=Speed
::: Item
:::: HomeBlaze.Components.ValueTile Path=../widgets/Pump/Temperature
::: Item
:::: HomeBlaze.Components.ValueTile Path=../widgets/Pump/Status Map=motorstate
::
~~~~

:: HomeBlaze.Components.FlowLayout MinWidth=140
::: Item
:::: HomeBlaze.Components.ValueTile Path=../widgets/Pump/CurrentSpeed Label="Pump speed" Icon=Speed
::: Item
:::: HomeBlaze.Components.ValueTile Path=../widgets/Pump/Temperature
::: Item
:::: HomeBlaze.Components.ValueTile Path=../widgets/Pump/Status Map=motorstate
::

- **Colour and icon of a case**: A matching case gives the value its colour and the tile its icon, see [ValueMap](#valuemap).
- **No distance to its surroundings**: A tile adds no distance above, below or beside itself. It is meant to stand in a layout such as `FlowLayout`, which does the spacing.
- **As wide as its place**: A tile is as wide as the place it stands in.

## Indicator

Shows a state as a coloured chip, or as an icon alone. Type name: `HomeBlaze.Components.Indicator`. It has no slots.

| Argument | Kind of value | Default | Meaning |
|----------|---------------|---------|---------|
| `Path` | Text | None | The [path](#value-widgets) of the property whose value the indicator shows |
| `Map` | Text | None | The name or the path of a [ValueMap](#valuemap) |
| `Icon` | Text | None | The [icon](#icons) |
| `Kind` | `Chip` or `Icon` | `Chip` | `Chip` is a chip with the icon and the text. `Icon` is the icon alone, with the text as its tooltip |

A chip with the map:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.Indicator Path=../widgets/Pump/Status Map=motorstate
::
~~~~

:: HomeBlaze.Components.Indicator Path=../widgets/Pump/Status Map=motorstate
::

The same status as an icon alone. The icon is the one of the matching case, and the tooltip has the text of the case:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.Indicator Path=../widgets/Pump/Status Map=motorstate Kind=Icon
::
~~~~

:: HomeBlaze.Components.Indicator Path=../widgets/Pump/Status Map=motorstate Kind=Icon
::

An indicator without a map, for a boolean. It has the primary colour while the value is true and the default colour otherwise. The icon comes from `Icon`:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.Indicator Path=../widgets/Pump/IsAtTargetSpeed Kind=Icon Icon=Speed
::
~~~~

:: HomeBlaze.Components.Indicator Path=../widgets/Pump/IsAtTargetSpeed Kind=Icon Icon=Speed
::

An indicator in the `HeaderRight` of a card:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.Card Title="Pump" Icon=WaterDrop
The pump runs at {{ ../widgets/Pump/CurrentSpeed }} RPM.

::: HeaderRight
:::: HomeBlaze.Components.Indicator Path=../widgets/Pump/Status Map=motorstate
::
~~~~

:: HomeBlaze.Components.Card Title="Pump" Icon=WaterDrop
The pump runs at {{ ../widgets/Pump/CurrentSpeed }} RPM.

::: HeaderRight
:::: HomeBlaze.Components.Indicator Path=../widgets/Pump/Status Map=motorstate
::

- **Text, colour and icon**: They come from the matching case of the map, see [ValueMap](#valuemap). `Icon` is the icon that stays when no case gives one.
- **Without a map**: A boolean that is true has the colour `Primary`, and every other value has `Default`. With a map the colour comes from the case alone.
- **No icon**: A chip without an icon shows its text only. An indicator of the kind `Icon` without an icon shows a plain circle.
- **A missing value**: The chip shows a dash. The icon form shows its icon in the default colour. Both have the path as tooltip, see [Value Widgets](#value-widgets). A chip with a value has no tooltip.
- **A small distance to its surroundings**: An indicator keeps a distance of 4 pixels to what stands next to it, so that several of them in a row do not touch.

## Progress

A bar that shows where a number stands between a minimum and a maximum. Type name: `HomeBlaze.Components.Progress`. It has no slots and no map.

| Argument | Kind of value | Default | Meaning |
|----------|---------------|---------|---------|
| `Path` | Text | None | The [path](#value-widgets) of the property whose value the bar shows |
| `Minimum` | Number with a fraction | `0` | The value at which the bar is empty |
| `Maximum` | Number with a fraction | `1` | The value at which the bar is full |
| `Color` | Colour | `Primary` | The [colour](#colours) of the bar |

Two bars for a speed. The highest target speed of a sample motor is 3000 RPM, so that is the end of the range. The second bar has another colour:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.Progress Path=../widgets/Pump/CurrentSpeed Minimum=0 Maximum=3000
::

:: HomeBlaze.Components.Progress Path=../widgets/Fan/CurrentSpeed Minimum=0 Maximum=3000 Color=Success
::
~~~~

:: HomeBlaze.Components.Progress Path=../widgets/Pump/CurrentSpeed Minimum=0 Maximum=3000
::

:: HomeBlaze.Components.Progress Path=../widgets/Fan/CurrentSpeed Minimum=0 Maximum=3000 Color=Success
::

- **The range**: The bar is empty at `Minimum` and full at `Maximum`. A value outside the range gives an empty or a full bar. The default range from 0 to 1 is what a property with the unit percent holds. A value from 0 to 100 needs `Max=100`.
- **No bar**: A value that is no number, a value that is not set and a range in which `Maximum` is not above `Minimum` show a dash in place of the bar.
- **No text**: The bar has no text. Show the number with an expression, or put the bar into a row of a [ValueList](#valuelist), where the value stands next to it.
- **Width**: Alone, a bar is as wide as the place it stands in. In a row of a value list it fills the room between the label and the value, and it is at least 48 pixels wide.

## ExpansionPanels

Page content in panels that open and close. Each panel has a title line that stays visible. Type name: `HomeBlaze.Components.ExpansionPanels`.

| Argument | Kind of value | Default | Meaning |
|----------|---------------|---------|---------|
| `Single` | Yes or no | `false` | Whether opening a panel closes the others. By default several panels can be open |

| Slot | Blocks | Holds |
|------|--------|-------|
| `Panel` | Any number | One panel per block. A `Panel` block creates a subject, so it takes arguments, an optional `#name` and any page content |

The arguments of a `Panel` block:

| Argument | Kind of value | Default | Meaning |
|----------|---------------|---------|---------|
| `Title` | Text | None | The title line of the panel |
| `Icon` | Text | None | The [icon](#icons) in front of the title |
| `Expanded` | Yes or no | `false` | Whether the panel is open when the page loads |

Two panels, the first of them open. The second one opens with a click:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.ExpansionPanels
::: Panel Title="Pump" Icon=WaterDrop Expanded=true
The pump runs at {{ ../widgets/Pump/CurrentSpeed }} RPM.
::: Panel Title="Fan" Icon=Air
The fan runs at {{ ../widgets/Fan/CurrentSpeed }} RPM.
::
~~~~

:: HomeBlaze.Components.ExpansionPanels
::: Panel Title="Pump" Icon=WaterDrop Expanded=true
The pump runs at {{ ../widgets/Pump/CurrentSpeed }} RPM.
::: Panel Title="Fan" Icon=Air
The fan runs at {{ ../widgets/Fan/CurrentSpeed }} RPM.
::

With `Single=true` only one panel is open at a time. Each panel holds a value list, and the first one is open when the page loads:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.ExpansionPanels Single=true
::: Panel Title="Pump" Icon=WaterDrop Expanded=true
:::: HomeBlaze.Components.ValueList
::::: Value Path=../widgets/Pump/CurrentSpeed
::::: Value Path=../widgets/Pump/Status Map=motorstate
::: Panel Title="Fan" Icon=Air
:::: HomeBlaze.Components.ValueList
::::: Value Path=../widgets/Fan/CurrentSpeed
::::: Value Path=../widgets/Fan/Status Map=motorstate
::
~~~~

:: HomeBlaze.Components.ExpansionPanels Single=true
::: Panel Title="Pump" Icon=WaterDrop Expanded=true
:::: HomeBlaze.Components.ValueList
::::: Value Path=../widgets/Pump/CurrentSpeed
::::: Value Path=../widgets/Pump/Status Map=motorstate
::: Panel Title="Fan" Icon=Air
:::: HomeBlaze.Components.ValueList
::::: Value Path=../widgets/Fan/CurrentSpeed
::::: Value Path=../widgets/Fan/Status Map=motorstate
::

- **All panels are rendered**: The content of a closed panel is rendered and kept up to date as well, so a value in a closed panel is current when the panel opens.
- **Opening and closing**: `Expanded` sets the state when the page loads. What the viewer opens and closes is not written to the page. It is kept for as long as the panel keeps its subject when the page is edited. That holds when the `ExpansionPanels` block and the panel both keep their subjects, which a name on each of them does, see [What an Edit of the Page Keeps](pages.md#what-an-edit-of-the-page-keeps).
- **Panels only**: An `ExpansionPanels` has no default slot. Content outside of a `Panel` block is an error.

## Alert

A highlighted box for a note or a warning. Type name: `HomeBlaze.Components.Alert`.

| Argument | Kind of value | Default | Meaning |
|----------|---------------|---------|---------|
| `Severity` | `Normal`, `Info`, `Success`, `Warning` or `Error` | `Info` | How serious the note is. It decides the colour and the icon |
| `Variant` | `Text`, `Filled` or `Outlined` | `Text` | `Text` is a tinted box, `Filled` is a solid one and `Outlined` has a border |
| `Icon` | Text | None | An [icon](#icons) that replaces the icon of the severity |

| Slot | Blocks | Holds |
|------|--------|-------|
| `Content` | Default slot | The text of the box: any page content |

An alert without arguments has the severity `Info`:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.Alert
The pump runs at {{ ../widgets/Pump/CurrentSpeed }} RPM.
::
~~~~

:: HomeBlaze.Components.Alert
The pump runs at {{ ../widgets/Pump/CurrentSpeed }} RPM.
::

A warning with a border:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.Alert Severity=Warning Variant=Outlined
The fan is {{ ../widgets/Fan/Status }}. Check it when it stays below its target of {{ ../widgets/Fan/TargetSpeed }} RPM.
::
~~~~

:: HomeBlaze.Components.Alert Severity=Warning Variant=Outlined
The fan is {{ ../widgets/Fan/Status }}. Check it when it stays below its target of {{ ../widgets/Fan/TargetSpeed }} RPM.
::

- **One-line text**: An alert removes the distance above its first paragraph and below its last one, so that a text of one line is centred next to the icon. Paragraphs in between keep their distance.
- **Fixed severity**: The severity is an argument, so it does not follow a value. Use an [Indicator](#indicator) for a state that changes.

## Colours

A colour is one of `Default`, `Primary`, `Secondary`, `Tertiary`, `Info`, `Success`, `Warning` and `Error`. They are the colours of the theme, so they follow it. `MudBlazor.Color` has a few more names that are accepted, but these are the ones meant for pages. The `IconColor` of a card, the `Color` of a case of a map and the `Color` of a progress bar take one.

## Icons

The `Icon` of a card, a tab, a panel, a row, a tile, an indicator, an alert and a case of a map is the name of a MudBlazor Material icon in its filled form, for example `Settings`, `Bolt`, `WaterDrop`, `AcUnit` or `Link`. The name is case-sensitive. An unknown name shows a default icon and no error.

---

## Related Documentation

- [Markdown Pages](pages.md) - The syntax of pages: expressions, blocks, slots and the `json` block
- [Page Errors](page-errors.md) - Every error message with its cause and its fix
- [Paths](paths.md) - Path syntax reference
