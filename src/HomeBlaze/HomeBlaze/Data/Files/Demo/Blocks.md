---
title: Blocks Demo
navTitle: Blocks Demo
position: 6
---

# Blocks Demo

This page demonstrates blocks that take page content: a card with a footer, a flow layout and tabs.

::::HomeBlaze.Components.Card title="Motor Card" icon=Settings
Card body with a live value: speed is {{ cardmotor/CurrentSpeed }} RPM.

:::HomeBlaze.Samples.Motor#cardmotor name=CardMotor targetSpeed=1200 simulationInterval=00:00:01
:::

:::Footer
Card footer text
:::
::::

::::HomeBlaze.Components.Card title="Reference Card" icon=Link
The motor of the first card, referenced by its name:

:::HomeBlaze.Components.Widget path=cardmotor
:::

A motor next to this page, referenced by a relative path:

:::HomeBlaze.Components.Widget path=../Conveyor
:::

The named motor has a temperature of {{ cardmotor/Temperature }} °C.
::::

## Flow

Each `Item` block is one cell. The cells are equally wide, at least `minWidth` pixels, and wrap to as many rows as the screen needs.

::::::HomeBlaze.Components.FlowLayout minWidth=320
:::::Item
::::HomeBlaze.Components.Card title="Compressor" icon=Bolt
:::HomeBlaze.Components.Widget path=../Compressor
:::
::::
:::::
:::::Item
::::HomeBlaze.Components.Card title="Water Pump" icon=WaterDrop
:::HomeBlaze.Components.Widget path=../WaterPump
:::
::::
:::::
:::::Item
::::HomeBlaze.Components.Card title="Notes"
A cell can hold any page content, not only widgets.

The compressor runs at {{ ../Compressor/CurrentSpeed }} RPM.
::::
:::::
::::::

## Tabs

Each `Tab` block is one tab. A tab with a name, like `#cooling`, stays the same tab when tabs are added, removed or moved.

:::::HomeBlaze.Components.TabLayout
::::Tab#cooling title="Cooling Fan" icon=Home
The cooling fan has a temperature of {{ ../CoolingFan/Temperature }} °C.

:::HomeBlaze.Components.Widget path=../CoolingFan
:::
::::
::::Tab#exhaust title="Exhaust Fan"
The exhaust fan runs at {{ ../ExhaustFan/CurrentSpeed }} RPM.

:::HomeBlaze.Components.Widget path=../ExhaustFan
:::
::::
:::::
