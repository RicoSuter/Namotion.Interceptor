# Landing WebSocket structural mutations as a PR stack

Status: approved design, pre-implementation. Supersedes PR #197 as the landing vehicle; the branch `feature/websocket-structural-mutations` remains the reference implementation and test-spec donor. Related: issue #475 (closed, not planned), PR #476 (merged), PR #358 (TLA+ precedent).

## Goal

Land the capability of PR #197 (stable subject IDs, structural mutation sync, reliable bidirectional WebSocket convergence) on current master as a sequence of reviewable PRs, ending simpler, cleaner, and more fully correct than the branch. Correctness is proven by ported tests, review passes, chaos runs, and a formal model, not by porting defensive machinery.

## Tenets

1. **Machinery must name its trigger.** Nothing is ported because the branch had it. Every mechanism enters with a stated failure it handles, demonstrated by a test or a model-checker trace. Anything that cannot name its trigger stays out until chaos testing or the model produces one. When the model finds a diverging interleaving, the trace is both the justification and the regression test for the mechanism that fixes it.
2. **Per-layer extraction method, chosen by measured drift.** Where master never touched the code (the update pipeline), take the branch wholesale and fix semantic drift. Where master reshaped the code (transport, change queue), re-implement on master using the branch as reference. Measured basis: since merge-base `36dcd520`, master changed the `Updates/` model and factory files by 0 lines and the applier trio by +73/-20, but the WebSocket client by +240/-113, the server by +152/-85, and `ChangeQueueProcessor` by +227/-78.
3. **Release-safe PRs.** Every PR compiles, passes the suite, and is shippable alone. Shared-model changes adapt all consumers in the same PR. No dead API whose callers land later.

## Approved breaking changes

Approved 2026-08-18:

- **WebSocket wire protocol**: hard break. Bump `WebSocketProtocol.Version`; the existing Welcome handshake rejects mismatched peers. No dual-shape support. The protocol is not in external use yet.
- **`Namotion.Interceptor.Connectors` public API**: delete `SubjectCollectionOperation`, `SubjectCollectionOperationType`, and `SubjectPropertyUpdate.Operations` (structural operations fold into `Items`). Reshape `SubjectPropertyItemUpdate`: `Index` (`required object`) becomes `Id` (`required string`, stable subject id); the previous `Id` (`string?`) becomes `Key` (`string?`, dictionary key, null for collections). `SubjectUpdate.Root` becomes `string?`. PublicApi snapshots updated.
- **Knock-on**: these types are the JSON wire shape wherever serialized, so AspNetCore HTTP payloads change shape for structural properties (`index` to `id`/`key`). MQTT, OPC UA, GraphQL, Mcp, Blazor, and HomeBlaze are unaffected.

## The stack

Four PRs, each targeting master and landing before the next starts. No stacked branches.

### PR A: stable-ID protocol model and serialize/apply pipeline

Method: take the branch's `Updates/` files wholesale, reconcile the small master delta, fix semantic drift.

- Protocol model per the approved break. Stable base62 subject IDs minted by `SubjectUpdateBuilder`.
- Rewritten `SubjectUpdateFactory`, `SubjectUpdateBuilder`, `SubjectItemsUpdateFactory`; ID-resolving `SubjectUpdateApplier`, `SubjectItemsUpdateApplier`, `SubjectUpdateApplyContext`. `CollectionDiffBuilder` deleted.
- Semantic drift to fix while porting: typed `ChangeOrigin` with one-shot source stamping (#366, unseen by the branch), the per-subject commit revision field (#399), lean readonly-struct `PropertyReference` (#389), commit-order delivery interactions (#420).
- Mechanical consumer adaptation in the same PR: WebSocket serializer, handler, and client compile against the new shapes with existing behavior preserved (value sync both directions, server-to-client structural updates now ID-based) and the protocol version bumped; no new client-to-server structural capability yet. ConnectorTester `SnapshotComparer` and `SnapshotIdMap` adapted to the ID-based shape.
- Not ported: `PendingApplyBuffer`, lazy on-demand ID minting for unregistered subjects. Both were built against the branch's weaker delivery layer; master has since landed commit-order delivery (#420) and commit revisions (#399). They re-enter only with a demonstrated trigger (tenet 1).

Gates: branch test suites ported as the spec (`StableIdApplyTests`, `StableIdCollectionTests`, update flow and snapshot tests), review agent pass, benchmark comparison for `*SubjectUpdateBenchmark*` (partial-update building is a connector hot path), one `websocket-load` Connector Tester run as the new-protocol baseline.

### PR B: lifecycle batch scope

Method: cherry-pick from the branch, re-validate against master's lifecycle ordering.

- `LifecycleInterceptor` batch scope keeping a subject attached and registered while it moves between structural properties within a single update; the small `ContextInheritanceHandler` adjustment; `BatchScopeTests`.
- Wired to its caller in the same PR: the applier from PR A uses the scope during apply, so ID-based keep/move semantics cannot transiently detach a subject and drop a concurrent write.
- Re-validated against registry-before-inheritance ordering (#427) and restricted context inheritance (#407), which the branch has never seen.

Gates: `BatchScopeTests`, an integration test proving no transient detach during a move-within-update, review agent pass.

### PR C: WebSocket transport reliability

Method: re-implement on master's current client and server, importing the branch's new files as-is (`ConnectionSequenceTracker`, `ResyncPayload`, `ClientHeartbeatPayload`, `MessageType` additions) and re-wiring by hand. The branch's client lifecycle rework and `SubjectSourceBase` migration are obsolete; master already has both.

- Client-to-server symmetry: monotonic sequence stamping on client updates, per-connection server-side gap detection answering with `Resync` (client re-pushes its complete owned state), client heartbeat carrying the last-sent sequence so the server detects trailing-idle gaps.
- The TLA+ model is written and checked before implementation (layout per PR #358). It models server and client exchanging sequenced updates over a lossy, reorderable, killable channel with exactly the recovery moves the protocol has (gap detection to Welcome resync one way, `Resync` request the other, heartbeat-carried trailing-gap detection). Checked properties: every fault interleaving either converges or triggers recovery (no silent permanent divergence), and no acknowledged write is lost. The model is what makes dropping the branch's extra nets defensible; any diverging interleaving it finds identifies the one net that was actually needed, re-added with its trigger documented.
- Connector Tester work in this PR: a `websocket-transactions` profile (`UseTransactions=true`, value mutations only; scalar transactions over WebSocket already work, proven by #476), so kills and disconnects land mid-commit. Transactional source writes bypass the retry queue, so a mid-commit disconnect surfaces as a commit failure rather than a queued retry, and convergence must hold through it. Harness hardening: the mutation strategies currently catch only `OperationCanceledException`; a chaos-induced commit failure throws `SubjectTransactionException` out of the mutation loop and kills the participant. Commit failure becomes a legitimate outcome: log, dispose the transaction, continue mutating.

Gates: TLA+ model checked, branch sequence and resync tests re-targeted to master's implementation, `websocket-chaos`, `websocket-load`, and `websocket-transactions` Connector Tester runs.

### PR D: structural ownership and transactional routing

Method: new work; the branch's ownership changes as reference.

- `ClaimPropertyOwnership` drops the `!p.CanContainSubjects` filter; `OnSubjectAttached` claims properties of newly attached subjects; `SourceOwnershipManager.ReleaseDetachedSubjects` handles the claim-vs-detach race.
- Transactional routing, direction B: structural properties are excluded from the transactional source-write set, apply locally at commit like no-source changes, and reach the server through the post-commit outbound queue. The exact exclusion mechanism (filter in `SourceTransactionWriter` classification versus an ownership-level flag) is decided in this PR's design phase.
- Why this dissolves the #475 read-during-commit problem instead of relaxing the guard: the transactional write path only ever hands the source scalar changes, whose serialization reads nothing. Structural serialization happens on the outbound queue's flush thread, which carries no ambient transaction, so the graph walk hits no guard. A flush-thread walk can observe another flow's half-applied commit, which is the existing quiescent-consistency delivery model (`SourceValuesMayBeStale`), converged by subsequent notifications.
- Mixed-transaction semantics, documented in `docs/tracking-transactions.md` as part of this PR: a transaction mixing scalar and structural changes commits scalars through the transactional source write and structurals through apply-then-queue. Structural mutations do not get the transaction's all-or-nothing source guarantee; scalars inside a newly attached subtree were never part of the transaction.
- Connector Tester work in this PR: structural mutations inside transactions and mixed scalar-plus-structural transactional batches, under the full fault-injection matrix. The convergence oracle is unchanged; final-state agreement is indifferent to which commits failed along the way.

Gates: transactional structural end-to-end tests, full chaos matrix including the transactional profiles, review agent pass.

## What is not ported at all

- **`StateDigest`** (276 lines plus heartbeat fields and recovery wiring): dropped entirely, including as tooling. The Connector Tester's `SnapshotComparer` is already the convergence oracle; the digest's only advantage is O(1) wire size, which tooling does not need. If the comparer is too slow at 20k nodes, that is a comparer perf fix.
- **`PendingApplyBuffer` and lazy ID minting**: see PR A. Re-enter only with a demonstrated trigger.
- **Branch changes to `ChangeQueueProcessor` and the WebSocket client lifecycle**: obsolete; master solved both differently (#420, #350, #454, and the `SubjectSourceBase` template).
- **The read-during-commit door stays closed.** Reading the model during a transaction commit remains a contract violation, guarded, with #476's diagnostic naming the remedy. A future connector that genuinely needs sibling state in payloads calls for enriching the change snapshot at capture time, a separate design with its own justification.

## Verification schedule

Long-running verification, agreed now per AGENTS.md: PR A one `websocket-load` run; PR C `websocket-chaos`, `websocket-load`, `websocket-transactions` runs; PR D the full matrix including transactional profiles. PR B needs no chaos run. Benchmarks: PR A compares `*SubjectUpdateBenchmark*` against master.

## Deferred to per-PR design

- PR C: TLA+ model file location and structure (follow #358).
- PR D: exclusion mechanism for structural properties in the transactional write set; exact wording of the mixed-transaction semantics in `docs/tracking-transactions.md`.
- Tail PR (only if missed in practice): remaining Connector Tester niceties from the branch (per-cycle CSV recording, failure diagnostics).

## Disposition of #197

Closed as superseded when PR A opens, with a comment linking this spec and the stack. The branch is kept (not deleted) as reference until PR D lands.
