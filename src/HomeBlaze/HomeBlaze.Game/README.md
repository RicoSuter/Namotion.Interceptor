# HomeBlaze.Game

Maintainer documentation for the game library: how it is built, which invariants hold it together and what is still open. How to write object types and tools is the subject of the [author guide](../HomeBlaze.Game.Abstractions/README.md).

## Overview

A `Game` subject is a multiplayer world that HomeBlaze shows as a page. Every browser client of the installation is a player on an infinite grid of 25 cm tiles. Players walk in 8 directions, look at the tiles in front of them and invoke the operations of the objects standing there, such as demolishing walls, adding doors to them, opening doors and sending messages, and use the tools of the game to build walls and plant trees. The page renders the world with Babylon.js as a tilted perspective scene centered on the own player. Players within 10 m along an open path talk to each other with their microphones; the server mixes the audio for every listener. Several `Game` subjects may exist; each is an independent world with its own players.

The simulation owns the state of a world and the subjects are its facade: one `GameSimulation` per game runs a fixed tick of 50 ms on plain state, fed by a lock-free queue of commands, and writes its state to the subjects; outside writes to the subjects are reported to it by a write interceptor that never waits. See [Simulation](#simulation) and [Facade](#facade).

| Project | Contents |
|---|---|
| `HomeBlaze.Game.Abstractions` | The extension contract that object libraries build on: `GameObject`, `GameTool`, `IGameWorld`, the values and the visuals; see the [author guide](../HomeBlaze.Game.Abstractions/README.md) |
| `HomeBlaze.Game` (Razor SDK) | `Game`, the simulation, players, views, voice, the page component, the JS modules and the vendored assets under `wwwroot/` |
| `HomeBlaze.Game.Buildings` (Razor SDK) | Walls, doors, the wall tool and the wall textures under `wwwroot/textures/` |
| `HomeBlaze.Game.Nature` (Razor SDK) | Trees, the tree tool, animals, and the tree and animal models under `wwwroot/models/` |
| `HomeBlaze.Game.Abstractions.Tests` | The public API snapshot of the abstractions (`VerifyChecksTests.PublicApi`) |
| `HomeBlaze.Game.Tests` | Unit tests of the core, the demo world tests and the E2E tests |
| `HomeBlaze.Game.Buildings.Tests`, `HomeBlaze.Game.Nature.Tests` | Unit tests of the object types, run against a real game |
| `HomeBlaze.Game.Benchmark` | BenchmarkDotNet benchmarks of the simulation, not run in CI; see [Scale](#scale) |

The app references the library and the object libraries `HomeBlaze.Game.Buildings` and `HomeBlaze.Game.Nature` in `HomeBlaze.csproj` and registers each assembly with one `.AddAssembly(...)` in `Program.cs`. `HomeBlaze.Game.Abstractions` is a host assembly like `HomeBlaze.Abstractions`, so the plugin loader shares it with plugins and they bind to the same types.

`Data/Files/Demo/Game.json` of the app holds the demo world, served at `/pages/Demo/Game`: a house of 56 x 40 tiles (14 x 10 m) with six rooms (living room, hall, kitchen, two bedrooms and a bathroom), brick outer walls, concrete inner walls and doors 4 to 6 tiles wide, a wooden shed with a closed door to the west, a forest of 32 trees to the east with three deer and a stag in it, each wandering within 6 m of where it was placed, 7 more trees around the house, and the tools `wall-tool` and `tree-tool`. New players spawn at 0, 0, just south of the open entrance. A script generated the file once with a fixed random seed; the script is not part of the repository, and the file is edited like any saved world. `DemoWorldTests` checks that it loads, that every room is reachable from the spawn point, that no tree blocks a door and that the animals stand on free tiles.

The library depends on `HomeBlaze.Abstractions`, `HomeBlaze.Game.Abstractions`, `HomeBlaze.Components` (`OperationInvoker`), `HomeBlaze.Services` (`CurrentClientConnection`, the configuration serializer), `HomeBlaze.Installation` (clients), `HomeBlaze.Storage.Abstractions` (`IConfigurationWriter`) and the Namotion.Interceptor generator, tracking and registry libraries. Dependency rule: `HomeBlaze.Game` may reference `HomeBlaze.AI` for agent players; `HomeBlaze.AI` never references or mentions the game. The core names no object type; the object libraries never reference the core.

| Folder | Contents |
|---|---|
| `Game*.cs` | The `Game` subject, split by concern: `Game.cs` (properties, tick handlers, the run loop), `Game.Facade.cs` (start, take-overs, what the write interceptor reports), `Game.World.cs` (the `IGameWorld` API, steps, messages, `GetActor`), `Game.Players.cs` (reconciling, renaming, housekeeping loop), `Game.Persistence.cs`, `Game.Voice.cs` (voice switches, volume table builds, voice loop) |
| `GamePlayer.cs`, `GameRegion.cs` | The player and the region subjects |
| `GameSimulationSettings.cs` | The tick, publish and save intervals |
| `Simulation/` | `GameSimulation` (tick, queue, schedules through `GameSchedules`), `GameWorldState` with `GameEntity` and `PlayerEntity`, the regions (`GameRegionMap`, `GameRegionState`, `GameObjectTakeOver`), `GamePublisher`, `GameSetWaiters`, `GameWriteInterceptor`, `OccupancySnapshot`, `SoundSnapshot`, `GameViewSession`, `GameViewSessions` |
| `World/` | Pure grid logic: `GameWorldIndex` with `ChangedChunks`, `ChangedEntities` and `GameEntityChange`, `SoundChunk`, `SoundMap`, `HearingPosition`, `HearingRange`, `SoundPathSearch`, `RateLimiter`, `SpawnFinder`, `TargetArea`, `FrontPlacement` |
| `View/` | `GameViewTracker`, `GameViewWindow`, `GameViewChanges`, and what goes to the browser: `GameViewPayload`, `GameViewPayloads`, `GameViewEntry`, `GameViewPart`, `GameViewUpdate`, `IGameViewSink`, `GamePanelState`, `GameVisualValidator`, `GameDirectionNames`, and `GameColors` for players |
| `Voice/` | `GameVoiceSettings`, `GameVoiceCodec`, `GameVoiceLoop`, `GameVoiceMixer`, `GameVolumeTable`, `GameVolumeTableBuilder`, `TalkerQueue`, `IGameVoiceSink` |
| `Clients/` | `IGameClientDirectory`, `GameClient` and the installation implementation |
| `Components/` | `GamePage.razor` with its code-behind (`GamePage.razor.cs`, layout and flushing; `GamePage.Panel.cs`, the panel), `GameViewConnection` (the page's side of its view session), `GameVoiceConnection` and `GameVoiceModule` (the voice of a page), `GameNameDialog.razor`, `GameCallerServiceProvider` |
| `wwwroot/` | `game.js` (lifecycle, Babylon loader, camera, frame rate), `game-input.js` (keyboard, pad, prediction), `game-entities.js` (parts, models, wind, glides, fading, players, tweens, ground, target marker), `game-voice.js` (audio context, capture, playback, codec seam), `game-voice-worklet.js` (capture and playback processors), `lib/`, `models/`, `textures/` |

## Model

North is +Y and east is +X. An object covers the tiles from `X` to `X + Width - 1` and from `Y` to `Y + Depth - 1`.

| Subject | Property | Kind | Notes |
|---|---|---|---|
| `Game` | `Name` | Configuration | Title falls back to "Game" |
| | `Players` | Configuration | `Dictionary<string, GamePlayer>` keyed by client id, managed by the game |
| | `Regions` | Configuration | `Dictionary<string, GameRegion>` keyed by the region's coordinates, `"{regionX}_{regionY}"`; managed by the game, which keeps every object in the region of its south-west corner |
| | `Tools` | Configuration | `Dictionary<string, GameTool>` keyed by tool name, managed by the user; the game never changes it |
| `GameRegion` | `Objects` | Configuration | `Dictionary<string, GameObject>` keyed by object name, unique across all regions. A region covers `GameRegion.Size` by `Size` tiles (64, 16 m); regionX is floor(X / 64), negative coordinates included |
| `GameObject` (abstract) | `X`, `Y`, size, blocking flags, `Visual` | | See the [author guide](../HomeBlaze.Game.Abstractions/README.md#game-objects-and-tools). An object with a width, depth or height outside 1 to 1024, possible in a loaded file that skips validation, is left out of the index, so it neither blocks nor renders |
| `GamePlayer` | `Facing` | Configuration | `GameDirection`, north by default |
| | `ClientId`, `ClientTitle`, `IsConnected` | State | `ClientId` is set from the dictionary key in `OnPlayersChanging` |
| | `IsMicrophoneOn`, `IsAudioOn` | State | Switched only by the player's own pages, not persisted, reset when the client disconnects |
| | `Width`, `Depth`, `Height` | Fixed | Always 2 x 2 x 7 (50 x 50 cm, 175 cm tall), `Size` and `BodyHeight`; a set value, also one from a loaded file, has no effect |
| | | | `IsMovementBlocking` and `IsVisible` follow `IsConnected` |

`GamePlayer` derives from `GameObject`, so one index covers players and objects and the renderer treats both alike. They still live in two collections because different parties manage them. The player's footprint is square, so turning never changes the tiles it covers. `GamePlayer` and `GameRegion` are `[ExcludeFromCreation]`.

`GameRegion.Objects` and `Tools` are typed as the abstract `GameObject` and `GameTool`, which relies on `ConfigurationJsonTypeInfoResolver` writing and reading `$type` for properties declared as an abstract `IConfigurable` class. That is why `GameObject` and `GameTool` are abstract classes rather than interfaces.

## Objects

Every object type lives in an object library and uses only `HomeBlaze.Game.Abstractions`; the [author guide](../HomeBlaze.Game.Abstractions/README.md) describes the contract and the rules. Members an object library declares itself may be internal, such as `WallTextures` or `Tree.Variants`.

| Project and namespace | Types |
|---|---|
| `HomeBlaze.Game.Buildings` | `Wall`, `WallMaterial`, `WallTool`, `Door`, `WallTextures`; walls and doors share a library because "Add door" creates doors |
| `HomeBlaze.Game.Nature` | `Tree`, `TreeVariant`, `TreeTool`, `Animal`, `AnimalVariant`, `AnimalModel` |

| Subject | Property | Kind | Notes |
|---|---|---|---|
| `Wall` | `Width`, `Depth`, `Height` | Configuration, `[Range(1, 1024)]` | Defaults 4 x 1 x 10 tiles (1 m long, 25 cm thick, 2.5 m tall). `IsSoundBlocking` is `Height >= 8`: a wall of 2 m or more blocks sound, a lower one lets it pass over |
| | `Material` | Configuration | `Bricks` (default), `Concrete` or `Wood` |
| `Door` | `Width`, `Depth`, `Height` | Configuration, `[Range(1, 1024)]` | Defaults 4 x 1 x 10; use 1 x 4 in a north-south wall |
| | `Material` | Configuration | The material of the lintel, `Bricks` by default; "Add door" copies the wall's |
| | `AutoCloseDelay` | Configuration | Zero by default, which keeps an opened door open; otherwise the door closes itself that long after it was opened (by `Open` or by writing `IsOpen`) or loaded open, and tries again after another delay while something stands in it. Negative values become zero |
| | `IsOpen` | Configuration | Closed by default. `IsMovementBlocking` and `IsSoundBlocking` are `!IsOpen`, whatever the height |
| `Tree` | `Variant`, `Rotation`, `Scale` | Configuration, `[Range]` | 0 to 6, 0 to 359 degrees clockwise, 80 to 120 percent; defaults 0, 0, 100. A loaded value outside the range shows variant 0, the rotation modulo 360 or the nearest scale |
| | `Width`, `Depth`, `Height` | Computed | The trunk footprint and height of the variant times the scale; not configurable. Blocks movement, not sound |
| `Animal` | `Variant` | Configuration | `Deer` (default) or `Stag`; an undefined value shows a deer |
| | `HomeX`, `HomeY`, `WanderRadius` | Configuration | The position it wanders around, set to where it stands on its first `OnAddedToWorld` when null, and how far in tiles, 24 (6 m) by default; zero or less lets it wander anywhere |
| | `Facing`, `IsEating`, `IsWalking` | State | The direction it faces and walks in, north by default, whether it eats and whether it walks a bout; not persisted, so a load never starts mid-walk |
| | `Width`, `Depth`, `Height` | Computed | A footprint of 4 x 4 tiles (1 m) that blocks movement, not sound; 6 tiles high for a deer, 8 for a stag |

Moving the object types into these libraries changed their `$type` names, and the objects moved from `Game.Objects` into `Game.Regions` (the file holds `"regions": { "0_0": { "objects": { ... } } }` instead of `"objects"`). The loader has no rename mapping, so a world saved before needs its objects grouped into regions and these names replaced in its file:

| Old `$type` | New `$type` |
|---|---|
| `HomeBlaze.Game.Wall`, `HomeBlaze.Game.Objects.Buildings.Wall` | `HomeBlaze.Game.Buildings.Wall` |
| `HomeBlaze.Game.Door`, `HomeBlaze.Game.Objects.Buildings.Door` | `HomeBlaze.Game.Buildings.Door` |
| `HomeBlaze.Game.Objects.Buildings.WallTool` | `HomeBlaze.Game.Buildings.WallTool` |
| `HomeBlaze.Game.Objects.Nature.Tree` | `HomeBlaze.Game.Nature.Tree` |
| `HomeBlaze.Game.Objects.Nature.TreeTool` | `HomeBlaze.Game.Nature.TreeTool` |

- `Wall`: one box of its material's texture, bricks 8, concrete 10 and wooden planks 6 tiles per repeat (2 m, 2.5 m and 1.5 m); a `Material` outside the enum looks like bricks. `IsSoundBlocking` is `Height >= 8` (the private `SoundBlockingHeight`, 2 m, above head height).
- "Add door" replaces a wall one tile thick by a wall, a door and a wall through `ReplaceAsync`, named `wall-` and `door-` plus 8 random hex digits, all with the wall's height and material. With a caller, whose player faces the wall straight (north, east, south or west), the door starts at the spot `GetSpotInFront(caller, width, 1)` along the wall, centered on the player with the extra tile of an odd width to the east or north; a diagonal facing is refused ("Face north, east, south or west to place objects."). Without a caller (subject page, MCP) it is centered on the wall. Either way it is moved inward so that one wall tile (`DoorMargin`) remains on each side. It is disabled through `KnownAttributes.IsEnabled` while the wall is thicker than one tile or shorter than 4 tiles (`MinimumDoorWallLength`). It refuses a width outside 2 to 8, a wall thicker than one tile ("Only a wall one tile thick can take a door."), a width that does not fit ("This wall is too short for a door of N tiles; it fits up to M."), a wall that reaches past the `int` range and pieces that would overlap another object ("Something is in the way."). Several doors per wall come naturally, because the remaining pieces are ordinary walls.
- `Door`: a lintel of its `Material` above the leaf height of 8.4 tiles (2.1 m, `LeafHeight`), left out when the door is not taller, and a wooden leaf 0.3 tiles thick with its pivot at the hinge, at the west end of a door running east to west or the south end of one running north to south, turned by -90 degrees while open, so it opens toward the north or the west. `Open` and `Close` write `IsOpen`; `Close` refuses with "Something is in the way." while another movement blocking object covers a door tile (`World.IsFree`). The [author guide](../HomeBlaze.Game.Abstractions/README.md#a-complete-door) walks through its code.
- `Tree`: `Variant` picks a row of the variant table `Tree.Variants`, `Rotation` turns the model around the trunk's center and `Scale` scales the row's sizes. `Width` and `Depth` are the trunk size times the scale, rounded to an even number of at least 2, so the trunk's center is a tile corner like a player's center; `Height` is the height times the scale, rounded up. The visual is one model part with the `Wind` effect in a box of crown by height by crown tiles, centered on the trunk. The crown is at least as wide as the model, so fitting it scales the model to the row's height, and the box holds the crown at every rotation. `TreeTool` takes a `Random` in an internal constructor, so tests seed it.

| Variant | Model | Height at 100 % | Trunk | Crown |
|---:|---|---:|---:|---:|
| 0 | `common_tree_1.glb` | 24 | 6 | 26 |
| 1 | `common_tree_2.glb` | 21 | 4 | 19 |
| 2 | `common_tree_3.glb` | 24 | 4 | 17 |
| 3 | `common_tree_1_autumn.glb` | 22 | 4 | 24 |
| 4 | `pine_tree_1.glb` | 22 | 4 | 13 |
| 5 | `pine_tree_2.glb` | 24 | 2 | 12 |
| 6 | `pine_tree_3.glb` | 16 | 2 | 8 |

All sizes are in tiles at a scale of 100 percent: the height is the shown height, the trunk the width and depth of the footprint, and the crown the diameter around the trunk that holds the whole crown at that height, taken from the model files.

- `Animal`, the sample of schedules, configuration driven behavior and animations. `OnAddedToWorld` sets the home when it has none and schedules a pause of 3 to 8 seconds (random, `MinimumPause` to `MaximumPause`); every pause or step replaces the one pending schedule, so running it again keeps one. A bout then runs on the game's thread: unless the animal eats, it rolls a turn from its facing (straight 50 percent, 45 degrees to either side 36 percent, 90 degrees 14 percent, `GetTurn`), and when that first step is blocked tries straight, then 45, 90 and 135 degrees to a random side first; only when all of these are blocked does it turn by 90 degrees in place, so it turns around over two bouts and the 300 ms rotation glide only ever shows small turns. A step counts as blocked when `World.IsFree` refuses the moved footprint or it would leave `WanderRadius` around the home without coming closer to it, so an animal outside walks back. A bout walks 4 to 12 tiles (`MinimumBoutSteps` to `MaximumBoutSteps`), one every 250 ms (`StepInterval`, about 1 m/s), writing `X`, `Y` and `Facing`, and ends at its last step, at a blocked step or when the animal eats, one interval after its last step so the walk lasts through the glide; then the next pause follows. "Feed" writes `IsEating`, and a change hook schedules its end after the eat animation, also for an animal added while eating, for example after a restart; the end of eating starts a new pause. The visual is one model part that plays `Eating` once while it eats, loops `Walk` during a bout and `Idle` otherwise, turned by `GameDirection.GetRotation`. The model is scaled to the variant's length (6 tiles for the deer, 7 for the stag) in a square box centered on the footprint, so its head and tail reach beyond it at every facing. The models are Quaternius' CC0 deer and stag, see `HomeBlaze.Game.Nature/wwwroot/models/animals/LICENSE.md`. `Animal` takes a `Random` in an internal constructor, so tests script it.

## Simulation

`GameSimulation` is the only writer of the world state. It runs one tick every `GameSimulationSettings.TickInterval` (50 ms) on a `PeriodicTimer` of the game's `TimeProvider`. A tick runs synchronously without awaiting, so the thread running it is the simulation thread for that tick: the thread-static `GameSimulation.Current` names the simulation whose tick runs on the calling thread, and `IsCurrent` (`Game.IsOnSimulationThread`) tells the write interceptor and `IsFree` where they run. Tests run ticks on the test thread with `Game.RunTickForTest`.

Every other thread reaches the simulation through a lock-free `ConcurrentQueue`:

- `Post(action)` queues an action for the next tick.
- `InvokeAsync(function, cancellationToken)` queues a command that answers. Its task, created with `RunContinuationsAsynchronously`, completes at the end of its tick, after the tick handlers, so a caller sees what the tick published. Canceling before the tick ran the command drops it. On the simulation thread the function runs inline instead.
- A place or replace from another thread waits in `GameSetWaiters` until a publish wrote the object set with its objects, which a save in progress delays, so `PlaceAsync` returns with the object attached.
- A tick runs only what was queued when it started; work that an action queues or schedules waits for the next tick.

A command that throws fails only its own task. A throwing action, schedule, tick handler or `OnAddedToWorld` is logged and the tick goes on: a streak of ticks with failures warns once, stays at debug level while it lasts, and warns again after a minute with the number it suppressed.

Each tick, in order:

1. The due schedules (`GameSchedules`, a priority queue by due time), then the queued commands in queue order: steps and turns, world commands, objects written from outside, take-overs, teleports, voice switches and reconciles of the players.
2. Publish the object set: read the objects written so far in the tick again, write the changed regions and the region set (see [Facade](#facade)), unless a save paused publishing, so placed objects are attached when their command completes, and call `OnAddedToWorld` for the objects that publish attached and that entered the world by a place, a replacement or a take-over.
3. Read again what those `OnAddedToWorld` calls wrote.
4. Publish the snapshots for other threads: the `OccupancySnapshot` that `IsFree` reads (immutable chunks, only the changed ones replaced, swapped in with a volatile write) and the players' positions that `GetSpotInFront` and `GetActor` read.
5. Let the voice loop run or pause.
6. Update the volume table: take the chunks whose sound blocking changed into the `SoundSnapshot`, publish the table of a finished build, start the next build when one is due, and raise `VolumeTableChanged` (see [Voice](#voice)). The tick never waits for a build.
7. Publish the players' state to the subjects when `PublishInterval` (250 ms) passed, unless a save paused publishing.
8. Start a save when `SaveInterval` (10 s) passed and something changed.
9. Complete the places and replaces whose objects a publish wrote.
10. Take in the view sessions opened since the last tick, drop the disposed ones and update the others (see [Views](#views)).
11. Clear the changes the index recorded in the tick.

Then the tasks of the commands of the tick complete.

State, touched only on the simulation thread (`GameWorldState`):

- the players by client id (`PlayerEntity`): area, facing, connected, title, the rate limiters of steps and turns, the time of the last message, the microphone page and the speaker pages;
- the objects by name: the subject and its entry (`GameEntity`: area, movement blocking, sound blocking, visible, visual, title, height), read from the subject when a command or a write changed it;
- the regions (`GameRegionMap`): which region holds each object and which regions the next publish writes;
- the chunk index (`GameWorldIndex`, chunks of 16 by 16 tiles), which records per tick the changed chunks, the changed entities with their area before, and the chunks whose sound blocked tiles may have changed;
- the schedules, in `GameSimulation`.

`Game.StopAsync` stops the simulation: one last tick runs what was queued, the schedules are dropped and later work fails with the public `GameStoppedException` of the abstractions. A start after a stop restarts it, takes the subjects over again, calls `OnAddedToWorld` for every object, since their schedules were dropped, and refreshes every view.

Steps keep the browser protocol: the page awaits the `MoveResult` of `Game.StepAsync`, which completes in the next tick; the browser's prediction hides that. A token bucket per player (`RateLimiter`, burst `StepBurst` of 3, one token per `StepInterval` of 180 ms) limits steps, a second one limits turns.

## Facade

The subjects show what the simulation holds, and every write to them reaches the simulation, which never asks the subjects again for what it owns.

Regions: only changed regions are written. A place or remove writes the dictionary of one region, so its cost grows with the objects of that region, not of the world. Adding or emptying a region also writes `Game.Regions`, which is small; empty regions are removed. An object whose position crosses a region boundary, such as a walking animal or a teleport through `X` and `Y`, migrates in one publish and never leaves the graph in between, so it keeps its identity and schedules, `OnAddedToWorld` does not run again and no outside write to it is missed: the publish writes the regions that gain objects first, still holding the objects that left them, then the region set with the new regions, then the regions without the objects that left them, and last the region set without the emptied regions. The tick reads the objects written earlier in the tick before it publishes, so a migration by a schedule, a command or an outside write is published in the same tick.

Publishing (`GamePublisher`) writes from the simulation thread: the changed regions and `Game.Regions` in the tick they changed, and every `PublishInterval` the latest values of the players changed since the last publish (`X`, `Y`, `Facing`, `IsConnected`, `ClientTitle`, `IsMicrophoneOn`, `IsAudioOn`) and `Game.Players` when the set changed. Object properties are not published; their writers set them. `PublishVersion` increases with every publish that wrote, which tells a page to read the subjects again. A synchronous observer of these writes on the simulation thread may place, remove or move objects; what it changes is published with the next publish.

The write interceptor (`GameWriteInterceptor`, registered once per context with `TryAddService` when a game starts) sees a write after it landed and reports it to the game of the written subject:

- A property of a `GameObject` of a running game marks the object changed, directly on the simulation thread (a schedule or `OnAddedToWorld` writing any object) and through the queue from other threads, so a command queued after the write sees it. The simulation reads every changed object again before the views update.
- `Game.Players` or `Game.Regions` queues a take-over carrying the new value, so a publish in between cannot overwrite it. The simulation takes over the world without overlap checks, cancels the schedules of removed objects and calls `OnAddedToWorld` for new ones once the publish that follows attached them, since a publish between the write and the take-over may have detached them. A take-over of the regions moves an object listed in another region than that of its position into its region, removes regions listed under a key that names no region, gives an object a new name when an earlier region listed its name, and writes the region set again.
- `GameRegion.Objects` queues a take-over of that region: objects it leaves out leave the world, new ones enter it, under a new name when another object has theirs, kept ones are read again, and the next publish writes the region again. Last writer wins.
- A write to `GamePlayer.X`, `Y` or `Facing` other than the publisher's own queues a teleport (without checks) or a turn; this rule comes before the general object rule.
- Writes to `GamePlayer.ClientId`, `ClientTitle`, `IsConnected`, `IsMicrophoneOn` and `IsAudioOn` from outside the simulation are refused before they land with `InvalidOperationException`, also through the registry; their setters stay internal. The game's own `OnPlayersChanging` hook assigns the client ids from the keys of `Players` on any thread.

The interceptor runs before the transaction interceptor, so a refused write fails where it is made. It never waits for the simulation, so a write from a synchronous change observer cannot deadlock with the simulation's own writes. It ignores exactly the publisher's own writes, by subject, property and written instance; an observer that writes another set during a publish is not ignored.

## Persistence

- The game writes its configuration through the first parent `IConfigurationWriter`. Without one it logs a warning and does not retry.
- It saves at most every `SaveInterval` (10 s) when something changed, and on shutdown: publish, pause publishing, write on the thread pool, resume publishing once the write completed. The file therefore holds one consistent publish. A write that hangs for more than a save interval lets publishing go on with a warning, at the price of a file that may mix two publishes.
- Steps, place, remove, replace, take-overs and writes to objects only request a save, which the next interval writes. The objects a publish attached are read again, for writes the interceptor did not see before, but request a save only when that read finds a change, so a game that starts and is left alone does not save. Canceling a world command after the game processed it leaves the change applied.
- A write that fails or returns false logs a warning and requests the save again, so the next interval retries; the game keeps running.
- A crash loses at most one save interval of positions and changes. Schedules are not saved; `OnAddedToWorld` sets them up again after a restart.
- A file that lists an object in another region than that of its position loads; the start moves the object into its region.
- Anonymous players are written like any other but dropped by the first reconcile after a load, because their client no longer exists.

## Clients and players

The game reads clients through the internal `IGameClientDirectory` (list clients with id, title, named and connected; send a notification; rename a client; recognize property changes that concern clients). `InstallationClientDirectory` finds the `[SingleInstance]` `HomeBlazeInstallation` in the registry and caches it while the registry still knows it; renaming calls `HomeBlazeClient.RenameAsync`, which names an anonymous client and saves the installation's client configuration. Game unit tests use `FakeClientDirectory` because the installation's own client reconciling is internal to it; `InstallationClientDirectoryTests` and the E2E tests cover the real one.

The housekeeping loop reads the clients and queues a reconcile, then waits for either the signal or `HousekeepingInterval` (5 s). The signal is a `SemaphoreSlim(0, 1)` released by `SignalReconcile` on client changes, outside writes of `Players` and `ApplyConfigurationAsync`. Exactly one pending wait is kept across passes: a new wait per pass would leave the old one queued to swallow the next signal. Reconcile rules, applied by the simulation:

- A client that is connected or named has a player; a new player starts at 0,0. A player whose client is gone, or is unnamed and disconnected, is removed.
- `ClientTitle` and `IsConnected` mirror the client. When a client connects, its player joins at the nearest position to where it stands at which its whole footprint is free, found ring by ring by `SpawnFinder` up to 256 tiles away.
- An offline named player keeps its saved position but is neither visible nor blocking, so nobody walks into an invisible obstacle.
- Renaming a client keeps its id, so its player keeps its position. Forgetting a client turns its tabs into new anonymous clients, so the old player is removed and new ones spawn. All tabs of a named client control the same player.
- A player that leaves or disconnects has its voice turned off and is released in the mixer by the simulation.
- Without an installation, `GetClients` returns null and the players stay as they are.

A player changes the name of its own client through the internal `Game.RenameAsync(caller, name, cancellationToken)`, which the page calls from the pencil button of its own Nearby entry. It is not an operation, so MCP, agents and other clients cannot rename a client through the game. It refuses a caller that is not a player of this game ("Only a player in the game can change their name.") and a name that is empty or longer than `MaximumNameLength` (50) after trimming, passes the trimmed name to the directory and queues a reconcile, through which the player takes the new title. `Game.GetClientName(caller)` gives the name the dialog starts from, null while the client is anonymous.

## Movement and interaction

Moving is not an operation. The page calls the internal `Game.StepAsync(clientId, direction, turnOnly)` once per step and passes only its own circuit's client, so MCP, agents and other clients cannot move a player. Rules, applied by the simulation:

- The client must have a player in this game, and the direction must be defined.
- The rate limiters of steps and turns refuse a step taken too early, which changes nothing.
- Every accepted attempt sets the facing, also when the step is blocked.
- A step moves the footprint one tile and is refused when any tile it would cover holds another movement blocking object that does not already overlap the player, so a player can always step out of what it stands in, such as a door closed on it.
- A diagonal step is refused when both of its orthogonal steps would be refused, so players cannot squeeze between corner-touching blockers.
- A step that would leave the `int` range is refused (`GameArea.TryMove` uses `long` arithmetic).

The target area (`TargetArea`) is the footprint shifted one tile toward the facing, minus the footprint itself: 2 tiles straight ahead, 3 tiles around the corner when facing diagonally. The view session computes the visible objects there, ordered from south-west to north-east, for the panel.

Operations are the actions, those of the objects and those of the tools in `Game.Tools`; the game itself has none. Those that need to know who acts take `[FromServices] GameCaller? caller`; the page invokes through `GameCallerServiceProvider`, which returns its `GameCaller` and delegates every other service to the circuit. The internal `Game.GetActor` is the single place that turns a caller into the connected player of that client in this game and returns null otherwise; outside the simulation thread it answers from the state of the last tick. The subject page, MCP and agents pass no caller.

| Operation | Actor | Behavior |
|---|---|---|
| `WallTool.BuildWallAsync` ("Build wall") | Required | `length` 1 to 40 (4 when empty), `height` 1 to 20 (10), `material` (`Bricks`). Builds a wall one tile thick right in front of the actor, across the facing and centered on it (`GetSpotInFront(caller, length, 1)`, `PlaceAsync`), so a length of 40 reaches about 20 tiles to each side; named `wall-` plus 8 random hex digits. Refuses parameters out of range, diagonal facing and blocked tiles |
| `TreeTool.PlantTreeAsync` ("Plant tree") | Required | `variant` 0 to 6, random when empty. Plants a tree with a random rotation and a random scale of 80 to 120 percent, its trunk footprint right in front of and centered on the actor; named `tree-` plus 8 random hex digits |
| `Wall.DemolishAsync`, `Tree.DemolishAsync` | None | `RemoveAsync` |
| `Wall.AddDoorAsync` ("Add door") | Optional | `width` 2 to 8 (4 when empty). Replaces the wall by a wall, a door and a wall through `ReplaceAsync`; see [Objects](#objects) |
| `Animal.Feed` ("Feed") | None | Writes `IsEating`; refuses an animal that already eats. Disabled while it eats (`Animal.Feed_IsEnabled`) |
| `Door.Open`, `Door.Close` | None | Write `IsOpen` directly. Refuse the current state, and `Close` refuses with "Something is in the way." while another movement blocking object covers a door tile |
| `GamePlayer.SendMessageAsync` | Optional | Notification "Message from {sender}" to the player's client, "Message" without a caller; a caller that is not a connected player of the game is refused ("Only a player in the game can send messages."). At most 500 characters after trimming; one message per second per sending player, kept in the simulation's player state |

The panel offers every operation except those excluded from the user interface and those disabled through their `KnownAttributes.IsEnabled` attribute (`Door.Open_IsEnabled`, `Door.Close_IsEnabled`, `Wall.AddDoor_IsEnabled`, `Animal.Feed_IsEnabled`). Disabled operations are left out rather than shown disabled, so the number keys only reach operations that apply. Invoking goes through `OperationInvoker.InvokeAsync`, so parameter, confirmation, error and result dialogs behave as in the subject browser. Before invoking, the page checks that the object is still in the target area; while a dialog is open the browser does not move the player. The page remembers the parameters entered per operation, keyed by object type and operation (`{type}.{method}`), so "Add door" remembers its width across walls, for as long as the page lives. It prefills the next dialog of that operation through `initialValues` of `OperationInvoker.InvokeAsync`, which reports the entered values through `parametersEntered`; only parameters entered in one field are prefilled.

## Views

A page registers a view session with the simulation (`Game.Views.Open`) with the client it follows and a sink. The page never reads the game or its subjects for the view and subscribes to no property changes.

- A session sees the chunks within `GameViewTracker.ViewRadius` (200 tiles, 50 m) of its player, or of the origin without one. Each tick it processes only the changed entities that touch its window, before or after the change, which it finds in buckets of 16 by 16 chunks (`GameViewChanges`) that the first session of the tick that needs them sorts the changes into: it upserts one that is in the window and differs from what the page knows, and removes one that left the window or the world. Whole chunks are read only for the edge strips that enter the window when its player crosses a chunk boundary and for a full refresh (the first update, another own player, a jump, a restart); the strips that leave the window are forgotten. So the cost of a page follows what changes inside its window, not how much stands in the changed chunks. At most `MaximumFullRefreshesPerTick` (4) sessions refresh fully in one tick; the others wait for the next.
- `GameViewTracker` is the per-page diff state: per known entity a small struct with what it compares (area, height, facing, blocking, voice switches, title, the visual reference, whether it is the page's player) and its id, updated in place, and per window chunk the known entities that cover it. An entity spanning several chunks is known once, and ids stay stable while an entity stays in view. An unchanged view returns a shared empty update and allocates nothing.
- Shared payloads: what the browser draws of an entity (kind, area, height, facing, title, color, blocking, voice switches and the validated parts of its visual) is an immutable `GameViewPayload` that `GameViewPayloads` builds at most once per entity per tick, when the first page needs it, and all pages of the tick share; an entity that changes outside every window builds none. An update entry carries the page's id for the entity, whether it is the page's player, and the payload; the browser reads `{ id, isSelf, payload }`. Payloads have two kinds, `player` and `object`. Player colors derive from the client id with FNV-1a and are cached per client id; players carry no visual, because the page draws them itself. Visual validation (`GameVisualValidator`, limits in the [author guide](../HomeBlaze.Game.Abstractions/README.md#visuals)) replaces an invalid visual by the object's default box and warns once per type, remembering the types in a `ConditionalWeakTable` so that an unloaded plugin type is not kept alive.
- Backpressure: while the previous update of a session has not reached the browser (`IGameViewSink.IsBusy`), the simulation skips the session and collects the changed entities that touch the window the page knows, each once with the area the page knows; the next update carries everything since the last one. Above `MaximumPendingEntities` (4,096) collected entities the session drops them and makes its next update a full refresh.
- Panel data comes from the session too, as a `GamePanelState`: the position text, the objects in front, recomputed when the player or the chunks in front of it changed, and whether the client has a player. Nearby comes from the volume table. Operations of the targets run through the usual operation flow on the subjects.

The page's side is `GameViewConnection`. The session delivers on the simulation thread, which only stores the update and raises `Delivered` on the thread pool; the page then applies it on its renderer with `applyUpdate` and releases the session for the next one, so at most one update is on its way to the browser. A `JSException` from `applyUpdate` marks the view broken; later updates skip the browser but keep the panel, the voice and the actions in step.

## Voice

Players talk to each other with their microphones. Here a talker is a player whose microphone is on, and the speaker is the toggle that plays what a player hears. The server decides who hears whom and mixes the audio for every listener in C#; the browser only captures and plays it with its native audio APIs. Frames go from the page to the mixer directly, never through the simulation.

Voice needs a secure context (HTTPS or `localhost`), because browsers offer `getUserMedia` and `AudioWorklet` nowhere else. On plain HTTP both buttons are disabled with the tooltip "Voice needs HTTPS". For manual testing from other devices on the LAN, start the app with an HTTPS URL as well, for example `--urls "http://0.0.0.0:5280;https://0.0.0.0:5281"`. Kestrel then serves HTTPS with the ASP.NET development certificate, which is issued for `localhost`, so each browser shows a certificate warning once.

Hearing range (`World/HearingRange`, the only definition of who hears whom and how loud):

- Sound travels along the shortest open path between the footprint centers. Players whose centers are `HearingRadius` (40 tiles, 10 m) or more apart never hear each other.
- The volume is the distance gain of the path length times the occlusion gain of the detour, the length by which the path exceeds the straight distance. The distance gain is 1 up to `ReferenceDistance` (4 tiles, 1 m) and `ReferenceDistance / length` beyond, so 2 m give 0.5 and 5 m give 0.2, times a smoothstep fade from 1 to 0 over the last `EdgeFade` (8 tiles, 2 m) before the radius. The occlusion gain is `1 / (1 + detour / HalvingDetour)`, so a detour of `HalvingDetour` (4 tiles, 1 m) halves the volume. The four constants live in `HearingRange`.
- Sound follows `GameObject.IsSoundBlocking`, not the movement rule `IsMovementBlocking`: a tile is sound blocked while an object with `IsSoundBlocking` covers it. Walls of at least 8 tiles (2 m) and closed doors block sound; lower walls, open doors, players, trees and objects that only block movement do not. Visibility does not matter, and the height matters only through an object's own rule, such as the wall's. A player in a room whose walls and doors are all closed is silent outside it.
- While the straight line between the centers touches no blocking tile, the path is that line and its exact length counts, with no detour. The walk takes every tile that the closed segment touches, also only at an edge or a corner, so the line slips neither between two walls that touch at a corner nor along the face of a wall. A line between two players at the same `Y` runs on the grid line between two rows and touches both. Tiles inside either player's footprint are skipped. The walk computes in half tiles, where every footprint center is a whole number, with `long` arithmetic, so it is exact, symmetric and safe near the `int` limits.
- Otherwise `World/SoundPathSearch` finds the path. It runs Dijkstra between the centers of the tiles within the radius plus 2 tiles around the source footprint, 86 by 86 tiles for a player. A tile is open while no object with `IsSoundBlocking` covers it. Steps go to the 16 tiles at the offsets (1, 0), (1, 1) and (2, 1) in every orientation, with integer lengths in thousandths of a tile, and need every tile that the segment between the two centers passes or touches to be open: a straight step only its target, a diagonal step also both tiles beside its corner, so it never cuts through a joint where two walls meet at a corner, and a step to (2, 1) also the tiles (1, 0) and (1, 1), since it passes through no corner point. The path keeps half a tile from a wall instead of only not touching it, so unlike the line it may run beside the face of a wall, and a gap of one tile passes sound although players, 2 tiles wide, cannot walk through it.
- The search starts at every tile of the source footprint with its grid length from the footprint center, and a target's length is the shortest over its footprint tiles of the length there plus the grid length to its center. The tiles of the source footprint count as open even where a sound blocking object overlaps them, like the line walk skips them, and a target tile that is not open still gets the length of a step into it but continues no path. So that both orders agree also there, the search always runs from the player with the ordinally smaller client id.
- A grid path exceeds the straight length by up to 2.75 % depending on its direction. The detour is therefore the grid length of the path minus the grid length without walls between the two centers by the same rule (`SoundPathSearch.GetOpenLength`, through the tiles nearest to each center, which is exact for players), which cancels most of that excess, so moving a player across the boundary of the line of sight changes the volume only slightly. A detour that bends only within the freedom the grid directions leave counts as none.
- Both orders give the same volume: the line walk visits the same tiles, and the search sums integer lengths and runs from a fixed end. Paths longer than the radius plus 2 tiles count as none, which cannot hide an audible path, because the grid excess within the radius stays below 1.1 tiles. A footprint whose width or depth is outside 1 to `GameObject.MaximumSize` gets no path, and a player of a size that the index leaves out gets no row.
- `SoundMap` reads the sound blocked tiles from the `SoundSnapshot` and labels the regions of connected open tiles by a flood fill; a region of more tiles than the search grid of a player (`MaximumClosedTiles`) counts as open. Two players in different regions, at least one of them closed, are silent to each other without a search (`SoundMap.AreSeparated`). The search reads the tiles of its area from the map's chunks into a bitmap and keeps the search lengths of recent small sources, such as players, while the sound version stays the same.

Volume table (`Voice/GameVolumeTable`, built by `GameVolumeTableBuilder`, driven by `Game.Voice.cs`):

- An immutable table with a row for every connected player, regardless of its toggles, listing the players it hears and their volume above 0. The mixer and Nearby read it without a lock.
- It is built on the thread pool from immutable inputs, one build at a time: the `SoundSnapshot` of the sound blocked tiles per chunk (immutable chunks of 16 by 16 bits, only the chunks whose sound blocking changed replaced, swapped in with a volatile write) and a copy of the client ids and areas of the connected players. The snapshot carries the index's `SoundVersion`, which only a change of a sound blocking object increases (added, removed, moved, resized or its `IsSoundBlocking` toggled), never a player's step.
- The tick starts a build when none runs, `VolumeTableInterval` (100 ms) passed since the last one started and the hearing players' positions or the sound blocked tiles changed. It publishes a finished table in the first tick after the build completed, with a volatile write, and then raises `VolumeTableChanged` on the simulation thread. A change that arrives while a build runs waits for it, so the extra latency is one tick plus up to two build durations. A voice switch changes no row, so it raises `VolumeTableChanged` without a build.
- The builder compares only the players within the hearing area of each other, found in cells of 64 by 64 tiles, and computes each pair once, from the player with the ordinally smaller client id, adding it to both rows. It owns its caches (the region labels and the search lengths), which only a new sound version drops.
- A failing build is logged, warning once per streak, and built again after the interval. A stop drops the table of a running build, and a restart starts from an empty table and builds afresh once that build completed.

Toggles: only the player's own pages switch `IsMicrophoneOn` and `IsAudioOn`, through the internal `Game.SetMicrophoneAsync(clientId, pageId, isOn)` and `Game.SetAudioAsync(clientId, pageId, isOn)`, commands that complete in the next tick. Like `StepAsync` they are not operations, so MCP, agents and other clients can read but not change them. They accept only a player of this game and refuse turning on while it is disconnected; turning off always works, so a closing page removes its share. The page id is the page's view id.

- The player entity keeps `MicrophonePageId`, the page that turned the microphone on last and the only one whose frames count, and `AudioPageIds`, the pages with the speaker on. Turning off removes only the calling page's share. `IsMicrophoneOn` and `IsAudioOn` follow them with the next publish.
- A disconnect resets both and drops the player's queued frames. The simulation tells the mixer about microphone pages; the mixer never calls into the game, so there is no lock order between them.

Audio and transport:

- 16 kHz, mono, signed 16-bit little-endian PCM in frames of 20 ms (320 samples, 640 bytes), about 256 kbit/s each way per active player.
- Frames travel over the Blazor connection: the browser calls `ReceiveMicrophoneFrame(byte[])` of the page, the page calls `play(viewId, frame)`. Blazor transfers both as binary, and the circuit identifies the player, so no extra endpoint exists.
- `GameVoiceCodec` in C# and `encodeFrame`/`decodeFrame` in `game-voice.js` are the only places that know the encoding, so Opus can replace it without touching the mixer, the range rule or the UI.

Mixer (`Voice/GameVoiceMixer`, one per game, with its own lock):

- `SetMicrophonePage(playerId, pageId)`, called by the simulation, creates the player's talker, clears its queue when the page changes, or removes it for none.
- `Receive(playerId, pageId, frame)` drops a frame that does not have exactly 640 bytes, does not come from the player's microphone page, or exceeds `FrameRateTolerance` times the frame rate (a `RateLimiter` with a burst of `ServerQueueMaximum`). Otherwise it decodes the samples into the talker's `TalkerQueue`, a pooled ring buffer, so steady speech allocates nothing per incoming frame.
- A talker queue plays once it holds its start size, grows the start size by `BufferGrowth` up to `ServerQueueMaximum` when it runs dry while playing, and shrinks it by one frame after `BufferShrinkDelay` of playing without running dry. Every dequeue notes how many frames it found, and at the end of each `TrimWindow` of playing the queue drops the oldest frames down to its start size when the fewest it found exceeded the start size by more than `TrimHeadroom`. Bursts and gaps therefore pass without a drop, because a burst raises the level only until the following gap consumes it, while delay that stays for a whole window, also after the start size shrank, is dropped. A frame arriving while the queue holds `ServerQueueMaximum` drops the oldest. `Trims` counts the drops of either kind and `Underruns` the times the queue ran dry while playing.
- `RegisterSink(playerId, sink)` adds a page with the speaker on and returns an `IDisposable` that removes it; a player can have several. `IGameVoiceSink.Send` returns without waiting for the browser, never changes the frame, which the listener's sinks share, and must not throw when a tick that already took the sinks calls it once more after its registration was disposed.
- `Mix(table)` takes one frame per playing talker into pooled buffers and copies the list of listeners under the mixer lock. Outside it, it sums for every listener with sinks the frames of the talkers in its table row, scaled by their volume and never its own, into a reused `int` buffer, clamps to 16 bits into a new `byte[]` and hands it to each of the listener's sinks. A listener that hears no frame in a tick gets nothing. A failing sink is logged as a warning the first time and at debug level after that.
- Volumes glide instead of jumping, so a new table does not click. The mixer keeps per listener the volume of each talker in the tick it last mixed it, in dictionaries it reuses, and scales each frame by a gain that runs linearly from the previous volume to the new one, reaching the new one exactly at the last sample. A pair new in the row, or one after a tick in which no talker had a frame, glides up from 0. A pair that left the row glides down to 0 over one more frame of the talker if the talker has one in that tick, and is dropped otherwise; listeners without sinks are dropped too. Mixing therefore allocates nothing in steady state apart from the one `byte[]` per listener frame.
- The voice loop (`GameVoiceLoop`) has its own thread of control: it ticks every `FrameDuration` on a `PeriodicTimer` of the game's `TimeProvider`, but only while a player has the microphone or the speaker on, which the simulation switches every tick; otherwise it pauses, which changes nothing anybody hears, since nothing is mixed or sent then. After a pause every talker glides in from silence, as after a gap. It stops with the simulation, also when that one ends on an unexpected exception. A failing tick logs a warning when ticks start failing and stays at debug level while they keep failing, so a persistent fault does not flood the log at the frame rate.

Settings (`Voice/GameVoiceSettings`, the one place for every timing and size):

| Constant | Value | Meaning |
|---|---|---|
| `SampleRate` | 16000 | Samples per second of every frame |
| `FrameDuration` | 20 ms | Length of one frame |
| `SamplesPerFrame`, `FrameBytes` | 320, 640 | Size of one frame, derived from the two above |
| `ServerQueueStart` | 1 frame | Frames a talker queue needs before it plays |
| `ServerQueueMaximum` | 6 frames | Frames beyond which a talker queue drops the oldest |
| `PlaybackBufferStart` | 40 ms | Audio the browser buffers before it plays |
| `PlaybackBufferMaximum` | 120 ms | Largest buffer the browser grows to |
| `BufferGrowth` | 1 frame | Growth of a buffer after it ran dry |
| `TrimWindow` | 1 s | Time over which a buffer watches its lowest level before it decides whether to drop standing delay |
| `TrimHeadroom` | 1 frame | Frames by which the lowest level of a buffer over `TrimWindow` may exceed its target before the buffer drops the excess down to the target |
| `PlaybackRamp` | 2 ms | Fade with which the browser's playback leaves and enters silence and smooths a dropped stretch |
| `BufferShrinkDelay` | 5 s | Time without running dry before a buffer shrinks by one frame |
| `VolumeTableInterval` | 100 ms | Shortest time between the starts of two volume table builds |
| `MaximumPendingSends` | 10 | Sends to a page in flight beyond which new frames to it are dropped |
| `FrameRateTolerance` | 1.5 | Allowed incoming frame rate as a multiple of the nominal rate |
| `PlayTimeout` | 1 s | Time after which a frame sent to a page counts as lost and no longer as pending |
| `CaptureStartTimeout` | 2 min | Time a page waits for the browser to start its microphone, including the permission prompt |
| `BrowserCallTimeout` | 10 s | Time a page waits for any other voice call to the browser |

The page passes the browser's values to `game-voice.js` when it starts, so JavaScript holds no copy. Expected delay from mouth to ear, estimated from these values and not yet measured: about 100 to 130 ms on the LAN and 150 to 220 ms over the internet, plus whatever Bluetooth headsets add.

Page (`GameVoiceConnection` and `GameVoiceModule`):

- The **Voice** section next to Position holds a microphone button (`Mic`/`MicOff`) and a speaker button (`VolumeUp`/`VolumeOff`), filled with the primary color while on and toggled by M and N too. Their accessible names stay "Microphone" and "Speaker", with `aria-pressed` for the state and `aria-keyshortcuts` for the key; the tooltips say "Turn the microphone on (M)" and so on. Until `game-voice.js` answered both are disabled, and without a player a button stays enabled only to turn off what is still on. While a toggle runs, further clicks and keys are ignored, but the button stays enabled so it keeps the focus.
- Turning the microphone on starts capturing first, waiting up to `CaptureStartTimeout`, and calls `SetMicrophoneAsync` only once that succeeded. Turning the speaker on starts playback, registers the page's sink and calls `SetAudioAsync`. When the game refuses, because the player is disconnected, the page stops the browser side again. Every other browser call waits at most `BrowserCallTimeout`. A start that the browser did not answer in time is followed by a stop, so a late success there does not keep capturing or playing.
- The sink counts sends in flight, drops frames while `MaximumPendingSends` are pending, and hands each frame to the circuit's dispatcher, because JS interop with byte arrays is not documented as thread-safe. Each `play` call times out after `PlayTimeout`, so a call that a lost connection never answers releases its slot. A failing send is logged at debug level.
- `VolumeTableChanged` schedules a flush, which refreshes Nearby and, through `Game.GetVoicePagesAsync`, stops the page's capture or playback when another page of the player took the microphone over or a disconnect reset the toggles. A change that arrives while a toggle runs is handled by a flush after it. The flush keeps doing this after the view broke, so a stale sink does not keep playing.
- When the browser ends the microphone track by itself, because the device was unplugged or access was revoked, `OnCaptureEnded` turns the microphone off like a tap and shows "The microphone was disconnected". While a toggle runs, it waits for that toggle to end.
- Disposing the page removes its sink, turns off only its own share and disposes the voice of its view in the browser.
- Errors appear below the top of the panel: "Microphone access was denied", "No microphone was found", "The microphone could not be started", "The microphone was disconnected", "The speaker could not be started", "Voice is not available while you are not connected", and "Tap the microphone or speaker to resume" while the browser suspended the audio context and a toggle is on. The tap itself resumes the context in the browser, and the first tap on either toggle that is on asks once more through `resume` instead of turning it off; a second tap turns it off.

Browser (`game-voice.js`, per view id like `game.js`):

| Call | Direction | Purpose |
|---|---|---|
| `initialize(viewId, dotNet, options)` | .NET to JS | Returns whether the browser offers voice (secure context, `getUserMedia`, `AudioWorkletNode`). Options: `element` (the panel), `sampleRate`, `frameSamples`, `bufferStartSamples`, `bufferMaximumSamples`, `bufferGrowthSamples`, `bufferHeadroomSamples`, `bufferShrinkDelaySeconds`, `trimWindowSeconds`, `rampSeconds` |
| `startCapture(viewId)`, `stopCapture(viewId)` | .NET to JS | Asks for the microphone with echo cancellation, noise suppression and automatic gain, and starts sending frames; returns `{ isStarted, error }` with `denied`, `not-found` or `failed` |
| `startPlayback(viewId)`, `stopPlayback(viewId)` | .NET to JS | Starts or stops the playback processor |
| `resume(viewId)` | .NET to JS | Resumes a suspended audio context where the browser allows it outside a user action |
| `play(viewId, frame)` | .NET to JS | Queues a mixed frame |
| `dispose(viewId)` | .NET to JS | Stops everything, releases the microphone and closes the audio context |
| `ReceiveMicrophoneFrame(frame)` | JS to .NET | One captured frame |
| `ToggleMicrophone()`, `ToggleAudio()` | JS to .NET | Keys M and N, sent by `game-input.js` |
| `OnAudioStateChanged(isSuspended)` | JS to .NET | The audio context was suspended or resumed |
| `OnCaptureEnded()` | JS to .NET | The microphone track ended without a stop |

- Clicks on the voice buttons (the elements with `data-game-voice` inside the panel) and the M and N keys unlock the audio synchronously inside the user's action, in capture-phase listeners: the first creates the `AudioContext`, and each one resumes it. Strict browsers such as Safari start and resume audio only there, while the page asks for it only after a round trip to the server. A resume is sent even while the context reads as running, because a suspend still in flight reads as running too.
- While nothing captures, plays or starts, the context is suspended, so an idle page does not keep the audio device busy.
- Every stop increases a generation counter of capture or playback, so a start that a stop overtook while it waited for the worklet module or the microphone gives up and releases what it got. The `onended` handler of the microphone track reports only for the current generation, and a stop clears it.
- The capture processor low-passes the microphone with two one-pole stages, resamples it to 16 kHz by linear interpolation, converts it to 16 bits and posts a frame every 320 samples. Its node is connected to the destination and outputs silence, which keeps the browser running it.
- The playback processor resamples frames to the context rate and plays from a buffer that starts at `PlaybackBufferStart`, grows by one frame up to `PlaybackBufferMaximum` when it runs dry, shrinks by one frame after `BufferShrinkDelay` of playing without running dry, and plays silence while empty. Like the talker queue it drops standing delay down to its target at the end of each `TrimWindow` when the lowest level over the window, read after each render call because frames arrive only between them, exceeded the target by more than `TrimHeadroom`. This also absorbs browsers that render audio in large bursts. The ring holds twice `PlaybackBufferMaximum`, and only a frame that does not fit drops the oldest audio on arrival.
- Starting, running dry and dropping the oldest audio each make the output jump, which clicks. The playback processor instead blends from its last output value into what follows over `PlaybackRamp` at the context rate: into silence when it runs dry, into the audio when it starts and after a drop.
- The playback processor counts underruns (running dry while playing) and trims (each drop at a window end or on arrival, however much it dropped) and posts what it counted at most every 250 ms while the counts change.
- A `stop` message ends a processor, because one whose `process` returns true keeps its node alive after the node was disconnected.
- For tests and diagnosis the panel carries `data-voice-sent-frames` and `data-voice-received-frames`, the frames sent and played, and `data-voice-underruns` and `data-voice-trimmed`, the underruns and trims of the playback processor, summed over its restarts.

## Threading

The rules of the simulation thread, the snapshots other threads read and the write interceptor are in [Simulation](#simulation) and [Facade](#facade). Beyond them:

- Property change observers, the game's own and the page's, run synchronously inside the write on the writer's thread, also on the simulation thread during a publish. They never block: the game only signals a reconcile, the page only schedules a flush.
- The volume table build runs on the thread pool and owns `GameVolumeTableBuilder`; the simulation starts a build only after it saw the previous one complete.
- The mixer's own lock guards its talker queues, microphone pages and sinks. Sinks run on the voice loop outside that lock, and `VolumeTableChanged` handlers run on the simulation thread; neither may block, so the page only hands frames to its dispatcher and schedules a flush.

## Page and browser protocol

`GamePage.razor` is the `[SubjectComponent(SubjectComponentType.Page, typeof(Game))]`. It resolves its client from `CurrentClientConnection.Connection.ClientId`, opens its view session for it, takes its player from `Game.Players` once a publish attached it, and shows "Joining the game…" until the player exists.

JS interface of `game.js`, keyed by a per-page view id because the pages of a tab share the module:

| Call | Direction | Purpose |
|---|---|---|
| `start(canvas, dotNet, options)` | .NET to JS | Probes WebGL, loads Babylon, the model and textures, returns `{ isStarted, error }`. Options: `viewId`, `pad`, `stepIntervalMs`, `baseUrl` |
| `applyUpdate(viewId, update)` | .NET to JS | Upserted entries `{ id, isSelf, payload }` and removed ids |
| `dispose(viewId)` | .NET to JS | Releases the view; a dispose during `start` makes it return not started |
| `Step(sequence, direction, turnOnly)` | JS to .NET | Calls `StepAsync`; returns the sequence, verdict, position and facing, or null without a player |
| `InvokeTargetAction(index)` | JS to .NET | Invokes the numbered action of the number key |

Voice has its own module, `game-voice.js`; see [Voice](#voice).

Flushing: a delivered update makes the page flush on its renderer: apply the update in the browser, then refresh the panel, which renders only when it changed. `VolumeTableChanged` schedules a flush after 50 ms (`FlushDelay`), which rebuilds Nearby from the new table.

Prediction and reconciliation (`game-input.js`): each step carries an increasing sequence number rather than key down and up, so a delayed message never makes a player walk further. The browser applies the step at once with `predictStep`, which mirrors `GameWorldState.TryGetStep` against the blocking objects it knows (not the rate limit). When the answer arrives, it drops the answered steps, starts again from the server's position and replays the steps still pending. A server update of the own player is taken only while no step is pending. When the own player leaves the view or stops being the own player, `reset` drops the prediction and pending steps, and a generation counter discards answers to steps sent before.

Other players, and the own player on a correction, move with a tween over one step interval and play the walk animation while it runs, which evens out network jitter. A jump of more than 6 tiles is placed without a tween.

Coordinates travel as absolute integers, which JS numbers hold exactly. `game-entities.js` subtracts an integer origin near the own player before anything reaches the GPU and rebases it when the player gets more than 2048 tiles away, so the world renders sharply near `int.MaxValue` despite 32-bit floats.

The Babylon loader in `game.js` fetches `babylon.js` and `babylonjs.loaders.min.js` once per tab and runs each wrapped in a function whose `define` parameter shadows the global one. It must stay: the app hosts an AMD loader with a global `define` (for the code editor), and Babylon's UMD build would otherwise register as an anonymous AMD module instead of setting `window.BABYLON`.

Input rules:

- Arrow keys and WASD combine into 8 directions (W plus D is north east) and repeat once per step interval while held. Shift with a direction sends one turn-only step. Number keys 1 to 9 invoke the numbered actions. M toggles the microphone and N the speaker, ignoring key repeats.
- Keys with Ctrl, Alt or Meta go to the browser. Keys and pad presses are ignored while focus is in an input field or a MudBlazor dialog is open. Losing window focus releases everything.
- The direction pad is shown for `(pointer: coarse), (max-width: 959.98px)`. `game-input.js` handles its pointer events without a round trip: a held button adds its directions to the held keys, so it repeats and combines like keys, and pointer capture keeps the press while the finger drifts. The middle button toggles turn-only mode, in which a press sends one turn. The pad blocks scrolling, text selection and the long press menu.

Rendering:

- One world unit is one tile. The camera looks at a point 3 tiles above the own player from 28 tiles above and 30 tiles south of it, follows smoothly and snaps on jumps over 20 tiles. Fog runs from 110 to 230 tiles. There are no shadow maps; a soft blob under each player gives depth.
- The ground is one 600 tile plane that follows the player in whole meters, so its grass texture and the 1 m grid drawn into the texture border stay on the tiles. The target area is marked tile by tile.
- Every object but a player is a node at the south-west corner of its footprint with one child per part of its visual: a pivot node that turns the part and an offset node below it. A box part is a box mesh with per-face UVs scaled to its size by `TextureScale`, so the texture keeps its size on every wall length; boxes share one material per texture and color. A model part instantiates the glTF of its URL, loaded once per URL, as cloned meshes that share geometry and materials (`instantiateModelsToScene` with `doNotInstantiate`). A model that fails to load shows a gray box of the part's size, and the console logs the URL once.
- A change of a part's offset or rotation glides over 300 ms (`GLIDE_MS`), easing out, which swings a door leaf; any other change of a part, or of the number of parts, rebuilds the object.
- `Wind` parts use a clone of each of their materials, cached per material, with a material plugin that moves each vertex sideways by the square of its height above its mesh's origin times `WIND_STRENGTH`, swaying at `WIND_SPEED` and gusting at `GUST_SPEED` with a phase from the mesh's world position, so neighboring trees do not sway in step. The phases wrap on the CPU, so a 32-bit float keeps them precise after hours. `Animation` plays the named animation group of a model in a loop, or once, holding its last frame, for `AnimationMode.Once`.
- Every object but a player fades to 30 % visibility (`FADED_VISIBILITY`) while it hides the own player's head, at 80 % of the player's height, from the camera. Every 100 ms (`SIGHTLINE_CHECK_MS`) the segment from the camera to the head is tested exactly against the triangles of each mesh whose bounding box it crosses; the boxes only preselect, because the bounds of a trunk with leaning branches can cross the segment where no triangle does. An object stays faded for 300 ms (`FADE_HOLD_MS`) after its last hit, so it does not blink while the camera follows a step, and its visibility glides over 200 ms (`FADE_MS`). An object is tested only after the scene rendered once since it was added, rebuilt or placed, because until then its meshes keep the bounds of the origin, next to the own player.
- A glTF material has a fixed transparency mode, in which Babylon ignores the mesh visibility for blending, so a faded mesh shows a blending copy of its material instead, created on first use and drawn with a depth pre-pass; the shared materials stay untouched, so fading one object never changes another.
- Players are instances of the glTF character, scaled to their `Height`, rotated to their facing, with the shirt tinted and a billboard label. The labels are drawn in a later rendering group, so walls never hide a name.
- The frame rate shows at the top left of the view in the element marked `data-game-fps`, which `game.js` updates every 500 ms.
- For tests and diagnosis the canvas carries `data-game-parts` (parts of the objects in view), `data-game-models` (model parts showing their model), `data-game-model-failures` (model parts showing the fallback box) and `data-game-faded` (objects faded now).

Side panel (240 px, below the canvas under the `Md` breakpoint): **Position** with the pad, **Voice** (microphone and speaker buttons, errors below the top of the panel), **Actions** (operations of the `Game`, then of every tool ordered by its name in `Tools`, disabled without a player; they come first, so they keep their place while targets come and go), **Looking at** (only while targets exist, actions numbered 1 to 9 across all targets) and **Nearby** (the own player first, then the players in its row of the volume table by title, each with a crossed-out microphone or speaker while that one is off, dimmed while the own speaker is off). The own entry has a pencil button, "Change my name", which opens `GameNameDialog` with the current name, or an empty field while the client is anonymous; Save, or Enter, renames the client, and a failure shows in the error dialog of failed actions. A new table with the same players keeps the list, because their toggles and titles reach it through the view.

## Assets and licenses

Everything is vendored under the `wwwroot/` of the library that uses it, served by `MapStaticAssets` (compressed and fingerprinted at build time) at `_content/<assembly name>/`. Each folder has a `LICENSE.md` with source and terms, which lists every file of the folder. The assets of an object type live in the `wwwroot/` of its object library, which `GameAssets.Url` turns into `_content/HomeBlaze.Game.Nature/models/...`, for example.

| Folder | Asset | License |
|---|---|---|
| `HomeBlaze.Game/wwwroot/lib/babylonjs/` | Babylon.js 9.31.0 core and glTF loader (UMD builds) | Apache 2.0 |
| `HomeBlaze.Game/wwwroot/models/` | `Casual_2.glb` from the Ultimate Modular Men pack by Quaternius, only `Idle_Neutral` and `Walk` kept | CC0 |
| `HomeBlaze.Game/wwwroot/textures/` | ambientCG Grass004 (`grass.jpg`), re-encoded | CC0 |
| `HomeBlaze.Game.Buildings/wwwroot/textures/` | ambientCG Bricks097 (`bricks.jpg`, stacked to a square), Concrete046 (`concrete.jpg`) and Planks021 (`wood.jpg`), re-encoded to 512 x 512 | CC0 |
| `HomeBlaze.Game.Nature/wwwroot/models/` | Seven trees of the low poly nature pack by flo-bit, unchanged GLB files from a pinned commit | CC0 |
| `HomeBlaze.Game.Nature/wwwroot/models/animals/` | Deer and stag by Quaternius, with their animations | CC0 |

To swap the character, replace the GLB and its license, then update `game-entities.js`: the file name in `LoadAssetContainerAsync`, `IDLE_ANIMATION`, `WALKING_ANIMATION`, `SHIRT_MATERIAL` (the material tinted with the player color) and `WALKING_SPEED_RATIO` so the feet keep pace with one tile per step. The model is scaled from its bounding box to the player's `Height` and must face north (+z) after the loader's handedness conversion.

## Testing

```
dotnet test src/HomeBlaze/HomeBlaze.Game.Tests -c Debug --filter "Category!=Integration"   # unit tests of the core
dotnet test src/HomeBlaze/HomeBlaze.Game.Buildings.Tests -c Debug                         # walls and doors
dotnet test src/HomeBlaze/HomeBlaze.Game.Nature.Tests -c Debug                            # trees and animals
dotnet test src/HomeBlaze/HomeBlaze.Game.Abstractions.Tests -c Debug                      # public API snapshot
dotnet test src/HomeBlaze/HomeBlaze.Game.Tests -c Debug --filter "Category=Integration"    # E2E tests
```

The E2E tests need Playwright's Chromium (`playwright.ps1 install chromium` from the test output folder). Logic lives in plain classes that unit tests cover; rendering is covered end to end, and bUnit is not used. Unit tests run the simulation on the test thread with a fake clock; they advance the clock and run ticks instead of waiting.

| Helper | Purpose |
|---|---|
| `GameTestContext` | A game in a `TestRoot` with a `FakeTimeProvider` and fake clients, whose ticks run on the test thread; its members are listed in the [author guide](../HomeBlaze.Game.Abstractions/README.md#testing) |
| `TestRoot` | A root subject that holds the game, and optionally an installation, and counts configuration writes; `FailsToWrite` simulates failures |
| `FakeClientDirectory` | Thread-safe clients, renames and recorded notifications; `RemoveInstallation` simulates a missing installation |
| `GameObjectSets` | Builds and reads the regions of a game from plain objects |
| `EntityFactory`, `SoundMapFactory`, `SoundIndexExtensions`, `VolumeTableFactory` | Build entities, sound maps and volume tables for the tests of the parts |
| `MovementBlocker` | An object that blocks movement but lets sound through, like a plant or a chair |
| `ProbeObject`, `HookObject`, `LifecycleObject`, `ScheduledObject` | Objects that record writes, change hooks, `OnAddedToWorld` calls and schedules |
| `VisualObject`, `WarnedVisualObject` | Objects whose visual a test sets; the second is a type of its own for the warning that is logged once per type |
| `FailingWriteInterceptor` | Fails writes of chosen properties, as a failing observer does |
| `RecordingLogger` | A thread-safe logger that records its entries |
| `RecordingViewSink`, `RecordingVoiceSink` | Sinks that record the view updates and the voice frames they get |
| `RepositoryPaths` | Finds repository files, such as the demo world and the tree models, from the test output |
| `VoiceFrames` | Builds and reads frames of 16-bit little-endian samples |

The helpers live in `HomeBlaze.Game.Tests/Testing/`; `HomeBlaze.Game.Buildings.Tests` and `HomeBlaze.Game.Nature.Tests` link the ones they use into their projects, keeping the namespace `HomeBlaze.Game.Tests.Testing`, and `HomeBlaze.Game` grants them `InternalsVisibleTo`. A public `HomeBlaze.Game.Testing` library is a follow-up.

The E2E tests use `PlaywrightFixture` from `HomeBlaze.E2E.Tests` through their own `GameE2ECollection`, because xUnit only finds collection definitions in the test assembly. They assert the side panel, not canvas pixels; headless Chromium renders WebGL in software. Every installation sees all circuits of the shared server, and circuits of closed browser contexts stay connected for minutes, so players of earlier tests may still stand in a new game. The tests therefore read where their own player joined, move the other players far aside, and assert positions relative to the join point.

The voice E2E tests launch their own Chromium with `--use-fake-ui-for-media-stream --use-fake-device-for-media-stream`, so the microphone is a test tone that needs no permission dialog; the test server at `127.0.0.1` is a secure context. They read the frame counters on the panel and prove that frames stop by waiting for the talker to send 50 more while the listener's count grows by at most `MaximumPendingSends`. Headless Chromium renders audio in callbacks of 4096 samples (about 85 ms), so the listener underruns often, but the window keeps it from trimming: the test allows at most one trim within the first 100 frames, for standing delay that a stall of the test machine can leave.

## Scale

The target is about 200 players and 100k objects on one server, with a tick well under 5 ms on average. The cost per page, movement and hearing depend on what is near a player, not on the size of the world, and coordinates cover the whole `int` range.

`HomeBlaze.Game.Benchmark` measures it on the demo world's houses and trees tiled to 100k objects, with 200 connected players and 200 view sessions; the spread rows put the players in groups of about 10 that do not see each other, the clustered rows put all of them within 120 by 120 tiles, so every page sees every step. Run it in Release, never in CI:

```
dotnet run -c Release --project src/HomeBlaze/HomeBlaze.Game.Benchmark -- --filter "*SimulationTickBenchmark*"
dotnet run -c Release --project src/HomeBlaze/HomeBlaze.Game.Benchmark -- --filter "*ObjectReloadBenchmark*"
```

Results on an i7-11700K, .NET 10, BenchmarkDotNet 0.15.5 (mean, allocated per operation):

| Benchmark | Mean | Allocated | Before regions, hearing thread and shared payloads |
|---|---:|---:|---:|
| `TickWithAllPlayersWalking` (spread) | 556 µs | 290 KB | 10.6 ms |
| `TickWithAllPlayersWalkingClustered` | 3.25 ms | 1,141 KB | 88.3 ms |
| `ViewSessionWithTenMovingPlayers` | 7.2 µs | 1.1 KB | 7.1 µs |
| `ViewSessionsWithAllPlayersMoved` (200 sessions) | 347 µs | 112 KB | 566 µs |
| `ViewSessionsWithAllPlayersMovedClustered` | 2.92 ms | 1,003 KB | 10.0 ms |
| `PublishMovedPlayers` (200 players) | 178 µs | 13 KB | 176 µs |
| `PublishObjectSetAfterOnePlace` | 38 µs | 39 KB | 25.7 ms |
| `VolumeTableBuildWithAllPlayersMoved`, on its own thread | 300 µs | 41 KB | 9.6 ms, in the tick |
| `VolumeTableBuildWithAllPlayersMovedClustered`, on its own thread | 27.5 ms | 333 KB | 77.2 ms, in the tick |
| `VolumeTableBuildAfterADoorToggled`, on its own thread | 6.4 ms | 42 KB | |
| `OccupancySnapshotWithMovedPlayers` | 36 µs | 57 KB | 37 µs |
| `ObjectReloadBenchmark.WriteObjects` (100k objects) | 12.1 s | 8.4 GB | |

The last column is the first version of the simulation, before the regions, the volume table builds on their own thread and the shared view payloads. The tick and view rows were measured after the shared view payloads, the other rows before them, which they do not touch. A tick only starts a volume table build and never waits for it, so the builds do not count toward the tick. A reload of 100k objects costs about 12 s and 8.4 GB, almost all of it in the host's per-subject reflection when the new subjects attach (`MethodPropertyInitializer`, `PropertyAttributeInitializer` in `HomeBlaze.Services`), not in the game; caching that per type is a separate change to the host.

## Known limits and follow-ups

- The door panel swing is visual only: blocking uses the door's footprint, and the swung panel can pass through walls or players.
- Fading tests only the segment from the camera to the head, so a lintel or a crown can still cover the body while the head is visible. The test uses the meshes without the sway of the wind shader, so a swaying crown fades a little early or late.
- The walking character is wider than its 2 x 2 tile footprint, so arms and legs can reach into neighboring walls.
- Last writer wins on a region: an outside writer that writes back a stale copy of a region's objects removes the objects that entered the region meanwhile, for example an animal that walked in.
- `Door.Close` checks `World.IsFree` before it writes `IsOpen`, from the state of the last tick, so a player stepping into the doorway in between ends up inside the closed door; the player can step out.
- The footprint of a tree follows its variant and scale from its south-west corner, so changing `Variant` or `Scale` on the subject page grows or shrinks it toward the north-east rather than around the trunk, and nothing checks that the new tiles are free.
- "Add door" reads the wall's geometry before the game processes the replacement, so a wall edited from outside in between is replaced by pieces of its former geometry.
- A stop that timed out while a save paused publishing can leave place and replace calls pending until the game starts again, which fails them with `GameStoppedException` and drops their objects.
- Without an installation the page keeps showing "Joining the game…" rather than saying it needs one.
- Only one character model.
- A tree blocks only the tiles of its trunk; its crown passes through walls and players.
- Remembered dialog parameters live as long as the page; a reload starts with empty fields.
- A page receives every visible object within `ViewRadius` (200 tiles in each direction) of its player, although the camera shows only about 60 tiles around it and the fog hides the rest. With many objects, either lower `ViewRadius` or let the page report the area its camera covers and send that area plus a margin.
- Every box part is its own mesh and every model part one mesh per model node, so thousands of walls and trees within the view radius mean thousands of draw calls. Thin instances of identical meshes would batch them. Fading tests every object in view each 100 ms, the bounding box of each mesh first; a spatial lookup along the sightline would keep it cheap in dense forests.
- Crowded views: with all 200 players in one spot, the view sessions take about 2.9 ms per tick, since every page compares every step it sees.
- A reload of 100k objects takes about 12 s and 8.4 GB in the host's per-subject reflection; see [Scale](#scale).
- `MaximumPendingSends` of 10 drops frames to a page once its round trip exceeds about 200 ms, because each send waits for the browser to acknowledge it.
- A stop that a connection drop loses after a toggle can leave the browser capturing while the server shows the microphone off. The mixer drops those frames, because the game no longer counts that page, and the next toggle recovers.

Follow-ups:

- Static assets of runtime-loaded plugins, a separate pull request on `master`. Loading a game extension from a NuGet package is verified once it lands.
- The 1,000+ players scale: storage other than one JSON file, and several processes.
- Saving schedules, so time-driven behavior continues where it stopped instead of starting again in `OnAddedToWorld`.
- A public `HomeBlaze.Game.Testing` library with a public `GameTestContext` that ticks on a fake clock, replacing the linked test files and the `InternalsVisibleTo` entries of the extension tests.
- Caching the host's per-subject reflection per type, which dominates a reload of a large world.
- A load test with real browsers, for example 20 to 50 headless Chromium pages walking through Playwright, to measure the Blazor circuits (SignalR updates, JS interop, renders) and the browser frame rates with many characters in view; the server benchmark covers only the simulation.
- Echo, the two-device test from #876: the browser's echo canceller works on the microphone, but the playback goes through an `AudioWorklet`, which Chrome's echo canceller may not cover, so without headphones a listener's speaker may reach its own microphone. This is not yet tested with real devices. If confirmed, route the playback through a local loopback `RTCPeerConnection` pair into an `<audio>` element. Headphones avoid it.

Voice follow-ups:

- Opus compression (Concentus on the server, WebCodecs in the browser) to cut the bandwidth about tenfold.
- Walls that muffle instead of block.
- Stereo panning by the talker's position relative to the listener.
- A speaking indicator in the 3D scene, which needs audio levels from the server.

Agent players are the planned follow-up. `GameCaller` and `Game.GetActor` already isolate who acts, so operations keep their signatures. It needs:

1. `HomeBlaze.AI` to expose the calling agent to operations generically, for example as a `[FromServices]` type defined there. `McpMethodInvoker` currently invokes with the global service provider.
2. Players bound to an agent subject instead of a client, always persisted and visible while the agent runs.
3. Perception and movement for agents, such as a `LookAround(radius)` query and a `MoveAsync(direction, steps)` operation.
4. Triggers for agent turns: being looked at, receiving a message, or a timer.
