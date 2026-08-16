# Container Relationship Reconciliation Design

Status: Design approved; awaiting written-spec review

## Context

Pull request #458 fixes stale list indices and dictionary keys in registry and parent-tracking metadata. Its current implementation enumerates a structural property once, then lets each consumer repair its own stored index copies. The registry uses an adaptive scan or rebuild algorithm, and parent tracking performs a second refresh over another set of copied values.

That approach has three problems:

1. The registry, registry parent view, and tracking parent view each own an independently mutable copy of the same relationship index.
2. The adaptive scan mutates relationships before reconciliation is known to succeed. A later dictionary key operation can throw and leave a permanently partial update.
3. A container occurrence is collapsed to one relationship per child and parent property. A dictionary that contains the same subject under two keys therefore cannot be represented faithfully.

The replacement models every subject-valued container occurrence as a graph relationship. Lifecycle tracking enumerates the container once and owns the canonical relationship handles. Registry and parent tracking keep ordered references to those handles and derive their public snapshots from them.

This is a breaking release, but the commonly consumed registry and parent snapshot APIs will retain their existing names, return types, and value semantics.

## Goals

- Make registry and parent metadata match the successfully enumerated property value after structural operations become quiescent.
- Represent every subject-valued dictionary, collection, and direct-property occurrence, including duplicate references to the same subject.
- Preserve container enumeration order and exact dictionary key or collection index metadata.
- Keep lifecycle membership distinct from occurrence relationships so duplicate entries do not duplicate attach and detach callbacks.
- Have one authoritative mutable index per live relationship rather than independent registry and parent-tracking index stores.
- Use one linear reconciliation algorithm based only on subject reference identity.
- Preserve thread safety, lock-free cached public reads, existing lifecycle handler ordering, and existing detach visibility.
- Finish enumeration and all failure-prone container inspection before mutating relationship state.
- Keep the final pull request focused and substantially smaller than the current adaptive implementation.

## Non-goals

- Detect mutations made directly to a mutable container when its property setter is not invoked.
- Make arbitrary user collection implementations safe for concurrent mutation during enumeration.
- Provide an atomic snapshot of the entire object graph across many properties. The guarantee is quiescent consistency with individually coherent public snapshots.
- Add an API that returns every possible path through a multi-parent graph.
- Change singular path selection beyond making it deterministic.
- Introduce per-property writer locks or otherwise replace the lifecycle interceptor's existing serialized writer model.
- Include the unrelated container-kind cache optimization or parent-lookup scenarios not reached by this relationship redesign.
- Preserve state inside a custom lifecycle handler that violates the documented exception-free handler contract.

## Terminology

- **Occurrence**: One subject-valued entry produced by enumerating a structural property. Two dictionary keys that point to the same subject are two occurrences.
- **Relationship**: The graph edge for one occurrence. It contains the parent `PropertyReference`, child `IInterceptorSubject`, and current dictionary key, collection index, or `null` for a direct property.
- **Membership**: The fact that a child is reachable through a parent property at least once. Membership is unique per `(parent property, child subject)` pair.
- **Relationship snapshot**: A public immutable value such as `SubjectParent`, `SubjectPropertyParent`, or `SubjectPropertyChild`, derived from the current relationship state.
- **Quiescent**: No structural write, context attach, or context detach is in progress, no interceptor invocation has written a backing value and is still waiting to reconcile it, and no structural value has been changed outside an intercepted assignment since its last successful reconciliation.

## Behavioral Contract

### Exact occurrence representation

For this dictionary:

```csharp
var values = new Dictionary<string, Node>
{
    ["alpha"] = child,
    ["beta"] = child,
};
```

the property has two child relationships in dictionary enumeration order:

```text
property --["alpha"]--> child
property --["beta"] ---> child
```

The child's registry parents and `GetParents()` result contain both incoming relationships. Removing `"alpha"` removes only the first relationship. The child loses membership in the property, and receives its property-reference removal callback, only when the final occurrence is removed.

### Container shapes

The reconciliation preserves the existing supported-shape rules:

- A direct subject produces one relationship with `Index == null`.
- `IDictionary` values are inspected and subject-valued entries use the exact enumerated key.
- Declared read-only dictionary shapes use their `KeyValuePair<,>` keys.
- Other `ICollection` and `IEnumerable` shapes use the zero-based enumeration position, including positions occupied by non-subject values.
- `null`, strings, non-subject values, and unsupported values produce no relationship.

Every supported container is enumerated exactly once per reconciliation. The resulting order is used by lifecycle membership, registry children, registry parents, and tracking parents. Consumers do not enumerate the property again.

### Ordering

`RegisteredSubjectProperty.Children` mirrors every subject-valued occurrence in source enumeration order.

Within one parent property, incoming parent relationships for the same child follow source enumeration order. Across different parent properties, parent-property groups preserve their existing attachment order. Reordering one container does not reorder unrelated parent-property groups.

### Path behavior

The same child can have several valid paths from one parent. Without an explicit root, existing singular APIs such as `TryGetPath()` return the path through the first current parent relationship. With an explicit root, the existing depth-first search returns the first relationship sequence that reaches that root. Within duplicate occurrences under one property, either rule selects the first occurrence in current container enumeration order. Existing cycle detection continues to use subject identity so duplicate edges do not defeat it.

This pull request does not add `GetAllPaths()`. Such an API can be designed separately if needed.

### Reference counts and lifecycle events

`GetReferenceCount()` continues to count distinct parent-property memberships, not occurrence relationships. Existing attach and detach callbacks also remain membership based:

- The first occurrence for `(property, child)` causes a property-reference addition.
- Additional occurrences for that pair add relationships but do not attach the child again.
- Removing an occurrence while another remains removes only that relationship.
- Removing the final occurrence causes a property-reference removal.
- Context attachment and detachment continue to occur only when the subject enters or leaves the graph.

The public documentation for reference count will say "distinct parent-property references" to remove the current ambiguity.

### Snapshot semantics

The existing high-level public results remain coherent point-in-time snapshots:

- `GetParents()` continues to return `ImmutableArray<SubjectParent>`.
- `RegisteredSubject.Parents` continues to return `ImmutableArray<SubjectPropertyParent>`.
- `RegisteredSubjectProperty.Children` continues to return `ImmutableArray<SubjectPropertyChild>`.

Previously returned arrays never change. A caller obtains current keys, indices, membership, and ordering by reading the property again. This avoids a captured immutable array acquiring current keys while retaining an old order.

The existing record structs retain their constructors, properties, and value equality. Their values are projections, not authoritative internal relationship storage.

## Required Invariants

When the graph is quiescent after successful enumeration and exception-free attach and detach callbacks, all of these invariants hold:

1. **Property agreement**: each structural property's processed membership state equals the distinct subjects in one complete enumeration of the property's current backing value. When relationship consumers are enabled, its canonical relationship sequence also equals every ordered subject occurrence in that enumeration.
2. **Occurrence preservation**: when relationship consumers are enabled, every subject-valued occurrence has exactly one relationship, including repeated references to the same subject.
3. **Outgoing and incoming bijection**: every materialized relationship occurs once in its canonical parent-property sequence and once in each enabled built-in consumer's corresponding incoming view.
4. **View agreement**: registry children, registry parents, and tracking parents project the same relationship subject and index values when those features are enabled.
5. **Order agreement**: outgoing registry relationships have the same order as source enumeration. Incoming relationships within one parent property have that same relative order.
6. **Membership agreement**: `(property, child)` membership exists if and only if at least one relationship for that pair exists.
7. **Reference-count agreement**: a subject's lifecycle reference count equals its number of distinct parent-property memberships.
8. **Reachability agreement**: every reachable subject is attached and registered when the registry is enabled; a subject with no root attachment and no parent-property membership is detached and unregistered.
9. **No dangling state**: detached parents and children leave no canonical relationships, registry edges, parent-tracking edges, or processed-property entries behind.
10. **Snapshot coherence**: each returned immutable array was built from one internally synchronized view. It is never mutated after publication.
11. **Safe publication**: readers observe an old coherent snapshot or a newer coherent snapshot, never partially initialized arrays or torn index references.
12. **Event ordering**: existing ancestor-registration guarantees, handler ordering, and `SubjectDetaching` graph visibility remain intact.

The backing property and metadata may temporarily differ while a write is between its backing-store update and serialized reconciliation. All writes that reach that window are considered in progress for the definition of quiescence.

## Architecture

### Canonical relationship handle

The Tracking assembly introduces a sealed `SubjectPropertyRelationship` reference type. It has an internal constructor and exposes read-only properties:

```csharp
public sealed class SubjectPropertyRelationship
{
    private object? _index;

    public PropertyReference Parent { get; }
    public IInterceptorSubject Child { get; }
    public object? Index => Volatile.Read(ref _index);

    internal void SetIndex(object? index) => Volatile.Write(ref _index, index);
}
```

`Parent` and `Child` never change. `Index` is changed internally with `Volatile.Write` and read with `Volatile.Read`, so the object reference is never torn and successful publication establishes the required memory ordering. The class retains reference-identity equality. It is not a record and is not suitable as a value-based hash key.

One handle exists per live occurrence when at least one relationship consumer is enabled. A retained occurrence reuses its handle. A removed handle is never pooled or reused because an advanced custom handler may retain it. Once removed, its final state remains readable but it is no longer present in any current relationship collection.

These handles are live advanced-handler objects, not public relationship snapshots. A retained handle's `Index` can change during a later reconciliation. The high-level immutable arrays described above copy the current handle values and remain frozen.

### Processed property state

`LifecycleInterceptor` replaces `_lastProcessedValues` as the structural baseline with processed-property state. Every state records the distinct child memberships produced by the last completed reconciliation. When a relationship consumer is enabled, it additionally contains the ordered canonical relationship handles.

This state, rather than a previous mutable container reference, is the old side of the next reconciliation. It therefore supports an in-place-mutated dictionary or collection that is assigned back through the property setter. It is also the exact state to detach if the parent leaves the graph.

Lifecycle-only configurations do not allocate a relationship object per occurrence. They retain only the distinct child membership references required for later diff and detach, using an inline-first representation for the common direct-child case. Full occurrence handles are materialized only when the context or subject has an `IPropertyRelationshipHandler`. This is a storage optimization, not a second reconciliation algorithm.

A direct subject property retains a reference-equality no-op when its canonical membership already matches. Enumerable container writes always reconcile, even when the container instance is reference-equal to the previously observed value.

### Equality-suppressed container refresh

`PropertyValueEqualityCheckHandler` currently sits outside lifecycle tracking and suppresses a setter call when the old and new container are the same reference. The new behavior treats such a setter call as an explicit structural refresh without turning it into a value change:

1. Before calling `EqualityComparer<TProperty>.Default`, the equality handler checks whether the current and new values are the same non-string `IEnumerable` reference and the declared property can contain subjects.
2. If so, it invokes a dedicated lifecycle refresh entry point for every resolved lifecycle interceptor and then returns without calling the remaining write chain.
3. Lifecycle refresh re-reads the backing value under its writer lock and runs normal relationship reconciliation.
4. Otherwise the equality handler retains its existing comparison and write-suppression behavior.
5. For a structural refresh, the terminal setter is not invoked, `PropertyWriteContext.IsWritten` remains false, and property-change, derived-change, transaction, and connector-write behavior remains suppressed exactly as for other equal values.

This also works with aggregated contexts because the outer equality handler explicitly refreshes every resolved lifecycle interceptor once. Other equality-suppressed values retain their existing no-op behavior.

### Relationship consumers

Relationship reconciliation is separated from property attach and detach lifecycle callbacks through a focused interface:

```csharp
public interface IPropertyRelationshipHandler
{
    void ReconcileChildRelationships(
        PropertyReference property,
        ReadOnlySpan<SubjectPropertyRelationship> relationships);
}
```

It replaces `IPropertyLifecycleHandler.RefreshCollectionProperty` from master and the PR's `RefreshChildIndices`. It is invoked with the full current ordered sequence:

- after initial attachment of a structural property,
- after every successfully enumerated structural property write,
- with an empty span when the property is detached.

Registry and parent tracking implement this interface and store ordered relationship handles internally. They do not own mutable index values. Their existing public immutable arrays remain lazily cached projections of those handles and are invalidated under the same consumer lock that protects relationship-list replacement.

The dispatcher invokes every relationship consumer even if one throws, records the first exception, completes built-in reconciliation, and then rethrows the first exception with its original stack. This prevents one custom consumer from stranding later built-in consumers. A failing custom consumer remains responsible for its own state, consistent with the lifecycle handler contract.

`SubjectLifecycleChange` gains an optional `Relationship` property. For a membership addition or removal, it identifies the first occurrence that drives that membership transition. Registry and parent tracking use it to preserve the relationship that existing `SubjectAttached` and `SubjectDetaching` observers can see before the final full-property reconciliation. Root context changes have no relationship.

### Registry storage

`RegisteredSubjectProperty` stores the ordered outgoing relationship handles. Its `Children` cache projects each handle to `SubjectPropertyChild`.

`RegisteredSubject` stores incoming relationship handles grouped by registered parent property. Its `Parents` cache projects them to `SubjectPropertyParent`. Reconciliation replaces the complete group for the changed parent property, preserving other property groups and their attachment order.

The registry relationship callback first resolves all subjects and the registered parent property while holding the registry's known-subject lock. It then releases that lock and updates individual outgoing and incoming collections without nesting their locks. Lifecycle serialization guarantees a single relationship writer, while each collection lock prevents a reader from publishing a stale cache after the corresponding replacement.

The registry never acquires the lifecycle writer lock. Lock order remains lifecycle, registry known-subject state, then individual relationship-view locks, with no reverse acquisition.

### Parent-tracking storage

Parent tracking replaces its `HashSet<SubjectParent>` with ordered relationship-handle groups. A group is identified by `PropertyReference.Comparer`, and occurrences within the group retain container order. `GetParents()` projects those handles to the existing `SubjectParent` values.

The common empty and single-parent cases retain inline-first storage, with ordered overflow storage allocated for additional relationships. No adaptive threshold or alternate reconciliation algorithm is permitted.

## Reconciliation Algorithm

All structural reconciliation runs under the existing lifecycle `_attachedSubjects` lock.

### Stage phase

1. Re-read the property's actual backing value after acquiring the lifecycle lock. This preserves the existing last-writer convergence behavior when `next()` calls race outside the lock.
2. If the parent is no longer attached, discard the write without creating relationship state. The lock prevents it from becoming attached or detached between this check and reconciliation.
3. Enumerate the value exactly once into an ordered temporary descriptor list. No graph state changes during enumeration.
4. Build the new distinct-membership set by subject reference identity and compare it with the processed property's old membership state.
5. When relationship consumers are enabled, match old relationship handles to new descriptors by child reference identity and occurrence number. The first new occurrence of a child matches its first old occurrence, the second matches the second, and so on.
6. Stage new handles for unmatched descriptors, removed handles left unmatched, pending index assignments for retained handles, the new ordered relationship sequence, and all membership transitions.

Matching is linear and uses only reference-identity hashing for subjects. It never invokes subject equality, dictionary-key equality, or dictionary-key hashing. Temporary maps and lists may be pooled after correctness is established, but the implementation has one algorithm for all collection sizes.

### Commit phase

1. Process membership removals in the existing reverse-detach order. `SubjectDetaching` still observes the old graph, and lifecycle removal handlers remove all occurrences for the departing `(property, child)` membership.
2. Process membership additions in source order. The first new occurrence supplies the lifecycle change's index. Existing registry-before-descent ordering remains unchanged.
3. Apply staged index assignments to retained handles and publish the new canonical ordered relationship sequence when relationship consumers are enabled. Otherwise publish only the new distinct-membership state.
4. When enabled, invoke all relationship consumers with the full sequence. Consumers replace their complete group for this property, so provisional first-occurrence entries from membership callbacks become the exact occurrence set.
5. If re-entrant activity detached the parent, do not restore its processed-property state. The detach path owns cleanup in that case.

No collection enumeration, key equality, key hashing, or subject equality runs in the commit phase. The only supported re-entrancy remains writes to different properties. A same-property reconciliation guard throws a clear `InvalidOperationException` instead of allowing baseline corruption.

### Initial attach

Initial attachment runs the same stage and commit pipeline from empty old state. The first occurrence of each unique child drives membership attachment and subtree descent. When relationship consumers are enabled, the final relationship callback publishes all occurrences, including duplicates, in exact source order.

### Detach

Detach reads canonical processed-property state rather than enumerating the current backing container. It detaches each unique child membership once, clears the property relationship consumers when present, and removes the processed-property state. This ensures that a concurrent or un-intercepted backing-store mutation cannot cause the lifecycle to detach relationships it never attached or retain relationships it did attach.

## Concurrency and Publication Model

### Serialized writers

The existing lifecycle lock remains the sole relationship writer lock. Context attach, context detach, and structural property reconciliation are serialized. The backing-store write through `next()` remains outside the lock.

When two threads write the same property, whichever thread acquires the lifecycle lock first re-reads and reconciles the backing value that is current at that point. A later interceptor invocation re-reads again. When all invocations complete, canonical relationships match the final backing value.

When a write races with parent detach, the operation that acquires the lifecycle lock first wins that serialized transition. A writer that finds the parent detached performs no relationship attachment. A detach uses canonical relationship state, not the possibly newer backing value, and therefore cannot leak an unprocessed child.

### Lock-free readers

Public parent and child getters retain their cached immutable-array model:

- Cache references are read and published with `Volatile.Read` and `Volatile.Write`.
- Cache construction and relationship-list replacement use the same per-view lock.
- Published arrays are never changed in place.
- A relationship's current index reference uses volatile access.

A reader overlapping reconciliation may receive the previous snapshot or a newly built snapshot while the write is active. The writer invalidates or replaces every affected cache before returning. A later reader after quiescence therefore obtains metadata derived from the committed relationship sequence.

Different public views are not published as one global transaction. During a write, a registry child reader and a parent-tracking reader can temporarily observe different generations. After the serialized write and all callbacks finish, the view-agreement invariant holds. This is the repository's established quiescent-consistency model.

### External container mutation

The library cannot make a non-thread-safe user collection safe when another thread mutates it during enumeration. Such enumeration can throw. The reconciliation still guarantees that no relationship or membership mutation occurs before enumeration finishes. Applications requiring concurrent in-place container mutation must synchronize it or assign an immutable replacement.

Direct in-place mutations with no intercepted property assignment are invisible. Assigning the same container reference through the intercepted setter is an explicit refresh and is supported.

## Failure Semantics

### Enumeration and key failures

Dictionary keys are treated as opaque metadata. Reconciliation does not call their `Equals` or `GetHashCode` implementations. A key object is stored exactly as enumerated.

If container enumeration or read-only dictionary projection throws, no staged relationship mutation is committed. Canonical relationships and all public metadata remain at their previous coherent state, and the exception propagates. The backing property may already contain the new value because `next()` runs first and cannot be rolled back safely. A later successful assignment retries reconciliation from canonical relationship state.

Permanent enumeration failure makes agreement with the backing value impossible. The guarantee therefore applies after successful enumeration. The important failure guarantee is no partial metadata mutation and a retryable canonical baseline.

### Handler failures

Lifecycle handlers are already documented as exception-free. Relationship dispatch nevertheless continues through every registered consumer after a consumer throws and rethrows the first exception afterward. This protects built-in registry and parent-tracking convergence from an unrelated custom consumer.

Existing attach and detach handler exception behavior is unchanged. Expanding transactional guarantees across arbitrary external lifecycle handlers is outside this pull request.

### Allocation failures

The lifecycle's ordinary descriptor, matching, and membership allocations occur before relationship commit. Consumer projection replacement can allocate while consumers reconcile. As elsewhere in the library, process-level failures such as `OutOfMemoryException` are not treated as recoverable transactional events.

## Public API Impact

Relative to master:

- Remove `IPropertyLifecycleHandler.RefreshCollectionProperty(PropertyReference, object?)`.
- Add the focused `IPropertyRelationshipHandler` interface with `ReconcileChildRelationships(PropertyReference, ReadOnlySpan<SubjectPropertyRelationship>)`.
- Add the public, internally constructed `SubjectPropertyRelationship` handle type.
- Add the optional `SubjectLifecycleChange.Relationship` property for membership transitions.
- Clarify `GetReferenceCount()` documentation to specify distinct parent-property memberships.

The PR-specific `SubjectChildReference` and `RefreshChildIndices` API do not remain in the final diff.

The primary read APIs and value types remain source compatible:

- `SubjectParent`
- `SubjectPropertyParent`
- `SubjectPropertyChild`
- `GetParents()`
- `RegisteredSubject.Parents`
- `RegisteredSubjectProperty.Children`

Their contents change intentionally when duplicate subject occurrences exist: callers now receive one value per occurrence instead of one per parent property and child pair.

## Performance and Allocation Strategy

Correctness is established before pooling or inline-storage optimization. The required complexity is:

- one enumeration of the new property value,
- linear reference-identity relationship matching,
- one relationship object per live occurrence when relationship consumers are enabled,
- no second property enumeration in registry or parent tracking,
- no user equality or hashing in reconciliation,
- cached zero-allocation public reads after snapshot construction.

One relationship object per occurrence is the main possible regression for registry and parent-tracking graphs. In the common `WithParents().WithRegistry()` configuration, it replaces independently owned index state and the parent-tracking hash-set entries. Lifecycle-only graphs retain compact distinct-membership state and do not pay the per-occurrence object cost. Removed relationship handles are collected normally and are not pooled because external relationship handlers can retain them.

After functional verification, temporary descriptor lists and reference-identity maps may reuse existing thread-local pools if measured allocation data justifies it. Pooling must clear all retained subject, property, key, and relationship references before return. The final code must retain one reconciliation path regardless of collection size.

Before finalizing the pull request, run comparative benchmarks against a base ref derived from `origin/master` for:

- `*ChildIndexRefreshBenchmark*`, focused on time and allocation scaling,
- the relevant `*RegistryBenchmark*` construction and parent-read cases,
- the two `*ParentLookupBenchmark*` registry-parent and tracked-parent read rows,
- one unchanged `ServiceOrderResolverBenchmark.LinearChain` row as a noise reference.

`ChildIndexRefreshBenchmark` and `ParentLookupBenchmark` do not exist on master, so their identical benchmark definitions must be applied to a temporary benchmark-only base ref before comparing them with the implementation branch. A class present only on the implementation branch is not a valid comparison.

Per the repository benchmarking guide, these targeted comparisons take approximately 75 minutes combined. A whole-suite benchmark is not required. Small timing changes inside the noise reference are inconclusive; allocations and scaling shape are the primary acceptance signals.

## Verification Strategy

### Semantic tests

Parameterized tests cover direct properties, arrays, mutable lists, `ICollection`, dictionaries, read-only dictionaries, and enumerable fallbacks. Each applicable shape covers:

- initial attachment,
- replacement with a new container,
- same-instance mutation followed by assignment,
- same-instance assignment remaining suppressed as a value-change notification while refreshing relationships,
- insertion, removal, reordering, and key replacement,
- one subject occurring multiple times,
- removal of one duplicate and removal of the final duplicate,
- one subject referenced by several parent properties,
- reference counts staying membership based when occurrence counts change,
- `null`, empty, and mixed subject/non-subject containers,
- cycles and self-references,
- context detach and reattach,
- previously captured public arrays remaining frozen after re-keying or reordering,
- a retained advanced relationship handle exposing its safely published current index,
- singular path selection through the first current occurrence.

Every test asserts all enabled views together: registry children, registry parents, tracking parents, reference count, known-subject membership, and path output.

### Failure tests

- A dictionary key whose equality and hash methods throw proves reconciliation never invokes them.
- An enumerator that throws after yielding earlier entries proves no partial relationship or cache mutation occurs.
- A successful retry after an enumeration failure proves canonical state was not advanced.
- A throwing relationship consumer proves later built-in consumers still reconcile before the exception propagates.
- Same-property re-entrancy proves the explicit guard fails without corrupting canonical state.

### Concurrency tests

Tests use barriers, events, and `AsyncTestHelpers.WaitUntilAsync`, never timing delays. They cover:

- concurrent writes to the same structural property,
- writes to different properties,
- write racing with parent detach,
- context detach racing with a descendant write,
- readers repeatedly taking children and parent snapshots while writers reorder and re-key,
- duplicate occurrence add/remove under concurrent replacement.

After worker completion, tests assert every quiescent invariant. During concurrency, each individual returned immutable array must be safe to enumerate and internally initialized. Cross-view equality is asserted after quiescence, not while a writer is active.

### Configuration matrix

The same core cases run with:

- lifecycle only,
- `WithParents()`,
- `WithRegistry()`,
- `WithParents().WithRegistry()`.

This prevents the shared relationship design from accidentally requiring either optional consumer.

### Repository verification

- Build `src/Namotion.Interceptor.slnx` with warnings as errors.
- Run all non-integration tests.
- Run public API snapshot verification and accept only the intentional lifecycle API changes.
- Measure lifecycle-only graph construction to prove that optional relationship consumers do not add per-occurrence relationship objects.
- Review OPC UA and HomeBlaze consumers of `SubjectPropertyChild` for duplicate-entry assumptions. Their implementation is not changed unless a failing unit test identifies a required adaptation.
- Integration tests are not required because connector implementations are outside the planned diff. Existing CI integration jobs provide additional coverage when the PR is pushed.

## Pull Request Scope and Review Structure

The final PR remains #458 and is rewritten around this design. The cumulative diff against master will remove:

- the adaptive scan-versus-rebuild algorithm,
- collection-size thresholds,
- specialized moved and rebuild pools,
- tests that exist only to exercise algorithm thresholds,
- the unrelated container-kind cache optimization,
- parent-lookup benchmark cases unrelated to the changed storage and read paths.

The branch will be organized into reviewable commits:

1. Add tests that define occurrence, ordering, duplicate, failure, and concurrency semantics.
2. Add canonical relationship state and linear lifecycle reconciliation.
3. Move parent tracking and registry to relationship-handle storage and derived snapshots.
4. Remove the old refresh paths and update public API snapshots and documentation.
5. Add or retain only the targeted refresh benchmarks needed to validate this design.

No compatibility adapter for `RefreshCollectionProperty` is required because the release permits a breaking API change. The PR description must replace the stale file counts, test counts, integration statement, and benchmark summary before review.

## Acceptance Criteria

The implementation is ready for merge when:

- every required invariant is covered by deterministic tests,
- the previously reproduced partial registry mutation cannot occur,
- duplicate subject occurrences appear as distinct ordered relationships everywhere,
- same-instance reassignment reconciles current container contents,
- hostile key equality and hashing are never invoked by reconciliation,
- all concurrency tests converge after quiescence without leaks, stale edges, torn reads, or permanently stale caches,
- the complete non-integration test suite and public API verification pass,
- targeted benchmarks show linear scaling and no unexplained allocation regression,
- the final diff contains one reconciliation algorithm and no unrelated optimization,
- lifecycle and registry design documentation describe the new relationship and concurrency model,
- the PR description accurately reports verification and the intentional public API break.
