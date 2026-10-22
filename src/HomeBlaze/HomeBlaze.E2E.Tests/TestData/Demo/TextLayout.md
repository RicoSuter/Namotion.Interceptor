---
title: Text Layout Demo
---

# Text Layout Demo

:: HomeBlaze.Components.Card Title="Layout Card"
One line in the card.

#### Heading in the card

::: HomeBlaze.Components.Card Title="Inner Card"
Inner text.
:::

Last line in the card.
::

:: HomeBlaze.Components.TabLayout
::: Tab Title="Layout Tab"
Text in the tab.

:::: HomeBlaze.Components.Card Title="Tab Card"
In the tab card.
::::

Text at the end of the tab.
::

:: HomeBlaze.Components.FlowLayout MinWidth=300
::: Item
Text in the flow item.

:::: HomeBlaze.Components.Card Title="Flow Card"
In the flow card.
::::

Text at the end of the flow item.
::: Item
Second item.
::

:: HomeBlaze.Components.GridLayout Rows=1 Columns=2
::: Cell Row=0 Column=0
:::: HomeBlaze.Components.FlowLayout MinWidth=100
::::: Item
Text in the cell flow.

:::::: HomeBlaze.Components.Card Title="Cell Flow Card"
In the cell flow card.
::::::
::: Cell Row=0 Column=1
:::: HomeBlaze.Components.TabLayout
::::: Tab Title="Cell Tab"
Text in the cell tab.

:::::: HomeBlaze.Components.Card Title="Cell Tab Card"
In the cell tab card.
::::::
::

:: HomeBlaze.Samples.Motor#layoutmotor Name=LayoutMotor TargetSpeed=1000 SimulationInterval=00:00:01
::

:: HomeBlaze.Components.Card Title="Footer Card"
Body of the footer card.
::: Footer
:::: HomeBlaze.Components.OperationButton Path=layoutmotor/EmergencyStop Color=Error Variant=Outlined
:::: HomeBlaze.Components.Control Path=layoutmotor/GetDiagnostics
::

<div class="raw-html-block">Raw HTML.</div>
