---
title: Sonos
icon: LibraryMusic
---

# Sonos

One `SonosSystem` represents a Sonos household. It finds the speakers itself, shows each room as a player with its bonded surrounds and subwoofer, and shows the groups the rooms currently form. State arrives through UPnP events as it changes, and a poll every 30 seconds reconciles everything the events might have missed.

## Configuration

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `SeedHost` | string | null | Any speaker of the household as `host` or `host:port`. It also selects the household when the network has several. Empty tries the speakers found since HomeBlaze started, then SSDP discovery. |
| `EventCallbackHost` | string | null | The address speakers send events to. Empty detects the local address that routes to the seed speaker. |
| `EventPort` | int | 6329 | The port of the event listener. The edit form accepts 1 to 65535. |
| `PollingInterval` | TimeSpan | 30 seconds | How often topology, state and favorites are reconciled. Event subscriptions are renewed on their own schedule, so events stay alive with any polling interval. |
| `RetryInterval` | TimeSpan | 30 seconds | Delay before reconnecting after a failed connection. |

Both intervals are clamped to 5 seconds to one hour when the system uses them, and zero or less uses 30 seconds. That also applies to values set in the JSON file or over MCP; the edit form only accepts 5 to 3600 seconds.

### Discovery

The system needs one reachable speaker. It reads the household topology from that speaker, which lists every room, satellite and group with their addresses. The seed speaker is chosen in this order: `SeedHost` if it answers within 5 seconds, otherwise each speaker found since HomeBlaze started in turn (each gets a 2 second probe, speakers still in the topology first), otherwise a search over SSDP multicast. When `SeedHost` does not answer, a Warning is logged once until it answers again. The known speakers are only kept in memory and are used when the connection is re-established, so after a restart without `SeedHost` discovery starts with SSDP. SSDP does not cross Docker bridge networks, so set `SeedHost` to any speaker IP in that case.

The seed decides the household: the system shows the household the seed speaker belongs to. When the network has more than one Sonos household, set `SeedHost` to a speaker of the one to show. To show another household as well, add a second `SonosSystem` with its own `SeedHost` and `EventPort`. SSDP takes whichever speaker answers first, so without `SeedHost`, or when `SeedHost` and every known speaker stop answering, the system can end up on another household.

### Events

Speakers push changes to `http://<EventCallbackHost>:<EventPort>/event/...`. The listener runs inside the HomeBlaze process on its own port; no second web server is involved.

- Under Docker host networking the detected address works. Under bridge networking it is the container address, which speakers cannot reach: set `EventCallbackHost` to the host IP and publish `EventPort`.
- Open `EventPort` in the host firewall.
- A speaker sends a first event right after it accepts a subscription. `AreEventsActive` turns true once an event has arrived. When the speakers accept the subscriptions but no event arrives within 15 seconds, a Warning names the callback address and points at the firewall, Docker and `EventCallbackHost`.
- If the callback port cannot be bound (it is taken, or on Windows there is no URL ACL), or no local IPv4 address routes to the speaker, the system runs on polling alone. It does the same when no subscription succeeds or no event arrives. `AreEventsActive` is false in all these cases and the widgets show "Events: polling only". `ActiveEventCallbackHost` is empty when the listener could not start or no local address was found, and keeps the callback host when the listener started but no subscription succeeded or no event arrived. Spotify Connect track details are only delivered through events, so they stay empty in that mode.
- On stop and after a configuration change, the system cancels all its subscriptions at once, within 2 seconds. A subscription it cannot cancel, for example on a speaker that is off, is logged at Information; the speaker drops it once it expires.

## Subjects

```
SonosSystem
├── Players[<uuid>]         SonosPlayer, one per room
│     └── Satellites[<uuid>]  SonosSatellite: surround, subwoofer or stereo partner
└── Groups[<coordinator uuid>]  SonosGroup, the rooms playing together
```

Keys are the RINCON ids of the speakers, so paths stay valid across restarts and IP changes. A group is keyed by its coordinator; a room that is not grouped is a group of one, as in the Sonos app. Boost and Bridge units only extend the network and are not shown.

A speaker that disappears from the topology stays in place and reports `IsConnected = false` until it reappears. Since a seed that just rebooted can briefly report part of the household, a player only goes offline when two topologies in a row, polled or evented, miss it; the first topology after connecting is applied at once. A newly found player shows `IsConnected = false` until its first poll succeeds. A speaker goes offline when it does not answer; when it answers a read with an error (a UPnP fault), it stays connected and the values of that read keep their last state.

## Operations

### SonosPlayer

| Operation | Description |
|-----------|-------------|
| `Play`, `Pause`, `Stop`, `Next`, `Previous`, `TogglePlayback` | Playback, sent to the group coordinator |
| `Seek` | Jumps within the current track (only when it has a duration) |
| `SetVolume`, `ChangeVolume`, `RampVolume` | Volume as a fraction from 0 to 1 (0 to 100 % in the operation dialog), a relative change from -1 to 1, or a smooth ramp to a volume. Values out of range are rejected, not clamped. |
| `Mute`, `Unmute` | Mute of this room |
| `PlayFavorite` | Plays a Sonos favorite by title, see [Favorites](#favorites) |
| `PlayUri` | Plays a URI once, as a normal track that ends and can be sought |
| `PlayStream` | Plays an http(s) radio or live stream, with an optional title. Sonos reconnects the stream when it ends. |
| `PlayNotification` | Plays an absolute http(s) sound URI over the current playback, which then resumes (S2 speakers). The volume is a fraction from 0 to 1 (0 to 100 % in the operation dialog), played at 1 percent at least. |
| `SwitchToTv`, `SwitchToLineIn` | Selects the TV or line-in input where available |
| `SetShuffle`, `SetRepeat`, `SetSleepTimer` | Play mode and sleep timer of the whole group, sent to the group coordinator. The sleep timer is at most 23:59:59, and zero cancels it. |
| `SetBass`, `SetTreble`, `SetLoudness` | Sound settings, bass and treble from -10 to 10 |
| `SetNightMode`, `SetSpeechEnhancement` | Home theater sound settings |
| `JoinGroup`, `LeaveGroup` | Joins the group of another room, given by room name or UUID, or becomes standalone. `LeaveGroup` is available for any member of a group of two or more rooms. |

Every operation is disabled while the system or the player is not connected. Operations sent to the group coordinator are also disabled while the coordinator is not connected.

`PlayUri` sends the URI to the speaker unchanged and without metadata, so Sonos plays a finite file such as an mp3 once and then stops. It accepts any URI that is not blank: native Sonos URIs (`x-sonos-...`, `x-rincon...`, `x-file-cifs:` and so on) are passed through the same way. `Source` reports `Radio` for every http(s) URI, so a file started with `PlayUri` shows as radio too.

`PlayStream` plays an `http://` or `https://` stream through the `x-rincon-mp3radio` scheme, with the title as metadata, so it shows as radio without a seek bar and Sonos reconnects when the stream ends. A URI that already starts with `x-rincon-mp3radio:` gets the same title metadata. `PlayStream` rejects any other scheme.

### SonosGroup

`Play`, `Pause`, `Stop`, `Next`, `Previous`, `TogglePlayback`, `Seek`, and group-wide `SetVolume`, `ChangeVolume`, `Mute`, `Unmute`, with the same volume ranges as a player. Group volume keeps the volume ratio between the rooms.

`PlayFavorite`, `PlayUri`, `PlayStream`, `SetShuffle`, `SetRepeat` and `SetSleepTimer` work as on a player and are sent to the coordinator.

### SonosSystem

| Operation | Description |
|-----------|-------------|
| `Refresh` | Reconciles now instead of at the next poll |
| `GroupAll` | Groups every connected room with the given room, by room name or UUID. A room that is a member of another group first leaves it and becomes the coordinator. |
| `UngroupAll` | Makes every room standalone |

### Favorites

`SonosSystem.Favorites` lists the Sonos favorites, read at every reconciliation through a connected player rather than a satellite, which refuses the request, group coordinators first. All pages are read, also beyond 100 favorites. When the read fails, the previous list stays. `PlayFavorite` matches the title ignoring case, and when two favorites share a title the first one plays. A playlist or album favorite (`IsContainer`) replaces the queue of the group and plays it. Favorites of type "shortcut" carry no URI and are not listed.

## State Properties

### SonosPlayer

| Property | Description |
|----------|-------------|
| `Uuid`, `RoomName` | The RINCON id and the room name |
| `TransportState`, `IsPlaying` | Playback state |
| `Volume`, `IsMuted` | Volume 0 to 1 and mute of this room |
| `CurrentTrackTitle`, `CurrentTrackArtist`, `CurrentTrackAlbum`, `CurrentTrackImageUri`, `CurrentTrackUri`, `CurrentTrackDuration` | The current track. When the track changes and Sonos reports no details for the new one, they are cleared rather than kept from the previous track. |
| `CurrentTrackPosition` | The position at the last poll; it is cleared when the track changes until the next poll reads it |
| `Source`, `MediaTitle`, `Shuffle`, `Repeat`, `SleepTimerRemaining` | Group state: where the audio comes from (TV, line-in, Spotify Connect, AirPlay, radio, queue), the name of the station or playlist when Sonos reports it, play mode and sleep timer. A group member reports the values of its coordinator. |
| `Bass`, `Treble`, `Loudness`, `NightMode`, `SpeechEnhancement` | Sound settings; night mode and speech enhancement only on home theater players. Any speech enhancement level above zero, as the Arc Ultra reports, counts as on. |
| `GroupCoordinatorUuid`, `IsGroupCoordinator` | Group membership |
| `IsHomeTheater`, `HasLineIn` | Whether the player is a home theater player and whether it has a line-in |
| `Satellites` | The bonded surround, subwoofer or stereo partner speakers of the room |
| `BatteryLevel`, `IsCharging` | Battery of portable speakers |
| `Model`, `ProductCode`, `SerialNumber`, `HardwareRevision`, `SoftwareVersion`, `IpAddress`, `MacAddress`, `IsWireless`, `IsConnected`, `StatusMessage` | Identity, network and connection |

### SonosSatellite

| Property | Description |
|----------|-------------|
| `Role` | `Subwoofer`, `RearLeft`, `RearRight`, `StereoPartner` or `Other` |
| `Uuid`, `RoomName` | The RINCON id and the room of the player it is bonded to |
| `Model`, `ProductCode`, `SerialNumber`, `HardwareRevision`, `SoftwareVersion`, `IpAddress`, `MacAddress`, `IsWireless`, `IsConnected`, `StatusMessage` | Identity, network and connection |

The title names the model, the room and the role, for example "Sonos Sub (Wohnzimmer, subwoofer)", since the units of one room often share a model. A satellite is read for its identity when it appears, after a firmware or address change and after a reconnect, and leaves the topology together with its player.

### SonosGroup

`GroupId`, `Coordinator`, `Members`, `Volume`, `IsMuted`, `MediaTitle`, and the playback and track state of the coordinator.

### SonosSystem

`Players`, `Groups`, `Favorites` (records with `Title`, `Uri`, `IsContainer` and `ImageUri`; to play one, call `PlayFavorite` on a player or group with its title), `AreEventsActive`, `ActiveEventCallbackHost`, `IsConnected`, `Status`, `StatusMessage`, `LastUpdated`.

## Limitations

- `Sonos.Base`, used for the SOAP calls, parses responses with `XmlSerializer`, so this library is not yet free of runtime reflection for Native AOT. Our own parsers use LINQ to XML.
- Album art is served by the speaker over plain http, so the browser must be able to reach the speaker. When HomeBlaze itself is served over https, browsers block it as mixed content.
- On Windows, listening on all interfaces with `HttpListener` needs a URL ACL or administrator rights. Without it the system runs on polling only. Linux, macOS and Docker need nothing.
- Notification clips use the Sonos audio clip API, which needs S2 speakers. The speaker's answer is not read, so a clip it rejects, for example for a URL it cannot load, is not reported.
- `SetSpeechEnhancement` writes `DialogLevel` 1 or 0, the on and off values of most home theater players; the Arc Ultra levels 1 to 4 are not exposed yet.
- Favorites of type "shortcut" (for example Sonos Radio station shortcuts) carry no URI and are not listed; see the follow-ups.
- A speaker that changes role (a standalone speaker becomes a surround or stereo partner, or a subwoofer moves to another room) keeps its old subject as an offline entry until restart.
- If the event listener stops accepting requests (it logs an error), the system runs on polling only until the next reconnect, for example after a configuration change or three failed reconciliations in a row.

## Troubleshooting

- **No Sonos speaker found:** discovery uses SSDP multicast, which does not cross VLANs or Docker bridge networks and can be blocked by a firewall. Set `SeedHost` to the IP of any speaker.
- **The rooms of another household show up:** the network has several Sonos households and SSDP found a speaker of another one. Set `SeedHost` to a speaker of the household to show.
- **"Events: polling only":** the speakers must be able to reach `EventPort` on the HomeBlaze host, so allow inbound TCP on that port from the speakers. Under Docker bridge networking set `EventCallbackHost` to the host IP and publish `EventPort`; Docker host networking works with the detected address. The Warning about events that did not arrive names the callback address the speakers were given. State still updates through polling.
- **Events never start on Windows:** the listener binds all interfaces (`+`), which needs a URL ACL for the port or administrator rights. Without it the system runs on polling only.
- **Operations are disabled or fail with "not connected":** either the system is not connected, and its `StatusMessage` says why, or the player is offline: it does not answer, it left the topology, or it appeared and was not polled yet. Its `StatusMessage` shows the last error. Operations for the group playback also need the group coordinator to be connected. Over MCP these failures arrive as a generic error, and the log has the details.
- **Favorites are empty or `PlayFavorite` reports an unknown favorite:** the error lists the known titles. Shortcut favorites are not listed, and the favorites are only read while a player is connected.
- **Night mode and speech enhancement do nothing or stay empty:** they exist only on home theater players (`IsHomeTheater`); the operations are disabled on other speakers.

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
- An album art proxy so artwork loads when HomeBlaze is served over https.
- Windows support for the event listener without a URL ACL, for example a raw socket listener.
- An AOT-clean SOAP client replacing `Sonos.Base`.
- `AvailableSoftwareUpdate`, from the topology event, which already carries it, or via `CheckForUpdate`.
- LED state and button lock settings.
- Home theater TV power state and other HTControl details.
- Opt-in live integration tests (`Category=Integration`, read-only, speaker IP from an environment variable).
- Upstream contributions to `Sonos.Base` (satellites in the topology model, `resMD` in DIDL) so the library's own parsers can shrink.
