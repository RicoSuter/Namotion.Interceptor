# Dictionary Relationship Reconciliation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace PR #458's mutable index-refresh implementation with one linear, immutable, occurrence-preserving relationship reconciliation model that keeps lifecycle, registry, and parent metadata quiescent-consistent and thread-safe.

**Architecture:** Each built-in `LifecycleInterceptor` owns processed membership state and, only when a relationship consumer is present, one immutable `SubjectPropertyRelationship` per live occurrence. It stages a complete container enumeration under its lifecycle lock, applies membership changes, publishes one generation, and sends the complete ordered sequence to registry and parent-tracking consumers. Consumers replace whole property groups behind their own gates and derive the existing immutable public snapshots.

**Tech Stack:** C# 13, .NET 9, xUnit, Verify, BenchmarkDotNet, PowerShell benchmark harness, GitHub CLI.

## Global Constraints

- Treat `origin/master` at `868a4d10` as the normative baseline. Do not depend on PR #419, PR #440, or the parallel context-cardinality design.
- Preserve current master's zero, one, or several `ILifecycleInterceptor` resolution and dispatch semantics.
- Correctness comes before pooling or inline-storage optimization. Use one linear reconciliation algorithm for every container size.
- Use subject reference identity in every reconciliation-reachable dictionary, set, and immutable snapshot. Never invoke subject value equality or dictionary-key equality or hashing.
- Enumerate and stage a structural property completely before graph or relationship mutation.
- Preserve exact source enumeration order and one relationship per subject-valued occurrence. Keep lifecycle membership unique per `(parent property, child subject)` within each lifecycle interceptor.
- Keep writers serialized by the existing lifecycle lock. Publish only immutable relationship objects and immutable public arrays.
- Tests must use `When<Condition>_Then<ExpectedBehavior>`, explicit Arrange/Act/Assert comments, and deterministic events or `AsyncTestHelpers.WaitUntilAsync`. Do not use sleeps or timing-based assertions.
- Do not add connector changes or run connector integration tests unless a compatibility failure requires such a change and the user approves the expanded scope.
- Do not retain the adaptive scan/rebuild algorithm, thresholds, container-kind cache, `SubjectChildReference`, or `RefreshChildIndices` API.
- Keep commits focused and free of AI attribution. Do not use em dashes in documentation or PR text.

---

## Task 1: Establish the relationship contract and ordered dispatcher

**Files:**
- Create: `src/Namotion.Interceptor.Tracking/Lifecycle/SubjectPropertyRelationship.cs`
- Create: `src/Namotion.Interceptor.Tracking/Lifecycle/IPropertyRelationshipHandler.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/SubjectLifecycleChange.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/LifecycleInterceptorExtensions.cs`
- Modify: `src/Namotion.Interceptor.Tracking.Tests/Models/SelfHandlingContainer.cs`
- Create: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/PropertyRelationshipHandlerTests.cs`
- Modify: `src/Namotion.Interceptor.Tracking.Tests/VerifyChecksTests.PublicApi.verified.txt`

- [ ] Write compile-failing tests that define the new public contract.

  Cover a context with two `IPropertyRelationshipHandler` services plus a parent subject implementing the same interface. Call the internal dispatcher directly with a synthetic ordered sequence and then an empty sequence. Assert context resolver order followed by subject-handler order. Add a handler that retains an individual relationship and prove that its `Parent`, `Child`, and `Index` stay frozen after a later synthetic generation.

- [ ] Run the focused tests and confirm they fail because the new API does not exist.

  Run: `dotnet test src/Namotion.Interceptor.Tracking.Tests/Namotion.Interceptor.Tracking.Tests.csproj --filter "FullyQualifiedName~PropertyRelationshipHandlerTests"`

- [ ] Add the immutable relationship type and focused handler interface.

  Implement this exact public shape:

  ```csharp
  public sealed class SubjectPropertyRelationship
  {
      internal SubjectPropertyRelationship(
          PropertyReference parent,
          IInterceptorSubject child,
          object? index);

      public PropertyReference Parent { get; }
      public IInterceptorSubject Child { get; }
      public object? Index { get; }
  }

  public interface IPropertyRelationshipHandler
  {
      void ReconcileChildRelationships(
          PropertyReference property,
          ReadOnlySpan<SubjectPropertyRelationship> relationships);
  }
  ```

  Keep reference identity equality by making `SubjectPropertyRelationship` a normal sealed class, not a record.

- [ ] Add `SubjectLifecycleChange.Relationship` as an optional read-only property and extend the internal constructor without weakening the existing `Index` contract.

- [ ] Add a dispatcher in `LifecycleInterceptorExtensions` that invokes all captured context handlers in resolver order, then the subject handler, continues after failures, and rethrows the first exception with its original stack.

- [ ] Update `SelfHandlingContainer` to implement the relationship handler and synchronously copy relationship references for assertions. Do not retain a span.

- [ ] Run the focused tests and public API verification.

  Run: `dotnet test src/Namotion.Interceptor.Tracking.Tests/Namotion.Interceptor.Tracking.Tests.csproj --filter "FullyQualifiedName~PropertyRelationshipHandlerTests|FullyQualifiedName~VerifyChecksTests.PublicApi"`

- [ ] Commit the contract separately.

  ```text
  feat(tracking): add ordered property relationship contract
  ```

## Task 2: Build one staged, immutable lifecycle reconciler

**Files:**
- Create: `src/Namotion.Interceptor.Tracking/Lifecycle/IStructuralPropertyRefreshHandler.cs`
- Create: `src/Namotion.Interceptor.Tracking/Lifecycle/SubjectPropertyRelationshipReconciler.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/LifecycleInterceptor.cs`
- Modify: `src/Namotion.Interceptor.Tracking/PropertyValueEqualityCheckHandler.cs`
- Modify: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/PropertyLifecycleRefreshTests.cs`
- Create: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/SubjectPropertyRelationshipReconcilerTests.cs`
- Modify: `src/Namotion.Interceptor.Tracking.Tests/Models/Garage.cs`

- [ ] Replace the current refresh-only tests with red tests for canonical processing.

  Cover direct subjects, arrays, mutable lists, `ICollection`, `IEnumerable`, `IDictionary`, declared read-only dictionaries, null/empty/mixed containers, duplicate subject references, reordering, re-keying, replacement, and removal. Assert exact occurrence order and that duplicate changes do not duplicate lifecycle attach or reference-count transitions.

- [ ] Add hostile key and hostile subject fixtures to the tracking test project. Their `Equals` and `GetHashCode` methods must throw, so a passing test proves the reconciler uses only opaque keys and subject reference identity.

- [ ] Run the focused tests and confirm the current PR implementation fails duplicate, immutable-generation, or hostile-equality cases.

  Run: `dotnet test src/Namotion.Interceptor.Tracking.Tests/Namotion.Interceptor.Tracking.Tests.csproj --filter "FullyQualifiedName~SubjectPropertyRelationshipReconcilerTests|FullyQualifiedName~PropertyLifecycleRefreshTests"`

- [ ] Implement internal staged descriptors and processed-property state in `SubjectPropertyRelationshipReconciler.cs`.

  The reconciler must:

  1. Enumerate the backing value once into source-ordered descriptors.
  2. Compute distinct membership using `ReferenceEqualityComparer.Instance`.
  3. Match the nth new occurrence of a child to its nth old occurrence in linear time.
  4. Reuse a relationship only for null/null direct indices, equal integer positions, or reference-equal dictionary key objects.
  5. Allocate a new immutable relationship for every moved, re-keyed, or unmatched occurrence.
  6. Return staged membership removals in reverse old order, additions in source order, and one complete new relationship sequence.

  Keep descriptor and match state internal. Do not expose container-kind thresholds or alternate algorithms.

- [ ] Replace `_lastProcessedValues` in `LifecycleInterceptor` with canonical processed-property state under the existing `_attachedSubjects` lock.

  Ordinary writes must capture relationship handlers before `next(ref context)`, call the backing setter, re-read the actual property value under the lifecycle lock, stage fully, validate the parent is still attached, apply membership transitions, publish the processed state, and dispatch the complete relationship sequence. A direct subject may retain a reference-equal no-op. An enumerable must reconcile even when the container reference is unchanged.

- [ ] Add a same-property reconciliation guard that throws `InvalidOperationException` before nested processing can corrupt the baseline. Preserve writes to different properties from lifecycle callbacks.

- [ ] Add the internal `IStructuralPropertyRefreshHandler.RefreshStructuralProperty(PropertyReference)` capability and implement it on built-in `LifecycleInterceptor`.

- [ ] Update `PropertyValueEqualityCheckHandler` to recognize a same-reference, non-string enumerable whose declared property can contain subjects. Resolve one immutable capability snapshot, invoke every built-in capability exactly once in resolver order, and return without invoking the remaining write chain. Preserve `IsWritten == false`, notifications, transactions, and connector suppression.

- [ ] Ensure lifecycle-only configurations store compact membership data, including first and last occurrence metadata, without allocating `SubjectPropertyRelationship` objects. Materialize relationships only when at least one relationship handler is captured.

- [ ] Run the focused tests and existing lifecycle concurrency tests.

  Run: `dotnet test src/Namotion.Interceptor.Tracking.Tests/Namotion.Interceptor.Tracking.Tests.csproj --filter "FullyQualifiedName~SubjectPropertyRelationshipReconcilerTests|FullyQualifiedName~PropertyLifecycleRefreshTests|FullyQualifiedName~ConcurrentWriteLifecycleTests"`

- [ ] Commit the canonical ordinary-write path.

  ```text
  refactor(tracking): reconcile immutable child relationships
  ```

## Task 3: Make initial attach, detach, and re-entrant cancellation canonical

**Files:**
- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/LifecycleInterceptor.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/SubjectPropertyRelationshipReconciler.cs`
- Modify: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/RecursiveAttachTests.cs`
- Modify: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/LifecycleEventsTests.cs`
- Create: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/RelationshipAttachDetachTests.cs`
- Create: `src/Namotion.Interceptor.Tracking.Tests/Models/ThrowingStructuralContainer.cs`

- [ ] Add red tests for initial attach staging and canonical detach.

  Test that all parent properties finish enumeration before the first lifecycle callback, a second-property enumeration failure publishes no child membership or relationship group, a retry can succeed, detach uses the last successful processed state instead of a subsequently mutated backing container, and context detach supplies the first old occurrence metadata.

- [ ] Add a red test where a lifecycle callback re-entrantly detaches the parent during a new child addition. Assert every applied addition is undone, the captured relationship consumers receive an empty sequence, no processed state is published, and no child or edge leaks.

- [ ] Add a callback-failure test proving an attach-in-progress marker is always cleared and a later attach is not blocked. Assert only the best-effort baseline promised by issue #384, not transactional rollback of arbitrary external handlers.

- [ ] Run the focused tests and confirm failure against the ordinary-only implementation.

  Run: `dotnet test src/Namotion.Interceptor.Tracking.Tests/Namotion.Interceptor.Tracking.Tests.csproj --filter "FullyQualifiedName~RelationshipAttachDetachTests|FullyQualifiedName~RecursiveAttachTests|FullyQualifiedName~LifecycleEventsTests"`

- [ ] Add a private attach-in-progress token under the lifecycle lock.

  Stage every structural property before callbacks. Commit unique child memberships in property and source order, ledger successful additions, provisionally publish processed states, perform the existing root/context attach, then dispatch full relationship groups in property order. The token must be invisible as graph membership and cleared in `finally` on every exit.

- [ ] Implement explicit abort and undo for re-entrant parent detach.

  Check attached or matching-token state before commit and after every callback-bearing transition. On cancellation, detach applied additions in reverse, clear captured relationship consumers with empty sequences, and remove provisional processed state. Never republish the staged non-empty generation.

- [ ] Change normal detach to read canonical processed-property state. Preserve `SubjectDetaching` visibility of the old graph, then send an empty relationship sequence before lifecycle removal handlers remove built-in consumers.

- [ ] Run all lifecycle tests.

  Run: `dotnet test src/Namotion.Interceptor.Tracking.Tests/Namotion.Interceptor.Tracking.Tests.csproj --filter "FullyQualifiedName~Lifecycle"`

- [ ] Commit attach and detach hardening.

  ```text
  fix(tracking): make relationship attach and detach abort-safe
  ```

## Task 4: Move parent tracking to ordered immutable relationship groups

**Files:**
- Modify: `src/Namotion.Interceptor.Tracking/Parent/ParentTrackingHandler.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Parent/ParentsHandlerExtensions.cs`
- Modify: `src/Namotion.Interceptor.Tracking.Tests/ParentTrackingHandlerTests.cs`
- Modify: `src/Namotion.Interceptor.Tracking.Tests/Parent/ParentAccessDuringLifecycleTests.cs`
- Modify: `src/Namotion.Interceptor.Tracking.Tests/VerifyChecksTests.PublicApi.verified.txt`

- [ ] Add red parent-tracking tests for two keys pointing to the same child, exact source order, removal of one duplicate, re-keying, reordering, frozen old snapshots, first-current-parent singular lookup, and two distinct subjects that override value equality.

- [ ] Add a reader/writer generation test. Force one reader into the first-cache-build window while a relationship moves and assert every returned array is either the complete old generation or complete new generation, never old order with new indices.

- [ ] Run the focused tests and confirm the current mutable-index storage fails at least one generation or duplicate assertion.

  Run: `dotnet test src/Namotion.Interceptor.Tracking.Tests/Namotion.Interceptor.Tracking.Tests.csproj --filter "FullyQualifiedName~ParentTrackingHandlerTests|FullyQualifiedName~ParentAccessDuringLifecycleTests"`

- [ ] Make `ParentTrackingHandler` implement `IPropertyRelationshipHandler` and add one private reconciliation gate around the complete callback.

- [ ] Replace `HashSet<SubjectParent>` storage with property-grouped ordered `SubjectPropertyRelationship` references in `ParentsHandlerExtensions`. Identify groups with `PropertyReference.Comparer`, preserve group attachment order, and preserve inline-first empty/single storage when it stays simple.

- [ ] Build `ImmutableArray<SubjectParent>` projections under the view lock, publish cache references with `Volatile.Write`, read with `Volatile.Read`, and invalidate the cache in the same critical section as a full group replacement. Previously returned arrays must never change.

- [ ] Keep lifecycle add/remove handlers only for membership and old-graph visibility. Let the full relationship callback replace the exact occurrence group.

- [ ] Run all parent-tracking tests and API verification.

  Run: `dotnet test src/Namotion.Interceptor.Tracking.Tests/Namotion.Interceptor.Tracking.Tests.csproj --filter "FullyQualifiedName~Parent|FullyQualifiedName~VerifyChecksTests.PublicApi"`

- [ ] Commit the consumer migration.

  ```text
  refactor(tracking): store ordered parent relationships
  ```

## Task 5: Move registry children and parents to full-group replacement

**Files:**
- Modify: `src/Namotion.Interceptor.Registry/SubjectRegistry.cs`
- Modify: `src/Namotion.Interceptor.Registry/Abstractions/RegisteredSubjectProperty.cs`
- Modify: `src/Namotion.Interceptor.Registry/Abstractions/RegisteredSubject.cs`
- Replace: `src/Namotion.Interceptor.Registry.Tests/ChildIndexPlacementTests.cs` with `src/Namotion.Interceptor.Registry.Tests/RelationshipReconciliationTests.cs`
- Modify: `src/Namotion.Interceptor.Registry.Tests/SubjectRegistryTests.cs`
- Modify: `src/Namotion.Interceptor.Registry.Tests/Paths/PathExtensionsTests.cs`
- Modify: `src/Namotion.Interceptor.Registry.Tests/VerifyChecksTests.PublicApi.verified.txt`
- Reuse: `src/Namotion.Interceptor.Registry.Tests/Models/EqualByValueItem.cs`
- Reuse: `src/Namotion.Interceptor.Registry.Tests/Models/PersonDirectory.cs`
- Reuse: `src/Namotion.Interceptor.Registry.Tests/Models/ReadOnlyPersonDictionary.cs`
- Reuse: `src/Namotion.Interceptor.Registry.Tests/Models/ReentrantKey.cs`

- [ ] Replace adaptive-threshold tests with red semantic tests.

  The matrix must cover direct, array, mutable list, collection, dictionary, read-only dictionary, enumerable fallback, mixed content, duplicates, insertion, removal, reorder, re-key, replacement, same-instance reassignment, context detach/reattach, cycles, self-reference, several parent properties, and singular path selection. In each applicable case assert registry children, registry parents, tracking parents when enabled, reference count, known-subject membership, and path output together.

- [ ] Add a key fixture whose equality and hash methods throw, an enumerator that throws after yielding, and a successful retry test. Assert no partial child or parent metadata is visible after the failed enumeration.

- [ ] Add tests that previously captured `Children` and `Parents` arrays remain frozen after a later reorder or re-key.

- [ ] Run the focused tests and confirm the adaptive implementation fails the new occurrence or immutability semantics.

  Run: `dotnet test src/Namotion.Interceptor.Registry.Tests/Namotion.Interceptor.Registry.Tests.csproj --filter "FullyQualifiedName~RelationshipReconciliationTests|FullyQualifiedName~SubjectRegistryTests|FullyQualifiedName~PathExtensionsTests"`

- [ ] Make `SubjectRegistry` implement `IPropertyRelationshipHandler` with a private operation-level relationship gate.

  Hold the gate for the whole full-group callback. Briefly use the known-subject lock to resolve registered subjects and the registered parent property, release it, then update outgoing and incoming groups without nesting individual view locks. Keep the lock order lifecycle lock, registry gate, known-subject lock released, then one relationship-view lock at a time.

- [ ] Store ordered outgoing relationship references in `RegisteredSubjectProperty`. Restore the simple supported-shape type checks from master and remove the PR-only container-kind cache, thresholds, adaptive scan/rebuild code, and moved/rebuild pools. Project `Children` to the existing `ImmutableArray<SubjectPropertyChild>` behind its existing read lock.

- [ ] Store incoming relationships in `RegisteredSubject` grouped by registered parent property. Full replacement must preserve unrelated property-group attachment order and project `Parents` to the existing `ImmutableArray<SubjectPropertyParent>` with its lock-free cached read path.

- [ ] Use `ReferenceEqualityComparer.Instance` for `_knownSubjects` and every registry subject-keyed temporary or immutable lookup. Verify that distinct value-equal subjects register and reconcile independently.

- [ ] Preserve lifecycle event order. `SubjectAttached` must still see the provisional first occurrence, `SubjectDetaching` must still see the complete old group, and full reconciliation must replace the group afterward or clear it during detach.

- [ ] Run the complete Registry test project and API verification.

  Run: `dotnet test src/Namotion.Interceptor.Registry.Tests/Namotion.Interceptor.Registry.Tests.csproj`

- [ ] Commit the registry migration.

  ```text
  refactor(registry): replace ordered relationship groups atomically
  ```

## Task 6: Pin failure, re-entrancy, and multi-lifecycle boundaries

**Files:**
- Modify: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/PropertyRelationshipHandlerTests.cs`
- Modify: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/RelationshipAttachDetachTests.cs`
- Modify: `src/Namotion.Interceptor.Registry.Tests/ConcurrentStructuralWriteLeakTests.cs`
- Create: `src/Namotion.Interceptor.Registry.Tests/RelationshipFailureTests.cs`
- Create: `src/Namotion.Interceptor.Registry.Tests/MultipleLifecycleRelationshipTests.cs`

- [ ] Add a throwing relationship consumer before the built-in consumers. Assert later consumers still receive the generation and the first exception is rethrown with its original stack.

- [ ] Add a same-property re-entry test and assert the clear `InvalidOperationException` leaves the last canonical generation intact. Also prove a lifecycle callback can still write a different property.

- [ ] Add zero-lifecycle and two-lifecycle configuration tests. Assert same-instance refresh invokes each built-in capability exactly once, unrelated custom `ILifecycleInterceptor` implementations receive no refresh callback, and shared built-in consumers replace equivalent sequences without duplicate edges.

- [ ] Add a test that removes one of two independently contributing lifecycle authorities and pins master's existing last-callback ownership limitation. Name and comment it explicitly as a preserved boundary, not a repaired guarantee.

- [ ] Run the new tests and confirm any missing dispatch or authority behavior fails before implementation adjustments.

  Run: `dotnet test src/Namotion.Interceptor.Registry.Tests/Namotion.Interceptor.Registry.Tests.csproj --filter "FullyQualifiedName~RelationshipFailureTests|FullyQualifiedName~MultipleLifecycleRelationshipTests|FullyQualifiedName~ConcurrentStructuralWriteLeakTests"`

- [ ] Make only the smallest implementation adjustments required by these tests. Do not add producer identity, unique lifecycle authority, graph movement, or context-cardinality APIs from PR #419.

- [ ] Re-run the focused Tracking and Registry tests.

  Run: `dotnet test src/Namotion.Interceptor.Tracking.Tests/Namotion.Interceptor.Tracking.Tests.csproj --filter "FullyQualifiedName~PropertyRelationshipHandlerTests|FullyQualifiedName~RelationshipAttachDetachTests"`

  Run: `dotnet test src/Namotion.Interceptor.Registry.Tests/Namotion.Interceptor.Registry.Tests.csproj --filter "FullyQualifiedName~RelationshipFailureTests|FullyQualifiedName~MultipleLifecycleRelationshipTests|FullyQualifiedName~ConcurrentStructuralWriteLeakTests"`

- [ ] Commit the hardening tests and narrow fixes.

  ```text
  test(tracking): pin relationship failure and authority semantics
  ```

## Task 7: Prove quiescent consistency under concurrency

**Files:**
- Create: `src/Namotion.Interceptor.Registry.Tests/RelationshipConcurrencyTests.cs`
- Modify: `src/Namotion.Interceptor.Registry.Tests/ConcurrentStructuralWriteLeakTests.cs`
- Modify: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/ConcurrentWriteLifecycleTests.cs`

- [ ] Add deterministic event-controlled tests for concurrent writes to the same property, writes to different properties, write racing with parent detach, context detach racing with descendant write, duplicate add/remove racing with replacement, and readers repeatedly snapshotting while writers reorder and re-key.

- [ ] During writes, assert each individual immutable array is safe, fully initialized, and internally one generation. After all workers finish, assert every quiescent invariant across children, parents, tracked parents, known subjects, and reference counts.

- [ ] Run each concurrency test repeatedly without hardcoded waits.

  Run: `dotnet test src/Namotion.Interceptor.Registry.Tests/Namotion.Interceptor.Registry.Tests.csproj --filter "FullyQualifiedName~RelationshipConcurrencyTests|FullyQualifiedName~ConcurrentStructuralWriteLeakTests" -- RunConfiguration.MaxCpuCount=1`

  Repeat the command at least 20 times from a shell loop only after one clean pass. Stop on the first failure and diagnose it with `superpowers:systematic-debugging` before changing code.

- [ ] Review every shared field touched by the new code. Confirm the owning lock or volatile publication rule in a short code comment only where the ownership is not obvious from structure.

- [ ] Commit the concurrency proof separately.

  ```text
  test(registry): prove quiescent relationship consistency
  ```

## Task 8: Remove the obsolete API and reduce the cumulative PR diff

**Files:**
- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/IPropertyLifecycleHandler.cs`
- Delete: `src/Namotion.Interceptor.Tracking/Lifecycle/SubjectChildReference.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Services/Lifecycle/PropertyAttributeInitializer.cs`
- Modify: `src/Namotion.Interceptor.Tracking.Tests/VerifyChecksTests.PublicApi.verified.txt`
- Modify: `src/Namotion.Interceptor.Registry.Tests/VerifyChecksTests.PublicApi.verified.txt`
- Modify: all compile errors found by the solution build

- [ ] Remove `IPropertyLifecycleHandler.RefreshCollectionProperty` relative to master and remove the PR-only `RefreshChildIndices` path. Leave only `AttachProperty` and `DetachProperty` on `IPropertyLifecycleHandler`.

- [ ] Delete `SubjectChildReference` and search the entire repository for stale references.

  Run: `rg -n "RefreshCollectionProperty|RefreshChildIndices|SubjectChildReference|ContainerKind|ScanUpdateThreshold|Rebuild" src docs`

  Expected: no production references to removed APIs or adaptive implementation terms. Documentation may mention removed API names only when explicitly describing the breaking change.

- [ ] Build the solution and fix consumers only where compilation or a focused behavioral test proves adaptation is required.

  Run: `dotnet build src/Namotion.Interceptor.slnx`

- [ ] Accept only the intended Tracking and Registry public API snapshot changes. Inspect `.received.txt` before replacing either verified snapshot, then run both public API tests again.

- [ ] Review the cumulative diff and remove unrelated parent-lookup benchmark rows, container-kind optimization, adaptive pools, threshold-only fixtures, and threshold-only documentation.

  Run: `git diff --stat origin/master...HEAD`

  Run: `git diff --check origin/master...HEAD`

- [ ] Commit the public API cleanup.

  ```text
  refactor(tracking): remove collection index refresh API
  ```

## Task 9: Update behavior and design documentation

**Files:**
- Modify: `docs/registry.md`
- Modify: `docs/benchmarking.md`
- Modify: `docs/superpowers/specs/2026-08-16-dictionary-relationship-reconciliation-design.md` only if implementation revealed an approved design correction
- Review: `src/HomeBlaze`
- Review: `src/Namotion.Interceptor.OpcUa`
- Review: registry serialization, path, and UI consumers found by repository search

- [ ] Document occurrence-preserving children and parents, exact enumeration order, singular path selection, membership-based reference counts, same-instance assignment as explicit refresh, invisible unassigned mutation, frozen snapshots, quiescent consistency, and the removed lifecycle refresh API.

- [ ] Search consumers for assumptions that `Children.Length` or `Parents.Length` is a distinct membership count or that one property/child pair has one path.

  Run: `rg -n "\.Children\.Length|\.Parents\.Length|GetParents\(\)|SubjectPropertyChild|SubjectPropertyParent" src/HomeBlaze src/Namotion.Interceptor.OpcUa src/Namotion.Interceptor.Registry src/Extensions`

- [ ] Add focused compatibility tests only if the audit identifies observable breakage. Do not change connector implementations merely to make the diff look comprehensive.

- [ ] Run documentation and whitespace checks.

  Run: `git diff --check origin/master...HEAD`

- [ ] Commit documentation separately.

  ```text
  docs(registry): document ordered occurrence relationships
  ```

## Task 10: Retain only targeted comparative benchmarks

**Files:**
- Modify: `src/Namotion.Interceptor.Benchmark/ChildIndexRefreshBenchmark.cs`
- Modify: `src/Namotion.Interceptor.Benchmark/ParentLookupBenchmark.cs`
- Review: `src/Namotion.Interceptor.Benchmark/RegistryBenchmark.cs`
- Review: `src/Namotion.Interceptor.Benchmark/ServiceOrderResolverBenchmark.cs`
- Modify: `docs/benchmarking.md`

- [ ] Read `docs/benchmarking.md` immediately before benchmarking and use `superpowers:using-git-worktrees` for an external worktree outside the repository.

- [ ] Keep benchmark definitions focused on replacement, reorder, re-key, duplicate occurrence, same-instance refresh, registry construction, registry parent reads, tracked parent reads, and one unchanged service-resolver noise reference. Remove rows unrelated to the redesigned storage or read paths.

- [ ] Include a lifecycle-only control that performs the same structural writes without registry or parent relationship consumers. Use its allocation results to confirm the compact processed state does not allocate one relationship object per occurrence.

- [ ] Build identical new benchmark definitions on a temporary base branch rooted at pinned `origin/master`. Do not compare a class that exists only on the implementation branch.

- [ ] Create the validated temporary base branch `benchmark/pr458-base-868a4d10`, temporary head branch `benchmark/pr458-head-final`, and external worktree `/Users/ricosuter/Projects/GitHub/Namotion.Interceptor-pr458-benchmark`. Abort if any name or path already exists unexpectedly. Never force-update the PR branch and never remove an unverified path.

- [ ] Run the agreed three-launch comparison from the external worktree.

  Run: `pwsh scripts/benchmark.ps1 -Filter "*ChildIndexRefreshBenchmark*","*RegistryBenchmark*","*ParentLookupBenchmark*","*ServiceOrderResolverBenchmark.LinearChain*" -LaunchCount 3 -BaseBranch benchmark/pr458-base-868a4d10`

  The temporary base branch name must be recorded in the benchmark log before running. Expect approximately 75 minutes. Keep the user updated at least once per hour.

- [ ] Inspect allocations, linear scaling shape, raw launch variance, exact base/head commit hashes, and the unchanged noise row. Treat small timing movement inside noise as inconclusive. Investigate unexplained allocation growth before proceeding.

- [ ] Record the exact benchmark command, commit hashes, and concise results in the eventual PR description. Clean up only the validated temporary worktree and temporary benchmark branches after results are preserved.

- [ ] Commit benchmark definition cleanup if it changed.

  ```text
  perf(registry): benchmark relationship reconciliation
  ```

## Task 11: Run full verification before review

**Files:**
- Review: entire cumulative diff against `origin/master`
- Modify: only files required by verification failures

- [ ] Invoke `superpowers:verification-before-completion` and run a clean solution build.

  Run: `dotnet build src/Namotion.Interceptor.slnx`

- [ ] Run every non-integration test.

  Run: `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`

- [ ] Run both public API tests explicitly and confirm there are no `.received.txt` files.

  Run: `dotnet test src/Namotion.Interceptor.Tracking.Tests/Namotion.Interceptor.Tracking.Tests.csproj --filter "FullyQualifiedName~VerifyChecksTests.PublicApi"`

  Run: `dotnet test src/Namotion.Interceptor.Registry.Tests/Namotion.Interceptor.Registry.Tests.csproj --filter "FullyQualifiedName~VerifyChecksTests.PublicApi"`

  Run: `rg --files src | rg '\.received\.txt$'`

  Expected: no output from the last command.

- [ ] Inspect cumulative scope and repository hygiene.

  Run: `git diff --check origin/master...HEAD`

  Run: `git status --short`

  Run: `git diff --stat origin/master...HEAD`

  Run: `rg -n "TODO|TBD|RefreshChildIndices|SubjectChildReference|ScanUpdateThreshold|ContainerKind" src docs`

- [ ] If any verification fails, use `superpowers:systematic-debugging`, add or tighten a regression test, fix only the demonstrated cause, and rerun the focused plus full verification commands.

## Task 12: Obtain an unbiased full-diff review and address findings

**Files:**
- Review: `docs/superpowers/specs/2026-08-16-dictionary-relationship-reconciliation-design.md`
- Review: complete `origin/master...HEAD` diff
- Modify: files implicated by confirmed review findings

- [ ] Invoke `superpowers:requesting-code-review` and spawn a fresh review subagent with no inherited conversation (`fork_turns: "none"`). The user explicitly requested this independent review.

- [ ] Give the reviewer only the repository path, pinned master SHA, final head SHA, design-spec path, PR #458 URL, and instructions to inspect the full diff and PR discussion. Ask for prioritized Critical, Important, and Minor findings across correctness, thread safety, quiescent consistency, API design, behavior changes, simplicity, performance, tests, documentation, and scope.

- [ ] Require a direct merge-readiness verdict and explicit identification of any design contradiction or unnecessary code that can be removed. Do not prime the reviewer with the implementation author's conclusions.

- [ ] Verify every finding against code and tests using `superpowers:receiving-code-review`. Fix all confirmed Critical and Important findings with a failing regression test first. Address Minor findings when they reduce risk or code size without broadening scope.

- [ ] Re-run focused verification after each fix, then repeat Task 11's complete build, non-integration tests, public API tests, diff check, and status check.

- [ ] If fixes materially alter the design contract, update the approved spec narrowly and request one focused follow-up review of that changed area from the same reviewer.

## Task 13: Push and finalize PR #458 metadata

**Files:**
- Create temporarily: `/private/tmp/pr-458-body.md`
- Modify remotely: PR #458 title and body

- [ ] Confirm the branch is clean, final commits are reviewable, the benchmark evidence is preserved, and the unbiased review has no unresolved Critical or Important findings.

- [ ] Push `fix/dictionary-key-refresh` to `origin` without force.

  Run: `git push origin fix/dictionary-key-refresh`

- [ ] Read the final PR state and discussion again.

  Run: `gh pr view 458 --repo RicoSuter/Namotion.Interceptor --json title,body,comments,reviews,files,commits,statusCheckRollup`

- [ ] Update the title to `Reconcile ordered container relationships and dictionary keys` unless the final implementation makes a narrower title more accurate.

- [ ] Write the final body with these exact sections: Summary, Semantics, API changes, Correctness and concurrency, Verification, Benchmarks, Compatibility and intentional behavior changes. Report only commands and results actually observed. State that connector integration tests were not run because connector implementations are outside the diff. Do not include stale file counts, test counts, benchmark claims, AI attribution, or em dashes.

- [ ] Apply the metadata update and inspect it after publication.

  Run: `gh pr edit 458 --repo RicoSuter/Namotion.Interceptor --title "Reconcile ordered container relationships and dictionary keys" --body-file /private/tmp/pr-458-body.md`

  Run: `gh pr view 458 --repo RicoSuter/Namotion.Interceptor --json title,body,url,statusCheckRollup`

- [ ] If remote checks start, inspect their current state. Do not claim they passed unless the reported status is complete and successful.

- [ ] Report the final head SHA, pushed branch, PR URL, exact local verification, benchmark comparison, intentional public API and behavior changes, independent-review verdict, and any remote checks still pending.

## Final Acceptance Checklist

- [ ] One immutable relationship exists per live subject occurrence when consumers are enabled.
- [ ] Duplicate occurrences preserve order everywhere but contribute one membership reference per property and lifecycle interceptor.
- [ ] Registry children, registry parents, and tracked parents converge to the same generation after quiescence.
- [ ] Same-instance assignment refreshes structure without becoming a value write under equality tracking.
- [ ] Enumeration, subject equality, and key-equality failures cannot partially mutate metadata.
- [ ] Re-entrant detach cannot leak staged child memberships or republish a cancelled relationship group.
- [ ] Previously returned arrays and retained relationships stay frozen.
- [ ] Lifecycle-only configurations do not allocate per-occurrence relationship objects.
- [ ] Master multi-lifecycle semantics and their known ownership limitation are preserved and tested.
- [ ] No adaptive algorithm, threshold-only code, or obsolete refresh API remains.
- [ ] Build, non-integration tests, API verification, concurrency repeats, and targeted benchmarks have current evidence.
- [ ] A fresh independent reviewer reports no unresolved Critical or Important findings.
- [ ] The pushed PR title and description match the final implementation and evidence.

## Design Coverage Map

| Design requirement | Implementation and proof |
| --- | --- |
| Immutable occurrence relationships and ordered dispatch | Tasks 1 and 2 |
| Compact lifecycle-only membership state | Tasks 2 and 10 |
| Same-instance equality-suppressed refresh | Tasks 2 and 6 |
| Staged enumeration, canonical detach, and re-entrant abort | Tasks 2 and 3 |
| Ordered parent-tracking storage and coherent publication | Task 4 |
| Atomic registry outgoing/incoming full-group replacement | Task 5 |
| Failure continuation and master multi-lifecycle boundary | Task 6 |
| Quiescent consistency and race coverage | Task 7 |
| Public API break and removal of adaptive PR code | Task 8 |
| Behavior, compatibility, and concurrency documentation | Task 9 |
| Allocation and scaling acceptance | Task 10 |
| Full repository evidence | Task 11 |
| Independent merge-readiness review | Task 12 |
| Final push and accurate PR metadata | Task 13 |
