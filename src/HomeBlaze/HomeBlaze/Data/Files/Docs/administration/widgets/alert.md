---
title: Alert
navTitle: Alert
---

# Alert

A highlighted box for a note or a warning. Type name: `HomeBlaze.Components.Alert`.

## Arguments

| Argument | Kind of value | Default | Meaning |
|----------|---------------|---------|---------|
| `Severity` | `Normal`, `Info`, `Success`, `Warning` or `Error` | `Info` | How serious the note is. It decides the color and the icon |
| `Variant` | `Text`, `Filled` or `Outlined` | `Text` | `Text` is a tinted box, `Filled` is a solid one and `Outlined` has a border |
| `Icon` | Text | None | An [icon](basics.md#icons) that replaces the icon of the severity |

## Slots

| Slot | Blocks | Holds |
|------|--------|-------|
| `Content` | Default slot | The text of the box: any page content |

## Samples

An alert without arguments has the severity `Info`:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.Alert
The pump runs at {{ ../Pump/CurrentSpeed }} RPM.
::
~~~~

:: HomeBlaze.Components.Alert
The pump runs at {{ ../Pump/CurrentSpeed }} RPM.
::

A warning with a border:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.Alert Severity=Warning Variant=Outlined
The fan is {{ ../Fan/Status }}. Check it when it stays below its target of {{ ../Fan/TargetSpeed }} RPM.
::
~~~~

:: HomeBlaze.Components.Alert Severity=Warning Variant=Outlined
The fan is {{ ../Fan/Status }}. Check it when it stays below its target of {{ ../Fan/TargetSpeed }} RPM.
::

## Behavior and Limits

- **One-line text**: An alert removes the distance above its first paragraph and below its last one, so that a text of one line is centered next to the icon. Paragraphs in between keep their distance.
- **Fixed severity**: The severity is an argument, so it does not follow a value. Use an [Indicator](indicator.md) for a state that changes.

## Related

- [Basics](basics.md) - Type names, paths, colors and icons
- [Overview](overview.md) - All widgets by group
- [Card](card.md) - A titled box around page content
- [Indicator](indicator.md) - A state that changes, as a chip or an icon
