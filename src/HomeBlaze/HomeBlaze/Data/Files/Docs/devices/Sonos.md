---
title: Sonos
icon: LibraryMusic
---

# Sonos

Controls a Sonos household over the speakers' local **UPnP API** on port 1400. One `SonosSystem` finds the speakers by itself, shows every room as a `SonosPlayer` with its bonded subwoofer, surrounds or stereo partner as `SonosSatellite` children, and shows the groups the rooms currently form as `SonosGroup` subjects. State arrives through UPnP event subscriptions as it changes, and a poll every 30 seconds reconciles everything the events might have missed, so the model stays correct with or without events.

No Sonos account, cloud connection or API key is involved: everything runs between HomeBlaze and the speakers on the local network.

## Supported Systems

- **S2** speakers are the target platform. Every feature, including notification sounds, works on S2.
- **S1** speakers serve the same UPnP services (AVTransport, RenderingControl, GroupRenderingControl, ZoneGroupTopology, ContentDirectory), which is all the library uses apart from notifications, so an S1 household should work as well. `PlayNotification` needs S2, see [Limitations](#limitations).
- An S1 and an S2 system in the same home are two separate households that Sonos cannot group together (see the [Sonos support article](https://support.sonos.com/en-us/article/known-limitations-with-separate-s1-and-s2-sonos-systems)). Add one `SonosSystem` per household, see [When to set SeedHost](#when-to-set-seedhost).
- All speaker types are shown as rooms: speakers, soundbars, Port, Amp and portables. Home theater features (TV input, night mode, speech enhancement) are offered on players whose device description lists the `HTControl` service, line-in on players that list `AudioIn`. Portables (Move, Roam) report their battery.
- Boost and Bridge units only extend the Sonos network and have nothing to play, so they are not shown.

## Quick Start

### Add the System

Create a `SonosSystem` in the UI (category "Devices"), or add a JSON file to the data folder, for example `Devices/Sonos.json`. The minimal configuration is the type alone; every setting has a default:

```json
{
  "$type": "Namotion.Devices.Sonos.SonosSystem"
}
```

The same file with every setting at its default:

```json
{
  "$type": "Namotion.Devices.Sonos.SonosSystem",
  "seedHost": null,
  "eventCallbackHost": null,
  "eventPort": 6329,
  "pollingInterval": "00:00:30",
  "retryInterval": "00:00:30"
}
```

On a plain LAN this is enough: the system finds a speaker over SSDP, reads the household and subscribes to its events. Under Docker bridge networking, across VLANs, or with more than one household on the network, set `SeedHost` and possibly `EventCallbackHost`, see [Configuration](#configuration). See [Subjects, Storage & Files](../administration/subjects.md) for where JSON files live.

### Check the Connection

The system widget and the edit dialog show two chips: **Connected** once the first reconciliation succeeded, and **Events: live** once at least one speaker delivered an event. "Events: polling only" means the state is still correct but only as current as the last poll; see [Troubleshooting](#troubleshooting).

### Embed Widgets

`SonosSystem`, `SonosPlayer` and `SonosGroup` have widgets: the system lists its rooms with what they play, a player shows its track, album art, transport buttons, mute and a volume slider, and a group shows its rooms, its track, play or pause and the group volume. Embed them in a [markdown page](../administration/pages.md) with `Widget` subjects that reference them by path. Each fenced block below uses the `subject(name)` info string documented there:

<!-- The backticks are HTML entities so that this page shows the blocks instead of instantiating them: HomeBlaze turns every subject block of a page into a live subject, also inside code blocks. -->
<pre><code>&#96;&#96;&#96;subject(sonosSystem)
{
  "$type": "HomeBlaze.Components.Widget",
  "path": "/Devices/Sonos"
}
&#96;&#96;&#96;

&#96;&#96;&#96;subject(livingRoom)
{
  "$type": "HomeBlaze.Components.Widget",
  "path": "/Devices/Sonos/Players[RINCON_000E58A0B1C201400]"
}
&#96;&#96;&#96;

&#96;&#96;&#96;subject(livingRoomGroup)
{
  "$type": "HomeBlaze.Components.Widget",
  "path": "/Devices/Sonos/Groups[RINCON_000E58A0B1C201400]"
}
&#96;&#96;&#96;
</code></pre>

The paths assume the system is stored as `Devices/Sonos.json`. Players and groups are keyed by RINCON ids, which you find as the `Uuid` of a player in the subject browser, or with the MCP `browse` tool. A group is keyed by its **coordinator**, so a group path stops resolving when another room becomes the coordinator, for example after the coordinator left the group; the widget then shows "Cannot resolve path". For a stable widget, embed the player of the room instead, or the system widget, which always lists every room.

## Configuration

| Property | Type | Default | Range | Description |
|----------|------|---------|-------|-------------|
| `SeedHost` | string | null | `host`, `host:port`, an IPv6 literal or `[IPv6]:port`, without a scheme | Any speaker of the household. It is the first speaker tried and selects the household. The port defaults to 1400. Empty tries the speakers found since HomeBlaze started, then SSDP discovery. |
| `EventCallbackHost` | string | null | host name or IP address | The address the speakers send events to. Empty detects the local IPv4 address that routes to the seed speaker. |
| `EventPort` | int | 6329 | 1 to 65535 | The port the event listener binds on all interfaces and the speakers send events to. |
| `PollingInterval` | TimeSpan | 30 seconds | 5 seconds to 1 hour | How often topology, player state and favorites are reconciled. |
| `RetryInterval` | TimeSpan | 30 seconds | 5 seconds to 1 hour | The delay before reconnecting after a failed connection. |

Every change restarts the connection at once: subscriptions are cancelled, the seed is selected again and the household is read again. The system keeps its players while it reconnects.

### Intervals

The edit form only accepts 5 to 3600 seconds. Values set in the JSON file or over MCP are clamped when the system uses them: below 5 seconds counts as 5 seconds, above one hour as one hour, and zero or less as the default of 30 seconds.

A long `PollingInterval` does not let the events lapse: the subscriptions are renewed on their own schedule, independent of polling (see [UPnP Event Subscriptions](#upnp-event-subscriptions)). A short one costs about ten small SOAP requests per room per poll (see [Polling](#polling)); with events live there is rarely a reason to poll more often than every 30 seconds.

### When to Set SeedHost

Leave it empty on a flat home network with one Sonos household. Set it to the IP address of any speaker, ideally one with a DHCP reservation, when:

- **Multicast does not reach HomeBlaze.** SSDP discovery uses multicast, which does not cross Docker bridge networks or VLANs and is often blocked by firewalls or Wi-Fi access points.
- **The network has more than one Sonos household**, for example an S1 and an S2 system, or a neighbor's speakers on a shared network. SSDP takes whichever speaker answers first, so without `SeedHost` the system can end up on the wrong household. To show a second household, add a second `SonosSystem` with its own `SeedHost` and a different `EventPort`.
- **Startup should not wait for discovery.** After a HomeBlaze restart the known speakers are gone (they are kept in memory only), so a system without `SeedHost` starts with a 5 second SSDP search.

The seed only has to answer the first request. When it later stops answering, the system moves on to another connected player, see [Seed Selection and Discovery](#seed-selection-and-discovery).

### When to Set EventCallbackHost

The speakers deliver events by sending HTTP requests to `http://<EventCallbackHost>:<EventPort>/event/...`. The listener runs inside the HomeBlaze process on its own port; no second web server or reverse proxy is involved.

| Setup | `EventCallbackHost` | `EventPort` |
|-------|---------------------|-------------|
| HomeBlaze directly on a host in the speakers' network | empty (detected) | open inbound in the host firewall |
| Docker with host networking (`network_mode: host`) | empty (detected) | open inbound in the host firewall |
| Docker with bridge networking (the default compose file) | the LAN IP of the Docker host | published in `ports`, for example `"6329:6329"` |
| Several network interfaces, or a VPN that wins the route to the speakers | the IP of the interface the speakers can reach | open inbound on that interface |

Under bridge networking the detected address is the container's own address, which the speakers cannot reach: the subscriptions succeed, but no event arrives. The system then logs a Warning that names the callback address it gave the speakers, and keeps the state current by polling.

## Status

| `Status` | When | `StatusMessage` |
|----------|------|-----------------|
| Stopped | Not started yet, or stopped | empty |
| Starting | Selecting the seed, starting the event listener and reading the household for the first time | empty |
| Running | Connected | empty, or "Reconciliation failed (n of 3): ..." while the seed does not answer and another speaker is tried |
| Error | The connection attempt failed; it is retried after `RetryInterval` | The reason, for example "No Sonos speaker found. Set SeedHost when multicast discovery is blocked, for example under Docker bridge networking." or "The SeedHost '...' did not answer, and no other Sonos speaker was found." |

`IsConnected` becomes true after the first reconciliation of a connection succeeded, and false as soon as the connection is torn down. Each player and satellite has its own `IsConnected` and `StatusMessage`, see [Offline Handling](#offline-handling).

## Subject Model

```
SonosSystem                                 the household
├── Players[RINCON_…]                       SonosPlayer, one per room
│     └── Satellites[RINCON_…]              SonosSatellite: subwoofer, rear left, rear right or stereo partner
├── Groups[RINCON_<coordinator>]            SonosGroup, the rooms playing together
│     ├── Coordinator ──> a SonosPlayer     references into Players, not copies
│     └── Members[]   ──> SonosPlayer…
└── Favorites[]                             SonosFavorite records: Title, Uri, IsContainer, ImageUri
```

### Keys and Paths

Players, satellites and groups are keyed by RINCON ids, the UPnP UUIDs Sonos assigns to each unit, typically `RINCON_` followed by the unit's MAC address and `01400`. They survive restarts, renames and IP changes, so paths and history stay valid. Room names are not keys: they can be renamed in the Sonos app at any time, and two speakers can briefly share one after a replacement. Operations that take a room accept the room name (ignoring case) or the RINCON id.

### Players and Satellites

A **player** is a room as the Sonos app shows it: the unit that plays, takes commands and reports state. Its title is the model and the room, for example "Sonos Arc (Living Room)".

A **satellite** is a unit bonded to a room that does not play on its own. The role is read from the topology's channel maps:

| `Role` | Source in the topology | Example |
|--------|------------------------|---------|
| `Subwoofer` | Home theater channel map lists `SW` for the unit | Sub, Sub Mini |
| `RearLeft`, `RearRight` | Home theater channel map lists `LR` or `RR` | a pair of Era 100 as surrounds |
| `StereoPartner` | An invisible member of the same group that the room's stereo channel map lists | the second speaker of a stereo pair |
| `Other` | A satellite whose channel is none of the above | |

A satellite carries the room name of its player, since a subwoofer reports its own zone name as "Sub", and its title names the model, the room and the role, for example "Sonos Sub (Living Room, subwoofer)", because the units of one room often share a model. Satellites have identity, network and firmware state but no playback state of their own: the room's player reports it.

Members marked `IsZoneBridge` (Boost, Bridge) and invisible members that are no stereo partner are skipped.

### Groups

A **group** is the set of rooms playing the same audio. A room that is not grouped is a group of one, as in the Sonos app, so every player belongs to exactly one group. The group is keyed by the RINCON id of its coordinator, the player that holds the queue and the transport. Sonos's own group id (`GroupId`) changes on every regroup, so it is shown but not used as the key.

Playback and track state of a group are the coordinator's. Volume and mute are the group's own, read from the coordinator's GroupRenderingControl service. A member player reports the source, play mode, sleep timer and media title of its coordinator, because Sonos reports those per group.

### Offline Handling

- **A player that leaves the topology stays.** It keeps its subject and its last state and reports `IsConnected = false` until it reappears, so dashboards and history keep their references. Players are only removed by a HomeBlaze restart.
- **Two topologies in a row must miss it.** A seed that just rebooted or woke up can briefly report part of the household, so a topology that misses players is applied only when the next topology, polled or evented, misses them too. The first topology after connecting is applied at once, since the previous one may be arbitrarily old.
- **A new player starts offline.** A player found in the topology reports `IsConnected = false` until its first poll succeeds, because only that poll proves there is a connection to command it through. A player that appears through a topology event is polled and subscribed at the next reconciliation.
- **No answer means offline, an error answer does not.** A player goes offline when a request fails at the transport level (timeout, connection refused) and its `StatusMessage` shows the error. When it answers a read with a UPnP fault, for example a value a model or a grouped member does not support, it stays connected and the values of that read keep their last state.
- **Satellites** go offline when they leave the topology together with or apart from their player, and while their identity read fails (it is retried at every reconciliation until it answers).
- **The whole household goes offline** when the connection is torn down: every player and satellite reports "The Sonos system is disconnected." until the next connection polls it.

## Interfaces

The interfaces come from `HomeBlaze.Abstractions` (see [Building Subjects](../development/building-subjects.md) and the [source](https://github.com/RicoSuter/Namotion.Interceptor/tree/master/src/HomeBlaze/HomeBlaze.Abstractions)). Dashboards, automations and agents that work with an interface work with Sonos without knowing it.

| Subject | Interfaces |
|---------|------------|
| `SonosSystem` | `IHubDevice`, `IConfigurable`, `IMonitoredService`, `IConnectionState`, `ILastUpdatedProvider`, `ITitleProvider`, `IIconProvider` |
| `SonosPlayer` | `IAudioPlayer` (with `IAudioPlayerState`, `IAudioPlayerController`, `IVolumeState`, `IVolumeController`), `IMediaTrackState`, `IBatteryState`, `IDeviceInfo`, `INetworkAdapter`, `ISoftwareState`, `IConnectionState`, `ITitleProvider`, `IIconProvider` |
| `SonosSatellite` | `IDeviceInfo`, `INetworkAdapter`, `ISoftwareState`, `IConnectionState`, `ITitleProvider`, `IIconProvider` |
| `SonosGroup` | `IAudioPlayer`, `IMediaTrackState`, `IVirtualSubject`, `ITitleProvider`, `IIconProvider` |

Interface members the speakers do not report are always empty: `SubnetMask`, `Gateway` and `SignalStrength` of `INetworkAdapter`, and `AvailableSoftwareUpdate` of `ISoftwareState`.

## State

Volumes and battery levels are fractions from 0 to 1, which the UI shows as 0 to 100 %. Durations and positions are `TimeSpan` values.

### SonosSystem

| Property | Unit | Description |
|----------|------|-------------|
| `Players` | | The room players by RINCON id, including offline ones |
| `Groups` | | The current groups by the RINCON id of their coordinator |
| `Favorites` | | The playable Sonos favorites, see [Favorites](#favorites) |
| `AreEventsActive` | | True while the event listener runs and at least one subscription has delivered an event |
| `ActiveEventCallbackHost` | | The host the speakers were told to send events to. Empty while the listener could not start, no local address routes to the seed or the system is not connected; it stays set when the listener runs but no event arrives |
| `IsConnected`, `Status`, `StatusMessage` | | See [Status](#status) |
| `LastUpdated` | time | When the last reconciliation completed |

### SonosPlayer

| Property | Unit | Description |
|----------|------|-------------|
| `Uuid`, `RoomName` | | The RINCON id and the room name |
| `TransportState` | | `Unknown`, `Stopped`, `Playing`, `Paused` or `Transitioning` |
| `IsPlaying` | | True while playing or transitioning, empty while the state is unknown |
| `Volume` | 0..1 | Volume of this room |
| `IsMuted` | | Mute of this room |
| `CurrentTrackTitle`, `CurrentTrackArtist`, `CurrentTrackAlbum` | | The current track. On radio the title is the song the station reports, otherwise the station |
| `CurrentTrackImageUri` | URI | Album art as an absolute URI; Sonos's relative `/getaa?...` paths are resolved against the speaker |
| `CurrentTrackUri` | URI | The URI of the current track |
| `CurrentTrackDuration` | TimeSpan | Zero or empty for streams, TV and line-in |
| `CurrentTrackPosition` | TimeSpan | The position at the last poll or command read-back; it is not advanced between polls and is cleared when the track changes |
| `Source` | | Where the group's audio comes from: `None`, `Tv`, `LineIn`, `SpotifyConnect`, `AirPlay`, `Radio`, `Queue` or `Other`, detected from the coordinator's transport URI |
| `MediaTitle` | | The station, playlist or album the group plays, when Sonos reports it |
| `Shuffle`, `Repeat` | | The group's play mode; `Repeat` is `Off`, `All` or `One` |
| `SleepTimerRemaining` | TimeSpan | The group's sleep timer, as of the last poll; empty when none runs |
| `Bass`, `Treble` | -10..10 | Equalizer of this room |
| `Loudness` | | Loudness compensation of this room |
| `NightMode`, `SpeechEnhancement` | | Home theater players only. Any speech enhancement level above zero counts as on, which covers the Arc Ultra's levels 1 to 4 |
| `GroupCoordinatorUuid`, `IsGroupCoordinator` | | Group membership |
| `IsHomeTheater`, `HasLineIn` | | Whether the device description lists the `HTControl` and `AudioIn` services |
| `Satellites` | | The bonded units of the room, by RINCON id |
| `BatteryLevel` | 0..1 | Portable speakers only, from the topology |
| `IsCharging` | | Portable speakers only |
| `Manufacturer`, `Model`, `ProductCode` | | "Sonos", the model name and the model number from the device description |
| `SerialNumber`, `MacAddress`, `HardwareRevision`, `SoftwareVersion` | | From `GetZoneInfo`; `SoftwareVersion` is the display version the Sonos app shows |
| `IpAddress`, `IsWireless` | | From the topology; `IsWireless` is false on Ethernet |
| `IsConnected`, `StatusMessage` | | See [Offline Handling](#offline-handling) |

When the track changes and Sonos reports no details for the new one, the track values are cleared rather than kept from the previous track. Spotify Connect is the exception in the other direction: its polls answer `NOT_IMPLEMENTED` for details its events delivered, so those are kept while the track stays the same, and they only arrive through events.

### SonosSatellite

| Property | Description |
|----------|-------------|
| `Role` | `Subwoofer`, `RearLeft`, `RearRight`, `StereoPartner` or `Other` |
| `Uuid`, `RoomName` | The RINCON id and the room of the player it is bonded to |
| `Manufacturer`, `Model`, `ProductCode`, `SerialNumber`, `HardwareRevision`, `SoftwareVersion` | Identity and firmware, read when it appears, after a firmware or address change and after a reconnect |
| `IpAddress`, `MacAddress`, `IsWireless` | Network |
| `IsConnected`, `StatusMessage` | Connection |

### SonosGroup

| Property | Unit | Description |
|----------|------|-------------|
| `GroupId` | | Sonos's current group id; changes on every regroup |
| `Coordinator`, `Members` | | References to the players in `Players` |
| `Volume` | 0..1 | Group volume |
| `IsMuted` | | Group mute |
| `IsPlaying`, `CurrentTrackTitle`, `CurrentTrackArtist`, `CurrentTrackAlbum`, `CurrentTrackImageUri`, `CurrentTrackUri`, `CurrentTrackPosition`, `CurrentTrackDuration`, `MediaTitle` | | The coordinator's values |

The title joins the room names, for example "Kitchen + Living Room".

## Operations

### Units and Validation

- **Percent parameters** (`volume`, `delta`) are fractions: 0 to 1 for a volume and -1 to 1 for a relative change. Over MCP and in code pass the fraction (0.25 is 25 %); the operation dialog takes 0 to 100 and divides by 100. Values out of range are rejected, not clamped. Sonos itself works in whole percent, so the fraction is rounded to the nearest percent.
- **TimeSpan parameters** (`position`, `duration`) are `hh:mm:ss` strings over MCP and JSON, for example `"00:30:00"`.
- **Enum parameters** take the member name, for example `All` for `repeat`.
- **Rooms** are a room name (ignoring case) or a RINCON id. An unknown room fails with the list of known rooms, an unknown favorite with the list of known titles.

### SonosPlayer

"Coordinator" in the target column means the command goes to the coordinator of the player's group and affects the whole group; "player" means it goes to this room only.

| Operation | Parameters | Target | Description |
|-----------|------------|--------|-------------|
| `Play`, `Pause`, `Stop`, `Next`, `Previous` | | coordinator | Transport of the group |
| `TogglePlayback` | | coordinator | Pauses when playing, otherwise plays |
| `Seek` | `position` (TimeSpan, at least zero) | coordinator | Jumps within the current track; enabled only while the track has a duration |
| `SetVolume` | `volume` (percent, 0 to 1) | player | Volume of this room |
| `ChangeVolume` | `delta` (percent, -1 to 1) | player | Relative change of this room's volume |
| `RampVolume` | `volume` (percent, 0 to 1) | player | Moves this room's volume gradually to the target, using Sonos's sleep timer ramp |
| `Mute`, `Unmute` | | player | Mute of this room |
| `PlayFavorite` | `title` | coordinator | Plays a Sonos favorite, see [Favorites](#favorites) |
| `PlayUri` | `uri` | coordinator | Plays a URI once as a track, see [Playing URIs](#playing-uris-streams-and-notifications) |
| `PlayStream` | `uri`, `title` (optional) | coordinator | Plays a radio or live stream |
| `PlayNotification` | `soundUri`, `volume` (percent, 0 to 1) | player | Plays a sound over the current playback, which then resumes (S2) |
| `SwitchToTv` | | player | Selects the TV input of a home theater player |
| `SwitchToLineIn` | | player | Selects the line-in of a player that has one |
| `SetShuffle` | `shuffle` (bool) | coordinator | Shuffle of the group; keeps the current repeat mode |
| `SetRepeat` | `repeat` (`Off`, `All`, `One`) | coordinator | Repeat mode of the group; keeps the current shuffle |
| `SetSleepTimer` | `duration` (TimeSpan, 0 to 23:59:59) | coordinator | Sleep timer of the group; zero cancels it |
| `SetBass`, `SetTreble` | `bass`, `treble` (integer, -10 to 10) | player | Equalizer of this room |
| `SetLoudness` | `loudness` (bool) | player | Loudness compensation of this room |
| `SetNightMode` | `nightMode` (bool) | player | Home theater players only |
| `SetSpeechEnhancement` | `speechEnhancement` (bool) | player | Home theater players only; writes `DialogLevel` 1 or 0 |
| `JoinGroup` | `room` | player | Joins the group the given room belongs to. Joining the own group does nothing; joining itself is rejected |
| `LeaveGroup` | | player | Makes this room standalone; enabled for any member, including the coordinator, of a group of two or more rooms |

### SonosGroup

| Operation | Parameters | Description |
|-----------|------------|-------------|
| `Play`, `Pause`, `Stop`, `Next`, `Previous`, `TogglePlayback`, `Seek` | as on a player | Transport, sent to the coordinator |
| `SetVolume` | `volume` (percent, 0 to 1) | Group volume. The rooms keep their volume ratio: a fresh volume snapshot is taken first, so a room changed in the Sonos app does not jump back |
| `ChangeVolume` | `delta` (percent, -1 to 1) | Relative change of the group volume, with the same snapshot |
| `Mute`, `Unmute` | | Mutes or unmutes every room of the group |
| `PlayFavorite`, `PlayUri`, `PlayStream`, `SetShuffle`, `SetRepeat`, `SetSleepTimer` | as on a player | Delegated to the coordinator |

Per-room settings (`RampVolume`, equalizer, inputs, notifications, grouping) exist on the players only.

### SonosSystem

| Operation | Parameters | Description |
|-----------|------------|-------------|
| `Refresh` | | Reads topology, state and favorites now instead of at the next poll |
| `GroupAll` | `room` | Groups every connected room with the given room (party mode). The given room becomes the coordinator; when it is a member of another group, it first leaves that group |
| `UngroupAll` | | Makes every connected room standalone |

### When Operations Are Enabled

Every operation is disabled while the system or the target is not connected; the UI greys it out, and a call fails with "not connected". Operations whose target is the coordinator also need the coordinator to be connected. `Seek` additionally needs a track with a duration, `SwitchToTv`, `SetNightMode` and `SetSpeechEnhancement` a home theater player, `SwitchToLineIn` a line-in, and `LeaveGroup` a group of at least two rooms.

After each command the players of the commanded group are read back, also when the command failed, so its effect shows without waiting for events or the next poll. Grouping commands (`JoinGroup`, `LeaveGroup`, `GroupAll`, `UngroupAll`) are followed by a full reconciliation instead, since they change the topology.

## Favorites

`SonosSystem.Favorites` lists the household's Sonos favorites, the "My Sonos" favorites of the Sonos app. Favorites belong to the household, not to a room, so they are read once per reconciliation from one speaker and any player or group can play them.

- **Read from players, not satellites.** Satellites answer the favorites request (`Browse` of `FV:2` on the ContentDirectory service) with HTTP 500, so the list is read through a connected room player, group coordinators first, and the next player is tried when one fails.
- **Paged.** Favorites are read in pages of 100 until all are read, so households with more than 100 favorites are complete. A page whose content did not change since the last read reuses its records.
- **Kept on failure.** When no player answers, the previous list stays and a Warning is logged once.
- **Shortcuts are skipped.** Favorites of type "shortcut", such as Sonos Radio station shortcuts, carry no URI and can only be started through the Sonos websocket API, so they are not listed.
- **Matching.** `PlayFavorite` matches the title ignoring case; when two favorites share a title, the first one plays.
- **Playing.** A playlist or album favorite (`IsContainer`) replaces the group's queue and plays it. Any other favorite, such as a station or a track, is set as the group's transport URI and plays without touching the queue. Both are sent with the favorite's stored DIDL metadata, which music services need to authorize playback. That metadata often contains account tokens, so it is kept internal and never appears in the state, the JSON or over MCP.

Each record has `Title`, `Uri`, `IsContainer` and `ImageUri` (the cover art as an absolute URI). The system only reports connected after its first reconciliation, which reads the favorites, so before that `PlayFavorite` fails with "not connected" instead of reporting every title as unknown.

## Playing URIs, Streams and Notifications

| Operation | Sonos mechanism | Behavior | Use for |
|-----------|-----------------|----------|---------|
| `PlayUri` | `SetAVTransportURI` with the URI unchanged and no metadata, then `Play` | Plays once as a normal track that ends and can be sought | An mp3 or other finite file; any native Sonos URI (`x-sonos-...`, `x-rincon...`, `x-file-cifs:`) |
| `PlayStream` | `SetAVTransportURI` with `x-rincon-mp3radio:` and a radio DIDL item carrying the title, then `Play` | Shows as radio with its title and without a seek bar; Sonos reconnects the stream when it ends | Internet radio and other live http(s) streams |
| `PlayNotification` | The audio clip API of the speaker's local websocket (`wss://<speaker>:1443/websocket/api`) | Plays the clip over the current playback of this room, which then resumes | Doorbells, alarms, announcements (S2 only) |
| `PlayFavorite` | Queue or transport URI with the favorite's metadata | As in the Sonos app | Music service content |

- `PlayUri` accepts any URI that is not blank. Since `Source` reports `Radio` for every http(s) URI, a file started with `PlayUri` shows as radio too, but it keeps its duration and seek bar.
- `PlayStream` accepts `http://`, `https://` and `x-rincon-mp3radio:` URIs and rejects any other scheme. It rewrites an http(s) scheme to `x-rincon-mp3radio`, because Sonos renders a plain stream as radio only behind that scheme. Without a title, the URI is the title.
- `PlayNotification` accepts only absolute http(s) URIs that the **speaker** can download, so use an address on the local network or a public URL, not `localhost`. The volume is played at 1 % at least, because the clip API rejects 0. A call times out after 10 seconds. The speaker's answer is not read, so a clip it rejects (for example for a URL it cannot load) is not reported.

## MCP and AI Agents

Agents reach Sonos through the HomeBlaze MCP tools `browse`, `search`, `get_property`, `list_methods` and `invoke_method` (see [AI](../architecture/design/ai.md)). Operations need MCP write access: the default compose file starts MCP read-only (`McpServer__ReadOnly`), which allows reading the state but no operation.

Paths are the same as in the UI, for example `Devices/Sonos`, `Devices/Sonos/Players[RINCON_000E58A0B1C201400]` and `Devices/Sonos/Groups[RINCON_000E58A0B1C201400]`. A good first step for an agent is to browse `Devices/Sonos` to learn the room names, the RINCON ids, the current groups and the favorite titles.

`list_methods` on a player returns each operation with its parameter hints. An abridged excerpt:

```json
{
  "methods": [
    {
      "name": "SetVolume",
      "kind": "operation",
      "title": "Set Volume",
      "description": "Sets the volume of this player.",
      "parameters": [
        { "name": "volume", "type": "number", "description": "fraction where 1 = 100% (0.2 = 20%)" }
      ]
    },
    {
      "name": "SetRepeat",
      "kind": "operation",
      "title": "Set Repeat",
      "description": "Sets the repeat mode of the player's group.",
      "parameters": [
        { "name": "repeat", "type": "string", "enum": ["Off", "All", "One"] }
      ]
    },
    {
      "name": "SetSleepTimer",
      "kind": "operation",
      "title": "Set Sleep Timer",
      "description": "Sets the sleep timer of the player's group, at most 23:59:59; zero cancels it.",
      "parameters": [
        { "name": "duration", "type": "string", "pattern": "^-?(\\d+\\.)?\\d{2}:\\d{2}:\\d{2}(\\.\\d{1,7})?$" }
      ]
    },
    {
      "name": "PlayStream",
      "kind": "operation",
      "title": "Play Stream",
      "description": "Plays an http(s) or x-rincon-mp3radio stream on the player's group as radio, which Sonos reconnects when it ends.",
      "parameters": [
        { "name": "uri", "type": "string" },
        { "name": "title", "type": "string", "nullable": true }
      ]
    }
  ]
}
```

`invoke_method` takes the arguments by parameter name:

```json
{ "path": "Devices/Sonos/Players[RINCON_000E58A0B1C201400]", "method": "SetVolume", "parameters": { "volume": 0.25 } }
{ "path": "Devices/Sonos/Players[RINCON_000E58A0B1C201400]", "method": "ChangeVolume", "parameters": { "delta": -0.05 } }
{ "path": "Devices/Sonos/Players[RINCON_000E58A0B1C201400]", "method": "SetSleepTimer", "parameters": { "duration": "00:30:00" } }
{ "path": "Devices/Sonos/Groups[RINCON_000E58A0B1C201400]", "method": "PlayFavorite", "parameters": { "title": "Radio Swiss Jazz" } }
{ "path": "Devices/Sonos", "method": "GroupAll", "parameters": { "room": "Kitchen" } }
```

Guidance for agents:

- **Percent is a fraction.** Pass 0.25 for 25 %, never 25. A value above 1 is rejected.
- **TimeSpan is `hh:mm:ss`.** 90 seconds is `"00:01:30"`, two hours `"02:00:00"`.
- **Enums by name.** `"All"`, not a number, is the readable choice; names are matched ignoring case.
- **Player or group.** Transport, favorites, URIs, streams, shuffle, repeat and sleep timer affect the whole group either way. Volume and mute on a player affect that room only, on a group all its rooms.
- **Self-correcting errors.** An unknown favorite or room returns the list of known titles or rooms, so the agent can retry with a valid one. A "not connected" failure arrives as a generic error over MCP; the agent can read `IsConnected` and `StatusMessage` to explain it.
- **State after a command.** The commanded group is read back before the call returns, so a following `get_property` sees the result.

## How It Works

### Connection Lifecycle

```
            ┌──────────────────────────────────────────────────────────────┐
            │ Starting                                                     │
            │  1. select a seed: SeedHost, known speakers, SSDP            │
            │  2. start the event listener on EventPort                    │
            │  3. reconcile: topology, players, satellites, favorites,     │
            │     subscriptions                                            │
            └──────────────┬──────────────────────────────┬────────────────┘
                   success │                              │ failure
                           v                              v
            ┌──────────────────────────────┐    ┌────────────────────────────┐
            │ Running (IsConnected)        │    │ Error                      │
            │  every PollingInterval:      │    │  wait RetryInterval, or    │
            │    reconcile                 │    │  less on a config change,  │
            │  at renewal deadlines:       │    │  then start again          │
            │    renew subscriptions       │    └────────────────────────────┘
            │  on NOTIFY: apply the event  │                  ^
            └──────────────┬───────────────┘                  │
                           │ 3 failed reconciliations,        │
                           │ config change or stop            │
                           v                                  │
            ┌──────────────────────────────┐                  │
            │ Teardown                     ├──────────────────┘
            │  unsubscribe all (2 s)       │
            │  stop listener, mark offline │
            └──────────────────────────────┘
```

A configuration change tears down and starts again at once, without waiting for `RetryInterval`. Removing the system from the graph or stopping HomeBlaze runs the same teardown and ends the loop.

### Seed Selection and Discovery

The seed is the speaker the household topology is read from. It is selected in this order on every connection attempt:

1. **`SeedHost`**, when set, with the full 5 second request timeout, since the configured seed is preferred. An invalid value fails the attempt with "The SeedHost '...' is invalid". When it does not answer, a Warning is logged once until it answers again, and the search goes on.
2. **Known speakers**, the players found since HomeBlaze started, with a 2 second probe each. Players still in the last topology are tried first, since a missing one was more likely unplugged or replaced.
3. **SSDP discovery.** An M-SEARCH for `urn:schemas-upnp-org:device:ZonePlayer:1` goes out over UDP multicast on every IPv4 interface for 5 seconds (using [Rssdp](https://github.com/Yortw/RSSDP)). The first response whose USN starts with `uuid:RINCON_` becomes the seed, since other UPnP devices can answer any search target.

A speaker counts as answering when it returns its zone group state. While connected, the seed changes when a reconciliation fails: the next one uses another connected player.

### Topology

The household layout comes from `GetZoneGroupState` of the ZoneGroupTopology service: an XML document that lists every group with its coordinator and members, and for each member its RINCON id, room name, address (`Location`), firmware build, Ethernet link, battery (`MoreInfo`) and bonded satellites with their channel maps. The library parses it with its own LINQ to XML parser rather than Sonos.Base's model, because it needs the satellites, the stereo channel maps and the invisible members.

The topology is read from the seed at every reconciliation and pushed by the seed's ZoneGroupTopology events whenever the household changes. Since the raw XML is identical in nearly every poll and event, an unchanged document is neither parsed nor applied again. A topology event applies the new layout at once, and connections follow immediately, so a command after an IP change already reaches the new address.

### Static Data

Identity rarely changes, so it is read only when needed: when a unit first appears, when its firmware build in the topology changes, when its address changes, and after a reconnect. Two reads per unit:

- `/xml/device_description.xml` (plain HTTP) for the model name, model number and the list of UPnP services, which decides `IsHomeTheater` (`HTControl`) and `HasLineIn` (`AudioIn`).
- `GetZoneInfo` of the DeviceProperties service for serial number, MAC address, hardware version and the display software version. When it answers with a fault, the description is applied alone and the zone info is read again at the next reconciliation.

### Polling

A reconciliation runs every `PollingInterval`, on `Refresh` and after grouping commands:

1. Read the topology from the seed and apply it (see [Ordering](#ordering-events-against-polls)), then open or move the connections to the units in it.
2. Poll every player in the topology, all players in parallel and the requests of one player one after another.
3. Read the identity of satellites that need it.
4. Read the favorites.
5. Unsubscribe obsolete, renew due and add missing event subscriptions.
6. Set `LastUpdated`.

A player poll is ten SOAP requests: `GetTransportInfo`, `GetTransportSettings`, `GetMediaInfo`, `GetPositionInfo` and `GetRemainingSleepTimerDuration` of AVTransport, and `GetVolume`, `GetMute`, `GetBass`, `GetTreble` and `GetLoudness` of RenderingControl. A home theater player adds `GetEQ` for `NightMode` and `DialogLevel`, a group coordinator `GetGroupVolume` and `GetGroupMute` of GroupRenderingControl. The position and the sleep timer are never evented, so polls (and command read-backs) are their only source.

Polling is both the fallback when events cannot reach HomeBlaze and the reconciliation that repairs anything an event missed, for example while a speaker rebooted or the network dropped a NOTIFY.

### UPnP Event Subscriptions

Sonos speakers implement UPnP eventing (GENA): a client sends `SUBSCRIBE` with a callback URL to a service's event URL, and the speaker then sends `NOTIFY` requests with the changed state to that URL until the subscription expires or is cancelled.

| Service | Subscribed on | Event URL | Delivers |
|---------|---------------|-----------|----------|
| AVTransport | every player in the topology | `/MediaRenderer/AVTransport/Event` | transport state, play mode, media and track URIs, track and media metadata, duration |
| RenderingControl | every player in the topology | `/MediaRenderer/RenderingControl/Event` | volume, mute, bass, treble, loudness, night mode, dialog level |
| GroupRenderingControl | every group coordinator | `/MediaRenderer/GroupRenderingControl/Event` | group volume and group mute |
| ZoneGroupTopology | the seed | `/ZoneGroupTopology/Event` | the whole topology on every change |

Satellites get no subscriptions: they have no playback state of their own.

- **Callback.** Each subscription gets its own callback path, `http://<callback host>:<EventPort>/event/<RINCON id>/<service>` (`seed/ZoneGroupTopology` for the topology), so a NOTIFY can be matched even before the speaker's `SUBSCRIBE` response with the subscription id (SID) arrived: Sonos sends the first NOTIFY right after accepting, often before its response.
- **Listener.** A small `HttpListener` inside HomeBlaze binds all interfaces on `EventPort`. It accepts only `NOTIFY` (other methods get 405), answers an unknown SID with 412 so the speaker drops a stale subscription, for example from before a restart, and refuses bodies over 1 MiB with 413.
- **Lifetime and renewal.** Subscriptions request a 30 minute lifetime (`TIMEOUT: Second-1800`) and are renewed at half the lifetime the speaker grants, at least one minute. The connection loop wakes up for the earliest renewal on its own, independent of the polling interval, so events never lapse with a long `PollingInterval`. A renewal the speaker rejects forgets the subscription, which is subscribed again in the same pass; a renewal that gets no answer is retried after 30 seconds.
- **Events live.** A SID only proves that the speaker accepted the `SUBSCRIBE`, not that its NOTIFY requests reach HomeBlaze. So `AreEventsActive` turns true only after the first NOTIFY arrived. When a speaker accepted a subscription but sent no event within 15 seconds, a Warning names the callback address and points at the firewall, Docker and `EventCallbackHost`; the loop wakes up for that deadline as well, so the Warning does not wait for the next poll.
- **Following the topology.** A player that leaves the topology or moves to a new address has its subscriptions cancelled, and they are made again at the new address. One missed poll does not drop a subscription: it is kept while the player stays in the topology. New subscriptions are only made to connected players.
- **Cancelling.** On stop, removal from the graph and every configuration change, all subscriptions are cancelled with `UNSUBSCRIBE` at once, within 2 seconds. A subscription that cannot be cancelled, for example on a speaker that is off, is logged at Information; the speaker drops it once it expires.

### Ordering Events Against Polls

Events and polls run concurrently: a poll that started before an event can finish after it and would overwrite the newer event values with older ones. Wall clock times cannot order them reliably, so the system hands out numbers from one monotonic sequence: a poll takes a number before it sends its first request, and an event takes one when it arrives.

Each kind of state keeps the number of its latest applied event and its latest applied poll: per player AVTransport (including the position), RenderingControl and the sleep timer, per group the group volume and mute, and for the household the topology. A poll applies only when no event of that kind arrived after the poll started and no poll that started later was applied already. Otherwise its values are discarded, because they describe a state the speaker has since replaced.

```
seq:   10                  11                       12
       poll starts         NOTIFY volume=40         poll returns volume=35
       (reads volume)      applied, event = 11      started at 10 < 11: volume is not applied
```

Command read-backs make the second rule matter: a read-back started after a command can complete before a regular poll that started earlier.

### Commands

Commands use [Sonos.Base](https://github.com/svrooij/sonos-net) for the SOAP calls, all sharing one `HttpClient` per connection. A command first checks that the system and the target are connected, runs, and then reads the players of the commanded group back, also when it failed, since a multi-step command such as playing a playlist favorite may have partly applied. A failed read-back is logged and never replaces the command's own result or exception. Argument errors (null or unknown values, a missing home theater or line-in) are thrown before anything is sent.

Group volume commands send `SnapshotGroupVolume` first: Sonos scales the member volumes from the last snapshot, so without a fresh one, a room changed in the Sonos app in the meantime would jump back.

The system raises its change notifications while it holds internal locks that every Sonos operation and poll also takes. Code that subscribes to Sonos property changes may start a Sonos operation, but must not wait for one synchronously (for example with `GetAwaiter().GetResult()`), or it deadlocks.

### Failure Handling and Logging

| Failure | Effect | Log |
|---------|--------|-----|
| No seed answers, or the first reconciliation fails | Attempt fails, `Status` Error, retried after `RetryInterval` | Warning when the failure starts or its message changes, Debug while it repeats |
| A later reconciliation fails, for example because the seed does not answer the topology read | Another connected player becomes the seed; after 3 failures in a row the connection is torn down and rebuilt | Warning per failure |
| A player or satellite does not answer | Only that unit goes offline; the reconciliation goes on | Warning when it goes offline or its error changes, Debug while it stays offline |
| A read is answered with a UPnP fault | The unit stays connected and those values keep their last state | Debug once when the fault starts |
| Favorites cannot be read | The previous list stays | Warning once, Debug while it lasts |
| A subscription request fails | That service is kept current by polling | Warning once per subscription, Debug while it lasts |
| Subscriptions accepted but no event arrives within 15 seconds | `AreEventsActive` stays false; polling keeps the state current | Warning once, naming the callback address |
| The event port cannot be bound or no local address routes to the seed | Polling only, `ActiveEventCallbackHost` empty | Warning |
| An event body cannot be parsed | The event is skipped; it still counts as received | Warning |
| The event listener stops accepting requests | Polling only until the next reconnect | Error |
| An `UNSUBSCRIBE` fails | The speaker drops the subscription when it expires | Information (Debug for 412, already gone) |

A failure that repeats at every poll is logged at Warning only when it starts, so an unplugged speaker does not flood the log. A new player is logged at Information when it is found.

### Timeouts and Limits

| Value | Duration |
|-------|----------|
| HTTP request timeout (SOAP, descriptions, subscriptions) | 5 seconds |
| `SeedHost` probe | 5 seconds |
| Known speaker probe | 2 seconds each |
| SSDP search | 5 seconds |
| Requested subscription lifetime | 30 minutes, renewed at half the granted lifetime |
| First event deadline before the Warning | 15 seconds |
| Retry of a renewal without an answer | 30 seconds |
| Notification clip | 10 seconds, plus 2 seconds to close the websocket |
| Teardown | 2 seconds to wait for a running reconciliation, then 2 seconds for all unsubscribes |
| NOTIFY body | 1 MiB at most |
| Failed reconciliations before reconnecting | 3 in a row |

## Network and Firewall

| Direction | Protocol and port | Purpose | Needed |
|-----------|-------------------|---------|--------|
| HomeBlaze to speakers | TCP 1400 (HTTP) | SOAP control and polling, device descriptions, `SUBSCRIBE` and `UNSUBSCRIBE` | always |
| Speakers to HomeBlaze | TCP `EventPort` (6329 by default) | `NOTIFY` event delivery | for live events; without it the system polls |
| HomeBlaze to multicast `239.255.255.250` | UDP 1900, answers come back as unicast UDP | SSDP discovery | only when neither `SeedHost` nor a known speaker answers |
| HomeBlaze to speakers | TCP 1443 (TLS websocket) | `PlayNotification` | for notifications |
| Browser to speakers | TCP 1400 (HTTP) | Album art in the widgets | for album art |

The local Sonos API has no authentication: any device that reaches port 1400 can control the speakers. That is how Sonos works, not something HomeBlaze adds, but keep it in mind when you expose HomeBlaze's MCP endpoint or UI.

### Docker

The default compose file uses bridge networking, where multicast discovery does not work and the detected callback address is the container's. Set `SeedHost` to a speaker IP, set `EventCallbackHost` to the LAN IP of the Docker host, and publish the event port:

```yaml
services:
  homeblaze:
    ports:
      - "8080:8080"
      - "6329:6329"   # Sonos events, the EventPort of the SonosSystem
```

With `network_mode: host` on Linux, discovery and the detected address work and nothing has to be set; see [Installation](../administration/installation.md) for what else changes with host networking.

### Windows

The listener binds all interfaces (`http://+:<EventPort>/`), which `HttpListener` on Windows only allows with administrator rights or a URL reservation. Without either, the system runs on polling only and logs a Warning. To allow it for the account HomeBlaze runs as, run once in an elevated prompt:

```
netsh http add urlacl url=http://+:6329/ user=<account running HomeBlaze>
```

Linux, macOS and Docker need nothing.

## Troubleshooting

- **"No Sonos speaker found":** discovery uses SSDP multicast, which does not cross VLANs or Docker bridge networks and can be blocked by a firewall or the Wi-Fi access point. Set `SeedHost` to the IP of any speaker.
- **"The SeedHost '...' did not answer":** check that HomeBlaze reaches `http://<speaker>:1400/xml/device_description.xml`. A speaker that changed its address after a DHCP renewal needs a new `SeedHost`, or a DHCP reservation.
- **The rooms of another household show up:** the network has several Sonos households (for example S1 and S2) and SSDP found a speaker of another one. Set `SeedHost` to a speaker of the household to show.
- **"Events: polling only":** the speakers must reach `EventPort` on the HomeBlaze host, so allow inbound TCP on that port from the speakers. Under Docker bridge networking set `EventCallbackHost` to the host IP and publish `EventPort`; host networking works with the detected address. The Warning about events that did not arrive names the callback address the speakers were given; compare it with the address of the host. State still updates, through polling.
- **Events never start on Windows:** see [Windows](#windows).
- **Events stop after a while:** the listener stopped accepting requests (logged at Error). `Refresh` does not help, since it only polls; a configuration change or a HomeBlaze restart reconnects and starts a new listener.
- **Operations are disabled or fail with "not connected":** either the system is not connected, and its `StatusMessage` says why, or the room is offline: it does not answer, it left the topology, or it just appeared and was not polled yet. Its `StatusMessage` shows the last error. Operations on the group's playback also need the group coordinator to be connected. Over MCP these failures arrive as a generic error, and the HomeBlaze log has the details.
- **A new room shows as offline for a while:** a room found through a topology event is polled at the next reconciliation. Call `Refresh` to poll it at once.
- **`Next`, `Previous`, `Seek` or `SetShuffle` fail on TV or radio:** Sonos answers them with a UPnP fault for sources without a queue. `Seek` is disabled while the track has no duration; the others are not yet, see [Follow-ups](#follow-ups).
- **Favorites are empty or `PlayFavorite` reports an unknown favorite:** the error lists the known titles. Shortcut favorites are not listed, and favorites are only read while a room player is connected.
- **`PlayNotification` plays nothing:** the speaker must be S2 and able to download the sound URI itself (not `localhost`, not an address only HomeBlaze can reach), and it must reach TCP 1443 on the speaker. A rejected clip is not reported.
- **Album art does not show:** the browser loads it directly from the speaker over plain HTTP. When HomeBlaze is served over HTTPS, the browser blocks it as mixed content; it also fails when the browser cannot reach the speakers' network.
- **A group widget shows "Cannot resolve path":** the group is keyed by its coordinator, which changed. Embed the player of the room instead, see [Embed Widgets](#embed-widgets).
- **Night mode and speech enhancement do nothing or stay empty:** they exist only on home theater players (`IsHomeTheater`); the operations are disabled on other speakers.
- **Volume operations fail on a Port, Amp or Connect:** the line-out is set to fixed volume in the Sonos app, so the speaker rejects volume changes.

## Limitations

- `Sonos.Base`, used for the SOAP calls, parses responses with `XmlSerializer`, so this library is not yet free of runtime reflection for Native AOT. The library's own parsers use LINQ to XML.
- Album art is served by the speaker over plain HTTP, so the browser must be able to reach the speaker, and browsers block it as mixed content when HomeBlaze is served over HTTPS.
- On Windows, listening on all interfaces with `HttpListener` needs a URL reservation or administrator rights. Without either the system runs on polling only. Linux, macOS and Docker need nothing.
- Notification clips use the Sonos audio clip API, which needs S2 speakers. The speaker's answer is not read, so a clip it rejects, for example for a URL it cannot load, is not reported.
- `SetSpeechEnhancement` writes `DialogLevel` 1 or 0, the on and off values of most home theater players; the Arc Ultra levels 1 to 4 are read as on but cannot be set yet.
- Favorites of type "shortcut" (for example Sonos Radio station shortcuts) carry no URI and are not listed.
- `CurrentTrackPosition` and `SleepTimerRemaining` are only as current as the last poll or command read-back, because Sonos does not event them.
- A group is keyed by its coordinator, so its path changes when the coordinator changes.
- A speaker that changes role (a standalone speaker becomes a surround or stereo partner, or a subwoofer moves to another room) keeps its old subject as an offline entry until restart.
- If the event listener stops accepting requests (it logs an error), the system runs on polling only until the next reconnect, for example after a configuration change or three failed reconciliations in a row.
- SSDP discovery and the detection of the callback address use IPv4 only.
- The household is not identified by its household id, so two `SonosSystem` subjects with seeds in the same household show the same rooms twice.

## Follow-ups

Not implemented yet; each fits the current structure:

- Alarms: list Sonos alarms as state, with enable and disable operations (`AlarmClockService`).
- Shortcut favorites through the Sonos local websocket favorites API.
- Refreshing favorites when they change, through the `FavoritesUpdateID` of a ContentDirectory subscription, instead of at every poll.
- Queue editing, Sonos playlists and music library browsing beyond favorites.
- The queue position (`CurrentTrack` and `NumberOfTracks`, already in the events and the poll) and the next track (`NextTrackURI`, `NextTrackMetaData`).
- Crossfade: `Get/SetCrossfadeMode` on the coordinator, already evented as `CurrentCrossfadeMode`.
- Transport capabilities: `CurrentTransportActions` and `CurrentValidPlayModes` could disable `Next`, `Previous`, `Seek`, `Pause`, `SetShuffle` and `SetRepeat` on TV and radio, where Sonos answers them with a fault.
- Line-in level: `Get/SetLineInLevel` of the `AudioIn` service on players with `HasLineIn`.
- Home theater levels: `SubGain`, `SubEnabled`, `SurroundEnable`, `SurroundLevel`, `MusicSurroundLevel`, `SurroundMode`, `HeightChannelLevel` and `AudioDelay` through `Get/SetEQ`, and the Arc Ultra speech enhancement level 1 to 4 with `SpeechEnhanceEnabled` for on and off.
- Trueplay: `Get/SetRoomCalibrationStatus`.
- Fixed output: `OutputFixed` on Port, Connect and Amp, so volume operations are disabled where they fail.
- Household identity: the household id (`GetHouseholdID`, or `MuseHouseholdId` from the topology event) so several households can be told apart.
- Spotify tracks by id, as v1's `PlaySpotifyTrack` did. `PlayUri` passes a native Spotify URI through, but Sonos needs account metadata for it, so Spotify content is played through favorites today.
- Our own audio clip client for `PlayNotification` that reads the speaker's answer and reports a rejected clip.
- An album art proxy so artwork loads when HomeBlaze is served over HTTPS.
- Windows support for the event listener without a URL reservation, for example a raw socket listener.
- An AOT-clean SOAP client replacing `Sonos.Base`.
- `AvailableSoftwareUpdate`, from the topology event, which already carries it, or via `CheckForUpdate`.
- LED state and button lock settings.
- Home theater TV power state and other HTControl details.
- A widget for `SonosSatellite`.
- Opt-in live integration tests (`Category=Integration`, read-only, speaker IP from an environment variable).
- Upstream contributions to `Sonos.Base` (satellites in the topology model, `resMD` in DIDL) so the library's own parsers can shrink.

## References

- [Sonos UPnP API documentation](https://sonos.svrooij.io/) (unofficial, community maintained): the services [AVTransport](https://sonos.svrooij.io/services/av-transport), [RenderingControl](https://sonos.svrooij.io/services/rendering-control), [GroupRenderingControl](https://sonos.svrooij.io/services/group-rendering-control), [ZoneGroupTopology](https://sonos.svrooij.io/services/zone-group-topology) and [ContentDirectory](https://sonos.svrooij.io/services/content-directory), the [communication basics](https://sonos.svrooij.io/sonos-communication) (port 1400, SOAP, discovery) and [metadata](https://sonos.svrooij.io/metadata) (DIDL-Lite)
- [Sonos.Base (sonos-net)](https://github.com/svrooij/sonos-net) and its [NuGet package](https://www.nuget.org/packages/Sonos.Base/), the SOAP client used for commands and reads
- [Rssdp](https://github.com/Yortw/RSSDP), the SSDP client used for discovery
- [Sonos support: known limitations with separate S1 and S2 systems](https://support.sonos.com/en-us/article/known-limitations-with-separate-s1-and-s2-sonos-systems)
- [Source of the library](https://github.com/RicoSuter/Namotion.Interceptor/tree/master/src/HomeBlaze/Namotion.Devices.Sonos) and of its [widgets](https://github.com/RicoSuter/Namotion.Interceptor/tree/master/src/HomeBlaze/Namotion.Devices.Sonos.HomeBlaze)
