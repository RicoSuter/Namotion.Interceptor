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

:: HomeBlaze.Components.ValueMap#lamp
::: Case Value=Stopped Icon=Lightbulb
::: Case Value=Starting Icon=Lightbulb
::: Case Value=Running Icon=Lightbulb
::: Case Value=Stopping Icon=Lightbulb
::: Case Value=Error Icon=Lightbulb
::

:: HomeBlaze.Components.ValueList
::: Value Path=valuemotor/Status Label="Lampe" Icon=Speed Map=lamp
::: Value Label="Lampe fest" Icon=Lightbulb
::: Value Path=valuemotor/Status Label="Ohne Symbol"
::

:: HomeBlaze.Components.ValueList
::: Value Path=valuemotor/Status Label="Nur Text"
::: Value Path=valuemotor/TargetSpeed Label="Auch Text"
::

:: HomeBlaze.Components.ValueTile Path=valuemotor/TargetSpeed Label="Tile Target" Icon=Speed
::

:: HomeBlaze.Components.Indicator Path=valuemotor/Status Map=status
::

:: HomeBlaze.Components.Indicator Path=valuemotor/Status Map=status Kind=Icon Icon=Circle
::

:: HomeBlaze.Components.Indicator Path=valuemotor/Status Map=nomap
::

:: HomeBlaze.Components.Progress Path=valuemotor/TargetSpeed Min=0 Max=1600
::

:: HomeBlaze.Components.Progress Path=valuemotor/Name
::

:: HomeBlaze.Components.Card Title="Picture Card" Image="/favicon.ico"
Body of the picture card.
::: Footer
Picture card footer
::

:: HomeBlaze.Components.ValueList
::: Value Path=valuemotor/TargetSpeed Label="Balken"
:::: HomeBlaze.Components.Progress Path=valuemotor/TargetSpeed Min=0 Max=1600
::

:: HomeBlaze.Components.ExpansionPanels
::: Panel Title="Open Panel" Icon=Bed Expanded=true
Open panel target {{ valuemotor/TargetSpeed }}
::: Panel Title="Closed Panel"
Closed panel speed {{ valuemotor/CurrentSpeed }} RPM
::

:: HomeBlaze.Components.ExpansionPanels Single=true
::: Panel Title="Exclusive A" Expanded=true
Exclusive A text
::: Panel Title="Exclusive B"
Exclusive B text
::

:: HomeBlaze.Components.Alert Severity=Warning
Alert text
::
