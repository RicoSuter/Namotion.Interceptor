# Deferred Subject Removal Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Prevent value changes from being silently dropped when subjects are momentarily unregistered during concurrent structural mutations. Two complementary fixes close two distinct race windows.

**Architecture:**
1. **SuppressRemoval on applier** (Tasks 1-4): Add `SuppressRemoval()` to `SubjectRegistry` using `[ThreadStatic]` counter and deferred detach set. During suppression, context-detach cleanup is deferred. On resume, check actual parent state to determine if removal is still warranted. Wrap `SubjectUpdateApplier.ApplyUpdate` in this scope. See `docs/design/deferred-subject-removal.md` for full rationale.
2. **CQP filter resilience** (Task 5): Make server-side CQP filters (WebSocket, MQTT) tolerant of momentarily-unregistered subjects by falling back to a subject ID check instead of dropping value changes. This closes the local-mutation race where structural mutations go through the lifecycle directly, not through the applier.

**Tech Stack:** C# 13, .NET 9.0, xUnit

---

### Task 1: Implement SuppressRemoval on SubjectRegistry

**Files:**
- Modify: `src/Namotion.Interceptor.Registry/SubjectRegistry.cs`

**Step 1: Add ThreadStatic fields and SuppressRemoval method**

Add after the existing field declarations (line 11):

```csharp
[ThreadStatic]
private static int s_suppressRemovalCount;

[ThreadStatic]
private static HashSet<IInterceptorSubject>? s_deferredDetaches;
```

Add public method:

```csharp
/// <summary>
/// Suppresses subject removal from the registry during the returned scope.
/// While suppressed, context-detach cleanup (removal from _knownSubjects,
/// _subjectIdToSubject, and parent/child cleanup) is deferred. On dispose,
/// only subjects that are genuinely orphaned (no parents) are removed.
/// PropertyReferenceRemoved/Added always run immediately.
/// </summary>
/// <returns>A disposable scope. On dispose, processes deferred removals.</returns>
public IDisposable SuppressRemoval()
{
    s_suppressRemovalCount++;
    s_deferredDetaches ??= [];
    return new RemovalSuppressionScope(this);
}
```

**Step 2: Add ResumeRemoval method**

```csharp
private void ResumeRemoval()
{
    lock (_knownSubjects)
    {
        s_suppressRemovalCount--;
        if (s_suppressRemovalCount == 0 && s_deferredDetaches is { Count: > 0 })
        {
            foreach (var subject in s_deferredDetaches)
            {
                if (_knownSubjects.TryGetValue(subject, out var registered) &&
                    registered.Parents.Length == 0)
                {
                    // Genuinely orphaned — execute deferred context-detach cleanup
                    foreach (var property in registered.Properties)
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

                    _knownSubjects.Remove(subject);

                    if (_subjectIdToSubject.Count > 0)
                    {
                        var subjectId = subject.TryGetSubjectId();
                        if (subjectId is not null)
                        {
                            _subjectIdToSubject.Remove(subjectId);
                        }
                    }
                }
            }

            s_deferredDetaches.Clear();
        }
    }
}
```

**Step 3: Add RemovalSuppressionScope nested class**

```csharp
private sealed class RemovalSuppressionScope(SubjectRegistry registry) : IDisposable
{
    private int _disposed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            registry.ResumeRemoval();
        }
    }
}
```

**Step 4: Modify HandleLifecycleChange — context-detach path**

In the `IsContextDetach` block (currently starting around line 167), wrap the cleanup in a suppression check:

Replace the existing context-detach block:
```csharp
if (change.IsContextDetach)
{
    // Remove stale parent references from children...
    // (existing cleanup code)
    _knownSubjects.Remove(change.Subject);
    // Clean up subject ID reverse index
    // (existing cleanup code)
}
```

With:
```csharp
if (change.IsContextDetach)
{
    if (s_suppressRemovalCount > 0)
    {
        // Defer removal — subject stays in both maps and
        // parent/child cleanup is skipped until scope dispose.
        s_deferredDetaches ??= [];
        s_deferredDetaches.Add(change.Subject);
    }
    else
    {
        // Immediate removal (existing behavior)
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
}
```

**Step 5: Build**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: Build succeeds

**Step 6: Run existing tests**

Run: `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`
Expected: All existing tests pass (no behavioral change when suppression not active)

**Step 7: Commit**

```bash
git add src/Namotion.Interceptor.Registry/SubjectRegistry.cs
git commit -m "feat: Add SuppressRemoval to SubjectRegistry for deferred context-detach cleanup"
```

---

### Task 2: Add unit tests for SuppressRemoval

**Files:**
- Modify: `src/Namotion.Interceptor.Registry.Tests/SubjectIdTests.cs` (or create new file `src/Namotion.Interceptor.Registry.Tests/SuppressRemovalTests.cs`)

**Step 1: Write tests**

Tests needed (use existing test patterns from `SubjectIdTests.cs` for setup):

1. **BasicSuppression_DetachDeferredThenProcessedOnResume** — Attach subject, start scope, detach subject, verify still in `_knownSubjects` and `_subjectIdToSubject`, dispose scope, verify removed from both.

2. **MoveBetweenProperties_SubjectStaysRegistered** — Attach subject as child of DictA, start scope, remove from DictA (triggers detach), add to DictB (triggers reattach), dispose scope, verify subject still registered with DictB as parent.

3. **GenuineRemoval_RemovedOnResume** — Attach subject, start scope, detach subject (no reattach), dispose scope, verify removed.

4. **NoScope_ExistingBehaviorUnchanged** — Attach and detach without scope, verify immediate removal (regression guard).

5. **NestedScopes_ProcessedOnOuterDispose** — Start outer scope, start inner scope, detach subject, dispose inner (no processing), verify still registered, dispose outer (processes), verify removed.

6. **SwapBetweenProperties_BothStayRegistered** — Two subjects X and Y. Start scope, swap (X: DictA→DictB, Y: DictB→DictA), dispose scope, verify both still registered.

7. **ThreadIsolation_UnscopedThreadNotAffected** — Start scope on Thread A, on Thread B detach a subject (no scope), verify Thread B's detach is immediate (not deferred).

8. **CrossThreadReattach_SkipsRemoval** — Start scope on Thread A, defer detach of X. On Thread B (no scope), reattach X. Dispose scope on Thread A, verify X still registered (has parents from Thread B).

9. **DeferredDetach_ThenImmediateDetachOnOtherThread** — Start scope on Thread A, defer detach of X. On Thread B (no scope), detach X immediately. Dispose scope on Thread A, verify no crash (X already removed, resume is no-op).

**Step 2: Run tests**

Run: `dotnet test src/Namotion.Interceptor.Registry.Tests --filter "Category!=Integration"`
Expected: All tests pass

**Step 3: Commit**

```bash
git add src/Namotion.Interceptor.Registry.Tests/
git commit -m "test: Add SuppressRemoval unit tests including concurrent scenarios"
```

---

### Task 3: Integrate SuppressRemoval into SubjectUpdateApplier

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors/Updates/Internal/SubjectUpdateApplier.cs`

**Step 1: Wrap apply in SuppressRemoval scope**

In `ApplyUpdate`, after `context.PreResolveSubjects(...)` and before the root processing block, get the registry and wrap the structural processing:

```csharp
var registry = subject.Context.TryGetService<ISubjectRegistry>() as SubjectRegistry;
using (registry?.SuppressRemoval())
{
    if (update.Root is not null && update.Subjects.TryGetValue(update.Root, out var rootProperties))
    {
        context.TryMarkAsProcessed(update.Root);
        ApplyPropertyUpdates(subject, rootProperties, context);
    }

    foreach (var (subjectId, properties) in update.Subjects)
    {
        if (context.TryResolveSubject(subjectId, out var targetSubject))
        {
            if (context.TryMarkAsProcessed(subjectId))
            {
                ApplyPropertyUpdates(targetSubject, properties, context);
            }
        }
    }
}
```

Add `using Namotion.Interceptor.Registry;` if not already present.

**Step 2: Build and test**

Run: `dotnet build src/Namotion.Interceptor.slnx && dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`
Expected: Build succeeds, all tests pass

**Step 3: Commit**

```bash
git add src/Namotion.Interceptor.Connectors/Updates/Internal/SubjectUpdateApplier.cs
git commit -m "fix: Wrap update apply in SuppressRemoval to prevent temporary unregistration during subject moves"
```

---

### Task 4: Add integration test for subject move during apply

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors.Tests/Updates/StableIdApplyTests.cs`

**Step 1: Write integration test**

```csharp
[Fact]
public void ApplyUpdate_SubjectMoveBetweenDicts_SubjectStaysRegistered()
{
    // Arrange: root with two dicts, subject X in DictA
    var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
    var x = new CycleTestNode { Name = "X" };
    var root = new CycleTestNode(context)
    {
        Name = "Root",
        Items = [x],
    };

    var rootId = root.GetOrAddSubjectId();
    var xId = x.GetOrAddSubjectId();
    var idRegistry = context.GetService<ISubjectIdRegistry>();

    // Verify X is registered before apply
    Assert.True(idRegistry.TryGetSubjectById(xId, out _));

    // Build update that moves X from Items to a different structural property
    // by removing X from Items and adding X to ObjectRef (different structural property)
    var update = new SubjectUpdate
    {
        Root = rootId,
        Subjects = new()
        {
            [rootId] = new()
            {
                ["Items"] = new SubjectPropertyUpdate
                {
                    Kind = SubjectPropertyUpdateKind.Collection,
                    Items = [] // Remove all items (removes X)
                },
                ["Child"] = new SubjectPropertyUpdate
                {
                    Kind = SubjectPropertyUpdateKind.Object,
                    Id = xId // Add X as ObjectRef
                }
            },
            [xId] = new()
            {
                ["Name"] = new SubjectPropertyUpdate
                {
                    Kind = SubjectPropertyUpdateKind.Value,
                    Value = "Moved"
                }
            }
        }
    };

    // Act
    SubjectUpdateApplier.ApplyUpdate(root, update, new DefaultSubjectFactory());

    // Assert: X should still be registered (never temporarily unregistered)
    Assert.True(idRegistry.TryGetSubjectById(xId, out var resolved));
    Assert.Same(x, resolved);
    Assert.Equal("Moved", x.Name);
    Assert.Same(x, root.Child);
}
```

**Step 2: Run tests**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "Category!=Integration"`
Expected: All tests pass

**Step 3: Commit**

```bash
git add src/Namotion.Interceptor.Connectors.Tests/Updates/StableIdApplyTests.cs
git commit -m "test: Add integration test for subject move between properties during apply"
```

---

### Task 5: Make server CQP filters resilient to momentarily-unregistered subjects

**Context:** `SuppressRemoval()` on the applier closes the apply-path race (subject moves within an update). But a second race exists: **local structural mutations** on any participant can temporarily unregister subjects, causing the CQP filter to drop concurrent value changes. This race caused the cycle 453 failure in ConnectorTester (server wrote `DecimalValue = 1517170.33`, CQP dropped it because a concurrent structural mutation had temporarily unregistered the subject).

The CQP filter on server connectors uses `TryGetRegisteredProperty()` to check both (a) that the subject is registered and (b) that the PathProvider includes the property. When a concurrent structural mutation detaches a subject, `TryGetRegisteredProperty()` returns null and the value change is silently dropped.

**Fix:** When `TryGetRegisteredProperty()` returns null, fall back to checking whether the subject has a valid subject ID (proving it was previously registered). If it does, the unregistration is momentary — let the value change through.

**Why this is safe:**
- A subject ID is only assigned when the subject enters the registry, proving prior registration
- The PathProvider configuration is static — a previously included property remains includable
- Value changes on momentarily-unregistered subjects are either relevant (subject mid-move) or moot (subject genuinely removed, structural update covers it)
- Properties on subjects without a context cannot reach the CQP (no interceptors)

**Files:**
- Modify: `src/Namotion.Interceptor.WebSocket/Server/WebSocketSubjectHandler.cs`
- Modify: `src/Namotion.Interceptor.Mqtt/Server/MqttSubjectServerBackgroundService.cs`

**Step 1: Update WebSocket server CQP filter**

In `CreateChangeQueueProcessor` (around line 377), replace the `propertyFilter` lambda:

```csharp
// Before:
propertyFilter: propertyReference =>
    propertyReference.TryGetRegisteredProperty() is { } property &&
    (_configuration.PathProvider?.IsPropertyIncluded(property) ?? true),

// After:
propertyFilter: propertyReference =>
{
    if (propertyReference.TryGetRegisteredProperty() is { } property)
    {
        return _configuration.PathProvider?.IsPropertyIncluded(property) ?? true;
    }

    // Momentarily unregistered due to concurrent structural mutation.
    // A subject ID proves prior registration — let value changes through.
    // Structural updates handle graph consistency independently.
    return propertyReference.Subject.TryGetSubjectId() is not null;
},
```

**Step 2: Update MQTT server CQP filter**

In `IsPropertyIncluded` (around line 88), apply the same pattern:

```csharp
// Before:
private bool IsPropertyIncluded(PropertyReference propertyReference) =>
    propertyReference.TryGetRegisteredProperty() is { } property &&
    _configuration.PathProvider.IsPropertyIncluded(property);

// After:
private bool IsPropertyIncluded(PropertyReference propertyReference)
{
    if (propertyReference.TryGetRegisteredProperty() is { } property)
    {
        return _configuration.PathProvider.IsPropertyIncluded(property);
    }

    // Momentarily unregistered due to concurrent structural mutation.
    return propertyReference.Subject.TryGetSubjectId() is not null;
}
```

Also update the MQTT server write handler (around line 235) and client write handler (around line 173) — anywhere `TryGetRegisteredProperty()` returning null causes a silent skip of value changes. For structural property checks (`CanContainSubjects`), the null guard is correct (structural changes for unregistered subjects should be skipped).

**Step 3: Build and test**

Run: `dotnet build src/Namotion.Interceptor.slnx && dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`
Expected: Build succeeds, all tests pass

**Step 4: Commit**

```bash
git add src/Namotion.Interceptor.WebSocket/Server/WebSocketSubjectHandler.cs src/Namotion.Interceptor.Mqtt/Server/MqttSubjectServerBackgroundService.cs
git commit -m "fix: Make server CQP filters resilient to momentarily-unregistered subjects during concurrent structural mutations"
```

---

### Task 6: Update documentation

**Files:**
- Modify: `docs/registry.md`
- Modify: `docs/connectors-subject-updates.md`
- Modify: `docs/plans/fixes.md`

**Step 1: Add section to registry.md**

After the "Lifecycle integration" subsection (around line 216), add:

```markdown
### Deferred subject removal

When applying updates that move subjects between structural properties, subjects can be temporarily unregistered between detach and re-attach. `SubjectRegistry.SuppressRemoval()` returns an `IDisposable` scope that defers context-detach cleanup:

```csharp
using (registry.SuppressRemoval())
{
    // Structural changes here won't remove subjects from the registry.
    // On dispose, only genuinely orphaned subjects are removed.
}
```

During suppression:
- `PropertyReferenceRemoved/Added` always run immediately (per-link, always correct)
- Context-detach cleanup is deferred (subjects stay in `_knownSubjects` and `_subjectIdToSubject`)
- On dispose, subjects with no parents are removed; subjects re-attached by any thread are kept

The suppression counter is thread-local — concurrent threads without a scope are not affected.

See [Deferred Subject Removal design](design/deferred-subject-removal.md) for full rationale and concurrent correctness analysis.
```

**Step 2: Add section to connectors-subject-updates.md**

Add a section "Deferred removal during structural apply":

```markdown
### Deferred removal during structural apply

`SubjectUpdateApplier.ApplyUpdate` wraps all structural processing in `SubjectRegistry.SuppressRemoval()`. This prevents subjects from being temporarily unregistered when they move between structural properties within the same update (e.g., removed from one dictionary, added to another).

Without suppression, the sequential processing of properties could fully detach a subject (removing it from `_knownSubjects` and `_subjectIdToSubject`) before re-attaching it to the target property. During this gap, the `ChangeQueueProcessor` filter — which depends on `_knownSubjects` via `TryGetRegisteredProperty()` — could drop value changes for the subject, causing permanent divergence.

With suppression, the subject stays visible in both maps throughout the apply window. On scope dispose, only subjects that are genuinely orphaned (removed but never re-attached, verified by checking `RegisteredSubject.Parents.Length == 0`) are cleaned up.

### CQP filter resilience for server connectors

Server-side CQP filters (WebSocket, MQTT) are resilient to momentarily-unregistered subjects. When `TryGetRegisteredProperty()` returns null during a concurrent structural mutation, the filter checks whether the subject has a valid subject ID (proving prior registration) and lets value changes through rather than dropping them.

This is defense-in-depth for cases where `SuppressRemoval()` is not active — e.g., local structural mutations on the server that go through the lifecycle directly, not through `SubjectUpdateApplier`.
```

**Step 3: Update fixes.md**

Close the "Open Problem" section and add Fix 17:

Replace the "Open Problem" section with `## Fix 17: Deferred subject removal and CQP filter resilience` and update the content to reflect the two-part solution:

1. **SuppressRemoval on SubjectUpdateApplier** — prevents temporary unregistration during subject moves within a single update (apply-path race). Keeps subjects visible in `_knownSubjects` and `_subjectIdToSubject` throughout the apply window.

2. **Resilient server CQP filters** — when `TryGetRegisteredProperty()` returns null, falls back to checking the subject's ID rather than dropping the value change. Closes the local-mutation race where structural mutations on any thread can temporarily unregister subjects while the CQP flush thread is checking property registration.

Mark as "Applied".

**Step 4: Commit**

```bash
git add docs/registry.md docs/connectors-subject-updates.md docs/plans/fixes.md
git commit -m "docs: Document deferred subject removal and CQP filter resilience (Fix 17)"
```
