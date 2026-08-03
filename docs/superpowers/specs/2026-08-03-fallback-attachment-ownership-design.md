# Fallback attachment ownership in InterceptorExecutor

Design for #402. Closes all five defects, with no public API change.

## Problem

`InterceptorExecutor` overrides both fallback mutators so that changing the topology also fires the lifecycle attach and detach callbacks. Both overrides re-derive at detach time what they already knew at attach time: they resolve `ILifecycleInterceptor` from the fallback context again, from a chain that may have changed or broken in between. Every defect in #402 follows from that.

```csharp
public override bool AddFallbackContext(IInterceptorSubjectContext context)
{
    var result = base.AddFallbackContext(context);          // commit
    if (result)
    {
        var array = context.GetServices<ILifecycleInterceptor>();   // resolve, can throw
        // AttachSubjectToContext for each
    }
    return result;
}

public override bool RemoveFallbackContext(IInterceptorSubjectContext context)
{
    if (HasFallbackContext(context))                        // check, lock free
    {
        var array = context.GetServices<ILifecycleInterceptor>();   // resolve, can throw
        // DetachSubjectFromContext for each
        return base.RemoveFallbackContext(context);         // commit
    }
    return false;
}
```

### Defect 1: a remove racing an add silently undoes a live registration

`RemoveFallbackContext` checks and commits non-atomically and runs the callbacks in the gap:

```
A: HasFallbackContext -> true      A is committed to detaching, has not yet
C: RemoveFallbackContext           C detaches and removes
C: AddFallbackContext              C adds and re-attaches
A: DetachSubjectFromContext        the subject is attached again, so this detaches for real
A: base.RemoveFallbackContext      removes the fallback C just added
```

C's registration is gone with no exception and no caller error. Idempotency does not help, because the re-attach in between makes A's detach a real one. Reachable wherever the same subject is mutated concurrently through `SubjectUpdateApplier`, `SubjectItemsUpdateApplier`, `OpcUaSubjectLoader`, `DynamicSubject` or `RootManager`.

### Defect 2: the double detach itself

Two threads can both pass `HasFallbackContext` and both run the detach callbacks. Benign today only because `LifecycleInterceptor` is the sole production `ILifecycleInterceptor` and its detach path happens to be idempotent under `lock (_attachedSubjects)`. Nothing in the contract requires that of a consumer implementation.

### Defect 3: a subject on a cyclic chain cannot be detached

The resolve runs before the removal. Since #400 a pure delegation cycle raises `InvalidOperationException` instead of overflowing the stack, so when the fallback's chain is such a cycle the resolve throws and `base.RemoveFallbackContext` is never reached. Detaching is the natural way to recover from a cycle and is exactly what does not work.

### Defect 4: the add path leaves a half-attached subject

The mirror. `base` commits, then the resolve throws, so the edge is registered, no attach callback ran, and the caller cannot tell how far it got.

### Defect 5: defect 3 also retains the subtree

`_usedByContexts` chains downward from the root, so a subject is collectable only once its fallback registration is actually removed. The aborted removal in defect 3 leaves the edge and the reverse entry in place, so the detached subtree stays reachable for the lifetime of the graph.

## Two orderings that are forced, not accidental

Both callbacks resolve their handlers through the *subject's own* context:

```
interceptor.Attach|DetachSubjectFromContext(_subject)
  -> LifecycleInterceptor uses subject.Context, which IS this executor
    -> Invoke{Added,Removed}LifecycleHandlers
      -> this.GetServices<ILifecycleHandler>()
```

A generated subject's executor has no services of its own, so that resolve returns nothing unless the fallback edge is in place. Therefore:

- **attach callbacks must run after the edge is committed**
- **detach callbacks must run before the edge is removed**

This was verified, not assumed. Inverting the remove path so `base` decides first, which is what #402 originally proposed, fails exactly four tests in `Namotion.Interceptor.Tracking.Tests`: `WhenRemovingInterceptors_ThenAllChildrenAreDetached`, `WhenRemovingInterceptors_ThenAllArrayChildrenAreDetached`, `WhenAssigningSubject_ThenAllSubjectsAreAttached` and `LifecycleEventsTests.SubjectAttached_FiresAfterHandler_And_SubjectDetaching_FiresBeforeHandler`. The first loses every detach event, because `SubjectRegistry` and `ContextInheritanceHandler` live on the parent context and become unreachable the moment the edge is gone.

Any future change to these overrides has to preserve both orderings. They are pinned by tests 6 and 7 below.

Note the distinction the rest of this design depends on: the *resolve* of `ILifecycleInterceptor` reads the **parent's** chain and does not need the edge, while the *callbacks* read **this** executor's chain and do. So the resolve can move earlier even though the callbacks cannot.

## Design

Stop re-deriving. Record what was attached, and make that record the sole ownership token for the edge.

The record lives in `ContextState`, alongside the fallback contexts, so an edge and its record move in a single interlocked publish. Keeping it in a separate field on the executor was tried and rejected: two independent atomic objects cannot transition together without a common serialization point, so every ordering leaves a window in which the edge exists with no record or the reverse, and either one strands the edge. `ContextState` is that common point, and it already exists.

### State

```csharp
private sealed class ContextState
{
    internal readonly ImmutableArray<object> Services;
    internal readonly ImmutableArray<InterceptorSubjectContext> FallbackContexts;

    // Parallel to FallbackContexts by index. Default when this context has no recorded
    // attachments at all, which is every context that is not an InterceptorExecutor. An
    // individual entry is default when that edge's record has already been taken by a
    // removal that has not yet completed its second phase.
    internal readonly ImmutableArray<ImmutableArray<ILifecycleInterceptor>> FallbackAttachments;
    ...
}
```

Invariant: `FallbackAttachments.IsDefault || FallbackAttachments.Length == FallbackContexts.Length`.

A parallel array rather than widening `FallbackContexts` into pairs, because that array is walked by `ComputeServices` and read by the `DelegationTarget` derivation, and doubling its stride would cost on paths this design has no business touching. The parallel array is read only by the two executor overrides.

### Every construction site must carry it

`ContextState` is constructed at **six** sites in `InterceptorSubjectContext.cs`, and all six must preserve `FallbackAttachments`:

| line | site | what it must do |
|---|---|---|
| 71 | field initializer | default |
| 141 | `AddFallbackContext` | append a default entry, or materialize when adding a record |
| 167 | `RemoveFallbackContext` | remove the entry at the same index |
| 205 | `TryAddService` | **carry unchanged** |
| 217 | `AddService` | **carry unchanged** |
| 1002 | `WithoutCaches` | **carry unchanged** |

The two service mutators are the dangerous ones. They rebuild the state as `new ContextState(state.Services.Add(service!), state.FallbackContexts)`, keeping the edges and silently wiping the records. A prototype built without those two carries fails all 8 seeds of `ContextConcurrencyFuzzTests` on the first round ("resolved 69 marker services but the final topology contains 40"), and adding them turns the whole solution green.

It is also reachable single-threaded, through the documented subtree-service feature that `ContextSubtreeServiceTests` covers:

```
parent(root) -> child; childContext.AddService<IWriteInterceptor>(...); parent.Child = null;
  without the carries:  child fallbacks after detach = [InterceptorExecutor#45658036]
  with the carries:     child fallbacks after detach = []
```

`RemoveFallbackContext` returns `false` and the edge is retained forever. That is defect 5 reintroduced by the fix for defect 5.

`WithoutCaches()` carrying it is equally load-bearing: attachments are topology, not a cache.

### Base API

Three `private protected` members on `InterceptorSubjectContext`, so they stay off the public surface and remain reachable from `InterceptorExecutor` in the same assembly:

```csharp
// One publish carrying both the edge and its record. Returns false when the edge exists.
private protected bool TryAddFallbackContextWithAttachment(
    IInterceptorSubjectContext context,
    ImmutableArray<ILifecycleInterceptor> interceptors);

// Phase one of removal: takes the record, leaves the edge. Exactly one caller can win.
// Returns false when the edge is absent OR its entry is already default, which on an
// executor means another thread took it and is between the phases.
private protected bool TryTakeFallbackAttachment(
    IInterceptorSubjectContext context,
    out ImmutableArray<ILifecycleInterceptor> interceptors);

// Phase two: removes the edge and its entry. Re-derives the index, so a concurrent
// mutation of a different edge between the phases is tolerated. No-op when already gone.
private protected void CompleteFallbackContextRemoval(IInterceptorSubjectContext context);
```

Contract details that are otherwise ambiguous enough for two implementers to diverge:

- **`TryTakeFallbackAttachment` returns `false` on a default entry**, and never returns `true` with a default `ImmutableArray` (whose `.Length` throws). On an executor a default entry can only mean "already taken", because the overrides always record, even an empty array. The taker completes phase two in its `finally`, so returning `false` to the second thread does not strand the edge. This reasoning depends entirely on all six sites carrying the array; if `TryAddService` wipes it, `false` here is what makes the edge permanently unremovable.
- **`CompleteFallbackContextRemoval` removes the attachment entry as well as the edge**, keeping the two arrays aligned.
- **The plain mutators must preserve `IsDefault`** rather than call `.Add(default)` on it, which throws on a default `ImmutableArray`. Only materialize when a real record arrives.

`TryAddFallbackContextWithAttachment` and `CompleteFallbackContextRemoval` take `_mutationLock`, publish, and run the trailing `InvalidateUsingContexts()` exactly as the existing mutators do, including the R4 `_usedByContexts` ordering. **`TryTakeFallbackAttachment` must not invalidate**, see Performance.

### The overrides

`InterceptorExecutor.cs` needs `using System.Collections.Immutable;`, which it does not have today.

```csharp
public override bool AddFallbackContext(IInterceptorSubjectContext context)
{
    // Preserves today's behaviour that a duplicate add neither resolves nor throws. Racy by
    // nature, and that is fine: a false negative costs one wasted resolve, because the
    // publish below arbitrates.
    if (HasFallbackContext(context))
    {
        return false;
    }

    // Reads the parent's chain, so it does not need the edge. A throw here leaves nothing
    // committed, which is what closes defect 4.
    var interceptors = context.GetServices<ILifecycleInterceptor>();

    if (!TryAddFallbackContextWithAttachment(context, interceptors))
    {
        return false;
    }

    for (var index = 0; index < interceptors.Length; index++)
    {
        interceptors[index].AttachSubjectToContext(_subject);
    }

    return true;
}

public override bool RemoveFallbackContext(IInterceptorSubjectContext context)
{
    // Taking the record is the arbiter. The edge is deliberately still in place, because the
    // callbacks below resolve their handlers through it.
    if (!TryTakeFallbackAttachment(context, out var interceptors))
    {
        return false;
    }

    try
    {
        for (var index = 0; index < interceptors.Length; index++)
        {
            interceptors[index].DetachSubjectFromContext(_subject);
        }
    }
    finally
    {
        CompleteFallbackContextRemoval(context);
    }

    return true;
}
```

An earlier revision wrapped the resolve in a `try` and committed from a `finally`, so the edge landed even when the resolve threw. That was circular: it committed the edge in order to keep the edge removable, when the removability problem only exists because it committed. Letting the exception skip the commit closes defect 4 and removes three hazards with it, namely the CS0157 dance around returning from a `finally`, a `finally` that could throw during exception propagation and mask the original, and the default-versus-empty `ImmutableArray` trap, since `interceptors` now only exists when the resolve succeeded.

### Why there is no window

**Add is a single publish.** The edge and its record appear together. There is no instant at which one exists without the other, so a concurrent remove either sees both and takes the record, or sees neither and returns `false`.

**Remove is two phases, and the gap between them is harmless.** Phase one drops the record and keeps the edge. Phase two drops the edge. In between, the edge exists with no record, and that is safe in both directions:

- a concurrent **remove** finds a default entry, returns `false`, and does not touch the edge, which the first remover is about to take out
- a concurrent **add** finds the edge present, so the publish returns `false` and cannot slip a record in

The gap has to exist, because the callbacks must run with the edge intact and they run outside `_mutationLock`. Placing the record take before it is what makes the gap benign.

### How each defect fares

| # | Defect | Outcome |
|---|---|---|
| 1 | Remove racing add undoes a live registration | **Closed.** Taking the record is a single arbiter under `_mutationLock`. A losing thread returns `false` and never reaches phase two, so it cannot remove an edge it does not own. |
| 2 | Double detach | **Closed.** Exactly one thread takes the record, so the callbacks run once by construction rather than by relying on `LifecycleInterceptor` being idempotent. |
| 3 | Cyclic chain blocks detach | **Closed** for removability. The executor no longer resolves on the detach path, so nothing can throw before the removal is guaranteed. See the caveat below on what still throws. |
| 4 | Half-attached subject on the add path | **Closed.** The resolve happens before the commit, so a throw leaves no edge and no attach. See below for the one shape that still commits, and why that is correct. |
| 5 | Retained subtree | **Closed.** Follows from 3: the edge and its reverse `_usedByContexts` entry always come out. |

### Defect 4, and the one shape that still commits

The resolve throws only when the chain it reads is a pure delegation cycle, or when a consumer's `Equals` or `GetHashCode` throws during the service walk's dedup. Two shapes reach it, and resolving before the commit separates them:

| shape | today | after |
|---|---|---|
| the parent's chain is **already** cyclic | edge committed, resolve throws, nothing attached | resolve throws first, **nothing committed** |
| the add itself **closes** the cycle | edge committed, resolve throws, nothing attached | resolve succeeds, edge committed, the *callbacks* throw |

The first is defect 4 and it is now an atomic failure. So is the unrelated-exception case, which today also leaves a half-attached subject.

The second is not defect 4. Resolving first means the parent is not yet on a loop, so the resolve succeeds and the commit is what closes the circle; the callbacks then throw because *this* context is now on it. The caller asked for an edge and got one, and what failed was notifying handlers that the caller's own topology made unreachable. That is the same caveat the detach side carries, and committing is the correct outcome because the topology is exactly what was requested.

`ContextConcurrencyFuzzTests` needs a small change for this. It records `edge.IsPresent = true` **before** the call (`:519-527`), with a comment explaining that the executor registers the edge and only then resolves. That comment documents the current implementation rather than requiring it. The model stays correct for the cycle-closing shape and becomes wrong for the already-cyclic shape, so the fuzzer must mark the edge absent when the call throws.

### Exception contract on both paths

Neither path swallows. On the add path a throwing resolve propagates with nothing committed. On the remove path a throwing callback propagates with the edge already removed, because phase two runs in the `finally`. Removal must never be blocked by a handler failure, which is the same principle #384 argues for elsewhere.

The asymmetry is deliberate: an add that cannot complete should leave no trace, while a remove that cannot complete must still remove, because a blocked removal is what strands edges and retains subtrees.

### Caveat on defect 3: removing a cyclic edge still throws

Removing the executor's own resolve is necessary but not sufficient. The recorded `LifecycleInterceptor.DetachSubjectFromContext` resolves its handlers through `subject.Context` (`LifecycleInterceptor.cs:70,73,195,278`), which is this executor, whose chain is the cycle. So the callback throws mid-loop, leaving `_attachedSubjects` and the reference counts partially updated.

Verified on a pure two-executor cycle: `RemoveFallbackContext` raises `InvalidOperationException("...delegation cycle...")` to the caller **and** the fallback array is `[]` afterwards. That is strictly better than today, where it throws with the edge still in place and the subtree retained, but it must be stated on the override and asserted that way in test 3.

### Accepted anomalies

All are documented on the overrides rather than fixed.

**An add can return `false` while the edge ends up absent.** A takes the record, C's add sees the edge still present and returns `false` without attaching, then A completes phase two. C read `false` as "already present" and the edge is gone. A legitimate linearization of two concurrent mutations.

**A remove and an add on one caller can return two contradictory `false`s.** During A's gap, B's remove returns `false` (the edge *was* present) and B's follow-up add also returns `false` (the edge is *still* present), and the edge ends absent.

**Topology and bookkeeping can still disagree in one interleaving.** C's add publishes and C is still inside its attach loop when A takes the record and completes phase two. The subject ends up in `_attachedSubjects` with an incremented reference count and no edge. This is narrower than today's defect 1, which loses an established registration, but the claim "topology and bookkeeping always agree afterwards" would be false and is not made.

**A detach callback that throws mid-loop loses the remaining callbacks permanently.** The record is already taken and the `finally` removes the edge, so there is nothing to retry against. The deliberate trade for guaranteeing removability.

### Reentrancy

These callbacks normally run with `lock (_attachedSubjects)` held by `LifecycleInterceptor` further up the stack, and `Monitor` is reentrant, so a handler can call back into these overrides on the same thread. Two same-thread holes, neither made worse by this design and both present today:

- a handler calling `RemoveFallbackContext` for the same context from inside an **attach** callback: the record is already published, the take succeeds, the edge goes, and the outer add keeps attaching against an edge that no longer exists
- a handler calling `AddFallbackContext` from inside a **detach** callback: it gets `false` because the edge is still present, and is then wiped by the outer `finally`

No guard is proposed. A handler that mutates the topology it is being notified about is outside the contract, the same position `LifecycleInterceptor` already documents for handlers writing the property being reconciled.

Note that "the callbacks run outside `_mutationLock`" is true of these paths but is not a global invariant: `TryAddService` invokes `exists` and `factory` under the lock, and `ContextConcurrencyTests:180-222` exercises that reentrancy. It is not relied on here.

### Behaviour change: attach and detach become symmetric

Detach now notifies exactly the interceptors that were *resolved* at attach time. Today the set is resolved fresh at detach time, so an `ILifecycleInterceptor` registered on the parent after the attach receives a detach it never saw an attach for, and one unregistered in between misses its detach. The new pairing is balanced by construction. Deliberate, defensible, and it belongs in the release notes.

"Resolved at attach time" and not "notified at attach time": if an attach callback throws at index k, the interceptors after it never received an attach, yet a later remove still detaches all of them from the record. So the exception case continues to rely on consumer detach idempotency, which defect 2 correctly notes the contract does not require. Closing that is #384's rollback problem.

### What remains, and why it is not a follow-up

In a pure delegation cycle every context on the loop resolves nothing, so the lifecycle handlers are unreachable from every route, not just from the one we happened to pick. The edge is still removed and the graph still recovers, but the registry bookkeeping for that subtree stays stale until it is rebuilt. Breaking the cycle first does not help: that is the inversion shown above, which leaves the executor with nothing to resolve.

The route that produces this shape in practice is the stranded-detach path tracked by #410, which is already open. A cycle built deliberately through the public API is a programming error, and "removable, recoverable, with stale registry entries until re-attach" is the right level of service for it. Documented on the overrides, not filed.

## Interaction with #400

The design adds a field to `ContextState`, which #400 landed a day earlier, so its invariants need checking rather than assuming.

- **"A `ContextState` is never installed twice"** holds. Every publish still constructs a fresh instance, including both removal phases. The cycle confirmation is unaffected.
- **`WithoutCaches()` must keep allocating** and must copy `FallbackAttachments` forward.
- **`DelegationTarget`** stays derived from `Services` and `FallbackContexts` only. Attachments never affect delegation collapse.
- **The resolution and invalidation paths never read `FallbackAttachments`**, so the hot path is untouched.
- **R4** (`_usedByContexts` is always a superset of the true using set) is preserved: phase two performs the unregister after the publish, exactly as `RemoveFallbackContext` does today. Phase one changes no edges, so it has no R4 obligation.

## Performance

**Phase one must not call `InvalidateUsingContexts`.** It changes no topology, only the record, and no resolution path reads the record, so the upward walk is pure waste. Worse, it walks the entire upward cone while the subtree is still fully connected and destroys the caches immediately before the detach issues one `GetServices<ILifecycleHandler>()` per subject through them (`LifecycleInterceptor.cs:278`). Today's single publish happens *after* the callbacks, when the subtree is already progressively disconnected.

Measured on a prototype, Release, a chain of N subjects, attach then detach the root edge, best of 3 by 20 iterations:

| depth | detach today | with phase-one invalidation | without it |
|---|---|---|---|
| 50 | 1 ms | 5 ms | |
| 100 | 3 ms | 17 ms | |
| 200 | 7 ms | 63 ms | |
| 400 | 13 ms | 128 ms | 28 ms |

So the cost is superlinear in depth if phase one invalidates, and roughly 2x if it does not. The residual 2x is phase one publishing a fresh state and therefore discarding this context's own caches, which is inherent to taking the record atomically.

This matters more than it first appears: `ContextInheritanceHandler.cs:25` calls `RemoveFallbackContext` once per subject during a subtree detach, so the factor applies per node, not per user-initiated call.

The add path pays one `HasFallbackContext` guard and is otherwise unchanged. Without that guard a duplicate add would newly resolve, which both costs a delegation resolve where it used to cost an `ImmutableArray.Contains` scan, and can newly throw when the parent's chain is cyclic. `SubjectUpdateApplier.cs:145`, `SubjectItemsUpdateApplier.cs:229` and `OpcUaSubjectLoader.cs:280` call this per item, so the guard is not optional.

Memory: one reference field per **`ContextState`**, not per context, and a `ContextState` is allocated on every mutation and every invalidation. Plus one `ImmutableArray` the length of `FallbackContexts` for executors that have records, 32 bytes for the dominant single-edge case. Non-executor contexts keep it default.

Confirm on `RegistryBenchmark`, reading both allocation and the detach path.

## Testing

Regression tests for the defects:

1. `WhenSameFallbackIsRemovedConcurrently_ThenDetachCallbacksRunOnce` (defect 2)
2. `WhenRemoveRacesAdd_ThenTheAddIsNotUndone` (defect 1)
3. `WhenChainIsDelegationCycle_ThenTheEdgeIsRemovedAndTheCallThrows` (defect 3). Asserts both halves: the `InvalidOperationException` reaches the caller *and* the edge is gone afterwards.
4. `WhenAttachResolveThrows_ThenNoEdgeIsRegistered` (defect 4). Its companion pins the shape that still commits: `WhenAddClosesADelegationCycle_ThenTheEdgeIsRegisteredAndTheCallThrows`
5. `WhenCyclicSubtreeIsDetached_ThenItBecomesCollectable` (defect 5), by weak reference probe

Guards for the two forced orderings:

6. `WhenFallbackIsAdded_ThenAttachCallbacksSeeTheEdge`
7. `WhenFallbackIsRemoved_ThenDetachCallbacksStillSeeTheEdge`

For the deliberate semantic change:

8. `WhenInterceptorIsRegisteredAfterAttach_ThenItIsNotNotifiedOnDetach`

For the state, and this one needs care:

9. `WhenServicesAreAddedAfterAFallback_ThenItsAttachmentSurvives`. It must assert that a **record still exists for every edge**, not merely that the two arrays are the same length. The `TryAddService` and `AddService` failure mode wipes the array to `default`, which *satisfies* a length invariant, so a length assertion is blind to the exact bug it exists to catch. Drive it through `AddService` and `TryAddService` on a context that already has a fallback, then assert the edge is still removable.

The existing `LifecycleInterceptorTests` snapshots stay untouched. If any of them move, the change is wrong. `ContextConcurrencyFuzzTests` and `ContextSubtreeServiceTests` are the two suites that catch the carry bug, so both must be run.

Conventions per AGENTS.md: `When<Condition>_Then<ExpectedBehavior>`, explicit `// Arrange`, `// Act`, `// Assert`, no hardcoded waits. Concurrency tests use `CountdownEvent` plus `ManualResetEventSlim` rendezvous and `AsyncTestHelpers.WaitUntilAsync`, matching `ContextFunctionCacheTests`.

Tests 1, 2 and 9 must be verified by mutation, not by passing once: break the arbiter, drop a carry, and confirm they fail.

## Scope

Five files:

- `src/Namotion.Interceptor/InterceptorSubjectContext.cs`: the `FallbackAttachments` field, all six construction sites carrying it, and the three `private protected` members
- `src/Namotion.Interceptor/Interceptors/InterceptorExecutor.cs`: the two overrides and the missing `using`
- `src/Namotion.Interceptor.Tests/Context/ContextConcurrencyFuzzTests.cs`: the edge model must mark an edge absent when a throwing add left nothing committed, instead of recording it present before the call
- `src/Namotion.Interceptor.Tests/Context/ContextStateReflection.cs`: it exposes `_state`, `_resolvedTerminal`, the function arrays and the marker, but not `FallbackContexts` or `FallbackAttachments`, which test 9 needs
- one new test file, which can live in `Namotion.Interceptor.Tests` since it reaches Tracking transitively through `Namotion.Interceptor.Testing`

Not touched: `ContextInheritanceHandler.cs`, `LifecycleInterceptor.cs`.

No public API change expected: `InterceptorExecutor` is sealed, the overrides exist, `ContextState` is private, and `private protected` members are not public surface. A prototype confirmed all six `VerifyChecksTests.PublicApi` snapshots pass unchanged, but re-confirm rather than assume.

`InterceptorSubjectContext.HasFallbackContext` keeps its caller, as the guard on the add path. Note for anyone tempted to remove it later: it cannot be called by a consumer subclass, because the only constructor is `private protected` and deliberately so, and it cannot be called by tests without reflection for the same reason.

Out of scope: #410 (stranded fallback edges on a partial detach), #207 (constructor context leak), #404 (factory under the mutation lock), #405 (invalidation walk recursion).
