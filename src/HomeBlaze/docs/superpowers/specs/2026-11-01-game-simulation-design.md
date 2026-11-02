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
  - `bool IsFree(GameArea area, GameObject? except = null)`: whether no movement blocking object other than `except` overlaps the area, callable from any thread: on the simulation thread it reads the live index, including the writes of earlier schedules in the same tick, elsewhere the snapshot of the last tick.
  - `Task PlaceAsync(GameObject gameObject, CancellationToken cancellationToken)`: adds the object under a name of its type name in lowercase, a dash and 8 random hex digits; refused with "Something is in the way." when a movement blocking object overlaps a movement blocking object.
  - `Task RemoveAsync(GameObject gameObject, CancellationToken cancellationToken)`.
  - `Task ReplaceAsync(GameObject gameObject, IReadOnlyList<GameObject> replacements, CancellationToken cancellationToken)`: atomic; refused when a movement blocking replacement overlaps a movement blocking object other than the replaced one or another replacement, or a replacement is listed twice.
  - `IDisposable Schedule(GameObject gameObject, TimeSpan delay, Action action)`: runs the action on the simulation thread at the first tick at or after the delay; disposing cancels it; removing the object cancels all its schedules; not saved.
- Values: `GameCaller` (the client id of the page that invokes an operation), `GameMessages` (the shared refusal texts, such as `Blocked`), `GameArea` (X, Y, Width, Depth, `Intersects`), `GameSpot` (X, Y, Width, Depth, Facing, `Area`), `GameDirection` with its offset helpers, `GameVisual`, `GameVisualPart`, `GameVisualShape`, `GameVisualEffect`, `GameVisualAnimationMode` (`Loop`, `Once`), `GameVisualVector`, `GameVisualPoint`, `GameVisualCache<TKey>`, `GameAssets.Url(Type, string)`.
- `GameVisualPart.AnimationMode`: `Loop` (default) plays `Animation` in a loop; `Once` plays it once whenever the part's `Animation` changes and holds the last frame.
- The object API methods throw `InvalidOperationException("The object is not part of this game.")` for objects of another game, and `ArgumentException` for invalid sizes (1 to `GameObject.MaximumSize`) or objects already in the game, as today. Canceling a world command before its tick drops it; after that the change stays applied.

`GameObject` and `GameTool` stay abstract classes because `GameRegion.Objects` and `Game.Tools` load polymorphically through `$type`, which the loader supports for abstract `IConfigurable` classes only, and because `X`/`Y` are shared persisted properties.

## Author rules

1. Describe the object with properties: `[Configuration]` and `[State]` properties plus overrides of `Visual`, the size and the blocking flags. The game reads only these.
2. Change the object by writing its properties, from operations, the subject browser, MCP or anywhere else. A write lands right away and is never refused by the game; the simulation reads the object again at the next tick. Where a rule matters, the author checks it with `World.IsFree` and throws `InvalidOperationException("Something is in the way.")`.
3. Everything beyond the object's own properties goes through `World`: `GetSpotInFront`, `IsFree`, `PlaceAsync`, `RemoveAsync`, `ReplaceAsync`, `Schedule`.
4. Time-driven behavior is set up in `OnAddedToWorld`, which the simulation calls on its thread when the object enters the world: on start, load, reload, place and replace.

A player inside a movement blocking object, for example a door closed by an automation while the player stood in it, can always step out: a step is only checked against blocking objects that do not already overlap the player's current area.

## Simulation (`HomeBlaze.Game/Simulation/`)

- `GameSimulation` is the only writer of the world state. It runs on a fixed tick, by default 50 ms, driven by the game's `TimeProvider`. A tick runs synchronously without awaiting; the thread running a tick is the simulation thread for that tick (a thread-static marker), so tests run ticks on the test thread through a test host with `AdvanceTicks(n)`.
- Everything else reaches it through a lock-free command queue (`Channel`). Commands that answer carry a `TaskCompletionSource` (`RunContinuationsAsynchronously`) completed at the end of their tick, after the tick handlers, so a caller sees what the tick published; a place or replace from another thread completes only once the object set with its objects is written, which a save in progress delays. A command that throws fails only its own task; a throwing scheduled action or `OnAddedToWorld` is logged, throttled, and the tick goes on.
- State, touched only on the simulation thread:
  - players by client id: area, facing, connected, title, rate limiters, last message time, voice pages;
  - objects by name: the subject and its generic entry (area, movement blocking, sound blocking, visible, visual), read from the subject when a command changed it;
  - the chunk index (`GameWorldIndex`, chunks of 16 by 16 tiles) and the chunks changed in this tick;
  - schedules in a priority queue by due tick.
- Each tick, in order:
  1. Run the due schedules, then the queued commands: steps and turns, world commands, object changes, take-overs, teleports, voice switches, client changes (then reconcile players), view sessions joining and leaving. Then write the changed regions and region set (see Facade) right away, so placed objects are attached to the game when their command completes, and call `OnAddedToWorld` for the placed objects and replacements it attached.
  2. Publish the occupancy snapshot for `IsFree`: immutable chunks, only changed chunks replaced, swapped in with a volatile write.
  3. Update the view sessions (see Views).
  4. Take the chunks whose sound blocking changed into the sound snapshot and publish the table of a finished volume table build. Then, when no build runs and 100 ms passed since the last one started, start the next build on the thread pool if the hearing players' positions or the sound blocked tiles changed; a voice switch alone starts none. Last, raise `VolumeTableChanged` when a table was published or a voice switch changed. The tick never waits for a build.
  5. Publish the players' state to the subjects when the publish interval (250 ms) passed.
  6. Start a save when the save interval (10 s) passed and something changed.
- `Step` keeps the browser protocol: the page awaits the `MoveResult` of its command, at most one tick later than today; the browser's prediction hides that. Steps and turns keep the token buckets (`StepInterval` 180 ms, burst 3).
- `GameSimulationSettings` (code-configurable, like `GameVoiceSettings`) holds the tick, publish, save and volume table intervals.

## Facade

- The objects live in regions: `Game.Regions` is a `[Configuration] Dictionary<string, GameRegion>`, and `GameRegion` is a core subject (`IConfigurable`, not polymorphic) with `[Configuration] Dictionary<string, GameObject> Objects`. A region covers 64 by 64 tiles (16 m) and its key is its coordinates, `"{regionX}_{regionY}"` with regionX = floor(X / 64), negative coordinates included. An object belongs to the region of its south-west corner; names are unique across all regions. `GameObject.World` resolves through the region to the game.
- Only changed regions are written: a place or remove writes the dictionary of one region, so its cost grows with the objects of that region, not of the world. Adding a region or emptying one also writes `Game.Regions`, which is small; empty regions are removed.
- An object whose position crosses a region boundary, such as a walking animal or a teleport through `X`/`Y`, migrates in one publish and never leaves the graph in between, so it keeps its identity and schedules, `OnAddedToWorld` does not run again and no outside write to it is missed. The publish writes the regions that gain objects first, still holding the objects that left them, then the region set with the new regions, then the regions without the objects that left them, and last the region set without the emptied regions. A migration is published in the first tick handler after the simulation read the move, at most one tick later.
- A place or replace from another thread completes once a publish wrote every region change.
- A write interceptor, registered once per context with `TryAddService`, sees writes after they landed:
  - to a property of a `GameObject` of a running game: mark the object changed, directly on the simulation thread (a schedule or `OnAddedToWorld` writing any object) and through the queue from other threads; the simulation reads every changed object again before the views update. The publish's own writes do not mark anything;
  - to `Game.Players` or `Game.Regions`: queue a take-over carrying the new value, so a publish in between cannot overwrite it; the simulation takes over the world without overlap checks and calls `OnAddedToWorld` for new objects and cancels the schedules of removed ones. A take-over of the regions moves an object listed in another region than that of its position into its region, removes regions listed under a key that names no region, gives an object a new name when an earlier region listed its name, and writes the region set again;
  - to `GameRegion.Objects`: queue a take-over of that region carrying the new value: objects of the region it leaves out leave the world, new ones enter it (under a new name when another object has theirs), kept ones are read again, and the next publish writes the region again;
  - to `GamePlayer.X`, `Y` or `Facing` from outside the simulation thread: queue a teleport or turn carrying the value; this rule comes before the general object rule.
- Writes to `GamePlayer.ClientId`, `ClientTitle`, `IsConnected`, `IsMicrophoneOn` and `IsAudioOn` from outside the simulation are refused before they land (`InvalidOperationException`), also through the registry; the setters stay internal.
- The interceptor never waits for the simulation, so a write from a synchronous change observer cannot deadlock with the simulation's own writes.
- Publishing writes, from the simulation thread, the latest values of the players changed since the last publish (`X`, `Y`, `Facing`, `IsConnected`, `ClientTitle`, `IsMicrophoneOn`, `IsAudioOn`), `Game.Players` when the set changed, and the changed regions and `Game.Regions` as above. Object properties are not published; their writers set them. The interceptor ignores exactly the publisher's own writes, by subject, property and written instance.

## Persistence

- The file holds the objects by region: `"regions": { "0_0": { "objects": { ... } } }`. A file that lists an object in another region than that of its position loads; the start moves the object into its region.
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

- The voice mixer keeps its own 20 ms loop and its own lock; the loop runs only while a player has the microphone or the speaker on and pauses otherwise, which changes nothing anybody hears, since nothing is mixed or sent then; after a pause every talker glides in from silence, as after a gap. frames go from the page to the mixer as today, never through the simulation.
- The volume table is built on its own thread from immutable inputs, one build at a time: a `SoundSnapshot` of the sound blocked tiles per chunk (immutable chunks of 16 by 16 bits, only the chunks whose sound blocking changed replaced, swapped in with a volatile write) and a copy of the client ids and areas of the connected players. The snapshot carries the index's `SoundVersion`, which only a change of a sound blocking object increases (added, removed, moved, resized or its `IsSoundBlocking` toggled), never a player's step. The tick publishes a finished table with a volatile write and then raises `VolumeTableChanged` on the simulation thread. A table is published in the first tick after its build completes. A change that arrives while a build runs waits for it, so the extra latency is one tick plus up to two build durations. A voice switch alone changes no row, so it raises `VolumeTableChanged` without a build. The mixer and Nearby read the table without a lock.
- The build owns its caches, which only a new sound version drops: the regions of connected open tiles, labeled by a flood fill that counts a region of more tiles than the search grid of a player as open, so two players in different regions, at least one of them closed, are silent to each other without a search; and the search lengths per source footprint, bounded in number. `HearingRange` and `SoundPathSearch` read the snapshot through these caches, with results identical to a search on the chunk index.
- A failing build is logged, warning once per streak, and built again after the interval. A stop drops the table of a running build, and a restart starts from an empty table and builds afresh once that build completed.
- Microphone and speaker switches are commands. A leaving or disconnecting player is released in the mixer by the simulation; the lock order between game and mixer disappears.
- Client changes are commands; the next tick reconciles the players. `RenameAsync` names the client in the installation as today and queues a reconcile.
- Messages keep their throttle of one per second per sender, kept in the simulation's player state.
- `GamePlayer` keeps only its facade properties and the "Send message" operation; `StepRateLimiter`, `TurnRateLimiter`, `LastMessageTimestamp`, `MicrophonePageId` and `AudioPageIds` move into the simulation.

## Objects

- Wall, Door, WallTool, WallMaterial move to `HomeBlaze.Game.Buildings`; Tree, TreeTool, TreeVariant to `HomeBlaze.Game.Nature`. Their behavior stays, rewritten on the new API: Demolish uses `RemoveAsync`; Open sets `IsOpen`; Close checks `IsFree` first; `AddDoor` replaces the wall by wall, door and wall through `ReplaceAsync`, centering the door on `GetSpotInFront(caller, doorWidth, 1)` when there is a caller and on the middle of the wall without one (MCP, agents), clamped so that a wall tile stays on each side.
- Door gets `[Configuration] TimeSpan AutoCloseDelay` (zero turns it off): opening schedules the close; `OnAddedToWorld` schedules it for a door that is open after a restart; a close that `IsFree` refuses is scheduled again after the delay.
- New `Animal` in Nature, the sample of the API: Quaternius' CC0 deer and stag with built-in animations (`LICENSE.md` next to them). After a pause of 3 to 8 seconds (random) it walks a bout of 4 to 12 tiles, one tile every 250 ms, by writing `X`/`Y` while `IsFree` allows the step and the step stays within `WanderRadius` (configuration, 24 tiles by default, zero for unlimited) of its home (`HomeX`/`HomeY`, configuration, set to where it stands on the first `OnAddedToWorld`); a blocked step ends the bout. A bout keeps the facing half of the time, turns by 45 degrees 36 percent and by 90 degrees 14 percent of the time, tries the smallest turns first when that direction is blocked, and turns by 90 degrees in place when only the way back is free, so it never turns around at once. It loops `Walk` during a bout (`IsWalking`, state) and `Idle` otherwise, and plays `Eating` once on its "Feed" operation, which also ends a bout. `OnAddedToWorld` starts the pauses.
- The demo world is regenerated with the new `$type` names and a few animals in the forest, each with its home where it was placed.

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

Both libraries are unreleased: the objects of a game move from `Game.Objects` into `Game.Regions` (the file format changes from `"objects"` to `"regions"`), the `$type` names change to `HomeBlaze.Game.Buildings.*` and `HomeBlaze.Game.Nature.*`, asset paths move with the libraries, the demo world is regenerated, and object types use `World` instead of `GetGame()` and the new object API.
