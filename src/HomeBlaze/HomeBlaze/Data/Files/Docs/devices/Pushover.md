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

Use a separate Pushover application, and so a separate application token, for each Pushover service and each installation, because canceling by tag acts on every emergency notification sent with the token.

The editor of the service sets the name, the application token and, per target, the name and the user key. A new target starts with the defaults below; change its emergency settings in the editor of the target.

### Target

| Property | Description |
|----------|-------------|
| `Name` | Display name |
| `UserKey` | Secret. The user key or group key of the recipient |
| `EmergencyRetry` | How often an emergency notification repeats until acknowledged, 30 seconds or more, default 60 seconds |
| `EmergencyExpire` | How long an emergency notification repeats, 30 seconds to 3 hours, default 1 hour |
| `MaximumActiveEmergencies` | How many emergency notifications of this recipient may be active at once, at least 1, default 5. Sending another one fails with an error naming the limit, before any request to Pushover |

## Operations

| Subject | Operation | Description |
|---------|-----------|-------------|
| Target | `SendNotification` | Sends a notification to this recipient and returns its id: the receipt of an emergency notification, otherwise the Pushover request id |
| Target | `CancelAllEmergencies` | Requires confirmation. Stops the repeats of every emergency notification of this recipient by its tag, also those sent before a restart |
| Emergency | `Cancel` | Requires confirmation. Stops the repeats of this emergency notification, enabled while it is active |
| Service | `SendToAllTargets` | Sends a notification to every target at once |
| Service | `CancelAllEmergencies` | Requires confirmation. Stops the repeats of the emergency notifications of every target by their tags, also those sent before a restart |

When `SendToAllTargets` or `CancelAllEmergencies` of the service fails for some targets, the others are still sent or canceled, and the error names the failed targets. The cancel operations require confirmation, so agents and MCP clients cannot call them; run them from the UI, which asks you to confirm.

## Priorities

| Notification priority | Pushover priority | Behavior |
|-----------------------|-------------------|----------|
| `Low` | -1 | No sound or vibration |
| `Normal` | 0 | The recipient's default alert |
| `High` | 1 | Alerts also during quiet hours |
| `Emergency` | 2 | Repeats every `EmergencyRetry` until acknowledged or until `EmergencyExpire`. Use it only for safety-critical events |

Each emergency notification is tracked on its own, so several can repeat at once, for example one for smoke and one for water, and each is acknowledged or canceled independently. See [Emergencies](#emergencies).

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
| `Emergencies` | The emergency notifications by receipt, see [Emergencies](#emergencies) |

## Emergencies

Sending an `Emergency` notification adds a Pushover emergency subject to the target's `Emergencies`, keyed by the receipt Pushover returned. The emergency is reachable at a path such as `/Devices/Pushover/Targets[rico]/Emergencies[r1a2b3]`.

| Property | Description |
|----------|-------------|
| `Receipt` | The receipt of the notification |
| `NotificationTitle` | The title of the notification as sent, or null when it had none |
| `Title` | The display title: the notification title, or "Emergency" when it had none |
| `Message` | The message of the notification |
| `SentAt` | When Pushover accepted the notification |
| `Status` | `Active`, `Acknowledged`, `Expired` or `Canceled` |
| `AcknowledgedAt` | When it was acknowledged |

| Status | Meaning |
|--------|---------|
| `Active` | The notification repeats and waits for acknowledgment |
| `Acknowledged` | The recipient acknowledged it |
| `Expired` | It stopped repeating after `EmergencyExpire` without acknowledgment |
| `Canceled` | It was canceled with `Cancel` or `CancelAllEmergencies`, or because its target was removed |

While an emergency notification is active, the service reads its receipt every 10 seconds; otherwise it makes no requests of its own, except that it validates the targets again every 30 seconds while it is in `Error` because Pushover cannot be reached. The receipts request carries the application token in its query string, which the .NET HTTP logging and tracing redact by default; do not disable that redaction. `Emergencies` keeps the 10 most recently sent emergencies that are no longer active, besides the active ones; an older one is removed when a newer one ends. When a history store is configured, the message and the status of each emergency are recorded in the history like any other state. `Emergencies` is kept in memory only: after a restart the list is empty, and an emergency that was still active keeps repeating on the phone until it is acknowledged or expires, without HomeBlaze tracking it. Every emergency notification is sent with the Pushover tag `target-{key}` of its target, with the key in lowercase and every character other than a letter, a digit or a dash replaced by a dash, so `CancelAllEmergencies` of the service or of the target still stops it. Removing a target cancels its tag in the background; if that fails, the emergency repeats until it expires. Once sending an emergency notification started, canceling the call does not stop it, so every emergency that Pushover accepted is tracked and can be canceled.

The service tracks the emergencies by the key of their target. When a configuration reload replaces a target but keeps its key, the new target shows the same emergencies and can still cancel them. The replaced instance is no longer part of the service: sending through it fails. An external edit of the configuration file replaces the target subjects this way, so their emergencies are briefly detached from the subject graph and attached again, keeping their state.

Receipts are read also while validating the targets fails, so an acknowledgment is not missed during a Pushover outage. A receipt that cannot be read, for example because of a network error, is read again on the next poll without changing the service status; after `EmergencyExpire` it is given up as `Expired`.

## Events

The service and each target implement `IObservable<PushoverEmergencyAcknowledgedEvent>`, which emits one event per emergency notification when it is acknowledged. Subscribe on the service for the events of all targets, or on a target for the events of its key; a subscription on a target keeps receiving them when a configuration reload replaces the target instance. Subscribing on a target that is not part of a service fails. The event carries the `TargetKey`, the `Emergency`, its `Id` (the receipt) and the `Timestamp` of the acknowledgment.

## Interfaces

| Subject | Interfaces |
|---------|------------|
| Service | `IObservable<PushoverEmergencyAcknowledgedEvent>`, `IConfigurable`, `IMonitoredService`, `ITitleProvider`, `IIconProvider` |
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
      "emergencyExpire": "01:00:00",
      "maximumActiveEmergencies": 5
    }
  }
}
```

## Limitations

An agent that may call `invoke_operation` can send any number of notifications below `Emergency`; only emergencies are bounded, by `MaximumActiveEmergencies`, so such an agent can use up the monthly message quota of the application. Pushover has no action buttons or replies, such as yes or no, so an answer is only possible as the acknowledgment of an emergency notification or through a link.

## Missing Features

The following features are not yet implemented but may be added in future versions:

- Sound, device and time to live: a chosen sound, delivery to one device of the user, and deleting a message after a time. Each user can already choose the sound in the Pushover app.
- Quota state: every response carries the monthly message limit, the remaining messages and the reset time in `X-Limit-App-*` headers, which could be shown on the service. The free quota is 10,000 messages a month.
- HTML and monospace formatting: bold, italic, colored and linked text, or a monospace font.
- Image attachments: an image of up to 5 MB, for example a camera snapshot.
- Emergency callback: Pushover can call a URL when an emergency notification is acknowledged, instead of being polled, which needs HomeBlaze reachable from the internet.
- Message timestamp: the time of the event instead of the time it was sent.
- Groups API: creating Pushover groups and managing their members. Group keys already work as target keys.
- End-to-end encryption with a key that only the recipient's devices hold.
- Glances: small values pushed to watch faces and widgets.

## References

- [Pushover API](https://pushover.net/api)
- [Pushover receipts API](https://pushover.net/api/receipts)
