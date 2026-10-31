# Game Voice Design

Players of `HomeBlaze.Game` talk to each other with their microphones. A player hears the players within 10 m in line of sight. The server mixes the audio individually for every listener in C#; the browser only captures and plays it with its native audio APIs.

This document is a working spec for the pull request. Before the pull request is ready, its content moves into `src/HomeBlaze/HomeBlaze.Game/README.md` and the file is deleted.

## Goals

- A microphone button turns the own microphone on and off; a speaker button turns hearing the others on and off. Both are independent and start off on every page load.
- A player hears another player when their centers are less than 10 m (40 tiles) apart and the straight line between them crosses no wall and no closed door. The volume fades linearly from full up close to silence at 10 m.
- The Nearby list in the panel shows the own player and exactly the players that the same rule makes audible.
- All audio processing that decides who hears whom, and how loud, happens on the server in C#.
- Timings live in one C# class so they can be tuned in code.

## Non-goals

These are follow-ups and are listed in the README once the feature is implemented:

- Opus compression (Concentus on the server, WebCodecs in the browser) to cut the bandwidth about tenfold.
- Sound travelling around corners and through openings by path length instead of line of sight.
- Walls that muffle instead of block.
- Stereo panning by the speaker's position relative to the listener.
- A speaking indicator in the 3D scene, which needs audio levels from the server.

## Prerequisite: secure context

`getUserMedia` and `AudioWorklet` exist only in secure contexts (HTTPS or `localhost`). The app is started for manual testing on `https://192.168.1.201:5281` with the ASP.NET development certificate in addition to `http://192.168.1.201:5280`. This changes how the app is started, not the code. On an insecure page both buttons are disabled with the tooltip "Voice needs HTTPS".

## Audio format and transport

- 16 kHz, mono, signed 16-bit little-endian PCM in frames of 20 ms (320 samples, 640 bytes). About 256 kbit/s each way per active player.
- Frames travel over the existing Blazor Server connection: the browser calls a `[JSInvokable]` method of the page with a `byte[]`, the page calls a JavaScript function with a `byte[]`. Blazor transfers both as binary. The connection already identifies the client and therefore the player, and no extra endpoint is needed.
- The encoding is one small seam on each side (`GameVoiceCodec` in C#, `encodeFrame`/`decodeFrame` in JavaScript), so that Opus can replace it without touching the mixer, the range rule or the UI.

## Settings

`Voice/GameVoiceSettings` is a static class of constants and the one place for every timing:

| Constant | Initial value | Meaning |
|---|---|---|
| `SampleRate` | 16000 | Samples per second of every frame |
| `FrameDuration` | 20 ms | Length of one frame; 320 samples |
| `ServerQueueStart` | 1 frame | Frames a speaker queue needs before it plays |
| `ServerQueueMaximum` | 6 frames | Frames beyond which the oldest are dropped |
| `PlaybackBufferStart` | 40 ms | Audio the browser buffers before it plays |
| `PlaybackBufferMaximum` | 120 ms | Largest buffer the browser grows to |
| `BufferGrowth` | 1 frame | Growth of a buffer after it ran dry |
| `BufferShrinkDelay` | 5 s | Time without running dry before a buffer shrinks by one frame |
| `VolumeTableInterval` | 100 ms | Shortest time between two rebuilds of the volume table |
| `MaximumPendingSends` | 10 | Sends to a page in flight beyond which new frames to it are dropped |
| `FrameRateTolerance` | 1.5 | Allowed incoming frame rate as a multiple of the nominal rate |

The page passes the browser-side values to `game-voice.js` when it starts, so JavaScript holds no copy. Expected delay from mouth to ear with these values: about 100 to 130 ms on the LAN and 150 to 220 ms over the internet, plus whatever Bluetooth headsets add.

## Components

### Hearing range

`World/HearingRange` is static and pure, and it is the only definition of who hears whom.

- `HearingRadius = 40` tiles (10 m).
- `GetVolume(GamePlayer first, GamePlayer second, GameWorldIndex index)` returns a value from 0 to 1. It computes the distance between the footprint centers as `double`; at a distance of `HearingRadius` or more it returns 0. Otherwise it walks the tiles that the segment between the centers touches (a supercover traversal, which includes both tiles when the segment passes exactly through a corner) and returns 0 when any of them is blocked for sound. Otherwise it returns `1 - distance / HearingRadius`.
- The walk always starts at the player with the smaller id, so `GetVolume(a, b)` equals `GetVolume(b, a)`.
- Tiles inside either player's footprint are skipped.
- A tile is blocked for sound when `GameWorldIndex.IsSoundBlocked(x, y)` finds a blocking object there that is not a `GamePlayer`. Walls and closed doors block; open doors and players do not. Wall height does not matter, so the 4-tile railing blocks like a full wall.
- Coordinates are computed in `long`/`double` so footprints near the `int` limits are safe.

### Player state

`GamePlayer` gets two `[State]` properties, `IsMicrophoneOn` and `IsAudioOn`. They are not saved, appear in `GameViewObject`, and are reset to `false` when the player disconnects. Only the player's own pages set them, through `Game.SetMicrophone(GameCaller caller, int pageId, bool isOn)` and `Game.SetAudio(GameCaller caller, int pageId, bool isOn)`. These are internal methods like `Step`, not operations, so MCP, agents and other clients can read but not change them. Both take `_lock` and check that the caller owns the player. The game keeps, per player, the microphone page (the page that switched the microphone on last) and the set of pages with the speaker on. `IsMicrophoneOn` is true while there is a microphone page, `IsAudioOn` while the set is not empty. A change marks the volume table dirty, and a change of the microphone page is passed on to the mixer.

### Volume table

- An immutable snapshot `GameVolumeTable`: for each connected player a row with the players audible to them and their volume above 0. Every connected player has a row regardless of the toggles, because Nearby uses it too.
- The game loop rebuilds it under `_lock` when a dirty flag is set and at least `VolumeTableInterval` passed since the last rebuild. Moves, joins, leaves, toggle changes and changes to walls and doors set the flag.
- It is published through a `volatile` field; the mixer and pages only read it. `Game` raises `VolumeTableChanged` after publishing, which pages use to refresh Nearby.
- Rows only contain pairs within `HearingRadius`, found through the chunk index, so the rebuild never compares all players with all players.

### Mixer

`Voice/GameVoiceMixer`, one per game, owns its own lock and never takes `_lock`.

- `Receive(string playerId, int pageId, ReadOnlySpan<byte> frame)` drops the frame when it does not have exactly 640 bytes, when the page is not the player's current microphone page, or when the player exceeds `FrameRateTolerance` times the frame rate (a token bucket like the step rate limit). Otherwise it copies the samples into the speaker's queue, a pooled ring buffer, so steady speech allocates nothing per incoming frame.
- The game tells the mixer the current microphone page of a player through `SetMicrophonePage(string playerId, int? pageId)`; frames from the player's other pages are dropped.
- Each speaker queue is adaptive: it plays once it holds its current start size, grows the start size by `BufferGrowth` up to `ServerQueueMaximum` when it runs dry while playing, shrinks it by one frame after `BufferShrinkDelay` without running dry, and drops the oldest frames beyond `ServerQueueMaximum`.
- `RegisterSink(string playerId, IGameVoiceSink sink)` returns an `IDisposable` that unregisters it. A sink is one page of the player with the speaker on; a player can have several.
- The tick runs every `FrameDuration` on a `PeriodicTimer` in its own loop of the existing runtime background service. It pops one frame per playing speaker under the mixer lock, then mixes outside it: for every listener with registered sinks it sums, into a pooled `int` buffer, the frames of the speakers in its row of the volume table, scaled by their volume and never including the listener itself, and clamps the sum to 16 bits. When anything was audible it encodes a new `byte[]` and hands it to each of the listener's sinks. A new array is required because the send runs asynchronously. Silence is not sent.
- An exception in a tick is logged and the next tick runs; voice never stops the game loop.
- When a player disconnects, the game clears the player's queue and microphone page.

### Page

- Two buttons in the panel's top row next to Position: microphone (`mic` / `mic_off`) and speaker (`volume_up` / `volume_off`), filled with the primary color while on. Keys `M` and `N` toggle them; they are ignored while typing in an input.
- Turning the microphone on asks `game-voice.js` to start capturing; only after that succeeds does the page call `Game.SetMicrophone`. Turning the speaker on registers the page's sink with the mixer and calls `Game.SetAudio`.
- `[JSInvokable] ReceiveMicrophoneFrame(byte[] frame)` passes the frame to the mixer with the page's id.
- The page's sink sends frames with `InvokeVoidAsync("play", viewId, frame)` without awaiting them in the tick. It counts sends in flight and drops new frames while `MaximumPendingSends` are pending. A failing send is logged at debug level and skipped.
- Disposing the page stops capturing, unregisters the sink and calls `SetMicrophone` and `SetAudio` with `false` for the page, which removes only this page's share.
- Nearby lists the own player and the players in the own row of the volume table, sorted as today. Each entry shows a crossed-out microphone when that player's microphone is off and a crossed-out speaker when their audio is off. With the own speaker off, the audible players are dimmed.
- Error messages appear under the buttons in the existing panel message style: "Microphone access was denied", "No microphone was found", and "Tap the speaker to resume" when the browser suspended the audio context.

### Browser

`wwwroot/game-voice.js` with two `AudioWorklet` processors:

- The `AudioContext` is created on the first click of either button, because browsers start audio only from a user action. A suspended context is resumed on the next click.
- Capture: `getUserMedia` with `echoCancellation`, `noiseSuppression` and `autoGainControl`; the capture processor resamples to 16 kHz, converts to 16-bit and posts a frame every 320 samples; the main thread passes it to the page.
- Playback: the playback processor resamples frames to the context rate and plays from a buffer that starts at `PlaybackBufferStart`, grows by one frame up to `PlaybackBufferMaximum` when it runs dry, shrinks after `BufferShrinkDelay` without running dry, and plays silence while empty.
- Per view id, like `game.js`, so several pages of one tab stay separate.
- For tests, the page element exposes `data-voice-received-frames`, the number of frames played.

## Threading

The existing invariants of the README hold. In addition:

- The volume table is built only by the game loop under `_lock` and read lock-free.
- The mixer lock guards the queues, microphone pages and sinks. The lock order is `_lock` before the mixer lock: the game may call the mixer while holding `_lock`, and the mixer never calls into the game.
- Change observers keep not taking `_lock`.

## Testing

Unit tests (`HomeBlaze.Game.Tests`, xUnit, `FakeTimeProvider`):

- `HearingRange`: volume at 0, 20 and just below 40 tiles, nothing at 40 and beyond, a wall blocks, a closed door blocks, an open door passes, another player does not block, symmetry for both orders, a diagonal past a wall corner, coordinates near the `int` limits.
- Mixer: sum and clamping, no own voice, nothing for a listener without sinks or for a speaker whose microphone is off, the adaptive queue starting, growing, trimming and shrinking, wrong sizes and too frequent frames dropped, only the latest microphone page counts, an exception in a sink does not stop the tick.
- Game: toggles change the table, only the owner switches them, disconnecting resets them, moving behind a wall or closing a door removes the pair from the table and from Nearby.

E2E (Playwright Chromium, launched with `--use-fake-ui-for-media-stream --use-fake-device-for-media-stream`, so the microphone is a test tone and no permission dialog appears; `localhost` is secure):

- Two pages turn on microphone and speaker; the listener's `data-voice-received-frames` rises and Nearby shows the other player.
- One page moves out of range; the counter stops rising and Nearby drops the player.
