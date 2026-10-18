---
title: Pushover
icon: NotificationsActive
---

# Pushover

[Pushover](https://pushover.net) delivers push notifications to phones, tablets and desktops. A Pushover service holds the application token of a Pushover application and one target per user or group key. Each target is a [notification publisher](../development/notifications.md), so agents, automations and alarms that send to all publishers reach each recipient once. The service itself is no publisher. A user who is in a group target and also has a target of their own still gets two copies.

## Configuration

### Service

| Property | Description |
|----------|-------------|
| `Name` | Display name, "Pushover" when empty |
| `ApplicationToken` | Secret. The API token of your application, created at pushover.net under "Your Applications" |
| `Targets` | The recipients, by a key made from the name when the target is added. The key stays when the target is renamed, so paths such as `/Devices/Pushover/Targets[rico]` keep working |

The editor of the service sets the name, the application token and, per target, the name and the user key. A new target starts with the defaults below; change its emergency settings in the editor of the target.

### Target

| Property | Description |
|----------|-------------|
| `Name` | Display name |
| `UserKey` | Secret. The user key or group key of the recipient |
| `EmergencyRetry` | How often an emergency notification repeats until acknowledged, 30 seconds or more, default 60 seconds |
| `EmergencyExpire` | How long an emergency notification repeats, 30 seconds to 3 hours, default 1 hour |

## Operations

| Subject | Operation | Description |
|---------|-----------|-------------|
| Target | `SendNotification` | Sends a notification to this recipient and returns its id: the receipt of an emergency notification, otherwise the Pushover request id |
| Service | `SendToAllTargets` | Sends a notification to every target at once |

When `SendToAllTargets` fails for some targets, the others are still sent, and the error names the failed targets. A target that a configuration reload replaced or that was removed is no longer part of the service: sending through it fails.

## Priorities

| Notification priority | Pushover priority | Behavior |
|-----------------------|-------------------|----------|
| `Low` | -1 | No sound or vibration |
| `Normal` | 0 | The recipient's default alert |
| `High` | 1 | Alerts also during quiet hours |
| `Emergency` | 2 | Repeats every `EmergencyRetry` until acknowledged in the Pushover app or until `EmergencyExpire`. Use it only for safety-critical events |

A notification without message text is rejected with an argument error before any request, also by `SendToAllTargets`.

Pushover limits the message to 1024 characters, the title to 250, the url to 512 and the url title to 100. The message, title and url title are shortened with an ellipsis and a warning in the log. A url over the limit is left out instead, together with its url title, since shortening a url would break the link it points to. Only an absolute `http` or `https` url is sent; any other url, such as a relative path or another scheme, is left out together with its url title, with a warning in the log.

## State Properties

### Service

| Property | Description |
|----------|-------------|
| `Status` | `Stopped` without application token, `Starting` while validating the targets, `Running`, or `Error` when the token is invalid or Pushover cannot be reached |
| `StatusMessage` | Details of the status |

### Target

| Property | Description |
|----------|-------------|
| `Status` | `Stopped` with "Not validated yet" until the key is checked, `Running` when Pushover accepted the key, `Error` with the reason otherwise. Keys are checked at start and after each configuration change |
| `Devices` | The devices of the user, as reported by Pushover |
| `LastSentAt` | When the last notification was accepted by Pushover |

The service makes no requests of its own besides validating the targets at start, after each configuration change, and every 30 seconds while it is in `Error` because Pushover cannot be reached.

## Interfaces

| Subject | Interfaces |
|---------|------------|
| Service | `IConfigurable`, `IMonitoredService`, `ITitleProvider`, `IIconProvider` |
| Target | `INotificationPublisher`, `IConfigurable`, `IMonitoredService`, `ITitleProvider`, `IIconProvider` |

## JSON Configuration Example

```json
{
  "$type": "Namotion.Devices.Pushover.PushoverService",
  "name": "Pushover",
  "applicationToken": "your-application-token",
  "targets": {
    "rico": {
      "name": "Rico",
      "userKey": "your-user-key",
      "emergencyRetry": "00:01:00",
      "emergencyExpire": "01:00:00"
    }
  }
}
```

## Limitations

An agent that may call `invoke_operation` can send any number of notifications, so such an agent can use up the monthly message quota of the application. Pushover has no action buttons or replies, such as yes or no, so an answer is only possible through a link. HomeBlaze does not see emergency acknowledgments yet, see [Missing Features](#missing-features).

## Missing Features

The following features are not yet implemented but may be added in future versions:

- Emergency tracking: show active emergencies, raise an event when one is acknowledged, and cancel them from HomeBlaze.
- Sound, device and time to live: a chosen sound, delivery to one device of the user, and deleting a message after a time. Each user can already choose the sound in the Pushover app.
- Quota state: every response carries the monthly message limit, the remaining messages and the reset time in `X-Limit-App-*` headers, which could be shown on the service. The free quota is 10,000 messages a month.
- HTML and monospace formatting: bold, italic, colored and linked text, or a monospace font.
- Image attachments: an image of up to 5 MB, for example a camera snapshot.
- Emergency callback: Pushover can call a URL when an emergency notification is acknowledged, which needs HomeBlaze reachable from the internet.
- Message timestamp: the time of the event instead of the time it was sent.
- Groups API: creating Pushover groups and managing their members. Group keys already work as target keys.
- End-to-end encryption with a key that only the recipient's devices hold.
- Glances: small values pushed to watch faces and widgets.

## References

- [Pushover API](https://pushover.net/api)
