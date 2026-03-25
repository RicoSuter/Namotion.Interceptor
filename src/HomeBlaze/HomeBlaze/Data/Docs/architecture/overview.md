---
title: HomeBlaze Architecture
navTitle: Architecture
position: 0
---

# HomeBlaze Architecture

This document describes the target architecture of HomeBlaze using the arc42 template. It serves as an introduction for evaluators and a reference for contributors. For implementation status of individual building blocks, see [Implementation State](state.md). Detailed designs are in [design/](design/).

## 1. Introduction and Goals

HomeBlaze is a modular .NET platform for building real-time digital twins: trackable object graphs that mirror physical or virtual systems. Built on Namotion.Interceptor, it provides automatic property interception, change tracking, and a Blazor-based operator UI out of the box.

Each HomeBlaze instance acts as a digital twin of its domain — a live model that stays synchronized with the physical or virtual system it represents.

Any domain — industrial plants, smart buildings, IoT networks — can be modeled as **subjects** (intercepted objects with tracked properties and operations). HomeBlaze hosts these subjects, connects them to external systems via protocol connectors (OPC UA, MQTT, WebSocket), and exposes them through a **Unified Namespace (UNS)** — a single, aggregated view of all operational data.

On top of the UNS, HomeBlaze builds a **knowledge graph** that extends live state with property history, documents, metadata, and AI-queryable structure. Both human operators and AI agents can browse, query, and act on this knowledge graph.

### Key Requirements

| Priority | Requirement |
|----------|------------|
| 1 | **Unified Namespace** — All subjects across all domains accessible through a single, browsable graph with typed properties, metadata, and operations |
| 2 | **Horizontal Scaling** — Multiple instances each own a domain slice and sync upward to a central instance via WebSocket |
| 3 | **High Availability** — Active-standby pairs with automatic failover via device connection promotion |
| 4 | **AI-Ready Knowledge Graph** — Browsable subjects, queryable properties, executable operations, property history, documents, and reactive change streams |
| 5 | **Same Binary Everywhere** — Role determined by loaded plugins and connector configuration, not separate binaries |

### Stakeholders

| Role | Concern |
|------|---------|
| Plant/building operator | Browse live state, execute operations, view dashboards |
| System integrator | Configure instances, map protocol sources to subjects, define domain models |
| AI agent | Query the knowledge graph, react to changes, execute operations |
| Platform developer | Extend with new subject types, connectors, and UI components |

---

## 2. Constraints

### Technical

| Constraint | Description |
|-----------|------------|
| .NET 9+ with C# 13 preview | Required for partial properties and source generation |
| Namotion.Interceptor foundation | All subject tracking, change detection, and connector protocols build on the interceptor library |
| WebSocket for inter-node sync | Instances communicate via the WebSocket connector (SubjectUpdate protocol) |
| Same binary, config-driven roles | Node role (satellite, central, standby) is determined by loaded plugins and configuration |

### Organizational

| Constraint | Description |
|-----------|------------|
| Domain-agnostic | The architecture must not assume a specific domain. Domain specifics live in plugins |
| Early maturity | The platform is in active development. Breaking changes are acceptable |
| Open source | Core platform and connectors are open source. Domain-specific plugins may be proprietary |

---

## 3. Context and Scope

### System Context

HomeBlaze sits between external device/data sources and its consumers. Each instance connects to domain-specific sources and participates in a multi-instance topology.

```
                        ┌────────────────────────────────┐
                        │           Consumers            │
                        │   Operators (Blazor UI)        │
                        │   AI Agents (built-in + MCP)   │
                        │   Third-party (MQTT, OPC UA)   │
                        └───────────────┬────────────────┘
                                        │
                        ┌───────────────▼────────────────┐
                        │      HomeBlaze Central         │
                        │      Unified Namespace         │
                        │      Knowledge Graph           │
                        └───┬────────────────────────┬───┘
                            │ WebSocket              │ WebSocket
                ┌───────────▼───────────┐ ┌──────────▼───────────┐
                │  HomeBlaze Satellite  │ │  HomeBlaze Satellite │
                │  OPC UA / MQTT /      │ │  OPC UA / MQTT /     │
                │  other connectors     │ │  other connectors    │
                └───────────┬───────────┘ └──────────┬───────────┘
                            │                        │
                  ┌─────────▼─────────┐   ┌──────────▼──────────┐
                  │   Field Devices   │   │   Field Devices     │
                  └───────────────────┘   └─────────────────────┘
```

### External Interfaces

| Interface | Direction | Protocol | Purpose |
|-----------|-----------|----------|---------|
| Field devices | Bidirectional | OPC UA, MQTT, custom | Read sensor data, write setpoints, call methods |
| Inter-node sync | Bidirectional | WebSocket (SubjectUpdate) | State and operation replication between instances |
| Standby replication | Unidirectional | WebSocket (SubjectUpdate) | Full state sync for high availability |
| Operator UI | Outbound | Blazor (SignalR) | Browse knowledge graph, execute operations, view dashboards |
| AI agents | Bidirectional | MCP (external), direct graph access (built-in) | Query subjects, react to changes, execute operations |
| Third-party consumers | Bidirectional | MQTT, OPC UA server | Expose knowledge graph to SCADA, historians, external systems |

### Scope Boundary

HomeBlaze owns the knowledge graph and its synchronization, including:

- Live state (subject properties, the Unified Namespace)
- History (time-series via plugin)
- Metadata and annotations (property attributes, user-defined dynamic attributes)
- Documents (storage subjects)
- Operations (methods on subjects, proxied across instances)
- AI agents (built-in subjects and external via MCP)

It does not currently own:

- Device firmware or PLC programs
- Alarm/event management (planned as a future building block — see [Alarms](design/alarms.md))

---

## 4. Building Block View

### Level 1 — Top-Level Building Blocks

```
┌─────────────────────────────────────────────────────────────┐
│                      HomeBlaze Instance                     │
│                                                             │
│  ┌────────────────┐  ┌───────────────┐  ┌────────────────┐  │
│  │  Knowledge     │  │  Connectors   │  │ Multi-Instance │  │
│  │  Graph         │  │               │  │ & HA           │  │
│  │                │  │  OPC UA       │  │                │  │
│  │  Subjects      │  │  MQTT         │  │ WebSocket Sync │  │
│  │  Operations    │  │  WebSocket    │  │ Standby Repl.  │  │
│  │  History       │  │  Custom       │  │ Failover       │  │
│  │  Documents     │  │               │  │                │  │
│  │  AI Agents     │  │               │  │                │  │
│  └────────┬───────┘  └───────┬───────┘  └───────┬────────┘  │
│           │                  │                   │          │
│  ┌────────▼──────────────────▼───────────────────▼───────┐  │
│  │              Namotion.Interceptor Core                │  │
│  │  Property Interception · Change Tracking · Registry   │  │
│  └───────────────────────────────────────────────────────┘  │
│                                                             │
│  ┌───────────────────────────────────────────────────────┐  │
│  │  Cross-Cutting                                        │  │
│  │  Plugin System (NuGet) · Operator UI (Blazor) ·       │  │
│  │  MCP Server · Observability                           │  │
│  └───────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────┘
```

Every HomeBlaze instance contains the same building blocks. What makes a satellite different from a central instance is which plugins are loaded and which connectors are active. All building blocks above the core — connectors, agents, device subjects, domain logic, UI components — are delivered as plugins (NuGet packages).

Detailed designs: [AI](design/ai.md), [History](design/history.md), [Plugins](design/plugins.md), [Security](design/security.md), [Observability](design/observability.md), [Configuration](design/configuration.md), [Audit](design/audit.md), [Alarms](design/alarms.md), [Versioning](design/versioning.md).

### Level 2 — Knowledge Graph

The knowledge graph is the central data structure. It unifies live state, history, metadata, documents, and operations into a single queryable model.

The **Unified Namespace (UNS)** is the live state layer: all subject properties and operations, synced across instances in real time. The **knowledge graph** extends the UNS with property history, documents, and user-defined metadata (annotations, tags, links between subjects).

```
┌──────────────────────────────────────────────────────┐
│                    Knowledge Graph                   │
│                                                      │
│  ┌───────────────────────────────────────────────┐   │
│  │  Unified Namespace (live state)               │   │
│  │                                               │   │
│  │  Typed properties with change tracking        │   │
│  │  Derived properties (computed)                │   │
│  │  Operations ([Operation] and [Query] methods) │   │
│  │  Property attributes (metadata, units, etc.)  │   │
│  │  Registry (browsable, queryable)              │   │
│  └───────────────────────────────────────────────┘   │
│                                                      │
│  ┌──────────────────────┐  ┌───────────────────────┐ │
│  │  Time-Series Store   │  │  Document Store       │ │
│  │                      │  │                       │ │
│  │  Property history    │  │  Markdown, PDFs,      │ │
│  │  Plugin-based sinks  │  │  config files         │ │
│  │  Queryable by path   │  │  Documents are        │ │
│  │  and time range      │  │  subjects themselves  │ │
│  └──────────────────────┘  └───────────────────────┘ │
└──────────────────────────────────────────────────────┘
```

| Layer | Contents | Access |
|-------|----------|--------|
| Unified Namespace (live) | Typed properties, derived properties, metadata attributes, operations | Registry, MCP tools, connectors |
| Time-Series Store | Property history, configurable retention | `get_history` MCP tool, plugin-based (see [History](design/history.md)) |
| Document Store | Markdown, PDFs, config files, linked to subjects via dynamic attributes | Standard `query` + `invoke_method` on document subjects |

Subjects can also carry user-defined metadata — annotations, tags, and links to other subjects — stored as dynamic attributes on the registry. These are persisted in configuration files and reapplied on restart, allowing operators to enrich the knowledge graph without modifying subject code. See [Configuration](design/configuration.md) for details.

**How each consumer accesses the knowledge graph:**

The knowledge graph is accessed through two layers of MCP tools. The base layer (`Namotion.Interceptor.Mcp`) provides generic subject browsing, property read/write, and type listing — usable with any Namotion.Interceptor application. The HomeBlaze layer (`HomeBlaze.Mcp`) adds rich querying with metadata (`$type`, `$icon`, `$title`, `$methods`), operation invocation, method listing, and history queries.

| Consumer | Access Pattern |
|----------|---------------|
| Operator UI | TrackingScope for live updates, subject browser for navigation, operations for actions |
| AI agents | Base MCP tools (`get_property`, `set_property`, `list_types`) + HomeBlaze MCP tools (`query`, `invoke_method`, `list_methods`, `get_history`) |
| Connectors | Read/write interceptors feed property changes in and out, operations map to protocol methods (e.g. OPC UA methods) |
| WebSocket sync | SubjectUpdate messages replicate the subject graph and proxy operations between instances |

### Level 2 — Connector Layer

Connectors operate at two levels. The core `Namotion.Interceptor` packages provide protocol implementations (OPC UA sync, MQTT messaging, WebSocket replication). HomeBlaze wraps these as manageable subjects with configuration, lifecycle, and UI — and adds higher-level features like automatic device discovery.

**Core connectors (Namotion.Interceptor)**

| Connector | Direction | Purpose |
|-----------|-----------|---------|
| OPC UA Client/Server | Bidirectional | Property sync with industrial devices, method mapping to operations |
| MQTT Client/Server | Bidirectional | IoT messaging, topic-based property mapping |
| WebSocket | Bidirectional | Subject graph replication, operation proxying |

**HomeBlaze connector subjects (plugins)**

| Subject | Wraps | Adds |
|---------|-------|------|
| OPC UA Server | `Namotion.Interceptor.OpcUa` server | Configurable subject with start/stop, path selection, UI editor |
| OPC UA Client (planned) | `Namotion.Interceptor.OpcUa` client | Connects to remote OPC UA servers, auto-discovers subjects (dynamic or statically typed) |
| Device subjects | Any core connector or custom protocol | Domain-specific devices (e.g., Philips Hue bridge, thermostat) as subjects with properties, operations, and custom UI |

All core connectors use the same interceptor pattern: inbound data from external systems is applied to subject properties with source tagging (to prevent feedback loops where a change echoes back to its origin), and outbound changes flow through a change queue that batches and distributes updates. See the [connectors documentation](../../../../../../docs/connectors.md) for protocol-level details.

### Level 2 — Multi-Instance and High Availability

Horizontal scaling and resilience both use the same WebSocket connector. A satellite syncs its full subject graph to the central instance; a standby syncs from its primary. Both use the SubjectUpdate protocol with Welcome snapshots for initial state and incremental updates for ongoing changes.

```
┌──────────────────┐           ┌───────────────────┐
│  Primary         │           │  Standby          │
│                  │    WS     │                   │
│  Device Conns.   │<----------│  WebSocket Client │
│  WebSocket Srv.  │           │  WebSocket Srv.   │
│  Knowledge Graph │           │  Knowledge Graph  │
│  (authoritative) │           │  (replica)        │
└──────────────────┘           └───────────────────┘
```

| Relationship | Mechanism | Data Flow |
|-------------|-----------|-----------|
| Satellite to Central | WebSocket client on satellite, server on central | State flows up, writes and operations flow down |
| Primary to Standby | WebSocket server on primary, client on standby | Full state replication |

On failover, the standby detects the primary is unreachable and performs a **fencing check** — it verifies it can still reach the field devices. This prevents false promotion during network partitions (if both primary and devices are unreachable, the standby assumes a network partition and does not promote). If fencing passes, the standby activates device connections, reads current device state to close any sync gap, and becomes the new primary. State converges because external devices are the authoritative source of truth — no distributed consensus is needed.

---

## 5. Runtime View

### Startup and Multi-Instance Sync

```
Satellite (Domain A)               Central                        Operator UI
       |                              |                               |
       |  1. Start, load plugins      |                               |
       |  2. Connect to devices       |                               |
       |     (OPC UA, MQTT, ...)      |                               |
       |  3. Build local knowledge    |                               |
       |     graph from device state  |                               |
       |                              |                               |
       |-- 4. WebSocket Connect ----->|                               |
       |<- 5. Hello / Welcome --------|                               |
       |                              |                               |
       |-- 6. SubjectUpdate (full) -->|  7. Merge into                |
       |                              |     unified namespace         |
       |                              |                               |
       |-- 8. SubjectUpdate (incr.) ->|  9. Update graph              |
       |                              |-- 10. Broadcast ------------->|
       |                              |      to all consumers         |
```

Incremental updates flow continuously — property values, structural changes (subjects added/removed via plug-and-play or auto-discovery), and operation invocations propagate across the topology. The full subject graph is live and reactive, changing dynamically as devices are discovered, plugins are loaded, or subjects are reconfigured.

### Write Path (Consumer to Device)

A consumer (operator or AI agent) writes a property or invokes an operation on the central UNS. The change flows down through the topology to the owning satellite, which executes it against the device. The confirmed value flows back up via the normal change path.

```
Operator UI                  Central                  Satellite              Device
     |                            |                        |                   |
     |  1. Set Temperature = 80   |                        |                   |
     |--------------------------->|                        |                   |
     |                            |  2. Apply to local     |                   |
     |                            |     graph, broadcast   |                   |
     |                            |-- 3. SubjectUpdate --->|                   |
     |                            |                        |  4. Write via     |
     |                            |                        |     connector     |
     |                            |                        |------------------>|
     |                            |                        |                   |
     |                            |                        |<-- 5. Confirmed --|
     |                            |                        |    (subscription) |
     |                            |<-- 6. Confirmed value--|                   |
     |<-- 7. Broadcast -----------|                        |                   |
     |   (confirmed value)        |                        |                   |
```

### Standby Failover

```
Primary                  Standby                        Central
    |                        |                             |
    |-- WS sync (ongoing) -->|                             |
    |                        |                             |
    X  Primary crashes       |                             |
                             |                             |
                 1. Detect primary    |                    |
                    unreachable       |                    |
                 2. Verify device     |                    |
                    reachability      |                    |
                    (fencing check)   |                    |
                 3. Activate device   |                    |
                    connections       |                    |
                 4. Read current      |                    |
                    device state      |                    |
                 5. Become new        |                    |
                    primary           |                    |
                             |                             |
                             |-- 6. Resume sync ---------->|
                             |      (as new primary)       |
```

State converges because external devices are the source of truth.

### AI Agent Interaction

```
AI Agent                  Knowledge Graph              Time-Series         Documents
     |                          |                          |                    |
     |  1. query(path, depth)   |                          |                    |
     |------------------------->|                          |                    |
     |<-- subject tree ---------|                          |                    |
     |                          |                          |                    |
     |  2. get_property(path)   |                          |                    |
     |------------------------->|                          |                    |
     |<-- value + metadata -----|                          |                    |
     |                          |                          |                    |
     |  3. get_history(path)    |                          |                    |
     |------------------------->|------------------------->|                    |
     |<-- time-series data -----|<-------------------------|                    |
     |                          |                          |                    |
     |  4. invoke_method(path,  |                          |                    |
     |     "Read")              |                          |                    |
     |------------------------->|---------------------------------------------->|
     |<-- document content -----|<----------------------------------------------|
     |                          |                          |                    |
     |  5. set_property(path,   |                          |                    |
     |     value)               |                          |                    |
     |------------------------->|  (flows through          |                    |
     |                          |   write path)            |                    |
```

Built-in agents access the same tools directly (in-process) and can additionally subscribe to property changes via reactive streams, triggering analysis only when declarative filter rules are met. External agents connect via MCP protocol and interact on-demand. See [AI Integration](design/ai.md) for details.

---

## 6. Scalability and Deployment

HomeBlaze scales in stages. Each stage adds capability without redesigning what came before — same binary, same subjects, different configuration.

### Stage 1: Single Instance

One process with everything: device connectors, knowledge graph, UI, AI agents. No satellites, no standby. Just a single process. Suitable for single home, small facility, prototyping.

```
┌────────────────────────────┐
│  HomeBlaze                 │
│                            │
│  Device connectors         │
│  Knowledge graph           │
│  Time-series store         │
│  Blazor UI + AI/MCP        │
└─────────────┬──────────────┘
              |
       Field devices
```

### Stage 2: Single Instance + Standby

Add a second node for resilience. Standby syncs via WebSocket, promotes on failure. For production single-site with uptime requirements.

```
┌─────────────────┐           ┌─────────────────┐
│  Primary        │    WS     │  Standby        │
│  (everything)   │<----------│  (replica)      │
└────────┬────────┘           └─────────────────┘
         |
    Field devices
```

### Stage 3: Multi-Instance

Split into satellites (device connections per area) and central UNS (aggregation, full history). For multi-area deployments, independent teams, deployment isolation.

```
┌─────────────────┐  ┌─────────────────┐
│  Satellite A    │  │  Satellite B    │
│  OPC UA/MQTT    │  │  OPC UA/MQTT    │
└────────┬────────┘  └────────┬────────┘
         |   WS               |   WS
         └─────────┬──────────┘
                   |
         ┌────────▼───────────┐
         │  Central UNS       │
         │  UI + AI/MCP       │
         │  Full history      │
         └────────────────────┘
```

### Stage 4: Multi-Instance + HA

Each satellite and central instance gets a standby. Same active-standby pattern at every level. For production multi-site with zero-tolerance downtime.

```
┌──────────────────┐        ┌─────────────────┐
│ Satellite A      │<-------│ Satellite A     │
│ Primary          │        │ Standby         │
└────────┬─────────┘        └─────────────────┘
         | WS
┌────────▼─────────┐        ┌─────────────────┐
│ Central UNS      │<-------│ Central UNS     │
│ Primary          │        │ Standby         │
└──────────────────┘        └─────────────────┘
```

### Migration Requires No Code Changes

| Transition | What changes |
|------------|-------------|
| 1 to 2 | Add second node, configure as standby |
| 2 to 3 | Move device connectors to satellite configs, add central UNS |
| 3 to 4 | Add standby node per pair |

### Resource Estimates

| Scale | Nodes | Properties | Memory per node |
|-------|-------|-----------|----------------|
| Small (single home) | 1 | ~1,000 | 128 MB |
| Medium (building/small plant) | 3-5 | ~50,000 | 512 MB |
| Large (multi-area plant) | 10+ | ~500,000 | 1-2 GB |

For containerized environments, each HA pair maps to a StatefulSet with 2 replicas. Pod ordinal determines role (0 = primary, 1 = standby). Services route traffic to the primary pod via role labels. Headless services provide stable DNS for standby-to-primary connections.

**Persistence model.** HomeBlaze does not persist live state — each instance recovers from its source of truth on restart. Satellites reconnect to field devices and re-read current values. Central instances receive Welcome snapshots from reconnecting satellites. Standbys receive Welcome snapshots from their primary. Only subject configuration (settings, topology) and time-series history are locally persisted. See [Configuration](design/configuration.md) for details.

---

## 7. Architecture Decisions

| # | Decision | Choice | Rationale |
|---|----------|--------|-----------|
| 1 | Node runtime | Same binary everywhere | Simplifies deployment, testing, and upgrades. Role is a configuration concern |
| 2 | Inter-node protocol | WebSocket (SubjectUpdate) | Atomic snapshots, structural graph sync, sequence-based consistency. Already built into Namotion.Interceptor |
| 3 | Device connection ownership | Satellites, not central | Failure isolation, independent deployment per domain |
| 4 | HA pattern | Active-standby (2 nodes per pair) | External devices are the source of truth. No need for distributed consensus |
| 5 | Failover mechanism | Standby promotes by activating device connections | Self-contained, fencing check prevents false promotion |
| 6 | Knowledge graph foundation | Namotion.Interceptor subject graph with registry | Typed properties, metadata, change tracking, and queryability with no impedance mismatch |
| 7 | Operations | First-class on subjects, migrating to Namotion.Interceptor registry | Natural extension: subjects have properties (nouns) and operations (verbs). Enables OPC UA method mapping |
| 8 | AI integration | Built-in agents (subjects) + external agents (MCP) | Two-mode model: in-process for automation, MCP for ad-hoc copilots |
| 9 | MCP tool layering | `Namotion.Interceptor.Mcp` (base) + `HomeBlaze.Mcp` (rich) | Interceptor library stays usable independently; HomeBlaze adds domain-specific tools |
| 10 | History | Plugin-based: abstractions (sink interface) + implementation (collector) | Any instance can optionally record history. Central UNS accumulates full history naturally via topology |
| 11 | Documents | Subjects with Read/Write operations, linked via dynamic attributes | No special infrastructure — standard subject model |
| 12 | Plugin contract | Subject types only (NuGet packages) | Everything is a subject. No separate plugin interfaces |
| 13 | Persistence | None for live state — recover from source of truth | External world is the persistence layer. Only configuration and history are locally persisted |
| 14 | Observability | OpenTelemetry + health subjects | Ops teams get standard tooling, AI agents can monitor the system itself |
| 15 | Versioning | Plugins depend on stable abstractions packages, not host versions | Independent upgradeability |

---

## 8. Glossary

| Term | Definition |
|------|-----------|
| Subject | An intercepted .NET object with tracked properties, metadata, and operations. The fundamental unit of the knowledge graph. Defined with `[InterceptorSubject]` |
| Unified Namespace (UNS) | The live state layer: all subject properties and operations across all instances, synced in real time via WebSocket |
| Knowledge Graph | Extends the UNS with property history, documents, and metadata. The full data model that operators and AI agents work with |
| Digital Twin | A HomeBlaze instance acts as a digital twin of its domain: a live, synchronized model of the physical or virtual system it represents |
| Operation | A method on a subject marked with `[Operation]` (state-changing) or `[Query]` (read-only), discoverable and invocable via MCP tools and protocol methods |
| Plugin | A NuGet package providing subject types. Connectors, agents, device subjects, UI components, and business logic are all delivered as plugins |
| Satellite | A HomeBlaze instance that owns device connections for a specific domain. Syncs its state upward to a central instance |
| Central Instance | A HomeBlaze instance that aggregates state from multiple satellites into a unified namespace |
| Primary | The active node in an HA pair. Owns device connections (satellites) or receives satellite sync (central) |
| Standby | The passive node in an HA pair. Maintains a full replica via WebSocket. Promotes to primary on failover |
| Fencing | Safety check before promotion: standby verifies it can reach devices. Prevents false promotion during network partitions |
| MCP | Model Context Protocol. The standard protocol through which external AI agents access the knowledge graph |
| Connector | A protocol integration bridging external systems into the knowledge graph. Core connectors live in Namotion.Interceptor; HomeBlaze wraps them as manageable subjects |
