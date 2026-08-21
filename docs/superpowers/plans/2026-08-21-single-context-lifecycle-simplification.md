# Single-Context Lifecycle Simplification Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace subject-local and fallback contexts with one nullable exact context per subject while preserving arbitrary object graphs, lifecycle callback behavior, Registry projection, and the scalar interception hot path.

**Architecture:** Core owns a lazy per-subject executor, exact attachment state, flat context services, singleton-service enforcement, interceptor execution, and a small public lifecycle seam. Tracking's single built-in `LifecycleInterceptor` owns explicit roots, occurrence-aware structural edges, authoritative parents, reachability across DAGs and cycles, callback ordering, and one reentrant lock per lifecycle/context. Registry remains an optional projection over committed Tracking notifications. Structural metadata admission uses a lazy, once-materialized continuation protocol so callback admission can fail before enumerating user input.

**Tech Stack:** C# 13 preview, .NET Standard 2.0 Core, .NET 9 feature libraries, xUnit, Verify/PublicApiGenerator, BenchmarkDotNet, PowerShell benchmark tooling.

**Design reference:** `docs/superpowers/specs/2026-08-21-single-context-lifecycle-simplification-design.md`

## Non-negotiable implementation constraints

- Preserve correctness first, then allocations/CPU, then style.
- Do not add or expand `InternalsVisibleTo`. Exercise lifecycle graph behavior through public subject, context, and Tracking APIs. Remove existing friend access only when this work naturally makes an entry unused.
- Do not add obsolete aliases for removed APIs. Temporary source-compatible members are allowed only inside intermediate tasks and must be deleted before Task 11 is committed.
- Do not add a guard for repeated interceptor continuations. Zero or one `next` call is a documented high-performance contract.
- Do not add a process-wide topology lock. The built-in implementation has one reentrant lock per lifecycle/context plus each executor's private monitor.
- Do not add rollback for exceptions thrown by lifecycle callbacks, backing writers, or metadata publishers. Those callbacks are synchronous and exception-free by contract; violations propagate.
- Do not use `ReferenceCount` as an ownership predicate. Ownership is `subject.TryGetContext() != null`; Registry membership is queried through Registry.
- Keep scalar setters on the existing direct/inlined route. Only properties whose declared type can contain subjects use structural attachment-revision capture and validation.
- Treat every collection or dictionary occurrence as an edge. Repeated references such as `[a, a, b]` count as two edges for `a`.
- Preserve existing callback and handler order, except for the approved change that authoritative `GetParents()` state is visible before the first handler.

## Task 1: Add a benchmark scaffold before changing runtime behavior

**Files:**

- Create: `src/Namotion.Interceptor.Benchmark/LifecycleOwnershipBenchmark.cs`
- Modify: `src/Namotion.Interceptor.Benchmark/Program.cs` only if the local debug entry point must recognize the new benchmark
- Test: benchmark project build

- [ ] Add generated benchmark subjects with a scalar property, a subject reference, a subject list, and a small cyclic shape.
- [ ] Add stable benchmark methods that compile against both the current and final public APIs: unattached scalar set, lifecycle-attached scalar set, structural reference replacement, duplicate-list replacement, and cyclic structural replacement. Set up attachment through context-taking constructors, which remain supported in both versions.
- [ ] Add `[MemoryDiagnoser]`; amortize sub-nanosecond scalar operations with `OperationsPerInvoke` where necessary.
- [ ] Do not benchmark removed fallback APIs or new attach/detach method names in this scaffold, because the file must remain source-identical at the comparison base.
- [ ] Run `dotnet build src/Namotion.Interceptor.Benchmark/Namotion.Interceptor.Benchmark.csproj -c Release`.
- [ ] Commit with `test: scaffold lifecycle ownership benchmarks` and record that commit hash as `LIFECYCLE_BENCHMARK_BASE` in the implementation notes or PR checklist. Final comparisons use this exact hash, not a moving branch.

## Task 2: Enforce generic singleton context-service contracts

**Files:**

- Create: `src/Namotion.Interceptor/ISingletonContextService.cs`
- Modify: `src/Namotion.Interceptor/InterceptorSubjectContext.cs`
- Modify: `src/Namotion.Interceptor/IInterceptorSubjectContext.cs` to document singleton validation on `AddService` and `TryAddService`
- Create: `src/Namotion.Interceptor.Tests/Context/SingletonContextServiceTests.cs`
- Modify: `src/Namotion.Interceptor.Tests/VerifyChecksTests.PublicApi.verified.txt`

- [ ] Write public-API tests for `ISingletonContextService<TContract>` and behavior tests named `WhenSecondSingletonContractIsAdded_ThenThrows`, `WhenSameSingletonInstanceIsAddedTwice_ThenThrows`, `WhenServiceImplementsTwoSingletonContracts_ThenBothAreReserved`, `WhenTryAddPredicateMatches_ThenFactoryIsNotCalled`, `WhenTryAddFactoryConflicts_ThenThrows`, `WhenTryAddFactoryReentrantlyAddsContract_ThenRevalidationThrows`, and `WhenSubjectsAreOwned_ThenSingletonCanStillBeAdded`.
- [ ] Run `dotnet test src/Namotion.Interceptor.Tests/Namotion.Interceptor.Tests.csproj --filter "FullyQualifiedName~SingletonContextServiceTests"` and confirm the new tests fail for the intended missing contract/validation behavior.
- [ ] Add the empty public marker:

```csharp
public interface ISingletonContextService<TContract>
{
}
```

- [ ] Cache the closed singleton-contract interfaces per implementation `Type`. Validate only the context's directly registered immutable service snapshot. Do not inspect fallback contexts and do not put reflection on interceptor lookup or execution paths.
- [ ] Under the existing context mutation lock, validate `AddService` before publication. In `TryAddService`, preserve the predicate-before-factory behavior, invoke the factory at most once, re-read the state after a reentrant factory, validate the produced service against that latest state, then publish.
- [ ] Ensure a duplicate throws even when the same instance or a different registration generic type is used.
- [ ] Run the focused tests, then `dotnet test src/Namotion.Interceptor.Tests/Namotion.Interceptor.Tests.csproj --filter "FullyQualifiedName~Context"`.
- [ ] Accept the Core public API snapshot and inspect the diff to ensure only `ISingletonContextService<TContract>` was added in this task.
- [ ] Commit with `feat: enforce singleton context service contracts`.

## Task 3: Introduce exact executor attachment state and the structural write route

**Files:**

- Create: `src/Namotion.Interceptor/Interceptors/InterceptorSubjectAttachment.cs`
- Create: `src/Namotion.Interceptor/Interceptors/SubjectPropertyRegistrationContext.cs`
- Move and modify: `src/Namotion.Interceptor.Tracking/SubjectPropertyTypeExtensions.cs` to `src/Namotion.Interceptor/SubjectPropertyTypeExtensions.cs`, retaining the `Namotion.Interceptor.Tracking` namespace so the public source API stays stable while the implementation moves into Core
- Modify: `src/Namotion.Interceptor/IInterceptorSubject.cs`
- Modify: `src/Namotion.Interceptor/Interceptors/IInterceptorExecutor.cs`
- Modify: `src/Namotion.Interceptor/Interceptors/InterceptorExecutor.cs`
- Modify: `src/Namotion.Interceptor/Interceptors/IWriteInterceptor.cs`
- Modify: `src/Namotion.Interceptor/InterceptorSubjectExtensions.cs`
- Modify: `src/Namotion.Interceptor/PropertyReferenceExtensions.cs`
- Modify: `src/Namotion.Interceptor/SubjectPropertyMetadata.cs`
- Modify: `src/Namotion.Interceptor/Cache/ReadInterceptorFactory.cs`
- Modify: `src/Namotion.Interceptor/Cache/WriteInterceptorFactory.cs`
- Modify: `src/Namotion.Interceptor.Generator/Models/PropertyMetadata.cs`
- Create: `src/Namotion.Interceptor.Generator/SubjectTypeClassifier.cs`
- Modify: `src/Namotion.Interceptor.Generator/SubjectMetadataExtractor.cs`
- Modify: `src/Namotion.Interceptor.Generator/SubjectCodeGenerator.cs`
- Modify: `src/Namotion.Interceptor.Generator/GeneratedMemberTable.cs`
- Modify: `src/Namotion.Interceptor.Generator/SubjectBaseContract.cs`
- Modify: `src/Namotion.Interceptor.Dynamic/DynamicSubject.cs`
- Modify: `src/Namotion.Interceptor.Dynamic/DynamicSubjectFactory.cs`
- Test: `src/Namotion.Interceptor.Tests/ExecutorPublicationTests.cs`
- Create: `src/Namotion.Interceptor.Tests/SubjectContextAttachmentTests.cs`
- Create: `src/Namotion.Interceptor.Tests/StructuralWriteAttachmentRevisionTests.cs`
- Modify: `src/Namotion.Interceptor.Generator.Tests/SubjectBaseShapeTests.cs`
- Modify: `src/Namotion.Interceptor.Generator.Tests/GeneratedMemberTableTests.cs`
- Create: `src/Namotion.Interceptor.Generator.Tests/SubjectTypeClassifierTests.cs`
- Modify: `src/Namotion.Interceptor.Dynamic.Tests/DynamicSubjectExecutorPublicationTests.cs`

- [ ] Write tests proving the executor is published once under a first-access race, an unattached subject reports null, `GetContext()` throws while `TryGetContext()` returns null, and an attachment snapshot cannot be used to mutate another executor.
- [ ] Write transition tests for unattached to inherited/explicit, inherited to explicit, explicit to inherited, attached to unattached, stale compare-and-swap failure, invalid null-explicit state, and prohibited direct context swap.
- [ ] Write generator shape tests proving `IInterceptorSubject` exposes `Executor`, generated subjects use `_executor`, and context-taking constructors call `AttachToContext(context)` after ordinary constructor chaining.
- [ ] Write generator classifier tests mirroring `SubjectPropertyTypeExtensionsTests` for direct subjects, `object`, plain interfaces, scalar values, subject collections, subject dictionaries, non-subject generic collections, non-generic collections, and hybrid subject/enumerable types.
- [ ] Write generated-output tests proving scalar properties emit `SetPropertyValue` and subject-capable properties emit `SetStructuralPropertyValue`. Write equivalent Dynamic tests proving reflection metadata chooses the structural route once during proxy construction, not on every write.
- [ ] Run the focused Core, Generator, and Dynamic tests and confirm they fail before implementation.
- [ ] Add `IInterceptorExecutor Executor { get; }` to `IInterceptorSubject`. Keep `Data`, `Properties`, and `AddProperties`. Retain `Context` and `SyncRoot` only as unannotated `TODO(single-context-cutover)` transition members so downstream projects compile until Task 11; do not use them in new code.
- [ ] Change `IInterceptorExecutor` so it no longer inherits `IInterceptorSubjectContext`. Expose nullable `Context`, `IsExplicitlyAttached`, ordinary read/write/invoke methods, `SetStructuralPropertyValue`, and the public raw attachment transition seam.
- [ ] Implement `InterceptorSubjectAttachment` as an allocation-free snapshot containing public context/explicit/revision values and an internal executor identity. Implement `TryUpdateAttachment(expected, context, isExplicit, out current)` under the executor's private monitor. Enforce exact reference identity, legal state shapes, and no direct non-null context swap.
- [ ] Keep the transition revision separate from the existing committed-property revision. Increment attachment revision on every successful context or explicit-bit transition.
- [ ] Implement `AttachToContext(context)` by applying the strict state table, then delegating to the target context's zero-or-one `ILifecycleInterceptor`; when none exists, perform the explicit raw transition for the root only. Implement `DetachFromContext(context)` against the executor's current exact context and its zero-or-one lifecycle, with the same strict validation before mutation.
- [ ] Move declared-type subject-shape classification into Core because Dynamic and the executor entry path need it without referencing Tracking. Cache runtime `Type` classification as today. Add the equivalent Roslyn symbol classifier so generated scalar setters contain no runtime type test.
- [ ] Make `SetStructuralPropertyValue` capture the attachment snapshot before resolving the context's interceptor chain. Store the required snapshot in `PropertyWriteContext<TProperty>`. At the terminal, take the private executor monitor, compare the attachment revision/context, throw on staleness before the backing delegate, then perform the existing write/timestamp/property-revision work.
- [ ] Leave scalar `SetPropertyValue` free of attachment snapshot capture/comparison. It may finish using a pinned old or new interceptor snapshot during a concurrent attachment change because it cannot change topology.
- [ ] Keep zero-interceptor reads and scalar writes direct/inlinable. Move terminal synchronization from public `subject.SyncRoot` to the executor monitor.
- [ ] Implement generated and Dynamic executor publication with `Interlocked.CompareExchange`; do not allocate an executor on scalar get/set until interception or structural revision safety requires it.
- [ ] Emit temporary `Context` and `SyncRoot` forwarding members in generated, Dynamic, and manual subjects so downstream projects compile during Tasks 4 through 10. `Context` forwards to `Executor.GetContext()` and therefore throws while unattached; `SyncRoot` forwards to a transition-only public monitor accessor. Mark the declarations with `TODO(single-context-cutover)`, do not mark them obsolete, do not use them in new implementation code, and delete both plus the public monitor accessor in Task 11.
- [ ] Run `dotnet test src/Namotion.Interceptor.Tests/Namotion.Interceptor.Tests.csproj`, `dotnet test src/Namotion.Interceptor.Generator.Tests/Namotion.Interceptor.Generator.Tests.csproj`, and `dotnet test src/Namotion.Interceptor.Dynamic.Tests/Namotion.Interceptor.Dynamic.Tests.csproj`.
- [ ] Inspect generated code for scalar and structural models and confirm scalar setters contain neither a declared-type lookup nor an attachment-revision comparison.
- [ ] Commit with `refactor: add exact subject attachment state`.

## Task 4: Flatten context service resolution and route execution through the exact context

**Files:**

- Modify: `src/Namotion.Interceptor/IInterceptorSubjectContext.cs`
- Rewrite: `src/Namotion.Interceptor/InterceptorSubjectContext.cs`
- Modify: `src/Namotion.Interceptor/Interceptors/InterceptorExecutor.cs`
- Delete: fallback-only helpers nested in `src/Namotion.Interceptor/InterceptorSubjectContext.cs`
- Delete: `src/Namotion.Interceptor.Tests/Context/ContextDeepGraphTests.cs`
- Delete: `src/Namotion.Interceptor.Tests/Context/ContextDelegationCycleTests.cs`
- Delete: `src/Namotion.Interceptor.Tests/Context/ContextServiceWalkOrderTests.cs`
- Delete: `src/Namotion.Interceptor.Tests/Context/ContextSubtreeServiceTests.cs`
- Delete or rewrite: `src/Namotion.Interceptor.Tests/Context/ContextConcurrencyFuzzTests.cs`
- Modify: `src/Namotion.Interceptor.Tests/Context/ContextConcurrencyTests.cs`
- Modify: `src/Namotion.Interceptor.Tests/Context/ContextFunctionCacheTests.cs`
- Modify: `src/Namotion.Interceptor.Tests/Context/ContextServiceResolutionTests.cs`

- [ ] Replace fallback/delegation tests with flat-context tests for registration order, assignability, cached interceptor-chain invalidation after late service publication, in-flight pinned snapshots, and concurrent reads plus service additions.
- [ ] Confirm the rewritten tests fail or do not compile while fallback APIs still define the old contract.
- [ ] Remove `AddFallbackContext` and `RemoveFallbackContext` from `IInterceptorSubjectContext` and `InterceptorSubjectContext` without obsolete aliases.
- [ ] Reduce context state to the directly registered immutable service array and local interceptor/function caches. Remove reverse-using-context tracking, fallback walk/cycle detection, delegation targets, cross-context invalidation, and their lock-order machinery.
- [ ] Resolve every executor read/write/invoke chain directly from the executor's current exact context snapshot; use empty cached chains while unattached.
- [ ] Preserve service ordering attributes and local cache invalidation when services are added. Late stateful services receive no subject replay.
- [ ] Run the full Core test project and the benchmark project build.
- [ ] Commit with `refactor: flatten interceptor subject contexts`.

## Task 5: Implement occurrence-aware built-in lifecycle ownership

**Files:**

- Rewrite: `src/Namotion.Interceptor.Tracking/Lifecycle/LifecycleInterceptor.cs`
- Create: `src/Namotion.Interceptor.Tracking/Lifecycle/LifecycleGraphState.cs`
- Create: `src/Namotion.Interceptor.Tracking/Lifecycle/SubjectEdge.cs`
- Create: `src/Namotion.Interceptor.Tracking/Lifecycle/SubjectEdgeCollection.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/LifecycleInterceptorExtensions.cs`
- Modify or delete when unused: `src/Namotion.Interceptor.Tracking/Lifecycle/PropertyReferenceSet.cs`
- Modify: `src/Namotion.Interceptor/Interceptors/ILifecycleInterceptor.cs`
- Modify: `src/Namotion.Interceptor.Tracking/InterceptorSubjectContextExtensions.cs`
- Rewrite: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/RecursiveAttachTests.cs`
- Rewrite: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/LifecycleEventsTests.cs`
- Modify: `src/Namotion.Interceptor.Tracking.Tests/LifecycleInterceptorTests.cs`
- Create: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/SingleContextOwnershipTests.cs`
- Create: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/OccurrenceEdgeTests.cs`
- Create: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/CycleReachabilityTests.cs`

- [ ] Through public APIs, write failing tests for strict repeated explicit attach, inherited-to-explicit promotion, strict detach, exact-context conflict, constructor explicit roots, no-lifecycle root-only behavior, assignment inheritance, unassignment release, and an explicit child surviving parent unassignment.
- [ ] Through public APIs, write failing DAG/cycle tests for multiple parents, self-cycle, multi-node cycle, cycle orphan release, cycle retained by another explicit root, and cross-context assignment rejected before the backing field changes.
- [ ] Write occurrence tests for `[a, a, b]`, duplicate removal, duplicate reorder, dictionary keys, and general enumerable ordinals. Assert `a.GetReferenceCount() == 2`, occurrence-aware parents, every edge callback, and only first/last subject attach/detach callbacks.
- [ ] Run the focused Tracking tests and confirm the current implementation fails the new strict attach, duplicate-count, and orphan-cycle cases.
- [ ] Evolve Core `ILifecycleInterceptor` to inherit `IWriteInterceptor` and `ISingletonContextService<ILifecycleInterceptor>`, with explicit attach, explicit detach, and metadata registration methods. Keep all members public enough for a third-party lifecycle package; do not use friend access.
- [ ] Construct one `LifecycleInterceptor(context)` per context. Store one context reference, one private reentrant lock, explicit roots, owned subjects, committed structural property baselines, incoming occurrence-aware edges, and outgoing edges.
- [ ] Implement inline storage for zero/one incoming edge and allocate/pool expanded occurrence storage only when multiplicity requires it. Preserve distinct property/index/key identity while matching retained duplicates deterministically in enumeration order.
- [ ] On explicit attach, validate strict rules, discover the current direct-readable structural component with visited sets, claim executors through the public Core compare-and-swap seam, then emit callbacks/handlers in existing order. Promote an inherited same-context subject without attach callbacks.
- [ ] On structural write, reject conflicts and claim the proposed new component before `next`, call `next` exactly once, then reconcile committed edges. Publish removals before additions and reverse old-removal/forward new-addition order as on master.
- [ ] On edge or explicit-root removal, compute reachability from every explicit root over committed outgoing occurrence edges. Release all unmarked subjects, including closed cycles, once each in deterministic first-visit order.
- [ ] Update `GetParents()` and `GetReferenceCount()` to query the built-in lifecycle state through the exact context. Return empty for unattached subjects or contexts using another lifecycle implementation.
- [ ] Ensure authoritative parent edges are visible before the first attach/detach handler. Keep callback behavior otherwise identical: context handlers, subject handlers, `SubjectAttached`/`SubjectDetaching`, property handlers, and recursive descent boundaries.
- [ ] Run `dotnet test src/Namotion.Interceptor.Tracking.Tests/Namotion.Interceptor.Tracking.Tests.csproj --filter "FullyQualifiedName~Lifecycle"` and then the full Tracking test project.
- [ ] Commit with `feat: own graph lifecycle in one context`.

## Task 6: Enforce lifecycle concurrency and reentrancy contracts

**Files:**

- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/LifecycleInterceptor.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/LifecycleGraphState.cs`
- Rewrite: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/ConcurrentWriteLifecycleTests.cs`
- Create: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/LifecycleReentrancyTests.cs`
- Modify: `src/Namotion.Interceptor.Tracking.Tests/Change/WritePipelineOrderTests.cs`

- [ ] Add synchronization-based tests, without `Task.Delay` or `Thread.Sleep`, for serialized same-context structural writes, parallel independent-context structural writes, two contexts racing to adopt one unattached subject, and an unattached/old-context structural chain racing with attach or reattach.
- [ ] Add callback tests proving scalar reads/writes are allowed, structural setters throw before backing mutation, explicit attach/detach throw, same-lifecycle `AddProperties` is reserved for Task 7, and an unsupported repeated `next` call is not guarded by Core.
- [ ] Add pipeline-order tests proving equality/veto interceptors can suppress before lifecycle, lifecycle is closest to the backing writer, and outer change/derived/transaction interceptors see lifecycle reconciliation completed after a successful terminal write.
- [ ] Run the focused tests and confirm intended failures.
- [ ] Use a thread-static or equivalent allocation-free callback phase marker associated with the active built-in lifecycle. Fail fast on forbidden structural/explicit operations before provisional claims, callbacks, or backing writes.
- [ ] Serialize all built-in structural topology changes through the lifecycle's private reentrant monitor. Never hold two lifecycle locks. Take the lifecycle lock before any executor monitor when both are required.
- [ ] At context write-chain construction only, place the singleton `ILifecycleInterceptor` closest to the terminal after ordinary ordering resolution. Do not annotate the class with a global `RunsLast` attribute because the same instance also participates in lifecycle-handler ordering.
- [ ] Ensure stale structural attachment snapshots fail at the executor terminal before mutation and provisional lifecycle state is released. Scalar writes retain the existing snapshot-pinning semantics and no attachment-revision guard.
- [ ] Run full Core and Tracking tests repeatedly enough to exercise the race tests deterministically.
- [ ] Commit with `fix: enforce lifecycle topology concurrency contracts`.

## Task 7: Make `AddProperties` atomic and topology-aware

**Files:**

- Modify: `src/Namotion.Interceptor/IInterceptorSubject.cs`
- Modify: `src/Namotion.Interceptor/Interceptors/SubjectPropertyRegistrationContext.cs`
- Modify: `src/Namotion.Interceptor/Interceptors/ILifecycleInterceptor.cs`
- Modify: `src/Namotion.Interceptor/Interceptors/InterceptorExecutor.cs`
- Modify: `src/Namotion.Interceptor.Generator/SubjectCodeGenerator.cs`
- Modify: `src/Namotion.Interceptor.Dynamic/DynamicSubject.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/LifecycleInterceptor.cs`
- Create: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/AddPropertiesLifecycleTests.cs`
- Modify: `src/Namotion.Interceptor.Dynamic.Tests/DynamicSubjectTests.cs`

- [ ] Write failing tests for once-only input enumeration, atomic duplicate-name rejection, one getter call per qualifying structural property, atomic scalar/structural batches, provisional-claim release on conflict, same-lifecycle callback success, unattached callback success, cross-context callback rejection before enumeration, derived structural exclusion, and a publisher that is invoked zero or one time by contract.
- [ ] Document that the input metadata enumerable must be synchronous, stable, and free of topology/metadata side effects. It is materialized exactly once after callback admission; unsupported iterator reentrancy receives no replay or rollback.
- [ ] Implement `SubjectPropertyRegistrationContext` with the subject and a lazy once-materialized metadata sequence. Core owns duplicate-name validation and constructs the complete immutable lookup before calling its synchronous publication continuation.
- [ ] For an owned subject, let lifecycle inspect callback state and reject a cross-context callback before forcing enumeration. For an unattached subject, publish metadata directly without ownership work.
- [ ] Classify initial ownership only for `IsIntercepted && !IsDerived && Type.CanContainSubjects() && GetValue != null`. Invoke each qualifying getter exactly once, capture the result, validate/claim its complete prospective subgraph, publish metadata atomically, invoke property handlers in input order, then commit captured edges and baselines as ordinary assignments.
- [ ] If enumeration, duplicate validation, getter, context validation, or claiming fails, publish no metadata, Registry state, lifecycle edges, or owned state. Release provisional executor claims. Keep the exception-free publisher contract and do not attempt rollback after a violating publisher mutates and throws.
- [ ] Ensure subsequent stored dynamic structural writes route through `SetStructuralPropertyValue`; derived/computed properties never establish edges.
- [ ] Run focused Core, Dynamic, and Tracking tests, then all three projects.
- [ ] Commit with `feat: make dynamic property admission lifecycle aware`.

## Task 8: Merge parent/context handlers into lifecycle and update Registry projection

**Files:**

- Delete: `src/Namotion.Interceptor.Tracking/Lifecycle/ContextInheritanceHandler.cs`
- Delete: `src/Namotion.Interceptor.Tracking/Parent/ParentTrackingHandler.cs`
- Modify: `src/Namotion.Interceptor.Tracking/InterceptorSubjectContextExtensions.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Parent/ParentsHandlerExtensions.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/LifecycleInterceptor.cs`
- Delete: `src/Namotion.Interceptor.Tracking.Tests/ContextInheritanceHandlerTests.cs`
- Delete: `src/Namotion.Interceptor.Tracking.Tests/ParentTrackingHandlerTests.cs`
- Rewrite: `src/Namotion.Interceptor.Tracking.Tests/Parent/ParentAccessDuringLifecycleTests.cs`
- Modify: `src/Namotion.Interceptor.Registry/SubjectRegistry.cs`
- Modify: `src/Namotion.Interceptor.Registry/Abstractions/RegisteredSubject.cs`
- Modify: `src/Namotion.Interceptor.Registry/Abstractions/RegisteredSubjectProperty.cs`
- Modify: `src/Namotion.Interceptor.Registry/InterceptorSubjectContextExtensions.cs`
- Modify: `src/Namotion.Interceptor.Registry/SubjectRegistryExtensions.cs`
- Modify: `src/Namotion.Interceptor.Registry.Tests/RegistryHandlerOrderTests.cs`
- Modify: `src/Namotion.Interceptor.Registry.Tests/RegistryAncestorResolutionTests.cs`
- Modify: `src/Namotion.Interceptor.Registry.Tests/DynamicPropertyLifecycleTests.cs`
- Modify: `src/Namotion.Interceptor.Registry.Tests/DynamicPropertyWithWriteInterceptorTests.cs`
- Modify: `src/Namotion.Interceptor.Registry.Tests/GraphBehavior/CycleTests.cs`
- Modify: `src/Namotion.Interceptor.Registry.Tests/GraphBehavior/DagTests.cs`

- [ ] Rewrite configuration tests so `WithLifecycle()` implicitly supplies parent tracking/context inheritance, `WithRegistry()` installs lifecycle first, repeated `WithRegistry().WithLifecycle()` is idempotent, and a custom lifecycle conflict fails before Registry is published.
- [ ] Rewrite ordering tests for Registry before lifecycle descent and source/hosted handlers after it. Assert `GetParents()` is already authoritative inside the first handler.
- [ ] Change the orphaned-cycle Registry snapshot from the old limitation to complete detach. Add duplicate occurrence/index/key Registry assertions.
- [ ] Add dynamic Registry tests proving the Registry projection exists before initial structural edge notifications and derived structural values do not create Registry parent edges.
- [ ] Remove `WithContextInheritance()` and `WithParents()` without obsolete aliases. Update `WithLifecycle()`, `WithFullPropertyTracking()`, and `WithRegistry()` to install the one default lifecycle through the singleton predicate.
- [ ] Make `LifecycleInterceptor` implement Tracking's `ILifecycleHandler` at the former context-inheritance descent slot. Migrate all `RunsBefore`/`RunsAfter` references from the deleted handlers to `LifecycleInterceptor`.
- [ ] Mark `SubjectRegistry` as `ISingletonContextService<ISubjectRegistry>`.
- [ ] Replace `RegisteredSubject.AddProperty`'s manual Registry insertion, explicit property attach workaround, and synthetic null-to-value write. Let `IPropertyLifecycleHandler.AttachProperty` create a missing `RegisteredSubjectProperty` before lifecycle publishes initial structural edges.
- [ ] Keep Registry parent/child snapshots as navigation projections only; no Registry state participates in lifecycle ownership or reachability.
- [ ] Run full Tracking and Registry test projects and inspect changed Verify snapshots.
- [ ] Commit with `refactor: merge ownership handlers into lifecycle`.

## Task 9: Mark first-party authorities as singleton and migrate configuration ordering

**Files:**

- Modify: `src/Namotion.Interceptor.Tracking/InterceptorSubjectContextExtensions.cs`
- Modify: `src/Namotion.Interceptor.Tracking/PropertyValueEqualityCheckHandler.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Change/DerivedPropertyChangeHandler.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Change/PropertyChangeInterceptor.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Recorder/ReadPropertyRecorder.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Transactions/SubjectTransactionInterceptor.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Transactions/ITransactionWriter.cs`
- Modify: `src/Namotion.Interceptor.Registry/Abstractions/ISubjectRegistry.cs`
- Modify: `src/Namotion.Interceptor.Connectors/InterceptorSubjectContextExtensions.cs`
- Modify: `src/Namotion.Interceptor.Connectors/Monitoring/SourceMonitor.cs`
- Modify: `src/Namotion.Interceptor.Connectors/Monitoring/SourceMonitoringExtensions.cs`
- Modify: `src/Namotion.Interceptor.Connectors/Transactions/SourceTransactionWriter.cs`
- Modify: `src/Namotion.Interceptor.Hosting/HostedServiceHandler.cs`
- Modify: `src/Namotion.Interceptor.Hosting/InterceptorHostingExtensions.cs`
- Modify: `src/Namotion.Interceptor.Hosting/InterceptorSubjectContextExtensions.cs`
- Modify: `src/Namotion.Interceptor.Validation/ValidationInterceptor.cs`
- Modify: `src/Namotion.Interceptor.Validation/DataAnnotationsValidator.cs`
- Modify: `src/Namotion.Interceptor.Validation/InterceptorSubjectContextExtensions.cs`
- Test: existing configuration tests in Core, Tracking, Registry, Connectors, Hosting, and Validation

- [ ] Add tests proving each configuration extension is idempotent for its own default service, rejects a conflicting custom service for the same contract, and does not publish dependent services before a lifecycle conflict is detected.
- [ ] Mark lifecycle, `ISubjectRegistry`, `ITransactionWriter`, SourceMonitor, SubjectTransactionInterceptor, PropertyChangeInterceptor, HostedServiceHandler, PropertyValueEqualityCheckHandler, DerivedPropertyChangeHandler, ReadPropertyRecorder, ValidationInterceptor, and DataAnnotationsValidator with appropriate `ISingletonContextService<TContract>` interfaces. Use the abstraction for lifecycle, Registry, and transaction-writer authority slots; use the concrete class for one-per-default-implementation services.
- [ ] Keep ordinary ordered interceptor chains, lifecycle handlers, validators, and user services plural unless a concrete implementation owns a documented singleton authority.
- [ ] Collapse SourceMonitor's current duplicate concrete/`ILifecycleHandler` registration to one service object; rely on assignability for all roles.
- [ ] Make every dependent configuration extension establish lifecycle first, then publish its own stateful services. Late direct additions remain allowed and receive no backfill.
- [ ] Run the affected project tests and `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"` as an early cross-project compile gate.
- [ ] Commit with `refactor: declare singleton context authorities`.

## Task 10: Migrate all first-party consumers and temporary construction ownership

**Files:**

- Modify: `src/HomeBlaze/HomeBlaze.Host/Components/HomeBlazorComponentBase.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Services/Lifecycle/PropertyAttributeInitializer.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Services/RootManager.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Services/SubjectContextFactory.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Services/SubjectPathResolver.cs`
- Modify: `src/Namotion.Interceptor.AspNetCore/Extensions/SubjectRegistryJsonExtensions.cs`
- Modify: `src/Namotion.Interceptor.Connectors/ISubjectSource.cs`
- Modify: `src/Namotion.Interceptor.Connectors/SourceOwnershipManager.cs`
- Modify: `src/Namotion.Interceptor.Connectors/SourcePropertyExtensions.cs`
- Modify: `src/Namotion.Interceptor.Connectors/SubjectFactoryExtensions.cs`
- Modify: `src/Namotion.Interceptor.Connectors/SubjectSourceBase.cs`
- Modify: `src/Namotion.Interceptor.Connectors/Updates/Internal/SubjectItemsUpdateApplier.cs`
- Modify: `src/Namotion.Interceptor.Connectors/Updates/Internal/SubjectUpdateApplier.cs`
- Modify: `src/Namotion.Interceptor.ConnectorTester/Engine/Mutation/MutationEngine.cs`
- Modify: `src/Namotion.Interceptor.GraphQL/GraphQLSubscriptionSender.cs`
- Modify: `src/Namotion.Interceptor.Mcp/Tools/SearchTool.cs`
- Modify: `src/Namotion.Interceptor.Mqtt/Client/MqttSubjectClientSource.cs`
- Modify: `src/Namotion.Interceptor.Mqtt/Server/MqttSubjectServer.cs`
- Modify: `src/Namotion.Interceptor.OpcUa/Client/OpcUaSubjectClientSource.cs`
- Modify: `src/Namotion.Interceptor.OpcUa/Client/OpcUaSubjectLoader.cs`
- Modify: `src/Namotion.Interceptor.OpcUa/Server/OpcUaSubjectServer.cs`
- Modify: `src/Namotion.Interceptor.Testing/ObjectExtensions.cs`
- Modify: `src/Namotion.Interceptor.WebSocket/Client/WebSocketSubjectClientSource.cs`
- Modify: `src/Namotion.Interceptor.WebSocket/Server/WebSocketSubjectHandler.cs`
- Modify affected sample `Program.cs` files under `src/Namotion.Interceptor.*Sample*`
- Modify corresponding unit/integration tests identified by the residual search below

- [ ] Add connector update tests for a temporary child success path, assignment failure, population failure, collection/dictionary batch ownership, shared children, and cycles. Assert successful assignment leaves the child inherited/non-explicit and failures release temporary roots.
- [ ] Add OPC UA loader tests for the same explicit construction-transfer pattern around asynchronous population.
- [ ] Change long-lived roots such as HomeBlaze `RootManager` to one explicit `Root.AttachToContext(_context)`.
- [ ] For newly constructed connector/OPC children, use this exact transfer protocol:

```csharp
var context = parent.GetContext();
child.AttachToContext(context);
try
{
    PopulateChild(child);
    parent.Child = child;
}
finally
{
    child.DetachFromContext(context);
}
```

- [ ] For collection/dictionary loaders, explicitly attach all newly populated temporary roots, assign the complete structural value, then detach every temporary root in `finally`. Ensure partial failure releases every temporary claim.
- [ ] Replace direct `subject.Context` usage with `GetContext()` where attachment is required and `TryGetContext()` where absence is valid. Resolve services only through the returned context.
- [ ] Replace direct executor casts from `subject.Context` with `subject.Executor`.
- [ ] Replace MQTT `RegisteredSubject.ReferenceCount <= 0` ownership checks with Registry membership or exact-context checks. Preserve the valid explicit-root case where reference count is zero.
- [ ] Change `WithSourceMonitoring()` and branch-scope consumers to use lifecycle's authoritative parents; remove `WithParents()` calls.
- [ ] Run this residual gate and classify every hit explicitly; no production hit may remain except deliberate mentions in design/plan or a type named `Context` unrelated to subjects:

```powershell
rg -n "\.Context\b|SyncRoot|AddFallbackContext|RemoveFallbackContext|WithContextInheritance|WithParents" src -g "*.cs"
```

- [ ] Run non-integration tests for Connectors, OPC UA, MQTT, WebSocket, Hosting, HomeBlaze services, and the solution.
- [ ] Commit with `refactor: migrate consumers to exact subject contexts`.

## Task 11: Remove transitional APIs and dead fallback/lifecycle code

**Files:**

- Modify: `src/Namotion.Interceptor/IInterceptorSubject.cs`
- Modify: `src/Namotion.Interceptor/Interceptors/IInterceptorExecutor.cs`
- Modify: `src/Namotion.Interceptor/Interceptors/InterceptorExecutor.cs`
- Modify: `src/Namotion.Interceptor/InterceptorSubjectContext.cs`
- Modify: `src/Namotion.Interceptor.Generator/SubjectCodeGenerator.cs`
- Modify: `src/Namotion.Interceptor.Generator/GeneratedMemberTable.cs`
- Delete: `src/Namotion.Interceptor.Benchmark/ContextDelegationDepthBenchmark.cs`
- Modify: `src/Namotion.Interceptor.Benchmark/DynamicSubjectBenchmark.cs`
- Modify: `src/Namotion.Interceptor.Benchmark/SubjectSourceBenchmark.cs`
- Delete any now-unused fallback state, old parent state, `PropertyReferenceSet`, and `TODO(single-context-cutover)` forwarding members
- Modify generated/manual subject-shape tests across Core, Generator, Dynamic, Connectors, and SourceWait test fixtures

- [ ] Delete every temporary forwarding member introduced in Task 3. Do not leave obsolete APIs.
- [ ] Confirm final `IInterceptorSubject` contains `Executor`, `Data`, `Properties`, and `AddProperties`, but no `Context` or `SyncRoot`.
- [ ] Confirm final executor is not a context and contains no subject-local service registration, fallback service lookup, Registry, parent, or reachability logic.
- [ ] Replace old fallback-focused benchmarks. Keep `LifecycleOwnershipBenchmark` source-identical to its Task 1 base commit.
- [ ] Update all manual test subjects to publish one executor with `Interlocked.CompareExchange` and route scalar/structural writes correctly. Do not add friend assembly access for convenience.
- [ ] Run the residual API search from Task 10 and `rg -n "TODO\(single-context-cutover\)" src` to prove no transition shim remains. Inspect `git diff LIFECYCLE_BENCHMARK_BASE -- "src/**/*.csproj"` and confirm this work added no `InternalsVisibleTo`; remove Core's existing Tracking friend entry only if the resulting production code no longer consumes any Core internal.
- [ ] Run Core, Generator, Dynamic, Tracking, Registry, and Connectors tests.
- [ ] Commit with `refactor: remove subject-local context compatibility`.

## Task 12: Update public API snapshots and documentation

**Files:**

- Modify: `docs/interceptor.md`
- Modify: `docs/tracking.md`
- Modify: `docs/subject-guidelines.md`
- Modify: `docs/design/tracking-lifecycle.md`
- Modify: `docs/aspnetcore.md`
- Modify: `docs/connectors-websocket.md`
- Modify any sample/readme pages found by `rg` for removed APIs
- Modify public API snapshots under `src/*Tests/VerifyChecksTests.PublicApi.verified.txt` where generated `.received.txt` files prove intentional changes
- Modify Verify graph snapshots affected by cycle cleanup, duplicate occurrences, or context display

- [ ] Update Core docs for nullable exact contexts, strict explicit roots, constructor attachment, `GetContext`/`TryGetContext`, no local services/fallbacks, raw lifecycle extensibility, and singleton service contracts.
- [ ] Update Tracking docs for automatic inheritance/parents under `WithLifecycle`, arbitrary cycles/DAGs, explicit-root reachability, occurrence-aware reference counts, callback order, callback reentrancy restrictions, zero-or-one continuations, and exception-free callback contracts.
- [ ] Update Registry docs to call it an optional reflection/navigation projection and document dynamic property admission before initial edges.
- [ ] Update connector docs with temporary explicit construction ownership and the fact that Registry reference count is not ownership.
- [ ] Keep Markdown paragraphs unwrapped and use no em dashes.
- [ ] Run every affected `VerifyChecksTests.PublicApi` test, copy only intentional `.received.txt` outputs over `.verified.txt`, delete received artifacts, and inspect that removed APIs are absent rather than obsolete.
- [ ] Run `rg -n "AddFallbackContext|RemoveFallbackContext|WithContextInheritance|WithParents|\.Context\b|SyncRoot" docs src -g "*.md" -g "*.cs"` and resolve every stale public example.
- [ ] Run `dotnet build src/Namotion.Interceptor.slnx` and `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`.
- [ ] Commit with `docs: document exact context lifecycle ownership`.

## Task 13: Perform performance and connector verification, then update the draft PR

**Files:**

- Read before action: `docs/benchmarking.md`
- Read before action: `docs/connector-tester.md`
- Modify: draft PR body/checklist only after evidence exists
- Add no benchmark result files unless the repository convention explicitly requires them

- [ ] Before running hours-long verification, agree the final scope with the maintainer. Proposed scope is the default non-integration solution suite plus targeted OPC UA, MQTT, and WebSocket integration tests; Connector Tester chaos for each affected connector at 100 cycles; one 15-minute load cycle for each connector; and memory mode only if allocation tests/benchmarks indicate risk.
- [ ] In an external worktree, pin the CPU, keep the machine quiet, and compare the final branch against the exact `LIFECYCLE_BENCHMARK_BASE` hash using `*LifecycleOwnershipBenchmark*`, `*RegistryBenchmark*`, and `*ServiceOrderResolverBenchmark.LinearChain*` with `-LaunchCount 3`. Do not use `-Short` for a decision.
- [ ] Repeat any small timing delta before interpreting it. Treat allocations as primary. Quote both resolved commit hashes and the unchanged noise-reference movement in the PR.
- [ ] Diff generated scalar/structural output and JIT disassembly for `SetPropertyValue`/`SetStructuralPropertyValue` if benchmark noise cannot resolve a small scalar-path change.
- [ ] Run the agreed targeted integration tests. If Connector Tester was approved, record exact commands, connector modes, cycle counts/durations, and findings; do not claim it ran if it was deferred.
- [ ] Run the final clean-tree verification: `dotnet build src/Namotion.Interceptor.slnx`, `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`, agreed targeted integration tests, `git diff --check`, `git status --short`, and residual removed-API searches.
- [ ] Use the `superpowers:requesting-code-review` skill for a design/implementation review and resolve findings through `superpowers:receiving-code-review` before claiming completion.
- [ ] Update the existing draft PR body with final diff composition, tests, benchmark evidence, connector verification/deferment, breaking migration notes, and remaining risk. Keep it draft until the user decides it is ready.
- [ ] Commit any review/documentation corrections, push the branch, and report the final commit hash and PR URL.

## Final acceptance checklist

- [ ] A subject is unattached or owned by exactly one exact context, with at most one explicit root anchor.
- [ ] Arbitrary cycles, DAGs, multiple parents, and duplicate collection/dictionary occurrences reconcile correctly.
- [ ] Subject attach/detach callbacks remain first-entry/last-exit events; edge handlers run for every real occurrence.
- [ ] Cross-context and forbidden callback structural mutations fail before backing mutation.
- [ ] `AddProperties` is atomic, once-materialized, derived-safe, and Registry-ready before initial edges.
- [ ] Core exposes a complete public third-party lifecycle seam without new friend-assembly access.
- [ ] Executors are not contexts; subject-local services, fallbacks, `Context`, `SyncRoot`, old handlers, and old configuration methods are absent.
- [ ] Singleton service contracts fail fast, including duplicate same-instance registrations, while late registration stays allowed without backfill.
- [ ] Scalar generated writes retain their direct route and benchmarks show no material regression beyond measured noise.
- [ ] All required build, unit, public API, agreed integration, benchmark, and Connector Tester evidence is attached to the draft PR.
