---
title: Dashboard Demo
navTitle: Dashboard Demo
position: 7
---

# Dashboard Demo

This page is a complete dashboard that uses every widget for pages: maps, value tiles, gauges, indicators, progress bars, controls, history charts, a table of subjects, blocks that depend on the width of the screen, expansion panels with cards and value lists, tabs inside tabs, a grid and a canvas. Every value is live, and every motor is the same motor wherever it is shown. [Widgets](../Docs/administration/widgets/overview.md) is the reference of each widget, with live samples.

:: HomeBlaze.Components.Alert
Every motor on this page is simulated. The speeds climb to the target of the motor, and the temperatures follow the speeds and the load. The controls change the power, the mode and the load of a motor until the next restart. They never change the files of the motors.
::

:: HomeBlaze.Components.ValueMap#motorstate
::: Case Value=Stopped Text="Off" Icon=StopCircle
::: Case Value=Starting Text="Starting up" Color=Warning Icon=PlayCircle
::: Case Value=Running Text="Running" Color=Success Icon=CheckCircle
::: Case Value=Stopping Text="Shutting down" Color=Warning Icon=PauseCircle
::: Case Value=Error Text="Fault" Color=Error Icon=Error
::

:: HomeBlaze.Components.ValueMap#motormode
::: Case Value=Eco Text="Eco" Color=Success Icon=EnergySavingsLeaf
::: Case Value=Normal Text="Normal" Color=Info Icon=Speed
::: Case Value=Boost Text="Boost" Color=Warning Icon=RocketLaunch
::

## Widgets and Views

:: HomeBlaze.Components.TabLayout#widgets
::: Tab#values Title="Values" Icon=Insights
:::: HomeBlaze.Components.Responsive To=Xs
::::: HomeBlaze.Components.Card Title="Motors" Icon=Settings
:::::: HomeBlaze.Components.ValueList
::::::: Value Path=/Demo/Compressor/Status Label="Compressor" Map=motorstate
{{ /Demo/Compressor/CurrentSpeed }} RPM
::::::: Value Path=/Demo/WaterPump/Status Label="Water Pump" Map=motorstate
{{ /Demo/WaterPump/CurrentSpeed }} RPM
::::::: Value Path=/Demo/Conveyor/Status Label="Conveyor" Map=motorstate
{{ /Demo/Conveyor/CurrentSpeed }} RPM
::::::: Value Path=/Demo/CoolingFan/Status Label="Cooling Fan" Map=motorstate
{{ /Demo/CoolingFan/CurrentSpeed }} RPM
::::::: Value Path=/Demo/ExhaustFan/Status Label="Exhaust Fan" Map=motorstate
{{ /Demo/ExhaustFan/CurrentSpeed }} RPM
:::: HomeBlaze.Components.Responsive From=Sm
::::: HomeBlaze.Components.FlowLayout MinWidth=180
:::::: Item
::::::: HomeBlaze.Components.ValueTile Path=/Demo/Compressor/CurrentSpeed Label="Compressor RPM" Icon=Bolt
:::::: Item
::::::: HomeBlaze.Components.ValueTile Path=/Demo/WaterPump/CurrentSpeed Label="Water Pump RPM" Icon=WaterDrop
:::::: Item
::::::: HomeBlaze.Components.ValueTile Path=/Demo/Conveyor/CurrentSpeed Label="Conveyor RPM" Icon=Settings
:::::: Item
::::::: HomeBlaze.Components.ValueTile Path=/Demo/CoolingFan/CurrentSpeed Label="Cooling Fan RPM" Icon=AcUnit
:::::: Item
::::::: HomeBlaze.Components.ValueTile Path=/Demo/ExhaustFan/CurrentSpeed Label="Exhaust Fan RPM" Icon=Air
::::: HomeBlaze.Components.Card Title="Current Temperatures" Icon=Thermostat
:::::: HomeBlaze.Components.FlowLayout MinWidth=120
::::::: Item
:::::::: HomeBlaze.Components.Gauge Path=/Demo/Compressor/Temperature Label="Compressor" Minimum=20 Maximum=100
::::::: Item
:::::::: HomeBlaze.Components.Gauge Path=/Demo/WaterPump/Temperature Label="Water Pump" Minimum=20 Maximum=100
::::::: Item
:::::::: HomeBlaze.Components.Gauge Path=/Demo/Conveyor/Temperature Label="Conveyor" Minimum=20 Maximum=100
::::::: Item
:::::::: HomeBlaze.Components.Gauge Path=/Demo/CoolingFan/Temperature Label="Cooling Fan" Minimum=20 Maximum=100
::::::: Item
:::::::: HomeBlaze.Components.Gauge Path=/Demo/ExhaustFan/Temperature Label="Exhaust Fan" Minimum=20 Maximum=100
:::: HomeBlaze.Components.Card Title="Temperature Trends, Last Hour" Icon=Timeline
::::: HomeBlaze.Components.FlowLayout MinWidth=180
:::::: Item
::::::: HomeBlaze.History.Blazor.HistoryChart Range=1h Height=100 Compact=true
:::::::: Series Path=/Demo/Compressor/Temperature Label="Compressor"
:::::: Item
::::::: HomeBlaze.History.Blazor.HistoryChart Range=1h Height=100 Compact=true
:::::::: Series Path=/Demo/WaterPump/Temperature Label="Water Pump"
:::::: Item
::::::: HomeBlaze.History.Blazor.HistoryChart Range=1h Height=100 Compact=true
:::::::: Series Path=/Demo/Conveyor/Temperature Label="Conveyor"
:::::: Item
::::::: HomeBlaze.History.Blazor.HistoryChart Range=1h Height=100 Compact=true
:::::::: Series Path=/Demo/CoolingFan/Temperature Label="Cooling Fan"
:::::: Item
::::::: HomeBlaze.History.Blazor.HistoryChart Range=1h Height=100 Compact=true
:::::::: Series Path=/Demo/ExhaustFan/Temperature Label="Exhaust Fan"
::::

A phone shows the status list above, a wider screen the speed tiles and the temperature gauges. The small charts show the temperature of each motor in the last hour. Click a small chart to open its history. The cards below show the status as chips, whether a motor is at its target as an icon, and the temperatures as bars:

:::: HomeBlaze.Components.FlowLayout MinWidth=300
::::: Item
:::::: HomeBlaze.Components.Card Title="Status" Icon=Info
::::::: HomeBlaze.Components.ValueList
:::::::: Value Path=/Demo/Compressor/CurrentSpeed Label="Compressor" Icon=Bolt
::::::::: HomeBlaze.Components.Indicator Path=/Demo/Compressor/Status Map=motorstate
:::::::: Value Path=/Demo/WaterPump/CurrentSpeed Label="Water Pump" Icon=WaterDrop
::::::::: HomeBlaze.Components.Indicator Path=/Demo/WaterPump/Status Map=motorstate
:::::::: Value Path=/Demo/Conveyor/CurrentSpeed Label="Conveyor" Icon=Settings
::::::::: HomeBlaze.Components.Indicator Path=/Demo/Conveyor/Status Map=motorstate
:::::::: Value Path=/Demo/CoolingFan/CurrentSpeed Label="Cooling Fan" Icon=AcUnit
::::::::: HomeBlaze.Components.Indicator Path=/Demo/CoolingFan/Status Map=motorstate
:::::::: Value Path=/Demo/ExhaustFan/CurrentSpeed Label="Exhaust Fan" Icon=Air
::::::::: HomeBlaze.Components.Indicator Path=/Demo/ExhaustFan/Status Map=motorstate
::::: Item
:::::: HomeBlaze.Components.Card Title="At Target" Icon=TrackChanges
::::::: HomeBlaze.Components.ValueList
:::::::: Value Path=/Demo/Compressor/TargetSpeed Label="Compressor" Icon=Bolt
::::::::: HomeBlaze.Components.Indicator Path=/Demo/Compressor/IsAtTargetSpeed Kind=Icon Icon=Speed
:::::::: Value Path=/Demo/WaterPump/TargetSpeed Label="Water Pump" Icon=WaterDrop
::::::::: HomeBlaze.Components.Indicator Path=/Demo/WaterPump/IsAtTargetSpeed Kind=Icon Icon=Speed
:::::::: Value Path=/Demo/Conveyor/TargetSpeed Label="Conveyor" Icon=Settings
::::::::: HomeBlaze.Components.Indicator Path=/Demo/Conveyor/IsAtTargetSpeed Kind=Icon Icon=Speed
:::::::: Value Path=/Demo/CoolingFan/TargetSpeed Label="Cooling Fan" Icon=AcUnit
::::::::: HomeBlaze.Components.Indicator Path=/Demo/CoolingFan/IsAtTargetSpeed Kind=Icon Icon=Speed
:::::::: Value Path=/Demo/ExhaustFan/TargetSpeed Label="Exhaust Fan" Icon=Air
::::::::: HomeBlaze.Components.Indicator Path=/Demo/ExhaustFan/IsAtTargetSpeed Kind=Icon Icon=Speed
::::: Item
:::::: HomeBlaze.Components.Card Title="Temperatures" Icon=Thermostat
::::::: HomeBlaze.Components.ValueList
:::::::: Value Path=/Demo/Compressor/Temperature Label="Compressor" Icon=Bolt
::::::::: HomeBlaze.Components.Progress Path=/Demo/Compressor/Temperature Minimum=20 Maximum=100
:::::::: Value Path=/Demo/WaterPump/Temperature Label="Water Pump" Icon=WaterDrop
::::::::: HomeBlaze.Components.Progress Path=/Demo/WaterPump/Temperature Minimum=20 Maximum=100
:::::::: Value Path=/Demo/Conveyor/Temperature Label="Conveyor" Icon=Settings
::::::::: HomeBlaze.Components.Progress Path=/Demo/Conveyor/Temperature Minimum=20 Maximum=100
:::::::: Value Path=/Demo/CoolingFan/Temperature Label="Cooling Fan" Icon=AcUnit
::::::::: HomeBlaze.Components.Progress Path=/Demo/CoolingFan/Temperature Minimum=20 Maximum=100
:::::::: Value Path=/Demo/ExhaustFan/Temperature Label="Exhaust Fan" Icon=Air
::::::::: HomeBlaze.Components.Progress Path=/Demo/ExhaustFan/Temperature Minimum=20 Maximum=100
:::: HomeBlaze.Components.Card Title="Gauge Sizes" Icon=Speed
The speed of the compressor in the three sizes of a gauge.

::::: HomeBlaze.Components.FlowLayout MinWidth=168
:::::: Item
::::::: HomeBlaze.Components.Gauge Path=/Demo/Compressor/CurrentSpeed Label="Small" Minimum=0 Maximum=3000 Size=Small
:::::: Item
::::::: HomeBlaze.Components.Gauge Path=/Demo/Compressor/CurrentSpeed Label="Medium" Minimum=0 Maximum=3000
:::::: Item
::::::: HomeBlaze.Components.Gauge Path=/Demo/Compressor/CurrentSpeed Label="Large" Minimum=0 Maximum=3000 Size=Large
::::

Widgets of this tab: [ValueList](../Docs/administration/widgets/value-list.md), [ValueTile](../Docs/administration/widgets/value-tile.md), [Gauge](../Docs/administration/widgets/gauge.md), [HistoryChart](../Docs/administration/widgets/history-chart.md), [Indicator](../Docs/administration/widgets/indicator.md), [Progress](../Docs/administration/widgets/progress.md), [Card](../Docs/administration/widgets/card.md), [FlowLayout](../Docs/administration/widgets/flow-layout.md) and [Responsive](../Docs/administration/widgets/responsive.md). The texts, colors and icons of the status and the mode come from the two [ValueMap](../Docs/administration/widgets/value-map.md) blocks at the top of this page.
::: Tab#controls Title="Controls" Icon=Tune
:::: HomeBlaze.Components.Responsive From=Sm
::::: HomeBlaze.Components.Card Title="Load" Icon=DonutLarge
:::::: HomeBlaze.Components.FlowLayout MinWidth=120
::::::: Item
:::::::: HomeBlaze.Components.Gauge Path=/Demo/Compressor/Load Label="Compressor"
::::::: Item
:::::::: HomeBlaze.Components.Gauge Path=/Demo/WaterPump/Load Label="Water Pump"
::::::: Item
:::::::: HomeBlaze.Components.Gauge Path=/Demo/Conveyor/Load Label="Conveyor"
::::::: Item
:::::::: HomeBlaze.Components.Gauge Path=/Demo/CoolingFan/Load Label="Cooling Fan"
::::::: Item
:::::::: HomeBlaze.Components.Gauge Path=/Demo/ExhaustFan/Load Label="Exhaust Fan"
:::: HomeBlaze.Components.FlowLayout#controlroom MinWidth=300
::::: Item
:::::: HomeBlaze.Components.Card Title="Compressor" Icon=Bolt IconColor=Primary Href="/browser/Demo/Compressor"
::::::: HomeBlaze.Components.Switch Path=/Demo/Compressor/IsPowered Label="Power" On=/Demo/Compressor/PowerOn Off=/Demo/Compressor/PowerOff
::::::: HomeBlaze.Components.Select Path=/Demo/Compressor/Mode Label="Mode" Map=motormode
::::::: HomeBlaze.Components.Slider Path=/Demo/Compressor/Load Label="Load"
::::::: HeaderRight
:::::::: HomeBlaze.Components.Indicator Path=/Demo/Compressor/Status Map=motorstate
::::::: Footer
:::::::: HomeBlaze.Components.OperationButton Path=/Demo/Compressor/EmergencyStop Color=Error Variant=Outlined
:::::::: HomeBlaze.Components.Control Path=/Demo/Compressor/GetDiagnostics
::::: Item
:::::: HomeBlaze.Components.Card Title="Water Pump" Icon=WaterDrop IconColor=Info Href="/browser/Demo/WaterPump"
::::::: HomeBlaze.Components.Switch Path=/Demo/WaterPump/IsPowered Label="Power" On=/Demo/WaterPump/PowerOn Off=/Demo/WaterPump/PowerOff
::::::: HomeBlaze.Components.Select Path=/Demo/WaterPump/Mode Label="Mode" Map=motormode
::::::: HomeBlaze.Components.Slider Path=/Demo/WaterPump/Load Label="Load"
::::::: HeaderRight
:::::::: HomeBlaze.Components.Indicator Path=/Demo/WaterPump/Status Map=motorstate
::::::: Footer
:::::::: HomeBlaze.Components.OperationButton Path=/Demo/WaterPump/EmergencyStop Color=Error Variant=Outlined
:::::::: HomeBlaze.Components.Control Path=/Demo/WaterPump/GetDiagnostics
::::: Item
:::::: HomeBlaze.Components.Card Title="Conveyor" Icon=Settings IconColor=Secondary Href="/browser/Demo/Conveyor"
::::::: HomeBlaze.Components.Switch Path=/Demo/Conveyor/IsPowered Label="Power" On=/Demo/Conveyor/PowerOn Off=/Demo/Conveyor/PowerOff
::::::: HomeBlaze.Components.Select Path=/Demo/Conveyor/Mode Label="Mode" Map=motormode
::::::: HomeBlaze.Components.Slider Path=/Demo/Conveyor/Load Label="Load"
::::::: HeaderRight
:::::::: HomeBlaze.Components.Indicator Path=/Demo/Conveyor/Status Map=motorstate
::::::: Footer
:::::::: HomeBlaze.Components.OperationButton Path=/Demo/Conveyor/EmergencyStop Color=Error Variant=Outlined
:::::::: HomeBlaze.Components.Control Path=/Demo/Conveyor/GetDiagnostics
::::: Item
:::::: HomeBlaze.Components.Card Title="Cooling Fan" Icon=AcUnit IconColor=Info Href="/browser/Demo/CoolingFan"
::::::: HomeBlaze.Components.Switch Path=/Demo/CoolingFan/IsPowered Label="Power" On=/Demo/CoolingFan/PowerOn Off=/Demo/CoolingFan/PowerOff
::::::: HomeBlaze.Components.Select Path=/Demo/CoolingFan/Mode Label="Mode" Map=motormode
::::::: HomeBlaze.Components.Slider Path=/Demo/CoolingFan/Load Label="Load"
::::::: HeaderRight
:::::::: HomeBlaze.Components.Indicator Path=/Demo/CoolingFan/Status Map=motorstate
::::::: Footer
:::::::: HomeBlaze.Components.OperationButton Path=/Demo/CoolingFan/EmergencyStop Color=Error Variant=Outlined
:::::::: HomeBlaze.Components.Control Path=/Demo/CoolingFan/GetDiagnostics
::::: Item
:::::: HomeBlaze.Components.Card Title="Exhaust Fan" Icon=Air IconColor=Info Href="/browser/Demo/ExhaustFan"
::::::: HomeBlaze.Components.Switch Path=/Demo/ExhaustFan/IsPowered Label="Power" On=/Demo/ExhaustFan/PowerOn Off=/Demo/ExhaustFan/PowerOff
::::::: HomeBlaze.Components.Select Path=/Demo/ExhaustFan/Mode Label="Mode" Map=motormode
::::::: HomeBlaze.Components.Slider Path=/Demo/ExhaustFan/Load Label="Load"
::::::: HeaderRight
:::::::: HomeBlaze.Components.Indicator Path=/Demo/ExhaustFan/Status Map=motorstate
::::::: Footer
:::::::: HomeBlaze.Components.OperationButton Path=/Demo/ExhaustFan/EmergencyStop Color=Error Variant=Outlined
:::::::: HomeBlaze.Components.Control Path=/Demo/ExhaustFan/GetDiagnostics
::::

A wider screen shows the load of each motor as a gauge, which its slider sets. Power, mode and load are runtime values of a motor: they last until the next restart, see [Basics](../Docs/administration/widgets/basics.md#control-writes). The emergency stop runs at once without confirmation, and **Power** brings the motor back. **Get Diagnostics** shows what the motor reports. Widgets of this tab: [Gauge](../Docs/administration/widgets/gauge.md), [Switch](../Docs/administration/widgets/switch.md), [Select](../Docs/administration/widgets/select.md), [Slider](../Docs/administration/widgets/slider.md), [OperationButton](../Docs/administration/widgets/operation-button.md), [Control](../Docs/administration/widgets/control.md), [Indicator](../Docs/administration/widgets/indicator.md), [Card](../Docs/administration/widgets/card.md), [FlowLayout](../Docs/administration/widgets/flow-layout.md) and [Responsive](../Docs/administration/widgets/responsive.md).
::: Tab#history Title="History" Icon=Timeline
:::: HomeBlaze.Components.Card Title="Speeds, Last Hour" Icon=Timeline
::::: HomeBlaze.History.Blazor.HistoryChart#trends Range=1h Height=320
:::::: Series Path=/Demo/Compressor/CurrentSpeed Label="Compressor"
:::::: Series Path=/Demo/WaterPump/CurrentSpeed Label="Water Pump"
:::::: Series Path=/Demo/Conveyor/CurrentSpeed Label="Conveyor"
:::::: Series Path=/Demo/CoolingFan/CurrentSpeed Label="Cooling Fan"
:::::: Series Path=/Demo/ExhaustFan/CurrentSpeed Label="Exhaust Fan"
:::::: Series Path=/Demo/Compressor/Status Label="Compressor Status"
::::

The speeds of the last hour as lines, and the status of the compressor as a band below them. The CPU and memory usage of the machine that runs HomeBlaze in the last hour, in percent:

:::: HomeBlaze.Components.Card Title="Host CPU and Memory, Last Hour" Icon=Computer
::::: HomeBlaze.History.Blazor.HistoryChart#hosttrends Range=1h Height=200
:::::: Series Path=/Servers/HostSystem/CpuUsage Label="CPU"
:::::: Series Path=/Servers/HostSystem/MemoryUsage Label="Memory"
::::

Widgets of this tab: [HistoryChart](../Docs/administration/widgets/history-chart.md) and [Card](../Docs/administration/widgets/card.md).
::: Tab#subjecttable Title="Table" Icon=TableChart
:::: HomeBlaze.Components.Card Title="Motors" Icon=TableChart
::::: HomeBlaze.Components.SubjectTable#motortable Source=".." Type=HomeBlaze.Samples.Motor
:::::: Column Property=Name Label="Motor"
:::::: Column Property=Status Map=motorstate
:::::: Column Property=CurrentSpeed Label="Speed"
:::::: Column Property=Temperature
:::::: Column Property=Mode Map=motormode
::::

One row for each motor of this folder. Widgets of this tab: [SubjectTable](../Docs/administration/widgets/subject-table.md) and [Card](../Docs/administration/widgets/card.md).
::: Tab#motors Title="Motors" Icon=Settings
:::: HomeBlaze.Components.ExpansionPanels
::::: Panel#drives Title="Drives" Icon=Settings Expanded=true
:::::: HomeBlaze.Components.FlowLayout MinWidth=300
::::::: Item
:::::::: HomeBlaze.Components.Card Title="Compressor" Subtitle="Simulated motor" Icon=Bolt IconColor=Primary Href="/browser/Demo/Compressor"
::::::::: HomeBlaze.Components.ValueList
:::::::::: Value Path=/Demo/Compressor/CurrentSpeed Icon=Speed
::::::::::: HomeBlaze.Components.Progress Path=/Demo/Compressor/CurrentSpeed Minimum=0 Maximum=3000
:::::::::: Value Path=/Demo/Compressor/TargetSpeed
:::::::::: Value Path=/Demo/Compressor/Temperature Icon=Thermostat
:::::::::: Value Path=/Demo/Compressor/IsAtTargetSpeed
::::::::: HeaderRight
:::::::::: HomeBlaze.Components.Indicator Path=/Demo/Compressor/Status Map=motorstate
::::::: Item
:::::::: HomeBlaze.Components.Card Title="Water Pump" Subtitle="Simulated motor" Icon=WaterDrop IconColor=Info Href="/browser/Demo/WaterPump"
::::::::: HomeBlaze.Components.ValueList
:::::::::: Value Path=/Demo/WaterPump/CurrentSpeed Icon=Speed
::::::::::: HomeBlaze.Components.Progress Path=/Demo/WaterPump/CurrentSpeed Minimum=0 Maximum=3000
:::::::::: Value Path=/Demo/WaterPump/TargetSpeed
:::::::::: Value Path=/Demo/WaterPump/Temperature Icon=Thermostat
:::::::::: Value Path=/Demo/WaterPump/IsAtTargetSpeed
::::::::: HeaderRight
:::::::::: HomeBlaze.Components.Indicator Path=/Demo/WaterPump/Status Map=motorstate
::::::: Item
:::::::: HomeBlaze.Components.Card Title="Conveyor" Subtitle="Simulated motor" Icon=Settings IconColor=Secondary Href="/browser/Demo/Conveyor"
::::::::: HomeBlaze.Components.ValueList
:::::::::: Value Path=/Demo/Conveyor/CurrentSpeed Icon=Speed
::::::::::: HomeBlaze.Components.Progress Path=/Demo/Conveyor/CurrentSpeed Minimum=0 Maximum=3000
:::::::::: Value Path=/Demo/Conveyor/TargetSpeed
:::::::::: Value Path=/Demo/Conveyor/Temperature Icon=Thermostat
:::::::::: Value Path=/Demo/Conveyor/IsAtTargetSpeed
::::::::: HeaderRight
:::::::::: HomeBlaze.Components.Indicator Path=/Demo/Conveyor/Status Map=motorstate
::::: Panel#fans Title="Fans" Icon=Air
:::::: HomeBlaze.Components.FlowLayout MinWidth=300
::::::: Item
:::::::: HomeBlaze.Components.Card Title="Cooling Fan" Subtitle="Simulated motor" Icon=AcUnit IconColor=Info
::::::::: HomeBlaze.Components.ValueList
:::::::::: Value Path=/Demo/CoolingFan/CurrentSpeed Icon=Speed
::::::::::: HomeBlaze.Components.Progress Path=/Demo/CoolingFan/CurrentSpeed Minimum=0 Maximum=3000
:::::::::: Value Path=/Demo/CoolingFan/TargetSpeed
:::::::::: Value Path=/Demo/CoolingFan/Temperature Icon=Thermostat
:::::::::: Value Path=/Demo/CoolingFan/IsAtTargetSpeed
::::::::: HeaderRight
:::::::::: HomeBlaze.Components.Indicator Path=/Demo/CoolingFan/Status Map=motorstate Kind=Icon
::::::: Item
:::::::: HomeBlaze.Components.Card Title="Exhaust Fan" Subtitle="Simulated motor" Icon=Air IconColor=Info
::::::::: HomeBlaze.Components.ValueList
:::::::::: Value Path=/Demo/ExhaustFan/CurrentSpeed Icon=Speed
::::::::::: HomeBlaze.Components.Progress Path=/Demo/ExhaustFan/CurrentSpeed Minimum=0 Maximum=3000
:::::::::: Value Path=/Demo/ExhaustFan/TargetSpeed
:::::::::: Value Path=/Demo/ExhaustFan/Temperature Icon=Thermostat
:::::::::: Value Path=/Demo/ExhaustFan/IsAtTargetSpeed
::::::::: HeaderRight
:::::::::: HomeBlaze.Components.Indicator Path=/Demo/ExhaustFan/Status Map=motorstate Kind=Icon
::::: Panel#table Title="All Motors" Icon=TableChart
| Motor | Status | Speed | Target | Delta | At target |
|-------|--------|------:|-------:|------:|-----------|
| **Compressor** | {{ /Demo/Compressor/Status }} | {{ /Demo/Compressor/CurrentSpeed }} | {{ /Demo/Compressor/TargetSpeed }} | {{ /Demo/Compressor/SpeedDelta }} | {{ /Demo/Compressor/IsAtTargetSpeed }} |
| **Water Pump** | {{ /Demo/WaterPump/Status }} | {{ /Demo/WaterPump/CurrentSpeed }} | {{ /Demo/WaterPump/TargetSpeed }} | {{ /Demo/WaterPump/SpeedDelta }} | {{ /Demo/WaterPump/IsAtTargetSpeed }} |
| **Conveyor** | {{ /Demo/Conveyor/Status }} | {{ /Demo/Conveyor/CurrentSpeed }} | {{ /Demo/Conveyor/TargetSpeed }} | {{ /Demo/Conveyor/SpeedDelta }} | {{ /Demo/Conveyor/IsAtTargetSpeed }} |
| **Cooling Fan** | {{ /Demo/CoolingFan/Status }} | {{ /Demo/CoolingFan/CurrentSpeed }} | {{ /Demo/CoolingFan/TargetSpeed }} | {{ /Demo/CoolingFan/SpeedDelta }} | {{ /Demo/CoolingFan/IsAtTargetSpeed }} |
| **Exhaust Fan** | {{ /Demo/ExhaustFan/Status }} | {{ /Demo/ExhaustFan/CurrentSpeed }} | {{ /Demo/ExhaustFan/TargetSpeed }} | {{ /Demo/ExhaustFan/SpeedDelta }} | {{ /Demo/ExhaustFan/IsAtTargetSpeed }} |

Speeds in RPM. The cards above and this table read the same motors. Widgets of this tab: [ExpansionPanels](../Docs/administration/widgets/expansion-panels.md), [FlowLayout](../Docs/administration/widgets/flow-layout.md), [Card](../Docs/administration/widgets/card.md), [ValueList](../Docs/administration/widgets/value-list.md), [Progress](../Docs/administration/widgets/progress.md) and [Indicator](../Docs/administration/widgets/indicator.md).
::: Tab#views Title="More Views" Icon=Dashboard
:::: HomeBlaze.Components.TabLayout
::::: Tab#devices Title="Devices" Icon=Devices
:::::: HomeBlaze.Components.TabLayout
::::::: Tab#pumps Title="Pumps" Icon=WaterDrop
:::::::: HomeBlaze.Components.FlowLayout MinWidth=320
::::::::: Item
:::::::::: HomeBlaze.Components.Card Title="Water Pump" Icon=WaterDrop
The water pump is {{ /Demo/WaterPump/Status }} at {{ /Demo/WaterPump/CurrentSpeed }} RPM.

::::::::::: HomeBlaze.Components.Widget Path=/Demo/WaterPump
::::::::::: Footer
{{ /Demo/WaterPump/SpeedDelta }} RPM away from its target
::::::::: Item
:::::::::: HomeBlaze.Components.Card Title="Compressor" Icon=Bolt
The compressor is {{ /Demo/Compressor/Status }} at {{ /Demo/Compressor/CurrentSpeed }} RPM.

::::::::::: HomeBlaze.Components.Widget Path=/Demo/Compressor
::::::::::: Footer
{{ /Demo/Compressor/SpeedDelta }} RPM away from its target
::::::: Tab#fansview Title="Fans" Icon=Air
:::::::: HomeBlaze.Components.TabLayout
::::::::: Tab#coolingfan Title="Cooling Fan" Icon=AcUnit
:::::::::: HomeBlaze.Components.FlowLayout MinWidth=320
::::::::::: Item
:::::::::::: HomeBlaze.Components.Widget Path=/Demo/CoolingFan
::::::::::: Item
:::::::::::: HomeBlaze.Components.Card Title="Cooling Fan Values" Icon=Info
::::::::::::: HomeBlaze.Components.ValueList
:::::::::::::: Value Path=/Demo/CoolingFan/Status Map=motorstate
:::::::::::::: Value Path=/Demo/CoolingFan/CurrentSpeed
:::::::::::::: Value Path=/Demo/CoolingFan/TargetSpeed
:::::::::::::: Value Path=/Demo/CoolingFan/IsAtTargetSpeed
::::::::: Tab#exhaustfan Title="Exhaust Fan" Icon=Air
:::::::::: HomeBlaze.Components.FlowLayout MinWidth=320
::::::::::: Item
:::::::::::: HomeBlaze.Components.Widget Path=/Demo/ExhaustFan
::::::::::: Item
:::::::::::: HomeBlaze.Components.Card Title="Exhaust Fan Values" Icon=Info
::::::::::::: HomeBlaze.Components.ValueList
:::::::::::::: Value Path=/Demo/ExhaustFan/Status Map=motorstate
:::::::::::::: Value Path=/Demo/ExhaustFan/CurrentSpeed
:::::::::::::: Value Path=/Demo/ExhaustFan/TargetSpeed
:::::::::::::: Value Path=/Demo/ExhaustFan/IsAtTargetSpeed
::::::: Tab#conveyor Title="Conveyor" Icon=Settings
:::::::: HomeBlaze.Components.Alert
The conveyor runs at {{ /Demo/Conveyor/CurrentSpeed }} of {{ /Demo/Conveyor/TargetSpeed }} RPM.
:::::::: HomeBlaze.Components.Widget Path=/Demo/Conveyor
::::::: Tab#sampledevices Title="Sample Devices" Icon=Extension
:::::::: HomeBlaze.Components.FlowLayout MinWidth=320
::::::::: Item
:::::::::: HomeBlaze.Components.Widget Path=/Devices/SampleDevice1
::::::::: Item
:::::::::: HomeBlaze.Components.Widget Path=/Devices/SampleDevice2
::::: Tab#grids Title="Grid and Canvas" Icon=GridView
:::::: HomeBlaze.Components.TabLayout
::::::: Tab#grid Title="Grid" Icon=GridView
:::::::: HomeBlaze.Components.GridLayout#dashboardgrid Rows=2 Columns=3
::::::::: Cell Row=0 Column=0
:::::::::: HomeBlaze.Components.Widget Path=/Demo/Compressor
::::::::: Cell Row=0 Column=1
:::::::::: HomeBlaze.Components.Widget Path=/Demo/WaterPump
::::::::: Cell Row=0 Column=2
:::::::::: HomeBlaze.Components.Widget Path=/Demo/Conveyor
::::::::: Cell Row=1 Column=0
:::::::::: HomeBlaze.Components.Widget Path=/Demo/CoolingFan
::::::::: Cell Row=1 Column=1 ColumnSpan=2
:::::::::: HomeBlaze.Components.Widget Path=/Demo/ExhaustFan
::::::: Tab#canvas Title="Canvas" Icon=Map
:::::::: HomeBlaze.Components.CanvasLayout#dashboardcanvas MinHeight=620 IsSnapToGridEnabled=true GridSize=20
::::::::: Node X=20 Y=20 Width=320 Height=280
:::::::::: HomeBlaze.Components.Widget Path=/Demo/WaterPump
::::::::: Node X=380 Y=60 Width=320 Height=280
:::::::::: HomeBlaze.Components.Widget Path=/Demo/Compressor
::::::::: Node X=200 Y=320 Width=320 Height=280
:::::::::: HomeBlaze.Components.Widget Path=/Demo/Conveyor
::::: Tab#hosted Title="On This Page" Icon=Tune
:::::: HomeBlaze.Components.Alert
The motor in this tab exists only on this page. The second card finds it by its name, `dashboardmotor`.
:::::: HomeBlaze.Components.FlowLayout MinWidth=320
::::::: Item
:::::::: HomeBlaze.Components.Card Title="Created Here" Icon=Add
::::::::: HomeBlaze.Samples.Motor#dashboardmotor Name="Dashboard Motor" TargetSpeed=1500 SimulationInterval=00:00:01
::::::: Item
:::::::: HomeBlaze.Components.Card Title="Read by Name" Icon=Link
::::::::: HomeBlaze.Components.ValueList
:::::::::: Value Path=dashboardmotor/Status Map=motorstate
:::::::::: Value Path=dashboardmotor/CurrentSpeed
:::::::::: Value Path=dashboardmotor/TargetSpeed
:::::::::: Value Path=dashboardmotor/IsAtTargetSpeed
::::::::: Footer
The same motor again as a widget: see the next card.
::::::: Item
:::::::: HomeBlaze.Components.Card Title="Shown by Name" Icon=Visibility
::::::::: HomeBlaze.Components.Widget Path=dashboardmotor
::

The tabs of this page are written without closing lines, except where text follows a block. The page source is the best place to see how the colons count the depth. Widgets of the tab More Views: [TabLayout](../Docs/administration/widgets/tab-layout.md) inside tabs, [Widget](../Docs/administration/widgets/widget.md), [GridLayout](../Docs/administration/widgets/grid-layout.md), [CanvasLayout](../Docs/administration/widgets/canvas-layout.md), [Alert](../Docs/administration/widgets/alert.md), [FlowLayout](../Docs/administration/widgets/flow-layout.md), [Card](../Docs/administration/widgets/card.md) and [ValueList](../Docs/administration/widgets/value-list.md).
