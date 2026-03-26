---
title: Alarms and Events
navTitle: Alarms
---

# Alarms and Events Design [Planned]

## Overview

Alarm and event management handles detection, notification, acknowledgment, and history of abnormal conditions across the knowledge graph.

**This building block is planned but not yet designed.** Core multi-instance sync and knowledge graph must stabilize first.

## Known Requirements

- Alarm conditions defined on subject properties (thresholds, state changes, rate of change)
- Alarm lifecycle: active, acknowledged, cleared
- Alarm history and statistics
- Integration with OPC UA Alarms & Conditions (industrial standard)
- Notification channels (UI, email, push, webhook) — alarm notifications should use subjects implementing `INotificationPublisher` (see [Notifications](../../development/notifications.md)). Which channels receive which alarms must be configurable per alarm definition (e.g., critical alarms to email + push, warnings to UI only)

## Open Questions

- Alarm definition model (declarative on subjects? separate alarm subjects?)
- Cross-instance alarm propagation (satellite detects, central aggregates?)
- Alarm shelving and suppression
- Integration with AI agents (agents as alarm responders)
- Priority and severity classification
