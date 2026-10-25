---
title: Live Layouts Demo
---

# Live Layouts Demo

:: HomeBlaze.Samples.Motor#livemotor Name=LiveMotor TargetSpeed=800 SimulationInterval=00:00:01
::

:: HomeBlaze.Components.GridLayout#livegrid Rows=1 Columns=2
::: Cell Row=0 Column=0
:::: HomeBlaze.Components.ValueTile Path=livemotor/Temperature Label="Grid Temperature"
::

:: HomeBlaze.Components.CanvasLayout#livecanvas MinHeight=200
::: Node X=20 Y=20 Width=300 Height=120
:::: HomeBlaze.Components.ValueTile Path=livemotor/Temperature Label="Canvas Temperature"
::
