---
title: Blocks Demo
---

# Blocks Demo

:: HomeBlaze.Components.Card title="Motor Card" icon=Settings
Card body with a live value: speed is {{ cardmotor/CurrentSpeed }} RPM.

::: HomeBlaze.Samples.Motor#cardmotor name=CardMotor targetSpeed=1200 simulationInterval=00:00:01
:::

::: Footer
Card footer text
:::
::

:: HomeBlaze.Components.Card title="Reference Card" icon=Link
The motor of the first card, referenced by its name:

::: HomeBlaze.Components.Widget path=cardmotor
:::

A motor next to this page, referenced by a relative path:

::: HomeBlaze.Components.Widget path=../Motor
:::

The named motor has a temperature of {{ cardmotor/Temperature }} °C.
::

## Flow

:: HomeBlaze.Components.FlowLayout minWidth=240
::: Item
:::: HomeBlaze.Components.Card title="Flow One"
First cell
::::
:::
::: Item
:::: HomeBlaze.Components.Card title="Flow Two"
Second cell
::::
:::
::: Item
Plain text cell
:::
::

## Tabs

:: HomeBlaze.Components.TabLayout
::: Tab title="First Tab" icon=Home
Content of the first tab
:::
::: Tab title="Second Tab"
Content of the second tab
:::
::
