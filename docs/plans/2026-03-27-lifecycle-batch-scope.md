# Lifecycle Batch Scope Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Replace SuppressRemoval (which desynchronized `_attachedSubjects` and `_knownSubjects`) with a lifecycle-level batch scope that defers `isLastDetach` processing, keeping both maps synchronized during `SubjectUpdateApplier.ApplyUpdate`.

**Architecture:** Add `CreateBatchScope()` to `LifecycleInterceptor` using `[ThreadStatic]` counter and deferred set. When `isLastDetach` occurs during a batch, the subject stays in `_attachedSubjects` with an empty reference set. On scope dispose, only genuinely orphaned subjects execute the full detach path. Remove `SuppressRemoval` from `SubjectRegistry` and `PreResolveSubjects` from `SubjectUpdateApplyContext` (both superseded).

**Tech Stack:** C# 13, .NET 9.0, xUnit

---

### Task 1: Add batch scope to LifecycleInterceptor

**Files:**
- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/LifecycleInterceptor.cs`

**Step 1: Add ThreadStatic fields and nested scope class after line 16**

Add after the existing `_subjectHashSetPool` field (line 16):

```csharp
[ThreadStatic]
private static int s_batchScopeCount;

[ThreadStatic]
private static HashSet<IInterceptorSubject>? s_deferredLastDetaches;

private sealed class BatchScope(LifecycleInterceptor lifecycle) : IDisposable
{
    private int _disposed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            lifecycle.EndBatchScope();
        }
    }
}
```

**Step 2: Add CreateBatchScope and EndBatchScope methods**

Add as public/private methods on the class:

```csharp
/// <summary>
/// Creates a batch scope that defers isLastDetach processing.
/// Subjects whose last property reference is removed during the scope
/// stay in _attachedSubjects with an empty set. On dispose, only
/// subjects still with an empty set are detached.
/// PropertyReferenceRemoved/Added always fire immediately.
/// </summary>
public IDisposable CreateBatchScope()
{
    s_batchScopeCount++;
    s_deferredLastDetaches ??= [];
    return new BatchScope(this);
}

private void EndBatchScope()
{
    lock (_attachedSubjects)
    {
        s_batchScopeCount--;
        if (s_batchScopeCount == 0 && s_deferredLastDetaches is { Count: > 0 })
        {
            foreach (var subject in s_deferredLastDetaches)
            {
                if (_attachedSubjects.TryGetValue(subject, out var set) && set.Count == 0)
                {
                    // Genuinely orphaned — execute full detach.
                    // We cannot call DetachFromProperty (it expects a specific property),
                    // so inline the isLastDetach cleanup path here.
                    _attachedSubjects.Remove(subject);

                    List<(IInterceptorSubject subject, PropertyReference property, object? index)>? children = null;
                    foreach (var entry in subject.Properties)
                    {
                        var subjectProperty = new PropertyReference(subject, entry.Key);
                        var metadata = entry.Value;
                        if (metadata is { IsIntercepted: true } && metadata.Type.CanContainSubjects())
                        {
                            if (_lastProcessedValues.TryGetValue(subjectProperty, out var lastProcessed) && lastProcessed is not null)
                            {
                                children ??= GetList();
                                FindSubjectsInProperty(subjectProperty, lastProcessed, null, children, null);
                            }

                            _lastProcessedValues.Remove(subjectProperty);
                        }

                        subject.DetachSubjectProperty(subjectProperty);
                    }

                    var count = subject.DecrementReferenceCount();
                    var change = new SubjectLifecycleChange
                    {
                        Subject = subject,
                        ReferenceCount = count,
                        IsContextDetach = true
                    };

                    SubjectDetaching?.Invoke(change);

                    if (subject is ILifecycleHandler subjectHandler)
                    {
                        subjectHandler.HandleLifecycleChange(change);
                    }

                    var array = subject.Context.GetServices<ILifecycleHandler>();
                    for (var i = 0; i < array.Length; i++)
                    {
                        array[i].HandleLifecycleChange(change);
                    }

                    if (children is not null)
                    {
                        foreach (var child in children)
                        {
                            DetachFromProperty(child.subject, subject.Context, child.property, child.index);
                        }

                        ReturnList(children);
                    }
                }
                // else: re-attached during batch → skip
            }

            s_deferredLastDetaches.Clear();
        }
    }
}
```

**Step 3: Modify DetachFromProperty to defer during batch scope**

In `DetachFromProperty` (line 200-266), replace the `isLastDetach` block (lines 213-237) with:

```csharp
if (isLastDetach)
{
    if (s_batchScopeCount > 0)
    {
        // Defer the full detach — subject stays in _attachedSubjects with empty set.
        // PropertyReferenceRemoved still fires below (per-link, always immediate).
        // ContextDetach deferred until scope dispose.
        s_deferredLastDetaches ??= [];
        s_deferredLastDetaches.Add(subject);
    }
    else
    {
        // Immediate detach (existing behavior)
        _attachedSubjects.Remove(subject);

        foreach (var entry in subject.Properties)
        {
            var subjectProperty = new PropertyReference(subject, entry.Key);

            var metadata = entry.Value;
            if (metadata is { IsIntercepted: true } && metadata.Type.CanContainSubjects())
            {
                if (_lastProcessedValues.TryGetValue(subjectProperty, out var lastProcessed) && lastProcessed is not null)
                {
                    children ??= GetList();
                    FindSubjectsInProperty(subjectProperty, lastProcessed, null, children, null);
                }

                _lastProcessedValues.Remove(subjectProperty);
            }

            subject.DetachSubjectProperty(subjectProperty);
        }
    }
}
```

Then change the `IsContextDetach` assignment (line 247) to only be true when NOT deferred:

```csharp
IsContextDetach = isLastDetach && s_batchScopeCount == 0
```

And guard the `SubjectDetaching` invocation (line 250-253):

```csharp
if (isLastDetach && s_batchScopeCount == 0)
{
    SubjectDetaching?.Invoke(change);
}
```

**Step 4: Build**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: Build succeeds

**Step 5: Run existing tests**

Run: `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`
Expected: All existing tests pass (no behavioral change when batch scope not active)

---

### Task 2: Add batch scope tests

**Files:**
- Create: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/BatchScopeTests.cs`

**Step 1: Write tests**

```csharp
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

public class BatchScopeTests
{
    [Fact]
    public void BasicBatchScope_DetachDeferredThenProcessedOnDispose()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var child = new Models.Person { FirstName = "Child" };
        var parent = new Models.Person(context) { FirstName = "Parent", Mother = child };
        var lifecycle = context.TryGetLifecycleInterceptor()!;
        var idRegistry = context.GetService<ISubjectIdRegistry>();

        child.SetSubjectId("childId");

        // Act — detach within batch scope
        var scope = lifecycle.CreateBatchScope();
        parent.Mother = null;

        // Assert — still in registry during scope
        Assert.True(idRegistry.TryGetSubjectById("childId", out _));

        // Act — dispose scope
        scope.Dispose();

        // Assert — now removed (genuinely orphaned)
        Assert.False(idRegistry.TryGetSubjectById("childId", out _));
    }

    [Fact]
    public void MoveBetweenProperties_SubjectStaysRegistered()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var child = new Models.Person { FirstName = "Child" };
        var parent = new Models.Person(context) { FirstName = "Parent", Mother = child };
        var lifecycle = context.TryGetLifecycleInterceptor()!;
        var idRegistry = context.GetService<ISubjectIdRegistry>();

        child.SetSubjectId("childId");

        // Act — move child from Mother to Father within batch scope
        using (lifecycle.CreateBatchScope())
        {
            parent.Mother = null;   // detach (deferred)
            parent.Father = child;  // reattach to different property
        }

        // Assert — child stays registered (was moved, not removed)
        Assert.True(idRegistry.TryGetSubjectById("childId", out var found));
        Assert.Same(child, found);
    }

    [Fact]
    public void GenuineRemoval_CleanedUpOnDispose()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var child = new Models.Person { FirstName = "Child" };
        var parent = new Models.Person(context) { FirstName = "Parent", Mother = child };
        var lifecycle = context.TryGetLifecycleInterceptor()!;
        var idRegistry = context.GetService<ISubjectIdRegistry>();

        child.SetSubjectId("childId");

        // Act — detach without reattach
        using (lifecycle.CreateBatchScope())
        {
            parent.Mother = null;
        }

        // Assert — genuinely orphaned, removed on dispose
        Assert.False(idRegistry.TryGetSubjectById("childId", out _));
    }

    [Fact]
    public void NoScope_ExistingBehaviorUnchanged()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var child = new Models.Person { FirstName = "Child" };
        var parent = new Models.Person(context) { FirstName = "Parent", Mother = child };
        var idRegistry = context.GetService<ISubjectIdRegistry>();

        child.SetSubjectId("childId");

        // Act — detach without any scope
        parent.Mother = null;

        // Assert — immediate removal
        Assert.False(idRegistry.TryGetSubjectById("childId", out _));
    }

    [Fact]
    public void NestedScopes_ProcessedOnOuterDispose()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var child = new Models.Person { FirstName = "Child" };
        var parent = new Models.Person(context) { FirstName = "Parent", Mother = child };
        var lifecycle = context.TryGetLifecycleInterceptor()!;
        var idRegistry = context.GetService<ISubjectIdRegistry>();

        child.SetSubjectId("childId");

        // Act — nested scopes
        var outer = lifecycle.CreateBatchScope();
        var inner = lifecycle.CreateBatchScope();

        parent.Mother = null;

        // Dispose inner — no processing yet
        inner.Dispose();
        Assert.True(idRegistry.TryGetSubjectById("childId", out _));

        // Dispose outer — processes deferred detaches
        outer.Dispose();
        Assert.False(idRegistry.TryGetSubjectById("childId", out _));
    }

    [Fact]
    public void ThreadIsolation_UnscopedThreadNotAffected()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var childA = new Models.Person { FirstName = "ChildA" };
        var childB = new Models.Person { FirstName = "ChildB" };
        var parent = new Models.Person(context) { FirstName = "Parent", Mother = childA, Father = childB };
        var lifecycle = context.TryGetLifecycleInterceptor()!;
        var idRegistry = context.GetService<ISubjectIdRegistry>();

        childA.SetSubjectId("childAId");
        childB.SetSubjectId("childBId");

        // Act — scope on current thread, detach childB on separate thread
        using (lifecycle.CreateBatchScope())
        {
            // Detach childA on this thread (scoped — deferred)
            parent.Mother = null;

            // Detach childB on another thread (no scope — immediate)
            var thread = new Thread(() => parent.Father = null);
            thread.Start();
            thread.Join();

            // Assert — childA still in registry (deferred), childB gone (immediate)
            Assert.True(idRegistry.TryGetSubjectById("childAId", out _));
            Assert.False(idRegistry.TryGetSubjectById("childBId", out _));
        }

        // After scope dispose, childA is also removed
        Assert.False(idRegistry.TryGetSubjectById("childAId", out _));
    }
}
```

**Step 2: Run tests**

Run: `dotnet test src/Namotion.Interceptor.Tracking.Tests --filter "Category!=Integration"`
Expected: All tests pass

---

### Task 3: Wire batch scope into SubjectUpdateApplier

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors/Updates/Internal/SubjectUpdateApplier.cs`

**Step 1: Replace SuppressRemoval with batch scope**

Replace lines 24-32 (the disabled SuppressRemoval block):

```csharp
context.Initialize(subject.Context, update.Subjects, update.CompleteSubjectIds, subjectFactory, transformValueBeforeApply);
context.PreResolveSubjects(update.Subjects.Keys, context.SubjectIdRegistry);

// TODO: SuppressRemoval disabled for testing — see cycle 6 failure investigation.
// ...
if (true)
{
```

With:

```csharp
context.Initialize(subject.Context, update.Subjects, update.CompleteSubjectIds, subjectFactory, transformValueBeforeApply);

// Batch scope: defer isLastDetach processing so subjects moving between
// structural properties within this update stay in _attachedSubjects and
// _knownSubjects throughout. Fixes the apply-path subject move race.
var lifecycle = subject.Context.TryGetLifecycleInterceptor();
using (lifecycle?.CreateBatchScope())
{
```

Add `using Namotion.Interceptor.Tracking.Lifecycle;` to imports if not present.

**Step 2: Replace TryResolveSubject with direct TryGetSubjectById**

In the Step 2 loop (line 56), replace:

```csharp
if (context.TryResolveSubject(subjectId, out var targetSubject))
```

With:

```csharp
if (context.SubjectIdRegistry.TryGetSubjectById(subjectId, out var targetSubject))
```

**Step 3: Remove the `using Namotion.Interceptor.Registry;` import** if it was only needed for `SubjectRegistry` (SuppressRemoval cast). Check if other usages remain first.

**Step 4: Build and test**

Run: `dotnet build src/Namotion.Interceptor.slnx && dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`
Expected: Build succeeds, all tests pass

---

### Task 4: Remove PreResolveSubjects from SubjectUpdateApplyContext

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors/Updates/Internal/SubjectUpdateApplyContext.cs`

**Step 1: Remove pre-resolution fields and methods**

Remove:
- Line 13: `private readonly Dictionary<string, IInterceptorSubject> _preResolvedSubjects = [];`
- Lines 62-73: `PreResolveSubjects` method
- Lines 80-88: `TryResolveSubject` method
- Line 99: `_preResolvedSubjects.Clear();` in `Clear()`

The `Initialize` method no longer needs `rootContext` parameter for pre-resolution (it still needs it for `SubjectRegistry` and `SubjectIdRegistry`).

**Step 2: Build and test**

Run: `dotnet build src/Namotion.Interceptor.slnx && dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`
Expected: Build succeeds, all tests pass

---

### Task 5: Remove SuppressRemoval from SubjectRegistry

**Files:**
- Modify: `src/Namotion.Interceptor.Registry/SubjectRegistry.cs`
- Delete: `src/Namotion.Interceptor.Registry.Tests/SuppressRemovalTests.cs`

**Step 1: Remove SuppressRemoval fields and methods from SubjectRegistry**

Remove:
- Lines 10-14: `[ThreadStatic] _suppressRemovalCount` and `_deferredDetaches` fields
- Lines 109-114: `SuppressRemoval()` method
- Lines 116-159: `ResumeRemoval()` method
- The `RemovalSuppressionScope` nested class (find it in the file and remove)

**Step 2: Simplify HandleLifecycleChange IsContextDetach path**

In `HandleLifecycleChange`, the `IsContextDetach` block (lines 232-272) has an `if (_suppressRemovalCount > 0)` branch. Remove the deferred branch and keep only the immediate path:

```csharp
if (change.IsContextDetach)
{
    // Immediate removal
    foreach (var property in registeredSubject.Properties)
    {
        if (!property.CanContainSubjects)
            continue;

        foreach (var child in property.Children)
        {
            var childRegistered = _knownSubjects.GetValueOrDefault(child.Subject);
            childRegistered?.RemoveParentsByProperty(property);
        }

        property.ClearChildren();
    }

    _knownSubjects.Remove(change.Subject);

    if (_subjectIdToSubject.Count > 0)
    {
        var subjectId = change.Subject.TryGetSubjectId();
        if (subjectId is not null)
        {
            _subjectIdToSubject.Remove(subjectId);
        }
    }
}
```

**Step 3: Delete SuppressRemovalTests.cs**

Delete file: `src/Namotion.Interceptor.Registry.Tests/SuppressRemovalTests.cs`

**Step 4: Build and test**

Run: `dotnet build src/Namotion.Interceptor.slnx && dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`
Expected: Build succeeds, all tests pass

---

### Task 6: Verify clean diff against master

**Step 1: Check diff**

Run: `git diff master --stat -- src/`

Expected: Changes only in:
- `LifecycleInterceptor.cs` — batch scope additions
- `SubjectUpdateApplier.cs` — `CreateBatchScope()` call, no SuppressRemoval/PreResolveSubjects
- `SubjectUpdateApplyContext.cs` — no PreResolveSubjects/TryResolveSubject
- `SubjectRegistry.cs` — no SuppressRemoval additions (clean vs master)
- `SuppressRemovalTests.cs` — deleted (was added on this branch)
- `BatchScopeTests.cs` — new
- Plus existing fixes (12-16) in applier/registry files

Run: `git diff master -- src/Namotion.Interceptor.Registry/SubjectRegistry.cs | grep -c "SuppressRemoval\|_deferredDetaches\|_suppressRemovalCount\|RemovalSuppressionScope"`

Expected: `0` — no SuppressRemoval artifacts remain

**Step 2: Search for leftover references**

Run: `grep -r "SuppressRemoval\|PreResolveSubjects\|TryResolveSubject\|_preResolvedSubjects" src/ --include="*.cs"`

Expected: No matches (all removed)

---

### Task 7: Update documentation

**Files:**
- Modify: `docs/plans/fixes.md`

**Step 1: Add Fix 18 to fixes.md**

Add after Fix 17:

```markdown
---

## Fix 18: Lifecycle batch scope (replaces SuppressRemoval)

**Files changed:** `LifecycleInterceptor.cs`, `SubjectUpdateApplier.cs`, `SubjectUpdateApplyContext.cs`, `SubjectRegistry.cs`

**Cause:** SuppressRemoval (Fix 17 Part 1) deferred registry cleanup but left lifecycle removal immediate. This desynchronized `_attachedSubjects` and `_knownSubjects` — the parent-dead check fired for concurrent mutations on mid-move subjects, rolling back lifecycle processing and creating registry leaks (`refCount=0, actual:FOUND`). Failed at cycle 6 with no chaos.

**Fix:** Lifecycle batch scope on `LifecycleInterceptor`. When `isLastDetach` occurs during a batch, the subject stays in `_attachedSubjects` with an empty reference set. No child cleanup, no `_lastProcessedValues` removal, no `ContextDetach`. On scope dispose, subjects whose set is still empty are genuinely orphaned — execute full detach. Subjects re-attached during the batch are silently skipped.

Both `_attachedSubjects` and `_knownSubjects` stay synchronized at all times. The parent-dead check never fires for mid-move subjects. The CQP filter succeeds because the subject never leaves `_knownSubjects`.

**Replaces:** SuppressRemoval on SubjectRegistry (removed), PreResolveSubjects on SubjectUpdateApplyContext (removed — subjects stay in `_subjectIdToSubject` naturally during batch).

**Status:** Applied. All unit tests pass. Awaiting ConnectorTester validation.
```

**Step 2: Update Fix 17 status**

Change Fix 17 status to:
```
**Status:** Reverted. Replaced by Fix 18 (lifecycle batch scope).
```

Remove the "Regression" subsection from Fix 17 (the information is now in Fix 18's "Cause" section).
