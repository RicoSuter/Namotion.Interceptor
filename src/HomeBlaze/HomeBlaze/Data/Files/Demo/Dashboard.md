---
title: Dashboard Demo
navTitle: Dashboard Demo
position: 7
---

# Dashboard Demo

This page is a complete dashboard built from the widgets for pages: tabs inside tabs, flows of cards, a grid and a canvas. Every value is live, and every motor is the same motor wherever it is shown. [Page Widgets](../Docs/administration/page-widgets.md) is the reference of each widget, with live samples.

<style>
.dashboard-kpi { font-size: 2rem; font-weight: 600; line-height: 1.2; }
.dashboard-note { border-left: 4px solid var(--mud-palette-primary); padding: 0.25rem 1rem; margin: 1rem 0; }
</style>

:: HomeBlaze.Components.TabLayout
::: Tab#dashboard Title="Dashboard" Icon=Dashboard
:::: HomeBlaze.Components.FlowLayout MinWidth=220
::::: Item
:::::: HomeBlaze.Components.Card Title="Compressor" Icon=Bolt
::::::: {.dashboard-kpi}
{{ /Demo/Compressor/CurrentSpeed }} RPM
::::::: Footer
Target {{ /Demo/Compressor/TargetSpeed }} RPM · {{ /Demo/Compressor/Status }}
::::: Item
:::::: HomeBlaze.Components.Card Title="Water Pump" Icon=WaterDrop
::::::: {.dashboard-kpi}
{{ /Demo/WaterPump/CurrentSpeed }} RPM
::::::: Footer
Target {{ /Demo/WaterPump/TargetSpeed }} RPM · {{ /Demo/WaterPump/Status }}
::::: Item
:::::: HomeBlaze.Components.Card Title="Conveyor" Icon=Settings
::::::: {.dashboard-kpi}
{{ /Demo/Conveyor/CurrentSpeed }} RPM
::::::: Footer
Target {{ /Demo/Conveyor/TargetSpeed }} RPM · {{ /Demo/Conveyor/Status }}
::::: Item
:::::: HomeBlaze.Components.Card Title="Cooling Fan" Icon=AcUnit
::::::: {.dashboard-kpi}
{{ /Demo/CoolingFan/CurrentSpeed }} RPM
::::::: Footer
Target {{ /Demo/CoolingFan/TargetSpeed }} RPM · {{ /Demo/CoolingFan/Status }}
::::: Item
:::::: HomeBlaze.Components.Card Title="Exhaust Fan" Icon=Air
::::::: {.dashboard-kpi}
{{ /Demo/ExhaustFan/CurrentSpeed }} RPM
::::::: Footer
Target {{ /Demo/ExhaustFan/TargetSpeed }} RPM · {{ /Demo/ExhaustFan/Status }}

:::: HomeBlaze.Components.Card Title="All Motors" Icon=TableChart
| Motor | Status | Speed | Target | Delta | At target |
|-------|--------|------:|-------:|------:|-----------|
| **Compressor** | {{ /Demo/Compressor/Status }} | {{ /Demo/Compressor/CurrentSpeed }} | {{ /Demo/Compressor/TargetSpeed }} | {{ /Demo/Compressor/SpeedDelta }} | {{ /Demo/Compressor/IsAtTargetSpeed }} |
| **Water Pump** | {{ /Demo/WaterPump/Status }} | {{ /Demo/WaterPump/CurrentSpeed }} | {{ /Demo/WaterPump/TargetSpeed }} | {{ /Demo/WaterPump/SpeedDelta }} | {{ /Demo/WaterPump/IsAtTargetSpeed }} |
| **Conveyor** | {{ /Demo/Conveyor/Status }} | {{ /Demo/Conveyor/CurrentSpeed }} | {{ /Demo/Conveyor/TargetSpeed }} | {{ /Demo/Conveyor/SpeedDelta }} | {{ /Demo/Conveyor/IsAtTargetSpeed }} |
| **Cooling Fan** | {{ /Demo/CoolingFan/Status }} | {{ /Demo/CoolingFan/CurrentSpeed }} | {{ /Demo/CoolingFan/TargetSpeed }} | {{ /Demo/CoolingFan/SpeedDelta }} | {{ /Demo/CoolingFan/IsAtTargetSpeed }} |
| **Exhaust Fan** | {{ /Demo/ExhaustFan/Status }} | {{ /Demo/ExhaustFan/CurrentSpeed }} | {{ /Demo/ExhaustFan/TargetSpeed }} | {{ /Demo/ExhaustFan/SpeedDelta }} | {{ /Demo/ExhaustFan/IsAtTargetSpeed }} |

::::: Footer
Speeds in RPM. The cards above and this table read the same motors.
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
::::: Tab#fans Title="Fans" Icon=Air
:::::: HomeBlaze.Components.TabLayout
::::::: Tab#coolingfan Title="Cooling Fan" Icon=AcUnit
:::::::: HomeBlaze.Components.FlowLayout MinWidth=320
::::::::: Item
:::::::::: HomeBlaze.Components.Widget Path=/Demo/CoolingFan
::::::::: Item
:::::::::: HomeBlaze.Components.Card Title="Cooling Fan Values" Icon=Info
| Value | Now |
|-------|----:|
| Status | {{ /Demo/CoolingFan/Status }} |
| Speed | {{ /Demo/CoolingFan/CurrentSpeed }} RPM |
| Target | {{ /Demo/CoolingFan/TargetSpeed }} RPM |
| At target | {{ /Demo/CoolingFan/IsAtTargetSpeed }} |
::::::: Tab#exhaustfan Title="Exhaust Fan" Icon=Air
:::::::: HomeBlaze.Components.FlowLayout MinWidth=320
::::::::: Item
:::::::::: HomeBlaze.Components.Widget Path=/Demo/ExhaustFan
::::::::: Item
:::::::::: HomeBlaze.Components.Card Title="Exhaust Fan Values" Icon=Info
| Value | Now |
|-------|----:|
| Status | {{ /Demo/ExhaustFan/Status }} |
| Speed | {{ /Demo/ExhaustFan/CurrentSpeed }} RPM |
| Target | {{ /Demo/ExhaustFan/TargetSpeed }} RPM |
| At target | {{ /Demo/ExhaustFan/IsAtTargetSpeed }} |
::::: Tab#conveyor Title="Conveyor" Icon=Settings
:::::: {.dashboard-note}
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
```json
{
  "Cells": [
    { "Row": 0, "Column": 0, "Child": { "$type": "HomeBlaze.Components.Widget", "Path": "/Demo/Compressor" } },
    { "Row": 0, "Column": 1, "Child": { "$type": "HomeBlaze.Components.Widget", "Path": "/Demo/WaterPump" } },
    { "Row": 0, "Column": 2, "Child": { "$type": "HomeBlaze.Components.Widget", "Path": "/Demo/Conveyor" } },
    { "Row": 1, "Column": 0, "Child": { "$type": "HomeBlaze.Components.Widget", "Path": "/Demo/CoolingFan" } },
    { "Row": 1, "Column": 1, "ColumnSpan": 2, "Child": { "$type": "HomeBlaze.Components.Widget", "Path": "/Demo/ExhaustFan" } }
  ]
}
```
::::: Tab#canvas Title="Canvas" Icon=Map
:::::: HomeBlaze.Components.CanvasLayout#dashboardcanvas MinHeight=620 IsSnapToGridEnabled=true GridSize=20
```json
{
  "Nodes": [
    { "X": 20, "Y": 20, "Width": 320, "Height": 280, "Child": { "$type": "HomeBlaze.Components.Widget", "Path": "/Demo/WaterPump" } },
    { "X": 380, "Y": 60, "Width": 320, "Height": 280, "Child": { "$type": "HomeBlaze.Components.Widget", "Path": "/Demo/Compressor" } },
    { "X": 200, "Y": 320, "Width": 320, "Height": 280, "Child": { "$type": "HomeBlaze.Components.Widget", "Path": "/Demo/Conveyor" } }
  ]
}
```
::: Tab#hosted Title="On This Page" Icon=Tune
:::: {.dashboard-note}
The motor in this tab exists only on this page. The second card finds it by its name, `dashboardmotor`.
:::: HomeBlaze.Components.FlowLayout MinWidth=320
::::: Item
:::::: HomeBlaze.Components.Card Title="Created Here" Icon=Add
::::::: HomeBlaze.Samples.Motor#dashboardmotor Name="Dashboard Motor" TargetSpeed=1500 SimulationInterval=00:00:01
::::: Item
:::::: HomeBlaze.Components.Card Title="Read by Name" Icon=Link
| Value | Now |
|-------|----:|
| Status | {{ dashboardmotor/Status }} |
| Speed | {{ dashboardmotor/CurrentSpeed }} RPM |
| Target | {{ dashboardmotor/TargetSpeed }} RPM |
| At target | {{ dashboardmotor/IsAtTargetSpeed }} |

::::::: Footer
The same motor again as a widget: see the next card.
::::: Item
:::::: HomeBlaze.Components.Card Title="Shown by Name" Icon=Visibility
::::::: HomeBlaze.Components.Widget Path=dashboardmotor
::

The layout above is written without closing lines, except where text follows a block. The page source is the best place to see how the colons count the depth.
