# OPC UA Loader Code Review Findings (PR 357, branch vs master)

High-effort multi-agent review of the four-phase load-plan rework. 14 candidates were found, 13 survived independent verification, 1 was refuted, 10 were reported.

## Correctness

### 1. CONFIRMED: Oversized single-node read aborts the entire initial-state load

`src/Namotion.Interceptor.OpcUa/Client/OpcUaSessionExtensions.cs:461`

`ReadSingleBatchAsync` only catches `BadTooManyOperations`/`BadEncodingLimitsExceeded`/`BadResponseTooLarge` when `count > 1`. Once splitting reduces a batch to one node (or a batch starts at one), the `ServiceResultException` escapes `ReadNodesAsync` into `LoadInitialStateAsync` (`OpcUaSubjectClientSource.cs:230`), which has no try/catch.

**Failure scenario:** One node whose value exceeds the server's response limits (for example a large array returning `BadResponseTooLarge`) cannot be split further, so the exception propagates and fails the initial-state read for every other property in the batch, turning the load into a reconnect failure loop instead of skipping just that node.

### 2. PLAUSIBLE: One bad dynamic-variable read rolls back the whole subject tree

`src/Namotion.Interceptor.OpcUa/Client/OpcUaTypeResolver.cs:98` (same root cause at lines 87 and 99)

`ResolveVariableTypesAsync` passes each node's DataType/ValueRank status to `ThrowIfTransientError`. But `ReadNodesAsync` (`OpcUaSessionExtensions.cs:496`) pads short server responses with `BadUnexpectedError` and its doc says callers classify statuses themselves. The sibling caller `LoadInitialStateAsync` (`OpcUaSubjectClientSource.cs:235`) treats non-good as skippable; here it throws `OpcUaTransientServiceException` instead, which propagates through `LoadChildPropertiesAsync` and `CreatePlanAsync` to the discovery rollback and aborts the entire load. The `session.ReadNodesAsync` call at line 87 is also outside any try/catch, unlike the old per-node catch-and-skip.

**Failure scenario:** A server that persistently returns a short read batch or a transient-classified status (for example `BadOutOfService`) for a single dynamic variable makes every load attempt abort and retry, so the entire subject tree never loads and the client crash-loops. Pre-change behavior returned null for that one node and loaded the remaining properties.

### 3. PLAUSIBLE: Positional collection-child reuse can shift values onto the wrong slot

`src/Namotion.Interceptor.OpcUa/Client/LoadPlan/OpcUaLoadPlanner.cs:679`

Collection child-subject reuse aligns existing children positionally (`existingChildren[i]`) against a child list produced by `DistinctByResolvedNodeId`, which drops references with duplicate resolved NodeIds, missing BrowseName, or an unregistered namespace URI, shifting every subsequent index by one.

**Failure scenario:** If the Nth server element is dropped by the filter, `childNodes[i]` for `i >= N` points to element `i + 1` while `existingChildren[i]` is the previously created element `i`. The reused subject for element `i` is then monitored against element `i + 1`'s node, so array element N onward permanently displays the wrong server values.

### 4. PLAUSIBLE: Collection kind is classified solely from children[0]

`src/Namotion.Interceptor.OpcUa/Client/OpcUaTypeResolver.cs:37`

`ResolveObjectNodeType` classifies a dynamic Object node's collection kind from `children[0].NodeClass` alone, relying on server-defined browse order and on the first hierarchical child being an Object.

**Failure scenario:** A dynamic Object node whose hierarchical children include a leading Variable (or whose element ordering varies across loads) is classified as a single `DynamicSubject` instead of an array/dictionary, so the sibling elements are silently dropped from the model.

### 5. PLAUSIBLE: Browse-continuation cap silently discards references

`src/Namotion.Interceptor.OpcUa/Client/OpcUaSessionExtensions.cs:268`

The old `BrowseNodeAsync` drained continuation points with an unbounded loop. The new `ProcessContinuationPointsAsync` caps pagination at `MaxBrowseContinuations` (default 100) rounds, then releases the still-pending continuation points and discards the untraversed tail. It does log before aborting, so this may be an accepted trade-off of the new configuration maxima.

**Failure scenario:** A node whose child references require more than 100 BrowseNext rounds (a large collection on a server that pages references in small chunks) has its remaining children silently dropped, so those child subjects and monitored items never load.

## Cleanup (all CONFIRMED)

### 6. Fallback-context links leak when Commit throws

`src/Namotion.Interceptor.OpcUa/Client/OpcUaSubjectLoader.cs:38`

Discovery-time staged-subject fallback-context links are only rolled back on a discovery exception (`CreatePlanAsync`'s catch). If the subsequent `plan.Commit()` throws, those links are never undone; `OpcUaLoadPlan.Commit`'s catch only releases ownership and metadata. Arguably a correctness issue rather than cleanup.

**Failure scenario:** During reconnect, if a value-applying `SetValueFromSource` in Commit steps 4-6 throws (for example a validation interceptor or derived-property recompute raises), each staged child subject stays referenced by its parent's `InterceptorSubjectContext._usedByContexts` and is never assigned to root. Repeated commit failures in a reconnect loop accumulate orphaned subject trees and monotonically grow memory.

### 7. Wasted MonitoredItem allocation on losing tie-break paths

`src/Namotion.Interceptor.OpcUa/Client/LoadPlan/OpcUaLoadPlanner.cs:962`

`MonitorValueNode` always allocates a `MonitoredItem` via `MonitoredItemFactory.Create` before calling `plan.AddClaim`, but `AddClaim`'s smaller-NodeId tie-break silently discards it whenever a duplicate claim for the same property already won. Defer MonitoredItem creation until after the tie-break resolves.

### 8. Commit step 1 re-linking is a guaranteed no-op

`src/Namotion.Interceptor.OpcUa/Client/LoadPlan/OpcUaLoadPlan.cs:69`

Commit step 1 re-adds fallback contexts for every staged subject, but `RegisterStagedSubject` already called `AddFallbackContext` during discovery before `AddStagedSubject`, so this loop (and the `_stagedSubjects` list duplicating the planner's `_discoveryLinks`) never does work. `AddFallbackContext` returns false each time.

### 9. Redundant tuple field in committedClaims

`src/Namotion.Interceptor.OpcUa/Client/LoadPlan/OpcUaLoadPlan.cs:64`

`committedClaims` is a `List<(PropertyReference, string Key)>` but `Key` is always the constant local `nodeIdKey` (`source.OpcUaNodeIdKey`). A `List<PropertyReference>` plus reusing the `nodeIdKey` local in the catch carries the same information.

### 10. Stale doc comments reference a renamed method

`src/Namotion.Interceptor.OpcUa.Tests/Client/OpcUaSubjectLoaderTestsBase.cs:150`

Doc comment references a non-existent method `BrowseManyNodesAsync` (renamed to `BrowseNodesAsync`). The same stale name appears in `OpcUaSubjectLoaderBatchingTests.cs` comments at lines 18, 102, and 168.

## Refuted

- `src/Namotion.Interceptor.OpcUa/Client/LoadPlan/OpcUaLoadPlanner.cs:954`: The claim that `MonitorValueNode`'s foreign-ownership pre-check duplicates the check in `OpcUaLoadPlan.Commit` was rejected. The two log sites are mutually exclusive per property (the pre-check early-returns, so no claim enters the plan).

## Review stats

- Level: high (4 finder agents, 13 verifier agents, Opus 4.8)
- Candidates: 14, verified: 14, kept: 13, refuted: 1, reported: 10
