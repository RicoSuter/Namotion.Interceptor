# Registry-Independent Pipeline Design

## Problem

The WebSocket connector's change pipeline (CQP → SubjectUpdateFactory → SubjectUpdateApplier) depends on shared mutable state (`SubjectRegistry`, `LifecycleInterceptor`) that changes underneath it during concurrent structural mutations. This produces silent value/structural drops that cause permanent divergence between server and client.

Over 22 fixes were applied to address individual race conditions. Many are genuine improvements (registry-independent apply, batch scope, pre-resolve), but they were developed reactively. The detection mechanism (heartbeat structural hash) has a "quiet only" limitation: it only compares hashes when no updates have been applied for 10+ seconds. If value mutations never stop, structural divergence in one part of the graph is never detected while another part is actively changing.

This design consolidates the changes into a principled architecture where:
1. The factory and applier are registry-independent at their core
2. Divergence detection is embedded in every update (no "quiet" window)
3. Only essential fixes are retained; workarounds are removed

## Design Overview

Three layers:

1. **Registry-independent pipeline** — Factory and applier use `subject.Properties` (compile-time generated metadata) instead of `SubjectRegistry` for property metadata. Drops due to "subject momentarily unregistered" are eliminated.
2. **Hash-in-update detection** — Every broadcast includes the current structural hash. Divergence is detected on the next update, not after a 10-second silence window.
3. **Idle heartbeat** — When no updates are broadcast for 10 seconds, a lightweight heartbeat carries the hash. Covers the edge case where the last update is lost and the system goes idle.

## Layer 1: Registry-Independent Pipeline

### Factory (serialization)

The `SubjectUpdateFactory` has two paths:

**Normal path** (subject registered): Uses `RegisteredSubjectProperty` for property metadata, PathProvider filtering via `ISubjectUpdateProcessor.IsIncluded`, and type-specific serialization (`IsSubjectDictionary`, `IsSubjectCollection`, `IsSubjectReference`).

**Fallback path** (subject momentarily unregistered): Uses `subject.Properties[propertyName]` (`SubjectPropertyMetadata`) for type determination (`Type.IsSubjectDictionaryType()`, etc.) and serialization. PathProvider check is skipped (see "CQP Filter" section for justification).

Both paths produce identical `SubjectPropertyUpdate` output. The fallback is not a degraded mode — it uses the same compile-time generated metadata, just accessed differently.

**Changes vs master:** The fallback path in `ProcessPropertyChange` (lines 126-181 in current branch) is new. This is the core of the registry-independent factory.

### CQP Filter

The `ChangeQueueProcessor` filter signature changes from `Func<RegisteredSubjectProperty, bool>` to `Func<PropertyReference, bool>`. This allows the filter to handle both registered and unregistered subjects.

**Why unregistered properties must not be silently dropped:**

A CQP dedup cancellation edge case causes permanent undetected value divergence if unregistered value changes are dropped:

1. `X.Decimal = 42` → change queued
2. `parent.Items = dictWithoutX` → X detached → structural change queued
3. `parent.Items = dictWithX` → X re-attached → structural change queued
4. CQP flush: drops `X.Decimal = 42` (X unregistered), dedup merges structural changes → old=dictWithX, new=dictWithX → no-op
5. Factory sees no structural change → no `ProcessSubjectComplete(X)` → value 42 permanently lost
6. Structural hash matches (structure identical, only values differ)

**Solution: Eager PathProvider caching with per-instance prefix**

The server-side filter (in `WebSocketSubjectHandler`) uses **eager caching** — on the first registered encounter for any property of a subject, ALL sibling properties are cached via PathProvider in a single pass. This guarantees that every property the PathProvider knows about is cached before any unregistered encounter can occur. The cache uses a per-instance prefix (`_filterCachePrefix` = GUID) to isolate cache entries between server instances, preventing stale cache hits when multiple `WebSocketSubjectHandler` instances share the same subject graph.

```csharp
private readonly string _filterCachePrefix = Guid.NewGuid().ToString("N");

Func<PropertyReference, bool> propertyFilter = propertyRef =>
{
    var property = propertyRef.TryGetRegisteredProperty();
    var cacheKey = ($"ws:{_filterCachePrefix}:included", propertyRef.Name);

    if (property is not null)
    {
        // Eager cache: on first registered encounter, cache ALL sibling properties
        if (!propertyRef.Subject.Data.ContainsKey(cacheKey))
        {
            foreach (var sibling in property.Subject.Properties)
            {
                var siblingCacheKey = ($"ws:{_filterCachePrefix}:included", sibling.Name);
                var siblingIncluded = PathProvider.IsPropertyIncluded(sibling);
                propertyRef.Subject.Data.GetOrAdd(siblingCacheKey, siblingIncluded);
            }
        }

        var included = PathProvider.IsPropertyIncluded(property);
        return included;
    }

    // Unregistered: use cached decision from subject.Data
    // Safe default: drop if no cache (all known properties are eagerly cached)
    return propertyRef.Subject.Data.TryGetValue(cacheKey, out var cached)
        && (bool)cached;
};
```

**Server-side filter behavior:**

```
Registered + PathProvider includes    → accept + cache (eager cache all siblings)
Registered + PathProvider excludes    → drop   + cache (eager cache all siblings)
Unregistered + cache hit "include"    → accept
Unregistered + cache hit "exclude"    → drop
Unregistered + no cache (never seen)  → drop (safe default, all known properties eagerly cached)
```

**Security properties:**
- PathProvider is always evaluated when the subject is registered (99.99%+ of changes)
- Eager caching ensures ALL sibling properties are cached on first registered encounter — no property can reach the "never seen" state unless it was truly never registered
- Unregistered properties use the SAME PathProvider decision made earlier — no bypass
- The "no cache" drop is safe: eager caching means all known properties are cached before any unregistered access can occur. A cache miss means the property was never seen while registered, which is only possible if its first-ever change happens during the microsecond unregistration window — the structural mutation's `ProcessSubjectComplete` will include the value

**Cache properties:**
- Stored on `subject.Data` → same lifecycle as subject, no external state, no memory leaks
- Per-instance prefix (`_filterCachePrefix` = GUID) → multiple server instances sharing the same subject graph have isolated caches, preventing stale cache hits or cross-instance interference
- One entry per property per subject, populated eagerly for all siblings on first encounter
- `ContainsKey` / `GetOrAdd` / `TryGetValue` are lock-free reads on the fast path (ConcurrentDictionary)

**Client-side filter is unchanged:** The client filter checks `TryGetSource()` to prevent echo loops. This MUST stay strict — relaxing it causes catastrophic echo loops (960 missing subjects in testing). Client-side value drops are harmless (properties not owned by this source).

### Applier (deserialization)

The `SubjectUpdateApplier` uses `PropertyReference` and `SubjectPropertyMetadata` instead of `RegisteredSubjectProperty`:

```csharp
// Master: depends on registry
var registeredProperty = subject.TryGetRegisteredProperty(propertyName, registry);
if (registeredProperty is null) return; // Silent skip!

// Branch: registry-independent
if (!subject.Properties.ContainsKey(property.Name)) return; // Only skips unknown properties
var metadata = property.Metadata; // Always available
```

Property updates are never silently skipped due to registration state. The subject always knows its own properties via the compile-time generated `Properties` dictionary.

### Batch Scope

`LifecycleInterceptor.CreateBatchScope()` defers `isLastDetach` processing during `SubjectUpdateApplier.ApplyUpdate`. Subjects moving between structural properties within the same update stay in `_attachedSubjects` and `_knownSubjects` throughout.

Required because the applier processes structural changes sequentially: removing X from DictA then adding X to DictB. Without batch scope, X would be fully detached (children cleaned, context removed) between the two operations.

Two additional changes for batch scope correctness:
- `ContextInheritanceHandler` condition changed from `ReferenceCount: 0` to `IsContextDetach: true` — prevents the `RemoveFallbackContext → DetachSubjectFromContext` chain from bypassing the batch scope
- `EndBatchScope` uses the root context (passed via `CreateBatchScope(rootContext)`) for handler resolution and child detach

### Pre-Resolve Subjects

`SubjectUpdateApplyContext.PreResolveSubjects()` caches subject ID → CLR instance mappings before structural processing begins. Handles the cross-thread race where the mutation engine removes a subject from the live registry on a different thread while the applier is processing.

The batch scope handles same-thread races (within the apply). Pre-resolve handles different-thread races (mutation engine concurrent with apply).

### Apply Lock

Per-subject lock stored in `subject.Data`. Serializes concurrent `ApplySubjectUpdate` calls (e.g., WebSocket receive loop + reconnection-triggered Welcome). Also used by hash computation to guarantee a consistent snapshot.

### CompleteSubjectIds

`SubjectUpdate.CompleteSubjectIds` distinguishes "new subject with complete state" from "reference to existing subject." Prevents the applier from fabricating default-valued instances when a concurrent mutation removed a subject from the ID registry.

### Phase 1 Property Application

New subjects (created without context during apply) get properties applied BEFORE entering the graph via `SetValue`. This builds the full subgraph in memory first, so concurrent mutations that read the backing store after `SetValue` find fully-populated instances.

### Applier Retry

The Step 2 loop buffers subjects not found on the first pass and retries after all known subjects are processed. Handles within-batch ordering where value changes for a new subject precede the structural change that creates it.

## Layer 2: Sent-State Structural Hash

### Problem

The previous approach (Fix 21) computed the structural hash from the **live graph** — walking `SubjectRegistry.KnownSubjects`, calling `property.GetValue()` through the interceptor chain, and hashing the result with SHA256. This has a fundamental flaw for per-update detection:

The server's live graph always contains mutations from the mutation engine that haven't been flushed by CQP yet. The `_applyUpdateLock` blocks other CQP flushes and applies, but **not the mutation engine**. Between the CQP buffer drain and hash computation (or even during hash computation), the mutation engine can structurally mutate the graph on another thread. The hash captures these unflushed mutations, but the update doesn't contain them. The client applies the update, computes its own hash, and gets a different result — false positive.

This is not a timing issue that can be fixed with better locking. The live graph and the CQP buffer are fundamentally different views of the state. The hash must reflect what was **sent**, not what **exists**.

### Design: Per-Connection Sent-State Hash

Instead of hashing the live graph, each `WebSocketClientConnection` on the server maintains its own `SentStructuralState` — a lightweight dictionary tracking the structural state implied by all updates sent to that specific client. The hash is computed from this dictionary using SHA256. Since the dictionary is only updated from the actual `SubjectUpdate` content (the exact data sent to the client), the hash is **consistent with what the client received by construction**.

The client maintains an identical `SentStructuralState` instance, updated from received `SubjectUpdate` content. Both sides compute SHA256 from their dictionaries. If the hashes differ, a real divergence occurred (dropped update, failed apply, bug in the pipeline).

**Why per-connection, not a single global instance:** A single global `_sentState` on the server gets re-initialized from the Welcome snapshot every time a new client connects. This re-initialization breaks the sent-state tracking for all other already-connected clients — their cumulative broadcast history is replaced by the new client's Welcome snapshot. Even an "initialize once" approach fails because the Welcome includes the complete live graph at snapshot time, which contains unflushed CQP mutations that the cumulative sent state doesn't have yet. This creates a hash mismatch for other clients, triggering unnecessary reconnections in a cascading loop. Per-connection state avoids both problems: each connection's `SentStructuralState` is initialized from the Welcome it was sent and updated from each broadcast it receives.

**Architectural property:** `SentStructuralState` depends only on `SubjectUpdate` and `SubjectPropertyUpdate` (connectors layer). No dependency on `SubjectRegistry`, interceptors, or core library. It's a pure WebSocket-internal concern — the lower layers don't know the hash exists.

### Data Structure

```csharp
internal sealed class SentStructuralState
{
    // subjectId → deterministic string of structural property content
    // Sorted for deterministic hash computation
    private readonly SortedDictionary<string, string> _subjectStructure
        = new(StringComparer.Ordinal);

    // subjectId → set of child subject IDs referenced by structural properties
    private readonly Dictionary<string, HashSet<string>> _children = new();

    // subjectId → number of parents referencing this subject
    private readonly Dictionary<string, int> _referenceCount = new();

    public string? ComputeHash() { ... } // SHA256 of _subjectStructure
    public void InitializeFromSnapshot(SubjectUpdate completeUpdate) { ... }
    public void UpdateFromBroadcast(SubjectUpdate partialUpdate) { ... }
}
```

**Per-connection lifecycle:** Each `WebSocketClientConnection` owns a `SentStructuralState` instance. It is created when the connection is established, initialized from the Welcome snapshot sent to that client, updated on every broadcast under `_applyUpdateLock`, and discarded when the connection closes. The server has no global sent state — only per-connection instances.

**Thread safety:** All `SentStructuralState` access on the server — both `UpdateSentState` (in `CreateUpdateWithSequence` on the broadcast path) and `ComputeSentStateHash` (in `BroadcastHeartbeatAsync` on the heartbeat path) — happens under `_applyUpdateLock`. There is no unsynchronized access to `SentStructuralState`. The hash is pre-computed under the lock and passed as a dictionary of per-connection hashes to `BroadcastUpdateAsync`, which runs outside the lock. This ensures the hash is always consistent with the update content while keeping I/O (serialization + send) outside the lock.

**Structural content extraction:** For each subject in the update, properties with `Kind` of `Object`, `Collection`, or `Dictionary` are structural. Their content is serialized to a deterministic string:
- `Object` → `"PropertyName:subjectId"` (or `"PropertyName:-"` if null)
- `Collection` → `"PropertyName:[id1,id2,...]"` (order preserved)
- `Dictionary` → `"PropertyName:{key1=id1,key2=id2,...}"` (sorted by key)

Properties with `Kind = Value` are ignored — structural hash only covers graph topology.

### Initialization (Welcome)

When a new client connects, the server creates a complete `SubjectUpdate` snapshot under `_applyUpdateLock`. A new `SentStructuralState` is created for that connection and initialized from the Welcome snapshot content. The client initializes its own `SentStructuralState` from the same Welcome content.

Both sides start from the same Welcome payload, so their initial hashes match. Subsequent broadcasts update both the connection's server-side `SentStructuralState` and the client's instance from the same update content, keeping them in sync. Other connections are unaffected — each has its own `SentStructuralState` reflecting its own Welcome baseline plus the broadcasts it has received.

### Update (per broadcast)

For each subject in the `SubjectUpdate.Subjects` dictionary:

1. **Extract structural content:** Walk properties, build deterministic string for structural properties only.
2. **Diff children:** Compare new child set against `_children[subjectId]`.
   - Removed children: decrement `_referenceCount`. If zero → recursively remove subject (its structure, its children's reference counts).
   - Added children: increment `_referenceCount`.
3. **Store:** Update `_subjectStructure[subjectId]` and `_children[subjectId]`.

### Hash Computation

Walk `_subjectStructure` (already sorted). Concatenate all entries: `"subjectId|structuralContent\n"`. SHA256 the result. Return as hex string.

Cost: O(all subjects) for hash computation, but from a plain `SortedDictionary` — no interceptor chain, no property reads, no registry access. Significantly faster than the live graph walk.

### Client-Originated Structural Mutations

When a client makes a structural mutation (e.g., UI adds a device):

1. Client's live graph changes immediately
2. Client's CQP sends the change to server
3. Server applies → server's CQP captures → broadcasts to all clients
4. Originating client receives broadcast, echo-filters during apply (already has the change)

The `SentStructuralState` is updated from received update **content**, not from apply **results**. Echo filtering is irrelevant — both the server and client process the same `SubjectUpdate` payload into their `SentStructuralState`. During the round-trip window (before the server broadcasts the mutation back), neither side's sent state includes the mutation, so hashes match. After the broadcast, both sides update from the same payload, so hashes still match.

This means client-originated structural mutations are handled correctly with no special cases.

### What It Replaces

| Removed | Replaced by |
|---------|------------|
| `StateHashComputer.cs` (live graph walk + SHA256) | `SentStructuralState` (plain dictionary + SHA256), one per connection on server |
| `ComputeStructuralHash()` under `_applyUpdateLock` on every broadcast | `connection.SentState.UpdateFromBroadcast(update)` per connection — O(changed subjects) dictionary update |
| `ComputeStructuralHash()` under apply lock on client per update | `_clientState.UpdateFromBroadcast(update)` — no lock needed |
| Live graph walk on heartbeat | `connection.SentState.ComputeHash()` per connection — plain dictionary walk |
| Client dependency on `_subject.GetApplyLock()` for hash | Not needed — hash computed from update content |
| Single global `_sentState` on server | Per-connection `SentStructuralState` on `WebSocketClientConnection` |

## Layer 3: Idle Heartbeat

When no updates are broadcast for 10 seconds, the server sends a lightweight heartbeat with `{sequence, structuralHash}`. The hash is computed per-connection from each connection's `SentStructuralState` under `_applyUpdateLock` — same `ComputeHash()` method, O(all subjects) from a plain dictionary. Each client receives a heartbeat with its own connection-specific hash.

This covers the edge case where the last update is lost and the system goes idle. During active operation, the heartbeat never fires — updates already carry the hash. The heartbeat is a "nothing happened, state is still X" signal.

Implementation: keep the existing 10-second periodic timer, but only send a heartbeat if no update was broadcast since the last tick. Heartbeat hash is read from each connection's `SentStructuralState`, not computed from the live graph. The `_applyUpdateLock` is held during hash computation to prevent races with concurrent `UpdateFromBroadcast` calls.

## Changes vs Master Summary

### Keep (essential improvements)

| Change | Fix | Why |
|--------|-----|-----|
| Stable base62 subject IDs | Pre-18 | Enables direct ID lookup, removes BuildPathToRoot |
| Applier uses PropertyReference/Metadata | 13 | Registry-independent apply |
| CompleteSubjectIds protocol field | 12 | Prevents hollow subject creation |
| Pre-resolve subjects | 14 | Cross-thread mutation race |
| Backing store race removal | 15 | Applier uses registry-only resolution |
| Phase 1 property application | 16 | New subjects populated before graph entry |
| Batch scope on LifecycleInterceptor | 18 | Same-thread subject moves during apply |
| ContextInheritanceHandler condition | 18 | Required for batch scope correctness |
| SuppressRemoval removed from registry | 18 | Replaced by batch scope |
| CQP filter Func\<PropertyReference\> | 19 | Prevents silent drops at CQP level |
| Factory structural fallback | 19 | Registry-independent serialization |
| Applier retry for deferred subjects | 20 | Within-batch ordering |
| Apply lock | 22 | Concurrent apply safety |

### Redesign (replace current approach)

| Change | Fix | New approach |
|--------|-----|-------------|
| Server CQP filter (blind accept of unregistered) | 19 | Eager cached inclusion filter — PathProvider decisions eagerly cached for ALL sibling properties on first registered encounter, with per-instance GUID prefix for multi-server isolation |
| `StateHashComputer` live graph walk | 21 | `SentStructuralState` — hash from sent update content, not live graph |
| Quiet-only hash comparison | 21 | Hash-in-update, compare on every apply |
| Separate heartbeat hash message | 21 | Hash embedded in update messages |
| Periodic heartbeat during active operation | 21 | Idle-only heartbeat (10s after last broadcast) |

### Revert (no longer needed)

| Change | Fix | Why revert |
|--------|-----|-----------|
| `StateHashComputer.cs` | 21 | Replaced by `SentStructuralState` |
| Diagnostic logging (if any remains) | Various | Was for investigation only |
| `_lastUpdateAppliedTicks` field in client | 21 | Not needed — sent-state hash is compared on every update |
| Consecutive mismatch counting (if present) | 21 | Not needed — sent-state hash has no false positives |
| Client apply lock for hash computation | 21 | Not needed — hash computed from update content, not live graph |

## Correctness Analysis

### Value changes during structural mutations

**Scenario:** Value change queued for subject X while X is momentarily unregistered.

- **CQP filter:** Cache hit → uses PathProvider decision from when X was registered → accept
- **Factory:** Fallback path serializes using `subject.Properties`
- **Client receives:** Value update for X → applied
- **Sent-state hash:** Not affected (value changes don't change structural hash)
- **Result:** No value loss, PathProvider enforced via cache

### CQP dedup cancellation

**Scenario:** Subject X removed and re-added to same property within one flush window.

- **CQP filter:** Cache hit for X.Decimal → accept (PathProvider previously included it)
- **CQP dedup:** Structural changes cancel out (old=withX, new=withX)
- **Factory:** Value change is serialized via fallback → included in update
- **Client receives:** X.Decimal = 42 directly (not via ProcessSubjectComplete)
- **Sent-state hash:** Structure unchanged (X still in same position) → hashes match
- **Result:** No value loss, PathProvider enforced via cache

### First-ever property change during unregistration (cache miss)

**Scenario:** Property's first-ever change occurs during the microsecond unregistration window.

- **CQP filter:** No cache entry → drop (safe default). With eager caching, this case is effectively impossible for known properties — all sibling properties are cached on the first registered encounter. A cache miss means the subject was never seen while registered, which requires its first-ever change to happen during the microsecond unregistration window.
- **Recovery:** Structural mutation that re-adds X includes `ProcessSubjectComplete(X)` → value included
- **Result:** Transient delay, recovered by structural update

### Subject moved between properties

**Scenario:** Subject X moves from DictA to DictB within same apply.

- **Batch scope:** X stays in `_attachedSubjects` throughout
- **CQP:** X never becomes unregistered → filter passes normally with PathProvider
- **Factory:** Normal path, PathProvider checked
- **Result:** No value loss, no PathProvider bypass

### Concurrent apply + local mutation

**Scenario:** WebSocket apply and mutation engine operate simultaneously.

- **Apply lock:** Serializes concurrent applies to same root
- **Pre-resolve:** Caches subject references before structural processing
- **Batch scope:** Protects same-thread subject moves within apply
- **Mutation engine:** Operates independently (different thread, no lock)
- **CQP on server:** Captures mutation engine changes, cached filter preserves PathProvider decisions
- **Sent-state hash:** Not affected — hash is computed from update content, not live graph. Concurrent mutations change the live graph but not the sent state.
- **Result:** Concurrent operation safe, PathProvider enforced, no false-positive hash mismatches

### Concurrent structural mutations during broadcast

**Scenario:** Mutation engine performs structural mutations while CQP flush creates update and computes hash.

- **Previous approach (live graph hash):** Hash captures unflushed mutations not in the update → false positive mismatch → unnecessary reconnection
- **Sent-state hash:** Hash is computed from the update content, not the live graph. Unflushed mutations are invisible to the hash. Both server and client process the same update content into their `SentStructuralState` → hashes match.
- **Result:** No false positives regardless of concurrent mutation rate

### Client-originated structural mutations

**Scenario:** Client makes a structural mutation (e.g., UI adds a device) while receiving server updates.

- **During round-trip:** Neither server's nor client's `SentStructuralState` includes the mutation → hashes match
- **After server broadcasts:** Both sides update `SentStructuralState` from the same broadcast content → hashes match
- **Echo filtering:** Client echo-filters during apply, but `SentStructuralState` is updated from received update content regardless → no divergence
- **Result:** Correct hash comparison throughout, no special cases needed

### Idle system with prior divergence

**Scenario:** Last update caused undetected divergence, system goes idle.

- **Idle heartbeat:** Fires after 10s of no broadcasts
- **Hash:** Computed from `SentStructuralState` (same as per-update hash)
- **Client:** Compares structural hash → detects mismatch → reconnects
- **Result:** Divergence detected within 10s of idle

## Performance Considerations

- **Hash computation from sent-state dictionary:** O(all subjects) to walk the `SortedDictionary` and SHA256, but from plain string data — no interceptor chain, no `property.GetValue()`, no registry access. Significantly faster than the live graph walk in `StateHashComputer`.
- **Per-broadcast dictionary update:** O(changed subjects) to update `_subjectStructure` and diff children. No full graph walk per broadcast.
- **Heartbeat hash:** Same `ComputeHash()` method per connection — plain dictionary walk. No live graph access.
- **Memory overhead:** O(subjects) per connection for the `SortedDictionary` + children tracking + reference counts. With N connections, total memory is O(N * subjects). Trivial for HomeBlaze-scale graphs (hundreds to thousands of subjects, few connections).
- **No lock contention for hash:** Client no longer needs `GetApplyLock()` for hash computation. Hash is derived from update content, not live graph state.
- **Factory fallback path:** Only used during the microsecond unregistration window. Normal path (with full registry metadata) handles 99.99%+ of changes.

## Implementation Notes

### SentStructuralState lifecycle

- **Server:** Per-connection instance, owned by `WebSocketClientConnection`. Created when the connection is established. Initialized from the Welcome snapshot sent to that client (under `_applyUpdateLock`). Updated from each broadcast under `_applyUpdateLock`. Hash is computed per-connection under `_applyUpdateLock` and passed as a pre-computed dictionary to `BroadcastUpdateAsync` (which runs outside the lock) — each client gets its own hash in the broadcast. Discarded when the connection closes.
- **Client:** Per-connection instance, initialized from Welcome content, updated from every received update (including echo-filtered ones).
- **Shared class:** Same `SentStructuralState` implementation used by both server and client. Only the update source differs (created updates vs received updates).
- **Heartbeat:** Hash is also per-connection. The heartbeat loop computes hashes from each connection's `SentStructuralState` under `_applyUpdateLock` to avoid races with concurrent broadcasts. All `SentStructuralState` access — both `UpdateSentState` and `ComputeSentStateHash` — is serialized under this single lock.

### Backward compatibility

The `structuralHash` field in update messages is optional (nullable string). Clients that don't understand it ignore it. Servers that don't compute it send null. The idle heartbeat uses the existing heartbeat message format with the hash field added.
