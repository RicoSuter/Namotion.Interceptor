---
title: TabLayout
navTitle: TabLayout
position: 11
---

# TabLayout

Shows one of several tabs at a time. Type name: `HomeBlaze.Components.TabLayout`. Slot blocks: `Tab`.

## Arguments

| Argument | Kind of value | Default | Meaning |
|----------|---------------|---------|---------|
| `Position` | `Top`, `Left`, `Right` or `Bottom` | `Top` | The side of the content that the tab bar is at. At `Left` and `Right` the tabs stand below each other |

## Slots

| Slot | Blocks | Holds |
|------|--------|-------|
| `Tab` | Any number | One tab per block. A `Tab` block creates a subject, so it takes arguments, an optional `#name` and any page content |

The arguments of a `Tab` block:

| Argument | Kind of value | Default | Meaning |
|----------|---------------|---------|---------|
| `Title` | Text | None | The label of the tab |
| `Icon` | Text | None | The [icon](basics.md#icons) on the tab |

## Samples

Two tabs with a name and an icon, and a third tab without a name and without an icon that holds plain text:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.TabLayout
::: Tab#pumptab Title="Pump" Icon=WaterDrop
The pump runs at {{ ../Pump/CurrentSpeed }} RPM.

:::: HomeBlaze.Components.Widget Path=../Pump
::: Tab#fantab Title="Fan" Icon=Air
The fan runs at {{ ../Fan/CurrentSpeed }} RPM.

:::: HomeBlaze.Components.Widget Path=../Fan
::: Tab Title="Notes"
A tab can hold any page content, not only widgets.
::
~~~~

:: HomeBlaze.Components.TabLayout
::: Tab#pumptab Title="Pump" Icon=WaterDrop
The pump runs at {{ ../Pump/CurrentSpeed }} RPM.

:::: HomeBlaze.Components.Widget Path=../Pump
::: Tab#fantab Title="Fan" Icon=Air
The fan runs at {{ ../Fan/CurrentSpeed }} RPM.

:::: HomeBlaze.Components.Widget Path=../Fan
::: Tab Title="Notes"
A tab can hold any page content, not only widgets.
::

Tabs inside tabs. The first tab holds a tab layout of its own, which has four colons, one more than the tab around it:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.TabLayout
::: Tab#motorstab Title="Motors" Icon=Settings
:::: HomeBlaze.Components.TabLayout
::::: Tab#innerpump Title="Pump" Icon=WaterDrop
:::::: HomeBlaze.Components.Widget Path=../Pump
::::: Tab#innerfan Title="Fan" Icon=Air
:::::: HomeBlaze.Components.Widget Path=../Fan
::: Tab#abouttab Title="About" Icon=Info
The inner tabs keep their selection while you switch between the outer tabs.
::
~~~~

:: HomeBlaze.Components.TabLayout
::: Tab#motorstab Title="Motors" Icon=Settings
:::: HomeBlaze.Components.TabLayout
::::: Tab#innerpump Title="Pump" Icon=WaterDrop
:::::: HomeBlaze.Components.Widget Path=../Pump
::::: Tab#innerfan Title="Fan" Icon=Air
:::::: HomeBlaze.Components.Widget Path=../Fan
::: Tab#abouttab Title="About" Icon=Info
The inner tabs keep their selection while you switch between the outer tabs.
::

Cards in a flow inside a tab. The colons count up from two to seven here: the tab layout, its tabs, the flow layout, its items, the cards and the widgets. The second `Item` ends the first item with its card and its widget, and the second `Tab` ends the first tab with everything in it:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.TabLayout
::: Tab#cardstab Title="Cards" Icon=Dashboard
:::: HomeBlaze.Components.FlowLayout MinWidth=320
::::: Item
:::::: HomeBlaze.Components.Card Title="Pump" Icon=WaterDrop
::::::: HomeBlaze.Components.Widget Path=../Pump
::::: Item
:::::: HomeBlaze.Components.Card Title="Fan" Icon=Air
::::::: HomeBlaze.Components.Widget Path=../Fan
::: Tab#texttab Title="Text" Icon=Notes
The pump runs at {{ ../Pump/CurrentSpeed }} RPM and the fan at {{ ../Fan/CurrentSpeed }} RPM.
::
~~~~

:: HomeBlaze.Components.TabLayout
::: Tab#cardstab Title="Cards" Icon=Dashboard
:::: HomeBlaze.Components.FlowLayout MinWidth=320
::::: Item
:::::: HomeBlaze.Components.Card Title="Pump" Icon=WaterDrop
::::::: HomeBlaze.Components.Widget Path=../Pump
::::: Item
:::::: HomeBlaze.Components.Card Title="Fan" Icon=Air
::::::: HomeBlaze.Components.Widget Path=../Fan
::: Tab#texttab Title="Text" Icon=Notes
The pump runs at {{ ../Pump/CurrentSpeed }} RPM and the fan at {{ ../Fan/CurrentSpeed }} RPM.
::

A tab layout inside a card. The tab layout is the body of the card, and the footer of the card stays below the tabs whichever tab is selected. The head line of the footer has three colons like the tab layout, so it ends the tab layout:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.Card Title="Motors" Icon=Settings
::: HomeBlaze.Components.TabLayout
:::: Tab#cardpump Title="Pump"
::::: HomeBlaze.Components.Widget Path=../Pump
:::: Tab#cardfan Title="Fan"
::::: HomeBlaze.Components.Widget Path=../Fan

::: Footer
Both tabs show the sample motors, the same motors as in the samples above.
::
~~~~

:: HomeBlaze.Components.Card Title="Motors" Icon=Settings
::: HomeBlaze.Components.TabLayout
:::: Tab#cardpump Title="Pump"
::::: HomeBlaze.Components.Widget Path=../Pump
:::: Tab#cardfan Title="Fan"
::::: HomeBlaze.Components.Widget Path=../Fan

::: Footer
Both tabs show the sample motors, the same motors as in the samples above.
::

The tab bar at the left of the content:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.TabLayout Position=Left
::: Tab#sidepump Title="Pump" Icon=WaterDrop
:::: HomeBlaze.Components.Widget Path=../Pump
::: Tab#sidefan Title="Fan" Icon=Air
:::: HomeBlaze.Components.Widget Path=../Fan
::
~~~~

:: HomeBlaze.Components.TabLayout Position=Left
::: Tab#sidepump Title="Pump" Icon=WaterDrop
:::: HomeBlaze.Components.Widget Path=../Pump
::: Tab#sidefan Title="Fan" Icon=Air
:::: HomeBlaze.Components.Widget Path=../Fan
::

## Behaviour and Limits

- **Tabs at a side need a wide window**: With `Position=Left` or `Position=Right` the tab bar is at the top while the browser window is narrower than 600 pixels, because the content would have too little room beside it. The width of the window counts, not that of the place the layout stands in. Content that cannot get as narrow as the room beside the tab bar, like a wide table, scrolls inside the tab.
- **All tabs are rendered**: Every tab is rendered and kept up to date, also the hidden ones. A subject in a hidden tab keeps running, and a hidden tab costs as much as a visible one.
- **The first tab is selected**: When the page opens, the first tab is shown. The selection is not saved and there is no argument for it.
- **Names keep the selection**: When the page text changes while the page is open, a tab with a name stays selected, also when tabs are added, removed or moved. A tab without a name is known by its position only, so the selection stays at the position. When the selected tab is gone, the first tab is selected.
- **Tabs only**: A `TabLayout` has no default slot. Content outside of a `Tab` block is an error.

## Related

- [Basics](basics.md) - Type names, paths, spacing, colours and icons
- [Overview](overview.md) - Every widget by group
- [ExpansionPanels](expansion-panels.md) - Page content in panels that open and close
- [Card](../page-widgets.md#card) - A titled box around page content, also around tabs
