---
title: Sonos
icon: LibraryMusic
---

# Sonos

Controls a Sonos household over the speakers' local UPnP API on port 1400, without a Sonos account or cloud connection. One `SonosSystem` finds the speakers, shows every room as a `SonosPlayer` with its bonded units as `SonosSatellite` children and, for a soundbar, its home theater settings as a `SonosHomeTheater` child, and the current groups as `SonosGroup` subjects. UPnP events keep the state live. A poll every 30 seconds reads what events do not deliver, such as the position of a playing track, and every 5 minutes a full reconciliation repairs anything the events missed.

## Supported Systems

- **S2** is the target platform; every feature works on it.
- **S1** serves the same UPnP services (AVTransport, RenderingControl, GroupRenderingControl, ZoneGroupTopology, ContentDirectory), so it should work too, except `PlayNotification`, which needs S2.
- S1 and S2 systems in one home are separate households that Sonos cannot group ([Sonos support](https://support.sonos.com/en-us/article/known-limitations-with-separate-s1-and-s2-sonos-systems)). Add one `SonosSystem` per household, see [When to Set SeedHost](#when-to-set-seedhost).
- Speakers, soundbars, Port, Amp and portables are shown as rooms. Players whose device description lists `HTControl`, such as Arc, Beam and Ray, offer the TV input and get a `HomeTheater` child with night mode and speech enhancement. Players that list `AudioIn` offer line-in. Portables (Move, Roam) report their battery.
- Boost and Bridge units (`IsZoneBridge`) have nothing to play and are not shown.

## Discovery

The Discover page of the create dialog finds Sonos households over SSDP. Every speaker that answers names its household, so the speakers of one household, satellites included, are one result, and each household on the network is its own result. Picking one fills in `SeedHost` with the speaker that has the lowest RINCON ID, which pins the new system to that household. It does not change `EventPort`, so a second household picked there still needs its own `EventPort`, because each system binds its event listener on that port. A household that is already added is recognized by any player or satellite its system knows, or by the speaker its `SeedHost` names. A system without `SeedHost` that has not connected since HomeBlaze started knows no speakers yet, so its household shows as new until it connects.

## Configuration

Create a `SonosSystem` in the UI (group "Media") or as a JSON file such as `Devices/Sonos.json`. The type alone is enough on a flat home network:

```json
{
  "$type": "Namotion.Devices.Sonos.SonosSystem"
}
```

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `SeedHost` | string | null | Any speaker of the household, as `host`, `host:port`, an IPv6 literal or `[IPv6]:port`, without a scheme (port 1400 by default). Empty tries the speakers found since HomeBlaze started, then SSDP discovery. |
| `EventCallbackHost` | string | null | The address the speakers send events to. Empty detects the local IPv4 address that routes to the seed. |
| `EventPort` | int | 6329 | The port the event listener binds on all interfaces. |
| `PollingInterval` | TimeSpan | 30 seconds | How often the system polls. While events are live, a poll reads only what they do not deliver, see [Polling](#polling). |
| `RetryInterval` | TimeSpan | 30 seconds | The delay before reconnecting after a failed connection. |

Both intervals accept 5 seconds to 1 hour; values outside that range set in JSON are clamped, and zero or less uses 30 seconds. A long `PollingInterval` does not let events lapse, since subscriptions are renewed on their own schedule. Every change restarts the connection at once and keeps the players.

### When to Set SeedHost

Set it to the IP address of any speaker, ideally one with a DHCP reservation, when:

- **Multicast does not reach HomeBlaze**, for example under Docker bridge networking, across VLANs, or behind a firewall or access point that blocks it.
- **The network has more than one household**, for example S1 and S2, or a neighbor's speakers. SSDP takes whichever speaker answers first. For a second household, add a second `SonosSystem` with its own `SeedHost` and `EventPort`. A household picked on the Discover page gets its `SeedHost` filled in, see [Discovery](#discovery).
- **Startup should not wait for discovery.** Known speakers are kept in memory only, so after a restart a system without `SeedHost` starts with a 5 second SSDP search.

The seed only has to answer the first request; later the system moves on to another connected player when it stops answering.

### When to Set EventCallbackHost

The speakers send events to `http://<EventCallbackHost>:<EventPort>/event/...`, served by a listener inside the HomeBlaze process. Leave it empty when HomeBlaze runs directly on the network or with Docker host networking. Under Docker bridge networking the detected address is the container's, which the speakers cannot reach, so set it to the LAN IP of the Docker host and publish `EventPort` (for example `"6329:6329"`), see [Installation](../administration/installation.md#hardware-and-discovery). On a host with several interfaces or a VPN that wins the route to the speakers, set it to the IP the speakers can reach.

## Status

| `Status` | When | `StatusMessage` |
|----------|------|-----------------|
| Starting | Selecting the seed, starting the listener, first reconciliation | empty |
| Running | Connected | empty, "Reconciliation failed (n of 3): ..." while another speaker is tried, or "Events unavailable: ..." while the event listener does not run: its port could not be bound, no local IPv4 address routes to the seed, or it stopped accepting requests. Each is logged as a Warning once and the listener is tried again at every poll, see [Troubleshooting](#troubleshooting) |
| Error | The attempt failed; retried after `RetryInterval` | for example "No Sonos speaker found. ..." or "The SeedHost '...' did not answer, and no other Sonos speaker was found." |

`IsConnected` turns true after the first successful reconciliation. The system widget shows **Connected** and **Events: live** or **Events: polling only**, see [Event Subscriptions](#event-subscriptions).

## Subject Model

```
SonosSystem                                 the household
├── Players[RINCON_…]                       SonosPlayer, one per room
│     ├── HomeTheater                       SonosHomeTheater, only on home theater players
│     └── Satellites[RINCON_…]              SonosSatellite: subwoofer, surround or stereo partner
├── Groups[RINCON_<coordinator>]            SonosGroup, references into Players
└── Favorites[]                             SonosFavorite: Title, Uri, IsContainer, ImageUri
```

Players, satellites and groups are keyed by RINCON ids, the UPnP UUIDs Sonos assigns to each unit (`Uuid` in the subject browser). They survive renames and IP changes. Operations that take a room accept the room name (ignoring case) or the RINCON id.

### Players and Satellites

A player is a room as the Sonos app shows it, titled with model and room, for example "Sonos Arc (Living Room)". A satellite is a bonded unit that does not play on its own; it has identity, network and firmware state, and its room's player reports the playback. Its role comes from the topology's channel maps:

| `Role` | Source | Example |
|--------|--------|---------|
| `Subwoofer` | Home theater channel map lists `SW` | Sub, Sub Mini |
| `RearLeft`, `RearRight` | Home theater channel map lists `LR` or `RR` | Era 100 surrounds |
| `StereoPartner` | Invisible group member listed in the room's stereo channel map | second speaker of a stereo pair |
| `Other` | Any other channel; may be refined into new roles later | |

A satellite carries its player's room name and is titled with model, room and role, for example "Sonos Sub (Living Room, subwoofer)". Invisible members that are no stereo partner are skipped.

### Home Theater

Settings that only a home theater player has live on its `HomeTheater` child, so a plain room neither shows them nor offers their operations. The child exists when the device description lists `HTControl` and is empty (null) otherwise, which `IsHomeTheater` also tells. It is created once, when the description is first read, and kept across polls and reconnects. Its path is `Players[RINCON_…]/HomeTheater`, for example `Players[RINCON_…]/HomeTheater/NightMode`.

### Groups

Every player belongs to exactly one group; an ungrouped room is a group of one. A group is keyed by the RINCON id of its coordinator, because Sonos's own group id changes on every regroup and is not exposed. Its path therefore stops resolving when another room becomes coordinator.

Playback state, track details, position, source, source title, shuffle, repeat and sleep timer are per group in Sonos, and a grouped member itself only reports a transport that points at its coordinator. Every member player therefore reports its coordinator's values, and the operations that set them go to the coordinator. A room shows what it plays on its own path, whatever it is grouped with. When it leaves the group or becomes its coordinator, the pointer it reported as a member is forgotten, so its playback state, track and source are empty until an event or the next poll delivers its own. Volume, mute and equalizer stay per room.

A group reports its coordinator's playback, track, source, play mode and sleep timer, and its own volume and mute (GroupRenderingControl).

### Offline Handling

- A player that leaves the topology keeps its subject and last state with `IsConnected = false` until it returns; only a restart removes it. A topology that misses players is applied only when the next one misses them too, since a rebooting seed can report part of the household. The topology reads a grouping command waits with are skipped while they miss players and do not count toward the two.
- While a player is not connected, its `PlaybackState` and `IsPlaying` are empty (unknown), so an unplugged speaker does not keep reporting that it plays once it is offline, and its going offline does not read as playback that stopped. The same holds for a grouped member while its coordinator is not connected, and a group follows its coordinator. Everything else keeps its last value, including the track, source, volume and settings, and `IsConnected` tells that it may be stale. The playback state returns with the next successful poll that reads it; the one from before the outage is forgotten.
- A new player is offline until its first poll succeeds. One found through a topology event is polled at the next poll.
- A transport failure (timeout, connection refused) of two polls in a row takes a unit offline with the error in `StatusMessage`. While events are live, a poll contacts only the seed and the coordinators of playing groups, see [Polling](#polling), so a speaker that loses power unnoticed by events, for example a member of a playing group or an idle room, keeps its last state, `Playing` included, until Sonos drops it from the topology or the full reconciliation polls it. The full reconciliation runs at the first poll at or after 5 minutes since the last one, and the second failed poll follows one polling interval later, so this takes at most 5 minutes plus two polling intervals, about 5.5 minutes with the defaults, where 30 seconds divides 5 minutes. A single failed poll changes nothing and is logged at Debug, so a lost request neither blanks the playback state nor disables operations. A UPnP fault answer counts as answered; the values of that read keep their last state. So does a single value that an answer leaves out or holds in a form that cannot be read.
- When the connection is torn down, every unit reports "The Sonos system is disconnected."

## Widgets

`SonosSystem` lists its rooms with what they play, `SonosPlayer` shows track, album art, playback controls, mute and volume, with night mode and speech enhancement switches on a home theater player, and `SonosGroup` shows its rooms, track and group volume. The player and group widgets show the track title, without one the `SourceTitle`, and without either "Playing" or "Nothing playing", since sources such as TV and line-in play without a title. Embed them with a `Widget` block as described in [Widget](../administration/widgets/widget.md), for example a room of a system stored as `Devices/Sonos.json`:

~~~~markdown
:: HomeBlaze.Components.Widget#livingRoom Path=/Devices/Sonos/Players[RINCON_000E58A0B1C201400]
::
~~~~

Prefer player or system widgets over group widgets: a group widget shows "Cannot resolve path" once its coordinator changes, while a player shows what its group plays on a path that stays.

## Interfaces

| Subject | Interfaces |
|---------|------------|
| `SonosSystem` | `IHubDevice`, `IConfigurable`, `IMonitoredService`, `IConnectionState`, `ILastUpdatedProvider`, `ITitleProvider`, `IIconProvider` |
| `SonosPlayer` | `IAudioPlayer`, `IBatteryState`, `IDeviceInfo`, `INetworkAdapter`, `ISoftwareState`, `IConnectionState`, `ITitleProvider`, `IIconProvider` |
| `SonosHomeTheater` | `ITitleProvider`, `IIconProvider` |
| `SonosSatellite` | `IDeviceInfo`, `INetworkAdapter`, `ISoftwareState`, `IConnectionState`, `ITitleProvider`, `IIconProvider` |
| `SonosGroup` | `IAudioPlayer`, `IVirtualSubject`, `ITitleProvider`, `IIconProvider` |

`IAudioPlayer` composes the capability interfaces `IMediaPlaybackState`, `IMediaPlaybackController`, `IVolumeState`, `IVolumeController` and `IMediaTrackState`, so a search for any of them finds players and groups.

The speakers do not report `SubnetMask`, `Gateway`, `SignalStrength` or `AvailableSoftwareUpdate`, so these stay empty.

## State

### SonosSystem

| Property | Description |
|----------|-------------|
| `Players`, `Groups`, `Favorites` | See [Subject Model](#subject-model) and [Favorites](#favorites) |
| `AreEventsActive` | True once at least one subscription delivered an event |
| `ActiveEventCallbackHost` | The host the speakers were told to send events to; empty when the listener could not start or no local address routes to the seed |
| `LastUpdated` | When the last poll completed |

### SonosPlayer

| Property | Unit | Description |
|----------|------|-------------|
| `Uuid`, `RoomName` | | RINCON id and room name |
| `PlaybackState`, `IsPlaying` | | `Stopped`, `Playing`, `Paused` or `Buffering` (Sonos's transitioning) of the room's group; empty while unknown, which includes while offline, see [Offline Handling](#offline-handling). `IsPlaying` is true for playing and buffering, false for paused and stopped, and empty while unknown |
| `Volume`, `IsMuted` | 0..1 | This room |
| `CurrentTrackTitle`, `CurrentTrackArtist`, `CurrentTrackAlbum`, `CurrentTrackUri` | | The track of the room's group, see [Track Details](#track-details) |
| `CurrentTrackImageUri` | URI | Album art; Sonos's relative `/getaa?...` paths are resolved against the coordinator, which serves it |
| `CurrentTrackDuration` | TimeSpan | Empty for streams, TV and line-in |
| `CurrentTrackPosition` | TimeSpan | As of the last poll or command read-back; Sonos does not event it, so every poll reads it while the group plays, and the next poll after an AVTransport event of the coordinator, such as a pause or a track change, reads it once |
| `Source` | | `None` (nothing loaded), `Tv`, `LineIn`, `SpotifyConnect`, `AirPlay`, `Radio`, `Queue` or `Other`, from the coordinator's transport URI; empty until it was read. `Other` may be refined into new values later |
| `SourceTitle` | | The station, playlist or album the group plays, not the track. For a queue it is the playlist or album enqueued last, which the current track need not come from |
| `Shuffle`, `Repeat` | | Group play mode; `Repeat` is `Off`, `All` or `One` |
| `SleepTimerRemaining` | TimeSpan | Group sleep timer as of the last poll; empty when none runs. Sonos does not event it, so while events are live every poll reads it only while the group plays or a timer runs, and a timer set on a paused group shows with the next full reconciliation |
| `Bass`, `Treble` | -10..10 | Equalizer of this room |
| `Loudness` | | Loudness compensation of this room |
| `HomeTheater` | | The [home theater](#home-theater) settings; empty on other players |
| `GroupCoordinatorUuid`, `IsGroupCoordinator` | | Group membership |
| `IsHomeTheater`, `HasLineIn` | | Device description lists `HTControl` (the `HomeTheater` child exists) or `AudioIn` |
| `BatteryLevel` | 0..1 | Portables only, from the topology |
| `IsCharging` | | Portables only |
| `Model`, `ProductCode`, `SerialNumber`, `MacAddress`, `HardwareRevision`, `SoftwareVersion` | | From the device description and `GetZoneInfo`; `SoftwareVersion` is the version the Sonos app shows |
| `IpAddress`, `IsWireless` | | From the topology |

### SonosHomeTheater

| Property | Description |
|----------|-------------|
| `NightMode` | Whether night mode is on |
| `SpeechEnhancement` | Whether speech enhancement is on; any dialog level above zero counts as on |

Both are empty until first read and keep their last value while the player is offline.

### SonosSatellite

`Role`, `Uuid`, `RoomName` (of its player), identity and firmware (read when it appears, after a firmware or address change and after a reconnect), `IpAddress`, `MacAddress`, `IsWireless`, `IsConnected` and `StatusMessage`.

### SonosGroup

`Coordinator` and `Members` (references to players), group `Volume` and `IsMuted`, and the coordinator's `PlaybackState`, `IsPlaying`, track properties, `Source`, `SourceTitle`, `Shuffle`, `Repeat` and `SleepTimerRemaining`, so everything a group operation sets can be read back on the group. The title joins the room names, for example "Kitchen + Living Room".

## Operations

"Coordinator" means the command goes to the group's coordinator and affects the whole group; "player" means this room only. Volumes are fractions (0 to 1, -1 to 1 for `delta`), rounded to whole Sonos percent.

### SonosPlayer

| Operation | Parameters | Target | Notes |
|-----------|------------|--------|-------|
| `Play`, `Pause`, `Stop`, `Next`, `Previous`, `TogglePlayback` | | coordinator | |
| `Seek` | `position` | coordinator | Enabled only while the group's track has a duration |
| `SetVolume`, `ChangeVolume`, `RampVolume` | `volume` or `delta` | player | `RampVolume` uses Sonos's sleep timer ramp |
| `Mute`, `Unmute` | | player | |
| `PlayFavorite` | `title` | coordinator | See [Favorites](#favorites) |
| `PlayUri`, `PlayStream` | `uri`, `title` (stream, optional) | coordinator | See [Playing URIs](#playing-uris-streams-and-notifications) |
| `PlayNotification` | `soundUri`, `volume` | player | S2 only |
| `SwitchToTv`, `SwitchToLineIn` | | player | Home theater or line-in players only; disabled and failing on others |
| `SetShuffle`, `SetRepeat` | `shuffle`, `repeat` | coordinator | Each keeps the other half of the play mode |
| `SetSleepTimer` | `duration` (0 to 23:59:59) | coordinator | Zero cancels it |
| `SetBass`, `SetTreble`, `SetLoudness` | -10 to 10, bool | player | |
| `JoinGroup` | `room` | player | Joins the group of the given room |
| `LeaveGroup` | | player | Enabled in groups of two or more rooms, also on the coordinator |

### SonosHomeTheater

| Operation | Parameters | Target | Notes |
|-----------|------------|--------|-------|
| `SetNightMode` | bool | player | |
| `SetSpeechEnhancement` | bool | player | Writes `DialogLevel` 1 or 0 |

### SonosGroup

`Play`, `Pause`, `Stop`, `Next`, `Previous`, `TogglePlayback`, `Seek`, `PlayFavorite`, `PlayUri`, `PlayStream`, `SetShuffle`, `SetRepeat` and `SetSleepTimer` as on a player, sent to the coordinator. `SetVolume` and `ChangeVolume` set the group volume and send `SnapshotGroupVolume` first, so the rooms keep their current ratio and a room changed in the Sonos app does not jump back. `Mute` and `Unmute` affect every room. Per-room settings exist on players only.

### SonosSystem

| Operation | Parameters | Description |
|-----------|------------|-------------|
| `Refresh` | | Runs a full reconciliation now |
| `GroupAll` | `room` | Groups every connected room with the given room, which becomes coordinator |
| `UngroupAll` | | Makes every connected room standalone |

Operations are disabled while the system, the target or, for coordinator operations, the coordinator is not connected; the home theater operations follow their player. An unknown room or favorite fails with the list of known names. After each command the players of the commanded group are read back, also when it failed, so the result shows without waiting for events; grouping commands run a full reconciliation instead. Sonos regroups after it has answered the command, so a successful grouping command first re-reads the topology, for about 2 seconds at most, until it shows the new groups.

## Behavior

### Favorites

`Favorites` lists the household's "My Sonos" favorites. While the ContentDirectory subscription is live, a favorite added or removed in the Sonos app shows within seconds: the speaker that holds the subscription sends an event whenever the favorites change, also when they were changed through another speaker, and the system reads them again. Renames are expected to work the same way, but that has not been confirmed. The seed holds the subscription. When the seed does not match the address of a visible player, because it is a satellite, which may not send these events, or `SeedHost` names it by a DNS name, the connected player with the lowest RINCON id holds it instead, and it moves to the next one while that player is not connected. The app can take a moment to save a change to the speakers. The favorites are also read at every full reconciliation, so every 5 minutes, as a safety net for a lost event, and with `Refresh`. While that subscription is not live, they are read at every full reconciliation, and at every poll while the topology subscription is not live either, so a change can take up to 5 minutes to show and to be found by `PlayFavorite`.

- Satellites answer the favorites `Browse` (`FV:2`) with HTTP 500, so the list is read through a connected room player, coordinators first, trying the next one on failure. When none answers, the previous list stays.
- Favorites are read in pages of 100, so large lists are complete, up to 20 pages, which is far more than a household has. A speaker that still reports more to come after that is logged as a Warning once, and the favorites read so far are used.
- Favorites of type "shortcut", such as Sonos Radio station shortcuts, carry no URI and are skipped.
- `PlayFavorite` matches the title ignoring case; with duplicate titles the first one plays. A container favorite (playlist, album) replaces the queue and plays it; any other favorite becomes the transport URI without touching the queue. Both are sent with the favorite's DIDL metadata, which music services need and which is kept internal because it often contains account tokens.

### Playing URIs, Streams and Notifications

| Operation | Sonos mechanism | Use for |
|-----------|-----------------|---------|
| `PlayUri` | `SetAVTransportURI` with the URI unchanged, then `Play` | A finite file such as an mp3, or a native Sonos URI (`x-sonos-...`, `x-rincon...`, `x-file-cifs:`). Plays once as a track with duration and seek bar. |
| `PlayStream` | `SetAVTransportURI` with `x-rincon-mp3radio:` and a radio DIDL item, then `Play` | Internet radio and live streams. Accepts `http://`, `https://` and `x-rincon-mp3radio:`, rewrites http(s) to `x-rincon-mp3radio` so Sonos shows it as radio, and sends an empty title when none is given. Sonos reconnects the stream when it ends. |
| `PlayNotification` | Audio clip API of the speaker's websocket (`wss://<speaker>:1443/websocket/api`) | Doorbells and announcements over the current playback, which then resumes. The speaker downloads the sound itself, so use an absolute http(s) URL it can reach, not `localhost`. The volume is at least 1 % and the call times out after 10 seconds. |

`Source` reports `Radio` for radio schemes such as `x-rincon-mp3radio:`, `x-sonosapi-stream:`, `aac:` and `hls-radio:`, so a stream started with `PlayStream` shows as radio, and `Other` for a plain http(s) file started with `PlayUri`.

### Track Details

When the source or track changes (a new transport or track URI), track values that Sonos does not report for the new one are cleared rather than kept from the previous one, and the position is cleared until the next poll. While the track stays the same, unknown values keep the current ones: polls can answer `NOT_IMPLEMENTED` for details the events delivered, and radio polls omit the album art its events delivered. For a Spotify Connect or AirPlay session, a poll describes the session rather than what it plays: the session as the track URI, the service name as the source title, and no shuffle or repeat. The track URI, the playlist name and the play mode that the session's events delivered are therefore kept while that session plays. An event that reports the same track without album art clears it, because a stream keeps its track URI from song to song.

Radio keeps one track URI from song to song. On radio `CurrentTrackTitle` is the song the station reports (often "ARTIST - TITLE"), otherwise the title Sonos reports for the stream. A `ZPSTR_` placeholder such as `ZPSTR_CONNECTING` or `ZPSTR_BUFFERING` keeps the current title and album art on the same stream and clears them when the stream starts or the station changes.

A title that only repeats the URL is left empty:

- `CurrentTrackTitle` and `SourceTitle` when they are the radio or http(s) URL itself, with or without its scheme, as other controllers write for a stream without a title.
- `CurrentTrackTitle` when it is the end of the stream URL's path, with or without the query (`96` for `.../aac/96`), which Sonos reports when the station sends no song. This applies to a track with a radio scheme and to an http(s) track that plays while the media is a station, such as an ad before the stream. `SourceTitle` still names the station.

Everything else is kept. A `SourceTitle` that equals the end of the URL stays, since it may be the title given to `PlayStream`, and an http(s) file started with `PlayUri` shows its file name, the only title an untagged file has.

## How It Works

### Seed Selection

The seed is the speaker the household topology (`GetZoneGroupState`) is read from. Each connection attempt tries `SeedHost` (5 second timeout), then the speakers known since startup (2 seconds each, those in the last topology first), then an SSDP M-SEARCH for `urn:schemas-upnp-org:device:ZonePlayer:1` on every IPv4 interface for 5 seconds, accepting the first `uuid:RINCON_` answer. While connected, a failed poll moves to another connected player; three failures in a row tear the connection down and rebuild it.

### Polling

A full reconciliation reads the topology, polls every player in parallel (about a dozen SOAP requests per room), reads satellite identity when needed, reads the favorites and maintains the subscriptions. The first poll of a connection, every poll at least 5 minutes after the last full reconciliation, `Refresh` and the grouping commands are full reconciliations. Every other poll reads only what events do not deliver:

| What | While events are live | Otherwise |
|------|-----------------------|-----------|
| Topology (`GetZoneGroupState` from the seed) | every poll | every poll |
| Position (`GetPositionInfo`) | every poll, of each playing or buffering group, and once after each AVTransport event of a coordinator | every poll |
| Sleep timer (`GetRemainingSleepTimerDuration`) | every poll, of each group that plays or has a timer running | every poll |
| Everything else a player reports, see [Event Subscriptions](#event-subscriptions) | every full reconciliation | every poll |
| Favorites | after each ContentDirectory event that reports a change, and every full reconciliation | every full reconciliation, and every poll while the topology subscription is not live |
| Satellite and player identity and firmware | when a unit appears, after a firmware or address change and after a reconnect | the same |

The events of a player are live while it answered its last poll, its identity is read, and its AVTransport, RenderingControl and, for a coordinator, GroupRenderingControl subscriptions are live. A subscription is live while the speaker has accepted it, an event arrived through it, its last renewal was answered, and the lifetime the speaker granted has not ended. Favorites follow the ContentDirectory subscription, see [Favorites](#favorites); a gap in its `SEQ` numbers, an event that could not be applied, or a read after an event that no player answered makes the next poll read them, once. Anything else is polled fully from the next poll on: a player whose subscription failed, lapsed or whose renewal went unanswered or was rejected, a new player until its first event, a player whose poll failed, and every player while the event listener does not run. A player is also polled fully once when one of its events could not be applied, or when the `SEQ` numbers of its events show a gap or start over, since an event was lost. A poll that fails, or in which a player does not answer the position or sleep timer read, makes the next poll a full reconciliation, because events sent meanwhile may be lost.

Speakers do not report that they dropped a subscription. A player that is missing from a topology, also one that is not applied yet, or whose boot sequence (`BootSeq`) changed may have rebooted or failed to send events, so it is polled fully and subscribed again, which makes it send its full state; for the seed that includes the topology and ContentDirectory subscriptions. The first event of a topology subscription follows the poll that made it within milliseconds, so it never counts as the second read that takes missing players offline. The topology is read at every poll because a single request notices a seed that stopped answering or rebooted, which no event reports. The full reconciliation every 5 minutes repairs whatever a lost event left behind, including events that stop reaching HomeBlaze while the speakers still accept the renewals.

Position and sleep timer are never evented, so polls and command read-backs are their only source. Events and polls share one monotonic sequence, so a poll that started before a newer event never overwrites it.

### Event Subscriptions

| Service | Subscribed on | Delivers |
|---------|---------------|----------|
| AVTransport | every player | playback state, play mode, media and track metadata |
| RenderingControl | every player | volume, mute, equalizer, night mode, dialog level |
| GroupRenderingControl | every coordinator | group volume and mute |
| ZoneGroupTopology | the seed | the whole topology on every change |
| ContentDirectory | the seed, or the connected player with the lowest RINCON id when the seed matches no visible player's address (a satellite or a DNS name) | `FavoritesUpdateID` (the speaker that made the change and a count per household) and `ContainerUpdateIDs` (with an `FV:2` entry for the favorites); the favorites are read again when either changes and after the first event of a subscription, see [Favorites](#favorites) |

Subscriptions request a 30 minute lifetime and are renewed at half the lifetime the speaker grants, independent of `PollingInterval`. A rejected renewal subscribes again; an unanswered one is retried after 30 seconds. A subscription only proves the speaker accepted it, so **Events: live** (`AreEventsActive`) means at least one event actually arrived. When no event arrives within 15 seconds of an accepted subscription, a Warning names the callback address. On stop, removal or any configuration change, all subscriptions are cancelled with `UNSUBSCRIBE` within 2 seconds; one that cannot be cancelled expires on the speaker. The listener answers unknown subscriptions with 412 so speakers drop stale ones.

## Network and Firewall

| Direction | Port | Purpose |
|-----------|------|---------|
| HomeBlaze to speakers | TCP 1400 | Control, polling, device descriptions, `SUBSCRIBE` and `UNSUBSCRIBE`; always needed |
| Speakers to HomeBlaze | TCP `EventPort` (6329) | Event delivery; without it the system polls only |
| HomeBlaze to `239.255.255.250` | UDP 1900, unicast answers | SSDP discovery; on the Discover page, and when neither `SeedHost` nor a known speaker answers |
| HomeBlaze to speakers | TCP 1443 | `PlayNotification`, over TLS; the certificate of the speaker is not validated, since no public authority issues it |
| Browser to speakers | TCP 1400 | Album art in the widgets |

The local Sonos API has no authentication: anything that reaches port 1400 can control the speakers.

On Windows, `HttpListener` binds all interfaces only with administrator rights or a URL reservation; without either, the system polls only and says so in `StatusMessage`. Reserve the port once in an elevated prompt:

```
netsh http add urlacl url=http://+:6329/ user=<account running HomeBlaze>
```

## Troubleshooting

- **"No Sonos speaker found":** multicast discovery is blocked (VLAN, Docker bridge, firewall, access point). Set `SeedHost`.
- **"The SeedHost '...' did not answer":** check that HomeBlaze reaches `http://<speaker>:1400/xml/device_description.xml`. Give the speaker a DHCP reservation.
- **Rooms of another household show up:** SSDP found a speaker of another household. Set `SeedHost`.
- **"Events unavailable: port ... could not be bound":** another process holds `EventPort`, or on Windows the URL reservation is missing. The system polls only, logs a Warning once and tries the port again at every poll, so events start once it is free; change `EventPort` to use another one.
- **"Events: polling only":** allow inbound TCP on `EventPort` from the speakers, and under Docker bridge networking set `EventCallbackHost` and publish the port. Compare the callback address in the Warning with the host's address. On Windows, see [Network and Firewall](#network-and-firewall).
- **Events stop after a while:** the listener stopped accepting requests (logged at Error). The system polls only and says so in `StatusMessage`, and the next poll binds the port again; the existing subscriptions keep their callback, so events resume, and a gap in their sequence numbers makes the affected players poll fully once.
- **"not connected":** check the `StatusMessage` of the system and the room. Coordinator operations also need the coordinator to be connected. A room that just appeared through an event is polled at the next poll, or at once with `Refresh`.
- **`Next`, `Previous` or `SetShuffle` fail on TV or radio:** Sonos answers them with a UPnP fault for sources without a queue.
- **`PlayNotification` plays nothing:** the speaker must be S2, reach the sound URL itself and be reachable on TCP 1443. A rejected clip is not reported.
- **Album art does not show:** the browser loads it from the speaker over plain HTTP, so it fails when the browser cannot reach the speakers or blocks it as mixed content on an HTTPS page. The widget shows a placeholder icon.
- **Volume operations fail on a Port, Amp or Connect:** the line-out is set to fixed volume in the Sonos app.

## Limitations

- `SetSpeechEnhancement` writes `DialogLevel` 1 or 0; the Arc Ultra levels 1 to 4 are read as on but cannot be set.
- An answer larger than 4 MB is a failed request, an event larger than 1 MB is refused, a document that nests more than 64 levels of elements is read as malformed, and favorites past 20 pages are not read, see [Favorites](#favorites). Speakers stay far below all four. The limits keep a broken or hostile device on the network from stalling HomeBlaze with an oversized, deeply nested or endless answer, and a document within them is read in time that grows in step with its size.
- A speaker that changes role (becomes a surround or stereo partner, or a subwoofer moves rooms) keeps its old subject as an offline entry until restart.
- SSDP discovery and callback address detection use IPv4 only.
- The household is not identified by its household id, so two systems seeded in the same household show the same rooms twice.

## Follow-ups

- Alarms as state, with enable and disable operations (`AlarmClockService`).
- Shortcut favorites through the Sonos local websocket favorites API.
- Queue editing, Sonos playlists and music library browsing.
- Queue position (`CurrentTrack`, `NumberOfTracks`) and next track (`NextTrackURI`, `NextTrackMetaData`).
- Crossfade (`Get/SetCrossfadeMode`, already evented).
- Disabling `Next`, `Previous`, `Seek`, `Pause`, `SetShuffle` and `SetRepeat` from `CurrentTransportActions` and `CurrentValidPlayModes`.
- Line-in level (`Get/SetLineInLevel`).
- Home theater levels on `HomeTheater` (`SubGain`, `SubEnabled`, `SurroundEnable`, `SurroundLevel`, `MusicSurroundLevel`, `SurroundMode`, `HeightChannelLevel`, `AudioDelay`) and the Arc Ultra speech enhancement levels.
- Trueplay (`Get/SetRoomCalibrationStatus`).
- Fixed output (`OutputFixed`) to disable volume operations on Port, Connect and Amp.
- A `HouseholdId` setting that pins the system to one household (`GetHouseholdID` or `MuseHouseholdId`), so discovery cannot attach to another one.
- Spotify tracks by id, as v1's `PlaySpotifyTrack` did; today Spotify content plays through favorites.
- Reporting a clip that the speaker rejects, by reading its answer on the audio clip websocket.
- Album art served through HomeBlaze for HTTPS pages, or the favorite's HTTPS `ImageUri` for radio.
- A Windows event listener that needs no URL reservation.
- `AvailableSoftwareUpdate` from the topology event or `CheckForUpdate`.
- LED state, button lock, and home theater TV power state.
- A widget for `SonosSatellite`.
- Live track progress in the widgets, counted up from the last polled position while playing, since the position is empty for up to one polling interval after a track starts.
- A read-back after a command limited to the affected player instead of its whole group.
- Satellite reads overlapped with the player polls, and known seed speakers probed concurrently instead of one after another.
- NOTIFY bodies parsed with an `XmlReader` without the intermediate string copies.
- Opt-in read-only live integration tests (`Category=Integration`).
- The track position as a pair of position and the time it was read, so a consumer can count it up.
- `AddRoom` and `RemoveRoom` on a group, to group from the target's side.
- `PauseAll` on the system.
- `VolumeUp` and `VolumeDown` without parameters, as safe volume steps.
- Confirmation before `GroupAll` and `UngroupAll`.
- A setting that turns the event listener off, for networks where the speakers cannot reach HomeBlaze.
- Excluded rooms: a setting for rooms that are not shown and that `GroupAll` skips.
- A volume cap: a setting for the highest volume any operation sets.
- Offline players for the topology's `VanishedDevices`, so a sleeping portable has its subject after a restart, and a `Forget` operation that removes a missing player or satellite.
- Serialized play mode writes, so parallel `SetShuffle` and `SetRepeat` calls cannot undo each other.
- A pure track presentation resolver that holds the track title, album art and source title rules outside `SonosPlayer`.

## References

- [Sonos UPnP API documentation](https://sonos.svrooij.io/) (unofficial, community maintained)
- [Rssdp](https://github.com/Yortw/RSSDP), the SSDP client
- [MCP server](https://github.com/RicoSuter/Namotion.Interceptor/blob/master/docs/mcp.md) for controlling the household from AI agents
- [Source of the library](https://github.com/RicoSuter/Namotion.Interceptor/tree/master/src/HomeBlaze/Namotion.Devices.Sonos) and of its [widgets](https://github.com/RicoSuter/Namotion.Interceptor/tree/master/src/HomeBlaze/Namotion.Devices.Sonos.HomeBlaze)
