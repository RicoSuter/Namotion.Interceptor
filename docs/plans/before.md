# Pre-Lifecycle Extraction Plan

This document contains instructions for extracting independent changes from the `feature/lifecycle-and-hosting-improvements` branch that can be merged BEFORE the main lifecycle feature PR.

## Context

The branch `feature/lifecycle-and-hosting-improvements` (PR #132) contains ~62 files with +2,694/-556 lines. To make review easier, we're extracting 7 independent PRs that have no dependency on the lifecycle API changes.

## Branch Strategy

For each PR below:
1. Create a new branch from `master`
2. Apply ONLY the specified changes
3. Run tests: `dotnet test src/Namotion.Interceptor.slnx`
4. Create PR to `master`

---

## PR 1: Context Recursion Bug Fix

**Priority:** High
**Risk:** Low
**Description:** Fixes stack overflow when contexts have circular fallback dependencies.

### Files to Create/Modify

#### 1. `src/Namotion.Interceptor/InterceptorSubjectContext.cs`

Add `visited` parameter to prevent infinite recursion in two methods:

**Change `GetServicesWithoutCache<TInterface>()`** (around line 238):

```csharp
// BEFORE:
private TInterface[] GetServicesWithoutCache<TInterface>()
{
    var services = _services
        .OfType<TInterface>()
        .Concat(_fallbackContexts.SelectMany(c => c.GetServices<TInterface>()))
        .Distinct()
        .ToArray();

    return services;
}

// AFTER:
private TInterface[] GetServicesWithoutCache<TInterface>()
{
    return GetServicesWithoutCache(typeof(TInterface), [])
        .OfType<TInterface>()
        .ToArray();
}

private IEnumerable<object> GetServicesWithoutCache(Type type, HashSet<InterceptorSubjectContext> visited)
{
    if (!visited.Add(this))
    {
        return [];
    }

    var services = _services
        .Where(type.IsInstanceOfType)
        .Concat(_fallbackContexts.SelectMany(c => c.GetServicesWithoutCache(type, visited)))
        .Distinct()
        .ToArray();

    return services;
}
```

**Change `OnContextChanged()`** (around line 250):

```csharp
// BEFORE:
[MethodImpl(MethodImplOptions.AggressiveInlining)]
private void OnContextChanged()
{
    _serviceCache?.Clear();
    _readInterceptorFunction?.Clear();
    _writeInterceptorFunction?.Clear();
    _methodInvocationFunction = null;

    _noServicesSingleFallbackContext = _services.Count == 0 && _fallbackContexts.Count == 1
        ? _fallbackContexts.Single() : null;

    foreach (var parent in _usedByContexts)
    {
        parent.OnContextChanged();
    }
}

// AFTER:
[MethodImpl(MethodImplOptions.AggressiveInlining)]
private void OnContextChanged()
{
    OnContextChanged([]);
}

private void OnContextChanged(HashSet<InterceptorSubjectContext> visited)
{
    if (!visited.Add(this))
    {
        return;
    }

    _serviceCache?.Clear();
    _readInterceptorFunction?.Clear();
    _writeInterceptorFunction?.Clear();
    _methodInvocationFunction = null;

    _noServicesSingleFallbackContext = _services.Count == 0 && _fallbackContexts.Count == 1
        ? _fallbackContexts.Single() : null;

    foreach (var parent in _usedByContexts)
    {
        parent.OnContextChanged(visited);
    }
}
```

#### 2. `src/Namotion.Interceptor.Tests/ContextRecursionTests.cs` (NEW FILE)

```csharp
using Xunit;

namespace Namotion.Interceptor.Tests
{
    public class ContextRecursionTests
    {
        [Fact]
        public void WhenContextsHaveCircularDependency_ThenOnContextChangedDoesNotStackOverflow()
        {
            var ctx1 = new InterceptorSubjectContext();
            var ctx2 = new InterceptorSubjectContext();

            // Create circular dependency
            ctx1.AddFallbackContext(ctx2);
            ctx2.AddFallbackContext(ctx1);

            // Trigger OnContextChanged
            ctx1.AddService("test");

            // Verify GetServices also works
            var services = ctx1.GetServices<string>();
            Assert.Contains("test", services);
        }
    }
}
```

### Git Commands

```bash
git checkout master
git checkout -b fix/context-recursion
# Apply changes above
git add -A
git commit -m "fix: Prevent stack overflow with circular fallback contexts

Add visited HashSet to OnContextChanged() and GetServicesWithoutCache()
to prevent infinite recursion when contexts have circular dependencies.

🤖 Generated with [Claude Code](https://claude.com/claude-code)

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

## PR 2: Parent Tracking Thread Safety

**Priority:** High
**Risk:** Low
**Description:** Makes `GetParents()` thread-safe by using `ImmutableHashSet` instead of `HashSet`.

### Files to Modify

#### 1. `src/Namotion.Interceptor.Tracking/Parent/ParentsHandlerExtensions.cs`

```csharp
// REPLACE ENTIRE FILE WITH:
using System.Collections.Immutable;

namespace Namotion.Interceptor.Tracking.Parent;

public static class ParentsHandlerExtensions
{
    private const string ParentsKey = "Namotion.Interceptor.Tracking.Parents";

    internal static void AddParent(this IInterceptorSubject subject, PropertyReference parent, object? index)
    {
        subject.Data.AddOrUpdate(
            (null, ParentsKey),
            _ => ImmutableHashSet.Create(new SubjectParent(parent, index)),
            (_, existing) => ((ImmutableHashSet<SubjectParent>)existing!).Add(new SubjectParent(parent, index)));
    }

    internal static void RemoveParent(this IInterceptorSubject subject, PropertyReference parent, object? index)
    {
        subject.Data.AddOrUpdate(
            (null, ParentsKey),
            _ => ImmutableHashSet<SubjectParent>.Empty,
            (_, existing) => ((ImmutableHashSet<SubjectParent>)existing!).Remove(new SubjectParent(parent, index)));
    }

    public static IReadOnlyCollection<SubjectParent> GetParents(this IInterceptorSubject subject)
    {
        if (subject.Data.TryGetValue((null, ParentsKey), out var parents))
        {
            return (ImmutableHashSet<SubjectParent>)parents!;
        }
        return ImmutableHashSet<SubjectParent>.Empty;
    }
}
```

#### 2. `src/Namotion.Interceptor.Blazor/TrackingScope.razor` (line ~279)

```csharp
// BEFORE:
HashSet<SubjectParent> parents;

// AFTER:
IReadOnlyCollection<SubjectParent> parents;
```

### Git Commands

```bash
git checkout master
git checkout -b fix/parent-tracking-thread-safety
# Apply changes above
git add -A
git commit -m "fix: Make GetParents() thread-safe with ImmutableHashSet

- Change internal storage from HashSet to ImmutableHashSet
- Use atomic AddOrUpdate operations for add/remove
- Return IReadOnlyCollection instead of HashSet

🤖 Generated with [Claude Code](https://claude.com/claude-code)

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

## PR 3: ObjectPool Consolidation + Max Size

**Priority:** High
**Risk:** Low
**Description:** Moves `ObjectPool<T>` from Registry to core library and adds max size limit to prevent unbounded memory growth.

### Files to Create/Modify/Delete

#### 1. `src/Namotion.Interceptor/Performance/ObjectPool.cs` (NEW FILE)

```csharp
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Namotion.Interceptor.Performance;

/// <summary>
/// A simple thread-safe object pool using ConcurrentBag.
/// </summary>
/// <typeparam name="T">The type of objects to pool.</typeparam>
public sealed class ObjectPool<T> where T : class
{
    private readonly ConcurrentBag<T> _objects = [];
    private readonly Func<T> _factory;
    private readonly int _maxSize;

    public ObjectPool(Func<T> factory, int maxSize = 64)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _maxSize = maxSize;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T Rent()
    {
        return _objects.TryTake(out var item) ? item : _factory();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Return(T item)
    {
        if (_objects.Count < _maxSize)
        {
            _objects.Add(item);
        }
    }
}
```

#### 2. `src/Namotion.Interceptor/Namotion.Interceptor.csproj`

Add InternalsVisibleTo for Tracking project:

```xml
<ItemGroup>
    <InternalsVisibleTo Include="Namotion.Interceptor.Tests" />
    <InternalsVisibleTo Include="Namotion.Interceptor.Benchmark" />
    <InternalsVisibleTo Include="Namotion.Interceptor.Tracking" />  <!-- ADD THIS LINE -->
</ItemGroup>
```

#### 3. `src/Namotion.Interceptor.Registry/Performance/ObjectPool.cs` (DELETE)

Delete this file entirely.

#### 4. `src/Namotion.Interceptor.Connectors/Updates/Performance/SubjectUpdatePools.cs`

```csharp
// BEFORE:
using Namotion.Interceptor.Registry.Performance;

// AFTER:
using Namotion.Interceptor.Performance;
```

#### 5. `src/Namotion.Interceptor.Mqtt/Client/MqttSubjectClientSource.cs`

```csharp
// BEFORE:
using Namotion.Interceptor.Registry.Performance;

// AFTER:
using Namotion.Interceptor.Performance;
```

#### 6. `src/Namotion.Interceptor.OpcUa/Client/Connection/SubscriptionManager.cs`

```csharp
// BEFORE:
using Namotion.Interceptor.Registry.Performance;

// AFTER:
using Namotion.Interceptor.Performance;
```

Also fix the null check on line ~208:

```csharp
// BEFORE:
var statusCode = monitoredItem.Status?.Error?.StatusCode ?? StatusCodes.Good;

// AFTER:
var statusCode = monitoredItem.Status.Error?.StatusCode ?? StatusCodes.Good;
```

### Git Commands

```bash
git checkout master
git checkout -b refactor/objectpool-consolidation
# Apply changes above
git add -A
git commit -m "refactor: Move ObjectPool to core library and add max size limit

- Move ObjectPool<T> from Registry.Performance to core Performance namespace
- Add maxSize parameter (default 64) to prevent unbounded memory growth
- Update all import statements
- Fix unnecessary null check in SubscriptionManager

🤖 Generated with [Claude Code](https://claude.com/claude-code)

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

## PR 4: RegisteredSubject PropertyStorage Optimization

**Priority:** Medium
**Risk:** Low
**Description:** Replaces `FrozenDictionary` with dual `Dictionary` + `ImmutableArray` structure for allocation-free enumeration.

### Files to Modify

#### 1. `src/Namotion.Interceptor.Registry/Abstractions/RegisteredSubject.cs`

This is a larger change. Key modifications:

1. Replace `FrozenDictionary<string, RegisteredSubjectProperty>` with new `PropertyStorage` class
2. Add `PropertyStorage` nested class with both `Dictionary` and `ImmutableArray`
3. Update constructor to build both structures
4. Update `Properties` getter to return cached array
5. Update `TryGetProperty` to use dictionary
6. Update `AddPropertyInternal` to rebuild both structures

See the full diff in the branch for exact changes. The key structure is:

```csharp
private sealed class PropertyStorage
{
    public readonly Dictionary<string, RegisteredSubjectProperty> Dictionary;
    public readonly ImmutableArray<RegisteredSubjectProperty> Array;

    public PropertyStorage(
        Dictionary<string, RegisteredSubjectProperty> dictionary,
        ImmutableArray<RegisteredSubjectProperty> array)
    {
        Dictionary = dictionary;
        Array = array;
    }
}

private volatile PropertyStorage _storage;

public ImmutableArray<RegisteredSubjectProperty> Properties
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    get => _storage.Array;
}

[MethodImpl(MethodImplOptions.AggressiveInlining)]
public RegisteredSubjectProperty? TryGetProperty(string propertyName)
{
    return _storage.Dictionary.GetValueOrDefault(propertyName);
}
```

### Git Commands

```bash
git checkout master
git checkout -b perf/registered-subject-optimization
# Apply changes
git add -A
git commit -m "perf: Optimize RegisteredSubject with PropertyStorage

- Replace FrozenDictionary with PropertyStorage class
- Maintain both Dictionary (for lookup) and ImmutableArray (for enumeration)
- Allocation-free Properties getter via cached ImmutableArray
- Use ImmutableCollectionsMarshal.AsImmutableArray for zero-copy

🤖 Generated with [Claude Code](https://claude.com/claude-code)

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

## PR 5: HostedServiceHandler Lock Fix

**Priority:** Critical
**Risk:** Low
**Description:** Fixes race condition by locking on the correct object.

### Files to Modify

#### 1. `src/Namotion.Interceptor.Hosting/HostedServiceHandler.cs` (line ~134)

```csharp
// BEFORE:
internal void AttachHostedService(IHostedService hostedService)
{
    lock (hostedService)  // WRONG: locking on the object being added
    {
        if (_hostedServices.Add(hostedService))
        {
            // ...
        }
    }
}

// AFTER:
internal void AttachHostedService(IHostedService hostedService)
{
    lock (_hostedServices)  // CORRECT: locking on the shared collection
    {
        if (_hostedServices.Add(hostedService))
        {
            // ...
        }
    }
}
```

**IMPORTANT:** Only change the lock target. Do NOT change method names or other logic in this file - those are part of the lifecycle PR.

### Git Commands

```bash
git checkout master
git checkout -b fix/hosted-service-lock
# Apply ONLY the lock change
git add -A
git commit -m "fix: Lock on _hostedServices instead of hostedService

Fix race condition where multiple threads could call Add() concurrently
because the lock was on the target object, not the shared collection.

🤖 Generated with [Claude Code](https://claude.com/claude-code)

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

## PR 6: HostedServiceHandlerTests Improvement

**Priority:** Low
**Risk:** None
**Description:** Improves test reliability by properly managing host lifecycle.

### Files to Modify

#### 1. `src/Namotion.Interceptor.Hosting.Tests/HostedServiceHandlerTests.cs`

Fix the `RunWithAppLifecycleAsync` helper method:

```csharp
// BEFORE:
private static async Task RunWithAppLifecycleAsync(...)
{
    // ...
    var host = builder.Build();
    _ = host.RunAsync(cancellationTokenSource.Token);  // Fire and forget
    // ...
}

// AFTER:
private static async Task RunWithAppLifecycleAsync(...)
{
    // ...
    var host = builder.Build();
    await host.StartAsync();
    try
    {
        await action(context);
    }
    finally
    {
        await host.StopAsync();
    }
}
```

### Git Commands

```bash
git checkout master
git checkout -b test/hosted-service-handler-tests
# Apply changes
git add -A
git commit -m "test: Improve HostedServiceHandlerTests reliability

Use explicit StartAsync/StopAsync instead of fire-and-forget RunAsync
for more reliable test execution and cleanup.

🤖 Generated with [Claude Code](https://claude.com/claude-code)

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

## PR 7: Benchmark Script

**Priority:** Low
**Risk:** None
**Description:** Adds cross-platform PowerShell script for comparing benchmarks between branches.

### Files to Create

#### 1. `scripts/benchmark.ps1` (NEW FILE)

Copy the entire benchmark.ps1 file from the feature branch. It's ~200 lines providing:
- Cross-platform benchmark comparison
- Filter support
- Auto-stash option
- Short mode for quick runs

### Git Commands

```bash
git checkout master
git checkout -b chore/benchmark-script
git checkout feature/lifecycle-and-hosting-improvements -- scripts/benchmark.ps1
git add -A
git commit -m "chore: Add benchmark comparison script

Add cross-platform PowerShell script for comparing benchmark results
between current branch and master.

Usage:
  pwsh scripts/benchmark.ps1                     # Run all benchmarks
  pwsh scripts/benchmark.ps1 -Filter \"*Source*\" # Filter by pattern
  pwsh scripts/benchmark.ps1 -Stash              # Auto-stash changes
  pwsh scripts/benchmark.ps1 -Short              # Quick benchmark

🤖 Generated with [Claude Code](https://claude.com/claude-code)

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

## Verification

After each PR is merged to master, rebase the `feature/lifecycle-and-hosting-improvements` branch:

```bash
git checkout feature/lifecycle-and-hosting-improvements
git rebase master
# Resolve any conflicts (extracted changes should auto-resolve)
git push --force-with-lease
```

## Summary

| PR | Description | Files | Risk |
|----|-------------|-------|------|
| 1 | Context recursion bug fix | 2 | Low |
| 2 | Parent tracking thread safety | 2 | Low |
| 3 | ObjectPool consolidation | 6 | Low |
| 4 | RegisteredSubject optimization | 1 | Low |
| 5 | HostedServiceHandler lock fix | 1 | Low |
| 6 | HostedServiceHandlerTests fix | 1 | None |
| 7 | Benchmark script | 1 | None |

**Total: 14 files extracted from the 62-file lifecycle PR**
