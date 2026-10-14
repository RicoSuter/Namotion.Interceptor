---
title: Dashboard Demo
navTitle: Dashboard Demo
position: 7
---

# Dashboard Demo

This page is a complete dashboard built from the widgets for pages: value tiles, expansion panels with cards and value lists, tabs inside tabs, a grid and a canvas. Every value is live, and every motor is the same motor wherever it is shown. [Page Widgets](../Docs/administration/page-widgets.md) is the reference of each widget, with live samples.

:: HomeBlaze.Components.ValueMap#motorstate
::: Case Value=Stopped Text="Off" Icon=StopCircle
::: Case Value=Starting Text="Starting up" Color=Warning Icon=PlayCircle
::: Case Value=Running Text="Running" Color=Success Icon=CheckCircle
::: Case Value=Stopping Text="Shutting down" Color=Warning Icon=PauseCircle
::: Case Value=Error Text="Fault" Color=Error Icon=Error
::

## Speeds

:: HomeBlaze.Components.FlowLayout MinWidth=160
::: Item
:::: HomeBlaze.Components.ValueTile Path=/Demo/Compressor/CurrentSpeed Label="Compressor RPM" Icon=Bolt
::: Item
:::: HomeBlaze.Components.ValueTile Path=/Demo/WaterPump/CurrentSpeed Label="Water pump RPM" Icon=WaterDrop
::: Item
:::: HomeBlaze.Components.ValueTile Path=/Demo/Conveyor/CurrentSpeed Label="Conveyor RPM" Icon=Settings
::: Item
:::: HomeBlaze.Components.ValueTile Path=/Demo/CoolingFan/CurrentSpeed Label="Cooling fan RPM" Icon=AcUnit
::: Item
:::: HomeBlaze.Components.ValueTile Path=/Demo/ExhaustFan/CurrentSpeed Label="Exhaust fan RPM" Icon=Air
::

:: HomeBlaze.Components.Alert
Every motor on this page is simulated. The speeds climb to the target of the motor and the temperatures follow the speeds.
::

## Motors

:: HomeBlaze.Components.ExpansionPanels
::: Panel#drives Title="Drives" Icon=Settings Expanded=true
:::: HomeBlaze.Components.FlowLayout MinWidth=300
::::: Item
:::::: HomeBlaze.Components.Card Title="Compressor" Subtitle="Simulated motor" Icon=Bolt IconColor=Primary Href="/browser/Demo/Compressor"
::::::: HomeBlaze.Components.ValueList
:::::::: Value Path=/Demo/Compressor/CurrentSpeed Icon=Speed
::::::::: HomeBlaze.Components.Progress Path=/Demo/Compressor/CurrentSpeed Minimum=0 Maximum=3000
:::::::: Value Path=/Demo/Compressor/TargetSpeed
:::::::: Value Path=/Demo/Compressor/Temperature Icon=Thermostat
:::::::: Value Path=/Demo/Compressor/IsAtTargetSpeed
::::::: HeaderRight
:::::::: HomeBlaze.Components.Indicator Path=/Demo/Compressor/Status Map=motorstate
::::: Item
:::::: HomeBlaze.Components.Card Title="Water Pump" Subtitle="Simulated motor" Icon=WaterDrop IconColor=Info Href="/browser/Demo/WaterPump"
::::::: HomeBlaze.Components.ValueList
:::::::: Value Path=/Demo/WaterPump/CurrentSpeed Icon=Speed
::::::::: HomeBlaze.Components.Progress Path=/Demo/WaterPump/CurrentSpeed Minimum=0 Maximum=3000
:::::::: Value Path=/Demo/WaterPump/TargetSpeed
:::::::: Value Path=/Demo/WaterPump/Temperature Icon=Thermostat
:::::::: Value Path=/Demo/WaterPump/IsAtTargetSpeed
::::::: HeaderRight
:::::::: HomeBlaze.Components.Indicator Path=/Demo/WaterPump/Status Map=motorstate
::::: Item
:::::: HomeBlaze.Components.Card Title="Conveyor" Subtitle="Simulated motor" Icon=Settings IconColor=Secondary Href="/browser/Demo/Conveyor"
::::::: HomeBlaze.Components.ValueList
:::::::: Value Path=/Demo/Conveyor/CurrentSpeed Icon=Speed
::::::::: HomeBlaze.Components.Progress Path=/Demo/Conveyor/CurrentSpeed Minimum=0 Maximum=3000
:::::::: Value Path=/Demo/Conveyor/TargetSpeed
:::::::: Value Path=/Demo/Conveyor/Temperature Icon=Thermostat
:::::::: Value Path=/Demo/Conveyor/IsAtTargetSpeed
::::::: HeaderRight
:::::::: HomeBlaze.Components.Indicator Path=/Demo/Conveyor/Status Map=motorstate
::: Panel#fans Title="Fans" Icon=Air
:::: HomeBlaze.Components.FlowLayout MinWidth=300
::::: Item
:::::: HomeBlaze.Components.Card Title="Cooling Fan" Subtitle="Simulated motor" Icon=AcUnit IconColor=Info
::::::: HomeBlaze.Components.ValueList
:::::::: Value Path=/Demo/CoolingFan/CurrentSpeed Icon=Speed
::::::::: HomeBlaze.Components.Progress Path=/Demo/CoolingFan/CurrentSpeed Minimum=0 Maximum=3000
:::::::: Value Path=/Demo/CoolingFan/TargetSpeed
:::::::: Value Path=/Demo/CoolingFan/Temperature Icon=Thermostat
:::::::: Value Path=/Demo/CoolingFan/IsAtTargetSpeed
::::::: HeaderRight
:::::::: HomeBlaze.Components.Indicator Path=/Demo/CoolingFan/Status Map=motorstate Kind=Icon
::::: Item
:::::: HomeBlaze.Components.Card Title="Exhaust Fan" Subtitle="Simulated motor" Icon=Air IconColor=Info
::::::: HomeBlaze.Components.ValueList
:::::::: Value Path=/Demo/ExhaustFan/CurrentSpeed Icon=Speed
::::::::: HomeBlaze.Components.Progress Path=/Demo/ExhaustFan/CurrentSpeed Minimum=0 Maximum=3000
:::::::: Value Path=/Demo/ExhaustFan/TargetSpeed
:::::::: Value Path=/Demo/ExhaustFan/Temperature Icon=Thermostat
:::::::: Value Path=/Demo/ExhaustFan/IsAtTargetSpeed
::::::: HeaderRight
:::::::: HomeBlaze.Components.Indicator Path=/Demo/ExhaustFan/Status Map=motorstate Kind=Icon
::: Panel#table Title="All Motors" Icon=TableChart
| Motor | Status | Speed | Target | Delta | At target |
|-------|--------|------:|-------:|------:|-----------|
| **Compressor** | {{ /Demo/Compressor/Status }} | {{ /Demo/Compressor/CurrentSpeed }} | {{ /Demo/Compressor/TargetSpeed }} | {{ /Demo/Compressor/SpeedDelta }} | {{ /Demo/Compressor/IsAtTargetSpeed }} |
| **Water Pump** | {{ /Demo/WaterPump/Status }} | {{ /Demo/WaterPump/CurrentSpeed }} | {{ /Demo/WaterPump/TargetSpeed }} | {{ /Demo/WaterPump/SpeedDelta }} | {{ /Demo/WaterPump/IsAtTargetSpeed }} |
| **Conveyor** | {{ /Demo/Conveyor/Status }} | {{ /Demo/Conveyor/CurrentSpeed }} | {{ /Demo/Conveyor/TargetSpeed }} | {{ /Demo/Conveyor/SpeedDelta }} | {{ /Demo/Conveyor/IsAtTargetSpeed }} |
| **Cooling Fan** | {{ /Demo/CoolingFan/Status }} | {{ /Demo/CoolingFan/CurrentSpeed }} | {{ /Demo/CoolingFan/TargetSpeed }} | {{ /Demo/CoolingFan/SpeedDelta }} | {{ /Demo/CoolingFan/IsAtTargetSpeed }} |
| **Exhaust Fan** | {{ /Demo/ExhaustFan/Status }} | {{ /Demo/ExhaustFan/CurrentSpeed }} | {{ /Demo/ExhaustFan/TargetSpeed }} | {{ /Demo/ExhaustFan/SpeedDelta }} | {{ /Demo/ExhaustFan/IsAtTargetSpeed }} |

Speeds in RPM. The cards above and this table read the same motors.
::

## More Views

:: HomeBlaze.Components.TabLayout
::: Tab#devices Title="Devices" Icon=Devices
:::: HomeBlaze.Components.TabLayout
::::: Tab#pumps Title="Pumps" Icon=WaterDrop
:::::: HomeBlaze.Components.FlowLayout MinWidth=320
::::::: Item
:::::::: HomeBlaze.Components.Card Title="Water Pump" Icon=WaterDrop
The water pump is {{ /Demo/WaterPump/Status }} at {{ /Demo/WaterPump/CurrentSpeed }} RPM.

::::::::: HomeBlaze.Components.Widget Path=/Demo/WaterPump
::::::::: Footer
{{ /Demo/WaterPump/SpeedDelta }} RPM away from its target
::::::: Item
:::::::: HomeBlaze.Components.Card Title="Compressor" Icon=Bolt
The compressor is {{ /Demo/Compressor/Status }} at {{ /Demo/Compressor/CurrentSpeed }} RPM.

::::::::: HomeBlaze.Components.Widget Path=/Demo/Compressor
::::::::: Footer
{{ /Demo/Compressor/SpeedDelta }} RPM away from its target
::::: Tab#fansview Title="Fans" Icon=Air
:::::: HomeBlaze.Components.TabLayout
::::::: Tab#coolingfan Title="Cooling Fan" Icon=AcUnit
:::::::: HomeBlaze.Components.FlowLayout MinWidth=320
::::::::: Item
:::::::::: HomeBlaze.Components.Widget Path=/Demo/CoolingFan
::::::::: Item
:::::::::: HomeBlaze.Components.Card Title="Cooling Fan Values" Icon=Info
::::::::::: HomeBlaze.Components.ValueList
:::::::::::: Value Path=/Demo/CoolingFan/Status Map=motorstate
:::::::::::: Value Path=/Demo/CoolingFan/CurrentSpeed
:::::::::::: Value Path=/Demo/CoolingFan/TargetSpeed
:::::::::::: Value Path=/Demo/CoolingFan/IsAtTargetSpeed
::::::: Tab#exhaustfan Title="Exhaust Fan" Icon=Air
:::::::: HomeBlaze.Components.FlowLayout MinWidth=320
::::::::: Item
:::::::::: HomeBlaze.Components.Widget Path=/Demo/ExhaustFan
::::::::: Item
:::::::::: HomeBlaze.Components.Card Title="Exhaust Fan Values" Icon=Info
::::::::::: HomeBlaze.Components.ValueList
:::::::::::: Value Path=/Demo/ExhaustFan/Status Map=motorstate
:::::::::::: Value Path=/Demo/ExhaustFan/CurrentSpeed
:::::::::::: Value Path=/Demo/ExhaustFan/TargetSpeed
:::::::::::: Value Path=/Demo/ExhaustFan/IsAtTargetSpeed
::::: Tab#conveyor Title="Conveyor" Icon=Settings
:::::: HomeBlaze.Components.Alert
The conveyor runs at {{ /Demo/Conveyor/CurrentSpeed }} of {{ /Demo/Conveyor/TargetSpeed }} RPM.
:::::: HomeBlaze.Components.Widget Path=/Demo/Conveyor
::::: Tab#sampledevices Title="Sample Devices" Icon=Extension
:::::: HomeBlaze.Components.FlowLayout MinWidth=320
::::::: Item
:::::::: HomeBlaze.Components.Widget Path=/Devices/SampleDevice1
::::::: Item
:::::::: HomeBlaze.Components.Widget Path=/Devices/SampleDevice2
::: Tab#grids Title="Grid and Canvas" Icon=GridView
:::: HomeBlaze.Components.TabLayout
::::: Tab#grid Title="Grid" Icon=GridView
:::::: HomeBlaze.Components.GridLayout#dashboardgrid Rows=2 Columns=3
::::::: Cell Row=0 Column=0
:::::::: HomeBlaze.Components.Widget Path=/Demo/Compressor
::::::: Cell Row=0 Column=1
:::::::: HomeBlaze.Components.Widget Path=/Demo/WaterPump
::::::: Cell Row=0 Column=2
:::::::: HomeBlaze.Components.Widget Path=/Demo/Conveyor
::::::: Cell Row=1 Column=0
:::::::: HomeBlaze.Components.Widget Path=/Demo/CoolingFan
::::::: Cell Row=1 Column=1 ColumnSpan=2
:::::::: HomeBlaze.Components.Widget Path=/Demo/ExhaustFan
::::: Tab#canvas Title="Canvas" Icon=Map
:::::: HomeBlaze.Components.CanvasLayout#dashboardcanvas MinHeight=620 IsSnapToGridEnabled=true GridSize=20
::::::: Node X=20 Y=20 Width=320 Height=280
:::::::: HomeBlaze.Components.Widget Path=/Demo/WaterPump
::::::: Node X=380 Y=60 Width=320 Height=280
:::::::: HomeBlaze.Components.Widget Path=/Demo/Compressor
::::::: Node X=200 Y=320 Width=320 Height=280
:::::::: HomeBlaze.Components.Widget Path=/Demo/Conveyor
::: Tab#hosted Title="On This Page" Icon=Tune
:::: HomeBlaze.Components.Alert
The motor in this tab exists only on this page. The second card finds it by its name, `dashboardmotor`.
:::: HomeBlaze.Components.FlowLayout MinWidth=320
::::: Item
:::::: HomeBlaze.Components.Card Title="Created Here" Icon=Add
::::::: HomeBlaze.Samples.Motor#dashboardmotor Name="Dashboard Motor" TargetSpeed=1500 SimulationInterval=00:00:01
::::: Item
:::::: HomeBlaze.Components.Card Title="Read by Name" Icon=Link
::::::: HomeBlaze.Components.ValueList
:::::::: Value Path=dashboardmotor/Status Map=motorstate
:::::::: Value Path=dashboardmotor/CurrentSpeed
:::::::: Value Path=dashboardmotor/TargetSpeed
:::::::: Value Path=dashboardmotor/IsAtTargetSpeed
::::::: Footer
The same motor again as a widget: see the next card.
::::: Item
:::::: HomeBlaze.Components.Card Title="Shown by Name" Icon=Visibility
::::::: HomeBlaze.Components.Widget Path=dashboardmotor
::

The tabs of this page are written without closing lines, except where text follows a block. The page source is the best place to see how the colons count the depth.
