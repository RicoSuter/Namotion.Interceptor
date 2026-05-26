---
title: Resilience and Fault Tolerance
navTitle: Resilience
status: Partial
---

# Resilience and Fault Tolerance Design

## Overview

HomeBlaze has a solid resilience foundation: external devices are the source of truth, live state is not persisted (recovered on restart), and the active-standby HA pattern uses fencing checks to prevent false promotion. This document captures known gaps and open questions for future hardening.

## What's Already Covered [Implemented]

| Mechanism | Status | Description |
|-----------|--------|-------------|
| Recovery from source of truth | Implemented | Satellites reconnect to devices, central receives Welcome snapshots — no stale state |
| Fencing check | Planned (with HA) | Standby verifies device reachability before promoting, preventing false promotion during network partitions |
| Source tagging | Implemented | Prevents feedback loops in bidirectional connector sync |
| Write retry queue | Implemented | Per-source ring buffer for outbound writes during disconnection |
| Per-connector independence | Implemented | Each connector has its own `ChangeQueueProcessor` with independent buffering and deduplication — a slow or failed connector does not block others |

## Open Areas [Planned]

### 1. Write Durability on Crash

The write retry queue is in-memory only — pending writes are lost on process restart. For a crash during a network blip, the operator has no way to know what was lost.

**Questions to investigate:**
- Should pending queue depth be logged on shutdown so operators can detect potential data loss?
- Is an optional write-ahead log (WAL) to disk warranted for critical writes?
- Should the system distinguish between "best-effort" and "guaranteed delivery" writes?

### 2. Cross-Instance Operation Idempotency

See [Methods and Operations — Idempotency](methods.md#idempotency) for the full discussion. Key resilience concern: if the WebSocket drops mid-RPC, pending operation invocations may be lost or duplicated. The design must define at-least-once vs. at-most-once guarantees.

### 3. Split-Brain Beyond Fencing

The fencing check prevents false promotion when the standby cannot reach devices. But additional split-brain scenarios exist:
- Primary is alive but partitioned from central — both primary and standby can still reach devices
- No leader election protocol or external arbiter is described
- If both nodes believe they are primary, both write to devices simultaneously

**Failover detection:**
- How does the standby decide the primary is truly dead vs. a transient network blip? (heartbeat timeout? miss count? external health check?)
- What is the detection latency vs. false-positive trade-off? Too fast = false promotion, too slow = extended downtime

**Preventing dual-primary:**
- OPC UA session exclusivity is not sufficient — most OPC UA servers allow multiple connections
- An external arbiter (lease/lock service, e.g., etcd, a shared database row lock, or a cloud-native equivalent) may be needed for reliable consensus
- Without an arbiter, both nodes may write to devices simultaneously — is last-writer-wins acceptable given that devices are the source of truth?
- Could a simpler approach work? E.g., primary periodically renews a lease on shared storage; standby only promotes if the lease expires

**Consumer redirection after failover:**
- When the standby becomes primary, how do consumers (satellites, central, operator UIs, AI agents) discover the new endpoint?
- Options: DNS failover (TTL-based, slow), virtual IP / floating IP (infrastructure-dependent), load balancer health check (consumers connect to a stable address, LB routes to active), explicit redirect message from standby before promotion
- WebSocket clients currently connect to a configured URL — if that URL points to the old primary, they won't automatically reach the new one
- Should the architecture mandate a stable endpoint (load balancer / virtual IP) for HA pairs, or support multiple discovery mechanisms?

### 4. Concurrent Reconnection Storm

If central restarts and many satellites reconnect simultaneously, each sends a full Welcome snapshot. This creates a burst of large messages that must be deserialized and merged concurrently.

**Questions to investigate:**
- Should central throttle or queue incoming satellite connections?
- Is a memory budget needed for concurrent Welcome processing?
- Should satellites implement backoff or priority ordering on reconnect?

### 5. Graceful Degradation

The architecture implies graceful degradation through connector independence and the "same binary" model, but degraded-mode behavior is not explicitly documented.

**Questions to investigate:**
- History sink down: does the history collector drop changes silently, buffer, or raise an alarm? (Connector independence suggests it won't block the pipeline, but this should be explicit)
- Central down: can operators still use satellite UIs for their local domain? (Implied by same binary, should be documented)
- Plugin load failure: does the instance start without the failed plugin, or refuse to start?

### 6. Configuration Corruption

Configuration is persisted as JSON files on disk.

**Decision [Planned]:** The `IStorageContainer.WriteBlobAsync` contract requires atomic writes — implementations must ensure either the full content is written successfully or the previous content remains intact (e.g., write to a temporary location, then rename). A crash or failure during write must not leave blobs in a corrupted partial state. The current `FluentStorageContainer` implementation does not yet enforce this and needs to be updated.

**Remaining questions to investigate:**
- Should configuration files include a checksum or schema version for validation on load?
- Should the system keep a backup of the last known good configuration and fall back on load failure?
- How should the system behave if configuration references a subject type from a plugin that is no longer available?

### 7. Time Synchronization

Timestamps are used throughout the system — change tracking, history recording, source timestamps propagated via OPC UA, and audit trails. Clock drift between nodes can cause subtle issues.

**Current behavior** (not yet documented in architecture):
- Value property timestamps propagate through protocol source timestamps (e.g., OPC UA `ServerTimestamp`)
- Structural timestamps (collection/dictionary/object changes) use local `DateTimeOffset.UtcNow` and are NOT synced across instances
- Each call to `SubjectChangeContext.ChangedTimestamp` without an explicit timestamp generates a new `DateTimeOffset.UtcNow`

**Questions to investigate:**
- Should NTP synchronization be a documented prerequisite for multi-instance deployments?
- Should the system detect and warn about clock skew between nodes?
- Should history recording use source timestamps (propagated) or local timestamps (when received)?
- Is monotonic ordering needed for the change pipeline, and if so, how to handle clock jumps?

### 8. Rate Limiting

No rate limiting exists on any entry point. A misbehaving or misconfigured client can overwhelm the system.

**Where rate limiting may be needed:**

| Entry Point | Risk | Example |
|-------------|------|---------|
| MCP server | External AI agent floods queries or writes | Runaway agent in a loop calling `query` at max speed |
| WebSocket sync | Satellite or peer sends excessive updates | Misconfigured connector producing high-frequency noise changes |
| Operation invocation | Expensive operations called too frequently | Cross-instance RPC amplifying load across the topology |
| Blazor UI (SignalR) | Many concurrent operator sessions | Large number of users triggering live subscriptions simultaneously |

**Questions to investigate:**
- Should rate limiting be per-connection, per-identity, or global?
- Should limits be configurable per entry point?
- Should the system respond with backpressure (slow down) or rejection (drop/error)?
- Are ASP.NET Core's built-in rate limiting middleware sufficient, or is custom logic needed for WebSocket and MCP?

## Priority

These items are not blocking current development. They should be addressed as the system matures toward production multi-instance deployments, roughly in this order:

1. **Configuration corruption** — low effort, high value, relevant even for single-instance
2. **Graceful degradation** — mostly documentation of existing behavior
3. **Time synchronization** — document requirements and current behavior
4. **Rate limiting** — relevant once MCP and multi-instance are in use
5. **Write durability** — design decision needed before production HA
6. **Concurrent reconnection** — relevant once multi-instance topology is under load
7. **Operation idempotency** — relevant when cross-instance RPC is implemented
8. **Split-brain** — relevant when HA is implemented
