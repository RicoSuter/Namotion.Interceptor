---
title: Blocks Demo
---

# Blocks Demo

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

::: HomeBlaze.Components.Widget Path=../Motor
:::

The named motor has a temperature of {{ cardmotor/Temperature }} °C.
::

## Flow

:: HomeBlaze.Components.FlowLayout MinWidth=240
::: Item
:::: HomeBlaze.Components.Card Title="Flow One"
First cell
::::
:::
::: Item
:::: HomeBlaze.Components.Card Title="Flow Two"
Second cell
::::
:::
::: Item
Plain text cell
:::
::

## Tabs

:: HomeBlaze.Components.TabLayout
::: Tab Title="First Tab" Icon=Home
Content of the first tab
:::
::: Tab Title="Second Tab"
Content of the second tab
:::
::
