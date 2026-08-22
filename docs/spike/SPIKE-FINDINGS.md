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
- `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`: **26 projects, 3299 pre-existing tests, 0 expected failures.**
  - A first baseline run reported 3003 across 21 projects. That figure was wrong. It used `--no-build`, which silently ran only the projects whose binaries were already present and skipped five `Namotion.Devices.*` assemblies worth 295 tests. This is the `--no-build` trap in a form worth naming: it does not only run stale binaries, it can also quietly reduce the set of assemblies that run at all, and the summary looks entirely normal. Always count the project lines, not just the totals.
  - `ChangeQueueProcessorTests.WhenTheTeardownWriteBlocks_ThenStopEndsAtTheConfiguredTimeout` failed once under full-suite parallel load and passes in isolation and on later full runs. It is a pre-existing load-sensitive deadline assertion, not fallout from this work, but it is not reliably reproducible either, so a single failure of that one test should not be read as a regression.
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

**D15. The reachability scan yields an unordered set, but detach order is observable and deterministic today.** Found during implementation design rather than by the review passes. Master releases through `DetachFromProperty`, a recursive descent: the subject is removed and notified first, then its children are collected from `_lastProcessedValues` in `subject.Properties` enumeration order and recursed into (`Tracking/Lifecycle/LifecycleInterceptor.cs:196-268`). Detach callbacks therefore arrive top-down in a deterministic order, and `docs/design/tracking-lifecycle.md` documents consumers depending on it: "each ancestor is deregistered further up the descent before the callback reaches a descendant, so the walk stops at the first one". A mark-and-sweep yields a set, and releasing it by iterating the owned-subject dictionary is nondeterministic, because `Dictionary<K,V>` enumeration order depends on insertion and removal history. The specification constrains only cycle traversal ("cycles use deterministic first-visit traversal"), not subtree release order, while separately listing preservation of current ordering as a goal. Fix: after marking, run a second traversal from the removed edge over committed outgoing edges, visiting only unmarked subjects, and release in first-visit order. That reproduces master exactly and stays deterministic for cycles. It has to be specified, because the obvious implementation is both nondeterministic and wrong.

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

Recorded per stage as the spike proceeds. Test expectation after each stage is 3299 pre-existing tests plus that stage's additions, with zero failures.

## Task 2, singleton context service contracts (`3d13ba3d`)

Landed clean. 266 insertions across five files, no existing test touched, PublicApi diff exactly one line. Validation is keyed on the closed `ISingletonContextService<TContract>` interface via `IsInstanceOfType`, so a different implementation type, the same instance re-registered, and a different `TService` registration generic all conflict as intended. The per-implementation-type contract cache is a static `ConcurrentDictionary` reachable only from the two mutators, so resolution and interception gained no work.

One semantic wrinkle worth knowing, inherent to the existing reentrancy contract rather than new: when a reentrant `TryAddService` factory publishes a service that then conflicts with the factory's own product, the outer call throws but the reentrant registration stays published. Singleton validation makes that asymmetry observable for the first time.

## Task 4, lifecycle ownership rewrite (`069b9bcc`)

Landed with the full suite green: 26 projects, **3347 passed, 0 failed**, build clean. 28 files, +1850 and -580. `PropertyReferenceSet` and its tests are gone, replaced by `SubjectOwnership` plus occurrence-aware edges.

### The most important finding of the spike: correction C3 was itself unsound

C3 said a provisional anchor is cleared by the first inherited structural edge. That is wrong, and it destroys ordinary object graphs.

The failing shape is the everyday back-reference. `child.Parent = root` gives the root an incoming edge. Under the rule as written, that edge clears the root's constructor anchor. The root now has no anchor and nothing else anchors it, so the next removal anywhere in the graph finds it unreachable and releases the entire tree. `root.Self = root` fails the same way. This was not a theoretical objection: it was reproduced with a probe and then caught independently by an existing production-shaped test, `SubjectUpdateCycleTests.WhenModelHasRefsCollectionsDictionariesWithCycles_ThenCreatePartialUpdateSucceeds`.

The refinement that works: **a provisional anchor is consumed only by an edge that provides independent support**, meaning the edge's parent has an anchored ancestor other than the subject itself. That is computed by walking committed incoming edges, which is exact, because reachability from a root means some root lies in the ancestor closure. A self-edge, or a back-edge from the subject's own subtree, fails the test and therefore never consumes the anchor. Cost is the parent's ancestor closure, typically tree depth, with an O(1) fast path when the assigning parent is itself anchored, which is the HomeBlaze adoption shape.

One consequence needs design sign-off: a constructor-attached subject in a mutually referencing pair keeps its anchor forever, because no edge ever provides independent support. `CycleTests.WhenBreakingCycle_ThenBothDetach` changes accordingly and was renamed.

The lesson generalises beyond this rule. C3 was introduced to fix a blocker that the review had proven with evidence, and it was still wrong, because it was reasoned about rather than executed. Only implementing it surfaced the back-reference case. That is the argument for the spike existing at all.

### Detach order on master is an artifact, and changing it moves hosted-service stop order

The detach order that post-descent handlers actually observe on master does not come from `DetachFromProperty`'s descent. It comes from `ContextInheritanceHandler` re-entering `DetachSubjectFromContext`, which re-reads the **backing store**, so handlers behind the descent slot (the testing helper, `SourceMonitor`, `HostedServiceHandler`) see children before parents. The deterministic top-down release traversal required by D15 makes the order uniform, which flips it for those handlers. Six Registry snapshots changed for this reason alone.

The operational consequence is worth flagging separately: **hosted-service stop order on subtree detach flips from children-first to parent-first.** That is not a snapshot detail, and it should be reviewed on its own merits before this design is taken further.

### The transitional promote path silently bypassed the lifecycle

Two related hazards, both fixed and pinned by new tests. `AttachToContext` on a subject whose fallback already exists dedups the `AddFallbackContext` call, so the explicit anchor landed on the executor but never reached the lifecycle. Separately, `ContextInheritanceHandler`'s reference-count-zero fallback removal re-enters `DetachSubjectFromContext`, which was stripping explicit anchors from retained subjects. Both are artifacts of running the old and new models side by side. They are the sharpest argument for the final cutover making attach and detach talk to the lifecycle directly rather than through fallbacks.

### Multi-context aggregation was load-bearing in more places than the review found

P14 identified the multi-`SourceMonitor` paths. The rewrite found more: `PerPropertySubscriptionLifecycleTests`, `SubjectTransactionTests`, six `SourceWaitResultTests`, and `SourceMonitorTests` all relied on one subject participating in two full-tracking contexts, which exact-context authority forbids. All were convertible to single-lifecycle arrangements that preserve each test's pinned intent, but the pattern is more widespread than the blast-radius table suggested.

### Where the reachability scan actually runs

The gate is one short-circuit in `LifecycleInterceptor.cs:566`:

```csharp
var retained = ownership.Anchor != SubjectAnchorKind.None ||
               (count > 0 && IsReachableFromRoots(subject));
```

`count` is the subject's remaining incoming edges after the removal, and `&&` short-circuits, so the scan never runs when `count` reaches zero. That is a logical shortcut rather than a heuristic: an unanchored subject that nothing points at is unreachable by definition, so no proof is required. The scan exists only for the genuinely undecidable case, where remaining edges may all originate from subjects that are themselves dying, which is a cycle or a shared node losing its other parents.

The practical consequence is that **exclusive ownership never pays for the scan**. `AddLotsOfPreviousCars` replaces a thousand cars, each holding exactly one incoming edge; every removal takes the count to zero, releases immediately, and descends into four tires that are each also singly referenced. Five thousand subjects leave the graph with zero scans, doing the same work master's reference-count teardown did. `ChangeAllTires` behaves the same way.

This corrects an expectation recorded earlier in this document, which predicted that every removal row would regress. It also corrects a second slip: an earlier note gave the skip condition as "zero edges and no children", but children never enter the decision, the release simply descends into them.

That is better than the design implied, and it moves where the cost lands. The remaining exposure is that the mark result is cached but invalidated by any graph mutation, so a batch removing k shared or cyclic edges recomputes up to k times: DAG-heavy graphs pay O(k · graph) where master paid O(1) per edge.

### Contract change: interceptors downstream of lifecycle now run inside its lock

The lifecycle gate is taken before `next` for structural writes only, scalar writes untouched, and held across the terminal commit and reconciliation. This is what retires the concurrent-baseline repair, exactly as B4 required. The measured consequence is that `ValidationInterceptor`, when registered without transactions, and any third-party interceptor ordered downstream now execute inside the lifecycle lock. That is a documented contract change and it compounds D10.

### Known gaps left open, deliberately

- Foreign-subject rejection during a root-attach descent throws mid-descent, leaving earlier children attached.
- A released subject keeps running the interceptor chain while its stale constructor fallback survives. Transitional, resolved by the final cutover.
- Garbage islands created by add-only writes persist until an edge removal touches them, which follows from releasing only from the removed edge's target.

## Task 3a, additive attachment mechanism (`5737d0a6`)

The staged approach worked. 1124 insertions, one deletion, and the whole suite stayed green, which the original plan's ordering could not have achieved.

The load-bearing constraint held: `git diff` of `Cache/WriteInterceptorFactory.cs` across the commit is **empty**, so the scalar terminal is byte-for-byte unchanged and gained no branch. The structural route got its own factory and its own per-`TProperty` cache in `ContextState`, and `PropertyWriteContext<TProperty>` gained exactly one `long` field, set only by the structural constructor. The two terminals are duplicated rather than factored, deliberately, because any shared helper would have turned the scalar terminal into a call. That duplication is a maintenance liability and is documented as such at both sites.

Two decisions taken during implementation that the design did not specify:

- The structural terminal holds the attachment monitor **through the commit**, not merely for the check. A check-then-release would leave open exactly the race the route exists to close, an attach landing between the validation and the write it validated. Lock order is `SyncRoot`, then the attachment monitor, never the reverse.
- If the transitional fallback call throws after the new-model transition has been applied, the transition is **not** rolled back. A compensating swap could race a concurrent transition and detach state another thread had already built on, and the old idiom's own partial-failure behaviour is closer to "attached" than to "unattached".

One review finding was sent back rather than accepted, and it turned out to be more than a performance point. The attachment getters each took the private monitor, so `TryGetContext()` cost an uncontended monitor enter and exit per call. That is the predicate the design migrates all ownership checks onto, and Registry's two hot lookups reach roughly 398 call sites through it.

Fixing it produced the most useful empirical result of the spike so far. A test was written that blocks a structural write inside the terminal, so the attachment monitor is provably held, and then reads the attachment state from another thread that already holds a lock of its own. **Against the locked getters that test blocked for its full ten-second window.** Against lock-free reads it returns instantly. That is blocker B1's failure mode reproduced in miniature and caught by a test: a parent-or-context query that takes a lifecycle-adjacent lock deadlocks against a consumer that queries it from inside its own lock. It is direct evidence that C1 is required, not merely prudent, and it generalises: **every query the design routes through lifecycle state must be a lock-free snapshot read.**

The fix stores the context and anchor first as volatile writes and the revision last through `Interlocked.Exchange`, so a reader pairing a revision with subsequently read fields can only ever see fields newer than that revision, which the compare-and-swap then rejects. `AttachmentRevision` uses `Interlocked.Read` because .NET Standard 2.0 also targets 32-bit runtimes where a plain 64-bit load can tear.

State after `e5f12994`: build clean, 26 projects, 3338 passed, 0 failed.

---

# Defects found by auditing the new code

A read-only audit of the lifecycle rewrite, run after the suite was already green, so none of this is caught by existing tests. Two items are serious.

**A1. BLOCKER: a duplicate occurrence is silently dropped when its new position collides with the subject's stale index, and a later write then detaches a live child.**

`LifecycleInterceptor.cs:465` (`HasIncomingEdge` early return in `AttachEdge`) against the addition marking at `:390-398`. Reconcile marks additions as the last excess occurrences *before* retained edges have their indices refreshed at `:420-434`, so an addition is attached with its new index while the subject still carries its old one. When the two coincide the occurrence is dropped: no `IncomingCount` increment, no `IsPropertyReferenceAdded`.

```csharp
parent.Children = [b, a];   // a holds incoming (Children, 1)
parent.Children = [a, a];   // addition marked at position 1, HasIncomingEdge(Children, 1) hits, dropped
                            // a.GetReferenceCount() == 1, but outgoing says [(a,0),(a,1)]
parent.Children = [a];      // removal of occurrence 1 falls back to "first edge of the property"
                            // and drops it: IncomingCount reaches 0 and a detaches
                            // while parent.Children still contains a
```

Incoming and outgoing accounting diverge at step 2 and the divergence detaches a still-referenced subject at step 3. `RefreshCollectionParents` then republishes a parent entry on the detached subject. It generalises to any write where an added occurrence lands on a position the subject already occupied, such as `[A,B,A]` to `[A,A,A]`. Existing tests only cover duplicate shrink and duplicate creation from scratch, neither of which collides.

**A2. MAJOR: holding the lifecycle gate across the terminal write creates a lock cycle with the read path, and correction C4 is what introduced it.**

The rewrite takes `_gate` before `next` (`LifecycleInterceptor.cs:219`) and the structural terminal then takes `subject.SyncRoot` (`Cache/StructuralWriteInterceptorFactory.cs:24,62`), giving gate then SyncRoot. The opposite order already exists and is master's: `Cache/ReadInterceptorFactory.cs:19-22` holds `SyncRoot` across the user's getter body, and a derived getter with a graph side effect reaches the lifecycle and blocks on the gate.

- Thread 1: structural write holds the gate, waits for `SyncRoot` in the terminal.
- Thread 2: reads a derived property, holds `SyncRoot` in the read terminal, its getter reparents a subject, waits for the gate.

This is not speculative. `Change/DerivedPropertyChangeHandler.cs:202-207` and `:249-251` document exactly this hazard and evaluate getters outside the lock specifically "to prevent deadlock with lock(_attachedSubjects) in LifecycleInterceptor when getters have side effects that write to subject-typed properties". That mitigation was built for master's ordering, where `next` ran before the lock. Holding the gate across `next` reintroduces the cycle through `SyncRoot` rather than through `data`.

This matters beyond the bug, because **C4 is mine.** B4 correctly established that lock scope, not terminal ordering, is what retires the concurrent-baseline repair. What the review did not establish is that the required lock scope is reachable at all while the read terminal runs user code under `SyncRoot`. The options are all real changes: stop running getters under `SyncRoot` on the read path, or keep `next` outside the gate and keep the repair, or narrow what the structural terminal locks. This needs a decision before the design can claim the repair is removable.

**A2 update after attempting to reproduce it: downgraded, mechanism as stated does not hold.**

Two probes were built to demonstrate the deadlock and neither reproduced it, on a graph shape designed to force the cycle with matching monitors on both sides. Inspection then explained why. `ReadInterceptorFactory` does hold `subject.SyncRoot` across `innerReadValue` (`Cache/ReadInterceptorFactory.cs:19-22`), but for a generated subject `innerReadValue` is a backing-field read, not user code. `[Derived]` properties are ordinary non-partial properties and are not intercepted reads at all, so a derived getter never executes under `SyncRoot` on the read path. The audit's stated mechanism is therefore wrong for generated subjects.

The inversion is still reachable, but through a narrower door: properties created by `RegisteredSubject.AddProperty` carry a user-supplied `getValue` delegate, and those *do* flow through the read terminal under `SyncRoot`. A dynamic property whose getter has a graph side effect can therefore hold `SyncRoot` while wanting the lifecycle gate, against a structural write holding the gate and wanting `SyncRoot`.

So A2 is real in principle and confined to dynamic properties with side-effecting getters, rather than the general hazard first described. It still needs closing, because that is exactly the shape HomeBlaze's property initializers work in, but it is not the blocker it was reported as. **This is a correction to a claim I passed on without verifying**, and it is the same failure mode the review process was built to prevent: an audit finding is a hypothesis until it is executed.

**Anchor retention, probed.** A mutually-referencing pair of constructor-attached subjects behaves asymmetrically. With `first.Partner = second` then `second.Partner = first`, and both edges then removed: `first` remains attached with reference count 0 forever, `second` detaches. The reason is ordering. `second`'s provisional anchor was consumed when `first` adopted it, because `first` was anchored and therefore provided independent support; `first`'s anchor was never consumed, because by the time `second` referenced it `second` was no longer an independent anchor.

This is not a regression against master, which leaks constructor-attached subjects in the same way, and the lifecycle holds strong references either way. It does mean the provisional anchor only solves the leak **when adoption actually happens**. For the dependency-injection path that motivated it, adoption always happens, so the HomeBlaze blocker stays fixed. For a subject that is constructed with a context and then discarded, or that only ever participates in a cycle, the anchor is permanent and the subject is retained by the lifecycle for the life of the context.

**A3. MAJOR: a foreign-subject claim can throw after the backing store has already committed.** `ThrowIfForeignSubject` at `:232-233` is the stated "rejected before any backing mutation" guarantee, but it samples `AttachedContext` once, and another context's lifecycle holds a different gate and can claim the subject between that check and `ClaimUnownedSubject` at `:802-824`. By then `next` has run, edges and baselines are committed, and the exception escapes the setter with some children attached and some not. `AttachSubjectToContext` at `:114-118` has no pre-pass at all, so a context-taking constructor over a foreign child leaves a half-built graph plus a registered fallback.

**A4. MAJOR: `RefreshCollectionParents` republishes parent entries for subjects released earlier in the same reconcile.** `:436-443`. The index-refresh loop at `:430` guards with `_ownedSubjects.TryGetValue`; the parent refresh does not, and rewrites entries for every subject found in the raw new value. A subject released during this reconcile gets a fresh parent entry after its detach callbacks ran, so `GetParents()` reports a parent for a subject whose `TryGetContext()` is null, which feeds path resolution in `SubjectPathResolver` and the OPC UA and MQTT path builders.

**A5 to A7, minor and partly unverified.** Reusing the pre-`next` edge parse is unsound if an inner interceptor replaces `context.NewValue` with a different instance (no in-repo interceptor does this today). Clearing outgoing edges at `:190-192` mutates graph shape without bumping the version, currently masked because every reachable path bumps it earlier. Re-entering `AttachSubjectToContext` for an already-owned subject re-seeds outgoing edges without diffing, which leaks incoming edges if the collection was mutated in place.

**A8, confirmed design consequence rather than a defect.** The mark cache never hits on the removal path: the removal bumps the version for the incoming change, and the scan traverses anchored roots and outgoing edges, so every removed occurrence forces a full recompute including an O(owned) anchor pre-pass. Removing k children from a graph of n subjects and e edges costs O(k · (n + e)). This is commented as a deliberate correctness-over-reuse tradeoff, and it is exactly what the new shared-parent benchmark rows exist to price.

### Defect fixes, and what fixing them revealed (`128f6f36`)

All three fixed, suite at 3355 passed and 0 failed. Each was driven by a test that failed first.

**A1** is fixed by having reconcile additions bypass the already-present check entirely, through an explicit "this is a known new occurrence" path. Reconcile already knows how many occurrences it intends to add, so consulting a set that still holds stale indices was never necessary. A `[Conditional("DEBUG")]` invariant now asserts, on every reconcile, that each owned subject's per-property incoming edge count equals the committed occurrence count. It ran across all 3355 tests without firing.

**The `RemoveIncoming` first-edge fallback was kept, with evidence.** The question was whether it masked bugs. It does not: a reachable case was constructed where a release descent triggered reentrantly from a removal callback drains committed edges whose refreshed index the target has not adopted yet. Making it throw would leak the edge and keep a child attached to a released parent. Constructing that case also exposed a **sibling defect that was fixed**: parent-entry removal was exact-match only, so the same interleaving left a stale parent entry on a released subject, reproducing U1's shape inside the new code.

**A3** is fixed by claiming every unowned candidate and its entire subtree before any mutation, making the executor attachment the cross-context arbiter so a competing context fails at its own claim step rather than after committing. Claims that the completed operation did not adopt, including vetoed and normalised-away writes, are released, and a rejected attach rolls back the raw registration.

**A4** is guarded, though honestly reported: after the A1 fix no end-to-end route to it survives, because additions always re-attach a new-value subject before the refresh runs. The guard is kept as a cheap invariant rather than a demonstrated fix.

Three further issues were found while fixing and deliberately not fixed:

- Writes on unattached subjects are entirely unintercepted, so a foreign reference can only be detected at attach or assign time. This is what forces the subtree claim walk and is inherent to the design rather than to this implementation.
- A subject that appears both deep inside a removed subtree and in the new collection value is fully detached and then re-attached within one reconcile. The final state is correct, but observers see a spurious detach and attach pair.
- With several lifecycle interceptors on one context, which the current registration API cannot construct, the fallback rollback would not unwind interceptors that had already completed their attach.

One interaction worth recording for anyone touching rollback: a first, broader rollback tripped five seeds of `ContextConcurrencyFuzzTests`, because that model relies on a registration surviving when service resolution itself raises on a delegation cycle. The rollback had to be scoped to attach failures only.

### What the audit checked and found sound

Recorded so it is not re-audited: the inline-to-list promotion and demotion arithmetic in `SubjectOwnership`; termination and correctness of the independent-support anchor rule on two-node and three-node provisional cycles in both construction orders; release-order and cycle-drain termination, including the two-node orphan cycle end to end; the reentrant-release guards in reconcile; pooled collection discipline on every path including exceptional ones; the property-baseline ledger; mark-cache invalidation apart from A6; the anchored-roots subset invariant; the Core attachment seam's publication ordering; and non-colliding collection reorders and shrinks.

# A second silent-tooling failure worth naming

`dotnet test` reported **0 failed while a test project aborted mid-run**. On the generator stage, `Namotion.Interceptor.Registry.Tests` stopped at 106 of 154 tests and the summary still printed a clean result. It was caught only by reconciling per-project counts against a baseline run, exactly the technique that had already been needed once for U7.

The cause is a genuine semantic of the new design. `ConcurrentStructuralWriteLeakTests` performs structural writes on subjects whose attachment another raw thread is transitioning. Those writes now hit the attachment guard's documented `InvalidOperationException`, and an unhandled exception on a raw `Thread` terminates the host process rather than failing a test.

Two things follow. First, **racing structural writes throwing is now user-visible behaviour**, and any consumer doing concurrent structural work on a subject that may be attaching has to expect it. That belongs in the migration notes, not just in a test fix. Second, and more general: on this repository a green `dotnet test` summary is not sufficient evidence that the suite ran. Per-project counts have to be reconciled against a known baseline, because two independent mechanisms have now been observed to reduce coverage while reporting success.

# Unrelated bugs found

Pre-existing defects on `master` that this work surfaced but did not cause. Each is independent of whether the single-context design proceeds.

**Disposition policy:** fix anything the migration already modifies, and record that the fix happened; document the rest so they can be picked up separately. Applied per bug below.

| Bug | Touched by this change? | Disposition |
|---|---|---|
| U1 parent entry leak on duplicate removal | yes, parent tracking is rewritten | **fixed** by occurrence-stable edge identity |
| U2 stale parent indices after reorder | yes, same rewrite | **fixed** by index refresh on collection reconcile |
| U3 orphaned cycle and self-cycle leak | yes, release path is rewritten | **fixed** by reachability release; the pinned limitation snapshot was replaced |
| U4 dead `RootManager` attach | yes, consumer migration rewrites that line | fix during consumer migration |
| U5 MQTT never caches root property mappings | yes, the plan replaces those exact guards | fix during consumer migration, but treat as a behaviour change: it enables caching that has never been on, so it needs MQTT integration verification rather than being waved through |
| U6 `Equals` without `GetHashCode` | yes, the class is deleted | resolved by deletion |
| U7 `--no-build` skipped five projects | tooling, not code | documented only; the corrected baseline is recorded above |

Details follow.

**U1. `GetParents()` leaks a parent entry permanently when a duplicated collection entry is removed.** *Measured.* `root.Children = [a, a, b]` then `root.Children = [b]` leaves `a` fully detached, with reference count 0 and absent from `KnownSubjects`, while `a.GetParents()` still reports `Children@0`. The cause is an index mismatch: attach recorded occurrence 0, and removal iterates the collection in reverse and therefore recorded occurrence 1, so `RemoveParent(property, 1)` never matches `SubjectParent(property, 0)`. The entry is unreachable garbage that also keeps the parent subject alive through the child's `Data` dictionary. Severity is raised by the fact that `GetParents()` is what `SourceScope.SearchGraph` walks, so a stale entry can make a detached subject look in-scope to source monitoring.

**U2. `GetParents()` reports stale indices after any collection reorder.** *Measured.* `root.Children = [a, b]` then `[b, a]` leaves `a.GetParents()` reporting `Index = 0` both times. `ParentTrackingHandler` implements only `ILifecycleHandler`, not `IPropertyLifecycleHandler`, so it never observes `RefreshCollectionProperty`. Registry does refresh, so Registry and `GetParents()` disagree after every reorder. Anything that pairs a parent entry with a collection position is wrong from that point on.

**U3. Orphaned cycles leak, including self-cycles.** The multi-node case is known and snapshot-pinned as a documented limitation (`Registry.Tests/GraphBehavior/CycleTests`). The self-cycle case is not: *measured*, `a.Father = a` then `root.Mother = a` then `root.Mother = null` leaves `a` registered with reference count 1 forever. No test covers it.

**U4. `RootManager`'s attach is dead code.** `HomeBlaze.Services/RootManager.cs:85` calls `Root.Context.AddFallbackContext(_context)`, which always returns `false` because the root was already attached by its context-taking constructor: the context is a dependency-injection singleton and `ActivatorUtilities` selects that constructor. The line has never done anything. It is harmless today and becomes a startup crash under any design that makes repeated explicit attachment throw.

**U5. MQTT never caches property mappings for its own root subject.** `Mqtt/Server/MqttSubjectServer.cs:492,522` and `Mqtt/Client/MqttSubjectClientSource.cs:611,640` guard a cache insert with `RegisteredSubject.ReferenceCount <= 0`, intending to catch a subject that detached during resolution. But *measured*, a root attached through the constructor has reference count 0 permanently, because `AttachToContext` never increments it. So every cache entry for a property on the connector's own root subject is evicted immediately and re-resolved on every single message. This is a live performance bug on the hot message path, not a theoretical one.

**U6. `ContextInheritanceHandler` overrides `Equals` without `GetHashCode`.** `Tracking/Lifecycle/ContextInheritanceHandler.cs:30-33`, with `#pragma warning disable CS0659`. The override is dead: the only dedup path that could use it is a `HashSet<object>`, which consults `GetHashCode` first. Harmless, but it implies a dedup guarantee that does not exist.

**U7. Tooling trap: `dotnet test --no-build` can silently reduce the set of assemblies that run.** A baseline run reported 3003 tests across 21 projects; the true figure is 3299 across 26. Five `Namotion.Devices.*` projects were skipped because their binaries had not been built, and the summary output looked entirely normal. This is distinct from the better-known "runs a stale binary" trap and is more dangerous, because the missing coverage is invisible unless project lines are counted rather than totals.

# Benchmarks

## Comparison base

`LIFECYCLE_BENCHMARK_BASE = 04fab84a`, which is master plus documentation plus the benchmark scaffold and no runtime change. Comparing the finished branch against that hash is therefore a comparison against master for every runtime purpose, while giving both arms identical benchmark source, which BenchmarkDotNet requires in order to produce comparable rows at all.

## Scaffold rows and why each exists

`LifecycleOwnershipBenchmark` carries ten rows. Two matter more than the rest:

- `SetScalarUnattached` paired with `SetStructuralUnattached`. Today the generated setter short-circuits to a direct field write when the executor field is null, allocating nothing (measured: 0 B, 2.8 ns scalar and 4.6 ns structural). The design requires structural writes to observe attachment state before resolving a chain, which forces executor publication on that path. This pair makes the resulting per-write allocation read directly against today's zero. The original plan measured only the scalar half, so the most likely allocation regression in the change was unmeasured.
- `ReplaceSingleChildReference` paired with `ReleaseSmallSubtreeFromLargeContext`. Both remove exactly one edge; the second does it in a context holding roughly 2000 additional retained subjects that the benchmark body never touches. **On master these two rows measure identically** (2554 ns and 2599 ns, 488 B each), which proves removal cost is independent of context size today. The design replaces reference-count teardown with a complete context-local reachability scan on every removal, so the ratio between exactly these two rows after the change is the whole answer on whether the full scan is affordable. No noise-floor argument is needed, because the control is inside the same run and the same class.

## Registry exposure

`RegistryBenchmark` is the row set the maintainer is most interested in, and it will not stay unchanged. Its setup holds roughly 5000 subjects (1000 cars, four tires each).

- Expected flat, or slightly faster from the flattened context, which removes a delegation-target branch and a second volatile state read per access: `Write`, `WriteWithTimestampScope`, `WriteNoOp`, `Read`, `DerivedAverage`, `GetOrAddSubjectId`, `GenerateSubjectId`, `KnownSubjectsSnapshot`.
- Expected slower, because all three remove subjects and therefore pay the new scan against that 5000-subject context: `ChangeAllTires` (removes four tires, so it is the starkest case: constant work today, whole-context work after), `IncrementDerivedAverage` (assigns `PreviousCars = null`), `AddLotsOfPreviousCars`.
- `ReadParents` carries a semantic risk rather than a timing one: if parent snapshots become occurrence-aware, `Parents.Length` changes value for duplicated children, so the row measures something slightly different.

Note that `GenerateSubjectId` is not a valid noise reference despite touching no subject itself, because its class setup builds a thousand tracked cars. `ServiceOrderResolverBenchmark.LinearChain` is the only genuinely insulated control and must be included in the filter.

## Runbook

Environment was verified before running: governor `performance`, `scaling_min_freq` and `scaling_max_freq` both 3600000, `intel_pstate/no_turbo` 1, reported 3600 MHz. The machine must additionally be quiet, so leftover MSBuild and test host processes have to be gone and the load average low before starting.

The run must happen from a worktree **outside** the repository, because BenchmarkDotNet searches for project files and a worktree nested under `.claude/worktrees/` gives it a second candidate, which aborts the run.

Multiple filter patterns must reach PowerShell as an array. Passing them from bash collapses them into one argument that matches nothing, so the invocation goes through `pwsh -Command` with the array written in PowerShell syntax:

```
pwsh -Command "& ./scripts/benchmark.ps1 -Filter '*LifecycleOwnershipBenchmark*','*RegistryBenchmark*','*ServiceOrderResolverBenchmark.LinearChain*' -LaunchCount 3 -BaseBranch 04fab84a"
```

Estimated cost is roughly 15 to 20 minutes per arm for the lifecycle rows plus about 15 minutes per arm for the registry rows, so about an hour and a quarter for the pair. `-Short` decides nothing and must not be used for this.

## CORRECTION: the first two comparison runs were invalid

Both earlier comparison runs, `069b9bcc` against `04fab84a` and `3201a081` against `bench/master-patched`, are **void**. Their HEAD arm served stale binaries, so both reports compared the base against itself. They were internally consistent, complete, and entirely wrong, and the conclusion drawn from them ("no regression on 23 rows") was wrong with them.

Nothing in the tooling reported a problem. What exposed it was a physical impossibility: the large-context shared-child row claimed 1.268 microseconds while an instrumented probe showed the same operation traversing 2003 graph nodes. Those cannot both be true.

**The binary-hash check performed earlier did not validate anything.** It compared builds produced *after* the run, in a different worktree, not the binaries the run actually executed. It gave a false sense of rigour. The only method that proved trustworthy was running each arm directly with `dotnet run -c Release` and cross-checking against a mechanism measured independently.

Practical consequence for anyone repeating this: do not trust a benchmark comparison whose arms agree closely on a row where the change is known to alter the algorithm. Agreement there is evidence of a broken harness, not of a neutral change.

## Results, measured directly per arm

Method: each arm run separately with `dotnet run --project src/Namotion.Interceptor.Benchmark -c Release`, no comparison script. CPU pinned at 3.6 GHz, turbo off, machine quiet.

### The reachability scan, priced

| Row | Patched master | Spike branch | Delta |
|---|---:|---:|---|
| `RemoveOneParentOfSharedChild`, small context | 1.274 us | 1.717 us | +35 percent |
| `RemoveOneParentOfSharedChildInLargeContext`, 2000 retained subjects | 1.262 us | 170.1 us | about 135 times |

Master's two rows are statistically identical, which is the control working: removal cost there is independent of context size. On the branch the same single-edge removal scales with the whole context.

The instrumented probe explains it exactly. Over 1000 toggles of the large-context row the lifecycle performed **1000 reachability recomputes visiting 2,003,000 nodes**, that is a full 2003-node mark per single removed edge. The mark cache never helps, because the removal bumps the graph version immediately before the query, which is finding A8 confirmed by measurement rather than by reading.

### D6 closed: the unattached structural write allocates nothing, because the guard was left off that path

Measured directly per arm, after generated setters began routing subject-capable properties to the structural entry point.

| Row | Patched master | Spike branch |
|---|---:|---:|
| `SetScalarUnattached` | 3.026 ns, 0 B | 2.907 ns, 0 B |
| `SetStructuralUnattached` | 6.732 ns, 0 B | 4.745 ns, 0 B |

The feared allocation regression did not happen, and the reason matters more than the number. The emitted structural helper keeps the existing no-executor short circuit:

```csharp
if (_context is null)
{
    setValue(this, newValue);
    return true;
}
```

So a structural write on a never-attached subject still writes the backing field directly, allocates nothing, and **never reaches the attachment guard**. D6 posed this as a choice between allocating an executor on every structural write of every subject, or leaving a hole on exactly the path the guard exists for. The implementation took the second option without stating it.

That hole is the race the structural route was introduced to close: one thread reads the null executor and writes the field directly while another attaches the subject concurrently, so the write lands without the lifecycle ever seeing it. Master has the identical hole, so this is not a regression, but it is an unmet goal, and the specification currently reads as though the guard covers this case. It does not.

The timing movement in the favourable direction, 6.73 to 4.75 nanoseconds for identical work on both arms, should not be believed from timings. Both helpers reduce to the same two statements when the executor is null, so a 29 percent difference on a sub-ten-nanosecond path is the shape this repository's benchmarking guidance says to attribute by disassembly rather than by more runs. It is recorded because it is favourable and therefore threatens nothing, not because it is understood.

### RegistryBenchmark, measured directly per arm

| Row | Patched master | Spike branch | Delta |
|---|---:|---:|---|
| `AddLotsOfPreviousCars` | 57.27 ms / 19,632,857 B | 59.03 ms / 20,608,482 B | +3.1 percent time, **+975 KB per operation** |
| `IncrementDerivedAverage` | 5001.7 ns | 4742.0 ns | -5.2 percent |
| `WriteNoOp` | 364.5 ns | 349.5 ns | -4.1 percent |
| `Write` | 1018.1 ns | 1029.3 ns | +1.1 percent |
| `WriteWithTimestampScope` | 940.6 ns | 930.4 ns | -1.1 percent |
| `Read` | 377.2 ns | 371.5 ns | -1.5 percent |
| `DerivedAverage` | 245.4 ns | 251.5 ns | +2.5 percent |
| `ChangeAllTires` | 15060.0 ns / 14112 B | 16041.1 ns / 14360 B | **+6.5 percent, +248 B** |
| `GetOrAddSubjectId` | 28.85 ns | 33.29 ns | **+15.4 percent** |
| `GenerateSubjectId` | 1028.8 ns | 1026.4 ns | flat |
| `KnownSubjectsSnapshot` | 0.2847 ns | 0.2863 ns | flat |
| `ReadParents` | 0.3342 ns | 0.3343 ns | flat |

So the earlier claim that Registry is unchanged was wrong twice over: it came from a void run, and the real answer is that Registry does change, modestly. Three effects are worth naming.

`GetOrAddSubjectId` costs 15 percent more. The error bars are 0.027 and 0.064 nanoseconds against a 4.4 nanosecond difference, so this is not measurement noise within either run.

`ChangeAllTires` costs 6.5 percent more and allocates 248 bytes more, despite being a pure tree-shaped removal that never scans. The extra is edge bookkeeping, not reachability.

`AddLotsOfPreviousCars` allocates about 975 kilobytes more per operation, roughly 5 percent. Against 5000 subjects that is about 195 bytes per subject of new per-subject ownership state, which is the occurrence-aware edge structure. Time barely moves, so this is a memory cost rather than a throughput cost, but it is the kind that compounds through GC pressure across a host.

Scalar and read paths are neutral to slightly faster, and `ReadParents` is exactly flat, which confirms the lock-free published parent snapshot did not cost anything on the read side.

**Caveat on the small deltas.** These are single-launch runs. The 135 times scan regression is beyond any doubt, but the 1 to 15 percent movements need a repeat at `-LaunchCount 3` before being treated as real, because this suite is known to swing several percent between identical runs. The allocation columns are deterministic and can be trusted as they stand.

### What this settles

The specification hedged: "Removal operations initially pay a complete context-local reachability scan. Benchmarks determine whether an affected-component index is justified." That question is now answered. The complete scan is affordable only for exclusive ownership, where the `count == 0` short circuit means it never runs at all. For any shared, DAG or cyclic removal it is O(owned + edges) per removed edge, and a batch of k such removals is O(k times the graph).

An affected-component or incrementally maintained reachability index is therefore **required**, not a deferred optimisation. The alternative is to accept that any application with shared subjects degrades quadratically as its graph grows, which for this library's industrial connector workloads is not acceptable.

The `count == 0` short circuit is worth keeping regardless: it is what makes tree-shaped ownership free, and it is why the entire original scaffold measured nothing.

## Results

Run of 2026-08-22, `069b9bcc` against `04fab84a`, `-LaunchCount 3`, memory randomization on, 23 rows, 24 minutes per pair. CPU verified pinned in sysfs before the run: governor `performance`, `scaling_min_freq` and `scaling_max_freq` both 3600000, `no_turbo` 1. The two arms' BenchmarkDotNet headers reported different maxima (1.88 GHz and 0.80 GHz), which is the known unreliability of that header rather than a drifting pin.

**Arm validity was verified rather than assumed**, because near-identical arms are exactly what a broken comparison looks like. At the base the lifecycle is 551 lines and `Namotion.Interceptor.Tracking.dll` is 104,448 bytes with md5 `eb458d79e8dfb609e657d432f87fa420`; at HEAD it is 1224 lines and 113,152 bytes with md5 `ba0197d6209e6a2c4272816ad6510678`. The script drives each arm through `dotnet run -c Release`, which builds, so both arms compiled and ran their own code.

### Headline

**No regression on any of the 23 rows, and allocations are byte-identical on every structural row.**

| Row | Base | Branch | Delta |
|---|---:|---:|---|
| `SetScalarUnattached` | 2.920 ns | 2.928 ns | flat, 0 B both |
| `SetStructuralUnattached` | 4.549 ns | 4.501 ns | flat, 0 B both |
| `SetScalarAttached` | 168.9 ns | 167.2 ns | flat |
| `ReplaceSingleChildReference` | 2607 ns | 2680 ns | +2.8 percent, 488 B both |
| `ReplaceCollectionUniqueChildren` | 8992 ns | 8986 ns | flat, 2248 B both |
| `ReplaceCollectionDuplicateChildren` | 4946 ns | 4951 ns | flat, 1224 B both |
| `ReorderCollection` | 854 ns | 860 ns | flat, 296 B both |
| `ReplaceCyclicChildGraph` | 875 ns | 872 ns | flat, 320 B both |
| `AttachAndReleaseSubtree` | 34155 ns | 34065 ns | flat, 9001 B both |
| `ReleaseSmallSubtreeFromLargeContext` | 2588 ns | 2619 ns | +1.2 percent, 488 B both |
| `RegistryBenchmark.ChangeAllTires` | 14700 ns | 14681 ns | flat, 14112 B both |
| `RegistryBenchmark.AddLotsOfPreviousCars` | 56.44 ms | 56.45 ms | flat |
| `RegistryBenchmark.IncrementDerivedAverage` | 5043 ns | 5002 ns | flat |
| `RegistryBenchmark.Write` | 1034 ns | 1031 ns | flat |
| `RegistryBenchmark.WriteNoOp` | 342 ns | 355 ns | +3.8 percent, no allocation change |
| `RegistryBenchmark.Read` | 369.6 ns | 378.1 ns | +2.3 percent |
| `RegistryBenchmark.ReadParents` | 0.339 ns | 0.342 ns | flat |
| `ServiceOrderResolverBenchmark.LinearChain` (control) | 1760 ns | 1768 ns | +0.4 percent |

The control moved +0.4 percent, so the +1.2 to +3.8 percent movements sit in the same band as unchanged code and none of them clears the noise floor. Nothing here is a real delta.

### The Registry question, answered

`RegistryBenchmark` does **not** change. That contradicts the prediction recorded earlier in this document, and the prediction was wrong for a specific and useful reason.

The reachability scan is skipped for add-only writes, for anchored targets, and for targets left with zero remaining incoming edges. Every removal in `RegistryBenchmark` is tree-shaped: a tire has exactly one parent, so `ChangeAllTires` releases four leaves and never scans. The same is true of `IncrementDerivedAverage` and `AddLotsOfPreviousCars`. The design's cost does not land where the earlier reasoning put it.

`ReleaseSmallSubtreeFromLargeContext` is the direct proof. It removes one edge in a context holding 2000 extra retained subjects and it stays within noise of `ReplaceSingleChildReference`, which removes one edge in a small context, on both arms. The 2000-subject bulk costs nothing after the change, because the scan never runs for that shape.

### What this run does NOT establish

Two gaps, both mine, both worth fixing before anyone treats this as a green light.

**The scan is still unmeasured.** Every row in the scaffold is tree-shaped or a full replacement, which is exactly the family the implementation skips the scan for. The scan runs when a node with remaining incoming edges is questioned, that is on shared, DAG and cycle removals, and the mark result is invalidated by any graph mutation, so a batch removing k shared edges recomputes up to k times: O(k · graph) where master paid O(1) per edge. **No row exercises that.** A DAG-removal row is needed, and because both arms must share benchmark source, adding it means re-cutting `LIFECYCLE_BENCHMARK_BASE` and re-running.

**`SetStructuralUnattached` is not yet meaningful.** It reads 0 B on both arms because the generator has not been changed to route structural properties to `SetStructuralPropertyValue`; that stage was deferred. The row measures the old scalar path on both sides. The allocation regression it was built to catch (D6, executor publication forced on unattached structural writes) remains unmeasured until the generator routing lands.

### What it does establish

The scalar, read, write and tree-shaped structural paths carry the ownership rewrite for free, in CPU and in allocations, against a genuinely different binary. Byte-identical allocation on every structural row is the strongest single signal: the occurrence-aware edge state kept the single-edge case inline, so ordinary one-parent subjects allocate exactly what they did before. That was a design requirement and it held.

## Environment notes

- The spike worktree lives at `.claude/worktrees/spike-single-context`, inside the repository. BenchmarkDotNet searches subfolders for project files and aborts with "Found more than one matching project file" when a worktree is nested, so benchmarks must run from a worktree placed outside the repo.
- The CPU boots throttled to 0.80 GHz. It must be pinned to 3.6 GHz with turbo off before any decision-grade timing run, and the machine must be quiet.
- `RegistryBenchmark` is the row set most exposed to this change. `Write`, `WriteWithTimestampScope`, `WriteNoOp`, `Read` and `DerivedAverage` should be flat or slightly faster from the flattened context. `ChangeAllTires`, `IncrementDerivedAverage` and `AddLotsOfPreviousCars` all remove subjects and therefore pay the new full reachability scan against a roughly 5000-subject context, so they are the rows to watch. `ReadParents` additionally has a semantic risk if Registry parent snapshots become occurrence-aware.
