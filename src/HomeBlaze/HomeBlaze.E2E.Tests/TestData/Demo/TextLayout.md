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
