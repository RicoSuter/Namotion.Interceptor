---
title: Scalability
navTitle: Scalability
status: Planned
---

# Scalability Design [Planned]

## Overview

HomeBlaze scales from single-instance (1K properties) to multi-instance (1M+ properties) using the same binary and architecture. This document covers architectural scaling limits and planned optimizations. For code-level bottlenecks see the [Scaling Considerations](../overview.md) table in the architecture overview; for validation approach see [System Testing](testing.md).

## Architectural Scaling Limit: Central UNS

The central UNS instance receives and holds the **full state of all connected satellites**. This is the primary architectural scaling ceiling — a single central node's memory, CPU, and network bandwidth determine the maximum aggregate graph size.

For most deployments (up to ~500K–1M properties across all satellites) this works well: the model is simple, every query hits local state, and operators get a single unified view. Beyond that, the central node becomes the bottleneck.

### When This Becomes a Problem

- Total properties across all satellites exceed what a single central node can hold in memory
- Aggregate change rate from all satellites exceeds what a single central node can process
- Welcome snapshots from many large satellites overwhelm the central node on restart

### Possible Directions (To Investigate After Load Testing)

These are options to explore if and when load testing reveals the central UNS ceiling. None are designed yet — they are listed here so the directions are captured.

| Direction | Description | Trade-off |
|-----------|-------------|-----------|
| **Selective sync** | Satellites sync only a filtered subset of their graph upward (e.g., only `[State]` properties, or properties matching a configurable filter). Central has structure + metadata but not every raw value | Reduces central memory and bandwidth. Queries for non-synced properties must be routed to the satellite — adds latency and requires satellite availability |
| **Hierarchical UNS** | Intermediate UNS nodes each aggregate a subset of satellites. A top-level UNS aggregates the intermediates | Distributes load across levels. Adds operational complexity (more nodes to manage) and query latency (extra hop) |
| **Federated query** | Central is a directory/router, not a replica. Queries are routed to the owning satellite on demand | Central stays lightweight. But queries fail when satellites are offline, and cross-satellite queries require fan-out |
| **Multiple independent UNS** | Separate domains that don't aggregate. Cross-domain queries handled by MCP or a routing layer | Simplest operationally — each UNS is independent. No single unified view across domains |

The current full-sync model is the right starting point. It is simple, correct, and sufficient for the target scale. These directions should only be pursued once load testing establishes where the actual ceiling is.

## Low-Level Optimizations

The following optimizations improve performance within the current architecture. They do not change the topology or sync model.

## Centralized Path Cache

### Problem

Path resolution — mapping between external paths (MQTT topics, OPC UA node paths, MCP query paths) and subject properties — is a frequent operation with no shared infrastructure. Currently:

| Connector | Caching Approach | Issue |
|-----------|-----------------|-------|
| MQTT client/server | Dual `ConcurrentDictionary` (path→property, property→path) per instance | Duplicated across client and server, O(n) cleanup on detach |
| OPC UA server | `ConcurrentDictionary` for NodeId resolution | Separate cache, different key space |
| OPC UA client | NodeId stored as property metadata | No path cache |
| WebSocket | No caching | Recalculates dynamically |
| Connectors base | Method-scoped temporary dictionary | Not persisted across calls |
| `PathProviderBase` | No caching | O(n) linear scan of properties per segment lookup |

Each connector that needs fast path lookups builds its own cache, with its own lifecycle management and its own cleanup logic.

### Proposed Solution

A **shared path cache service** registered on the `IInterceptorSubjectContext`, providing O(1) bidirectional lookups for all consumers.

```
┌─────────────────────────────────────────────────┐
│  IInterceptorSubjectContext                      │
│                                                  │
│  ┌────────────────────────────────────────────┐  │
│  │  PathCacheService                          │  │
│  │                                            │  │
│  │  path segment → RegisteredSubjectProperty  │  │
│  │  RegisteredSubjectProperty → path segment  │  │
│  │                                            │  │
│  │  Invalidated via lifecycle events          │  │
│  │  (subject attach/detach)                   │  │
│  └────────────────────────────────────────────┘  │
│                                                  │
│  Used by: MQTT, OPC UA, WebSocket, MCP, etc.    │
└─────────────────────────────────────────────────┘
```

**Key characteristics:**
- Registered as a context service, shared across all connectors on the same context
- Bidirectional: path→property and property→path
- Per-`IPathProvider`: different connectors may use different path providers (camelCase for JSON APIs, attribute-based for OPC UA), so the cache is keyed by (pathProvider, segment) or the cache is per path provider instance
- Invalidation via lifecycle events: when a subject is attached or detached, affected cache entries are updated automatically
- Lazy population: entries are cached on first access, not eagerly built for the entire graph
- Thread-safe: `ConcurrentDictionary` or similar

**What this replaces:**
- MQTT's `_propertyToTopic` / `_pathToProperty` / `_topicToProperty` dictionaries
- The O(n) scan in `PathProviderBase.TryGetPropertyFromSegment()`
- Method-scoped temporary caches in `GetPropertiesFromPaths()`

### Open Questions

- Should the cache be per path provider instance or support multiple providers in one cache?
- Should full path resolution (multi-segment, e.g., `/Machines/CNC01/Temperature`) also be cached, or only single-segment lookups?
- Should connectors be able to opt out (e.g., if they need custom key spaces like OPC UA NodeIds)?
- How to handle `[InlinePaths]` dictionary keys that are dynamic (keys added/removed at runtime)?

## Registry Indexing

### Problem

`GetAllProperties()` and type-based queries walk the entire graph recursively. At 500K+ properties, this becomes expensive for MCP queries, history collector property selection, and connector initialization.

### Planned Approach

Add optional indexes on the registry, maintained incrementally as subjects attach/detach:

| Index | Use Case |
|-------|----------|
| By type/interface | `query` with type filter, `list_types` counts |
| By path prefix | Scoped queries, connector property selection |
| By attribute | History collector selecting `[State]` properties, security checking `[SubjectAuthorize]` |

Indexes would be context services, updated via lifecycle events. Only indexes that are actually registered are maintained — no overhead for unused indexes.

### Open Questions

- Which indexes are needed for v1 vs. deferred?
- Should indexes be opt-in per context configuration, or always-on when registry is enabled?
- Memory overhead of maintaining indexes vs. cost of linear scans?

## Welcome Snapshot Optimization

### Problem

`SubjectUpdate.CreateCompleteUpdate()` serializes the entire reachable graph into a single in-memory structure. For large graphs (100K+ properties), this creates large memory allocations and long serialization times.

### Possible Approaches

| Approach | Pros | Cons |
|----------|------|------|
| Chunked Welcome | Stream subjects in batches, receiver assembles | Requires protocol change, partial state during assembly |
| Compressed Welcome | Gzip/Brotli the JSON payload | Reduces wire time, but memory allocation for the uncompressed structure remains |
| MessagePack format | Binary serialization instead of JSON | Smaller payload, faster serialization — but adds format negotiation complexity |
| Lazy property inclusion | Only include properties that have been written at least once | Reduces payload for sparse graphs, but requires tracking "ever-written" state |

### Open Questions

- Which approach to prioritize? Compression is lowest effort. Chunking requires protocol changes.
- Should Welcome snapshots be a protocol-level concern (WebSocket connector) or a general SubjectUpdate concern?
- Can the receiver apply partial snapshots incrementally, or must it wait for the full snapshot before serving queries?

## Change Pipeline Partitioning

### Problem

The `PropertyChangeQueue` distributes changes to all subscriber queues synchronously during property write interception. At very high change rates, this distribution becomes a bottleneck.

### Possible Approaches

- Partition the change queue by subject subtree (each satellite's subjects in a separate partition)
- Use lock-free ring buffers instead of `ConcurrentQueue` for subscriptions
- Allow connectors to subscribe to specific subtrees rather than the full graph

### Open Questions

- At what change rate does the current architecture bottleneck? (Load testing will determine this)
- Is partitioning needed, or is the current model sufficient with deduplication handling the load?

## Priority

1. **Centralized path cache** — addresses duplicated code across connectors and the O(n) scan bottleneck
2. **Welcome snapshot compression** — low-effort improvement for multi-instance deployments
3. **Registry indexing** — needed when MCP queries and history selection hit performance limits
4. **Welcome chunking / MessagePack** — needed at large scale, requires protocol changes
5. **Change pipeline partitioning** — only if load testing reveals a bottleneck
