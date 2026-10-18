---
title: Pushover
navTitle: Pushover
---

# Pushover

[Pushover](https://pushover.net) delivers push notifications to phones, tablets and desktops. A Pushover service holds the application token of a Pushover application and one target per user or group key. Each target is a [notification publisher](../development/notifications.md), so agents, automations and alarms that send to all publishers reach each recipient once. The service itself is no publisher.

## Configuration

### Service

| Property | Description |
|----------|-------------|
| `Name` | Display name, "Pushover" when empty |
| `ApplicationToken` | Secret. The API token of your application, created at pushover.net under "Your Applications" |
| `Targets` | The recipients, by a key made from the name when the target is added. The key stays when the target is renamed, so paths such as `/Devices/Pushover/Targets[rico]` keep working |

### Target

| Property | Description |
|----------|-------------|
| `Name` | Display name |
| `UserKey` | Secret. The user key or group key of the recipient |
| `EmergencyRetry` | How often an emergency notification repeats until acknowledged, 30 seconds or more, default 60 seconds |
| `EmergencyExpire` | How long an emergency notification repeats, at most 3 hours, default 1 hour |

## Operations

| Subject | Operation | Description |
|---------|-----------|-------------|
| Target | `SendNotification` | Sends a notification to this recipient and returns its id: the receipt of an emergency notification, otherwise the Pushover request id |
| Target | `CancelAllEmergencies` | Stops the repeats of every active emergency notification of this recipient, enabled while one is active |
| Emergency | `Cancel` | Stops the repeats of this emergency notification, enabled while it is active |
| Service | `SendToAllTargets` | Sends a notification to every target at once. If some fail, the others are still sent and the error names the failed targets |
| Service | `CancelAllEmergencies` | Stops the repeats of the active emergency notifications of every target |

When canceling several emergency notifications, a failure of one does not stop the others; the error names the failed ones.

## Priorities

| Notification priority | Pushover priority | Behavior |
|-----------------------|-------------------|----------|
| `Low` | -1 | No sound or vibration |
| `Normal` | 0 | The recipient's default alert |
| `High` | 1 | Alerts also during quiet hours |
| `Emergency` | 2 | Repeats every `EmergencyRetry` until acknowledged or until `EmergencyExpire` |

Each emergency notification is tracked on its own, so several can repeat at once, for example one for smoke and one for water, and each is acknowledged or canceled independently. See [Emergencies](#emergencies).

Pushover limits the message to 1024 characters, the title to 250, the url to 512 and the url title to 100. The message, title and url title are shortened with an ellipsis and a warning in the log. A url over the limit is left out instead, together with its url title, since shortening a url would break the link it points to.

## State Properties

### Service

| Property | Description |
|----------|-------------|
| `Status` | `Stopped` without application token, `Running`, or `Error` when the token is invalid or Pushover cannot be reached |
| `StatusMessage` | Details of the status |

### Target

| Property | Description |
|----------|-------------|
| `Status` | `Running` when Pushover accepted the key, `Error` with the reason otherwise. Keys are checked at start and after each configuration change |
| `Devices` | The devices of the user, as reported by Pushover |
| `LastSentAt` | When the last notification was accepted by Pushover |
| `Emergencies` | The emergency notifications by receipt, see [Emergencies](#emergencies) |

## Emergencies

Sending an `Emergency` notification adds a Pushover emergency subject to the target's `Emergencies`, keyed by the receipt Pushover returned. The receipt is also the `Id` of the `NotificationResult` the send returns and of the acknowledgement event, so a caller can tell which acknowledgement belongs to its send. The emergency is reachable at a path such as `/Devices/Pushover/Targets[rico]/Emergencies[r1a2b3]`.

| Property | Description |
|----------|-------------|
| `Receipt` | The receipt of the notification |
| `Title` | The title of the notification, or null when it had none; the subject is shown as "Emergency" then |
| `Message` | The message of the notification |
| `SentAt` | When Pushover accepted the notification |
| `Status` | `Active`, `Acknowledged`, `Expired` or `Canceled` |
| `AcknowledgedAt` | When it was acknowledged |
| `AcknowledgedByDevice` | The device it was acknowledged on |

| Status | Meaning |
|--------|---------|
| `Active` | The notification repeats and waits for acknowledgement |
| `Acknowledged` | The recipient acknowledged it |
| `Expired` | It stopped repeating after `EmergencyExpire` without acknowledgement |
| `Canceled` | It was canceled with `Cancel` or `CancelAllEmergencies`, or because its target was removed |

While an emergency notification is active, the service reads its receipt every 10 seconds; otherwise it makes no requests. An emergency that is no longer active is removed from `Emergencies` 24 hours after it ended. `Emergencies` is kept in memory only: after a restart the list is empty, and an emergency that was still active keeps repeating on the phone until it is acknowledged or expires, without HomeBlaze tracking or canceling it.

The service tracks the emergencies by the key of their target. When a configuration reload replaces a target but keeps its key, the new target shows the same emergencies and can still cancel them, including one whose send was still running during the reload. When a target is removed, its active emergencies are canceled, including one whose send completes after the removal.

## Events

A target implements `IObservable<PushoverEmergencyAcknowledgedEvent>` and emits one event per emergency notification when it is acknowledged. The event carries the `Target`, the `Emergency`, its `Id` (the receipt, the same as the `NotificationResult.Id` of the send), the `Timestamp` of the acknowledgement and the `AcknowledgedByDevice`. Until the system-wide message bus exists, subscribe on the target directly.

## Interfaces

| Subject | Interfaces |
|---------|------------|
| Service | `IConfigurable`, `IMonitoredService`, `ITitleProvider`, `IIconProvider` |
| Target | `INotificationPublisher`, `IObservable<PushoverEmergencyAcknowledgedEvent>`, `IConfigurable`, `IMonitoredService`, `ITitleProvider`, `IIconProvider` |
| Emergency | `ITitleProvider`, `IIconProvider` |

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

## Missing Features

The following features are not yet implemented but may be added in future versions:

### Sound, Device and Time to Live

Pushover can play a chosen sound, deliver to one device of the user, and delete a message after a time. Each user can already choose the sound in the Pushover app.

### Quota State

Every response carries the monthly message limit, the remaining messages and the reset time in `X-Limit-App-*` headers, which could be shown on the service. The free quota is 10,000 messages a month.

### HTML and Monospace Formatting

Messages can contain bold, italic, colored and linked text, or use a monospace font.

### Image Attachments

A message can carry an image of up to 5 MB, for example a camera snapshot.

### Emergency Callback and Tags

Pushover can call a URL when an emergency notification is acknowledged, instead of being polled, which needs HomeBlaze reachable from the internet. Tags allow canceling a chosen group of emergency notifications at once.

### Message Timestamp

A message can carry the time of the event instead of the time it was sent.

### Groups API

Pushover groups can be created and their members managed through the API. Group keys already work as target keys.

### End-to-End Encryption

Messages can be encrypted with a key that only the recipient's devices hold.

### Glances

Small values can be pushed to watch faces and widgets.

### Answers

Pushover has no action buttons or replies, such as yes or no; Pushover lists them as an open feature request. Only the acknowledgement of emergency notifications and links are available, and an answer through a link would need HomeBlaze reachable from the phone.

## References

- [Pushover API](https://pushover.net/api)
- [Pushover receipts API](https://pushover.net/api/receipts)
