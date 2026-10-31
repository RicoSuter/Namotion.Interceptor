# HomeBlaze Game: Design

Date: 2026-10-31

## Goal

A multiplayer world inside HomeBlaze. Every browser client of the installation is a player on an infinite grid of 25 cm tiles, rendered as a tilted 3D perspective scene with the own player centered on screen. Players walk in 8 directions, look at the tile in front of them and invoke the operations of the object standing there. Players can build and demolish walls and send each other messages.

## Scope

In scope for the first version:

- One `Game` subject that is a page, with players and objects as children.
- One player per installation client, bound to that client; only that client moves it.
- Movement in 8 directions, one tile per step, with blocking.
- A `GameObject` base subject with a footprint of several tiles, and one object type, `Wall`.
- Interaction: the operations of the objects in front of the player, and the operations of the game, invoked through the usual operation flow.
- Rendering with Babylon.js, an animated CC0 character model, walls of 2.5 m.
- Unit and E2E tests.

Out of scope, planned as follow-ups:

- Players controlled by agents (see [Follow-up: agent players](#follow-up-agent-players)).
- Touch controls.
- More object types and interactions between players beyond sending a message.

## Projects and dependencies

| Project | Contents |
|---|---|
| `src/HomeBlaze/HomeBlaze.Game` (new, Razor SDK, references `SonarAnalyzer.CSharp` with `PrivateAssets="all"`) | Subjects, page component, JS modules, vendored Babylon.js under `wwwroot/lib/`, the character model under `wwwroot/models/` and textures under `wwwroot/textures/`, each with its license file |
| `src/HomeBlaze/HomeBlaze.Game.Tests` (new) | Unit tests and E2E tests |
| `src/HomeBlaze/HomeBlaze` (app) | Project reference to `HomeBlaze.Game` and `.AddAssembly(typeof(Game).Assembly)` in `Program.cs` |

`HomeBlaze.Game` references:

- `HomeBlaze.Abstractions` (attributes, metadata, `Notification`).
- `HomeBlaze.Components` (`OperationInvoker`).
- `HomeBlaze.Services` (`CurrentClientConnection`).
- `HomeBlaze.Installation` (`HomeBlazeInstallation`, `HomeBlazeClient`).
- `HomeBlaze.Storage.Abstractions` (`IConfigurationWriter`).
- The Namotion.Interceptor generator, tracking and registry libraries.

`HomeBlaze.Game.Tests` references `HomeBlaze.Game` and `HomeBlaze.E2E.Tests` (for `PlaywrightFixture`). `HomeBlaze.Game` makes its internals visible to the tests.

Two changes outside the new projects:

- `CurrentClientConnection` moves from `HomeBlaze.Host` (internal) to `HomeBlaze.Services.Clients` as a public scoped service, so the game page can learn the client of its circuit. `HomeBlaze.Host` keeps registering and setting it.
- `ConfigurationJsonTypeInfoResolver` in `HomeBlaze.Services` writes and reads `$type` also for properties declared as an abstract `IConfigurable` class, such as `Dictionary<string, GameObject>`. Before, only properties declared as `IConfigurable` or `IInterceptorSubject` were polymorphic.

Dependency rule: `HomeBlaze.Game` may later reference `HomeBlaze.AI`; `HomeBlaze.AI` never references or mentions the game.

The game finds the installation with `registry.KnownSubjects.Keys.OfType<HomeBlazeInstallation>()`, as other subjects find history stores. `HomeBlazeInstallation` is `[SingleInstance]`.

## Subject model

```
Game : BackgroundService, IConfigurable, ITitleProvider, IIconProvider
  [Configuration] string Name
  [Configuration] Dictionary<string, GamePlayer> Players   key: client id, managed by the game
  [Configuration] Dictionary<string, GameObject> Objects   key: object name, managed by the user and by player actions
  [Operation] BuildWallAsync([FromServices] GameCaller? caller, CancellationToken)

GameObject (abstract) : IConfigurable
  [Configuration] int X                                    south-west corner of the footprint, grows to the east
  [Configuration] int Y                                    south-west corner of the footprint, grows to the north
  [Configuration, Range(1, 1024)] int Width                footprint in tiles, east to west
  [Configuration, Range(1, 1024)] int Depth                footprint in tiles, north to south
  [Configuration, Range(1, 1024)] int Height               in tiles, for rendering only
  virtual bool IsBlocking => true
  virtual bool IsVisible => true

Wall : GameObject
  Width, Depth, Height default to 4, 1, 10                 a segment 1 m long, 25 cm thick, 2.5 m tall
  [Operation] DemolishAsync(CancellationToken)

GamePlayer : GameObject
  Width, Depth, Height default to 2, 2, 7                  50 by 50 cm footprint, 175 cm tall
  [Configuration] GameDirection Facing
  [State] string ClientId
  [State] string ClientTitle
  [State] bool IsConnected
  [Derived] string Title                                   the title of the client
  override bool IsBlocking => IsConnected
  override bool IsVisible => IsConnected                   a player is blocking and visible only while connected
  [Operation] SendMessageAsync(string message, [FromServices] GameCaller? caller, CancellationToken)

enum GameDirection { North, NorthEast, East, SouthEast, South, SouthWest, West, NorthWest }
```

`Game.StepInterval` is a constant of 180 ms, about 1.4 m/s.

Several `Game` subjects may exist; each one is an independent world with its own players.

One tile is 25 cm. An object covers the tiles from X to X + Width - 1 and from Y to Y + Depth - 1. The player's footprint is square, so turning never changes the tiles it covers and diagonal facing needs no special rules; the character is scaled to the height of the player and drawn slim inside it. `Width`, `Depth` and `Height` are configuration on every object, so a hand-edited wall can have any size from 1 to 1024 tiles per axis.

`GamePlayer` derives from `GameObject`, so one occupancy lookup covers players and walls and the renderer treats both as objects. Players and objects still live in separate collections because different parties manage them and only named players persist.

### Actor resolution

Operations that need to know who acts declare `[FromServices] GameCaller? caller`. `GameCaller` describes the caller; in the first version it only carries the `GamePlayer` set by the game page. `Game.ResolveActor(GameCaller? caller)` is the single place that turns a caller into the acting player of this game, and returns null when there is none, also for a player of another game. The subject page, MCP and agents pass no caller, so operations that require an actor refuse with a clear message there. Operations that do not need the actor, such as `Wall.DemolishAsync`, take no caller. The agent follow-up extends `GameCaller` and `ResolveActor` without changing operation signatures.

The game page invokes operations with a small `IServiceProvider` wrapper that returns its `GameCaller` and hands every other request to the circuit's service provider. `MethodMetadata.InvokeAsync` already resolves nullable `[FromServices]` parameters from the provider it is given, and `OperationInvoker.InvokeAsync` already takes the provider as an argument, so neither changes.

## Players and clients

The game runs as a `BackgroundService`. It reads the clients through an internal `IGameClientDirectory`, which lists them with id, title, whether they are named and whether they are connected, sends a notification to a client and recognizes the property changes that concern the clients. `InstallationClientDirectory` implements it for the installation; unit tests use a fake, because the installation's client reconciling is internal to it, and the E2E tests cover the real installation. The game's loop reconciles its players on every client change, signaled through property change tracking, and at least every 5 seconds:

- A client that is connected or named has a player. A player starts with its footprint at 0,0 and, whenever its client connects, joins at the nearest position to where it stands where its whole footprint is free, found by a spiral search.
- A player is removed when its client is no longer in `Clients`, or is unnamed and disconnected.
- `IsConnected` and `ClientTitle` mirror the client. Only connected players are visible and blocking (`IsVisible` and `IsBlocking` follow `IsConnected`), so the page renders only them and nobody walks into an invisible obstacle. An offline named player keeps its saved position. When it reconnects onto tiles that became blocked, it moves to the nearest free position by the same spiral search.
- Renaming a client keeps its id, so its player keeps its position and persists from then on.
- Forgetting a client turns its tabs into new anonymous clients, so the old player is removed and new ones spawn.
- Without an installation subject the game keeps its players as they are and the page says so.
- All tabs of a named client control and show the same player.

## Movement

Moving is not an operation. The page calls the internal `Game.TryMove(GamePlayer player, GameDirection direction, bool turnOnly)` once per step and only ever passes the player of its own circuit's client, so MCP, agents and other clients cannot move a player. The result carries whether the step was accepted plus the authoritative position and facing. The browser sends each step with a sequence number rather than key down and up, so a delayed message never makes a player walk further than intended.

Rules, all enforced on the server under the game's lock:

- A step moves the footprint by one tile in the facing direction, 25 cm.
- Every attempt sets `Facing`, also when the step is blocked. `turnOnly` (Shift) only turns. A save is requested only when the position or facing changed.
- A step is refused when any tile the footprint newly covers holds a blocking object, wall or player.
- A diagonal step is refused when both of its orthogonal steps would be refused, so players cannot squeeze between corner-touching blockers.
- A token bucket per player refills one token per `StepInterval` with a burst of 3, and a second one limits turns the same way. A refused step or turn leaves the player unchanged.
- A step that would leave the `int` range is refused, checked with `long` arithmetic.
- The target area is the footprint shifted one tile in the facing direction, minus the footprint itself: the 2 tiles in front when facing straight, the 3 tiles around the front corner when facing diagonally.

### Index

One lock in `Game` guards moves, building, demolishing and reconciling. The game keeps:

- a chunk index of 16 by 16 tile chunks to the objects whose footprint intersects them, for radius queries;
- a tile to blocking objects lookup covering every tile of each footprint, for occupancy.

The game's own changes update both incrementally. Changes from outside the game only mark the index stale, and the next use rebuilds it under the lock. They are a replaced `Objects` or `Players`, a changed X, Y, Width, Depth or `IsConnected` of an object of the game, and an applied configuration, for example through the configuration editor or a reload. Two blocking objects on one tile, possible through hand edits, simply leave the tile blocked. An object whose width, depth or height is outside 1 to 1024, possible in a loaded file that skips the range validation, is left out of the index, so it neither blocks nor renders.

Change observers run while the change observable holds its own lock, which every write under the game's lock waits for, so they mark the index stale without taking the game's lock. An outside replacement of `Objects` or `Players` happens without the game's lock, so it is recorded under the lock as pending, and the game's copy-on-write of either collection starts from it instead of a stale copy, as the installation does for its clients. An outside replacement of `Players` also signals the loop to reconcile, so reloaded players of connected clients join right away.

## Actions and interaction

- The side panel's "Looking at" section shows the operations of the objects in the own player's target area, grouped by object and numbered 1 to 9 across all of them for the keyboard.
- The side panel's "Actions" section shows the operations of the `Game`.
- All operations are offered, except those whose `ExcludeFrom` includes `ExclusionTarget.UserInterface`.
- Invoking goes through `OperationInvoker.InvokeAsync`, so parameters open the usual `MethodParameterDialog`, `RequiresConfirmation` opens `MethodConfirmationDialog`, errors and results open their usual dialogs, exactly as in the subject browser.
- Before invoking, the page checks that the object is still in the target area. While a dialog is open the keyboard does not move the player, so the target cannot change during a dialog.

Operations of the first version:

- `Game.BuildWallAsync`: places a new default `Wall`, 4 by 1 tiles, across the actor's facing direction directly in front of the actor, centered on its footprint. Refuses without actor, when the actor faces diagonally, or when any of the tiles is blocked. The wall gets a generated unique name.
- `Wall.DemolishAsync`: removes the wall from its parent game, found with `TryGetFirstParent<Game>()`, and saves the game. It needs no actor.
- `GamePlayer.SendMessageAsync`: sends a notification with the title "Message from {sender}", or "Message" without an actor, through the client directory to the open tabs of the player's client. Refuses when the player's client is unknown or not connected, when the message is longer than 500 characters, or when the sending player sent a message less than a second ago.

## Persistence

- The game writes its configuration through `IConfigurationWriter` of its parent, as the installation does.
- Building and demolishing write immediately.
- Joins, moves and turns of players are coalesced into one write by the game's loop, at most every 5 seconds, and pending changes are written in `StopAsync`.
- Anonymous players are written like any other but dropped on load by reconciling, the same way the installation drops unnamed clients without a circuit.
- A failed write logs a warning and stays pending, so the loop's next flush tries again; the game keeps running. A final write that `StopAsync` cancels logs a warning.
- `ApplyConfigurationAsync` marks the index stale, which the next use rebuilds, and signals the loop to reconcile the players.

## Page, rendering and synchronization

### Page

`GamePage.razor` with `[SubjectComponent(SubjectComponentType.Page, typeof(Game))]`:

- The page is served at `/pages/{path}` without the file extension, for the demo world `/pages/Demo/Game`.
- The page fills the width of the main content and the viewport height below the app bar. The canvas takes all of it except for a side panel on the right and carries no overlays besides the loading, joining and error messages.
- The side panel is always visible, 240 px wide and styled like the rest of HomeBlaze. From top to bottom it shows:
  - **Position**: `X 3 · Y -2 · facing NE`.
  - **Looking at**: shown only while objects are in the target area; each object's title with its operations as buttons, numbered 1 to 9 across all objects.
  - **Actions**: the operations of the `Game`, such as "Build wall".
  - **Nearby**: the connected players within the view radius, each with its color dot, the own player first.
- On viewports narrower than the MudBlazor `Md` breakpoint, the panel moves below the canvas.
- The page passes `StepInterval` to JS, which uses it for key repeat, the walk tween and the walk animation speed. Each page has its own view id, because the pages of a tab share the module.
- The page resolves its player from `CurrentClientConnection.Connection.ClientId` and `Game.Players` on every update, shows "Joining the game…" until the player exists, and resolves again when the client id changes.
- A loading indicator shows until Babylon and the model are ready. A longer first load is acceptable.

### JS module

`game.js`, an ES module imported by the page, with `game-input.js` for keyboard, steps and prediction and `game-entities.js` for meshes, tweens, ground and target marker:

- Loads `babylon.js` and `babylonjs.loaders.min.js` from `_content/HomeBlaze.Game/lib/babylonjs/` once per tab through a loader that fetches each script and runs it with the page's global AMD `define` shadowed. The app hosts an AMD loader (Monaco), which would otherwise make Babylon's UMD build register as an anonymous AMD module instead of setting `window.BABYLON`. It creates the engine on the canvas and disposes everything when the page goes away.
- Combines held keys (arrow keys and WASD) into one of 8 directions, W plus D giving north east, and repeats once per `StepInterval` while held. Shift turns only. The number keys 1 to 9 invoke the numbered actions. Each step calls .NET through a `DotNetObjectReference`.
- Ignores keys while focus is in an input field or a MudBlazor dialog is open.
- Predicts the own player's steps against the objects it knows nearby and moves immediately. Each step carries an increasing sequence number, which the page echoes with the server's verdict. When the server refuses a step, the browser resets to the server's position and replays the pending steps sent after it (client-side prediction with server reconciliation). When the own player leaves the view or stops being the own player, the prediction and the pending steps are dropped, so the next own player starts from the server's position.
- Moves other players, and the own player on a correction, with a tween over one `StepInterval` from the old to the new position, so their walking looks even despite network jitter, and plays the walk animation while the tween runs.

### Synchronization

- First, a snapshot of every object whose footprint lies within 200 tiles (50 m) in each direction of the own player: id, kind, X, Y, Width, Depth, Height, facing, title and whether it is the own player.
- Then additions, moves and removals in batches, at most about 20 per second. The page observes property changes of the game and its objects, and each flush asks the game for the diff of the objects within the radius, computed with the chunk index.
- Change observers run synchronously inside the writes of the object graph, so the page's handler only schedules a flush; calls to JS happen when the batch is flushed.
- Coordinates travel as absolute integers, which JS numbers hold exactly. JS subtracts an integer origin near the own player, rebased when the player gets 2048 tiles away from it, before anything reaches the GPU, so the world renders sharply near `int.MaxValue` despite 32-bit floats on the GPU.
- Static assets are served by `MapStaticAssets`, which compresses them with Brotli at build time and caches them by fingerprint, so the game also works over the internet.

### Rendering

- One world unit is one tile, 25 cm. A perspective camera 28 tiles above and 30 tiles south of the point it looks at, about 43 degrees down, follows the own player smoothly. Fog fades the world out between 110 and 230 tiles.
- The ground is one plane that follows the player in whole meters, with a repeating grass texture and a faint grid line every 4 tiles (1 m). The target area is marked tile by tile.
- Walls are individual boxes of their footprint and height with a brick texture, not thin instances, so every face scales its UVs and the texture keeps its size on every wall length. The texture repeats every 8 by 4 tiles. Walls between the camera and the own player's head, found by a segment-box test, fade to semi-transparent.
- Players are the animated glTF character with `Idle_Neutral` and `Walk` animations, scaled to the height of the player, rotated to their facing, with the shirt tinted with a color derived from the client id and labeled with their title. There are no shadow maps; a soft blob under each player and the lit wall faces give the depth.

## Assets

All assets are vendored under `HomeBlaze.Game/wwwroot/` with their license files and pinned versions:

- `lib/babylonjs/`: Babylon.js 9.31.0 core and glTF loader (Apache 2.0).
- `models/`: the "Casual_2" character of the Ultimate Modular Men pack by Quaternius (CC0), converted to GLB with only the `Idle_Neutral` and `Walk` animations.
- `textures/`: ambientCG Grass004 and Bricks097 (CC0), re-encoded to save bandwidth.

## Demo world

`src/HomeBlaze/HomeBlaze/Data/Files/Demo/Game.json` holds a `Game` with a house built from walls, so the game can be tried right away when running from source:

- A house of 40 by 32 tiles (10 by 8 m) north of the spawn position 0,0, so a new player starts outside and looks at it.
- Outer walls built from wall segments, 10 tiles (2.5 m) tall, with a door gap of 4 tiles (1 m) in the south wall in front of the spawn position.
- Inner walls that divide it into a hall and two rooms behind it, connected by door gaps of 4 tiles.
- A pillar, a fence and two blocks touching at a corner outside, to try blocking and the diagonal squeeze rule.

The seed folder of fresh installations stays unchanged.

## Development workflow

After each implementation task that leaves something runnable, the app is started in Release mode, listening on `http://192.168.1.201:<port>` so it can be tried from the local network, and work continues with the next task while it runs. The running instance is restarted after later tasks change the game.

## Error handling and edge cases

| Situation | Behavior |
|---|---|
| No installation subject | The game stays idle and the page says that it needs the installation |
| Player does not exist yet | The page shows "Joining the game…" |
| Several tabs of a named client | All control and show the same player; the token bucket belongs to the player |
| An anonymous tab reconnects with a new circuit | It is a new client: the old player is removed and a new one spawns |
| An operation fails or `BuildWallAsync` finds a tile blocked | The operation throws with a clear message; `OperationInvoker` shows its error dialog |
| Writing the configuration fails | A logged warning; the game keeps running |
| Shutdown | Pending position changes are written in `StopAsync` |
| Configuration reloaded or edited by hand | Index rebuilt and players reconciled; overlapping blockers leave the tile blocked |
| WebGL unavailable, or Babylon or a model fails to load | The page shows a message instead of the canvas; the side panel and the server side are unaffected |

## Testing

All tests live in `HomeBlaze.Game.Tests`, named `When<Condition>_Then<ExpectedBehavior>` with `// Arrange`, `// Act`, `// Assert` and without hardcoded waits. bUnit is not used: logic lives in plain classes that unit tests cover, and rendering is covered end to end.

Unit tests:

- Movement: blocked step refused but turns; a step blocked by only one tile of the footprint refused; diagonal squeeze refused; turn only; token bucket refuses the 4th quick step or turn within one `StepInterval` (`FakeTimeProvider`); a blocked step without a turn requests no save; steps past `int.MaxValue` and `int.MinValue` refused.
- Target area for straight and diagonal facing.
- Spawning at 0,0 and at the nearest free position for the whole footprint.
- Reconciling against a fake `IGameClientDirectory`: anonymous client gets a player that disappears on disconnect; named client's player stays while offline and does not block; a reconnecting player on a blocked tile moves to the nearest free tile; renaming keeps the player; forgetting removes it; without an installation the players are kept.
- Chunk index: radius queries, objects spanning several chunks, incremental updates, rebuild after an outside change, objects with sizes outside the range left out.
- View diffs: objects entering and leaving the radius as the own player or they move; an unchanged view allocates nothing.
- Loop: a signal after a won housekeeping delay still reconciles promptly; an outside move of an object while the game holds its lock does not wait for the game; an outside replacement of `Players` and an applied configuration reconcile the players.
- Operations: `BuildWallAsync` builds across the facing direction, refuses a blocked tile, diagonal facing and a missing actor; `DemolishAsync` removes the wall; `SendMessageAsync` reaches the other client with the sender's name through the fake directory, and refuses a too long message and a second message within a second.
- Actor resolution and the service provider wrapper.
- Persistence: anonymous players dropped on load, coalesced position writes, pending writes on stop, a failing write that does not throw and is retried by the next flush, a canceled stop that does not throw.
- Serialization: walls keep their type and footprint through a round trip of a game.

E2E tests, marked `Category=Integration`, use `PlaywrightFixture` from `HomeBlaze.E2E.Tests` through an own collection definition, with headless Chromium rendering WebGL through SwiftShader. They assert the side panel, not canvas pixels. The circuits of closed browser contexts stay connected for minutes, so the players of earlier tests may still stand in a new game. The tests therefore read where their own player joined, move the other players far away, and assert positions relative to the join position:

- The page loads and shows the join position, facing north, with the own player under "Nearby".
- An arrow key pressed once moves the player one tile; Shift with an arrow only changes the facing.
- Walking into a wall keeps the position and shows the wall under "Looking at".
- Build a wall from "Actions", then demolish it from "Looking at".
- With two browser contexts, each lists the other under "Nearby", and "Send message" opens the notification dialog in the other context.

The Connector Tester and benchmarks do not apply.

## Follow-up: agent players

Players controlled by agents get their own spec. The first version prepares for it:

- Actor resolution lives in `Game.ResolveActor(GameCaller?)` and operations take `GameCaller`, so adding agents changes neither the operations nor their signatures.
- A player's binding is modeled so that an agent binding can be added next to the client binding without breaking the persisted format.

The follow-up needs:

1. `HomeBlaze.AI` to expose the calling agent to operations generically, for example as a `[FromServices]` type defined in `HomeBlaze.AI`. Each agent turn already has its own token and tool set (`AgentTurnTokenRegistry`), but `McpMethodInvoker` invokes with the global service provider today.
2. Players configured with a reference to an agent subject, always persisted and visible while the agent runs.
3. What an agent perceives and does: a query such as `LookAround(radius)` and an operation such as `MoveAsync(direction, steps)`.
4. What triggers an agent turn: being looked at, receiving a message, or a timer.
