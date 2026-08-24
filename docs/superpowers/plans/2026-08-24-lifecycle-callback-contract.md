# Lifecycle Callback Contract Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the lifecycle callback contract uniform and enforced, so that mutating topology from a callback and exposing an untrackable subject both fail fast instead of corrupting or silently doing nothing.

**Architecture:** One rule replaces a depth-dependent exemption: a lifecycle callback may evaluate anything and may mutate no topology. Enforcement moves into `CallbackReentrancyGuard` (uniform depth), the two explicit attach and detach entry points on `LifecycleInterceptor`, and a new check in `DerivedPropertyChangeHandler` for subjects a derived property exposes but nothing owns. Because the contract makes a mid-operation release impossible, three accommodations built to survive it become deletable, gated on a reachability proof rather than on green tests.

**Tech Stack:** C# 13 preview, .NET 9 for extensions, xUnit, `PublicApiGenerator` plus `Verify` for public API snapshots.

**Spec:** `docs/superpowers/specs/2026-08-24-lifecycle-callback-contract-design.md`

**Already landed, do not redo:** `LifecycleContractViolationException` and the filtered catches (`ebbed3eb`); the `AreBaselinesSeeded` guard in `PropertyAdmission.Admit` (`5d548827`).

---

## File Structure

| File | Responsibility | Change |
|---|---|---|
| `src/Namotion.Interceptor.Tracking/Lifecycle/CallbackReentrancyGuard.cs` | thread-local callback depth, contract check | modify: uniform depth |
| `src/Namotion.Interceptor.Tracking/Lifecycle/LifecycleInterceptor.cs` | write protocol, gate, attach and detach entry points | modify: guard calls at `AttachSubjectToContext`, `DetachSubjectFromContext` |
| `src/Namotion.Interceptor.Tracking/Change/DerivedPropertyChangeHandler.cs` | derived evaluation and change detection | modify: untracked-subject check |
| `src/Namotion.Interceptor.Tracking/Lifecycle/StructuralValueScanner.cs` | the one interpretation of "which subjects does this value hold" | reuse, no change |
| `src/Namotion.Interceptor.Tracking/Lifecycle/StructuralReconciler.cs` | write-time diff and reconcile | modify in Task 6, conditional |
| `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/CallbackContractTests.cs` | every contract test in one place | create |
| `docs/design/tracking-lifecycle.md` | shipped internal design doc | modify: callback contract section |
| `docs/superpowers/specs/2026-08-21-single-context-lifecycle-simplification-design.md` | the superseded decision 3 wording | modify |

---

## Task 1: Make the guard uniform at every graph depth

The exemption currently works only at the top level of an attach, because the descent runs inside the notifier's callback scope. A single-level test passes against the broken code, so the depth-2 case is the one that matters.

**Files:**
- Create: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/CallbackContractTests.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/CallbackReentrancyGuard.cs`

- [ ] **Step 1: Write the failing tests**

Create `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/CallbackContractTests.cs`:

```csharp
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

public class CallbackContractTests
{
    private static IInterceptorSubjectContext CreateContext()
    {
        return InterceptorSubjectContext
            .Create()
            .WithLifecycle();
    }

    [Fact]
    public void WhenAPropertyCallbackWritesStructuralPropertyAtTopLevel_ThenItThrows()
    {
        // Arrange
        Exception? callbackException = null;
        Person? stranger = null;
        var handler = new DelegatePropertyLifecycleHandler(change =>
        {
            if (callbackException is not null)
            {
                return;
            }

            callbackException = Record.Exception(() => stranger!.Father = new Person());
        });

        var context = CreateContext().WithService(() => handler, _ => false);
        stranger = new Person(context) { FirstName = "S" };

        // Act
        var root = new Person(context) { FirstName = "R" };

        // Assert
        Assert.IsType<LifecycleContractViolationException>(callbackException);
        Assert.NotNull(root);
    }

    [Fact]
    public void WhenAPropertyCallbackWritesStructuralPropertyBelowTheFirstLevel_ThenItThrows()
    {
        // Arrange: three levels, so the callback for the deepest subject runs inside the
        // descent's own callback scope. This is the case a single-level test cannot see.
        Exception? deepException = null;
        Person? stranger = null;
        var handler = new DelegatePropertyLifecycleHandler(change =>
        {
            if (change.Subject is not Person { FirstName: "leaf" })
            {
                return;
            }

            deepException = Record.Exception(() => stranger!.Father = new Person());
        });

        var context = CreateContext().WithService(() => handler, _ => false);
        stranger = new Person(context) { FirstName = "S" };

        var top = new Person(context) { FirstName = "top" };
        var mid = new Person { FirstName = "mid" };
        var leaf = new Person { FirstName = "leaf" };
        mid.Father = leaf;

        // Act
        top.Father = mid;

        // Assert
        Assert.IsType<LifecycleContractViolationException>(deepException);
    }
}
```

Create `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/DelegatePropertyLifecycleHandler.cs`:

```csharp
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

internal sealed class DelegatePropertyLifecycleHandler(Action<SubjectPropertyLifecycleChange> onAttach)
    : IPropertyLifecycleHandler
{
    public void AttachProperty(SubjectPropertyLifecycleChange change) => onAttach(change);

    public void DetachProperty(SubjectPropertyLifecycleChange change)
    {
    }
}
```

- [ ] **Step 2: Run the tests to verify the depth-2 case fails**

Run: `dotnet test src/Namotion.Interceptor.Tracking.Tests --filter "FullyQualifiedName~CallbackContractTests"`

Expected: `WhenAPropertyCallbackWritesStructuralPropertyAtTopLevel_ThenItThrows` FAILS (no exception is thrown, because the exemption applies at depth 0). `WhenAPropertyCallbackWritesStructuralPropertyBelowTheFirstLevel_ThenItThrows` PASSES already, because the exemption is silently revoked below the first level. That asymmetry is the bug, and seeing it before the fix is the point of this step.

- [ ] **Step 3: Make the check uniform**

In `src/Namotion.Interceptor.Tracking/Lifecycle/CallbackReentrancyGuard.cs`, change the condition in `ThrowIfInsideCallback` from `_callbackDepth > 0` to `IsInsideAnyCallback`:

```csharp
    /// <summary>Called on entry of the lifecycle's structural write protocol.</summary>
    public static void ThrowIfInsideCallback()
    {
        if (IsInsideAnyCallback)
        {
            throw new LifecycleContractViolationException(
                "A lifecycle callback must not write a structural (subject-typed) property. The " +
                "callback runs while the lifecycle holds its topology gate mid-reconcile, so the " +
                "write would re-enter the reconciler on half-updated edge state. Defer the write " +
                "until the triggering operation completes.");
        }
    }
```

Then update the XML comment on `_propertyCallbackDepth`, which currently claims the depth "feeds only `IsInsideAnyCallback`, never" the throw check. That sentence becomes false with this change. Replace it with: `Property callbacks are not exempt: a callback may evaluate anything and may mutate no topology, so this depth feeds the contract check exactly like the lifecycle callback depth.`

- [ ] **Step 4: Run the tests to verify both pass**

Run: `dotnet test src/Namotion.Interceptor.Tracking.Tests --filter "FullyQualifiedName~CallbackContractTests"`
Expected: PASS, 2 tests.

- [ ] **Step 5: Run the whole suite for fallout**

Run: `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration" > /tmp/uniform.log 2>&1; echo $?`
Then: `grep -oE "Total: +[0-9]+" /tmp/uniform.log | awk '{s+=$2; n++} END {print "assemblies="n" total="s}'` and `grep -E "Failed: +[1-9]" /tmp/uniform.log`

Expected: 26 assemblies, 3,409 total, no line from the second grep.

This step can genuinely fail. Making the guard uniform newly rejects writes that previously succeeded at depth 0, so any test relying on that shape breaks. If one does, read it before changing it: if it asserts a supported behaviour the spec now forbids, update the test and note it in the commit; if it reveals a first-party handler mutating topology from a property callback, stop and report, because that is a production bug this plan did not anticipate.

- [ ] **Step 6: Commit**

```bash
git add src/Namotion.Interceptor.Tracking/Lifecycle/CallbackReentrancyGuard.cs \
        src/Namotion.Interceptor.Tracking.Tests/Lifecycle/CallbackContractTests.cs \
        src/Namotion.Interceptor.Tracking.Tests/Lifecycle/DelegatePropertyLifecycleHandler.cs
git commit -m "fix: apply the callback contract at every graph depth

ThrowIfInsideCallback tested only the lifecycle callback depth, so the property
callback exemption held at the top of an attach and vanished below it: the
descent runs inside the notifier's scope. Testing IsInsideAnyCallback makes the
rule uniform, which is what the contract says."
```

---

## Task 2: Guard explicit attach and detach

`AttachSubjectToContext` and `DetachSubjectFromContext` take the lifecycle gate with no guard call, while `WriteProperty` and `TryAddProperties` both have one. Two threads, each holding its own gate inside a callback and attaching into the other's context, deadlock permanently, because there is one gate per lifecycle and no order among gates.

**Files:**
- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/LifecycleInterceptor.cs` (`AttachSubjectToContext`, `DetachSubjectFromContext`)
- Modify: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/CallbackContractTests.cs`

- [ ] **Step 1: Write the failing tests**

Append to `CallbackContractTests.cs`:

```csharp
    [Fact]
    public void WhenALifecycleCallbackAttachesASubject_ThenItThrows()
    {
        // Arrange
        Exception? callbackException = null;
        var context = CreateContext()
            .WithService(() => new DelegateLifecycleHandler(change =>
            {
                if (callbackException is not null)
                {
                    return;
                }

                callbackException = Record.Exception(
                    () => new Person { FirstName = "X" }.AttachToContext(change.Subject.GetContext()));
            }), _ => false);

        // Act
        _ = new Person(context) { FirstName = "R" };

        // Assert
        Assert.IsType<LifecycleContractViolationException>(callbackException);
    }

    [Fact]
    public void WhenALifecycleCallbackDetachesASubject_ThenItThrows()
    {
        // Arrange
        Exception? callbackException = null;
        Person? pinned = null;
        var context = CreateContext()
            .WithService(() => new DelegateLifecycleHandler(change =>
            {
                if (callbackException is not null || pinned is null || ReferenceEquals(change.Subject, pinned))
                {
                    return;
                }

                callbackException = Record.Exception(() => pinned.DetachFromContext(pinned.GetContext()));
            }), _ => false);

        pinned = new Person(context) { FirstName = "P" };

        // Act
        _ = new Person(context) { FirstName = "R" };

        // Assert
        Assert.IsType<LifecycleContractViolationException>(callbackException);
        Assert.NotNull(pinned.TryGetContext());
    }

    [Fact]
    public void WhenTwoLifecyclesAttachIntoEachOtherFromCallbacks_ThenNeitherDeadlocks()
    {
        // Arrange: the reproduction of the cross-lifecycle gate deadlock. Each thread holds its
        // own gate inside a callback and reaches for the other's. The contract must reject the
        // attach before either gate is requested, so both threads finish.
        var first = CreateContext();
        var second = CreateContext();
        var ready = new CountdownEvent(2);

        void Body(IInterceptorSubjectContext own, IInterceptorSubjectContext other)
        {
            own.WithService(() => new DelegateLifecycleHandler(_ =>
            {
                ready.Signal();
                ready.Wait(TimeSpan.FromSeconds(5));
                Record.Exception(() => new Person { FirstName = "X" }.AttachToContext(other));
            }), _ => false);

            _ = new Person(own) { FirstName = "R" };
        }

        // Act
        var a = new Thread(() => Body(first, second));
        var b = new Thread(() => Body(second, first));
        a.Start();
        b.Start();

        // Assert: a bounded join, so a regression fails the test instead of hanging the suite.
        Assert.True(a.Join(TimeSpan.FromSeconds(10)), "thread a did not finish, the gates deadlocked");
        Assert.True(b.Join(TimeSpan.FromSeconds(10)), "thread b did not finish, the gates deadlocked");
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/Namotion.Interceptor.Tracking.Tests --filter "FullyQualifiedName~CallbackContractTests"`

Expected: the two single-threaded tests FAIL, because no exception is thrown and `Record.Exception` returns null. The deadlock test either FAILS on the bounded join or hangs until the 10 second join elapses; either way it does not pass.

- [ ] **Step 3: Add the guard calls**

In `src/Namotion.Interceptor.Tracking/Lifecycle/LifecycleInterceptor.cs`, add `CallbackReentrancyGuard.ThrowIfInsideCallback();` as the first statement of both `AttachSubjectToContext` and `DetachSubjectFromContext`, before the `lock (_gate)`.

Both are unconditional, unlike `TryAddProperties`, which permits same-lifecycle reentry with `IsInsideAnyCallback && !Monitor.IsEntered(_gate)`. Explicit attach and detach have no supported reentrant case: even the same-lifecycle call runs a full claim, seed and attach descent mid-reconcile, which is the corruption the guard exists to prevent.

```csharp
    public void AttachSubjectToContext(IInterceptorSubject subject, IInterceptorSubjectContext context, SubjectAnchorKind anchor)
    {
        CallbackReentrancyGuard.ThrowIfInsideCallback();

        lock (_gate)
        {
```

```csharp
    public void DetachSubjectFromContext(IInterceptorSubject subject, IInterceptorSubjectContext context)
    {
        CallbackReentrancyGuard.ThrowIfInsideCallback();

        lock (_gate)
        {
```

The guard's message names structural property writes only. Widen it in `CallbackReentrancyGuard.ThrowIfInsideCallback` so an attach or detach violation reads correctly:

```csharp
            throw new LifecycleContractViolationException(
                "A lifecycle callback must not change graph topology: no structural " +
                "(subject-typed) property write, and no explicit attach or detach. The callback " +
                "runs while the lifecycle holds its topology gate mid-reconcile, so the change " +
                "would re-enter the reconciler on half-updated edge state, and reaching a second " +
                "lifecycle's gate from inside a callback can deadlock. Defer the change until the " +
                "triggering operation completes.");
```

Update the assertion in `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/GraphOwnershipTests.cs` that matches on the old message text, from `"lifecycle callback must not write a structural"` to `"lifecycle callback must not change graph topology"`.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test src/Namotion.Interceptor.Tracking.Tests --filter "FullyQualifiedName~CallbackContractTests"`
Expected: PASS, 5 tests, and the deadlock test completes in well under its 10 second bound.

- [ ] **Step 5: Run the whole suite**

Run: `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration" > /tmp/attachguard.log 2>&1; echo $?`
Then: `grep -E "Failed: +[1-9]" /tmp/attachguard.log`

Expected: exit 0 and no output from the grep.

- [ ] **Step 6: Commit**

```bash
git add src/Namotion.Interceptor.Tracking/Lifecycle/LifecycleInterceptor.cs \
        src/Namotion.Interceptor.Tracking/Lifecycle/CallbackReentrancyGuard.cs \
        src/Namotion.Interceptor.Tracking.Tests/Lifecycle/CallbackContractTests.cs \
        src/Namotion.Interceptor.Tracking.Tests/Lifecycle/GraphOwnershipTests.cs
git commit -m "fix: reject explicit attach and detach from inside a lifecycle callback

Both entry points took the gate with no guard call, so the documented contract
was unenforced there. With one gate per lifecycle and no order among gates, two
threads attaching into each other's context from callbacks deadlock; the test
reproduces it with a bounded join."
```

---

## Task 3: Fail fast on a derived property that exposes an untracked subject

A derived getter that lazily creates a subject writes a private field, so no interceptor sees it, and derived properties never establish ownership edges. The subject is created and silently never attached. This is the quietest hole in the area.

**Files:**
- Modify: `src/Namotion.Interceptor.Tracking/Change/DerivedPropertyChangeHandler.cs`
- Modify: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/CallbackContractTests.cs`

- [ ] **Step 1: Write the failing tests**

Append to `CallbackContractTests.cs`:

```csharp
    [Fact]
    public void WhenADerivedPropertyExposesAnUnattachedSubject_ThenItThrows()
    {
        // Arrange
        var context = CreateContext();

        // Act & Assert: the lazily created child is owned by nothing, so it would never be
        // tracked. Attach-time evaluation of the derived getter is where that surfaces.
        var exception = Record.Exception(() => new LazyDerivedSubject(context));

        Assert.IsType<LifecycleContractViolationException>(exception);
        Assert.Contains("derived", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WhenADerivedPropertyProjectsAnAttachedSubject_ThenItDoesNotThrow()
    {
        // Arrange
        var context = CreateContext();

        // Act: FirstChild projects a subject already owned through the stored Children edge.
        var subject = new ProjectingDerivedSubject(context);
        subject.Children = [new Person { FirstName = "C" }];

        // Assert
        Assert.NotNull(subject.FirstChild);
        Assert.NotNull(subject.FirstChild!.TryGetContext());
    }
```

Create `src/Namotion.Interceptor.Tracking.Tests/Models/LazyDerivedSubject.cs`:

```csharp
using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Tracking.Tests.Models;

[InterceptorSubject]
public partial class LazyDerivedSubject
{
    private Person? _child;

    public partial string? Name { get; set; }

    /// <summary>Lazy initialisation inside a getter: the child is owned by nothing.</summary>
    [Derived]
    public Person Current => _child ??= new Person { FirstName = "lazy" };
}
```

Create `src/Namotion.Interceptor.Tracking.Tests/Models/ProjectingDerivedSubject.cs`:

```csharp
using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Tracking.Tests.Models;

[InterceptorSubject]
public partial class ProjectingDerivedSubject
{
    public partial Person[]? Children { get; set; }

    /// <summary>A projection of an edge the stored property already owns.</summary>
    [Derived]
    public Person? FirstChild => Children is { Length: > 0 } children ? children[0] : null;
}
```

- [ ] **Step 2: Run the tests to verify the first fails**

Run: `dotnet test src/Namotion.Interceptor.Tracking.Tests --filter "FullyQualifiedName~CallbackContractTests"`

Expected: `WhenADerivedPropertyExposesAnUnattachedSubject_ThenItThrows` FAILS, because nothing throws today and `Record.Exception` returns null. `WhenADerivedPropertyProjectsAnAttachedSubject_ThenItDoesNotThrow` PASSES already; it is the guard against over-rejecting in Step 3.

- [ ] **Step 3: Add the check**

In `src/Namotion.Interceptor.Tracking/Change/DerivedPropertyChangeHandler.cs`, add a private helper and call it wherever a derived value has just been evaluated (both the attach path in `AttachProperty` and the recalculation path). Use `StructuralValueScanner` rather than hand-rolling the shape dispatch, since it is the single interpretation of "which subjects does this value hold":

```csharp
    /// <summary>
    /// Rejects a derived value that exposes a subject the graph does not own. Derived properties
    /// establish no ownership edges, so such a subject is never attached, never registered and
    /// never released: silent before this check existed.
    /// </summary>
    private static void ThrowIfExposesUntrackedSubject(PropertyReference property, object? value)
    {
        if (value is null || !property.Metadata.Type.CanContainSubjects())
        {
            return;
        }

        var context = property.Subject.TryGetContext();
        if (context is null)
        {
            return;
        }

        var occurrences = LifecycleScratch.RentOccurrenceList();
        try
        {
            StructuralValueScanner.CollectOccurrences(property.Metadata.Type, value, occurrences);
            foreach (var occurrence in occurrences)
            {
                if (ReferenceEquals(occurrence.Subject.TryGetContext(), context))
                {
                    continue;
                }

                throw new LifecycleContractViolationException(
                    $"The derived property '{property.Name}' returned a subject that is not " +
                    "attached to this context. Derived properties establish no ownership edges, " +
                    "so that subject is never tracked, registered or released. Assign it to a " +
                    "stored (non-derived) property instead, or attach it explicitly.");
            }
        }
        finally
        {
            LifecycleScratch.Return(occurrences);
        }
    }
```

The `CanContainSubjects()` short circuit is what keeps this off the hot path for the common case: a `string` or `decimal` derived property never scans.

Call it immediately after each evaluation, inside the existing `try` blocks so the filtered catches see it and, per the spec, deliberately do not absorb it:

```csharp
                    data.LastKnownValue = EvaluateAndStabilize(data, change.Property, callerHoldsLock: true);
                    ThrowIfExposesUntrackedSubject(change.Property, data.LastKnownValue);
```

```csharp
                        newValue = EvaluateAndStabilize(data, derivedProperty, callerHoldsLock: false);
                        ThrowIfExposesUntrackedSubject(derivedProperty, newValue);
```

Add whichever `using` directives the file lacks for `LifecycleScratch`, `StructuralValueScanner` and `CanContainSubjects`; `LifecycleContractViolationException` is already reachable through the existing `Namotion.Interceptor.Tracking.Lifecycle` using.

- [ ] **Step 4: Run the tests to verify both pass**

Run: `dotnet test src/Namotion.Interceptor.Tracking.Tests --filter "FullyQualifiedName~CallbackContractTests"`
Expected: PASS, 7 tests.

- [ ] **Step 5: Run the whole suite, and read any failure carefully**

Run: `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration" > /tmp/derived.log 2>&1; echo $?`
Then: `grep -E "Failed: +[1-9]" /tmp/derived.log`

Expected: exit 0, no output.

A failure here is informative rather than a nuisance: it means some existing subject exposes an untracked subject from a derived property, which is exactly the silent bug this check exists to find. Report the subject and property rather than weakening the check.

- [ ] **Step 6: Commit**

```bash
git add src/Namotion.Interceptor.Tracking/Change/DerivedPropertyChangeHandler.cs \
        src/Namotion.Interceptor.Tracking.Tests/Lifecycle/CallbackContractTests.cs \
        src/Namotion.Interceptor.Tracking.Tests/Models/LazyDerivedSubject.cs \
        src/Namotion.Interceptor.Tracking.Tests/Models/ProjectingDerivedSubject.cs
git commit -m "feat: reject a derived property that exposes an untracked subject

Lazy initialisation inside a derived getter writes a private field, so no
interceptor sees it, and derived properties establish no ownership edges: the
subject was created and silently never attached. Master tracked such subjects,
this branch stopped, and until now stopped quietly. The check is confined to
derived properties whose declared type can contain subjects."
```

---

## Task 4: Prove or disprove that the accommodations are unreachable

An investigation task with a decision gate. It writes no production code.

**Files:**
- Read: `src/Namotion.Interceptor.Tracking/Lifecycle/StructuralReconciler.cs`, `LifecycleInterceptor.cs`, `PropertyAdmission.cs`, `OwnershipGraph.cs`, `AttachTraversal.cs`, `ReleaseTraversal.cs`
- Create: `docs/superpowers/plans/2026-08-24-accommodation-reachability.md`

- [ ] **Step 1: Enumerate every path that reaches the reconcile loops**

For each, record whether it can release the writing parent while `StructuralReconciler.ReconcileOrdinal` or `ReconcileKeyed` is mid-loop, and cite file:line. Start from `LifecycleInterceptor.WriteProperty` and `PropertyAdmission`, and follow every callback fan-out that runs between the baseline commit and the end of the loop.

- [ ] **Step 2: Check the two named candidates explicitly**

Both are permitted from inside a callback, so neither is excluded by Tasks 1 and 2:

1. Reentrant same-lifecycle `TryAddProperties`, which passes `IsInsideAnyCallback && !Monitor.IsEntered(_gate)` and therefore proceeds when the gate is already held by this thread.
2. `ReleaseUnusedClaims` compensation on a throwing or suppressing terminal, reached from `LifecycleInterceptor` after `next` returns.

For each, answer one question: can it release the writing parent mid-loop? Show the call chain or show where it is impossible.

- [ ] **Step 3: Write the finding**

Write `docs/superpowers/plans/2026-08-24-accommodation-reachability.md` with the path enumeration, the two candidate answers, and a one-line verdict: either "no path can release the writing parent mid-reconcile, the accommodations are unreachable" or "path X can, they stay".

- [ ] **Step 4: Commit**

```bash
git add docs/superpowers/plans/2026-08-24-accommodation-reachability.md
git commit -m "docs: record whether the reconcile accommodations are still reachable"
```

- [ ] **Step 5: Gate**

If the verdict is "unreachable", continue to Task 5. If any path can still release the writing parent, **stop and report**: Task 5 is cancelled, and the removal-loop residue defect from the review becomes a live bug needing its own fix.

---

## Task 5: Delete the accommodations, conditional on Task 4

Only if Task 4 concluded "unreachable".

**Files:**
- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/StructuralReconciler.cs`
- Modify: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/CallbackContractTests.cs`

- [ ] **Step 1: Write the test that pins the contract, not the mechanism**

Mutation testing showed all six exits can be deleted with the suite green, so a test asserting current behaviour proves nothing. Pin the reason they are deletable instead. Append to `CallbackContractTests.cs`:

```csharp
    [Fact]
    public void WhenAPropertyCallbackTriesToReleaseTheWritingParent_ThenItThrowsAndTheGraphIsIntact()
    {
        // The reconciler's released-parent early exits were deleted because this throws. If the
        // contract is ever loosened, this test fails first and says why the exits must come back.
        Exception? callbackException = null;
        Person? root = null;
        var handler = new DelegatePropertyLifecycleHandler(change =>
        {
            if (callbackException is not null || change.Subject is not Person { FirstName: "y" })
            {
                return;
            }

            callbackException = Record.Exception(() => root!.Father = null);
        });

        var context = CreateContext().WithService(() => handler, _ => false);
        root = new Person(context) { FirstName = "R" };
        var parent = new Person { FirstName = "P" };
        root.Father = parent;

        // Act
        parent.Children = [new Person { FirstName = "y" }, new Person { FirstName = "z" }];

        // Assert
        Assert.IsType<LifecycleContractViolationException>(callbackException);
        Assert.Same(context, parent.TryGetContext());
        Assert.Equal(2, parent.Children!.Length);
        foreach (var child in parent.Children)
        {
            Assert.Same(context, child.TryGetContext());
            Assert.Equal(1, child.GetReferenceCount());
        }
    }
```

- [ ] **Step 2: Run it to verify it passes before any deletion**

Run: `dotnet test src/Namotion.Interceptor.Tracking.Tests --filter "FullyQualifiedName~WhenAPropertyCallbackTriesToReleaseTheWritingParent"`
Expected: PASS. It must pass before the deletion, because it describes the contract established in Tasks 1 and 2, not the deletion.

- [ ] **Step 3: Delete the six early exits**

Remove the released-parent early exits in `StructuralReconciler.ReconcileKeyed` and `ReconcileOrdinal`, and the comments that justify them by the exemption. Leave the inexact same-property fallback in `SubjectOwnership.RemoveIncoming` alone: it also covers stored-index lag inside the reconcile window, which is a separate justification.

- [ ] **Step 4: Run the whole suite**

Run: `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration" > /tmp/deleted.log 2>&1; echo $?`
Then: `grep -E "Failed: +[1-9]" /tmp/deleted.log`

Expected: exit 0, no output.

- [ ] **Step 5: Record the size change**

Run: `pwsh scripts/diff-composition.ps1`

Expected: production code net lower than the previous run. Record the number in the commit message, per the repository's standing rule that a removal stage shows a net-negative production diff.

- [ ] **Step 6: Commit**

```bash
git add src/Namotion.Interceptor.Tracking/Lifecycle/StructuralReconciler.cs \
        src/Namotion.Interceptor.Tracking.Tests/Lifecycle/CallbackContractTests.cs
git commit -m "refactor: delete the released-parent early exits

They existed only to survive a callback releasing the writing parent
mid-reconcile. That release now throws at the attempt, and the reachability
proof found no other path, so the state they compensated for cannot arise."
```

---

## Task 6: Reconcile the documentation

Two shipped documents state opposite contracts, and that contradiction is what let four defects through the first review.

**Files:**
- Modify: `docs/design/tracking-lifecycle.md` (Callback Contract section)
- Modify: `docs/superpowers/specs/2026-08-21-single-context-lifecycle-simplification-design.md` (decision 3)

- [ ] **Step 1: Rewrite the Callback Contract section**

In `docs/design/tracking-lifecycle.md`, replace the paragraph claiming property callbacks are exempt, and the paragraph naming the two accommodations, with the single rule. Keep it on one line per paragraph, no hard wrapping, no em dashes:

```markdown
Lifecycle callbacks are synchronous and exception-free by contract; violations propagate with no rollback. A callback may evaluate anything, including user getters, and may change no graph topology: no structural property write, no explicit attach or detach, and no cross-context `AddProperties`. Violations throw `LifecycleContractViolationException` in every build, uniformly at every graph depth, because the silent failure modes are graph corruption and a deadlock between two lifecycle gates. Property lifecycle callbacks (`IPropertyLifecycleHandler.AttachProperty`/`DetachProperty`) are not exempt: the derived-property handler evaluates user getters from its attach callback, and evaluation is what the contract permits.

`DerivedPropertyChangeHandler` absorbs exceptions from derived getters, keeping the last known value and recomputing on the next dependency write, and filters `LifecycleContractViolationException` out of that absorption so a contract breach cannot hide behind a derived value that silently never initializes. A derived property whose declared type can contain subjects also throws when it returns a subject this context does not own, because derived properties establish no ownership edges and such a subject would never be tracked.
```

If Task 5 deleted the early exits, also delete the sentence stating that the two accommodations are "not removable while the exemption stands". If Task 4 kept them, rewrite that sentence to name their surviving justification instead.

- [ ] **Step 2: Correct decision 3 in the older spec**

In `docs/superpowers/specs/2026-08-21-single-context-lifecycle-simplification-design.md`, decision 3 claims the rule "is detected by a `[Conditional("DEBUG")]` guard so Release pays nothing". No such guard exists anywhere in production. Replace that clause with: `It is detected by CallbackReentrancyGuard, which is live in every build, because the silent failure mode is graph corruption. See the 2026-08-24 lifecycle callback contract design for the settled rule.`

- [ ] **Step 3: Verify no stale claim survives**

Run: `grep -rn "are a supported shape\|Conditional(\"DEBUG\")\|not removable while the exemption" docs/ --include=*.md`

Expected: no hits outside `docs/superpowers/plans/`, which are historical records and are deliberately not reconciled.

- [ ] **Step 4: Commit**

```bash
git add docs/design/tracking-lifecycle.md \
        docs/superpowers/specs/2026-08-21-single-context-lifecycle-simplification-design.md
git commit -m "docs: state one callback contract instead of two contradictory ones

The design doc called topology mutation from a property callback a supported
shape while the spec called it a contract violation, and neither described the
code. The rule is now uniform and enforced, so both say the same thing."
```

---

## Definition of done

- The contract is uniform at every depth, and a depth-2 test proves it.
- Explicit attach and detach are guarded, and the cross-lifecycle deadlock fails a bounded join rather than hanging.
- A derived property exposing an untracked subject throws; one projecting an owned subject does not.
- The accommodations are either deleted with a reachability proof recorded, or kept with the reason recorded and the removal-loop residue reopened as a live bug.
- No shipped document claims topology mutation from a property callback is supported, and no document claims a `[Conditional("DEBUG")]` guard exists.
- Full unit suite green: 26 assemblies, zero failures, zero warnings.
