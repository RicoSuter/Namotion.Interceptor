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

- Status: success
- Branch: `performance/attach-detach-cache-known-subjects`
- Commit: `9112081f`
- Files changed: `src/Namotion.Interceptor.Registry/SubjectRegistry.cs` (+17 / -2)
- Notes: Added `_knownSubjectsSnapshot` field. `KnownSubjects` getter does a lock-free `Volatile.Read` first; on miss it locks, double-checks, builds the snapshot, and stores via `Volatile.Write`. Invalidation (set to null) added at the two `_knownSubjects` mutation sites under the lock. Comparison ran against `master` instead of the parent perf branch (script ignored `-BaseBranch`); the absolute numbers are still meaningful since master had no baseline benchmark for this method.

| Method                  | Mean (master)   | Mean (candidate) | Allocated (master) | Allocated (candidate) |
|-------------------------|----------------:|-----------------:|-------------------:|----------------------:|
| AddLotsOfPreviousCars   | 80,446,703 ns   | 79,142,265 ns    | 22,416,635 B       | 22,416,635 B          |
| IncrementDerivedAverage | 6,450 ns        | 6,264 ns         | 128 B              | 128 B                 |
| Write                   | 408 ns          | 397 ns           | 0 B                | 0 B                   |
| Read                    | 426 ns          | 424 ns           | 0 B                | 0 B                   |
| DerivedAverage          | 280 ns          | 274 ns           | 0 B                | 0 B                   |
| ChangeAllTires          | 15,021 ns       | 14,460 ns        | 16,064 B           | 16,064 B              |
| GetOrAddSubjectId       | 28.3 ns         | 27.7 ns          | 0 B                | 0 B                   |
| GenerateSubjectId       | 836 ns          | 822 ns           | 72 B               | 72 B                  |
| KnownSubjectsSnapshot   | (not on master) | 1.801 ns         | (not on master)    | 0 B                   |

`KnownSubjectsSnapshot` is now constant-time and zero-allocation. The previous `ToImmutableDictionary()` of a 1001-subject graph produced significant per-call allocation; the cached path skips it entirely. Other benchmarks within noise.

### 2. concurrent-known-subjects

Replace `_knownSubjects` `Dictionary + lock` with `ConcurrentDictionary` (the author TODO from #67). Keep small write-side coordination only where multi-step atomicity matters.

#### Results

- Status: success (regression on `AddLotsOfPreviousCars` ~+8 percent)
- Branch: `performance/attach-detach-concurrent-known-subjects`
- Commit: `c279d4f2`
- Files changed: `src/Namotion.Interceptor.Registry/SubjectRegistry.cs` (+30 / -23)
- Notes: `_knownSubjects` switched to `ConcurrentDictionary`, lock-free reads via `TryGetValue`, multi-step transactions moved under a new `_writeLock`. All 109 Registry and 199 Tracking tests pass. Compared against master (the script bug at the time forced master as base; now fixed). `AddLotsOfPreviousCars` regresses ~8 percent. `KnownSubjectsSnapshot` reports 1.47 ms / 400 KB but has no master baseline (the benchmark only exists on the parent branch), so this is an absolute number, not a delta. The win this candidate is meant to capture (lock contention on concurrent writes) is not exercised by the single-threaded benchmark suite. The existing 8-percent regression on the bulk-attach path is the only signed delta vs base.

| Method                  | Mean (master)    | Mean (candidate)   | Allocated (master) | Allocated (candidate) |
|-------------------------|-----------------:|-------------------:|-------------------:|----------------------:|
| AddLotsOfPreviousCars   | 77,977,910 ns    | 84,356,741 ns (+8%)| 22,416,634 B       | 22,656,712 B          |
| IncrementDerivedAverage | 6,469 ns         | 6,160 ns           | 128 B              | 128 B                 |
| Write                   | 387 ns           | 394 ns             | 0 B                | 0 B                   |
| Read                    | 427 ns           | 420 ns             | 0 B                | 0 B                   |
| DerivedAverage          | 276 ns           | 278 ns             | 0 B                | 0 B                   |
| ChangeAllTires          | 14,588 ns        | 14,971 ns          | 16,064 B           | 16,256 B              |
| GetOrAddSubjectId       | 27.8 ns          | 27.9 ns            | 0 B                | 0 B                   |
| GenerateSubjectId       | 827 ns           | 821 ns             | 72 B               | 72 B                  |
| KnownSubjectsSnapshot   | (not on master)  | 1,473,572 ns       | (not on master)    | 400,520 B             |

### 3. events-outside-lock

Hoist `SubjectAttached` / `SubjectDetaching` invocation outside the lock. Buffer events to a thread-local list, fire after lock release.

#### Results

- Status: skipped
- Reason: not benchmarkable with the current setup, and the implementation collides with a documented invariant. Concretely:
  - The `RegistryBenchmark` suite has no external subscribers wired to `SubjectAttached` / `SubjectDetaching`. Internal handlers route through `ILifecycleHandler.HandleLifecycleChange`, which we do NOT move out of the lock. With no subscriber work happening under the lock in the benchmark setup, hoisting the events shows no measurable delta.
  - `SubjectDetaching` is documented (XML doc on the event) and tested (`LifecycleEventsTests.SubjectAttached_FiresAfterHandler_And_SubjectDetaching_FiresBeforeHandler`) to fire BEFORE the lifecycle handler, so detach subscribers see the full graph. Hoisting both events outside the lock inverts the detach-side ordering, breaks the test, and silently weakens a public-facing guarantee. A partial implementation that hoists only `SubjectAttached` would preserve semantics but reduce the already-invisible win.
- Recommendation: revisit if and when a benchmark exercises external subscribers under contention. Out of scope for this pass.

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
