# OPC UA Loader Failure Recovery Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Make OPC UA loader failures non-destructive. Discovery queues all root mutations and source claims; an atomic apply phase commits them on success. Failure path detaches staged subjects from the root context, leaving root untouched, so the base class's retry policy runs on a clean slate.

**Architecture:** `OpcUaLoadContext` becomes transactional: it owns the per-load tracking lists (`PendingClaims`, `PendingRootOps`, `StagedSubjects`) and exposes `QueueClaim`, `QueueRootMutation`, `RegisterStagedSubject`, `Apply`, and `Rollback` methods. The loader calls these instead of mutating directly. The loader's per-method code shrinks; the context absorbs the bookkeeping.

**Tech Stack:** .NET 9, xUnit, OPCFoundation.NetStandard.Opc.Ua 1.5.376.244, Verify + PublicApiGenerator.

**Reference:** Design doc at `docs/design/opcua-loader-failure-recovery.md`. Context inheritance findings (independent) at `docs/design/context-inheritance-findings.md`.

---

## Task 1: Failing test for partial-load bug

**Files:**
- Create: `src/Namotion.Interceptor.OpcUa.Tests/Client/OpcUaSubjectLoaderFailureTests.cs`

**Step 1: Write the failing test**

The test simulates a transient failure during discovery and asserts that root remains at its pre-load state. Use the existing test infrastructure (`OpcUaSubjectLoaderTests.cs` for patterns: fake `ISession`, fake `IOpcUaSubjectClientSource`, build a root subject, invoke the loader).

```csharp
[Fact]
public async Task WhenLoadFailsDuringDiscovery_ThenRootRemainsAtPreLoadState()
{
    // Arrange
    var (loader, rootSubject, session, ownership) = TestFixtures.CreateLoader();
    session.QueueBrowseFault(afterCallCount: 2, StatusCodes.BadServerHalted);

    // Act
    var exception = await Assert.ThrowsAsync<OpcUaTransientServiceException>(
        () => loader.LoadSubjectAsync(rootSubject, TestNodes.Root, session, default));

    // Assert
    Assert.Equal(0, ownership.Properties.Count);
    Assert.Null(rootSubject.GetType().GetProperty("FirstChild")!.GetValue(rootSubject));
    Assert.Null(rootSubject.GetType().GetProperty("SecondChild")!.GetValue(rootSubject));
}
```

If `TestFixtures` and `TestNodes` helpers don't exist yet, extract patterns from `OpcUaSubjectLoaderTests.cs` and `OpcUaSessionExtensionsTests.cs`. The fake session needs a `QueueBrowseFault(afterCallCount, statusCode)` hook to make the N-th browse call throw `OpcUaTransientServiceException`.

**Step 2: Run test to verify it fails**

```bash
dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "FullyQualifiedName~WhenLoadFailsDuringDiscovery_ThenRootRemainsAtPreLoadState"
```

Expected: FAIL. Either an assertion fails (root has child references assigned) or `ownership.Properties.Count > 0`.

**Step 3: Commit the failing test**

```bash
git add src/Namotion.Interceptor.OpcUa.Tests/Client/OpcUaSubjectLoaderFailureTests.cs
git commit -m "Add failing test for partial-load state on transient failure"
```

---

## Task 2: Failing test for registry orphan check

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa.Tests/Client/OpcUaSubjectLoaderFailureTests.cs`

**Step 1: Write the failing test**

```csharp
[Fact]
public async Task WhenLoadFails_ThenRegistryKnownSubjectsContainsNoOrphans()
{
    // Arrange
    var (loader, rootSubject, session, _) = TestFixtures.CreateLoader();
    var registry = rootSubject.Context.GetService<ISubjectRegistry>();
    var preLoadKeys = registry.KnownSubjects.Keys.ToHashSet();
    session.QueueBrowseFault(afterCallCount: 2, StatusCodes.BadServerHalted);

    // Act
    await Assert.ThrowsAsync<OpcUaTransientServiceException>(
        () => loader.LoadSubjectAsync(rootSubject, TestNodes.Root, session, default));

    // Assert
    var postFailureKeys = registry.KnownSubjects.Keys.ToHashSet();
    var orphans = postFailureKeys.Except(preLoadKeys).ToArray();
    Assert.Empty(orphans);
}
```

**Step 2: Run test to verify it fails**

```bash
dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "FullyQualifiedName~WhenLoadFails_ThenRegistryKnownSubjectsContainsNoOrphans"
```

Expected: FAIL. `orphans` will contain the staged subjects that were registered via `AddFallbackContext` triggering `IsContextAttach`.

**Step 3: Commit the failing test**

```bash
git commit -am "Add failing test for registry orphan check on failure"
```

---

## Task 3: Failing test for GC eligibility

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa.Tests/Client/OpcUaSubjectLoaderFailureTests.cs`

**Step 1: Write the failing test**

```csharp
[Fact]
public async Task WhenLoadFails_ThenStagedSubjectsAreGarbageCollectable()
{
    // Arrange
    WeakReference[] CaptureStagedRefs(OpcUaSubjectClientSource source)
    {
        var staged = source.StagedSubjectsForTest;
        return staged.Select(s => new WeakReference(s)).ToArray();
    }

    var (loader, rootSubject, session, _) = TestFixtures.CreateLoader();
    session.QueueBrowseFault(afterCallCount: 2, StatusCodes.BadServerHalted);

    WeakReference[] weakRefs;
    try
    {
        await loader.LoadSubjectAsync(rootSubject, TestNodes.Root, session, default);
        weakRefs = Array.Empty<WeakReference>();
    }
    catch (OpcUaTransientServiceException)
    {
        // Act: drop strong references, force GC.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        weakRefs = []; // populated via hook in next iteration
    }

    // Assert: see implementation note below for the actual mechanism.
    // The fixture exposes a per-load staged-subject hook for test introspection.
}
```

Note: this test needs a small hook on `OpcUaLoadContext` (test-only accessor) to capture weak references to staged subjects before exception propagates. Alternative: use a `ConditionalWeakTable<IInterceptorSubject, object>` populated via a debug hook. Document the chosen mechanism inline.

**Step 2: Run test to verify it fails**

```bash
dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "FullyQualifiedName~WhenLoadFails_ThenStagedSubjectsAreGarbageCollectable"
```

Expected: FAIL. Without cleanup, staged subjects are kept alive via `_usedByContexts` on the root context.

**Step 3: Commit the failing test**

```bash
git commit -am "Add failing test for GC cleanup of staged subjects on failure"
```

---

## Task 4: Failing test for retry from clean slate

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa.Tests/Client/OpcUaSubjectLoaderFailureTests.cs`

**Step 1: Write the failing test**

```csharp
[Fact]
public async Task WhenLoadFailsAndRetries_ThenSecondAttemptSucceedsCleanly()
{
    // Arrange
    var (loader, rootSubject, session, ownership) = TestFixtures.CreateLoader();
    session.QueueBrowseFault(afterCallCount: 2, StatusCodes.BadServerHalted);

    // Act
    await Assert.ThrowsAsync<OpcUaTransientServiceException>(
        () => loader.LoadSubjectAsync(rootSubject, TestNodes.Root, session, default));

    var monitoredItems = await loader.LoadSubjectAsync(rootSubject, TestNodes.Root, session, default);

    // Assert
    Assert.NotEmpty(monitoredItems);
    Assert.NotNull(rootSubject.GetType().GetProperty("FirstChild")!.GetValue(rootSubject));
    Assert.True(ownership.Properties.Count > 0);
}
```

**Step 2: Run test to verify it fails**

```bash
dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "FullyQualifiedName~WhenLoadFailsAndRetries_ThenSecondAttemptSucceedsCleanly"
```

Expected: FAIL. Second attempt hits "property already owned" errors from first attempt's leaked claims.

**Step 3: Commit the failing test**

```bash
git commit -am "Add failing test for clean retry after transient failure"
```

---

## Task 5: Make OpcUaLoadContext transactional

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa/Client/OpcUaLoadContext.cs`

**Step 1: Add tracking state and transactional methods**

Add three lists, a `_committed` flag, and the transactional API. The context becomes `IDisposable`. The `Dispose` method runs rollback if `Apply` was not called.

```csharp
internal sealed class OpcUaLoadContext : IDisposable
{
    // ... existing fields ...
    private readonly IInterceptorSubject _rootSubject;
    private readonly SourceOwnershipManager _ownership;
    private readonly OpcUaSubjectClientSource _source;
    private readonly List<(PropertyReference Property, NodeId NodeId)> _pendingClaims = new();
    private readonly List<Action> _pendingRootOps = new();
    private readonly List<IInterceptorSubject> _stagedSubjects = new();
    private bool _committed;

    public OpcUaLoadContext(
        ISession session,
        IInterceptorSubject rootSubject,
        SourceOwnershipManager ownership,
        OpcUaSubjectClientSource source,
        uint maxReferencesPerNode,
        int maxBrowseContinuations,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        Session = session;
        _rootSubject = rootSubject;
        _ownership = ownership;
        _source = source;
        // ... existing init ...
    }

    public IInterceptorSubject RootSubject => _rootSubject;

    public void QueueClaim(PropertyReference property, NodeId nodeId)
        => _pendingClaims.Add((property, nodeId));

    public void QueueRootMutation(Action mutation)
        => _pendingRootOps.Add(mutation);

    public void RegisterStagedSubject(IInterceptorSubject subject)
    {
        subject.Context.AddFallbackContext(_rootSubject.Context);
        _stagedSubjects.Add(subject);
    }

    public void Apply()
    {
        foreach (var (property, nodeId) in _pendingClaims)
        {
            if (!_ownership.ClaimSource(property))
            {
                logger.LogError(
                    "Property {Subject}.{Property} already owned by another source. Skipping OPC UA claim.",
                    property.Subject.GetType().Name, property.Name);
                continue;
            }
            property.SetPropertyData(_source.OpcUaNodeIdKey, nodeId);
        }

        foreach (var op in _pendingRootOps)
        {
            op();
        }

        _committed = true;
    }

    public void Dispose()
    {
        if (_committed) return;
        foreach (var staged in _stagedSubjects)
        {
            staged.Context.RemoveFallbackContext(_rootSubject.Context);
        }
        _stagedSubjects.Clear();
        _pendingClaims.Clear();
        _pendingRootOps.Clear();
    }
}
```

Note: capture `logger` in a field (it is currently passed via primary constructor; promote to field if not already).

**Step 2: Verify the project compiles**

```bash
dotnet build src/Namotion.Interceptor.OpcUa.slnx
```

Expected: BUILD SUCCESS. No behavior change yet (the new APIs are unused).

**Step 3: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa/Client/OpcUaLoadContext.cs
git commit -m "Make OpcUaLoadContext transactional: QueueClaim, QueueRootMutation, Apply, Dispose"
```

---

## Task 6: Route OpcUaSubjectLoader through transactional context

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa/Client/OpcUaSubjectLoader.cs`

This is the largest change. Sub-steps work through the loader from top to bottom.

**Step 1: Update LoadSubjectAsync to use transactional pattern**

```csharp
public async Task<IReadOnlyList<MonitoredItem>> LoadSubjectAsync(
    IInterceptorSubject subject,
    ReferenceDescription node,
    ISession session,
    CancellationToken cancellationToken)
{
    using var context = new OpcUaLoadContext(
        session,
        subject,
        _ownership,
        _source,
        _configuration.MaxReferencesPerNode,
        _configuration.MaxBrowseContinuations,
        _logger,
        cancellationToken);

    await LoadSubjectsAsync([(node, subject)], context).ConfigureAwait(false);
    context.Apply();
    return context.MonitoredItems;
}
```

Behavior: if `LoadSubjectsAsync` throws, `Dispose` runs rollback. If it succeeds, `Apply` commits, then `Dispose` is a no-op.

**Step 2: Replace MonitorValueNode body with queue calls**

Before:
```csharp
private void MonitorValueNode(NodeId nodeId, RegisteredSubjectProperty property, List<MonitoredItem> monitoredItems)
{
    if (!_ownership.ClaimSource(property.Reference)) { _logger.LogError(...); return; }
    property.Reference.SetPropertyData(_source.OpcUaNodeIdKey, nodeId);
    var monitoredItem = MonitoredItemFactory.Create(_configuration, nodeId, property);
    monitoredItems.Add(monitoredItem);
}
```

After (takes context instead of monitoredItems list):
```csharp
private void MonitorValueNode(NodeId nodeId, RegisteredSubjectProperty property, OpcUaLoadContext context)
{
    context.QueueClaim(property.Reference, nodeId);
    var monitoredItem = MonitoredItemFactory.Create(_configuration, nodeId, property);
    context.MonitoredItems.Add(monitoredItem);
}
```

Update all four call sites: lines 294, 405, 439, 682, 764. Pass `context` instead of `context.MonitoredItems`.

**Step 3: Defer root SetValueFromSource calls**

Identify mutations whose target subject is `context.RootSubject`. Lines to inspect: 350, 472, 490, 575. At each site:

Before:
```csharp
property.SetValueFromSource(_source, null, null, reusedSubject);
```

After:
```csharp
if (property.Subject == context.RootSubject)
{
    context.QueueRootMutation(() => property.SetValueFromSource(_source, null, null, reusedSubject));
}
else
{
    property.SetValueFromSource(_source, null, null, reusedSubject);
}
```

Same pattern for collection/dictionary assignments at lines 472, 490.

**Step 4: Defer root AddProperty calls**

In `TryCreateDynamicProperty` at line 333: `AddProperty` may run on root or a staged child. If `registeredSubject.Subject == context.RootSubject`, queue the call. The method needs access to context; thread `OpcUaLoadContext` through the call chain (already passed implicitly via cascade of methods that take context).

Refactor `TryCreateDynamicProperty` to take an additional `OpcUaLoadContext` parameter and route the `AddProperty` call appropriately. Note: `AddProperty` returns the new `RegisteredSubjectProperty` which subsequent code uses. For root deferral, the return path needs adjustment: synthesize a deferred property reference or move the entire "add and use new property" block into the queued mutation.

Cleanest approach: only defer the structural AddProperty for root. The subsequent uses of the returned property (resolving name, classifying type) can still happen during discovery against the still-detached property object, with the actual `RegisteredSubject.AddProperty` registration deferred. If the registry's `AddProperty` is atomic (creates the property and immediately makes it visible), this requires more care.

If atomicity is required: queue the AddProperty as a no-arg action that runs in apply phase, but populate the in-memory `RegisteredSubjectProperty` reference object during discovery. Verify with a quick `grep` whether `RegisteredSubject.AddProperty` returns an object that exists independently of registration. If not, defer the entire downstream classification too, keyed by browse name.

Mark this sub-step as the primary integration risk. If complications arise, the simplest fallback is: only root-level child subject references (`SetValueFromSource(stagedChild)`) are deferred. Dynamic properties on root continue to be added live during discovery. This still solves the customer-visible bug (root child slots never get half-loaded children) while leaving root with extra dynamic properties on failure (a smaller, recoverable leak: next retry attempts the same `AddProperty` and either succeeds via existing or no-ops). Document the choice in the design doc.

**Step 5: Register all staged subjects**

Locate every site where the loader creates a new subject:
- Line 357: `_configuration.SubjectFactory.CreateSubjectAsync(...)`
- Line 545: `_configuration.SubjectFactory.CreateCollectionSubjectAsync(...)`

After each creation:
```csharp
context.RegisterStagedSubject(subjectToLoad);
```

This call also runs `subject.Context.AddFallbackContext(rootContext)` (the explicit symmetric add per design). The previous line-361 `AddFallbackContext(parentSubject.Context)` becomes redundant for staged subjects (they reach root via the root fallback). Remove it for new subjects; keep it only for `existingSubject is null` paths if any external caller relies on parent-context fallback.

Verify that the source generator's auto-add behavior (constructor-time `AddFallbackContext`) is not duplicated. If the subject factory passes a context to the constructor (it likely does), our explicit `AddFallbackContext(rootContext)` may be a deduped no-op. This is acceptable: cleanup via `RemoveFallbackContext` is idempotent. Document expected behavior in the design doc.

**Step 6: Compile and run unit tests**

```bash
dotnet build src/Namotion.Interceptor.OpcUa.slnx
dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "Category!=Integration"
```

Expected: BUILD SUCCESS. The four failing tests from Tasks 1-4 should now pass. Pre-existing loader tests must still pass.

**Step 7: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa/Client/OpcUaSubjectLoader.cs
git commit -m "Route loader through transactional context for atomic apply on success and rollback on failure"
```

---

## Task 7: Verify all failure tests pass

**Step 1: Run the failure test suite**

```bash
dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "FullyQualifiedName~OpcUaSubjectLoaderFailureTests"
```

Expected: all four tests PASS.

**Step 2: Run the full unit test suite to catch regressions**

```bash
dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"
```

Expected: PASS. No regressions in tracking, registry, or other OPC UA tests.

If a test regresses, debug before proceeding. Common cause: a subject that was previously claimed during discovery now expects a claim that has been deferred to apply. Adjust the test expectation only if the new behavior is semantically correct.

---

## Task 8: Add atomicity test (apply phase is observably atomic)

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa.Tests/Client/OpcUaSubjectLoaderFailureTests.cs` (or split into a new file `OpcUaSubjectLoaderAtomicityTests.cs`)

**Step 1: Write the test**

```csharp
[Fact]
public async Task WhenLoadSucceeds_ThenRootChildAssignmentsOccurAfterDiscoveryCompletes()
{
    // Arrange
    var (loader, rootSubject, session, _) = TestFixtures.CreateLoader();
    var assignmentCountDuringDiscovery = 0;
    var browseCountAtLastAssignment = 0;

    rootSubject.Context.GetPropertyChangeObservable(ImmediateScheduler.Instance)
        .Where(change => ReferenceEquals(change.Property.Subject, rootSubject))
        .Subscribe(_ =>
        {
            assignmentCountDuringDiscovery++;
            browseCountAtLastAssignment = session.BrowseCallCount;
        });

    // Act
    await loader.LoadSubjectAsync(rootSubject, TestNodes.Root, session, default);

    // Assert
    // All root assignments fired AFTER all browse calls (apply phase runs at the end)
    Assert.Equal(session.BrowseCallCount, browseCountAtLastAssignment);
    Assert.True(assignmentCountDuringDiscovery > 0);
}
```

**Step 2: Run and verify it passes**

```bash
dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "FullyQualifiedName~WhenLoadSucceeds_ThenRootChildAssignmentsOccurAfterDiscoveryCompletes"
```

Expected: PASS (apply phase runs after discovery completes; all browse calls happen first).

**Step 3: Commit**

```bash
git commit -am "Add test verifying root assignments occur after discovery completes"
```

---

## Task 9: Add claim-before-attach test

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa.Tests/Client/OpcUaSubjectLoaderFailureTests.cs`

**Step 1: Write the test**

```csharp
[Fact]
public async Task WhenApplyExecutes_ThenClaimsAreInPlaceBeforeRootAttachmentsAreObserved()
{
    // Arrange
    var (loader, rootSubject, session, ownership) = TestFixtures.CreateLoader();
    var ownedPropertyCountAtAttachment = 0;

    rootSubject.Context.GetPropertyChangeObservable(ImmediateScheduler.Instance)
        .Where(change => ReferenceEquals(change.Property.Subject, rootSubject)
                         && change.NewValue is IInterceptorSubject)
        .Subscribe(change =>
        {
            // Observer fires synchronously inside the apply burst.
            // Count properties owned by the source at this moment.
            ownedPropertyCountAtAttachment = ownership.Properties.Count;
        });

    // Act
    var monitoredItems = await loader.LoadSubjectAsync(rootSubject, TestNodes.Root, session, default);

    // Assert
    // Claims happen before root mutations in the apply phase, so at the moment
    // an observer sees root.Child appear, the child's leaf claims are already in place.
    Assert.True(ownedPropertyCountAtAttachment > 0);
    Assert.Equal(ownedPropertyCountAtAttachment, ownership.Properties.Count);
}
```

**Step 2: Run and verify it passes**

```bash
dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "FullyQualifiedName~WhenApplyExecutes_ThenClaimsAreInPlaceBeforeRootAttachmentsAreObserved"
```

Expected: PASS.

**Step 3: Commit**

```bash
git commit -am "Add test verifying claims complete before root attachments fire observers"
```

---

## Task 10: Run integration test suite

**Step 1: Run OPC UA integration tests**

```bash
dotnet test src/Namotion.Interceptor.OpcUa.Tests
```

Expected: PASS. Includes server-side integration tests that exercise the loader against a real `OpcUaServer`.

**Step 2: If any test fails, investigate before continuing**

Look at the failure stack trace. The most likely categories:
- A subject creation pattern that doesn't run through `context.RegisterStagedSubject` (find and add the call)
- A root-target mutation that wasn't deferred (audit `SetValueFromSource` and `AddProperty` sites again)
- Mutation against a subject that became "root-relative" through nested loading (the root-check needs to traverse via parent chain, not just identity)

---

## Task 11: Run chaos connector tester

**Step 1: Run the chaos test for the OPC UA connector**

```bash
pwsh -Command "cd src/Namotion.Interceptor.OpcUa.ChaosTests && dotnet test"
```

Or use the project's existing chaos invocation. The goal: validate behavior over 200+ chaos cycles with rotating fault profiles (no-chaos, server-only faults, client-only faults, all-clients faults, full chaos).

Expected: PASS, with no zombie subjects, no permanent failure loops, and memory plateauing rather than climbing.

**Step 2: Inspect logs for failure-recovery behavior**

```bash
grep -E "OpcUaTransientServiceException|Property .* already owned|Reconnection failed" logs/*.log | head -50
```

Expected: transient exceptions appear, followed by retry attempts that succeed within a few cycles. No "already owned" log spam (would indicate cleanup is incomplete).

---

## Task 12: Public API verification

**Step 1: Run the public API snapshot test**

```bash
dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "FullyQualifiedName~VerifyChecksTests.PublicApi"
```

If the test fails because of a snapshot mismatch:

```bash
mv src/Namotion.Interceptor.OpcUa.Tests/VerifyChecksTests.PublicApi.received.txt \
   src/Namotion.Interceptor.OpcUa.Tests/VerifyChecksTests.PublicApi.verified.txt
```

Diff the change. The public surface should be unchanged in this PR (transactional context is internal, loader changes are internal). If anything public was added, audit whether it should be public; revert to internal if not deliberate.

**Step 2: Run final full test suite**

```bash
dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"
dotnet test src/Namotion.Interceptor.OpcUa.Tests
```

Expected: PASS.

**Step 3: Commit any snapshot updates**

```bash
git add src/Namotion.Interceptor.OpcUa.Tests/VerifyChecksTests.PublicApi.verified.txt
git commit -m "Update public API snapshot" # only if there are intentional public changes
```

---

## Task 13: Update design doc with implementation notes

**Files:**
- Modify: `docs/design/opcua-loader-failure-recovery.md`

**Step 1: Add a "Final implementation notes" section** documenting:

- The deferral scope for root `AddProperty` (full deferral or simplified to "structural only" if Task 6 Step 4 needed the fallback)
- The interaction with the source generator's auto-add of constructor context (deduped no-op, harmless)
- Anything else surprising discovered during implementation

**Step 2: Commit**

```bash
git add docs/design/opcua-loader-failure-recovery.md
git commit -m "Document implementation notes on failure-recovery design"
```

---

## Out of scope (file as follow-up issues if not already)

- Atomic discovery-phase observability (Issue #320 full ambition).
- Loader idempotency (Issue #3); now unnecessary because retries are clean-slate.
- Root not in `SubjectsByNodeId` (Issue #2).
- Config validation gaps (Issue #4).
- Generator/context inheritance redesign (separate findings doc at `docs/design/context-inheritance-findings.md`).

## Remember

- **TDD discipline**: tests first, watch them fail, implement, watch them pass. The four failing tests in Tasks 1-4 are the contract; the refactor in Tasks 5-6 satisfies them.
- **Bite-sized commits**: at least one per task. If a sub-step takes more than ~10 minutes, commit at the sub-step boundary too.
- **No `Task.Delay`**: use `ManualResetEventSlim` or `AsyncTestHelpers.WaitUntilAsync` for any async synchronization.
- **No em dashes** in docs or commits.
- **No AI attribution** in commits, PRs, or comments.
