# Task 12 Lifecycle Fixes Report

## Scope and baseline

- Worktree: `/Users/ricosuter/Projects/GitHub/Namotion.Interceptor/.claude/worktrees/pr-458`
- Baseline: `8fc27b8a18159dce31a3132452615f06b3f71467`
- Scope: Tracking lifecycle final-review findings 1 through 3 only.
- Design inputs: `AGENTS.md`, the approved container relationship reconciliation design, the lifecycle internal design, and the relevant lifecycle, reconciler, Tracking test, and Registry test code.
- Implementation reports were not read.

## Finding 1: three-authority same-property reentry

### Hypothesis

With outer authorities A, B, and C, C commits its membership and invokes the reentrant relationship callback. The nested setter reaches A first and throws A's private same-property marker. C has already committed. B rejects the marker because the old predicate requires marker authority identity, so B skips reconciliation. A recognizes its marker, reconciles, and rethrows. The child therefore receives two authority contributions instead of three.

### RED

The existing integration test was extended to register three explicit `LifecycleInterceptor` authorities and require three reference contributions.

Command:

```text
dotnet test src/Namotion.Interceptor.Registry.Tests/Namotion.Interceptor.Registry.Tests.csproj --filter "FullyQualifiedName~ConcurrentStructuralWriteLeakTests.WhenARelationshipCallbackReentersTheSameProperty_ThenTheCommittedGenerationRemainsCanonical" --no-restore
```

Observed failure:

```text
Assert.Equal() Failure: Values differ
Expected: 3
Actual:   2
ConcurrentStructuralWriteLeakTests.cs:line 67
Failed: 1, Passed: 0, Total: 1
```

### Root cause and implementation

`IsSamePropertyReconciliationException` matched both the private marker's authority and its `PropertyReference`. That allowed only the marker-owning upstream authority to complete during unwind. The marker remains private, but it now carries and matches only the guarded `PropertyReference`. Every upstream authority whose outer terminal already committed can finish that same property generation before the original guard exception is rethrown. Unmarked `InvalidOperationException` instances are still not caught, and the property comparison preserves different-property behavior.

### GREEN

The exact focused command passed 1 of 1 tests. A later replacement also proved all three authorities detached the original generation and contributed to the replacement.

## Finding 2: stale setter-only concurrent reconciliation

### Hypothesis

Getter-backed structural properties converge because each authority re-reads the backing value after acquiring its lifecycle lock. Getterless properties instead use the invocation's captured written value. If T1 commits A and blocks downstream before lifecycle resumes, T2 can commit and fully reconcile B. When T1 resumes, its older captured A currently overwrites B's canonical processed state.

### RED

A deterministic event-gated test parks T1 after the terminal commit. T2 then commits and fully reconciles B before T1 is released. There are no delay-based waits.

Command:

```text
dotnet test src/Namotion.Interceptor.Tracking.Tests/Namotion.Interceptor.Tracking.Tests.csproj --filter "FullyQualifiedName~ConcurrentWriteLifecycleTests.WhenAnOlderSetterOnlyWriteResumesAfterANewerReconciliation_ThenTheNewerCommittedGenerationRemainsCanonical" --no-restore
```

Observed failure after T1 resumed:

```text
Assert.Equal() Failure: Values differ
Expected: 0
Actual:   1
ConcurrentWriteLifecycleTests.cs:line 313
Failed: 1, Passed: 0, Total: 1
```

Before T1 was released, the test had already proved B owned the sole membership and the published relationship generation.

### Root cause and implementation

The terminal assigns `PropertyWriteContext.Revision`, but `LifecycleInterceptor` discarded it. The revision now flows into structural reconciliation and is retained privately in `ProcessedPropertyState`. Getterless reconciliation returns without mutation when its committed revision is older than the property's canonical revision. The stored revision is monotonic for ordinary reconciliation. Getter-backed properties retain the existing behavior of re-reading the actual backing value under the lifecycle lock.

### GREEN

The exact focused command passed 1 of 1 tests. After release, A remained detached, B retained one contribution, and the final relationship generation still referenced B.

## Finding 3: reentrant structural writes during initial attach staging

### Hypothesis

`StageSubjectProperties` invokes structural getters and enumerators while the subject is in `_attachingSubjects` but before any complete staged generation is committed. A reentrant setter mutates backing storage. Its nested reconciliation sees that the parent is not attached and returns. The outer attach then commits descriptors captured from the old same property or from an earlier already-staged property, leaving backing storage and canonical metadata inconsistent.

### RED

Two generated-property tests cover:

1. An enumerable that writes the property currently being enumerated.
2. A later enumerable that writes an earlier already-staged structural property.

Command:

```text
dotnet test src/Namotion.Interceptor.Tracking.Tests/Namotion.Interceptor.Tracking.Tests.csproj --filter "FullyQualifiedName~RelationshipAttachDetachTests.WhenInitialAttachEnumerationWrites" --no-restore
```

Observed failures:

```text
Assert.IsType() Failure: Value is null
Expected: typeof(System.InvalidOperationException)
Actual:   null
RelationshipAttachDetachTests.cs:line 149

Assert.IsType() Failure: Value is null
Expected: typeof(System.InvalidOperationException)
Actual:   null
RelationshipAttachDetachTests.cs:line 105

Failed: 2, Passed: 0, Total: 2
```

Both attach operations returned normally, which confirmed that the nested structural setters reached their terminals during staging. Code tracing showed the nested lifecycle call returned for the not-yet-attached parent, after which the outer stale snapshot committed.

### Root cause and implementation

Initial attach had an operation token but no guard spanning the complete whole-subject staging pass. A private thread-static map now tracks staging subjects per `LifecycleInterceptor` authority. `StageSubjectProperties` enters the guard before resolving handlers or reading any property and clears it in `finally`. Structural writes consult the guard before `next`, so the backing setter cannot run for the subject currently being staged. The exception identifies the property and explains that initial structural properties are being staged.

The guard does not add a global lock. It applies only to structural writes, the exact subject, the executing authority, and the current thread. Different subjects and ordinary non-structural writes remain outside it. The same-property reconciliation guard remains independent.

### GREEN

The focused command passed 2 of 2 tests. The tests also prove no membership or relationship group was published, backing storage was not mutated by the rejected structural write, and the guard cleared after the failed staging pass.

## Regression verification

Focused lifecycle and reconciler regression set:

```text
Passed: 29, Failed: 0
```

Focused Registry concurrency regression set:

```text
Passed: 14, Failed: 0
```

Full projects:

```text
dotnet test src/Namotion.Interceptor.Tracking.Tests/Namotion.Interceptor.Tracking.Tests.csproj --no-restore
Passed: 557, Failed: 0, Skipped: 0

dotnet test src/Namotion.Interceptor.Registry.Tests/Namotion.Interceptor.Registry.Tests.csproj --no-restore
Passed: 205, Failed: 0, Skipped: 0
```

Diff verification:

```text
git diff --check
exit 0, no output
```

The full project runs included their public API verification tests. The new fields and revision state are private/internal, and no public API snapshot changed. HomeBlaze tests, benchmarks, connector tests, docs, and unrelated optimization were not run or changed, as required.

## Files changed

- `src/Namotion.Interceptor.Tracking/Lifecycle/LifecycleInterceptor.cs`
- `src/Namotion.Interceptor.Tracking/Lifecycle/SubjectPropertyRelationshipReconciler.cs`
- `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/ConcurrentWriteLifecycleTests.cs`
- `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/RelationshipAttachDetachTests.cs`
- `src/Namotion.Interceptor.Registry.Tests/ConcurrentStructuralWriteLeakTests.cs`
- This report.

## Commit and concerns

- Required commit message: `fix(tracking): close structural reconciliation races`
- Result commit hash: reported in the parent handoff because a commit cannot contain its own final hash.
- Architecture conflicts: none found.
- Remaining concerns: none within the requested scope. The approved overlapping-authority removal limitation remains unchanged.

## Lifecycle fix review round 1: durable getterless ordering

### Reviewer finding and hypothesis

The first getterless ordering fix retained the committed revision in `ProcessedPropertyState`. That state belongs to one lifecycle authority and is removed on detach. Reattaching a setter-only subject stages an empty generation with revision zero because there is no getter. An older invocation parked after its terminal commit can therefore resume after detach and reattach, see no newer processed revision, and publish its stale captured value even though a later terminal commit still owns the backing store.

The durable per-property terminal watermark in `PropertyWriteState` survives lifecycle cleanup. Lifecycle reconciliation models the subject's own backing store, so both local and source-originated commits have already reached its destination and must participate in ordering.

### Deterministic RED

The existing event-gated getterless race was extended as follows:

1. T1 commits local generation A and parks downstream before lifecycle reconciliation.
2. T2 commits generation B from a source and fully reconciles.
3. The test proves the non-source watermark equals T1's revision while the all-terminal watermark is newer.
4. The subject detaches and reattaches, clearing lifecycle membership and rebuilding getterless processed state without a value.
5. T1 is released.

Command:

```text
dotnet test src/Namotion.Interceptor.Tracking.Tests/Namotion.Interceptor.Tracking.Tests.csproj --filter "FullyQualifiedName~ConcurrentWriteLifecycleTests.WhenAnOlderSetterOnlyWriteResumesAfterLifecycleReattach_ThenTheLatestTerminalRevisionRemainsAuthoritative" --no-restore
```

Observed failure:

```text
Assert.Equal() Failure: Values differ
Expected: 0
Actual:   1
ConcurrentWriteLifecycleTests.cs:line 332
Failed: 1, Passed: 0, Total: 1
```

The stale A generation was attached after lifecycle cleanup even though B remained the latest terminal commit.

### Root cause and implementation

`ProcessedPropertyState.Revision` was not a terminal watermark. It was cleared with lifecycle canonical state, so it could not order an invocation across detach and reattach.

Getterless reconciliation now calls:

```text
PropertyReference.TryGetWriteState(
    includeSourceCommitsInRevision: true,
    out latestTerminalRevision,
    out _)
```

It skips only when `context.Revision < latestTerminalRevision`. Strict inequality is intentional. An invocation whose revision equals the terminal watermark remains eligible to retry reconciliation after a pre-commit enumeration or handler failure. The three-authority same-property unwind also relies on each authority being able to reconcile the same committed revision.

`ProcessedPropertyState.Revision`, the reconciler revision argument, and all associated state plumbing were removed, restoring the compact processed state. Getter-backed properties do not consult the watermark and retain their existing backing-value re-read under the lifecycle lock.

### Source commit semantics

The RED makes T2 source-originated. Before cleanup it asserts:

- `TryGetWriteState(false, ...)` returns T1's older local revision.
- `TryGetWriteState(true, ...)` returns T2's newer terminal revision.

This pins `includeSourceCommitsInRevision: true` as required. Using the non-source watermark would compare T1 against its own equal revision and incorrectly allow the stale reconciliation.

### GREEN and regression results

The exact focused regression passed 1 of 1 tests.

Focused Tracking attach and reconciler matrix:

```text
Passed: 25, Failed: 0, Skipped: 0
```

This includes both initial-attach staging regressions and the focused reconciler suite.

Focused Registry lifecycle matrix:

```text
Passed: 5, Failed: 0, Skipped: 0
```

This includes the three-authority same-property regression, which proves equal-revision upstream reconciliation remains eligible, and all four getter-backed same-property concurrency configurations.

Full projects:

```text
dotnet test src/Namotion.Interceptor.Tracking.Tests/Namotion.Interceptor.Tracking.Tests.csproj --no-restore
Passed: 557, Failed: 0, Skipped: 0

dotnet test src/Namotion.Interceptor.Registry.Tests/Namotion.Interceptor.Registry.Tests.csproj --no-restore
Passed: 205, Failed: 0, Skipped: 0
```

Round 1 diff verification:

```text
git diff --check
exit 0, no output
```

- Required round 1 commit message: `fix(tracking): retain getterless write ordering`
- Result commit hash: reported in the parent handoff because a commit cannot contain its own final hash.
- Architecture conflicts: none found.
- Remaining concerns: none within the requested scope.
