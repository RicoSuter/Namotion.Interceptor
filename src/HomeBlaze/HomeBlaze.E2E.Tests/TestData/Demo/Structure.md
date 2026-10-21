---
title: Structure Demo
---

# Structure Demo

:: HomeBlaze.Samples.Motor#structuremotor Name=StructureMotor TargetSpeed=1000 SimulationInterval=00:00:01
::

:: HomeBlaze.Components.Switch Path=structuremotor/IsPowered
::

:: HomeBlaze.Components.ValueList
::: Value Path=structuremotor/TargetSpeed Label="Row A"
:::
::: Value Path=structuremotor/CurrentSpeed Label="Row B"
:::
::

:: HomeBlaze.Components.ValueMap#structuremap
::: Case Value=Stopped Text="Off"
:::
::

:: HomeBlaze.Components.Responsive To=Xs
::: HomeBlaze.Components.Card Title="Phone Card"
Only on phones.
:::
::
