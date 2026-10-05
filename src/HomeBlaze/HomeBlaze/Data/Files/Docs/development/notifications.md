---
title: Notifications
navTitle: Notifications
position: 5
---

# Notifications

## Overview

HomeBlaze defines a standard interface for subjects that can send notifications to users. Notification channels are subjects themselves — any subject implementing `INotificationPublisher` can deliver messages to operators.

## INotificationPublisher

```csharp
public interface INotificationPublisher
{
    [Operation]
    Task SendNotificationAsync(string message, CancellationToken cancellationToken);
}
```

A notification publisher is any subject that delivers messages to an external channel. Examples include:

| Channel | Description |
|---------|-------------|
| Email | Sends notifications via SMTP or email API |
| Push notification | Mobile or browser push notifications |
| Telegram / Slack | Chat platform integration |
| Webhook | HTTP POST to a configured URL |
| SMS | Text message delivery |

Each channel is a separate subject with its own `[Configuration]` properties (e.g., SMTP server, API key, recipient list). Operators configure notification channels through the standard subject editor UI.

## Usage

Since `SendNotificationAsync` is marked with `[Operation]`, it is:
- Invocable from the Blazor UI (operators can send test notifications)
- Discoverable via the registry and MCP tools (`list_methods`, `invoke_method`)
- Callable by built-in AI agents for automated notifications

### From Other Subjects

Subjects that need to send notifications (e.g., alarm handlers, automation rules) can resolve notification publishers from the knowledge graph and invoke them directly:

```csharp
// Find all notification channels
var publishers = registry
    .GetAllSubjects()
    .OfType<INotificationPublisher>();

// Send to all configured channels
foreach (var publisher in publishers)
{
    await publisher.SendNotificationAsync(
        "Temperature exceeded threshold on CNC-01",
        cancellationToken);
}
```

In the planned [Alarms](../architecture/design/alarms.md) system, alarm definitions will include configurable notification routing — which channels receive which alarms, based on alarm severity and type.
