---
title: Card Spacing Demo
navTitle: Card Spacing Demo
---

# Card Spacing Demo

This page shows cards in their common variants side by side, so that the spacing inside a card can be compared: between the header and the body, between the rows of a legend that wraps and between the rows of a value list. Every value comes from the simulated motors of the [Dashboard Demo](Dashboard.md). The lines show the last hour, so a chart fills up while HomeBlaze runs.

## Charts

:: HomeBlaze.Components.FlowLayout MinWidth=420
::: Item
:::: HomeBlaze.Components.Card Title="Many Series" Subtitle="With a subtitle, the legend wraps." Icon=Timeline
::::: HomeBlaze.History.Blazor.HistoryChart#manywithsubtitle Range=1h Height=300
:::::: Series Path=/Demo/Compressor/CurrentSpeed Label="Compressor"
:::::: Series Path=/Demo/WaterPump/CurrentSpeed Label="Water Pump"
:::::: Series Path=/Demo/Conveyor/CurrentSpeed Label="Conveyor"
:::::: Series Path=/Demo/CoolingFan/CurrentSpeed Label="Cooling Fan"
:::::: Series Path=/Demo/ExhaustFan/CurrentSpeed Label="Exhaust Fan"
:::::: Series Path=/Demo/Compressor/TargetSpeed Label="Compressor Target"
:::::: Series Path=/Demo/WaterPump/TargetSpeed Label="Water Pump Target"
::: Item
:::: HomeBlaze.Components.Card Title="Many Series" Icon=Timeline
::::: HomeBlaze.History.Blazor.HistoryChart#manywithoutsubtitle Range=1h Height=300
:::::: Series Path=/Demo/Compressor/CurrentSpeed Label="Compressor"
:::::: Series Path=/Demo/WaterPump/CurrentSpeed Label="Water Pump"
:::::: Series Path=/Demo/Conveyor/CurrentSpeed Label="Conveyor"
:::::: Series Path=/Demo/CoolingFan/CurrentSpeed Label="Cooling Fan"
:::::: Series Path=/Demo/ExhaustFan/CurrentSpeed Label="Exhaust Fan"
:::::: Series Path=/Demo/Compressor/TargetSpeed Label="Compressor Target"
:::::: Series Path=/Demo/WaterPump/TargetSpeed Label="Water Pump Target"
::: Item
:::: HomeBlaze.Components.Card Title="One Series" Subtitle="With a subtitle, the legend has one row." Icon=Timeline
::::: HomeBlaze.History.Blazor.HistoryChart#onewithsubtitle Range=1h Height=300
:::::: Series Path=/Demo/Compressor/Temperature Label="Compressor"
::: Item
:::: HomeBlaze.Components.Card Title="Two Series" Icon=Timeline
::::: HomeBlaze.History.Blazor.HistoryChart#twowithoutsubtitle Range=1h Height=300
:::::: Series Path=/Demo/Compressor/Temperature Label="Compressor"
:::::: Series Path=/Demo/WaterPump/Temperature Label="Water Pump"
::: Item
:::: HomeBlaze.Components.Card Title="Energy per Day" Subtitle="Bars of a counter, one group per day." Icon=CalendarMonth
::::: HomeBlaze.History.Blazor.HistoryChart#energybars Range=30d Bucket=day Aggregation=Delta Height=300
:::::: Series Path=/Demo/Compressor/Energy Label="Compressor"
:::::: Series Path=/Demo/WaterPump/Energy Label="Water Pump"
:::::: Series Path=/Demo/Conveyor/Energy Label="Conveyor"
:::::: Series Path=/Demo/CoolingFan/Energy Label="Cooling Fan" Negate=true
::: Item
:::: HomeBlaze.Components.Card Title="Speeds" Subtitle="Lines of the last hour." Icon=ShowChart
::::: HomeBlaze.History.Blazor.HistoryChart#speedlines Range=1h Height=300
:::::: Series Path=/Demo/Conveyor/CurrentSpeed Label="Conveyor"
:::::: Series Path=/Demo/CoolingFan/CurrentSpeed Label="Cooling Fan"
:::::: Series Path=/Demo/ExhaustFan/CurrentSpeed Label="Exhaust Fan"
::

## Value Lists

:: HomeBlaze.Components.FlowLayout MinWidth=300
::: Item
:::: HomeBlaze.Components.Card Title="One Entry" Subtitle="With a subtitle." Icon=Info
::::: HomeBlaze.Components.ValueList
:::::: Value Path=/Demo/Compressor/CurrentSpeed Icon=Speed
::: Item
:::: HomeBlaze.Components.Card Title="One Entry" Icon=Info
::::: HomeBlaze.Components.ValueList
:::::: Value Path=/Demo/Compressor/CurrentSpeed Icon=Speed
::: Item
:::: HomeBlaze.Components.Card Title="Four Entries" Subtitle="With icons and a subtitle." Icon=Bolt
::::: HomeBlaze.Components.ValueList
:::::: Value Path=/Demo/Compressor/CurrentSpeed Label="Speed" Icon=Speed
:::::: Value Path=/Demo/Compressor/Temperature Label="Temperature" Icon=Thermostat
:::::: Value Path=/Demo/Compressor/Load Label="Load" Icon=DonutLarge
:::::: Value Path=/Demo/Compressor/Energy Label="Energy" Icon=ElectricBolt
::: Item
:::: HomeBlaze.Components.Card Title="Four Entries" Icon=Bolt
::::: HomeBlaze.Components.ValueList
:::::: Value Path=/Demo/WaterPump/CurrentSpeed Label="Speed"
:::::: Value Path=/Demo/WaterPump/Temperature Label="Temperature"
:::::: Value Path=/Demo/WaterPump/Load Label="Load"
:::::: Value Path=/Demo/WaterPump/Energy Label="Energy"
::: Item
:::: HomeBlaze.Components.Card Title="Some Icons" Subtitle="Rows with and without an icon." Icon=Settings
::::: HomeBlaze.Components.ValueList
:::::: Value Path=/Demo/Conveyor/CurrentSpeed Label="Speed" Icon=Speed
:::::: Value Path=/Demo/Conveyor/Temperature Label="Temperature"
:::::: Value Path=/Demo/Conveyor/Status Label="Status" Icon=Info
::::::: HomeBlaze.Components.Indicator Path=/Demo/Conveyor/Status
:::::: Value Path=/Demo/Conveyor/Load Label="Load"
::::::: HomeBlaze.Components.Progress Path=/Demo/Conveyor/Load
::: Item
:::: HomeBlaze.Components.Card Title="Long Texts" Subtitle="A label and a value that wrap." Icon=WrapText
::::: HomeBlaze.Components.ValueList
:::::: Value Path=/Demo/CoolingFan/CurrentSpeed Label="The current speed of the cooling fan in the cabinet of the compressor station" Icon=AcUnit
:::::: Value Path=/Demo/CoolingFan/Name Label="Name" Icon=Label
:::::: Value Path=/Demo/CoolingFan/Temperature Label="Temperature" Icon=Thermostat
::

## Other Cards

:: HomeBlaze.Components.FlowLayout MinWidth=300
::: Item
:::: HomeBlaze.Components.Card Title="Only a Title"
::: Item
:::: HomeBlaze.Components.Card Title="Title, Subtitle and Text" Subtitle="A short line under the title." Icon=Article
The compressor runs at {{ /Demo/Compressor/CurrentSpeed }} RPM and the water pump at {{ /Demo/WaterPump/CurrentSpeed }} RPM. This paragraph shows how text sits under the header of a card.
::: Item
:::: HomeBlaze.Components.Card Title="Header and Footer" Subtitle="An indicator in the header." Icon=Bolt
::::: HomeBlaze.Components.ValueList
:::::: Value Path=/Demo/Compressor/CurrentSpeed Label="Speed" Icon=Speed
:::::: Value Path=/Demo/Compressor/TargetSpeed Label="Target" Icon=TrackChanges
::::: HeaderRight
:::::: HomeBlaze.Components.Indicator Path=/Demo/Compressor/Status
::::: Footer
:::::: HomeBlaze.Components.Control Path=/Demo/Compressor/GetDiagnostics
::

:: HomeBlaze.Components.GridLayout Rows=1 Columns=5 CellAspectRatio=1:1
::: Cell Row=0 Column=0
:::: HomeBlaze.Components.ValueTile Path=/Demo/Compressor/CurrentSpeed Label="Compressor RPM" Icon=Bolt
::: Cell Row=0 Column=1
:::: HomeBlaze.Components.ValueTile Path=/Demo/WaterPump/Temperature Label="Water Pump" Icon=Thermostat
::: Cell Row=0 Column=2
:::: HomeBlaze.Components.Switch Kind=Tile Path=/Demo/Conveyor/IsPowered On=/Demo/Conveyor/PowerOn Off=/Demo/Conveyor/PowerOff Label="Conveyor" Icon=Settings
::: Cell Row=0 Column=3
:::: HomeBlaze.Components.Card Title="In a Cell" Icon=GridView
::::: HomeBlaze.Components.ValueList
:::::: Value Path=/Demo/ExhaustFan/CurrentSpeed Label="Speed" Icon=Speed
:::::: Value Path=/Demo/ExhaustFan/Temperature Label="Temperature" Icon=Thermostat
::

## Small Cards on a Canvas

The nodes below have a fixed size, so a card in them shows whether its rows still fit: 250 by 184 pixels with three rows, and 250 by 140 pixels with two rows.

:: HomeBlaze.Components.CanvasLayout#smallcards MinHeight=240
::: Node X=20 Y=20 Width=250 Height=184
:::: HomeBlaze.Components.Card Title="Three Rows" Icon=BatteryChargingFull
::::: HomeBlaze.Components.ValueList
:::::: Value Path=/Demo/Compressor/Load Label="Load" Icon=DonutLarge
:::::: Value Path=/Demo/Compressor/CurrentSpeed Label="Speed" Icon=Speed
:::::: Value Path=/Demo/Compressor/Temperature Label="Temperature" Icon=Thermostat
::: Node X=300 Y=20 Width=250 Height=140
:::: HomeBlaze.Components.Card Title="Two Rows" Icon=SolarPower
::::: HomeBlaze.Components.ValueList
:::::: Value Path=/Demo/WaterPump/Load Label="Load" Icon=DonutLarge
:::::: Value Path=/Demo/WaterPump/CurrentSpeed Label="Speed" Icon=Speed
::: Node X=580 Y=20 Width=250 Height=140
:::: HomeBlaze.Components.Card Title="Two Rows" Subtitle="With a subtitle." Icon=SolarPower
::::: HomeBlaze.Components.ValueList
:::::: Value Path=/Demo/Conveyor/Load Label="Load" Icon=DonutLarge
:::::: Value Path=/Demo/Conveyor/CurrentSpeed Label="Speed" Icon=Speed
::
