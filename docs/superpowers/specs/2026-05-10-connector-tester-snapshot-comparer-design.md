# ConnectorTester Snapshot Comparer

Date: 2026-05-10
Status: Draft

## Problem

The ConnectorTester `VerificationEngine` decides whether a test cycle passes by snapshotting each participant's `TestNode` graph and comparing them. The current comparison is raw string equality on a JSON-serialized `SubjectUpdate.CreateCompleteUpdate(root, [])`.

This works for OPC UA and MQTT today (path-derived subject IDs are identical across participants, and high-rate value mutations cause all timestamps to converge in steady state). It does not work for any protocol that generates per-participant subject IDs, has order-dependent serialization (dictionary item order), or has connector paths that legitimately produce divergent timestamp state.

Two open feature branches have hit the limitation and each invented a local fix:

- `feature/websocket-structural-mutations` (PR #197): adds normalization on top of `SubjectUpdate.CreateCompleteUpdate` (subject sort, property sort, dictionary-by-key sort, root-id replacement, structural-timestamp stripping), introduces a field-by-field JSON-walking comparison, and applies a "null timestamp on either side matches anything" rule.
- `feature/opcua-bidirectional-structural-sync`: replaces `CreateCompleteUpdate` with a direct graph walk that builds a `SortedDictionary<string, JsonObject>` keyed by hierarchical path, and compares only value properties.

Both fixes work locally. They diverge in approach. Without a canonical solution on master, the two branches conflict at rebase time and any future structural-mutation work (planned for OPC UA, MQTT) will repeat the choice.

## Goals

- Land a single canonical snapshot-comparison contract on master, suitable for OPC UA, MQTT, WebSocket, and future connectors.
- Verify maximally: catch value differences, structural-position differences, timestamp-propagation differences (where determinate), and bugs in `CreateCompleteUpdate` / `ApplySubjectUpdate`.
- Respect the architectural contract: the `NullTimestampTicks` sentinel in `SubjectChangeContext` deliberately preserves "never explicitly written" state across the wire. The comparator must respect that, not flag it.
- Make the comparison rules an explicit, unit-tested contract, not a maintained-by-comment convention.
- Improve failure diagnostics so that when convergence fails, the operator can localize the bug to connector wire, snapshot logic, applier, or test model without re-running the test.
- Coordinate so PR #197 and `feature/opcua-bidirectional-structural-sync` rebase cleanly and drop their local fixes.

## Non-goals

- No `MutationEngine` refactor (the WebSocket branch's collapse of the abstract base + subclasses is out of scope).
- No `TestNode` graph or property changes.
- No removal of load profiles, `PerformanceProfiler`, `cycles.csv`, or any infrastructure landed by PR #279.
- No WebSocket-specific code in this PR. WebSocket sequence diagnostics stays on PR #197.
- No structural-mutation enablement work in OPC UA/MQTT connectors. This PR only updates the tester.
- No new public API in core libraries. Everything lives inside the `Namotion.Interceptor.ConnectorTester` project.

## Architectural alignment with `SubjectChangeContext`

This is load-bearing for the comparison rule and worth pinning explicitly. The contract referenced here lives on master since PR #198 ("fix: Preserve null timestamp when source provides no timestamp", commit `cf917172`); this PR consumes it without modifying it.

`SubjectChangeContext` distinguishes three states for a write timestamp (`SubjectChangeContext.cs:18-24,63-76`):

| State | Sentinel | Meaning |
|---|---|---|
| Undefined | `0` (UndefinedTimestampTicks) | No scope set; getter falls back to `GetTimestampFunction()` |
| Explicit null | `-1` (NullTimestampTicks) | Caller explicitly wrote `WithChangedTimestamp(null)`; preserved as "never explicitly written" downstream |
| Real timestamp | positive ticks | Caller provided a real timestamp |

`PropertyReference.TryGetWriteTimestamp()` (`PropertyReference.cs:86-95`) returns `null` when the stored ticks are `0` (i.e. the explicit-null sentinel was preserved through the apply chain) and returns the timestamp otherwise. `SubjectUpdateFactory` reads this `null`-able timestamp and serializes it to the wire as `null` or a value. Receivers apply via `WithChangedTimestamp(propertyUpdate.Timestamp)` which round-trips the explicit-null state.

In other words: `null` is a *first-class state* in the model, not a missing value. Two participants comparing snapshots can legitimately end up with `null` on one side and a real timestamp on the other when one participant locally constructed the property and another adopted it from an apply path that preserved a null timestamp. Failing the cycle in that case would penalise correct connector behaviour.

Therefore the canonical comparison rule treats `null` on either side as compatible; only when both sides have a non-null value must the values be equal. This is *not* a softening: it is the explicit contract of the timestamp model.

## Design

### Components

| Status | Path | Purpose |
|---|---|---|
| New | `src/Namotion.Interceptor.ConnectorTester/Engine/SnapshotComparer.cs` | Static class. Single home for snapshot construction and comparison. |
| New | `src/Namotion.Interceptor.ConnectorTester.Tests/Engine/SnapshotComparerTests.cs` | xUnit unit tests proving the contract. |
| Modified | `src/Namotion.Interceptor.ConnectorTester/Engine/VerificationEngine.cs` | Calls `SnapshotComparer`. Adds per-cycle JSON failure dump, `LogPropertyDiffsWithTimestamps`, `LogReSyncCheck`. |
| Modified | `docs/connector-tester.md` | Rewrites the verification semantics section to match the new contract. |

No changes to `MutationEngine`, `TestNode`, configuration types, `Program.cs`, or any code outside the `ConnectorTester` project.

### `SnapshotComparer` API

```csharp
public static class SnapshotComparer
{
    public static string Capture(TestNode root);
    public static bool SnapshotsMatch(string snapshotA, string snapshotB);
}
```

Two methods, separately documented and tested. Comparison cannot collapse to plain string equality because the null-timestamp rule needs structural awareness; the `SnapshotsMatch` helper provides that awareness behind a clear contract.

#### `string Capture(TestNode root)`

1. Build a `SubjectUpdate` via `SubjectUpdate.CreateCompleteUpdate(root, [])`. Cycles are safe by construction (flat dictionary keyed by subject ID; references are stored as strings, not nested objects). Verified by `src/Namotion.Interceptor.Connectors.Tests/Updates/SubjectUpdateCycleTests.cs`.
2. Serialize to JSON with `WriteIndented = false`.
3. Parse the JSON to a `JsonNode` tree and apply normalizations in place:
   - Capture the root subject ID from the top-level `"root"` field. Replace it with `"ROOT"`.
   - For each subject in `"subjects"`:
     - For each property:
       - If `kind != "Value"` (Collection, Dictionary, Object), remove the `"timestamp"` field. Structural-property timestamps are local graph-creation moments and are not synced by any protocol; they are guaranteed to differ across participants.
       - If `kind == "Object"` and `"id" == rootId`, replace `"id"` with `"ROOT"`.
       - If `kind in {"Collection", "Dictionary"}` and `"items"` exists: for each item, if `"id" == rootId`, replace `"id"` with `"ROOT"`.
       - If `kind == "Dictionary"`: sort `"items"` by their `"index"` field (the dictionary key, serialized as a string by the SubjectPropertyItemUpdate type) using `StringComparer.Ordinal`.
   - Sort the keys of `"subjects"` using `StringComparer.Ordinal`. Within each subject, sort the property-name keys using `StringComparer.Ordinal`. Subject-ID keys equal to `rootId` are renamed to `"ROOT"` before sorting (so the renamed key lands in the sorted position).
4. Serialize the normalized tree back to a JSON string and return it.

Sorted (unordered model state, deterministic representation needed):

- Dictionary `"items"` (sorted by each item's `"index"` field, which holds the dictionary key as a string)
- The top-level `"subjects"` map (sorted by subject ID)
- Property-name keys within each subject (sorted by name)

**Not sorted** (ordered model state, order is part of equality):

- Collection `"items"`. Collection order is a first-class property of the model; mutations preserve it. Sorting would mask divergence such as a move that places an item at index 2 on one participant and index 5 on another.

The output is deterministic with respect to per-participant differences in subject IDs, structural-property timestamps, and dictionary-item order: equal source state produces byte-identical strings *except* where Value-property timestamps legitimately differ (one null, the other not).

#### `bool SnapshotsMatch(string snapshotA, string snapshotB)`

1. Fast path: if `snapshotA == snapshotB`, return `true`. Captures the common case after normalization.
2. Parse both as `JsonNode` trees.
3. Compare `"subjects"` objects: same set of subject keys; for each subject, same set of property keys.
4. For each property, iterate all field keys (union of both sides) and compare:
   - **`timestamp` field:** matches when (a) both sides are `null`, or (b) both sides are non-null and equal, or (c) exactly one side is `null` (the architectural-contract rule). Mismatches only when both sides are non-null and unequal.
   - **all other fields:** compared by `JsonNode.ToJsonString()` equality. New fields added to `SubjectPropertyUpdate` in the future are picked up automatically without changes here.

### Failure diagnostics

Run only when the convergence loop fails. Never affect the pass path; never replace the failure signal (each is wrapped in a try/catch that logs a warning on its own failure).

#### Per-cycle JSON snapshot dump

For every participant, write `logs/cycle{N:D3}-fail-{participant-name}.json` with formatted (indented) normalized JSON. Makes failures diff-able with any text-diff tool. Path is computed relative to the existing `logs/` directory used by `CycleLoggerProvider`; no new directory creation logic.

#### `LogPropertyDiffsWithTimestamps(snapshots)`

Walks the JSON diff between the reference participant (snapshot[0]) and each other. For every property whose JSON value differs:

- Resolve the subject from the per-participant `ISubjectRegistry` (`IInterceptorSubject.Context.TryGetService<ISubjectRegistry>()`).
- Read each side's `TryGetWriteTimestamp()` for the property.
- Log a single line: `subjectId.PropertyName: server=42 (written 12:34:56.789), client-a=37 (written never)`.

Distinguishes "never written by this participant" (`written never`) from "written but converged to a different value." The former points at message loss or graph-construction divergence; the latter points at conflict-resolution or ordering bugs.

#### `LogReSyncCheck(snapshots)`

For each diverged participant:

1. Build `SubjectUpdate.CreateCompleteUpdate(referenceRoot, [])`.
2. Apply via `divergedRoot.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance)`.
3. Re-snapshot both participants (using `SnapshotComparer.Capture`) and compare via `SnapshotComparer.SnapshotsMatch`.
4. Classify:
   - **Match after re-apply** &rarr; log `transient delivery gap`. The divergence was reconcilable from a complete state. Suspect connector wire (lost messages, missed reconnect catch-up), or snapshot timing across participants.
   - **Still diverged** &rarr; log `still diverged after re-apply`. The reconciliation machinery itself cannot converge the graphs. Suspect `CreateCompleteUpdate`, `ApplySubjectUpdate`, the `TestNode` model, or the snapshot logic in this PR. Exonerates the connector wire.

Diverged participants are mutated. Acceptable because the cycle has already failed and the process is shutting down (`_applicationLifetime.StopApplication()` follows). Documented in the method's comment.

### Tests

`SnapshotComparerTests.cs` builds small `TestNode` graphs in-process. No connector wiring. Runs as part of `dotnet test ... --filter "Category!=Integration"`. Naming convention `When<Condition>_Then<Behavior>`, explicit Arrange/Act/Assert (matching CLAUDE.md project conventions).

| Test | Purpose |
|---|---|
| `WhenSnapshotsAreIdentical_ThenMatch` | Sanity: identical graphs produce equal snapshots. |
| `WhenPropertyOrderInJsonDiffers_ThenMatch` | Property-key sort is stable. |
| `WhenSubjectOrderInJsonDiffers_ThenMatch` | Subject-key sort is stable. |
| `WhenDictionaryItemOrderDiffers_ThenMatch` | Dictionary items normalized by key. |
| `WhenCollectionItemOrderDiffers_ThenDoNotMatch` | Collection items are NOT sorted; order is part of equality. Pins the asymmetry against accidental future change. |
| `WhenRootIdDiffersAcrossParticipants_ThenMatch` | Root-ID normalization to `"ROOT"` works including in nested `id` references inside Collection/Dictionary items. |
| `WhenValueDiffers_ThenDoNotMatch` | Different value for `StringValue`/`IntValue`/`DecimalValue`/`LongValue` fails. |
| `WhenStructuralPropertyTimestampsDiffer_ThenMatch` | Collection/Dictionary/Object timestamps stripped during normalization. |
| `WhenBothValueTimestampsAreNonNullAndEqual_ThenMatch` | Strict positive case. |
| `WhenBothValueTimestampsAreNonNullAndDiffer_ThenDoNotMatch` | Strict negative case. |
| `WhenOneValueTimestampIsNull_ThenMatch` | The architectural-contract rule: null on either side is legitimate. |
| `WhenBothValueTimestampsAreNull_ThenMatch` | Both null is also legitimate. |
| `WhenSubjectKeysDiffer_ThenDoNotMatch` | Different sets of subjects fails. |
| `WhenPropertyKeysDiffer_ThenDoNotMatch` | Different sets of properties on a subject fails. |
| `WhenGraphHasCycle_ThenSnapshotIsStable` | Snapshot produces a deterministic string for cyclic graphs. (Cycle handling lives in `SubjectUpdate.CreateCompleteUpdate`; this test pins the assumption so a regression there surfaces in tester tests too.) |

Tests use direct property writes with explicit `WithChangedTimestamp(null)` and `WithChangedTimestamp(specificTimestamp)` to force the timestamp states being verified.

### Documentation updates

`docs/connector-tester.md`:

- **Mutate/Converge Cycle section** (around line 60-68): rewrite the verification paragraph. Describe normalized snapshots and the comparison rules accurately. Document what normalization removes (per-participant root IDs, structural-property timestamps, item order) and what is compared (Value timestamps with null-rule semantics, all other property fields strictly).
- **Failure output section** (around line 145): mention the new `cycle{N:D3}-fail-{participant}.json` files as the canonical artifact for diff investigation.
- **What the Tester Detects section** (around line 370): add bullets for `LogPropertyDiffsWithTimestamps` and `LogReSyncCheck` describing what each surfaces and how to read the output.

## Coordination with feature branches

After this PR merges:

- **PR #197 (feature/websocket-structural-mutations)** rebases onto master. Local snapshot-comparison code in `VerificationEngine.cs` (`SnapshotsMatch`, `PropertiesMatch`, `JsonValuesEqual`, inline `CreateSnapshot` normalization, the inline failure diagnostics) is removed and replaced by calls to `SnapshotComparer`. The WebSocket-specific `LogSequenceDiagnostics` stays on the PR.
- **`feature/opcua-bidirectional-structural-sync`** rebases. Commit `1e30c44d "fix(tester): use path-based snapshot comparison for structural mutations"` is reverted; the canonical comparator on master replaces it. Test profiles added by that branch (`appsettings.opcua-structural*.json`) are unaffected.

## Risks

- **`LogReSyncCheck` mutates participant state on failure.** Acceptable because it runs only after the cycle has failed and the process is shutting down. Documented in the method.
- **Per-cycle JSON dump assumes `logs/` exists.** True today (`CycleLoggerProvider` creates it). If that ever changes the dump silently fails; the warning catch logs it.
- **The OPC UA branch has to revert one commit on rebase.** Small, mechanical.
- **The null-timestamp rule could mask a real bug if a connector path produces unintended null timestamps.** Mitigation: `LogPropertyDiffsWithTimestamps` already prints `written never` vs `written T`, so an operator investigating a failure sees null-vs-real divergence even when the comparator would not fail on it. If a class of bugs needing strict timestamp checking emerges, the rule can be revisited with concrete evidence.

## Out of scope follow-ups

- Whether to keep `LogReSyncCheck` on PR #197 (it duplicates intent with the WebSocket Welcome resync). PR #197 owners decide separately.
- Path-based view of properties in `LogPropertyDiffsWithTimestamps` log output for human readability (currently uses subject IDs). Polish; deferrable.
