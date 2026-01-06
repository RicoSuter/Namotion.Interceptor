# Microsoft.Extensions.ObjectPool Migration Plan

## Goal

Replace all custom pooling implementations with `Microsoft.Extensions.ObjectPool` for code standardization and consistent memory behavior.

## Current State

The codebase has **7 pool usages** across 4 files:

| File | Current Implementation | Collection Type |
|------|------------------------|-----------------|
| `SubjectUpdatePools.cs` | Custom ObjectPool | `Dictionary<IInterceptorSubject, SubjectUpdate>` |
| `SubjectUpdatePools.cs` | Custom ObjectPool | `Dictionary<SubjectPropertyUpdate, SubjectPropertyUpdateReference>` |
| `SubjectUpdatePools.cs` | Custom ObjectPool | `HashSet<IInterceptorSubject>` |
| `MqttSubjectClientSource.cs` | Custom ObjectPool | `List<MqttUserProperty>` |
| `SubscriptionManager.cs` | Custom ObjectPool | `List<PropertyUpdate>` |
| `LifecycleInterceptor.cs` | ThreadStatic Stack | `List<(IInterceptorSubject, PropertyReference, object?)>` |
| `LifecycleInterceptor.cs` | ThreadStatic Stack | `HashSet<IInterceptorSubject>` |

## Implementation Plan

### Step 1: Add NuGet Package to Required Projects

Add `Microsoft.Extensions.ObjectPool` v9.0.0 to these projects:

| Project | File |
|---------|------|
| `Namotion.Interceptor.Tracking` | `Namotion.Interceptor.Tracking.csproj` |
| `Namotion.Interceptor.Connectors` | `Namotion.Interceptor.Connectors.csproj` |
| `Namotion.Interceptor.Mqtt` | `Namotion.Interceptor.Mqtt.csproj` |
| `Namotion.Interceptor.OpcUa` | `Namotion.Interceptor.OpcUa.csproj` |

```xml
<PackageReference Include="Microsoft.Extensions.ObjectPool" Version="9.0.0" />
```

### Step 2: Create Reusable Pool Policies

Create in `Namotion.Interceptor.Tracking/Performance/` (shared via project reference):

**ListPoolPolicy.cs:**
```csharp
using Microsoft.Extensions.ObjectPool;

namespace Namotion.Interceptor.Tracking.Performance;

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

**HashSetPoolPolicy.cs:**
```csharp
using Microsoft.Extensions.ObjectPool;

namespace Namotion.Interceptor.Tracking.Performance;

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

**DictionaryPoolPolicy.cs:**
```csharp
using Microsoft.Extensions.ObjectPool;

namespace Namotion.Interceptor.Tracking.Performance;

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

### Step 3: Migrate LifecycleInterceptor.cs

**Before:**
```csharp
[ThreadStatic]
private static Stack<List<(IInterceptorSubject subject, PropertyReference property, object? index)>>? _listPool;

[ThreadStatic]
private static Stack<HashSet<IInterceptorSubject>>? _hashSetPool;

private static List<...> GetList()
{
    _listPool ??= new Stack<...>();
    return _listPool.Count > 0 ? _listPool.Pop() : [];
}

private static void ReturnList(List<...> list)
{
    list.Clear();
    _listPool!.Push(list);
}
```

**After:**
```csharp
using Microsoft.Extensions.ObjectPool;
using Namotion.Interceptor.Tracking.Performance;

private static readonly ObjectPool<List<(IInterceptorSubject subject, PropertyReference property, object? index)>> ListPool =
    new DefaultObjectPool<List<(IInterceptorSubject, PropertyReference, object?)>>(
        new ListPoolPolicy<(IInterceptorSubject, PropertyReference, object?)>(), 256);

private static readonly ObjectPool<HashSet<IInterceptorSubject>> HashSetPool =
    new DefaultObjectPool<HashSet<IInterceptorSubject>>(
        new HashSetPoolPolicy<IInterceptorSubject>(), 256);

private static List<...> GetList() => ListPool.Get();
private static void ReturnList(List<...> list) => ListPool.Return(list);

private static HashSet<IInterceptorSubject> GetHashSet() => HashSetPool.Get();
private static void ReturnHashSet(HashSet<IInterceptorSubject> hashSet) => HashSetPool.Return(hashSet);
```

### Step 4: Migrate SubjectUpdatePools.cs

**Before:**
```csharp
using Namotion.Interceptor.Performance;

private static readonly ObjectPool<Dictionary<IInterceptorSubject, SubjectUpdate>> KnownSubjectUpdatesPool
    = new(() => new Dictionary<IInterceptorSubject, SubjectUpdate>());

public static Dictionary<...> RentKnownSubjectUpdates() => KnownSubjectUpdatesPool.Rent();

public static void ReturnKnownSubjectUpdates(Dictionary<...> dictionary)
{
    dictionary.Clear();
    KnownSubjectUpdatesPool.Return(dictionary);
}
```

**After:**
```csharp
using Microsoft.Extensions.ObjectPool;
using Namotion.Interceptor.Tracking.Performance;

private static readonly ObjectPool<Dictionary<IInterceptorSubject, SubjectUpdate>> KnownSubjectUpdatesPool =
    new DefaultObjectPool<Dictionary<IInterceptorSubject, SubjectUpdate>>(
        new DictionaryPoolPolicy<IInterceptorSubject, SubjectUpdate>(), 256);

public static Dictionary<...> RentKnownSubjectUpdates() => KnownSubjectUpdatesPool.Get();

public static void ReturnKnownSubjectUpdates(Dictionary<...> dictionary)
{
    KnownSubjectUpdatesPool.Return(dictionary);  // Policy handles Clear()
}
```

Apply same pattern to `PropertyUpdatesPool` and `ProcessedParentPathsPool`.

### Step 5: Migrate MqttSubjectClientSource.cs

**Before:**
```csharp
using Namotion.Interceptor.Performance;

private static readonly ObjectPool<List<MqttUserProperty>> UserPropertiesPool
    = new(() => new List<MqttUserProperty>(1));
```

**After:**
```csharp
using Microsoft.Extensions.ObjectPool;
using Namotion.Interceptor.Tracking.Performance;

private static readonly ObjectPool<List<MqttUserProperty>> UserPropertiesPool =
    new DefaultObjectPool<List<MqttUserProperty>>(
        new ListPoolPolicy<MqttUserProperty>(1), 256);
```

Update usages: `Rent()` → `Get()`

### Step 6: Migrate SubscriptionManager.cs

**Before:**
```csharp
using Namotion.Interceptor.Performance;

private static readonly ObjectPool<List<PropertyUpdate>> ChangesPool
    = new(() => new List<PropertyUpdate>(16));
```

**After:**
```csharp
using Microsoft.Extensions.ObjectPool;
using Namotion.Interceptor.Tracking.Performance;

private static readonly ObjectPool<List<PropertyUpdate>> ChangesPool =
    new DefaultObjectPool<List<PropertyUpdate>>(
        new ListPoolPolicy<PropertyUpdate>(16), 256);
```

Update usages: `Rent()` → `Get()`

### Step 7: Delete Custom ObjectPool

Delete `src/Namotion.Interceptor/Performance/ObjectPool.cs`.

Also remove InternalsVisibleTo for Tracking in `Namotion.Interceptor.csproj` if no longer needed.

## Files Changed Summary

| Action | File |
|--------|------|
| MODIFY | `Namotion.Interceptor.Tracking/Namotion.Interceptor.Tracking.csproj` |
| MODIFY | `Namotion.Interceptor.Connectors/Namotion.Interceptor.Connectors.csproj` |
| MODIFY | `Namotion.Interceptor.Mqtt/Namotion.Interceptor.Mqtt.csproj` |
| MODIFY | `Namotion.Interceptor.OpcUa/Namotion.Interceptor.OpcUa.csproj` |
| CREATE | `Namotion.Interceptor.Tracking/Performance/ListPoolPolicy.cs` |
| CREATE | `Namotion.Interceptor.Tracking/Performance/HashSetPoolPolicy.cs` |
| CREATE | `Namotion.Interceptor.Tracking/Performance/DictionaryPoolPolicy.cs` |
| MODIFY | `Namotion.Interceptor.Tracking/Lifecycle/LifecycleInterceptor.cs` |
| MODIFY | `Namotion.Interceptor.Connectors/Updates/Performance/SubjectUpdatePools.cs` |
| MODIFY | `Namotion.Interceptor.Mqtt/Client/MqttSubjectClientSource.cs` |
| MODIFY | `Namotion.Interceptor.OpcUa/Client/Connection/SubscriptionManager.cs` |
| DELETE | `Namotion.Interceptor/Performance/ObjectPool.cs` |
| MODIFY | `Namotion.Interceptor/Namotion.Interceptor.csproj` (remove InternalsVisibleTo) |

## API Migration Reference

| Before | After |
|--------|-------|
| `pool.Rent()` | `pool.Get()` |
| `pool.Return(obj)` | `pool.Return(obj)` |
| Manual `obj.Clear()` before return | Handled by policy's `Return()` |
| `new ObjectPool<T>(() => new T())` | `new DefaultObjectPool<T>(policy, maxSize)` |

## Pool Configuration

All pools use max size **256** (increased from default 64) for high-throughput scenarios.

## Behavioral Analysis

### Clearing Patterns - No Regressions

The current custom `ObjectPool<T>` does **NOT** clear objects on Return:
```csharp
public void Return(T item)
{
    if (_objects.Count < _maxSize)
        _objects.Add(item);  // No Clear()!
}
```

Callers handle clearing in two ways:

| Pattern | Files | After Migration |
|---------|-------|-----------------|
| Clear → Return | SubjectUpdatePools, SubscriptionManager, LifecycleInterceptor | Redundant but harmless (policy also clears) |
| Rent → Clear | MqttSubjectClientSource | Redundant but harmless (object already clean) |

**Result:** All patterns are safe. Manual `Clear()` calls become redundant but cause no issues.

### Optional Cleanup

After migration, you can optionally remove manual `Clear()` calls since policies handle it:

```csharp
// Before (redundant clear):
dictionary.Clear();
Pool.Return(dictionary);

// After (cleaner):
Pool.Return(dictionary);  // Policy clears automatically
```

This is optional - leaving them is safe, just slightly inefficient.

### ThreadStatic → Shared Pool

The `LifecycleInterceptor.cs` change from ThreadStatic to shared pool:
- **Before:** Zero contention, but each thread keeps its own pool (memory waste with many threads)
- **After:** Minimal contention via thread-local caching in `DefaultObjectPool`, shared across threads

`DefaultObjectPool<T>` uses a `_firstItem` thread-local fast path plus a `ConcurrentBag<T>` shared store, providing good performance while sharing objects across threads.

## Verification

After migration:
1. Run `dotnet build src/Namotion.Interceptor.slnx`
2. Run `dotnet test src/Namotion.Interceptor.slnx`
3. Optionally run benchmarks: `pwsh scripts/benchmark.ps1 -Short`
