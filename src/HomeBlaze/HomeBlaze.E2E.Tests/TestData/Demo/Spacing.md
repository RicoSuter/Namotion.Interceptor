---
title: Spacing Demo
---

# Spacing Demo

:: HomeBlaze.Samples.Motor#spacingmotor Name="Spacing Motor" TargetSpeed=1000 SimulationInterval=00:00:01
::

Text before the gauge card.

:: HomeBlaze.Components.Card#gaugecard Title="Gauge Card"
::: HomeBlaze.Components.FlowLayout MinWidth=120
:::: Item
::::: HomeBlaze.Components.Gauge Path=spacingmotor/CurrentSpeed Minimum=0 Maximum=3000 Label="First"
:::: Item
::::: HomeBlaze.Components.Gauge Path=spacingmotor/CurrentSpeed Minimum=0 Maximum=3000 Label="Second"
::

Text before the top flow.

:: HomeBlaze.Components.FlowLayout#topflow MinWidth=300
::: Item
:::: HomeBlaze.Components.Card Title="Short Card"
One line.
::: Item
:::: HomeBlaze.Components.Card Title="Tall Card"
::::: HomeBlaze.Components.ValueList
:::::: Value Path=spacingmotor/CurrentSpeed
:::::: Value Path=spacingmotor/TargetSpeed
:::::: Value Path=spacingmotor/Temperature
::::: Footer
:::::: HomeBlaze.Components.OperationButton Path=spacingmotor/EmergencyStop Color=Error Variant=Outlined
::: Item
:::: HomeBlaze.Components.Card Title="Third Card"
Third.
::: Item
:::: HomeBlaze.Components.Card Title="Fourth Card"
Fourth.
::

Text after the top flow.

:: HomeBlaze.Components.Card Title="Mixed Card"
A paragraph first.

::: HomeBlaze.Components.FlowLayout MinWidth=160
:::: Item
::::: HomeBlaze.Components.ValueTile Path=spacingmotor/CurrentSpeed Label="Speed"
:::: Item
::::: HomeBlaze.Components.ValueTile Path=spacingmotor/TargetSpeed Label="Target"
:::

A paragraph last.
::

:: HomeBlaze.Components.Card Title="List Card"
::: HomeBlaze.Components.ValueList
:::: Value Path=spacingmotor/Temperature Label="Temperature"
::::: HomeBlaze.Components.Progress Path=spacingmotor/Temperature Minimum=20 Maximum=100
:::: Value Path=spacingmotor/TargetSpeed Label="At target"
::::: HomeBlaze.Components.Indicator Path=spacingmotor/IsAtTargetSpeed Kind=Icon Icon=Speed
::

:: HomeBlaze.Components.Card Title="Footer Card"
Body of the footer card.
::: Footer
:::: HomeBlaze.Components.OperationButton Path=spacingmotor/EmergencyStop Color=Error Variant=Outlined
:::: HomeBlaze.Components.Control Path=spacingmotor/GetDiagnostics
::

:: HomeBlaze.Components.Card Title="Controls Card"
::: HomeBlaze.Components.Switch Path=spacingmotor/IsPowered Label="Power" On=spacingmotor/PowerOn Off=spacingmotor/PowerOff
::: HomeBlaze.Components.Select Path=spacingmotor/Mode Label="Mode"
::: HomeBlaze.Components.Slider Path=spacingmotor/Load Label="Load"
::

:: HomeBlaze.Components.TabLayout#spacingtabs
::: Tab Title="Hidden First"
:::: HomeBlaze.Components.Responsive Show=Phone
::::: HomeBlaze.Components.Card Title="Phone Card"
Only on a phone.
:::: HomeBlaze.Components.Responsive Show=NotPhone
::::: HomeBlaze.Components.Card Title="Wide Card"
Not on a phone.
:::: HomeBlaze.Components.Alert
The last block of the tab.
::

:: HomeBlaze.Components.ExpansionPanels#spacingpanels
::: Panel Title="Open Panel" Expanded=true
:::: HomeBlaze.Components.Card Title="Panel Card"
In a panel.
::

Text after the panels.
