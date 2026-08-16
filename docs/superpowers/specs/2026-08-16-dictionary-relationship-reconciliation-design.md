# Container Relationship Reconciliation Design

Status: Design approved; independently reviewed; ready for implementation planning

## Context

Pull request #458 fixes stale list indices and dictionary keys in registry and parent-tracking metadata. Its current implementation enumerates a structural property once, then lets each consumer repair its own stored index copies. The registry uses an adaptive scan or rebuild algorithm, and parent tracking performs a second refresh over another set of copied values.

That approach has three problems:

1. The registry, registry parent view, and tracking parent view each own an independently mutable copy of the same relationship index.
2. The adaptive scan mutates relationships before reconciliation is known to succeed. A later dictionary key operation can throw and leave a permanently partial update.
3. A container occurrence is collapsed to one relationship per child and parent property. A dictionary that contains the same subject under two keys therefore cannot be represented faithfully.

The replacement models every subject-valued container occurrence as a graph relationship. Each executing lifecycle interceptor enumerates the container once and owns its canonical immutable relationship objects. Registry and parent tracking replace their ordered relationship groups from those full sequences and derive their public snapshots from them.

This is a breaking release, but the commonly consumed registry and parent snapshot APIs will retain their existing names, return types, and value semantics.

## Baseline and Parallel Design Compatibility

This pull request is designed, implemented, and tested against current `master`. It takes no source, semantic, or merge-order dependency on pull request #419 or #440. Neither proposal is part of the baseline.

Current master permits zero, one, or several resolved `ILifecycleInterceptor` services. PR #458 preserves that model:

- Every resolved lifecycle interceptor remains in the write pipeline in current service order.
- Each `LifecycleInterceptor` owns and locks its own processed-property state.
- Same-instance structural refresh takes one resolver snapshot and invokes every built-in lifecycle structural-refresh capability in it exactly once.
- Lifecycle and relationship handlers remain ordered multi-service contracts and retain current subject-handler dispatch.
- No #419 unique-service, graph-owner, inherited-context, or explicit-root API is required.

The parallel context-cardinality and Hosting document is a non-normative compatibility check. Its separation of occurrences from membership, multi-service extension handlers, and reuse of the lifecycle structural lock are compatible with this design. If #419 later lands, its one-authority rule can simplify lifecycle resolution without changing occurrence matching or consumer storage. If #440 later lands, it can reuse the same lifecycle lock without interacting with relationship indices.

PR #458 should therefore remain based on `master`, not stacked on #419 or #440. If another pull request lands first, the later branch rebases and removes only complexity made genuinely obsolete by the landed code.

## Goals

- Make registry and parent metadata match the successfully enumerated property value after structural operations become quiescent.
- Represent every subject-valued dictionary, collection, and direct-property occurrence, including duplicate references to the same subject.
- Preserve container enumeration order and exact dictionary key or collection index metadata.
- Keep lifecycle membership distinct from occurrence relationships so duplicate entries do not duplicate attach and detach callbacks.
- Share one immutable relationship object between the lifecycle sequence and its consumers rather than maintaining independently mutable registry and parent-tracking index stores.
- Use one linear reconciliation algorithm based only on subject reference identity.
- Preserve thread safety, cached public snapshot behavior, existing lifecycle handler ordering, and existing detach visibility.
- Finish enumeration and all failure-prone container inspection before mutating relationship state.
- Keep the final pull request focused and substantially smaller than the current adaptive implementation.
- Keep lifecycle-authority iteration at one boundary and preserve current master's zero-or-many service resolution and dispatch semantics.

## Non-goals

- Detect mutations made directly to a mutable container when its property setter is not invoked.
- Make arbitrary user collection implementations safe for concurrent mutation during enumeration.
- Provide an atomic snapshot of the entire object graph across many properties. The guarantee is quiescent consistency with individually coherent public snapshots.
- Add an API that returns every possible path through a multi-parent graph.
- Change singular path selection beyond making it deterministic.
- Introduce per-property writer locks or otherwise replace the lifecycle interceptor's existing serialized writer model.
- Include the unrelated container-kind cache optimization or parent-lookup scenarios not reached by this relationship redesign.
- Implement context cardinality, graph movement, parent-context inheritance, or hosting state machines from #419 or #440.
- Redefine the pre-existing ownership semantics of a subject concurrently attached through several independently removable lifecycle contexts. This pull request preserves master's dispatch model; it does not absorb #419's cardinality problem.
- Guarantee correct public graph ownership after independently removing one of several lifecycle authorities that still overlap on the same subject. Master already removes lifecycle and registry contributions without a producer identity in this topology. PR #458 documents and preserves that limitation.
- Preserve state inside a custom lifecycle handler that violates the documented exception-free handler contract.

## Terminology

- **Occurrence**: One subject-valued entry produced by enumerating a structural property. Two dictionary keys that point to the same subject are two occurrences.
- **Relationship**: The immutable graph edge for one occurrence. It contains the parent `PropertyReference`, child `IInterceptorSubject`, and dictionary key, collection index, or `null` observed in one successful reconciliation.
- **Membership**: The fact that a child is reachable through a parent property at least once. Membership is unique per `(parent property, child subject)` pair.
- **Relationship snapshot**: A public immutable value such as `SubjectParent`, `SubjectPropertyParent`, or `SubjectPropertyChild`, derived from the current relationship state.
- **Canonical relationship sequence**: The ordered immutable relationship objects owned by one `LifecycleInterceptor` for one property. Current master can have an equivalent sequence in more than one lifecycle interceptor.
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

`GetReferenceCount()` continues to count lifecycle parent-reference contributions, not occurrence relationships. With one lifecycle interceptor this equals the number of distinct parent-property memberships. With several lifecycle interceptors, each interceptor can contribute once for the same membership, as on current master. Existing attach and detach callbacks also remain membership based within each lifecycle interceptor:

- The first occurrence for `(property, child)` causes a property-reference addition.
- Additional occurrences for that pair add relationships but do not attach the child again.
- Removing an occurrence while another remains removes only that relationship.
- Removing the final occurrence causes a property-reference removal.
- Context attachment and detachment continue to occur only when the subject enters or leaves the graph.

The relationship layer does not redefine current master's explicit context attach, context detach, or graph-exit behavior. It supplies distinct parent-property membership additions and removals to each executing `LifecycleInterceptor`; that interceptor remains responsible for its existing `IsContextAttach`, `IsContextDetach`, and reference-count transitions. A future root-ownership design may change those lifecycle rules without changing occurrence relationships.

The public documentation for reference count will say "parent-property lifecycle references" and explicitly state that repeated occurrences within one property and lifecycle interceptor do not increase it.

### Snapshot semantics

The existing high-level public results remain coherent point-in-time snapshots:

- `GetParents()` continues to return `ImmutableArray<SubjectParent>`.
- `RegisteredSubject.Parents` continues to return `ImmutableArray<SubjectPropertyParent>`.
- `RegisteredSubjectProperty.Children` continues to return `ImmutableArray<SubjectPropertyChild>`.

Previously returned arrays never change. A caller obtains current keys, indices, membership, and ordering by reading the property again. This avoids a captured immutable array acquiring current keys while retaining an old order.

The existing record structs retain their constructors, properties, and value equality. Their values are projections, not authoritative internal relationship storage.

## Required Invariants

When the graph is quiescent after successful enumeration and exception-free attach and detach callbacks, all of these invariants hold for each lifecycle interceptor and for public consumers whose currently contributing lifecycle callbacks agree on the property's attachment state:

1. **Property agreement**: each lifecycle interceptor's processed membership state for a structural property equals the distinct subjects in one complete enumeration of the property's current backing value. When relationship consumers are enabled, that interceptor's canonical relationship sequence also equals every ordered subject occurrence in that enumeration.
2. **Occurrence preservation**: when relationship consumers are enabled, every subject-valued occurrence has exactly one immutable relationship in each executing lifecycle interceptor's processed sequence, including repeated references to the same subject.
3. **Outgoing and incoming bijection**: each enabled built-in consumer installs one complete relationship sequence per parent property. Its outgoing and incoming views each contain every relationship from that installed sequence exactly once; it never appends equivalent sequences from multiple lifecycle callbacks.
4. **View agreement**: registry children, registry parents, and tracking parents project the same relationship subject and index values when those features are enabled.
5. **Order agreement**: outgoing registry relationships have the same order as source enumeration. Incoming relationships within one parent property have that same relative order.
6. **Membership agreement**: `(property, child)` membership exists if and only if at least one relationship for that pair exists.
7. **Reference-count agreement**: each executing lifecycle interceptor contributes at most one reference for a distinct `(property, child)` membership, exactly as on master. The subject's total count remains the sum of those current-master lifecycle contributions; duplicate occurrences within one membership never increment it.
8. **Lifecycle and registry agreement**: existing lifecycle attach and detach transitions continue to govern registry membership. Adding or removing a duplicate occurrence alone never causes a context attach, context detach, registration, or unregistration.
9. **No dangling state**: when the lifecycle callbacks agree that a parent or child is detached, it leaves no canonical relationships, registry edges, parent-tracking edges, or processed-property entries behind.
10. **Snapshot coherence**: each returned immutable array was built from one internally synchronized view. It is never mutated after publication.
11. **Safe publication**: readers observe an old coherent snapshot or a newer coherent snapshot, never partially initialized arrays or torn index references.
12. **Event ordering**: existing ancestor-registration guarantees, handler ordering, and `SubjectDetaching` graph visibility remain intact.

The backing property and metadata may temporarily differ while a write is between its backing-store update and serialized reconciliation. All writes that reach that window are considered in progress for the definition of quiescence.

Current master's source-less lifecycle callbacks cannot represent independent ownership contributions. If one of several overlapping lifecycle interceptors detaches while another remains attached, built-in consumers retain master's last-callback behavior and the relationship-agreement invariants above do not apply until the lifecycle callbacks agree again. PR #458 adds no new failure in that topology, but it also does not claim to repair it. The master-compatibility tests document this boundary explicitly.

## Architecture

### Canonical immutable relationship

The Tracking assembly introduces a sealed immutable `SubjectPropertyRelationship` reference type. It has an internal constructor and exposes read-only properties:

```csharp
public sealed class SubjectPropertyRelationship
{
    public PropertyReference Parent { get; }
    public IInterceptorSubject Child { get; }
    public object? Index { get; }
}
```

`Parent`, `Child`, and `Index` never change. The class retains reference-identity equality. It is not a record and is not suitable as a value-based hash key. Immutability lets every consumer safely share a relationship while its old and new ordered groups are published independently.

One relationship object exists per live occurrence in each executing `LifecycleInterceptor` when at least one relationship consumer is enabled. A retained occurrence may reuse its relationship only when its index metadata is known unchanged without calling user equality:

- Direct relationships reuse the old object while both indices are `null`.
- Positional collection relationships reuse it when the integer position is unchanged.
- Dictionary relationships reuse it only when the exact key object is reference-equal.
- Otherwise reconciliation creates a new immutable relationship for that occurrence.

This intentionally allocates a replacement for value-type dictionary keys that are boxed again during enumeration. It never invokes key equality merely to retain object identity. Removed or replaced relationships are never pooled because an advanced custom handler may retain them. A retained relationship remains a frozen occurrence snapshot; it never changes after callback return.

### Processed property state

`LifecycleInterceptor` replaces `_lastProcessedValues` as the structural baseline with processed-property state. Every state records the distinct child memberships produced by the last completed reconciliation. When a relationship consumer is enabled, it additionally contains the ordered canonical immutable relationships.

This state, rather than a previous mutable container reference, is the old side of the next reconciliation. It therefore supports an in-place-mutated dictionary or collection that is assigned back through the property setter. It is also the exact state to detach if the parent leaves the graph.

Lifecycle-only configurations do not allocate a relationship object per occurrence. Their compact membership record retains the child plus the first and last old occurrence indices. The first index preserves initial-add and context-detach callback semantics; the last index preserves master's reverse-order ordinary-removal semantics. An inline-first representation covers the common direct-child case. Full occurrence relationships are materialized only when the context or subject has an `IPropertyRelationshipHandler`. This is a storage optimization, not a second reconciliation algorithm.

A direct subject property retains a reference-equality no-op when its canonical membership already matches. Enumerable container writes always reconcile, even when the container instance is reference-equal to the previously observed value.

The descriptor enumeration and matching algorithm live in a focused internal relationship reconciler. `LifecycleInterceptor` owns the processed state and supplies its existing membership attach and detach operations; the helper does not own context edges, context attach or detach rules, or reference-count storage. This keeps the relationship change focused and limits overlap with unrelated lifecycle work.

### Equality-suppressed container refresh

`PropertyValueEqualityCheckHandler` currently sits outside lifecycle tracking and suppresses a setter call when the old and new container are the same reference. The new behavior treats such a setter call as an explicit structural refresh without turning it into a value change:

1. Before calling `EqualityComparer<TProperty>.Default`, the equality handler checks whether the current and new values are the same non-string `IEnumerable` reference and the declared property can contain subjects.
2. If so, it resolves the internal structural-refresh capability implemented by every built-in `LifecycleInterceptor`, invokes each resolved capability exactly once, and then returns without calling the remaining write chain.
3. Lifecycle refresh re-reads the backing value under its writer lock and runs normal relationship reconciliation.
4. Otherwise the equality handler retains its existing comparison and write-suppression behavior.
5. For a structural refresh, the terminal setter is not invoked, `PropertyWriteContext.IsWritten` remains false, and property-change, derived-change, transaction, and connector-write behavior remains suppressed exactly as for other equal values.

The Tracking assembly defines a small internal structural-refresh capability implemented by `LifecycleInterceptor` and resolved through the existing assignable-service mechanism:

```csharp
internal interface IStructuralPropertyRefreshHandler
{
    void RefreshStructuralProperty(PropertyReference property);
}
```

The equality handler takes one immutable snapshot of these capabilities and invokes each entry exactly once in resolver order. Each implementation independently locks and reconciles its own processed state. A custom `ILifecycleInterceptor` that does not implement structural property processing has nothing to refresh and is not promised a callback. This avoids expanding the public `ILifecycleInterceptor` contract. Other equality-suppressed values retain their existing no-op behavior.

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

`IPropertyRelationshipHandler` is an ordered, intentionally multi-service extension contract. It is not a unique service. A context can validly resolve a registry handler, a parent-tracking handler, and custom relationship handlers together.

For an ordinary intercepted structural write, each `LifecycleInterceptor` resolves and captures its relationship-handler sequence before calling `next(ref context)`. Any service-resolution failure already visible to that interceptor therefore happens before the backing setter. The captured sequence is used for that reconciliation even if context topology changes concurrently; a later operation observes the newer context state. Attach and detach retain current master's handler-resolution and subject-handler ordering.

When current master resolves several lifecycle interceptors, the same relationship handler can receive an equivalent full-property sequence from each one. Built-in consumers unconditionally replace the full group and never compare relationship indices, so equivalent authority callbacks produce the same public sequence rather than duplicate edges. Each lifecycle interceptor still owns independent canonical relationships and processed state; no callback is invoked twice by the same interceptor for one reconciliation.

Registry and parent tracking implement this interface and store ordered immutable relationships internally. Their existing public immutable arrays remain lazily cached projections and are invalidated under the same consumer lock that protects relationship-list replacement. A handler may retain an individual immutable relationship after the call, but the `ReadOnlySpan` itself is valid only for the synchronous callback and must not be retained.

The dispatcher invokes every relationship consumer even if one throws, records the first exception, completes built-in reconciliation, and then rethrows the first exception with its original stack. This prevents one custom consumer from stranding later built-in consumers. A failing custom consumer remains responsible for its own state, consistent with the lifecycle handler contract.

Relationship handlers run synchronously while the executing lifecycle interceptor's structural lock is held. They must be fast, must not block, and must not re-enter lifecycle operations. They can update their own bounded structural state but cannot start asynchronous work or invoke arbitrary user collection logic.

`SubjectLifecycleChange` gains an optional `Relationship` property. For a membership addition or removal, it identifies the exact materialized occurrence whose `Index` drives that lifecycle transition. `Index` is always supplied; `Relationship` is supplied only when occurrence relationships were materialized because at least one relationship consumer is present. Registry and parent tracking use it to preserve the relationship that existing `SubjectAttached` and `SubjectDetaching` observers can see before the final full-property reconciliation. Root context changes have no relationship.

The exact callback contract is:

| Transition | Occurrence metadata | Dispatch order |
| --- | --- | --- |
| Ordinary membership addition | First new occurrence in source order; `Index` always and `Relationship` when materialized | Context `ILifecycleHandler` services in current order, subject lifecycle handler, optional `SubjectAttached` and subtree descent |
| Ordinary membership removal | Last old occurrence, matching master's reverse traversal; `Index` always and `Relationship` when materialized | Optional `SubjectDetaching`, subject lifecycle handler, context `ILifecycleHandler` services in current order |
| Context detach of a property membership | First old occurrence, matching master's forward property scan; `Index` always and `Relationship` when materialized | Existing context-detach lifecycle order |
| Full relationship reconciliation | Complete new source-ordered sequence | Context `IPropertyRelationshipHandler` services in resolver order, then the parent subject handler when implemented |
| Property detach while its subject leaves the lifecycle | Empty sequence after `SubjectDetaching` has observed the old graph and before removal lifecycle handlers remove the subject from built-in consumers | Context relationship handlers in resolver order, then the parent subject handler when implemented |

The relationship dispatcher continues after an exception and rethrows the first exception only after every captured context and subject relationship handler has run. Existing lifecycle-handler exception ordering is unchanged.

### Registry storage

`RegisteredSubjectProperty` stores the ordered outgoing relationships. Its `Children` cache projects each one to `SubjectPropertyChild`.

`RegisteredSubject` stores incoming relationships grouped by registered parent property. Its `Parents` cache projects them to `SubjectPropertyParent`. Reconciliation replaces the complete group for the changed parent property, preserving other property groups and their attachment order.

`SubjectRegistry` has one private relationship-reconciliation gate. Its relationship callback holds that gate for the complete full-group operation, briefly takes the registry's known-subject lock to resolve all subjects and the registered parent property, releases the known-subject lock, and then updates individual outgoing and incoming collections without nesting their view locks. The gate prevents callbacks from different lifecycle interceptors from interleaving one property's outgoing replacement with another callback's incoming replacement. Each view lock prevents a reader from publishing a stale cache after its corresponding replacement.

The registry never acquires the lifecycle writer lock. Lock order remains lifecycle, registry relationship-reconciliation gate, registry known-subject state while resolving, then individual relationship-view locks during replacement, with no reverse acquisition.

### Parent-tracking storage

Parent tracking replaces its `HashSet<SubjectParent>` with ordered relationship groups. A group is identified by `PropertyReference.Comparer`, and occurrences within the group retain container order. `GetParents()` projects those relationships to the existing `SubjectParent` values. `ParentTrackingHandler` likewise holds one private relationship-reconciliation gate across a complete callback that can update several children's parent groups.

The common empty and single-parent cases retain inline-first storage, with ordered overflow storage allocated for additional relationships. No adaptive threshold or alternate reconciliation algorithm is permitted. The consumer-wide gates add no contention in the common one-lifecycle configuration because that lifecycle already serializes relationship writes; they exist for master's several-lifecycle case.

## Reconciliation Algorithm

All structural reconciliation performed by one `LifecycleInterceptor` runs under that instance's existing `_attachedSubjects` lock.

### Stage phase

1. Re-read the property's actual backing value after acquiring the lifecycle lock. This preserves the existing last-writer convergence behavior when `next()` calls race outside the lock.
2. For an ordinary write, if the parent is no longer attached, discard the write without creating relationship state. Initial attach instead requires its matching attach-in-progress token. The lock prevents this lifecycle interceptor's attached or attaching state from changing concurrently, while re-entrant changes are detected by the later commit checks.
3. Enumerate the value exactly once into an ordered temporary descriptor list. No graph state changes during enumeration.
4. Build the new distinct-membership set by subject reference identity and compare it with the processed property's old membership state.
5. When relationship consumers are enabled, match old relationships to new descriptors by child reference identity and occurrence number. The first new occurrence of a child matches its first old occurrence, the second matches the second, and so on.
6. Reuse a matched immutable relationship only when its index is known unchanged by the rules above. Stage replacement relationships for moved or re-keyed matches, new relationships for unmatched descriptors, removed old relationships, the new ordered relationship sequence, and all membership transitions.

Matching is linear and uses only reference-identity hashing for subjects. Every reconciliation-reachable subject-keyed dictionary, set, and immutable snapshot in lifecycle and registry storage uses `ReferenceEqualityComparer`; `PropertyReference.Comparer` already compares its subject by reference. Reconciliation therefore never invokes subject equality, dictionary-key equality, or dictionary-key hashing. Temporary maps and lists may be pooled after correctness is established, but the implementation has one algorithm for all collection sizes.

### Commit phase

1. Check that the parent remains attached after all user getter and enumerator code from staging. If it was re-entrantly detached, invoke the abort path below and stop before any membership transition.
2. Process membership removals in the existing reverse-detach order. `SubjectDetaching` still observes the old graph, and lifecycle removal handlers remove all occurrences for the departing `(property, child)` membership.
3. After every callback-bearing transition, check whether the parent remains attached to this lifecycle interceptor. If it was re-entrantly detached, invoke the abort path below and stop.
4. Process membership additions in source order, recording each successfully applied addition. The first new occurrence supplies the lifecycle change's index. Existing registry-before-descent ordering remains unchanged. Repeat the parent-attached check after every addition.
5. Check parent attachment once more, then publish the new canonical ordered relationship sequence when relationship consumers are enabled. Otherwise publish only the new compact membership state.
6. When enabled, invoke all relationship consumers with the full sequence. Consumers replace their complete group for this property, so provisional first-occurrence entries from membership callbacks become the exact occurrence set.

The re-entrant-detach abort path never publishes staged state. It detaches every successfully applied new membership in reverse order, invokes the captured relationship-handler sequence with an empty span, and removes any processed-property entry left by the outer operation. Removals already applied do not need restoration because this lifecycle interceptor no longer contains the parent. Cleanup operations are idempotent with the re-entrant detach path. This closes the window in which a new child could otherwise remain attached without canonical state capable of removing it.

No collection enumeration, key equality, key hashing, or subject equality runs in the commit phase. Existing lifecycle callbacks retain their current support for writes to different properties. Relationship handlers cannot re-enter lifecycle operations. A same-property reconciliation guard throws a clear `InvalidOperationException` instead of allowing baseline corruption.

### Initial attach

Initial attachment uses the same descriptor and membership logic with an attach-specific state transition:

1. Under the lifecycle lock, create a private attach-in-progress token for the parent. The token is not visible as graph membership and produces no callbacks.
2. Enumerate and stage every structural property of the parent before mutating membership or consumer state. If any getter, enumerator, or dictionary projection throws, remove the token and leave no processed state, child membership, or relationship group.
3. Commit the staged unique child memberships in master's property and source order, recording every successful addition in the token's undo ledger. The first occurrence of each unique child drives membership attachment and subtree descent.
4. After a property's memberships succeed, publish its canonical processed state provisionally. Do not invoke its full relationship callback yet.
5. After all child memberships succeed, perform master's existing explicit context attach for the parent. This preserves child-before-root lifecycle callback order and existing ancestor registration behavior.
6. If the parent remains attached, invoke each property's full relationship callback in property order, publishing duplicates and exact source order, then remove the attach-in-progress token.

The general commit predicate accepts either an already attached parent or the matching active attach token. A re-entrant `DetachSubjectFromContext(parent)` recognizes and cancels that token even before the parent enters `_attachedSubjects`. The outer attach then reverses every ledgered child addition, clears provisionally published processed states, invokes captured relationship handlers with empty groups, removes the token, and never performs or retains the root attach. If re-entrant detach occurs after the root attach, normal detach reads the provisionally published canonical states and performs the same cleanup. Concurrent attach calls are serialized by the lifecycle lock and cannot create a second token.

This stages all of the parent's own structural enumeration before the first lifecycle callback, matching master's collect-before-attach order and strengthening its failure behavior by avoiding partially seeded `_lastProcessedValues`. Failures thrown later by child attachment or arbitrary lifecycle callbacks remain governed by #384.

The attach method removes its attach-in-progress token in a `finally` block on every exit, including callback exceptions. A callback failure after lifecycle mutation does not attempt unsafe rollback through more arbitrary callbacks. Provisionally published processed states remain as the best available baseline, and no later uncommitted property state or full relationship sequence is published. A later attach attempt is not blocked: it stages all properties again, treats any retained provisional state and idempotent lifecycle membership as its old baseline, and replaces full relationship groups after successful completion. This can restore built-in relationship convergence, but it does not replay arbitrary lifecycle handlers skipped by the original exception; that limitation remains #384. If the parent entered `_attachedSubjects` before the exception, an explicit detach uses the retained processed state for cleanup.

### Detach

Detach reads canonical processed-property state rather than enumerating the current backing container. It detaches each unique child membership once, clears the property relationship consumers when present, and removes the processed-property state. This ensures that a concurrent or un-intercepted backing-store mutation cannot cause the lifecycle to detach relationships it never attached or retain relationships it did attach.

## Concurrency and Publication Model

### Serialized writers

Each existing lifecycle lock remains the sole relationship writer lock for its interceptor's processed state. Context attach, context detach, and structural property reconciliation within that interceptor are serialized. The backing-store write through `next()` remains outside the lock.

Current master can execute several lifecycle interceptors for one write. No operation holds two lifecycle locks at once. Each interceptor re-reads the backing value under its own lock and publishes a full property group, so concurrent or interleaved authority callbacks converge to the final backing value after all write invocations finish. Consumer locks serialize their own full-group replacements.

When two threads write the same property, whichever thread acquires the lifecycle lock first re-reads and reconciles the backing value that is current at that point. A later interceptor invocation re-reads again. When all invocations complete, canonical relationships match the final backing value.

When a write races with parent detach, the operation that acquires the lifecycle lock first wins that serialized transition. A writer that finds the parent detached performs no relationship attachment. A detach uses canonical relationship state, not the possibly newer backing value, and therefore cannot leak an unprocessed child.

### Lock-free readers

Public parent and child getters retain their cached immutable-array model and existing read-lock behavior:

- Registry-parent and tracked-parent cache references are read and published with `Volatile.Read` and `Volatile.Write`, preserving their lock-free cached fast path.
- `RegisteredSubjectProperty.Children` retains its existing per-view lock on reads.
- Cache construction and relationship-list replacement use each view's existing lock.
- Published arrays are never changed in place.
- Relationship objects are immutable, so an old group cannot observe index changes from a newer generation.

A reader overlapping reconciliation may receive the previous snapshot or a newly built snapshot while the write is active. Each individual snapshot projects one immutable ordered relationship group and therefore cannot combine old ordering with new indices. The writer invalidates or replaces every affected cache before returning. A later reader after quiescence therefore obtains metadata derived from the committed relationship sequence.

Different public views are not published as one global transaction. During a write, a registry child reader and a parent-tracking reader can temporarily observe different generations. After the serialized write and all callbacks finish, the view-agreement invariant holds. This is the repository's established quiescent-consistency model.

### Parallel Hosting compatibility

PR #458 uses only current master's lifecycle structural lock and introduces no Hosting dependency or second graph-membership lock. If #440 later exposes that same lock through `RunUnderLifecycleLockIfAttached`, both features have a compatible serialization boundary for attached-state checks, relationship changes, and target-registration requests.

The compatible lock branches are:

```text
lifecycle structural lock -> registry relationship gate
                          -> registry known-subject lock (released)
                          -> registry relationship-view lock
lifecycle structural lock -> parent-tracking relationship gate
                          -> tracked-parent view lock
lifecycle structural lock -> per-subject hosting state lock (released)
                          -> hosting handler lock -> target lock
```

The new consumer gates are acquired only by their relationship callbacks; no lifecycle callback or public getter acquires them and then enters lifecycle. Registry and parent getters never acquire the lifecycle lock. Registry and relationship-view code never enters Hosting. Hosting transition bodies run after structural locks are released and never call relationship handlers. A custom relationship handler that tries to enter Hosting or lifecycle operations violates the synchronous handler contract.

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

The no-partial-mutation guarantee applies to container enumeration, dictionary projection, descriptor construction, matching, and other pre-commit inspection. It does not apply after lifecycle or relationship callbacks begin. The explicit re-entrant-detach abort path is guaranteed for exception-free callbacks, but arbitrary throwing lifecycle handlers retain master's #384 failure semantics.

### Lifecycle membership failures

The relationship reconciler does not duplicate context or lifecycle validation. Each executing `LifecycleInterceptor` uses its current-master operations to add or remove distinct parent-property membership and publishes its new canonical relationship sequence only after those operations return successfully.

For an ordinary write, failures while resolving the lifecycle write pipeline or the captured relationship-handler sequence occur before the backing setter. Failures from lifecycle attach, detach, or user handlers retain their current timing.

This pull request does not claim to solve the general lifecycle rollback limitation tracked by #384. If an existing attach or detach transition itself fails after producing lifecycle side effects, its recovery remains owned by that lifecycle work. Relationship enumeration and key failures still have the stronger no-mutation guarantee defined above. Detaching a lifecycle interceptor clears that interceptor's processed relationship state; a later attachment creates or reconciles state owned by the lifecycle interceptor that performs it.

### Allocation failures

The lifecycle's ordinary descriptor, matching, and membership allocations occur before relationship commit. Consumer projection replacement can allocate while consumers reconcile. As elsewhere in the library, process-level failures such as `OutOfMemoryException` are not treated as recoverable transactional events.

## Public API Impact

Relative to master:

- Remove `IPropertyLifecycleHandler.RefreshCollectionProperty(PropertyReference, object?)`.
- Add the focused `IPropertyRelationshipHandler` interface with `ReconcileChildRelationships(PropertyReference, ReadOnlySpan<SubjectPropertyRelationship>)`.
- Add the public, internally constructed immutable `SubjectPropertyRelationship` type.
- Add the optional `SubjectLifecycleChange.Relationship` property for membership transitions.
- Clarify `GetReferenceCount()` documentation to specify parent-property lifecycle contributions and distinguish them from occurrence relationships.

The PR-specific `SubjectChildReference` and `RefreshChildIndices` API do not remain in the final diff.

The primary read APIs and value types remain source compatible:

- `SubjectParent`
- `SubjectPropertyParent`
- `SubjectPropertyChild`
- `GetParents()`
- `RegisteredSubject.Parents`
- `RegisteredSubjectProperty.Children`

Their contents change intentionally when duplicate subject occurrences exist: callers now receive one value per occurrence instead of one per parent property and child pair.

Subject-keyed lifecycle and registry storage now uses reference identity consistently. Two distinct `IInterceptorSubject` instances that override value equality and were incorrectly collapsed by master are represented independently. This is an intentional correctness change and receives a public-behavior regression test.

## Behavior Changes Relative to Master

The intentional observable changes are:

- Registry children, registry parents, and tracked parents contain one entry per occurrence rather than one per distinct `(property, child)` membership.
- Those entries mirror source enumeration order. Reordering or re-keying can therefore change singular path selection to the first current occurrence.
- Enumerable structural writes reconcile even when the container reference is unchanged. With equality tracking, same-instance assignment becomes an explicit refresh whose backing setter and value-change notifications remain suppressed. Without equality tracking, the normal setter still runs as before, but lifecycle processing no longer exits only because the reference is equal. Either path can now spend enumeration work or propagate an enumeration or relationship-handler exception where master skipped lifecycle processing.
- Detach uses the last successfully processed state rather than re-enumerating a mutable container. Unassigned in-place mutations remain invisible and cannot change which memberships detach.
- Same-property reconciliation re-entrancy throws `InvalidOperationException` instead of risking baseline corruption.
- Relationship-handler dispatch continues to later handlers and rethrows the first handler exception after dispatch, unlike the removed refresh callback's stop-at-first behavior.
- Distinct subjects that override value equality are no longer collapsed in lifecycle or registry subject-keyed state.

The following master behavior is intentionally preserved:

- Lifecycle reference counts and attach or detach callbacks remain per distinct parent-property membership within each lifecycle interceptor, not per occurrence.
- The first new occurrence supplies ordinary membership-add metadata, the last old occurrence supplies ordinary reverse-removal metadata, and the first old occurrence supplies context-detach metadata.
- Equality-suppressed structural refresh does not set `IsWritten`, publish property-change or derived-change notifications, participate in a transaction, or send connector writes.
- Direct container mutation without an intercepted assignment remains invisible.
- Previously returned public immutable arrays remain frozen.
- Zero, one, or several lifecycle services retain master's resolution and dispatch behavior, including its pre-existing independently removable authority limitation.
- Public views are quiescent-consistent rather than one atomic graph-wide transaction.

The compatibility audit must specifically inspect consumers that treat `Children.Length` or `Parents.Length` as a membership count, assume only one path per `(property, child)`, or implement the removed refresh method. OPC UA, HomeBlaze, serialization, path, and UI code are the known review targets. The same-instance enumeration cost and per-occurrence immutable relationship allocations are measured before merge.

## Performance and Allocation Strategy

Correctness is established before pooling or inline-storage optimization. The required complexity is:

- one enumeration of the new property value,
- linear reference-identity relationship matching,
- one relationship object per live occurrence when relationship consumers are enabled,
- no second property enumeration in registry or parent tracking,
- no user equality or hashing in reconciliation,
- cached zero-allocation public reads after snapshot construction.

One relationship object per occurrence and executing lifecycle interceptor is the main possible regression for registry and parent-tracking graphs. In the common single-lifecycle `WithParents().WithRegistry()` configuration, it replaces independently owned index state and the parent-tracking hash-set entries. Re-keyed or moved occurrences allocate new immutable objects; unchanged relationships are reused only without user equality. Lifecycle-only graphs retain compact membership state and do not pay the per-occurrence object cost. Removed relationships are collected normally and are not pooled because external relationship handlers can retain them.

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
- a retained advanced relationship remaining frozen after its occurrence is re-keyed or reordered,
- singular path selection through the first current occurrence.

Every test asserts all enabled views together: registry children, registry parents, tracking parents, reference count, known-subject membership, and path output.

### Failure tests

- A dictionary key whose equality and hash methods throw proves reconciliation never invokes them.
- An enumerator that throws after yielding earlier entries proves no partial relationship or cache mutation occurs.
- A successful retry after an enumeration failure proves canonical state was not advanced.
- A throwing relationship consumer proves later built-in consumers still reconcile before the exception propagates.
- Same-property re-entrancy proves the explicit guard fails without corrupting canonical state.
- A lifecycle callback that re-entrantly detaches the parent during a new membership addition proves applied additions are undone and consumers remain empty.
- A subject with two structural properties whose second enumerator throws proves neither property publishes processed state, membership, or relationships and a later successful retry attaches both.
- A lifecycle callback failure during initial attach proves the attach-in-progress token is always cleared and does not block a later attach attempt, without claiming rollback of arbitrary handler side effects.
- Distinct subjects with hostile value equality prove all reconciliation-reachable subject storage uses reference identity.

### Concurrency tests

Tests use barriers, events, and `AsyncTestHelpers.WaitUntilAsync`, never timing delays. They cover:

- concurrent writes to the same structural property,
- writes to different properties,
- write racing with parent detach,
- context detach racing with a descendant write,
- readers repeatedly taking children and parent snapshots while writers reorder and re-key,
- a reader forced into the first-cache-build window while a relationship moves, proving it receives one immutable generation rather than new indices in old order,
- duplicate occurrence add/remove under concurrent replacement.

After worker completion, tests assert every quiescent invariant. During concurrency, each individual returned immutable array must be safe to enumerate and internally initialized. Cross-view equality is asserted after quiescence, not while a writer is active.

### Configuration matrix

The same core cases run with:

- lifecycle only,
- `WithParents()`,
- `WithRegistry()`,
- `WithParents().WithRegistry()`.

This prevents the shared relationship design from accidentally requiring either optional consumer.

Master-compatibility cases additionally cover zero lifecycle services and an aggregated context resolving two lifecycle interceptors. They prove that:

- a same-instance structural refresh invokes each resolved built-in lifecycle structural-refresh capability exactly once while unrelated custom `ILifecycleInterceptor` services remain untouched,
- each interceptor converges its independent processed state to the final backing value,
- a shared built-in relationship consumer replaces equivalent full sequences without duplicating public relationships, and
- existing lifecycle, property-handler, and subject-handler ordering is unchanged.

These tests exercise master's existing aggregation dispatch. An additional test removes one of several independently contributing lifecycle authorities and pins master's existing last-callback ownership limitation so #458 cannot accidentally claim stronger semantics or make them worse. The tests do not import #419's proposed unique-authority, graph-movement, or explicit-root rules.

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
3. Move parent tracking and registry to immutable relationship storage and derived snapshots.
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
