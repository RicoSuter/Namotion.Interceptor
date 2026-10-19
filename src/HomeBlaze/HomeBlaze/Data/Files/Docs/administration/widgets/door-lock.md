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
| `Label` | Text | The title of the lock, otherwise the path | The label of the tile |
| `Map` | Name or path of a `ValueMap` | None | The [ValueMap](value-map.md) that gives a lock state its text, fill and badge icon |
| `Subtitle` | Text | The state of the lock, followed by " · open" while the door is open | The second line of the tile |

## Samples

This sample operates the simulated lock `FrontDoor` next to this page, of the type `HomeBlaze.Samples.SimulatedDoorLock`. Its operation **Set door state** in the property panel opens or closes the simulated door. What it changes lasts until the lock restarts, see [Control Writes](basics.md#control-writes).

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

### Map

The cases of the map are matched by the name of the lock state, such as `Locked`, `Unlocked` or `Jammed`:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.ValueMap#lockstates
::: Case Value=Locked Text="Secured" Color=Tertiary Icon=Lock
::: Case Value=Unlocked Text="Not secured" Color=Warning Icon=LockOpen
::: Case Value=Jammed Text="Check lock" Color=Error Icon=LockReset
:: HomeBlaze.Components.GridLayout#mappedgrid Rows=1 Columns=3 CellAspectRatio=1:1
::: Cell Row=0 Column=0
:::: HomeBlaze.Components.DoorLock Path=../FrontDoor Map=lockstates
::
~~~~

:: HomeBlaze.Components.ValueMap#lockstates
::: Case Value=Locked Text="Secured" Color=Tertiary Icon=Lock
::: Case Value=Unlocked Text="Not secured" Color=Warning Icon=LockOpen
::: Case Value=Jammed Text="Check lock" Color=Error Icon=LockReset
:: HomeBlaze.Components.GridLayout#mappedgrid Rows=1 Columns=3 CellAspectRatio=1:1
::: Cell Row=0 Column=0
:::: HomeBlaze.Components.DoorLock Path=../FrontDoor Map=lockstates
::

## Behavior and Limits

- **Icon**: The door, closed or open, when the lock also implements `IDoorSensor`. Without a door sensor, or while its state is unknown, the closed door.
- **Badge**: A closed lock while locked, an open lock while unlocked or unlatched, an hourglass while the lock moves, a reset lock while it is jammed and a question mark while its state is unknown.
- **Fill**: The surface while locked, the warning color while unlocked or unlatched, and the error color while jammed.
- **Map**: A matched case replaces the text, the fill and the badge, and the text then leaves out " · open". A [CSS color](value-map.md#behavior-and-limits) of a case fills the tile with black or white text and badge, and the `TextColor` of the case replaces the color of both, see [Text colors](value-map.md#behavior-and-limits). A state without a case keeps them.
- **Click**: A locked lock is unlocked, an unlocked, unlatched or jammed one is locked, with the `Unlock` and `Lock` operations of a lock that implements `IDoorLockController`. `Unlock` asks for confirmation. A lock that only shows its state, without these operations, shows a disabled tile. The tile is also disabled while the lock moves or its state is unknown, while the operation is not enabled, and while the page is edited. A progress circle shows an operation that runs.
- **Accessibility**: The label is the accessible name of the tile. Its accessible description is the state of the lock, also when `Subtitle` replaces it on the tile, followed by what a click does while the tile is enabled, for example "Locked, unlocks on click". The tooltip names the operation that a click runs.
- **Missing lock**: A path that leads nowhere, or to a subject that is no lock, shows a question mark and a disabled tile.
- **Fills its place**: Like the other tiles, see [Switch](switch.md#tiles).

## Related

- [Switch](switch.md) - Tiles for booleans
- [Overview](overview.md) - All widgets by group
