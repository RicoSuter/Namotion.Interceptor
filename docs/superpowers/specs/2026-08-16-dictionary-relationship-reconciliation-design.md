# Container Relationship Reconciliation Design

Status: Design approved and independently reviewed

## Context

Pull request #458 fixes stale list indices and dictionary keys in registry and parent-tracking metadata. The former design let each consumer repair a mutable copy of relationship metadata. It could partially mutate state when later container inspection failed, and it collapsed repeated references to one relationship per child and parent property.

The replacement models every subject-valued occurrence as an immutable graph relationship. Each executing lifecycle interceptor enumerates a structural property once and owns its canonical sequence. Registry and parent tracking replace complete ordered groups from that sequence and derive their public snapshots from it.

This design is based on current `master`. It has no source, semantic, or merge-order dependency on pull request #419, pull request #440, or the parallel context-cardinality proposal. It preserves master's zero, one, or several resolved `ILifecycleInterceptor` services, their service order, their independently locked processed state, and the existing last-callback ownership limitation when overlapping lifecycle authorities are removed independently.

## Goals

- Make relationship metadata agree with the last successfully enumerated property value after structural operations become quiescent.
- Represent every subject occurrence, including repeated references.
- Preserve source enumeration order and exact key or position metadata.
- Keep lifecycle membership unique per parent property and child within each lifecycle interceptor.
- Share immutable relationships rather than independently mutable index copies.
- Use one linear reconciliation algorithm based only on subject reference identity.
- Finish failure-prone container inspection before graph or relationship mutation.
- Preserve thread safety, immutable public snapshots, handler ordering, and detach visibility.
- Avoid per-occurrence relationship objects in lifecycle-only configurations.

## Non-goals

- Detect direct mutable-container changes without an intercepted property assignment.
- Make arbitrary user containers safe for concurrent mutation during enumeration.
- Publish one atomic snapshot across the whole object graph. The guarantee is quiescent consistency with individually coherent snapshots.
- Add an API for every possible path through a multi-parent graph.
- Replace the lifecycle interceptor's serialized writer model.
- Add adaptive thresholds, a container-kind cache, or unrelated parent-lookup optimization.
- Implement context cardinality, graph movement, parent-context inheritance, hosting state machines, or producer identity.
- Repair master's overlapping-authority ownership limitation.
- Roll back arbitrary side effects from a custom lifecycle handler that violates the exception-free handler contract.

## Terminology

- **Occurrence**: one subject-valued entry produced by structural-property enumeration.
- **Relationship**: an immutable edge containing a parent `PropertyReference`, child `IInterceptorSubject`, and key, position, or `null`.
- **Membership**: reachability of a child through a parent property at least once. It is unique per parent property and child within one lifecycle interceptor.
- **Canonical relationship sequence**: the ordered immutable relationships owned by one lifecycle interceptor for one property.
- **Relationship snapshot**: a public immutable projection such as `SubjectParent`, `SubjectPropertyParent`, or `SubjectPropertyChild`.
- **Quiescent**: no structural write, attach, or detach is in progress, and no unassigned structural mutation has occurred since the last successful reconciliation.

## Behavioral Contract

The public occurrence, ordering, path, reference-count, same-instance refresh, and frozen-snapshot behavior is documented in [Registry](../../registry.md). That document is the user-facing contract; this design defines the internal invariants and publication rules that implement it.

Every supported container is enumerated exactly once per reconciliation:

- A direct subject produces one relationship with a `null` index.
- `IDictionary` entries use their exact enumerated key and subject-valued value.
- Declared read-only dictionaries use their `KeyValuePair<,>` key.
- Other `ICollection` and `IEnumerable` shapes use zero-based enumeration positions, including positions occupied by non-subject values.
- Nulls, strings, non-subject values, and unsupported values produce no relationship.

Occurrences and lifecycle membership remain separate. Repeated references produce repeated ordered relationships, but only the first occurrence adds membership and only removal of the final occurrence removes membership. With one lifecycle interceptor, `GetReferenceCount()` counts distinct parent-property memberships. With several, it remains the sum of master's independently contributing lifecycle references.

## Required Invariants

When the graph is quiescent after successful enumeration and exception-free callbacks:

1. **Property agreement**: processed membership equals the distinct subject references in one complete enumeration. When consumers are enabled, canonical relationships equal every ordered occurrence in that enumeration.
2. **Occurrence preservation**: every subject-valued occurrence has exactly one immutable relationship per executing lifecycle interceptor with consumers enabled.
3. **Outgoing and incoming bijection**: each built-in consumer installs one complete sequence per parent property, and both views contain each installed relationship exactly once.
4. **View agreement**: Registry children, Registry parents, and tracked parents project the same subjects and indices when enabled.
5. **Order agreement**: outgoing relationships follow source order. Incoming relationships within one parent-property group preserve that order.
6. **Membership agreement**: membership exists if and only if at least one occurrence for that parent property and child exists.
7. **Reference-count agreement**: an executing lifecycle interceptor contributes at most one reference per membership. Duplicate occurrences never increment it.
8. **Lifecycle and registry agreement**: duplicate-only changes do not attach, detach, register, or unregister a subject.
9. **No dangling state**: detached parents and children leave no canonical relationships, consumer edges, or processed-property entries.
10. **Snapshot coherence**: each immutable public array is built from one synchronized view and never changes after publication.
11. **Safe publication**: readers see an old or new coherent snapshot, never a torn or partially initialized one.
12. **Event ordering**: ancestor registration, handler order, and `SubjectDetaching` visibility remain intact.

The backing value and metadata may temporarily differ between a backing-store write and its serialized reconciliation. Such an operation is not quiescent.

Master's source-less lifecycle callbacks cannot represent independent authority ownership. If overlapping lifecycle authorities disagree after one is removed, the agreement invariants do not apply until their callbacks agree again. This design neither adds nor repairs that limitation.

## Architecture

### Canonical immutable relationship

Tracking exposes an immutable reference type:

```csharp
public sealed class SubjectPropertyRelationship
{
    public PropertyReference Parent { get; }
    public IInterceptorSubject Child { get; }
    public object? Index { get; }
}
```

The constructor is internal. The type retains reference-identity equality and is not a record. A custom handler may retain an instance after a callback, so removed relationships are never mutated or pooled.

The nth new occurrence of a child matches its nth old occurrence. A matched relationship is reused only when metadata is known unchanged without user equality:

- both direct indices are `null`;
- both positional indices are equal integers; or
- dictionary keys are the same object by reference.

Moved, re-keyed, and unmatched occurrences receive new immutable relationships. Re-boxed value-type keys therefore receive new relationships.

### Processed property state

Each `LifecycleInterceptor` owns processed state under its existing lifecycle lock. The state records distinct child memberships and, when a relationship handler is present, the ordered canonical relationships. It is the old side of future reconciliation and the exact state used for detach.

Lifecycle-only configurations retain compact membership entries with first and last occurrence metadata. The first occurrence drives addition and context-detach metadata; the last drives ordinary reverse-removal metadata. The common direct-child case remains inline. This storage optimization does not create a second algorithm.

Direct subject properties may retain a reference-equal no-op. Enumerable writes reconcile even when the container reference is unchanged.

### Equality-suppressed structural refresh

`PropertyValueEqualityCheckHandler` treats assignment of the same non-string enumerable reference as an explicit structural refresh when the declared property can contain subjects:

1. Resolve one immutable snapshot of internal `IStructuralPropertyRefreshHandler` capabilities.
2. Invoke each built-in capability exactly once in resolver order.
3. Each capability re-reads the backing value under its lifecycle lock and performs normal reconciliation.
4. Return without invoking the rest of the write chain.

The terminal setter is not invoked. `IsWritten` remains false, and property notifications, derived notifications, transactions, and connector writes remain suppressed. Custom lifecycle interceptors without the internal capability receive no refresh callback.

### Relationship consumers and dispatch

```csharp
public interface IPropertyRelationshipHandler
{
    void ReconcileChildRelationships(
        PropertyReference property,
        ReadOnlySpan<SubjectPropertyRelationship> relationships);
}
```

The interface is an ordered multi-service extension contract. It receives the full source-ordered sequence after initial attachment and successful structural writes, and an empty span on property detach. Context handlers run in resolver order, followed by a subject handler. Dispatch continues after failures and rethrows the first exception with its original stack after every handler has run.

For ordinary writes, each lifecycle interceptor captures its relationship handlers before invoking the backing setter. Relationship handlers run synchronously under the lifecycle structural lock. They must be fast, non-blocking, and must not re-enter lifecycle operations.

`SubjectLifecycleChange.Relationship` identifies the immutable occurrence that supplies membership-transition metadata when relationships were materialized. `Index` is always available:

| Transition | Metadata occurrence |
| --- | --- |
| Ordinary membership addition | First new occurrence in source order |
| Ordinary membership removal | Last old occurrence, preserving reverse-removal behavior |
| Context detach | First old occurrence, preserving forward property scan |
| Root context transition | No relationship |

`SubjectDetaching` observes the old relationship group before an empty full-group publication and built-in consumer removal.

### Registry storage

`RegisteredSubjectProperty` stores ordered outgoing relationship references and lazily projects `Children`. `RegisteredSubject` stores incoming relationships grouped by registered parent property and lazily projects `Parents`. Replacing one group preserves unrelated group attachment order.

`SubjectRegistry` holds one relationship-reconciliation gate for a complete full-group callback. It briefly takes the known-subject lock to resolve registered subjects and the parent property, releases it, then updates outgoing and incoming views without nesting their individual locks.

Every reconciliation-reachable subject-keyed registry map uses `ReferenceEqualityComparer.Instance`. Keys remain opaque metadata and are never compared or hashed.

### Parent-tracking storage

Parent tracking stores ordered relationship groups identified by `PropertyReference.Comparer`. `GetParents()` lazily projects them to `SubjectParent`. A private relationship gate covers a complete callback that can update several child groups. Empty and single-parent cases retain inline storage; overflow storage preserves order.

## Reconciliation Flow

All reconciliation performed by one lifecycle interceptor runs under that interceptor's lifecycle lock.

### Stage

1. Re-read the actual backing value after acquiring the lock.
2. Confirm that an ordinary-write parent is attached or that initial attach owns the matching token.
3. Enumerate the value once into ordered descriptors without mutating graph state.
4. Build distinct membership with subject reference identity.
5. Match occurrences by child reference and occurrence number.
6. Stage reverse-order removals, source-order additions, and one complete relationship sequence.

Matching is linear. It never invokes subject equality, dictionary-key equality, or dictionary-key hashing.

### Commit

1. Recheck parent attachment after staging.
2. Apply membership removals in reverse old-occurrence order.
3. Recheck attachment after every callback-bearing transition.
4. Apply membership additions in source order and ledger successful additions.
5. Recheck attachment, then publish the processed state.
6. Dispatch the complete relationship sequence when consumers are enabled.

The same-property reconciliation guard throws `InvalidOperationException` before nested processing can corrupt the baseline. Writes to different properties remain supported.

If a callback re-entrantly detaches the parent, the abort path never publishes staged state. It reverses successfully applied additions, sends an empty sequence to captured relationship handlers, and clears processed state. Already applied removals need no restoration because the parent is detached.

### Initial attach

Initial attach creates a private token under the lifecycle lock and stages every structural property before the first callback. Enumeration failure clears the token without publishing child membership, processed state, or relationship groups.

Membership additions then run in property and source order. Successfully applied additions are recorded, processed states are provisionally installed, and master's existing explicit context attach follows. If the parent remains attached, full relationship groups publish in property order and the token is removed.

Re-entrant context detach cancels the token and reverses its ledger. The token is always cleared in `finally`, including callback failure. Arbitrary callback failures retain master's best-effort behavior from issue #384; a later attach can retry from retained canonical state.

### Detach

Detach reads canonical processed state rather than re-enumerating the backing container. It detaches each distinct membership once, clears relationship consumers, and removes processed state. Unassigned backing-container mutations therefore cannot change which relationships detach.

## Concurrency and Publication

Each lifecycle interceptor's existing lock is its sole processed-state writer lock. Attach, detach, and structural reconciliation within that interceptor are serialized. The backing setter remains outside the lock.

Several lifecycle interceptors never hold each other's locks. Each re-reads the backing value under its own lock and publishes a full group. Consumer gates serialize complete replacements, so completed invocations converge on the final backing value.

When writes race, each locked reconciliation uses the backing value current at its lock acquisition. When a write races with detach, the first lifecycle-lock holder wins that transition. A writer that finds a detached parent publishes nothing; detach uses canonical state and cannot leak an unprocessed child.

Lock order is:

```text
lifecycle lock -> registry relationship gate
               -> registry known-subject lock (released)
               -> one registry view lock
lifecycle lock -> parent-tracking relationship gate
               -> one tracked-parent view lock
```

No consumer getter or relationship-view lock enters lifecycle. Registry never holds the known-subject lock while acquiring a relationship-view lock.

Public projections remain immutable:

- Registry-parent and tracked-parent cache references use `Volatile.Read` and `Volatile.Write`.
- Registry children retain their existing per-view lock.
- Cache construction and group replacement use the owning view lock.
- Published arrays and relationship objects never change in place.

A reader overlapping a write may see an old or new coherent generation. Different public views are not one global transaction and may temporarily differ. They agree after the serialized write and callbacks become quiescent.

Concurrent mutation of a non-thread-safe user container may make its enumeration throw. The library guarantees only that no relationship or membership mutation occurs before enumeration completes.

## Failure Semantics

### Enumeration and key failures

Dictionary keys are opaque and stored exactly as enumerated. Reconciliation never calls their equality or hash methods.

If enumeration or read-only dictionary projection throws, no staged relationship mutation commits. Canonical metadata remains at its previous coherent generation, the backing property may already contain the attempted value, and a later assignment can retry from canonical state.

### Handler and membership failures

Relationship dispatch continues through all consumers and rethrows the first failure afterward, protecting later built-in consumers. A failing custom consumer owns its own partial state.

Lifecycle handlers remain subject to their documented exception-free contract. The no-partial-mutation guarantee covers pre-commit inspection, not arbitrary callback side effects. Initial attach tokens are always cleared, but this design does not add transactional rollback for issue #384.

Each lifecycle interceptor uses master's existing membership operations and publishes the new canonical sequence only after they return successfully. Ordinary-write pipeline and relationship-handler resolution failures occur before the backing setter.

### Allocation failures

Lifecycle descriptor, matching, and membership allocations occur before commit. Consumer projection can allocate during publication. Process-level failures such as `OutOfMemoryException` are not recoverable transactions.

## Public API Impact

Relative to master:

- Remove `IPropertyLifecycleHandler.RefreshCollectionProperty(PropertyReference, object?)`.
- Add `IPropertyRelationshipHandler.ReconcileChildRelationships(PropertyReference, ReadOnlySpan<SubjectPropertyRelationship>)`.
- Add the public, internally constructed immutable `SubjectPropertyRelationship`.
- Add optional `SubjectLifecycleChange.Relationship`.
- Clarify that `GetReferenceCount()` counts parent-property lifecycle contributions, not occurrences.

The PR-specific `SubjectChildReference` and `RefreshChildIndices` APIs do not remain.

The existing `SubjectParent`, `SubjectPropertyParent`, `SubjectPropertyChild`, `GetParents()`, `RegisteredSubject.Parents`, and `RegisteredSubjectProperty.Children` APIs retain their names and types. Their contents intentionally preserve one entry per occurrence. Distinct subjects that override value equality are represented independently.

## Performance and Allocation

The required steady-state complexity is:

- one source enumeration;
- linear subject-reference occurrence matching;
- one immutable relationship per live occurrence and executing lifecycle interceptor when consumers are enabled;
- no second property enumeration in consumers;
- no user equality or hashing during reconciliation; and
- cached, allocation-free public reads after snapshot construction.

Lifecycle-only state uses compact membership storage without per-occurrence relationship objects. Re-keyed and moved occurrences allocate new immutable relationships. Relationships retained by external handlers are not pooled.

Registry provisional publication uses one immutable committed group plus a temporary reference-identity overlay. On its first mutation, the overlay records one state per distinct committed child in O(n). Each later membership removal or addition updates that state and its optional linked-list addition node in average O(1), without inspecting relationship indices.

`Children` lazily projects visible committed occurrences followed by active provisional additions under its view lock. Full publication replaces the committed group, clears the overlay, and invalidates the cache atomically. An unobserved write remains O(n). An external callback that requests a complete snapshot after every membership event can still request O(n squared) total projection work.

The reconciler does not allocate removed-relationship scratch or a duplicate new-subject set. Reverse-old-occurrence membership removal order remains intact.

Further optimization of global consumer gates, high fan-in parent storage, multi-authority execution, HomeBlaze cache generations, or generalized pooling is outside this design.

Targeted comparison uses:

```text
*ChildIndexRefreshBenchmark*
*RegistryBenchmark.AddLotsOfPreviousCars*
*ParentLookupBenchmark*
*ServiceOrderResolverBenchmark.LinearChain*
```

The new benchmark classes must have byte-identical definitions on the benchmark-only master base. Allocation and scaling shape are primary; timing movement inside the unchanged resolver noise row is inconclusive.

The user runs the targeted three-launch comparison on another machine. Local benchmark
status remains pending until those results are supplied. Before merge, the final
implementation must be compared against pinned production base
`868a4d109d53b24805c9ee180efbf5029ee12c1a` with byte-identical benchmark definitions
on the benchmark-only base.

## Acceptance Criteria

The implementation is ready for merge when:

- every required invariant has deterministic proof;
- enumeration failure cannot partially mutate registry or parent state;
- duplicates appear as distinct ordered relationships in every enabled view;
- same-instance reassignment reconciles current container contents;
- hostile subject or key equality and hashing are never invoked;
- re-entrant detach leaves no staged membership or relationship group;
- completed concurrency cases converge without leaks, stale edges, torn reads, or permanently stale caches;
- previously returned arrays and retained relationships remain frozen;
- lifecycle-only graphs allocate no per-occurrence relationship objects;
- master's multi-lifecycle dispatch and ownership limitation remain preserved;
- the non-integration suite and public API verification pass;
- the external targeted three-launch comparison against pinned production base
  `868a4d109d53b24805c9ee180efbf5029ee12c1a` is complete and shows linear scaling with
  no unexplained allocation regression;
- static inspection confirms provisional Registry updates and full replacement are linear when unobserved;
- the cumulative diff contains one reconciliation algorithm and no unrelated optimization; and
- Registry documentation and final pull request metadata describe the semantics and intentional API break accurately.
