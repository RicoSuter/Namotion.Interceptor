# OPC UA Loader Comparison: Four-Phase Plan vs PR 313 Staged Loader

This document compares two implementations of the OPC UA client subject loader against the criteria in the design spec (`docs/superpowers/specs/2026-07-06-opcua-master-comparison-loader-design.md`):

- **PR 313** (`feature/improve-opc-ua-loader-browse-performance`): a staged loader built around a stateful `OpcUaLoadContext` that mixes live and deferred mutations during discovery and commits the deferred ones in `Apply()`.
- **This branch** (`design/opcua-master-comparison-loader`): a four-phase model (discover, validate, commit, subscribe) where discovery accumulates ownership claims, values, and root assignments into the passive `OpcUaLoadPlan` and a single ordered `Commit` applies them. Dynamic property creation and staged-subject linking still happen eagerly during discovery, as in PR 313 (see below), so discovery is not purely passive.

Both branches reuse the same protocol and classification layer verbatim (batched browse/read via `OpcUaSessionExtensions`, `OpcUaStatusCodeClassifier`, `OpcUaTypeResolver`, `SubscriptionHealthMonitor`, `OutboundWriter`), so the loader architecture is the only variable under comparison.

## Same acceptance tests pass

PR 313's targeted test suite was copied into this branch first and frozen as the contract (Task 1). The rewrite makes it pass without weakening any assertion. Only one test coupled to a removed internal type (`WhenApplyFailsMidway_ThenOwnershipFromPreviousLoadIsRetained`) was re-pointed from `OpcUaLoadContext` to `OpcUaLoadPlan`, preserving its exact assertions.

- Non-integration OPC UA tests: 299 passed, 0 failed (the frozen suite plus the two new-behavior tests).
- OPC UA integration tests: 27 passed, 0 failed (load, subscribe with callback gating and sweep ordering, and reconnect against the sample server).
- Public API snapshot (`VerifyChecksTests.PublicApi`): passes against the copied `verified.txt` with no changes, so the public surface equals PR 313's.

## Fewer internal lifecycle states during loading

PR 313's `OpcUaLoadContext` is a single stateful object that, during discovery, applies non-root value assignments live, links staged subjects live, and queues claims and root operations for `Apply()`. It carries a browse cache, a claim queue with a dedup index, a root-operation queue, a staged-subject list, and a `_committed` flag, and it has two rollback scopes: `Dispose()` for staged subjects and `Apply()`'s catch for claims.

This branch splits those responsibilities:

- `OpcUaLoadPlan` is passive data (staged subjects, claims with the smaller-node-id tie-break, deferred staged values, deferred root assignments) plus one `Commit`. It performs no mutation until `Commit`.
- `OpcUaLoadPlanner` owns discovery: a browse cache, node reuse maps, and the staged-subject links it established (for discovery-failure rollback).

The concrete simplification is that value assignments are uniform: PR 313 applied non-root values live during discovery and deferred only root values, whereas this branch defers all value assignments to `Commit`. There is one commit path with one claim-and-metadata rollback, rather than live-plus-deferred application interleaved through discovery.

What did not get simpler: discovery still has two eager side effects in both designs, because nested dynamic discovery requires them. Dynamic properties are added to their subjects during discovery so the subject is usable, and newly created child subjects are linked into the parent context so their own children are discoverable. The staged-subject linking needs a discovery-failure rollback (a leftover dynamic property is instead re-matched by the next load through its node-id attribute). So `OpcUaLoadPlan` is passive data, but the discovery phase as a whole is not purely side-effect-free.

## Clearer phase boundaries

This branch draws a hard line: discover and validate are fully retry-clean (no ownership, subscription, polling, or read-after-write state survives a failure), and commit and subscribe are the only phases that change durable state. PR 313's boundary is softer because `QueueOrApplySetValue` applies non-root values during discovery. The frozen failure tests confirm both branches leave no orphaned subjects or claims after a failed load.

## Browse/read batching parity

Identical. Both branches call the same copied `OpcUaSessionExtensions.BrowseNodesAsync`/`ReadNodesAsync`/`DistinctByResolvedNodeId`, which handle chunking to the server's operation limits, split-retry on oversized batches, and BrowseNext continuation. The batching, continuation, and split-retry tests pass unchanged.

## Code size and churn

| Metric | This branch | PR 313 |
|--------|-------------|--------|
| Loader core (production) | 1,239 lines (`OpcUaLoadPlan` 132, `OpcUaLoadPlanner` 1,066, thin `OpcUaSubjectLoader` 41) | 1,298 lines (`OpcUaSubjectLoader` 743, `OpcUaAttributeLoader` 262, `OpcUaLoadContext` 293) |
| Churn outside the OPC UA client internals | 0 files (the new `IReadAfterWriteRegistrar` seam lives under `Client/ReadAfterWrite/`) | 21 files, +296 / -10 |

The loader core is comparable in size (about 4 percent smaller). The clearer win is containment: this branch confines all changes to the OPC UA client internals, tests, and docs, whereas PR 313 also touched 21 files outside its client internals.

## Two genuinely new behaviors

Both are absent from PR 313 and are covered by new focused tests:

- **Callback gating** (`OpcUaSubscriptionCallbackGatingTests`): data-change callbacks are enabled only as the final step of subscription setup, closing the window where a notification could write into a subject mid-setup. PR 313 attached the callback before creating monitored items and guarded only with a shutting-down flag.
- **Sweep before read-after-write** (`OpcUaSubscriptionSweepOrderingTests`): the detached-subject sweep runs before read-after-write registration, so a subject that detached during setup is never registered. PR 313 registered read-after-write per batch and then swept.

## Known minor difference

`OpcUaLoadPlan.AddClaim` resolves a duplicate-node-id tie-break silently, whereas PR 313's `OpcUaLoadContext.QueueClaim` logged a warning on the same event. This is a deliberate reduction in log noise for a rare graph-shaped-address-space case; the resolution (keep the smaller node id) is identical and no test observes the log.

## Verdict

This branch meets the spec's bar: it passes the same frozen acceptance suite, keeps browse/read batching identical, draws clearer phase boundaries, and confines its churn to the OPC UA client internals, all at a loader-core size comparable to (slightly below) PR 313. The simplification is modest rather than dramatic, because nested dynamic discovery keeps a discovery-time staged-subject rollback in both designs. The concrete wins are the uniform deferral of all mutations to a single ordered commit, the tighter change containment, and the two new reliability behaviors (callback gating and sweep-before-read-after-write ordering).

If the maintainer judges the four-phase design's clearer boundaries and containment worth adopting, this branch is a suitable base. If parity in size is judged insufficient to justify replacing the merged PR 313 loader, the two new behaviors (callback gating and sweep-before-read-after-write ordering) are self-contained and can be applied to PR 313 directly.
