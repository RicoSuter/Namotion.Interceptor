---
title: Selection Demo
---

# Selection Demo

:: HomeBlaze.Samples.Motor#selectionmotor Name=SelectionMotor TargetSpeed=1000 SimulationInterval=00:00:01
::

:: HomeBlaze.Components.Card Title="Selection Card"
Body of the selection card.
::: HeaderRight
:::: HomeBlaze.Components.Indicator Path=selectionmotor/Status
::

:: HomeBlaze.Components.Switch Path=selectionmotor/IsPowered
::

:: HomeBlaze.Components.Slider Path=selectionmotor/TargetSpeed
::

:: HomeBlaze.Components.OperationButton Path=selectionmotor/PowerOff
::

:: HomeBlaze.Components.Card Title="List Card"
::: HomeBlaze.Components.ValueList
:::: Value Path=selectionmotor/TargetSpeed Label="Nested Row"
::

:: HomeBlaze.Components.ValueList
::: Value Path=selectionmotor/TargetSpeed Label="Top Level Row"
::
