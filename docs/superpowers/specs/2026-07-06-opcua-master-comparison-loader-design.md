# OPC UA Master-Based Comparison Loader Design

## Context

PR 313 improves OPC UA client loader performance and reliability through batched browse/read calls, staged load context, retry behavior, and subscription setup changes. The current PR is a useful reference implementation, but it has accumulated enough lifecycle complexity that a parallel design should be evaluated.

The comparison branch starts from `origin/master`, not from the PR branch. Its goal is to satisfy the same behavioral requirements and tests as the current PR while using a simpler architecture.

Breaking public API changes from the current PR are acceptable and do not require compatibility aliases.

## Goals

- Preserve the current PR's intended OPC UA loader performance gains.
- Preserve or improve the current PR's reliability behavior.
- Use the current PR's tests as the acceptance suite for the comparison branch.
- Make load failure boundaries easier to reason about than the current staged-loader implementation.
- Keep durable graph, ownership, and subscription mutations out of discovery wherever practical.
- Keep subscription setup free of detached-subject and read-after-write registration races.

## Non-Goals

- Do not preserve the current PR's internal loader structure for its own sake.
- Do not add compatibility aliases for renamed configuration properties.
- Do not broaden the scope into unrelated OPC UA server, MQTT, WebSocket, or connector-tester behavior.
- Do not optimize for minimal diff size. Optimize for a clearer design that can be compared fairly against the current PR.

## Acceptance Strategy

The new branch should first port or recreate the relevant tests from the current PR. These tests define parity between the two PRs.

Required coverage areas:

- Batched browse and read behavior.
- Browse continuation handling.
- Split retry for operation-limit or response-size failures.
- Loader failure cleanup and retryability.
- Dynamic property and dynamic attribute deduplication.
- Deterministic handling of duplicate references and duplicate value claims.
- Initial value read behavior.
- Subscription setup failure handling.
- Detached-subject cleanup during subscription setup.
- Read-after-write registration only for surviving monitored items.
- Reconnect and subscription health behavior where the current PR added or changed tests.

The comparison is meaningful only if the new branch passes the same targeted suite that validates the current PR.

## Architecture

Use a two-phase OPC UA load model with an explicit subscription setup phase.

### 1. Discover

Discovery builds an in-memory load plan from OPC UA browse/read results. It should batch protocol calls, follow continuation points, resolve node ids, read metadata and values, and record candidate subject, property, attribute, and monitored-item bindings.

Discovery should avoid durable side effects:

- No durable source ownership claims.
- No root property mutations.
- No subscription registration.
- No long-lived monitored-item registration.
- No read-after-write registration.

Limited temporary object construction is acceptable if it is owned by the load plan and discarded on discovery failure.

### 2. Validate

Validation normalizes and deduplicates the plan before durable application state is changed.

Deterministic server-shape conflicts should be resolved here:

- Duplicate dynamic property names.
- Duplicate dynamic attribute names.
- Duplicate structured targets.
- Duplicate dictionary keys.
- Multiple browse paths to the same value property.
- Unresolvable node ids.
- Unsupported monitored-item candidates.

Retry should be reserved for transient protocol or session failures. Deterministic address-space conflicts should be skipped, deduped, or reported according to documented local rules because retrying the same server shape is not expected to help.

### 3. Commit

Commit applies the accepted plan to the Interceptor graph in one bounded phase:

1. Create or attach planned subjects.
2. Apply accepted initial values.
3. Claim source ownership.
4. Store OPC UA node-id metadata on claimed properties.
5. Build the monitored-item list for successfully claimed properties.

Commit should be ordered so rollback remains small. If full root-mutation rollback is too invasive, the design may define commit as the final boundary, but then the retry-clean guarantee must stop before root setters run. The preferred design is to keep discovery and validation fully retry-clean and keep commit narrow enough that newly claimed ownership, metadata, and monitored items can be released on failure.

### 4. Subscribe

Subscription registration consumes the committed monitored-item list.

Callbacks remain gated until setup is complete:

1. Create subscriptions.
2. Add monitored items.
3. Apply changes.
4. Remove failed monitored items.
5. Move permanent unsupported items to polling when configured.
6. Sweep detached subjects.
7. Register read-after-write only for surviving monitored items.
8. Enable callbacks.

This removes the race where notifications can reach detached subjects during registration and prevents stale read-after-write tracking for subjects that detached before setup finished.

## Failure Boundaries

Discovery failure:
No durable graph, source ownership, subscription, polling, or read-after-write state changes should remain. Retry starts from a clean state.

Validation failure:
Deterministic conflicts are resolved locally and consistently. Validation should not hide transient OPC UA failures as local skip decisions.

Commit failure:
Newly established ownership and monitored-item state should be released. If root mutation rollback is not implemented, root mutation must be documented as the commit boundary and not included in the retry-clean guarantee.

Subscription failure:
Failed monitored items are pruned locally. Permanent unsupported subscription cases may move to polling. Read-after-write registration happens only after pruning and detached-subject sweeping.

## Comparison Criteria

Compare the current PR and the master-based comparison PR on:

- Same acceptance tests pass.
- Fewer lifecycle states during loading.
- Clearer failure boundaries.
- Less rollback-specific code.
- Less coupling between attribute loading, subject loading, ownership, and subscription setup.
- Similar or better browse/read batching behavior.
- Similar or lower code churn outside OPC UA client internals.

The new PR should be preferred only if it achieves comparable behavior with a simpler design. If it becomes larger or more fragile than the current staged-loader PR, the current PR should remain the base and only the subscription/read-after-write cleanup should be applied there.
