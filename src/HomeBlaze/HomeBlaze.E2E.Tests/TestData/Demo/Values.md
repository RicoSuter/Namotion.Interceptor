---
title: Values Demo
---

# Values Demo

:: HomeBlaze.Samples.Motor#valuemotor Name=ValueMotor TargetSpeed=800 SimulationInterval=00:00:01
::

:: HomeBlaze.Components.Card Title="Linked Card" Subtitle="Card subtitle" Icon=Bolt IconColor=Warning Href="Blocks.md" Outlined=true
Body of the linked card.
::: HeaderRight
Corner text
::

:: HomeBlaze.Components.Card Title="Plain Card"
Body of the plain card.
::

:: HomeBlaze.Components.ValueMap#status
::: Case Value=Stopped Text="steht" Color=Error
::: Case Value=Starting Text="startet" Color=Warning
::: Case Value=Running Text="läuft" Color=Success
::: Case Value=Stopping Text="stoppt" Color=Warning
::

:: HomeBlaze.Components.ValueList
::: Value Path=valuemotor/TargetSpeed Label="Soll" Icon=Speed
::: Value Path=valuemotor/CurrentSpeed Label="Ist"
::: Value Path=valuemotor/Temperature
::: Value Path=valuemotor/Status Label="Zustand" Map=status
::: Value Path=valuemotor/Missing Label="Fehlt"
::: Value Label="Zwei Werte"
{{ valuemotor/TargetSpeed }} von {{ valuemotor/TargetSpeed | raw }}
::

:: HomeBlaze.Components.ValueTile Path=valuemotor/TargetSpeed Label="Tile Target" Icon=Speed
::
