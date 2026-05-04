# Follow-ups

Follow-up improvements identified during the WebSocket structural mutation fix session.

---

## CQP filter: PathProvider inclusion cache via `subject.Data`

**Priority:** High (security) — **IMPLEMENTED** (Fix 23, enhanced in Fix 24)
**Context:** Fix 19 relaxed the server CQP filter to accept changes for momentarily unregistered subjects, bypassing PathProvider. This prevents value drops during structural mutations but allows PathProvider-excluded properties to leak during the microsecond unregistration window.

**Implementation:** Eager PathProvider caching was implemented — on the first registered encounter for any property, ALL sibling properties are cached via PathProvider in a single pass. This eliminates the "never seen while registered" cache miss window entirely. The "no cache → drop" default is now safe because eager caching ensures all known properties are cached before any unregistered access can occur. No security bypass is possible.

The cache uses a per-instance prefix (`_filterCachePrefix` = GUID) to isolate cache entries between server instances sharing the same subject graph. See [registry-independent pipeline design](2026-04-06-registry-independent-pipeline-design.md) for full design.

**Risk if deferred:** N/A — implemented.

---

## Hash-in-update: embed structural hash in every broadcast

**Priority:** High (reliability) — **IMPLEMENTED** (Fix 23)
**Context:** Fix 21's heartbeat structural hash only compares when the system is "quiet" (no updates for 10+ seconds). If value mutations never stop, structural divergence goes undetected.

**Follow-up:** Embed cached structural hash in every CQP broadcast message. Client compares after each apply. Add idle-only heartbeat (10s after last broadcast) for edge case coverage. See [registry-independent pipeline design](2026-04-06-registry-independent-pipeline-design.md) for full design.

**Risk if deferred:** Structural divergence in one part of the graph goes undetected while another part has active value mutations. Current "quiet only" mechanism may never fire in production systems with continuous sensor data.

---

## Structural hash: lazy recomputation instead of per-heartbeat graph walk

**Priority:** Medium (performance) — **SUPERSEDED** (Fix 24)
**Context:** The sent-state hash approach (Fix 24) eliminated the live graph walk entirely. The hash is computed from a plain `SortedDictionary`, which is significantly faster than the interceptor-chain-based graph walk. Lazy recomputation is no longer needed.

---

## Lightweight structural sync instead of full reconnect

**Priority:** Low (optimization)
**Context:** On hash mismatch, the client currently triggers a full WebSocket reconnection → Welcome (complete state). A lighter approach would send a SyncRequest that triggers a Welcome-style response without dropping the connection.

**Follow-up:** Add `SyncRequest` message type to the WebSocket protocol. Server responds with complete state update (same as Welcome) without closing the connection. Saves the TCP/TLS handshake and connection setup overhead.

**Risk if deferred:** Unnecessary reconnection overhead on hash mismatch. Acceptable for rare mismatches but inefficient if mismatches are frequent during high-mutation periods.

---

## Incremental structural hash

**Priority:** Low (optimization) — **SUPERSEDED** (Fix 24)
**Context:** The sent-state hash approach (Fix 24) already provides O(changed subjects) per update for dictionary maintenance. SHA256 is recomputed from the dictionary on each broadcast, but this is a plain data walk — no interceptor chain, no registry access. For graphs where SHA256 recomputation becomes a bottleneck, XOR-based incremental hashing can be added to `SentStructuralState` as a future optimization.

---

## SentStructuralState: partial structural update merging

**Priority:** Low (correctness edge case)
**Context:** When a partial update includes only some structural properties of a subject, `BuildStructuralContent` processes only the included properties, losing track of children from non-included structural properties. For example, if a subject has both `Items` (dictionary) and `Config` (object ref), and an update only includes `Items`, the stored structural content for that subject will only reflect `Items` — the `Config` reference is lost from the tracked state.

**Impact:** This does NOT cause false-positive hash mismatches. Both server and client process the same partial update through the same `BuildStructuralContent` logic, so both make the same "mistake" — their hashes remain consistent. However, `TrackedSubjectCount` may drift from the actual subject count because orphaned children (referenced only by the non-included structural property) may be incorrectly removed from tracking or incorrectly retained.

**Follow-up:** When processing a partial update for a subject that already has tracked structural content, merge the update's structural properties with the existing stored content rather than replacing it. Only the properties present in the update should be updated; non-included structural properties should retain their previous values and children.

---

## Value divergence detection

**Priority:** Medium (reliability)
**Context:** The structural hash (Fix 24) only covers graph topology — which subjects exist and their structural property references. If a WebSocket send fails silently (not a connection drop, just a failed send), value updates are permanently lost. The structural hash won't detect this because the structure is correct — only the value is wrong. The value stays wrong until either another mutation overwrites it (self-heal) or a reconnection happens for another reason (Welcome re-syncs everything). If mutations stop and no reconnection happens, the lost value persists indefinitely.

**Follow-up:** Add a lightweight value hash (e.g., hash of all value property timestamps or a rolling checksum) to the sent-state model. Include it alongside the structural hash in broadcasts and heartbeats. On mismatch, trigger reconnection + Welcome to re-sync all values. Trade-off: value hashing is more expensive than structural hashing (more data to hash) and may produce more false positives during concurrent mutations (values change faster than structure).

**Risk if deferred:** Silent permanent value loss on failed WebSocket sends without connection drop. Self-heals if mutations continue or if a reconnection happens for another reason. Unlikely in practice (TCP guarantees delivery for open connections) but possible with application-level send failures.
