# Connector Reliability Redesign

Proposals arising from the 2026-07-28 audit. Current-state facts and citations live in `docs/design/connectors-reliability.md`; this document only argues for changes.

Nothing here is approved. Section 9 lists the decisions needed before any of it starts.

## 1. The finding that reorders everything

The audit ran eight independent tracks. Four of them, working on unrelated subsystems, traced their worst gap to the same root:

- **Inbound apply**: an inbound value is dropped while the field holds a local one.
- **Transactions (#338)**: the commit window leaves model and source permanently split.
- **Change ordering (#373)**: a delayed notification silently reverts a just-written value.
- **Core pipeline**: two writers pass the equality gate on the same stale snapshot.

All four are the same defect. The generated setter reads the backing field outside the lock, the equality handler compares that unsynchronized snapshot, and only the store is under `SyncRoot`. Every echo-suppression, reconciliation, and convergence scheme in the codebase is layered on top of a racy read-compare-write.

A second, independent convergence appeared in the proposals. Three audits, reasoning from outbound writes, from inbound applies, and from reconnection, each arrived at the same structure: **one per-property map owned by the source for its whole lifetime**, holding what that source is believed to know. That map is proposal 2.

The consequence for planning: proposal 2 compares an intended value against a current value, so it inherits the race. **Proposal 1 must land first**, or the new machinery is built on the same sand.

## 2. Proposal 1: make the write a real compare-and-set

Move the current-value read and the equality comparison inside `SyncRoot`, which the terminal already holds.

**Closes.** The four findings in section 1, plus duplicate `(Old, New)` publication for a single transition, plus the merged-diff baseline that never existed.

**Cost.** One lock acquisition on the suppressed write path, which is today the cheapest path. This is a real hot-path regression and must be benchmarked before and after.

**Risk.** This changes what `CurrentValue` means to an `IWriteInterceptor`: today it is a pre-chain snapshot, afterwards it is the value under the lock. That is a public API behavior change and needs explicit approval before implementation.

**Do not skip this and patch the symptoms.** #373, PR #375, and the ABA hole in the reconnect re-apply are three surfaces of one defect. Fixing them separately means three mechanisms that each partially compensate for a race that is still there.

## 3. Proposal 2: one per-property pending-write map

Replace the change queue's deduplication buffer, the write retry queue, and the reconnect re-apply with a single map keyed by `PropertyReference`, owned by the source for its whole lifetime rather than per connection.

| Today | After |
|---|---|
| Coalescing: backward scan plus reverse, reorders across properties | Map upsert. Free, no scan, no reorder |
| Retry queue: ring buffer, drop-oldest, unbounded duplicates per property | Entry stays until acknowledged. One entry per property |
| Reconnect reconcile: compares against the change's old value, which is wrong | "If the current local value still equals my intended value, resend, else drop". Correct, and needs no old-value baseline |
| Bound: post-deduplication count, misdenominated, and `null` everywhere | Distinct-property count, naturally bounded by model size |
| Overflow: a policy nobody enables | Overflow means more distinct properties than the model has, which is a bug, not routine |

Add a commit sequence stamped inside `SyncRoot` and stored on the map entry, so last-writer-wins is decided by commit order rather than dispatch order.

**Subsumes.** #385, #281, #352, #282, #228, #200, #362, and the ABA hole. Makes PR #353 unnecessary.

**Obsoletes.** PR #350 already shipped a bound that no call site enables; the map replaces the concept rather than enabling it.

**Extension.** The audit of the inbound path proposed the same map hold "last value known to source S". That collapses five mechanisms (equality suppression, origin survival, queue echo skip, re-apply comparison, read-after-write timestamp skip) into one predicate: publish to S if and only if `shadow(S) != stored`. Adopt this if proposal 1 lands, since it depends on the stored value being read coherently.

## 4. Proposal 3: reconnection re-enters the pump

`SubjectSourceBase.ExecuteAsync` defines the correct sequence, including the retry-queue reconcile, but it only ever runs on first connect. Every connector reconnects inside its own monitor task and calls the load path directly, skipping the reconcile. `ReapplyRetryQueue` has exactly one call site.

Invert this. Connectors **report** connection loss; the base **performs** recovery.

- Add one hook for health or loss signalling.
- Add an explicit state machine: `Stopped, Connecting, Loading, Synchronized, Degraded`.
- Recovery becomes a full pump cycle, so the reconcile happens by construction.

**Absorbs into the base.** The outer retry loop, backoff with jitter and the circuit breaker (duplicated near-verbatim between MQTT and WebSocket, roughly 120 lines), kill-restart, the WebSocket monitor loop, the five scattered `StartBuffering` call sites, reconnect-time reconcile, stop-time drain, and state publication.

**Stays protocol-specific, as health signals feeding the base.** The OPC UA SDK's subscription transfer, subscription and node-level healing, polling fallback, read-after-write, and the WebSocket wire-level liveness checks (receive timeout, sequence gap, heartbeat gap).

**Closes.** The reconnect blind window, the missing drain, the replay-over-read inversion (one place to add a freshness guard), and two OPC UA races that exist only because more than one actor can drive reconnection.

**Cost.** Reconnect re-runs `StartListeningAsync`, which for OPC UA means re-browsing. Slower than today's monitored-item recreation, but re-browsing is precisely what fixes the structural-drift half of the false eventual-consistency guarantee. Make it opt-out, not opt-in.

**Relationship to PR #354.** That PR publishes state without removing the duplicated machinery. It should be extended to own reconnection, not merely observe it, and should absorb #195 and #277.

## 5. Proposal 4: `SubjectServerBase`

Servers have no base class, and the audit found 12 reliability gaps with no open issue against 2 issues we started with. Nine behaviors are currently duplicated three times or missing entirely:

1. Restart and backoff (OPC UA has it, MQTT has a fixed delay, standalone WebSocket has none and hot-spins)
2. Change queue construction and lifetime (identical in all three)
3. Bounded queue and drop accounting (exists, zero adopters)
4. Per-client registry and connection accounting (three implementations)
5. Initial-state gating and concurrency capping (two hand-rolled, one of them is #292)
6. Slow-client policy (only WebSocket; the others delegate to an SDK with no visibility)
7. **Inbound failure reporting (solved zero times)**
8. Detach cleanup and handler lifetime symmetry
9. A diagnostics surface (only OPC UA has one)

Item 7 is the highest-value piece. A central `InboundWriteFailure { Path, Reason, Message }` produced by the apply path, with a per-protocol hook to serialize it, closes the server half of #231, the MQTT swallowed-PUBACK gap, and the OPC UA rejected-value divergence at once.

**Also decide once.** Outbound loss detection. WebSocket proves an in-house sequence is cheap. MQTT's QoS1 and OPC UA's monitored-item queues are *assumptions*: neither server checks queue-overflow status codes or broker drop counters. A base-class delivery monitor that each protocol feeds would make all three observable.

## 6. Proposal 5: make unclaimed properties visible

Two changes, both small, that convert the worst class of silent gap into a signal.

**Claim coverage report.** Return a `ClaimReport { Claimed, ConflictedWith, NoMapping, NoSetter }` from the startup scan and log it once at warning level when any bucket is non-empty. Today a property dropped by a mapping filter is dropped with zero diagnostics, and MQTT's "subscribed to N topics" counts candidates before the skips, so it over-reports.

**Attach-side claiming.** Have the ownership manager subscribe to attach as well as detach and invoke a source-supplied claim callback. This closes the late-attached-subject gap and makes `connectors.md:25` true. Fold #387 into this; they are the same root cause seen from the subscription side.

Also filter claims on `HasSetter`, or claim read-only properties explicitly and log them. Reclassify #102 from a cosmetic access-modifier question to a correctness issue.

## 7. Proposal 6: one path engine

Five independent subject-path walkers exist, and they disagree in ways that are already causing a live bug: MQTT builds topics with the Registry builder and resolves them with the Connectors walker, so it publishes `[InlinePaths]` topics it can never accept a write on. #240's note that the duplication is "not causing bugs today" is wrong.

Make Registry the only walker; Connectors layers factory, caching, and write concerns on top. A shared core needs: convert unwrapping, three resolution modes (strict single, member chain, chain with index), index and dictionary-key literal extraction, one throw-versus-null policy, and it must live in `Namotion.Interceptor` core to avoid the Tracking-to-Registry cycle.

Separately, the mapper stack is heavier than the problem: six indirections to turn a property into a topic string, three of which only produce a segment, with two layers reading the same fluent registry. Collapsing to one segment source plus one path composer would remove `IPropertyMapper<T>`, the reverse composite, both composite shims, and both path-provider adapters, and would force forward and reverse symmetry because both directions would share a composer.

## 8. Proposal 7: core hardening and verification

**Core.** Three cheap, high-value fixes independent of everything above:

- Contain lifecycle handler exceptions per handler and make the reconciliation baseline update unconditional in a `finally`, so a throw cannot desynchronize the baseline from a store that already committed (#384, which is understated as filed).
- Add a thread-static depth counter to the notification path, converting an uncatchable `StackOverflowException` into a catchable diagnostic naming the cycle.
- Isolate dispatch failures per channel so one throwing observer cannot suppress the derived cascade.

**Verification.** The tester is not in CI and nothing gates on it. In rough order of value per effort:

1. Turn on what already exists: `UseTransactions` and `StructuralMutationRate` are implemented and disabled in every shipped profile.
2. Add the tester to a nightly job. It should not replace the unit tests, which are the ones that run in CI and which cover ordering the tester cannot see.
3. Assert the invariants that already have counters: drop count and pending-write count should be zero at convergence.
4. Build the fake-source harness (epic row 7, still unfiled). No architectural blockers: the three source hooks are already virtual, the sealed pump is what you want to keep, and a delegate-driven test source already exists. Roughly 3-5 days for the harness, plus 3-5 days for the observation-log assertions, which are the real work and which depend on the state machine from proposal 3.

Also make the fault recovery real: the chaos engine's recovery only flips a flag rather than reversing the fault, against a hardcoded grace period, so recovery is currently timing luck.

## 9. Sequencing

```
Proposal 1 (atomic CAS)  ─┬─→ Proposal 2 (pending-write map)  ─→ Proposal 3 (pump re-entry)
                          └─→ core hardening (8a)

Proposal 4 (SubjectServerBase)   independent
Proposal 5 (claim visibility)    independent, smallest, highest signal-per-line
Proposal 6 (path engine)         independent
Proposal 7 (verification)        1 and 2 are independent; 4 gates on proposal 3
```

Proposals 4, 5, and 6 share no code with 1 through 3 and can run in parallel by different people.

**Recommended first move: proposal 5.** It is the smallest change, it needs no API approval, and it converts the largest class of invisible failure into a log line. Everything else is easier to prioritize once we can see how many properties are actually unclaimed in a real deployment.

## 10. Decisions needed

1. **Approve the `CurrentValue` semantic change** in proposal 1. Public API behavior change; nothing else in this document is sound without it.
2. **Scope.** Is this initiative source-side only (1, 2, 3), or does it include the server side (4)? They share almost no code, and the server side is where the unknowns are.
3. **Transactions.** The path is roughly 1400 lines delivering, by its own contract, bounded-window eventual consistency plus a synchronous acknowledgment. The compensating-revert machinery is most of the complexity and is the source of three of the four persistent-divergence cells. Rollback-by-inverse-write on a store with no compare-and-swap cannot be made correct. Options: keep and shrink it (route through the retry queue, add resync, deprecate multi-source rollback), or offer a simpler single-property confirmed write alongside and let the heavy path atrophy.
4. **#338 closure.** The recorded decision to close it as best-effort self-healing rests on two premises that are both false. Either gate the commit window, re-push the applied value once, or make closure explicitly conditional on the resync primitive landing.
5. **Contract ownership.** `docs/design/connectors-reliability.md` now states the contract for all four quadrants. Decide whether the P1-P5 spec is superseded by it or remains the source-write-path detail it references.

## 11. Issue housekeeping

Verified against code, independent of any proposal above.

**Close as stale or wrong.** #214 (the named mechanism no longer exists), #210 (no property-removal API exists anywhere; fold into the future removal epic), #346 (already marked rejected in the epic), #363 (written against symbols that exist only on an unmerged branch).

**Merge.** #282, #228, and #200 are one issue. #240 and #378 are both path consolidation, though #378 is partly stale: two of its four resolvers already share a helper.

**Retitle or re-scope.** #308 (item 3 is fixed), #384 (add the baseline desynchronization), #231 (widen from client-side acknowledgment to server-side inbound failure reporting), #266 (the premise about attribute traversal is refuted; the real problem is path identity), #102 (from cosmetic to correctness), #292 (add the stall dimension), #206 (the real problem is re-sending the entire root subject on every change), #373 (add the interleaving that needs no delayed notification).

**File.** The fake-source harness, and the twelve server-side gaps listed in the contract document.
