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
- [Runtime Scenarios](architecture/runtime-scenarios.md) — Step-by-step system flows
- [Implementation State](architecture/state.md) — What is implemented vs. planned
- [Project Structure](architecture/project-structure.md) — .NET project layout and dependencies
- [Design Documents](architecture/design/) — Detailed designs for individual building blocks
- [Architecture Decisions](architecture/decisions/) — ADRs for significant design choices

### [Plans](plans/)

Working design documents for features not yet implemented. During implementation, the relevant content migrates into [design documents](architecture/design/) and [architecture decisions](architecture/decisions/). Completed plans are removed. [Implementation State](architecture/state.md) tracks overall status.

- [Dynamic Subject Proxying](plans/dynamic-subject-proxying.md) — WebSocket sync with dynamic proxy subjects
- [AI Agents](plans/ai-agents.md) — Built-in LLM-powered agent subjects

### [Administration](administration/installation.md)

Setup, deployment, and runtime configuration for operators and administrators.

- [Installation](administration/installation.md) — Getting started
- [Configuration](administration/configuration.md) — Storage, paths, and subject configuration
- [Markdown Pages](administration/pages.md) — Interactive pages with embedded subjects
- [Monitoring](administration/monitoring.md) — Health checks and observability
- [Troubleshooting](administration/troubleshooting.md) — Diagnostics and common issues
- [Upgrading](administration/upgrading.md) — Version upgrades and migration

### [Development](development/building-subjects.md)

Guides for building custom subjects, plugins, and UI components.

- [Building Subjects](development/building-subjects.md) — Creating custom subject types
- [Configurable Subjects](development/configurable-subject.md) — Subject serialization and persistence
- [Notifications](development/notifications.md) — Notification channel interface

### [Devices](devices/gpio.md)

Per-device documentation and setup guides.

### [Glossary](glossary.md)

Terms and concepts used throughout HomeBlaze.
