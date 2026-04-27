# Performance: SubjectRegistry and LifecycleInterceptor attach/detach hot path

Tracks the measurement-driven performance pass for issue #271.

## Motivation

`LifecycleInterceptor.WriteProperty` and `SubjectRegistry.HandleLifecycleChange` sit on the hot path of any consumer that mutates the subject graph. Previous attempt #67 is stale and only covered a marginal slice. This pass implements each candidate on its own branch, benchmarks it against the parent `performance/attach-detach` baseline (which already includes the new `KnownSubjectsSnapshot` benchmark), and bundles the winners.

## Hot spots

- `LifecycleInterceptor._attachedSubjects` is one global lock for all graph mutations in a context. Event invocation (`SubjectAttached`, `SubjectDetaching`) happens inside that lock.
- `SubjectRegistry._knownSubjects` is `Dictionary + lock`. Every read takes the lock, and `KnownSubjects` allocates a full `ToImmutableDictionary()` copy per access.
- `RegisteredSubject._parents` is an `ImmutableArray` mutated via `Add` / `Remove` (O(n) plus allocation per change).
- `RegisteredSubjectProperty._children` is a `List` plus invalidated immutable cache (rebuilt on every external read after a write).
- `LifecycleInterceptor._attachedSubjects[subject] = []` allocates a `HashSet` per attached subject even when only one property reference is ever held.

## Benchmark setup

- Filter: `*Registry*` (covers all candidates).
- Script: `pwsh scripts/benchmark.ps1 -Filter "*Registry*" -BaseBranch performance/attach-detach -LaunchCount 3 -Stash`.
- Each candidate also runs `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"` before benchmarking.
- The parent branch adds a `KnownSubjectsSnapshot` benchmark so candidate 1 has a baseline to compare against.

Existing coverage (per `RegistryBenchmark`):

- `AddLotsOfPreviousCars` (1000-element collection replace, exercises 4, 5, 6)
- `ChangeAllTires` (small collection replace)
- `IncrementDerivedAverage` (write plus null-replace)
- `Write` / `Read` / `DerivedAverage` (non-graph hot paths, regression check for 7)
- `GetOrAddSubjectId` / `GenerateSubjectId` (id registry paths)
- `KnownSubjectsSnapshot` (new, exercises 1 and 2)

## Decision criteria

- Keep a candidate if it shows a measurable allocation drop on at least one relevant benchmark with no regression elsewhere.
- Reject candidates that win on one benchmark but regress another by a comparable amount, unless the win is on the dominant hot path.
- Reject any candidate that loosens thread-safety guarantees without preserving documented invariants.

## Out of scope

- Changing the public `ILifecycleHandler` / `IPropertyLifecycleHandler` interfaces.
- Per-subject locking model (large blast radius, separate effort).

## Candidates

### 1. cache-known-subjects

Cache the `KnownSubjects` snapshot. Maintain a lazily rebuilt `ImmutableDictionary` field invalidated on attach / detach so reads return the cached copy without allocating.

#### Results

(pending)

### 2. concurrent-known-subjects

Replace `_knownSubjects` `Dictionary + lock` with `ConcurrentDictionary` (the author TODO from #67). Keep small write-side coordination only where multi-step atomicity matters.

#### Results

(pending)

### 3. events-outside-lock

Hoist `SubjectAttached` / `SubjectDetaching` invocation outside the lock. Buffer events to a thread-local list, fire after lock release.

#### Results

(pending)

### 4. batch-collection-mutations

Batch parent / child mutations on collection replace. Today an N-item collection replace costs O(N^2) `ImmutableArray.Remove` plus N allocations. Add a single `RemoveRange` style path.

#### Results

(pending)

### 5. inline-single-parent

Inline the single-parent case in `RegisteredSubject._parents`. Most subjects have exactly one parent, but every attach allocates a 2-element array.

#### Results

(pending)

### 6. inline-attached-references

Inline-storage for `_attachedSubjects[subject]`. Replace the per-subject `HashSet<PropertyReference>` with a `(single, extra?)` struct so the common single-reference case skips the allocation.

#### Results

(pending)

### 7. lock-free-equality-check

Lock-free fast path in `WriteProperty` for `ReferenceEquals(lastProcessed, newValue)`. Use `ConcurrentDictionary` for `_lastProcessedValues` so the equality check can happen before the lock.

#### Results

(pending)
