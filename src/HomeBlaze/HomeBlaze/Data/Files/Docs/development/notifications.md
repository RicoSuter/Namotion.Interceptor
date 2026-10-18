---
title: Notifications
navTitle: Notifications
position: 5
---

# Notifications

## Overview

HomeBlaze defines a standard interface for subjects that can send notifications to users. Notification channels are subjects themselves: any subject implementing `INotificationPublisher` delivers messages to one recipient or group.

## INotificationPublisher

```csharp
public interface INotificationPublisher
{
    [Operation]
    Task<NotificationResult> SendNotificationAsync(Notification notification, CancellationToken cancellationToken);
}

public sealed record Notification
{
    public required string Message { get; init; }
    public string? Title { get; init; }
    public NotificationPriority Priority { get; init; } = NotificationPriority.Normal;
    public string? Url { get; init; }
    public string? UrlTitle { get; init; }
}

public sealed record NotificationResult
{
    public string? Id { get; init; }
}
```

Members are added to `Notification` as optional members, so publishers and callers keep working when it grows. A channel ignores a member it cannot show and shortens a value longer than its own limit. `Url` must be an absolute `http` or `https` address; a channel leaves out any other value together with `UrlTitle`. Each member carries a `[Description]` that the MCP tools list to agents.

| Priority | Meaning |
|----------|---------|
| `Low` | Delivered without sound or vibration |
| `Normal` | Delivered with the recipient's default alert |
| `High` | Delivered with an alert also during quiet hours |
| `Emergency` | Repeated until acknowledged, for safety-critical events only. A channel without repeats delivers it as `High` |

`NotificationResult.Id` is the channel's identifier of the delivered notification, or null when the channel has none. Its format is channel-specific; for Pushover it is the receipt of an emergency notification and the request id otherwise, see [Pushover](../devices/Pushover.md). Acknowledgement events of a channel carry the same id, so a caller can tell which acknowledgement belongs to its send.

A notification publisher is any subject that delivers messages to an external channel, for example [Pushover](../devices/Pushover.md), email, chat platforms, webhooks or SMS. Each channel has its own `[Configuration]` properties (server, API key, recipient), edited through the standard subject editor.

### One Publisher per Recipient

Implement `INotificationPublisher` on the subject that delivers to one recipient or group, not on a subject that holds several of them. Callers send to every publisher they find, so a container that also published would deliver each message twice. A container can offer its own operation to send to all its recipients, such as the Pushover service's `SendToAllTargets`.

## Usage

Since `SendNotificationAsync` is marked with `[Operation]`, it is:
- Invocable from the Blazor UI, where the notification members are entered in a form (operators can send test notifications)
- Discoverable via the registry and MCP tools (`list_methods`, `invoke_operation`)
- Callable by built-in AI agents for automated notifications

### From AI Agents

A [built-in agent](../administration/ai-agents.md) finds the publishers through its tools: `list_types` finds the `INotificationPublisher` interface, and `search` with that interface in its `types` parameter finds the subjects that implement it. `list_methods` shows the exact method name and parameters: the operation is registered without the `Async` suffix, as `SendNotification` with a `notification` parameter whose members are listed with their types and descriptions, for example `{"notification": {"message": "Door open", "priority": "High"}}`. The priority is given by name; a number or an unknown member is rejected. The agent calls it with `invoke_operation`, which returns the `NotificationResult` as its result, for example `{"success": true, "result": {"Id": "r1a2b3"}}`, and must be in the agent's `AllowedTools`; it is off by default, so an agent without it can only report findings on its page. Do not mark `SendNotificationAsync` with `RequiresConfirmation`, because operations that require confirmation cannot be called through MCP, see [AI Agents](../administration/ai-agents.md#tools-and-permissions).

### From Other Subjects

Subjects that need to send notifications (e.g., alarm handlers, automation rules) can resolve notification publishers from the registry and invoke them directly:

```csharp
// Find all notification channels
var publishers = registry.KnownSubjects.Keys
    .OfType<INotificationPublisher>()
    .ToArray();

// Send to all configured channels
var notification = new Notification
{
    Message = "Temperature exceeded threshold on CNC-01",
    Priority = NotificationPriority.High
};

foreach (var publisher in publishers)
{
    await publisher.SendNotificationAsync(notification, cancellationToken);
}
```

In the planned [Alarms](../architecture/design/alarms.md) system, alarm definitions will include configurable notification routing: which channels receive which alarms, based on alarm severity and type.
