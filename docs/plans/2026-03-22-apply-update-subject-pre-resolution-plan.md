# ApplyUpdate Subject Pre-Resolution Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Fix permanent property value loss when `ApplyUpdate`'s own structural changes detach a subject before Step 2 can apply its property values.

**Architecture:** Pre-resolve all subject IDs in `update.Subjects.Keys` to `IInterceptorSubject` references before processing any updates. Step 2 uses this cache instead of the live registry. Also revert the deferred `_pendingIdCleanup` queue in `SubjectRegistry` (which this fix replaces).

**Tech Stack:** C# 13, .NET 9, xUnit

**Design document:** `docs/plans/2026-03-22-apply-update-subject-pre-resolution-design.md`

---

### Task 1: Revert SubjectRegistry deferred cleanup to eager removal

The deferred `_pendingIdCleanup` queue was a global registry behavior change for what is an `ApplyUpdate`-scoped problem. Pre-resolution (Task 2) replaces it entirely. Revert to eager removal first so the problem is observable in tests.

**Files:**
- Modify: `src/Namotion.Interceptor.Registry/SubjectRegistry.cs`

**Step 1: Remove deferred cleanup infrastructure**

Remove the `_pendingIdCleanup` field (line 16), the `FlushPendingIdCleanup()` method (lines 206-216), and the `FlushPendingIdCleanup()` call in `HandleLifecycleChange` (line 109). Replace the deferred enqueue block (lines 181-193) with immediate removal:

```csharp
// In HandleLifecycleChange, the IsContextDetach block (replacing lines 177-194):
                    if (change.IsContextDetach)
                    {
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

The full diff for `SubjectRegistry.cs`:

1. Remove line 16 (`private readonly Queue<string> _pendingIdCleanup = new();`)
2. Remove line 109 (`FlushPendingIdCleanup();`)
3. Remove the comment block lines 106-108
4. Replace lines 177-194 (deferred enqueue in IsContextDetach) with the immediate removal above
5. Remove lines 200-216 (the `FlushPendingIdCleanup` method and its doc comment)
6. Remove lines 13-16 (the deferred cleanup comment block and queue field)

**Step 2: Build to verify no compile errors**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add src/Namotion.Interceptor.Registry/SubjectRegistry.cs
git commit -m "refactor: revert SubjectRegistry to eager ID cleanup on detach

Pre-resolution in SubjectUpdateApplyContext (next commit) replaces the
deferred _pendingIdCleanup queue. Eager removal is simpler and correct
— the registry should not hold onto stale entries."
```

---

### Task 2: Revert SubjectIdTests to expect immediate removal on detach

The test `SetSubjectId_WhenSubjectDetached_RemovesFromReverseIndexOnNextLifecycleEvent` was written for the deferred cleanup behavior. Revert it to expect immediate removal.

**Files:**
- Modify: `src/Namotion.Interceptor.Registry.Tests/SubjectIdTests.cs`

**Step 1: Replace the deferred cleanup test**

Replace the test at lines 60-84 with:

```csharp
    [Fact]
    public void SetSubjectId_WhenSubjectDetached_RemovesFromReverseIndex()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var child = new Models.Person { FirstName = "Child" };
        var parent = new Models.Person(context) { FirstName = "Parent", Mother = child };
        var idRegistry = context.GetService<ISubjectIdRegistry>();

        child.SetSubjectId("childId");
        Assert.True(idRegistry.TryGetSubjectById("childId", out _));

        // Act — detach child
        parent.Mother = null;

        // Assert — ID is immediately removed from reverse index
        Assert.False(idRegistry.TryGetSubjectById("childId", out _));
    }
```

**Step 2: Run registry tests to verify**

Run: `dotnet test src/Namotion.Interceptor.Registry.Tests --filter "FullyQualifiedName~SubjectIdTests"`
Expected: All tests PASS

**Step 3: Commit**

```bash
git add src/Namotion.Interceptor.Registry.Tests/SubjectIdTests.cs
git commit -m "test: update SubjectIdTests to expect immediate cleanup on detach"
```

---

### Task 3: Write failing test — structural change detaches subject before Step 2

Write a test that proves the problem: an `ApplyUpdate` whose Step 1 structural change detaches a subject, causing Step 2 to fail to find it and permanently lose its property values.

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors.Tests/Updates/StableIdApplyTests.cs`

**Step 1: Write the failing test**

Add this test to the `StableIdApplyTests` class:

```csharp
    [Fact]
    public void ApplyUpdate_WhenStructuralChangeDetachesSubject_ThenStep2PropertyUpdatesStillApplied()
    {
        // Arrange: root → child via object ref
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var child = new CycleTestNode { Name = "OriginalName" };
        var root = new CycleTestNode(context) { Name = "Root", Child = child };

        var rootId = root.GetOrAddSubjectId();
        var childId = child.GetOrAddSubjectId();

        // Build an update that BOTH removes child (structural) AND updates child's Name (value).
        // This happens when CQP batches a value change and a structural change together:
        //   1. Sender sets child.Name = "UpdatedName"
        //   2. Sender sets root.Child = null
        //   3. Both changes are flushed in the same CQP batch
        var update = new SubjectUpdate
        {
            Root = rootId,
            Subjects = new()
            {
                [rootId] = new()
                {
                    ["Child"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Object,
                        Id = null // Remove child
                    }
                },
                [childId] = new()
                {
                    ["Name"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Value,
                        Value = "UpdatedName"
                    }
                }
            }
        };

        // Act
        SubjectUpdateApplier.ApplyUpdate(root, update, new DefaultSubjectFactory());

        // Assert
        Assert.Null(root.Child); // structural change applied
        Assert.Equal("UpdatedName", child.Name); // value applied despite detach
    }
```

**Step 2: Run test to verify it fails**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~ApplyUpdate_WhenStructuralChangeDetachesSubject"`
Expected: FAIL — `child.Name` is still `"OriginalName"` because Step 2 can't find child by ID after Step 1 detached it.

---

### Task 4: Implement pre-resolution in SubjectUpdateApplyContext

Add the pre-resolved subjects cache and a `TryResolveSubject` method that checks the cache first, then falls back to the live registry.

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors/Updates/Internal/SubjectUpdateApplyContext.cs`

**Step 1: Add pre-resolution fields and methods**

Add a `Dictionary<string, IInterceptorSubject>?` field and two methods:

```csharp
    // Add field after _processedSubjectIds (line 12):
    private Dictionary<string, IInterceptorSubject>? _preResolvedSubjects;
```

Add the pre-resolve method (after the `Initialize` method):

```csharp
    /// <summary>
    /// Pre-resolves all subject IDs in <paramref name="subjectIds"/> to their
    /// <see cref="IInterceptorSubject"/> instances using the live registry.
    /// Must be called before any structural changes are applied, so that subjects
    /// detached by the apply's own processing can still be found in Step 2.
    /// </summary>
    public void PreResolveSubjects(
        IEnumerable<string> subjectIds,
        ISubjectIdRegistry idRegistry)
    {
        foreach (var subjectId in subjectIds)
        {
            if (idRegistry.TryGetSubjectById(subjectId, out var subject))
            {
                _preResolvedSubjects ??= [];
                _preResolvedSubjects[subjectId] = subject;
            }
        }
    }

    /// <summary>
    /// Tries to resolve a subject by ID. Checks the pre-resolved cache first
    /// (captured before structural changes), then falls back to the live registry
    /// (for subjects created during the apply, e.g., by structural processing).
    /// </summary>
    public bool TryResolveSubject(string subjectId, out IInterceptorSubject subject)
    {
        if (_preResolvedSubjects is not null &&
            _preResolvedSubjects.TryGetValue(subjectId, out subject!))
        {
            return true;
        }

        return SubjectIdRegistry.TryGetSubjectById(subjectId, out subject!);
    }
```

Add cleanup in the `Clear` method (after line 63):

```csharp
        _preResolvedSubjects?.Clear();
        _preResolvedSubjects = null;
```

**Step 2: Build to verify no compile errors**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: Build succeeded

---

### Task 5: Use pre-resolution in SubjectUpdateApplier Step 2

Wire up the pre-resolution: call `PreResolveSubjects` before processing, and use `TryResolveSubject` in the Step 2 loop instead of the live registry.

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors/Updates/Internal/SubjectUpdateApplier.cs`

**Step 1: Add PreResolveSubjects call and update Step 2**

In `ApplyUpdate`, after `context.Initialize(...)` (line 24), add:

```csharp
            context.PreResolveSubjects(update.Subjects.Keys, context.SubjectIdRegistry);
```

In the Step 2 loop (lines 43-54), replace `idRegistry.TryGetSubjectById` with `context.TryResolveSubject`. The full Step 2 block becomes:

```csharp
            // Always process remaining subjects by subject ID lookup.
            // When the root path ran above, it recursively processed subjects reachable
            // from the root's structural properties. But partial updates can contain changes
            // to subjects NOT reachable from the root's changed properties (e.g., a deeply
            // nested ObjectRef change in the same batch as a root scalar change).
            // TryMarkAsProcessed ensures no subject is processed twice.
            foreach (var (subjectId, properties) in update.Subjects)
            {
                // Subjects not found in the ID registry are expected: they are new subjects
                // whose structural parent (collection/dictionary/object ref) will create them
                // and apply their properties via ApplyPropertiesIfAvailable during this same update.
                if (context.TryResolveSubject(subjectId, out var targetSubject) &&
                    context.TryMarkAsProcessed(subjectId))
                {
                    ApplyPropertyUpdates(targetSubject, properties, context);
                }
            }
```

Note: the `var idRegistry = context.SubjectIdRegistry;` line (43) is no longer needed and should be removed.

**Step 2: Run the failing test — verify it now passes**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~ApplyUpdate_WhenStructuralChangeDetachesSubject"`
Expected: PASS

**Step 3: Run all unit tests**

Run: `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`
Expected: All PASS

**Step 4: Commit**

```bash
git add src/Namotion.Interceptor.Connectors/Updates/Internal/SubjectUpdateApplyContext.cs src/Namotion.Interceptor.Connectors/Updates/Internal/SubjectUpdateApplier.cs src/Namotion.Interceptor.Connectors.Tests/Updates/StableIdApplyTests.cs
git commit -m "fix: pre-resolve subject references before applying updates

ApplyUpdate's Step 2 lost property values when the apply's own structural
changes (Step 1) detached a subject from the registry. Pre-resolution
captures subject references before processing starts, so Step 2 always
finds subjects that existed at apply-start time."
```

---

## Summary of all changed files

| File | Change |
|------|--------|
| `src/Namotion.Interceptor.Registry/SubjectRegistry.cs` | Remove `_pendingIdCleanup` queue, `FlushPendingIdCleanup()`, restore eager ID removal on detach |
| `src/Namotion.Interceptor.Registry.Tests/SubjectIdTests.cs` | Revert test to expect immediate removal on detach |
| `src/Namotion.Interceptor.Connectors/Updates/Internal/SubjectUpdateApplyContext.cs` | Add `_preResolvedSubjects` dict, `PreResolveSubjects()`, `TryResolveSubject()` |
| `src/Namotion.Interceptor.Connectors/Updates/Internal/SubjectUpdateApplier.cs` | Call `PreResolveSubjects` before processing; Step 2 uses `TryResolveSubject` instead of live registry |
| `src/Namotion.Interceptor.Connectors.Tests/Updates/StableIdApplyTests.cs` | Add test: structural change detaches subject, Step 2 property update still applied |

## What is NOT changed

- Structural lookup call sites in `ApplyObjectUpdate` (line 168) and `ResolveOrCreateSubject` (line 235) continue using the live registry directly. `CompleteSubjectIds` handles failures there.
- No new registry API surface.
- No changes to `SubjectUpdateFactory`, `ChangeQueueProcessor`, or any other file.
