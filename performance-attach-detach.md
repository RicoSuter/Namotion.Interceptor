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

- Status: success — but **net negative** at LaunchCount=3 (recommend reject)
- Branch: `performance/attach-detach-batch-collection-mutations`
- Commit: `d2fec50f`
- Files changed: `RegisteredSubject.cs`, `RegisteredSubjectProperty.cs`, `SubjectRegistry.cs` (+87 / -13)
- Notes: Reshaped `RegisteredSubject._parents` from `ImmutableArray` to a private `List` plus a lazily rebuilt `ImmutableArray` cache. Public `Parents` API and locking unchanged. `RemoveParent` scans backward (matches the reverse-detach order) for O(1) tail removal; `RemoveParentsByProperty` does in-place compaction; `UpdateParentIndex` writes in place. Added internal `RegisteredSubjectProperty.RemoveChildrenWhere(predicate)` (single `List.RemoveAll` + single cache invalidation) but not wired into orchestration (would require expanding `IPropertyLifecycleHandler`). All 109 Registry + 199 Tracking tests pass.

LaunchCount=3 results (the LaunchCount=1 numbers reported earlier turned out to be dominated by baseline drift; the supposed -18.5 percent on AddLots was noise):

| Method                  | Mean (parent) | Mean (candidate) | Δ Mean | Allocated (parent) | Allocated (candidate) |
|-------------------------|--------------:|-----------------:|-------:|-------------------:|----------------------:|
| AddLotsOfPreviousCars   | 54,077,497 ns | 55,579,390 ns    | +2.8%  | 22,416,671 B       | 22,736,756 B          |
| IncrementDerivedAverage | 4,159.95 ns   | 4,160.25 ns      | flat   | 128 B              | 128 B                 |
| Write                   | 266.62 ns     | 265.66 ns        | flat   | 0 B                | 0 B                   |
| Read                    | 284.40 ns     | 288.62 ns        | +1.5%  | 0 B                | 0 B                   |
| DerivedAverage          | 185.29 ns     | 180.24 ns        | -2.7%  | 0 B                | 0 B                   |
| ChangeAllTires          | 9,790.01 ns   | 9,497.63 ns      | -3.0%  | 16,064 B           | 16,320 B              |
| GetOrAddSubjectId       | 19.76 ns      | 18.17 ns         | -8.0%  | 0 B                | 0 B                   |
| GenerateSubjectId       | 540.87 ns     | 531.30 ns        | -1.8%  | 72 B               | 72 B                  |
| KnownSubjectsSnapshot   | 926,600 ns    | 898,454 ns       | -3.0%  | 320,472 B          | 320,472 B             |

At LaunchCount=3, `AddLotsOfPreviousCars` regresses ~2.8 percent on mean and adds 320 KB of allocation (the `_parentsCache` rebuild allocates a fresh ImmutableArray per read-after-mutation, which offsets the in-place compaction win and then some). All other deltas are within ±3 percent — noise band even at LaunchCount=3. The candidate's targeted hot path is the only one with a real signed delta, and it is negative. Recommend rejecting.

### 5. inline-single-parent

Inline the single-parent case in `RegisteredSubject._parents`. Most subjects have exactly one parent, but every attach allocates a 2-element array.

#### Results

- Status: success — but **net negative** at LaunchCount=3 on the targeted bulk-attach path (recommend reject)
- Branch: `performance/attach-detach-inline-single-parent`
- Commit: `7e71b7f2`
- Files changed: `src/Namotion.Interceptor.Registry/Abstractions/RegisteredSubject.cs` (+119 / -14)
- Notes: Replaced `_parents` `ImmutableArray` with inline-storage layout: `_firstParent` struct + `_hasFirstParent` flag + nullable `_additionalParents` `List`, plus a cached `_parentsSnapshot` invalidated on mutation. Common case (one parent) avoids the per-add `ImmutableArray` allocation. Promotion / demotion logic handles the transition between zero, one, and many parents. All 1497 unit tests pass (Registry 109, Tracking 199, full suite green). No public API change. Locking unchanged.

LaunchCount=3 results (replaces the earlier LaunchCount=1 numbers, which were dominated by baseline drift):

| Method                  | Mean (parent)   | Mean (candidate) | Δ Mean | Allocated (parent) | Allocated (candidate) | Δ Allocated |
|-------------------------|----------------:|-----------------:|-------:|-------------------:|----------------------:|------------:|
| AddLotsOfPreviousCars   | 54,959,038 ns   | 64,635,493 ns    | +17.6% | 22,416,691 B       | 22,257,019 B          | -159 KB     |
| IncrementDerivedAverage | 4,242 ns        | 4,969 ns         | +17.1% | 128 B              | 128 B                 | 0           |
| Write                   | 268.86 ns       | 322.63 ns        | +20.0% | 0 B                | 0 B                   | 0           |
| Read                    | 290.38 ns       | 362.01 ns        | +24.7% | 0 B                | 0 B                   | 0           |
| DerivedAverage          | 196.20 ns       | 206.29 ns        | +5.1%  | 0 B                | 0 B                   | 0           |
| ChangeAllTires          | 11,571 ns       | 9,784 ns         | -15.4% | 16,064 B           | 15,936 B              | -128 B      |
| GetOrAddSubjectId       | 20.85 ns        | 18.94 ns         | -9.2%  | 0 B                | 0 B                   | 0           |
| GenerateSubjectId       | 595.60 ns       | 533.17 ns        | -10.5% | 72 B               | 72 B                  | 0           |
| KnownSubjectsSnapshot   | 1,103,203 ns    | 917,887 ns       | -16.8% | 320,472 B          | 320,472 B             | 0           |

Cross-cutting noise is still visible: `Write`, `Read`, `DerivedAverage`, `GenerateSubjectId`, `KnownSubjectsSnapshot`, and `GetOrAddSubjectId` all post double-digit deltas in different directions despite the candidate not touching their code paths. Three LaunchCounts is not enough to fully suppress system-level drift on this run. The signed deltas that *can* plausibly be attributed to the candidate are:

- `AddLotsOfPreviousCars`: +17.6% mean, -159 KB allocated. The 1000-subject bulk attach pays for the new cache-rebuild path on every read after every parent mutation, and the saved `ImmutableArray` allocation is not enough to offset the per-mutation overhead.
- `ChangeAllTires`: -15.4% mean, -128 B allocated. The smaller bulk-attach path benefits because the cache-rebuild is amortized across far fewer mutations.

Net judgment: the candidate trades CPU on the largest hot path (1000-subject attach) for a 159 KB allocation drop. The mean regression on the dominant bulk-attach scenario outweighs the allocation win. Same conclusion as candidate 4. Recommend rejecting.

### 6. inline-attached-references

Inline-storage for `_attachedSubjects[subject]`. Replace the per-subject `HashSet<PropertyReference>` with a `(single, extra?)` struct so the common single-reference case skips the allocation.

#### Results

- Status: success (clean allocation win on bulk attach paths)
- Branch: `performance/attach-detach-inline-attached-references`
- Commit: `38aa3d71`
- Files changed: `src/Namotion.Interceptor.Tracking/Lifecycle/LifecycleInterceptor.cs` (+111 / -6)
- Notes: Replaced `Dictionary<IInterceptorSubject, HashSet<PropertyReference>>` with `Dictionary<IInterceptorSubject, PropertyReferenceSet>` where `PropertyReferenceSet` is a private struct holding the first reference inline and only allocating a backing `HashSet` for the second-and-beyond references. Invariant: `Additional` never duplicates `First`. Used `CollectionsMarshal.GetValueRefOrAddDefault` and `CollectionsMarshal.GetValueRefOrNullRef` to mutate the dictionary value slot in place; without these the struct copy semantics would silently drop mutations. Lock scope and ordering unchanged. Public surface unchanged. All Registry + Tracking unit tests pass.

| Method                  | Mean (parent) | Mean (candidate) | Δ Mean | Allocated (parent) | Allocated (candidate) | Δ Allocated |
|-------------------------|--------------:|-----------------:|-------:|-------------------:|----------------------:|------------:|
| AddLotsOfPreviousCars   | 56,958,413 ns | 52,468,750 ns    | -7.9%  | 22,416,667 B       | 20,456,707 B          | **-1.96 MB**|
| IncrementDerivedAverage | 4,373 ns      | 4,443 ns         | +1.6%  | 128 B              | 128 B                 | 0           |
| Write                   | 278 ns        | 284 ns           | +2.2%  | 0 B                | 0 B                   | 0           |
| Read                    | 299 ns        | 323 ns           | +8.0%  | 0 B                | 0 B                   | 0           |
| DerivedAverage          | 200 ns        | 204 ns           | +2.3%  | 0 B                | 0 B                   | 0           |
| ChangeAllTires          | 10,163 ns     | 10,250 ns        | +0.9%  | 16,064 B           | 14,496 B              | -1,568 B    |
| GetOrAddSubjectId       | 19.3 ns       | 22.6 ns          | +17%   | 0 B                | 0 B                   | 0           |
| GenerateSubjectId       | 554 ns        | 652 ns           | +18%   | 72 B               | 72 B                  | 0           |
| KnownSubjectsSnapshot   | 980,247 ns    | 1,123,147 ns     | +14.6% | 320,472 B          | 320,472 B             | 0           |

The targeted allocation drop is clear: each subject in the bulk attach now skips a per-subject `HashSet` allocation (~2 KB per subject × 1000 subjects ~= 2 MB saved on `AddLotsOfPreviousCars`, plus ~1.5 KB saved on the smaller `ChangeAllTires`). `GenerateSubjectId` and `KnownSubjectsSnapshot` regressing despite untouched code paths and identical allocations is the same noise signature seen in candidate 5; LaunchCount=3 in Phase 3 will settle whether any cross-cutting mean delta is real.

### 7. lock-free-equality-check

Lock-free fast path in `WriteProperty` for `ReferenceEquals(lastProcessed, newValue)`. Use `ConcurrentDictionary` for `_lastProcessedValues` so the equality check can happen before the lock.

#### Results

- Status: success — but **net negative** at LaunchCount=3 for the current benchmark coverage (recommend reject)
- Branch: `performance/attach-detach-lock-free-equality-check`
- Commit: `c8230995`
- Files changed: `src/Namotion.Interceptor.Tracking/Lifecycle/LifecycleInterceptor.cs` (+22 / -4)
- Notes: `_lastProcessedValues` switched to `ConcurrentDictionary`. Added a lock-free `TryGetValue` + `ReferenceEquals` early-out before lock acquisition in `WriteProperty`. Locked path retains the existing double-checked re-read so concurrent updates between fast-path and lock acquisition are caught. All mutating sites still hold the `_attachedSubjects` lock; only `Remove` -> `TryRemove` rename was needed. All Tracking + Registry unit tests pass (one transient flake in `Hosting.Tests` due to a `Task.Delay(100)` race, unrelated, passes on rerun).

LaunchCount=3 results (replaces the earlier LaunchCount=1 numbers, which showed an across-the-board ~15-19 percent shift driven by baseline drift, including on benchmarks the candidate cannot affect):

| Method                  | Mean (parent) | Mean (candidate) | Δ Mean | Allocated (parent) | Allocated (candidate) | Δ Allocated |
|-------------------------|--------------:|-----------------:|-------:|-------------------:|----------------------:|------------:|
| AddLotsOfPreviousCars   | 53,526,991 ns | 58,590,081 ns    | +9.5%  | 22,416,703 B       | 22,656,796 B          | +240 KB     |
| IncrementDerivedAverage | 4,179.52 ns   | 4,188.68 ns      | flat   | 128 B              | 128 B                 | 0           |
| Write                   | 264.19 ns     | 271.66 ns        | +2.8%  | 0 B                | 0 B                   | 0           |
| Read                    | 284.31 ns     | 296.09 ns        | +4.1%  | 0 B                | 0 B                   | 0           |
| DerivedAverage          | 186.85 ns     | 187.69 ns        | flat   | 0 B                | 0 B                   | 0           |
| ChangeAllTires          | 10,447 ns     | 10,052 ns        | -3.8%  | 16,064 B           | 16,064 B              | 0           |
| GetOrAddSubjectId       | 18.68 ns      | 18.84 ns         | flat   | 0 B                | 0 B                   | 0           |
| GenerateSubjectId       | 531.10 ns     | 551.53 ns        | +3.8%  | 72 B               | 72 B                  | 0           |
| KnownSubjectsSnapshot   | 919,494 ns    | 964,616 ns       | +4.9%  | 320,472 B          | 320,472 B             | 0           |

Cross-cutting noise mostly settled at LaunchCount=3. `IncrementDerivedAverage`, `DerivedAverage`, `GetOrAddSubjectId` are flat as expected (untouched code paths). The signed deltas attributable to the candidate:

- `AddLotsOfPreviousCars`: +9.5% mean, +240 KB allocated. The 1000-subject bulk attach hammers the locked write path (every subject's properties get a first-time write, so the fast path always misses); `ConcurrentDictionary` is measurably slower than `Dictionary` here and adds per-bucket allocation. Real signal.
- Cross-cutting +3-5% on `Write` / `Read` / `GenerateSubjectId` / `KnownSubjectsSnapshot` is harder to attribute, since these benchmarks should be ~unaffected by the change. Likely residual baseline drift, but the consistent direction leaves room for a small fixed-cost regression on the fast path itself.

The fast path NEVER triggers in any existing benchmark (every value is fresh). The win this candidate is meant to capture (skip the lock when the same reference is written twice) is never measured, while the cost of switching to `ConcurrentDictionary` shows up cleanly on the bulk-attach path. Without a benchmark that exercises same-reference writes, this candidate is purely a regression. Recommend rejecting; revisit if and when a same-reference-write benchmark is added.
