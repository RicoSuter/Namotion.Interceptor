---
title: Layouts Demo
navTitle: Layouts Demo
position: 7
---

# Layouts Demo

This page shows the layout widgets at work: `FlowLayout`, `TabLayout` and `Card`, alone and inside each other. Change the width of the browser window while you read it, because a flow only shows what it does when its width changes.

The layouts are written in the short form: a head line ends the blocks before it that have as many colons or more, so an `Item`, a `Tab` and the blocks in them need no closing line. Each layout ends with its own closing line of two colons, because text of the page follows it.

## Flow with the default width

A `FlowLayout` without arguments gives every cell at least 320 pixels. Make the window narrower and wider and watch the three motors wrap: the number of columns follows the width.

~~~~markdown
:: HomeBlaze.Components.FlowLayout
::: Item
:::: HomeBlaze.Components.Widget Path=../Compressor
::: Item
:::: HomeBlaze.Components.Widget Path=../WaterPump
::: Item
:::: HomeBlaze.Components.Widget Path=../Conveyor
::
~~~~

:: HomeBlaze.Components.FlowLayout
::: Item
:::: HomeBlaze.Components.Widget Path=../Compressor
::: Item
:::: HomeBlaze.Components.Widget Path=../WaterPump
::: Item
:::: HomeBlaze.Components.Widget Path=../Conveyor
::

## Flow with a small width

With `MinWidth=120` a cell may get as narrow as 120 pixels, so many cells fit into one row. The six cards wrap only when the window gets very narrow.

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

## Flow with a large width

With `MinWidth=600` a cell is never narrower than 600 pixels, as long as the page is that wide. The two cards stand next to each other only in a wide window, and below each other in every other one.

:: HomeBlaze.Components.FlowLayout MinWidth=600
::: Item
:::: HomeBlaze.Components.Card Title="Cooling Fan" Icon=AcUnit
The cooling fan has a temperature of {{ ../CoolingFan/Temperature }} °C.

::::: HomeBlaze.Components.Widget Path=../CoolingFan
::: Item
:::: HomeBlaze.Components.Card Title="Exhaust Fan" Icon=Air
The exhaust fan runs at {{ ../ExhaustFan/CurrentSpeed }} RPM.

::::: HomeBlaze.Components.Widget Path=../ExhaustFan
::

## Tabs with names and icons

Each `Tab` block is one tab with a `Title` and an optional `Icon`. The `#name` after `Tab` is what the page knows the tab by: a tab with a name keeps its content and stays selected when tabs are added, removed or moved in the page text.

~~~~markdown
:: HomeBlaze.Components.TabLayout
::: Tab#cooling Title="Cooling Fan" Icon=AcUnit
:::: HomeBlaze.Components.Widget Path=../CoolingFan
::: Tab#exhaust Title="Exhaust Fan" Icon=Air
:::: HomeBlaze.Components.Widget Path=../ExhaustFan
::: Tab#notes Title="Notes" Icon=Notes
A tab can hold any page content, not only widgets.
::
~~~~

:: HomeBlaze.Components.TabLayout
::: Tab#cooling Title="Cooling Fan" Icon=AcUnit
:::: HomeBlaze.Components.Widget Path=../CoolingFan
::: Tab#exhaust Title="Exhaust Fan" Icon=Air
:::: HomeBlaze.Components.Widget Path=../ExhaustFan
::: Tab#notes Title="Notes" Icon=Notes
A tab can hold any page content, not only widgets.
::

## Tabs without names

A tab without a name is known by its position only. It looks the same, but when the page text changes, the selection stays at the position, also when another tab stands there now.

:: HomeBlaze.Components.TabLayout
::: Tab Title="First"
The first tab has no name and no icon.
::: Tab Title="Second"
The second tab has no name and no icon.
::

## Cards in a flow inside a tab

A block inside another block has more colons than the block around it. Here a tab layout holds a tab, the tab holds a flow, the flow holds items, an item holds a card and the card holds a widget, so the colons count up from two to seven. The second `Item` ends the first item with its card and its widget, and the second `Tab` ends the first tab with everything in it.

~~~~markdown
:: HomeBlaze.Components.TabLayout
::: Tab#pumps Title="Pumps" Icon=WaterDrop
:::: HomeBlaze.Components.FlowLayout MinWidth=320
::::: Item
:::::: HomeBlaze.Components.Card Title="Water Pump" Icon=WaterDrop
::::::: HomeBlaze.Components.Widget Path=../WaterPump
::::: Item
:::::: HomeBlaze.Components.Card Title="Compressor" Icon=Bolt
::::::: HomeBlaze.Components.Widget Path=../Compressor
::: Tab#belt Title="Conveyor Belt" Icon=Settings
The conveyor runs at {{ ../Conveyor/CurrentSpeed }} RPM.
::
~~~~

:: HomeBlaze.Components.TabLayout
::: Tab#pumps Title="Pumps" Icon=WaterDrop
:::: HomeBlaze.Components.FlowLayout MinWidth=320
::::: Item
:::::: HomeBlaze.Components.Card Title="Water Pump" Icon=WaterDrop
::::::: HomeBlaze.Components.Widget Path=../WaterPump
::::: Item
:::::: HomeBlaze.Components.Card Title="Compressor" Icon=Bolt
::::::: HomeBlaze.Components.Widget Path=../Compressor
::: Tab#belt Title="Conveyor Belt" Icon=Settings
The conveyor runs at {{ ../Conveyor/CurrentSpeed }} RPM.
::

## Tabs inside a card

A card takes any page content, also a layout. The tab layout is the body of this card, and the footer of the card stays below the tabs whichever tab is selected. The head line of the footer has three colons like the tab layout, so it ends the tab layout.

:: HomeBlaze.Components.Card Title="Fans" Icon=Air
::: HomeBlaze.Components.TabLayout
:::: Tab#cardcooling Title="Cooling Fan"
::::: HomeBlaze.Components.Widget Path=../CoolingFan
:::: Tab#cardexhaust Title="Exhaust Fan"
::::: HomeBlaze.Components.Widget Path=../ExhaustFan

::: Footer
Both fans are shown by reference, so they are the same motors as in the sections above.
::

## Cards

A card has an optional `Title`, an optional `Icon` and an optional `Footer` block. The first card has all three. The second card has none of them and is a plain box around its content. Both are written with every closing line, which stays valid.

:: HomeBlaze.Components.Card Title="Conveyor Belt" Icon=Settings
The conveyor runs at {{ ../Conveyor/CurrentSpeed }} RPM.

::: HomeBlaze.Components.Widget Path=../Conveyor
:::

::: Footer
Target speed: {{ ../Conveyor/TargetSpeed }} RPM
:::
::

:: HomeBlaze.Components.Card
This card has no title, no icon and no footer.
::
