# Microsoft.Extensions.ObjectPool Migration Plan

## Goal

Replace all custom pooling implementations with `Microsoft.Extensions.ObjectPool` for code standardization and consistent memory behavior.

## Current State

The codebase has **8 pool usages** across 4 files:

| File | Current Implementation | Collection Type |
|------|------------------------|-----------------|
| `SubjectUpdatePools.cs` | Custom ObjectPool | `Dictionary<IInterceptorSubject, SubjectUpdate>` |
| `SubjectUpdatePools.cs` | Custom ObjectPool | `Dictionary<SubjectPropertyUpdate, SubjectPropertyUpdateReference>` |
| `SubjectUpdatePools.cs` | Custom ObjectPool | `HashSet<IInterceptorSubject>` |
| `MqttSubjectClientSource.cs` | Custom ObjectPool | `List<MqttUserProperty>` |
| `SubscriptionManager.cs` | Custom ObjectPool | `List<PropertyUpdate>` |
| `LifecycleInterceptor.cs` | ThreadStatic Stack | `List<(IInterceptorSubject, PropertyReference, object?)>` |
| `LifecycleInterceptor.cs` | ThreadStatic Stack | `HashSet<IInterceptorSubject>` |
| `LifecycleInterceptor.cs` | ThreadStatic Stack | `HashSet<PropertyReference>` |

## Implementation Plan

### Step 1: Add NuGet Package to Required Projects

Add `Microsoft.Extensions.ObjectPool` v9.0.0 to these projects:

| Project | File |
|---------|------|
| `Namotion.Interceptor.Registry` | `Namotion.Interceptor.Registry.csproj` |
| `Namotion.Interceptor.Tracking` | `Namotion.Interceptor.Tracking.csproj` |

```xml
<PackageReference Include="Microsoft.Extensions.ObjectPool" Version="9.0.0" />
```

Note: `Namotion.Interceptor.Connectors`, `Namotion.Interceptor.Mqtt`, and `Namotion.Interceptor.OpcUa` get the package transitively through their reference to Registry.

### Step 2: Create Reusable Pool Policies

Create in `Namotion.Interceptor.Registry/Performance/` (replacing the custom ObjectPool):

**ListPoolPolicy.cs:**
```csharp
using Microsoft.Extensions.ObjectPool;

namespace Namotion.Interceptor.Registry.Performance;

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

namespace Namotion.Interceptor.Registry.Performance;

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

namespace Namotion.Interceptor.Registry.Performance;

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
private static Stack<HashSet<IInterceptorSubject>>? _subjectHashSetPool;

[ThreadStatic]
private static Stack<HashSet<PropertyReference>>? _propertyHashSetPool;

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
// Similar for GetSubjectHashSet, GetPropertyHashSet, ReturnSubjectHashSet, ReturnPropertyHashSet
```

**After:**
```csharp
using Microsoft.Extensions.ObjectPool;
using Namotion.Interceptor.Registry.Performance;

private static readonly ObjectPool<List<(IInterceptorSubject subject, PropertyReference property, object? index)>> ListPool =
    new DefaultObjectPool<List<(IInterceptorSubject, PropertyReference, object?)>>(
        new ListPoolPolicy<(IInterceptorSubject, PropertyReference, object?)>(8), 256);

private static readonly ObjectPool<HashSet<IInterceptorSubject>> SubjectHashSetPool =
    new DefaultObjectPool<HashSet<IInterceptorSubject>>(
        new HashSetPoolPolicy<IInterceptorSubject>(), 256);

private static readonly ObjectPool<HashSet<PropertyReference>> PropertyHashSetPool =
    new DefaultObjectPool<HashSet<PropertyReference>>(
        new HashSetPoolPolicy<PropertyReference>(), 256);

private static List<...> GetList() => ListPool.Get();
private static void ReturnList(List<...> list) => ListPool.Return(list);  // REMOVED: list.Clear()

private static HashSet<IInterceptorSubject> GetSubjectHashSet() => SubjectHashSetPool.Get();
private static void ReturnSubjectHashSet(HashSet<IInterceptorSubject> hashSet) => SubjectHashSetPool.Return(hashSet);  // REMOVED: hashSet.Clear()

private static HashSet<PropertyReference> GetPropertyHashSet() => PropertyHashSetPool.Get();
private static void ReturnPropertyHashSet(HashSet<PropertyReference> hashSet) => PropertyHashSetPool.Return(hashSet);  // REMOVED: hashSet.Clear()
```

All manual `Clear()` calls removed - policies handle clearing in `Return()`.

### Step 4: Migrate SubjectUpdatePools.cs

**Before:**
```csharp
using Namotion.Interceptor.Registry.Performance;

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
using Namotion.Interceptor.Registry.Performance;

private static readonly ObjectPool<Dictionary<IInterceptorSubject, SubjectUpdate>> KnownSubjectUpdatesPool =
    new DefaultObjectPool<Dictionary<IInterceptorSubject, SubjectUpdate>>(
        new DictionaryPoolPolicy<IInterceptorSubject, SubjectUpdate>(), 256);

public static Dictionary<...> RentKnownSubjectUpdates() => KnownSubjectUpdatesPool.Get();

public static void ReturnKnownSubjectUpdates(Dictionary<...> dictionary)
{
    // REMOVED: dictionary.Clear() - policy handles this
    KnownSubjectUpdatesPool.Return(dictionary);
}
```

Apply same pattern to `PropertyUpdatesPool` and `ProcessedParentPathsPool` - remove all manual `Clear()` calls.

### Step 5: Migrate MqttSubjectClientSource.cs

**Before:**
```csharp
using Namotion.Interceptor.Registry.Performance;

private static readonly ObjectPool<List<MqttUserProperty>> UserPropertiesPool
    = new(() => new List<MqttUserProperty>(1));

// Usage (inefficient - clears on acquire):
var userProps = UserPropertiesPool.Rent();
userProps.Clear();
```

**After:**
```csharp
using Microsoft.Extensions.ObjectPool;
using Namotion.Interceptor.Registry.Performance;

private static readonly ObjectPool<List<MqttUserProperty>> UserPropertiesPool =
    new DefaultObjectPool<List<MqttUserProperty>>(
        new ListPoolPolicy<MqttUserProperty>(1), 256);

// Usage (efficient - already clean from policy):
var userProps = UserPropertiesPool.Get();
// REMOVED: userProps.Clear() - policy cleared on Return()
```

Update usages: `Rent()` → `Get()`, remove `Clear()` after `Get()`

### Step 6: Migrate SubscriptionManager.cs

**Before:**
```csharp
using Namotion.Interceptor.Registry.Performance;

private static readonly ObjectPool<List<PropertyUpdate>> ChangesPool
    = new(() => new List<PropertyUpdate>(16));

// Usage:
s.changes.Clear();
ChangesPool.Return(s.changes);
```

**After:**
```csharp
using Microsoft.Extensions.ObjectPool;
using Namotion.Interceptor.Registry.Performance;

private static readonly ObjectPool<List<PropertyUpdate>> ChangesPool =
    new DefaultObjectPool<List<PropertyUpdate>>(
        new ListPoolPolicy<PropertyUpdate>(16), 256);

// Usage (efficient - no manual clear):
// REMOVED: s.changes.Clear() - policy handles this
ChangesPool.Return(s.changes);
```

Update usages: `Rent()` → `Get()`, remove `Clear()` before `Return()`

### Step 7: Delete Custom ObjectPool

Delete `src/Namotion.Interceptor.Registry/Performance/ObjectPool.cs`.

## Files Changed Summary

| Action | File |
|--------|------|
| MODIFY | `Namotion.Interceptor.Registry/Namotion.Interceptor.Registry.csproj` |
| MODIFY | `Namotion.Interceptor.Tracking/Namotion.Interceptor.Tracking.csproj` |
| CREATE | `Namotion.Interceptor.Registry/Performance/ListPoolPolicy.cs` |
| CREATE | `Namotion.Interceptor.Registry/Performance/HashSetPoolPolicy.cs` |
| CREATE | `Namotion.Interceptor.Registry/Performance/DictionaryPoolPolicy.cs` |
| MODIFY | `Namotion.Interceptor.Tracking/Lifecycle/LifecycleInterceptor.cs` |
| MODIFY | `Namotion.Interceptor.Connectors/Updates/Performance/SubjectUpdatePools.cs` |
| MODIFY | `Namotion.Interceptor.Mqtt/Client/MqttSubjectClientSource.cs` |
| MODIFY | `Namotion.Interceptor.OpcUa/Client/Connection/SubscriptionManager.cs` |
| DELETE | `Namotion.Interceptor.Registry/Performance/ObjectPool.cs` |

## API Migration Reference

| Before | After |
|--------|-------|
| `pool.Rent()` | `pool.Get()` |
| `pool.Return(obj)` | `pool.Return(obj)` |
| Manual `obj.Clear()` before return | Handled by policy's `Return()` |
| `new ObjectPool<T>(() => new T())` | `new DefaultObjectPool<T>(policy, maxSize)` |

## Pool Configuration

All pools use max size **256** (increased from default 64) for high-throughput scenarios.

## Performance: Clear() Strategy

### Principle: Clear on Return, Never on Acquire

For maximum performance, collections should be cleared **only once** in the policy's `Return()` method. This ensures:
- No redundant clearing (double Clear() wastes CPU cycles)
- Predictable state (caller always receives clean object from Get())
- Single responsibility (policy owns cleanup logic)

### Current Patterns to Fix

| File | Current Pattern | Problem | Required Fix |
|------|-----------------|---------|--------------|
| `SubjectUpdatePools.cs` | `Clear()` → `Return()` | Redundant (policy clears) | Remove `Clear()` calls |
| `SubscriptionManager.cs` | `Clear()` → `Return()` | Redundant (policy clears) | Remove `Clear()` call |
| `LifecycleInterceptor.cs` | `Clear()` → `Push()` | Will be redundant | Remove `Clear()` calls |
| `MqttSubjectClientSource.cs` | `Rent()` → `Clear()` | Wrong location | Remove `Clear()` call |

### After Migration

```csharp
// WRONG - double clear, wastes cycles:
dictionary.Clear();
Pool.Return(dictionary);

// WRONG - clearing on acquire instead of return:
var list = Pool.Get();
list.Clear();

// CORRECT - policy handles clearing in Return():
Pool.Return(dictionary);  // Policy clears here
var list = Pool.Get();    // Already clean
```

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
