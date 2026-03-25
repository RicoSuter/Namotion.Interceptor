---
title: HomeBlaze Documentation
navTitle: Home
position: 0
---

# HomeBlaze Documentation

HomeBlaze is a modular .NET platform for building real-time digital twins. It models physical and virtual systems as trackable object graphs — **subjects** with properties, operations, and metadata — and keeps them synchronized with external systems via protocol connectors (OPC UA, MQTT, WebSocket).

Multiple HomeBlaze instances can form a distributed topology: satellites connect to field devices, a central instance aggregates them into a Unified Namespace, and active-standby pairs provide high availability. Both human operators (via Blazor UI) and AI agents (via MCP) can browse, query, and act on the knowledge graph.

## Documentation Sections

### [Architecture](architecture/overview.md)

System architecture, scaling stages, deployment topology, and design decisions. Start here to understand how HomeBlaze works as a system.

- [Architecture Overview](architecture/overview.md) — Arc42 system architecture
- [Implementation State](architecture/state.md) — What is implemented vs. planned
- [Project Structure](architecture/project-structure.md) — .NET project layout and dependencies
- [Design Documents](architecture/design/) — Detailed designs for individual building blocks

### [Administration](administration/installation.md)

Setup, deployment, and runtime configuration for operators and administrators.

- [Installation](administration/installation.md) — Getting started
- [Configuration](administration/configuration.md) — Storage, paths, and subject configuration

### [Development](development/building-subjects.md)

Guides for building custom subjects, plugins, and UI components.

- [Building Subjects](development/building-subjects.md) — Creating custom subject types
- [Configurable Subjects](development/configurable-subject.md) — Subject serialization and persistence
- [Markdown Pages](development/pages.md) — Interactive pages with embedded subjects

### [Devices](devices/gpio.md)

Per-device documentation and setup guides.

### [Glossary](glossary.md)

Terms and concepts used throughout HomeBlaze.
