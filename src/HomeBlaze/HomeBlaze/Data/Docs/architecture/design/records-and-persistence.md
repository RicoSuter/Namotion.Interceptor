---
title: Records and Persistence
navTitle: Records and Persistence
status: Design
---

# Records and Persistence Design

## Overview

HomeBlaze has two kinds of state, and they follow different rules. Naming the difference is the point of this document.

- **Reflected state** mirrors an external source of truth. The subject graph is a live projection that is recovered from the source on restart. A field device is one source of truth. A database is another.
- **Originated state** is created inside the platform and is its own source of truth. Production orders, batch records, genealogy, quality results, the audit trail, and the alarm journal are not recoverable from the field. They must be durably persisted with transactional guarantees.

Decision #16 ("no external database for reflected live state") applies only to reflected state. Originated state may, and often must, live in a database. This document defines how originated state fits the subject model without duplicating the live graph or replicating large record sets across instances. See the reflection-plane vs control-plane framing in the [Architecture Overview](../overview.md).

## A database is just another source of truth

A database-backed subject is a connector to a database, conceptually identical to the OPC UA or MQTT connector:

- The database holds the durable, transactional truth.
- The subject is a reactive projection over it. Its tracked properties are a view of database state, refreshed on read or via change notification, not the authoritative copy.
- Recovery works the same as for a device: on restart, re-read from the database.

This keeps one mental model (the graph mirrors a source of truth) and avoids a separate, bolted-on persistence pillar.

## When to model data as a subject

A subject is the right abstraction only when the thing needs to be **live, addressable, and actionable**. Otherwise it is plain data.

| Expose as | Use for | Why |
|-----------|---------|-----|
| Subject / property | The active working set: current tag values, the order running now, the batch in progress, an active alarm, a device | Live change tracking, path addressability in the UNS, reactive subscription, and its own operations (Pause, Abort, Acknowledge) |
| DTO from a `[Query]` method | Historical or bulk records: closed batches, completed orders, quality history, the audit log, the cleared-alarm journal, time-series history | Immutable, queried on demand, displayed, never live. No benefit from being a graph node, and materializing millions of them would not scale |

Rule of thumb: if the UI or an agent needs to subscribe to it changing, it is a subject. If it is read, filtered, and displayed, it is a DTO.

The historian already follows this rule: `get_property_history` returns data, not subjects. Records, audit, and the alarm journal generalize the same pattern.

## Records as DTOs from query methods

Historical and bulk records are returned as plain, serializable POCOs from `[Query]` operations on a database-backed subject. They never enter the subject graph. Consequences:

- They are point-in-time snapshots, not reactive. Anything that must update live belongs in a subject or property instead.
- They are not addressable by a UNS path. Cross-instance references and "watch this record" only work for records promoted to subjects, which should be the small active set.
- AI agents reach them through `invoke_method` / `list_methods`, not through `query` / graph traversal.

## Cross-instance access: replicate vs proxy

Every subject is one of two kinds for synchronization, and this must be explicit:

| Kind | Example | Cross-instance behavior |
|------|---------|-------------------------|
| Materialized and replicated | Live device state, active working set | Full subject state syncs over WebSocket (Welcome snapshot plus incremental updates) |
| Virtual and proxied | Database-backed record subjects | State is not replicated. Query operations are proxied (RPC) to the owning node, which runs them against its database and returns DTOs over the wire |

This makes the cross-instance RPC contract load-bearing for records, not just for commands (see [Methods](methods.md)). It also means record data never inflates the Welcome snapshot or the central node's memory.

## Transaction boundaries

- The ACID boundary is the subject, treated as an aggregate. A transaction lives inside one subject's operation, against that subject's database.
- The subject graph itself has no multi-subject transaction, and none should be added. Cross-aggregate consistency uses events or sagas, not two-phase commit across subjects.
- Practical guidance: model each aggregate (order, batch, lot) as one database-backed subject that owns its transaction boundary.

## Query surface

Record queries are exposed through the specific `[Query]` methods the subject author writes (for example `GetBatches(from, to, status)`), not through generic graph traversal or arbitrary query strings. This is a deliberate, bounded, injection-safe API surface. The tradeoff is that record discoverability is author-driven rather than schema-driven, which is normal for this kind of system.

## Relationship to the decisions table

- Decision #16 is scoped to reflected live state. A database for originated records is explicitly in scope.
- Decision #17 (records and system of record) records this model. See the [Architecture Overview](../overview.md).

## Open questions

- How a database-backed subject declares itself virtual/proxied vs materialized (attribute, base class, or configuration).
- Refresh and staleness policy for the projected properties of a database-backed subject (read-through, change-notification, or polling).
- Whether a small set of generic record-query shapes (paging, time-range, status filter) should be standardized so every author does not reinvent them.
- Interaction with lazy or windowed subjects for the rare case of a very large active working set that must stay live and addressable.

## Status

Design. The model is decided. Database-backed subjects and DTO-returning `[Query]` methods are partially achievable today. The explicit materialized-vs-proxied classification and record-query proxying over WebSocket depend on the planned cross-instance RPC (message types 5-6, see [Methods](methods.md)).
