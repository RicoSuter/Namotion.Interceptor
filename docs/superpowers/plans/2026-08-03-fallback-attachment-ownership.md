# Fallback Attachment Ownership Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `InterceptorExecutor` remember the lifecycle interceptors it attached to and use that record as the ownership token for the fallback edge, closing all five defects in #402.

**Architecture:** Each fallback edge added through `InterceptorExecutor` gets a `FallbackAttachment` record holding the interceptors resolved at attach time. The record lives on `InterceptorSubjectContext` in a list guarded by the existing `_mutationLock`, which is what makes it atomic with the topology it owns. It carries a phase so a removal cannot overtake an attach that is still running: a remover arriving mid-attach hands its removal to the attaching thread rather than refusing, because refusing strands the edge and waiting deadlocks.

**Tech Stack:** C# 13, .NET Standard 2.0 (core project), xUnit, BenchmarkDotNet, PublicApiGenerator + Verify.

**Reference implementation:** commit `a4723531` is a validated prototype of this design (full solution suite 2653 passed, 0 failed). It is the source of truth for the code in this plan. Use `git show a4723531:<path>` to consult it.

**Design spec:** `docs/superpowers/specs/2026-08-03-fallback-attachment-ownership-design.md`. Where the spec and this plan disagree, the plan wins: the spec predates the prototype and still describes a phase-only refusal that was measured to strand edges.

## Global Constraints

- Never include AI attribution in commit messages, PR descriptions or GitHub comments: no agent names, no `Co-Authored-By` trailers, no "Generated with" footers.
- No em dashes in docs, READMEs or PR descriptions.
- Comments explain only the non-obvious. Do not narrate what the code already says.
- Do not reference issue numbers from production code. Reference code from issues instead.
- Correctness and test coverage come first. `InterceptorSubjectContext.cs` is already over 1,100 lines and this work adds roughly 110 more, which is accepted for now: Task 8 is a separate structural round once the behaviour is proven. Do not use `partial` to hide the growth.
- Test naming: `When<Condition>_Then<ExpectedBehavior>`. Explicit `// Arrange`, `// Act`, `// Assert` comments (`// Act & Assert` for exception tests).
- No hardcoded waits. Use `AsyncTestHelpers.WaitUntilAsync` or `ManualResetEventSlim` / `CountdownEvent` rendezvous. Never `Task.Delay` or `Thread.Sleep`.
- The core project targets .NET Standard 2.0. `ExceptionDispatchInfo`, `ImmutableArray<T>` and `private protected` are all available there. `SpinWait.SpinOnce(int)` is not.
- Build is warnings-as-errors with nullable enabled.
- Benchmarks run through `pwsh scripts/benchmark.ps1`, never a hand-rolled `dotnet run`. The script checks out the base branch, runs both sides and emits a comparison report.
- Public API must not change. `src/Namotion.Interceptor.Tests/VerifyChecksTests.PublicApi.verified.txt` must stay byte-identical.

---

## File Structure

| File | Responsibility |
|---|---|
| `src/Namotion.Interceptor/FallbackAttachment.cs` (create) | The record type and the removal-outcome enum. Pure data, no logic, no locking. |
| `src/Namotion.Interceptor/InterceptorSubjectContext.cs` (modify) | One field plus the four locked operations that keep the record and the topology in step. They need `_mutationLock`, `_state`, `PublishState`, `InvalidateUsingContexts` and `GetOrCreateUsedByContexts`, all private, so they live here for now. Task 8 revisits the placement. |
| `src/Namotion.Interceptor/Interceptors/InterceptorExecutor.cs` (modify) | The two overrides and the shared detach helper. Callback sequencing lives here. |
| `src/Namotion.Interceptor.Tests/Context/FallbackAttachmentOwnershipTests.cs` (create) | All new behaviour tests. |
| `src/Namotion.Interceptor.Tests/Context/ContextConcurrencyFuzzTests.cs` (modify) | Two model sites that assume a failed add leaves its edge behind. |

---

### Task 1: The ownership record, its phase, and the handoff

This is the core mechanism. The record, the phase and the handoff are interdependent: a record without a phase lets a removal overtake an unfinished attach and leave the bookkeeping disagreeing with the topology, and a phase without a handoff makes a mid-attach removal return `false` and strand the edge. Both were measured on the prototype, so they land together.

**Files:**
- Create: `src/Namotion.Interceptor/FallbackAttachment.cs`
- Modify: `src/Namotion.Interceptor/InterceptorSubjectContext.cs` (the field block near line 74, and the new members before TryAddService)
- Modify: `src/Namotion.Interceptor/Interceptors/InterceptorExecutor.cs`
- Test: `src/Namotion.Interceptor.Tests/Context/FallbackAttachmentOwnershipTests.cs`

**Interfaces:**
- Consumes: `InterceptorSubjectContext._mutationLock`, `_state`, `PublishState`, `InvalidateUsingContexts`, `GetOrCreateUsedByContexts`, `_usedByContexts`, `ContextState`, all existing private members of the context.
- Produces:
  - `internal sealed class FallbackAttachment` with fields `Context` (`InterceptorSubjectContext`), `Interceptors` (`ImmutableArray<ILifecycleInterceptor>`), `InvokedInterceptorCount` (`int`), `IsAttachCompleted` (`bool`), `IsPendingRemoval` (`bool`), `Next` (`FallbackAttachment?`)
  - `internal enum FallbackRemovalOutcome { NotPresent, Deferred, Claimed }`
  - `private protected FallbackAttachment? InterceptorSubjectContext.TryBeginFallbackAttachment(InterceptorSubjectContext contextImpl, ImmutableArray<ILifecycleInterceptor> interceptors)`
  - `private protected bool InterceptorSubjectContext.CompleteFallbackAttachment(FallbackAttachment attachment, int invokedInterceptorCount)`
  - `private protected FallbackRemovalOutcome InterceptorSubjectContext.TryTakeFallbackAttachment(InterceptorSubjectContext contextImpl, out FallbackAttachment? attachment)`
  - `private protected void InterceptorSubjectContext.CompleteFallbackContextRemoval(InterceptorSubjectContext contextImpl)`

- [ ] **Step 1: Write the failing race test**

Create `src/Namotion.Interceptor.Tests/Context/FallbackAttachmentOwnershipTests.cs`:

```csharp
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Testing;

namespace Namotion.Interceptor.Tests.Context;

public class FallbackAttachmentOwnershipTests
{
    [Fact]
    public async Task WhenRemoveRunsDuringAttachCallbacks_ThenTheEdgeIsStillRemoved()
    {
        // Arrange: the attach callback parks, which is what a real attach does when the remover
        // already holds the lifecycle lock it needs. The edge is published by then, so a remover
        // arriving here finds a record whose attach has not completed.
        var parentContext = InterceptorSubjectContext.Create();
        using var attachStarted = new ManualResetEventSlim(false);
        using var releaseAttach = new ManualResetEventSlim(false);
        var interceptor = new ParkingLifecycleInterceptor(attachStarted, releaseAttach);
        parentContext.AddService<ILifecycleInterceptor>(interceptor);

        var childContext = ((IInterceptorSubject)new ContextProbeSubject()).Context;

        // Act
        var add = Task.Factory.StartNew(
            () => childContext.AddFallbackContext(parentContext), TaskCreationOptions.LongRunning);

        Assert.True(attachStarted.Wait(TimeSpan.FromSeconds(30)),
            "The attach callback never started, so the removal below would not race it.");

        var removed = childContext.RemoveFallbackContext(parentContext);
        releaseAttach.Set();
        var added = await add;

        // Assert: both calls report success, and the edge really is gone once everything settles.
        // The child executor owns no services, so resolving nothing means the fallback is gone.
        Assert.True(added);
        Assert.True(removed);
        await AsyncTestHelpers.WaitUntilAsync(
            () => childContext.GetServices<ILifecycleInterceptor>().IsEmpty,
            message: "The fallback edge survived a removal that raced the attach callbacks.");

        Assert.Equal(1, interceptor.AttachCount);
        Assert.Equal(1, interceptor.DetachCount);
    }

    [Fact]
    public void WhenInterceptorIsRegisteredAfterAttach_ThenItIsNotNotifiedOnDetach()
    {
        // Arrange: detach must replay what attach resolved, not what the parent happens to hold
        // when the removal runs.
        var parentContext = InterceptorSubjectContext.Create();
        var atAttachTime = new CountingLifecycleInterceptor();
        parentContext.AddService<ILifecycleInterceptor>(atAttachTime);

        var childContext = ((IInterceptorSubject)new ContextProbeSubject()).Context;
        Assert.True(childContext.AddFallbackContext(parentContext));

        var afterAttach = new CountingLifecycleInterceptor();
        parentContext.AddService<ILifecycleInterceptor>(afterAttach);

        // Act
        Assert.True(childContext.RemoveFallbackContext(parentContext));

        // Assert
        Assert.Equal(1, atAttachTime.DetachCount);
        Assert.Equal(0, afterAttach.DetachCount);
    }

    private sealed class ParkingLifecycleInterceptor(
        ManualResetEventSlim attachStarted,
        ManualResetEventSlim releaseAttach) : ILifecycleInterceptor
    {
        private int _attachCount;
        private int _detachCount;

        public int AttachCount => Volatile.Read(ref _attachCount);

        public int DetachCount => Volatile.Read(ref _detachCount);

        public void AttachSubjectToContext(IInterceptorSubject subject)
        {
            Interlocked.Increment(ref _attachCount);
            attachStarted.Set();
            releaseAttach.Wait();
        }

        public void DetachSubjectFromContext(IInterceptorSubject subject)
        {
            Interlocked.Increment(ref _detachCount);
        }
    }

    private sealed class CountingLifecycleInterceptor : ILifecycleInterceptor
    {
        private int _detachCount;

        public int DetachCount => Volatile.Read(ref _detachCount);

        public void AttachSubjectToContext(IInterceptorSubject subject)
        {
        }

        public void DetachSubjectFromContext(IInterceptorSubject subject)
        {
            Interlocked.Increment(ref _detachCount);
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/Namotion.Interceptor.Tests --filter "FullyQualifiedName~FallbackAttachmentOwnershipTests" -v q --nologo`

Expected: both FAIL. `WhenRemoveRunsDuringAttachCallbacks` hangs or fails because today's `RemoveFallbackContext` resolves and detaches while the attach is mid-flight. `WhenInterceptorIsRegisteredAfterAttach` fails on `Assert.Equal(0, afterAttach.DetachCount)` with actual `1`, because today the set is re-resolved at detach time.

- [ ] **Step 3: Create the record type**

Create `src/Namotion.Interceptor/FallbackAttachment.cs`:

```csharp
using System.Collections.Immutable;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor;

/// <summary>
/// Ownership record for one fallback edge registered through
/// <see cref="Interceptors.InterceptorExecutor"/>, holding the lifecycle interceptors that the
/// matching attach resolved so the detach replays exactly those.
/// </summary>
/// <remarks>
/// Every field is read and written under the owning context's mutation lock, which is what makes a
/// record atomic with the edge it owns. Nothing here is thread safe on its own.
/// </remarks>
internal sealed class FallbackAttachment
{
    internal InterceptorSubjectContext Context = null!;

    internal ImmutableArray<ILifecycleInterceptor> Interceptors;

    /// <summary>How far the attach loop got, so a detach replays exactly that prefix.</summary>
    internal int InvokedInterceptorCount;

    /// <summary>Set once the attach loop has finished, including when it threw.</summary>
    internal bool IsAttachCompleted;

    /// <summary>A remover arrived mid-attach and handed its removal to the attaching thread.</summary>
    internal bool IsPendingRemoval;

    internal FallbackAttachment? Next;
}

internal enum FallbackRemovalOutcome
{
    /// <summary>No edge, or another remover already owns it.</summary>
    NotPresent,

    /// <summary>Handed to the thread still attaching, which will run the callbacks and the removal.</summary>
    Deferred,

    /// <summary>This caller owns the removal.</summary>
    Claimed
}
```

- [ ] **Step 4: Add the field**

In `src/Namotion.Interceptor/InterceptorSubjectContext.cs`, immediately after the `_mutationLock` declaration (around line 74), add:

```csharp
    // Ownership records for fallback edges added through InterceptorExecutor. Null on every other
    // context. Read and written only under _mutationLock, which is what makes a record atomic with
    // the edge it owns, and never touched by a resolution or invalidation path.
    private FallbackAttachment? _fallbackAttachments;
```

- [ ] **Step 5: Add the four locked operations**

In the same file, insert immediately before `public bool TryAddService<TService>(...)`. They keep a fallback edge and its record in step: both are mutated under `_mutationLock`, so no observer that takes the lock can see one without the other.

Removal is two phases because the detach callbacks have to run while the edge is still resolvable, and they cannot run under the lock. Phase one claims the record and leaves the edge; phase two drops the edge. Between them the edge exists with no record, which is safe in both directions: a concurrent remove finds nothing to claim, and a concurrent add sees the edge present and declines.

```csharp
    /// <summary>
    /// Publishes the edge and its record in one locked section. Returns null when the edge exists.
    /// </summary>
    private protected FallbackAttachment? TryBeginFallbackAttachment(
        InterceptorSubjectContext contextImpl,
        ImmutableArray<ILifecycleInterceptor> interceptors)
    {
        var attachment = new FallbackAttachment
        {
            Context = contextImpl,
            Interceptors = interceptors
        };

        lock (_mutationLock)
        {
            var state = Volatile.Read(ref _state);
            if (state.FallbackContexts.Contains(contextImpl))
            {
                return null;
            }

            // R4: register into the fallback before publishing, as AddFallbackContext does.
            var usedByContexts = contextImpl.GetOrCreateUsedByContexts();
            lock (usedByContexts)
            {
                usedByContexts.Add(this);
            }

            attachment.Next = _fallbackAttachments;
            _fallbackAttachments = attachment;

            PublishState(new ContextState(state.Services, state.FallbackContexts.Add(contextImpl)));
        }

        InvalidateUsingContexts();
        return attachment;
    }

    /// <summary>
    /// Marks the attach finished and reports whether a remover handed its removal to this thread.
    /// Must be called from a finally, so a throwing attach still leaves a removable edge.
    /// </summary>
    private protected bool CompleteFallbackAttachment(FallbackAttachment attachment, int invokedInterceptorCount)
    {
        lock (_mutationLock)
        {
            attachment.InvokedInterceptorCount = invokedInterceptorCount;
            attachment.IsAttachCompleted = true;

            if (!attachment.IsPendingRemoval)
            {
                return false;
            }

            UnlinkFallbackAttachment(attachment);
            return true;
        }
    }

    /// <summary>
    /// Phase one of removal. Claims the record and deliberately leaves the edge, because the
    /// detach callbacks resolve their handlers through it. Publishes nothing, so no invalidation.
    /// </summary>
    private protected FallbackRemovalOutcome TryTakeFallbackAttachment(
        InterceptorSubjectContext contextImpl,
        out FallbackAttachment? attachment)
    {
        lock (_mutationLock)
        {
            attachment = _fallbackAttachments;
            while (attachment is not null && !ReferenceEquals(attachment.Context, contextImpl))
            {
                attachment = attachment.Next;
            }

            if (attachment is null)
            {
                return FallbackRemovalOutcome.NotPresent;
            }

            if (!attachment.IsAttachCompleted)
            {
                // Waiting would deadlock: the attaching thread is inside callbacks that take the
                // lifecycle lock, which this caller may already hold. Refusing would strand the
                // edge. So hand the removal to the thread that owns the attach.
                var alreadyHandedOver = attachment.IsPendingRemoval;
                attachment.IsPendingRemoval = true;
                attachment = null;
                return alreadyHandedOver ? FallbackRemovalOutcome.NotPresent : FallbackRemovalOutcome.Deferred;
            }

            UnlinkFallbackAttachment(attachment);
            return FallbackRemovalOutcome.Claimed;
        }
    }

    /// <summary>
    /// Phase two of removal: drops the edge once the detach callbacks have run. No-op when the
    /// edge is already gone.
    /// </summary>
    private protected void CompleteFallbackContextRemoval(InterceptorSubjectContext contextImpl)
    {
        lock (_mutationLock)
        {
            var state = Volatile.Read(ref _state);
            var index = state.FallbackContexts.IndexOf(contextImpl);
            if (index < 0)
            {
                return;
            }

            PublishState(new ContextState(state.Services, state.FallbackContexts.RemoveAt(index)));

            // R4: unregister only after publishing, as RemoveFallbackContext does.
            var usedByContexts = Volatile.Read(ref contextImpl._usedByContexts);
            if (usedByContexts is not null)
            {
                lock (usedByContexts)
                {
                    usedByContexts.Remove(this);
                }
            }
        }

        InvalidateUsingContexts();
    }

    private void UnlinkFallbackAttachment(FallbackAttachment attachment)
    {
        if (ReferenceEquals(_fallbackAttachments, attachment))
        {
            _fallbackAttachments = attachment.Next;
            return;
        }

        var previous = _fallbackAttachments;
        while (previous is not null && !ReferenceEquals(previous.Next, attachment))
        {
            previous = previous.Next;
        }

        if (previous is not null)
        {
            previous.Next = attachment.Next;
        }
    }
```

- [ ] **Step 6: Rewrite the executor overrides**

In `src/Namotion.Interceptor/Interceptors/InterceptorExecutor.cs`, add to the usings at the top:

```csharp
using System.Runtime.ExceptionServices;
```

Replace both overrides (currently lines 58-89) with:

```csharp
    /// <remarks>
    /// The attach callbacks run after the edge is published, and they must: they resolve their
    /// handlers through this executor, which finds nothing until the fallback is in place.
    /// </remarks>
    public override bool AddFallbackContext(IInterceptorSubjectContext context)
    {
        // Cast first, matching the base mutator, so a foreign context fails here rather than
        // after an arbitrary service walk.
        var contextImpl = (InterceptorSubjectContext)context;
        if (HasFallbackContext(contextImpl))
        {
            return false;
        }

        // Reads the fallback's chain, not this one, so it does not need the edge. Resolving
        // before publishing is what leaves nothing behind when it throws.
        var interceptors = contextImpl.GetServices<ILifecycleInterceptor>();

        var attachment = TryBeginFallbackAttachment(contextImpl, interceptors);
        if (attachment is null)
        {
            return false;
        }

        try
        {
            for (var index = 0; index < interceptors.Length; index++)
            {
                interceptors[index].AttachSubjectToContext(_subject);
            }
        }
        finally
        {
            if (CompleteFallbackAttachment(attachment, interceptors.Length))
            {
                // A remover arrived mid-attach and handed its removal over. It has already told
                // its caller the edge is gone, so this must happen even while an attach exception
                // is propagating, and must not replace that exception.
                try
                {
                    DetachAndCompleteRemoval(attachment);
                }
                catch (Exception)
                {
                    // The attach failure is the one worth reporting.
                }
            }
        }

        return true;
    }

    /// <remarks>
    /// The detach callbacks run before the edge is removed, and they must: they resolve their
    /// handlers through this executor, which finds nothing once the fallback is gone.
    /// <para>
    /// Returning <c>true</c> means the removal is committed, not necessarily that the edge is
    /// already gone: when an add is still running its attach callbacks, the removal is handed to
    /// that thread and completes there. Waiting instead would deadlock, because the attaching
    /// thread is inside callbacks that take the lifecycle lock this caller may already hold.
    /// </para>
    /// </remarks>
    public override bool RemoveFallbackContext(IInterceptorSubjectContext context)
    {
        var contextImpl = (InterceptorSubjectContext)context;

        switch (TryTakeFallbackAttachment(contextImpl, out var attachment))
        {
            case FallbackRemovalOutcome.NotPresent:
                return false;

            case FallbackRemovalOutcome.Deferred:
                return true;

            default:
                DetachAndCompleteRemoval(attachment!);
                return true;
        }
    }

    /// <summary>
    /// Runs the recorded detach callbacks and then drops the edge.
    /// </summary>
    private void DetachAndCompleteRemoval(FallbackAttachment attachment)
    {
        try
        {
            var interceptors = attachment.Interceptors;
            for (var index = 0; index < attachment.InvokedInterceptorCount; index++)
            {
                interceptors[index].DetachSubjectFromContext(_subject);
            }
        }
        finally
        {
            // A handler failure must never block the removal, because a blocked removal is what
            // strands edges and retains subtrees.
            CompleteFallbackContextRemoval(attachment.Context);
        }
    }
```

- [ ] **Step 7: Run the new tests to verify they pass**

Run: `dotnet test src/Namotion.Interceptor.Tests --filter "FullyQualifiedName~FallbackAttachmentOwnershipTests" -v q --nologo`

Expected: PASS, 2 tests.

- [ ] **Step 8: Mutation-verify the handoff**

Temporarily change the `Deferred` return in `TryTakeFallbackAttachment` to `FallbackRemovalOutcome.NotPresent`:

```csharp
                return alreadyHandedOver ? FallbackRemovalOutcome.NotPresent : FallbackRemovalOutcome.NotPresent;
```

Run: `dotnet test src/Namotion.Interceptor.Tests --filter "FullyQualifiedName~WhenRemoveRunsDuringAttachCallbacks" --nologo`

Expected: FAIL on `Assert.True(removed)`. This is the measurement that proves the handoff is load-bearing rather than decorative: without it a mid-attach removal returns `false` and leaves the edge behind. Revert the change and re-run to confirm PASS.

- [ ] **Step 9: Run the lifecycle suites**

Run: `dotnet test src/Namotion.Interceptor.Tracking.Tests --filter "Category!=Integration" -v q --nologo`

Expected: PASS, 412 tests. These pin the two forced callback orderings. If any `LifecycleInterceptorTests` snapshot moves, the ordering has been broken and the change is wrong.

Run: `dotnet test src/Namotion.Interceptor.Registry.Tests --filter "Category!=Integration" -v q --nologo`

Expected: PASS, 143 tests.

- [ ] **Step 10: Commit**

```bash
git add src/Namotion.Interceptor/FallbackAttachment.cs \
        src/Namotion.Interceptor/InterceptorSubjectContext.cs \
        src/Namotion.Interceptor/Interceptors/InterceptorExecutor.cs \
        src/Namotion.Interceptor.Tests/Context/FallbackAttachmentOwnershipTests.cs
git commit -m "Record the interceptors a fallback attach resolved and own the edge by that record

Detach replayed a fresh resolve against a chain that may have changed or
broken since the attach, which is where every defect in the fallback
overrides came from. The record is now the ownership token, guarded by the
same lock that publishes the topology so the two move together.

It carries a phase, because a removal that overtakes an unfinished attach
leaves the edge absent while the bookkeeping still says attached. A remover
arriving mid-attach hands its removal to the attaching thread rather than
refusing: refusing strands the edge, and waiting deadlocks against the
lifecycle lock the attaching thread is parked on."
```

---

### Task 2: Update the fuzz model for a failed add that leaves no edge

`ContextConcurrencyFuzzTests` models an add as always leaving its edge, which was true when the executor published before resolving. Task 1 resolves first, so a failed add now leaves nothing. Both model sites need updating; the second one alone is not sufficient, measured at 5 of 8 seeds still failing.

**Files:**
- Modify: `src/Namotion.Interceptor.Tests/Context/ContextConcurrencyFuzzTests.cs` (around lines 460-469 and 519-527)

**Interfaces:**
- Consumes: the executor behaviour from Task 1.
- Produces: nothing consumed by later tasks.

- [ ] **Step 1: Run the fuzz tests to see the failure**

Run: `dotnet test src/Namotion.Interceptor.Tests --filter "FullyQualifiedName~ContextConcurrencyFuzzTests" -v q --nologo`

Expected: FAIL on 5 of the 8 seeds, with messages of the form "Context s1 resolved 69 marker services but the final topology contains 40".

- [ ] **Step 2: Fix the setup site**

In `BuildTopology`, replace the catch body:

```csharp
            catch (InvalidOperationException exception) when (IsDelegationCycle(exception))
            {
                // The executor resolves the lifecycle interceptors before publishing anything, so
                // a raise leaves no edge behind and the model has to record its absence.
                edge.IsPresent = false;
            }
```

- [ ] **Step 3: Fix the runtime mutation site**

In `RunOperation`, replace the add branch:

```csharp
                // Recorded after the call: the executor resolves the lifecycle interceptors before
                // publishing, so an add that raises on a circular chain leaves no edge. Removal is
                // the other way round, it keeps the edge visible for the detach callbacks and
                // drops it in a finally, so a raise there still means the edge is gone.
                edge.Source.Context.AddFallbackContext(edge.Target.Context);
                edge.IsPresent = true;
```

- [ ] **Step 4: Run the fuzz tests to verify they pass**

Run: `dotnet test src/Namotion.Interceptor.Tests --filter "FullyQualifiedName~ContextConcurrencyFuzzTests" -v q --nologo`

Expected: PASS, 8 tests.

- [ ] **Step 5: Commit**

```bash
git add src/Namotion.Interceptor.Tests/Context/ContextConcurrencyFuzzTests.cs
git commit -m "Model a failed fallback add as leaving no edge

The executor now resolves the lifecycle interceptors before publishing, so an
add that raises on a circular chain commits nothing. Both model sites recorded
the edge as present regardless, which was correct only for the previous
publish-then-resolve order."
```

---

### Task 3: Detach the invoked prefix, best effort

If an attach callback throws at index k, the interceptors after it never received an attach, so detaching the whole recorded array would unbalance them. And once the record is claimed, an interceptor skipped by an earlier failure can never be balanced by a later removal, so the detach loop must not stop at the first throw.

**Files:**
- Modify: `src/Namotion.Interceptor/Interceptors/InterceptorExecutor.cs`
- Test: `src/Namotion.Interceptor.Tests/Context/FallbackAttachmentOwnershipTests.cs`

**Interfaces:**
- Consumes: `FallbackAttachment.InvokedInterceptorCount` from Task 1.
- Produces: nothing new.

- [ ] **Step 1: Write the failing tests**

Add to `FallbackAttachmentOwnershipTests`:

```csharp
    [Fact]
    public void WhenDetachInterceptorThrows_ThenLaterInterceptorsStillReceiveDetach()
    {
        // Arrange
        var parentContext = InterceptorSubjectContext.Create();
        parentContext.AddService<ILifecycleInterceptor>(new ThrowingDetachInterceptor());
        var following = new CountingLifecycleInterceptor();
        parentContext.AddService<ILifecycleInterceptor>(following);

        var childContext = ((IInterceptorSubject)new ContextProbeSubject()).Context;
        Assert.True(childContext.AddFallbackContext(parentContext));

        // Act & Assert: the failure surfaces, the interceptor behind it still heard about the
        // detach, and the edge came out regardless.
        var exception = Assert.Throws<InvalidOperationException>(
            () => childContext.RemoveFallbackContext(parentContext));

        Assert.Equal("Detach failed.", exception.Message);
        Assert.Equal(1, following.DetachCount);
        Assert.True(childContext.GetServices<ILifecycleInterceptor>().IsEmpty);
    }

    [Fact]
    public void WhenAttachInterceptorThrows_ThenOnlyTheInvokedPrefixIsDetached()
    {
        // Arrange: the first interceptor throws on attach, so the second never receives one and
        // must not receive a detach either.
        var parentContext = InterceptorSubjectContext.Create();
        parentContext.AddService<ILifecycleInterceptor>(new ThrowingAttachInterceptor());
        var neverAttached = new CountingLifecycleInterceptor();
        parentContext.AddService<ILifecycleInterceptor>(neverAttached);

        var childContext = ((IInterceptorSubject)new ContextProbeSubject()).Context;
        Assert.Throws<InvalidOperationException>(() => childContext.AddFallbackContext(parentContext));

        // Act
        Assert.True(childContext.RemoveFallbackContext(parentContext));

        // Assert
        Assert.Equal(0, neverAttached.DetachCount);
    }

    private sealed class ThrowingDetachInterceptor : ILifecycleInterceptor
    {
        public void AttachSubjectToContext(IInterceptorSubject subject)
        {
        }

        public void DetachSubjectFromContext(IInterceptorSubject subject)
        {
            throw new InvalidOperationException("Detach failed.");
        }
    }

    private sealed class ThrowingAttachInterceptor : ILifecycleInterceptor
    {
        public void AttachSubjectToContext(IInterceptorSubject subject)
        {
            throw new InvalidOperationException("Attach failed.");
        }

        public void DetachSubjectFromContext(IInterceptorSubject subject)
        {
        }
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/Namotion.Interceptor.Tests --filter "FullyQualifiedName~FallbackAttachmentOwnershipTests" -v q --nologo`

Expected: `WhenDetachInterceptorThrows` FAILs on `Assert.Equal(1, following.DetachCount)` with actual `0`, because the loop stops at the throw. `WhenAttachInterceptorThrows` FAILs on `Assert.Equal(0, neverAttached.DetachCount)` with actual `1`, because Task 1 passes `interceptors.Length` as the invoked count.

- [ ] **Step 3: Count the invoked prefix**

In `AddFallbackContext`, replace the attach loop and the completion call:

```csharp
        var invokedInterceptorCount = 0;
        try
        {
            for (var index = 0; index < interceptors.Length; index++)
            {
                // Counted before the call: a thrower may have mutated itself, so its detach still
                // has to run.
                invokedInterceptorCount = index + 1;
                interceptors[index].AttachSubjectToContext(_subject);
            }
        }
        finally
        {
            if (CompleteFallbackAttachment(attachment, invokedInterceptorCount))
```

- [ ] **Step 4: Make the detach loop best effort**

Replace the body of `DetachAndCompleteRemoval`:

```csharp
    private void DetachAndCompleteRemoval(FallbackAttachment attachment)
    {
        ExceptionDispatchInfo? failure = null;
        try
        {
            var interceptors = attachment.Interceptors;
            for (var index = 0; index < attachment.InvokedInterceptorCount; index++)
            {
                try
                {
                    interceptors[index].DetachSubjectFromContext(_subject);
                }
                catch (Exception exception)
                {
                    failure ??= ExceptionDispatchInfo.Capture(exception);
                }
            }
        }
        finally
        {
            // A handler failure must never block the removal, because a blocked removal is what
            // strands edges and retains subtrees.
            CompleteFallbackContextRemoval(attachment.Context);
        }

        failure?.Throw();
    }
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test src/Namotion.Interceptor.Tests --filter "FullyQualifiedName~FallbackAttachmentOwnershipTests" -v q --nologo`

Expected: PASS, 4 tests.

- [ ] **Step 6: Commit**

```bash
git add src/Namotion.Interceptor/Interceptors/InterceptorExecutor.cs \
        src/Namotion.Interceptor.Tests/Context/FallbackAttachmentOwnershipTests.cs
git commit -m "Detach only the prefix the attach invoked, and do not stop at a failure

An attach that throws at index k never notified the interceptors after it, so
detaching the whole recorded set would unbalance them. The thrower stays in
the prefix because it may have mutated itself.

The detach loop continues past a failure and reports the first one afterwards:
the record is already claimed by then, so an interceptor skipped here could
never be balanced by a later removal."
```

---

### Task 4: Keep the edge removable when the invalidation walk fails

`TryBeginFallbackAttachment` publishes, then invalidates, then returns the record. If the invalidation throws, the caller's `finally` never runs, the record stays unattached, and every later removal defers to an attach that will never complete. The edge becomes permanently unremovable.

**Files:**
- Modify: `src/Namotion.Interceptor/InterceptorSubjectContext.cs`

**Interfaces:**
- Consumes: `CompleteFallbackAttachment` from Task 1.
- Produces: nothing new.

- [ ] **Step 1: Guard the post-publish invalidation**

Replace the tail of `TryBeginFallbackAttachment`:

```csharp
        try
        {
            InvalidateUsingContexts();
        }
        catch
        {
            // The edge is already visible. Leaving the record unattached here would make it
            // permanently unremovable, because every later removal defers to an attach that will
            // never complete.
            CompleteFallbackAttachment(attachment, 0);
            throw;
        }

        return attachment;
    }
```

- [ ] **Step 2: Verify the build and the suites**

Run: `dotnet build src/Namotion.Interceptor.slnx -v q --nologo`

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

Run: `dotnet test src/Namotion.Interceptor.Tests --filter "Category!=Integration" -v q --nologo`

Expected: PASS.

This step has no dedicated test. `InvalidateUsingContexts` fails only on `OutOfMemoryException` or a thread interrupt, neither of which can be provoked without a seam that would itself be more risk than the guard. The reasoning is recorded in the code comment.

- [ ] **Step 3: Commit**

```bash
git add src/Namotion.Interceptor/InterceptorSubjectContext.cs
git commit -m "Keep a published fallback edge removable when its invalidation fails

The record is linked and the edge published before the invalidation walk runs.
If that walk exits, the caller's finally never marks the record attached, so
every later removal defers to an attach that never completes and the edge can
never be taken out."
```

---

### Task 5: Regression tests for the cyclic and acyclic detach cases

These pin defects 3, 4 and 5, plus the declared behaviour change on an add that closes a cycle. The shapes matter: a pure two-executor cycle records no interceptors on either side, so its detach loop runs zero times and nothing throws. To see the throw, the record has to be captured non-empty and the chain has to turn cyclic afterwards.

**Files:**
- Test: `src/Namotion.Interceptor.Tests/Context/FallbackAttachmentOwnershipTests.cs`

**Interfaces:**
- Consumes: the executor behaviour from Tasks 1 and 3.
- Produces: nothing.

- [ ] **Step 1: Write the tests**

Add to `FallbackAttachmentOwnershipTests`:

```csharp
    [Fact]
    public void WhenChainBecomesCyclicAfterAttach_ThenTheEdgeIsRemovedAndTheCallThrows()
    {
        // Arrange: the record has to be captured non-empty and the chain has to turn cyclic
        // afterwards. On a pure two-executor cycle both records are empty, the detach loop runs
        // zero times, and the removal succeeds silently.
        var rootContext = InterceptorSubjectContext.Create();
        rootContext.AddService<ILifecycleInterceptor>(new CountingLifecycleInterceptor());

        var first = ((IInterceptorSubject)new ContextProbeSubject()).Context;
        var second = ((IInterceptorSubject)new ContextProbeSubject()).Context;

        Assert.True(second.AddFallbackContext(rootContext));
        Assert.True(first.AddFallbackContext(second));
        Assert.True(second.RemoveFallbackContext(rootContext));
        Assert.True(second.AddFallbackContext(first));

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => first.RemoveFallbackContext(second));
        Assert.False(HasFallback(first, second));
    }

    [Fact]
    public void WhenAttachResolveThrows_ThenNoEdgeIsRegistered()
    {
        // Arrange: a pure cycle between two contexts, so resolving through it raises.
        var first = ((IInterceptorSubject)new ContextProbeSubject()).Context;
        var second = ((IInterceptorSubject)new ContextProbeSubject()).Context;
        var third = ((IInterceptorSubject)new ContextProbeSubject()).Context;

        Assert.True(first.AddFallbackContext(second));
        Assert.True(second.AddFallbackContext(first));

        // Act & Assert: third's add resolves through the existing cycle and must commit nothing.
        Assert.Throws<InvalidOperationException>(() => third.AddFallbackContext(first));
        Assert.False(HasFallback(third, first));
    }

    [Fact]
    public void WhenAddClosesADelegationCycle_ThenTheEdgeIsRegisteredAndNothingThrows()
    {
        // Arrange: closing the circle needs both ends empty, so the resolve returns nothing and
        // the callback loop has no iterations. This raised before the resolve moved ahead of the
        // publish, so it is a declared behaviour change.
        var first = ((IInterceptorSubject)new ContextProbeSubject()).Context;
        var second = ((IInterceptorSubject)new ContextProbeSubject()).Context;
        Assert.True(second.AddFallbackContext(first));

        // Act
        var added = first.AddFallbackContext(second);

        // Assert
        Assert.True(added);
        Assert.True(HasFallback(first, second));
    }

    [Fact]
    public void WhenFallbackIsMutated_ThenBothCallbackSetsSeeTheEdge()
    {
        // Arrange: both orderings are forced, not chosen. The callbacks resolve their handlers
        // through the subject's own context, which finds nothing unless the edge is in place, so
        // attach must run after the publish and detach before the removal. Inverting either one
        // silently drops every lifecycle event for the subtree.
        var parentContext = InterceptorSubjectContext.Create();
        var interceptor = new EdgeObservingLifecycleInterceptor();
        parentContext.AddService<ILifecycleInterceptor>(interceptor);

        var childContext = ((IInterceptorSubject)new ContextProbeSubject()).Context;

        // Act
        Assert.True(childContext.AddFallbackContext(parentContext));
        Assert.True(childContext.RemoveFallbackContext(parentContext));

        // Assert
        Assert.True(interceptor.SawEdgeOnAttach, "The attach callback ran before the edge was published.");
        Assert.True(interceptor.SawEdgeOnDetach, "The detach callback ran after the edge was removed.");
    }

    [Fact]
    public void WhenAcyclicFallbackIsRemoved_ThenTheChildIsNoLongerRetained()
    {
        // Arrange
        var parentContext = InterceptorSubjectContext.Create();
        parentContext.AddService<ILifecycleInterceptor>(new CountingLifecycleInterceptor());

        // Act: in its own frame so no local keeps the subject alive for the probe below.
        var probe = AttachAndDetach(parentContext);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // Assert
        Assert.False(probe.IsAlive, "The parent context still retains the detached child.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AttachAndDetach(InterceptorSubjectContext parentContext)
    {
        var child = new ContextProbeSubject();
        var childContext = ((IInterceptorSubject)child).Context;
        Assert.True(childContext.AddFallbackContext(parentContext));
        Assert.True(childContext.RemoveFallbackContext(parentContext));
        return new WeakReference(child);
    }

    private sealed class EdgeObservingLifecycleInterceptor : ILifecycleInterceptor
    {
        internal bool SawEdgeOnAttach;

        internal bool SawEdgeOnDetach;

        public void AttachSubjectToContext(IInterceptorSubject subject)
        {
            SawEdgeOnAttach = !subject.Context.GetServices<ILifecycleInterceptor>().IsEmpty;
        }

        public void DetachSubjectFromContext(IInterceptorSubject subject)
        {
            SawEdgeOnDetach = !subject.Context.GetServices<ILifecycleInterceptor>().IsEmpty;
        }
    }

    private static bool HasFallback(IInterceptorSubjectContext context, IInterceptorSubjectContext fallback)
    {
        var state = ContextStateReflection.GetState((InterceptorSubjectContext)context);
        var fallbackContexts = (System.Collections.IEnumerable)state
            .GetType()
            .GetField("FallbackContexts", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(state)!;

        foreach (var entry in fallbackContexts)
        {
            if (ReferenceEquals(entry, fallback))
            {
                return true;
            }
        }

        return false;
    }
```

- [ ] **Step 2: Run the tests**

Run: `dotnet test src/Namotion.Interceptor.Tests --filter "FullyQualifiedName~FallbackAttachmentOwnershipTests" -v q --nologo`

Expected: PASS, 9 tests. If `WhenChainBecomesCyclicAfterAttach` reports no exception, the construction is wrong: check that `rootContext` still carried a lifecycle interceptor when `first.AddFallbackContext(second)` ran, since that is what makes `first`'s record non-empty.

- [ ] **Step 3: Commit**

```bash
git add src/Namotion.Interceptor.Tests/Context/FallbackAttachmentOwnershipTests.cs
git commit -m "Cover cyclic detach, atomic add failure, and the cycle-closing add

Removing an edge whose chain turned cyclic after the attach raises, because
the recorded callback resolves its handlers through a chain that no longer
resolves, but the edge comes out anyway.

An add whose pre-commit resolve raises commits nothing. An add that closes the
circle now succeeds, since both ends are empty at that point and there are no
callbacks to run."
```

---

### Task 6: Benchmark against master

The record costs an allocation per edge and the removal costs a second lock acquisition. AGENTS.md ranks allocations above CPU, so this needs a number rather than a prediction. `RegistryBenchmark` is the relevant suite because it is attach heavy.

**Files:**
- No source changes. Produces a report file in the repository root.

**Interfaces:**
- Consumes: the complete implementation from Tasks 1 through 5.
- Produces: benchmark numbers for the PR description.

- [ ] **Step 1: Run the comparison**

Run: `pwsh scripts/benchmark.ps1 -Filter "*RegistryBenchmark*" -LaunchCount 3`

Expected: a `benchmark_YYYY-MM-DD_HHmmss.md` file comparing this branch against `master`. Do not hand-roll this with `dotnet run`; the script handles the base-branch checkout and the comparison.

- [ ] **Step 2: Read the result and decide**

The two rows that matter are `AddLotsOfPreviousCars` (attach heavy) and `ChangeAllTires` (re-parenting). Check both `Mean` and `Allocated`.

If allocations rise by more than a few percent on either, stop and report rather than continuing. The fallback position is an inline single-edge representation on the context instead of a linked node, which is a design change and needs a decision, not a tweak.

- [ ] **Step 3: Record the numbers**

Add the comparison table to the PR description under a "Performance" heading, stating the commit it was measured at. Do not commit the generated report file.

---

### Task 7: Amend the issues

#402's own Fix section is wrong and would lose every detach event, and three of its acceptance criteria overstate what this delivers. It cannot be closed truthfully until its text matches.

**Files:**
- No source changes. GitHub issue edits.

**Interfaces:**
- Consumes: the implementation and its measured behaviour.
- Produces: nothing.

- [ ] **Step 1: Draft the #402 amendment and get approval**

Draft edits, and post nothing until the repository owner approves the text:

- strike the "Make `base` the arbiter, and resolve after it" Fix section, keeping a note that it was measured to lose every detach event in `Namotion.Interceptor.Tracking.Tests`, so nobody re-proposes it
- strike the boldface claim that the original "resolve before removal" objection was wrong, which is itself wrong
- defect 3: restate the criterion as "the edge is always removed", since the call can still raise
- defect 5 and Tests item 5: scope to the acyclic case
- Tests item 2: rename away from "a remove racing an add does not undo the add" to the guarantee that actually holds, which is that no thread removes an edge whose record it does not own and the callbacks run exactly once
- Scope: invoke #402's own sanctioned split for #207

- [ ] **Step 2: Comment on #384**

Post a comment recording that the detach side of the same exception family is now reachable and unfixed: on a cyclic chain the lifecycle handlers are unreachable from every context on the loop, so `LifecycleInterceptor._attachedSubjects` and `SubjectRegistry._knownSubjects` both retain the subtree. Include the measurement:

```
childCount = 0:  threw / fallbacks after = 0 / _attachedSubjects = 0 / retained: []
childCount = 2:  threw / fallbacks after = 0 / _attachedSubjects = 2 / retained: [CC1, S1]
```

State that the existing comment's item 2, a throwing add leaving a committed edge, is delivered by this work.

- [ ] **Step 3: Comment on #207**

Post a comment noting that the atomic removal primitive #207 needs now exists, naming `TryTakeFallbackAttachment` and `CompleteFallbackContextRemoval`.

- [ ] **Step 4: Close #402 on merge**

Only after the amendment is approved and posted.

---

### Task 8: Structural round, once the behaviour is proven

Tasks 1 through 5 add roughly 110 lines to a file already over 1,100. That is accepted while the behaviour is being established, and paid back here. Do this only after everything above is green, so a structural change is never mixed with a behavioural one and the test suite is the safety net for the move.

**Files:**
- Modify: `src/Namotion.Interceptor/InterceptorSubjectContext.cs`
- Create: whatever the chosen shape needs

**Interfaces:**
- Consumes: everything from Tasks 1 through 5.
- Produces: identical behaviour. No test may change.

- [ ] **Step 1: Pick a shape**

Not `partial`, which hides the size rather than reducing it. Two candidates, both mechanical:

- a `static class FallbackAttachmentList` taking `ref FallbackAttachment? head`, holding find, link and unlink. Removes about 40 lines and makes the list independently testable, leaving the four locked operations on the context because they need `_mutationLock`, `_state`, `PublishState`, `InvalidateUsingContexts` and `GetOrCreateUsedByContexts`, all private.
- a `sealed class FallbackAttachmentRegistry` instance field owning the list and its phase transitions, with the context passing the locked section in. Removes more, at the cost of one extra allocation per context that uses it, so it needs the Task 6 benchmark rerun.

Prefer the first unless the second measurably reads better; it has no allocation cost and no benchmark obligation.

- [ ] **Step 2: Move the code with no behavioural change**

Cut and paste only. If a diff line changes behaviour, it belongs in an earlier task, not here.

- [ ] **Step 3: Verify nothing moved**

Run: `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration" -v q --nologo`

Expected: PASS, same counts as after Task 5. Any change in behaviour means the move was not mechanical.

Run: `dotnet build src/Namotion.Interceptor.slnx -v q --nologo`

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 4: Commit**

```bash
git add src/Namotion.Interceptor/
git commit -m "Extract the fallback attachment list from the context class

Pure move, no behaviour change. The list operations are independent of the
context's locking and read better on their own."
```

---

## Definition of Done

- `dotnet build src/Namotion.Interceptor.slnx` succeeds with 0 warnings
- `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"` passes in full
- `VerifyChecksTests.PublicApi.verified.txt` is unchanged
- no `partial` was used to split `InterceptorSubjectContext`, and Task 8 has reduced its growth
- the handoff mutation in Task 1 Step 8 has been shown to fail a test
- the benchmark comparison is recorded in the PR description
- the #402 amendment is approved and posted
