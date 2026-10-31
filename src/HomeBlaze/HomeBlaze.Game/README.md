# HomeBlaze.Game

Maintainer documentation for the game library: how it is built, which invariants hold it together and what is still open.

## Overview

A `Game` subject is a multiplayer world that HomeBlaze shows as a page. Every browser client of the installation is a player on an infinite grid of 25 cm tiles. Players walk in 8 directions, look at the tiles in front of them and invoke the operations of the objects standing there, such as building and demolishing walls, opening doors and sending messages. The page renders the world with Babylon.js as a tilted perspective scene centered on the own player. Several `Game` subjects may exist; each is an independent world with its own players.

| Project | Contents |
|---|---|
| `HomeBlaze.Game` (Razor SDK) | Subjects, the page component, the JS modules and the vendored assets under `wwwroot/` |
| `HomeBlaze.Game.Tests` | Unit tests and E2E tests |

The app registers the library with a project reference in `HomeBlaze.csproj` and `.AddAssembly(typeof(HomeBlaze.Game.Game).Assembly)` in `Program.cs`. `Data/Files/Demo/Game.json` of the app holds a demo world with a house of walls and door openings, served at `/pages/Demo/Game`.

The library depends on `HomeBlaze.Abstractions`, `HomeBlaze.Components` (`OperationInvoker`), `HomeBlaze.Services` (`CurrentClientConnection`, the configuration serializer), `HomeBlaze.Installation` (clients), `HomeBlaze.Storage.Abstractions` (`IConfigurationWriter`) and the Namotion.Interceptor generator, tracking and registry libraries. Dependency rule: `HomeBlaze.Game` may reference `HomeBlaze.AI` for agent players; `HomeBlaze.AI` never references or mentions the game.

| Folder | Contents |
|---|---|
| `Game*.cs` | The `Game` subject, split by concern: `Game.cs` (properties, copy-on-write helpers), `Game.Runtime.cs` (loop, change observer), `Game.Index.cs`, `Game.Players.cs`, `Game.Movement.cs`, `Game.Actions.cs`, `Game.Persistence.cs`, `Game.View.cs` |
| `GameObject.cs`, `Wall.cs`, `Door.cs`, `GamePlayer.cs` | The objects on the grid |
| `Clients/` | `IGameClientDirectory` and its installation implementation |
| `World/` | Pure grid logic: `Footprint`, `GameWorldIndex`, `SpawnFinder`, `StepRateLimiter`, `TargetArea`, `WallPlacement` |
| `View/` | `GameViewTracker` and the objects and updates sent to the browser |
| `Components/` | `GamePage.razor` and `GameCallerServiceProvider` |
| `wwwroot/` | `game.js` (lifecycle, Babylon loader, camera), `game-input.js` (keyboard, pad, prediction), `game-entities.js` (meshes, tweens, ground, target marker), `lib/`, `models/`, `textures/` |

## Model

North is +Y and east is +X. An object covers the tiles from `X` to `X + Width - 1` and from `Y` to `Y + Depth - 1`.

| Subject | Property | Kind | Notes |
|---|---|---|---|
| `Game` | `Name` | Configuration | Title falls back to "Game" |
| | `Players` | Configuration | `Dictionary<string, GamePlayer>` keyed by client id, managed by the game |
| | `Objects` | Configuration | `Dictionary<string, GameObject>` keyed by object name, managed by the user and by player actions |
| `GameObject` (abstract) | `X`, `Y` | Configuration | South-west corner of the footprint |
| | `Width`, `Depth`, `Height` | Configuration, `[Range(1, 1024)]` | In tiles; `Height` is only used for rendering |
| | `IsMovementBlocking`, `IsVisible` | Virtual, not persisted | `true` by default |
| `Wall` | | | Defaults 4 x 1 x 10 tiles (1 m long, 25 cm thick, 2.5 m tall) |
| `Door` | `IsOpen` | Configuration | Defaults 4 x 1 x 10, closed; use 1 x 4 in a north-south wall. `IsMovementBlocking => !IsOpen` |
| `GamePlayer` | `Facing` | Configuration | `GameDirection`, north by default |
| | `ClientId`, `ClientTitle`, `IsConnected` | State | `ClientId` is set from the dictionary key in `OnPlayersChanging` |
| | | | Defaults 2 x 2 x 7 (50 x 50 cm, 175 cm tall). `IsMovementBlocking` and `IsVisible` follow `IsConnected` |

`GamePlayer` derives from `GameObject`, so one index covers players and objects and the renderer treats both alike. They still live in two collections because different parties manage them. The player's footprint is square, so turning never changes the tiles it covers. `GamePlayer` is `[ExcludeFromCreation]`.

`Objects` is typed as the abstract `GameObject`, which relies on `ConfigurationJsonTypeInfoResolver` writing and reading `$type` for properties declared as an abstract `IConfigurable` class.

Persistence:

- The game writes its configuration through the first parent `IConfigurationWriter`. Without one it logs a warning and does not retry.
- Building, demolishing, opening and closing write immediately, outside the lock.
- Joins, moves and turns only set `_isSaveRequested`; the next loop pass writes them in one go. `StopAsync` writes what is still pending and logs a warning when it is canceled.
- A write that fails or returns false logs a warning and sets the request again, so the next pass retries; the game keeps running.
- Anonymous players are written like any other but dropped by the first reconcile after a load, because their client no longer exists.

## Clients and players

The game reads clients through the internal `IGameClientDirectory` (list clients with id, title, named and connected; send a notification; recognize property changes that concern clients). `InstallationClientDirectory` finds the `[SingleInstance]` `HomeBlazeInstallation` in the registry and caches it while the registry still knows it. Unit tests use `FakeClientDirectory` because the installation's own client reconciling is internal to it; the E2E tests cover the real one.

`ReconcilePlayers` rules:

- A client that is connected or named has a player; a new player starts at 0,0. A player whose client is gone, or is unnamed and disconnected, is removed. `Players` is replaced only when the set changes.
- `ClientTitle` and `IsConnected` mirror the client. When a client connects, its player joins at the nearest position to where it stands at which its whole footprint is free, found ring by ring by `SpawnFinder` up to 256 tiles away.
- An offline named player keeps its saved position but is neither visible nor blocking, so nobody walks into an invisible obstacle.
- Renaming a client keeps its id, so its player keeps its position. Forgetting a client turns its tabs into new anonymous clients, so the old player is removed and new ones spawn. All tabs of a named client control the same player.
- Without an installation, `GetClients` returns null and the players stay as they are.

The loop in `ExecuteAsync` reconciles, flushes a pending save and then waits for either the signal or `HousekeepingInterval` (5 s). The signal is a `SemaphoreSlim(0, 1)` released by `SignalReconcile` on client changes, outside replacements of `Players` and `ApplyConfigurationAsync`. Exactly one pending wait is kept across passes: a new wait per pass would leave the old one queued to swallow the next signal.

## Movement and interaction

Moving is not an operation. The page calls the internal `Game.TryMove(player, direction, turnOnly)` once per step and passes only the player of its own circuit's client, so MCP, agents and other clients cannot move a player. Rules, all under `_lock`:

- The player must be the instance in this game's `Players`, and the direction defined.
- A token bucket per player (`StepRateLimiter`, burst 3, one token per `StepInterval` of 180 ms) limits steps, a second one limits turns. A refused take changes nothing.
- Every accepted attempt sets `Facing`, also when the step is blocked. A save is requested only when the position or facing changed.
- A step moves the footprint one tile and is refused when any tile it would cover holds another blocking object.
- A diagonal step is refused when both of its orthogonal steps would be refused, so players cannot squeeze between corner-touching blockers.
- A step that would leave the `int` range is refused (`Footprint.TryMove` uses `long` arithmetic).

The target area (`TargetArea`) is the footprint shifted one tile toward the facing, minus the footprint itself: 2 tiles straight ahead, 3 tiles around the corner when facing diagonally. `GetTargetObjects` returns the visible objects there, ordered from south-west to north-east.

Operations are the actions. Those that need to know who acts take `[FromServices] GameCaller? caller`; the page invokes through `GameCallerServiceProvider`, which returns its `GameCaller` and delegates every other service to the circuit. `Game.ResolveActor` is the single place that turns a caller into a player of this game and returns null otherwise, also for a player of another game. The subject page, MCP and agents pass no caller.

| Operation | Actor | Behavior |
|---|---|---|
| `Game.BuildWallAsync` | Required | Places a default wall across the facing, right in front of and centered on the actor (`WallPlacement`), named `wall-` plus 8 random hex digits. Refuses diagonal facing and blocked tiles |
| `Wall.DemolishAsync` | None | Removes the wall from its parent game |
| `Door.OpenAsync`, `Door.CloseAsync` | None | `Game.SetDoorOpenAsync` under the lock. Refuses the current state, and refuses closing while another blocking object covers a door tile |
| `GamePlayer.SendMessageAsync` | Optional | Notification "Message from {sender}" to the player's client, "Message" without actor. At most 500 characters after trimming; one message per second per sending player |

The panel offers every operation except those excluded from the user interface and those disabled through their `KnownAttributes.IsEnabled` attribute (`Door.Open_IsEnabled`, `Door.Close_IsEnabled`). Disabled operations are left out rather than shown disabled, so the number keys only reach operations that apply. Invoking goes through `OperationInvoker.InvokeAsync`, so parameter, confirmation, error and result dialogs behave as in the subject browser. Before invoking, the page checks that the object is still in the target area; while a dialog is open the browser does not move the player.

## Threading invariants

`_lock` guards `Players`, `Objects`, the positions and door states of their objects, the index, `_pendingPlayers`, `_pendingObjects`, `_isSaveRequested` and the per-player rate limiters and message timestamps. Every game write that two players could race on (move, join, build, demolish, open, close) happens under it, so two players never take one tile.

The property change observer (`Game.OnPropertyChanged`, and the page's handler) runs synchronously inside the write that caused it, on the writer's thread, while the change observable holds its own synchronization gate. A write under `_lock` waits for that gate. If an observer took `_lock`, an outside write delivering its change (holding the gate, waiting for `_lock`) and a game write (holding `_lock`, waiting for the gate) would deadlock. So observers never take `_lock` and never block: they call `SignalReconcile` or `RequestIndexRebuild`, and the page only schedules a flush. Apart from client changes, `OnPropertyChanged` ignores writes made on a thread that holds `_lock`, because the game keeps the index up to date for its own writes.

The index is rebuilt lazily. `RequestIndexRebuild` sets `_isIndexStale` with `Volatile.Write`, without the lock. `GetIndex`, called under the lock, clears the flag with `Interlocked.Exchange` before rebuilding from `Players` and `Objects`, so a request that arrives during a rebuild causes another one on the next use. A rebuild is requested by outside changes (a replaced `Players` or `Objects`, a changed `X`, `Y`, `Width`, `Depth`, `IsConnected` or `IsOpen` of an object of this game), by an applied configuration of the game or an object, and by reconciling when the player set changes. The game's other changes update the index incrementally. Two blocking objects on one tile, possible through hand edits, simply keep it blocked. An object with a width, depth or height outside 1 to 1024, possible in a loaded file that skips validation, is left out of the index, so it neither blocks nor renders.

Outside replacements of `Players` or `Objects` (configuration editor, reload) happen without `_lock`. `OnPlayersChanging` and `OnObjectsChanging` run in the generated setter before the write reaches the interceptor chain, so they may take `_lock`; when the writing thread does not hold it, they record the new value as pending. `CurrentPlayers` and `CurrentObjects` return the pending value when there is one, and every copy-on-write of the game starts from them and clears the pending value through `ReplacePlayers` and `ReplaceObjects`. This keeps a game write that runs between an outside write's hook and its landing from discarding the outside value.

The page reads some state without the lock:

- `Game.Players` and its dictionaries: they are copy-on-write and never mutated after assignment, so reading the current reference is safe.
- Single properties of objects, such as a target's title for the panel: one intercepted read of a property is never torn.
- `GameViewTracker.Known`: the tracker is only updated by `Game.UpdateView`, which the page calls from its own renderer dispatcher, and read on that same dispatcher right after, so no other thread touches it.

Everything else the page needs (`TryMove`, `GetTargetObjects`, `UpdateView`) takes the lock itself.

## Page and browser protocol

`GamePage.razor` is the `[SubjectComponent(SubjectComponentType.Page, typeof(Game))]`. It resolves its player from `CurrentClientConnection.Connection.ClientId` and `Game.Players` on every flush and shows "Joining the game…" until the player exists.

JS interface of `game.js`, keyed by a per-page view id because the pages of a tab share the module:

| Call | Direction | Purpose |
|---|---|---|
| `start(canvas, dotNet, options)` | .NET to JS | Probes WebGL, loads Babylon, the model and textures, returns `{ isStarted, error }`. Options: `viewId`, `pad`, `stepIntervalMs`, `baseUrl` |
| `applyUpdate(viewId, update)` | .NET to JS | Upserted view objects and removed ids |
| `dispose(viewId)` | .NET to JS | Releases the view; a dispose during `start` makes it return not started |
| `Step(sequence, direction, turnOnly)` | JS to .NET | Calls `TryMove`; returns the sequence, verdict, position and facing, or null without a player |
| `InvokeTargetAction(index)` | JS to .NET | Invokes the numbered action of the number key |

Flushing: the page subscribes to property changes of the game and of every `GameObject`. The handler only sets a flag and starts a 50 ms delay, after which `FlushAsync` runs on the renderer dispatcher, so updates go out in batches of at most about 20 per second. A flush asks `Game.UpdateView` for the diff of the visible objects within `ViewRadius` (200 tiles, 50 m) of the own player, or of the origin without one, found through the chunk index. A `JSException` from `applyUpdate` marks the view broken and stops the subscription.

`GameViewTracker` keeps one `GameViewObject` per known object with an id unique within the page. It recreates a view object only when a sent field changed and returns a shared empty update when nothing changed, so an unchanged view allocates nothing. Player colors derive from the client id with FNV-1a and are cached per client id; walls and doors have fixed colors.

Prediction and reconciliation (`game-input.js`): each step carries an increasing sequence number rather than key down and up, so a delayed message never makes a player walk further. The browser applies the step at once with `predictStep`, which mirrors `Game.TryGetStep` against the blocking objects it knows (not the rate limit). When the answer arrives, it drops the answered steps, starts again from the server's position and replays the steps still pending. A server update of the own player is taken only while no step is pending. When the own player leaves the view or stops being the own player, `reset` drops the prediction and pending steps, and a generation counter discards answers to steps sent before.

Other players, and the own player on a correction, move with a tween over one step interval and play the walk animation while it runs, which evens out network jitter. A jump of more than 6 tiles is placed without a tween.

Coordinates travel as absolute integers, which JS numbers hold exactly. `game-entities.js` subtracts an integer origin near the own player before anything reaches the GPU and rebases it when the player gets more than 2048 tiles away, so the world renders sharply near `int.MaxValue` despite 32-bit floats.

The Babylon loader in `game.js` fetches `babylon.js` and `babylonjs.loaders.min.js` once per tab and runs each wrapped in a function whose `define` parameter shadows the global one. It must stay: the app hosts an AMD loader with a global `define` (for the code editor), and Babylon's UMD build would otherwise register as an anonymous AMD module instead of setting `window.BABYLON`.

Input rules:

- Arrow keys and WASD combine into 8 directions (W plus D is north east) and repeat once per step interval while held. Shift with a direction sends one turn-only step. Number keys 1 to 9 invoke the numbered actions.
- Keys with Ctrl, Alt or Meta go to the browser. Keys and pad presses are ignored while focus is in an input field or a MudBlazor dialog is open. Losing window focus releases everything.
- The direction pad is shown for `(pointer: coarse), (max-width: 959.98px)`. `game-input.js` handles its pointer events without a round trip: a held button adds its directions to the held keys, so it repeats and combines like keys, and pointer capture keeps the press while the finger drifts. The middle button toggles turn-only mode, in which a press sends one turn. The pad blocks scrolling, text selection and the long press menu.

Rendering:

- One world unit is one tile. The camera looks at a point 3 tiles above the own player from 28 tiles above and 30 tiles south of it, follows smoothly and snaps on jumps over 20 tiles. Fog runs from 110 to 230 tiles. There are no shadow maps; a soft blob under each player gives depth.
- The ground is one 600 tile plane that follows the player in whole meters, so its grass texture and the 1 m grid drawn into the texture border stay on the tiles. The target area is marked tile by tile.
- Walls are individual boxes with per-face UVs scaled to their size (one brick repeat per 8 x 4 tiles), not thin instances, so the texture keeps its size on every wall length.
- Doors are a brick lintel from 8.4 tiles up to the full height and a wooden panel 8.4 tiles tall and 0.3 thick, hinged at the west end of an east-west opening or the south end of a north-south one. Opening swings it a quarter turn over 300 ms.
- Walls and doors that the segment from the camera to the own player's head crosses fade to 30 % visibility.
- Players are instances of the glTF character, scaled to their `Height`, rotated to their facing, with the shirt tinted and a billboard label.

Side panel (240 px, below the canvas under the `Md` breakpoint): **Position** with the pad, **Looking at** (only while targets exist, actions numbered 1 to 9 across all targets), **Actions** (operations of the `Game`, disabled without a player) and **Nearby** (players in view, the own one first).

## Assets and licenses

Everything is vendored under `wwwroot/`, served by `MapStaticAssets` (compressed and fingerprinted at build time). Each folder has a `LICENSE.md` with source and terms.

| Folder | Asset | License |
|---|---|---|
| `lib/babylonjs/` | Babylon.js 9.31.0 core and glTF loader (UMD builds) | Apache 2.0 |
| `models/` | `Casual_2.glb` from the Ultimate Modular Men pack by Quaternius, only `Idle_Neutral` and `Walk` kept | CC0 |
| `textures/` | ambientCG Grass004 (`grass.jpg`) and Bricks097 (`bricks.jpg`), re-encoded | CC0 |

To swap the character, replace the GLB and its license, then update `game-entities.js`: the file name in `LoadAssetContainerAsync`, `IDLE_ANIMATION`, `WALKING_ANIMATION`, `SHIRT_MATERIAL` (the material tinted with the player color) and `WALKING_SPEED_RATIO` so the feet keep pace with one tile per step. The model is scaled from its bounding box to the player's `Height` and must face north (+z) after the loader's handedness conversion.

## Testing

```
dotnet test src/HomeBlaze/HomeBlaze.Game.Tests --filter "Category!=Integration"   # unit tests
dotnet test src/HomeBlaze/HomeBlaze.Game.Tests --filter "Category=Integration"    # E2E tests
```

The E2E tests need Playwright's Chromium (`playwright.ps1 install chromium` from the test output folder). Logic lives in plain classes that unit tests cover; rendering is covered end to end, and bUnit is not used.

| Helper | Purpose |
|---|---|
| `TestRoot` | A root subject that holds the game and counts configuration writes; `FailsToWrite` simulates failures |
| `FakeClientDirectory` | Thread-safe clients and recorded notifications; `RemoveInstallation` simulates a missing installation |
| `GameTestContext` | A game in a `TestRoot` with a `FakeTimeProvider` and fake clients; adds connected players, walls and doors |
| `FrozenTimeProvider` | A stopped clock that runs a callback inside its next reading, which lets a test act while the game holds its lock |

The E2E tests use `PlaywrightFixture` from `HomeBlaze.E2E.Tests` through their own `GameE2ECollection`, because xUnit only finds collection definitions in the test assembly. They assert the side panel, not canvas pixels; headless Chromium renders WebGL in software. Every installation sees all circuits of the shared server, and circuits of closed browser contexts stay connected for minutes, so players of earlier tests may still stand in a new game. The tests therefore read where their own player joined, move the other players far aside, and assert positions relative to the join point.

## Known limits and follow-ups

- The door panel swing is visual only: blocking uses the door's footprint, and the swung panel can pass through walls or players.
- The lintel can still cover the player in some camera positions; fading tests only the segment from the camera to the head.
- The walking character is wider than its 2 x 2 tile footprint, so arms and legs can reach into neighboring walls.
- An outside replacement of `Objects` that lands after a game write that started from its pending value wins, discarding that game change (for example a wall built in that window). The same race on `Players` heals on the next reconcile.
- Without an installation the page keeps showing "Joining the game…" rather than saying it needs one.
- Only one character model, and only walls and doors as object types.

Agent players are the planned follow-up. `ResolveActor` and `GameCaller` already isolate who acts, so operations keep their signatures. It needs:

1. `HomeBlaze.AI` to expose the calling agent to operations generically, for example as a `[FromServices]` type defined there. `McpMethodInvoker` currently invokes with the global service provider.
2. Players bound to an agent subject instead of a client, always persisted and visible while the agent runs.
3. Perception and movement for agents, such as a `LookAround(radius)` query and a `MoveAsync(direction, steps)` operation.
4. Triggers for agent turns: being looked at, receiving a message, or a timer.
