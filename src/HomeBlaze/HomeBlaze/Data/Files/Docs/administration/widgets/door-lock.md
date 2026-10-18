---
title: DoorLock
navTitle: DoorLock
position: 45
---

# DoorLock

A tile that shows a door lock and its door, and locks or unlocks the lock with one click. It works with any lock, a subject that implements `IDoorLockState`, such as a [Nuki](../../devices/Nuki.md) smart lock. Type name: `HomeBlaze.Components.DoorLock`.

## Arguments

| Argument | Kind of value | Default | Meaning |
|----------|---------------|---------|---------|
| `Path` | Path (Subject) | None | The [path](basics.md#paths) of the lock |
| `Label` | Text | The title of the lock | The label of the tile |
| `Subtitle` | Text | The state of the lock | The second line of the tile |

## Samples

This sample operates the simulated lock `FrontDoor` next to this page. What it changes lasts until the lock restarts.

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.GridLayout Rows=1 Columns=3 CellAspectRatio=1:1
::: Cell Row=0 Column=0
:::: HomeBlaze.Components.DoorLock Path=../FrontDoor
::
~~~~

:: HomeBlaze.Components.GridLayout Rows=1 Columns=3 CellAspectRatio=1:1
::: Cell Row=0 Column=0
:::: HomeBlaze.Components.DoorLock Path=../FrontDoor
::

## Behavior and Limits

- **Icon**: The door, closed or open, when the lock also implements `IDoorSensor`. Without a door sensor, or while its state is unknown, the closed door.
- **Badge**: A closed lock while locked, an open lock while unlocked or unlatched, an hourglass while the lock moves, a reset lock while it is jammed and a question mark while its state is unknown.
- **Fill**: The surface while locked, the warning color while unlocked or unlatched, and the error color while jammed.
- **Click**: A locked lock is unlocked, an unlocked or unlatched one is locked. `Unlock` asks for confirmation. While the lock moves, while it is jammed or its state is unknown, while there is no operation to invoke, and while the page is edited, the tile is disabled. A progress circle shows an operation that runs.
- **Missing lock**: A path that leads nowhere, or to a subject that is no lock, shows a question mark and a disabled tile.
- **Fills its place**: Like the other tiles, see [Switch](switch.md#tiles).

## Related

- [Switch](switch.md) - Tiles for booleans
- [Overview](overview.md) - All widgets by group
