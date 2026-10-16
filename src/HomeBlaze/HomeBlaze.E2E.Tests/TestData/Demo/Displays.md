---
title: Displays Demo
---

# Displays Demo

:: HomeBlaze.Samples.Motor#dispmotor Name="Display Motor" TargetSpeed=1000 SimulationInterval=00:00:01
:: HomeBlaze.Samples.Motor#dispmotor2 Name="Second Motor" TargetSpeed=600 SimulationInterval=00:00:01
::

:: HomeBlaze.Components.Gauge Path=dispmotor/TargetSpeed Minimum=0 Maximum=2000 Label="Gauge Target"
::

:: HomeBlaze.Components.Gauge Path=dispmotor/Missing Minimum=0 Maximum=2000 Label="Gauge Missing"
::

:: HomeBlaze.Components.Gauge Path=dispmotor/CurrentSpeed Label="Gauge No Range"
::

:: HomeBlaze.Components.SubjectTable#disptable Source="../Displays.md" Type=HomeBlaze.Samples.Motor
::: Column Property=Name Label="Motor"
::: Column Property=TargetSpeed Label="Target"
::

:: HomeBlaze.Components.ValueMap#dispstatus
::: Case Value=Stopped Text="stopped" Color=Error
::: Case Value=Starting Text="starting" Color=Warning
::: Case Value=Running Text="running" Color=Success
::: Case Value=Stopping Text="stopping" Color=Warning
::

:: HomeBlaze.Components.SubjectTable Source="../Displays.md" Type=HomeBlaze.Samples.Motor
::: Column Property=Status Label="State" Map=dispstatus
::

:: HomeBlaze.Components.SubjectTable Source="../Displays.md" Type=HomeBlaze.Samples.Motor
::: Column Property=Missing Label="Gap"
::

:: HomeBlaze.Components.Gauge Path=dispmotor/TargetSpeed Minimum=0 Maximum=2000 Label="Gauge Broken Map" Map=nogaugemap
::

:: HomeBlaze.Components.SubjectTable Source="../Displays.md" Type=HomeBlaze.Samples.Motor
::: Column Property=Name Label="Broken Name"
::: Column Property=Status Label="Broken State" Map=nocolumnmap
::

:: HomeBlaze.Components.Progress Path=dispmotor/TargetSpeed Minimum=2000 Maximum=0
::

:: HomeBlaze.Components.Responsive Show=NotPhone
Wide screen text
::

:: HomeBlaze.Components.Responsive Show=Phone
Phone screen text
::
