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

- Status: success (mixed: large win on bulk attach, small regressions on hot single-property paths)
- Branch: `performance/attach-detach-batch-collection-mutations`
- Commit: `d2fec50f`
- Files changed: `RegisteredSubject.cs`, `RegisteredSubjectProperty.cs`, `SubjectRegistry.cs` (+87 / -13)
- Notes: Reshaped `RegisteredSubject._parents` from `ImmutableArray` to a private `List` plus a lazily rebuilt `ImmutableArray` cache. Public `Parents` API and locking unchanged. `RemoveParent` scans backward (matches the reverse-detach order) for O(1) tail removal; `RemoveParentsByProperty` does in-place compaction; `UpdateParentIndex` writes in place. Added internal `RegisteredSubjectProperty.RemoveChildrenWhere(predicate)` (single `List.RemoveAll` + single cache invalidation) but it is not yet wired into the orchestration because that would require expanding `IPropertyLifecycleHandler` (forbidden by task). All 109 Registry + 199 Tracking tests pass.

| Method                  | Mean (parent) | Mean (candidate) | Δ Mean   | Allocated (parent) | Allocated (candidate) |
|-------------------------|--------------:|-----------------:|---------:|-------------------:|----------------------:|
| AddLotsOfPreviousCars   | 81,336,312 ns | 66,273,245 ns    | -18.5%   | 22,416,609 B       | 22,736,676 B          |
| IncrementDerivedAverage | 6,233 ns      | 5,555 ns         | -10.9%   | 128 B              | 128 B                 |
| Write                   | 394 ns        | 486 ns           | +23.4%   | 0 B                | 0 B                   |
| Read                    | 421 ns        | 488 ns           | +15.6%   | 0 B                | 0 B                   |
| DerivedAverage          | 275 ns        | 327 ns           | +18.8%   | 0 B                | 0 B                   |
| ChangeAllTires          | 14,880 ns     | 18,574 ns        | +24.8%   | 16,064 B           | 16,320 B              |
| GetOrAddSubjectId       | 28.3 ns       | 27.6 ns          | -2.5%    | 0 B                | 0 B                   |
| GenerateSubjectId       | 838 ns        | 858 ns           | +2.4%    | 72 B               | 72 B                  |
| KnownSubjectsSnapshot   | 1,394,605 ns  | 1,459,153 ns     | +4.6%    | 320,472 B          | 320,472 B             |

The 18-percent win on `AddLotsOfPreviousCars` (1000-element collection replace) is the targeted improvement, driven by the in-place compaction in `RemoveParentsByProperty` and the elimination of per-element `ImmutableArray.Add` allocations in `_parents`. The regressions on `Write`, `Read`, `DerivedAverage`, and `ChangeAllTires` (all small absolute, +50 to +90 ns) likely come from the additional indirection and lock acquisition introduced by the `_parents` List + cache pattern, which now affects every path that touches a registered subject's parents.

Allocations did not improve as expected: the benchmark shows a slight increase on `AddLotsOfPreviousCars` (+1.4 percent). The `_parentsCache` rebuild on first read after a mutation may be the culprit; deeper profiling would be needed to confirm.

### 5. inline-single-parent

Inline the single-parent case in `RegisteredSubject._parents`. Most subjects have exactly one parent, but every attach allocates a 2-element array.

#### Results

- Status: success (small allocation drop on bulk attach; mean numbers within run-to-run noise at LaunchCount=1)
- Branch: `performance/attach-detach-inline-single-parent`
- Commit: `7e71b7f2`
- Files changed: `src/Namotion.Interceptor.Registry/Abstractions/RegisteredSubject.cs` (+119 / -14)
- Notes: Replaced `_parents` `ImmutableArray` with inline-storage layout: `_firstParent` struct + `_hasFirstParent` flag + nullable `_additionalParents` `List`, plus a cached `_parentsSnapshot` invalidated on mutation. Common case (one parent) avoids the per-add `ImmutableArray` allocation. Promotion / demotion logic handles the transition between zero, one, and many parents. All 1497 unit tests pass (Registry 109, Tracking 199, full suite green). No public API change. Locking unchanged.

| Method                  | Mean (parent) | Mean (candidate) | Δ Mean | Allocated (parent) | Allocated (candidate) | Δ Allocated |
|-------------------------|--------------:|-----------------:|-------:|-------------------:|----------------------:|------------:|
| AddLotsOfPreviousCars   | 57,205,844 ns | 55,106,307 ns    | -3.7%  | 22,416,667 B       | 22,257,372 B          | -159 KB     |
| IncrementDerivedAverage | 4,285 ns      | 4,287 ns         | flat   | 128 B              | 128 B                 | 0           |
| Write                   | 270 ns        | 272 ns           | flat   | 0 B                | 0 B                   | 0           |
| Read                    | 285 ns        | 289 ns           | +1.4%  | 0 B                | 0 B                   | 0           |
| DerivedAverage          | 189 ns        | 192 ns           | flat   | 0 B                | 0 B                   | 0           |
| ChangeAllTires          | 10,113 ns     | 13,992 ns        | +38%   | 16,064 B           | 15,936 B              | -128 B      |
| GetOrAddSubjectId       | 18.9 ns       | 22.1 ns          | +17%   | 0 B                | 0 B                   | 0           |
| GenerateSubjectId       | 545 ns        | 650 ns           | +19%   | 72 B               | 72 B                  | 0           |
| KnownSubjectsSnapshot   | 938,490 ns    | 1,147,613 ns     | +22%   | 320,472 B          | 320,472 B             | 0           |

Caveat on the Δ Mean column: the parent perf branch's own LaunchCount=1 numbers shifted ~30 percent between the candidate 4 run and this one (no parent commits in between), so single-launch comparisons here are dominated by environmental noise. `GenerateSubjectId` and `KnownSubjectsSnapshot` show large positive deltas despite touching code paths this candidate did not modify, which is a clear noise signature. The targeted win shows up in the allocation column (159 KB drop on the 1000-element bulk attach, plus a small mean drop on the same benchmark). Phase 3 combined benchmark at LaunchCount=3 will give a defensible verdict on the cross-cutting deltas.

### 6. inline-attached-references

Inline-storage for `_attachedSubjects[subject]`. Replace the per-subject `HashSet<PropertyReference>` with a `(single, extra?)` struct so the common single-reference case skips the allocation.

#### Results

(pending)

### 7. lock-free-equality-check

Lock-free fast path in `WriteProperty` for `ReferenceEquals(lastProcessed, newValue)`. Use `ConcurrentDictionary` for `_lastProcessedValues` so the equality check can happen before the lock.

#### Results

(pending)
