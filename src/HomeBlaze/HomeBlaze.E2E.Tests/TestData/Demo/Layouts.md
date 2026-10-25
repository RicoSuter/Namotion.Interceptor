---
title: Layouts Demo
---

# Layouts Demo

:: HomeBlaze.Components.FlowLayout MinWidth=240
::: Item
:::: HomeBlaze.Samples.Motor#flowmotor Name=FlowMotor TargetSpeed=800 SimulationInterval=00:00:01
::: Item
:::: HomeBlaze.Components.Card Title="Beside The Motor"
A card next to a widget that has a minimum width.
::

:: HomeBlaze.Components.TabLayout
::: Tab Title="Card Tab"
:::: HomeBlaze.Components.Card Title="Card In A Tab"
The only content of the tab.
::: Tab Title="Text Tab"
Text only
::
