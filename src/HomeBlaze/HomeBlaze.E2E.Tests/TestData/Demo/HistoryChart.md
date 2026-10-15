---
title: History Chart Demo
---

# History Chart Demo

:: HomeBlaze.Samples.Motor#chartmotor Name=ChartMotor TargetSpeed=900 SimulationInterval=00:00:01
::

:: HomeBlaze.History.Blazor.HistoryChart Range=1h
::: Series Path=chartmotor/CurrentSpeed Label="Chart speed"
::: Series Path=chartmotor/Status Label="Chart status"
::

:: HomeBlaze.History.Blazor.HistoryChart Range=1h
::: Series Path=chartmotor/CurrentSpeed Label="Mixed speed"
::: Series Path=chartmotor/Temperature Label="Mixed temperature"
::

:: HomeBlaze.Components.TabLayout
::: Tab Title="First"
The chart is in the second tab, which is hidden when the page opens.
::: Tab Title="Speed"
:::: HomeBlaze.History.Blazor.HistoryChart Range=6h Height=200
::::: Series Path=chartmotor/CurrentSpeed Label="Tab speed"
::
