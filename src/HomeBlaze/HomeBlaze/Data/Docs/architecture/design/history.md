---
title: Time-Series History
navTitle: History
status: Planned
---

# Time-Series History Design [Planned]

## Overview

The time-series store records property changes over time, enabling historical queries, trend analysis, and AI-driven insights. It is implemented as a plugin using the standard abstractions + implementation pattern. History is originated data on the reflection plane: once recorded it is its own source of truth and cannot be recovered from the device, so its durability model is store-and-forward (see below and the [Architecture Overview](../overview.md)).

## Architecture

### Package Structure

| Package | Provides |
|---------|----------|
| `HomeBlaze.History.Abstractions` | `IHistoryStore` interface — defines how to query historical data from a sink |
| Sink packages (e.g., `HomeBlaze.History.Sqlite`) | Concrete sink subjects — each is a self-contained `BackgroundService` that records and serves history |

### How It Works

Each **history sink is a standalone subject** — a `BackgroundService` with its own `ChangeQueueProcessor` that subscribes to the property change pipeline independently. No central orchestrator is needed. Each sink:

1. Subscribes to the change pipeline via its own `ChangeQueueProcessor`
2. Filters which properties to record (configurable per sink)
3. Writes changes to its storage backend
4. Exposes query capabilities via an abstraction interface (`IHistoryStore`)

This follows the same pattern as connectors — each sink is fully independent, with its own buffering and deduplication. Multiple sinks can run simultaneously without interfering with each other.

**Sink implementations** are subjects themselves — no single sink is the default. Each deployment chooses the sink(s) appropriate for its scale and infrastructure:

| Sink | Use Case |
|------|----------|
| File-based (CSV/JSON) | Simplest option. No external dependencies. Suitable for small setups or prototyping |
| SQLite | Single-node, moderate volume. No external database required |
| InfluxDB / TimescaleDB | High-volume, long retention. Purpose-built for time-series workloads |
| In-memory | Testing and development |
| Custom | Any storage backend via plugin (`IHistorySink` interface) |

Multiple sinks can run simultaneously (e.g., local SQLite for edge analytics + central InfluxDB for long-term storage).

### Which Properties Are Historized

Configurable at the plugin level. In HomeBlaze, `[State]` marks runtime properties (sensor readings, device status) as distinct from `[Configuration]` properties (persisted settings). The history plugin naturally targets `[State]` properties by default — these are the values that change over time and are worth tracking. An industrial plugin might apply a more selective filter (e.g., only numeric values, or only properties explicitly marked with a custom `[Historize]` attribute).

### Central History Is Best-Effort, Not Complete

The central instance records history for changes it receives over WebSocket, so during connected periods it accumulates history for the whole topology. It is not automatically complete: a satellite that is disconnected is invisible to central (no changes flow), and sink backpressure can drop changes under load. Central history therefore has gaps for every offline window and every overload burst.

Completeness on the reflection plane requires **store-and-forward at the edge**: each satellite buffers its own history durably while central is unreachable, and backfills the gap to central on reconnect. This makes the satellite, not the transient WebSocket stream, the source of truth for its own history. Each satellite installs the history plugin for this local buffer (also useful for offline operation and edge analytics).

### Querying History

Consumers discover all `IHistoryStore` subjects via the registry, query each, and merge results. No explicit configuration of "which store to use" is needed — adding a sink subject to the graph automatically makes its data available for queries.

`IHistoryStore` exposes a `Priority` property. When time ranges overlap across stores, the higher-priority store's data wins. This enables a tiered model where each tier has different resolution and retention:

| Sink | Priority | Retention | Resolution | Use Case |
|------|----------|-----------|------------|----------|
| In-memory | Highest | Minutes | Full (every change) | Real-time queries, recent context for AI agents |
| SQLite | Medium | Days | Full or sampled | Edge analytics, local history without external DB |
| InfluxDB / TimescaleDB | Lower | Years | Downsampled for old data | Long-term trends, compliance |

For a query spanning the last 24 hours, the in-memory store provides the most recent minutes at full resolution, and InfluxDB fills in the rest. Overlapping ranges are resolved by priority — the higher-priority store's values take precedence.

Initial implementation will likely start with in-memory + InfluxDB only.

### AI Access [Planned]

History tools are deferred beyond stage 1. The core MCP tools (see [MCP Server plan](../../../../docs/plans/mcp-server.md)) provide current-state access; history tools will be added once the history subsystem is stable.

Planned tools in `HomeBlaze.AI`:

| Tool | Parameters | Description |
|------|-----------|-------------|
| `get_property_history` | `path`, `from?`, `to?`, `interval?`, `aggregation?` | Query time-series data for a property. Queries all `IHistoryStore` subjects from the registry and merges results by priority |

Aggregation modes: `avg`, `min`, `max`, `sum`, `count`, `last`. The `interval` parameter enables downsampling for large time ranges.

For event and command history, see `get_event_history` and `get_command_history` in [Messages](messages.md).

## Key Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| History as plugin | Not built-in to core | Optional per instance. Not all nodes need history |
| Sink architecture | Each sink is a standalone `BackgroundService` with its own `ChangeQueueProcessor` | No central orchestrator. Same pattern as connectors. Sinks are fully independent |
| Query abstraction | `IHistoryStore` interface in abstractions package | Multiple storage backends without coupling. MCP `get_property_history` tool queries available stores |
| Recording scope | Configurable at plugin level | Different domains have different needs |
| History locality | Local per instance; satellites store-and-forward to central | Satellite is the source of truth for its own history and backfills central after a disconnect. Central alone is best-effort |
| Retention | Configurable per instance | Edge nodes may keep days, central may keep years |

## Open Questions

- Retention policy configuration model (per-property? per-type? global?)
- Downsampling strategy for long-term storage
- History export/import for backup and migration
- `IHistoryStore` query interface design (sync vs async, streaming for large ranges, aggregation push-down to sink)
- Edge store-and-forward buffer: durable local format, size bound, and backfill protocol to central on reconnect
- Gap-marker representation in query results (how a consumer sees "data was dropped here")

## Implementation Notes

### Backpressure, Overload, and Loss

Each sink has its own `ChangeQueueProcessor`, so backpressure is handled per-sink with bounded-queue semantics, the same as connectors. The change pipeline (the reflection-plane hot path) must never block for a slow sink: property writes, connector sync, and UI updates cannot stall because history is behind. The central UNS sees all changes from all satellites and therefore has the highest aggregate change rate, so it is the most exposed to overload.

Because the pipeline cannot block, a sink that falls behind must absorb or account for the overflow rather than silently dropping it. History is originated data: once lost it cannot be recovered from the device. The required behavior:

- **Spill to a durable local buffer** rather than dropping. A bounded in-memory queue drains into local durable storage (the edge store-and-forward buffer) when the sink or its backend is slow or unreachable.
- **Mark gaps when data is genuinely lost.** If the durable buffer itself is exhausted (sustained overload beyond local capacity), the sink records an explicit gap marker for the affected range, so a later query can distinguish "no change" from "data was dropped". A query result must never silently present a gap as continuity.
- **Expose queue depth, buffer size, and drop/gap counts as metrics** so operators can detect and address overload (for example by increasing sink throughput, reducing property filter scope, or adding retention limits).

| Alternative | Why not |
|-------------|---------|
| Unbounded in-memory buffering | OOM risk under sustained load, especially on central UNS |
| Backpressure the change pipeline | Would block the hot path: property writes, connector sync, and UI updates would stall because a history sink is slow |
| Silently drop oldest on overflow | Loses originated data that cannot be recovered, and hides the loss from queries. Replaced by spill-to-durable-buffer plus explicit gap markers |
| Central orchestrator dispatching to sinks | Adds a single point of failure and coupling between sinks. If the orchestrator is slow, all sinks suffer |
