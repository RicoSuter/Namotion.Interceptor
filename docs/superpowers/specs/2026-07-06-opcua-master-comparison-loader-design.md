# OPC UA Master-Based Comparison Loader Design

## Context

PR 313 improves OPC UA client loader performance and reliability through batched browse/read calls, a staged load context, split-retry behavior, and subscription setup changes.

PR 313 is already a staged loader. Ownership claims, root property mutations, monitored-item registration, subscription creation, and read-after-write registration are all kept out of discovery and applied later. It implements a narrow commit with no root-mutation rollback: the load context releases only the claims it committed during that apply and documents that root setter values were not captured and cannot be undone. It classifies transient versus permanent status codes through a shared classifier, registers read-after-write only for surviving monitored items, and sweeps detached subjects after registration.

PR 313 works, but its loader has accumulated enough internal lifecycle state that a parallel design should be evaluated for whether the same behavior can be produced with fewer moving parts. This branch does not claim PR 313 does durable work during discovery or needs root-mutation rollback; it already avoids both. The claim to test is narrower: that a from-master rewrite can pass the same acceptance suite with fewer internal lifecycle states and clearer phase boundaries.

Two behaviors in this design are genuinely new relative to PR 313, not reorganizations:

- Gate subscription callbacks until setup completes. PR 313 attaches the data-change callback before creating monitored items and guards only with a shutting-down flag, so a notification can reach a subject mid-setup. This design enables callbacks as an explicit final step, which is what actually closes that window.
- Order the detached-subject sweep before read-after-write registration. PR 313 registers read-after-write per batch and then sweeps; this design sweeps first, so read-after-write is never registered for a subject that detached during setup.

Everything else in this design targets the same observable behavior as PR 313 with a simpler internal structure. Breaking public API changes are acceptable and do not require compatibility aliases.

## Goals

- Preserve PR 313's OPC UA loader performance gains.
- Preserve PR 313's observable reliability behavior. The copied acceptance suite is the contract.
- Reduce the number of internal lifecycle states during loading relative to PR 313.
- Add the two new behaviors above: callback gating and sweep-before-read-after-write ordering.
- Confine durable graph, ownership, and subscription mutations to the commit and subscribe phases. Never mutate durable state in discovery or validation.

## Non-Goals

- Do not preserve PR 313's internal loader structure for its own sake.
- Do not change observable failure semantics that the copied tests pin. Simplification must come from fewer internal states, not different externally visible behavior. A deliberate behavior change requires changing the corresponding test on purpose, as a reviewed act.
- Do not add compatibility aliases for renamed configuration properties.
- Do not broaden scope into unrelated OPC UA server, MQTT, WebSocket, or connector-tester behavior.
- Do not optimize for minimal diff size. Optimize for a clearer design that can be compared fairly against PR 313.

## Acceptance Strategy

Copy PR 313's targeted test suite into the new branch first, before writing any loader code, and treat it as a frozen contract. The tests define parity between the two branches; the rewrite is validated by making them pass without weakening them.

Porting cost to plan for: tests that assert through observable or public surface (session extensions, status classifier, batching end-to-end, configuration validation, root-path resolution, subscription health) port unchanged. Tests that reach into PR 313 internal types (`OpcUaLoadContext`, `OpcUaSubjectLoader` internals, the test base's `private protected` members) will not compile against a different internal structure and must be re-pointed at the new internals while preserving their assertions. Re-pointing must preserve the assertion, not relax it.

Freezing the suite also freezes PR 313's failure semantics. `WhenApplyFailsMidway_...` pins that a mid-commit failure releases newly committed claims but does not restore root property values. `WhenLoadSucceeds_ThenSourceClaimsHappenBeforeRootAssignmentInApply` pins that ownership claims are committed before a sub-subject is assigned onto root. The rewrite must reproduce these exactly. "Clearer failure boundaries" therefore means fewer internal states, not different observable rollback.

The two behavior changes that are new relative to PR 313 need their own focused tests in addition to the copied suite:

- Data-change callbacks stay gated until subscription setup completes.
- Read-after-write registration runs only after the detached-subject sweep, so a subject that detached during setup is never registered for read-after-write.

Required coverage areas:

- Batched browse and read behavior.
- Browse continuation handling.
- Split retry for operation-limit or response-size failures.
- Status-code classification: the permanent whitelist, transient codes (including `BadTooManyMonitoredItems` as transient and retryable), and that both `Good` and `Uncertain` are neither permanent nor transient.
- Write and subscription-health error classification routed through the shared classifier.
- Configuration validation: positive `MaxBrowseContinuations` and `MaxAttributeTraversals`, plus the other numeric bound and default contracts.
- Loader failure cleanup and retryability, including claim-before-root-assignment ordering and the un-undoable root mutation on mid-commit failure.
- Dynamic property and dynamic attribute deduplication.
- Dynamic attribute depth cap (`MaxAttributeTraversals`) and cross-round browse-cache reuse when an attribute's parent node id was already browsed in an earlier round.
- Collection rebind by index and dictionary rebind by invariant-string key, including existing entries preserved when a container node's browse fails permanently.
- Deterministic handling of duplicate references and duplicate value claims via the smaller-node-id tie-break.
- Initial value read behavior: best-effort, where a not-ready or transient value leaves that property unset.
- Subscription setup failure handling.
- Detached-subject cleanup during subscription setup.
- Read-after-write registration only for surviving monitored items, registered after the detached-subject sweep.
- Reconnect and subscription health behavior where PR 313 added or changed tests.

The comparison is meaningful only if the new branch passes the same targeted suite that validates PR 313.

## Architecture

Use a four-phase OPC UA load model: discover, validate, commit, subscribe.

### 1. Discover

Discovery builds an in-memory load plan from OPC UA browse/read results. It batches protocol calls, follows continuation points, resolves node ids, reads metadata and values, and records candidate subject, property, attribute, and monitored-item bindings.

Discovery must avoid durable side effects:

- No durable source ownership claims.
- No root property mutations.
- No subscription registration.
- No long-lived monitored-item registration.
- No read-after-write registration.

Discovery must also record, per browsed container node, whether the browse succeeded (possibly with zero children) or failed this round. A failed browse is not the same as a node with no children. This distinction is load-bearing for collections and dictionaries: a plan whose source browse failed must never be used to overwrite an existing container. The batched browse primitive encodes this by omitting failed node ids from its results, so a missing entry means "browse failed" and an empty entry means "no children."

Limited temporary object construction is acceptable if it is owned by the load plan and discarded on discovery failure.

### 2. Validate

Validation normalizes and deduplicates the plan before durable application state is changed.

Deterministic server-shape conflicts are resolved here:

- Duplicate dynamic property names.
- Duplicate dynamic attribute names.
- Duplicate structured targets.
- Duplicate dictionary keys.
- Multiple browse paths to the same value property (resolved by the smaller-node-id tie-break).
- Unresolvable node ids.
- Unsupported monitored-item candidates.

The transient-versus-deterministic rule has two boundaries with different policies:

- Structure discovery boundary (browse, node-id resolution, type resolution): a transient protocol or session failure aborts the load so it retries from a clean state. Deterministic address-space conflicts are skipped, deduped, or reported per documented local rules, because retrying the same server shape does not help. Validation must not surface a transient structure failure as a local skip.
- Best-effort value-read boundary: a transient or not-ready value status such as `BadWaitingForInitialData` leaves that one property unset for the subscription to backfill and does not abort the load. Validation must not escalate a transient value-read status into a load abort.

The value-read carve-out is deliberate: the initial value read is best-effort and never classifies or throws, so one not-ready value must not cancel the whole load.

### 3. Commit

Commit applies the accepted plan to the Interceptor graph in one bounded phase. Order the steps so ownership is claimed before the owning subject becomes reachable from root:

1. Create or attach planned subjects into a staged graph that is not yet reachable from root.
2. Claim source ownership on the planned properties.
3. Store OPC UA node-id metadata on claimed properties.
4. Apply accepted initial values to staged subjects where those subjects are not yet reachable from root.
5. Assign the staged subjects onto their root properties.
6. Apply accepted root-level value mutations.
7. Build the monitored-item list for successfully claimed properties.

Claims (step 2) must be committed before root assignment (step 5). The acceptance suite pins this: an observer reacting to a root property change must already see the ownership claim. Reversing the order makes the assignment observable before the claim and fails `WhenLoadSucceeds_ThenSourceClaimsHappenBeforeRootAssignmentInApply`.

On commit failure, release the ownership claims and metadata committed during this phase and discard the monitored-item list. Root property values assigned in steps 5 and 6 are the documented commit boundary and are not restored on failure, matching PR 313. This is the accepted limit of the retry-clean guarantee, not an open question: discovery and validation are fully retry-clean, and commit is kept narrow enough that newly claimed ownership, metadata, and monitored items are released on failure while root values are the stated boundary.

### 4. Subscribe

Subscription registration consumes the committed monitored-item list.

Hand-off contract for the monitored-item list:

- The list is owned by the load operation until subscribe consumes it. It is not stored on the session before subscribe runs.
- It references properties and subjects in a form that lets subscribe detect detachment through live attachment state (the registry / property-reference validity), rather than pinning detached subjects alive.
- Between commit building the list and subscribe consuming it, a subject may detach. The sweep is the reconciliation point and must consult live attachment state, not the list alone.

Callbacks remain gated until setup is complete:

1. Create subscriptions.
2. Add monitored items.
3. Apply changes.
4. Remove failed monitored items.
5. Move permanent unsupported items to polling when configured. "Permanent unsupported" is defined by the shared status classifier's permanent whitelist, and the hand-off targets the existing polling subsystem.
6. Sweep subjects that detached since commit, consulting live attachment state.
7. Register read-after-write only for surviving, still-attached monitored items, after the sweep.
8. Enable callbacks.

Enabling callbacks only at step 8 is what closes the race where a notification reaches a detached subject during registration. A sweep alone narrows the window but does not close it: a notification arriving between apply-changes and the sweep can still write into a detached subject, which is why PR 313's sweep-only approach leaves a residual window. Sweeping before read-after-write registration (step 6 before step 7) also prevents stale read-after-write tracking for subjects that detached before setup finished.

## Failure Boundaries

Discovery failure:
No durable graph, source ownership, subscription, polling, or read-after-write state changes remain. Retry starts from a clean state.

Validation failure:
Deterministic conflicts are resolved locally and consistently. Validation does not hide transient structure failures as local skip decisions, and does not escalate best-effort transient value-read statuses into load aborts.

Commit failure:
Newly established ownership and metadata are released and the monitored-item list is discarded. Root property values assigned during commit are the documented commit boundary and are not restored, matching PR 313. This boundary is inside the retry-clean guarantee's stated limit, not an unresolved decision.

Subscription failure:
Failed monitored items are pruned locally. Permanent unsupported subscription cases may move to polling. Read-after-write registration happens only after pruning and detached-subject sweeping, and callbacks are enabled only after all of the above.

## Comparison Criteria

Compare PR 313 and the master-based comparison PR on:

- Same acceptance tests pass.
- Fewer internal lifecycle states during loading.
- Clearer phase boundaries (discover and validate fully retry-clean; commit and subscribe the only durable-mutation phases).
- Similar or better browse/read batching behavior.
- Similar or lower code churn outside OPC UA client internals.

Both branches keep the same commit boundary (no root-mutation rollback), so the comparison is not about less rollback code; it is about whether the same behavior needs fewer internal states to produce.

The new PR should be preferred only if it achieves comparable behavior with a simpler design. If it becomes larger or more fragile than the current staged-loader PR, PR 313 should remain the base and only the two genuinely new behaviors (callback gating and sweep-before-read-after-write ordering) should be applied there.
