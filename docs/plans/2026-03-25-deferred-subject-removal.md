# Deferred Subject Removal Implementation Plan

**Goal:** Prevent value changes from being silently dropped when subjects are momentarily unregistered during concurrent structural mutations. Three complementary changes close all known race windows.

**Architecture:**
1. **SuppressRemoval on applier** (Tasks 1-4): Add `SuppressRemoval()` to `SubjectRegistry` using `[ThreadStatic]` counter and deferred detach set. During suppression, context-detach cleanup is deferred. On resume, check actual parent state to determine if removal is still warranted. Wrap `SubjectUpdateApplier.ApplyUpdate` in this scope. See `docs/design/deferred-subject-removal.md` for full rationale.
2. **CQP filter resilience** (Task 5): Make server-side CQP filters (WebSocket, MQTT) tolerant of momentarily-unregistered subjects by falling back to a subject ID check instead of dropping value changes. This closes the local-mutation race where structural mutations go through the lifecycle directly, not through the applier.
3. **Write handler simplification** (Task 5): Replace `TryGetRegisteredProperty()` with `change.Property.Metadata.Type.CanContainSubjects()` in MQTT write handlers. The `CanContainSubjects` check is derived from the same compile-time type info and is always available regardless of registry state — no race possible. `TryGetTopicForProperty` is updated to accept a nullable registered property (cache hit works without it; cache miss returns null for momentarily-unregistered subjects).

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

In the `IsContextDetach` block (line 167), wrap the cleanup in a suppression check. When `s_suppressRemovalCount > 0`, defer the removal instead of executing it immediately.

**Step 5: Build and run tests**

```
dotnet build src/Namotion.Interceptor.slnx
dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"
```

---

### Task 2: Add unit tests for SuppressRemoval

**Files:**
- Create: `src/Namotion.Interceptor.Registry.Tests/SuppressRemovalTests.cs`

**Tests:**

1. **BasicSuppression_DetachDeferredThenProcessedOnResume** — Attach subject, start scope, detach subject, verify still in `_knownSubjects` and `_subjectIdToSubject`, dispose scope, verify removed from both.

2. **MoveBetweenProperties_SubjectStaysRegistered** — Attach subject as child of property A, start scope, remove from A (triggers detach), add to B (triggers reattach), dispose scope, verify subject still registered with B as parent.

3. **GenuineRemoval_RemovedOnResume** — Attach subject, start scope, detach subject (no reattach), dispose scope, verify removed.

4. **NoScope_ExistingBehaviorUnchanged** — Attach and detach without scope, verify immediate removal (regression guard).

5. **NestedScopes_ProcessedOnOuterDispose** — Start outer scope, start inner scope, detach subject, dispose inner (no processing), verify still registered, dispose outer (processes), verify removed.

6. **ThreadIsolation_UnscopedThreadNotAffected** — Start scope on Thread A, on Thread B detach a subject (no scope), verify Thread B's detach is immediate (not deferred).

7. **CrossThreadReattach_SkipsRemoval** — Start scope on Thread A, defer detach of X. On Thread B (no scope), reattach X. Dispose scope on Thread A, verify X still registered (has parents from Thread B).

---

### Task 3: Integrate SuppressRemoval into SubjectUpdateApplier

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors/Updates/Internal/SubjectUpdateApplier.cs`

Wrap the structural processing (root path + remaining subjects loop) in `using (registry?.SuppressRemoval())`.

---

### Task 4: Add integration test for subject move during apply

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors.Tests/Updates/StableIdApplyTests.cs`

Test: `ApplyUpdate_SubjectMoveBetweenCollectionAndObjectRef_SubjectStaysRegistered` — Move subject X from Items collection to Child object ref within the same update. Verify X stays registered throughout, value updates applied, and correct parent references.

---

### Task 5: CQP filter resilience and write handler simplification

**Files:**
- Modify: `src/Namotion.Interceptor.WebSocket/Server/WebSocketSubjectHandler.cs`
- Modify: `src/Namotion.Interceptor.Mqtt/Server/MqttSubjectServerBackgroundService.cs`
- Modify: `src/Namotion.Interceptor.Mqtt/Client/MqttSubjectClientSource.cs`

**Step 1: WebSocket CQP filter — subject ID fallback**

Replace the `propertyFilter` lambda in `CreateChangeQueueProcessor`:

```csharp
propertyFilter: propertyReference =>
{
    if (propertyReference.TryGetRegisteredProperty() is { } property)
    {
        return _configuration.PathProvider?.IsPropertyIncluded(property) ?? true;
    }

    // Momentarily unregistered due to concurrent structural mutation.
    // A subject ID proves prior registration — let value changes through.
    return propertyReference.Subject.TryGetSubjectId() is not null;
},
```

**Step 2: MQTT server CQP filter — subject ID fallback**

Same pattern for `IsPropertyIncluded`.

**Step 3: MQTT server write handler — metadata-based CanContainSubjects**

Replace the `TryGetRegisteredProperty()` / `CanContainSubjects` check with:
```csharp
if (change.Property.Metadata.Type.CanContainSubjects())
    continue;
```

Update `TryGetTopicForProperty` to accept nullable `RegisteredSubjectProperty?`. Cache hit path doesn't use the parameter. Cache miss with null property returns null.

**Step 4: MQTT client write handler — same simplification**

Same metadata-based `CanContainSubjects` check and nullable `TryGetTopicForProperty`.

**Step 5: Build and run tests**

```
dotnet build src/Namotion.Interceptor.slnx
dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"
```

---

### Task 6: Update documentation

**Files:**
- Modify: `docs/registry.md`
- Modify: `docs/connectors-subject-updates.md`
- Modify: `docs/plans/fixes.md`

Update fixes.md Fix 17 status to "Applied" and reflect the three-part solution (SuppressRemoval + CQP filter resilience + write handler simplification). Add deferred removal sections to registry.md and connectors-subject-updates.md.
