---
title: Responsive
navTitle: Responsive
position: 15
---

# Responsive

A block that shows its content only when the browser window is between two widths, for example a short list on phones and a wide table on desktops. Type name: `HomeBlaze.Components.Responsive`.

## Arguments

| Argument | Kind of value | Default | Meaning |
|----------|---------------|---------|---------|
| `From` | A breakpoint: `Xs`, `Sm`, `Md`, `Lg`, `Xl` or `Xxl` | `Xs` | The smallest breakpoint that shows the content |
| `To` | A breakpoint: `Xs`, `Sm`, `Md`, `Lg`, `Xl` or `Xxl` | `Xxl` | The largest breakpoint that shows the content, not before `From` |

The content shows when the window is at least as wide as the lower edge of `From` and narrower than the upper edge of `To`. Without arguments it always shows. The breakpoints are those of MudBlazor, the component library of HomeBlaze:

| Breakpoint | Window width | Typical screen |
|------------|--------------|----------------|
| `Xs` | less than 600 pixels | Phone |
| `Sm` | 600 to 959 pixels | Small tablet |
| `Md` | 960 to 1279 pixels | Large tablet |
| `Lg` | 1280 to 1919 pixels | Desktop |
| `Xl` | 1920 to 2559 pixels | Large desktop |
| `Xxl` | 2560 pixels or more | Very large desktop |

| Shown on | Arguments |
|----------|-----------|
| Phones | `To=Xs` |
| Tablets | `From=Sm To=Md` |
| Desktops | `From=Lg` |
| All but phones | `From=Sm` |
| All but desktops | `To=Md` |

MudBlazor also has combined breakpoints such as `SmAndUp`; `From` and `To` take only the single ones above, and a combined one shows an error in place of the block, see [Value Widget Errors](../page-errors.md#value-widget-errors).

## Slots

| Slot | Blocks | Holds |
|------|--------|-------|
| `Content` | Default slot | The content: any page content |

## Samples

One sentence for wide screens and a shorter one for phones. Make the browser window narrower than 600 pixels to see the second one take the place of the first. The head line of the second block ends the first one:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.Responsive From=Sm
The pump runs at {{ ../Pump/CurrentSpeed }} RPM and the fan at {{ ../Fan/CurrentSpeed }} RPM.
:: HomeBlaze.Components.Responsive To=Xs
Pump {{ ../Pump/CurrentSpeed }}, fan {{ ../Fan/CurrentSpeed }} RPM.
::
~~~~

:: HomeBlaze.Components.Responsive From=Sm
The pump runs at {{ ../Pump/CurrentSpeed }} RPM and the fan at {{ ../Fan/CurrentSpeed }} RPM.
:: HomeBlaze.Components.Responsive To=Xs
Pump {{ ../Pump/CurrentSpeed }}, fan {{ ../Fan/CurrentSpeed }} RPM.
::

A table of the sample motors on desktops and a list of their speeds on smaller screens. `To=Md` ends where `From=Lg` starts, so exactly one of them shows at any width:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.Responsive From=Lg
::: HomeBlaze.Components.SubjectTable Source=".." Type=HomeBlaze.Samples.Motor
:::: Column Property=Name
:::: Column Property=CurrentSpeed Label="Speed"
:::: Column Property=Temperature
:: HomeBlaze.Components.Responsive To=Md
::: HomeBlaze.Components.ValueList
:::: Value Path=../Pump/CurrentSpeed Label="Pump"
:::: Value Path=../Fan/CurrentSpeed Label="Fan"
::
~~~~

:: HomeBlaze.Components.Responsive From=Lg
::: HomeBlaze.Components.SubjectTable Source=".." Type=HomeBlaze.Samples.Motor
:::: Column Property=Name
:::: Column Property=CurrentSpeed Label="Speed"
:::: Column Property=Temperature
:: HomeBlaze.Components.Responsive To=Md
::: HomeBlaze.Components.ValueList
:::: Value Path=../Pump/CurrentSpeed Label="Pump"
:::: Value Path=../Fan/CurrentSpeed Label="Fan"
::

## Behaviour and Limits

- **The width**: It is the width of the browser window, not of the place where the block stands. A narrow card on a desktop still counts as a desktop.
- **Always rendered**: The content is rendered and kept live on every screen, and only the styles of the page hide it. This works without JavaScript and when the window is resized. Content that is hidden still costs its rendering, so a page that shows the same values twice for two screens does twice the work.
- **Edit mode**: The content is hidden in edit mode as well. To edit hidden content, make the window wide or narrow enough, or edit the source of the page.

## Related

- [Basics](basics.md) - Type names, paths and the rules that all widgets share
- [Overview](overview.md) - All widgets by group
- [SubjectTable](subject-table.md) - The table of the second sample
