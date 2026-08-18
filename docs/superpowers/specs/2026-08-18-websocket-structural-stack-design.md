# Landing WebSocket structural mutations as a PR stack

Status: approved design, pre-implementation, reviewed by an independent design-review agent on 2026-08-18 with all findings folded in. Supersedes PR #197 as the landing vehicle; the branch `feature/websocket-structural-mutations` remains the reference implementation and test-spec donor. Related: issue #475 (closed, not planned), PR #476 (merged), PR #358 (TLA+ precedent).

## Goal

Land the capability of PR #197 (stable subject IDs, structural mutation sync, reliable bidirectional WebSocket convergence) on current master as a sequence of reviewable PRs, ending simpler, cleaner, and more fully correct than the branch. The end-state acceptance bar: the Connector Tester runs indefinitely with structural mutations and with transactions on and off, converging every cycle under kills, disconnects, and gaps. Correctness is proven by ported tests, review passes, chaos runs, and a formal model, not by porting defensive machinery.

## Tenets

1. **Machinery must name its trigger.** Nothing is ported because the branch had it. Every mechanism enters with a stated failure it handles, demonstrated by a test or a model-checker trace. Anything that cannot name its trigger stays out until chaos testing or the model produces one. The rule cuts both ways: a branch mechanism whose trigger is shown to still exist on master is ported with that trigger documented (see the ChangeQueueProcessor final flush in PR C).
2. **Per-layer extraction method, chosen by measured drift.** Where master never touched the code (the update protocol model and factories), take the branch wholesale and fix semantic drift. Where master reshaped the code (transport, change queue), re-implement on master using the branch as reference. Measured basis: since merge-base `36dcd520`, master changed the `Updates/` protocol model and factory files by 0 lines, the applier trio by +68/-20 (plus +5/-0 on the `SubjectUpdateExtensions` entry point for the #366 `ChangeOrigin` signature), but the WebSocket client by +240/-113, the server by +152/-85, and `ChangeQueueProcessor` by +227/-78.
3. **Release-safe PRs.** Every PR compiles, passes the suite, and is shippable alone. Shared-model changes adapt all consumers in the same PR. No dead API whose callers land later.

## Approved breaking changes

Approved 2026-08-18:

- **WebSocket wire protocol**: hard break. Bump `WebSocketProtocol.Version`; the existing Welcome handshake rejects mismatched peers. No dual-shape support. The protocol is not in external use yet.
- **`Namotion.Interceptor.Connectors` public API**: delete `SubjectCollectionOperation`, `SubjectCollectionOperationType`, and `SubjectPropertyUpdate.Operations` (structural operations fold into `Items`). Reshape `SubjectPropertyItemUpdate`: `Index` (`required object`) becomes `Id` (`required string`, stable subject id); the previous `Id` (`string?`) becomes `Key` (`string?`, dictionary key, null for collections). `SubjectUpdate.Root` becomes `string?`. PublicApi snapshots updated.
- **Knock-on**: these types are the JSON wire shape wherever serialized, so AspNetCore HTTP payloads change shape for structural properties (`index` to `id`/`key`). MQTT, OPC UA, GraphQL, Mcp, Blazor, and HomeBlaze are unaffected. PR A includes a release-notes entry for the AspNetCore payload change and the package version bump.

## The stack

Four PRs, each targeting master and landing before the next starts. No stacked branches.

### PR A: stable-ID protocol model and serialize/apply pipeline

Method: take the branch's `Updates/` files wholesale, then hand-merge the applier trio. This is stated plainly: the appliers are a hand-merge of two divergent rewrites of the same methods. Master's side added `ChangeOrigin` stamping with the sent-value survival-evidence rule (including the double-conversion reference-equality subtlety in `SubjectUpdateApplyContext`); the branch's side restructured the same methods for ID resolution. Master's origin-survival tests (#366, #374) passing unmodified is a merge gate alongside the ported StableId suites.

- Protocol model per the approved break. Stable base62 subject IDs minted by `SubjectUpdateBuilder`, stored in `subject.Data` (bounded: IDs die with the subject; the registry reverse index is removed on detach).
- Rewritten `SubjectUpdateFactory`, `SubjectUpdateBuilder`, `SubjectItemsUpdateFactory`; ID-resolving `SubjectUpdateApplier`, `SubjectItemsUpdateApplier`, `SubjectUpdateApplyContext`. `CollectionDiffBuilder` deleted.
- **Unregistered-subject serialization: `ProcessSubjectFromMetadata` is kept.** Its trigger is demonstrable on master today: the serializer mints a subject ID and then bails for a subject whose registration has not completed, emitting a reference to an ID with no properties entry; the receiver materializes a default-valued subject that cannot converge later because the applier skips properties absent from an update. Metadata-based complete serialization closes that hole at the emit site. With it in place, no receiver ever gets a reference to a subject it lacks data for, which is what makes dropping the inbound `PendingApplyBuffer` and outbound lazy ID minting coherent as a pair (see below).
- Stripped from the wholesale port, re-added later or never: the `CreateBatchScope` call in the applier (PR B re-adds it with the scope itself), `PendingApplyBuffer` wiring, the per-root apply lock and `GetApplyLock` API (master's callers serialize applies; dropped, stated here so nobody re-derives it), and the branch's `Diag*` public counters (replaced by the diagnostics tripwire below).
- Semantic drift to fix while porting: typed `ChangeOrigin` with one-shot source stamping (#366, unseen by the branch), the per-subject commit revision field (#399), lean readonly-struct `PropertyReference` (#389), commit-order delivery interactions (#420).
- Mechanical consumer adaptation in the same PR: WebSocket serializer, handler, and client compile against the new shapes with existing behavior preserved (value sync both directions, server-to-client structural updates now ID-based) and the protocol version bumped; no new client-to-server structural capability yet. ConnectorTester `SnapshotComparer` and `SnapshotIdMap` adapted to the ID-based shape.
- Docs in the same PR: `docs/connectors-subject-updates.md` rewritten for the ID protocol (the branch's rewrite as the draft). With a hard wire break, protocol docs must change the day the shape does.

Gates: branch test suites ported as the spec (`StableIdApplyTests`, `StableIdCollectionTests`, update flow and snapshot tests), master's origin-survival tests passing unmodified, a regression test for the unregistered-subject emit path, review agent pass, benchmark comparison for `*SubjectUpdateBenchmark*` (partial-update building is a connector hot path), and two Connector Tester runs: `websocket-load` as the new-protocol baseline (recording payload sizes, since 22-character base62 IDs replace small per-update integers on every subject key), and a new server-side structural-churn profile (`StructuralMutationRate` above zero on the server, transactions off). No profile in the repo exercises structural mutation today; PR A rewrites the structural pipeline, so it gets the first structural gate.

### PR B: lifecycle batch scope

Method: cherry-pick from the branch, re-validate against master's lifecycle ordering.

- `LifecycleInterceptor` batch scope keeping a subject attached and registered while it moves between structural properties within a single update; the small `ContextInheritanceHandler` adjustment; `BatchScopeTests`.
- Wired to its caller in the same PR: the applier from PR A uses the scope during apply, so ID-based keep/move semantics cannot transiently detach a subject and drop a concurrent write.
- A-before-B is safe: PR A without the scope is the status quo. Master's cross-property moves already transiently detach, and within-property moves never detach; the scope removes the existing transient, it does not fix a regression A would introduce.
- Re-validated against registry-before-inheritance ordering (#427) and restricted context inheritance (#407), which the branch has never seen.

Gates: `BatchScopeTests`, an integration test proving no transient detach during a move-within-update, review agent pass.

### PR C: WebSocket transport reliability

Method: re-implement on master's current client and server, importing the branch's new files as-is (`ConnectionSequenceTracker`, `ResyncPayload`, `ClientHeartbeatPayload`, `MessageType` additions) and re-wiring by hand. The branch's client lifecycle rework and `SubjectSourceBase` migration are obsolete; master already has both.

- Client-to-server symmetry: monotonic sequence stamping on client updates, per-connection server-side gap detection answering with `Resync` (client re-pushes its complete owned state), client heartbeat carrying the last-sent sequence so the server detects trailing-idle gaps.
- **ChangeQueueProcessor final flush, ported with its named trigger.** Master's `ProcessAsync` disposal path cancels the flush task without draining already-dequeued changes, so up to one buffer window of client writes per disconnect is discarded: never sequenced, never retried, invisible to an agreement oracle because the Welcome apply overwrites them on both sides. The branch's final-flush-in-finally fix carries over; this is tenet 1 operating in the re-add direction.
- The TLA+ model is written and checked before implementation (layout per PR #358). Abstraction boundary, fixed here rather than deferred: the channel within a connection is TCP, neither lossy nor reordering, so channel loss is not the fault model. The first-class fault actions are endpoint-side: connection kill, send-side loss (a change dequeued but never sent), apply-side loss (an update received but not applied), and reconnect with Welcome resync. Checked properties: safety (no acknowledged write is lost; every fault interleaving either converges or triggers recovery) and liveness (after faults quiesce, both sides converge; recovery terminates). A safety-only model is satisfied by an infinite recovery loop, so liveness is not optional. The model defends the sequence and resync protocol; the serialization-layer holes are defended by PR A's regression tests and the chaos gates, and the spec does not claim otherwise.
- Connector Tester work in this PR: a `websocket-transactions` profile (`UseTransactions=true`, value mutations only; #476 added the first end-to-end transactional WebSocket tests, and this chaos run is the sustained proof), so kills and disconnects land mid-commit. Transactional source writes bypass the retry queue, so a mid-commit disconnect surfaces as a commit failure rather than a queued retry, and convergence must hold through it. Harness hardening: the mutation strategies currently catch only `OperationCanceledException`; a chaos-induced commit failure throws `SubjectTransactionException` out of the mutation loop and kills the participant. Commit failure becomes a legitimate outcome: log, dispose the transaction, continue mutating.
- Docs in the same PR: `docs/connectors-websocket.md` updated for sequences, `Resync`, and heartbeats (the branch's rewrite as the draft).

Gates: TLA+ model checked (safety and liveness), branch sequence and resync tests re-targeted to master's implementation, review agent pass, Connector Tester runs: `websocket-chaos`, `websocket-load`, `websocket-transactions`.

### PR D: structural ownership and transactional routing

Method: new work; the branch's ownership changes as reference.

- `ClaimPropertyOwnership` drops the `!p.CanContainSubjects` filter; `OnSubjectAttached` claims properties of newly attached subjects; `SourceOwnershipManager.ReleaseDetachedSubjects` handles the claim-vs-detach race.
- Transactional routing, direction B: structural properties are excluded from the transactional source-write set, apply locally at commit like no-source changes, and reach the server through the post-commit outbound queue. The exact exclusion mechanism (filter in `SourceTransactionWriter` classification versus an ownership-level flag) is decided in this PR's design phase.
- Why this dissolves the #475 read-during-commit problem instead of relaxing the guard: the transactional write path only ever hands the source scalar changes, whose serialization reads nothing. Structural serialization happens on the outbound queue's flush thread, which carries no ambient transaction, so the graph walk hits no guard.
- **Known race, decided in this PR's design phase (not silently accepted):** a flush-thread structural serialization can embed a scalar value read before a transaction commits that scalar. The commit's local apply is source-marked, so echo suppression drops the only notification that would re-assert the committed value; the stale structural update then reverts the server, and both sides converge on the pre-commit value. A committed write is silently lost while the agreement oracle passes. The branch fixed the identical server-side race by serializing `CreatePartialUpdateFromChanges` under the apply lock; PR D's design either applies the client-side analogue (serialize outbound structural serialization against transactional local apply) or documents the revert as accepted semantics. Either way the Connector Tester gains a write-durability assertion (the final converged value of a mutated property is the last value any participant committed to it), because agreement alone cannot see this class of failure.
- **Structural changes in the retry queue and reconnect reconcile, decided in this PR's design phase:** the post-commit push goes through the retry queue, and every kill exercises it. A queued structural change replayed against a model the Welcome resync has since moved is undefined today and could silently revert a committed mutation. Likely shape: retry entries for structural properties re-serialize current model state at flush time, and reconcile treats owned structure as model-wins over the Welcome, but the decision belongs next to the exclusion mechanism.
- Mixed-transaction semantics, documented in `docs/tracking-transactions.md` as a per-mode matrix, not a blanket sentence. Under `Rollback`, a source failure withholds no-source changes entirely, so structurals fail atomically with the scalars locally and nothing diverges. Under `BestEffort`, structurals land while failed scalars are excluded from local apply, and model and server agree. What structural changes actually lack is atomic source delivery, not local all-or-nothing.
- Connector Tester work in this PR, enumerated: a client-side structural-churn profile with transactions off; structural mutations inside transactions; mixed scalar-plus-structural transactional batches; all under the full fault-injection matrix (kills, disconnects, gaps), plus the write-durability oracle above.

Gates: transactional structural end-to-end tests, review agent pass, the full Connector Tester matrix: `websocket-chaos`, `websocket-load`, `websocket-transactions`, server and client structural-churn profiles with transactions off, structural and mixed transactional profiles. This matrix is the acceptance bar from the Goal section.

## What is not ported at all

- **`StateDigest`** (276 lines plus heartbeat fields and recovery wiring): dropped entirely, including as tooling. The Connector Tester's `SnapshotComparer` is already the convergence oracle; the digest's only advantage is O(1) wire size, which tooling does not need. If the comparer is too slow at 20k nodes, that is a comparer perf fix.
- **Accepted consequence, stated explicitly: production has no content-divergence detector.** Sequence gap detection catches lost messages, not wrong applies; an applied-but-wrong divergence in production persists until the affected property changes again or the connection resyncs. This is the price of dropping the digest and it is accepted deliberately. As a cheap tripwire, serialization and apply drop counters are exposed through `WebSocketServerDiagnostics` (master's #454 infrastructure), replacing the branch's `Diag*` public surface.
- **`PendingApplyBuffer` and outbound lazy ID minting**: dropped as a coupled pair. Lazy minting is what creates value updates for subjects the receiver has never seen, which is what made the inbound buffer necessary; with `ProcessSubjectFromMetadata` kept (PR A) and master's drop-with-log policy for unresolvable outbound changes, neither side of the pair has a remaining trigger. They re-enter only if a chaos run or the model produces one.
- **Branch changes to the WebSocket client lifecycle and `SubjectSourceBase` migration**: obsolete; master solved both differently (#454 and the `SubjectSourceBase` template). The branch's `ChangeQueueProcessor` changes are NOT blanket-obsolete: the final flush carries into PR C with its trigger named; the diagnostic counters are replaced by the diagnostics tripwire above.
- **The read-during-commit door stays closed.** Reading the model during a transaction commit remains a contract violation, guarded, with #476's diagnostic naming the remedy. A future connector that genuinely needs sibling state in payloads calls for enriching the change snapshot at capture time, a separate design with its own justification.

## Verification schedule

Long-running verification, agreed now per AGENTS.md: PR A `websocket-load` (with payload-size recording) plus the server-side structural-churn profile; PR C `websocket-chaos`, `websocket-load`, `websocket-transactions`; PR D the full matrix enumerated in its section, which is the acceptance bar. PR B needs no chaos run. Benchmarks: PR A compares `*SubjectUpdateBenchmark*` against master.

## Deferred to per-PR design

- PR D: exclusion mechanism for structural properties in the transactional write set; the serialization-versus-commit ordering decision for the known race; structural retry and reconcile semantics; exact wording of the mixed-transaction matrix in `docs/tracking-transactions.md`.
- Tail PR (only if missed in practice): remaining Connector Tester niceties from the branch (per-cycle CSV recording, failure diagnostics).

## Disposition of #197

Closed as superseded when PR A opens, with a comment linking this spec and the stack. The branch is kept (not deleted) as reference until PR D lands. Everything in the #197 PR body is dispositioned by this spec: ported (protocol, pipeline, metadata serialization, batch scope, sequence and resync machinery, final flush), re-implemented (transport wiring), or named as dropped with the reason (digest, buffer, minting, apply lock, lifecycle rework).
