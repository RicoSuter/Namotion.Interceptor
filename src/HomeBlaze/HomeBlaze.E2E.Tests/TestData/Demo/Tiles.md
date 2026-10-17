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
::: Cell Row=0 Column=3
:::: HomeBlaze.Components.OperationButton#ontile Kind=Tile Path=tilemotor/PowerOn
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

:: HomeBlaze.Components.ValueMap#colorlessmap
::: Case Value=True Text="Colorless"
::

:: HomeBlaze.Components.Switch#colorlesstile Kind=Tile Path=tilemotor/IsPowered Map=colorlessmap Label="Colorless case"
::

:: HomeBlaze.Components.Switch#unresolvedmaptile Kind=Tile Path=tilemotor/IsPowered Map=unresolvedtilemap Label="Unresolved map"
::

:: HomeBlaze.Components.Switch#blockedtile Kind=Tile Path=tilemotor/IsPowered Label="Blocked tile" On=tilemotor/PowerOff Off=tilemotor/PowerOn
::

:: HomeBlaze.Components.OperationButton#nowheretile Kind=Tile Path=tilemotor/DoesNotExistEither
::

:: HomeBlaze.Components.GridLayout#squaregrid Rows=2 Columns=3 CellAspectRatio=1:1
::: Cell Row=0 Column=0
:::: HomeBlaze.Components.Switch#squaretile Kind=Tile Path=tilemotor/IsPowered Label="Square"
::

:: HomeBlaze.Components.GridLayout#widegrid Rows=1 Columns=9 CellAspectRatio=1:1
::: Cell Row=0 Column=0
:::: HomeBlaze.Components.Switch#narrowtile Kind=Tile Path=tilemotor/IsPowered Label="Narrow"
::

:: HomeBlaze.Components.CanvasLayout#fitcanvas MinHeight=200 Width=400 Scale=FitWidth
::: Node X=0 Y=0 Width=200 Height=200
:::: HomeBlaze.Components.Switch#canvastile Kind=Tile Path=tilemotor/IsPowered Label="Canvas tile"
::
