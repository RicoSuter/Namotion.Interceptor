# HomeBlaze.Game

Maintainer documentation for the game library: how it is built, which invariants hold it together and what is still open.

## Overview

A `Game` subject is a multiplayer world that HomeBlaze shows as a page. Every browser client of the installation is a player on an infinite grid of 25 cm tiles. Players walk in 8 directions, look at the tiles in front of them and invoke the operations of the objects standing there, such as building and demolishing walls, opening doors and sending messages. The page renders the world with Babylon.js as a tilted perspective scene centered on the own player. Players within 10 m in line of sight talk to each other with their microphones; the server mixes the audio for every listener. Several `Game` subjects may exist; each is an independent world with its own players.

| Project | Contents |
|---|---|
| `HomeBlaze.Game` (Razor SDK) | Subjects, the page component, the JS modules and the vendored assets under `wwwroot/` |
| `HomeBlaze.Game.Tests` | Unit tests and E2E tests |

The app registers the library with a project reference in `HomeBlaze.csproj` and `.AddAssembly(typeof(HomeBlaze.Game.Game).Assembly)` in `Program.cs`. `Data/Files/Demo/Game.json` of the app holds a demo world with a house of walls and door openings, served at `/pages/Demo/Game`.

The library depends on `HomeBlaze.Abstractions`, `HomeBlaze.Components` (`OperationInvoker`), `HomeBlaze.Services` (`CurrentClientConnection`, the configuration serializer), `HomeBlaze.Installation` (clients), `HomeBlaze.Storage.Abstractions` (`IConfigurationWriter`) and the Namotion.Interceptor generator, tracking and registry libraries. Dependency rule: `HomeBlaze.Game` may reference `HomeBlaze.AI` for agent players; `HomeBlaze.AI` never references or mentions the game.

| Folder | Contents |
|---|---|
| `Game*.cs` | The `Game` subject, split by concern: `Game.cs` (properties, copy-on-write helpers), `Game.Runtime.cs` (loops, change observer), `Game.Index.cs`, `Game.Players.cs`, `Game.Movement.cs`, `Game.Actions.cs`, `Game.Persistence.cs`, `Game.View.cs`, `Game.Volume.cs` (volume table), `Game.Voice.cs` (toggles, voice loop) |
| `GameObject.cs`, `Wall.cs`, `Door.cs`, `GamePlayer.cs` | The objects on the grid |
| `Clients/` | `IGameClientDirectory` and its installation implementation |
| `World/` | Pure grid logic: `Footprint`, `GameWorldIndex`, `HearingRange`, `SoundPathSearch`, `RateLimiter`, `SpawnFinder`, `TargetArea`, `WallPlacement` |
| `View/` | `GameViewTracker` and the objects and updates sent to the browser |
| `Voice/` | `GameVoiceSettings`, `GameVoiceCodec`, `GameVolumeTable`, `TalkerQueue`, `GameVoiceMixer`, `IGameVoiceSink` |
| `Components/` | `GamePage.razor` and `GameCallerServiceProvider` |
| `wwwroot/` | `game.js` (lifecycle, Babylon loader, camera), `game-input.js` (keyboard, pad, prediction), `game-entities.js` (meshes, tweens, ground, target marker), `game-voice.js` (audio context, capture, playback, codec seam), `game-voice-worklet.js` (capture and playback processors), `lib/`, `models/`, `textures/` |

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
| | `IsSoundBlocking` | Virtual, not persisted | Whether the object stops sound, `false` by default, so an object that blocks movement, such as a plant or a chair, lets sound through; see the hearing range under [Voice](#voice) |
| `Wall` | | | Defaults 4 x 1 x 10 tiles (1 m long, 25 cm thick, 2.5 m tall). `IsSoundBlocking => true` |
| `Door` | `IsOpen` | Configuration | Defaults 4 x 1 x 10, closed; use 1 x 4 in a north-south wall. `IsMovementBlocking` and `IsSoundBlocking` are `!IsOpen` |
| `GamePlayer` | `Facing` | Configuration | `GameDirection`, north by default |
| | `ClientId`, `ClientTitle`, `IsConnected` | State | `ClientId` is set from the dictionary key in `OnPlayersChanging` |
| | `IsMicrophoneOn`, `IsAudioOn` | State | Switched only by the player's own pages, not persisted, reset when the client disconnects |
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
- A token bucket per player (`RateLimiter`, burst `StepBurst` of 3, one token per `StepInterval` of 180 ms) limits steps, a second one limits turns. A refused take changes nothing.
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

## Voice

Players talk to each other with their microphones. Here a talker is a player whose microphone is on, and the speaker is the toggle that plays what a player hears. The server decides who hears whom and mixes the audio for every listener in C#; the browser only captures and plays it with its native audio APIs.

Voice needs a secure context (HTTPS or `localhost`), because browsers offer `getUserMedia` and `AudioWorklet` nowhere else. On plain HTTP both buttons are disabled with the tooltip "Voice needs HTTPS". For manual testing from other devices on the LAN, start the app with an HTTPS URL as well, for example `--urls "http://0.0.0.0:5280;https://0.0.0.0:5281"`. Kestrel then serves HTTPS with the ASP.NET development certificate, which is issued for `localhost`, so each browser shows a certificate warning once.

Hearing range (`World/HearingRange`, the only definition of who hears whom and how loud):

- Sound travels along the shortest open path between the footprint centers. Players whose centers are `HearingRadius` (40 tiles, 10 m) or more apart never hear each other.
- The volume is the distance gain of the path length times the occlusion gain of the detour, the length by which the path exceeds the straight distance. The distance gain is 1 up to `ReferenceDistance` (4 tiles, 1 m) and `ReferenceDistance / length` beyond, so 2 m give 0.5 and 5 m give 0.2, times a smoothstep fade from 1 to 0 over the last `EdgeFade` (8 tiles, 2 m) before the radius. The occlusion gain is `1 / (1 + detour / HalvingDetour)`, so a detour of `HalvingDetour` (4 tiles, 1 m) halves the volume. The four constants live in `HearingRange`.
- Sound follows `GameObject.IsSoundBlocking`, not the movement rule `IsMovementBlocking`: `GameWorldIndex.IsSoundBlocked` blocks a tile that an object with `IsSoundBlocking` covers. Walls and closed doors block sound; open doors, players and objects that only block movement do not. The index stores the flag in its entry, so opening or closing a door increases its `Version` and rebuilds the volume table. Visibility does not matter, and the height does not either, so a low wall blocks like a full one. A player in a room whose walls and doors are all closed is silent outside it.
- While the straight line between the centers touches no blocking tile, the path is that line and its exact length counts, with no detour. The walk takes every tile that the closed segment touches, also only at an edge or a corner, so the line slips neither between two walls that touch at a corner nor along the face of a wall. A line between two players at the same `Y` runs on the grid line between two rows and touches both. Tiles inside either player's footprint are skipped. The walk computes in half tiles, where every footprint center is a whole number, with `long` arithmetic, so it is exact, symmetric and safe near the `int` limits.
- Otherwise `World/SoundPathSearch` finds the path. It runs Dijkstra over the corners of the tiles within the radius plus 2 tiles around the source footprint, 87 by 87 corners for a player. A corner is open while none of its four tiles blocks sound, so a path keeps the same rule as the line: it never touches a blocking tile, cuts no corner and slips through no joint where two walls meet at a corner. Steps go to the 16 corners at the offsets (1, 0), (1, 1) and (2, 1) in every orientation, with integer lengths in thousandths of a tile; every tile such a step touches belongs to one of its two corners, so a step needs no other check. The search starts at the open corners of the source footprint with their grid length from its center and ends at the open corners of the target footprint the same way.
- A grid path exceeds the straight length by up to 2.75 % depending on its direction. The detour is therefore the grid length of the path minus the grid length without walls between the two centers, which cancels most of that excess, so moving a player across the boundary of the line of sight changes the volume only slightly. A detour that bends only within the freedom the grid directions leave counts as none.
- Both orders give the same volume: the line walk visits the same tiles, and the search sums integer lengths over an undirected graph with the same start and end rule at both footprints. Paths longer than the radius plus 2 tiles count as none, which cannot hide an audible path, because the grid excess within the radius stays below 1.1 tiles.
- The search reads the footprints of the sound blocking objects of the area once from the index (`GameWorldIndex.QuerySoundBlocking`) into a bitmap, not one index lookup per tile. Its buffers live in a `SoundPathSearch` that the game reuses under `_lock`, so a rebuild allocates nothing in it in steady state. The vertex range is clamped to the corners of the `int` range, as `GetArea` clamps the area.

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
- `GameVolumeTable.Build` compares only the players within `HearingRange.GetArea`, which the chunk index finds, and computes each pair once, from the player with the ordinally smaller client id, adding it to both rows. That player goes first, so its blocked pairs share one search, which runs only when the straight line of one of them is blocked. A rebuild therefore costs at most one bounded search per player, at most every `VolumeTableInterval` (100 ms): about 0.05 ms per search and 1 to 2 ms per rebuild for 10 players near each other in a walled area, measured with an optimized build. `GameVolumeTable.Create` builds a table from given rows; tests use it as a factory.
- `UpdateVolumeTable`, called only by the voice loop, first checks without the lock that `VolumeTableInterval` passed since the last rebuild, so a stale index is not rebuilt on every tick. The gate only limits rebuilds: once the interval passed without a change, every tick of the idle voice loop still takes `_lock` to compare the version, which is uncontended and short. Under `_lock` it then rebuilds when the index `Version` changed or `_isVolumeTableDirty` is set. The version increases with every change of the index, which covers moves, joins, leaves, walls and doors, also from outside through the lazy index rebuild. Toggles, resets and released players set the flag although rows ignore toggles, so that `VolumeTableChanged` tells the pages about them.
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

`_lock` guards `Players`, `Objects`, the positions and door states of their objects, the index, `_pendingPlayers`, `_pendingObjects`, `_isSaveRequested`, the per-player rate limiters and message timestamps, the per-player microphone page and speaker pages, `_voicePlayers`, and the volume table's dirty flag, index version and scratch list. Every game write that two players could race on (move, join, build, demolish, open, close) happens under it, so two players never take one tile.

The property change observer (`Game.OnPropertyChanged`, and the page's handler) runs synchronously inside the write that caused it, on the writer's thread, while the change observable holds its own synchronization gate. A write under `_lock` waits for that gate. If an observer took `_lock`, an outside write delivering its change (holding the gate, waiting for `_lock`) and a game write (holding `_lock`, waiting for the gate) would deadlock. So observers never take `_lock` and never block: they call `SignalReconcile` or `RequestIndexRebuild`, and the page only schedules a flush. Apart from client changes, `OnPropertyChanged` ignores writes made on a thread that holds `_lock`, because the game keeps the index up to date for its own writes.

The index is rebuilt lazily. `RequestIndexRebuild` sets `_isIndexStale` with `Volatile.Write`, without the lock. `GetIndex`, called under the lock, clears the flag with `Interlocked.Exchange` before rebuilding from `Players` and `Objects`, so a request that arrives during a rebuild causes another one on the next use. A rebuild is requested by outside changes (a replaced `Players` or `Objects`, a changed `X`, `Y`, `Width`, `Depth`, `IsConnected` or `IsOpen` of an object of this game), by an applied configuration of the game or an object, and by reconciling when the player set changes. The game's other changes update the index incrementally. Two blocking objects on one tile, possible through hand edits, simply keep it blocked. An object with a width, depth or height outside 1 to 1024, possible in a loaded file that skips validation, is left out of the index, so it neither blocks nor renders.

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

`GameViewTracker` keeps one `GameViewObject` per known object with an id unique within the page. It recreates a view object only when a sent field changed and returns a shared empty update when nothing changed, so an unchanged view allocates nothing. Player colors derive from the client id with FNV-1a and are cached per client id; walls and doors have fixed colors.

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
- Walls are individual boxes with per-face UVs scaled to their size (one brick repeat per 8 x 4 tiles), not thin instances, so the texture keeps its size on every wall length.
- Doors are a brick lintel from 8.4 tiles up to the full height and a wooden panel 8.4 tiles tall and 0.3 thick, hinged at the west end of an east-west opening or the south end of a north-south one. Opening swings it a quarter turn over 300 ms.
- Walls and doors that the segment from the camera to the own player's head crosses fade to 30 % visibility.
- Players are instances of the glTF character, scaled to their `Height`, rotated to their facing, with the shirt tinted and a billboard label.

Side panel (240 px, below the canvas under the `Md` breakpoint): **Position** with the pad, **Voice** (microphone and speaker buttons, errors below the top of the panel), **Looking at** (only while targets exist, actions numbered 1 to 9 across all targets), **Actions** (operations of the `Game`, disabled without a player) and **Nearby** (the own player first, then the players in its row of the volume table by title, each with a crossed-out microphone or speaker while that one is off, dimmed while the own speaker is off). A new table with the same players keeps the list, because their toggles and titles reach it through the view.

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
| `VoiceFrames` | Builds and reads frames of 16-bit little-endian samples |
| `RecordingVoiceSink` | A thread-safe sink that records the frames the mixer sends |

The E2E tests use `PlaywrightFixture` from `HomeBlaze.E2E.Tests` through their own `GameE2ECollection`, because xUnit only finds collection definitions in the test assembly. They assert the side panel, not canvas pixels; headless Chromium renders WebGL in software. Every installation sees all circuits of the shared server, and circuits of closed browser contexts stay connected for minutes, so players of earlier tests may still stand in a new game. The tests therefore read where their own player joined, move the other players far aside, and assert positions relative to the join point.

The voice E2E tests launch their own Chromium with `--use-fake-ui-for-media-stream --use-fake-device-for-media-stream`, so the microphone is a test tone that needs no permission dialog; the test server at `127.0.0.1` is a secure context. They read the frame counters on the panel and prove that frames stop by waiting for the talker to send 50 more while the listener's count grows by at most `MaximumPendingSends`. Headless Chromium renders audio in callbacks of 4096 samples (about 85 ms), so the listener underruns often, but the window keeps it from trimming: the test allows at most one trim within the first 100 frames, for standing delay that a stall of the test machine can leave.

## Known limits and follow-ups

- The door panel swing is visual only: blocking uses the door's footprint, and the swung panel can pass through walls or players.
- The lintel can still cover the player in some camera positions; fading tests only the segment from the camera to the head.
- The walking character is wider than its 2 x 2 tile footprint, so arms and legs can reach into neighboring walls.
- An outside replacement of `Objects` that lands after a game write that started from its pending value wins, discarding that game change (for example a wall built in that window). The same race on `Players` heals on the next reconcile.
- Without an installation the page keeps showing "Joining the game…" rather than saying it needs one.
- Only one character model, and only walls and doors as object types.
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
