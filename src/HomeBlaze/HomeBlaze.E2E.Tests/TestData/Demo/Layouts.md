---
title: Layouts Demo
---

# Layouts Demo

:: HomeBlaze.Components.FlowLayout minWidth=240
::: Item
:::: HomeBlaze.Samples.Motor#flowmotor name=FlowMotor targetSpeed=800 simulationInterval=00:00:01
::::
:::
::: Item
:::: HomeBlaze.Components.Card title="Beside The Motor"
A card next to a widget that has a minimum width.
::::
:::
::

:: HomeBlaze.Components.TabLayout
::: Tab title="Card Tab"
:::: HomeBlaze.Components.Card title="Card In A Tab"
The only content of the tab.
::::
:::
::: Tab title="Text Tab"
Text only
:::
::
