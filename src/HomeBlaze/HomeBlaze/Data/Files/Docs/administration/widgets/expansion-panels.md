---
title: ExpansionPanels
navTitle: ExpansionPanels
---

# ExpansionPanels

Page content in panels that open and close. Each panel has a title line that stays visible. Type name: `HomeBlaze.Components.ExpansionPanels`. Slot blocks: `Panel`.

## Arguments

| Argument | Kind of value | Default | Meaning |
|----------|---------------|---------|---------|
| `Single` | Yes or no | `false` | Whether opening a panel closes the others. By default several panels can be open |

## Slots

| Slot | Blocks | Holds |
|------|--------|-------|
| `Panel` | Any number | One panel per block: any page content |

The arguments of a `Panel` block:

| Argument | Kind of value | Default | Meaning |
|----------|---------------|---------|---------|
| `Title` | Text | None | The title line of the panel |
| `Icon` | Text | None | The [icon](basics.md#icons) in front of the title |
| `Expanded` | Yes or no | `false` | Whether the panel is open when the page loads |

## Samples

Two panels, the first of them open. The second one opens with a click:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.ExpansionPanels
::: Panel Title="Pump" Icon=WaterDrop Expanded=true
The pump runs at {{ ../Pump/CurrentSpeed }} RPM.
::: Panel Title="Fan" Icon=Air
The fan runs at {{ ../Fan/CurrentSpeed }} RPM.
::
~~~~

:: HomeBlaze.Components.ExpansionPanels
::: Panel Title="Pump" Icon=WaterDrop Expanded=true
The pump runs at {{ ../Pump/CurrentSpeed }} RPM.
::: Panel Title="Fan" Icon=Air
The fan runs at {{ ../Fan/CurrentSpeed }} RPM.
::

With `Single=true` only one panel is open at a time. Each panel holds a value list, and the first one is open when the page loads. The status uses the map of [ValueMap](value-map.md):

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.ExpansionPanels Single=true
::: Panel Title="Pump" Icon=WaterDrop Expanded=true
:::: HomeBlaze.Components.ValueList
::::: Value Path=../Pump/CurrentSpeed
::::: Value Path=../Pump/Status Map=../value-map.md/motorstate
::: Panel Title="Fan" Icon=Air
:::: HomeBlaze.Components.ValueList
::::: Value Path=../Fan/CurrentSpeed
::::: Value Path=../Fan/Status Map=../value-map.md/motorstate
::
~~~~

:: HomeBlaze.Components.ExpansionPanels Single=true
::: Panel Title="Pump" Icon=WaterDrop Expanded=true
:::: HomeBlaze.Components.ValueList
::::: Value Path=../Pump/CurrentSpeed
::::: Value Path=../Pump/Status Map=../value-map.md/motorstate
::: Panel Title="Fan" Icon=Air
:::: HomeBlaze.Components.ValueList
::::: Value Path=../Fan/CurrentSpeed
::::: Value Path=../Fan/Status Map=../value-map.md/motorstate
::

## Behavior and Limits

- **All panels are rendered**: The content of a closed panel is rendered and kept up to date as well, so a value in a closed panel is current when the panel opens.
- **Opening and closing**: `Expanded` sets the state when the page loads. What the viewer opens and closes is not written to the page. It is kept for as long as the panel keeps its subject when the page is edited. That holds when the `ExpansionPanels` block and the panel both keep their subjects, which a name on each of them does, see [What an Edit of the Page Keeps](../pages.md#what-an-edit-of-the-page-keeps).

## Related

- [Basics](basics.md) - Type names, paths, spacing, colors and icons
- [Overview](overview.md) - All widgets by group
- [TabLayout](tab-layout.md) - One of several tabs at a time
- [ValueList](value-list.md) - Rows with a label and a value
