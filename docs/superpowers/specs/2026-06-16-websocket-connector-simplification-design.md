# WebSocket Connector Simplification: Symmetric Delivery Guarantee, Hash Layer Removal

Date: 2026-06-16
Status: design approved, pending spec review
Scope: PR #197 (`feature/websocket-structural-mutations`), item 2 (re-evaluate prior fixes, remove dead complexity)

## Background

The WebSocket connector delivers changes reliably in one direction only:

- **server to client**: every broadcast carries a monotonic `Sequence` (`WebSocketSubjectHandler.CreateUpdateWithSequence`). The client tracks it (`ClientSequenceTracker`), detects gaps on updates (`IsUpdateValid`) and during idle (`IsHeartbeatInSync`), and recovers by reconnecting and pulling complete state via the Welcome handshake. Reliable.
- **client to server**: the client sends `CreatePartialUpdateFromChanges` via `SendAsync` with no sequence and no acknowledgement. The server applies received updates with no inbound sequence tracking. The `WriteRetryQueue` only re-sends on reconnect. Fire and forget: a message lost on a live connection has no detection and no recovery.

During a period when the client never actually connected (refactor #286 removed the driver that started the receive pump; fixed on this branch), a verification layer was added on top: `SentStructuralState` (a per-connection shadow of structural state reconstructed from the update stream), a SHA256 structural hash carried on every broadcast and heartbeat, a client side `HasStructuralHashMismatch` check, a client side `HasRegistryDivergence` check (shadow versus live registry), and a full reconnect on any divergence.

## Evidence (connector-tester experiments)

1. **chaos, 388 cycles, all pass**: post-GC heap drifted up monotonically (about 33 MB to 42 MB). 28 `registry divergence` events, each triggering a reconnect. The structural divergence signal was false (shadow strand bloat: shadow tracked up to 13,030 subjects versus a registry of about 490), but the reconnects those false signals triggered were incidentally healing real client to server losses.
2. **value-load, 8 cycles, about 4 hours**: post-GC heap flat (about 547 MB band). The steady-state value path has no leak.
3. **structural mutations, no chaos, 16 minutes**: live heap grew from about 56 MB to about 2.5 GB, unbounded, floor rising monotonically. With no reconnect to clear it, the `SentStructuralState` shadow is a severe memory leak under structural churn.
4. **no-hash chaos (both hash triggers disabled), decisive test**: passed 20 cycles including 4 full-chaos rounds, then FAILED on cycle 21, a no-chaos cycle. `SUBJ_408.DecimalValue` was 14035.16 on client-b but 0 on server and client-a ("written never" on the server). client-b's value write never reached the server and never recovered. The re-sync classifier confirmed a wire-level client to server delivery gap.

## Problem

Client to server delivery has no reliability guarantee. The hash/shadow layer masked this gap through incidental reconnects, at the cost of: an unbounded memory leak under structural churn, 28 false-alarm reconnects, 20 to 35 second convergence stalls on the cycles where they fired, and roughly 300 lines of complexity. The hash checks are structural and value-blind, so they never detected the value losses directly; the reconnects they triggered for structural reasons happened to resync everything via Welcome.

## Goals and non-goals

Goal: make client to server delivery reliable, symmetric to server to client, then remove the entire hash/shadow layer. Preserve all data-path correctness fixes (12 to 22).

Non-goals: changing server to client delivery, changing the data-path fixes. Item 3 (diagnostic counter cleanup: the `Diag*` counters in `SubjectUpdateExtensions`, the `ChangeQueueProcessor` diagnostic counters) and item 4 (PR split) are tracked separately.

## Design

### Correctness guarantee

Every client to server update is applied by the server in order and exactly once, or the server detects a gap and recovers the client's current state. This is the symmetric analog of the existing server to client guarantee. With both directions guaranteed, the structural hash has nothing left to verify.

### Detection: client to server sequencing (two parts)

Both checks mirror mechanisms that already exist for the server to client direction. They count messages rather than hashing content, so they catch value and structural losses alike.

1. **In-stream gap**: the client stamps each outbound `Update` message with a monotonic per-connection sequence (it currently sends none). The server tracks the expected-next sequence per connection; a received sequence greater than expected means at least one message was lost.
2. **Trailing/idle gap**: the in-stream check cannot catch a loss that is the last message before the system goes idle, which is exactly the cycle-21 failure (the lost write was the final write of the mutate phase, after which mutations paused for convergence and no further message arrived to reveal the gap). Mirror `IsHeartbeatInSync` in reverse: during idle, the client communicates its last-sent sequence to the server (piggybacked on a heartbeat reply or a lightweight client heartbeat), and the server verifies it has received up to that sequence.

### Recovery: targeted resync (reverse Welcome)

On a detected client to server gap, the server sends a `Resync` control message to that one connection. The client responds with a complete update of its owned state (reusing the complete-update machinery, `CreateCompleteUpdate`, restricted to owned properties), which the server applies authoritatively. This reuses the same complete-state-transfer mechanism Welcome already uses, in the reverse direction, and keeps the connection up (no full server to client Welcome for a gap that was purely client to server).

Symmetric framing: a server to client gap means the client pulls complete state from the authoritative server (Welcome); a client to server gap means the server pulls complete state from the client (reverse Welcome).

Fallback if targeted resync proves more involved than expected: trigger the existing reconnect on an inbound gap and extend the Hello/Welcome handshake so the client always pushes its owned complete-state after receiving Welcome. Heavier (drops the connection, full transfer both ways per gap) but reuses the reconnect path. Targeted resync is the preferred end state.

### Remove (the entire hash/shadow layer)

- `SentStructuralState` (`Internal/SentStructuralState.cs`, about 311 lines)
- server per-connection hash computation, `UpdatePayload.StructuralHash`, and the `WebSocketClientConnection` sent-state
- client `HasStructuralHashMismatch` and `HasRegistryDivergence` and their call sites
- `HeartbeatPayload.StateHash` (keep `HeartbeatPayload.Sequence`)
- `IdleDivergenceCheckDelay` config and the client `_lastUpdateReceivedTicks` it gates
- the already-dead `ChangeQueueProcessor.IsIdle()` and `_lastFlushWithChangesTicks` (no callers)

### Keep

- Core protocol: server to client sequencing (`ClientSequenceTracker`), the heartbeat `Sequence` check, the Hello/Welcome handshake.
- Data-path correctness fixes 12 to 22: registry-independent applier and `PropertyAccessor` fallback, `CompleteSubjectIds`, subject pre-resolution, registry-only structural resolution, lifecycle batch scope, the CQP `PropertyReference` filter and factory structural fallback and applier retry, retry-queue optimistic concurrency, and the per-subject apply lock. Keep the apply lock for apply serialization; drop only its now-unneeded role of giving the hash a consistent snapshot.

## Implementation staging (disable, implement, verify, remove)

1. Hashing is already disabled (current spike: both client triggers return false). Keep it disabled through implementation so the tests prove the new mechanism carries correctness on its own.
2. Implement client to server sequencing (both checks) and the targeted resync recovery.
3. Re-run validation with hashing still disabled.
4. Only after validation is green, remove the hash/shadow code listed above.

Disabling before deleting means that if the replacement has a flaw, re-enabling the hash is a one-line revert: the system is never without a safety net mid-implementation.

## Validation

- no-hash chaos run passes well past 21 cycles, including full-chaos rounds and the cycle-21 trailing-idle case.
- structural mutations no-chaos run (20-minute mutate phase, about 1800 structural ops per second): post-GC and live heap stay flat (no shadow to leak).
- the 143 WebSocket integration tests pass.
- the existing connector-tester chaos and load profiles pass.

## Risks and open questions

- The targeted resync needs a complete owned-state update. Confirm `SourceOwnershipManager` exposes the owned property set, or use full `CreateCompleteUpdate` as a simpler first cut.
- Trailing-idle detection cadence: tie the client's last-sent-sequence report to the heartbeat interval.
- Sequence reset on reconnect: the client sequence resets per connection; the server must reset expected-next on the new connection's Hello/Welcome.
- The exact transport cause of the cycle-21 loss (a discrete client-send or server-apply-drop bug versus inherent fire-and-forget loss) was not pinpointed. The guarantee makes recovery correct regardless, but if a discrete data-path bug also exists it should be filed and fixed separately.

## Temporary scaffolding to revert before commit

- the two `return false` spikes and the `#pragma warning disable CS0162` in `WebSocketSubjectClientSource.cs` (these become the real removal in stage 4)
- `appsettings.websocket-structural-nochaos.json` and its `launchSettings.json` profile (experiment-only; or keep as a documented tester profile if useful)
