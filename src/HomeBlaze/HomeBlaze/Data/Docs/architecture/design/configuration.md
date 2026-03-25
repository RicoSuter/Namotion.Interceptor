---
title: Configuration and Persistence
navTitle: Configuration
---

# Configuration and Persistence Design

## Overview

HomeBlaze persists **configuration** (subject settings, topology, plugin list) and **history** (time-series data) locally. Live state is not persisted — it recovers from the source of truth (devices, peers) on restart.

## Persistence Model

| What | Persisted? | Recovers from |
|------|-----------|--------------|
| Subject configuration | Yes (JSON files) | Local disk |
| Plugin configuration | Yes (JSON files) | Local disk |
| Dynamic metadata / annotations | Yes (JSON files) | Local disk |
| Property values (live state) | No | Field devices (satellites), peers via WebSocket Welcome snapshot (central/standby) |
| Time-series history | Yes (via history plugin) | Local database |

### Recovery on Restart

Each layer recovers from its source of truth:

| Instance | Recovers from | Mechanism |
|----------|--------------|-----------|
| Satellite | Field devices | Connectors reconnect, read current values |
| Central UNS | Satellites | Satellites reconnect, send Welcome snapshot with full state |
| Standby | Primary | Reconnects, receives Welcome snapshot |

## Subject Configuration

Subjects marked with `[Configuration]` properties have their configuration persisted to JSON files. On startup, configuration is loaded and subjects are instantiated with their persisted settings.

The `[State]` attribute marks runtime-only properties that are not persisted.

## Dynamic Metadata and Annotations

User-created metadata (annotations, tags, links between subjects) are stored as dynamic attributes on the registry. These are persisted in their own JSON config files, separate from subject configuration, so they survive restarts and can be reapplied to subjects as they are instantiated.

This enables operators and integrators to enrich the knowledge graph with domain-specific metadata without modifying subject code.

## Key Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| No live state persistence | Recover from source of truth | External world (devices, peers) is authoritative. Avoids stale state |
| Configuration format | JSON files | Human-readable, diffable, version-controllable |
| Dynamic metadata storage | Separate JSON config files | Decoupled from subject configuration, reapplied on restart |
| Configuration vs state | `[Configuration]` (persisted) vs `[State]` (runtime-only) | Clear developer intent, explicit persistence boundary |

## Open Questions

- Configuration migration strategy when subject types change across versions
- Dynamic metadata schema and validation
- Configuration sync between instances (should central know satellite configs?)
