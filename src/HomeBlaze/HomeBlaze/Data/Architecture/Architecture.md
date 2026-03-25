---
title: HomeBlaze Architecture
navTitle: Architecture
position: 0
---

# HomeBlaze Architecture

This document describes the architecture of HomeBlaze using the arc42 template. It serves both as an introduction for evaluators and as a reference for contributors.

## 1. Introduction and Goals

HomeBlaze is a modular .NET platform for building real-time digital twins: trackable object graphs that mirror physical or virtual systems. Built on Namotion.Interceptor, it provides automatic property interception, change tracking, persistence, and a Blazor-based operator UI out of the box.

Any domain - industrial plants, smart buildings, IoT networks - can be modeled as **subjects** (intercepted objects with tracked properties). HomeBlaze hosts these subjects, connects them to external systems via protocol connectors (OPC UA, MQTT, WebSocket), and exposes them through a **Unified Namespace (UNS)** - a single, aggregated view of all operational data. On top of the UNS, HomeBlaze builds a **knowledge graph** that extends live state with property history, documents, metadata, and AI-queryable structure. Both human operators and AI agents can browse, query, and act on this knowledge graph.

### Key Requirements

| Priority | Requirement |
|----------|------------|
| 1 | **Unified Namespace** - All subjects across all domains are accessible through a single, browsable graph with typed properties, metadata, and operations |
| 2 | **Horizontal Scaling** - Multiple HomeBlaze instances each own a domain slice (e.g. a plant area, a building) and sync upward to a central instance via WebSocket |
| 3 | **High Availability** - Every node replicates its full state to a standby via WebSocket. On failover, the standby promotes by activating device connections (OPC UA, MQTT), becoming the new primary. State converges automatically because external devices are the source of truth |
| 4 | **AI-Ready Knowledge Graph** - The knowledge graph is structured for LLM/agent consumption: browsable subjects, queryable properties, executable operations, property history, attached documents, and reactive change streams |
| 5 | **Same Binary Everywhere** - Satellites, central instances, and dashboards all run the same HomeBlaze binary. Role is determined by which plugins are loaded and which connectors are configured. The Blazor UI can be enabled or disabled per deployment |

### Stakeholders

| Role | Concern |
|------|---------|
| Plant/building operator | Browse live state, execute operations, view dashboards |
| System integrator | Configure instances, map protocol sources to subjects, define domain models |
| AI agent | Query the knowledge graph, react to changes, execute operations |
| Platform developer | Extend with new subject types, connectors, and UI components |

---

## 2. Constraints

### Technical Constraints

| Constraint | Description |
|-----------|------------|
| .NET 9+ with C# 13 preview | Required for partial properties and source generation. All nodes run the same runtime |
| Namotion.Interceptor foundation | All subject tracking, change detection, and connector protocols are built on the interceptor library. This is not pluggable |
| WebSocket for inter-node sync | Instances communicate via the WebSocket connector (SubjectUpdate protocol). No custom sync protocol |
| Same binary, config-driven roles | Node role (satellite, central, standby) is determined by loaded plugins and configuration, not by separate binaries |

### Organizational Constraints

| Constraint | Description |
|-----------|------------|
| Domain-agnostic | The architecture must not assume a specific domain (industrial, home, building). Domain specifics live in plugins |
| Early maturity | The platform is in active development. Smaller breaking changes are acceptable |
| Open source | The core platform and connectors are open source. Domain-specific plugins may be proprietary |

---

## 3. Context and Scope

### System Context

HomeBlaze sits between external device/data sources and its consumers (human operators, AI agents, third-party systems). Each HomeBlaze instance connects to its domain-specific sources and participates in a multi-instance topology.

```
                        ┌─────────────────────────────┐
                        │        Consumers            │
                        │  Operators (Blazor UI)      │
                        │  AI Agents (built-in)       │
                        │  Third-party (MQTT, OPC UA) │
                        └─────────────┬───────────────┘
                                      │
                        ┌─────────────▼───────────────┐
                        │   HomeBlaze Central          │
                        │                              │
                        │   Unified Namespace          │
                        │   Knowledge Graph            │
                        │   WebSocket Server           │
                        └──┬───────────────────────┬───┘
                           │ WebSocket             │ WebSocket
                ┌──────────▼──────────┐  ┌─────────▼──────────┐
                │ HomeBlaze Satellite  │  │ HomeBlaze Satellite │
                │ (Domain A)          │  │ (Domain B)          │
                │                     │  │                     │
                │ OPC UA / MQTT /     │  │ OPC UA / MQTT /     │
                │ other connectors    │  │ other connectors    │
                └──────────┬──────────┘  └─────────┬──────────┘
                           │                       │
                  ┌────────▼────────┐    ┌─────────▼────────┐
                  │  Field Devices   │    │  Field Devices    │
                  │  PLCs, Sensors,  │    │  Smart Home,      │
                  │  Actuators       │    │  IoT Devices      │
                  └─────────────────┘    └──────────────────┘
```

### External Interfaces

| Interface | Direction | Protocol | Purpose |
|-----------|-----------|----------|---------|
| Field devices | Bidirectional | OPC UA, MQTT, custom | Read sensor data, write setpoints/commands |
| Inter-node sync | Bidirectional | WebSocket (SubjectUpdate) | State replication between HomeBlaze instances |
| Standby replication | Unidirectional (primary to standby) | WebSocket (SubjectUpdate) | Full state sync for high availability |
| Operator UI | Outbound | Blazor (SignalR) | Browse knowledge graph, execute operations, view dashboards |
| AI / LLM agents | Bidirectional | Direct graph access, MCP | Query subjects, react to changes, execute operations |
| Third-party consumers | Bidirectional | MQTT, OPC UA server | Expose knowledge graph to SCADA, historians, external dashboards |

### Scope Boundary

HomeBlaze owns the knowledge graph and its synchronization, including:

- Live state (subject properties, the Unified Namespace)
- History (built-in time-series database)
- Metadata and annotations (property attributes)
- Documents (storage system)
- AI agents (built-in LLM subjects operating on the knowledge graph)

It does not currently own:

- Device firmware or PLC programs
- Alarm/event management (planned, not yet architected)

---

## 4. Building Block View

### Level 1 - Top-Level Building Blocks

```
┌──────────────────────────────────────────────────────────┐
│                    HomeBlaze Instance                      │
│                                                           │
│  ┌─────────────┐  ┌──────────────┐  ┌─────────────────┐  │
│  │  Knowledge   │  │  Connector   │  │  Multi-Instance │  │
│  │  Graph       │  │  Layer       │  │  & HA           │  │
│  │              │  │              │  │                 │  │
│  │  Subjects    │  │  OPC UA      │  │  WebSocket Sync │  │
│  │  History     │  │  MQTT        │  │  Standby Repl.  │  │
│  │  Documents   │  │  WebSocket   │  │  Failover Mgmt  │  │
│  │  Metadata    │  │  Custom      │  │                 │  │
│  └──────┬───────┘  └──────┬───────┘  └────────┬────────┘  │
│         │                 │                    │           │
│  ┌──────▼─────────────────▼────────────────────▼────────┐ │
│  │              Namotion.Interceptor Core                 │ │
│  │  Property Interception, Change Tracking, Registry     │ │
│  └───────────────────────────────────────────────────────┘ │
│                                                           │
│  ┌──────────────┐  ┌──────────────┐  ┌────────────────┐  │
│  │  Operator UI  │  │  AI Agents   │  │  Plugins       │  │
│  │  (Blazor)    │  │  (LLM)       │  │                │  │
│  │              │  │              │  │                 │  │
│  │  Browser     │  │  Graph Query │  │  Subject Types  │  │
│  │  Dashboards  │  │  Change Sub. │  │  Business Logic │  │
│  │  Operations  │  │  Doc Access  │  │  Custom UI      │  │
│  └──────────────┘  └──────────────┘  └────────────────┘  │
└──────────────────────────────────────────────────────────┘
```

Every HomeBlaze instance contains the same building blocks. What makes a satellite different from a central instance is which plugins are loaded and which connectors are active.

### Level 2 - Knowledge Graph

The knowledge graph is the central data structure that all other building blocks interact with. It unifies live state, history, metadata, and documents into a single queryable model.

The **Unified Namespace (UNS)** is the live state layer: all subject properties, synced across instances in real time. The **knowledge graph** extends the UNS with property history, documents, and metadata, forming the full picture that AI agents and operators work with.

```
┌──────────────────────────────────────────────────┐
│                 Knowledge Graph                    │
│                                                   │
│  ┌─────────────────────────────────────────────┐  │
│  │  Unified Namespace (live state)              │  │
│  │                                              │  │
│  │  Typed properties with change tracking       │  │
│  │  Derived properties (computed)               │  │
│  │  Property attributes (metadata, units, etc.) │  │
│  │  Operations (executable actions)             │  │
│  │  Registry (browsable, queryable)             │  │
│  └──────────────────────────────────────────────┘  │
│                                                   │
│  ┌─────────────────────┐  ┌────────────────────┐  │
│  │  Time-Series Store   │  │  Document Store    │  │
│  │                      │  │                    │  │
│  │  Property history    │  │  Markdown pages    │  │
│  │  Built-in DB plugin  │  │  PDFs, manuals     │  │
│  │  Queryable by path   │  │  JSON config files │  │
│  │  and time range      │  │  Browsable via     │  │
│  │                      │  │  storage subjects  │  │
│  └─────────────────────┘  └────────────────────┘  │
└──────────────────────────────────────────────────┘
```

**How each consumer accesses the knowledge graph:**

| Consumer | Access Pattern |
|----------|---------------|
| Operator UI | TrackingScope for live updates, subject browser for navigation, operations for actions |
| AI Agents | Registry queries for live state, time-series queries for history, storage access for documents |
| Connectors | Read/write interceptors feed property changes in and out |
| WebSocket sync | SubjectUpdate messages replicate the subject graph between instances |

### Level 2 - Connector Layer

Connectors bridge external systems into the knowledge graph. Each connector is a plugin that maps external data to subject properties.

| Connector | Direction | Purpose |
|-----------|-----------|---------|
| OPC UA Client | Bidirectional | Connect to PLCs, industrial devices |
| OPC UA Server | Bidirectional | Expose knowledge graph to industrial clients, accept writes and method calls |
| MQTT Client/Server | Bidirectional | IoT messaging, third-party integration |
| WebSocket | Bidirectional | Inter-node sync, consumer data delivery |
| Custom | Either | Domain-specific protocols via plugin |

All connectors use the same interceptor pattern: inbound data is applied with source tagging to prevent feedback loops, outbound changes flow through the change queue.

### Level 2 - Multi-Instance and High Availability

Horizontal scaling and resilience both use the same WebSocket connector.

```
┌─────────────────┐         ┌─────────────────┐
│  Primary         │         │  Standby         │
│                  │  WS     │                  │
│  Device Conns.   │◄───────│  WebSocket Client │
│  WebSocket Srv.  │         │  WebSocket Srv.  │
│  Knowledge Graph │         │  Knowledge Graph │
│  (authoritative) │         │  (replica)       │
└─────────────────┘         └─────────────────┘

On failover:
  1. Standby detects primary is unreachable
  2. Standby verifies it can reach devices (fencing)
  3. Standby activates device connections
  4. Standby becomes new primary
  5. State converges via device reads
```

**Multi-instance topology:**

| Relationship | Mechanism | Data Flow |
|-------------|-----------|-----------|
| Satellite to Central | WebSocket client on satellite, server on central | Reads flow up, writes flow down |
| Primary to Standby | WebSocket server on primary, client on standby | Full state replication |
| Central to Consumers | WebSocket server on central | Reads flow to consumers, writes flow back |

---

## 5. Runtime View

### Scenario 1 - Startup and Multi-Instance Sync

```
Satellite (Domain A)              Central                       Operator UI
       │                              │                              │
       │  1. Start, load plugins      │                              │
       │  2. Connect to devices       │                              │
       │     (OPC UA, MQTT, ...)      │                              │
       │  3. Build local knowledge    │                              │
       │     graph from device state  │                              │
       │                              │                              │
       │── 4. WebSocket Connect ─────>│                              │
       │<─ 5. Hello / Welcome ────────│                              │
       │                              │                              │
       │── 6. SubjectUpdate (full) ──>│  7. Merge into               │
       │                              │     unified namespace        │
       │                              │                              │
       │── 8. SubjectUpdate (incr.) ─>│  9. Update graph             │
       │                              │── 10. Broadcast ────────────>│
       │                              │      to all consumers        │
       │                              │                              │
```

### Scenario 2 - Write Path (Consumer to Device)

A consumer (operator or AI agent) writes a setpoint. The write flows down through the topology to the device, and the confirmed value flows back up.

```
Operator UI                  Central                  Satellite              PLC
     │                            │                        │                   │
     │  1. Set Temperature = 80   │                        │                   │
     │───────────────────────────>│                        │                   │
     │                            │  2. Apply to local     │                   │
     │                            │     graph, broadcast   │                   │
     │                            │── 3. SubjectUpdate ──>│                   │
     │                            │                        │  4. Change        │
     │                            │                        │     triggers      │
     │                            │                        │     OPC UA write  │
     │                            │                        │──────────────────>│
     │                            │                        │                   │
     │                            │                        │<── 5. PLC confirms│
     │                            │                        │    (subscription) │
     │                            │                        │                   │
     │                            │<── 6. Confirmed value──│                   │
     │<── 7. Broadcast ───────────│                        │                   │
     │   (confirmed value)        │                        │                   │
```

### Scenario 3 - Standby Failover

```
Primary                  Standby                     Central
    │                        │                            │
    │── WS sync (ongoing) ──>│                            │
    │                        │                            │
    X  Primary crashes       │                            │
                             │                            │
                 1. Detect primary    │                    │
                    unreachable       │                    │
                 2. Verify device     │                    │
                    reachability      │                    │
                    (fencing check)   │                    │
                 3. Activate device   │                    │
                    connections       │                    │
                 4. Read current      │                    │
                    device state      │                    │
                    (close sync gap)  │                    │
                 5. Relabel as        │                    │
                    primary           │                    │
                             │                            │
                             │── 6. Resume sync ─────────>│
                             │      (as new primary)      │
                             │                            │
```

### Scenario 4 - AI Agent Interaction

```
AI Agent (LLM)            Knowledge Graph              Time-Series         Documents
     │                          │                          │                    │
     │  1. browse_subjects()    │                          │                    │
     │─────────────────────────>│                          │                    │
     │<── subject tree ─────────│                          │                    │
     │                          │                          │                    │
     │  2. read_property(       │                          │                    │
     │     "domain.press.temp") │                          │                    │
     │─────────────────────────>│                          │                    │
     │<── 85.2 (unit=C,        │                          │                    │
     │    max=90)               │                          │                    │
     │                          │                          │                    │
     │  3. get_history(         │                          │                    │
     │     "...temp", 24h)      │                          │                    │
     │──────────────────────────┼─────────────────────────>│                    │
     │<── time-series data ─────┼──────────────────────────│                    │
     │                          │                          │                    │
     │  4. search_documents(    │                          │                    │
     │     "press maintenance") │                          │                    │
     │──────────────────────────┼──────────────────────────┼───────────────────>│
     │<── matching docs ────────┼──────────────────────────┼────────────────────│
     │                          │                          │                    │
     │  5. set_property(        │                          │                    │
     │     "...speed", 120)     │                          │                    │
     │─────────────────────────>│  (flows down             │                    │
     │                          │   via write path)        │                    │
```

---

## 6. Deployment View

### Single Instance

For small setups (single home, small facility), one HomeBlaze instance does everything:

```
┌─────────────────────────────┐
│  HomeBlaze                   │
│                              │
│  Knowledge Graph             │
│  Device Connectors           │
│  Blazor UI                   │
│  AI Agents                   │
│  Time-Series DB              │
│                              │
│  Ports:                      │
│    5000 - Blazor UI          │
│    8081 - WebSocket (opt.)   │
└──────────────┬───────────────┘
               │
        ┌──────▼──────┐
        │   Devices    │
        └─────────────┘
```

No satellites, no standby. Just a single process.

### Single Instance with HA

Add a standby for resilience. Same binary, standby syncs via WebSocket.

```
┌──────────────────┐         ┌──────────────────┐
│  Primary          │   WS    │  Standby          │
│                   │◄───────│                    │
│  Device Conns.    │         │  WS Client        │
│  WS Server        │         │  WS Server (ready)│
│  Blazor UI        │         │  Blazor UI (opt.) │
└────────┬──────────┘         └──────────────────┘
         │
  ┌──────▼──────┐
  │   Devices    │
  └─────────────┘
```

### Multi-Instance with HA

Full deployment: multiple satellites, central instance, each with optional standby.

```
                              ┌──────────────────┐
                              │  Central          │
                              │  Primary          │
                         ┌───>│  WS Server        │◄───┐
                         │    │  Blazor UI        │    │
                         │    └────────┬──────────┘    │
                         │             │ WS            │
                    WS   │    ┌────────▼──────────┐    │ WS
                         │    │  Central          │    │
                         │    │  Standby          │    │
                         │    └──────────────────┘    │
                         │                             │
          ┌──────────────┴───┐          ┌──────────────┴───┐
          │  Satellite A      │          │  Satellite B      │
          │  Primary          │          │  Primary          │
          │  WS Server        │          │  WS Server        │
          └────────┬──────────┘          └────────┬──────────┘
                   │ WS                           │ WS
          ┌────────▼──────────┐          ┌────────▼──────────┐
          │  Satellite A      │          │  Satellite B      │
          │  Standby          │          │  Standby          │
          └──────────────────┘          └──────────────────┘
                   │                              │
            ┌──────▼──────┐                ┌──────▼──────┐
            │  Devices A   │                │  Devices B   │
            └─────────────┘                └─────────────┘
```

### Kubernetes Deployment

For containerized environments, each HA pair maps to a StatefulSet with 2 replicas. Pod ordinal determines role (0 = primary, 1 = standby).

| Component | Workload | Replicas | Role Selection |
|-----------|----------|----------|----------------|
| Satellite per domain | StatefulSet | 2 | Pod ordinal |
| Central | StatefulSet | 2 | Pod ordinal |
| Standalone (no HA) | Deployment | 1 | Always primary |

Services route traffic to the primary pod via role labels. Headless services provide stable DNS for standby-to-primary connections.

### Resource Estimates

| Scale | Nodes | Properties | Memory per node | CPU per node |
|-------|-------|-----------|-----------------|-------------|
| Small (single home) | 1 | ~1,000 | 128 MB | 0.5 cores |
| Medium (building / small plant) | 3-5 | ~50,000 | 512 MB | 1 core |
| Large (multi-area plant) | 10+ | ~500,000 | 1-2 GB | 2 cores |

---

## 7. Architecture Decisions

| # | Decision | Choice | Alternatives Considered | Rationale |
|---|----------|--------|------------------------|-----------|
| 1 | Node runtime | Same HomeBlaze binary everywhere | Separate binaries per role | Simplifies deployment, testing, and upgrades. Role is a configuration concern, not a build concern |
| 2 | Inter-node protocol | WebSocket (SubjectUpdate) | MQTT, gRPC, custom | Atomic snapshots, structural graph sync, sequence-based consistency. Already built into Namotion.Interceptor |
| 3 | Device connection ownership | Satellites, not central | Central connects to all devices | Failure isolation, independent deployment per domain, protocol complexity stays at the edge |
| 4 | HA node count | 2 per pair (active-standby) | 3+ with consensus (Raft/Paxos) | External devices act as source of truth and implicit tiebreaker. No need for distributed consensus |
| 5 | HA pattern | Active-standby, not active-active | Active-active with conflict resolution | Avoids duplicate device connections. Many protocols (OPC UA) have limited subscription capacity per client |
| 6 | Failover mechanism | Standby promotes by activating device connections | External orchestrator reassigns roles | Self-contained, no external dependency. Fencing check (verify device reachability) prevents false promotion during network partitions |
| 7 | Knowledge graph structure | Namotion.Interceptor subject graph with registry | Separate graph database (Neo4j, etc.) | Subject graph already provides typed properties, metadata, change tracking, and queryability. No impedance mismatch |
| 8 | History storage | Built-in time-series DB plugin | External historian (InfluxDB, TimescaleDB) | Reduces operational dependencies. Can still integrate with external historians as an additional connector |
| 9 | AI integration | Built-in LLM agent subjects with direct graph access | External AI service connecting via API | Direct access to knowledge graph, history, and documents. No serialization overhead for queries. Agents are subjects themselves, visible in the graph |
| 10 | Domain model distribution | Shared .NET library of subject types, distributed as NuGet packages | Schema registry, dynamic types | Compile-time safety via source generation. All nodes referencing the same domain must share the model types |
| 11 | UI availability | Same binary, UI exposed or hidden per deployment config | Separate dashboard application | Any node can serve as an operator station. Useful for on-site debugging of satellites |
| 12 | Third-party integration | Protocol connectors (MQTT server, OPC UA server) on central instance | REST API, custom gateway | Standard industrial protocols. SCADA systems and historians can consume without custom integration |
| 13 | Alarm/event management | Deferred (not yet architected) | Build into v1 | Core multi-instance sync and knowledge graph must stabilize first. Alarms will be designed as a separate building block |

---

## Glossary

| Term | Definition |
|------|-----------|
| Unified Namespace (UNS) | The live state layer of the system: all subject properties across all instances, synced in real time via WebSocket. An established industrial IoT concept for aggregating all operational data into one accessible structure |
| Knowledge Graph | Extends the UNS with property history, documents, metadata, and AI-queryable structure. The full data model that operators and AI agents work with |
| Subject | An intercepted .NET object with tracked properties, metadata, and operations. The fundamental unit of the knowledge graph. Defined with `[InterceptorSubject]` and compiled via source generation |
| Digital Twin | Each HomeBlaze instance is a digital twin of its domain: a live, synchronized model of the physical or virtual system it represents |
| Plugin | A .NET library containing subject types, business logic, and optionally UI components for a specific domain. Distributed as NuGet packages |
| Satellite | A HomeBlaze instance that owns device connections for a specific domain (e.g. a plant area, a building, a home). Syncs its state upward to a central instance |
| Central Instance | A HomeBlaze instance that collects state from multiple satellites into a unified namespace. Typically has no direct device connections |
| Primary | The active node in an HA pair. Owns device connections (for satellites) or receives satellite sync (for central instances) |
| Standby | The passive node in an HA pair. Maintains a full replica via WebSocket sync. Promotes to primary on failover |
| Promotion | The process where a standby activates device connections and becomes the new primary after detecting a failure |
| Fencing | A safety check before promotion: the standby verifies it can reach devices. If both primary and devices are unreachable, it assumes a network partition and does not promote |
| Connector | A plugin that bridges an external protocol (OPC UA, MQTT, WebSocket) into the knowledge graph by mapping external data to subject properties |
