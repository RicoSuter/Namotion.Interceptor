# Reconcile Accommodation Reachability

Task 4 of the 2026-08-24 lifecycle callback contract plan. Decides whether the six released-parent early exits in `StructuralReconciler` are still reachable after Tasks 1 to 3 made every topology mutation from a lifecycle or property callback throw `LifecycleContractViolationException`. The argument is from reachability over the code, not from tests: two independent mutation runs already showed the suite stays green with all six exits neutralised, on this branch and on the pre-change parent, so green tests decide nothing here.

## Verdict

**A third-party write interceptor ordered downstream of the lifecycle can release the writing parent at callback depth zero, so the loops still observe `!IsOwned(parent)`: the accommodations stay.** Task 5 is cancelled, and the removal-loop residue defect becomes a live bug needing its own fix.

## The six exits

All six test the same predicate, `!graph.IsOwned(parent)`, where `parent` is the subject whose structural property is being reconciled:

| Exit | Location | Position |
|---|---|---|
| 1 | `src/Namotion.Interceptor.Tracking/Lifecycle/StructuralReconciler.cs:86` | keyed removal loop, after each `RemoveEdge` |
| 2 | `StructuralReconciler.cs:105` | keyed addition loop, after each `AttachEdge` |
| 3 | `StructuralReconciler.cs:111` | keyed, before the collection refresh |
| 4 | `StructuralReconciler.cs:170` | ordinal removal loop, after each `RemoveEdge` |
| 5 | `StructuralReconciler.cs:189` | ordinal addition loop, after each `AttachEdge` |
| 6 | `StructuralReconciler.cs:195` | ordinal, before the retained-index refresh |

The predicate tests a state, not a timing: the exits fire whether the parent was released mid-loop or before the loop was entered. `Reconcile` itself has no ownership check at entry (`StructuralReconciler.cs:22-60`), so a parent released after the caller's `IsOwned` check but before `Reconcile` runs the loops in the released state and only the exits stop them. That distinction decides candidate 3 below.

`IsOwned` flips to false in exactly one place: `OwnershipGraph.RemoveOwnership` (`OwnershipGraph.cs:73-76`), called only from `ReleaseTraversal.Release` (`ReleaseTraversal.cs:115`). `Release` is reached from `RemoveEdge` when a subject loses its last support (`ReleaseTraversal.cs:24-43`) and from `ReleaseRoot` on explicit detach (`ReleaseTraversal.cs:49-58`, called at `LifecycleInterceptor.cs:321`). So "release the writing parent" means exactly: cause one of those two calls with the parent in the released set.

## Paths that reach the reconcile loops

`Reconcile` (`StructuralReconciler.cs:22`) dispatches to `ReconcileKeyed` (`:48`) or `ReconcileOrdinal` (`:52`). It has exactly two production callers:

**Path 1: the structural write protocol.** Generated and dynamic structural setters enter `InterceptorExecutor.SetStructuralPropertyValue` (`src/Namotion.Interceptor/Interceptors/InterceptorExecutor.cs:207`), which takes the lifecycle gate (`:236`) and the attachment monitor (`:239`), then runs the write chain (`WriteStructuralValue`, `:262`). Inside the chain, `LifecycleInterceptor.WriteProperty` (`src/Namotion.Interceptor.Tracking/Lifecycle/LifecycleInterceptor.cs:104`) checks the callback guard (`:117`), checks `IsOwned(subject)` (`:130`), claims the proposed component (`:139`), calls `next` (`:141`), rereads the authoritative getter (`:145-146`), and calls `Reconcile` (`:146`). The compensation `ReleaseUnusedClaims` runs in the `finally` (`:152`).

**Path 2: property admission on an owned, seeded subject.** `InterceptorExecutor.AddProperties` (`InterceptorExecutor.cs:317`) routes to `LifecycleInterceptor.TryAddProperties` (`LifecycleInterceptor.cs:189`), which admits same-lifecycle reentry from inside a callback when the thread already holds the gate (`:197`) and calls `PropertyAdmission.Admit` (`:218`). `Admit` calls `Reconcile` at `src/Namotion.Interceptor.Tracking/Lifecycle/PropertyAdmission.cs:80`, always for a freshly published property, so `GetBaseline` returns null, the removal loops are empty, and only additions run.

No other production code calls `Reconcile`. The two seeding paths bypass it deliberately: `AttachTraversal.SeedAndAttachChildren` (`AttachTraversal.cs:24`) and `PropertyAdmission.AdmitUnowned` (`PropertyAdmission.cs:109`) call `AttachEdge` directly, and the comment at `PropertyAdmission.cs:139-142` records why: the exits read `IsOwned` on the writing parent, which is legitimately false in the claimed-not-yet-published state, so `Reconcile` would stop after the first occurrence there.

## What runs between the baseline commit and the end of the loop

The baseline commits at `StructuralReconciler.cs:43`. From there to the end of the loops, the following can execute user or third-party code:

1. Lifecycle handler fan-out, the subject events, and the collection refresh: all enter a callback scope first (`LifecycleNotifier.cs:27, 33, 52, 67, 82` all call `CallbackReentrancyGuard.EnterScope`). The attach descent (`LifecycleInterceptor.HandleLifecycleChange`, `LifecycleInterceptor.cs:244`, into `SeedChildrenIfNeeded`) runs inside the `InvokeAddedLifecycleHandlers` scope, so the user getters it evaluates through `OwnershipGraph.CollectStructuralChildren` (`OwnershipGraph.cs:173`) are inside a callback scope too.
2. Property attach and detach callbacks: enter a property callback scope first (`LifecycleInterceptorExtensions.cs:32, 52`). `DerivedPropertyChangeHandler.AttachProperty` evaluates user derived getters from there.
3. Code inside either scope that attempts a structural write throws at `LifecycleInterceptor.cs:117`; an explicit attach throws at `:259`; an explicit detach throws at `:297`; a cross-context `AddProperties` throws at `:197-204`. `ThrowIfInsideCallback` tests `IsInsideAnyCallback` (`CallbackReentrancyGuard.cs:57-69`), which covers both depths. So no callback can release the writing parent anymore; it can only throw or evaluate.
4. Non-callback user code invoked by the loops at callback depth zero: user `IDictionary` lookups in `IsHeldAt` (`StructuralReconciler.cs:124-128` via `SubjectLookup.FindSubjectInDictionary`, `src/Namotion.Interceptor.Tracking/SubjectLookup.cs:67-72`), user collection enumeration in every reachability validation (`ReleaseTraversal.cs:93-100` into `ReachabilityWalk.cs:70` into `OwnershipGraph.CommitsEdgeTo`, `OwnershipGraph.cs:124-128`, into `StructuralValueScanner.Contains`, `StructuralValueScanner.cs:127-174`, which enumerates arbitrary `IDictionary`, `ICollection` and `IEnumerable` implementations), and user `Equals` on dictionary-key index objects (`SubjectOwnership.RemoveIncoming`, `SubjectOwnership.cs:115, 135`). None of this is inside a guard scope.

## Can the loops release the parent by their own mechanics?

No. This was checked as a falsification attempt, including the cycle shapes, and it holds:

- The removal loops remove outgoing edges of the parent, and the cascade removes edges of subjects already determined anchor-unreachable. A simple path from an anchor to the parent visits the parent only once, at its end, so it uses no outgoing edge of the parent; and if any removed edge lay on such a path, its endpoints would be anchor-reachable via the path prefix, contradicting the unreachability that put them in the released set. By induction the parent stays anchor-reachable through the whole cascade, and `Release` visits only subjects that are not (`ReleaseTraversal.cs:36-42, 93-100`).
- The addition loops remove nothing. The only anchor mutation they reach is `ConsumeProvisionalAnchor` (`AttachTraversal.cs:132-145`), which clears an anchor only when the edge's parent is anchor-reachable excluding the anchored subject itself, so consumption never orphans what it consumes. `SetAnchor(None)` is reached only from explicit detach (`LifecycleInterceptor.cs:310`), which callbacks cannot call.
- The complete inventory of ownership and anchor mutations confirms there is no other writer: `RemoveOwnership` only at `ReleaseTraversal.cs:115`, `ReleaseClaim` at `ReleaseTraversal.cs:140`, `OwnershipGraph.cs:464, 487` and `LifecycleInterceptor.cs:315`, `SetAnchor` at `LifecycleInterceptor.cs:283, 310`, `ClearProvisionalAnchor` at `AttachTraversal.cs:142`.

So a mid-loop release of the writing parent requires code outside the loops' own mechanics: a callback (now throws) or depth-zero user code (candidates below).

## Candidate 1: reentrant same-lifecycle TryAddProperties

**Cannot release the writing parent.**

The reentry gate (`LifecycleInterceptor.cs:197`) admits the call when the thread already holds the gate, which is exactly the mid-reconcile case, and `Admit` runs. Everything `Admit` does either adds topology or throws:

- `CaptureStructuralValues` (`PropertyAdmission.cs:48`, method at `:182`) invokes user getters, but the thread is still inside the callback scope that initiated the reentrant call, so a structural write or explicit detach from a getter throws at `LifecycleInterceptor.cs:117` or `:297`. Evaluation alone releases nothing.
- `ClaimCapturedComponents` (`PropertyAdmission.cs:202`) discovers and claims unattached subjects; `DiscoverComponent` skips attached subjects entirely (`OwnershipGraph.cs:397-408`), and claiming only sets executor attachments. A lost claim race throws `InvalidOperationException` (`PropertyAdmission.cs:218-221`), which is the throw-unwind case, not a release.
- `registration.Publish` swaps metadata; `InvokePropertyAttachCallbacks` (`PropertyAdmission.cs:63`) runs inside property callback scopes.
- The `Reconcile` at `PropertyAdmission.cs:80` sees no old baseline (the property was just published), so its removal loop is empty and only `AttachEdge` runs, which adds edges and consumes provisional anchors under the independent-support condition, releasing nothing.
- The compensation in the `finally` (`PropertyAdmission.cs:88`) is candidate 2's shape and is answered below: it cannot touch an owned subject.

So the reentrant admission can add subjects and edges mid-loop, and it can throw mid-loop, but it cannot make `IsOwned(parent)` false.

## Candidate 2: ReleaseUnusedClaims compensation

**Cannot release the writing parent.**

Two timing shapes exist. On its own write, `ReleaseUnusedClaims` (`LifecycleInterceptor.cs:152`) runs in the `finally` after `Reconcile` has returned or thrown, so it never overlaps the loops of the write it compensates. On a throwing terminal, `next` throws at `:141` and `Reconcile` at `:146` never runs at all, so there is no mid-loop state to corrupt. On a suppressing terminal, the getter reread at `:145-146` returns the unchanged stored value, which is reference-equal to the baseline, and `Reconcile` returns at `StructuralReconciler.cs:25-28` before any loop.

The nested shape, a reentrant `Admit`'s compensation running while the outer loop is mid-flight (`PropertyAdmission.cs:88, 170`), cannot release the parent either, for two independent reasons:

1. The claimed list cannot contain the writing parent. `DiscoverComponent` adds only unattached subjects (`OwnershipGraph.cs:397-410`), and the writing parent is attached and owned, checked under the same gate at `LifecycleInterceptor.cs:130`.
2. `ReleaseUnusedClaims` (`OwnershipGraph.cs:481-490`) releases only subjects that are `!IsOwned && !IsAnchored`, and `ReleaseClaim` (`OwnershipGraph.cs:284-300`) mutates only the executor attachment. It removes no edges, runs no release traversal, and never touches the `_owned` map, so it cannot flip `IsOwned` for anything, let alone the parent.

## Candidate 3: a downstream third-party write interceptor

**Can release the writing parent at callback depth zero. This is the path that keeps the accommodations.**

The prior review's argument was that any callback-initiated write re-enters the chain at callback depth above zero. That is true of callback-initiated writes and is verified above, but it does not close this candidate, because a downstream write interceptor is not callback-initiated. It runs during `next` at `LifecycleInterceptor.cs:141`, at callback depth zero, on the writing thread, which holds the gate reentrantly.

The placement is real, not hypothetical. `LifecycleInterceptor` carries no `[RunsLast]`, the first-party interceptors pin themselves ahead of it with `[RunsBefore]` (`DerivedPropertyChangeHandler.cs:20`, `PropertyChangeInterceptor.cs:23`, `ValidationInterceptor.cs:14`, `SubjectTransactionInterceptor.cs:13`), and `ServiceOrderResolver` keeps registration order among unordered services (`src/Namotion.Interceptor/Ordering/ServiceOrderResolver.cs:158-181`, the sorted ready set), so a third-party `IWriteInterceptor` registered after `WithLifecycle` with no ordering attributes lands after the lifecycle in the chain. The codebase plans for exactly this: `OwnershipGraph.cs:475-479` documents that such an interceptor "can run downstream and suppress the continuation", and the exit comment at `StructuralReconciler.cs:88-90` names "a third-party write interceptor running downstream of the lifecycle" as a trigger.

From there the release is mechanical, with no pathological ingredient beyond the interceptor mutating topology:

1. The downstream interceptor performs an ordinary structural write on another owned subject, for example `Q.Child = null` where that edge is the last support of the writing parent `P`. The nested protocol re-enters the gate reentrantly (`InterceptorExecutor.cs:236`), passes the guard at `LifecycleInterceptor.cs:117` because the depth is zero, and its own `Reconcile` removes the edge; `IsStillHeld` fails for `P` (`ReleaseTraversal.cs:36`, count zero and no anchor, the provisional anchor having been consumed when `Q.Child = P` attached with `Q` independently supported), and `Release(P)` runs to completion, including `RemoveOwnership(P)` (`ReleaseTraversal.cs:115`) and `RemoveBaselines(P)` (`:116`). The same thread already holds `P`'s attachment monitor, and monitors are reentrant, so `ReleaseClaim(P)` succeeds.
2. Alternatively the interceptor calls `P.DetachFromContext(...)` (or on an ancestor): the guard at `LifecycleInterceptor.cs:297` passes at depth zero, and `ReleaseRoot` releases `P` directly (`:310-322`).
3. `next` returns, and the outer `WriteProperty` proceeds to `Reconcile` at `:146` with `IsOwned(P)` false. There is no ownership recheck between `:141` and `:146`, and none at `Reconcile` entry. `GetBaseline` returns null because `RemoveBaselines` dropped the entry, so the removal loops are empty, `SetBaseline` at `StructuralReconciler.cs:43` commits the new value for the released parent, and the first `AttachEdge` in the addition loop runs before exit 2 or exit 5 fires and stops everything after it.

Strictly, this interceptor does not execute mid-loop; it executes before the loops and the loops then run in the released-parent state. The exits do not distinguish the two, and without them the loops would attach every new occurrence and publish a collection refresh on behalf of a released owner. So the precise answer to the candidate's question is: a write interceptor at callback depth zero cannot interleave with the loops of the very write it sits under, but it can produce the exact state the exits test for, and the exits are the only thing that reacts to it.

Two adjacent depth-zero shapes reach the same state through the same window and are recorded for completeness. A hand-written or normalizing terminal setter is user code running as the chain terminal, inside `next`, at depth zero. And on dynamic subjects the authoritative getter reread at `LifecycleInterceptor.cs:145-146` invokes a user delegate at depth zero after `next` and immediately before `Reconcile`. Both can release the parent exactly like the downstream interceptor.

Finally, the mid-loop case proper is reachable only through the depth-zero user code the loops themselves invoke, listed in item 4 of the fan-out inventory: a side-effecting user dictionary indexer, collection enumerator, or index `Equals` could run the full write protocol reentrantly from inside a loop and release the parent genuinely mid-flight. This is labelled as speculation about pathological consumer code rather than an expected shape, but nothing in the guard or the contract prevents it, and it fires exits 1 and 4, which the before-entry shape cannot reach (its removal loops are empty because the baselines are gone).

## The throw-unwind note

A callback that throws instead of releasing unwinds through the loops with no rollback: the new baseline is already committed (`StructuralReconciler.cs:43`) naming every new occurrence, only a prefix of the removals and additions has been performed, and the `finally` at `LifecycleInterceptor.cs:149-154` hands back the claims of subjects whose `AttachEdge` never ran, leaving them unattached while the committed baseline still names them until the property's next write. That state is bad, but it is not a state the exits were silently covering: all six test `graph.IsOwned(parent)`, and a throw does not change ownership, so the exits never fired on the throw path and deleting or keeping them changes nothing about it. Confirmed by inspection of every exit site; the exception simply propagates through `Reconcile`'s `finally` (`StructuralReconciler.cs:55-59`), which only returns scratch lists.

## Consequences

- **Task 5 is cancelled.** The deletion was gated on "the state they compensate for cannot arise", and it can arise, at callback depth zero, through an extension point the codebase documents and accommodates elsewhere (`OwnershipGraph.cs:475-479`).
- **The surviving justification must replace the dead one in the exit comments and the design doc.** The callback justification is genuinely dead: Tasks 1 and 2 closed every callback path, and that half of the comment at `StructuralReconciler.cs:88-90` is now false. The downstream-interceptor half is the live half.
- **The removal-loop residue defect is a live bug.** A mid-loop release after the baseline commit makes the parent's release collect children from the already-committed new baseline, stranding the interrupted loop's unprocessed old occurrences. With callbacks closed this needs the pathological mid-loop shape, but the before-entry shape has its own residue that the exits bound without repairing: `SetBaseline` at `StructuralReconciler.cs:43` recreates a baseline entry for a released parent that nothing ever removes again, `CommitsEdgeTo` validates against it, and one child is attached with an edge from the dead owner before exit 2 or 5 fires. A fix (for example an ownership check at `Reconcile` entry before the baseline commit) is follow-up work outside this task, which changes no production code.
