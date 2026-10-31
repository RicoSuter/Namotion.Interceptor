# HomeBlaze.Game

Maintainer documentation for the game library: how it is built, which invariants hold it together and what is still open.

## Overview

A `Game` subject is a multiplayer world that HomeBlaze shows as a page. Every browser client of the installation is a player on an infinite grid of 25 cm tiles. Players walk in 8 directions, look at the tiles in front of them and invoke the operations of the objects standing there, such as demolishing walls, adding doors to them, opening doors and sending messages, and use the tools of the game to build walls and plant trees. Object types such as walls, doors and trees use only the public API of the core; see [Objects and extension points](#objects-and-extension-points). The page renders the world with Babylon.js as a tilted perspective scene centered on the own player. Players within 10 m along an open path talk to each other with their microphones; the server mixes the audio for every listener. Several `Game` subjects may exist; each is an independent world with its own players.

| Project | Contents |
|---|---|
| `HomeBlaze.Game` (Razor SDK) | Subjects, the page component, the JS modules and the vendored assets under `wwwroot/` |
| `HomeBlaze.Game.Tests` | Unit tests and E2E tests |

The app registers the library with a project reference in `HomeBlaze.csproj` and `.AddAssembly(typeof(HomeBlaze.Game.Game).Assembly)` in `Program.cs`. `Data/Files/Demo/Game.json` of the app holds the demo world, served at `/pages/Demo/Game`: a house of 56 x 40 tiles (14 x 10 m) with six rooms (living room, hall, kitchen, two bedrooms and a bathroom), brick outer walls, concrete inner walls and doors 4 to 6 tiles wide, a wooden shed with a closed door to the west, a forest of 32 trees to the east, 7 more trees around the house, and the tools `wall-tool` and `tree-tool`. New players spawn at 0, 0, just south of the open entrance. A script generated the file once with a fixed random seed; the script is not part of the repository, and the file is edited like any saved world. `DemoWorldTests` checks that it loads, that every room is reachable from the spawn point and that no tree blocks a door.

The library depends on `HomeBlaze.Abstractions`, `HomeBlaze.Components` (`OperationInvoker`), `HomeBlaze.Services` (`CurrentClientConnection`, the configuration serializer), `HomeBlaze.Installation` (clients), `HomeBlaze.Storage.Abstractions` (`IConfigurationWriter`) and the Namotion.Interceptor generator, tracking and registry libraries. Dependency rule: `HomeBlaze.Game` may reference `HomeBlaze.AI` for agent players; `HomeBlaze.AI` never references or mentions the game.

| Folder | Contents |
|---|---|
| `Game*.cs` | The `Game` subject, split by concern: `Game.cs` (properties, copy-on-write helpers), `Game.Runtime.cs` (loops, change observer), `Game.Index.cs`, `Game.Players.cs`, `Game.Movement.cs`, `Game.Actions.cs` (`ResolveActor`, messages), `Game.Objects.cs` (object API), `Game.Placement.cs` (placement API), `Game.Persistence.cs`, `Game.View.cs`, `Game.Volume.cs` (volume table), `Game.Voice.cs` (toggles, voice loop) |
| `GameObject.cs`, `GamePlayer.cs`, `GameObjectSnapshot.cs` | The base of every object on the grid, the player, and the snapshot with which `UpdateAsync` undoes a refused change |
| `Visuals/` | `GameVisual`, `GameVisualPart`, `GameVisualVector`, `GameVisualPoint`, `GameVisualShape`, `GameVisualEffect`, `GameVisualCache`, `GameAssets`, `GameVisualValidator` |
| `Tools/` | `GameTool`, `GamePlacement` |
| `Objects/Buildings/` | `Wall`, `WallMaterial`, `WallTool`, `Door`, `WallTextures` |
| `Objects/Nature/` | `Tree`, `TreeVariant`, `TreeTool` |
| `Clients/` | `IGameClientDirectory` and its installation implementation |
| `World/` | Pure grid logic: `Footprint`, `GameWorldIndex`, `HearingRange`, `SoundPathSearch`, `RateLimiter`, `SpawnFinder`, `TargetArea`, `FrontPlacement` |
| `View/` | `GameViewTracker` and what it sends to the browser: `GameViewObject`, `GameViewPart`, `GameViewUpdate`, and `GameColors` for players |
| `Voice/` | `GameVoiceSettings`, `GameVoiceCodec`, `GameVolumeTable`, `TalkerQueue`, `GameVoiceMixer`, `IGameVoiceSink` |
| `Components/` | `GamePage.razor`, `GameNameDialog.razor` and `GameCallerServiceProvider` |
| `wwwroot/` | `game.js` (lifecycle, Babylon loader, camera, frame rate), `game-input.js` (keyboard, pad, prediction), `game-entities.js` (parts, models, wind, glides, fading, players, tweens, ground, target marker), `game-voice.js` (audio context, capture, playback, codec seam), `game-voice-worklet.js` (capture and playback processors), `lib/`, `models/`, `textures/`, `objects/` (assets of the object types) |

## Model

North is +Y and east is +X. An object covers the tiles from `X` to `X + Width - 1` and from `Y` to `Y + Depth - 1`.

| Subject | Property | Kind | Notes |
|---|---|---|---|
| `Game` | `Name` | Configuration | Title falls back to "Game" |
| | `Players` | Configuration | `Dictionary<string, GamePlayer>` keyed by client id, managed by the game |
| | `Objects` | Configuration | `Dictionary<string, GameObject>` keyed by object name, managed by the user and by player actions |
| | `Tools` | Configuration | `Dictionary<string, GameTool>` keyed by tool name, managed by the user; the game never changes it |
| `GameObject` (abstract) | `X`, `Y` | Configuration | South-west corner of the footprint |
| | `Width`, `Depth`, `Height` | Virtual, not persisted | In tiles, 1 to `MaximumSize` (1024); `Height` is used for rendering, for the size check of the index and by walls for sound blocking. Walls and doors override them as configuration, trees compute them from variant and scale, players fix them |
| | `Visual` | Virtual, not persisted | A `GameVisual`, by default a gray box of the footprint and `Height`; see [Objects and extension points](#objects-and-extension-points) |
| | `IsMovementBlocking`, `IsVisible` | Virtual, not persisted | `true` by default |
| | `IsSoundBlocking` | Virtual, not persisted | Whether the object stops sound, `false` by default, so an object that blocks movement, such as a plant or a chair, lets sound through; see the hearing range under [Voice](#voice) |
| `Wall` | `Width`, `Depth`, `Height` | Configuration, `[Range(1, 1024)]` | Defaults 4 x 1 x 10 tiles (1 m long, 25 cm thick, 2.5 m tall). `IsSoundBlocking` is `Height >= 8`: a wall of 2 m or more blocks sound, a lower one lets it pass over |
| | `Material` | Configuration | `Bricks` (default), `Concrete` or `Wood` |
| `Door` | `Width`, `Depth`, `Height` | Configuration, `[Range(1, 1024)]` | Defaults 4 x 1 x 10; use 1 x 4 in a north-south wall |
| | `Material` | Configuration | The material of the lintel, `Bricks` by default; "Add door" copies the wall's |
| | `IsOpen` | Configuration | Closed by default. `IsMovementBlocking` and `IsSoundBlocking` are `!IsOpen`, whatever the height |
| `Tree` | `Variant`, `Rotation`, `Scale` | Configuration, `[Range]` | 0 to 6, 0 to 359 degrees clockwise, 80 to 120 percent; defaults 0, 0, 100. A loaded value outside the range shows variant 0, the rotation modulo 360 or the nearest scale |
| | `Width`, `Depth`, `Height` | Computed | The trunk footprint and height of the variant times the scale; not configurable. Blocks movement, not sound |
| `GamePlayer` | `Facing` | Configuration | `GameDirection`, north by default |
| | `ClientId`, `ClientTitle`, `IsConnected` | State | `ClientId` is set from the dictionary key in `OnPlayersChanging` |
| | `IsMicrophoneOn`, `IsAudioOn` | State | Switched only by the player's own pages, not persisted, reset when the client disconnects |
| | `Width`, `Depth`, `Height` | Fixed | Always 2 x 2 x 7 (50 x 50 cm, 175 cm tall), `Size` and `BodyHeight`; a set value, also one from a loaded file, has no effect |
| | | | `IsMovementBlocking` and `IsVisible` follow `IsConnected` |

`GamePlayer` derives from `GameObject`, so one index covers players and objects and the renderer treats both alike. They still live in two collections because different parties manage them. The player's footprint is square, so turning never changes the tiles it covers. `GamePlayer` is `[ExcludeFromCreation]`.

`Objects` and `Tools` are typed as the abstract `GameObject` and `GameTool`, which relies on `ConfigurationJsonTypeInfoResolver` writing and reading `$type` for properties declared as an abstract `IConfigurable` class.

Persistence:

- The game writes its configuration through the first parent `IConfigurationWriter`. Without one it logs a warning and does not retry.
- `PlaceAsync`, `RemoveAsync`, `UpdateAsync` and `ReplaceAsync`, and with them building, planting, demolishing, adding doors, opening and closing, write immediately, outside the lock. Canceling their token only stops the write: the change stays applied and a later pass saves it.
- Joins, moves and turns only set `_isSaveRequested`; the next loop pass writes them in one go. `StopAsync` writes what is still pending and logs a warning when it is canceled.
- A write that fails or returns false logs a warning and sets the request again, so the next pass retries; the game keeps running.
- Anonymous players are written like any other but dropped by the first reconcile after a load, because their client no longer exists.

## Objects and extension points

The core (`Game*.cs`, `GameObject`, `GamePlayer`, `Visuals/`, `Tools/`, `World/`, `View/`, `Voice/`, `Components/`) names no object type. Every object type lives in a folder of `Objects/` with a namespace of its own and uses only public and protected members of the core, so a later split into libraries only moves files and changes the `$type` names in saved worlds. Reviews check this rule; members an object folder declares itself may be internal, such as `WallTextures` or `Tree.Variants`.

| Folder | Namespace | Types |
|---|---|---|
| `Objects/Buildings/` | `HomeBlaze.Game.Objects.Buildings` | `Wall`, `WallMaterial`, `WallTool`, `Door`, `WallTextures`; walls and doors share a folder because "Add door" creates doors |
| `Objects/Nature/` | `HomeBlaze.Game.Objects.Nature` | `Tree`, `TreeVariant`, `TreeTool` |

Moving `Wall` and `Door` into `Objects/Buildings` changed their `$type` from `HomeBlaze.Game.Wall` and `HomeBlaze.Game.Door` to `HomeBlaze.Game.Objects.Buildings.Wall` and `HomeBlaze.Game.Objects.Buildings.Door`. The loader has no rename mapping, so a world saved before needs those names replaced in its file.

Extension points:

| Member | Contract |
|---|---|
| `GameObject.Visual` | How the object looks, a `GameVisual`; by default a gray box (`GameVisual.DefaultColor`) filling the footprint and `Height` |
| `GameObject.GetGame()` | Protected: the game whose `Objects` holds the object, to call the object API from an operation |
| `GameTool` | An abstract configurable subject in `Game.Tools`, saved with `$type` like objects. It reaches its game through the protected `GetGame()`, which follows the subject graph rather than a constructor, and offers operations with a nullable `[FromServices] GameCaller` parameter, which the page lists for every player |
| `GameAssets.Url(type, path)` | `_content/<assembly of type>/<path>`, so the URL of an asset under `wwwroot/` follows its type into another library |
| `Game.ResolveActor(caller)` | The player of this game the caller acts as, or null |

Tool and operation parameters with a default are nullable, so an empty dialog field means the default, both in the dialog and over MCP. `[Range]` documents the limits; `MethodParameterDialog` does not enforce it, so the operations check again.

Visuals (`Visuals/`):

- `GameVisual` is an immutable record of `GameVisualPart`s that compares by value, parts included. `GameVisual.Box(width, height, depth, color)` creates one box.
- A part is a `Box` or a `Model` (`GameVisualShape`) with `Offset` and `Size` in tiles (`GameVisualVector`, relative to the south-west corner of the footprint at ground level; as a size, X is the width, Y the height and Z the depth), a `Pivot` (`GameVisualPoint`) and a `Rotation` around it in degrees clockwise seen from above, `Texture`, `TextureScale` (tiles per repeat horizontally and vertically) and `Color` (`#rrggbb`, which tints the texture) for boxes, `Model` for models, `Effect` (`None` or `Wind`) and `Animation` (the name of a model animation that loops). A model is scaled uniformly to the largest size whose bounding box fits in `Size`, with its origin at the horizontal center of the part's box and its lowest point at the box's bottom.
- An object computes its visual from configuration or state properties only, because the page observes exactly those, and caches it per state with `GameVisualCache<TKey>`, which returns the same instance for an unchanged key, so the tracker's comparison stops at the reference and allocates nothing.
- `GameViewTracker` validates a visual when it builds a view object (`GameVisualValidator`). More than 16 parts (`MaximumParts`), offsets, sizes or pivots outside -1024 to 1024 tiles (`MaximumExtent`), negative sizes, a rotation that is not finite, undefined enum values, a texture scale outside 0 (exclusive) to 1024, a color other than `#rrggbb`, an animation name over 64 characters, a model part without a model, and URLs that do not start with `_content/`, are longer than 512 characters or contain `..` or `%` make the object show as its default box. The server logs a warning once per object type and process, remembering the types in a `ConditionalWeakTable` so that an unloaded plugin type is not kept alive. The browser receives the parts with short property names (`GameViewPart`).

Object API on `Game`:

| Member | Behavior |
|---|---|
| `GetPlacementInFront(caller, across, along)` | Returns a `GamePlacement(X, Y, Width, Depth, Facing)` for an object `across` tiles wide across the facing direction and `along` tiles deep along it, starting on the tile right in front of the caller's player and centered on the player's center across the facing (an odd remainder goes east or north). Does not check whether the tiles are free. Refuses without a player ("Only a player in the game can place objects."), when facing diagonally ("Face north, east, south or west to place objects.") and beyond the `int` range ("Something is in the way.") |
| `PlaceAsync(caller, namePrefix, gameObject, cancellationToken)` | Adds the object under the prefix, a dash and 8 random hex digits. Refuses without a player, and with "Something is in the way." when the object blocks movement and a movement blocking object covers one of its tiles |
| `RemoveAsync(gameObject, cancellationToken)` | Removes an object of this game |
| `UpdateAsync(gameObject, change, cancellationToken)` | Runs `change` under `_lock` and updates the index. When the change throws, or the object then blocks movement and overlaps another movement blocking object, `GameObjectSnapshot` writes back the writable configuration and state properties that differ from before through the registry, and the call throws ("Something is in the way." for the overlap). The change may only set properties of the object and must not call into the game |
| `ReplaceAsync(gameObject, replacements, cancellationToken)` | Removes the object and adds the replacements under new names in one step under `_lock`, so no page sees an intermediate state. Refuses, changing nothing, when a movement blocking replacement overlaps a movement blocking object other than the replaced one or another replacement, and when a replacement is listed twice or already part of the game |

All five take `_lock` themselves; the four that change the world save right away outside it, and canceling only stops the write, so the change stays applied and a later pass saves it. Refusals throw `InvalidOperationException` with a message for the player, which the error dialog shows; an object that is not part of this game is refused with "The object is not part of this game.". Invalid arguments, such as an empty name prefix or a size outside 1 to 1024, throw `ArgumentException`.

Objects:

- `Wall`: one box of its material's texture, bricks 8, concrete 10 and wooden planks 6 tiles per repeat (2 m, 2.5 m and 1.5 m); a `Material` outside the enum looks like bricks. `IsSoundBlocking` is `Height >= 8` (the private `SoundBlockingHeight`, 2 m, above head height).
- "Add door" replaces a wall one tile thick by a wall, a door and a wall, named `wall-` and `door-` plus 8 random hex digits, all with the wall's height and material. The door is centered on the caller's player along the wall, with the extra tile of an odd width to the east or north as in `GetPlacementInFront`, or on the wall's center without a player (subject page, MCP), and moved inward so that one wall tile (`DoorMargin`) remains on each side. It is disabled through `KnownAttributes.IsEnabled` while the wall is thicker than one tile or shorter than 4 tiles (`MinimumDoorWallLength`). It refuses a width outside 2 to 8, a wall thicker than one tile ("Only a wall one tile thick can take a door."), a width that does not fit ("This wall is too short for a door of N tiles; it fits up to M."), a wall that reaches past the `int` range and pieces that would overlap another object ("Something is in the way."). Several doors per wall come naturally, because the remaining pieces are ordinary walls.
- `Door`: a lintel of its `Material` above the leaf height of 8.4 tiles (2.1 m, `LeafHeight`), left out when the door is not taller, and a wooden leaf 0.3 tiles thick with its pivot at the hinge, at the west end of a door running east to west or the south end of one running north to south, turned by -90 degrees while open, so it opens toward the north or the west.
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

## Clients and players

The game reads clients through the internal `IGameClientDirectory` (list clients with id, title, named and connected; send a notification; rename a client; recognize property changes that concern clients). `InstallationClientDirectory` finds the `[SingleInstance]` `HomeBlazeInstallation` in the registry and caches it while the registry still knows it; renaming calls `HomeBlazeClient.RenameAsync`, which names an anonymous client and saves the installation's client configuration. Game unit tests use `FakeClientDirectory` because the installation's own client reconciling is internal to it; `InstallationClientDirectoryTests` and the E2E tests cover the real one.

`ReconcilePlayers` rules:

- A client that is connected or named has a player; a new player starts at 0,0. A player whose client is gone, or is unnamed and disconnected, is removed. `Players` is replaced only when the set changes.
- `ClientTitle` and `IsConnected` mirror the client. When a client connects, its player joins at the nearest position to where it stands at which its whole footprint is free, found ring by ring by `SpawnFinder` up to 256 tiles away.
- An offline named player keeps its saved position but is neither visible nor blocking, so nobody walks into an invisible obstacle.
- Renaming a client keeps its id, so its player keeps its position. Forgetting a client turns its tabs into new anonymous clients, so the old player is removed and new ones spawn. All tabs of a named client control the same player.
- Without an installation, `GetClients` returns null and the players stay as they are.

A player changes the name of its own client through the internal `Game.RenameAsync(caller, name, cancellationToken)`, which the page calls from the pencil button of its own Nearby entry. It is not an operation, so MCP, agents and other clients cannot rename a client through the game. It refuses a caller that is not a player of this game ("Only a player in the game can change their name.") and a name that is empty or longer than `MaximumNameLength` (50) after trimming, passes the trimmed name to the directory and signals a reconcile, through which the player takes the new title. `Game.GetClientName(caller)` gives the name the dialog starts from, null while the client is anonymous.

The loop in `ExecuteAsync` reconciles, flushes a pending save and then waits for either the signal or `HousekeepingInterval` (5 s). The signal is a `SemaphoreSlim(0, 1)` released by `SignalReconcile` on client changes, outside replacements of `Players` and `ApplyConfigurationAsync`. Exactly one pending wait is kept across passes: a new wait per pass would leave the old one queued to swallow the next signal.

## Movement and interaction

Moving is not an operation. The page calls the internal `Game.TryMove(player, direction, turnOnly)` once per step and passes only the player of its own circuit's client, so MCP, agents and other clients cannot move a player. Rules, all under `_lock`:

- The player must be the instance in this game's `Players`, and the direction defined.
- A token bucket per player (`RateLimiter`, burst `StepBurst` of 3, one token per `StepInterval` of 180 ms) limits steps, a second one limits turns. A refused take changes nothing.
- Every accepted attempt sets `Facing`, also when the step is blocked. A save is requested only when the position or facing changed.
- A step moves the footprint one tile and is refused when any tile it would cover holds another blocking object.
- A diagonal step is refused when both of its orthogonal steps would be refused, so players cannot squeeze between corner-touching blockers.
- A step that would leave the `int` range is refused (`Footprint.TryMove` uses `long` arithmetic).

The target area (`TargetArea`) is the footprint shifted one tile toward the facing, minus the footprint itself: 2 tiles straight ahead, 3 tiles around the corner when facing diagonally. `GetTargetObjects` returns the visible objects there, ordered from south-west to north-east.

Operations are the actions, those of the objects and those of the tools in `Game.Tools`; the game itself has none. Those that need to know who acts take `[FromServices] GameCaller? caller`; the page invokes through `GameCallerServiceProvider`, which returns its `GameCaller` and delegates every other service to the circuit. `Game.ResolveActor` is the single place that turns a caller into a player of this game and returns null otherwise, also for a player of another game. The subject page, MCP and agents pass no caller.

| Operation | Actor | Behavior |
|---|---|---|
| `WallTool.BuildWallAsync` ("Build wall") | Required | `length` 1 to 40 (4 when empty), `height` 1 to 20 (10), `material` (`Bricks`). Builds a wall one tile thick right in front of the actor, across the facing and centered on it (`GetPlacementInFront(caller, length, 1)`, `PlaceAsync`), so a length of 40 reaches about 20 tiles to each side; named `wall-` plus 8 random hex digits. Refuses parameters out of range, diagonal facing and blocked tiles |
| `TreeTool.PlantTreeAsync` ("Plant tree") | Required | `variant` 0 to 6, random when empty. Plants a tree with a random rotation and a random scale of 80 to 120 percent, its trunk footprint right in front of and centered on the actor; named `tree-` plus 8 random hex digits |
| `Wall.DemolishAsync`, `Tree.DemolishAsync` | None | `Game.RemoveAsync` |
| `Wall.AddDoorAsync` ("Add door") | Optional | `width` 2 to 8 (4 when empty). Replaces the wall by a wall, a door and a wall through `Game.ReplaceAsync`; see [Objects and extension points](#objects-and-extension-points) |
| `Door.OpenAsync`, `Door.CloseAsync` | None | `Game.UpdateAsync`. Refuses the current state, and refuses closing with "Something is in the way." while another movement blocking object covers a door tile |
| `GamePlayer.SendMessageAsync` | Optional | Notification "Message from {sender}" to the player's client, "Message" without actor. At most 500 characters after trimming; one message per second per sending player |

The panel offers every operation except those excluded from the user interface and those disabled through their `KnownAttributes.IsEnabled` attribute (`Door.Open_IsEnabled`, `Door.Close_IsEnabled`, `Wall.AddDoor_IsEnabled`). Disabled operations are left out rather than shown disabled, so the number keys only reach operations that apply. Invoking goes through `OperationInvoker.InvokeAsync`, so parameter, confirmation, error and result dialogs behave as in the subject browser. Before invoking, the page checks that the object is still in the target area; while a dialog is open the browser does not move the player. The page remembers the parameters entered per operation, keyed by object type and operation (`{type}.{method}`), so "Add door" remembers its width across walls, for as long as the page lives. It prefills the next dialog of that operation through `initialValues` of `OperationInvoker.InvokeAsync`, which reports the entered values through `parametersEntered`; only parameters entered in one field are prefilled.

## Voice

Players talk to each other with their microphones. Here a talker is a player whose microphone is on, and the speaker is the toggle that plays what a player hears. The server decides who hears whom and mixes the audio for every listener in C#; the browser only captures and plays it with its native audio APIs.

Voice needs a secure context (HTTPS or `localhost`), because browsers offer `getUserMedia` and `AudioWorklet` nowhere else. On plain HTTP both buttons are disabled with the tooltip "Voice needs HTTPS". For manual testing from other devices on the LAN, start the app with an HTTPS URL as well, for example `--urls "http://0.0.0.0:5280;https://0.0.0.0:5281"`. Kestrel then serves HTTPS with the ASP.NET development certificate, which is issued for `localhost`, so each browser shows a certificate warning once.

Hearing range (`World/HearingRange`, the only definition of who hears whom and how loud):

- Sound travels along the shortest open path between the footprint centers. Players whose centers are `HearingRadius` (40 tiles, 10 m) or more apart never hear each other.
- The volume is the distance gain of the path length times the occlusion gain of the detour, the length by which the path exceeds the straight distance. The distance gain is 1 up to `ReferenceDistance` (4 tiles, 1 m) and `ReferenceDistance / length` beyond, so 2 m give 0.5 and 5 m give 0.2, times a smoothstep fade from 1 to 0 over the last `EdgeFade` (8 tiles, 2 m) before the radius. The occlusion gain is `1 / (1 + detour / HalvingDetour)`, so a detour of `HalvingDetour` (4 tiles, 1 m) halves the volume. The four constants live in `HearingRange`.
- Sound follows `GameObject.IsSoundBlocking`, not the movement rule `IsMovementBlocking`: `GameWorldIndex.IsSoundBlocked` blocks a tile that an object with `IsSoundBlocking` covers. Walls of at least 8 tiles (2 m) and closed doors block sound; lower walls, open doors, players, trees and objects that only block movement do not. The index stores the flag in its entry, so opening or closing a door increases its `Version` and rebuilds the volume table. Visibility does not matter, and the height matters only through an object's own rule, such as the wall's. A player in a room whose walls and doors are all closed is silent outside it.
- While the straight line between the centers touches no blocking tile, the path is that line and its exact length counts, with no detour. The walk takes every tile that the closed segment touches, also only at an edge or a corner, so the line slips neither between two walls that touch at a corner nor along the face of a wall. A line between two players at the same `Y` runs on the grid line between two rows and touches both. Tiles inside either player's footprint are skipped. The walk computes in half tiles, where every footprint center is a whole number, with `long` arithmetic, so it is exact, symmetric and safe near the `int` limits.
- Otherwise `World/SoundPathSearch` finds the path. It runs Dijkstra between the centers of the tiles within the radius plus 2 tiles around the source footprint, 86 by 86 tiles for a player. A tile is open while no object with `IsSoundBlocking` covers it. Steps go to the 16 tiles at the offsets (1, 0), (1, 1) and (2, 1) in every orientation, with integer lengths in thousandths of a tile, and need every tile that the segment between the two centers passes or touches to be open: a straight step only its target, a diagonal step also both tiles beside its corner, so it never cuts through a joint where two walls meet at a corner, and a step to (2, 1) also the tiles (1, 0) and (1, 1), since it passes through no corner point. The path keeps half a tile from a wall instead of only not touching it, so unlike the line it may run beside the face of a wall, and a gap of one tile passes sound although players, 2 tiles wide, cannot walk through it.
- The search starts at every tile of the source footprint with its grid length from the footprint center, and a target's length is the shortest over its footprint tiles of the length there plus the grid length to its center. The tiles of the source footprint count as open even where a sound blocking object overlaps them, like the line walk skips them, and a target tile that is not open still gets the length of a step into it but continues no path. So that both orders agree also there, the search always runs from the player with the ordinally smaller client id.
- A grid path exceeds the straight length by up to 2.75 % depending on its direction. The detour is therefore the grid length of the path minus the grid length without walls between the two centers by the same rule (`SoundPathSearch.GetOpenLength`, through the tiles nearest to each center, which is exact for players), which cancels most of that excess, so moving a player across the boundary of the line of sight changes the volume only slightly. A detour that bends only within the freedom the grid directions leave counts as none.
- Both orders give the same volume: the line walk visits the same tiles, and the search sums integer lengths and runs from a fixed end. Paths longer than the radius plus 2 tiles count as none, which cannot hide an audible path, because the grid excess within the radius stays below 1.1 tiles. A footprint whose width or depth is outside 1 to `GameObject.MaximumSize` gets no path, and `GameVolumeTable.Build` gives a player of a size that the index leaves out no row.
- The search reads the footprints of the sound blocking objects of the area once from the index (`GameWorldIndex.QuerySoundBlocking`) into a bitmap, not one index lookup per tile. Its buffers live in a `SoundPathSearch` that the game reuses under `_lock`, so a rebuild allocates nothing in it in steady state. The tile range is clamped to the `int` range, as `GetArea` clamps the area.

Audio and transport:

- 16 kHz, mono, signed 16-bit little-endian PCM in frames of 20 ms (320 samples, 640 bytes), about 256 kbit/s each way per active player.
- Frames travel over the Blazor connection: the browser calls `ReceiveMicrophoneFrame(byte[])` of the page, the page calls `play(viewId, frame)`. Blazor transfers both as binary, and the circuit identifies the player, so no extra endpoint exists.
- `GameVoiceCodec` in C# and `encodeFrame`/`decodeFrame` in `game-voice.js` are the only places that know the encoding, so Opus can replace it without touching the mixer, the range rule or the UI.

Toggles: only the player's own pages switch `IsMicrophoneOn` and `IsAudioOn`, through the internal `Game.SetMicrophone(caller, pageId, isOn)` and `Game.SetAudio(caller, pageId, isOn)`. Like `TryMove` they are not operations, so MCP, agents and other clients can read but not change them. Both take `_lock`, accept only a player of this game, and refuse turning on while it is disconnected; turning off always works, so a closing page removes its share. The page id is the page's view id.

- The game keeps per player, not intercepted, `MicrophonePageId`, the page that turned the microphone on last and the only one whose frames count, and `AudioPageIds`, the pages with the speaker on. Turning off removes only the calling page's share. `IsMicrophoneOn` and `IsAudioOn` follow them.
- A disconnect resets both and drops the player's queued frames.
- `_voicePlayers` lists the instances with a toggle on. When a player leaves, or an outside assignment of `Players` (configuration editor, reload) replaces its instance, the next reconcile turns the voice of the orphaned instance off in `ReleaseVoiceOfReplacedPlayers` and hands the mixer the microphone page of the instance that took its place.

Volume table (`Voice/GameVolumeTable`, `Game.Volume.cs`):

- An immutable snapshot with a row for every connected player, regardless of its toggles, listing the players it hears and their volume above 0. Nearby uses it too.
- `GameVolumeTable.Build` compares only the players within `HearingRange.GetArea`, which the chunk index finds, and computes each pair once, from the player with the ordinally smaller client id, adding it to both rows. That player goes first, so its blocked pairs share one search, which runs only when the straight line of one of them is blocked. A rebuild therefore costs at most one bounded search per player, at most every `VolumeTableInterval` (100 ms): about 0.07 ms per search and 1.5 ms per rebuild for 10 players near each other in a walled area, measured with an optimized build. `GameVolumeTable.Create` builds a table from given rows; tests use it as a factory.
- `UpdateVolumeTable`, called only by the voice loop, first checks without the lock that `VolumeTableInterval` passed since the last rebuild, so a stale index is not rebuilt on every tick. The gate only limits rebuilds: once the interval passed without a change, every tick of the idle voice loop still takes `_lock` to compare the version, which is uncontended and short. Under `_lock` it then rebuilds when the index `Version` changed or `_isVolumeTableDirty` is set. The version increases with every change of the index, which covers moves, joins, leaves and changed objects, also from outside through the lazy index rebuild. Toggles, resets and released players set the flag although rows ignore toggles, so that `VolumeTableChanged` tells the pages about them.
- It publishes the table through a `volatile` field and then raises `VolumeTableChanged` outside the lock.

Mixer (`Voice/GameVoiceMixer`, one per game, with its own lock):

- `SetMicrophonePage(playerId, pageId)`, called by the game under `_lock`, creates the player's talker, clears its queue when the page changes, or removes it for none.
- `Receive(playerId, pageId, frame)` drops a frame that does not have exactly 640 bytes, does not come from the player's microphone page, or exceeds `FrameRateTolerance` times the frame rate (a `RateLimiter` with a burst of `ServerQueueMaximum`). Otherwise it decodes the samples into the talker's `TalkerQueue`, a pooled ring buffer, so steady speech allocates nothing per incoming frame.
- A talker queue plays once it holds its start size, grows the start size by `BufferGrowth` up to `ServerQueueMaximum` when it runs dry while playing, and shrinks it by one frame after `BufferShrinkDelay` of playing without running dry. Every dequeue notes how many frames it found, and at the end of each `TrimWindow` of playing the queue drops the oldest frames down to its start size when the fewest it found exceeded the start size by more than `TrimHeadroom`. Bursts and gaps therefore pass without a drop, because a burst raises the level only until the following gap consumes it, while delay that stays for a whole window, also after the start size shrank, is dropped. A frame arriving while the queue holds `ServerQueueMaximum` drops the oldest. `Trims` counts the drops of either kind and `Underruns` the times the queue ran dry while playing.
- `RegisterSink(playerId, sink)` adds a page with the speaker on and returns an `IDisposable` that removes it; a player can have several. `IGameVoiceSink.Send` returns without waiting for the browser, never changes the frame, which the listener's sinks share, and must not throw when a tick that already took the sinks calls it once more after its registration was disposed.
- `Mix(table)` takes one frame per playing talker into pooled buffers and copies the list of listeners under the mixer lock. Outside it, it sums for every listener with sinks the frames of the talkers in its table row, scaled by their volume and never its own, into a reused `int` buffer, clamps to 16 bits into a new `byte[]` and hands it to each of the listener's sinks. A listener that hears no frame in a tick gets nothing. A failing sink is logged as a warning the first time and at debug level after that.
- Volumes glide instead of jumping, so a table rebuild does not click. The mixer keeps per listener the volume of each talker in the tick it last mixed it, in dictionaries it reuses, and scales each frame by a gain that runs linearly from the previous volume to the new one, reaching the new one exactly at the last sample. A pair new in the row, or one after a tick in which no talker had a frame, glides up from 0. A pair that left the row glides down to 0 over one more frame of the talker if the talker has one in that tick, and is dropped otherwise; listeners without sinks are dropped too. Mixing therefore allocates nothing in steady state apart from the one `byte[]` per listener frame.
- The voice loop (`RunVoiceLoopAsync`) runs beside the housekeeping loop in `ExecuteAsync`, ticks every `FrameDuration` on a `PeriodicTimer` of the game's `TimeProvider`, and stops with the housekeeping loop, also when that one ends on an unexpected exception. `TickVoice` updates the volume table and mixes in separate `try` blocks, so a failing rebuild or `VolumeTableChanged` handler does not skip the mix. A failing tick logs a warning when ticks start failing and stays at debug level while they keep failing, so a persistent fault does not flood the log at the frame rate.

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
| `VolumeTableInterval` | 100 ms | Shortest time between two rebuilds of the volume table |
| `MaximumPendingSends` | 10 | Sends to a page in flight beyond which new frames to it are dropped |
| `FrameRateTolerance` | 1.5 | Allowed incoming frame rate as a multiple of the nominal rate |
| `PlayTimeout` | 1 s | Time after which a frame sent to a page counts as lost and no longer as pending |
| `CaptureStartTimeout` | 2 min | Time a page waits for the browser to start its microphone, including the permission prompt |
| `BrowserCallTimeout` | 10 s | Time a page waits for any other voice call to the browser |

The page passes the browser's values to `game-voice.js` when it starts, so JavaScript holds no copy. Expected delay from mouth to ear, estimated from these values and not yet measured: about 100 to 130 ms on the LAN and 150 to 220 ms over the internet, plus whatever Bluetooth headsets add.

Page (`GamePage.razor`):

- The **Voice** section next to Position holds a microphone button (`Mic`/`MicOff`) and a speaker button (`VolumeUp`/`VolumeOff`), filled with the primary color while on and toggled by M and N too. Their accessible names stay "Microphone" and "Speaker", with `aria-pressed` for the state and `aria-keyshortcuts` for the key; the tooltips say "Turn the microphone on (M)" and so on. Until `game-voice.js` answered both are disabled, and without a player a button stays enabled only to turn off what is still on. While a toggle runs, further clicks and keys are ignored, but the button stays enabled so it keeps the focus.
- Turning the microphone on starts capturing first, waiting up to `CaptureStartTimeout`, and calls `SetMicrophone` only once that succeeded. Turning the speaker on starts playback, registers the page's sink and calls `SetAudio`. When the game refuses, because the player is disconnected, the page stops the browser side again. Every other browser call waits at most `BrowserCallTimeout`. A start that the browser did not answer in time is followed by a stop, so a late success there does not keep capturing or playing.
- The sink counts sends in flight, drops frames while `MaximumPendingSends` are pending, and hands each frame to the circuit's dispatcher with `InvokeAsync`, because JS interop with byte arrays is not documented as thread-safe. Each `play` call times out after `PlayTimeout`, so a call that a lost connection never answers releases its slot. A failing send is logged at debug level.
- `VolumeTableChanged` schedules a flush, which refreshes Nearby and, through `Game.GetVoicePages`, stops the page's capture or playback when another page of the player took the microphone over or a disconnect reset the toggles. A change that arrives while a toggle runs is handled by a flush after it. The flush keeps doing this after the view broke, so a stale sink does not keep playing.
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

## Threading invariants

`_lock` guards `Players`, `Objects`, the positions of their objects and the states that `UpdateAsync` changes, the index, `_pendingPlayers`, `_pendingObjects`, `_isSaveRequested`, the per-player rate limiters and message timestamps, the per-player microphone page and speaker pages, `_voicePlayers`, and the volume table's dirty flag, index version and scratch list. Every game write that two players could race on (move, join, place, remove, update, replace) happens under it, so two players never take one tile.

The property change observer (`Game.OnPropertyChanged`, and the page's handler) runs synchronously inside the write that caused it, on the writer's thread, while the change observable holds its own synchronization gate. A write under `_lock` waits for that gate. If an observer took `_lock`, an outside write delivering its change (holding the gate, waiting for `_lock`) and a game write (holding `_lock`, waiting for the gate) would deadlock. So observers never take `_lock` and never block: they call `SignalReconcile` or `RequestIndexRebuild`, and the page only schedules a flush. Apart from client changes, `OnPropertyChanged` ignores writes made on a thread that holds `_lock`, because the game keeps the index up to date for its own writes.

The index is rebuilt lazily. `RequestIndexRebuild` sets `_isIndexStale` with `Volatile.Write`, without the lock. `GetIndex`, called under the lock, clears the flag with `Interlocked.Exchange` before rebuilding from `Players` and `Objects`, so a request that arrives during a rebuild causes another one on the next use. A rebuild is requested by outside changes (a replaced `Players` or `Objects`, or any property change of an object of this game on a thread that does not hold `_lock`, because the core knows no object type and cannot tell which properties its footprint and blocking follow), by an applied configuration of the game or an object, and by reconciling when the player set changes. The game's other changes update the index incrementally. Two blocking objects on one tile, possible through hand edits, simply keep it blocked. An object with a width, depth or height outside 1 to 1024, possible in a loaded file that skips validation, is left out of the index, so it neither blocks nor renders.

Outside replacements of `Players` or `Objects` (configuration editor, reload) happen without `_lock`. `OnPlayersChanging` and `OnObjectsChanging` run in the generated setter before the write reaches the interceptor chain, so they may take `_lock`; when the writing thread does not hold it, they record the new value as pending. `CurrentPlayers` and `CurrentObjects` return the pending value when there is one, and every copy-on-write of the game starts from them and clears the pending value through `ReplacePlayers` and `ReplaceObjects`. This keeps a game write that runs between an outside write's hook and its landing from discarding the outside value.

The page reads some state without the lock:

- `Game.Players` and its dictionaries: they are copy-on-write and never mutated after assignment, so reading the current reference is safe.
- Single properties of objects, such as a target's title for the panel: one intercepted read of a property is never torn.
- `GameViewTracker.Known`: the tracker is only updated by `Game.UpdateView`, which the page calls from its own renderer dispatcher, and read on that same dispatcher right after, so no other thread touches it.

Everything else the page needs (`TryMove`, `GetTargetObjects`, `UpdateView`, `SetMicrophone`, `SetAudio`, `GetVoicePages`) takes the lock itself.

Voice adds these rules:

- The volume table is built only by the voice loop under `_lock` and published through a `volatile` field, so the mixer and the pages read it without the lock. The time of the last rebuild is written under `_lock` with `Volatile.Write` and read before the lock.
- The mixer's own lock guards its talker queues, microphone pages and sinks. The lock order is `_lock` before the mixer lock: the game tells the mixer about microphone pages while holding `_lock`, and the mixer never calls into the game.
- Sinks run on the voice loop outside both locks, and `VolumeTableChanged` handlers run there outside `_lock`. Neither may block, so the page only hands frames to its dispatcher and schedules a flush.
- Change observers keep not taking `_lock`.

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

Voice has its own module, `game-voice.js`; see Voice.

Flushing: the page subscribes to property changes of the game and of every `GameObject`. The handler only sets a flag and starts a 50 ms delay, after which `FlushAsync` runs on the renderer dispatcher, so updates go out in batches of at most about 20 per second. A flush asks `Game.UpdateView` for the diff of the visible objects within `ViewRadius` (200 tiles, 50 m) of the own player, or of the origin without one, found through the chunk index. A `JSException` from `applyUpdate` marks the view broken and stops the subscription; later flushes skip the view but keep the voice and the actions in step, although Position and the toggle icons of Nearby may stay stale. `VolumeTableChanged` schedules a flush too, which rebuilds Nearby from the new table.

`GameViewTracker` keeps one `GameViewObject` per known object with an id unique within the page. It recreates a view object only when a sent field changed and returns a shared empty update when nothing changed, so an unchanged view allocates nothing. Player colors derive from the client id with FNV-1a and are cached per client id. Other objects carry their colors and textures in their visual, which the tracker compares by value after a reference check, and send the parts of the validated visual; players carry no visual, because the page draws them itself. View objects have two kinds, `player` and `object`.

Prediction and reconciliation (`game-input.js`): each step carries an increasing sequence number rather than key down and up, so a delayed message never makes a player walk further. The browser applies the step at once with `predictStep`, which mirrors `Game.TryGetStep` against the blocking objects it knows (not the rate limit). When the answer arrives, it drops the answered steps, starts again from the server's position and replays the steps still pending. A server update of the own player is taken only while no step is pending. When the own player leaves the view or stops being the own player, `reset` drops the prediction and pending steps, and a generation counter discards answers to steps sent before.

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
- `Wind` parts use a clone of each of their materials, cached per material, with a material plugin that moves each vertex sideways by the square of its height above its mesh's origin times `WIND_STRENGTH`, swaying at `WIND_SPEED` and gusting at `GUST_SPEED` with a phase from the mesh's world position, so neighboring trees do not sway in step. The phases wrap on the CPU, so a 32-bit float keeps them precise after hours. `Animation` plays the named animation group of a model in a loop.
- Every object but a player fades to 30 % visibility (`FADED_VISIBILITY`) while it hides the own player's head, at 80 % of the player's height, from the camera. Every 100 ms (`SIGHTLINE_CHECK_MS`) the segment from the camera to the head is tested exactly against the triangles of each mesh whose bounding box it crosses; the boxes only preselect, because the bounds of a trunk with leaning branches can cross the segment where no triangle does. An object stays faded for 300 ms (`FADE_HOLD_MS`) after its last hit, so it does not blink while the camera follows a step, and its visibility glides over 200 ms (`FADE_MS`). An object is tested only after the scene rendered once since it was added, rebuilt or placed, because until then its meshes keep the bounds of the origin, next to the own player.
- A glTF material has a fixed transparency mode, in which Babylon ignores the mesh visibility for blending, so a faded mesh shows a blending copy of its material instead, created on first use and drawn with a depth pre-pass; the shared materials stay untouched, so fading one object never changes another.
- Players are instances of the glTF character, scaled to their `Height`, rotated to their facing, with the shirt tinted and a billboard label.
- The frame rate shows at the top left of the view in the element marked `data-game-fps`, which `game.js` updates every 500 ms.
- For tests and diagnosis the canvas carries `data-game-parts` (parts of the objects in view), `data-game-models` (model parts showing their model), `data-game-model-failures` (model parts showing the fallback box) and `data-game-faded` (objects faded now).

Side panel (240 px, below the canvas under the `Md` breakpoint): **Position** with the pad, **Voice** (microphone and speaker buttons, errors below the top of the panel), **Actions** (operations of the `Game`, then of every tool ordered by its name in `Tools`, disabled without a player; they come first, so they keep their place while targets come and go), **Looking at** (only while targets exist, actions numbered 1 to 9 across all targets) and **Nearby** (the own player first, then the players in its row of the volume table by title, each with a crossed-out microphone or speaker while that one is off, dimmed while the own speaker is off). The own entry has a pencil button, "Change my name", which opens `GameNameDialog` with the current name, or an empty field while the client is anonymous; Save, or Enter, renames the client, and a failure shows in the error dialog of failed actions. A new table with the same players keeps the list, because their toggles and titles reach it through the view.

## Assets and licenses

Everything is vendored under `wwwroot/`, served by `MapStaticAssets` (compressed and fingerprinted at build time). Each folder has a `LICENSE.md` with source and terms, which lists every file of the folder. The assets of an object type live under `objects/<folder>/`, apart from those of the core, so they move with the type into another library.

| Folder | Asset | License |
|---|---|---|
| `lib/babylonjs/` | Babylon.js 9.31.0 core and glTF loader (UMD builds) | Apache 2.0 |
| `models/` | `Casual_2.glb` from the Ultimate Modular Men pack by Quaternius, only `Idle_Neutral` and `Walk` kept | CC0 |
| `textures/` | ambientCG Grass004 (`grass.jpg`), re-encoded | CC0 |
| `objects/buildings/textures/` | ambientCG Bricks097 (`bricks.jpg`, stacked to a square), Concrete046 (`concrete.jpg`) and Planks021 (`wood.jpg`), re-encoded to 512 x 512 | CC0 |
| `objects/nature/models/` | Seven trees of the low poly nature pack by flo-bit, unchanged GLB files from a pinned commit | CC0 |

To swap the character, replace the GLB and its license, then update `game-entities.js`: the file name in `LoadAssetContainerAsync`, `IDLE_ANIMATION`, `WALKING_ANIMATION`, `SHIRT_MATERIAL` (the material tinted with the player color) and `WALKING_SPEED_RATIO` so the feet keep pace with one tile per step. The model is scaled from its bounding box to the player's `Height` and must face north (+z) after the loader's handedness conversion.

## Testing

```
dotnet test src/HomeBlaze/HomeBlaze.Game.Tests --filter "Category!=Integration"   # unit tests
dotnet test src/HomeBlaze/HomeBlaze.Game.Tests --filter "Category=Integration"    # E2E tests
```

The E2E tests need Playwright's Chromium (`playwright.ps1 install chromium` from the test output folder). Logic lives in plain classes that unit tests cover; rendering is covered end to end, and bUnit is not used.

| Helper | Purpose |
|---|---|
| `TestRoot` | A root subject that holds the game, and optionally an installation, and counts configuration writes; `FailsToWrite` simulates failures |
| `FakeClientDirectory` | Thread-safe clients, renames and recorded notifications; `RemoveInstallation` simulates a missing installation |
| `GameTestContext` | A game in a `TestRoot` with a `FakeTimeProvider` and fake clients; adds connected players, walls, doors, other objects and tools |
| `MovementBlocker` | An object that blocks movement but lets sound through, like a plant or a chair |
| `VisualObject`, `WarnedVisualObject` | Objects whose visual a test sets; the second is a type of its own for the warning that is logged once per type |
| `RecordingLogger` | A thread-safe logger that records its entries |
| `RepositoryPaths` | Finds repository files, such as the demo world and the tree models, from the test output |
| `FrozenTimeProvider` | A stopped clock that runs a callback inside its next reading, which lets a test act while the game holds its lock |
| `VoiceFrames` | Builds and reads frames of 16-bit little-endian samples |
| `RecordingVoiceSink` | A thread-safe sink that records the frames the mixer sends |

The E2E tests use `PlaywrightFixture` from `HomeBlaze.E2E.Tests` through their own `GameE2ECollection`, because xUnit only finds collection definitions in the test assembly. They assert the side panel, not canvas pixels; headless Chromium renders WebGL in software. Every installation sees all circuits of the shared server, and circuits of closed browser contexts stay connected for minutes, so players of earlier tests may still stand in a new game. The tests therefore read where their own player joined, move the other players far aside, and assert positions relative to the join point.

The voice E2E tests launch their own Chromium with `--use-fake-ui-for-media-stream --use-fake-device-for-media-stream`, so the microphone is a test tone that needs no permission dialog; the test server at `127.0.0.1` is a secure context. They read the frame counters on the panel and prove that frames stop by waiting for the talker to send 50 more while the listener's count grows by at most `MaximumPendingSends`. Headless Chromium renders audio in callbacks of 4096 samples (about 85 ms), so the listener underruns often, but the window keeps it from trimming: the test allows at most one trim within the first 100 frames, for standing delay that a stall of the test machine can leave.

## Known limits and follow-ups

- The door panel swing is visual only: blocking uses the door's footprint, and the swung panel can pass through walls or players.
- Fading tests only the segment from the camera to the head, so a lintel or a crown can still cover the body while the head is visible. The test uses the meshes without the sway of the wind shader, so a swaying crown fades a little early or late.
- The walking character is wider than its 2 x 2 tile footprint, so arms and legs can reach into neighboring walls.
- An outside replacement of `Objects` that lands after a game write that started from its pending value wins, discarding that game change (for example a wall built in that window). The same race on `Players` heals on the next reconcile.
- Without an installation the page keeps showing "Joining the game…" rather than saying it needs one.
- Only one character model.
- A tree blocks only the tiles of its trunk; its crown passes through walls and players.
- The footprint of a tree follows its variant and scale from its south-west corner, so changing `Variant` or `Scale` on the subject page grows or shrinks it toward the north-east rather than around the trunk, and nothing checks that the new tiles are free.
- "Add door" reads the wall's geometry before the game takes its lock, so a wall edited from outside in between is replaced by pieces of its former geometry.
- `UpdateAsync` restores only the configuration and state properties that differ from the snapshot, so a loaded value that validation rejects, such as a size out of range, does not make the restore throw. A refused change that set such a property itself still does: the restore writes the invalid value back, which throws and hides the exception that refused the change.
- Remembered dialog parameters live as long as the page; a reload starts with empty fields.
- A page receives every visible object within `ViewRadius` (200 tiles in each direction) of its player, although the camera shows only about 60 tiles around it and the fog hides the rest. With many objects, either lower `ViewRadius` or let the page report the area its camera covers and send that area plus a margin.
- Scale: the cost per page, movement and hearing depend on what is near a player, not on the size of the world, and coordinates cover the whole `int` range. These parts grow with the whole world instead, which is fine for several thousand objects and a few dozen players:
  - All objects live in the `Game` subject and every save rewrites the whole `Game.json`. Much larger worlds need regions in separate files or a database.
  - A rebuild of the index, after an outside change, an applied configuration or a joining or leaving player, re-adds every object under `_lock`. An index that updates incrementally would avoid it.
  - Every page subscribes to changes of all objects, so every step anywhere schedules a flush on every page; each flush only diffs its own area. Change notifications per region would keep distant changes away.
  - Every box part is its own mesh and every model part one mesh per model node, so thousands of walls and trees within the view radius mean thousands of draw calls. Thin instances of identical meshes would batch them.
  - Fading tests every object in view each 100 ms, the bounding box of each mesh first; a spatial lookup along the sightline would keep it cheap in dense forests.
- `MaximumPendingSends` of 10 drops frames to a page once its round trip exceeds about 200 ms, because each send waits for the browser to acknowledge it.
- Echo: the browser's echo canceller works on the microphone, but the playback goes through an `AudioWorklet`, which Chrome's echo canceller may not cover, so without headphones a listener's speaker may reach its own microphone. This is not yet tested with real devices. If confirmed, route the playback through a local loopback `RTCPeerConnection` pair into an `<audio>` element. Headphones avoid it.
- A stop that a connection drop loses after a toggle can leave the browser capturing while the server shows the microphone off. The mixer drops those frames, because the game no longer counts that page, and the next toggle recovers.

Voice follow-ups:

- Opus compression (Concentus on the server, WebCodecs in the browser) to cut the bandwidth about tenfold.
- Walls that muffle instead of block.
- Stereo panning by the talker's position relative to the listener.
- A speaking indicator in the 3D scene, which needs audio levels from the server.
- Extracting the voice handling of `GamePage.razor`, about a third of the component, into its own class if more voice UI lands.

Agent players are the planned follow-up. `ResolveActor` and `GameCaller` already isolate who acts, so operations keep their signatures. It needs:

1. `HomeBlaze.AI` to expose the calling agent to operations generically, for example as a `[FromServices]` type defined there. `McpMethodInvoker` currently invokes with the global service provider.
2. Players bound to an agent subject instead of a client, always persisted and visible while the agent runs.
3. Perception and movement for agents, such as a `LookAround(radius)` query and a `MoveAsync(direction, steps)` operation.
4. Triggers for agent turns: being looked at, receiving a message, or a timer.
