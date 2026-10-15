---
title: Responsive
navTitle: Responsive
position: 15
---

# Responsive

A block that shows its content only on screens of some widths, for example a short list on phones and a wide table on desktops. Type name: `HomeBlaze.Components.Responsive`.

## Arguments

| Argument | Kind of value | Default | Meaning |
|----------|---------------|---------|---------|
| `Show` | `Phone`, `Tablet`, `Desktop`, `NotPhone` or `NotDesktop` | `NotPhone` | The screens that show the content, see the table below |

| `Show` | Shows the content when the browser window is |
|--------|-----------------------------------------------|
| `Phone` | less than 600 pixels wide |
| `Tablet` | 600 to 1279 pixels wide |
| `Desktop` | 1280 pixels wide or more |
| `NotPhone` | 600 pixels wide or more |
| `NotDesktop` | less than 1280 pixels wide |

## Slots

| Slot | Blocks | Holds |
|------|--------|-------|
| `Content` | Default slot | The content: any page content |

## Samples

One sentence for wide screens and a shorter one for phones. Make the browser window narrower than 600 pixels to see the second one take the place of the first. The head line of the second block ends the first one:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.Responsive Show=NotPhone
The pump runs at {{ ../Pump/CurrentSpeed }} RPM and the fan at {{ ../Fan/CurrentSpeed }} RPM.
:: HomeBlaze.Components.Responsive Show=Phone
Pump {{ ../Pump/CurrentSpeed }}, fan {{ ../Fan/CurrentSpeed }} RPM.
::
~~~~

:: HomeBlaze.Components.Responsive Show=NotPhone
The pump runs at {{ ../Pump/CurrentSpeed }} RPM and the fan at {{ ../Fan/CurrentSpeed }} RPM.
:: HomeBlaze.Components.Responsive Show=Phone
Pump {{ ../Pump/CurrentSpeed }}, fan {{ ../Fan/CurrentSpeed }} RPM.
::

A table of the sample motors on desktops and a list of their speeds on smaller screens:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.Responsive Show=Desktop
::: HomeBlaze.Components.SubjectTable Source=".." Type=HomeBlaze.Samples.Motor
:::: Column Property=Name
:::: Column Property=CurrentSpeed Label="Speed"
:::: Column Property=Temperature
:: HomeBlaze.Components.Responsive Show=NotDesktop
::: HomeBlaze.Components.ValueList
:::: Value Path=../Pump/CurrentSpeed Label="Pump"
:::: Value Path=../Fan/CurrentSpeed Label="Fan"
::
~~~~

:: HomeBlaze.Components.Responsive Show=Desktop
::: HomeBlaze.Components.SubjectTable Source=".." Type=HomeBlaze.Samples.Motor
:::: Column Property=Name
:::: Column Property=CurrentSpeed Label="Speed"
:::: Column Property=Temperature
:: HomeBlaze.Components.Responsive Show=NotDesktop
::: HomeBlaze.Components.ValueList
:::: Value Path=../Pump/CurrentSpeed Label="Pump"
:::: Value Path=../Fan/CurrentSpeed Label="Fan"
::

## Behaviour and Limits

- **The width**: It is the width of the browser window, not of the place where the block stands. A narrow card on a desktop still counts as a desktop.
- **Always rendered**: The content is rendered and kept live on every screen, and only the styles of the page hide it. This works without JavaScript and when the window is resized. Content that is hidden still costs its rendering, so a page that shows the same values twice for two screens does twice the work.
- **The breakpoints**: The widths are the breakpoints of MudBlazor, the component library of HomeBlaze: `sm` from 600 pixels and `lg` from 1280 pixels.
- **Edit mode**: The content is hidden in edit mode as well. To edit hidden content, make the window wide or narrow enough, or edit the source of the page.

## Related

- [Basics](basics.md) - Type names, paths and the rules that all widgets share
- [Overview](overview.md) - All widgets
- [SubjectTable](subject-table.md) - The table of the second sample
