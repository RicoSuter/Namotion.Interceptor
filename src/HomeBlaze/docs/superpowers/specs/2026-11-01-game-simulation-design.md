# Game simulation redesign

Working spec for the pull request on top of #876 (branch `feat/homeblaze-game-simulation`). It moves into the READMEs before the pull request is ready and is deleted then.

## Goals

- The simulation owns the world state; subjects are its facade. Movement, collisions, sound, views and voice volumes run on plain state on one simulation thread.
- No game lock, no detection of outside changes after the fact, no copy-on-write races.
- Scale target on one server: about 200 players and 100k objects, one tick under 5 ms on average, proven by a benchmark.
- Game object types are extensions that reference only `HomeBlaze.Game.Abstractions`, so other developers can ship them as NuGet plugins. The author API is small, with exactly one way to do each thing.
- General tidying of the game code and removal of everything the redesign makes obsolete.

Out of scope: storage other than one JSON file and several processes (the 1,000+ players scale), static assets of runtime-loaded plugins (a separate pull request on `master`), saving schedules.

## Projects

| Project | Contents | References |
|---|---|---|
| `HomeBlaze.Game.Abstractions` | The extension contract | HomeBlaze.Abstractions, Namotion.Interceptor (+ generator), Namotion.Interceptor.Tracking |
| `HomeBlaze.Game` | `Game`, simulation, players, views, voice, page, renderer | Game.Abstractions and what it references today |
| `HomeBlaze.Game.Buildings` | Wall, Door, WallTool, WallMaterial, wall textures | Game.Abstractions and HomeBlaze.Abstractions only |
| `HomeBlaze.Game.Nature` | Tree, TreeTool, TreeVariant, Animal, AnimalVariant, tree and animal models | Game.Abstractions and HomeBlaze.Abstractions only |
| `HomeBlaze.Game.Benchmark` | BenchmarkDotNet benchmarks of the simulation | HomeBlaze.Game, Buildings, Nature |

- Buildings and Nature are Razor class libraries for their `wwwroot` assets only, served at `_content/HomeBlaze.Game.Buildings/...` and `_content/HomeBlaze.Game.Nature/...`; each asset folder keeps its `LICENSE.md`.
- The host references all of them and registers each assembly with `typeProvider.AddAssembly(...)`.
- `HomeBlaze.Game.Abstractions` is a host assembly like `HomeBlaze.Abstractions`, so the plugin loader shares it with plugins and they bind to the same types. Its public API is snapshot-tested with `VerifyChecksTests.PublicApi`.
- Test projects: `HomeBlaze.Game.Tests` (core, demo world, E2E), `HomeBlaze.Game.Buildings.Tests`, `HomeBlaze.Game.Nature.Tests`, `HomeBlaze.Game.Abstractions.Tests` (public API snapshot). Extension tests also reference `HomeBlaze.Game` to run against a real game.

## Extension contract (`HomeBlaze.Game.Abstractions`)

Namespace `HomeBlaze.Game.Abstractions`.

- `GameObject`, abstract `[InterceptorSubject]` class implementing `IConfigurable`:
  - `[Configuration] int X`, `[Configuration] int Y`;
  - virtual `Width`, `Depth`, `Height` (1, not intercepted; types with a configurable size override them with `[Configuration]` as Wall and Door do today), `IsMovementBlocking` (true), `IsSoundBlocking` (false), `IsVisible` (true), `Visual` (gray box of the size);
  - `GameArea Area => new(X, Y, Width, Depth)`;
  - `protected IGameWorld World`, which throws `InvalidOperationException` while the object is not part of a game;
  - `protected virtual void OnAddedToWorld()`, empty by default, which the game reaches through the explicitly implemented `IGameObjectLifecycle`.
- `GameTool`, abstract `[InterceptorSubject]` class implementing `IConfigurable`, with `protected IGameWorld World`.
- `IGameWorld`:
  - `GameSpot GetSpotInFront(GameCaller? caller, int across, int along)`: the area right in front of the caller's player, centered across its facing direction (odd remainder east or north), with the facing; throws `InvalidOperationException` with "Only a player in the game can place objects." or "Face north, east, south or west to place objects.". It does not check whether the area is free.
  - `bool IsFree(GameArea area, GameObject? except = null)`: whether no movement blocking object other than `except` overlaps the area, callable from any thread: on the simulation thread it reads the live index, elsewhere the snapshot of the last tick.
  - `Task PlaceAsync(GameObject gameObject, CancellationToken cancellationToken)`: adds the object under a name of its type name in lowercase, a dash and 8 random hex digits; refused with "Something is in the way." when a movement blocking object overlaps a movement blocking object.
  - `Task RemoveAsync(GameObject gameObject, CancellationToken cancellationToken)`.
  - `Task ReplaceAsync(GameObject gameObject, IReadOnlyList<GameObject> replacements, CancellationToken cancellationToken)`: atomic; refused when a movement blocking replacement overlaps a movement blocking object other than the replaced one or another replacement, or a replacement is listed twice.
  - `IDisposable Schedule(GameObject gameObject, TimeSpan delay, Action action)`: runs the action on the simulation thread at the first tick at or after the delay; disposing cancels it; removing the object cancels all its schedules; not saved.
- Values: `GameCaller` (the client id of the page that invokes an operation), `GameMessages` (the shared refusal texts, such as `Blocked`), `GameArea` (X, Y, Width, Depth, `Intersects`), `GameSpot` (X, Y, Width, Depth, Facing, `Area`), `GameDirection` with its offset helpers, `GameVisual`, `GameVisualPart`, `GameVisualShape`, `GameVisualEffect`, `GameVisualAnimationMode` (`Loop`, `Once`), `GameVisualVector`, `GameVisualPoint`, `GameVisualCache<TKey>`, `GameAssets.Url(Type, string)`.
- `GameVisualPart.AnimationMode`: `Loop` (default) plays `Animation` in a loop; `Once` plays it once whenever the part's `Animation` changes and holds the last frame.
- The object API methods throw `InvalidOperationException("The object is not part of this game.")` for objects of another game, and `ArgumentException` for invalid sizes (1 to `GameObject.MaximumSize`) or objects already in the game, as today. Canceling a world command before its tick drops it; after that the change stays applied.

`GameObject` and `GameTool` stay abstract classes because `Game.Objects` and `Game.Tools` load polymorphically through `$type`, which the loader supports for abstract `IConfigurable` classes only, and because `X`/`Y` are shared persisted properties.

## Author rules

1. Describe the object with properties: `[Configuration]` and `[State]` properties plus overrides of `Visual`, the size and the blocking flags. The game reads only these.
2. Change the object by writing its properties, from operations, the subject browser, MCP or anywhere else. A write lands right away and is never refused by the game; the simulation reads the object again at the next tick. Where a rule matters, the author checks it with `World.IsFree` and throws `InvalidOperationException("Something is in the way.")`.
3. Everything beyond the object's own properties goes through `World`: `GetSpotInFront`, `IsFree`, `PlaceAsync`, `RemoveAsync`, `ReplaceAsync`, `Schedule`.
4. Time-driven behavior is set up in `OnAddedToWorld`, which the simulation calls on its thread when the object enters the world: on start, load, reload, place and replace.

A player inside a movement blocking object, for example a door closed by an automation while the player stood in it, can always step out: a step is only checked against blocking objects that do not already overlap the player's current area.

## Simulation (`HomeBlaze.Game/Simulation/`)

- `GameSimulation` is the only writer of the world state. It runs on a fixed tick, by default 50 ms, driven by the game's `TimeProvider`. A tick runs synchronously without awaiting; the thread running a tick is the simulation thread for that tick (a thread-static marker), so tests run ticks on the test thread through a test host with `AdvanceTicks(n)`.
- Everything else reaches it through a lock-free command queue (`Channel`). Commands that answer carry a `TaskCompletionSource` (`RunContinuationsAsynchronously`) completed at the end of their tick, after the tick handlers, so a caller sees what the tick published. A command that throws fails only its own task; a throwing scheduled action or `OnAddedToWorld` is logged, throttled, and the tick goes on.
- State, touched only on the simulation thread:
  - players by client id: area, facing, connected, title, rate limiters, last message time, voice pages;
  - objects by name: the subject and its generic entry (area, movement blocking, sound blocking, visible, visual), read from the subject when a command changed it;
  - the chunk index (`GameWorldIndex`, chunks of 16 by 16 tiles) and the chunks changed in this tick;
  - schedules in a priority queue by due tick.
- Each tick, in order:
  1. Run the due schedules, then the queued commands: steps and turns, world commands, object changes, take-overs, teleports, voice switches, client changes (then reconcile players), view sessions joining and leaving. Then write a changed object set to `Game.Objects` right away, so placed objects are attached to the game when their command completes, and call `OnAddedToWorld` for the placed objects and replacements it attached.
  2. Publish the occupancy snapshot for `IsFree`: immutable chunks, only changed chunks replaced, swapped in with a volatile write.
  3. Update the view sessions (see Views).
  4. Rebuild the volume table at most every 100 ms when positions or sound blocking changed.
  5. Publish the players' state to the subjects when the publish interval (250 ms) passed.
  6. Start a save when the save interval (10 s) passed and something changed.
- `Step` keeps the browser protocol: the page awaits the `MoveResult` of its command, at most one tick later than today; the browser's prediction hides that. Steps and turns keep the token buckets (`StepInterval` 180 ms, burst 3).
- `GameSimulationSettings` (code-configurable, like `GameVoiceSettings`) holds the tick, publish, save and volume table intervals.

## Facade

- A write interceptor, registered once per context with `TryAddService`, sees writes after they landed:
  - to a property of a `GameObject` of a running game: mark the object changed, directly on the simulation thread (a schedule or `OnAddedToWorld` writing any object) and through the queue from other threads; the simulation reads every changed object again before the views update. The publish's own writes do not mark anything;
  - to `Game.Players` or `Game.Objects`: queue a take-over carrying the new value, so a publish in between cannot overwrite it; the simulation takes over the world without overlap checks and calls `OnAddedToWorld` for new objects and cancels the schedules of removed ones;
  - to `GamePlayer.X`, `Y` or `Facing` from outside the simulation thread: queue a teleport or turn carrying the value; this rule comes before the general object rule.
- Writes to `GamePlayer.ClientId`, `ClientTitle`, `IsConnected`, `IsMicrophoneOn` and `IsAudioOn` from outside the simulation are refused before they land (`InvalidOperationException`), also through the registry; the setters stay internal.
- The interceptor never waits for the simulation, so a write from a synchronous change observer cannot deadlock with the simulation's own writes.
- Publishing writes, from the simulation thread, the latest values of the players changed since the last publish (`X`, `Y`, `Facing`, `IsConnected`, `ClientTitle`, `IsMicrophoneOn`, `IsAudioOn`) and `Game.Players`/`Game.Objects` when their set changed. Object properties are not published; their writers set them.

## Persistence

- Saves at most every save interval when something changed, and on shutdown: publish, pause publishing, write the configuration through the storage as today, resume. A failed or canceled write stays pending for the next attempt. The file always holds one consistent publish.
- A crash loses at most one save interval of positions.
- Schedules are not saved; `OnAddedToWorld` sets them up again after a restart.

## Views

- The page registers a view session with the simulation: its player, a sink and its page id. The page no longer reads the game or its subjects for the view, and no longer subscribes to all property changes.
- A session sees the chunks within `ViewRadius` (200 tiles) of its player. Each tick it diffs only the changed chunks inside its window, plus the edge strips that enter or leave the window when its player crosses a chunk boundary. `GameViewTracker` stays as the diff state (upserts, removals, stable ids, visual validation with a warning once per type).
- Backpressure: while the previous update of a session has not reached the browser, the simulation skips the session and keeps collecting its changed chunks; the next update carries everything since the last one.
- Panel data comes from the session too: the position text, the objects in front (recomputed when the player or the chunks in front of it changed) and Nearby (from the volume table). Operations of the targets run through the usual operation flow on the subjects.
- The browser protocol and `game-entities.js` stay as they are, plus support for `AnimationMode.Once`.

## Voice and players

- The voice mixer keeps its own 20 ms loop and its own lock; frames go from the page to the mixer as today, never through the simulation.
- The simulation builds the volume table on its thread and publishes it as today; the mixer and Nearby read it without a lock. `HearingRange` and `SoundPathSearch` read the simulation's chunk index.
- Microphone and speaker switches are commands. A leaving or disconnecting player is released in the mixer by the simulation; the lock order between game and mixer disappears.
- Client changes are commands; the next tick reconciles the players. `RenameAsync` names the client in the installation as today and queues a reconcile.
- Messages keep their throttle of one per second per sender, kept in the simulation's player state.
- `GamePlayer` keeps only its facade properties and the "Send message" operation; `StepRateLimiter`, `TurnRateLimiter`, `LastMessageTimestamp`, `MicrophonePageId` and `AudioPageIds` move into the simulation.

## Objects

- Wall, Door, WallTool, WallMaterial move to `HomeBlaze.Game.Buildings`; Tree, TreeTool, TreeVariant to `HomeBlaze.Game.Nature`. Their behavior stays, rewritten on the new API: Demolish uses `RemoveAsync`; Open sets `IsOpen`; Close checks `IsFree` first; `AddDoor` replaces the wall by wall, door and wall through `ReplaceAsync`, centering the door on `GetSpotInFront(caller, doorWidth, 1)` when there is a caller and on the middle of the wall without one (MCP, agents), clamped so that a wall tile stays on each side.
- Door gets `[Configuration] TimeSpan AutoCloseDelay` (zero turns it off): opening schedules the close; `OnAddedToWorld` schedules it for a door that is open after a restart; a close that `IsFree` refuses is scheduled again after the delay.
- New `Animal` in Nature, the sample of the API: a CC0 model with built-in animations (selection and license checked during implementation, `LICENSE.md` next to it). It plays `Idle` and `Walk` in a loop, plays `Eat` once on its "Feed" operation, and every 2 to 6 seconds (random) steps one tile in a random direction by writing `X`/`Y` when `IsFree` allows it; its rotation follows the walking direction. `OnAddedToWorld` starts the stepping.
- The demo world is regenerated with the new `$type` names and a few animals in the forest.

## Page tidying

- `GamePage.razor` splits into the page (markup and a code-behind for layout and panel), `GameViewConnection` (the page's side of its view session and the update queue) and `GameVoiceConnection` (the voice wiring); `GameViewSession` is the simulation's side.
- Folders by responsibility: `Simulation/`, `View/`, `Voice/`, `World/`, `Components/`.

## Removed

`Game._lock`, `_pendingPlayers`, `_pendingObjects`, `CurrentPlayers`, `CurrentObjects`, `ReplacePlayers`, `ReplaceObjects`, the stale index flag and `RequestIndexRebuild`, `OnPropertyChanged` with `IsOutsideChange`, `GameObjectSnapshot`, `Game.UpdateAsync`, `ResolveActor`, `namePrefix`, `GamePlacement` (now `GameSpot`), `HasPendingSave`, `GameObject.GetGame()`, `GameTool.GetGame()`, the index requests in `ApplyConfigurationAsync`, the page's subscription to all property changes.

## Documentation

- `HomeBlaze.Game.Abstractions/README.md`: the author guide. Rules 1 to 4, a complete door and tool, visuals and animations (Loop, Once, Wind), assets and licenses, packaging as a plugin (Razor class library, reference only the abstractions, register the assembly), testing an object against a real game.
- `HomeBlaze.Game/README.md`: the internals (simulation, tick order, facade, views, voice, persistence, known limits, scale) and a link to the author guide instead of repeating it.
- Follow-ups in the game README: static assets of runtime-loaded plugins (separate pull request on `master`; loading a game extension from a NuGet package is verified once it lands), the 1,000+ players scale (storage other than JSON, several processes), saving schedules, the two-device echo test from #876.

## Testing

- `HomeBlaze.Game.Tests` runs the simulation with a fake clock and the test host; no hardcoded waits.
- New tests: command order within a tick; outside writes and take-overs racing a publish; refusal of writes to simulation-owned player properties; `IsFree` from another thread; stepping out of a blocking object; `Schedule` and its cancellation on removal and dispose; `OnAddedToWorld` on start, load, place and replace; changed chunks and edge strips in the view sessions; backpressure; publish and save intervals and paused publishing while saving; loading an overlapping world; a throwing command or schedule not stopping the tick.
- The existing unit tests are adapted with unchanged behavior; Buildings and Nature tests cover Wall, Door with `AutoCloseDelay`, Tree and Animal against a real game.
- The 20 E2E tests stay; one more checks that an animal's model and animation load.
- `HomeBlaze.Game.Abstractions.Tests` snapshots the public API.

## Benchmark

`HomeBlaze.Game.Benchmark` (BenchmarkDotNet, not in CI): one tick with 200 walking players, 100k objects (the demo houses and trees tiled) and 200 view sessions, target under 5 ms on average; publishing 200 moved players; one view session diff with 10 other players in view. The numbers go into the pull request.

## Breaking changes

Both libraries are unreleased: the `$type` names change to `HomeBlaze.Game.Buildings.*` and `HomeBlaze.Game.Nature.*`, asset paths move with the libraries, the demo world is regenerated, and object types use `World` instead of `GetGame()` and the new object API.
