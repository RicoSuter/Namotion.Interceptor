# Lifecycle Callback Contract: Design

Decides the contract scope of the `IPropertyLifecycleHandler` reentrancy exemption, and the failure mode for subjects that the lifecycle cannot track. Written after a five-reviewer pass on `ea1a81d0` reproduced four defects that all descend from one ambiguity.

## The problem

Two shipped documents state opposite contracts.

- Design decision 3 of the single-context spec: "a property getter must not write a subject-typed property. Violating it is a contract violation, not a supported shape, and it is detected by a `[Conditional("DEBUG")]` guard so Release pays nothing."
- `docs/design/tracking-lifecycle.md`: "Property lifecycle callbacks are exempt, deliberately: the derived-property handler evaluates user getters from its attach callback, and derived getters that write subject-typed properties are a supported shape."

Neither describes the code. There is no `[Conditional("DEBUG")]` guard anywhere in production. The exemption exists but works only at the top level of an attach, because `CallbackReentrancyGuard.ThrowIfInsideCallback` tests `_callbackDepth` while `EnterPropertyCallbackScope` increments a separate `_propertyCallbackDepth` that, by its own comment, feeds only `IsInsideAnyCallback`. The attach descent runs inside the notifier's callback scope, so a property callback below the first level throws where the documentation promises it will not.

That ambiguity produced four reproduced defects: the removal loop stranding occurrences permanently, `AttachTraversal.Publish` throwing out of an ordinary setter, explicit attach and detach bypassing the guard entirely, and the depth-dependent exemption itself.

## Decisions

**1. A property lifecycle callback may evaluate, and may not mutate topology.**

Evaluating a user getter from a property callback stays supported, which is what `DerivedPropertyChangeHandler` needs and the only production path that runs user code from a callback. Writing a subject-typed property, or an explicit attach or detach, from inside any lifecycle callback is a contract violation that throws in every build configuration. Property callbacks get no exemption, so the rule is uniform at every graph depth.

This is closer to today's real behaviour than the fuller contract would be: consumers below the first level already fail, silently.

**2. The guard becomes uniform, and covers attach and detach.**

`ThrowIfInsideCallback` tests `IsInsideAnyCallback` rather than `_callbackDepth` alone, which removes the depth dependence in one line. `LifecycleInterceptor.AttachSubjectToContext` and `DetachSubjectFromContext` gain the guard call they never had; a reproduction showed two threads, each holding its own lifecycle gate inside a callback and attaching into the other's context, deadlock permanently. There is one gate per lifecycle and no order among gates, so the callback contract is the only thing preventing a thread from holding two.

**3. Contract violations get their own type, and are the only thing the derived handler does not absorb.**

`DerivedPropertyChangeHandler` absorbs exceptions from derived getters in two places, and both stay. The absorption is deliberate resilience aimed at buggy or transiently failing getters, with a coherent recovery story: keep the last known value, recompute on the next dependency write. Removing it is a different change from settling this contract, and a blanket removal would not even have achieved the goal, because only the attach-time catch was in view. The second catch, on the dependency-driven recalculation path, runs far more often.

Instead, `LifecycleContractViolationException : InvalidOperationException` is thrown by the reentrancy guard and by the check in decision 4, and both catches filter it:

```csharp
catch (Exception exception) when (exception is not LifecycleContractViolationException)
```

A dedicated type is required rather than convenient: the guard previously threw a plain `InvalidOperationException`, which a filter cannot distinguish from a getter's own. Deriving from `InvalidOperationException` keeps existing consumer catches working.

This is also the only shape in which decision 4 functions at all. An orphan detected during recalculation would otherwise be swallowed by the second catch, on the hot path, which is exactly the silent failure the check exists to remove.

**4. A derived property that yields an untracked subject throws.**

Evaluating a `[Derived]` property whose declared type can contain subjects, where the result holds a subject not attached to this context, throws `LifecycleContractViolationException` and says why: derived properties do not establish ownership, so that subject will never be tracked.

This closes the quietest hole in the area. The pattern

```csharp
[Derived]
public Child Current => _child ??= new Child();
```

writes a private field, so no interceptor sees it and no guard fires, and because derived properties never establish ownership edges (`OwnershipGraph.IsStructural` requires `IsDerived: false`) the child is created and silently never attached. Master tracked such subjects; this branch stopped, and until now stopped silently.

Scope and limits:

- Cost is confined to derived properties whose declared type can contain subjects. The generator already classifies fail-closed, so scalar derived properties pay nothing. An `object`-declared derived property cannot be excluded by its declared type, so the check additionally short-circuits on the runtime type of the returned value: a string or boxed scalar result exits before any scan.
- The legitimate projection case still passes: a derived property returning a subject already attached through a stored property is untouched.
- The check is best-effort by construction. `[Derived]` reads are not intercepted, so a getter the library never evaluates is never checked. Attach-time evaluation catches the lazy-initialisation shape, which is the one that matters.

**5. Three accommodations are deleted, gated on a reachability proof.**

The six released-parent early exits in `StructuralReconciler`, and the released-subject guards that `AttachTraversal.Publish` and `ReleaseTraversal` would otherwise need, exist solely to survive a callback releasing the writing parent mid-operation. Under decision 1 that release throws at the attempt, so the state they compensate for cannot arise.

Deletion is not justified by the tests passing. Mutation testing showed all six exits can be removed with 911 of 911 tests green, so the suite cannot distinguish either way. The obligation is a reachability argument that tries to falsify itself: enumerate every path reaching the reconcile loops and show each is either guarded or unable to mutate topology. Two candidates must be checked explicitly, because both are permitted from inside a callback: reentrant same-lifecycle `TryAddProperties`, and `ReleaseUnusedClaims` compensation on a throwing or suppressing terminal. If either can release a parent mid-reconcile, that path decides the question and the accommodations are kept and fixed instead.

The inexact same-property fallback in `SubjectOwnership.RemoveIncoming` is treated separately: it also covers stored-index lag inside the reconcile window, which is unrelated to the exemption, so it stays until that second justification is independently retired.

**6. What this deliberately does not do.**

Topology mutation from a property callback stays unsupported. Supporting it means making the exemption depth-correct, adding released-subject guards to every traversal, and fixing the removal-loop residue, where the parent's release collects children from the already-committed new baseline and therefore cannot see the stranded old occurrences. That last piece touches the reconcile-order invariant the rest of the model rests on, and every future traversal would carry a standing obligation the compiler cannot enforce. A correct narrow contract is a better base for that work later than a half-built wide one.

Re-establishing ownership edges from derived properties is also out of scope, and is a separate design question about the derived model rather than about callbacks: a derived getter may return a different object per evaluation, so an edge from a projection can attach objects the next evaluation orphans.

Neither exclusion leaves a silent failure. Decision 1 throws on the mutation, decision 4 throws on the orphan.

One worry that motivated an earlier, wider version of decision 3 turned out not to apply, and is recorded so it is not re-derived: the generated context constructor is `public Car(IInterceptorSubjectContext context) : this()`, so the parameterless constructor runs to completion before `AttachToContext`, and attach cannot observe a half-built subject through that path.

The rule creates an asymmetry that consumers must be told about plainly: a side-effecting derived getter that writes a subject-typed property is now illegal when evaluated at attach, and stays legal when evaluated on recalculation, because only the attach path runs inside a lifecycle callback. `SideEffectPerson.Greeting` in the test suite is exactly this shape. That asymmetry is a consequence of the contract being about callbacks rather than about getters, and it is deliberate, but it is the kind of thing that reads as a bug when met in the wild.

## Consequences

Both documents that state the contract are rewritten to the single rule. `tracking-lifecycle.md` loses the paragraph justifying accommodations that no longer exist, and design decision 3 loses the `[Conditional("DEBUG")]` claim, which was never true of the tree.

One breaking change is added, and it is the most consumer-visible item in the surrounding pull request: a subject returned from a derived property and attached nowhere else used to be tracked on master, then silently stopped being tracked on this branch, and now throws.

## Testing

The contract needs tests that fail if it is loosened again, not tests that pass today:

- A structural write from a property attach callback throws at depth 0 and at depth 2. The depth dependence is the actual defect, so a single-level test would have passed against the broken code.
- An explicit attach, and a detach, from inside a lifecycle callback throw.
- The cross-lifecycle case: two lifecycles, a callback attaching into the other, fails fast rather than deadlocking. Bounded join, not an unbounded wait.
- A derived getter that mutates topology surfaces its exception rather than silently no-opping, which is the regression that removing the catch prevents.
- A derived property returning an unattached subject throws; one returning a subject attached through a stored property does not.
- If the accommodations are deleted, a test that fails when the contract is loosened, so the deletion cannot be quietly undone by re-adding an exemption.
