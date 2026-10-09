---
title: Sonos
icon: LibraryMusic
---

# Sonos

One `SonosSystem` represents a Sonos household. It finds the speakers itself, shows each room as a player with its bonded surrounds and subwoofer, and shows the groups the rooms currently form. State arrives through UPnP events as it changes, and a poll every 30 seconds reconciles everything the events might have missed.

## Configuration

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `SeedHost` | string | null | Any speaker of the household as `host` or `host:port`. Empty tries the speakers found since HomeBlaze started, then SSDP discovery. |
| `EventCallbackHost` | string | null | The address speakers send events to. Empty detects the local address that routes to the seed speaker. |
| `EventPort` | int | 6329 | The port of the event listener. |
| `PollingInterval` | TimeSpan | 30 seconds | How often topology, state and favorites are reconciled, at most one hour. Event subscriptions are renewed on their own schedule, so events stay alive with any polling interval. |
| `RetryInterval` | TimeSpan | 30 seconds | Delay before reconnecting after a failure. |

### Discovery

The system needs one reachable speaker. It reads the household topology from that speaker, which lists every room, satellite and group with their addresses. The seed speaker is chosen in this order: `SeedHost` if set, otherwise each speaker found since HomeBlaze started in turn (each gets a 2 second probe, speakers still in the topology first), otherwise a search over SSDP multicast. The known speakers are only kept in memory and are used when the connection is re-established, so after a restart without `SeedHost` discovery starts with SSDP. SSDP does not cross Docker bridge networks, so set `SeedHost` to any speaker IP in that case.

### Events

Speakers push changes to `http://<EventCallbackHost>:<EventPort>/event/...`. The listener runs inside the HomeBlaze process on its own port; no second web server is involved.

- Under Docker host networking the detected address works. Under bridge networking it is the container address, which speakers cannot reach: set `EventCallbackHost` to the host IP and publish `EventPort`.
- Open `EventPort` in the host firewall.
- If the callback port cannot be bound (it is taken, or on Windows there is no URL ACL), or no local IPv4 address routes to the speaker, the system runs on polling alone. It does the same when the listener starts but no subscription succeeds. `AreEventsActive` is false in all these cases and the widgets show "Events: polling only". `ActiveEventCallbackHost` is empty when the listener could not start or no local address was found, and keeps the callback host when the listener started but no subscription succeeded. Spotify Connect track details are only delivered through events, so they stay empty in that mode.

## Subjects

```
SonosSystem
├── Players[<uuid>]         SonosPlayer, one per room
│     └── Satellites[<uuid>]  SonosSatellite: surround, subwoofer or stereo partner
└── Groups[<coordinator uuid>]  SonosGroup, the rooms playing together
```

Keys are the RINCON ids of the speakers, so paths stay valid across restarts and IP changes. A group is keyed by its coordinator; a room that is not grouped is a group of one, as in the Sonos app. A speaker that disappears from the topology stays in place and reports `IsConnected = false` until it reappears.

## Operations

### SonosPlayer

| Operation | Description |
|-----------|-------------|
| `Play`, `Pause`, `Stop`, `Next`, `Previous`, `TogglePlayback` | Playback, sent to the group coordinator |
| `Seek` | Jumps within the current track (only when it has a duration) |
| `SetVolume`, `ChangeVolume`, `RampVolume` | Volume as a fraction from 0 to 1, a relative change from -1 to 1, or a smooth ramp to a volume. Values out of range are rejected, not clamped. |
| `Mute`, `Unmute` | Mute of this room |
| `PlayFavorite` | Plays a Sonos favorite by name, see `SonosSystem.Favorites` |
| `PlayUri` | Plays an http(s) stream as radio, with an optional title, or a native Sonos URI |
| `PlayNotification` | Plays a sound over the current playback, which then resumes (S2 speakers). The volume is a fraction from 0 to 1, played at 1 percent at least. |
| `SwitchToTv`, `SwitchToLineIn` | Selects the TV or line-in input where available |
| `SetShuffle`, `SetRepeat`, `SetSleepTimer` | Play mode and sleep timer of the whole group, sent to the group coordinator (zero cancels the timer) |
| `SetBass`, `SetTreble`, `SetLoudness` | Sound settings, bass and treble from -10 to 10 |
| `SetNightMode`, `SetSpeechEnhancement` | Home theater sound settings |
| `JoinGroup`, `LeaveGroup` | Joins another room's group by room name, or becomes standalone. `LeaveGroup` is available for any member of a group of two or more rooms. |

Every operation is disabled while the system or the player is not connected. Operations sent to the group coordinator are also disabled while the coordinator is not connected.

`PlayUri` plays an `http://` or `https://` stream through the `x-rincon-mp3radio` scheme, with the title as metadata. A URI that already starts with `x-rincon-mp3radio:` gets the same title metadata. Any other native Sonos URI is passed through unchanged, without metadata.

### SonosGroup

`Play`, `Pause`, `Stop`, `Next`, `Previous`, `TogglePlayback`, `Seek`, and group-wide `SetVolume`, `ChangeVolume`, `Mute`, `Unmute`, with the same volume ranges as a player. Group volume keeps the volume ratio between the rooms.

### SonosSystem

| Operation | Description |
|-----------|-------------|
| `Refresh` | Reconciles now instead of at the next poll |
| `GroupAll` | Groups every connected room with the given room |
| `UngroupAll` | Makes every room standalone |

## State Properties

### SonosPlayer

| Property | Description |
|----------|-------------|
| `Uuid`, `RoomName` | The RINCON id and the room name |
| `TransportState`, `IsPlaying`, `Source` | Playback state and where the audio comes from (TV, line-in, Spotify Connect, AirPlay, radio, queue) |
| `Volume`, `IsMuted` | Volume 0 to 1 and mute |
| `CurrentTrackTitle`, `CurrentTrackArtist`, `CurrentTrackAlbum`, `CurrentTrackImageUri`, `CurrentTrackUri`, `CurrentTrackPosition`, `CurrentTrackDuration` | The current track |
| `Shuffle`, `Repeat`, `SleepTimerRemaining` | Play mode and sleep timer of the group |
| `Bass`, `Treble`, `Loudness`, `NightMode`, `SpeechEnhancement` | Sound settings; night mode and speech enhancement only on home theater players |
| `GroupCoordinatorUuid`, `IsGroupCoordinator` | Group membership |
| `IsHomeTheater`, `HasLineIn` | Whether the player is a home theater player and whether it has a line-in |
| `Satellites` | The bonded surround, subwoofer or stereo partner speakers of the room |
| `BatteryLevel`, `IsCharging` | Battery of portable speakers |
| `Model`, `ProductCode`, `SerialNumber`, `HardwareRevision`, `SoftwareVersion`, `IpAddress`, `MacAddress`, `IsWireless`, `IsConnected`, `StatusMessage` | Identity, network and connection |

### SonosGroup

`GroupId`, `Coordinator`, `Members`, `Volume`, `IsMuted`, and the playback and track state of the coordinator.

### SonosSystem

`Players`, `Groups`, `Favorites` (records with `Title`, `Uri`, `IsContainer` and `ImageUri`; to play one, call `PlayFavorite` on a player with its title), `AreEventsActive`, `ActiveEventCallbackHost`, `IsConnected`, `Status`, `StatusMessage`, `LastUpdated`.

## Limitations

- `Sonos.Base`, used for the SOAP calls, parses responses with `XmlSerializer`, so this library is not yet free of runtime reflection for Native AOT. Our own parsers use LINQ to XML.
- Album art is served by the speaker over plain http, so the browser must be able to reach the speaker. When HomeBlaze itself is served over https, browsers block it as mixed content.
- On Windows, listening on all interfaces with `HttpListener` needs a URL ACL or administrator rights. Without it the system runs on polling only. Linux, macOS and Docker need nothing.
- Notification clips use the Sonos audio clip API, which needs S2 speakers.
- Favorites of type "shortcut" (for example Sonos Radio station shortcuts) carry no URI and are not listed; see the follow-ups.
- A speaker that changes role (a standalone speaker becomes a surround or stereo partner, or a subwoofer moves to another room) keeps its old subject as an offline entry until restart.
- If the event listener stops accepting requests (it logs an error), the system runs on polling only until the next reconnect, for example after a configuration change or three failed reconciliations in a row.
- `PlayUri` always plays an `http://` or `https://` URI as radio, so a plain audio file shows no seek bar and cannot be sought. The `PlayTrack` operation of the HomeBlaze v1 Sonos library could play such a file as a seekable track.

## Troubleshooting

- **No Sonos speaker found:** discovery uses SSDP multicast, which does not cross VLANs or Docker bridge networks and can be blocked by a firewall. Set `SeedHost` to the IP of any speaker.
- **"Events: polling only":** the speakers must be able to reach `EventPort` on the HomeBlaze host, so allow inbound TCP on that port from the speakers. Under Docker bridge networking set `EventCallbackHost` to the host IP and publish `EventPort`; Docker host networking works with the detected address. State still updates through polling.
- **Events never start on Windows:** the listener binds all interfaces (`+`), which needs a URL ACL for the port or administrator rights. Without it the system runs on polling only.
- **Night mode and speech enhancement do nothing or stay empty:** they exist only on home theater players (`IsHomeTheater`); the operations are disabled on other speakers.

## Follow-ups

Not implemented yet; each fits the current structure:

- Alarms: list Sonos alarms as state, with enable and disable operations (`AlarmClockService`).
- Shortcut favorites through the Sonos local websocket favorites API.
- Queue editing, Sonos playlists and music library browsing beyond favorites.
- An album art proxy so artwork loads when HomeBlaze is served over https.
- Windows support for the event listener without a URL ACL, for example a raw socket listener.
- An AOT-clean SOAP client replacing `Sonos.Base`.
- `AvailableSoftwareUpdate` via `CheckForUpdate`.
- LED state and button lock settings.
- Home theater TV power state and other HTControl details.
- Opt-in live integration tests (`Category=Integration`, read-only, speaker IP from an environment variable).
- Upstream contributions to `Sonos.Base` (satellites in the topology model, `resMD` in DIDL) so the library's own parsers can shrink.
