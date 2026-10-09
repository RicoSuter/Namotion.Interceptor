# Sonos Migration Design (HomeBlaze v1 to v2)

Working spec for migrating `HomeBlaze.Sonos` (v1, `../HomeBlaze/src/HomeBlaze.Sonos`) to `Namotion.Devices.Sonos` (v2). It is removed before the pull request is finalized, as with earlier working specs.

## Goals

- Every v1 capability is present in v2 (see the checklist at the end).
- State flows through `[InterceptorSubject]` partial properties, with no manual change detection.
- Updates arrive through UPnP events with a slow reconciliation poll, without referencing ASP.NET Core.
- Subjects model the Sonos household as it is: visible room players, their bonded satellites, and zone groups.
- New capabilities found on the live household and in the Sonos API are added where agreed below.

## Live household used for design

Queried read-only from 192.168.1.101, .110, .116, .121 (and .137 via topology):

- 10 units in 4 rooms: Wohnzimmer (Arc, 2 Era 300 surrounds, Sub), Büro (Ray, 2 Ones as rear surrounds, Sub), Küche (Era 100), Terasse (Roam, battery powered).
- All on S2 software 97.1 (display version 18.8).
- Satellites expose no AVTransport service. `GetZoneGroupState` lists them as `<Satellite Invisible="1">` with `HTSatChanMapSet` channel roles (`SW`, `LR`, `RR`, ...).
- The Roam reports `MoreInfo="RawBattPct:100,BattPct:100,BattChg:CHARGING,BattTmp:23"` in topology.
- Spotify Connect (`x-sonos-vli:...,spotify:...`) returns `TrackMetaData=NOT_IMPLEMENTED` from `GetPositionInfo`; its metadata only arrives through AVTransport events.
- TV input appears as `x-sonos-htastream:<uuid>:spdif`.
- `GetEQ` for `NightMode` and `DialogLevel` succeeds on soundbars and fails with UPnP error 402 on the Era 100 and Roam.
- Sonos Favorites (`Browse FV:2`): "Discover Sonos Radio", "Radio FM1", "SRF 3", "Trending in Germany".

## Projects and dependencies

| Project | Content |
|---|---|
| `src/HomeBlaze/Namotion.Devices.Sonos/` | Subjects, event listener, parsers, `SonosServiceCollectionExtensions`. No UI. |
| `src/HomeBlaze/Namotion.Devices.Sonos.HomeBlaze/` | Blazor components (Razor SDK, references device project, `HomeBlaze.Components.Abstractions`, MudBlazor). |
| `src/HomeBlaze/Namotion.Devices.Sonos.Tests/` | Unit tests. |

Packages pinned in `src/Directory.Packages.props`:

- `Sonos.Base` 0.4.0 (was 0.2.0-beta0038). Used for SOAP commands and queries only. Its methods no longer carry the `Async` suffix (`Play`, `GetTransportInfo`, ...).
- `Rssdp` 5.0.0 (was 4.0.4). Used for SSDP discovery when no seed is known.
- `Sonos.Base.Events.Http` is not used. It references `Microsoft.AspNetCore.App` and starts a second Kestrel server; v2 has its own listener instead.

Known AOT gap: `Sonos.Base` parses SOAP responses with `XmlSerializer`. Our own parsers use `XmlReader` only. Documented in `Sonos.md`.

The device project references `Namotion.Interceptor`, the generator (as analyzer), `Namotion.Interceptor.Registry`, `Namotion.Interceptor.Hosting`, `HomeBlaze.Abstractions`, plus `Microsoft.Extensions.Hosting.Abstractions`, `Microsoft.Extensions.Http` and `Microsoft.Extensions.Logging.Abstractions`. `InternalsVisibleTo` only for the test project.

## Abstraction changes (`HomeBlaze.Abstractions/Media`)

No type in the repository implements or reads these interfaces yet, so the change is free.

`IAudioPlayerState` keeps playback state only:

```csharp
[State(Position = 140)] bool? IsPlaying { get; }
[State(Position = 141)] bool? IsMuted { get; }
// Volume via IVolumeState (145)
```

`CurrentTrack`, `CurrentPosition` and `Duration` move to a new interface:

```csharp
[SubjectAbstraction]
[Description("Reports the currently playing media track.")]
public interface IMediaTrackState
{
    [State(Position = 146)] string? CurrentTrackTitle { get; }
    [State(Position = 147)] string? CurrentTrackArtist { get; }
    [State(Position = 148)] string? CurrentTrackAlbum { get; }
    [State(Position = 149)] string? CurrentTrackImageUri { get; }
    [State(Position = 150)] string? CurrentTrackUri { get; }
    [State(Position = 151)] TimeSpan? CurrentTrackPosition { get; }
    [State(Position = 152)] TimeSpan? CurrentTrackDuration { get; }
}
```

`SeekAsync` stays on `IAudioPlayerController`. `IAudioPlayer` does not inherit `IMediaTrackState`; Sonos players and groups implement both.

## Interface mapping

| v1 | v2 |
|---|---|
| `IAudioPlayer` (int volume 0 to 100) | `IAudioPlayer` (`decimal? Volume` 0..1, `SetVolumeAsync(decimal)`) |
| `IsAudioPlaying`, `IsAudioMuted`, `AudioVolume` | `IsPlaying`, `IsMuted`, `Volume` |
| `CurrentAudioTrackTitle` | `CurrentTrackTitle` |
| `CurrentAudioTrackCreator`, `Album`, `ImageUri`, `Uri` | `IMediaTrackState` |
| none | `CurrentTrackPosition`, `CurrentTrackDuration`, `SeekAsync` |
| `INetworkAdapter` (IP only) | `INetworkAdapter` (IP, MAC, `IsWireless`) |
| none | `IDeviceInfo`, `ISoftwareState`, `IConnectionState`, `IBatteryState` |
| `IThing`, `IIconProvider` | `ITitleProvider`, `IIconProvider` |
| `SonosSystem : IVirtualThing` | `SonosSystem : IHubDevice, IMonitoredService, IConnectionState` |
| none | `SonosGroup : IAudioPlayer, IMediaTrackState, IVirtualSubject` |

## Subject hierarchy

```
SonosSystem                                         hosted BackgroundService
├── Players:  Dictionary<uuid, SonosPlayer>         visible room players
│     └── Satellites: Dictionary<uuid, SonosSatellite>
└── Groups:   Dictionary<coordinatorUuid, SonosGroup>
      └── Members: SonosPlayer[]                    references into Players
```

Dictionary keys are RINCON UUIDs (`RINCON_38420BD1533001400`). Groups are keyed by coordinator UUID, not by the Sonos group ID (`RINCON_...:1010349259`), which changes on every regroup. Every visible player belongs to exactly one group, so an ungrouped room is a single-member group, as in the Sonos app.

### SonosSystem

`[InterceptorSubject]`, `BackgroundService`, `IConfigurable`, `IHubDevice`, `IMonitoredService`, `IConnectionState`, `ITitleProvider`, `IIconProvider`, `ILastUpdatedProvider`. Constructor injects `IHttpClientFactory` and `ILogger<SonosSystem>`.

| Property | Kind | Notes |
|---|---|---|
| `SeedHost` | `[Configuration]` `string?` | Any speaker IP. Empty uses known IPs, then SSDP. |
| `EventCallbackHost` | `[Configuration]` `string?` | Address speakers send events to. Empty auto-detects. |
| `EventPort` | `[Configuration]` `int` | Default 6329 (same as v1). |
| `PollingInterval` | `[Configuration]` `TimeSpan` | Default 30 s. Non-positive falls back to default. |
| `RetryInterval` | `[Configuration]` `TimeSpan` | Default 30 s. |
| `Players`, `Groups` | `[State]` dictionaries | `internal set`, replaced only when membership changes. |
| `Favorites` | `[State]` `string[]` | Favorite names, refreshed each poll. |
| `AreEventsActive` | `[State]` `bool` | False when the listener could not start or no subscription succeeded. |
| `IsConnected`, `Status`, `StatusMessage`, `LastUpdated` | `[State]` | `internal set`. |
| `Title`, `IconName`, `IconColor` | `[Derived]` | "Sonos System". |

### SonosDevice (base for players and satellites)

`IDeviceInfo`, `INetworkAdapter`, `ISoftwareState`, `IConnectionState`, `ITitleProvider`, `IIconProvider`.

- `Uuid`, `RoomName`, `Model` ("Sonos Arc"), `ProductCode` ("S19"), `Manufacturer` ("Sonos"), `SerialNumber`, `HardwareRevision`.
- `IpAddress`, `MacAddress`, `IsWireless` (topology `WirelessMode`/`EthLink`), `SubnetMask`/`Gateway`/`SignalStrength` null.
- `SoftwareVersion` (display version, "18.8"), `AvailableSoftwareUpdate` null.
- `IsConnected` from topology membership. `StatusMessage` carries the last per-device failure.
- `Title` derived as "Model (Room)", matching v1.
- `Update(...)` methods apply topology, device description and zone info without recreating the subject.

### SonosPlayer : SonosDevice

`IAudioPlayer`, `IMediaTrackState`, `IBatteryState`.

- Playback: `TransportState` (enum `SonosTransportState`: Stopped, Playing, Paused, Transitioning), derived `IsPlaying`, `IsMuted`, `Volume`.
- Track: all `IMediaTrackState` properties. `CurrentTrackImageUri` is made absolute (`http://<ip>:1400/getaa?...`).
- `Source` derived from the transport URI (enum `SonosSource`: None, Tv, LineIn, SpotifyConnect, AirPlay, Radio, Queue, Other).
- Play mode: `Shuffle` (`bool?`), `Repeat` (enum `SonosRepeatMode`: Off, All, One), parsed from `PlayMode`. `SleepTimerRemaining` (`TimeSpan?`).
- Sound: `Bass`, `Treble` (`int?`, -10..10), `Loudness`, `NightMode`, `SpeechEnhancement` (`bool?`, null when unsupported).
- Grouping: `GroupCoordinatorUuid`, derived `IsGroupCoordinator`. No object reference to the group, to avoid a cycle.
- Battery: `BatteryLevel` (0..1), `IsCharging`, both null without a battery.
- Capabilities (internal, from the device description): home theater (HTControl), AudioIn.
- `Satellites`.

### SonosSatellite : SonosDevice

- `Role` (enum `SonosSatelliteRole`: Subwoofer, RearLeft, RearRight, StereoPartner, Other), from the channel map of the owning player.

### SonosGroup

`IAudioPlayer`, `IMediaTrackState`, `IVirtualSubject`, `ITitleProvider`, `IIconProvider`.

- `GroupId` (current Sonos group ID, informational), `Coordinator` (`SonosPlayer`), `Members` (`SonosPlayer[]`).
- `Title` derived from member room names, for example "Wohnzimmer + Küche".
- `Volume`, `IsMuted` from GroupRenderingControl.
- Playback and track properties `[Derived]` from `Coordinator`.

### Rules

- All state properties are `partial` with `internal set`; only configuration is public. All initialized in constructors.
- Subjects are updated in place and never recreated in the poll loop.
- Collections are replaced, never mutated, and only when membership changed (reference comparison, as `HueBridge.DictionaryEquals`).
- A player or satellite missing from topology stays in its dictionary with `IsConnected = false` until restart, so automation paths stay valid.

## Connectivity

### Main loop (`SonosSystem.ExecuteAsync`)

Follows `HueBridge`: a configuration change signals a restart of the loop; stopping sets `Status = Stopped`.

1. Resolve a seed: `SeedHost`, then known player IPs, then SSDP `urn:schemas-upnp-org:device:ZonePlayer:1` on all up IPv4 interfaces (5 s), taking the first responder whose manufacturer contains "Sonos".
2. Start `SonosEventListener` on `EventPort`. If binding fails, log a warning, set `AreEventsActive = false`, continue with polling only.
3. Every `PollingInterval`, reconcile:
   - Topology: `GetZoneGroupState` on the seed, parsed by our `ZoneGroupStateParser`. Create or update players, satellites, groups; mark missing ones offline; parse battery from `MoreInfo`.
   - Static data once per device and again only when IP or software version changes: device description (`/xml/device_description.xml`) and `GetZoneInfo`.
   - Dynamic state for all connected players in parallel: transport info, position info, volume, mute, transport settings, sleep timer, bass, treble, loudness; night mode and speech enhancement only on home theater players; group volume and mute on coordinators.
   - Favorites: `Browse FV:2` on the seed.
   - Subscriptions: ensure AVTransport and RenderingControl per player, GroupRenderingControl per coordinator, ZoneGroupTopology on the seed. Renew subscriptions past half their 30 minute lifetime.
4. `RefreshAsync` operation on `SonosSystem` triggers an immediate reconciliation (v1 `Refresh`).

### Events

`SonosEventListener` (internal) wraps `System.Net.HttpListener`:

- Sends `SUBSCRIBE` (with `CALLBACK`, `NT: upnp:event`, `TIMEOUT: Second-1800`), renewal `SUBSCRIBE` with `SID`, and `UNSUBSCRIBE`, using the shared `HttpClient`.
- Accepts `NOTIFY /event/{uuid}/{service}`, routes by `SID` header, answers 200, or 412 for an unknown SID.
- Parses bodies with `XmlReader` into internal DTOs: `AvTransportEvent`, `RenderingControlEvent`, `GroupRenderingControlEvent`, and the topology through `ZoneGroupStateParser`.
- Applies DTOs to the subject on the listener thread.

Callback host auto-detection: connect a UDP socket to the seed on port 1400 and read the local endpoint. Under Docker bridge networking this yields the container IP, which speakers cannot reach; `EventCallbackHost` or host networking is required there.

### Poll and event ordering

Each player records the time of the last event per service. A poll records its start time and does not apply a service's fields if an event for that service arrived after the poll started.

`NOT_IMPLEMENTED` and empty metadata from polls mean unknown and keep the current value. This preserves Spotify Connect metadata delivered by events.

### Commands

- Transport commands (play, pause, stop, next, previous, seek, toggle, set URI, favorites) go to the group coordinator's `Sonos.Base` connection.
- Volume, mute, EQ, sleep timer and grouping commands go to the player itself.
- After a command, the affected service is refreshed immediately, so results are deterministic without events.
- Commands throw `InvalidOperationException` when the player or system is disconnected.

### Failure handling

- Topology failure tries the next known IP as seed. After 3 consecutive failures: `Status = Error`, `IsConnected = false`, wait `RetryInterval`, rediscover.
- A per-player failure marks only that player `IsConnected = false` with `StatusMessage`, logged once per state transition, not every poll.
- No exception is swallowed without logging.

### Resources

- One named `HttpClient` from `IHttpClientFactory` (5 s timeout), used by `SonosServiceProvider` and the listener.
- One cached `Sonos.Base.SonosDevice` per player, recreated only when the IP changes.
- Shutdown: best effort `UNSUBSCRIBE` for every subscription (2 s budget), stop the listener, dispose connections.

## Operations

All writes are `[Operation]` methods. State updates through the follow-up refresh or events.

### SonosPlayer

| Operation | Implementation |
|---|---|
| `PlayAsync`, `PauseAsync`, `StopAsync`, `NextAsync`, `PreviousAsync` | Coordinator `Play`/`Pause`/`Stop`/`Next`/`Previous` |
| `TogglePlaybackAsync` | `SonosDevice.TogglePlayback` |
| `SeekAsync(TimeSpan)` | Coordinator `AVTransportService.Seek` (`REL_TIME`) |
| `SetVolumeAsync(decimal)` | 0..1 clamped, rounded to 0..100, `SetVolume` |
| `ChangeVolumeAsync(decimal delta)` | `SetRelativeVolume` |
| `RampVolumeAsync(decimal target)` | `RampToVolume` (`SLEEP_TIMER_RAMP_TYPE`) |
| `MuteAsync`, `UnmuteAsync` | `SetMute` |
| `PlayFavoriteAsync(string name)` | Stream favorites: `SetAVTransportURI(res, resMD)` then `Play`. Container favorites: `RemoveAllTracksFromQueue`, `AddURIToQueue(res, resMD)`, `SwitchToQueue`, `Play`. Unknown name throws `ArgumentException` listing favorites. |
| `PlayUriAsync(string uri, string? title)` | `http(s)` URLs play as radio style stream with escaped DIDL metadata; native `x-...` URIs pass through. Replaces v1 `PlayTrack`, `PlayRadio`, `PlaySpotifyTrack`. |
| `PlayNotificationAsync(string soundUri, decimal volume)` | `SonosDevice.QueueNotification`; plays over current playback and resumes. |
| `SwitchToTvAsync` | `SwitchToSpdif` |
| `SwitchToLineInAsync` | `SwitchToLineIn` |
| `SetShuffleAsync(bool)`, `SetRepeatAsync(SonosRepeatMode)` | `SetPlayMode` combining both |
| `SetSleepTimerAsync(TimeSpan)` | `ConfigureSleepTimer`, zero cancels |
| `SetBassAsync(int)`, `SetTrebleAsync(int)` | Validated -10..10 |
| `SetLoudnessAsync(bool)` | `SetLoudness` |
| `SetNightModeAsync(bool)`, `SetSpeechEnhancementAsync(bool)` | `SetEQ` (`NightMode`, `DialogLevel`) |
| `JoinGroupAsync(string roomNameOrUuid)` | `SetAVTransportURI("x-rincon:{coordinatorUuid}")`; unknown room throws `ArgumentException` listing rooms |
| `LeaveGroupAsync` | `BecomeCoordinatorOfStandaloneGroup` |

`IsEnabled` via `[Derived] [PropertyAttribute(..., KnownAttributes.IsEnabled)]`:

- `SwitchToTv`, `SetNightMode`, `SetSpeechEnhancement`: home theater players only.
- `SwitchToLineIn`: players with AudioIn.
- `Seek`: only when the track has a known duration.
- `LeaveGroup`: only in a group with more than one member.
- Playback operations: only while the player and system are connected.

### SonosGroup

Transport and `TogglePlaybackAsync` through the coordinator. `SetVolumeAsync` calls `SnapshotGroupVolume` then `SetGroupVolume`. `ChangeVolumeAsync` uses `SetRelativeGroupVolume`. `MuteAsync`/`UnmuteAsync` use `SetGroupMute`.

### SonosSystem

`RefreshAsync`, `GroupAllAsync(string coordinatorRoom)` (joins every other visible player to the coordinator), `UngroupAllAsync()` (every player in a multi-member group leaves).

## Parsers (own code)

`Sonos.Base` does not cover these, so we parse with `XmlReader`:

- `ZoneGroupStateParser`: groups, members, satellites (`Invisible`), `HTSatChanMapSet`/`ChannelMapSet` roles, `WirelessMode`, `EthLink`, `SoftwareVersion`, `MoreInfo` battery. `Sonos.Base`'s model lacks satellites and channel maps. Shared by polls and topology events.
- `FavoritesParser`: title, `res` and `r:resMD` from `BrowseResponse.Result`. `Sonos.Base`'s `DidlTrack` drops `resMD`.
- `DeviceDescriptionParser`: model name, model number, service list.
- AVTransport and RenderingControl `LastChange` event parsers, plus DIDL track metadata (title, creator, album, album art, `r:streamContent`).

## UI (`Namotion.Devices.Sonos.HomeBlaze`)

- `_Imports.razor` with the standard usings.
- `SonosSystemWidget`: connection chip, events chip ("live" or "polling only"), player and group counts, compact room list with playing icon and title.
- `SonosPlayerWidget`: room, model, source chip, battery with charging icon when present, album art, title, artist, previous/play-pause/next, mute toggle, volume slider committing on release. Controls disabled when disconnected or not enabled.
- `SonosGroupWidget`: group title, member chips, now playing, transport and group volume.
- `SonosSystemEditComponent`: `SeedHost`, `EventCallbackHost` (auto-detected value as placeholder), `EventPort`, `PollingInterval` and `RetryInterval` in seconds; local state, dirty tracking, `OnInitialized`; read-only current state block. Also used for creation (`IsCreating`). No setup component is needed.
- No edit components for players or groups.

## Tests (`Namotion.Devices.Sonos.Tests`)

Unit tests only, no live devices. Recorded payloads are embedded test files with anonymized UUIDs, MACs and IPs.

- Parsers: topology (4 players, 6 satellites, roles, Roam battery), AVTransport events (Spotify Connect metadata, TV, `NOT_IMPLEMENTED`, play mode, duration), RenderingControl and GroupRenderingControl events, favorites (4 entries with `res` and `resMD`), device description.
- Source detection table: `x-sonos-htastream` Tv, `x-sonos-vli...spotify` SpotifyConnect, `x-rincon-mp3radio`/`aac://` Radio, `x-rincon-queue` Queue, `x-rincon-stream` LineIn, AirPlay, Other.
- Subjects: instances kept across topology updates, missing players go offline but stay, groups keyed by coordinator, group properties follow coordinator, `IsEnabled` derivations, volume conversion and clamping, poll and event ordering, `NOT_IMPLEMENTED` keeps metadata.
- Listener: real `HttpListener` on loopback with an ephemeral port; `NOTIFY` routes by SID to the right player; unknown SID returns 412 without throwing.
- Conventions: `When<Condition>_Then<Expected>`, Arrange/Act/Assert, no hardcoded waits.

## Docs and integration

- `src/HomeBlaze/HomeBlaze/Data/Files/Docs/devices/Sonos.md`: setup, discovery and callback host (Docker bridge versus host networking), subject tree, operations, favorites and notifications, limitations (`XmlSerializer` AOT gap, album art mixed content over HTTPS, Windows `HttpListener` URL ACL, notification clips depending on the S2 audio clip API), and a final **Follow-ups** chapter listing the items below.
- `src/HomeBlaze/HomeBlaze/Data/Files/Devices/Sonos.json` sample configuration.
- `src/Namotion.Interceptor.slnx`: the three projects.
- `src/HomeBlaze/HomeBlaze/HomeBlaze.csproj`: project references.
- `src/HomeBlaze/HomeBlaze/Program.cs`: `AddAssembly` for `SonosSystem` and the UI assembly.
- `SonosServiceCollectionExtensions.AddSonos(...)` via `AddHostedSubject`.

## Follow-ups (documented in Sonos.md, not implemented)

- Alarms: list Sonos alarms as state, enable and disable operations (`AlarmClockService`).
- Queue editing and browsing beyond favorites (Sonos playlists, music library).
- Album art proxy so art loads when HomeBlaze is served over HTTPS.
- Windows support for the event listener without a URL ACL (for example a raw socket listener).
- AOT-clean SOAP client replacing `Sonos.Base`.
- `AvailableSoftwareUpdate` via `CheckForUpdate`.
- LED state and button lock settings.
- Home theater TV power state and other HTControl details.
- Opt-in live integration tests (`Category=Integration`, read-only, speaker IP from an environment variable).
- Upstream contributions to `Sonos.Base` (satellites in the topology model, `resMD` in DIDL) so own parsers can shrink.

## Verification

- `dotnet build src/Namotion.Interceptor.slnx`: 0 errors, 0 warnings.
- `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`: passes.
- Run HomeBlaze against the live household. Read-only first; any write operation only after asking, starting with harmless ones such as volume ±1 on an idle room.
- Review the v1 checklist below.

## v1 feature checklist

| v1 | v2 |
|---|---|
| SSDP discovery on all interfaces every 3 min | Seed host, known IPs, SSDP; topology every 30 s |
| `Host` callback setting | `EventCallbackHost` (auto-detected) and `EventPort` (6329) |
| `Refresh` operation | `SonosSystem.RefreshAsync` |
| AVTransport and RenderingControl events | Own listener, plus GroupRenderingControl and ZoneGroupTopology |
| `Uuid`, `ModelName`, `RoomName`, `Host`, `IpAddress`, `IsConnected` | `Uuid`, `Model`, `RoomName`, `IpAddress`, `IsConnected`, plus `IDeviceInfo` and `INetworkAdapter` |
| `IsAudioPlayer` | `SonosPlayer` versus `SonosSatellite` |
| Playing, volume, mute, track title/creator/album/image/URI | `IAudioPlayerState` and `IMediaTrackState` |
| Play, pause, stop, next, previous, mute, unmute, set volume | Same, plus seek, toggle, relative volume, ramp |
| `SwitchToTv` | `SwitchToTvAsync` |
| `PlayTrack`, `PlayRadio`, `PlaySpotifyTrack` | `PlayUriAsync`, `PlayFavoriteAsync` |
| Title "Model (Room)", icon by player type | `ITitleProvider`, `IIconProvider` |
| Album art | Absolute `CurrentTrackImageUri` |
