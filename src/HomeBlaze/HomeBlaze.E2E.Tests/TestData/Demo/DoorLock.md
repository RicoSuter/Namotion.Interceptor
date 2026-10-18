---
title: Door Lock Demo
---

# Door Lock Demo

This page is changed by the door lock tile tests.

:: HomeBlaze.Samples.SimulatedDoorLock#frontdoor Name="Front door" TransitionDuration=00:00:00.500
::

:: HomeBlaze.Components.GridLayout#lockgrid Rows=1 Columns=2 CellAspectRatio=1:1
::: Cell Row=0 Column=0
:::: HomeBlaze.Components.DoorLock#locktile Path=frontdoor
::: Cell Row=0 Column=1
:::: HomeBlaze.Components.DoorLock#missinglocktile Path=doesnotexist Label="Missing"
::
