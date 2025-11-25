# Batched Attach/Detach API Design Plan

## Executive Summary

The current lifecycle system processes attach/detach operations one-by-one, causing significant performance overhead when assigning large collections (1000+ items). The proposed solution **replaces singular lifecycle handler methods with plural variants** (`AttachSubjects`/`DetachSubjects`) that accept spans or collections, enabling handlers to process multiple changes in a single operation. This eliminates redundant lock acquisitions, reduces virtual dispatch overhead, and allows for optimized bulk operations. The change **directly updates `ILifecycleHandler` interface** (breaking change - acceptable per requirements), removing singular methods entirely for a cleaner, more performant API surface.

### Two-Phase Implementation Strategy

**Phase 1-2: Safe Refactoring (Implement First)**
- 100% safe, 100% necessary improvements
- Batched API with property grouping
- No behavior changes, just structural refactoring
- Expected: 60-75% performance improvement
- Target: 59.5ms → 15-25ms, 25MB → 18-22MB

**Phase 3: Experimental Optimizations (Benchmark-Driven)**
- Try various _children read/write optimizations
- Accept only if benchmarks show >5% improvement
- May provide additional 5-15% gains
- Reject if regression or minimal benefit

**This plan prioritizes proven, safe optimizations first, then validates experimental approaches with benchmarks.**

---

## Current Architecture Performance Issues

### Key Bottlenecks

**Per 1000-item attach operation:**
- **Lock acquisitions**: 2000 (1000 × `_knownSubjects` + 1000 × per-child locks)
- **Virtual method calls**: 4000+ (1000 items × 4 handlers)
- **Time**: ~60ms baseline
- **Allocations**: ~25-28 MB

### Main Problems

1. **Lock Contention**: Each child addition acquires locks separately
2. **Virtual Dispatch Overhead**: 4 handler calls × 1000 items = 4000 virtual calls
3. **Incremental Growth**: Child collections grow one-by-one instead of batched
4. **O(n²) Complexity in AddChild**: `List<T>.Contains()` check is O(n), causing ~500,000 comparisons for 1000 items

### Critical O(n²) Complexity Issue

**RegisteredSubjectProperty.AddChild** currently uses linear search for deduplication:

```csharp
internal void AddChild(SubjectPropertyChild child)
{
    lock (_children)
    {
        if (!_children.Contains(child))  // ❌ O(n) for each of 1000 items = O(n²)
        {
            _children.Add(child);
        }
    }
}
```

**Impact for 1000 sequential adds**:
- Item 0: 0 comparisons
- Item 1: 1 comparison
- Item 2: 2 comparisons
- ...
- Item 999: 999 comparisons
- **Total: ~500,000 struct equality comparisons!**

This O(n²) complexity may be **more expensive than lock contention** itself.

**RegisteredSubjectProperty.RemoveChild** has similar issues:
- `IndexOf()` is O(n) per removal
- `RemoveAt()` shifts remaining items = O(n)
- Combined: O(n×k) for k removals

**Batching solves this**: With batch operations, we can use O(n) data structures (Dictionary/HashSet) or trust the caller and skip deduplication entirely.

---

## Proposed Architecture: Batched Lifecycle API

### API Design

#### Updated Interface: `ILifecycleHandler` (Breaking Change)

```csharp
namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// Handles lifecycle events for subjects in the interceptor tree.
/// BREAKING CHANGE: Singular methods replaced with plural (batch) methods for performance.
/// </summary>
public interface ILifecycleHandler
{
    /// <summary>
    /// Called when one or more subjects are attached to the subject tree.
    /// Implementations should handle both single-item and bulk scenarios efficiently.
    /// </summary>
    /// <param name="changes">Read-only span of lifecycle changes (zero-copy access).</param>
    void AttachSubjects(ReadOnlySpan<SubjectLifecycleChange> changes);

    /// <summary>
    /// Called when one or more subjects are detached from the subject tree.
    /// Implementations should handle both single-item and bulk scenarios efficiently.
    /// </summary>
    /// <param name="changes">Read-only span of lifecycle changes (zero-copy access).</param>
    void DetachSubjects(ReadOnlySpan<SubjectLifecycleChange> changes);
}
```

**Design Rationale:**

1. **Direct replacement**: Simpler API - one interface, not two
2. **`ReadOnlySpan<T>` signature**: Zero allocation, zero copy, type safe
3. **Handles both single and bulk**: Single-item fast path for common cases

---

## Performance Improvements Expected

### Phase 1-2 (Batched API) Expected Results

**Per 1000-item attach operation:**
- **Lock acquisitions**: 2000 → ~4-10 (99%+ reduction)
- **Virtual method calls**: 4000 → 4 (99.9% reduction)
- **O(n²) comparisons**: ~500,000 → eliminated (99.9%+ reduction)
- **Expected time**: **15-25ms** (60-75% improvement from 59.5ms baseline)
- **Expected allocations**: **18-22 MB** (15-30% reduction from 25MB baseline)

**Key improvements**:
1. Property grouping eliminates repeated lock acquisitions
2. Batching eliminates repeated virtual dispatch
3. Grouping enables efficient bulk operations (no O(n²) Contains checks)

### Phase 3 (Experimental) Additional Gains

Additional optimizations may provide:
- **5-15% further improvement** if _children read patterns are allocation-heavy
- **Negligible impact** if reads are infrequent during lifecycle operations
- **Benchmark-driven**: Only keep what measurably helps

---

## Critical Optimization Needed: RegisteredSubjectProperty._children

### Current Implementation Issues

**Problem**: `Children` property allocates array on every read:
```csharp
public ICollection<SubjectPropertyChild> Children
{
    get
    {
        lock (_children)
        {
            return _children.ToArray(); // ❌ ALLOCATES ON EVERY READ!
        }
    }
}
```

**Impact**:
- ToArray() creates new array allocation on every access
- Frequent reads during lifecycle operations cause GC pressure
- Performance-critical path

### Required Optimization

**Goal**: Make `Children` reads allocation-free and lock-free

**Requirements**:
1. **Zero allocations** on read path
2. **Lock-free reads** for concurrent access
3. **Thread-safe** struct access (no torn reads)
4. **Batched writes** remain efficient

**Potential approaches to explore**:
- Volatile wrapper with immutable array
- Cached snapshot with invalidation
- Other allocation-free read patterns

**Priority**: HIGH - This is a critical performance bottleneck

---

## Performance Optimization Strategies

This section documents all potential optimization approaches. The implementation follows a **two-phase strategy**:

1. **Phase 1-2 (Safe refactoring)**: Implement 100% safe, 100% necessary improvements that are guaranteed to help
2. **Phase 3 (Experimental)**: Try different approaches with benchmarks to find optimal solutions

### Safe Optimizations (Phase 1-2)

These optimizations are **guaranteed safe** and will be implemented first:

1. **Batched API conversion** (AttachSubjects/DetachSubjects)
   - Eliminates 99%+ lock acquisitions
   - Eliminates 99.9% virtual dispatch overhead
   - Enables bulk optimizations
   - **Risk**: None - pure refactoring
   - **Benefit**: 60-75% improvement expected

2. **Property grouping in SubjectRegistry**
   - Group changes by property before calling AddChild
   - Reduces lock acquisitions from 2000 to ~4-10
   - **Risk**: None - maintains same semantics
   - **Benefit**: Major lock contention reduction

3. **Collection expressions for single items**
   - Use `ReadOnlySpan<T> span = [item];` instead of array allocation
   - **Risk**: None - compiler optimization
   - **Benefit**: Zero allocation for single-item case

4. **Remove AggressiveInlining from large methods**
   - Let JIT decide inlining strategy for methods >32 bytes
   - **Risk**: None - performance hint only
   - **Benefit**: Better code cache utilization

### Experimental Optimizations (Phase 3)

These optimizations require **benchmarking to validate** and will be tried after Phase 1-2:

#### For _children O(n²) Complexity:

1. **Skip deduplication in batched operations**
   - Trust caller to not send duplicates
   - Use List.AddRange directly
   - **Risk**: Medium - behavior change if duplicates exist
   - **Benefit**: Eliminates ~500k comparisons
   - **Validation needed**: Ensure lifecycle system never sends duplicates

2. **HashSet for temporary deduplication**
   - Use HashSet during batch add for O(n) deduplication
   - Convert back to List after
   - **Risk**: Low - maintains semantics
   - **Benefit**: O(n²) → O(n)
   - **Cost**: HashSet allocation + conversion

3. **Dictionary<SubjectPropertyChild, int> for tracking**
   - O(1) lookups instead of O(n)
   - Maintains insertion order via index value
   - **Risk**: Low - maintains semantics
   - **Benefit**: O(n²) → O(n)
   - **Cost**: Dictionary allocation

4. **Pre-allocate List capacity**
   - `_children.Capacity = _children.Count + newCount;`
   - Eliminates multiple reallocation+copy operations
   - **Risk**: None
   - **Benefit**: Reduces allocations during growth

#### For _children Read Allocations:

1. **Lazy recalculation with cached ImmutableArray**
   - Cache ImmutableArray, invalidate on writes, rebuild on first read
   - **Risk**: Low - standard lazy pattern
   - **Benefit**: Zero allocations if read multiple times without writes
   - **Cost**: One allocation on rebuild, bool dirty flag

2. **Copy-on-write ImmutableArray**
   - Store ImmutableArray directly, replace on writes
   - Return ImmutableArray directly (thread-safe)
   - **Risk**: Low - immutable semantics
   - **Benefit**: Zero allocations on reads, thread-safe
   - **Cost**: ImmutableArray.Builder allocations on writes

3. **Volatile wrapper class for ImmutableArray**
   - Wrap ImmutableArray in class with volatile field
   - **Risk**: Medium - previous attempt caused regression
   - **Benefit**: Lock-free reads, atomic struct access
   - **Cost**: Wrapper class allocation (1 per property)
   - **Note**: Only viable with batching that reduces write frequency

4. **Cached snapshot with invalidation flag**
   - Cache array snapshot, invalidate on writes
   - **Risk**: Low
   - **Benefit**: Eliminates ToArray() if read multiple times
   - **Cost**: One array allocation per invalidation

5. **Lock-free Interlocked.CompareExchange**
   - Atomic CAS-based updates
   - **Risk**: High - complex concurrent logic
   - **Benefit**: Lock-free reads and writes
   - **Cost**: Development complexity, potential retry loops

#### Other Potential Optimizations:

1. **Cache dictionary lookups in SubjectRegistry**
   - Store RegisteredSubject/RegisteredSubjectProperty refs during batch
   - Reduces 3000 dictionary lookups to ~100
   - **Risk**: Low
   - **Benefit**: Reduces dictionary access overhead

2. **ArrayPool for temporary grouping buffers**
   - Rent arrays for property grouping
   - **Risk**: Low
   - **Benefit**: Reduces allocations
   - **Cost**: ArrayPool API complexity

3. **Span-based processing throughout**
   - Use CollectionsMarshal.AsSpan where possible
   - Avoid intermediate collections
   - **Risk**: Low
   - **Benefit**: Zero-copy operations

4. **Struct pooling** (SubjectPropertyChild)
   - **Risk**: N/A - structs are value types, don't need pooling
   - **Benefit**: None

### Benchmark-Driven Development for Phase 3

For each experimental optimization:
1. Implement in isolated branch
2. Run benchmark suite (AddLotsOfPreviousCars, single property changes)
3. Compare: time, allocations, GC collections
4. Accept if: >5% improvement, no regressions, maintains correctness
5. Reject if: <5% improvement, any regression, or adds complexity without benefit

---

## Handler Migration Summary

### Handlers Requiring Update

| Handler                       | Current API       | New API          | Priority |
|-------------------------------|-------------------|------------------|----------|
| `SubjectRegistry`             | AttachSubject     | AttachSubjects   | P0       |
| `ParentTrackingHandler`       | AttachSubject     | AttachSubjects   | P1       |
| `ContextInheritanceHandler`   | AttachSubject     | AttachSubjects   | P1       |
| `HostedServiceHandler`        | AttachSubject     | AttachSubjects   | P1       |

---

## Implementation Phases

### Phase 1: Core API Update (100% Safe Refactoring)

**Goal**: Convert to batched API without changing behavior

1. Update `ILifecycleHandler` interface with breaking changes
   - Replace `AttachSubject(SubjectLifecycleChange)` with `AttachSubjects(ReadOnlySpan<SubjectLifecycleChange>)`
   - Replace `DetachSubject(SubjectLifecycleChange)` with `DetachSubjects(ReadOnlySpan<SubjectLifecycleChange>)`

2. Update `LifecycleInterceptor` to batch operations
   - Collect all changes before dispatching to handlers
   - Use collection expressions for single-item case: `[change]`

3. Update all lifecycle tests
   - Ensure all existing tests pass
   - Validate behavior unchanged

### Phase 2: Handler Migration (100% Safe Refactoring)

**Goal**: Implement batched handlers with property grouping

4. Migrate `SubjectRegistry` to batched API
   - Group changes by property
   - Call AddChild/RemoveChild once per property group
   - Maintain exact same semantics

5. Migrate `ParentTrackingHandler`
   - Simple loop over span
   - Same per-item logic

6. Migrate `ContextInheritanceHandler`
   - Simple loop over span
   - Same per-item logic

7. Migrate `HostedServiceHandler`
   - Simple loop over span
   - Same per-item logic

8. **Benchmark Phase 1-2 results**
   - **Target**: 60-75% improvement (59.5ms → 15-25ms)
   - **Target**: 15-30% memory reduction (25MB → 18-22MB)
   - **Validate**: <5% regression on single-item operations

### Phase 3: Experimental Optimizations (Benchmark-Driven)

**Goal**: Try additional optimizations, keep only what benchmarks prove helps

**Approach**: For each optimization:
- Implement in isolated branch
- Run full benchmark suite
- Accept only if >5% improvement with no regressions

**Optimization candidates** (in priority order):

9. **Skip deduplication in AddChild**
   - Validate lifecycle system never sends duplicates
   - If safe: eliminate ~500k comparisons

10. **Lazy-cached ImmutableArray for Children getter**
    - Try lazy recalculation approach
    - Benchmark read/write patterns

11. **Pre-allocate List capacity**
    - `_children.Capacity = _children.Count + newCount;`
    - Low-risk, easy win

12. **Cache dictionary lookups in SubjectRegistry**
    - Reduce 3000 lookups to ~100
    - Measure actual impact

13. **Other strategies from "Performance Optimization Strategies" section**
    - Try as time permits
    - Always benchmark before committing

### Phase 4: Final Validation

14. Run full benchmark suite
15. Validate all success criteria met
16. Document final performance characteristics
17. Update public documentation

---

## Breaking Changes

### Public API Impact

1. **`ILifecycleHandler` interface changed**:
   - ❌ Removed: `void AttachSubject(SubjectLifecycleChange change)`
   - ❌ Removed: `void DetachSubject(SubjectLifecycleChange change)`
   - ✅ Added: `void AttachSubjects(ReadOnlySpan<SubjectLifecycleChange> changes)`
   - ✅ Added: `void DetachSubjects(ReadOnlySpan<SubjectLifecycleChange> changes)`

2. **Migration required**: All implementations must update to plural methods

---

## Success Criteria

✅ **60-75% performance improvement** for large collection assignments
✅ **15-30% memory reduction** through bulk operations
✅ **Zero allocations** on `Children` property reads
✅ **Thread-safe** concurrent access
✅ **<5% regression** on single-item operations

---

## Next Steps

1. Complete Phase 1-2: Batched lifecycle API migration
2. **Prioritize Phase 3**: Optimize `_children` for allocation-free reads
3. Benchmark and validate all changes
4. Document performance characteristics
