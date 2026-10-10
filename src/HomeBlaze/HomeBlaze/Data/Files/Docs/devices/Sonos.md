---
title: Sonos
icon: LibraryMusic
---

# Sonos

Controls a Sonos household over the speakers' local UPnP API on port 1400, without a Sonos account or cloud connection. One `SonosSystem` finds the speakers, shows every room as a `SonosPlayer` with its bonded units as `SonosSatellite` children, and the current groups as `SonosGroup` subjects. UPnP events keep the state live, and a poll every 30 seconds reconciles anything the events missed.

## Supported Systems

- **S2** is the target platform; every feature works on it.
- **S1** serves the same UPnP services (AVTransport, RenderingControl, GroupRenderingControl, ZoneGroupTopology, ContentDirectory), so it should work too, except `PlayNotification`, which needs S2.
- S1 and S2 systems in one home are separate households that Sonos cannot group ([Sonos support](https://support.sonos.com/en-us/article/known-limitations-with-separate-s1-and-s2-sonos-systems)). Add one `SonosSystem` per household, see [When to Set SeedHost](#when-to-set-seedhost).
- Speakers, soundbars, Port, Amp and portables are shown as rooms. TV input, night mode and speech enhancement are offered on players whose device description lists `HTControl`, line-in on players that list `AudioIn`. Portables (Move, Roam) report their battery.
- Boost and Bridge units (`IsZoneBridge`) have nothing to play and are not shown.

## Configuration

Create a `SonosSystem` in the UI (category "Devices") or as a JSON file such as `Devices/Sonos.json`. The type alone is enough on a flat home network:

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
| `PollingInterval` | TimeSpan | 30 seconds | How often topology, player state and favorites are reconciled. |
| `RetryInterval` | TimeSpan | 30 seconds | The delay before reconnecting after a failed connection. |

Both intervals accept 5 seconds to 1 hour; values outside that range set in JSON are clamped, and zero or less uses 30 seconds. A long `PollingInterval` does not let events lapse, since subscriptions are renewed on their own schedule. Every change restarts the connection at once and keeps the players.

### When to Set SeedHost

Set it to the IP address of any speaker, ideally one with a DHCP reservation, when:

- **Multicast does not reach HomeBlaze**, for example under Docker bridge networking, across VLANs, or behind a firewall or access point that blocks it.
- **The network has more than one household**, for example S1 and S2, or a neighbor's speakers. SSDP takes whichever speaker answers first. For a second household, add a second `SonosSystem` with its own `SeedHost` and `EventPort`.
- **Startup should not wait for discovery.** Known speakers are kept in memory only, so after a restart a system without `SeedHost` starts with a 5 second SSDP search.

The seed only has to answer the first request; later the system moves on to another connected player when it stops answering.

### When to Set EventCallbackHost

The speakers send events to `http://<EventCallbackHost>:<EventPort>/event/...`, served by a listener inside the HomeBlaze process. Leave it empty when HomeBlaze runs directly on the network or with Docker host networking. Under Docker bridge networking the detected address is the container's, which the speakers cannot reach, so set it to the LAN IP of the Docker host and publish `EventPort` (for example `"6329:6329"`), see [Installation](../administration/installation.md#hardware-and-discovery). On a host with several interfaces or a VPN that wins the route to the speakers, set it to the IP the speakers can reach.

## Status

| `Status` | When | `StatusMessage` |
|----------|------|-----------------|
| Starting | Selecting the seed, starting the listener, first reconciliation | empty |
| Running | Connected | empty, or "Reconciliation failed (n of 3): ..." while another speaker is tried |
| Error | The attempt failed; retried after `RetryInterval` | for example "No Sonos speaker found. ..." or "The SeedHost '...' did not answer, and no other Sonos speaker was found." |

`IsConnected` turns true after the first successful reconciliation. The system widget shows **Connected** and **Events: live** or **Events: polling only**, see [Event Subscriptions](#event-subscriptions).

## Subject Model

```
SonosSystem                                 the household
├── Players[RINCON_…]                       SonosPlayer, one per room
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
| `Other` | Any other channel | |

A satellite carries its player's room name and is titled with model, room and role, for example "Sonos Sub (Living Room, subwoofer)". Invisible members that are no stereo partner are skipped.

### Groups

Every player belongs to exactly one group; an ungrouped room is a group of one. A group is keyed by the RINCON id of its coordinator, because Sonos's `GroupId` changes on every regroup. Its path therefore stops resolving when another room becomes coordinator.

Transport and track state of a group are the coordinator's; volume and mute are the group's own (GroupRenderingControl). Source, shuffle, repeat, sleep timer and media title are per group in Sonos, so a member player reports its coordinator's values, and the operations that set them go to the coordinator.

### Offline Handling

- A player that leaves the topology keeps its subject and last state with `IsConnected = false` until it returns; only a restart removes it. A topology that misses players is applied only when the next one misses them too, since a rebooting seed can report part of the household.
- A new player is offline until its first poll succeeds. One found through a topology event is polled at the next reconciliation.
- A transport failure (timeout, connection refused) takes a unit offline with the error in `StatusMessage`. A UPnP fault answer does not; the values of that read keep their last state.
- When the connection is torn down, every unit reports "The Sonos system is disconnected."

## Widgets

`SonosSystem` lists its rooms with what they play, `SonosPlayer` shows track, album art, transport, mute and volume, and `SonosGroup` shows its rooms, track and group volume. Embed them as described in [Markdown Pages](../administration/pages.md#widget-rendering), for example a room of a system stored as `Devices/Sonos.json`:

<!-- The backticks are HTML entities so that this page shows the block instead of instantiating it: HomeBlaze turns every subject block of a page into a live subject, also inside code blocks. -->
<pre><code>&#96;&#96;&#96;subject(livingRoom)
{
  "$type": "HomeBlaze.Components.Widget",
  "path": "/Devices/Sonos/Players[RINCON_000E58A0B1C201400]"
}
&#96;&#96;&#96;
</code></pre>

Prefer player or system widgets over group widgets: a group widget shows "Cannot resolve path" once its coordinator changes.

## Interfaces

| Subject | Interfaces |
|---------|------------|
| `SonosSystem` | `IHubDevice`, `IConfigurable`, `IMonitoredService`, `IConnectionState`, `ILastUpdatedProvider`, `ITitleProvider`, `IIconProvider` |
| `SonosPlayer` | `IAudioPlayer`, `IMediaTrackState`, `IBatteryState`, `IDeviceInfo`, `INetworkAdapter`, `ISoftwareState`, `IConnectionState`, `ITitleProvider`, `IIconProvider` |
| `SonosSatellite` | `IDeviceInfo`, `INetworkAdapter`, `ISoftwareState`, `IConnectionState`, `ITitleProvider`, `IIconProvider` |
| `SonosGroup` | `IAudioPlayer`, `IMediaTrackState`, `IVirtualSubject`, `ITitleProvider`, `IIconProvider` |

The speakers do not report `SubnetMask`, `Gateway`, `SignalStrength` or `AvailableSoftwareUpdate`, so these stay empty.

## State

### SonosSystem

| Property | Description |
|----------|-------------|
| `Players`, `Groups`, `Favorites` | See [Subject Model](#subject-model) and [Favorites](#favorites) |
| `AreEventsActive` | True once at least one subscription delivered an event |
| `ActiveEventCallbackHost` | The host the speakers were told to send events to; empty when the listener could not start or no local address routes to the seed |
| `LastUpdated` | When the last reconciliation completed |

### SonosPlayer

| Property | Unit | Description |
|----------|------|-------------|
| `Uuid`, `RoomName` | | RINCON id and room name |
| `TransportState`, `IsPlaying` | | `Unknown`, `Stopped`, `Playing`, `Paused` or `Transitioning`; `IsPlaying` includes transitioning |
| `Volume`, `IsMuted` | 0..1 | This room |
| `CurrentTrackTitle`, `CurrentTrackArtist`, `CurrentTrackAlbum`, `CurrentTrackUri` | | See [Track Details](#track-details) |
| `CurrentTrackImageUri` | URI | Album art; Sonos's relative `/getaa?...` paths are resolved against the speaker |
| `CurrentTrackDuration` | TimeSpan | Empty for streams, TV and line-in |
| `CurrentTrackPosition` | TimeSpan | As of the last poll or command read-back; Sonos does not event it |
| `Source` | | `None`, `Tv`, `LineIn`, `SpotifyConnect`, `AirPlay`, `Radio`, `Queue` or `Other`, from the coordinator's transport URI |
| `MediaTitle` | | The station, playlist or album the group plays |
| `Shuffle`, `Repeat` | | Group play mode; `Repeat` is `Off`, `All` or `One` |
| `SleepTimerRemaining` | TimeSpan | Group sleep timer as of the last poll; empty when none runs |
| `Bass`, `Treble` | -10..10 | Equalizer of this room |
| `Loudness` | | Loudness compensation of this room |
| `NightMode`, `SpeechEnhancement` | | Home theater only; any dialog level above zero counts as on |
| `GroupCoordinatorUuid`, `IsGroupCoordinator` | | Group membership |
| `IsHomeTheater`, `HasLineIn` | | Device description lists `HTControl` or `AudioIn` |
| `BatteryLevel` | 0..1 | Portables only, from the topology |
| `IsCharging` | | Portables only |
| `Model`, `ProductCode`, `SerialNumber`, `MacAddress`, `HardwareRevision`, `SoftwareVersion` | | From the device description and `GetZoneInfo`; `SoftwareVersion` is the version the Sonos app shows |
| `IpAddress`, `IsWireless` | | From the topology |

### SonosSatellite

`Role`, `Uuid`, `RoomName` (of its player), identity and firmware (read when it appears, after a firmware or address change and after a reconnect), `IpAddress`, `MacAddress`, `IsWireless`, `IsConnected` and `StatusMessage`.

### SonosGroup

`GroupId`, `Coordinator` and `Members` (references to players), group `Volume` and `IsMuted`, and the coordinator's playback, track and `MediaTitle`. The title joins the room names, for example "Kitchen + Living Room".

## Operations

"Coordinator" means the command goes to the group's coordinator and affects the whole group; "player" means this room only. Volumes are fractions (0 to 1, -1 to 1 for `delta`), rounded to whole Sonos percent.

### SonosPlayer

| Operation | Parameters | Target | Notes |
|-----------|------------|--------|-------|
| `Play`, `Pause`, `Stop`, `Next`, `Previous`, `TogglePlayback` | | coordinator | |
| `Seek` | `position` | coordinator | Enabled only while the track has a duration |
| `SetVolume`, `ChangeVolume`, `RampVolume` | `volume` or `delta` | player | `RampVolume` uses Sonos's sleep timer ramp |
| `Mute`, `Unmute` | | player | |
| `PlayFavorite` | `title` | coordinator | See [Favorites](#favorites) |
| `PlayUri`, `PlayStream` | `uri`, `title` (stream, optional) | coordinator | See [Playing URIs](#playing-uris-streams-and-notifications) |
| `PlayNotification` | `soundUri`, `volume` | player | S2 only |
| `SwitchToTv`, `SwitchToLineIn` | | player | Home theater or line-in players only |
| `SetShuffle`, `SetRepeat` | `shuffle`, `repeat` | coordinator | Each keeps the other half of the play mode |
| `SetSleepTimer` | `duration` (0 to 23:59:59) | coordinator | Zero cancels it |
| `SetBass`, `SetTreble`, `SetLoudness` | -10 to 10, bool | player | |
| `SetNightMode`, `SetSpeechEnhancement` | bool | player | Home theater only; speech enhancement writes `DialogLevel` 1 or 0 |
| `JoinGroup` | `room` | player | Joins the group of the given room |
| `LeaveGroup` | | player | Enabled in groups of two or more rooms, also on the coordinator |

### SonosGroup

Transport, `PlayFavorite`, `PlayUri`, `PlayStream`, `SetShuffle`, `SetRepeat` and `SetSleepTimer` as on a player, sent to the coordinator. `SetVolume` and `ChangeVolume` set the group volume and send `SnapshotGroupVolume` first, so the rooms keep their current ratio and a room changed in the Sonos app does not jump back. `Mute` and `Unmute` affect every room. Per-room settings exist on players only.

### SonosSystem

| Operation | Parameters | Description |
|-----------|------------|-------------|
| `Refresh` | | Reconciles now instead of at the next poll |
| `GroupAll` | `room` | Groups every connected room with the given room, which becomes coordinator |
| `UngroupAll` | | Makes every connected room standalone |

Operations are disabled while the system, the target or, for coordinator operations, the coordinator is not connected. An unknown room or favorite fails with the list of known names. After each command the players of the commanded group are read back, also when it failed, so the result shows without waiting for events; grouping commands run a full reconciliation instead.

## Behavior

### Favorites

`Favorites` lists the household's "My Sonos" favorites, read once per reconciliation.

- Satellites answer the favorites `Browse` (`FV:2`) with HTTP 500, so the list is read through a connected room player, coordinators first, trying the next one on failure. When none answers, the previous list stays.
- Favorites are read in pages of 100, so large lists are complete.
- Favorites of type "shortcut", such as Sonos Radio station shortcuts, carry no URI and are skipped.
- `PlayFavorite` matches the title ignoring case; with duplicate titles the first one plays. A container favorite (playlist, album) replaces the queue and plays it; any other favorite becomes the transport URI without touching the queue. Both are sent with the favorite's DIDL metadata, which music services need and which is kept internal because it often contains account tokens.

### Playing URIs, Streams and Notifications

| Operation | Sonos mechanism | Use for |
|-----------|-----------------|---------|
| `PlayUri` | `SetAVTransportURI` with the URI unchanged, then `Play` | A finite file such as an mp3, or a native Sonos URI (`x-sonos-...`, `x-rincon...`, `x-file-cifs:`). Plays once as a track with duration and seek bar. |
| `PlayStream` | `SetAVTransportURI` with `x-rincon-mp3radio:` and a radio DIDL item, then `Play` | Internet radio and live streams. Accepts `http://`, `https://` and `x-rincon-mp3radio:`, rewrites http(s) to `x-rincon-mp3radio` so Sonos shows it as radio, and sends an empty title when none is given. Sonos reconnects the stream when it ends. |
| `PlayNotification` | Audio clip API of the speaker's websocket (`wss://<speaker>:1443/websocket/api`) | Doorbells and announcements over the current playback, which then resumes. The speaker downloads the sound itself, so use an absolute http(s) URL it can reach, not `localhost`. The volume is at least 1 % and the call times out after 10 seconds. |

`Source` reports `Radio` for every http(s) URI, so a file started with `PlayUri` shows as radio too.

### Track Details

When the source or track changes (a new transport or track URI), track values that Sonos does not report for the new one are cleared rather than kept from the previous one, and the position is cleared until the next poll. While the track stays the same, unknown values keep the current ones: Spotify Connect polls answer `NOT_IMPLEMENTED` for details its events delivered, and radio polls omit the album art its events delivered.

Radio keeps one track URI from song to song. On radio `CurrentTrackTitle` is the song the station reports (often "ARTIST - TITLE"), otherwise the station. A `ZPSTR_` placeholder such as `ZPSTR_CONNECTING` or `ZPSTR_BUFFERING` keeps the current title on the same stream and clears it when the stream starts or the station changes. A title that is only the end of the stream URL (`96` for `.../aac/96`), which Sonos reports when the station sends no song, is left empty while `MediaTitle` still names the station. A title or `MediaTitle` that is the stream URL itself, with or without its scheme, as other controllers write for a stream without a title, is left empty too.

## How It Works

### Seed Selection

The seed is the speaker the household topology (`GetZoneGroupState`) is read from. Each connection attempt tries `SeedHost` (5 second timeout), then the speakers known since startup (2 seconds each, those in the last topology first), then an SSDP M-SEARCH for `urn:schemas-upnp-org:device:ZonePlayer:1` on every IPv4 interface for 5 seconds, accepting the first `uuid:RINCON_` answer. While connected, a failed reconciliation moves to another connected player; three failures in a row tear the connection down and rebuild it.

### Polling

Each reconciliation reads the topology, polls every player in parallel (about ten SOAP requests per room), reads satellite identity when needed, reads the favorites and maintains the subscriptions. Polling is the fallback when events cannot reach HomeBlaze and repairs anything an event missed. Position and sleep timer are never evented, so polls and command read-backs are their only source. Events and polls share one monotonic sequence, so a poll that started before a newer event never overwrites it.

### Event Subscriptions

| Service | Subscribed on | Delivers |
|---------|---------------|----------|
| AVTransport | every player | transport state, play mode, media and track metadata |
| RenderingControl | every player | volume, mute, equalizer, night mode, dialog level |
| GroupRenderingControl | every coordinator | group volume and mute |
| ZoneGroupTopology | the seed | the whole topology on every change |

Subscriptions request a 30 minute lifetime and are renewed at half the lifetime the speaker grants, independent of `PollingInterval`. A rejected renewal subscribes again; an unanswered one is retried after 30 seconds. A subscription only proves the speaker accepted it, so **Events: live** (`AreEventsActive`) means at least one event actually arrived. When no event arrives within 15 seconds of an accepted subscription, a Warning names the callback address. On stop, removal or any configuration change, all subscriptions are cancelled with `UNSUBSCRIBE` within 2 seconds; one that cannot be cancelled expires on the speaker. The listener answers unknown subscriptions with 412 so speakers drop stale ones.

## Network and Firewall

| Direction | Port | Purpose |
|-----------|------|---------|
| HomeBlaze to speakers | TCP 1400 | Control, polling, device descriptions, `SUBSCRIBE` and `UNSUBSCRIBE`; always needed |
| Speakers to HomeBlaze | TCP `EventPort` (6329) | Event delivery; without it the system polls only |
| HomeBlaze to `239.255.255.250` | UDP 1900, unicast answers | SSDP discovery; only when neither `SeedHost` nor a known speaker answers |
| HomeBlaze to speakers | TCP 1443 | `PlayNotification` |
| Browser to speakers | TCP 1400 | Album art in the widgets |

The local Sonos API has no authentication: anything that reaches port 1400 can control the speakers.

On Windows, `HttpListener` binds all interfaces only with administrator rights or a URL reservation; without either, the system polls only. Reserve the port once in an elevated prompt:

```
netsh http add urlacl url=http://+:6329/ user=<account running HomeBlaze>
```

## Troubleshooting

- **"No Sonos speaker found":** multicast discovery is blocked (VLAN, Docker bridge, firewall, access point). Set `SeedHost`.
- **"The SeedHost '...' did not answer":** check that HomeBlaze reaches `http://<speaker>:1400/xml/device_description.xml`. Give the speaker a DHCP reservation.
- **Rooms of another household show up:** SSDP found a speaker of another household. Set `SeedHost`.
- **"Events: polling only":** allow inbound TCP on `EventPort` from the speakers, and under Docker bridge networking set `EventCallbackHost` and publish the port. Compare the callback address in the Warning with the host's address. On Windows, see [Network and Firewall](#network-and-firewall).
- **Events stop after a while:** the listener stopped accepting requests (logged at Error). `Refresh` only polls; a configuration change or restart starts a new listener.
- **"not connected":** check the `StatusMessage` of the system and the room. Coordinator operations also need the coordinator to be connected. A room that just appeared through an event is polled at the next reconciliation, or at once with `Refresh`.
- **`Next`, `Previous` or `SetShuffle` fail on TV or radio:** Sonos answers them with a UPnP fault for sources without a queue.
- **`PlayNotification` plays nothing:** the speaker must be S2, reach the sound URL itself and be reachable on TCP 1443. A rejected clip is not reported.
- **Album art does not show:** the browser loads it from the speaker over plain HTTP, so it fails when the browser cannot reach the speakers or blocks it as mixed content on an HTTPS page. The widget shows a placeholder icon.
- **Volume operations fail on a Port, Amp or Connect:** the line-out is set to fixed volume in the Sonos app.

## Limitations

- `Sonos.Base`, used for SOAP, parses with `XmlSerializer`, so the library is not yet free of runtime reflection for Native AOT.
- `SetSpeechEnhancement` writes `DialogLevel` 1 or 0; the Arc Ultra levels 1 to 4 are read as on but cannot be set.
- A speaker that changes role (becomes a surround or stereo partner, or a subwoofer moves rooms) keeps its old subject as an offline entry until restart.
- SSDP discovery and callback address detection use IPv4 only.
- The household is not identified by its household id, so two systems seeded in the same household show the same rooms twice.

## Follow-ups

- Alarms as state, with enable and disable operations (`AlarmClockService`).
- Shortcut favorites through the Sonos local websocket favorites API.
- Refreshing favorites on `FavoritesUpdateID` changes of a ContentDirectory subscription instead of at every poll.
- Queue editing, Sonos playlists and music library browsing.
- Queue position (`CurrentTrack`, `NumberOfTracks`) and next track (`NextTrackURI`, `NextTrackMetaData`).
- Crossfade (`Get/SetCrossfadeMode`, already evented).
- Disabling `Next`, `Previous`, `Seek`, `Pause`, `SetShuffle` and `SetRepeat` from `CurrentTransportActions` and `CurrentValidPlayModes`.
- Line-in level (`Get/SetLineInLevel`).
- Home theater levels (`SubGain`, `SubEnabled`, `SurroundEnable`, `SurroundLevel`, `MusicSurroundLevel`, `SurroundMode`, `HeightChannelLevel`, `AudioDelay`) and the Arc Ultra speech enhancement levels.
- Trueplay (`Get/SetRoomCalibrationStatus`).
- Fixed output (`OutputFixed`) to disable volume operations on Port, Connect and Amp.
- Household identity (`GetHouseholdID` or `MuseHouseholdId`) to tell households apart.
- Spotify tracks by id, as v1's `PlaySpotifyTrack` did; today Spotify content plays through favorites.
- An own audio clip client that reports rejected clips.
- Album art served through HomeBlaze for HTTPS pages, or the favorite's HTTPS `ImageUri` for radio.
- A Windows event listener that needs no URL reservation.
- An AOT-clean SOAP client replacing `Sonos.Base`.
- `AvailableSoftwareUpdate` from the topology event or `CheckForUpdate`.
- LED state, button lock, and home theater TV power state.
- A widget for `SonosSatellite`.
- Opt-in read-only live integration tests (`Category=Integration`).
- Upstream contributions to `Sonos.Base` (satellites in the topology model, `resMD` in DIDL).

## References

- [Sonos UPnP API documentation](https://sonos.svrooij.io/) (unofficial, community maintained)
- [Sonos.Base (sonos-net)](https://github.com/svrooij/sonos-net), the SOAP client, and [Rssdp](https://github.com/Yortw/RSSDP), the SSDP client
- [MCP server](https://github.com/RicoSuter/Namotion.Interceptor/blob/master/docs/mcp.md) for controlling the household from AI agents
- [Source of the library](https://github.com/RicoSuter/Namotion.Interceptor/tree/master/src/HomeBlaze/Namotion.Devices.Sonos) and of its [widgets](https://github.com/RicoSuter/Namotion.Interceptor/tree/master/src/HomeBlaze/Namotion.Devices.Sonos.HomeBlaze)
