# WebSocket Protocol Reliability: Continuous Divergence Detection Under Load

Status: Draft for review
Scope: `Namotion.Interceptor.WebSocket` (PR #197, `feature/websocket-structural-mutations`)
Related: [connectors-websocket.md](../../connectors-websocket.md), [connectors-subject-updates.md](../../connectors-subject-updates.md)

## Problem

The protocol works (810+ chaos cycles, 0 FAIL), but its content-divergence backstop does not run when it
is most needed.

The state digest is the only mechanism that detects **non-gap content divergence**: a value that arrived
but applied incorrectly (a torn apply, a structural-churn race), where no message was lost so the sequence
layer sees nothing wrong. The digest is **idle-gated**: `WebSocketSubjectHandler.BroadcastHeartbeatAsync`
skips the heartbeat (which carries the digest) whenever `timeSinceLastBroadcast < HeartbeatInterval`, and
`_lastBroadcastTicks` is refreshed on every broadcast. So under sustained load (for example a sensor
changing a property every second) the heartbeat never fires, the digest is never compared, and content
divergence is **never detected**. The digest only fired in the connector tester because the test has a
quiescent convergence phase after each mutate burst.

The dangerous case is concrete: a setpoint or config value written once, whose update is lost, while other
properties keep the channel busy. Continuously-changing properties **self-heal** (the next update
overwrites any drop); a value that changes once and then goes static while the link stays busy stays
diverged indefinitely, undetected.

### What "converge under load" means

Instantaneous equality (client state equals server state at the same wall-clock moment) is never true
under continuous change, because the client is always a propagation delay behind. That is the wrong notion.
The correct one: the client is a **correct replica of a prefix of the log**. The client has applied through
some sequence S, and consistency means "the client's state equals what the server's state was at S."
Convergence is a per-sequence property, verified against a sequence point, not against "right now." This
definition is what makes a continuous digest possible.

## Goals and non-goals

Goals:
- Detect content divergence under **sustained load**, soundly (no false positives, no missed settled
  divergence), not only at idle.
- Preserve all current recovery coverage and the proven reliability. No regression on bad connections.

Non-goals (out of scope):
- Server acknowledgment of client writes (#231). Convergence comes from other layers; deferred.
- HeapMB drift investigation. Tracked separately (other agent).
- Full log-spine rewrite (making all state changes sequenced so `state@S` is globally well-defined). The
  settling margin below avoids needing it; see Alternatives.
- Per-subject Merkle tree (localizing which subtree diverged). A recovery-efficiency optimization, not
  needed for detection; see Alternatives.

## Design: settled-subset, sequence-anchored digest

Hash only the part of the graph that has been **stable long enough to be comparable**, anchored by
sequence so both sides agree exactly on what that part is.

### Mechanism

- Each side tracks, per property, the sequence at which it last changed: the server records
  `last_change_seq(P)` as the broadcast sequence when P was last sent; the client records it as the
  sequence of the update in which P was last applied. For a property both have processed via the same
  broadcast, these are equal. Both are derived from the normal update stream; no value is retained, only a
  sequence number.
- The **settled subset** at sequence S is `{ P : last_change_seq(P) <= S - margin }`, where `margin` is a
  small number of sequences that exceeds the flush/propagation horizon (the window in which a write can be
  written to the backing store but not yet broadcast/applied).
- The server periodically (every `DigestInterval`, regardless of activity) computes the digest over its
  settled subset using **live values**, and sends `(S, digest)`.
- The client, having applied through S in order, computes the digest over its settled subset using its
  live values, and compares.
- **Pending-write gate:** the client performs the comparison only when it has **no un-echoed outbound
  writes**. This excludes the client's own optimistic, not-yet-authoritative writes, which would otherwise
  look like divergence. For realistic asymmetric load (server telemetry continuous, client setpoints
  occasional) clean comparison points are frequent.
- The digest is **client-evaluated only**. The client→server digest (`ClientHeartbeat.Digest`) is removed
  as redundant: a digest mismatch is symmetric, so the single client-side comparison detects divergence in
  both directions.

### Why it is sound under load

The naive whole-graph live digest is unsound under load because the server's live state runs ahead of the
broadcast sequence (writes hit the store before being sequenced at flush time, see Alternatives). The
settling restriction resolves this on three counts, because **the skew lives entirely in the
recently-written properties**, which the settled subset excludes:

1. **No live-leads-sequence skew.** A settled property has had no write for at least `margin` sequences, so
   it has no unflushed backlog and its live value equals its value at S. The lead/lag is confined to the
   excluded (recently-written) set.
2. **No torn reads.** Tearing only affects properties being concurrently written, which are exactly the
   churning ones we exclude. The settled subset is stable to read, so no lock is required (unlike a
   whole-graph snapshot).
3. **Identical subsets on both sides.** Inclusion is keyed on the shared **broadcast sequence**, not a wall
   clock, so both sides include precisely the same properties: no clock skew, no fuzzy boundary band, and
   therefore no false positives. Debounce is not needed for soundness (a small K may still be kept only as
   defense-in-depth against a local app write racing the client's own digest computation).

### Why excluding the churning properties is safe

A property that keeps changing self-heals: if it diverges, its next update overwrites the divergence
(current-state / LWW). So a churning property never *stays* diverged. Divergence only persists if a
property **settles** at a wrong value, and once it has been stable for `margin` sequences it enters the
subset and is caught. Coverage is therefore exactly right: verify what can stay broken (settled), skip what
fixes itself (churning).

### Filter alignment (required)

`StateDigest.Compute` currently hashes all non-derived, getter-bearing properties and does **not** apply
the connector's `ISubjectUpdateProcessor`/`PathProvider` filter, while the wire format and client ownership
both honor it. A property that is filtered out of sync but still hashed would sit at its local default on
the client and mismatch permanently. With a continuous digest this becomes a permanent thrash source, so
the digest **must apply the same inclusion filter as the transport**.

### Recovery on a detected mismatch

A confirmed mismatch routes into the existing recovery: the client reconnects, the server sends a full
Welcome snapshot, and the client converges. Additionally, on reconnect the client **re-pushes its complete
owned state** (reuse `BuildOwnedStateUpdate`, triggered from the reconnect path rather than from a server
`Resync`).

Rationale for the re-push, stated precisely: for a connection-loss reconnect it is redundant, because the
write retry queue already holds the fresh write and current-state semantics mean only the latest value
matters. It is load-bearing specifically for **digest-triggered** recovery: detection can lag by up to
`DigestInterval + margin`, which may exceed the retry queue's `RetryTime`/ring-buffer window, so a lost
client write could otherwise be discarded when the client adopts the server baseline. Owned state is small,
so re-asserting it on every reconnect is cheap insurance.

### Delivery and liveness

The digest is broadcast to all clients tagged with a single sequence S (one computation, reused per
connection, as today). Send it either as a small dedicated message after broadcast S, or as a field
piggybacked on a periodic update batch. The existing idle heartbeat is retained for **liveness/dead-connection
detection** (the client's `ReceiveTimeout` still depends on periodic traffic); only the digest's
idle-gating is removed. Preserve the existing empty-digest skip (no registry / pre-Welcome) so a client
without comparable state never thrashes. The digest remains **timestamp-insensitive** (values are hashed,
sequence is used only to select the subset), so clock skew cannot cause a mismatch.

## Alternatives considered

1. **Idle-gated whole-graph digest (current).** Rejected: never fires under sustained load, which is the
   normal operating mode. This is the problem being fixed.
2. **Continuous whole-graph live digest anchored to S.** Rejected as unsound: the server's live state runs
   ahead of the broadcast sequence because a write mutates the backing store immediately
   (`PropertyChangeQueue.WriteProperty` writes then enqueues) but is sequenced later at flush
   (`CreateUpdateWithSequence`), with `BufferTime` batching. `_applyUpdateLock` does not guard the server's
   own writers, so even holding it does not produce a clean cut. A client at S can never match a live digest
   that includes unflushed backlog: systematic false mismatch under load. (This is the flaw the settled
   subset fixes by excluding exactly the skewed, recently-written properties.)
3. **Lock-free whole-graph digest + debounce.** Rejected: each comparison is unsound (torn snapshots), and
   churning properties inject persistent noise, forcing an unwinnable tradeoff (sensitive enough to catch a
   static divergence means thrash under load; desensitized enough to avoid thrash can miss real divergence).
   Debounce only papers over this statistically and fails in the high-load regime.
4. **Incremental hash over the transmitted update stream.** Rejected as wrong-for-purpose: it verifies what
   the client *received* (already guaranteed by sequence numbers), not what it *applied*, so it misses the
   torn-apply content divergence that is the digest's entire reason to exist. A cumulative variant also
   poisons permanently on a transient divergence that later self-heals.
5. **Timestamp-based settling window.** The same settled-subset idea but keyed on wall-clock timestamps.
   Rejected in favor of sequence anchoring: timestamps introduce clock skew and a fuzzy boundary band where
   the two sides disagree on membership, reintroducing false positives under high churn. Sequence anchoring
   makes the subset boundary exact and identical on both sides.
6. **Per-subject Merkle tree.** Not chosen for this PR: a single settled-subset hash is sufficient to
   *detect* divergence; a Merkle tree only helps *localize* it to resync a subtree instead of the whole
   graph, which is a recovery-efficiency optimization. Documented as a future option for very large graphs.
7. **Full log-spine rewrite** (all state changes are sequenced log entries, `state@S = fold(log[1..S])`).
   This is the clean-slate design that makes `state@S` globally well-defined and would make a whole-graph
   digest anchorable. Rejected as out of scope: it adds contention/latency to the hot write path (the
   current design writes immediately and sequences lazily for throughput) and is a large rewrite of a
   working system. The settling margin achieves a sound digest without it.

## Mechanism inventory

| Mechanism | Action | Reason |
|---|---|---|
| Server→client sequence + gap detect → reconnect | keep | Instant, constantly-exercised workhorse |
| Reconnect → Welcome (full snapshot) | keep | Baseline recovery |
| Write retry queue | keep | Recovers fresh client writes on reconnect |
| Inbound apply-race guards (`PendingApplyBuffer`, deferred-detach) | keep | Apply correctness; primary under-load defense |
| Per-property `last_change_seq` tracking | **add** | Selects the settled subset on both sides |
| State digest | **change** | Idle-gated whole-graph → continuous settled-subset, sequence-anchored, filter-aligned, client-evaluated |
| Owned-state re-push on reconnect (`BuildOwnedStateUpdate`) | **add** | Preserves client writes on digest-triggered recovery that lags past the retry window |
| `ClientHeartbeat.Digest` (client→server digest) | **remove** | Redundant with the symmetric client-side comparison |
| Idle heartbeat (liveness) | keep | Dead-connection detection; only the digest's idle-gating is removed |
| Reverse path (`ConnectionSequenceTracker`, `Resync`, `ClientHeartbeat`, client sequence stamping) | keep (Phase 2 candidate) | Real-time gap detection; removal is a separate gated decision |

## Phasing

- **Phase 1 (this PR):** the settled-subset continuous digest (with `last_change_seq` tracking, the
  pending-write gate, filter alignment) + owned-state re-push on reconnect + remove `ClientHeartbeat.Digest`.
  Keep the reverse path. This closes the detection-under-load gap soundly with no loss of existing coverage.
- **Phase 2 (separate, gated on Phase 1 verification):** consider removing the reverse path, now that a
  sound continuous backstop exists. Justified only if the verification below stays green without it. Held
  out of this PR given the bad-connection reliability requirement.

## Verification

The connector tester's current chaos profile has a quiescent convergence phase, the only reason the idle
digest ever fired. The oracle must change to exercise the real failure mode:

- **Continuous-load profile:** mutate continuously with a mix of churning and settling properties, no
  quiescent phase.
- **Injected settled divergence:** a test-only hook that, with no sequence gap, corrupts or drops one
  applied value on one side and then lets that property go static (the torn-apply / lost-setpoint case).
- **One-shot client write dropped under continuous server load.**
- **Assertions:** both are detected once the affected property settles, within roughly
  `DigestInterval + margin`, and the system converges with the client's write preserved. Run with the
  reverse path present (Phase 1 acceptance), then again without it (Phase 2 gate). This becomes a permanent
  regression test.

Existing acceptance unchanged: full unit suite green, WebSocket integration suite green, long chaos run
0 FAIL.

## Risks and open questions

- **Per-property `last_change_seq` memory.** One sequence number per property, O(graph) bounded, overwritten
  on each change (not the per-update unbounded shadow structure removed earlier), so leak-free but a real
  per-property cost (~8 bytes). Confirm acceptable against the largest target graphs; reuse existing
  per-property metadata storage if possible.
- **`margin` sizing.** Must exceed the flush/propagation horizon so settled implies no pending backlog, but
  small enough that settled divergence is detected promptly. Derive from `BufferTime` and measured
  propagation; tune against the continuous-load test.
- **`DigestInterval` vs retry window.** Detection can lag by `DigestInterval + margin`; the owned-state
  re-push covers client-write preservation past the retry window, but keep `DigestInterval` modest
  (e.g. 10-30s) so detection latency stays bounded.
- **Residual gap: systematic corruption of a churning property.** A deterministic apply bug that corrupts a
  property on every apply while it keeps changing stays excluded and uncaught. This is a gross bug that
  testing should catch, accepted as out of scope for a runtime backstop. The digest targets transient races
  whose divergence then settles.
- **Pending-write gate under continuous client writing.** If a client writes continuously (uncommon), clean
  comparison points are rare and detection latency rises. Acceptable: the digest is a backstop and client→server
  delivery is also covered by the retry queue. Revisit only if such a deployment appears.
