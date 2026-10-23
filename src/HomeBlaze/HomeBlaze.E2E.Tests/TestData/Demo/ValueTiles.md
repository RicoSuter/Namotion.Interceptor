---
title: Value Tiles Demo
---

# Value Tiles Demo

This page is changed by the value tile widget tests.

:: HomeBlaze.Samples.Motor#valuemotor Name=ValueMotor TargetSpeed=800 SimulationInterval=00:00:01
::

:: HomeBlaze.Components.ValueMap#runningmap
::: Case Value=True Text="Running" Color=Warning Icon=Bolt
::

:: HomeBlaze.Components.GridLayout#valuegrid Rows=1 Columns=4 CellAspectRatio=1:1
::: Cell Row=0 Column=0
:::: HomeBlaze.Components.ValueTile Kind=Tile Path=valuemotor/TargetSpeed Label="Target" Icon=Speed
::: Cell Row=0 Column=1
:::: HomeBlaze.Components.ValueTile Kind=Tile Path=valuemotor/IsPowered Map=runningmap Label="Powered"
::: Cell Row=0 Column=2
:::: HomeBlaze.Components.ValueTile Kind=Tile Path=valuemotor/DoesNotExist Label="Missing"
::: Cell Row=0 Column=3
:::: HomeBlaze.Components.OperationButton#icontile Kind=Tile Path=valuemotor/PowerOn
::

:: HomeBlaze.Components.ValueTile Path=valuemotor/TargetSpeed Label="Box target"
::

:: HomeBlaze.Components.ValueTile Kind=Tile Path=valuemotor/TargetSpeed Label="Alone"
::

:: HomeBlaze.Components.CanvasLayout#gaugecanvas MinHeight=200 Width=400
::: Node X=0 Y=0 Width=200 Height=200
:::: HomeBlaze.Components.Gauge Path=valuemotor/TargetSpeed Label="Canvas gauge" Size=Small
::

:: HomeBlaze.Components.GridLayout#gaugegrid Rows=1 Columns=4 CellAspectRatio=1:1
::: Cell Row=0 Column=0
:::: HomeBlaze.Components.Gauge Path=valuemotor/TargetSpeed Label="Grid gauge"
::

:: HomeBlaze.Components.FlowLayout MinWidth=168
::: Item
:::: HomeBlaze.Components.Gauge Path=valuemotor/TargetSpeed Label="Flow small" Size=Small
::: Item
:::: HomeBlaze.Components.Gauge Path=valuemotor/TargetSpeed Label="Flow large" Size=Large
::

:: HomeBlaze.Components.ValueList
::: Value Path=valuemotor/TargetSpeed Label="List target"
::

:: HomeBlaze.Components.CanvasLayout#mincanvas MinHeight=200 Width=400 MinWidth=600 Scale=FitWidth
::: Node X=0 Y=0 Width=200 Height=200
:::: HomeBlaze.Components.ValueTile Kind=Tile Path=valuemotor/TargetSpeed Label="Min canvas tile"
::
