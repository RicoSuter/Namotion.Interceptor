# Fallback attachment ownership in InterceptorExecutor

Design for #402. Closes all five defects listed there, with no public API change and no follow-up issue.

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

This was verified, not assumed. Inverting the remove path so `base` decides first, which is what #402 originally proposed, fails four tests in `Namotion.Interceptor.Tracking.Tests`. `WhenRemovingInterceptors_ThenAllChildrenAreDetached` loses every detach event:

```
Verified (expected)                          Received (with the inversion)
[                                            [
  + NA (attached, refs: 0),                    + NA (attached, refs: 0),
  + Mother1.Mother -> Mother2 (attached),      + Mother1.Mother -> Mother2 (attached),
  + Mother2.Mother -> Mother3 (attached),      + Mother2.Mother -> Mother3 (attached)
  - Mother1.Mother -> Mother2 (detached),    ]
  - Mother2.Mother -> Mother3 (detached),
  - Mother1 (detached, refs: 0)
]
```

`SubjectRegistry` and `ContextInheritanceHandler` live on the parent context and become unreachable the moment the edge is gone, so nothing is detached and nothing is unregistered.

Any future change to these overrides has to preserve both orderings. They are pinned by tests 6 and 7 below.

Note the distinction that the rest of this design depends on: the *resolve* of `ILifecycleInterceptor` reads the **parent's** chain and does not need the edge, while the *callbacks* read **this** executor's chain and do. So the resolve can move earlier even though the callbacks cannot.

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
    // individual entry is default when that edge carries no record, either because it was
    // added through the plain mutator or because its record has already been taken.
    internal readonly ImmutableArray<ImmutableArray<ILifecycleInterceptor>> FallbackAttachments;
    ...
}
```

Invariant: `FallbackAttachments.IsDefault || FallbackAttachments.Length == FallbackContexts.Length`.

A parallel array rather than widening `FallbackContexts` into pairs, because that array is walked by `ComputeServices` and read by the `DelegationTarget` derivation, and doubling its stride would cost on paths this design has no business touching. The parallel array is read only by the two executor overrides.

`WithoutCaches()` must carry `FallbackAttachments` through unchanged. It is topology, not a cache.

### Base API

Three `private protected` members on `InterceptorSubjectContext`, so they stay off the public surface and remain reachable from `InterceptorExecutor` in the same assembly:

```csharp
// One publish carrying both the edge and its record. Returns false when the edge exists.
private protected bool TryAddFallbackContextWithAttachment(
    IInterceptorSubjectContext context,
    ImmutableArray<ILifecycleInterceptor> interceptors);

// Phase one of removal: takes the record, leaves the edge. Exactly one caller can win.
private protected bool TryTakeFallbackAttachment(
    IInterceptorSubjectContext context,
    out ImmutableArray<ILifecycleInterceptor> interceptors);

// Phase two: removes the edge. Re-derives the index, so a concurrent mutation between the
// phases is tolerated. No-op when the edge is already gone.
private protected void CompleteFallbackContextRemoval(IInterceptorSubjectContext context);
```

All three take `_mutationLock` and publish exactly as the existing mutators do, including the R4 `_usedByContexts` ordering and the trailing `InvalidateUsingContexts()`. The existing public `AddFallbackContext` and `RemoveFallbackContext` keep working unchanged for contexts that are not executors, appending and removing a default attachment entry.

### The overrides

```csharp
public override bool AddFallbackContext(IInterceptorSubjectContext context)
{
    // Resolves the parent's chain, so it does not need the edge and must not be inside the
    // publish. Empty and not default: a default ImmutableArray throws on .Length, and this
    // is the value a later remove reads back when the resolve below throws.
    var interceptors = ImmutableArray<ILifecycleInterceptor>.Empty;
    bool added;
    try
    {
        interceptors = context.GetServices<ILifecycleInterceptor>();
    }
    finally
    {
        // Recorded even on a throw, so the edge never becomes unremovable.
        added = TryAddFallbackContextWithAttachment(context, interceptors);
    }

    if (!added)
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

The `added` local exists because C# forbids returning from a `finally` (CS0157). The record has to be written in the `finally` so a throwing resolve still leaves a removable edge, but the duplicate-add result has to be acted on outside it. Nothing is swallowed: when the resolve throws, the record is written and the exception then propagates past the `if`.

### Why there is no window

**Add is a single publish.** The edge and its record appear together. There is no instant at which one exists without the other, so a concurrent remove either sees both and takes the record, or sees neither and returns `false`.

**Remove is two phases, and the gap between them is harmless.** Phase one drops the record and keeps the edge. Phase two drops the edge. In between, the edge exists with no record, and that is safe in both directions:

- a concurrent **remove** finds no record, returns `false`, and does not touch the edge, which the first remover is about to take out
- a concurrent **add** finds the edge present, so `TryAddFallbackContextWithAttachment` returns `false` and cannot slip a record in

The gap has to exist, because the callbacks must run with the edge intact and cannot run under `_mutationLock`. Placing the record take before it is what makes the gap benign.

### How each defect closes

| # | Defect | Closed by |
|---|---|---|
| 1 | Remove racing add undoes a live registration | Taking the record is a single arbiter under `_mutationLock`. A losing thread returns `false` and never reaches phase two, so it cannot remove an edge it does not own. |
| 2 | Double detach | Exactly one thread takes the record, so the callbacks run once by construction rather than by relying on `LifecycleInterceptor` being idempotent. |
| 3 | Cyclic chain blocks detach | The executor no longer resolves on the detach path, so nothing can throw before the removal is guaranteed. Phase two is in the `finally` and always runs. See the caveat below. |
| 4 | Half-attached subject on the add path | The record is written in the `finally`, so a throwing resolve still leaves a removable edge. |
| 5 | Retained subtree | Follows from 3: the edge and its reverse `_usedByContexts` entry always come out. |

### Exception contract on both paths

Neither `try` swallows. On the add path a throwing resolve propagates with the edge registered and the record empty, so the caller learns the attach did not complete and the edge is still removable. On the remove path a throwing callback propagates with the edge already removed, because phase two runs in the `finally`. Removal must never be blocked by a handler failure, which is the same principle #384 argues for elsewhere.

### Caveat on defect 3: removing a cyclic edge still throws

Removing the executor's own resolve is necessary but not sufficient. The recorded `LifecycleInterceptor.DetachSubjectFromContext` resolves its handlers through `subject.Context` (`LifecycleInterceptor.cs:70,73,195,278`), which is this executor, whose chain is the cycle. So the callback throws mid-loop, leaving `_attachedSubjects` and the reference counts partially updated.

What the design guarantees is that the edge comes out anyway, via the `finally`, so the graph is recoverable. What it does not do is make the call succeed cleanly. `RemoveFallbackContext` on a cyclic chain **throws to the caller with the edge removed**. That is strictly better than today, where it throws with the edge still in place and the subtree retained, but it must be stated on the override and asserted that way in test 3 rather than left to the implementer to guess.

### Accepted anomalies

**A remove and an add racing on the same edge can end with the add having returned `false` while the edge ends up absent.**

```
A: TryTakeFallbackAttachment -> wins, edge still present
C: AddFallbackContext -> the edge is present, so this returns false and does not attach
A: detach callbacks, CompleteFallbackContextRemoval -> edge gone
```

C read `false` as "already present" and the edge is gone. This is a legitimate linearization of two concurrent mutations, and unlike today's behaviour the topology and the lifecycle bookkeeping still agree afterwards.

**A detach callback that throws mid-loop loses the remaining callbacks permanently.** The record is already taken and the `finally` removes the edge, so there is nothing left to retry against. Today the edge survives a throw, so a caller can retry and the idempotent callbacks converge. This is the deliberate trade for guaranteeing removability.

Both are documented on the overrides rather than fixed.

### Reentrancy

These callbacks normally run with `lock (_attachedSubjects)` already held by `LifecycleInterceptor` further up the stack, and `Monitor` is reentrant, so a handler can call back into these overrides on the same thread. The relevant case is a handler that calls `RemoveFallbackContext` for the same context from inside an attach callback: the record is already published, so the take succeeds, the edge is removed, and the outer add then continues attaching the remaining interceptors against an edge that no longer exists.

This is not made worse by the design, and the analogous hole exists today. It is called out because the exactly-once argument is otherwise easy to misread as covering it: it covers concurrent threads, not a handler reentering on the same one. No guard is proposed. A handler that mutates the topology it is being notified about is outside the contract, which is the same position `LifecycleInterceptor` already documents for handlers writing the property being reconciled.

### Behaviour change: attach and detach become symmetric

Detach now notifies exactly the interceptors that were *resolved* at attach time. Today the set is resolved fresh at detach time, so an `ILifecycleInterceptor` registered on the parent after the attach receives a detach it never saw an attach for, and one unregistered in between misses its detach. The new pairing is balanced by construction. This is deliberate and is the defensible semantic, but it is a behaviour change and belongs in the release notes.

"Resolved at attach time" and not "notified at attach time": if an attach callback throws at index k, the interceptors after it never received an attach, yet a later remove still detaches all of them from the record. So the exception case continues to rely on consumer detach idempotency, which defect 2 correctly notes the contract does not require. Closing that would mean recording how far the attach loop got, which is #384's rollback problem and is out of scope.

### What remains, and why it is not a follow-up

In a pure delegation cycle every context on the loop resolves nothing, so the lifecycle handlers are unreachable from every route, not just from the one we happened to pick. The edge is still removed and the graph still recovers, but the registry bookkeeping for that subtree stays stale until it is rebuilt. Breaking the cycle first does not help: that is the inversion shown above, which leaves the executor with nothing to resolve.

The route that produces this shape in practice is the stranded-detach path tracked by #410, which is already open. A cycle built deliberately through the public API is a programming error, and "removable, recoverable, with stale registry entries until re-attach" is the right level of service for it. This gets documented on the overrides, not filed.

## Interaction with #400

The design adds a field to `ContextState`, which #400 landed a day earlier, so its invariants need checking rather than assuming.

- **"A `ContextState` is never installed twice"** holds. Every publish still constructs a fresh instance, including both removal phases. The cycle confirmation, which proves a loop existed at one instant from a state having been installed exactly once, is unaffected.
- **`WithoutCaches()` must keep allocating** and must copy `FallbackAttachments` forward. Returning `this` would break the invalidation CAS, which its own comment already warns about.
- **`DelegationTarget`** stays derived from `Services` and `FallbackContexts` only. Attachments never affect delegation collapse.
- **The resolution and invalidation paths never read `FallbackAttachments`**, so the hot path is untouched.
- **R4** (`_usedByContexts` is always a superset of the true using set) is preserved because the new mutators reuse the existing register-before-publish and unregister-after-publish ordering. Phase two performs the unregister, exactly as `RemoveFallbackContext` does today.

## Performance

The remove path loses two operations and gains one publish:

| | today | after |
|---|---|---|
| `HasFallbackContext` | `Volatile.Read` plus `ImmutableArray.Contains` scan | gone |
| `GetServices<ILifecycleInterceptor>` on detach | delegation target resolve plus cache lookup | gone |
| publishes | one | two |

The second publish is the cost of the two-phase removal. It allocates one `ContextState` and runs one extra `InvalidateUsingContexts` walk. Removal is a topology mutation, not a hot path, but on a large re-parenting this doubles the invalidation work, which is the one number worth watching.

The add path is neutral: the same resolve, the same single publish.

Memory: one reference field on `ContextState`, 8 bytes, plus one `ImmutableArray` of the same length as `FallbackContexts` for executors that have recorded attachments. For the dominant single-edge subject that is a 32 byte array. Non-executor contexts keep it default and pay only the 8 byte field. The stored interceptor `ImmutableArray` is the instance the parent context already caches, so it is a pointer copy and not a second allocation.

This must be measured, not assumed. `RegistryBenchmark` is the relevant one because it is attach heavy, and it should be read for both allocation and the extra invalidation walk. Treat a material regression as a gate on the design rather than something to tune afterwards.

## Testing

Regression tests for the defects:

1. `WhenSameFallbackIsRemovedConcurrently_ThenDetachCallbacksRunOnce` (defect 2)
2. `WhenRemoveRacesAdd_ThenTheAddIsNotUndone` (defect 1), asserting that the edge and the lifecycle bookkeeping agree afterwards
3. `WhenChainIsDelegationCycle_ThenTheEdgeIsRemovedAndTheCallThrows` (defect 3). Asserts both halves: the `InvalidOperationException` reaches the caller *and* the edge is gone afterwards. Asserting a clean `true` would be wrong, see the caveat above.
4. `WhenAttachResolveThrows_ThenTheEdgeRemainsRemovable` (defect 4)
5. `WhenCyclicSubtreeIsDetached_ThenItBecomesCollectable` (defect 5), by weak reference probe

Guards for the two forced orderings, so a future refactor cannot silently reintroduce the inversion:

6. `WhenFallbackIsAdded_ThenAttachCallbacksSeeTheEdge`
7. `WhenFallbackIsRemoved_ThenDetachCallbacksStillSeeTheEdge`

For the deliberate semantic change:

8. `WhenInterceptorIsRegisteredAfterAttach_ThenItIsNotNotifiedOnDetach`

For the state invariant, since a parallel array is the one place this design can silently rot:

9. `WhenFallbacksAreAddedAndRemoved_ThenAttachmentsStayIndexAlignedWithFallbacks`, driving an interleaving of adds and removes across several contexts and asserting the invariant through `ContextStateReflection`.

The existing `LifecycleInterceptorTests` snapshots stay untouched. If any of them move, the change is wrong.

Conventions per AGENTS.md: `When<Condition>_Then<ExpectedBehavior>`, explicit `// Arrange`, `// Act`, `// Assert`, and no hardcoded waits. Concurrency tests use `CountdownEvent` plus `ManualResetEventSlim` rendezvous and `AsyncTestHelpers.WaitUntilAsync`, matching `ContextFunctionCacheTests`.

Tests 1, 2 and 9 must be verified by mutation, not by passing once: break the arbiter and confirm they fail.

## Scope

Three files:

- `src/Namotion.Interceptor/InterceptorSubjectContext.cs`: the `FallbackAttachments` field on `ContextState`, `WithoutCaches` carrying it, and the three `private protected` members
- `src/Namotion.Interceptor/Interceptors/InterceptorExecutor.cs`: the two overrides
- one new test file

Not touched: `ContextInheritanceHandler.cs`, `LifecycleInterceptor.cs`.

No public API change expected. `InterceptorExecutor` is sealed, the two overrides already exist, `ContextState` is private, and `private protected` members are not part of the public surface. Confirm by running `VerifyChecksTests.PublicApi` rather than by assuming, since this is the one claim in this design that a build can settle outright.

`InterceptorSubjectContext.HasFallbackContext` loses its only production caller. Leave it: it is `protected` on a public unsealed class, so removing it would break a consumer subclass, and tests 3 and 7 use it.

Out of scope: #410 (stranded fallback edges on a partial detach), #207 (constructor context leak), #404 (factory under the mutation lock), #405 (invalidation walk recursion). None of them touch these files.
