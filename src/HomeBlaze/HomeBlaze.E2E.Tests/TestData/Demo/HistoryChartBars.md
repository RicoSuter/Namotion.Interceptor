---
title: History Chart Bars
---

# History Chart Bars

:: HomeBlaze.Samples.Motor#barmotor Name=BarMotor TargetSpeed=900 SimulationInterval=00:00:01
::

:: HomeBlaze.History.Blazor.HistoryChart Range=7d Bucket=day Aggregation=Delta Height=200
::: Series Path=barmotor/Energy Label="Daily energy"
::

:: HomeBlaze.History.Blazor.HistoryChart Range=7d Bucket=day Aggregation=Delta Height=200
::: Series Path=barmotor/Energy Label="Taken"
::: Series Path=barmotor/Energy Label="Fed in" Negate=true
::

:: HomeBlaze.History.Blazor.HistoryChart Range=1h Height=150
::: Series Path=barmotor/CurrentSpeed Label="Peak speed" Aggregation=Maximum
::: Series Path=barmotor/CurrentSpeed Label="Mean speed"
::
