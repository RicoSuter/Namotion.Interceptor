# Microsoft.Extensions.ObjectPool Migration Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Replace all custom pooling implementations with `Microsoft.Extensions.ObjectPool` for code standardization, consistent memory behavior, and optimal performance (clear on return, never on acquire).

**Architecture:** Create reusable pool policies in `Namotion.Interceptor.Registry/Performance/`, migrate 4 files that use pooling, delete the custom ObjectPool class. All clearing happens in policy's `Return()` method only.

**Tech Stack:** Microsoft.Extensions.ObjectPool 9.0.0, .NET 9.0

---

## Task 1: Add NuGet Package to Registry Project

**Files:**
- Modify: `src/Namotion.Interceptor.Registry/Namotion.Interceptor.Registry.csproj:9-12`

**Step 1: Add package reference**

Add the Microsoft.Extensions.ObjectPool package to the Registry project.

```xml
<ItemGroup>
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="9.0.10" />
    <PackageReference Include="Microsoft.Extensions.ObjectPool" Version="9.0.0" />
    <PackageReference Include="System.Reactive" Version="6.1.0" />
</ItemGroup>
```

**Step 2: Build to verify package restored**

Run: `dotnet build src/Namotion.Interceptor.Registry/Namotion.Interceptor.Registry.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add src/Namotion.Interceptor.Registry/Namotion.Interceptor.Registry.csproj
git commit -m "chore: add Microsoft.Extensions.ObjectPool to Registry project"
```

---

## Task 2: Add NuGet Package to Tracking Project

**Files:**
- Modify: `src/Namotion.Interceptor.Tracking/Namotion.Interceptor.Tracking.csproj:15-18`

**Step 1: Add package reference**

Add the Microsoft.Extensions.ObjectPool package to the Tracking project.

```xml
<ItemGroup>
    <PackageReference Include="Microsoft.Extensions.ObjectPool" Version="9.0.0" />
    <PackageReference Include="System.Reactive" Version="6.1.0" />
    <ProjectReference Include="..\Namotion.Interceptor\Namotion.Interceptor.csproj" />
</ItemGroup>
```

**Step 2: Build to verify package restored**

Run: `dotnet build src/Namotion.Interceptor.Tracking/Namotion.Interceptor.Tracking.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add src/Namotion.Interceptor.Tracking/Namotion.Interceptor.Tracking.csproj
git commit -m "chore: add Microsoft.Extensions.ObjectPool to Tracking project"
```

---

## Task 3: Create ListPoolPolicy

**Files:**
- Create: `src/Namotion.Interceptor.Registry/Performance/ListPoolPolicy.cs`

**Step 1: Create the pool policy file**

```csharp
using Microsoft.Extensions.ObjectPool;

namespace Namotion.Interceptor.Registry.Performance;

/// <summary>
/// Pool policy for <see cref="List{T}"/> that clears on return.
/// </summary>
/// <typeparam name="T">The type of elements in the list.</typeparam>
public sealed class ListPoolPolicy<T> : PooledObjectPolicy<List<T>>
{
    private readonly int _initialCapacity;

    public ListPoolPolicy(int initialCapacity = 0)
    {
        _initialCapacity = initialCapacity;
    }

    public override List<T> Create() => new(_initialCapacity);

    public override bool Return(List<T> obj)
    {
        obj.Clear();
        return true;
    }
}
```

**Step 2: Build to verify compilation**

Run: `dotnet build src/Namotion.Interceptor.Registry/Namotion.Interceptor.Registry.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add src/Namotion.Interceptor.Registry/Performance/ListPoolPolicy.cs
git commit -m "feat: add ListPoolPolicy for Microsoft.Extensions.ObjectPool"
```

---

## Task 4: Create HashSetPoolPolicy

**Files:**
- Create: `src/Namotion.Interceptor.Registry/Performance/HashSetPoolPolicy.cs`

**Step 1: Create the pool policy file**

```csharp
using Microsoft.Extensions.ObjectPool;

namespace Namotion.Interceptor.Registry.Performance;

/// <summary>
/// Pool policy for <see cref="HashSet{T}"/> that clears on return.
/// </summary>
/// <typeparam name="T">The type of elements in the set.</typeparam>
public sealed class HashSetPoolPolicy<T> : PooledObjectPolicy<HashSet<T>>
{
    public override HashSet<T> Create() => [];

    public override bool Return(HashSet<T> obj)
    {
        obj.Clear();
        return true;
    }
}
```

**Step 2: Build to verify compilation**

Run: `dotnet build src/Namotion.Interceptor.Registry/Namotion.Interceptor.Registry.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add src/Namotion.Interceptor.Registry/Performance/HashSetPoolPolicy.cs
git commit -m "feat: add HashSetPoolPolicy for Microsoft.Extensions.ObjectPool"
```

---

## Task 5: Create DictionaryPoolPolicy

**Files:**
- Create: `src/Namotion.Interceptor.Registry/Performance/DictionaryPoolPolicy.cs`

**Step 1: Create the pool policy file**

```csharp
using Microsoft.Extensions.ObjectPool;

namespace Namotion.Interceptor.Registry.Performance;

/// <summary>
/// Pool policy for <see cref="Dictionary{TKey, TValue}"/> that clears on return.
/// </summary>
/// <typeparam name="TKey">The type of keys in the dictionary.</typeparam>
/// <typeparam name="TValue">The type of values in the dictionary.</typeparam>
public sealed class DictionaryPoolPolicy<TKey, TValue> : PooledObjectPolicy<Dictionary<TKey, TValue>>
    where TKey : notnull
{
    public override Dictionary<TKey, TValue> Create() => new();

    public override bool Return(Dictionary<TKey, TValue> obj)
    {
        obj.Clear();
        return true;
    }
}
```

**Step 2: Build to verify compilation**

Run: `dotnet build src/Namotion.Interceptor.Registry/Namotion.Interceptor.Registry.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add src/Namotion.Interceptor.Registry/Performance/DictionaryPoolPolicy.cs
git commit -m "feat: add DictionaryPoolPolicy for Microsoft.Extensions.ObjectPool"
```

---

## Task 6: Migrate LifecycleInterceptor - Replace Pool Fields

**Files:**
- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/LifecycleInterceptor.cs:1-19`

**Step 1: Update using statements and replace ThreadStatic fields**

Replace lines 1-19 with:

```csharp
using System.Collections;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.ObjectPool;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Registry.Performance;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Tracking.Lifecycle;

public class LifecycleInterceptor : IWriteInterceptor, ILifecycleInterceptor
{
    private readonly Dictionary<IInterceptorSubject, HashSet<PropertyReference>> _attachedSubjects = [];

    private static readonly ObjectPool<List<(IInterceptorSubject subject, PropertyReference property, object? index)>> ListPool =
        new DefaultObjectPool<List<(IInterceptorSubject, PropertyReference, object?)>>(
            new ListPoolPolicy<(IInterceptorSubject, PropertyReference, object?)>(8), 256);

    private static readonly ObjectPool<HashSet<IInterceptorSubject>> SubjectHashSetPool =
        new DefaultObjectPool<HashSet<IInterceptorSubject>>(
            new HashSetPoolPolicy<IInterceptorSubject>(), 256);

    private static readonly ObjectPool<HashSet<PropertyReference>> PropertyHashSetPool =
        new DefaultObjectPool<HashSet<PropertyReference>>(
            new HashSetPoolPolicy<PropertyReference>(), 256);
```

**Step 2: Build to check for errors (will fail - helper methods not updated yet)**

Run: `dotnet build src/Namotion.Interceptor.Tracking/Namotion.Interceptor.Tracking.csproj`
Expected: Build errors (helper methods reference old fields)

---

## Task 7: Migrate LifecycleInterceptor - Update Helper Methods

**Files:**
- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/LifecycleInterceptor.cs:405-452`

**Step 1: Replace the Performance region with new implementations**

Replace lines 405-452 (the entire `#region Performance` block) with:

```csharp
    #region Performance

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static List<(IInterceptorSubject subject, PropertyReference property, object? index)> GetList()
        => ListPool.Get();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static HashSet<IInterceptorSubject> GetSubjectHashSet()
        => SubjectHashSetPool.Get();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static HashSet<PropertyReference> GetPropertyHashSet()
        => PropertyHashSetPool.Get();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ReturnList(List<(IInterceptorSubject, PropertyReference, object?)> list)
        => ListPool.Return(list);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ReturnSubjectHashSet(HashSet<IInterceptorSubject> hashSet)
        => SubjectHashSetPool.Return(hashSet);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ReturnPropertyHashSet(HashSet<PropertyReference> hashSet)
        => PropertyHashSetPool.Return(hashSet);

    #endregion
```

**Step 2: Build to verify compilation**

Run: `dotnet build src/Namotion.Interceptor.Tracking/Namotion.Interceptor.Tracking.csproj`
Expected: Build succeeded

**Step 3: Run tests to verify behavior**

Run: `dotnet test src/Namotion.Interceptor.Tracking.Tests/Namotion.Interceptor.Tracking.Tests.csproj`
Expected: All tests pass

**Step 4: Commit**

```bash
git add src/Namotion.Interceptor.Tracking/Lifecycle/LifecycleInterceptor.cs
git commit -m "refactor: migrate LifecycleInterceptor to Microsoft.Extensions.ObjectPool"
```

---

## Task 8: Migrate SubjectUpdatePools

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors/Updates/Performance/SubjectUpdatePools.cs`

**Step 1: Replace entire file contents**

```csharp
using System.Runtime.CompilerServices;
using Microsoft.Extensions.ObjectPool;
using Namotion.Interceptor.Registry.Performance;

namespace Namotion.Interceptor.Connectors.Updates.Performance;

internal static class SubjectUpdatePools
{
    private static readonly ObjectPool<Dictionary<IInterceptorSubject, SubjectUpdate>> KnownSubjectUpdatesPool =
        new DefaultObjectPool<Dictionary<IInterceptorSubject, SubjectUpdate>>(
            new DictionaryPoolPolicy<IInterceptorSubject, SubjectUpdate>(), 256);

    private static readonly ObjectPool<Dictionary<SubjectPropertyUpdate, SubjectPropertyUpdateReference>> PropertyUpdatesPool =
        new DefaultObjectPool<Dictionary<SubjectPropertyUpdate, SubjectPropertyUpdateReference>>(
            new DictionaryPoolPolicy<SubjectPropertyUpdate, SubjectPropertyUpdateReference>(), 256);

    private static readonly ObjectPool<HashSet<IInterceptorSubject>> ProcessedParentPathsPool =
        new DefaultObjectPool<HashSet<IInterceptorSubject>>(
            new HashSetPoolPolicy<IInterceptorSubject>(), 256);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Dictionary<IInterceptorSubject, SubjectUpdate> RentKnownSubjectUpdates()
        => KnownSubjectUpdatesPool.Get();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ReturnKnownSubjectUpdates(Dictionary<IInterceptorSubject, SubjectUpdate> dictionary)
        => KnownSubjectUpdatesPool.Return(dictionary);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Dictionary<SubjectPropertyUpdate, SubjectPropertyUpdateReference> RentPropertyUpdates()
        => PropertyUpdatesPool.Get();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ReturnPropertyUpdates(Dictionary<SubjectPropertyUpdate, SubjectPropertyUpdateReference>? dictionary)
    {
        if (dictionary is not null)
        {
            PropertyUpdatesPool.Return(dictionary);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static HashSet<IInterceptorSubject> RentProcessedParentPaths()
        => ProcessedParentPathsPool.Get();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ReturnProcessedParentPaths(HashSet<IInterceptorSubject> hashSet)
        => ProcessedParentPathsPool.Return(hashSet);
}
```

**Step 2: Build to verify compilation**

Run: `dotnet build src/Namotion.Interceptor.Connectors/Namotion.Interceptor.Connectors.csproj`
Expected: Build succeeded

**Step 3: Run tests**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests/Namotion.Interceptor.Connectors.Tests.csproj`
Expected: All tests pass

**Step 4: Commit**

```bash
git add src/Namotion.Interceptor.Connectors/Updates/Performance/SubjectUpdatePools.cs
git commit -m "refactor: migrate SubjectUpdatePools to Microsoft.Extensions.ObjectPool"
```

---

## Task 9: Migrate MqttSubjectClientSource - Update Pool Declaration

**Files:**
- Modify: `src/Namotion.Interceptor.Mqtt/Client/MqttSubjectClientSource.cs:1-28`

**Step 1: Update using statements and pool declaration**

Replace lines 1-28 with:

```csharp
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;
using MQTTnet;
using MQTTnet.Packets;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Connectors.Paths;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Performance;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Mqtt.Client;

/// <summary>
/// MQTT client source that subscribes to an MQTT broker and synchronizes properties.
/// </summary>
internal sealed class MqttSubjectClientSource : BackgroundService, ISubjectSource, IAsyncDisposable
{
    // Pool for UserProperties lists to avoid allocations on hot path
    private static readonly ObjectPool<List<MqttUserProperty>> UserPropertiesPool =
        new DefaultObjectPool<List<MqttUserProperty>>(
            new ListPoolPolicy<MqttUserProperty>(1), 256);
```

**Step 2: Build to check for errors (will fail - usages not updated)**

Run: `dotnet build src/Namotion.Interceptor.Mqtt/Namotion.Interceptor.Mqtt.csproj`
Expected: Build errors (Rent() method no longer exists)

---

## Task 10: Migrate MqttSubjectClientSource - Update Pool Usages

**Files:**
- Modify: `src/Namotion.Interceptor.Mqtt/Client/MqttSubjectClientSource.cs:202-203`

**Step 1: Update Rent() to Get() and remove Clear()**

Find and replace the usage around line 202-203:

Before:
```csharp
var userProps = UserPropertiesPool.Rent();
userProps.Clear();
```

After:
```csharp
var userProps = UserPropertiesPool.Get();
```

**Step 2: Build to verify compilation**

Run: `dotnet build src/Namotion.Interceptor.Mqtt/Namotion.Interceptor.Mqtt.csproj`
Expected: Build succeeded

**Step 3: Run tests**

Run: `dotnet test src/Namotion.Interceptor.Mqtt.Tests/Namotion.Interceptor.Mqtt.Tests.csproj`
Expected: All tests pass

**Step 4: Commit**

```bash
git add src/Namotion.Interceptor.Mqtt/Client/MqttSubjectClientSource.cs
git commit -m "refactor: migrate MqttSubjectClientSource to Microsoft.Extensions.ObjectPool"
```

---

## Task 11: Migrate SubscriptionManager - Update Pool Declaration

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa/Client/Connection/SubscriptionManager.cs:1-17`

**Step 1: Update using statements and pool declaration**

Replace lines 1-17 with:

```csharp
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.OpcUa.Client.Polling;
using Namotion.Interceptor.OpcUa.Client.Resilience;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Performance;
using Namotion.Interceptor.Tracking.Change;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Client.Connection;

internal class SubscriptionManager : IAsyncDisposable
{
    private static readonly ObjectPool<List<PropertyUpdate>> ChangesPool =
        new DefaultObjectPool<List<PropertyUpdate>>(
            new ListPoolPolicy<PropertyUpdate>(16), 256);
```

**Step 2: Build to check for errors (will fail - usages not updated)**

Run: `dotnet build src/Namotion.Interceptor.OpcUa/Namotion.Interceptor.OpcUa.csproj`
Expected: Build errors (Rent() method no longer exists)

---

## Task 12: Migrate SubscriptionManager - Update Pool Usages

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa/Client/Connection/SubscriptionManager.cs`

**Step 1: Update Rent() to Get()**

Find around line 124 and replace:
```csharp
var changes = ChangesPool.Rent();
```
With:
```csharp
var changes = ChangesPool.Get();
```

**Step 2: Remove Clear() before Return()**

Find around line 160-161 and replace:
```csharp
s.changes.Clear();
ChangesPool.Return(s.changes);
```
With:
```csharp
ChangesPool.Return(s.changes);
```

**Step 3: Build to verify compilation**

Run: `dotnet build src/Namotion.Interceptor.OpcUa/Namotion.Interceptor.OpcUa.csproj`
Expected: Build succeeded

**Step 4: Run tests**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests/Namotion.Interceptor.OpcUa.Tests.csproj`
Expected: All tests pass

**Step 5: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa/Client/Connection/SubscriptionManager.cs
git commit -m "refactor: migrate SubscriptionManager to Microsoft.Extensions.ObjectPool"
```

---

## Task 13: Delete Custom ObjectPool Class

**Files:**
- Delete: `src/Namotion.Interceptor.Registry/Performance/ObjectPool.cs`

**Step 1: Delete the file**

```bash
rm src/Namotion.Interceptor.Registry/Performance/ObjectPool.cs
```

**Step 2: Build entire solution to verify no remaining usages**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add -A
git commit -m "refactor: delete custom ObjectPool class (replaced by Microsoft.Extensions.ObjectPool)"
```

---

## Task 14: Run Full Test Suite

**Files:**
- None (verification only)

**Step 1: Run all tests**

Run: `dotnet test src/Namotion.Interceptor.slnx`
Expected: All tests pass

**Step 2: If tests fail, investigate and fix**

Check output for failed tests. Common issues:
- Missing using statements
- Incorrect method names (Rent vs Get)
- Missing Clear() calls that were actually needed (shouldn't happen with this plan)

---

## Task 15: Final Verification and Cleanup

**Files:**
- None (verification only)

**Step 1: Verify build succeeds**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: Build succeeded with no warnings

**Step 2: Verify tests pass**

Run: `dotnet test src/Namotion.Interceptor.slnx`
Expected: All tests pass

**Step 3: Optional - Run benchmarks**

Run: `dotnet run --project src/Namotion.Interceptor.Benchmark -c Release -- --filter "*Pool*"`
Expected: Benchmarks complete (compare with baseline if available)

---

## Summary

| Task | Description | Files |
|------|-------------|-------|
| 1 | Add NuGet to Registry | `Namotion.Interceptor.Registry.csproj` |
| 2 | Add NuGet to Tracking | `Namotion.Interceptor.Tracking.csproj` |
| 3 | Create ListPoolPolicy | `ListPoolPolicy.cs` (new) |
| 4 | Create HashSetPoolPolicy | `HashSetPoolPolicy.cs` (new) |
| 5 | Create DictionaryPoolPolicy | `DictionaryPoolPolicy.cs` (new) |
| 6-7 | Migrate LifecycleInterceptor | `LifecycleInterceptor.cs` |
| 8 | Migrate SubjectUpdatePools | `SubjectUpdatePools.cs` |
| 9-10 | Migrate MqttSubjectClientSource | `MqttSubjectClientSource.cs` |
| 11-12 | Migrate SubscriptionManager | `SubscriptionManager.cs` |
| 13 | Delete custom ObjectPool | `ObjectPool.cs` (delete) |
| 14-15 | Verification | None |

## Performance Notes

- **Clear on Return only**: All policies clear collections in `Return()`, callers never clear manually
- **Pool size 256**: Increased from default 64 for high-throughput scenarios
- **DefaultObjectPool**: Uses thread-local fast path + shared store for good performance
