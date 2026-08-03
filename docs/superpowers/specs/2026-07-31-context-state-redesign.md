# InterceptorSubjectContext copy-on-write state redesign

Replaces the targeted locking patch in PR #400. One file rewritten: `src/Namotion.Interceptor/InterceptorSubjectContext.cs`. Public API unchanged. This spec doubles as the implementation plan (single-task rewrite).

## Motivation

Six concurrency defects surfaced in this file. Five share one root cause: state spread across
five mutable fields plus derived values and caches that must be manually resynced under a lock.

1. Lock-order inversion deadlock (query walks down under `_lock`, invalidation walks up).
2. First-attach fast path permanently skipping invalidation.
3. Stale `_noServicesSingleFallbackContext` while `_lock` is held (the patch's own regression, 1658/3000 violations).
4. `_methodInvocationFunction = null` outside the lock, losing the invalidation to a concurrent chain build.
5. Cyclic fallback graphs (reachable via `ContextInheritanceHandler` back-references) plus `factory()` under `_lock` re-entering the same context (fired on 58 HomeBlaze tests).
6. Cache insert after `Clear()`: a compiled interceptor chain missing an interceptor, cached permanently. Silent loss of interception.

## Design

One immutable snapshot per context, published atomically. Readers take no locks.

```csharp
private sealed class ContextState
{
    internal readonly ImmutableArray<object> Services;
    internal readonly ImmutableArray<InterceptorSubjectContext> FallbackContexts;

    // Derived in the constructor from the two fields above, so no reader can ever
    // observe it disagreeing with them (defect 3 becomes unrepresentable).
    internal readonly InterceptorSubjectContext? DelegationTarget;
    // == Services.IsEmpty && FallbackContexts.Length == 1 ? FallbackContexts[0] : null

    // Caches belong to the state that produced them, created lazily via
    // Interlocked.CompareExchange on first use. A topology change publishes a NEW state,
    // so a late insert from a concurrent computation lands in the abandoned state and is
    // never read again (defect 6 becomes unrepresentable).
    private ConcurrentDictionary<Type, object>? _serviceCache;      // boxed ImmutableArray<T>
    private ConcurrentDictionary<Type, Delegate>? _readFunctions;
    private ConcurrentDictionary<Type, Delegate>? _writeFunctions;
    private Delegate? _methodInvocationFunction;                    // lazy via CAS, no lock

    internal ContextState WithoutCaches();   // same Services/FallbackContexts, null caches
}

private ContextState _state;                 // read via Volatile.Read (CS0420 avoidance),
                                             // written via Volatile.Write or Interlocked
private readonly object _mutationLock = new(); // serializes mutators; NEVER held on a query
```

No shared static empty state: caches live on the state object, so sharing one instance across
contexts would let their caches contaminate each other. Each context constructs its own initial
empty state (~72 B, replacing three eagerly allocated HashSets).

`_usedByContexts` stays a `HashSet<InterceptorSubjectContext>`, lazily created, guarded by the
existing static `UsedByContextsLock`. A CAS-swapped array would make N children registering on
one parent O(N^2). Lock order: `_mutationLock` -> `UsedByContextsLock`, never reverse;
`UsedByContextsLock` critical sections only touch the set (snapshot or add/remove), never call
into another context.

### Rules

**R1. Queries take no locks.** A query is one `Volatile.Read(ref _state)`, then a lock-free walk
reading other contexts' states the same way. The existing `[ThreadStatic]` visited sets still
terminate cycles. No locks on the query path means no lock cycle: defects 1 and 5 are structural
non-issues, including for cyclic fallback graphs.

**R2. Mutators publish with a volatile write under `_mutationLock`, no CAS loop.** The only
lock-free writer is invalidation, which never changes topology; a mutator's state carries fresh
caches, so overwriting a concurrent invalidation preserves its intent. Mutators re-read `_state`
inside the lock (after any `factory()` call) so reentrant mutations are not lost.

**R3. Invalidation is one unconditional CAS attempt, lock-free.**

```csharp
var current = Volatile.Read(ref _state);
Interlocked.CompareExchange(ref _state, current.WithoutCaches(), current);
```

No early-out when caches look absent: a reader may be lazily creating a cache concurrently, and
skipping would let its insert survive (defect 6 reborn). No retry on failure: every competing
write (mutator or other invalidator) also publishes cache-free state, so the intent is satisfied
either way. Publishing a new state IS self-invalidation, so there is no `Clear()` and no
null-out, which dissolves defect 4's ordering question.

**R4. Superset registration order.** `_usedByContexts` must always be a superset of the true
using set; extra entries only cost a spurious invalidation, missing entries cause permanent
staleness (defect 2's class). Therefore: `AddFallbackContext` registers `this` in the fallback's
`_usedByContexts` BEFORE publishing the new state; `RemoveFallbackContext` unregisters AFTER
publishing. Defect 2's conditional fast path is deleted entirely.

### Method algorithms

**GetServices\<T\>**: read state; if `DelegationTarget` != null, delegate (recursion, chains are
1-2 hops); if state is empty (no services, no fallbacks), return `ImmutableArray<T>.Empty`
without touching caches; else `TryGetValue` on the state's service cache, on miss compute from
THE SAME state snapshot (own Services filtered by type, plus fallbacks walked lock-free with the
visited set, `Distinct`, `ServiceOrderResolver.OrderByDependencies`), then
`GetOrAdd(key, value)` (the two-arg overload: canonicalizes racing computations, no closure
allocation; netstandard2.0-compatible).

Cache-fill invariant: every entry inserted into a state's cache is computed from that state's own
`Services`/`FallbackContexts` (child contexts read live, unavoidable and same as today).

**ExecuteInterceptedRead/Write**: read state; delegate if target set; else `TryGetValue` on the
state's read/write function cache; on miss build the chain from services resolved off the same
state snapshot, `TryAdd`, return. `EnsureInitialized` is deleted (the state carries its caches).

**ExecuteInterceptedInvoke**: same, with the single `Delegate` field lazily set via
`Interlocked.CompareExchange(ref state._methodInvocationFunction, computed, null)`; return the
canonical winner. The old `lock (_lock)` block is deleted.

**AddFallbackContext**: under `_mutationLock`: read state; if fallback already present return
false; register upward (R4); publish state with fallback appended; release lock; invalidate
using contexts (below). Returns true.

**RemoveFallbackContext**: under `_mutationLock`: read state; if absent return false; publish
state with fallback removed; unregister upward (R4); release lock; invalidate using contexts.

**TryAddService**: under `_mutationLock`: exists-check via the lock-free walk; if exists return
false; call `factory()` (may reenter this context; `Monitor` is reentrant, the inner mutation
publishes, and the outer re-read below picks it up); re-read `_state`; publish with the service
appended; release lock; invalidate using contexts. The exists-check runs before `factory()`;
a factory registering the same service type into the same context is its own responsibility.

**AddService**: same without the check and factory.

**HasFallbackContext**: lock-free read of `state.FallbackContexts`.

**Invalidate using contexts** (replaces `OnContextChanged`): with the `[ThreadStatic]` visited
set, mark self visited (self was invalidated by the publish itself), snapshot users under
`UsedByContextsLock`, release, then for each user: R3-invalidate it and recurse into its users.
The `Debug.Assert(!Monitor.IsEntered(_lock))` guards are deleted along with `_lock`: the walk
takes no mutation locks, so calling it anywhere is deadlock-free. Keep the existing 0/1/many
parent snapshot shapes to avoid array allocation in the common cases.

## What must NOT change

- Public and protected API surface (`PublicApi` snapshot tests must pass untouched).
- `InterceptorExecutor` (only subclass; overrides only public virtuals). Its non-atomic
  `RemoveFallbackContext` double-detach is pre-existing and filed separately, not fixed here.
- The `SubjectPathResolver` change from the patch stays (the self-registration was redundant),
  even though factory reentrancy is now safe.
- Tests from the patch pass UNCHANGED: `ContextLockingTests` (deadlock theory, 4 cases;
  `TryAddService` atomicity, 3000 iterations) and `FallbackContextAttachTests`. These are the
  evidence the redesign fixes what the patch fixed.

## New tests

Follow `When<Condition>_Then<ExpectedBehavior>` and Arrange/Act/Assert conventions, no hardcoded
waits.

1. `WhenServiceFactoryRegistersIntoSameContext_ThenNoDeadlockOccurs`: `TryAddService` whose
   factory calls `AddService` on the same context; both services resolvable afterwards.
2. `WhenFallbackGraphContainsCycle_ThenQueriesAndMutationsDoNotDeadlock`: A->B->A via
   `AddFallbackContext`, concurrent `GetServices` and `AddService` loops on both, joined with
   `AsyncTestHelpers.WaitUntilAsync`.
3. `WhenServicesAreAddedConcurrentlyWithQueries_ThenQuiescentStateSeesAllServices`: N writer
   tasks adding distinct services + M reader tasks querying in a loop; after joining, one final
   `GetServices` must contain all N (verifies no permanently poisoned cache, defect 6).

## Performance gates

`pwsh scripts/benchmark.ps1 -Stash` (branch vs master), same set as the patch baseline for
comparability: `AddLotsOfPreviousCars`, `ChangeAllTires`, `GetOrAddSubjectId`, controls `Write`,
`Read`, `WriteNoOp`, `WriteWithTimestampScope`, `DerivedAverage`, `IncrementDerivedAverage`,
`GenerateSubjectId`, `ReadParents`. Allocation columns are the authoritative signal; timings are
read against the control spread. Expected: hot path +1 dependent load per hop (noise-level),
allocations flat or improved (initial state ~72 B replaces ~200 B of eager HashSets; each
mutation allocates ~104 B transient). A regression beyond the control spread on the write/read
controls or a clear allocation regression on the attach benchmarks means fall back to the
already-verified patch (kept in git history).

## Constraints

- netstandard2.0: no 3-arg `GetOrAdd`, use `Volatile.Read`/`Write` + `Interlocked` on a
  non-volatile field (avoids CS0420 with warnings-as-errors).
- Comments: whys only, per repo convention. The four rules R1-R4 belong as comments on the
  code they govern (R4 on the two fallback mutators, R3 on Invalidate, R2 on the publish helper).
- No em dashes anywhere. No AI attribution in commits.
