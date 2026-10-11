---
title: Layouts Demo
navTitle: Layouts Demo
position: 7
---

# Layouts Demo

This page shows the layout widgets at work: `FlowLayout`, `TabLayout` and `Card`, alone and inside each other. Change the width of the browser window while you read it, because a flow only shows what it does when its width changes.

## Flow with the default width

A `FlowLayout` without arguments gives every cell at least 320 pixels. Make the window narrower and wider and watch the three motors wrap: the number of columns follows the width.

~~~~markdown
:: HomeBlaze.Components.FlowLayout
::: Item
:::: HomeBlaze.Components.Widget path=../Compressor
::::
:::
::: Item
:::: HomeBlaze.Components.Widget path=../WaterPump
::::
:::
::: Item
:::: HomeBlaze.Components.Widget path=../Conveyor
::::
:::
::
~~~~

:: HomeBlaze.Components.FlowLayout
::: Item
:::: HomeBlaze.Components.Widget path=../Compressor
::::
:::
::: Item
:::: HomeBlaze.Components.Widget path=../WaterPump
::::
:::
::: Item
:::: HomeBlaze.Components.Widget path=../Conveyor
::::
:::
::

## Flow with a small width

With `minWidth=120` a cell may get as narrow as 120 pixels, so many cells fit into one row. The six cards wrap only when the window gets very narrow.

:: HomeBlaze.Components.FlowLayout minWidth=120
::: Item
:::: HomeBlaze.Components.Card title="One"
First cell
::::
:::
::: Item
:::: HomeBlaze.Components.Card title="Two"
Second cell
::::
:::
::: Item
:::: HomeBlaze.Components.Card title="Three"
Third cell
::::
:::
::: Item
:::: HomeBlaze.Components.Card title="Four"
Fourth cell
::::
:::
::: Item
:::: HomeBlaze.Components.Card title="Five"
Fifth cell
::::
:::
::: Item
:::: HomeBlaze.Components.Card title="Six"
Sixth cell
::::
:::
::

## Flow with a large width

With `minWidth=600` a cell is never narrower than 600 pixels, as long as the page is that wide. The two cards stand next to each other only in a wide window, and below each other in every other one.

:: HomeBlaze.Components.FlowLayout minWidth=600
::: Item
:::: HomeBlaze.Components.Card title="Cooling Fan" icon=AcUnit
The cooling fan has a temperature of {{ ../CoolingFan/Temperature }} °C.

::::: HomeBlaze.Components.Widget path=../CoolingFan
:::::
::::
:::
::: Item
:::: HomeBlaze.Components.Card title="Exhaust Fan" icon=Air
The exhaust fan runs at {{ ../ExhaustFan/CurrentSpeed }} RPM.

::::: HomeBlaze.Components.Widget path=../ExhaustFan
:::::
::::
:::
::

## Tabs with names and icons

Each `Tab` block is one tab with a `title` and an optional `icon`. The `#name` after `Tab` is what the page knows the tab by: a tab with a name keeps its content and stays selected when tabs are added, removed or moved in the page text.

~~~~markdown
:: HomeBlaze.Components.TabLayout
::: Tab#cooling title="Cooling Fan" icon=AcUnit
:::: HomeBlaze.Components.Widget path=../CoolingFan
::::
:::
::: Tab#exhaust title="Exhaust Fan" icon=Air
:::: HomeBlaze.Components.Widget path=../ExhaustFan
::::
:::
::: Tab#notes title="Notes" icon=Notes
A tab can hold any page content, not only widgets.
:::
::
~~~~

:: HomeBlaze.Components.TabLayout
::: Tab#cooling title="Cooling Fan" icon=AcUnit
:::: HomeBlaze.Components.Widget path=../CoolingFan
::::
:::
::: Tab#exhaust title="Exhaust Fan" icon=Air
:::: HomeBlaze.Components.Widget path=../ExhaustFan
::::
:::
::: Tab#notes title="Notes" icon=Notes
A tab can hold any page content, not only widgets.
:::
::

## Tabs without names

A tab without a name is known by its position only. It looks the same, but when the page text changes, the selection stays at the position, also when another tab stands there now.

:: HomeBlaze.Components.TabLayout
::: Tab title="First"
The first tab has no name and no icon.
:::
::: Tab title="Second"
The second tab has no name and no icon.
:::
::

## Cards in a flow inside a tab

A block inside another block has more colons than the block around it. Here a tab layout holds a tab, the tab holds a flow, the flow holds items, an item holds a card and the card holds a widget, so the fences count up from two colons to seven.

~~~~markdown
:: HomeBlaze.Components.TabLayout
::: Tab#pumps title="Pumps" icon=WaterDrop
:::: HomeBlaze.Components.FlowLayout minWidth=320
::::: Item
:::::: HomeBlaze.Components.Card title="Water Pump" icon=WaterDrop
::::::: HomeBlaze.Components.Widget path=../WaterPump
:::::::
::::::
:::::
::::: Item
:::::: HomeBlaze.Components.Card title="Compressor" icon=Bolt
::::::: HomeBlaze.Components.Widget path=../Compressor
:::::::
::::::
:::::
::::
:::
::: Tab#belt title="Conveyor Belt" icon=Settings
The conveyor runs at {{ ../Conveyor/CurrentSpeed }} RPM.
:::
::
~~~~

:: HomeBlaze.Components.TabLayout
::: Tab#pumps title="Pumps" icon=WaterDrop
:::: HomeBlaze.Components.FlowLayout minWidth=320
::::: Item
:::::: HomeBlaze.Components.Card title="Water Pump" icon=WaterDrop
::::::: HomeBlaze.Components.Widget path=../WaterPump
:::::::
::::::
:::::
::::: Item
:::::: HomeBlaze.Components.Card title="Compressor" icon=Bolt
::::::: HomeBlaze.Components.Widget path=../Compressor
:::::::
::::::
:::::
::::
:::
::: Tab#belt title="Conveyor Belt" icon=Settings
The conveyor runs at {{ ../Conveyor/CurrentSpeed }} RPM.
:::
::

## Tabs inside a card

A card takes any page content, also a layout. The tab layout is the body of this card, and the footer of the card stays below the tabs whichever tab is selected.

:: HomeBlaze.Components.Card title="Fans" icon=Air
::: HomeBlaze.Components.TabLayout
:::: Tab#cardcooling title="Cooling Fan"
::::: HomeBlaze.Components.Widget path=../CoolingFan
:::::
::::
:::: Tab#cardexhaust title="Exhaust Fan"
::::: HomeBlaze.Components.Widget path=../ExhaustFan
:::::
::::
:::

::: Footer
Both fans are shown by reference, so they are the same motors as in the sections above.
:::
::

## Cards

A card has an optional `title`, an optional `icon` and an optional `Footer` block. The first card has all three. The second card has none of them and is a plain box around its content.

:: HomeBlaze.Components.Card title="Conveyor Belt" icon=Settings
The conveyor runs at {{ ../Conveyor/CurrentSpeed }} RPM.

::: HomeBlaze.Components.Widget path=../Conveyor
:::

::: Footer
Target speed: {{ ../Conveyor/TargetSpeed }} RPM
:::
::

:: HomeBlaze.Components.Card
This card has no title, no icon and no footer.
::
