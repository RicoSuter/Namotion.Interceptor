---
title: Tiles Demo
---

# Tiles Demo

This page is changed by the tile widget tests.

:: HomeBlaze.Samples.Motor#tilemotor Name=TileMotor TargetSpeed=800 SimulationInterval=00:00:01
::

:: HomeBlaze.Components.ValueMap#powered
::: Case Value=True Text="Running" Color=Warning Icon=Bolt
::: Case Value=False Text="Off" Icon=PowerOff
::

:: HomeBlaze.Components.GridLayout#tilegrid Rows=1 Columns=4
::: Cell Row=0 Column=0
:::: HomeBlaze.Components.Switch#powertile Kind=Tile Path=tilemotor/IsPowered On=tilemotor/PowerOn Off=tilemotor/PowerOff Map=powered Label="Motor" Subtitle="Hall"
::::: Badge
:::::: HomeBlaze.Components.Indicator Path=tilemotor/IsPowered Kind=Icon Icon=DirectionsRun
::: Cell Row=0 Column=1
:::: HomeBlaze.Components.Switch#plaintile Kind=Tile Path=tilemotor/IsPowered Label="Plain"
::: Cell Row=0 Column=2
:::: HomeBlaze.Components.Switch#missingtile Kind=Tile Path=tilemotor/DoesNotExist Label="Missing"
::

:: HomeBlaze.Components.ValueMap#falseonly
::: Case Value=False Text="Idle"
::

:: HomeBlaze.Components.GridLayout#casegrid Rows=1 Columns=2
::: Cell Row=0 Column=0
:::: HomeBlaze.Components.Switch#nocasetile Kind=Tile Path=tilemotor/IsPowered Map=falseonly Label="No case"
::: Cell Row=0 Column=1
:::: HomeBlaze.Components.Switch#casetexttile Kind=Tile Path=tilemotor/IsPowered Map=powered Label="Case text"
::
