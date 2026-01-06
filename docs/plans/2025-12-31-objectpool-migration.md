# Replace Custom ObjectPool with Microsoft.Extensions.ObjectPool

## Goal

Replace the custom `ObjectPool<T>` implementation (ConcurrentBag-based) and ThreadStatic Stack pools with `Microsoft.Extensions.ObjectPool` for code standardization and better memory behavior under high thread counts.

## Background

The codebase had two pooling patterns:

1. **Custom ObjectPool** (`Namotion.Interceptor.Performance.ObjectPool<T>`)
   - ConcurrentBag-based with configurable max size (default 64)
   - Used in: SubjectUpdatePools, MqttSubjectClientSource, SubscriptionManager, LifecycleInterceptorExtensions

2. **ThreadStatic Stack pools** in `LifecycleInterceptor.cs`
   - Per-thread stacks with no size limit
   - Zero contention but wastes memory with many threads

## Implementation

### 1. Add NuGet Package

Add `Microsoft.Extensions.ObjectPool` (v9.0.0) to `Namotion.Interceptor.csproj`.

### 2. Create Reusable Pool Policies

Create shared policies in `Namotion.Interceptor/Performance/`:

**CollectionPoolPolicy.cs** - For dictionaries (clears on return):
```csharp
public sealed class CollectionPoolPolicy<T> : PooledObjectPolicy<T>
    where T : class, IDictionary, new()
{
    public override T Create() => new();
    public override bool Return(T obj) { obj.Clear(); return true; }
}
```

**ListPoolPolicy.cs** - For lists with configurable initial capacity:
```csharp
public sealed class ListPoolPolicy<T> : PooledObjectPolicy<List<T>>
{
    private readonly int _initialCapacity;
    public ListPoolPolicy(int initialCapacity = 0) => _initialCapacity = initialCapacity;
    public override List<T> Create() => new(_initialCapacity);
    public override bool Return(List<T> obj) { obj.Clear(); return true; }
}
```

**HashSetPoolPolicy.cs** - For hash sets:
```csharp
public sealed class HashSetPoolPolicy<T> : PooledObjectPolicy<HashSet<T>>
{
    public override HashSet<T> Create() => [];
    public override bool Return(HashSet<T> obj) { obj.Clear(); return true; }
}
```

### 3. Update Pool Usages

All pools use max size 256 (higher than default for high-throughput scenarios).

**LifecycleInterceptorExtensions.cs** - ReferenceCounter pool:
```csharp
// ReferenceCounter implements IResettable for automatic reset
private sealed class ReferenceCounter : IResettable
{
    public int Value;
    public bool TryReset() { Value = 0; return true; }
}

private static readonly ObjectPool<ReferenceCounter> CounterPool =
    new DefaultObjectPool<ReferenceCounter>(new DefaultPooledObjectPolicy<ReferenceCounter>(), 256);
```

**LifecycleInterceptor.cs** - Replace ThreadStatic Stack pools:
```csharp
// Before: [ThreadStatic] private static Stack<List<...>>? _listPool;
// After:
private static readonly ObjectPool<List<(IInterceptorSubject, PropertyReference, object?)>> ListPool =
    new DefaultObjectPool<...>(new ListPoolPolicy<...>(), 256);
```

**SubjectUpdatePools.cs** - Dictionary and HashSet pools:
```csharp
private static readonly ObjectPool<Dictionary<IInterceptorSubject, SubjectUpdate>> KnownSubjectUpdatesPool =
    new DefaultObjectPool<...>(new CollectionPoolPolicy<...>(), 256);
```

**MqttSubjectClientSource.cs** - UserProperties list pool:
```csharp
private static readonly ObjectPool<List<MqttUserProperty>> UserPropertiesPool =
    new DefaultObjectPool<List<MqttUserProperty>>(new ListPoolPolicy<MqttUserProperty>(1), 256);
```

**SubscriptionManager.cs** - Changes list pool:
```csharp
private static readonly ObjectPool<List<PropertyUpdate>> ChangesPool =
    new DefaultObjectPool<List<PropertyUpdate>>(new ListPoolPolicy<PropertyUpdate>(16), 256);
```

### 4. API Changes

| Before | After |
|--------|-------|
| `pool.Rent()` | `pool.Get()` |
| `pool.Return(obj)` | `pool.Return(obj)` |
| Manual `obj.Clear()` before return | Handled by policy |

### 5. Delete Custom Implementation

Delete `src/Namotion.Interceptor/Performance/ObjectPool.cs`.

## Files Changed

| File | Change |
|------|--------|
| `Namotion.Interceptor.csproj` | Add Microsoft.Extensions.ObjectPool package |
| `Performance/ObjectPool.cs` | **DELETED** |
| `Performance/CollectionPoolPolicy.cs` | **CREATED** |
| `Performance/ListPoolPolicy.cs` | **CREATED** |
| `Performance/HashSetPoolPolicy.cs` | **CREATED** |
| `LifecycleInterceptorExtensions.cs` | Update pool, add IResettable |
| `LifecycleInterceptor.cs` | Replace ThreadStatic with shared pools |
| `SubjectUpdatePools.cs` | Update all 3 pools |
| `MqttSubjectClientSource.cs` | Update UserPropertiesPool |
| `SubscriptionManager.cs` | Update ChangesPool |

## Benchmark Results

Comparing feature branch vs master (same machine, Linux i7-11700K):

| Benchmark | Master | Feature | Change |
|-----------|--------|---------|--------|
| AddLotsOfPreviousCars | 53.9ms / 23.8MB | 55.4ms / 22.4MB | +3% time / **-6% alloc** |
| ChangeAllTires | 12,098 ns / 17.6KB | 13,339 ns / 16.4KB | +10% time / **-7% alloc** |
| Write | 373 ns | 426 ns | +14% time |
| Read | 451 ns | 458 ns | +2% |
| DerivedAverage | 321 ns | 310 ns | -3% |
| IncrementDerivedAverage | 7,913 ns | 7,781 ns | -2% |
| CreateCompleteUpdate | 5,382 ns | 5,117 ns | -5% |
| CreatePartialUpdate | 2,099 ns | 2,048 ns | -2% |

**Trade-offs:**
- Memory allocation improved in key areas (AddLotsOfPreviousCars -6%, ChangeAllTires -7%)
- CPU regression in some hot paths (Write +14%, ChangeAllTires +10%)
- The ThreadStatic → shared pool change adds synchronization overhead but reduces memory waste with many threads

## Alternatives Considered

1. **Keep ThreadStatic for LifecycleInterceptor only** - Would preserve zero-contention performance but lose code consistency
2. **Keep custom ObjectPool entirely** - Simpler but non-standard, harder to maintain
3. **Use ArrayPool for lists** - More complex, doesn't help with Dictionary/HashSet

## Decision

Proceed with full migration to standardize on Microsoft.Extensions.ObjectPool. The memory improvements and code standardization outweigh the minor CPU regression. The shared pool will perform better under high thread counts where ThreadStatic would waste memory.
