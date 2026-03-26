---
title: Time-Series History
navTitle: History
---

# Time-Series History Design [Planned]

## Overview

The time-series store records property changes over time, enabling historical queries, trend analysis, and AI-driven insights. It is implemented as a plugin using the standard abstractions + implementation pattern.

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

### Central UNS Has Complete History

Since the central UNS receives all property changes from all satellites via WebSocket, it naturally accumulates a full history of the entire graph. No explicit history sync needed — it is a side effect of the topology.

Each satellite can also install the history plugin independently for local history (useful for offline operation or edge analytics).

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

### AI Access

A dedicated MCP tool in `HomeBlaze.Mcp`:

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
| History locality | Local per instance, central accumulates naturally | No explicit history sync protocol needed |
| Retention | Configurable per instance | Edge nodes may keep days, central may keep years |

## Open Questions

- Retention policy configuration model (per-property? per-type? global?)
- Downsampling strategy for long-term storage
- History export/import for backup and migration
- `IHistoryStore` query interface design (sync vs async, streaming for large ranges, aggregation push-down to sink)

## Implementation Notes

### Backpressure and Overload

Since each sink has its own `ChangeQueueProcessor`, backpressure is handled per-sink with the same bounded queue semantics as connectors. When a sink's queue overflows (sink is slower than the change rate), the oldest unprocessed entries are dropped.

This is especially relevant for the central UNS, which sees all changes from all satellites and therefore has the highest aggregate change rate.

| Alternative | Why not |
|-------------|---------|
| Unbounded in-memory buffering | OOM risk under sustained load, especially on central UNS |
| Backpressure the change pipeline | Would block the hot path — property writes, connector sync, and UI updates would stall because a history sink is slow |
| Central orchestrator dispatching to sinks | Adds a single point of failure and coupling between sinks. If the orchestrator is slow, all sinks suffer |

Queue depth and drop count should be exposed as metrics so operators can detect and address overload (e.g., by increasing sink throughput, reducing property filter scope, or adding retention limits).
