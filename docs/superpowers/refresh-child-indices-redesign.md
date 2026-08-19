# Child index refresh: what landed, and how to measure it

Implemented. This document now records the delivered state and the benchmark recipe, replacing the pre-implementation brief.

## State

- **Repo:** `/Users/ricosuter/Projects/GitHub/Namotion.Interceptor`.
- **Branch:** `fix/dictionary-key-refresh`, pushed. Two commits ahead of `master`:
  - `4e193ae5` fix(registry): keep child indices and removal correct for every container shape
  - `0baddaca` refactor(tracking): hand the derived child indices to the refresh callback
- **PR:** <https://github.com/RicoSuter/Namotion.Interceptor/pull/458>. Its body still describes only the first commit, so the redesign and the breaking change are undocumented there.
- **Green:** Registry 174, Tracking 430, Connectors 633, Namotion.Interceptor 156, Generator 244, Dynamic 6, Hosting 9, Validation 3. Zero warnings. No integration suites and no benchmarks were run.

## What the second commit changed

`IPropertyLifecycleHandler.RefreshCollectionProperty(PropertyReference, object?)` became
`RefreshChildIndices(PropertyReference, ReadOnlySpan<SubjectChildReference>)`. The lifecycle interceptor already
derives every child pair while reconciling the write, so the registry no longer derives them again from the raw
value, which is where the two copies had drifted apart three times.

Deleted from `RegisteredSubjectProperty`: `RefreshCollectionPositions`, `RefreshDictionaryKeys`,
`BuildDictionaryKeys`, `BuildCollectionPositions`, both `[ThreadStatic]` reuse dictionaries, the tolerant sort
comparer, and the shape dispatch. 215 lines out, 32 in. The `ContainerKind` byte cache stays; it serves the four
public predicates, not the refresh.

### Deviations from the original proposal, and why

- **`SubjectChildReference` carries three fields** (`Subject`, `Property`, `Index`), not two. The proposal wanted
  the `Property` field dropped, but `FindSubjectsInProperties` collects across *many* properties into one pooled
  list (`LifecycleInterceptor.cs:408`) and the attach, detach and last-detach paths read per-entry `property`.
  One pool, one element type, no copy: the tuple simply became the named struct.
- **First-wins on duplicates**, not last-wins. Attach records the first index and rejects later occurrences, so
  last-wins made a rewrite that changed nothing move a multi-key subject's path. First-wins also demotes the
  `oldTouchedSubjects.Overlaps(newTouchedSubjects)` gate from semantics to a pure optimisation.
- **The reorder is a stable permutation**, matched children in span order, unmatched children preserved in
  relative order at the tail. Rebuilding `_children` from the span would silently drop a child stranded by an
  unsupported in-place mutation, and its parent entry would then be unreachable for the `IsContextDetach` cleanup
  in `SubjectRegistry.cs:202-214`.
- **The refresh now also dispatches to a subject which implements `IPropertyLifecycleHandler` itself**, matching
  `AttachProperty`/`DetachProperty` in `LifecycleInterceptorExtensions`. The interface doc promised this.
- **Four public-API snapshot regions changed**, not one: the reverted `InternalsVisibleTo`, the interface method,
  the new `SubjectChildReference` type, and `ParentTrackingHandler`'s interface list.
- **`HomeBlaze.Services/Lifecycle/PropertyAttributeInitializer` had an implicit implementation** of the old
  method, deleted here. This is the breaking change worth stating in the PR body: an implicit implementation
  keeps compiling and silently stops being called.

## Tests

Added to `SubjectRegistryTests.cs`: first index kept for a subject under two keys; first position kept for a
subject held twice in one collection; children follow a collection reorder; children follow a dictionary
rewritten in another order. Strengthened `WhenAStrandedChildKeepsAKeyAndTheValueBecomesACollection` from
"does not throw" to asserting the stranded child survives at the tail with its old key. Added to
`ParentTrackingHandlerTests.cs`: a reorder moves the tracked index with `WithParents()` and no registry, which
was stale before this commit.

Mutation-tested, each caught: scanning from index 0 instead of the slot (2 tests), removing the reorder branch
(7 tests), and emptying the parent refresh loop (1 tracking + 2 registry tests).

## Measure this before merging

Two known risks. Neither is fixed, both are deliberate and reported rather than guessed at.

1. **`ParentTrackingHandler` probes `subject.Data` once per span entry.** `ParentsHandlerExtensions.cs:21`
   looks up a `ConcurrentDictionary<(string? property, string key), object?>` under a 37-character constant key
   whose hash is recomputed per probe. The old code probed only for children whose index actually changed, so a
   single dictionary re-key in a 1000-entry map went from about 1 probe to 1000. Attach already pays one probe
   per child, so this is a new volume, not a new kind of cost.
2. **The placement scan is O(n²) for a full reorder.** Aligned order, prepend and remove-from-front are all
   O(n). Reversing or shuffling a large collection scans to the end on every slot, so a 1000-item reversal costs
   about 500k reference comparisons plus 1000 `RemoveAt`/`Insert` moves.

Expected improvements to confirm: the caller's container is no longer enumerated a second time per retaining
write, the index box the old code allocated on change is gone (the span already carries it), and both reuse
dictionaries are gone.

### Recipe

Read `docs/benchmarking.md` first; its rules decide what the numbers mean. In particular: pin the CPU, keep the
machine quiet (this was prepared on a machine at load average 9, which is why nothing was measured there),
allocation columns survive noise far better than timings, and `-Short` decides nothing.

`ChildIndexRefreshBenchmark` is the new class, and is the only benchmark that reaches this code: the existing
`RegistryBenchmark.AddLotsOfPreviousCars` and `ChangeAllTires` replace every instance, so nothing is retained
and the callback never fires. It has six cases over `Count` {4, 1000} × `TrackParents` {false, true}, so 24 rows
per arm. `ReplaceCollection` retains nothing and is the in-class reference row for writes the refresh cannot
reach; `ServiceOrderResolverBenchmark.LinearChain` is the out-of-class noise reference the docs recommend.

Both base arms need the benchmark file, which only exists on this branch, or the filter matches nothing there.
Three local branches are already prepared on the machine where this was written:

- `bench-head` = `0baddaca` (the arm under test)
- `bench-base-458` = `4e193ae5` + the benchmark file (does the redesign regress the fix it replaces?)
- `bench-base-master` = `master` + the benchmark file (does the whole PR regress the released state?)

To recreate them anywhere:

```bash
git branch bench-head <redesign-sha>
git branch bench-base-458 <fix-sha>
git switch bench-base-458
git checkout bench-head -- src/Namotion.Interceptor.Benchmark/ChildIndexRefreshBenchmark.cs
git commit -m "test(benchmark): add the retaining-write child index cases"
```

Then, from a worktree placed **outside** the repository, on `bench-head`:

```
pwsh scripts/benchmark.ps1 -Filter "*ChildIndexRefreshBenchmark*","*ServiceOrderResolverBenchmark.LinearChain*" -BaseBranch bench-base-458 -LaunchCount 3
```

and again with `-BaseBranch bench-base-master`. Note that the master arm does not refresh dictionary keys at all,
so `RekeyOneDictionaryEntry` compares "does nothing, incorrectly" against "does the work": expect it to be
slower, and report it as the price of the fix rather than as a regression.

For "did the shared write path get slower", a benchmark is the wrong instrument, because every container write
pays that path and the delta is small. Diff the machine code instead, per the last section of
`docs/benchmarking.md`, over `WriteProperty` and `FindSubjectsInProperty`. The tuple and the struct have
identical layout, so the expectation is identical instructions.

## Deferred findings, worth folding in where cheap

- `ParentsKey` is hashed per probe even when parent tracking was never registered. Gate on a registered flag,
  mirroring `SubjectRegistryExtensions.HasSubjectIds`.
- `docs/design/tracking-lifecycle.md:170-175` still says "Two locks exist" and omits `_children`.
- `Models/ReadOnlyPersonDictionary.cs` in `Registry.Tests` duplicates
  `Namotion.Interceptor.Testing.ReadOnlyDictionaryWrapper<TKey, TValue>`; substitute and delete.
- `SubjectRegistry.cs:193` still passes an `Index` to `RemoveChild`, which ignores it.
- In-place mutation can still strand a child permanently when `_lastProcessedValues` loses sight of it, so no
  future write issues a detach. Pre-existing, and better on this branch than on master, but the `4e193ae5`
  commit message overstates it as closed.
