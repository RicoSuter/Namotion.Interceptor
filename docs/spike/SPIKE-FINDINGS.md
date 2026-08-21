# Spike findings: single-context lifecycle simplification (PR #494)

**Branch:** `spike/single-context-lifecycle`, a temporary branch off `rewrite/single-context-lifecycle`
**PR under review:** https://github.com/RicoSuter/Namotion.Interceptor/pull/494
**Base:** `master` at `0418410c`, unchanged since the PR branch was cut (verified 2026-08-22)
**Purpose:** review the design specification and implementation plan against the real code, correct what is wrong, then implement as a spike to surface problems, fallout, blast radius, and benchmark deltas versus master.

## Status

| Phase | State |
|---|---|
| Spec and plan review | complete, four independent verification passes |
| Corrections to spec and plan | in progress |
| Implementation | in progress |
| Benchmarks vs master | pending |

## Baseline

- `dotnet build src/Namotion.Interceptor.slnx`: green at `1f9366fe`. That commit is docs-only, so this is master's build.
- `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`: 3003 passed, 1 failed.
  - The failure is `ChangeQueueProcessorTests.WhenTheTeardownWriteBlocks_ThenStopEndsAtTheConfiguredTimeout`, which passes in isolation. It is a deadline assertion that misses under full-suite parallel load, so it is a pre-existing baseline flake and not fallout from this work. Later runs must be compared against this baseline rather than against "all green".
- Solution size: 1201 C# files, about 169.5k lines.
- Benchmark comparison base `LIFECYCLE_BENCHMARK_BASE`: see the Benchmarks section. It is master plus documentation plus the benchmark scaffold, with no runtime change.

## Measured blast radius

Textual hits on `\.Context\b|SyncRoot|AddFallbackContext|RemoveFallbackContext|WithContextInheritance|WithParents`, plus files found only by semantic analysis.

| Symbol removed by the design | Files |
|---|---:|
| `.Context` (subject or executor context access) | 79 |
| `SyncRoot` | 17 |
| `AddFallbackContext` / `RemoveFallbackContext` | 32 |
| `WithParents` / `WithContextInheritance` | 37 |
| **Union, solution wide** | **124 (62 production, 62 test)** |

Per project, with the category that matters more than the count:

| Project | Production | Test | Category |
|---|---:|---:|---|
| Registry | 4 | 7 | mechanical, plus nullability decisions at the two hot extension methods, plus removed `AddProperty` synthetic write |
| Connectors | 10 | 11 | semantic decisions, plus a removed capability (multi-monitor paths become unreachable) |
| OPC UA | 3 | 2 | semantic: only the scalar loader path needs the transfer protocol, the collection and dictionary paths do not |
| MQTT | 2 | 1 | semantic: the reference-count guards are cache-eviction race guards, not ownership checks |
| WebSocket | 2 | 1 | mechanical, plus one public `Context` property |
| Hosting | 3 | 1 | semantic: unattached subjects, plus handler ordering migration |
| AspNetCore / GraphQL / MCP / Validation | 4 | 0 | mechanical |
| HomeBlaze.Services | 7 | 3 projects | blocker-grade semantic, see B3 |
| HomeBlaze.Host | 1 | 0 | mechanical |
| ConnectorTester | 2 | 9 | mechanical, but omitted from every plan task and on the critical path for verification |
| Testing | 1 | n/a | removed capability, delete rather than migrate |
| Samples | 8 | 0 | mechanical |

---

# Review verdict

The ownership model the design proposes is a real improvement over master, and most of the specification's characterisation of master is accurate. The specification and the plan as written, however, cannot be executed. Four independent verification passes (Core, Tracking, Registry and consumers, Generator and Dynamic) checked every load-bearing claim against the code and found genuine design defects, not wording problems, plus a task ordering that never compiles.

Everything below carries file:line evidence. Items marked *measured* were reproduced by running code against master.

## Blockers

### B1. `GetParents()` as a lifecycle-state query is an ABBA deadlock against `SourceMonitor`

Spec: "`GetParents()` remains a Tracking extension and queries the authoritative lifecycle state through the subject's current context."

Today `GetParents()` is a leaf read of a per-subject `ParentsSet` cached in `subject.Data`, behind a volatile boxed `ImmutableArray` fast path, with no relationship to the lifecycle lock (`Tracking/Parent/ParentsHandlerExtensions.cs:64-71`, `:76-135`). Two lock orders exist in first-party code today and are safe only because of that:

- lifecycle then monitor: `SourceMonitor.HandleLifecycleChange` runs inside the lifecycle attach lock and calls `OnWaitConditionChanged`, which takes `_lock` (`Connectors/Monitoring/SourceMonitor.cs:90-96`, `:417-420`).
- monitor then graph walk: `OnWaitConditionChanged` deliberately holds `_lock` across the whole pass and calls `IsBranchSynchronized` inside it (`:417-437`), which walks parents through `SourceScope.SearchGraph` (`Connectors/Monitoring/SourceScope.cs:74`). `WaitForSynchronizationAsync` does the same (`:307-312`).

If `GetParents()` starts taking the lifecycle lock, the second order becomes monitor then lifecycle while the first stays lifecycle then monitor. `SourceOwnershipManager` composes into the same cycle (`OnSubjectDetaching` runs inside the lifecycle lock and takes `_lock`, `SourceOwnershipManager.cs:120-138`), and so does `HostedServiceHandler` (`Hosting/HostedServiceHandler.cs:146` takes `lock (_hostedServices)` inside the lifecycle lock and reaches the monitor through `DeferCompletion`). The spec's lock-ordering rule only covers "lifecycle lock before executor monitor" and does not see any of this.

Independently confirmed for this report.

**Correction (C1):** the lifecycle owns parent state but must publish it as an immutable per-subject snapshot that `GetParents()` reads with no lifecycle lock, exactly as `ParentsSet` does today. Single authority is preserved because the lifecycle remains the only writer. It is also cheaper than the spec's version.

### B2. The compile-time structural classifier fails open and cannot be made to agree with the runtime one

Spec: "Generated setters select `SetStructuralPropertyValue` from the declared property type at generation time." Plan: create a Roslyn `SubjectTypeClassifier` mirroring the runtime `SubjectPropertyTypeExtensions`.

*Measured.* The generator emits the `: IInterceptorSubject` base-list entry itself, so at classification time a same-compilation subject symbol does not carry it. Running the generator's own test host:

```
Child.AllInterfaces = []
Child implements IInterceptorSubject (symbol) = False
PlainDerived implements IInterceptorSubject (symbol) = False
PlainDerived has [InterceptorSubject] = False
LibChild (cross-assembly) implements IInterceptorSubject = True
```

The runtime answers `true` for all three. `dynamic` diverges as well: the symbol is `TypeKind.Dynamic` while the runtime `PropertyType` is `object`, which `CanDirectlyHoldSubject` accepts. Unresolvable types fall back to the literal string `"object"` in the extractor. Multi-dimensional subject arrays take the runtime's non-generic `ICollection` fallback, which Roslyn does not synthesise.

The consequence is unstated and silent. A false negative emits the scalar setter, so the attachment-revision guard that this design exists to add is skipped for that property, while `LifecycleInterceptor` still performs structural work because it classifies from metadata. It fails open.

**Correction (C2), also a simplification:** do not mirror the runtime classifier. Invert the test so it fails closed. Emit the scalar route only for types provably incapable of holding a subject (primitives, `string`, `decimal`, `DateTime`, `DateTimeOffset`, `TimeSpan`, `Guid`, enums, and nullable forms of those), and emit the structural route for everything else, including `object`, `dynamic`, interfaces, unresolved types, and every same-compilation type. A false positive costs one branch on a rare property; a false negative is a correctness hole. This deletes the entire mirrored-classifier work item and its test matrix.

### B3. Context-taking constructors as strict explicit roots break HomeBlaze's entire object graph

Spec: `new Subject(context)` "creates strict explicit roots. A second explicit attach on such a subject throws", and "assigning such a child does not implicitly remove its explicit anchor."

HomeBlaze registers the context as a DI singleton (`HomeBlaze.Services/ServiceCollectionExtensions.cs:22`) and deserialises every subject with `ActivatorUtilities.CreateInstance(_serviceProvider, type)` (`ConfigurableSubjectSerializer.cs:83`). `ActivatorUtilities` picks the greediest satisfiable constructor, and the generator emits `(IInterceptorSubjectContext)` for every subject with a parameterless constructor (`Generator/SubjectCodeGenerator.cs:286-292`). So every deserialised subject is already attached at construction. *Measured:* a probe using the real `AddHomeBlazeServices()` container reported the subject as registered before any assignment took place.

Two consequences, both fatal:

1. `RootManager.cs:85` currently calls `Root.Context.AddFallbackContext(_context)`, which is **dead code today**: it returns `false` because the fallback is already present (`InterceptorSubjectContext.cs:125-128`). The plan's replacement, `Root.AttachToContext(_context)`, throws at HomeBlaze startup under invariant 2.
2. Every deserialised child device becomes a **permanent explicit root**. Under invariant 4, removing it from the parent graph never detaches it, so its hosted service never stops and Registry never evicts it. The whole device graph leaks.

The same mechanism reaches `HomeBlaze.Services/SubjectFactory.cs:25` (the UI "add device" wizard) and `Connectors/DefaultSubjectFactory.cs:21-23` whenever a service provider carrying the context is reachable. None of `ConfigurableSubjectSerializer.cs`, `SubjectFactory.cs`, `ServiceCollectionExtensions.cs`, `DefaultSubjectFactory.cs` or `HostedSubjectServiceCollectionExtensions.cs` appears in any plan task.

Note what master does here: the constructor attaches, the later parent edge finds the fallback already present and does nothing, and unparenting calls `RemoveFallbackContext` and detaches. Master's constructor attachment is *silently consumed by the parent edge*. The specification names that behaviour and treats it as a defect to remove, without noticing that it is what makes DI-driven construction work.

**Correction (C3):** a context-taking constructor performs a *provisional* root attachment whose anchor is cleared the first time the subject gains an inherited structural edge in the same context. `AttachToContext` continues to set a real explicit anchor that is never auto-cleared, and duplicate `AttachToContext` still throws. This reproduces master's observable ergonomics, fixes the leak, and removes the need for the specification's temporary-construction try/finally protocol in every loader, which is the single largest simplification available in this change (see S1).

### B4. "Terminal ordering makes the concurrent-baseline repair unnecessary" is false; lock scope is what does it

Spec Migration: "remove the post-writer concurrent-baseline repair model where terminal ordering now makes it unnecessary."

The repair model is three mechanisms in `Tracking/Lifecycle/LifecycleInterceptor.cs`: the getter reread instead of `context.NewValue` (`:306-312`), the post-hoc undo when the parent was detached between `next()` and lock acquisition (`:357-372`), and detach descending from `_lastProcessedValues` rather than the backing store (`:226-232`). Today `next()` runs at `:294`, before `lock (_attachedSubjects)` at `:302`. Chain position is irrelevant to that window: wherever lifecycle sits, the backing write still commits outside the lifecycle lock, so two threads writing the same structural property interleave exactly as before.

What actually closes the race is the specification's own structural-write protocol, which enters the lock at step 3 and calls `next` at step 7. That is a lock-scope change, and terminal ordering is its precondition, not its cause. An implementer following the Migration bullet literally would delete the repair, keep next-then-lock, and reintroduce the exact defect `Tracking.Tests/Lifecycle/ConcurrentWriteLifecycleTests.cs` was written to pin.

The getter reread is additionally not only a concurrency repair: it is the only place the stored value is read back for setters that normalise, which the specification requires elsewhere.

**Correction (C4):** state the requirement as "the lifecycle lock is held across the terminal write", make terminal ordering its precondition, and keep the getter reread with its non-concurrency rationale stated.

## Further design defects

**D5. Removing `SyncRoot` removes the lock that serialises metadata publication.** The specification lists the executor monitor's duties as "terminal reads, writes, context transitions, and attachment-revision checks" and omits `AddProperties`. Today both generated and Dynamic `AddProperties` take `lock (((IInterceptorSubject)this).SyncRoot)` around the read-merge-write of `_properties` (`Generator/SubjectCodeGenerator.cs:193`, `Dynamic/DynamicSubject.cs:39`), the same object as terminal reads and writes. The eleven-step `AddProperties` sequence never names a lock, so two racing batches lose one, and unattached `AddProperties` is explicitly permitted with no lifecycle lock to fall back on.

**D6. A structural write on a never-attached subject has nothing to hold the attachment revision.** The generated scalar fast path is `_context is null` then a direct field write with no write context at all (`Generator/SubjectCodeGenerator.cs:452-461`). The structural setter must capture attachment state before chain resolution, so on that branch it must either force `InterceptorExecutor.GetOrCreate`, allocating an executor for every subject that ever takes a structural write including ones never attached, during construction and deserialisation, or accept an unguarded hole on exactly the path the guard exists for. The specification picks neither. Task 1's benchmark set measured unattached *scalar* writes and not unattached *structural* writes, so the most likely allocation regression in the change was unmeasured; the scaffold has been extended to cover it.

**D7. Two further breaking behaviour changes are undeclared.** The specification says "One narrow observation changes intentionally."
- *Structural writes from lifecycle callbacks work today.* `LifecycleInterceptor.cs:286-291` documents the contract as re-entrant for different properties, forbidding only same-property reconciliation. *Measured:* a handler that sets `root.Mother = extra` during `IsContextAttach` succeeds on master. The specification forbids all of it. That also makes dead the deadlock-avoidance design in `Change/DerivedPropertyChangeHandler.cs:202-207`, `:249-251`, `:375-382`, which exists precisely so derived getters with subject-typed side effects can run inside the lifecycle lock.
- *The inherited context becomes visible earlier.* *Measured* with a `[RunsBefore(typeof(SubjectRegistry))]` probe: on master, inside the first context handler for a child entering via `root.Father = child`, `change.Subject.Context.TryGetService<ISubjectRegistry>()` returns null, because `ContextInheritanceHandler` has not run yet.

**D8. The `GetParents()` timing claim is wrong in kind.** *Measured* resolved order is `[SubjectRegistry, ParentTrackingHandler, ContextInheritanceHandler]`, but `ParentTrackingHandler` is opt-in: neither `WithFullPropertyTracking()` nor `WithRegistry()` registers it, only `WithParents()` and `WithSourceMonitoring()` do (`Tracking/InterceptorSubjectContextExtensions.cs:18-25`, `:171-178`; `Registry/InterceptorSubjectContextExtensions.cs:12-18`), and `Tracking.Tests/Parent/ParentAccessDuringLifecycleTests.cs:89-107` pins that `WithLifecycle()` alone yields an empty `GetParents()`. For the common Registry configuration the change is empty to populated, not later to earlier. The audit also misses `HostedServiceHandler` and three first-party handlers with no ordering attributes at all (`HomeBlaze.Services/Lifecycle/MethodPropertyInitializer.cs:13`, `SubjectPathResolver.cs:18`, `Lifecycle/PropertyAttributeInitializer.cs:17`).

**D9. Occurrence-aware `GetParents()` is new functionality, and master's parent state is already corrupt for duplicates.** The Migration section says only "Keep `GetParents()`". *Measured:* `root.Children = [a,b]` then `[b,a]` leaves `a.GetParents()` reporting `Index = 0` both times while Registry does refresh, so the two disagree after any reorder. Worse, `[a,a,b]` then `[b]` leaves `a` fully detached (reference count 0, absent from `KnownSubjects`) while `a.GetParents()` still reports `Children@0`, a permanently leaked entry: attach fired with `Index = 0`, detach fired with `Index = 1` because removal iterates in reverse. Index-stable occurrence identity has to be built from scratch and has no existing coverage.

**D10. Forcing lifecycle terminal silently reorders `ValidationInterceptor`.** *Measured* chain for `WithFullPropertyTracking().WithDataAnnotationValidation()` is `[PropertyValueEqualityCheckHandler, DerivedPropertyChangeHandler, PropertyChangeInterceptor, LifecycleInterceptor, ValidationInterceptor]`. `ValidationInterceptor` carries only `[RunsBefore(typeof(SubjectTransactionInterceptor))]`, vacuous without transactions, so registration order lands it *inside* lifecycle today. Under terminal ordering it moves outside, changing when a validation throw happens relative to provisional subject claiming. `Tracking.Tests/Change/WritePipelineOrderTests.cs` pins the chain but omits validation.

**D11. `[RunsLast]` would fail silently, and the proposed alternative has no seam.** `OrderWithPartitioning` sorts First, Middle and Last groups independently and `TopologicalSortInto` builds its index from one group only (`Ordering/ServiceOrderResolver.cs:79-92`, `:113-121`). A class-level `[RunsLast]` on a merged `LifecycleInterceptor` would make `SubjectRegistry`'s `RunsBefore(LifecycleInterceptor)` and `SourceMonitor`'s `RunsAfter(LifecycleInterceptor)` find no target in the `ILifecycleHandler` array and be dropped without error, because `ValidateCrossGroupDependencies` never checks a middle service pointing into another group. The plan is right to avoid it but gives a weaker reason. Its alternative, reordering at write-chain construction only, has no existing hook: `Cache/WriteInterceptorFactory.cs:9` consumes the ordered array verbatim, and `WritePipelineOrderTests` asserts on `GetServices<IWriteInterceptor>()`, so reported and executed order would diverge.

**D12. The claim set and the commit set can differ, with no stated resolution.** The structural-write protocol discovers and provisionally claims candidates from the *proposed* value before `next`, then reconciles against the *committed* value re-read through the getter. For any setter that normalises or substitutes, those sets differ, and nothing says the claims for subjects that never landed are released.

**D13. Connectors bind to the concrete `LifecycleInterceptor`, contradicting the third-party-lifecycle claim.** `SourceOwnershipManager.cs:47-53` resolves `TryGetLifecycleInterceptor()`, which is `TryGetService<LifecycleInterceptor>()` on the concrete class (`Tracking/Lifecycle/LifecycleInterceptorExtensions.cs:11-14`), and subscribes to `LifecycleInterceptor.SubjectDetaching` (`LifecycleInterceptor.cs:31`). Neither `SubjectAttached` nor `SubjectDetaching` appears on the proposed `ILifecycleInterceptor` seam. So "a third-party lifecycle package does not need Tracking internals" holds only if you also give up every connector.

**D14. Subject-local service registration with subtree scoping is a tested, documented capability that the specification deletes without naming it.** `Tracking.Tests/ContextInheritanceHandlerTests.cs:142-180` registers a service on `person.Mother`'s own context and asserts it is visible from `person.Mother` and its ancestors but not from `person`. The semantics are documented as load-bearing in production XML docs (`Connectors/InterceptorSubjectContextExtensions.cs:36-39`) and in `docs/interceptor.md:59-68`. There is no first-party production consumer, so removal is safe, but it is a public capability removal that belongs in the breaking-changes list.

## Plan defects

**P1. The tree does not compile from Task 4 through Task 9.** Task 4 removes the fallback APIs but its file list is Core only. Surviving callers at that point: `Tracking/Lifecycle/ContextInheritanceHandler.cs:21,25` (deleted in Task 8), `OpcUa/Client/OpcUaSubjectLoader.cs:280`, `Connectors/Updates/Internal/SubjectUpdateApplier.cs:145`, `SubjectItemsUpdateApplier.cs:229`, `HomeBlaze.Services/RootManager.cs:85` (all Task 10), plus two benchmarks (Task 11). Task 9's own cross-project compile gate is unreachable.

**P2. The Task 3 transition shim throws exactly where the repository uses it.** `Context => Executor.GetContext()` throws while unattached, but the canonical attach idiom is `subject.Context.AddFallbackContext(ctx)` on an unattached subject, 108 occurrences across 29 files, and Core's own tests read `((IInterceptorSubject)subject).Context` on a bare `new Car()` (`Tests/ExecutorPublicationTests.cs:14-15`, `Tests/Context/ContextFunctionCacheTests.cs:23,74,159`).

**P3. The scalar path cannot avoid the attachment-revision check by the prescribed mechanism.** `PropertyWriteContext<TProperty>` is one struct shared by scalar and structural writes, threaded `ref` end to end (`Interceptors/IWriteInterceptor.cs:31`, `:23`). The terminal is cached per interceptor-set under `PropertyTypeIndex<TProperty>`, not per property (`Cache/WriteInterceptorFactory.cs:9`, `InterceptorSubjectContext.cs:511`, `:47-51`), so scalar and structural properties of the same `TProperty` share it and the terminal must branch at runtime unless a second cache array is added to `ContextState`. Neither document mentions that second cache.

**P4. Instance-level service dedup lives inside the fallback walk being deleted.** `InterceptorSubjectContext.cs:716-735` dedups through a `HashSet<object>` before ordering, and `Connectors/InterceptorSubjectContextExtensions.cs:47-58` relies on it today by registering the same `SourceMonitor` instance twice. The specification handles that one case through singleton contracts but says nothing about a user registering the same non-singleton object twice, which resolves once today and twice afterwards.

**P5. The generator change breaks the cross-assembly base contract.** Setters are emitted in both root and derived mode but the accessor helpers only in root mode, so a derived-mode subject calls an inherited helper. Adding `SetStructuralPropertyValue` adds a fifth required shape to `GeneratedMemberTable.AccessorHelpers`, which `SubjectBaseContract.SatisfiesContract` iterates, so every base assembly compiled by the released generator fails the contract and raises NI0012, a build error under `TreatWarningsAsErrors`. Unavoidable, and it belongs in the breaking-changes list.

**P6. The new `AddProperties` seam is silently bypassed by stale bases.** `AddProperties` is emitted into consumer code in root mode only and `SubjectBaseContract` never checks its shape, so a derived subject over a stale base inherits the old body, never reaches the lifecycle seam, and produces no diagnostic. Unlike P5 this fails silently.

**P7. `IInterceptorSubject.Executor` must be an explicit interface implementation.** `DynamicSubjectFactory.CreateSubject` reflects over all properties and turns unknown ones into intercepted properties (`Dynamic/DynamicSubjectFactory.cs:30-49`); `Context`, `SyncRoot` and `Data` are safe today only because they are private explicit implementations. A public or protected `Executor` gives every Castle-proxied subject a phantom property and breaks `DynamicSubjectTests.cs:121-135`.

**P8. Dynamic structural routing lives in Registry, not Dynamic.** Truly dynamic properties never reach Castle: they are created by `RegisteredSubject.AddProperty`, whose setter lambda hard-codes the scalar route (`Registry/Abstractions/RegisteredSubject.cs:337`), as does the boxed path (`PropertyReferenceExtensions.cs:15-17`). Task 3 does not list `RegisteredSubject.cs`.

**P9. `TryGetContext()` allocates.** `IInterceptorSubject.Executor` is non-nullable and lazily published, so asking an unattached subject whether it has a context allocates an executor, while the specification simultaneously tells first-party code to migrate ownership checks from the allocation-free `ReferenceCount > 0` to `TryGetContext() != null`.

**P10. The temporary-construction claim is true for only one of the four named loaders.**

| Site | Master behaviour | Pre-attaches? |
|---|---|---|
| `OpcUaSubjectLoader.cs:277-292`, scalar | attach at `:280`, populate, assign at `:291` | yes |
| `OpcUaSubjectLoader.cs:296-341`, collection | build, assign at `:331`, populate at `:337-340` | no, assign first |
| `OpcUaSubjectLoader.cs:343-384`, dictionary | assign at `:377`, populate at `:379-383` | no, assign first |
| `SubjectUpdateApplier.cs:144-152` | attach at `:145`, populate, assign at `:151` | yes |
| `SubjectItemsUpdateApplier.cs:220-234` | attach at `:229`, populate, return; caller assigns later | yes |
| `SubjectFactoryExtensions.cs:9,15` | reads a service provider only, no attach | no |

So the plan's collection and dictionary bullet describes a change OPC UA does not need and would gratuitously reorder. Where it is needed, `SubjectItemsUpdateApplier.CreateAndApplyItem` is called from four sites and the structural value is only assigned at two commit points, so every temporary root would have to be threaded through with `finally` on the exception and `structureChanged == false` paths. Correction C3 removes this problem entirely.

**P11. The MQTT reference-count sites are cache-eviction race guards, not ownership checks.** `Mqtt/Server/MqttSubjectServer.cs:492,522` and `Mqtt/Client/MqttSubjectClientSource.cs:611,640` add to a cache and then validate. *Measured:* a root attached via the constructor has `ReferenceCount == 0`, so the guard today unconditionally evicts every cache entry for a property on the connector's own root subject, and the mapping is re-resolved on every message. Switching to `TryGetContext() != null` turns on caching that has never been on. That is probably a fix, but the plan describes it as preservation.

**P12. Consumers read `subject.Context` before the subject is provably attached, and each is a semantic decision.** `SubjectSourceBase.cs:180` runs in `StartAsync` and today yields zero monitors for an unattached subject, a documented silent-degradation path (`ISubjectSource.cs:20-23`). `GetContext()` would throw instead. Same shape in every connector constructor initialiser: `OpcUaSubjectClientSource.cs:113-115`, `MqttSubjectClientSource.cs:56`, `WebSocketSubjectClientSource.cs:63`, `WebSocketSubjectHandler.cs:70`, `MqttSubjectServer.cs:89`, `OpcUaSubjectServer.cs:100`, `SourceOwnershipManager.cs:47`.

**P13. Registry's two hot extension methods are the real chokepoint.** `Registry/SubjectRegistryExtensions.cs:97` and `:139` resolve the registry through `subject.Context.TryGetService<ISubjectRegistry>()`, and there are 398 call sites of `TryGetRegisteredSubject`/`TryGetRegisteredProperty`, many on legitimately unattached subjects (`ConfigurableSubjectSerializer.cs:100-127` branches on the null result). Both must become `TryGetContext()?...`. Four lines, not 398, but `GetContext()` there would throw across the codebase.

**P14. The multi-`SourceMonitor` code paths become unreachable and no task removes them.** `SourceMonitoringExtensions.GetSourceMonitor`'s throw branch (`:41-43`), `WaitForAllMonitorsAsync` (`:115-136`), the `CompositeDisposable` (`:84-87`), and `SubjectSourceBase._registeredMonitors` with `UnwindRegistrations` (`:180-211`) all become dead, and five test files become tests of an impossible state.

**P15. Files that no task lists.** `Tests/InterceptorTests.cs:93-105` deliberately registers two `ILifecycleInterceptor`s, casts to `RemoveFallbackContext`, and Verify-snapshots the result, colliding with the singleton contract, with `ILifecycleInterceptor : IWriteInterceptor`, and with the fallback removal at once. `Tests/Context/ContextStateReflection.cs:26-28,46-49` eagerly resolves `ContextState._resolvedTerminal` and `CyclicDelegationMarker` with `?? throw` and fails at type-init once the delegation machinery goes, breaking `ContextFunctionCacheTests`, which Task 4 keeps. `Tracking.Tests/Lifecycle/PropertyReferenceSetTests.cs` reaches the internal struct slated for deletion. `Generator.Tests/SubjectBaseDiagnosticsTests.cs` holds three named NI0014 `SyncRoot` tests plus 24 hand-written base fixtures, and all 16 generator Verify snapshots change. `Tracking.Tests/WriteTimestampTests.cs:168` locks `SyncRoot` externally, the exact pattern the removal cites as its rationale, so it needs redesign rather than porting. `ConnectorTester/Hosting/ConnectorTesterHost.cs:120` plus nine ConnectorTester test files call `WithParents()`.

**P16. Smaller gaps.** `_attachedSubjects` is not reference-count decision state: every ownership decision reads the `PropertyReferenceSet`, and the boxed counter in `subject.Data` is written but never read for a decision. "The existing unique-subject fast path remains" describes code that does not exist: there is one reconciliation path with per-subject retention sets, which is exactly why duplicates collapse. Removing `WithParents()` makes parent maintenance unconditional for every Registry user with no benchmark row for it. Core has four `InternalsVisibleTo` entries, not one, and the Tracking entry cannot be removed because Tracking consumes Core internals this work does not touch (`Change/PropertyChangeInterceptor.cs:192,237`, `Change/DerivedPropertyChangeHandler.cs:367`). `HomeBlaze.Services/Lifecycle/MethodPropertyInitializer.cs:13` has no ordering attribute and hard-throws if it ever resolves ahead of `SubjectRegistry`, which Task 8's constraint-graph rewrite can cause. `Namotion.Interceptor.Testing/ObjectExtensions.cs:9` becomes dead rather than migrated.

## What the reviews confirmed as correct

Recorded so it is not re-derived. The attach call chain is exactly as described, ending in `ContextInheritanceHandler` driving descent through `AddFallbackContext`. `TryAddService` really is predicate-before-factory. The flat context is genuinely cheaper on the hot path, not merely simpler: it removes a delegation-target branch and a second volatile state read per access. `PropertyWriteContext` is a struct passed by ref and never copied per hop, so added fields cost stack width and two stores rather than per-hop copies. Scalar writes really do pay the same lifecycle dispatch on master, and its type test JIT-folds. The attach and detach notification sequences match the specification step for step. `SubjectAttached` and `SubjectDetaching` are already once per transition. The orphaned-cycle limitation is real and snapshot-pinned, and master additionally leaks an orphaned self-cycle with no test at all. Reference count is genuinely not an attachment predicate: a root has count 0. Collapsing SourceMonitor's double registration is behaviour-preserving, since services are a flat array resolved by `OfType` and the `TService` parameter is already decorative. Exactly three production ordering attributes reference the deleted handlers, no fourth. `WithRegistry()` genuinely does not imply parents today, so folding them into `WithLifecycle()` is a capability addition. No production code uses subject-local service registration. `docs/connectors-monitoring.md:93` already documents that cross-tree sharing is broken on master, so the hard rejection replaces a documented silent misbehaviour rather than a working feature. SourceMonitor's post-descent handler position is real and would be preserved by `[RunsAfter(LifecycleInterceptor)]`.

---

# Corrections applied

| ID | Correction | Source |
|---|---|---|
| C1 | Lifecycle publishes an immutable per-subject parent snapshot; `GetParents()` reads it with no lifecycle lock | B1 |
| C2 | Generator classifies fail-closed (scalar route only for provably non-subject types); no mirrored Roslyn classifier | B2 |
| C3 | Context-taking constructors create a provisional root anchor cleared on first inherited edge; `AttachToContext` stays strict | B3 |
| C4 | Requirement restated as "lifecycle lock held across the terminal write", with terminal ordering as its precondition | B4 |
| C5 | Executor monitor's stated duties include metadata publication | D5 |
| C6 | Structural setters force executor publication on unattached subjects; the allocation is stated and benchmarked | D6 |
| C7 | Breaking-changes list gains: callback structural writes, earlier context visibility, subtree-scoped services, base-contract rebuild (NI0012), `ValidationInterceptor` reordering | D7, D10, D14, P5 |
| C8 | Task order restructured so every commit compiles (see below) | P1, P2 |
| C9 | A second per-`TProperty` terminal cache for the structural route, keeping the scalar terminal unchanged | P3 |
| C10 | `GetParents()` occurrence identity is new work with new tests, not "keep" | D9 |

## Simplifications adopted

The request was to keep the resulting code as minimal as possible without giving up performance or concurrency correctness. These are the reductions the review made available.

- **S1. The temporary-construction try/finally protocol disappears** from every loader and update applier, because C3 makes the constructor anchor self-releasing. This removes the plan's hardest migration item, which would otherwise have threaded temporary roots through four call sites to two commit points with `finally` on every path.
- **S2. The mirrored Roslyn classifier disappears** (C2), along with its test matrix. A short "provably scalar" predicate replaces a faithful reimplementation of a 227-line runtime classifier.
- **S3. Multi-instance machinery becomes deletable** once singleton contracts land: the monitor-count throw branch, `WaitForAllMonitorsAsync`, the composite disposable, and `SubjectSourceBase`'s monitor array with its unwind path (P14). This is net-negative production code.
- **S4. Fallback deletion is genuinely large**: about 670 of 1088 lines of `InterceptorSubjectContext.cs`, roughly 62 per cent, are delegation, walk, cycle-detection and cross-context invalidation machinery. Four things inside it are not fallback machinery and must survive: the per-frame `ServiceOrderResolver` call, instance dedup, the `PublishState` exchange (whose stated rationale evaporates and must be rewritten rather than carried over), and `ContextState.IsEmpty`.
- **S5. `ContextInheritanceHandler` and `ParentTrackingHandler` collapse into the lifecycle**, removing two handler classes, two configuration extensions, and the ordering-cycle hazard that the pair created.
- **S6. `Namotion.Interceptor.Testing/ObjectExtensions.cs` is deleted rather than migrated**, and `PropertyReferenceSet` with its test file goes with the reconciliation rewrite.

Anti-simplifications, recorded so they are not mistaken for cleanup opportunities: the getter reread must stay (B4), instance dedup must stay (P4), and the concurrent-baseline repair may only be deleted together with the lock-scope change, never before it.

## Restructured task order

The original order never compiles. The corrected order keeps every commit green.

| New | Was | Change |
|---|---|---|
| 1 | 1 | Benchmark scaffold. Extended with an unattached structural write row (D6) and a small-removal-in-large-context row to measure the reachability scan. |
| 2 | 2 | Singleton contracts. Unchanged. |
| 3 | 3 + 4 | **Merged.** Attachment state, structural write route, and the flat context land together, because the shim cannot bridge them (P2). Consumers migrate to `TryGetContext()` in the same commit, since the fallback APIs vanish here (P1). |
| 4 | 5 | Lifecycle ownership, with C1's snapshot-published parents. |
| 5 | 6 | Concurrency, with C4's lock scope stated correctly. |
| 6 | 7 | `AddProperties`, with C5's publication lock. |
| 7 | 8 | Handler merge and Registry projection. |
| 8 | 9 | Singleton authorities, plus S3's dead multi-instance removal. |
| 9 | 10 | Remaining consumer migration. Much smaller after C3 removes the transfer protocol. |
| 10 | 11 | Transitional removal. |
| 11 | 12 | Snapshots and docs. |
| 12 | 13 | Benchmarks and verification. |

Each task additionally gains a simplification gate: before committing, delete what the change made dead, and confirm the diff is net-negative wherever the task's purpose was removal.

---

# Implementation fallout

_pending_

---

# Benchmarks

_pending_

## Environment notes

- The spike worktree lives at `.claude/worktrees/spike-single-context`, inside the repository. BenchmarkDotNet searches subfolders for project files and aborts with "Found more than one matching project file" when a worktree is nested, so benchmarks must run from a worktree placed outside the repo.
- The CPU boots throttled to 0.80 GHz. It must be pinned to 3.6 GHz with turbo off before any decision-grade timing run, and the machine must be quiet.
- `RegistryBenchmark` is the row set most exposed to this change. `Write`, `WriteWithTimestampScope`, `WriteNoOp`, `Read` and `DerivedAverage` should be flat or slightly faster from the flattened context. `ChangeAllTires`, `IncrementDerivedAverage` and `AddLotsOfPreviousCars` all remove subjects and therefore pay the new full reachability scan against a roughly 5000-subject context, so they are the rows to watch. `ReadParents` additionally has a semantic risk if Registry parent snapshots become occurrence-aware.
