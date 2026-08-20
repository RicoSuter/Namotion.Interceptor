# Explicit Subject Ownership Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace fallback lifecycle coupling with strict explicit roots, one effective ownership route, Core membership, global structural/topology serialization, recursive lifecycle reconciliation, and stable serial outcomes.

**Architecture:** One process-wide reentrant `OwnershipTopologyGate` surrounds every potentially structural write and topology mutation. Core owns reservation, commit, unbounded restart, generation, and cleanup. Tracking prepares lifecycle-coordinated graph reservations and reconciles committed operations through a capability-minimal public stack-only facade with one narrowly constrained callback-phase route publication capability. Zero-interceptor reads remain lock-free; intercepted read terminals and scalar write terminals use one private executor monitor. Scalar/read/invoke/service-resolution paths remain outside the topology gate.

**Tech Stack:** C# 13 preview, .NET Standard 2.0 Core, .NET 9/10 feature projects, source generation, xUnit, Verify/PublicApiGenerator, immutable context snapshots, monitor synchronization, thread-static pools.

**Spec:** `docs/superpowers/specs/2026-08-18-explicit-subject-ownership-design.md`

## Global Constraints

- Task 2 starts from exact HEAD `0ff54c7e00b7f425f98a82e7d0926644376e05ac`, after completed Task 1 commits `cf39188b` and `0ff54c7e`.
- Apply the roadmap stack-wide acceptance criterion to PR #474, #419, later #472, and later #440. Contention, stale state, and unfinished internal transitions do not escape as library failures.
- One process-wide reentrant `OwnershipTopologyGate` is the only ownership/topology operation monitor.
- Enter it before structural action selection and before explicit/context/route/metadata mutation. Scalar writes, reads, invokes, and service resolution remain outside.
- Preserve ordinary successful single-context callback order and visibility except the approved fallback/inheritance identity changes.
- Core owns ledger commit/restart/finalize. The provider exposes reservation, view, prospective-array inspection, one exact committed-view callback-phase route publication, reconciliation, and exact release only.
- A nested write into a `Preparing` reservation cancels the complete outer batch, commits independently, and causes terminal-only rediscovery from final values.
- No internal restart replays an executed interceptor prefix, backing writer, lifecycle callback, one public context callback invocation, or one input materialization.
- `TryAddService` predicate/factory semantics, same-thread cross-context reentrancy, exact invocation count, and reentrant state reread remain exact.
- Legal active context mutation preserves the exact lifecycle coordinator identity set and affects future pinned arrays. Authority-changing publication rejects before immutable state publication.
- A plain configured context with no lifecycle coordinator may explicitly own/route its root and compose services, but it performs no implicit child discovery/adoption/release and structural setters do ordinary writes. Any exact single resolved `ILifecycleInterceptor` is canonical; `WithLifecycle()` is the standard built-in registration and two distinct instances are incompatible.
- One `AddProperties` call is one atomic once-materialized batch with one prepare/commit/reconcile/release. Only the distinct direct `GetOwnershipValue` reader can create initial automatic edges.
- Strict explicit roots, composition-only fallbacks, cycles, DAGs, repeated references, reference counts, and route descriptor exactness remain binding.
- Generated/custom backing writers and ownership readers are synchronous direct storage operations. Ordinary `GetValue` is never ownership discovery. Callbacks/factories do not synchronously wait for another thread requiring the topology gate. Library-controlled restart is unbounded; permanently nonconverging direct readers and sustained adversarial scheduler contention are unsupported liveness conditions, not finite retry failures.
- Remove public `IInterceptorSubject.SyncRoot`. Every initialized subject has exactly one private property/state monitor in its `InterceptorExecutor`; no second subject lock or public atomic-lock snapshot capability remains.
- Warmed stable-topology structural writes allocate zero beyond configured application work. Actual route mutation separately counts PR #474 route/state, reverse fan-out snapshots, service-walk/cache/`ImmutableArray`, invalidation generations, and first-use retained capacity. Construct a replacement only after the exact stale check and add no other avoidable allocation. Measure the exact design rows, including branch-local handlers/fallbacks, reverse fan-out greater than one, and `InitializedContextZeroReadInterceptors`, at master, cleaned PR #474, and final PR #419.
- Tests use When/Then names, explicit Arrange/Act/Assert, and events/barriers rather than sleeps.
- Add no new Tracking friend access, compatibility provider, fallback lifecycle adapter, alternate synchronization path, boxed provider payload, or unconstrained provider commit capability.
- Task 2 is one atomic no-stub RED-first unit in one worktree and agent session. Every final Core, Tracking, Registry, Generator, Dynamic, Hosting, Connectors, and OPC UA test and oracle is authored before any production edit. Existing-surface rows that still compile are observed semantic RED. References to absent final public types establish project-level compilation RED only; they do not imply that the selected semantic test methods executed. The remaining final tests are source-reviewed against the closed manifest and all semantics prove together at the one complete GREEN gate. Mapping, RED, combined implementation, and final verification are subphases only: there is no intermediate compile/GREEN requirement, stub provider, transitional provider, commit, or handoff.
- Do not use em dashes.

## File and Responsibility Map

### Core state and package infrastructure

- Create `src/Namotion.Interceptor/Ownership/SubjectOwnershipWriteContext.cs`, `SubjectMetadataAddition.cs`, and `SubjectMetadataAdditionBatchContext.cs`: stack-only generic write and atomic metadata-batch provider inputs. Attach/detach use direct subject/context arguments.
- Create `SubjectOwnershipOperation.cs` and `SubjectOwnershipView.cs`: count/index membership view, reservation, current/prospective array selection, and exact committed-view callback-phase route facade without provider ledger commit/retry or object payload.
- Create `SubjectMetadataCommitRegistration.cs`: normally visible exact one-shot commit identity for cross-assembly Registry publication; no equality/name inference.
- Create `ContextAuthorityActivation.cs`: active configured-context authority identity, lease, generation, and status state, never a monitor.
- Create `SubjectOwnershipState.cs`: explicit anchor, ordered parents, selected parent, domain, coordinator, generation, and pending batch.
- Create `SubjectOwnershipBatch.cs`: pooled tentative/committed ledger, facade-operation bookkeeping, route entries, cancellation, and cleanup.
- Create `SubjectOwnershipCoordinator.cs`: `OwnershipTopologyGate`, explicit operations, structural admission, context prospective validation, metadata transition, commit/retry, and cleanup.
- Modify `ILifecycleInterceptor.cs` to the six provider methods in the spec, including exact one-time release.
- Modify `IInterceptorSubject.cs`, `IInterceptorExecutor.cs`, `InterceptorExecutor.cs`, and `SubjectPropertyMetadata.cs` for public `SyncRoot` removal, one private executor monitor, structural admission, direct `GetOwnershipValue`, ownership state, and the public advanced package-infrastructure `AddProperties` entry required by generated consumer assemblies.
- Modify `InterceptorSubjectContext.cs` for gate-first context mutation, cross-context `TryAddService` reentrancy, prospective coordinator validation, immutable array pinning, and self-gating PR #474 route publication.
- Modify `ReadInterceptorFactory.cs`, `PropertyReadContext`, `WriteInterceptorFactory.cs`, and `WriteInterceptorChain.cs` so the zero-interceptor read stays lock-free, only intercepted read/write terminals use the executor monitor, and structural writes defer only at the exact coordinator node.
- Modify `WriteInterceptorFactory.cs` and `WriteInterceptorChain.cs` for structural terminal commit and exact-node exception deferral.

### Generator, Dynamic, and Tracking

- Preserve Task 1 `SubjectPropertyTypeClassifier`, `SubjectPropertyMetadata.CanContainSubjects`, and generator `PropertyMetadata.CanContainSubjects`; add only the distinct ownership-reader metadata consumed by Task 2.
- Modify generator metadata/setter/constructor/AddProperties emission and Dynamic equivalents. Generated intercepted partial structural metadata reads its backing field directly; computed/nonintercepted metadata supplies no ownership reader.
- Modify `src/Namotion.Interceptor.Dynamic/DynamicSubjectFactory.cs` to associate its exact existing interceptor/storage with the proxy subject before `AddProperties` and emit cached static/noncapturing ownership readers over that subject-owned association.
- Create Tracking `SubjectOwnershipTraversal.cs` and `LifecycleReconciliationState.cs`; the latter owns pooled TLS provider state and exact pinned arrays.
- Rewrite `LifecycleInterceptor.cs` as the built-in provider and recursive handler.
- Delete `ContextInheritanceHandler.cs` and `PropertyReferenceSet.cs`; migrate ordering attributes and tests.
- Modify Registry `RegisteredSubject.cs` and `SubjectRegistry.cs` for exact `SubjectMetadataCommitRegistration` promotion and remove the old synthetic intercepted write/value-equality inference.

### Closed Task 2 audit additions

Task 2 also owns these previously missed full-gate consumers:

```text
src/Namotion.Interceptor.OpcUa.Tests/Client/OpcUaSubjectLoaderTests.cs
src/Namotion.Interceptor.Hosting.Tests/HostedServiceHandlerTests.cs
src/Namotion.Interceptor.Tracking.Tests/Change/PerPropertySubscriptionLifecycleTests.cs
src/Namotion.Interceptor.Tracking.Tests/Transactions/SubjectTransactionTests.cs
src/Namotion.Interceptor.Tracking.Tests/Change/FallbackContextInvalidationTests.cs
src/Namotion.Interceptor.Generator.Tests/BaseClassInterceptionBehaviorTests.cs
src/Namotion.Interceptor.Tests/Context/ContextConcurrencyFuzzTests.cs
src/Namotion.Interceptor.Tests/InterceptorTests.WhenAddingAndRemovingContext_ThenInterceptorsAreCalledInTheRightOrder.verified.txt
src/Namotion.Interceptor.Tracking.Tests/Lifecycle/PropertyReferenceSetTests.cs
src/Namotion.Interceptor.Dynamic.Tests/DynamicSubjectTests.cs
src/Namotion.Interceptor.Dynamic.Tests/DynamicSubjectTests.WhenInterceptingDynamicSubject_ThenTheyAreCalled.verified.txt
src/Namotion.Interceptor.Registry.Tests/GraphBehavior/CycleTests.cs
src/Namotion.Interceptor.Registry.Tests/GraphBehavior/CycleTests.WhenBreakingCycle_ThenBothDetach.verified.txt
src/Namotion.Interceptor.Registry.Tests/GraphBehavior/CycleTests.WhenBreakingCycleBetweenExplicitRoots_ThenBothStayAttached.verified.txt
src/Namotion.Interceptor.Registry.Tests/GraphBehavior/CycleTests.WhenInternalCycleOrphaned_ThenCycleStaysAttached_Limitation.verified.txt
src/Namotion.Interceptor.Registry.Tests/GraphBehavior/CycleTests.WhenInternalCycleOrphaned_ThenWholeComponentDetaches.verified.txt
```

The exact complete phase manifest is binding in `.superpowers/sdd/2026-08-19-explicit-subject-ownership/task-2-internal-map.md`.

---

### Task 1: Canonical Structural Property Classification (Complete)

**Commits:** `cf39188b` and `0ff54c7e`

**Produces:**

```csharp
public static class SubjectPropertyTypeClassifier
{
    public static bool CanContainSubjects(Type type);
    public static bool IsSubjectReferenceType(Type type);
    public static bool IsSubjectCollectionType(Type type);
    public static bool IsSubjectDictionaryType(Type type);
}

public bool SubjectPropertyMetadata.CanContainSubjects { get; }
```

- [x] Core/runtime classifier and parity tests.
- [x] Generator symbol classifier and retained boolean.
- [x] Tracking forwarders with no second cache.
- [x] Dynamic property-classification correction.
- [x] Public API snapshot and focused suites.

Do not reopen classifier behavior during Task 2. Consume the retained flag.

### Task 2: Atomic Global Ownership and Topology Protocol

#### Preflight (Complete)

At exact HEAD `0ff54c7e`, the existing Core callback test passed 1/1 and Tracking order tests passed 33/33. The Core fallback lifecycle oracle is:

```text
a: Attached
b: Attached
a: Detached
b: Detached
```

Only the approved composition-only fallback cutover changes that oracle. Other order/visibility facts remain stable.

#### Carried-forward metadata semantic-oracle debt correction before Task 2 closure

The full Core gate exposed a Task 1 semantic snapshot omission at `src/Namotion.Interceptor.Tests/InterceptorTests.WhenReadingMetadata_ThenItShouldBeCorrect.verified.txt`. Before the Task 2 atomic commit, inspect the received difference and accept only the five `CanContainSubjects: false` lines produced by Task 1's `SubjectPropertyMetadata.CanContainSubjects`. Run:

```bash
dotnet test src/Namotion.Interceptor.Tests/Namotion.Interceptor.Tests.csproj --filter "FullyQualifiedName~InterceptorTests.WhenReadingMetadata_ThenItShouldBeCorrect" --no-restore
git diff --check -- src/Namotion.Interceptor.Tests/InterceptorTests.WhenReadingMetadata_ThenItShouldBeCorrect.verified.txt
git add src/Namotion.Interceptor.Tests/InterceptorTests.WhenReadingMetadata_ThenItShouldBeCorrect.verified.txt
git diff --cached --check
git diff --cached --name-only
git commit -m "Correct structural metadata oracle"
```

The focused test must pass and the cached name list must contain only that exact oracle. This distinct correction commit closes carried-forward Task 1 evidence debt before Task 2 closes. The path is not part of the Task 2 128-path union, and `GetOwnershipValue` is not the cause of its five boolean lines.

#### Subphase A: Original final RED set plus implementation-discovered Registry RED

**Files:** Exact atomic 128-path Task 2 manifest in the internal map, including every Core/Tracking/Registry/Generator/Dynamic test, test double, all four changed Public API oracles, generated Verify snapshots, both changed callback oracles, the five CycleTests source/oracle paths, Hosting and Connectors production/tests, the WebSocket SampleClient, every SyncRoot/inheritance consumer, and the OPC UA compile consumer.

**Interfaces produced:**

```csharp
public interface ILifecycleInterceptor : IWriteInterceptor
{
    void PrepareSubjectAttachment(
        IInterceptorSubject subject,
        IInterceptorSubjectContext context,
        ref SubjectOwnershipOperation operation);
    void PrepareSubjectDetachment(
        IInterceptorSubject subject,
        IInterceptorSubjectContext context,
        ref SubjectOwnershipOperation operation);
    void PrepareSubjectPropertyWrite<TProperty>(
        ref SubjectOwnershipWriteContext<TProperty> context,
        ref SubjectOwnershipOperation operation);
    void PrepareSubjectMetadataAdditionBatch(
        ref SubjectMetadataAdditionBatchContext context,
        ref SubjectOwnershipOperation operation);
    void ReconcileSubjectOwnership(ref SubjectOwnershipOperation operation);
    void ReleaseSubjectOwnership(ref SubjectOwnershipOperation operation);
}

public readonly ref struct SubjectMetadataAddition
{
    public SubjectPropertyMetadata Metadata { get; }
    public object? CurrentValue { get; }
}

public readonly ref struct SubjectMetadataAdditionBatchContext
{
    public IInterceptorSubject Subject { get; }
    public int Count { get; }
    public SubjectMetadataAddition GetAddition(int index);
}

public readonly ref struct SubjectOwnershipOperation
{
    public IInterceptorSubjectContext OwnershipDomain { get; }
    public SubjectOwnershipView GetView(IInterceptorSubject subject);
    public void ReserveParentAddition(PropertyReference property, IInterceptorSubject child);
    public void ReserveParentRemoval(PropertyReference property, IInterceptorSubject child);
    public void ReserveFinalRelease(IInterceptorSubject subject);
    public void SelectActiveParent(IInterceptorSubject subject, PropertyReference? parent);
    public ImmutableArray<TService> GetCurrentServices<TService>(IInterceptorSubject subject);
    public ImmutableArray<TService> GetProspectiveServices<TService>(IInterceptorSubject subject);
    public bool TryPublishCommittedRoute(in SubjectOwnershipView committedView);
}

public readonly ref struct SubjectMetadataCommitRegistration
{
    public static SubjectMetadataCommitRegistration Register(
        IInterceptorSubject subject,
        Action onCommitted);
    public void Dispose();
}
```

- [ ] **Step 1: Run executable existing-surface REDs, then author the complete final test set before production edits**

The complete final test inventory includes these exact rows, but the execution order below is binding. Add only the rows that compile against the existing surface before the first command block; add compile-dependent rows after that block and before Step 2:

```text
SubjectAttachmentTests.WhenAttachingToPlainContext_ThenAttachContextIsReported
SubjectAttachmentTests.WhenPlainContextRootHasPrepopulatedChild_ThenChildIsNotImplicitlyOwned
SubjectAttachmentTests.WhenAttachingTwice_ThenPersistentFailurePrecedesMutation
SubjectAttachmentTests.WhenDetachingMissingOrWrongContext_ThenPersistentFailurePrecedesMutation
SubjectAttachmentTests.WhenAttachingToUnsupportedContext_ThenArgumentFailurePrecedesResolution
ContextAuthorityActivationTests.WhenRepeatedPathsResolveSameCoordinator_ThenActivationSucceeds
ContextAuthorityActivationTests.WhenDistinctCoordinatorsResolve_ThenPublicationRejects
ContextAuthorityActivationTests.WhenLegalActiveMutationPreservesCoordinator_ThenFutureArraysChange
ContextAuthorityActivationTests.WhenMutationChangesCapturedCoordinator_ThenStateDoesNotPublish
ContextAuthorityActivationTests.WhenTryAddFactoryInvokesOwnership_ThenFactoryRunsOnceAndNestedOperationCompletes
ContextAuthorityActivationTests.WhenTryAddCallbacksMutateDifferentContext_ThenEachRunsOnceAndNestedWorkCompletes
ContextAuthorityActivationTests.WhenExternalReverseTryAddContends_ThenItWaitsBeforeTargetContextLock
ContextAuthorityActivationTests.WhenFallbackAndCoordinatorMutationsContend_ThenResultMatchesSerialOrder
ContextOwnershipRouteTests.WhenDirectRouteMutatorsContend_ThenSelfGatingPreservesExactWinner
SubjectOwnershipProviderContractTests.WhenRoutePublicationIsInspected_ThenOnlyExactCommittedViewPhaseCapabilityExists
SubjectOwnershipProviderContractTests.WhenRoutePublicationUsesWrongPhaseSubjectGenerationOrDescriptor_ThenPersistentFailureOccurs
SubjectOwnershipProviderContractTests.WhenOlderCommittedViewPublishesAfterLaterGeneration_ThenItReturnsStaleWithoutAllocation
SubjectOwnershipProviderContractTests.WhenOperationCompletesCancelsOrThrows_ThenReleaseRunsExactlyOnce
SubjectOwnershipOperationTests.WhenPublicFacadeIsInspected_ThenOnlyReservationViewSelectionRouteAndReleaseCapabilitiesExist
SubjectOwnershipOperationTests.WhenMembershipsAreRead_ThenCountAndIndexEnumerationDoesNotBox
SubjectOwnershipStateTests.WhenFirstParentIsReserved_ThenNoOverflowListIsAllocated
SubjectOwnershipStateTests.WhenFinalRelationshipReleases_ThenExecutorDropsOwnershipState
SubjectPrivateLockTests.WhenPublicSubjectApiIsInspected_ThenSyncRootIsAbsent
SubjectPrivateLockTests.WhenZeroInterceptorReadRuns_ThenDirectDelegateRemainsLockFree
SubjectPrivateLockTests.WhenInterceptedReadOrScalarWriteRuns_ThenOnlyExecutorMonitorIsUsed
LifecycleArrayPinningTests.WhenHandlerMutatesCompositionAndNests_ThenOuterKeepsOldArraysAndNestedUsesNewArrays
LifecycleArrayPinningTests.WhenSameExecutorHandlerMutatesBeforeRoutePublication_ThenFreshRouteMergesMutation
ContextOwnershipRouteTests.WhenRoutePublishes_ThenGateSpansRegisterPublishUnregisterAndCompleteInvalidation
ContextAuthorityActivationTests.WhenCustomLifecycleProviderIsOnlyResolvedInstance_ThenItIsCanonicalAndReceivesProtocol
StructuralContinuationTests.WhenRepeaterImmediatelyUpstreamCallsNextZeroOneOrTwoTimes_ThenCoordinatorReconcilesPerInvocation
StructuralContinuationTests.WhenRepeaterImmediatelyDownstreamCallsNextZeroOneOrTwoTimes_ThenCoordinatorReconcilesFinalValueOnce
StructuralContinuationTests.WhenDownstreamSecondAttemptIsIncompatible_ThenFirstStoreReconcilesOnceBeforeRethrow
StructuralContinuationTests.WhenDownstreamThrowsAfterSecondStore_ThenFinalStoreReconcilesOnceBeforeOriginalRethrow
StructuralOwnershipAdmissionTests.WhenScalarPrefixAttachesOrDetachesOwnSubject_ThenSelectedChainCompletesAndFutureRouteChanges
SubjectMetadataAdditionTests.WhenUnregisteredNestedSameSubjectAdditionRunsDuringEnumeration_ThenOuterRegistryTokenCannotBeStolen
DynamicOwnershipDiscoveryReaderTests.WhenDynamicProxyIsCreated_ThenExactStorageAssociationPrecedesAddProperties
DynamicOwnershipDiscoveryReaderTests.WhenDynamicDirectReaderReadsMissingValue_ThenItMatchesMemoizedPropertyDefault
DynamicOwnershipDiscoveryReaderTests.WhenDynamicSubjectCollects_ThenNoStaticStorageAssociationRetainsIt
OwnershipDiscoveryReaderTests.WhenCustomDirectReaderContractIsInspected_ThenNoRuntimeTlsGuardIsAdded
OwnershipProviderPoolTests.WhenLargeGraphExceedsRetentionLimit_ThenTlsBuffersAreDropped
```

Author the existing-surface tests and final expected oracles first. Update the Core callback oracle to the composition-only result, update the Dynamic order oracle by removing only fallback-driven leading `a/b Attached` and trailing `a/b Detached` while preserving every read/write line and order, and update the Core, Tracking, Registry, and Connectors Public API oracles to the exact final signatures. Run the following executable existing-surface gate immediately, before compile-dependent final test files are added:

```bash
dotnet test src/Namotion.Interceptor.Tests/Namotion.Interceptor.Tests.csproj --filter "FullyQualifiedName~InterceptorTests.WhenAddingAndRemovingContext_ThenInterceptorsAreCalledInTheRightOrder" --no-restore
dotnet test src/Namotion.Interceptor.Tests/Namotion.Interceptor.Tests.csproj --filter "FullyQualifiedName~VerifyChecksTests.PublicApi" --no-restore
dotnet test src/Namotion.Interceptor.Tracking.Tests/Namotion.Interceptor.Tracking.Tests.csproj --filter "FullyQualifiedName~VerifyChecksTests.PublicApi" --no-restore
dotnet test src/Namotion.Interceptor.Registry.Tests/Namotion.Interceptor.Registry.Tests.csproj --filter "FullyQualifiedName~VerifyChecksTests.PublicApi" --no-restore
dotnet test src/Namotion.Interceptor.Connectors.Tests/Namotion.Interceptor.Connectors.Tests.csproj --filter "FullyQualifiedName~VerifyChecksTests.PublicApi" --no-restore
dotnet test src/Namotion.Interceptor.Dynamic.Tests/Namotion.Interceptor.Dynamic.Tests.csproj --filter "FullyQualifiedName~DynamicSubjectTests.WhenInterceptingDynamicSubject_ThenTheyAreCalled" --no-restore
dotnet test src/Namotion.Interceptor.Tests/Namotion.Interceptor.Tests.csproj --filter "FullyQualifiedName~SubjectPrivateLockTests|FullyQualifiedName~ContextOwnershipRouteTests" --no-restore
dotnet test src/Namotion.Interceptor.Generator.Tests/Namotion.Interceptor.Generator.Tests.csproj --filter "FullyQualifiedName~BaseClassInterceptionBehaviorTests|FullyQualifiedName~GeneratedMemberTableTests|FullyQualifiedName~SubjectBaseDiagnosticsTests|FullyQualifiedName~SubjectBaseShapeTests|FullyQualifiedName~InterfaceDefaultPropertyTests|FullyQualifiedName~SourceGeneratorTests|FullyQualifiedName~VirtualPartialTests" --no-restore
```

Expected: each changed oracle or existing-surface behavior fails for its intended old callback/API/lock/route/generated-shape semantics. Record method execution and exact mismatch for each command. If a row unexpectedly names a final absent type, move it to the compilation-RED set below and record that it did not execute; do not add a production stub to recover a semantic RED.

Then author every remaining structural, recursive-membership, metadata, Registry, callback-order, pinning, constructor, Dynamic, Hosting, Connectors, OPC UA, Public API, and Verify test listed in Subphases B/C and in the map's 128-path closed union. No production or test-support implementation starts yet. Source-review every final test method and oracle against that union, including the exact `SubjectPrivateLockTests`, `ContextOwnershipRouteTests`, `StructuralInterceptorPinningTests`, Core, Tracking, Registry, and Connectors `VerifyChecksTests.PublicApi`, Core callback oracle, Dynamic callback oracle, and Generator class filters. The callback schedule uses `factoryEntered`, `externalAttemptingEntry`, `allowFactory`, `nestedCompleted`, and `externalCallbackEntered`. The external task signals immediately before its public call and waits outside its context monitor. Assert predicate/factory counts are exactly one and the external callback has not entered before release.

The existing full Registry oracle later exposed a semantic RED after Task 2 production work had begun: the rooted planner releases the unanchored `A -> B <-> C` component and strict constructors keep both configured subjects as explicit roots. Record that discovery timing honestly. Rename `WhenBreakingCycle_ThenBothDetach` to `WhenBreakingCycleBetweenExplicitRoots_ThenBothStayAttached` and `WhenInternalCycleOrphaned_ThenCycleStaysAttached_Limitation` to `WhenInternalCycleOrphaned_ThenWholeComponentDetaches`, delete both legacy oracles, and create both final-semantic oracles. Do not claim these renamed tests preceded production. Before accepting either oracle, review callback order and reference-count values against the ordinary single-context contract. The orphan oracle must prove `A`, `B`, and `C` all release, not merely copy the current received text.

- [ ] **Step 2: Run the complete atomic RED gate**

```bash
dotnet test src/Namotion.Interceptor.Tests/Namotion.Interceptor.Tests.csproj --filter "FullyQualifiedName~SubjectAttachmentTests|FullyQualifiedName~ContextAuthorityActivationTests|FullyQualifiedName~SubjectOwnershipProviderContractTests|FullyQualifiedName~SubjectOwnershipOperationTests|FullyQualifiedName~SubjectOwnershipStateTests|FullyQualifiedName~SubjectPrivateLockTests|FullyQualifiedName~ContextOwnershipRouteTests|FullyQualifiedName~ContextServiceWalkOrderTests|FullyQualifiedName~ContextDeepGraphTests|FullyQualifiedName~ContextConcurrencyFuzzTests|FullyQualifiedName~CommitRevisionTests" --no-restore
dotnet test src/Namotion.Interceptor.Tests/Namotion.Interceptor.Tests.csproj --filter "FullyQualifiedName~StructuralOwnershipAdmissionTests|FullyQualifiedName~StructuralOwnershipConcurrencyTests|FullyQualifiedName~StructuralInterceptorPinningTests|FullyQualifiedName~StructuralContinuationTests|FullyQualifiedName~ContextFunctionCacheTests|FullyQualifiedName~InterceptorTests.WhenAddingAndRemovingContext_ThenInterceptorsAreCalledInTheRightOrder|FullyQualifiedName~VerifyChecksTests.PublicApi" --no-restore
dotnet test src/Namotion.Interceptor.Tracking.Tests/Namotion.Interceptor.Tracking.Tests.csproj --filter "FullyQualifiedName~OwnershipMembershipTests|FullyQualifiedName~SubjectMetadataAdditionTests|FullyQualifiedName~OwnershipDiscoveryReaderTests|FullyQualifiedName~OwnershipProviderPoolTests|FullyQualifiedName~LifecycleCallbackOrderTests|FullyQualifiedName~LifecycleArrayPinningTests|FullyQualifiedName~LifecycleEventsTests|FullyQualifiedName~FallbackCompositionLifecycleTests|FullyQualifiedName~ConcurrentWriteLifecycleTests|FullyQualifiedName~RecursiveAttachTests|FullyQualifiedName~LifecycleInterceptorTests|FullyQualifiedName~ContextInheritanceHandlerTests|FullyQualifiedName~PropertyReferenceSetTests" --no-restore
dotnet test src/Namotion.Interceptor.Tracking.Tests/Namotion.Interceptor.Tracking.Tests.csproj --filter "FullyQualifiedName~DerivedPropertyChangeHandlerTests|FullyQualifiedName~DerivedPropertyCleanupTests|FullyQualifiedName~DerivedPropertyConcurrencyTests|FullyQualifiedName~FallbackContextInvalidationTests|FullyQualifiedName~PerPropertySubscriptionLifecycleTests|FullyQualifiedName~ParentAccessDuringLifecycleTests|FullyQualifiedName~SubjectTransactionTests|FullyQualifiedName~WriteTimestampTests|FullyQualifiedName~SubjectPropertyTypeExtensionsTests|FullyQualifiedName~VerifyChecksTests.PublicApi" --no-restore
dotnet test src/Namotion.Interceptor.Registry.Tests/Namotion.Interceptor.Registry.Tests.csproj --filter "FullyQualifiedName~DynamicPropertyLifecycleTests|FullyQualifiedName~ConcurrentStructuralWriteLeakTests|FullyQualifiedName~RegistryHandlerOrderTests|FullyQualifiedName~VerifyChecksTests.PublicApi" --no-restore
dotnet test src/Namotion.Interceptor.Generator.Tests/Namotion.Interceptor.Generator.Tests.csproj --filter "FullyQualifiedName~StructuralSetterShapeTests|FullyQualifiedName~BaseClassInterceptionBehaviorTests|FullyQualifiedName~GeneratedMemberTableTests|FullyQualifiedName~SubjectBaseDiagnosticsTests|FullyQualifiedName~SubjectBaseShapeTests|FullyQualifiedName~InterfaceDefaultPropertyTests|FullyQualifiedName~SourceGeneratorTests|FullyQualifiedName~VirtualPartialTests" --no-restore
dotnet test src/Namotion.Interceptor.Dynamic.Tests/Namotion.Interceptor.Dynamic.Tests.csproj --filter "FullyQualifiedName~DynamicOwnershipAdmissionTests|FullyQualifiedName~DynamicSubjectTests|FullyQualifiedName~DynamicOwnershipDiscoveryReaderTests" --no-restore
dotnet test src/Namotion.Interceptor.Hosting.Tests/Namotion.Interceptor.Hosting.Tests.csproj --filter "FullyQualifiedName~HostedServiceHandlerTests" --no-restore
dotnet test src/Namotion.Interceptor.Connectors.Tests/Namotion.Interceptor.Connectors.Tests.csproj --filter "FullyQualifiedName~SourceMonitorTests|FullyQualifiedName~DefaultSubjectFactoryTests|FullyQualifiedName~SourceWaitTests|FullyQualifiedName~VerifyChecksTests.PublicApi" --no-restore
dotnet test src/Namotion.Interceptor.OpcUa.Tests/Namotion.Interceptor.OpcUa.Tests.csproj --filter "FullyQualifiedName~OpcUaSubjectLoaderTests" --no-restore
```

Expected: affected projects that reference the absent final provider/context/operation types or signatures fail compilation. Save each command and compiler diagnostic at project granularity. A compilation failure means no test in that project is claimed to have executed, even though the filter enumerates every final class/oracle that must compile at GREEN. Any command that still compiles must execute and fail semantically for its intended old behavior. Before production edits, review every compile-blocked test and oracle in source against the exact manifest and record the review checklist. No temporary facade, compiling stub, transitional provider, intermediate production cutover, false intermediate GREEN, commit, or handoff is permitted. The one complete final GREEN gate in Step 20 is the semantic proof for compile-blocked rows.

#### Subphase B: One combined implementation, no intermediate GREEN

- [ ] **Step 3: Add stack-only provider API and strict extensions**

Implement the exact spec signatures. `SubjectOwnershipOperation` exposes `OwnershipDomain`, `GetView`, count/index membership, reservation methods, `SelectActiveParent`, `ReserveFinalRelease`, current/prospective generic array access, and `TryPublishCommittedRoute(in SubjectOwnershipView)`. The route method accepts only Core's exact committed subject/generation/expected-descriptor view during the matching reconcile phase, succeeds at most once, returns false for a stale older generation, and persistently rejects provider misuse. It exposes no provider ledger commit, retry, finalization, object state, boxed enumeration, or runtime catch-all payload. Add the batch metadata types and `SubjectMetadataCommitRegistration` surface exactly as specified. Tracking keeps a pooled TLS LIFO frame; Core calls `ReleaseSubjectOwnership` exactly once from every finalization path.

Add strict extensions:

```csharp
public static void AttachToContext(
    this IInterceptorSubject subject,
    IInterceptorSubjectContext context);
public static void DetachFromContext(
    this IInterceptorSubject subject,
    IInterceptorSubjectContext context);
public static bool TryGetAttachContext(
    this IInterceptorSubject subject,
    out IInterceptorSubjectContext? context);
```

Change the interface declaration, advanced custom-provider test implementation, and test doubles to the final signatures. Do not add a compiling stub or transitional built-in provider here: `LifecycleInterceptor` receives its only implementation of the six methods in Step 17, and the atomic worktree may remain uncompilable until then. The exact single `ILifecycleInterceptor` identity resolved in a domain is canonical regardless of concrete type; `WithLifecycle()` only installs the standard implementation. Delete old subject-only lifecycle callbacks and executor fallback lifecycle overrides as part of the combined implementation. Replace/delete the obsolete fallback callback Verify oracle.

- [ ] **Step 4: Implement `OwnershipTopologyGate` and gate-first context mutation**

In `SubjectOwnershipCoordinator`, add the single static gate and exact entry scopes named in the internal map. Refactor all context mutators to enter it before `_mutationLock`. Make `TryChangeOwnershipRoute` self-enter before its context lock and retain the topology gate through reverse register, publication, conditional unregister, and complete invalidation while releasing `_mutationLock` before invalidation. Preserve `TryAddService` predicate/factory ordering, same-context and cross-context same-thread reentrancy, exact invocation counts, and state reread. Compute raw prospective lifecycle identity and affected active reverse domains before immutable publication. Permit exact-identity-preserving mutation and reject identity change. While an initiating structural prefix is active, persistently reject own attach/detach; while route-free, also reject own coordinator replacement. External topology callers wait and then re-evaluate. Scalar prefixes receive no TLS marker, stay outside the topology gate, may synchronously attach/detach, complete their already selected scalar chain, and affect future operations.

Pin the exact immutable interceptor array after gate entry. During preparation, pin one prospective lifecycle array plus logical coordinator insertion index and one property-handler array per affected subject using the current immutable executor state plus a route overlay, without creating routed state. Compute the insertion index from the exact coordinator's rank in the complete ordered gathered service set, so an advanced canonical provider need not implement Tracking's `ILifecycleHandler`; the built-in keeps the former `ContextInheritanceHandler` rank. Current detach arrays come from the old immutable state. A legal callback mutation changes future and nested later calls only; the outer operation retains its arrays. At the exact coordinator phase, reread then-current state and materialize/merge the fresh route after the exact stale check.

- [ ] **Step 5: Implement activation, explicit ledger, coordinator binding, and stable errors**

Implement activation states `Absent -> Preparing -> Active -> Releasing -> Absent`, lazy binding, failed-first cleanup, exact lease count, and same-instance repeated paths. Implement nullable executor ownership state with inline first parent. Strict arguments and semantic conflicts throw only the documented persistent exception types.

Remove `SyncRoot` from `IInterceptorSubject`, generator/Dynamic/manual shapes, collision and NI0014 tests, and Core Public API. Move only intercepted read and scalar write terminal locking into one private executor monitor; preserve `ReadInterceptorFactory`'s zero-interceptor direct delegate branch exactly. Structural/root/metadata work takes global gate then one executor monitor. Add XML requiring custom read delegates to be synchronous, nonblocking, topology-free direct storage reads. Add static normal-path tests proving generated/Dynamic compliance and the absence of a scalar/read TLS guard; do not execute an intentional inversion or promise a runtime diagnostic for advanced caller misuse. Rewrite the existing atomic-root timestamp test as quiescent consistency without an externally held monitor.

- [ ] **Step 6: Migrate atomic semantic consumers**

- `HostedServiceHandlerTests`: detach with `DetachFromContext`.
- `PerPropertySubscriptionLifecycleTests`: use one lifecycle authority for aggregation; rows intentionally combining two assert persistent incompatibility.
- `SubjectTransactionTests`: use one lifecycle authority and transaction-only composition.
- `FallbackContextInvalidationTests`: separate legal nonunique invalidation from first-coordinator rejection.
- `OpcUaSubjectLoaderTests`: remove the direct old provider call.
- `BaseClassInterceptionBehaviorTests`: characterize strict constructor attachment.
- `ContextConcurrencyFuzzTests`: model composition-only fallback and serial context outcomes.

- [ ] **Step 7: Keep the atomic implementation open**

Do not require compilation or GREEN here. Inspect the working diff only for signature drift, temporary providers, alternate monitors, scalar-path branches, and unlisted snapshots. The final Core, Tracking, Registry, Generator, and Dynamic implementations are intentionally incomplete until Subphase C. Any received Verify file is held for the final gate; accept only the strict APIs/provider surface and the approved Dynamic fallback callback removal.

#### Structural RED inventory already authored and run in Step 1 and Step 2

**Files:** Core ownership coordinator/batch/state, executor/context/write chain/terminal, generator/Dynamic setter/constructor emission, Registry/non-generic writers, structural tests and snapshots from the exact map.

**Reference inventory: global structural scheduling RED tests belong to Step 1**

Add exact tests:

```text
StructuralOwnershipConcurrencyTests.WhenExternalStructuralCallsContend_ThenBothCompleteInSerialOrder
StructuralOwnershipConcurrencyTests.WhenCallbacksNestAcrossDomains_ThenNestedWorkRunsInline
StructuralOwnershipConcurrencyTests.WhenSimultaneousCrossDomainCallbacksRun_ThenExternalCallerWaitsAndReevaluates
StructuralOwnershipAdmissionTests.WhenReservedDescendantWrites_ThenOuterCancelsAndRestartsFromFinalValue
StructuralOwnershipAdmissionTests.WhenOuterLaterRejects_ThenIndependentNestedWriteSurvivesWithoutOwnership
StructuralOwnershipAdmissionTests.WhenInitiatingRevisionChanges_ThenTerminalRestartsAndWriterRunsOnce
StructuralOwnershipAdmissionTests.WhenRouteFreePrefixChangesOwnCoordinator_ThenPersistentFailurePrecedesPublication
StructuralOwnershipAdmissionTests.WhenRouteFreePrefixAttachesOrDetachesOwnSubject_ThenImmediateRetryFailsIdentically
StructuralOwnershipAdmissionTests.WhenOwnedPrefixDetachesOwnSubject_ThenImmediateRetryFailsIdentically
StructuralOwnershipAdmissionTests.WhenExternalAttachOrDetachWaitsForPrefix_ThenItCompletesAfterRelease
StructuralOwnershipAdmissionTests.WhenTraversalMutationCancelsBeyondLegacyLimitThenStabilizes_ThenOriginalCallSucceeds
StructuralOwnershipAdmissionTests.WhenApplicationTraversalNeverConverges_ThenNoFiniteLibraryFailurePathExists
StructuralOwnershipAdmissionTests.WhenDownstreamThrowsAfterCommit_ThenReconciliationPrecedesOriginalRethrow
StructuralOwnershipAdmissionTests.WhenDownstreamThrowsBeforeTerminal_ThenNoOperationIsExposed
StructuralContinuationTests.WhenRepeaterImmediatelyUpstreamCallsNextZeroOneOrTwoTimes_ThenCoordinatorReconcilesPerInvocation
StructuralContinuationTests.WhenRepeaterImmediatelyDownstreamCallsNextZeroOneOrTwoTimes_ThenCoordinatorReconcilesFinalValueOnce
StructuralContinuationTests.WhenDownstreamSecondAttemptIsIncompatible_ThenFirstStoreReconcilesOnceBeforeRethrow
StructuralContinuationTests.WhenDownstreamThrowsAfterSecondStore_ThenFinalStoreReconcilesOnceBeforeOriginalRethrow
StructuralOwnershipAdmissionTests.WhenScalarPrefixAttachesOrDetachesOwnSubject_ThenSelectedChainCompletesAndFutureRouteChanges
StructuralInterceptorPinningTests.WhenPrefixMutatesCompositionAndNests_ThenOuterUsesPinnedChainAndNestedUsesNewChain
StructuralSetterShapeTests.WhenGeneratedContextConstructorRuns_ThenItCreatesStrictExplicitAttachment
StructuralSetterShapeTests.WhenGeneratedParameterlessConstructorRuns_ThenItRemainsRouteFreeUntilParentCommit
DynamicOwnershipAdmissionTests.WhenDynamicContextConstructorRuns_ThenItCreatesStrictExplicitAttachment
DynamicOwnershipAdmissionTests.WhenDynamicParameterlessChildIsPublished_ThenItInheritsAutomatically
DynamicOwnershipAdmissionTests.WhenPlainContextStructuralChildIsAssigned_ThenChildIsNotImplicitlyOwned
```

Use `firstEntered`, `nestedEntered`, `externalWaiting`, `allowNested`, `terminalCommitted`, and `reconciliationCompleted` events. Assertions occur while the losing task is still blocked, never after a timing delay.

**Reference gate:** every row must have been authored and source-reviewed in Steps 1-2. A row that compiled must have saved semantic RED. A compile-blocked row has only the saved project-level compiler RED until the complete final GREEN gate and must not be described as executed.

Do not rerun an intermediate GREEN gate here. Do not add a bridge merely to execute a compile-blocked row.

- [ ] **Step 10: Enter the topology turn before structural action selection**

Preserve the existing scalar overload and add the structural overload:

```csharp
bool SetPropertyValue<TProperty>(
    string propertyName,
    TProperty newValue,
    TProperty currentValue,
    Action<IInterceptorSubject, TProperty> writeValue);

bool SetPropertyValue<TProperty>(
    string propertyName,
    TProperty newValue,
    TProperty currentValue,
    Action<IInterceptorSubject, TProperty> writeValue,
    bool canContainSubjects);
```

Generated scalar properties keep the four-argument call and existing helper body. Generated structural properties call the five-argument overload with retained `true`; it enters the global gate before action selection and pins the chain. If no exact lifecycle coordinator owns the subject, it performs the ordinary intercepted write and creates no child membership/route/callback. With one exact coordinator, create one accumulator per coordinator invocation. A repeater upstream creates separate invocations and reconciles each; a repeater downstream performs ordered terminal attempts inside one accumulator and reconciles only the final successful value once when the coordinator regains control. Dynamic/Registry factories choose their delegate once from metadata when possible.

- [ ] **Step 11: Implement Core-owned batch commit and terminal-only restart**

Use batch states `Preparing`, `Committed`, `Reconciling`, `Finalized`, `Cancelled`. Rent/push a pooled `StructuralPrefixScope` before the structural chain, store the active-prefix identity there, and return it in `finally`; add no scope or marker to scalar execution. When the exact coordinator node is entered, rent/push a child `StructuralCoordinatorInvocation` accumulator plus Tracking frame whose `PreviousOperation` points to that scope. Its inline terminal-attempt status and first record hold ordered attempt/revision/generation and final committed delta; it does not allocate a queue. The accumulator remains `Preparing` while downstream runs. Each downstream terminal independently changes its inline attempt to `Preparing`, reserves/validates, reserves mutable capacity, rechecks initiating revision under the private executor monitor, calls its direct writer once, stamps revision, commits its Core ledger, clears attempt pending markers, marks the attempt `Committed`, and replaces the accumulator's final delta while clearing superseded tentative buffers. When the coordinator regains control, zero stores finalize without reconciliation and one or more stores transition the accumulator once to `Committed` for coalesced reconciliation. Upstream repetition enters and releases a separate child accumulator each time. Validators find the initiating structural scope through the batch link.

When a nested structural call sees its subject in a `Preparing` terminal attempt, call `CancelCurrentAttempt()`, clear all of that attempt's tentative entries, and run nested work from committed/route-free state. A previous successful terminal in the same downstream coordinator accumulator is already committed and remains its fallback final delta. Preparation and terminal loops inspect the cancelled attempt status after every reentrant application call and return control to the outer Core boundary without throwing. The boundary clears/reuses the inline attempt and repeats discovery from final values without replaying prefix, an already executed writer, or callback. A whole attach/detach/metadata batch still uses `Cancel()` and never transitions from `Cancelled` back to `Preparing`.

Restart library-controlled cancellation, stale revision, and stale snapshots without a numeric bound. The stabilizing RED row performs at least 256 deterministic traversal mutations and must succeed in the original call. The permanent-nonconvergence row statically proves no counter or exception branch exists; it never runs an intentionally hanging synchronous call. Permanently changing application traversal is an unsupported liveness condition.

- [ ] **Step 12: Implement exact coordinator-node exception deferral**

Record the exact coordinator node in `WriteInterceptorChain<TProperty>`. Its continuation captures a downstream exception only if the accumulator contains a successful terminal store. When downstream returns or throws, expose only the accumulator's final coalesced committed operation and reconcile once; zero stores reconcile zero times. A permanently incompatible later attempt throws before its store, then unwind reconciles the prior successful store. A throw after the second successful store reconciles that final value. Finalize Core and rethrow the original with `ExceptionDispatchInfo`. Preterminal exceptions with no store cancel with no operation exposure. Original downstream exception wins over reconciliation, which wins over finalization.

- [ ] **Step 13: Emit strict constructors and structural flags**

Generated and Dynamic context constructors call `AttachToContext`. Parameterless constructors remain route-free. They inherit only when an exact lifecycle coordinator commits a parent membership. Generated structural and Dynamic/Registry/PropertyReference non-generic writes consume retained `CanContainSubjects`; generated scalar properties keep the original overload. Update snapshots and assert scalar source/IL retains no topology argument or branch.

- [ ] **Step 14: Perform the no-intermediate-gate structural review**

Do not run or require GREEN. Inspect generated scalar snapshots and terminal diff while continuing directly into Subphase C. Confirm warmed stable-topology structural execution has no task, closure, ordered callback queue, or per-call allocation. Add static assertions that stale expected descriptor, stale generation, incompatible domain, and stale initiating revision exit before construction of a replacement route/state. A successful route mutation separately records every allowed PR #474 reverse snapshot, service-walk/cache/array, route/state, invalidation generation, and first-use retained-capacity object.

#### Subphase C: Complete the combined provider, metadata, Registry, Generator, and Dynamic implementation

**Files:** Tracking traversal/reconciliation/provider, lifecycle/parent/registry/connector/hosting handlers, AddProperties Generator/Dynamic/Core entries, `DynamicSubjectFactory.cs`, Dynamic direct-storage tests and snapshots, inheritance deletion/migration files, and all exact atomic tests/snapshots from the map.

**Reference inventory: recursive membership and `AddProperties` RED tests belong to Step 1**

Add exact tests:

```text
OwnershipMembershipTests.WhenPropertyAddsChild_ThenMembershipAndBaselineCommitBeforeCallbacks
OwnershipMembershipTests.WhenCyclesAndSharedDagAreAttached_ThenEachMembershipIsCountedOncePerProperty
OwnershipMembershipTests.WhenRepeatedCollectionReferenceOccurs_ThenCoreCountIsOneAndIndicesAreRetained
OwnershipMembershipTests.WhenCommittedNestedGenerationWins_ThenStaleCallbackTailCannotClearIt
OwnershipMembershipTests.WhenFinalAnchorRemovalOrphansCycle_ThenEveryMembershipAndRouteClearsAndEachSubjectDetachesOnce
SubjectMetadataAdditionTests.WhenScalarMetadataIsAdded_ThenNoLifecycleWorkRuns
SubjectMetadataAdditionTests.WhenInputEnumerableIsObserved_ThenItIsMaterializedExactlyOnce
SubjectMetadataAdditionTests.WhenTwoStructuralMetadataEntriesAreAdded_ThenOneAtomicBatchReconciles
SubjectMetadataAdditionTests.WhenMixedScalarAndStructuralMetadataIsAdded_ThenOneAtomicDictionaryPublishes
SubjectMetadataAdditionTests.WhenSecondOwnershipReaderThrows_ThenNoMetadataOrOwnershipPublishes
SubjectMetadataAdditionTests.WhenEnumerableReentersBetweenEntries_ThenOuterBatchRevalidatesAtomically
SubjectMetadataAdditionTests.WhenOwnershipReaderIsMissing_ThenNoInitialEdgeAndFutureSetterIsTracked
SubjectMetadataAdditionTests.WhenPlainContextOwnsSubject_ThenStructuralMetadataDoesNoLifecycleWork
SubjectMetadataAdditionTests.WhenDuplicateNameIsAdded_ThenExistingBehaviorAndStateArePreserved
SubjectMetadataAdditionTests.WhenDerivedBaseContextThenAddPropertiesRuns_ThenCurrentChildrenAttachOnce
OwnershipDiscoveryReaderTests.WhenGeneratedPartialPropertyIsDiscovered_ThenBackingFieldReaderBypassesReadInterceptors
OwnershipDiscoveryReaderTests.WhenComputedPropertyDependsOnConcurrentScalarState_ThenItCannotBecomeAutomaticEdge
OwnershipDiscoveryReaderTests.WhenDynamicMetadataSuppliesDirectReader_ThenCurrentValueReconciles
DynamicOwnershipDiscoveryReaderTests.WhenDynamicProxyIsCreated_ThenExactStorageAssociationPrecedesAddProperties
DynamicOwnershipDiscoveryReaderTests.WhenDynamicDirectReaderReadsMissingValue_ThenItMatchesMemoizedPropertyDefault
DynamicOwnershipDiscoveryReaderTests.WhenDynamicSubjectCollects_ThenNoStaticStorageAssociationRetainsIt
OwnershipDiscoveryReaderTests.WhenCustomDirectReaderContractIsInspected_ThenNoRuntimeTlsGuardIsAdded
DynamicPropertyLifecycleTests.WhenExactCommitTokenCommits_ThenExactWrapperPublishesBeforeCallbacks
DynamicPropertyLifecycleTests.WhenMetadataAdmissionRejects_ThenExactTokenNeverPublishes
DynamicPropertyLifecycleTests.WhenPostCommitPropertyCallbackThrows_ThenExactWrapperRemainsBeforeOriginalRethrow
DynamicPropertyLifecycleTests.WhenNestedSameNameEqualMetadataRuns_ThenOnlyExactCommittedWrapperPublishes
DynamicPropertyLifecycleTests.WhenNestedSameNameDifferentMetadataRuns_ThenNoNameOrValueInferenceOccurs
DynamicPropertyLifecycleTests.WhenNestedAdditionCreatesDuplicateAfterOuterCancellation_ThenOuterTokenIsDiscarded
DynamicPropertyLifecycleTests.WhenUnregisteredNestedSameSubjectAdditionRunsDuringEnumeration_ThenOuterTokenCannotBeStolen
LifecycleCallbackOrderTests.WhenAttachAndDetachRun_ThenCombinedCurrentOrderAndRouteVisibilityAreExact
LifecycleArrayPinningTests.WhenCommittedHandlerAddsServiceAndNests_ThenAllOuterPhasesStayPinned
```

For `WhenFinalAnchorRemovalOrphansCycle_ThenEveryMembershipAndRouteClearsAndEachSubjectDetachesOnce`, arrange `Root -> A -> B <-> C`, remove `Root -> A`, and inspect Core ownership views plus Tracking callbacks directly. Assert that every property membership and effective route for `A`, `B`, and `C` is absent after commit and that final detach runs exactly once per subject in the ordinary callback order. This assertion is independent of the Registry Verify oracle.

**Reference gate:** every row must have been authored and source-reviewed in Steps 1-2. Save semantic RED only for a row that executed; otherwise retain the exact project-level compiler RED and defer semantic proof to Step 20.

Do not create a transitional provider or rerun an intermediate GREEN.

- [ ] **Step 17: Implement recursive provider and committed reconciliation**

Create pooled cycle-aware traversal and an internal TLS LIFO `LifecycleReconciliationState`. Preparation exists only for domains with one exact resolved `ILifecycleInterceptor`; the standard `LifecycleInterceptor` and an advanced custom implementation receive the same protocol. It reserves exact membership/route changes and invokes only `SubjectPropertyMetadata.GetOwnershipValue`, never ordinary `GetValue`. For every subject/phase it pins current arrays for detach work and prospective arrays plus exact coordinator indexes for attach work from the current state/route overlay. The prospective graph is rooted only in explicit anchors. Every subject that was reachable from the committed root set but is absent from the prospective rooted graph receives `ReserveFinalRelease`, so simple final-anchor orphan-component release is a Task 2 invariant and requires no synthetic root or disconnected attached-subject state. A downstream-repeater coordinator invocation retains one entry baseline and replaces its final committed delta after each successful terminal; it emits no per-terminal callback queue. Core commits after provider return. `ReconcileSubjectOwnership` records committed baselines before callbacks, performs complete old detach before new attach, dispatches the exact coordinator slot recursively, and checks exact generation/descriptor before each stale-tail action. At the route slot it builds the descriptor/routed state from the then-current immutable executor state after the exact stale check. Core calls `ReleaseSubjectOwnership` once after success, cancellation, restart, or exception; Tracking clears and returns the frame.

Add `SubjectPropertyMetadata.GetOwnershipValue` as `Func<IInterceptorSubject, string, Type, object?>?` and the matching final optional constructor parameter. Generator intercepted partial structural metadata emits a noncapturing direct backing-field delegate; computed/nonintercepted metadata emits null. In `DynamicSubjectFactory.cs`, keep `DynamicSubjectInterceptor._propertyValues` and the ordinary read path. Create the interceptor explicitly, create the proxy, then add the exact interceptor to `subject.Data` under one private static package-qualified GUID tuple key before metadata cache lookup and `AddProperties`. Cached metadata uses one static noncapturing helper receiving subject/name/type, resolves that subject-owned interceptor, and calls its direct `ReadProperty`. Preserve the current memoized missing-value default, allocate only the existing interceptor plus one Data node per proxy, and retain no subject in static state. Update Core Public API and every generator/Dynamic snapshot. Add XML/static normal-path tests only; no arbitrary `GetValue` traversal or runtime custom-reader diagnostic remains.

Delete `_attachedSubjects`, boxed count data, `PropertyReferenceSet`, and functional inheritance handler behavior. Move ordering attributes to `LifecycleInterceptor` and preserve ordinary visibility table.

- [ ] **Step 18: Implement globally serialized `AddProperties`**

Add this exact public method to `IInterceptorExecutor` and implement it on `InterceptorExecutor`:

```csharp
public void AddProperties(
    IEnumerable<SubjectPropertyMetadata> properties,
    Action<IInterceptorSubject, IReadOnlyDictionary<string, SubjectPropertyMetadata>> writeProperties);
```

Generated/Dynamic implementations delegate to it with a noncapturing static subject-cast writer. On Core method entry, before taking the topology gate, enumerating input, reading metadata, or running any application code, claim the exact matching pending Registry token into the local operation. Under the global gate, materialize once into an array, rebuild the prospective dictionary after any input-enumerator reentrancy, and fill parallel current-value slots only through `GetOwnershipValue(subject, metadata.Name, metadata.Type)`. A second-reader failure, duplicate, or permanent incompatibility publishes nothing. Call `PrepareSubjectMetadataAdditionBatch` once for the complete mixed batch, then perform one metadata writer swap, one Core ledger/generation commit, one reconcile, and one release. Missing ownership reader publishes metadata with no current edge. Cold value-type candidates may box.

Implement `SubjectMetadataCommitRegistration` with an internal exact token reference and thread-local nested stack. Registry allocates its exact `RegisteredSubjectProperty`, registers its exact promotion callback, then calls Core. Core's first `AddProperties` entry action claims the token before input enumeration/reentrancy or any application code and invokes it exactly once at metadata/ledger commit before reconciliation or later user callback. Precommit rejection discards/releases it. A nested Registry call claims its own pushed token; an unregistered nested same-subject call from the outer enumerable sees the outer token already claimed and cannot steal it. Postcommit user failure leaves the exact wrapper public and rethrows the original. Remove value-equality/name inference, old manual null-to-value intercepted write, separate property attach, and lifecycle-only pending lookup.

- [ ] **Step 19: Remove optional inheritance and migrate exact consumers**

Make the exact single resolved `ILifecycleInterceptor` the recursive ownership coordinator; `WithLifecycle()` remains the standard built-in registration rather than a concrete-type gate. Remove `WithContextInheritance`, `ContextInheritanceHandler`, and `PropertyReferenceSet`. Update every compiled source and Public API snapshot path in the map's freshly generated inventory, including `PropertyReferenceSetTests.cs`, `DynamicSubjectTests.cs`, `DynamicSubjectTests.WhenInterceptingDynamicSubject_ThenTheyAreCalled.verified.txt`, Connectors production/tests, Hosting production/tests, the WebSocket SampleClient, and every SyncRoot/inheritance consumer. Task 2 also migrates both existing fallback-as-detach actions in `LifecycleEventsTests`: `SubjectDetaching_FiresForRootSubject_WhenContextRemoved` and `SubjectAttached_FiresAfterHandler_And_SubjectDetaching_FiresBeforeHandler` each replace `RemoveFallbackContext(context)` with exact `DetachFromContext(context)` because fallback removal is composition-only. The approved Dynamic oracle removes fallback lifecycle attach/detach entries while retaining the complete read/write order. Preserve composition-only connector tests that resolve one exact coordinator. Functional user documentation and diagrams remain assigned to Task 7.

- [ ] **Step 20: Close Task 2 atomic group**

Run:

```bash
dotnet build src/Namotion.Interceptor/Namotion.Interceptor.csproj --no-restore
dotnet build src/Namotion.Interceptor.Tracking/Namotion.Interceptor.Tracking.csproj --no-restore
dotnet build src/Namotion.Interceptor.Generator/Namotion.Interceptor.Generator.csproj --no-restore
dotnet build src/Namotion.Interceptor.Dynamic/Namotion.Interceptor.Dynamic.csproj --no-restore
dotnet build src/Namotion.Interceptor.Hosting/Namotion.Interceptor.Hosting.csproj --no-restore
dotnet build src/Namotion.Interceptor.Connectors/Namotion.Interceptor.Connectors.csproj --no-restore
dotnet build src/Namotion.Interceptor.WebSocket.SampleClient/Namotion.Interceptor.WebSocket.SampleClient.csproj --no-restore
dotnet test src/Namotion.Interceptor.Tests/Namotion.Interceptor.Tests.csproj --no-restore
dotnet test src/Namotion.Interceptor.Generator.Tests/Namotion.Interceptor.Generator.Tests.csproj --no-restore
dotnet test src/Namotion.Interceptor.Dynamic.Tests/Namotion.Interceptor.Dynamic.Tests.csproj --no-restore
dotnet test src/Namotion.Interceptor.Tracking.Tests/Namotion.Interceptor.Tracking.Tests.csproj --no-restore
dotnet test src/Namotion.Interceptor.Registry.Tests/Namotion.Interceptor.Registry.Tests.csproj --filter "FullyQualifiedName~CycleTests.WhenBreakingCycleBetweenExplicitRoots_ThenBothStayAttached|FullyQualifiedName~CycleTests.WhenInternalCycleOrphaned_ThenWholeComponentDetaches" --no-restore
dotnet test src/Namotion.Interceptor.Registry.Tests/Namotion.Interceptor.Registry.Tests.csproj --no-restore
dotnet test src/Namotion.Interceptor.Hosting.Tests/Namotion.Interceptor.Hosting.Tests.csproj --no-restore
dotnet test src/Namotion.Interceptor.Connectors.Tests/Namotion.Interceptor.Connectors.Tests.csproj --no-restore
dotnet test src/Namotion.Interceptor.OpcUa.Tests/Namotion.Interceptor.OpcUa.Tests.csproj --no-restore
dotnet test src/Namotion.Interceptor.Tests/Namotion.Interceptor.Tests.csproj --filter "FullyQualifiedName~VerifyChecksTests.PublicApi|FullyQualifiedName~InterceptorTests.WhenAddingAndRemovingContext_ThenInterceptorsAreCalledInTheRightOrder" --no-restore
dotnet test src/Namotion.Interceptor.Tracking.Tests/Namotion.Interceptor.Tracking.Tests.csproj --filter "FullyQualifiedName~VerifyChecksTests.PublicApi" --no-restore
dotnet test src/Namotion.Interceptor.Registry.Tests/Namotion.Interceptor.Registry.Tests.csproj --filter "FullyQualifiedName~VerifyChecksTests.PublicApi" --no-restore
dotnet test src/Namotion.Interceptor.Connectors.Tests/Namotion.Interceptor.Connectors.Tests.csproj --filter "FullyQualifiedName~VerifyChecksTests.PublicApi" --no-restore
dotnet test src/Namotion.Interceptor.Dynamic.Tests/Namotion.Interceptor.Dynamic.Tests.csproj --filter "FullyQualifiedName~DynamicSubjectTests.WhenInterceptingDynamicSubject_ThenTheyAreCalled" --no-restore
dotnet build src/Namotion.Interceptor.slnx --no-restore
git diff --check
```

Inspect the two renamed CycleTests outputs before the full Registry result. Confirm the explicit-root oracle has no final detach, and confirm the orphan oracle releases `A`, `B`, and `C` with ordinary callback order and correct reference counts. Do not mechanically accept a received file. Do not commit until the entire atomic Task 2 gate is green and independent review approves the atomic diff. Stage exactly the map's deduplicated 128-path manifest, not a broad `src` or project-prefix pathspec:

```bash
git add -- \
  src/Namotion.Interceptor.Connectors.Tests/DefaultSubjectFactoryTests.cs \
  src/Namotion.Interceptor.Connectors.Tests/SourceMonitorTests.cs \
  src/Namotion.Interceptor.Connectors.Tests/SourceWaitTests.cs \
  src/Namotion.Interceptor.Connectors.Tests/VerifyChecksTests.PublicApi.verified.txt \
  src/Namotion.Interceptor.Connectors/Monitoring/SourceMonitor.cs \
  src/Namotion.Interceptor.Dynamic.Tests/DynamicOwnershipAdmissionTests.cs \
  src/Namotion.Interceptor.Dynamic.Tests/DynamicOwnershipDiscoveryReaderTests.cs \
  src/Namotion.Interceptor.Dynamic.Tests/DynamicSubjectTests.WhenInterceptingDynamicSubject_ThenTheyAreCalled.verified.txt \
  src/Namotion.Interceptor.Dynamic.Tests/DynamicSubjectTests.cs \
  src/Namotion.Interceptor.Dynamic/DynamicSubject.cs \
  src/Namotion.Interceptor.Dynamic/DynamicSubjectFactory.cs \
  src/Namotion.Interceptor.Generator.Tests/BaseClassInterceptionBehaviorTests.cs \
  src/Namotion.Interceptor.Generator.Tests/GeneratedMemberTableTests.cs \
  src/Namotion.Interceptor.Generator.Tests/Snapshots/InterfaceDefaultPropertyTests.ClassOverridesInterfaceProperty_ClassWins.verified.txt \
  src/Namotion.Interceptor.Generator.Tests/Snapshots/InterfaceDefaultPropertyTests.InterfaceDefaultProperty_IncludedInDefaultProperties.verified.txt \
  src/Namotion.Interceptor.Generator.Tests/Snapshots/InterfaceDefaultPropertyTests.InterfaceDerivedProperty_IncludedInDefaultProperties.verified.txt \
  src/Namotion.Interceptor.Generator.Tests/Snapshots/InterfaceDefaultPropertyTests.InterfaceHierarchy_AllDefaultPropertiesIncluded.verified.txt \
  src/Namotion.Interceptor.Generator.Tests/Snapshots/InterfaceDefaultPropertyTests.MultipleInterfaces_AllDefaultPropertiesIncluded.verified.txt \
  src/Namotion.Interceptor.Generator.Tests/Snapshots/SourceGeneratorTests.WhenGeneratingClassWithInheritanceAndCustomAttribute_ThenBasePropertiesAreIncluded.verified.txt \
  src/Namotion.Interceptor.Generator.Tests/Snapshots/SourceGeneratorTests.WhenGeneratingClassWithInheritance_ThenPartialClassIsGenerated.verified.txt \
  src/Namotion.Interceptor.Generator.Tests/Snapshots/SourceGeneratorTests.WhenGeneratingClassWithInterceptorSubject_ThenPartialClassIsGenerated.verified.txt \
  src/Namotion.Interceptor.Generator.Tests/Snapshots/SourceGeneratorTests.WhenGeneratingClassWithPrivateProtectedProperty_ThenPropertyCorrectlyGenerated.verified.txt \
  src/Namotion.Interceptor.Generator.Tests/Snapshots/SourceGeneratorTests.WhenGeneratingClassWithProtectedInternalProperty_ThenPropertyCorrectlyGenerated.verified.txt \
  src/Namotion.Interceptor.Generator.Tests/Snapshots/SourceGeneratorTests.WhenGeneratingClassWithProtectedProperty_ThenPropertyCorrectlyGenerated.verified.txt \
  src/Namotion.Interceptor.Generator.Tests/Snapshots/SourceGeneratorTests.WhenGeneratingDeepNestedClass_ThenPartialClassIsGeneratedWithAllContainingTypes.verified.txt \
  src/Namotion.Interceptor.Generator.Tests/Snapshots/SourceGeneratorTests.WhenGeneratingNestedClass_ThenPartialClassIsGeneratedWithContainingTypes.verified.txt \
  src/Namotion.Interceptor.Generator.Tests/Snapshots/VirtualPartialTests.Test_OverridePartial_GeneratesCorrectly.verified.txt \
  src/Namotion.Interceptor.Generator.Tests/Snapshots/VirtualPartialTests.Test_VirtualInheritanceChain_GeneratesCorrectly.verified.txt \
  src/Namotion.Interceptor.Generator.Tests/Snapshots/VirtualPartialTests.Test_VirtualPartial_GeneratesCorrectly.verified.txt \
  src/Namotion.Interceptor.Generator.Tests/StructuralSetterShapeTests.cs \
  src/Namotion.Interceptor.Generator.Tests/SubjectBaseDiagnosticsTests.cs \
  src/Namotion.Interceptor.Generator.Tests/SubjectBaseShapeTests.cs \
  src/Namotion.Interceptor.Generator/GeneratedMemberTable.cs \
  src/Namotion.Interceptor.Generator/SubjectBaseContract.cs \
  src/Namotion.Interceptor.Generator/SubjectCodeGenerator.cs \
  src/Namotion.Interceptor.Hosting.Tests/HostedServiceHandlerTests.cs \
  src/Namotion.Interceptor.Hosting/HostedServiceHandler.cs \
  src/Namotion.Interceptor.OpcUa.Tests/Client/OpcUaSubjectLoaderTests.cs \
  src/Namotion.Interceptor.Registry.Tests/ConcurrentStructuralWriteLeakTests.cs \
  src/Namotion.Interceptor.Registry.Tests/DynamicPropertyLifecycleTests.cs \
  src/Namotion.Interceptor.Registry.Tests/GraphBehavior/CycleTests.WhenBreakingCycleBetweenExplicitRoots_ThenBothStayAttached.verified.txt \
  src/Namotion.Interceptor.Registry.Tests/GraphBehavior/CycleTests.WhenBreakingCycle_ThenBothDetach.verified.txt \
  src/Namotion.Interceptor.Registry.Tests/GraphBehavior/CycleTests.WhenInternalCycleOrphaned_ThenCycleStaysAttached_Limitation.verified.txt \
  src/Namotion.Interceptor.Registry.Tests/GraphBehavior/CycleTests.WhenInternalCycleOrphaned_ThenWholeComponentDetaches.verified.txt \
  src/Namotion.Interceptor.Registry.Tests/GraphBehavior/CycleTests.cs \
  src/Namotion.Interceptor.Registry.Tests/RegistryHandlerOrderTests.cs \
  src/Namotion.Interceptor.Registry.Tests/VerifyChecksTests.PublicApi.verified.txt \
  src/Namotion.Interceptor.Registry/Abstractions/RegisteredSubject.cs \
  src/Namotion.Interceptor.Registry/Abstractions/RegisteredSubjectProperty.cs \
  src/Namotion.Interceptor.Registry/InterceptorSubjectContextExtensions.cs \
  src/Namotion.Interceptor.Registry/SubjectRegistry.cs \
  src/Namotion.Interceptor.Tests/CommitRevisionTests.cs \
  src/Namotion.Interceptor.Tests/Context/ContextConcurrencyFuzzTests.cs \
  src/Namotion.Interceptor.Tests/Context/ContextDeepGraphTests.cs \
  src/Namotion.Interceptor.Tests/Context/ContextFunctionCacheTests.cs \
  src/Namotion.Interceptor.Tests/Context/ContextOwnershipRouteTests.cs \
  src/Namotion.Interceptor.Tests/Context/ContextServiceWalkOrderTests.cs \
  src/Namotion.Interceptor.Tests/InterceptorTests.WhenAddingAndRemovingContext_ThenInterceptorsAreCalledInTheRightOrder.verified.txt \
  src/Namotion.Interceptor.Tests/InterceptorTests.cs \
  src/Namotion.Interceptor.Tests/Ownership/ContextAuthorityActivationTests.cs \
  src/Namotion.Interceptor.Tests/Ownership/StructuralContinuationTests.cs \
  src/Namotion.Interceptor.Tests/Ownership/StructuralInterceptorPinningTests.cs \
  src/Namotion.Interceptor.Tests/Ownership/StructuralOwnershipAdmissionTests.cs \
  src/Namotion.Interceptor.Tests/Ownership/StructuralOwnershipConcurrencyTests.cs \
  src/Namotion.Interceptor.Tests/Ownership/SubjectAttachmentTests.cs \
  src/Namotion.Interceptor.Tests/Ownership/SubjectOwnershipOperationTests.cs \
  src/Namotion.Interceptor.Tests/Ownership/SubjectOwnershipProviderContractTests.cs \
  src/Namotion.Interceptor.Tests/Ownership/SubjectOwnershipStateTests.cs \
  src/Namotion.Interceptor.Tests/Ownership/SubjectPrivateLockTests.cs \
  src/Namotion.Interceptor.Tests/VerifyChecksTests.PublicApi.verified.txt \
  src/Namotion.Interceptor.Tracking.Tests/Change/DerivedPropertyChangeHandlerTests.cs \
  src/Namotion.Interceptor.Tracking.Tests/Change/DerivedPropertyCleanupTests.cs \
  src/Namotion.Interceptor.Tracking.Tests/Change/DerivedPropertyConcurrencyTests.cs \
  src/Namotion.Interceptor.Tracking.Tests/Change/FallbackContextInvalidationTests.cs \
  src/Namotion.Interceptor.Tracking.Tests/Change/PerPropertySubscriptionLifecycleTests.cs \
  src/Namotion.Interceptor.Tracking.Tests/ContextInheritanceHandlerTests.cs \
  src/Namotion.Interceptor.Tracking.Tests/Lifecycle/ConcurrentWriteLifecycleTests.cs \
  src/Namotion.Interceptor.Tracking.Tests/Lifecycle/FallbackCompositionLifecycleTests.cs \
  src/Namotion.Interceptor.Tracking.Tests/Lifecycle/LifecycleArrayPinningTests.cs \
  src/Namotion.Interceptor.Tracking.Tests/Lifecycle/LifecycleCallbackOrderTests.cs \
  src/Namotion.Interceptor.Tracking.Tests/Lifecycle/LifecycleEventsTests.cs \
  src/Namotion.Interceptor.Tracking.Tests/Lifecycle/OwnershipDiscoveryReaderTests.cs \
  src/Namotion.Interceptor.Tracking.Tests/Lifecycle/OwnershipMembershipTests.cs \
  src/Namotion.Interceptor.Tracking.Tests/Lifecycle/OwnershipProviderPoolTests.cs \
  src/Namotion.Interceptor.Tracking.Tests/Lifecycle/PropertyReferenceSetTests.cs \
  src/Namotion.Interceptor.Tracking.Tests/Lifecycle/RecursiveAttachTests.cs \
  src/Namotion.Interceptor.Tracking.Tests/Lifecycle/SubjectMetadataAdditionTests.cs \
  src/Namotion.Interceptor.Tracking.Tests/LifecycleInterceptorTests.cs \
  src/Namotion.Interceptor.Tracking.Tests/Models/SideEffectHolder.cs \
  src/Namotion.Interceptor.Tracking.Tests/Models/SideEffectPerson.cs \
  src/Namotion.Interceptor.Tracking.Tests/Parent/ParentAccessDuringLifecycleTests.cs \
  src/Namotion.Interceptor.Tracking.Tests/SubjectPropertyTypeExtensionsTests.cs \
  src/Namotion.Interceptor.Tracking.Tests/Transactions/SubjectTransactionTests.cs \
  src/Namotion.Interceptor.Tracking.Tests/VerifyChecksTests.PublicApi.verified.txt \
  src/Namotion.Interceptor.Tracking.Tests/WriteTimestampTests.cs \
  src/Namotion.Interceptor.Tracking/Change/DerivedPropertyChangeHandler.cs \
  src/Namotion.Interceptor.Tracking/InterceptorSubjectContextExtensions.cs \
  src/Namotion.Interceptor.Tracking/Lifecycle/ContextInheritanceHandler.cs \
  src/Namotion.Interceptor.Tracking/Lifecycle/LifecycleInterceptor.cs \
  src/Namotion.Interceptor.Tracking/Lifecycle/LifecycleInterceptorExtensions.cs \
  src/Namotion.Interceptor.Tracking/Lifecycle/LifecycleReconciliationState.cs \
  src/Namotion.Interceptor.Tracking/Lifecycle/PropertyReferenceSet.cs \
  src/Namotion.Interceptor.Tracking/Lifecycle/SubjectOwnershipTraversal.cs \
  src/Namotion.Interceptor.Tracking/Parent/ParentTrackingHandler.cs \
  src/Namotion.Interceptor.WebSocket.SampleClient/Program.cs \
  src/Namotion.Interceptor/Cache/ReadInterceptorFactory.cs \
  src/Namotion.Interceptor/Cache/WriteInterceptorChain.cs \
  src/Namotion.Interceptor/Cache/WriteInterceptorFactory.cs \
  src/Namotion.Interceptor/IInterceptorSubject.cs \
  src/Namotion.Interceptor/InterceptorSubjectContext.cs \
  src/Namotion.Interceptor/InterceptorSubjectExtensions.cs \
  src/Namotion.Interceptor/Interceptors/IInterceptorExecutor.cs \
  src/Namotion.Interceptor/Interceptors/ILifecycleInterceptor.cs \
  src/Namotion.Interceptor/Interceptors/IReadInterceptor.cs \
  src/Namotion.Interceptor/Interceptors/IWriteInterceptor.cs \
  src/Namotion.Interceptor/Interceptors/InterceptorExecutor.cs \
  src/Namotion.Interceptor/Ownership/ContextAuthorityActivation.cs \
  src/Namotion.Interceptor/Ownership/SubjectMetadataAddition.cs \
  src/Namotion.Interceptor/Ownership/SubjectMetadataAdditionBatchContext.cs \
  src/Namotion.Interceptor/Ownership/SubjectMetadataCommitRegistration.cs \
  src/Namotion.Interceptor/Ownership/SubjectOwnershipBatch.cs \
  src/Namotion.Interceptor/Ownership/SubjectOwnershipCoordinator.cs \
  src/Namotion.Interceptor/Ownership/SubjectOwnershipOperation.cs \
  src/Namotion.Interceptor/Ownership/SubjectOwnershipState.cs \
  src/Namotion.Interceptor/Ownership/SubjectOwnershipView.cs \
  src/Namotion.Interceptor/Ownership/SubjectOwnershipWriteContext.cs \
  src/Namotion.Interceptor/PropertyReferenceExtensions.cs \
  src/Namotion.Interceptor/SubjectPropertyMetadata.cs
```

Immediately after staging, prove that the index is the exact 128-path union shown in the map and that no Task 2 manifest path is unstaged or untracked:

```bash
git diff --cached --name-only
git diff --exit-code -- src/Namotion.Interceptor src/Namotion.Interceptor.Tests src/Namotion.Interceptor.Tracking src/Namotion.Interceptor.Tracking.Tests src/Namotion.Interceptor.Registry src/Namotion.Interceptor.Registry.Tests src/Namotion.Interceptor.Generator src/Namotion.Interceptor.Generator.Tests src/Namotion.Interceptor.Dynamic src/Namotion.Interceptor.Dynamic.Tests src/Namotion.Interceptor.Hosting src/Namotion.Interceptor.Hosting.Tests src/Namotion.Interceptor.Connectors src/Namotion.Interceptor.Connectors.Tests src/Namotion.Interceptor.WebSocket.SampleClient src/Namotion.Interceptor.OpcUa.Tests
git ls-files --others --exclude-standard -- src/Namotion.Interceptor src/Namotion.Interceptor.Tests src/Namotion.Interceptor.Tracking src/Namotion.Interceptor.Tracking.Tests src/Namotion.Interceptor.Registry src/Namotion.Interceptor.Registry.Tests src/Namotion.Interceptor.Generator src/Namotion.Interceptor.Generator.Tests src/Namotion.Interceptor.Dynamic src/Namotion.Interceptor.Dynamic.Tests src/Namotion.Interceptor.Hosting src/Namotion.Interceptor.Hosting.Tests src/Namotion.Interceptor.Connectors src/Namotion.Interceptor.Connectors.Tests src/Namotion.Interceptor.WebSocket.SampleClient src/Namotion.Interceptor.OpcUa.Tests
```

The first output must equal the map's sorted 128-path union exactly. The second command must exit zero with no output, and the third must produce no output. Any extra staged path or missing/unstaged/untracked manifest path blocks the commit. Then make the one Task 2 commit and prove that none of those project scopes can leak into Task 5:

```bash
git commit -m "Implement atomic subject ownership"
git status --short --untracked-files=all -- src/Namotion.Interceptor src/Namotion.Interceptor.Tests src/Namotion.Interceptor.Tracking src/Namotion.Interceptor.Tracking.Tests src/Namotion.Interceptor.Registry src/Namotion.Interceptor.Registry.Tests src/Namotion.Interceptor.Generator src/Namotion.Interceptor.Generator.Tests src/Namotion.Interceptor.Dynamic src/Namotion.Interceptor.Dynamic.Tests src/Namotion.Interceptor.Hosting src/Namotion.Interceptor.Hosting.Tests src/Namotion.Interceptor.Connectors src/Namotion.Interceptor.Connectors.Tests src/Namotion.Interceptor.WebSocket.SampleClient src/Namotion.Interceptor.OpcUa.Tests
```

Expected after commit: no output. If any Task 2 manifest path remains, amend the Task 2 commit before starting Task 3 or Task 5. Task 5's later broad stage command must never collect a Task 2 cutover path.

### Task 3: Deterministic Routes, Compatibility, and Advanced Topology Verification

**Files:**

```text
src/Namotion.Interceptor.Tracking.Tests/Lifecycle/OwnershipRouteSelectionTests.cs       create
src/Namotion.Interceptor.Tracking.Tests/Lifecycle/OwnershipCycleTests.cs                create
src/Namotion.Interceptor.Tracking.Tests/Lifecycle/OwnershipCompatibilityTests.cs        create
src/Namotion.Interceptor.Tracking/Lifecycle/SubjectOwnershipTraversal.cs
src/Namotion.Interceptor.Tracking/Lifecycle/LifecycleInterceptor.cs
src/Namotion.Interceptor/Ownership/SubjectOwnershipState.cs
src/Namotion.Interceptor/Ownership/SubjectOwnershipBatch.cs
src/Namotion.Interceptor/Ownership/SubjectOwnershipCoordinator.cs
src/Namotion.Interceptor.Tests/Ownership/SubjectAttachmentTests.cs
src/Namotion.Interceptor.Registry.Tests/ParentTrackingTests.cs
src/Namotion.Interceptor.Registry.Tests/DynamicPropertyLifecycleTests.cs
```

**Consumes:** Task 2 ordered memberships, Core views/reservations, recursive callback seam, and exact self-gating route mutator.

**Produces:** deterministic acyclic route selection, explicit-to-inherited transfer, complete subtree compatibility validation, exact multi-anchor/DAG/repeated-occurrence membership, weak-reference cleanup proof, and an optional behavior-neutral affected-component traversal optimization.

- [ ] **Step 1: Add route and capability RED tests**

Add `WhenSecondCompatibleParentIsAdded_ThenReferenceCountIncrementsWithoutRouteChurn`, `WhenActiveParentIsRemoved_ThenEarliestSurvivingAcyclicParentBecomesRoute`, `WhenExplicitAnchorExists_ThenParentDoesNotReplaceRoute`, `WhenExplicitAnchorDetachesWithParentRemaining_ThenRouteTransfersWithoutLifecycleChurn`, `WhenDescendantBelongsToDifferentDomain_ThenBackingValueDoesNotCommit`, `WhenTwoPlainContextsShareCoordinator_ThenDomainsRemainIncompatible`, `WhenRepeatedOccurrencesUseOneParentProperty_ThenCountIsOne`, `WhenSameSubjectUsesTwoParentProperties_ThenCountIsTwo`, and `WhenBranchServicesDiffer_ThenDescendantsRemainSiblingIsolated`.

- [ ] **Step 2: Add advanced object, collection, dictionary, cycle, DAG, and lifetime tests**

Add `WhenOneAnchorOwnsCycle_ThenEverySubjectHasOneDomain`, `WhenTwoAnchorsReachCycle_ThenFinalAnchorReleaseControlsLifetime`, `WhenParentsShareDag_ThenMembershipsRemainExact`, `WhenEarliestParentWouldCreateRouteCycle_ThenNextAcyclicParentWins`, `WhenDictionaryAndRepeatedCollectionReferencesChange_ThenKeysIndicesAndCountsAgree`, and `WhenFinalExternalAnchorIsRemoved_ThenInternalCycleCountsDoNotRetainComponent`. The final-external-anchor row is cross-layer verification and must already be GREEN from Task 2's rooted-release invariant. The two-anchor, shared-DAG, repeated-occurrence, route-cycle, subtree-compatibility, and weak-reference assertions provide the advanced Task 3 evidence.

Use this exact skeleton contract for every Task 3 row:

| Test file | Arrange | Act | Assert |
|---|---|---|---|
| `OwnershipRouteSelectionTests.cs` | two configured roots, ordered exact parent properties, descriptor capture | add/remove/transfer one membership through a setter or strict detach | explicit route wins or earliest surviving acyclic parent wins; old descriptor cannot mutate later state |
| `OwnershipCompatibilityTests.cs` | complete proposed subtree with compatible or distinct exact domain/coordinator identities | execute one structural setter | compatible graph commits once; incompatible graph leaves backing value, ledgers, routes, baselines, and callbacks unchanged |
| `OwnershipCycleTests.cs` | isolated cycle/DAG plus exact external anchors and weak references | attach, change repeated property occurrences, then remove anchors | Task 2 simple final-anchor release stays GREEN; exact multi-anchor property-key counts and index projections remain correct; weak references collect |

Each method contains `// Arrange`, one `// Act`, and `// Assert`; exception rows use `// Act & Assert`. No helper hides the public operation under test.

- [ ] **Step 3: Run the mixed inherited-GREEN and advanced-RED gate**

```bash
dotnet test src/Namotion.Interceptor.Tracking.Tests/Namotion.Interceptor.Tracking.Tests.csproj --filter "FullyQualifiedName~OwnershipRouteSelectionTests|FullyQualifiedName~OwnershipCycleTests|FullyQualifiedName~OwnershipCompatibilityTests" --no-restore
```

Expected: `WhenFinalExternalAnchorIsRemoved_ThenInternalCycleCountsDoNotRetainComponent` passes against Task 2. Only genuinely missing deterministic route selection, explicit-to-inherited transfer, complete-subtree compatibility, multi-anchor/DAG/repeated-occurrence accounting, or weak-reference behavior may fail semantically. No compile failure or simple orphan-release failure is acceptable.

- [ ] **Step 4: Implement missing advanced route/compatibility behavior and optionally optimize affected-component traversal**

Keep the first parent inline and allocate insertion-ordered overflow only on the second distinct parent property. Explicit route wins. Otherwise scan survivors only when the active parent disappears and skip a candidate whose target ancestry contains the subject. Traverse every new reachable child before terminal commit and reserve each subject once. Every install, transfer, and clear creates a fresh PR #474 descriptor and checks exact generation plus descriptor. Add no new synchronization; every mutation uses the existing topology turn and self-gating route mutator.

Task 2's whole-root prospective traversal and simple final-anchor orphan release are binding behavior, not missing Task 3 implementation. If profiling shows the whole-root scan must be narrowed, Task 3 may replace it with a pooled affected-component scan only as a behavior-neutral optimization: on possible anchor loss, scan the affected component for explicit or outside incoming anchors and reserve complete release only when none exists. Before retaining that optimization, prove identical callback order/counts, Core ledger and route state, Tracking baselines, Registry projection, weak-reference cleanup, and allocation behavior for the Task 2 simple case plus Task 3 multi-anchor, DAG, and repeated-reference schedules. Otherwise keep the Task 2 traversal unchanged.

- [ ] **Step 5: Run GREEN and commit**

```bash
dotnet test src/Namotion.Interceptor.Tests/Namotion.Interceptor.Tests.csproj --filter "FullyQualifiedName~SubjectAttachmentTests" --no-restore
dotnet test src/Namotion.Interceptor.Tracking.Tests/Namotion.Interceptor.Tracking.Tests.csproj --filter "FullyQualifiedName~OwnershipRouteSelectionTests|FullyQualifiedName~OwnershipCycleTests|FullyQualifiedName~OwnershipCompatibilityTests|FullyQualifiedName~LifecycleInterceptorTests" --no-restore
dotnet test src/Namotion.Interceptor.Registry.Tests/Namotion.Interceptor.Registry.Tests.csproj --filter "FullyQualifiedName~DynamicPropertyLifecycleTests|FullyQualifiedName~Parent" --no-restore
git diff --check
git add src/Namotion.Interceptor src/Namotion.Interceptor.Tests src/Namotion.Interceptor.Tracking src/Namotion.Interceptor.Tracking.Tests src/Namotion.Interceptor.Registry.Tests
git commit -m "Enforce one effective ownership route"
```

### Task 4: Bounded Concurrency, Model, and Lifetime Verification

**Files:**

```text
src/Namotion.Interceptor.Tests/Ownership/OwnershipTopologyConcurrencyTests.cs          create
src/Namotion.Interceptor.Tracking.Tests/Lifecycle/OwnershipConcurrencyTests.cs         create
src/Namotion.Interceptor.Tracking.Tests/Lifecycle/OwnershipLifetimeTests.cs            create
src/Namotion.Interceptor.Tracking.Tests/Lifecycle/OwnershipCallbackFailureTests.cs     create
src/Namotion.Interceptor.Tracking.Tests/Lifecycle/ConcurrentWriteLifecycleTests.cs
src/Namotion.Interceptor.Tracking.Tests/Lifecycle/LifecycleEventsTests.cs
src/Namotion.Interceptor.Registry.Tests/DynamicPropertyLifecycleTests.cs
src/Namotion.Interceptor.Registry.Tests/ConcurrentStructuralWriteLeakTests.cs
```

**Consumes:** Tasks 2 and 3 complete semantics.

**Produces:** additional deterministic admission evidence, quiescent model checks, and weak release proof. It is test-only and expected GREEN against completed Tasks 2 and 3.

Task 2 already owns both existing `LifecycleEventsTests` fallback-detach-to-strict-detach migrations required for its full gate: `SubjectDetaching_FiresForRootSubject_WhenContextRemoved` and `SubjectAttached_FiresAfterHandler_And_SubjectDetaching_FiresBeforeHandler`. Task 4 may add the adversarial rows below to the same file, but it does not defer or repeat either migration.

- [ ] **Step 1: Add bounded adversarial verification tests**

Add `WhenBarrierStartedOwnershipRoundsSettle_ThenEveryOutcomeMatchesASerialModel`, `WhenFailedAttachIsReleased_ThenNoActivationOrProviderFrameRetainsGraph`, `WhenCancelledBatchIsReleased_ThenTentativeReferencesCollect`, `WhenExplicitRootAndMultiParentCycleRelease_ThenWholeComponentCollects`, `WhenMetadataAdmissionFails_ThenCommitTokenWrapperAndGraphCollects`, and `WhenLaterGenerationWins_ThenSupersededBatchCollects`.

Every negative blocked assertion has `attemptingEntry` immediately before the public call and `enteredCallback` after admission. No target-occupancy exception is expected. Fixed rounds combine attach/detach, structural replacement, legal context mutation, atomic `AddProperties`, cycles, DAGs, repeats, and cross-context callbacks. Assert backing/model, one domain, selected acyclic route or explicit win, memberships/baselines, Registry/parent projection, no token/batch, and no transient library exception.

The executable blocking skeleton is: the callback phase sets `firstEntered` and waits on `allowFirst`; the competing task sets `secondAttemptingEntry` immediately before its public call; its admitted callback sets `secondEntered`; the test waits for `secondAttemptingEntry`, asserts `secondEntered` is unset, releases `allowFirst`, joins both tasks, then asserts one valid serial result.

- [ ] **Step 2: Add combined exception and token verification**

Add `WhenCommitNotificationAndLaterCallbackWouldThrow_ThenFirstPostCommitExceptionWinsAndCleanupCompletes`, `WhenCommittedDownstreamAndRegistryReconciliationThrow_ThenOriginalDownstreamExceptionWins`, and `WhenNestedSameNameTokenIsCancelledAfterInnerCommit_ThenOnlyInnerWrapperSurvives`. These combine already implemented Core/Tracking/Registry boundaries; they are expected GREEN and do not duplicate a Task 2 deterministic regression row.

- [ ] **Step 3: Run the expected-GREEN verification gate**

```bash
dotnet test src/Namotion.Interceptor.Tests/Namotion.Interceptor.Tests.csproj --filter "FullyQualifiedName~OwnershipTopologyConcurrencyTests" --no-restore
dotnet test src/Namotion.Interceptor.Tracking.Tests/Namotion.Interceptor.Tracking.Tests.csproj --filter "FullyQualifiedName~OwnershipConcurrencyTests|FullyQualifiedName~OwnershipLifetimeTests|FullyQualifiedName~OwnershipCallbackFailureTests" --no-restore
dotnet test src/Namotion.Interceptor.Registry.Tests/Namotion.Interceptor.Registry.Tests.csproj --filter "FullyQualifiedName~DynamicPropertyLifecycleTests|FullyQualifiedName~ConcurrentStructuralWriteLeakTests" --no-restore
```

Expected: GREEN. Record all model seeds/round counts and weak-reference evidence. If any row reproduces a real defect, stop this task without editing production, write the smallest explicit plan/map amendment naming that failing row and proposed implementation owner, obtain review, then run a separate RED/fix/GREEN cycle. Task 4 never assumes a test must fail merely to justify production work.

- [ ] **Step 4: Run full affected GREEN and commit tests only**

```bash
dotnet test src/Namotion.Interceptor.Tests/Namotion.Interceptor.Tests.csproj --no-restore
dotnet test src/Namotion.Interceptor.Tracking.Tests/Namotion.Interceptor.Tracking.Tests.csproj --no-restore
dotnet test src/Namotion.Interceptor.Registry.Tests/Namotion.Interceptor.Registry.Tests.csproj --no-restore
git diff --check
git add src/Namotion.Interceptor.Tests src/Namotion.Interceptor.Tracking.Tests src/Namotion.Interceptor.Registry.Tests
git commit -m "Verify subject ownership concurrency"
```

### Task 5: First-Party Ownership Migration

**Initial reviewed files:**

```text
src/HomeBlaze/HomeBlaze.Services/RootManager.cs
src/HomeBlaze/HomeBlaze.Services/ConfigurableSubjectSerializer.cs
src/HomeBlaze/HomeBlaze.Services/SubjectFactory.cs
src/HomeBlaze/HomeBlaze.Storage/Internal/FileSubjectFactory.cs
src/HomeBlaze/HomeBlaze.Services.Tests/Serialization/ConfigurableSubjectSerializerTests.cs
src/HomeBlaze/HomeBlaze.Services.Tests/SubjectFactoryTests.cs                         create
src/HomeBlaze/HomeBlaze.Services.Tests/RootManagerTests.cs                           create
src/HomeBlaze/HomeBlaze.Storage.Tests/Internal/FileSubjectFactoryTests.cs            create
src/Namotion.Interceptor.SamplesModel/Root.cs
src/Namotion.Interceptor.ConnectorTester/Model/TestNode.cs
src/Namotion.Interceptor.Mqtt.SampleClient/Program.cs
src/Namotion.Interceptor.Mqtt.SampleServer/Program.cs
src/Namotion.Interceptor.OpcUa.SampleClient/Program.cs
src/Namotion.Interceptor.OpcUa.SampleServer/Program.cs
src/Namotion.Interceptor.SampleBlazor/Program.cs
src/Namotion.Interceptor.SampleConsole/Program.cs
src/Namotion.Interceptor.SampleMachine/Program.cs
src/Namotion.Interceptor.SampleWeb/Program.cs
src/Namotion.Interceptor.WebSocket.SampleClient/Program.cs
src/Namotion.Interceptor.WebSocket.SampleServer/Program.cs
src/Namotion.Interceptor.Benchmark/SubjectHierarchyBenchmark.cs
src/Namotion.Interceptor.Benchmark/RegistryBenchmark.cs
src/Namotion.Interceptor.Benchmark/SubjectTransactionBenchmark.cs
src/Namotion.Interceptor.Benchmark/SubjectSourceBenchmark.cs
src/Namotion.Interceptor.Benchmark/SubjectUpdateBenchmark.cs
src/Namotion.Interceptor.Benchmark/SourcePathProviderBenchmark.cs
src/Namotion.Interceptor.Benchmark/PropertyChangeSubscriptionsBenchmark.cs
src/Namotion.Interceptor.Benchmark/DynamicSubjectBenchmark.cs
src/Namotion.Interceptor.Benchmark/ContextDelegationDepthBenchmark.cs
```

**Consumes:** strict context constructors and completed inherited ownership.

**Produces:** explicit root lifetime boundaries and route-free property-child construction across first-party consumers.

- [ ] **Step 1: Run and classify the constructor/fallback audit**

Run these exact searches across the repository and record every hit in `task-5-constructor-inventory.md` as explicit root, property child, composition fallback, legacy shorthand, or test topology. The list above is an initial reviewed inventory, not a claim that future source remains closed:

```bash
rg -n "new [A-Za-z_][A-Za-z0-9_.<>]*\([^)]*(context|Context)" src/HomeBlaze src/Namotion.Interceptor.* --glob '*.cs'
rg -n "AddFallbackContext|RemoveFallbackContext" src/HomeBlaze src/Namotion.Interceptor.* --glob '*.cs'
rg -n "IInterceptorSubjectContext|InterceptorSubjectContext" src/HomeBlaze src/Namotion.Interceptor.* --glob '*.cs'
```

If a new semantic hit lies outside the initial list, amend and review the manifest before editing it.

- [ ] **Step 2: Add RED tests**

Add `WhenRootManagerLoadsRoot_ThenAttachAndDetachAreExact`, `WhenSerializerCreatesPropertyChildWithContextService_ThenChildIsRouteFree`, `WhenSubjectFactoryResolvesOtherDependencies_ThenItIgnoresContextConstructor`, `WhenFileFactoryCreatesStoredChild_ThenParentPublicationOwnsIt`, and `WhenSampleModelCreatesPersons_ThenOnlyRootIsExplicit`.

Test skeleton contract:

| Exact test | File | Arrange and Act | Assert |
|---|---|---|---|
| `WhenRootManagerLoadsRoot_ThenAttachAndDetachAreExact` | `RootManagerTests.cs` | load first root, replace with second, stop manager | first exact context anchor detaches before second attaches; no property child receives explicit anchor |
| `WhenSerializerCreatesPropertyChildWithContextService_ThenChildIsRouteFree` | `ConfigurableSubjectSerializerTests.cs` | deserialize owned parent whose child type has context and non-context constructors | child is created without explicit context and becomes owned only after parent setter commits |
| `WhenSubjectFactoryResolvesOtherDependencies_ThenItIgnoresContextConstructor` | `SubjectFactoryTests.cs` | provider contains context plus another required dependency; create child | selected constructor resolves the non-context dependency and does not anchor child |
| `WhenFileFactoryCreatesStoredChild_ThenParentPublicationOwnsIt` | `FileSubjectFactoryTests.cs` | create stored child and publish through owning parent | no ownership before publication; exact inherited domain/route after publication |
| `WhenSampleModelCreatesPersons_ThenOnlyRootIsExplicit` | `SubjectFactoryTests.cs` | construct sample root and add people through properties | root reports exact attach context; each person reports no explicit attach and one inherited membership |

- [ ] **Step 3: Run RED**

```bash
dotnet test src/HomeBlaze/HomeBlaze.Services.Tests/HomeBlaze.Services.Tests.csproj --filter "FullyQualifiedName~RootManagerTests|FullyQualifiedName~ConfigurableSubjectSerializerTests|FullyQualifiedName~SubjectFactoryTests" --no-restore
dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests/HomeBlaze.Storage.Tests.csproj --filter "FullyQualifiedName~FileSubjectFactory" --no-restore
```

Expected: RED where DI chooses context constructors or root replacement omits exact detach.

- [ ] **Step 4: Migrate roots and children minimally**

Keep context constructors only for owned application/server/client roots and detach at existing replacement/shutdown boundaries. Construct property children route-free, publish through parent setters, and let normal ownership adopt them. Keep composition fallbacks. Preserve `ContextDelegationDepthBenchmark` as fallback-composition depth rather than ownership-route depth.

- [ ] **Step 5: Run GREEN, compile, and commit**

```bash
dotnet test src/HomeBlaze/HomeBlaze.Services.Tests/HomeBlaze.Services.Tests.csproj --no-restore
dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests/HomeBlaze.Storage.Tests.csproj --no-restore
dotnet build src/Namotion.Interceptor.Benchmark/Namotion.Interceptor.Benchmark.csproj --no-restore
dotnet build src/Namotion.Interceptor.SamplesModel/Namotion.Interceptor.SamplesModel.csproj --no-restore
dotnet build src/Namotion.Interceptor.slnx --no-restore
git diff --check
git add src
git commit -m "Migrate explicit and inherited subject construction"
```

### Task 6: Route-Free Factories and Connector Publication Order

**Initial reviewed files:**

```text
src/Namotion.Interceptor.Connectors/ISubjectFactory.cs
src/Namotion.Interceptor.Connectors/DefaultSubjectFactory.cs
src/Namotion.Interceptor.Connectors/SubjectFactoryExtensions.cs
src/Namotion.Interceptor.Connectors/Updates/Internal/SubjectUpdateApplier.cs
src/Namotion.Interceptor.Connectors/Updates/Internal/SubjectItemsUpdateApplier.cs
src/Namotion.Interceptor.Connectors/Paths/PathExtensions.cs
src/Namotion.Interceptor.Connectors.Tests/DefaultSubjectFactoryTests.cs
src/Namotion.Interceptor.Connectors.Tests/Updates/SubjectUpdateExtensionsTests.cs
src/Namotion.Interceptor.Connectors.Tests/Updates/SubjectUpdateTests.cs
src/Namotion.Interceptor.Connectors.Tests/Updates/SubjectUpdateCollectionTests.cs
src/Namotion.Interceptor.Connectors.Tests/Updates/SubjectUpdateDictionaryTests.cs
src/Namotion.Interceptor.Connectors.Tests/Updates/SubjectUpdateCycleTests.cs
src/Namotion.Interceptor.Connectors.Tests/Updates/SubjectUpdateReadOnlyTypesTests.cs
src/Namotion.Interceptor.Connectors.Tests/RouteFreeSubjectFactoryContractTests.cs       create
src/Namotion.Interceptor.OpcUa/OpcUaSubjectFactory.cs
src/Namotion.Interceptor.OpcUa/Client/OpcUaSubjectLoader.cs
src/Namotion.Interceptor.OpcUa.Tests/Client/OpcUaSubjectFactoryTests.cs
src/Namotion.Interceptor.OpcUa.Tests/Client/OpcUaSubjectLoaderTests.cs
src/Namotion.Interceptor.Connectors.Tests/VerifyChecksTests.PublicApi.verified.txt
src/Namotion.Interceptor.OpcUa.Tests/VerifyChecksTests.PublicApi.verified.txt
```

**Consumes:** Task 5's construction distinction and the general parent terminal.

**Produces:** route-free factory contract and parent-first connector/OPC UA publication.

Before RED, rerun constructor/factory/publication searches over all connector and OPC UA projects, record the exact result, and amend this initial inventory under review for any semantic hit. Do not claim a permanently closed manifest across later branch changes.

- [ ] **Step 1: Add factory and publication RED tests**

Add `WhenServiceProviderContainsContext_ThenDefaultFactoryCreatesRouteFreeChild`, `WhenNonContextDependenciesExist_ThenFactoryResolvesThem`, `WhenCustomFactoryReturnsExplicitRoot_ThenPublicationRejects`, `WhenCustomFactoryReturnsInheritedSubject_ThenPopulationRejects`, `WhenFactoryResultPublishes_ThenRecursivePopulationSeesOwnership`, `WhenTwoProvidersDiffer_ThenTypeCacheDoesNotReuseWinner`, `WhenNonContextConstructorsAreAmbiguous_ThenFactoryRejects`, `WhenObjectChildIsCreated_ThenParentPublishesBeforeRecursivePopulation`, `WhenCollectionChildIsCreated_ThenStableIndexPublishesBeforeRecursivePopulation`, `WhenDictionaryChildIsCreated_ThenStableKeyPublishesBeforeRecursivePopulation`, `WhenPathCreatesIntermediateSubjects_ThenEachParentPublishesBeforeDescent`, and `WhenOpcUaLoaderCreatesDagSubject_ThenParentPublicationPrecedesCacheAndPopulation`.

Use this exact skeleton contract:

| Test family/file | Arrange and Act | Assert |
|---|---|---|
| factory rows in `DefaultSubjectFactoryTests.cs` and `RouteFreeSubjectFactoryContractTests.cs` | create two service providers with different viable non-context dependencies and an available context service; invoke public factory once per provider | result has no explicit/inherited owner before publication; provider-specific winner is not cached; ambiguity and preowned custom results fail before population with unchanged state |
| object/collection/dictionary/path rows in update tests | callback records child ownership at creation, parent setter return, and first recursive child write | route-free at creation, exact inherited owner at first recursive write, one parent backing publication, stable exact property/index/key identity |
| OPC UA rows in `OpcUaSubjectLoaderTests.cs` | create a shared DAG child behind an OPC UA parent with callback barriers recording cache insertion/population | parent property commit precedes DAG cache insertion and first descendant population; repeated node reuses exact committed subject |

Every row uses explicit `// Arrange`, `// Act`, and `// Assert`; rejection rows use `// Act & Assert` and also assert the recursive-population callback count is zero.

- [ ] **Step 2: Run RED**

```bash
dotnet test src/Namotion.Interceptor.Connectors.Tests/Namotion.Interceptor.Connectors.Tests.csproj --filter "FullyQualifiedName~RouteFreeSubjectFactoryContractTests|FullyQualifiedName~DefaultSubjectFactoryTests|FullyQualifiedName~SubjectUpdate" --no-restore
dotnet test src/Namotion.Interceptor.OpcUa.Tests/Namotion.Interceptor.OpcUa.Tests.csproj --filter "FullyQualifiedName~OpcUaSubjectFactoryTests|FullyQualifiedName~OpcUaSubjectLoaderTests" --no-restore
```

Expected: RED where DI chooses a context constructor or recursive population precedes parent commit.

- [ ] **Step 3: Cache candidates, not provider-specific winners**

Cache ordered non-context constructor candidates and compiled invokers per type. Prefer parameterless. Per call, resolve all non-context dependencies, require exactly one viable candidate when needed, and never cache a provider-specific choice. Public XML promises route-free unowned property children.

- [ ] **Step 4: Publish before recursive population**

For object, collection, dictionary, path, and OPC UA paths: create and validate route-free children; write the complete parent property once; reread stable index/key/property and require exact identity; then recurse. Cache newly created OPC UA DAG subjects only after successful parent publication. Do not restore fallback-as-attach.

- [ ] **Step 5: Run GREEN and commit**

```bash
dotnet test src/Namotion.Interceptor.Connectors.Tests/Namotion.Interceptor.Connectors.Tests.csproj --no-restore
dotnet test src/Namotion.Interceptor.OpcUa.Tests/Namotion.Interceptor.OpcUa.Tests.csproj --no-restore
dotnet test src/Namotion.Interceptor.Mqtt.Tests/Namotion.Interceptor.Mqtt.Tests.csproj --no-restore
dotnet test src/Namotion.Interceptor.WebSocket.Tests/Namotion.Interceptor.WebSocket.Tests.csproj --no-restore
git diff --check
git add src/Namotion.Interceptor.Connectors src/Namotion.Interceptor.Connectors.Tests src/Namotion.Interceptor.OpcUa src/Namotion.Interceptor.OpcUa.Tests
git commit -m "Publish route-free connector children through parents"
```

No Connector Tester run occurs here. The agreed external final-hash run is Task 7.

### Task 7: Canonical Documentation, Simplification Audit, and Release Gates

**Files:**

```text
README.md
docs/interceptor.md
docs/generator.md
docs/dynamic.md
docs/subject-guidelines.md
docs/tracking.md
docs/registry.md
docs/hosting.md
docs/connectors.md
docs/connectors-subject-updates.md
docs/connectors-opcua-client.md
docs/connectors-websocket.md
docs/connectors-monitoring.md
docs/design/generator-supported-shapes.md
docs/design/context-resolution.md
docs/design/tracking-lifecycle.md
docs/design/tracking-derived-properties.md
src/Namotion.Interceptor.Tests/VerifyChecksTests.PublicApi.verified.txt
src/Namotion.Interceptor.Tracking.Tests/VerifyChecksTests.PublicApi.verified.txt
src/Namotion.Interceptor.Registry.Tests/VerifyChecksTests.PublicApi.verified.txt
src/Namotion.Interceptor.Hosting.Tests/VerifyChecksTests.PublicApi.verified.txt
src/Namotion.Interceptor.Connectors.Tests/VerifyChecksTests.PublicApi.verified.txt
src/Namotion.Interceptor.OpcUa.Tests/VerifyChecksTests.PublicApi.verified.txt
src/Namotion.Interceptor.Mqtt.Tests/VerifyChecksTests.PublicApi.verified.txt
src/Namotion.Interceptor.WebSocket.Tests/VerifyChecksTests.PublicApi.verified.txt
```

**Consumes:** completed PR #419 behavior.

**Produces:** canonical user model, migration guidance, no-legacy proof, local release evidence, and exact external handoff.

- [ ] **Step 1: Capture documentation RED**

Run:

```bash
rg -n "SyncRoot" README.md docs --glob '*.md'
rg -n "WithContextInheritance|ContextInheritanceHandler" README.md docs --glob '*.md'
rg -n "fallback.*(attach|detach|inherit)|(?:attach|detach|inherit).*fallback" README.md docs --glob '*.md'
```

Expected RED: current canonical/feature guidance still contains public atomic-lock, optional-inheritance, or fallback-as-ownership language, or the migration scan has not yet been reconciled. Save the exact hit list in the Task 7 report. No production behavior changes in this task.

- [ ] **Step 2: Rewrite canonical and feature documentation**

Make `docs/interceptor.md` canonical for private executor locking, lock-free zero-interceptor reads, strict root attach/detach, lifecycle-required recursive ownership, direct ownership readers, parent membership, one domain/route, composition fallback, atomic dynamic metadata, stable errors, and route-free child construction. Feature docs link rather than duplicate. Rewrite Tracking lifecycle internals around Core ownership and exact callback tables. Rewrite connector monitoring's first-fallback ownership guidance and the context-resolution lock/invalidation design. Remove public atomic-lock snapshot guidance.

- [ ] **Step 3: Run legacy and capability scans**

```bash
rg -n "WithContextInheritance|ContextInheritanceHandler|PropertyReferenceSet|_attachedSubjects|IncrementReferenceCount|DecrementReferenceCount" src docs --glob '*.cs' --glob '*.md'
rg -n "SyncRoot" src docs README.md --glob '*.cs' --glob '*.md' --glob '*.txt'
rg -n "AddFallbackContext|RemoveFallbackContext" src docs --glob '*.cs' --glob '*.md'
rg -n "no library-controlled ownership/context/lifecycle operation may fail solely from transient state" docs/superpowers/specs docs/superpowers/plans
rg -n "stack-wide acceptance criterion" docs/superpowers/specs/2026-08-17-single-effective-context-stack-roadmap.md docs/superpowers/specs/2026-08-18-explicit-subject-ownership-design.md
```

Expected: no functional legacy ownership or public `SyncRoot` guidance. Every remaining fallback is explicitly composition. Migration/history references are labeled. The acceptance-criterion sentence occurs exactly once in the roadmap; the #474, #419, #472, and #440 roadmap sections and PR #419 design cross-reference that single clause.

- [ ] **Step 4: Run API and local release gates**

Run `VerifyChecksTests.PublicApi` in Core, Tracking, Registry, Hosting, Connectors, OPC UA, MQTT, and WebSocket and inspect every received file. Then run one at a time:

```bash
dotnet build src/Namotion.Interceptor.slnx
dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"
dotnet pack src/Namotion.Interceptor.slnx
git diff --check
```

Expected GREEN. Public Core changes are the advanced lifecycle facade with constrained route phase, metadata batch/commit registration, distinct `SubjectPropertyMetadata.GetOwnershipValue`, strict APIs/classifier already accepted, generated-consumer `IInterceptorExecutor.AddProperties`, and `SyncRoot` removal. Tracking optional inheritance is removed. No unrelated surface is accepted.

- [ ] **Step 5: Commit permanent docs and accepted snapshots**

```bash
git add README.md docs src
git commit -m "Document explicit subject ownership"
```

- [ ] **Step 6: Freeze exact hashes and obtain independent review**

Record exact master, cleaned PR #474 base, and final PR #419 head. Obtain whole-PR correctness/concurrency/API/performance-shape review and combined-stack simplification review. Any code/doc fix invalidates affected hashes and review evidence.

- [ ] **Step 7: Prepare stable-machine benchmark handoff**

Read `docs/benchmarking.md`. Prepare one identical temporary benchmark patch and record its digest at exact master, cleaned PR #474 base, and final PR #419 head. Exact rows are `InitializedContextZeroReadInterceptors` with generated disassembly, `StructuralWriteStableTopology`, `StructuralWriteNoRouteChangeConservative`, `OwnershipRouteAttach`, `OwnershipRouteDetach`, `OwnershipRouteTransferReparent`, `OwnershipRouteAttachBranchLocalHandlerFallback`, `OwnershipRouteTransferReverseFanoutGreaterThanOne`, one-context structural throughput, two-disconnected-domain serialization, Registry, construction, context depth, and connector/HomeBlaze convoying. Stable-topology rows allocate zero beyond configured application work. Route rows count operation count, bytes, fresh route/state, reverse fan-out snapshots, prospective service-walk/cache/`ImmutableArray`, invalidation generations, and first-use retained capacity. Development-machine numbers are non-authoritative. A repeatable zero-read/scalar/stable-topology regression, route-row allocation count/bytes or throughput regression beyond agreed noise, new avoidable warmed allocation, or operational convoying reopens design.

- [ ] **Step 8: Execute the agreed external Connector Tester gate at final PR #419 hash**

The maintainer/stable machine runs OPC UA, MQTT, and WebSocket chaos profiles for 100 cycles each. Keep all five rotating profiles: `no-chaos`, `server-only`, `client-a-only`, `all-clients`, and `full-chaos`. Set `StructuralMutationRate=1` for server, client A, and client B while retaining ordinary value mutation. No load-profile run is required.

Preserve each run directory with `cycles.csv`, `findings.log`, chaos events, performance/memory artifacts, every `FAIL` log, and failure snapshots/diagnostics. Record machine, exact hash, command/config diff, exit, profile counts, structural/value mutation counts, convergence findings, and post-GC heap trend. Any correctness, quiescence, or leak finding blocks release.

- [ ] **Step 9: Adjudicate external results and finalize**

Accept only exact-final-hash PASS for stable benchmarks and all three Connector Tester runs. Any subsequent source/doc/rebase change requires new hashes and affected review/external evidence. Push only after remote base/head match the recorded reviewed hashes and PR descriptions link retained artifacts.

## Plan Self-Review Checklist

- [ ] Task 1 remains complete and unchanged.
- [ ] Every Task 2 public/internal signature matches the revised spec and internal map.
- [ ] Core owns ledger commit/restart/finalize; the provider has only the exact constrained committed-view route phase and no retry/object payload.
- [ ] Exactly one ownership/topology monitor exists.
- [ ] Every route mutation self-enters the topology gate; each initialized subject has one private executor monitor and no public `SyncRoot`.
- [ ] Every planned public failure is persistent or an explicit programming-contract error.
- [ ] Context mutation, cross-context `TryAddService`, fallback mutation, Registry `AddProperties`, exception deferral, nested cancellation, and array pinning have RED tests.
- [ ] No finite cancellation/retry limit or transient public outcome exists.
- [ ] The broader seven-file audit and obsolete Verify path are in the closed manifest.
- [ ] Tasks 3 through 7 name exact files, RED commands/failures, minimal implementation, GREEN commands, interfaces, and commit boundaries.
- [ ] Every negative blocking assertion has an attempted-entry handshake.
- [ ] Scalar and structural performance gates and intentional two-domain loss are measured.
- [ ] No incomplete step, timing wait, compatibility adapter, or unowned cleanup path remains.
