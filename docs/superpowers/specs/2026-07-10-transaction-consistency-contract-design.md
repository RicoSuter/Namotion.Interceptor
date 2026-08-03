# Transaction consistency contract and epic completion roadmap

- Date: 2026-07-10 (revised 2026-07-18 after ChangeOrigin merged)
- Status: proposed design (brainstormed), pending review
- Issue: #342 (epic #347, row 4). Answers the contract question left open by #338 and #340.
- Relationship: this document narrows the scope that PR #349 and PR #354 deferred to #342, and it builds on the typed `ChangeOrigin` foundation merged in #366 and #374. It does not add a subsystem; it draws the correctness line and records what the epic does and does not build.

## Revision note (2026-07-18)

The 2026-07-10 draft assumed `SubjectPropertyChange.Source` was an untyped `object?` and proposed cutting a typed origin discriminator. That is now obsolete: #366 (merged 2026-07-13) introduced the typed `ChangeOrigin` and #374 (merged 2026-07-15) hardened it. This revision updates the contract, the validation mechanism, and the roadmap to that foundation. The correctness floor is unchanged; only its expression moved onto `ChangeOrigin`.

## 1. Purpose and framing

#342 was originally a broad design task: a consistency contract plus a divergence tracker, a connector state machine, a `ChangeOrigin` discriminator, a manual converge API, and an optional anti-entropy read-back. Stepping back after the earlier rows landed or were designed, most of that scope is either already delivered elsewhere or is observability that sits above the correctness floor.

The guiding constraint is correctness with the least code. The question this document answers is deliberately narrow: what is the minimum needed to guarantee correctness for source transactions. Everything that is not load bearing for that guarantee is cut or deferred, with the rationale recorded so the decision is auditable later.

What is already covered when this design starts:

- P2 (exactly once) is delivered by #343 via PR #344 (merged): the change queue no longer re-pushes a value the transaction already wrote.
- P3 (convergence) is delivered by #340 via PR #349 (designed, not yet merged): source-wins resynchronization, decoupled from reconnect, retrying until it lands.
- P5 (truthful provenance) is delivered by the typed `ChangeOrigin` in PR #366 and PR #374 (merged), which closed #345 and #365 and superseded PR #348. The remaining provenance case (an equality-suppressed projection that leaves a source diverged) is handled by PR #372 (open), which adds the `Correction` kind.

What remains for #342 is therefore small: write the contract, add one validation rule so P3 cannot be blocked by local validation, and decide #338. That is the whole of it.

## 2. Scope

Sources only: the external system owns the data, and the local model mirrors it. Servers are out of scope: there the local model owns the data and connected clients are replicas, so the divergence direction does not exist.

## 3. The consistency contract

Headline: bounded-window eventual consistency, with guaranteed convergence and no silent divergence. True multi-source atomicity is not offered, and the contract states this plainly. There is no distributed transaction coordinator, and OPC UA has no server-side compare-and-swap. What the library guarantees, per source-bound property:

### P1: atomicity, best-effort by mode

A clean commit (single source, single batch, all items accepted) is atomic: both local and source end at the new value, or a clean failure leaves both at the old value. Otherwise the failure-handling mode defines intent:

- `Rollback` aims for all-or-nothing.
- `BestEffort` allows per-change outcomes.

When the source cannot honor the intent (partial acceptance followed by a failed revert, or an indeterminate write), atomicity cannot be guaranteed against a store with no server-side transaction. The fallback is deterministic: the source's actual state wins and the local model converges to it (P3), and the failure is reported to the commit caller (P4). Atomicity is the intent; convergence plus reporting is the guarantee.

### P2: exactly-once delivery

A committed value reaches its source exactly once. The change queue never re-pushes a value the transaction already applied. Delivered by #343 (PR #344, merged).

### P3: convergence

Once writes settle and the transport is healthy, `local == source` for every source-bound property. Temporary divergence windows are permitted (this is a distributed system); permanent silent divergence is not. The convergence mechanism is source-wins resynchronization (#340, PR #349): a full source-wins reload through the inbound path, decoupled from reconnect, retrying until it lands and escalating to a transport cycle when needed.

### P4: no silent divergence

Any divergence a commit cannot resolve is reported to the caller through the thrown `SubjectTransactionException`. Because source-authoritative applies are never rejected by validation (section 5), the only residual that can block convergence is a local property setter that physically cannot store the authoritative value (a type mismatch or a setter that throws). That case surfaces on the inbound-apply error path (logged) and is documented as a modeling bug, not a supported state. The library does not promise to converge against a local setter that refuses the source's own value; it promises that such a case is loud and documented rather than silent.

### P5: truthful provenance

A change carries a source only when its stored value is exactly the value that source sent or confirmed. This is now structural, encoded in the typed `ChangeOrigin` (core `Namotion.Interceptor`):

- `Local`: local user writes, hook cascades, INotifyPropertyChanged write-backs, derived recalculations, and interceptor-transformed values. Flows to every bound source.
- `FromSource`: a value an external source sent inbound.
- `Confirmed`: a value a source confirmed during transaction commit replay.
- `Correction` (PR #372, open): a value the model already holds that a source's inbound projection collapsed to, synthesized so the source converges to the model rather than staying diverged.

Framework-computed consequence writes are `Local` by default, so echo suppression (`change.Origin.Source` reference comparison in `ChangeQueueProcessor`) and re-push stay correct without per-callback scopes. This is what #366 delivered in place of PR #348's counter-scope approach.

### Bounded windows

"Bounded" names the transient windows that are allowed and self-heal:

- Commit in flight: between the source write and the local apply or revert.
- Resync in flight: while a source-wins snapshot is being reloaded.
- Commit-window concurrency: a non-transactional local write or an inbound apply interleaving a commit apply (section 6).

Each window converges once writes settle. None is a permanent state.

## 4. What guarantees the contract

Four mechanisms, only one of which is new work in this issue:

1. Exactly-once and source-marked commit applies: #343 (PR #344, merged). Referenced, not redefined here.
2. Typed provenance so authority attaches to exactly one write: #366 and #374 (merged), plus the `Correction` kind in #372 (open). Referenced, not redefined here.
3. Source-wins resynchronization as the convergence backstop for commit failures: #340 (PR #349, designed). Referenced, not redefined here.
4. Validation scoping so P3 cannot be blocked by local validation: the one new rule in this issue, defined below.

## 5. Mechanism: validation scoping

Validation gates local-origin writes only. It runs when `context.Origin.Kind == ChangeOriginKind.Local` and is skipped otherwise.

Rationale: after #366, the write context carries a typed origin, and `PropertyValidationContext<TProperty>` already exposes it to validators. A non-`Local` origin (`FromSource`, `Confirmed`, and `Correction` once #372 lands) is authoritative or model-derived, not first-time user input. Rejecting those values only creates local/source divergence, which is the thing the contract forbids. Local user writes and derived recalculations are `Local` and are still validated.

Today the plumbing exists but the policy does not: `ValidationInterceptor.WriteProperty` builds a `PropertyValidationContext` with `context.Origin` and passes it to each validator, but it still validates every origin, and `DataAnnotationsValidator` explicitly ignores the origin. The rule adds the missing skip at the interceptor, so the guarantee is library-wide rather than per-validator opt-in:

```csharp
public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
{
    // Source-authoritative writes (inbound FromSource, commit-replay Confirmed) are not
    // user input. Rejecting external truth only creates local/source divergence, so
    // validation gates local-origin writes only. A source value the setter cannot store
    // is a modeling bug, surfaced by the setter itself, not something validation masks.
    if (context.Origin.Kind == ChangeOriginKind.Local)
    {
        // existing validate-and-throw over the registered IPropertyValidator services
    }

    next(ref context);
}
```

Properties of this rule:

- `ChangeOrigin` and `ChangeOriginKind` live in the core `Namotion.Interceptor` assembly, which `ValidationInterceptor` already references (it reads `context.Origin` today). No new dependency and no layering inversion. The 2026-07-10 draft's concern about reaching for `ISubjectSource` from the validation layer no longer applies.
- It is default-on with no configuration knob. An application that wants to police external data should filter at the source, not through a validator that silently drops and diverges.
- It is a behavior change that must be pinned by a test. Today `ValidationInterceptor` also runs at commit replay, where the origin is now `Confirmed`, so a source-owned property captured as user input is validated twice: once at capture (`Local`) and again at replay (`Confirmed`). The new rule removes the second pass. This is the intended correction (a value validated at capture, or confirmed by the source, must not be re-rejected at replay), and it is called out rather than made silently.

Residual: a local setter that cannot physically store the authoritative value (type mismatch, or a setter that throws) still fails on the inbound-apply path (`SubjectPropertyWriter.Write` logs and drops). That property stays diverged until the source pushes it again. This is the documented modeling-bug residual of P4.

## 6. Decision: #338 commit-window isolation

Decision: document as best-effort, proven to self-heal to convergence. Do not gate the commit window.

`_isCommitting` already rejects tracked writes during a commit, so a concurrent transactional write cannot interleave. Only non-transactional local writes and inbound applies can, because they do not pass through `CaptureChange`. The cases:

- Inbound source value, same property, mid-window: the commit is writing that property to the source, so after the commit both sides hold the committed value. The mid-window inbound value was the source's prior state and is superseded. Converges.
- Concurrent non-transactional local write, same property, mid-window: ordinary last-writer-wins between two local intents. The model's final value is pushed out by the normal outbound path and converges. A lost racy write is standard concurrency behavior, not a divergence.
- Concurrent third-party write at the source during the commit: the only unarbitrable case. The revert writes captured old values, not a compare-and-swap, so it can overwrite another client's intent. It does not break local/source agreement (both end at the same value). This is a documented protocol limit, matching residual 3 in the PR #349 design.

In every failed-commit case, P3's resync is the ultimate backstop. Gating the window (a lock spanning the commit apply, nested inside the write paths) adds cost and complexity without buying convergence that settle-and-resync does not already provide. #338 is therefore closed by this design as documented best-effort.

## 7. Explicitly out of scope

Recorded with rationale so the cuts are auditable. Note that the typed origin discriminator, which the 2026-07-10 draft proposed cutting, is no longer a cut: it shipped in #366 and is now the foundation the contract rests on.

| Item | Disposition and rationale |
|---|---|
| `TriggeredBy` and `Presumed` origin kinds | Deferred, not built. #366 parked both in #342 as additive extensions to `ChangeOrigin`. Under the least-code constraint they stay deferred: `Presumed` is only meaningful if the read-back optimization (PR #349, deferred) is adopted, and `TriggeredBy` (causal provenance for diagnostics) has no consumer yet. Both are additive later without changing the contract. |
| `SourceConsistencyTracker`, `DivergenceDetected`/`DivergenceResolved` events, green/degraded aggregate | Observability, above the correctness floor. If wanted later it rides the existing `SourceEvent` stream from PR #354 (add a `PendingDivergenceCount` mirror of `PendingWriteCount`, plus an event kind), not a new subsystem. P4 is already satisfied for the commit caller by the exception. |
| Connector state machine (`Stopped`/`Connecting`/`Synchronizing`/`Synchronized`) | Already delivered by PR #354's `SourceState` (`NoSource`/`Connecting`/`Synchronized`/`Stopped`), which deliberately folds `Synchronizing` into `Connecting`. Rebuilding it here would duplicate #354. |
| Anti-entropy: periodic read-back compare or dirty re-push | Resync plus the reconnect snapshot already guarantee eventual convergence. PR #349 deferred targeted read-back as an optimization, and no workload needs scheduled detection. Not built. |
| Rows 3 and 4 as features (undeliverable non-transactional write, inbound apply failure) | These are the non-transactional write path, not transaction correctness. Row 4 intersects transactions only through resync-apply failure, which section 5 plus the P4 residual already cover. The broader non-transactional write loss (write-retry-queue overflow drops the oldest silently) is a real but separate gap, tracked as its own follow-up (section 9). |

## 8. Epic completion roadmap

This is the plan to finish epic #347 in the simplest correct order. Two dependencies that the original epic assumed have dissolved under this design:

- #342 no longer depends on #354. Cutting the tracker and events means the contract needs no observability substrate, so #354 leaves the transaction-correctness critical path and becomes an independent, land-whenever feature.
- #342's runtime change depends only on #344 and #366 (both merged). The validation-scoping rule is small and needs nothing from #349's implementation. The contract's P3 guarantee, however, is only true in code once #349 is implemented, so the document and the resync land close together.

### Current status (2026-07-18)

| Row | Issue | PR | Status | Meaning |
|---|---|---|---|---|
| 1 | #343 | #344 | merged | source-marked commit applies (P2) |
| 2 | #345, #365 | #366, #374 | merged; #348 closed (superseded) | typed `ChangeOrigin` provenance (P5) |
| 2b | (part of P5) | #372 | open | `Correction` origin kind; finishes the diverged-projection case |
| 3 | #346 | none | open, rejected | queue-level echo filter unsound; folded here; close the GitHub issue |
| 4 | #342 | this spec | in design | the contract, the validation rule, the #338 decision |
| 5 | #340 | #349 | design committed | resync primitive (P3 backstop); implementation pending |
| 6 | #338 | none | open | commit-window isolation; decided here (section 6) |
| 7 | none | none | not started | connector-tester soak and acceptance gate |
| adjacent | #354 | #354 | design committed | source sync state and event stream (observability), now independent |
| adjacent | #355 and local reconciliation redesign | #355 | open | connect/reconnect write reconciliation (non-transactional path) |

### Recommended order

1. Merge PR #372 (`Correction` kind). It finishes P5 by converging the equality-suppressed diverged-projection case, the last silent-divergence hole in provenance.
2. Merge this #342 spec (row 4). It is a design artifact and a narrowing document: it defines the contract, specifies the validation rule against the merged `ChangeOrigin`, decides #338, and records the cuts. Its immediate value is that it resolves every "owned by #342" punt in the #349 and #354 specs, so those can be implemented against a settled scope.
3. Implement PR #349 (row 5): the resync primitive, the runtime P3 backstop. Merge master into that branch first (it predates #344 and #366), then implement. When this lands, P3 becomes real in code.
4. Implement #342 (row 4): the origin-gated skip in `ValidationInterceptor`, finalize the durable contract doc (section 11), and close #338. Small enough to ride #349's PR or stand alone.
5. Independent tracks, any time, off the critical path:
   - #354: source sync state and event stream. No longer gates transaction correctness.
   - #355 and the local reconciliation redesign: connect/reconnect write reconciliation on the non-transactional path.
   - Row 7: the connector-tester soak gate. Under this design it asserts the convergence floor (post-settle `local == source`) plus the loud residual, rather than the stronger tracker-based invariants that were cut. It remains a nightly or soak gate, not a per-PR gate.

### Simplest path to "epic done"

The epic is complete for transaction correctness once rows 1, 2 (including #372), 4, and 5 are merged and #338 is closed. That is: #344 (done), #366 and #374 (done), #372, this contract plus the validation rule, and #349's resync. Everything else (#354, #355, row 7) improves observability, non-transactional reconciliation, and test coverage; none of it is required to honor the contract in section 3.

## 9. Follow-up issues (output of this design)

- Close #338 with the decision in section 6.
- Close #346 on GitHub with its rejection rationale (already recorded in the epic).
- New issue: non-transactional undeliverable-write policy (row 3 divergence source). The write-retry queue drops the oldest silently on overflow, drops immediately at size 0, and retries a permanently rejected write forever. Decide converge (resync), bounded retry then report, or configurable. Separate from transaction correctness; reuses the existing `RequestResynchronization` primitive.
- Optional, later: divergence observability on PR #354's `SourceEvent` stream, only if a consumer needs to alert on stuck divergence.
- Optional, later: `TriggeredBy` and `Presumed` origin kinds, only when a causal-diagnostics consumer or the read-back optimization appears.

## 10. Testing

- Validation scoping: a `Local` write is validated as today; a `FromSource` apply skips validation and the source value is stored; a `Confirmed` commit replay does not re-run validation (the flagged behavior change); a derived recalculation (`Local`) is still validated.
- Contract convergence (P3, P4): covered by PR #349's suite; referenced here, not duplicated.
- #338: a non-transactional write or an inbound apply interleaving a commit converges after settle. This is an integration or soak assertion, tied to row 7.
- Conventions: `When<Condition>_Then<ExpectedBehavior>` naming, explicit Arrange/Act/Assert, and `AsyncTestHelpers.WaitUntilAsync` for eventual paths rather than fixed delays.

## 11. Documentation

One canonical location for the durable contract, cross-referenced elsewhere:

- `docs/tracking-transactions.md` gains a "Consistency contract" section holding the P1 to P5 promise, the bounded-window list, and the #338 best-effort statement. This is the long-lived, user-facing home. It references the `ChangeOrigin` kinds documented with #366.
- `docs/connectors.md` cross-references it from the inbound update and source sections, and documents the validation-scoping rule (source-authoritative applies are not rejected) where inbound apply behavior is described.
- This spec is the decision record and does not duplicate the contract text; it links to the canonical doc.

## 12. Components touched

| Component | Change |
|---|---|
| `Namotion.Interceptor.Validation` | `ValidationInterceptor.WriteProperty` gains the `context.Origin.Kind == ChangeOriginKind.Local` guard. Tests pin the four validation cases in section 10. |
| `docs/tracking-transactions.md` | new "Consistency contract" section (canonical) |
| `docs/connectors.md` | cross-reference plus the validation-scoping note |
| Issue tracker | close #338 and #346; open the non-transactional undeliverable-write follow-up |

No changes to `ISubjectSource`, `SubjectPropertyChange`, `WriteResult`, the transaction types, `ChangeOrigin`, or any connector are required by this issue. The origin substrate is already merged (#366, #374); the resync backstop and the sync-state stream are extended by #349 and #354 on their own branches. This issue only adds the validation skip and narrows what the others must do.
