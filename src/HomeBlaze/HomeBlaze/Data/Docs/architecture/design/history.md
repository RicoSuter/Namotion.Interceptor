---
title: Time-Series History
navTitle: History
---

# Time-Series History Design

## Overview

The time-series store records property changes over time, enabling historical queries, trend analysis, and AI-driven insights. It is implemented as a plugin using the standard abstractions + implementation pattern.

## Architecture

### Package Structure

| Package | Provides |
|---------|----------|
| `HomeBlaze.History.Abstractions` | `IHistorySink` interface — defines what a history storage backend must implement |
| `HomeBlaze.History` | History collector subject — subscribes to the property change queue and dispatches to registered sink subjects |

### How It Works

The **history collector** is a subject that subscribes to the property change pipeline. When a tracked property changes, the collector dispatches the change (property path, value, timestamp) to all registered `IHistorySink` implementations.

**Sink implementations** are subjects themselves. Examples:
- Built-in SQLite sink (default)
- InfluxDB sink (plugin)
- In-memory sink (testing)
- Any custom sink via plugin

### Which Properties Are Historized

Configurable at the plugin level. In HomeBlaze, `[State]` marks runtime properties (sensor readings, device status) as distinct from `[Configuration]` properties (persisted settings). The history plugin naturally targets `[State]` properties by default — these are the values that change over time and are worth tracking. An industrial plugin might apply a more selective filter (e.g., only numeric values, or only properties explicitly marked with a custom `[Historize]` attribute).

### Central UNS Has Complete History

Since the central UNS receives all property changes from all satellites via WebSocket, it naturally accumulates a full history of the entire graph. No explicit history sync needed — it is a side effect of the topology.

Each satellite can also install the history plugin independently for local history (useful for offline operation or edge analytics).

### AI Access

A dedicated MCP tool in `HomeBlaze.Mcp`:

| Tool | Parameters | Description |
|------|-----------|-------------|
| `get_history` | `path`, `from?`, `to?`, `interval?`, `aggregation?` | Query time-series data for a property |

Aggregation modes: `avg`, `min`, `max`, `sum`, `count`, `last`. The `interval` parameter enables downsampling for large time ranges.

## Key Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| History as plugin | Not built-in to core | Optional per instance. Not all nodes need history |
| Sink abstraction | `IHistorySink` interface in abstractions package | Multiple storage backends without coupling |
| Recording scope | Configurable at plugin level | Different domains have different needs |
| History locality | Local per instance, central accumulates naturally | No explicit history sync protocol needed |
| Retention | Configurable per instance | Edge nodes may keep days, central may keep years |

## Open Questions

- Retention policy configuration model (per-property? per-type? global?)
- Downsampling strategy for long-term storage
- History export/import for backup and migration
