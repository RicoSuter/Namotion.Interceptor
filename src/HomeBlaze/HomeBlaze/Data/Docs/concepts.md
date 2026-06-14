---
title: Concepts
navTitle: Concepts
position: 1
---

# Concepts

HomeBlaze is a platform for building real-time digital twins — live object models that mirror physical and virtual systems. This page introduces the core ideas in five minutes.

---

## What HomeBlaze Does

HomeBlaze hosts **subjects** (tracked objects representing devices, files, processes, or any domain entity) and synchronizes them with external systems via protocol connectors (OPC UA, MQTT, WebSocket). Both human operators (via a Blazor UI) and AI agents (via MCP) can browse, query, and act on the resulting knowledge graph.

For the full architectural view, see [Architecture Overview](architecture/overview.md).

---

## Subjects

A **subject** is an intercepted .NET object whose properties are automatically tracked. Any class decorated with `[InterceptorSubject]` becomes a subject — motors, sensors, files, folders, dashboards.

Subjects have:
- **Properties** — values the subject exposes (speed, temperature, status)
- **Operations** — methods operators or agents can invoke
- **Metadata** — display name, icon, authorization rules

See [Building Subjects](development/building-subjects.md) for how to author them in C#.

---

## The Object Graph

All subjects live in a tree — the **object graph** — rooted at a top-level storage container:

```
Root (FluentStorageContainer)
├── Children
│   ├── demo/
│   │   ├── Conveyor.json (Motor)
│   │   └── dashboard.md (MarkdownFile)
│   └── docs/
│       └── Configuration.md (MarkdownFile)
```

Folders become path segments. Files become subjects. Every subject has a **path** like `/demo/Conveyor` — see [Paths](administration/paths.md).

---

## Three Kinds of Properties

Subjects distinguish three kinds of properties by attribute:

| Attribute | Persisted? | Displayed? | Purpose |
|---|---|---|---|
| `[Configuration]` | Yes | In edit panel | Settings that survive restarts (IP address, interval) |
| `[State]` | No | In property panel | Live values (current speed, temperature) |
| `[Derived]` | No | In property panel | Computed from other properties (full name, efficiency) |

See [Building Subjects](development/building-subjects.md) for author details and [Configurable Subject Serialization](development/configurable-subject.md) for persistence rules.

---

## Storage

Subjects are persisted as JSON files on disk (or another blob backend). The folder structure directly mirrors the object graph: `Data/demo/motor1.json` becomes the subject at `/demo/motor1`.

The root storage container is defined in `root.json`. See [Subjects, Storage & Files](administration/subjects.md) for the admin's view and [Storage Design](architecture/design/storage.md) for the design rationale (pluggable backends, recovery model).

---

## UI and APIs

HomeBlaze exposes the object graph through multiple surfaces:

| Surface | Audience | Purpose |
|---|---|---|
| Blazor UI | Human operators | Browse, configure, invoke operations, view dashboards |
| Markdown pages | Human operators | Interactive dashboards with live data binding |
| MCP server | AI agents | Tool-call access to browse/query/invoke |
| Protocol connectors | External systems | OPC UA, MQTT, WebSocket sync |

---

## Where to Go Next

| Task | Guide |
|---|---|
| Get HomeBlaze running | [Installation](administration/installation.md) |
| Configure app settings | [Configuration](administration/configuration.md) |
| Add and edit subjects | [Subjects, Storage & Files](administration/subjects.md) |
| Reference a subject by path | [Paths](administration/paths.md) |
| Build an interactive page | [Markdown Pages](administration/pages.md) |
| Write a custom subject in C# | [Building Subjects](development/building-subjects.md) |
| Look up a term | [Glossary](glossary.md) |
