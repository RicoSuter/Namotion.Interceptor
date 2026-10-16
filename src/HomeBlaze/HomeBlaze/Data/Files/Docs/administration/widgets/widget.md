---
title: Widget
navTitle: Widget
position: 22
---

# Widget

Shows the widget of a subject that exists somewhere else: in a file of its own, or in another block of the page. Type name: `HomeBlaze.Components.Widget`. Its block stays empty.

## Arguments

| Argument | Kind of value | Default | Meaning |
|----------|---------------|---------|---------|
| `Path` | Path to a subject | None | The [path](basics.md#paths) to the subject to show |

## Slots

A `Widget` has no slots.

## Samples

The three ways to refer to a subject:

- **By the name of a block**: `Path=mymotor` leads to the block with the name `#mymotor` on the same page.
- **By a relative path**: `Path=../Pump` starts at the page, so it leads to the file `Pump.json` next to the page.
- **By an absolute path**: `Path=/Docs/administration/widgets/Pump` leads to the same subject from every page.

By a path relative to the page, here the shared [sample motor](basics.md#sample-motors) `Pump`:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.Widget Path=../Pump
::
~~~~

:: HomeBlaze.Components.Widget Path=../Pump
::

A path that leads nowhere shows a message in place of a widget. Nothing exists at this path:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.Widget Path=/Missing/Motor
::
~~~~

:: HomeBlaze.Components.Widget Path=/Missing/Motor
::

## Behaviour and Limits

- **Not a copy**: The widget shows the subject itself. A motor that is shown twice is one motor.
- **A path that leads nowhere**: The widget shows the notice `Cannot resolve path` with the path. This is no error box, and a `Widget` without a `Path` shows it too, see [Notices That Are No Errors](../page-errors.md#notices-that-are-no-errors).
- **A path that leads back**: A `Widget` that refers to itself or to a block that contains it shows the notice `Cannot show path` in place of the content. Two blocks that show each other end with the same notice.
- **A subject without a widget**: When the type of the subject has no widget, the notice `No Widget component for` with the type name is shown.

## Related

- [Basics](basics.md) - Type names, paths and the sample motors
- [Overview](overview.md) - All widgets by group
- [Card](card.md) - A titled box around a widget
- [GridLayout](grid-layout.md) - Widgets in fixed rows and columns
