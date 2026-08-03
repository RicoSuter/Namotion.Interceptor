# Path compression for delegation chains

Extension of PR #400 (branch `fix/context-locking`, base `e43cc945`, plus `dc05be3c` depth
benchmark and `e7bbd564` fuzz corpus extension). This spec assumes the copy-on-write design
already on the branch: one immutable `ContextState` per context, rules R1-R4, iterative
`CollectServices` and `InvalidateUsingContexts`.

Adversarially reviewed 2026-08-01: SOUND-WITH-CONDITIONS. One defect found and fixed here (the
restart livelock, see the slow path), two commit-2 rules that had to be stated because the obvious
implementation is the wrong one, and the write-back proof replaced by the stronger lemma L1 it
actually needs.

## Problem

A context with no own services and exactly one fallback context (a "delegating" context) resolves
everything through that fallback. `ContextInheritanceHandler` gives every attached child's executor
its parent's context as fallback, and executors register no services on themselves, so the default
topology is thousands of pure-proxy executors delegating toward one base context that holds all
services. Chain length equals subject-graph depth.

Today every intercepted read, write and method invocation re-walks the whole chain:
`ResolveDelegationTarget` takes one inline hop, `FollowDelegationChain` takes up to 8 unchecked
hops and then continues under Floyd cycle detection, which doubles the hop count. Cost per
operation is O(depth) dependent volatile loads. A suspected cycle triggers an exact re-walk plus a
two-pass edge confirmation, which has a known ABA residual (4 false positives in 37M
reflection-assisted direct walks; 0 in ~1.9B public-API calls).

## Load-bearing facts (all verified in source on the branch)

- F1. `PublishState` and `WithoutCaches` always allocate a fresh `ContextState`. No state object is
  ever installed twice, and a state object is only ever installed on the one context that created
  it. Therefore: **observing the same `ContextState` object in `context._state` at two points in
  time proves the field held it for the whole interval.** State object identity is ABA-free.
- F2. Delegation edges are fallback edges. `ContextState.DelegationTarget` is derived in the
  constructor as `services.IsEmpty && fallbackContexts.Length == 1 ? fallbackContexts[0] : null`.
- F3. Any topology mutation on any context publishes a fresh state for that context and the
  used-by walk (`InvalidateUsingContexts`) replaces the state of every context that transitively
  resolves through it (R4 superset invariant; publish is a full fence per `PublishState`).
- F4. `CollectServices` / `TryEnterContext` follow delegation from whatever state they are handed,
  so resolution completes correctly from any context on a chain, terminal or not.
- F5. The compiled-function caches fill on the *resolved* context's state
  (`resolved.GetWriteInterceptorFunction<TProperty>(state)`), so today thousands of proxies already
  share the one cache on the base context's state. This must be preserved.
- F6. `InterceptorExecutor.AddFallbackContext` runs attach callbacks (which call
  `context.GetServices<ILifecycleInterceptor>()` on the parent context) after the base mutation
  returns, outside `_mutationLock`.

## Design

### New cache slot on `ContextState`

```csharp
// null            = not resolved yet
// InterceptorSubjectContext = terminal of the chain starting at this state's DelegationTarget
// CyclicChainSentinel       = this state's chain was proven cyclic
private object? _resolvedTerminal;
```

Read with `Volatile.Read`, filled once by `Interlocked.CompareExchange(ref _resolvedTerminal, value,
null)`. Never replaced after fill: a stale value is discarded with the whole state (F3), not
repaired in place. Both outcomes are cacheable: a terminal, or the cyclic verdict so the second
query throws without re-walking.

The slot stores the terminal **context**, never the terminal's `ContextState` and never a compiled
function. Caching either of those on the delegating state is unsound: the invalidation walk visits
contexts in arbitrary order, so a reader can pin the already-invalidated upstream state, read the
not-yet-invalidated terminal state, and cache it; the upstream context is already in the walk's
`visited` set and is never invalidated again, so it would serve pre-mutation topology forever.
Storing the context forces a fresh `Volatile.Read(ref terminal._state)` on every query, which is
what guarantees freshness. That one read is the floor; it cannot be removed.

### Hot path (reads, writes, method invocations, `GetServices`)

```csharp
var state = Volatile.Read(ref _state);
var delegationTarget = state.DelegationTarget;
var resolved = this;
if (delegationTarget is not null)
{
    resolved = ResolveDelegationTarget(state, delegationTarget, out state);
}
var function = resolved.GetWriteInterceptorFunction<TProperty>(state);
```

`ResolveDelegationTarget` (aggressively inlined):

1. `var slot = state.ResolvedTerminal` (volatile read).
2. Cyclic sentinel: throw the delegation-cycle exception.
3. Terminal `t`: `state = Volatile.Read(ref t._state)`. If `state.DelegationTarget is null`,
   return `t`. Done: 2 loads past the pin, flat at any depth.
4. Slot null, or the cached terminal has itself started delegating (stale hint window, see
   invalidation): fall to the slow path.

The stale-hint check in step 3 is a routing optimization, not a correctness requirement: by F4,
handing a delegating state to the function-cache layer still resolves correct services through
`CollectServices`. The check exists so that compiled-function caches only ever fill on true
terminals (F5), up to a benign race window.

### Slow path: exact walk with write-back

Runs once per state, not once per query, so it can afford exactness. Reuses the two ThreadStatic
buffers (`visited` set, `path` list); the path list records **(context, pinned state object)**
pairs.

```
walk(entry, entryState):
  restart:
    visited.Clear(); path.Clear()
    current = entry
    // Re-pinned on every pass, NOT reused from the caller. Reusing the caller's pin livelocks:
    // if the entry's own edge changed after it was pinned, every pass replays the same stale
    // first edge, reaches the same repeat, fails the same confirmation and restarts, forever,
    // with no mutation in flight. One mutation before quiescence is enough to spin the walk
    // permanently. This is why the recursive version re-read the state at the top of each pass.
    currentState = Volatile.Read(current._state)
    if currentState.DelegationTarget is null: return (current, currentState)
    loop:
      // on every iteration but the first: currentState = Volatile.Read(current._state)
      slot = currentState.ResolvedTerminal
      if slot is cyclic sentinel: cache cyclic on all pinned path states; throw
      if slot is terminal t:
          tState = Volatile.Read(t._state)
          if tState.DelegationTarget is null:
              write-back t into all pinned path states; return (t, tState)
          // stale hint on an intermediate: ignore it, keep walking hop by hop
      if currentState.DelegationTarget is null:
          write-back current into all pinned path states; return (current, currentState)
      if not visited.Add(current):
          // revisit: suspected cycle, confirm by state identity
          for each (node, pinnedState) on the candidate loop:
              if Volatile.Read(node._state) is not ReferenceEquals pinnedState: goto restart
          cache cyclic sentinel on all pinned path states (loop and tail); throw
      path.Add(current, currentState)
      current = currentState.DelegationTarget
```

**Write-back invariant.** Write-back and verdict caching land only on the exact pinned state
objects recorded during the walk, never on a re-read of `node._state`. Soundness needs more than
F1, because the cached terminal depends on the whole downstream chain rather than on the node it
is stored at. It rests on this lemma:

> **L1. A state pinned before a mutation's publish never survives that mutation's invalidation
> walk.** For every context above the mutation, `InvalidateState` reads the current state once and
> issues one CAS. Either the CAS succeeds, replacing whatever was installed, or it fails, which by
> F1 means a strictly newer object was installed in between. Either way the field no longer holds
> any object pinned before the publish. (R4 guarantees the walk reaches every such context.)

So a fill can only land on a state that a reader can still pin if that state was installed *after*
the last publish affecting the chain. The terminal written into it was computed from reads
sequenced after that pin, so it reflects post-mutation topology. A fill onto any older pin lands
on an abandoned object that no reader will pin again. Fill-once CAS also means two racing walks
cannot fight: both terminals were valid at their own walk times, the first wins, and by F3 a
winner that later becomes invalid is discarded with its state.

The freshness of the downstream reads themselves rests on acquire loads plus per-location
coherence, with the publish fence supplying the one StoreLoad edge (`PublishState`). In the case
where no happens-before edge exists between the mutator and a stalled walk, staleness is excluded
by hardware ordering (on ARMv8 the read-from-external edge participates in the ordered-before
relation, closing the cycle) rather than by the documented .NET model, which does not state that
cumulativity. This is not new exposure: the service cache and compiled-function fills already on
the branch have the identical structure.

**Cycle confirmation is ABA-free by construction.** The old design compared delegation targets
across two passes, which a rolling sequence of rewirings could defeat (each edge re-verified at a
different time). The new confirmation compares `ContextState` object identity: by F1, identity
unchanged at confirmation time proves each edge existed continuously from its pin to the
confirmation, and since all pins precede all confirmations, all loop edges coexisted at the moment
the last pin was taken. A repeat under mutation without a real cycle necessarily replaced at least
one state object and restarts the walk. A real cycle has no edge to lose and confirms on the first
pass. This deletes `UncheckedDelegationHops`, `FollowDelegationChain` (Floyd),
`ResolveDelegationChainExactly`, and `DelegationLoopStillClosed`.

**Restart termination:** with the entry re-pinned on every pass, each restart requires some state
object on the candidate loop to have been replaced since it was pinned, which requires a publish
or an invalidation. Once those stop, one further pass either finds a terminal or confirms. Note
that a cache-only invalidation (`WithoutCaches`) also replaces the object and so also forces a
restart, where the old target comparison survived it; that costs one extra pass per invalidation
that lands mid-confirmation and is bounded by the same quiescence argument.

**Buffer hygiene:** the ThreadStatic `visited` and `path` buffers are cleared in a `finally`, as in
the code this replaces, so the throw path cannot leave them populated for the next walk on the
thread.

**Construction cost:** write-back onto every path node makes building and first-resolving an
N-deep chain O(N) total instead of O(N^2), and intermediate shortcut hits (step "slot is terminal
t") keep later walks from re-traversing compressed suffixes.

### Cached cyclic verdict staleness

A state carrying the cyclic sentinel throws without walking. If the cycle is broken (edge removed
or service added on a loop member), the mutating context publishes fresh state and by F3 the
used-by walk replaces the state of every context on and above the loop, discarding every cached
verdict. The window between the break and the walk completing can still throw from a pinned
pre-break state; that is the same transient class as a mid-walk race today and is invisible after
quiescence, which is the consistency level the library documents.

### Invalidation: nothing new

The compressed pointer depends only on the delegation edges of the chain, which is a subset of
what the service cache on the same state already depends on (the full reachable fallback graph).
Every mutation that could change a chain already invalidates every state holding a pointer into it
(F2 + F3). No new registration, no new walk, no new lock.

### Known asymmetry, kept deliberately

A delegation cycle throws when it is the queried context's own chain, and contributes nothing
silently when reached as a fallback of a collecting context (the `visited` set cuts it before any
detection runs). Pre-existing, identical in old and new code, and unifying either way would change
observable behavior of working graphs. Documented, not changed.

## Commit 2: compression in the service walk

`TryEnterContext` additionally consults the cached terminal of the state it pinned: a hit resolves
the entered chain in one hop instead of walking hop by hop (each hop currently does a
`visited.Add` plus a volatile state read).

Two rules, both required, neither of which is the obvious implementation:

- **The cyclic sentinel must NOT throw here.** It marks the entered context visited and returns
  `false`, exactly as the hop-by-hop walk does when it runs into an already visited context. The
  hot path throws on the sentinel; the collecting walk must not, or a graph that works today
  starts failing: a collecting context with fallbacks `[C1, S]` where `C1` sits on a pure cycle
  contributes nothing for `C1` today and resolves `S` normally. Throwing there would break the
  documented asymmetry below and the "aggregation and ordering unchanged" guarantee.
- **The jump target still goes through `visited.Add`.** Skipping it would frame an already visited
  terminal a second time in a diamond (collecting context with fallbacks `[A, Y]` where `A`'s
  chain and `Y`'s fallback both end at `T`), producing a second buffer region that only the
  parent's `ReduceFrame` deduplicates, which changes the reduce sequence. Skipped intermediates
  are deliberately not added: they are delegating, contribute nothing, and the hop-by-hop walk
  never framed them either.

Equivalence claim to be gated by the 3,200-graph differential ordering test: shortcutting changes
*when* intermediate chain nodes enter `visited`, but not the result. A delegating node contributes
no services of its own, and any other path reaching an intermediate later resolves to the same
terminal (delegation is deterministic, one edge per context), which is then already visited and
contributes nothing, exactly as if the intermediate had been cut directly.

## Commit 3: type-indexed function slots (benchmark-gated)

After compression, only terminal states fill compiled-function caches (F5), so the per-access
`ConcurrentDictionary<Type, Delegate>.TryGetValue` (Type hash, modulo, bucket walk, castclass) can
become a flat array lookup:

- A process-global dense index per property type: `static class PropertyTypeIndex<TProperty> {
  static readonly int Value = Interlocked.Increment(ref _next); }`.
- On `ContextState`: `Delegate?[]` grown by CAS-replacing the array with a larger copy; slot fill
  by CAS. A fill lost to a concurrent growth is benign: the function is recomputed and refilled,
  and today's code already lets a losing racer invoke its own equivalent chain instance once
  (`CreateReadInterceptorFunction` returns the locally built function, not the cache winner).

Memory: arrays exist only on the handful of terminal states, sized by the process-wide property
type count (tens). Proxies allocate nothing. Lands only if the depth benchmark measures a win over
commit 1.

## What does not change

Mutators, R1-R4, `CollectServices` aggregation and ordering, `InvalidateUsingContexts`, the
used-by leaf locking, the public API (PublicApi snapshots must stay byte-identical).

## Perf model

Per intercepted operation the resolution goes from O(depth) dependent loads (2+2N today, with
Floyd doubling past 8 hops) to a constant: pin, delegation target, slot, sentinel test, terminal
state, stale-hint test, then the unchanged function-cache lookup. The constant is not five loads,
it is a fixed handful plus the dictionary probe that commit 3 addresses; the claim that survives
is flat-versus-linear, not the exact count. Depth 1 neutral, depth >= 2 wins, non-delegating
contexts untouched. Per state: +8 bytes.

Measured baseline on this branch (`DelegationDepthBenchmark`, 3 property accesses per op, CPU
pinned to 3.6 GHz): Write 697.7 / 733.6 / 1,244.9 ns and Read 179.9 / 231.6 / 725.3 ns at depths
1 / 8 / 64, so roughly 8.7 ns per hop per op, and at depth 64 the walk is about three quarters of
the read. `RegistryBenchmark` guards the shallow shapes.

## Verification plan

| Harness | Role |
|---|---|
| 2,633 unit tests + PublicApi | unchanged, must pass |
| ContextLockingTests | unchanged (mutation side untouched) |
| ContextConcurrencyFuzzTests | corpus extension LANDED (`e7bbd564`): proxy contexts that never receive services, chained head to tail, tail open; the model predicts which contexts reject and asserts they raise; coverage assertions require 3+ hop chains and pure cycles to exist. Still to add: post-quiescence oracle that every reachable state's cached slot equals a fresh single-threaded walk (terminal or cyclic verdict). Deep sweep 1000/16/600 |
| ContextServiceWalkOrderTests | gate for commit 2 |
| ABA reflection harness | rerun against state-identity confirmation, required: 0 false positives |
| ContextDelegationCycleTests | adapt: drop the 8/9 hop-boundary rationale, add first-query vs cached-verdict vs cycle-formed-after-cache cases |
| ContextDeepGraphTests | unchanged, expected faster |
| Adversarial reviews | re-run on the query path (mutation-side results carry over) |
| Benchmarks | depth benchmark before/after per commit; final `benchmark.ps1 -Stash -LaunchCount 5`, CPU pinned |
