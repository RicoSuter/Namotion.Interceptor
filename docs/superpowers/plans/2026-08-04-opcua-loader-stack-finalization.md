# OPC UA loader stack: finalization plan

Status: approved 2026-08-04. All three decisions in section 3 are settled.

Covers PRs #397, #398, #357, #313 and issue #320. Consolidates all four into one PR.

## 1. Where the stack stands

Measured, not estimated. Temporary counter around `session.BrowseAsync` / `BrowseNextAsync`,
in-process `OpcUaTestServer`, 241 subjects (40 machines x 5 sensors), ~1000 value properties,
server advertising `MaxNodesPerBrowse = 4000` and `MaxBrowseContinuationPoints = 100`.

| variant | browse round-trips | nodes submitted |
|---|---|---|
| master | 1,083 | 1,083 |
| #313 | 9 | 1,084 |
| #357 (four-phase) | 16 | 1,084 |
| #357, continuation cap disabled | 9 | 1,084 |

Wall clock is not a usable signal on loopback. At 5 ms RTT master is roughly 5.4 s of browse
latency against 45 ms; at 20 ms WAN roughly 22 s against 180 ms.

Three conclusions follow:

1. The 120x IO reduction belongs to #313. #357 adds none of it.
2. #357 is a 78% round-trip regression against #313, caused entirely by #397's unconditional
   continuation-point cap.
3. The four-phase atomicity layer buys zero performance, and its correctness model is not
   reachable with the registry APIs as they exist.

On point 3: `RegisteredSubject.Children` is written only by `SubjectRegistry.HandleLifecycleChange`,
so a fabricated detached view always has empty `Children`. The loader reads `.Children` in three
places to decide whether to reuse an existing child subject. Off-tree discovery therefore silently
loses subject reuse and logs a misleading "the parent type must instantiate this property in its
constructor" message. Issue #320 had already ruled this out:

> **Out of scope:** structural atomicity for `DynamicSubject` roots (where dynamic properties are
> added during load). That requires a heavier "staging subject" design and should be tracked
> separately if needed.

and prescribed what #313 already implements: defer only value-bearing root mutations
(`OpcUaLoadContext.QueueOrApplySetValue`), keep subjects on-tree during discovery
(`RegisterStagedSubject` calling `AddFallbackContext` eagerly) so `.Children` resolves.

### Branch geometry

| branch | PR | base | commits behind master | prod diff |
|---|---|---|---|---|
| `feature/opcua-session-batching` | #397 | master | 2 | +651 / -21 |
| `feature/opcua-subscription-gating` | #398 | #397 | 2 | +136 / -39 |
| `design/opcua-master-comparison-loader` | #357 | #398 | 2 | +1,461 / -672 |
| `feature/improve-opc-ua-loader-browse-performance` | #313 | master @ #350 | **18** | +2,036 / -570 |

Master has since rewritten four files #313 also touches: `SubscriptionManager.cs` (+254),
`OpcUaStatusCodeClassifier.cs` (+52, landed via #364), `OutboundWriter.cs`,
`SubscriptionHealthMonitor.cs`. Conflicts on a #313 rebase are guaranteed and non-trivial.

### What is actually shared between #313 and #357

The two PRs are far closer than their diffs suggest.

Byte-identical: `OpcUaTypeResolver.cs` (165), `OpcUaBrowseName.cs` (33).
Near-identical: `OpcUaSubjectClientSource.cs` (64 differing lines out of ~770).

Tests are effectively the same suite. Seven of nine loader test files are byte-identical; the
other two differ by 14 and 33 lines. The only genuine behavioural delta in tests is the
browse-completion contract: on hitting the pagination safety bound #313 returns a truncated child
list, #357 omits the node. That behaviour lives in `OpcUaSessionExtensions`, so it belongs to #397,
not to the loader model.

The real divergence is one file group:

| | #313 | #357 |
|---|---|---|
| loader | `OpcUaSubjectLoader.cs` 743 | `OpcUaSubjectLoader.cs` 41 (shell) |
| staging | `OpcUaLoadContext.cs` 293 | `LoadPlan/OpcUaLoadPlan.cs` 130 |
| attributes | `OpcUaAttributeLoader.cs` 262 | folded into planner |
| planner | n/a | `LoadPlan/OpcUaLoadPlanner.cs` 1,069 |
| **total** | **1,298** | **1,240** |

Same size, different shape. #313 splits into three cohesive files; #357 concentrates into one
1,069-line planner, which also runs against the repository preference for extracting cohesive
helper classes rather than growing a single large file.

## 2. What we keep and what we drop

**Keep (#313's model):**

- Eager attach during discovery (`RegisterStagedSubject` adds the parent context as fallback
  immediately), so `.Children` resolves and subject reuse works.
- Deferred root value burst (`QueueOrApplySetValue` defers only ops whose
  `property.Subject` is the root), which is exactly the #320 contract.
- `Apply()` claim-then-root-op ordering, so an observer seeing a new root child finds all of that
  child's leaves already source-owned.
- The rollback bookkeeping in `Apply()` and `Dispose()`, and the eight tests in
  `OpcUaSubjectLoaderFailureTests`. **Correction to an earlier read of mine:** these are not
  four-phase scaffolding. They cover #313's own documented contract (no registry orphans after a
  failed load, pre-existing ownership retained across a mid-`Apply` throw, clean retry, nested
  staged rollback, permanent-bad-status skip). They stay.
- The three-file split (loader / load context / attribute loader).

**Keep (#397/#398):**

- `_callbacksEnabled` gate plus `CompleteSetup` ordering from #398. #313 has neither; its
  `SubscriptionManager` comment concedes the window is open.
- #397's `OpcUaSessionExtensions`, `OpcUaStatusCodeClassifier` split
  (`SessionPermanentCodes` vs `LoadSkipCodes`), `OpcUaTransientServiceException`,
  split-and-retry on `BadTooManyOperations`, and the browse-completion contract.
  #313's own copies of these three files are superseded and get deleted.

**Drop:**

- `LoadPlan/OpcUaLoadPlanner.cs` and `LoadPlan/OpcUaLoadPlan.cs` in their entirety.
- The off-tree `_detachedViews` / `GetRegisteredView` mechanism.
- PRs #397, #398, #357 and #313 as separate units. See D3.

## 3. Decisions (resolved)

### D1. Public API break in `OpcUaTypeResolver`

Master today:

```csharp
public virtual Task<Type?> TryGetTypeForNodeAsync(ISession session, ReferenceDescription reference, CancellationToken cancellationToken)
```

Both #313 and #357 replace it with:

```csharp
public virtual Type ResolveObjectNodeType(IReadOnlyList<ReferenceDescription> children)
public virtual Task<IReadOnlyDictionary<NodeId, Type?>> ResolveVariableTypesAsync(ISession session, IReadOnlyCollection<ReferenceDescription> variables, CancellationToken cancellationToken)
protected virtual Type? TryMapBuiltInType(BuiltInType builtInType)
```

This is inherent to batching, not cosmetic: a per-node async resolver cannot be called once per
batch. The type is `public virtual`, so a subclass overriding `TryGetTypeForNodeAsync` breaks at
compile time.

Note that three of #313's four apparent API breaks are already on master via #364
(`MaxItemsPerSubscription`, `MaxReferencesPerNode`, `SubscriptionMaxNotificationsPerPublish`), so
this is the only one left.

**Decided: take the break (approved 2026-08-04).** Keeping a per-node shim would either defeat the
batching or require a resolver that silently behaves differently depending on which entry point was
used. Call it out in the PR description as a breaking change with the migration shape: an override
of `TryGetTypeForNodeAsync` becomes an override of `ResolveVariableTypesAsync`, receiving the whole
batch instead of one reference.

### D2. Shape of the continuation-point cap

Current code, `OpcUaSessionExtensions.cs:31-41`, caps unconditionally:

```csharp
int continuationPointLimit = session.ServerCapabilities?.MaxBrowseContinuationPoints ?? 0;
return continuationPointLimit > 0 && continuationPointLimit < operationLimit
    ? continuationPointLimit
    : operationLimit;
```

This is what costs the 7 extra round-trips. The cap only matters when a browse can actually leave
continuation points open, which requires `maxReferencesPerNode != 0`. When it is 0 the server
returns everything in one page and issues no continuation point at all (confirmed in the SDK at
`CustomNodeManager.cs:1317`).

**Decided:**

1. Apply the cap only when `maxReferencesPerNode != 0`.
2. Add `BadNoContinuationPoints` handling to `BrowseBatchAsync`: on that status, halve the batch
   size and retry, so a server that under-reports its quota still converges. Today the code has
   split-and-retry only for `BadTooManyOperations`, and `BadNoContinuationPoints` is not in
   `LoadSkipCodes`, so it throws transient and the reconnect retries the same batch size forever.

Alternative considered and rejected: leave the cap unconditional and accept 16 round-trips. It is
still 68x better than master, but it is a self-inflicted cost with a known cheap fix.

### D3. PR structure

**Decided: one PR on a fresh branch off current master. Close #397, #398, #357 and #313.**

The stack was split because ~6,400 lines read as unreviewable in one unit. That rationale does not
survive what we now know:

- #397 has zero callers on its own branch. A reviewer cannot judge whether `BrowseNodesAsync`'s
  completion contract is right without seeing the consumer that depends on it.
- #397 needs the D2 behaviour change, which is motivated entirely by the loader's round-trip
  measurements. Reviewing it before that motivation exists is reviewing it blind.
- The #313 and #357 test suites are ~90% identical, so a split review reads the same ~4,500 lines
  two or three times.

Consolidated size against master: **+2,248 / -732 production** across 13 files, **+4,506 / -757
tests**. Swapping the planner for the #313 model is close to a wash (1,240 lines against 1,298), so
the final PR lands near +2,300 prod and +4,500 test. Two thirds of the total is new test files.
The substance is one ~1,300-line loader file group.

Reviewability comes from commit structure instead of PR structure: five commits, reviewable in
order, approved once. See section 4.

The tree is seeded from #357's content on current master with the planner swapped out, not by
replaying #313's 18-commit-stale branch. #313 branched at #350 and master has since rewritten four
files it touches.

One wording correction is owed on the #313 comment already posted: it says the refactor is "mostly
deletion", which was based on treating `OpcUaSubjectLoaderFailureTests` as four-phase scaffolding.
It is not. That correction goes in the closing comment.

## 4. Execution plan

One PR, five commits, on a fresh branch off current master. Each commit builds and its tests pass
on its own, so the PR can be reviewed commit by commit.

### Commit 1: fix the shutdown-flag clobber in `SubscriptionManager`

This one is a live bug on master today, independent of everything else in this PR.
`SubscriptionManager.cs:119` (master) resets the shutdown flag inside
`CreateBatchedSubscriptionsAsync`:

```csharp
// Reset shutdown flag AFTER clearing collections - prevents old callbacks from processing
// during the window between flag reset and collection clearing (defense-in-depth).
_shuttingDown = false;
```

`_shuttingDown` is set true only in `DisposeAsync` (master line 530), and disposal is terminal:
`SessionManager.DisposeAsync` is guarded by an `Interlocked.Exchange(ref _disposed, 1)` and the
`SubscriptionManager` instance is constructed once per `SessionManager` and never replaced. So the
flag should be monotonic. As written, a reconnect-triggered setup racing a disposal clobbers the
disposal signal, and the callback guard at master line 180 then lets notifications through on a
disposed manager.

Commit 3 adds `CompleteSetup`, whose guard reads the same flag:

```csharp
// Never re-open a gate that DisposeAsync closed: it may have run concurrently with setup.
_callbacksEnabled = !_shuttingDown;
```

Without this fix that guard is dead on arrival, which is why the fix goes first.

**Fix:** delete the `_shuttingDown = false;` line and its comment.

**Test:** `WhenDisposeRacesSubscriptionSetup_ThenCallbacksStaySuppressed`. Dispose the manager, then
run subscription setup, then assert no notification is delivered. Event-based sync only, no delays.

**Verify:** `dotnet test src/Namotion.Interceptor.OpcUa.Tests` (full, including integration, since
this is connector code).

### Commit 2: batched session primitives

#397's content: `OpcUaSessionExtensions.cs`, the `OpcUaStatusCodeClassifier` split
(`SessionPermanentCodes` vs `LoadSkipCodes`), `OpcUaTransientServiceException`, split-and-retry on
`BadTooManyOperations`, and the browse-completion contract. Plus D2 (conditional cap and
`BadNoContinuationPoints` fallback) folded in rather than added as a follow-up, so the file never
lands in the state that costs 7 round-trips.

**Tests to add** in `OpcUaSessionExtensionsTests`:

- `WhenMaxReferencesPerNodeIsZero_ThenBrowseBatchIsNotCappedByContinuationPoints`: mock session
  advertising `MaxNodesPerBrowse = 4000` and `MaxBrowseContinuationPoints = 100`, browse 1,000
  nodes with `maxReferencesPerNode: 0`, assert a single browse call.
- `WhenMaxReferencesPerNodeIsNonZero_ThenBrowseBatchIsCappedByContinuationPoints`: same server,
  `maxReferencesPerNode: 50`, assert batches of 100.
- `WhenServerReturnsBadNoContinuationPoints_ThenBatchSizeHalvesAndRetries`: mock returns the status
  for batches above N, assert the call converges and every input node appears in the result.

#397's existing `OpcUaSessionExtensionsTests` (719 lines) must stay green, in particular the two
browse-completion-contract assertions (`Assert.Empty(result)` on hitting the pagination bound).

**Verify:** `dotnet test src/Namotion.Interceptor.OpcUa.Tests`.

### Commit 3: subscription setup gating

#398's content: the `_callbacksEnabled` gate and `CompleteSetup` (sweep detached subjects, register
read-after-write survivors, open the gate, in that order), plus `OpcUaSubscriptionSweepOrderingTests`
and the `SubscriptionManagerTestHarness`. Depends on commit 1 for its guard to be live.

**Verify:** `dotnet test src/Namotion.Interceptor.OpcUa.Tests`.

### Commit 4: loader configuration knobs

`OpcUaClientConfiguration` gains `MaxBrowseContinuations` (default 100, bounds pagination depth so a
server returning a fresh continuation point forever cannot loop the loader) and
`MaxAttributeTraversals` (default 100, bounds attribute-of-attribute depth), each with a positive
value check in the existing validation method, plus the matching `OpcUaClientConfigurationTests`
cases.

Purely additive with no consumers until commit 5, so it builds and tests on its own.

**Correction to the original plan.** This commit was going to carry the batched type resolver as
well. It cannot: master's `OpcUaSubjectLoader` calls `TypeResolver.TryGetTypeForNodeAsync` at lines
102 and 228, and the batched resolver removes that method, so a commit that lands the resolver
without the loader does not compile. `OpcUaTypeResolver` also depends on `OpcUaBrowseName`. The
resolver, the browse-name helper, and the loader are therefore one atomic unit and all move together
in commit 5. Splitting them would have been an invented seam rather than a real one.

**Verify:** `dotnet test src/Namotion.Interceptor.OpcUa.Tests`. The public API gains exactly the two
config properties.

### Commit 5: the loader

Seeded from #357's tree with the planner replaced by #313's model.

Three things verified before starting, which make this commit much smaller than first estimated:

- **The loader seam is byte-identical between the two models.** Both declare
  `private readonly OpcUaSubjectLoader _subjectLoader`, both construct it as
  `new OpcUaSubjectLoader(subject, configuration, _ownership, this, logger)`, and both call
  `await _subjectLoader.LoadSubjectAsync(_subject, rootNode, session, loadCts.Token)`.
  `OpcUaSubjectClientSource` therefore needs **no changes at all**. Keep master's version.
- **The 64-line `OpcUaSubjectClientSource` divergence is not model-related.** It is
  `EscalatePersistentlyFailedItemsAsync` (already on master, #313 simply predates it), a
  `CancelSafelyAsync` helper extracted over three call sites, `FindChildByBrowseName` rewritten
  from LINQ to a foreach, a lock-free rationale comment on `RemoveItemsForSubject`, and one stray
  blank line. Starting from master's version picks all of that up for free.
- **#313 already calls commit 2's exact API.** `OpcUaLoadContext.BrowseAsync` already invokes
  `Session.BrowseNodesAsync(missing, _maxReferencesPerNode, _maxBrowseContinuations, _logger, CancellationToken)`,
  matching #397's signature exactly. No rewrite needed, only deleting #313's own copies of the three
  protocol files so commit 2's versions bind.

Steps:

1. Bring in `Client/OpcUaBrowseName.cs` (33) and the batched `Client/OpcUaTypeResolver.cs` (165),
   byte-identical between #313 and #357. This is where the D1 public API break lands, and it has to
   be in the same commit as the loader because master's loader calls the method it removes.
2. Replace master's `Client/OpcUaSubjectLoader.cs` with #313's (743) and add
   `Client/OpcUaLoadContext.cs` (293) and `Client/OpcUaAttributeLoader.cs` (262). Do not bring
   #313's own copies of `OpcUaSessionExtensions.cs`, `OpcUaStatusCodeClassifier.cs` or
   `OpcUaTransientServiceException.cs`: commit 2's versions are the ones that bind.
3. Bring in the loader test suite (`OpcUaSubjectLoaderTests`, `...TestsBase`, `...BatchingTests`,
   `...DedupTests`, `...AttributeTests`, `...DictionaryReuseTests`, `...FailureTests`,
   `OpcUaRootPathResolutionTests`), taking the #313 variants and applying the two known deltas:
   `OpcUaSubjectLoaderFailureTests` naming (`Apply` against `Commit`, and the
   `WithPropertyChangeObservable` to `WithPropertyChangeSubscriptions` master rename), and
   `OpcUaSubjectLoaderBatchingTests` lines 721-725 and 795-797, which take the #357 expectations
   (`Assert.Empty(result)`) because commit 2 now owns that behaviour.

**Verify:** full `Namotion.Interceptor.OpcUa.Tests` including integration, plus the round-trip probe
showing 9. Nothing merges until the probe reproduces 9. Accept the new
`VerifyChecksTests.PublicApi.verified.txt`: it should show exactly the `OpcUaTypeResolver` change
from D1 and nothing else, since `OpcUaTransientServiceException` landed in commit 2 and the two
config properties in commit 4.

### Finally

Open the PR, then close #397, #398, #357 and #313 with pointers to it. The closing comment on #313
carries the correction noted in D3.

## 5. Risks

**Commit 5 is the only risky one.** Commits 1 through 4 are small and independently tested.

Two risks originally listed here have been retired by inspection:

- *The `OpcUaSubjectClientSource` reconciliation* turned out not to be model-related at all, and the
  loader seam is byte-identical. See commit 5 above. No reconciliation needed.
- *The browse-completion contract change* turned out to be the contract #313's loader already
  assumes. Every consumption site uses `TryGetValue` and handles the miss, and the miss branches say
  so explicitly: "Missing entry = the browse failed with a permanent bad status (transient failures
  abort the whole load). Keep the property's current items rather than overwriting them with an
  empty collection; the next load retries." What #397 changes is that the *pagination-bound* case
  now behaves like the permanent-bad-status case instead of returning a truncated list that reads
  as complete. That makes the session layer consistent with what the loader already expected, so it
  strengthens #313 rather than destabilising it.

Remaining risk:

- *`OpcUaSubjectLoaderBatchingTests` lines 721-725 and 795-797* are the one place where the old
  truncating expectation is written down. They must flip to `Assert.Empty(result)`. If any other
  test turns out to depend on truncation, that is a signal to re-examine rather than to edit the
  assertion.
- *`OpcUaDataTypesTests.NullableGuidType_ShouldSyncBothDirections` is a confirmed pre-existing
  flake on master* (1 failure in 4 runs on an unmodified worktree). Do not attribute it to this work.
- *CI does not run connector integration tests for shared-library path changes.* Run
  `Namotion.Interceptor.OpcUa.Tests` locally at every step.

## 6. Housekeeping

Throwaway worktrees still holding the temporary `BrowseCounter.cs` and browse instrumentation:
`/tmp/master-rt`, `/tmp/pr313-rt`. Remove once the step 2 re-measurement is done, since the probe
needs to run again there for the before/after comparison. `/tmp/pr397`, `/tmp/pr398`, `/tmp/pr357`
stay until their PRs close.
