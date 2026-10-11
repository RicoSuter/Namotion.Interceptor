---
title: Blocks Demo
navTitle: Blocks Demo
position: 6
---

# Blocks Demo

This page demonstrates blocks that take page content: a card with a footer, a flow layout and tabs, and the same card with and without its closing lines.

Each area has a page of its own to try things out:

- [Page Widgets](../Docs/administration/page-widgets.md): the reference of the widgets, which shows flows, tabs, cards, a grid and a canvas live, alone and inside each other
- [Dashboard](Dashboard.md): a complete dashboard built from these widgets
- [References](References.md): names, relative and absolute paths, and what a reference cannot show
- [Configuration](Configuration.md): values on the head line, the `json` block, style blocks and the page editor
- [Errors](Errors.md): the common error boxes, each with the block that causes it and how to fix it

:: HomeBlaze.Components.Card Title="Motor Card" Icon=Settings
Card body with a live value: speed is {{ cardmotor/CurrentSpeed }} RPM.

::: HomeBlaze.Samples.Motor#cardmotor Name=CardMotor TargetSpeed=1200 SimulationInterval=00:00:01
:::

::: Footer
Card footer text
:::
::

:: HomeBlaze.Components.Card Title="Reference Card" Icon=Link
The motor of the first card, referenced by its name:

::: HomeBlaze.Components.Widget Path=cardmotor
:::

A motor next to this page, referenced by a relative path:

::: HomeBlaze.Components.Widget Path=../Conveyor
:::

The named motor has a temperature of {{ cardmotor/Temperature }} °C.
::

## Flow

Each `Item` block is one cell. The cells are equally wide, at least `MinWidth` pixels, and wrap to as many rows as the screen needs.

:: HomeBlaze.Components.FlowLayout MinWidth=320
::: Item
:::: HomeBlaze.Components.Card Title="Compressor" Icon=Bolt
::::: HomeBlaze.Components.Widget Path=../Compressor
:::::
::::
:::
::: Item
:::: HomeBlaze.Components.Card Title="Water Pump" Icon=WaterDrop
::::: HomeBlaze.Components.Widget Path=../WaterPump
:::::
::::
:::
::: Item
:::: HomeBlaze.Components.Card Title="Notes"
A cell can hold any page content, not only widgets.

The compressor runs at {{ ../Compressor/CurrentSpeed }} RPM.
::::
:::
::

## Tabs

Each `Tab` block is one tab. A tab with a name, like `#cooling`, stays the same tab when tabs are added, removed or moved.

:: HomeBlaze.Components.TabLayout
::: Tab#cooling Title="Cooling Fan" Icon=Home
The cooling fan has a temperature of {{ ../CoolingFan/Temperature }} °C.

:::: HomeBlaze.Components.Widget Path=../CoolingFan
::::
:::
::: Tab#exhaust Title="Exhaust Fan"
The exhaust fan runs at {{ ../ExhaustFan/CurrentSpeed }} RPM.

:::: HomeBlaze.Components.Widget Path=../ExhaustFan
::::
:::
::

## Closing lines

A closing line is needed only before something that belongs further out. The two cards below are the same card. The first has every closing line. The second has none: the head line of its footer ends the motor, and the end of the page ends the footer and the card. [Closing and Nesting](../Docs/administration/pages.md#closing-and-nesting) has the rule.

~~~~markdown
:: HomeBlaze.Components.Card Title="Every Closing Line" Icon=Settings
The motor runs at {{ closedmotor/CurrentSpeed }} RPM.

::: HomeBlaze.Samples.Motor#closedmotor Name=ClosedMotor TargetSpeed=900 SimulationInterval=00:00:01
:::

::: Footer
Target speed: {{ closedmotor/TargetSpeed }} RPM
:::
::

:: HomeBlaze.Components.Card Title="Short Form" Icon=Settings
The motor runs at {{ openmotor/CurrentSpeed }} RPM.

::: HomeBlaze.Samples.Motor#openmotor Name=OpenMotor TargetSpeed=900 SimulationInterval=00:00:01

::: Footer
Target speed: {{ openmotor/TargetSpeed }} RPM
~~~~

:: HomeBlaze.Components.Card Title="Every Closing Line" Icon=Settings
The motor runs at {{ closedmotor/CurrentSpeed }} RPM.

::: HomeBlaze.Samples.Motor#closedmotor Name=ClosedMotor TargetSpeed=900 SimulationInterval=00:00:01
:::

::: Footer
Target speed: {{ closedmotor/TargetSpeed }} RPM
:::
::

:: HomeBlaze.Components.Card Title="Short Form" Icon=Settings
The motor runs at {{ openmotor/CurrentSpeed }} RPM.

::: HomeBlaze.Samples.Motor#openmotor Name=OpenMotor TargetSpeed=900 SimulationInterval=00:00:01

::: Footer
Target speed: {{ openmotor/TargetSpeed }} RPM
