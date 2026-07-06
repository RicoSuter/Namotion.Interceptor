# OPC UA Master-Based Comparison Loader Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rewrite the OPC UA client subject loader on master as an explicit four-phase model (discover, validate, commit, subscribe) that passes PR 313's frozen acceptance suite with fewer internal lifecycle states, and adds two new behaviors: subscription callback gating and sweep-before-read-after-write ordering.

**Architecture:** Freeze PR 313's targeted test suite first. Reuse PR 313's stable protocol and classification layer verbatim (batched browse/read, status classifier, type resolver, health monitor, write classification) so the loader architecture is the only variable under comparison. Replace PR 313's stateful load context with a pure discovery planner that produces an in-memory `OpcUaLoadPlan` (no durable side effects), a validator that deduplicates and resolves deterministic conflicts, and a committer that applies the plan in one bounded, ordered phase (claim ownership before assigning subjects onto root; release only claims committed during that phase on failure; do not restore root values). Add gated subscription setup that sweeps detached subjects before registering read-after-write and enables data-change callbacks only as the final step.

**Tech Stack:** C# 13 (preview partial properties), .NET 9.0, OPCFoundation.NetStandard.Opc.Ua.* 1.5.376.244, xUnit 2.9.3, Moq 4.20.72, System.Reactive 6.0.1, PublicApiGenerator + Verify for API snapshots.

## Global Constraints

- The frozen acceptance suite is the contract. Copy PR 313's targeted tests first and make them pass without weakening any assertion. A deliberate behavior change requires editing the corresponding test on purpose as a reviewed act.
- PR 313 lives on branch `feature/improve-opc-ua-loader-browse-performance`. Copy files from it with `git checkout feature/improve-opc-ua-loader-browse-performance -- <path>`. Do not use `git show "<branch>:<path>"` in zsh: the `:s` parameter modifier mangles it. Quote the full ref literally if you must read (not copy) a file.
- Preserve the loader public contract exactly. It already matches on master: ctor `OpcUaSubjectLoader(IInterceptorSubject, OpcUaClientConfiguration, SourceOwnershipManager, OpcUaSubjectClientSource, ILogger)`, entry `Task<IReadOnlyList<MonitoredItem>> LoadSubjectAsync(IInterceptorSubject, ReferenceDescription, ISession, CancellationToken)`, and `internal SourceOwnershipManager OpcUaSubjectClientSource.Ownership`, `internal string OpcUaSubjectClientSource.OpcUaNodeIdKey`.
- Discovery and validation must be fully retry-clean: no durable graph, ownership, subscription, polling, or read-after-write state may remain after a discovery or validation failure.
- Commit boundary matches PR 313: on commit failure release newly claimed ownership and the node-id metadata committed this phase and discard the monitored-item list, but do not restore root property values assigned during commit.
- Keep `InternalsVisibleTo("Namotion.Interceptor.OpcUa.Tests")` in `src/Namotion.Interceptor.OpcUa/Namotion.Interceptor.OpcUa.csproj`. Without it the loader, plan, classifier, and health-monitor tests do not compile.
- Breaking public API changes are acceptable; do not add compatibility aliases for renamed configuration properties. PR 313 renames exactly three config properties (see Task 2); the copied public API snapshot depends on these renames.
- Build with `dotnet build src/Namotion.Interceptor.slnx`. Run unit tests with `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "Category!=Integration"`. Run a single group with `--filter "FullyQualifiedName~<Name>"`.
- Style: nullable enabled, warnings as errors, descriptive names (no abbreviations), minimal comments (explain only the non-obvious), no em dashes in docs or comments. Never include AI attribution in commit messages.
- Do not commit inside subagents. Commit at task boundaries as the plan directs.

## Verified reference facts

- `MonitoredItemFactory.Create(OpcUaClientConfiguration configuration, NodeId nodeId, RegisteredSubjectProperty property, IInterceptorSubject rootSubject)` returns `MonitoredItem`.
- `OpcUaSubjectFactory.CreateSubjectAsync(RegisteredSubjectProperty property, ReferenceDescription node, ISession session, CancellationToken)` returns `Task<IInterceptorSubject>`; `CreateCollectionSubjectAsync(RegisteredSubjectProperty collectionProperty, ReferenceDescription node, object? index, ISession session, CancellationToken)` returns `Task<IInterceptorSubject>`.
- `SourceOwnershipManager.ClaimSource(PropertyReference)` returns false when owned by a different source and is idempotent for the same source; `ReleaseSource(PropertyReference)`; `Properties` is a snapshot.
- `PropertyReference` has `SetPropertyData(string, object?)`, `TryGetPropertyData(string, out object?)`, `RemovePropertyData(string)`, `TryGetSource(out ISubjectSource?)`, and `public static readonly PropertyReferenceComparer Comparer`.
- `RegisteredSubjectPropertyExtensions.SetValueFromSource(this RegisteredSubjectProperty, object source, DateTimeOffset? changed, DateTimeOffset? received, object? value)`.
- `IInterceptorSubjectContext.AddFallbackContext(IInterceptorSubjectContext)` links a staged subject to the parent context.
- `subject.TryGetRegisteredSubject()` returning null means the subject is not attached to the registry (the detach test).
- PR 313 renames config properties `MaximumItemsPerSubscription -> MaxItemsPerSubscription`, `MaximumReferencesPerNode -> MaxReferencesPerNode`, `SubscriptionMaximumNotificationsPerPublish -> SubscriptionMaxNotificationsPerPublish`, and adds `MaxBrowseContinuations` (int, 100) and `MaxAttributeTraversals` (int, 100).
- `OpcUaBrowseName` is `internal static class` with `bool TryGetBracketContent(string, out ReadOnlySpan<char>)`.
- Only `OpcUaSubjectLoaderFailureTests.cs` couples to the internal commit type (one method, `WhenApplyFailsMidway_ThenOwnershipFromPreviousLoadIsRetained`). Every other test uses the public loader surface.

## File Map

Production files under `src/Namotion.Interceptor.OpcUa/Client/` unless noted.

- Copy verbatim from PR 313 (stable layer, not the design being compared): `OpcUaStatusCodeClassifier.cs`, `OpcUaTransientServiceException.cs`, `OpcUaBrowseName.cs`, `OpcUaSessionExtensions.cs`, `OpcUaTypeResolver.cs`, `OutboundWriter.cs`, `Resilience/SubscriptionHealthMonitor.cs`, `Connection/SubscriptionManager.cs` (later edited in Phase C for the two new behaviors).
- Edit: `OpcUaClientConfiguration.cs` (rename three properties, add two bounds and validations).
- Edit: `OpcUaSubjectClientSource.cs` (expose root-path helpers as internal seams; keep loader wiring and node-id/ownership members that already exist; update any renamed config references).
- Create (the redesign core): `LoadPlan/OpcUaLoadPlan.cs`, `LoadPlan/OpcUaLoadPlanner.cs`, `LoadPlan/OpcUaLoadCommitter.cs`.
- Rewrite: `OpcUaSubjectLoader.cs` (orchestrate planner then committer; delete the recursive side-effecting internals).

Test files under `src/Namotion.Interceptor.OpcUa.Tests/Client/`.

- Copy verbatim from PR 313: all thirteen PR 313 test files (see Task 1).
- Delete on port: `AutoHealingTests.cs`, `WriteErrorClassificationTests.cs`.
- Re-point one method: `WhenApplyFailsMidway_ThenOwnershipFromPreviousLoadIsRetained` in `OpcUaSubjectLoaderFailureTests.cs`.
- New (Phase C): `OpcUaSubscriptionCallbackGatingTests.cs`, `OpcUaSubscriptionSweepOrderingTests.cs`.
- Update: `VerifyChecksTests.PublicApi.verified.txt`.

---

### Task 1: Freeze the acceptance suite from PR 313

**Files:**
- Copy: the thirteen PR 313 test files listed below into `src/Namotion.Interceptor.OpcUa.Tests/Client/`.
- Copy: `src/Namotion.Interceptor.OpcUa.Tests/VerifyChecksTests.PublicApi.verified.txt`.
- Delete: `src/Namotion.Interceptor.OpcUa.Tests/Client/AutoHealingTests.cs`, `src/Namotion.Interceptor.OpcUa.Tests/Client/WriteErrorClassificationTests.cs`.

**Interfaces:**
- Produces: the frozen contract. After this task the test project references production types that do not exist yet (`OpcUaSessionExtensions`, `OpcUaStatusCodeClassifier`, the renamed config properties, `OpcUaLoadPlan`), so the test assembly does not build. That is the expected red baseline.

- [ ] **Step 1: Port the thirteen PR 313 test files verbatim**

```bash
git checkout feature/improve-opc-ua-loader-browse-performance -- \
  src/Namotion.Interceptor.OpcUa.Tests/Client/OpcUaClientConfigurationTests.cs \
  src/Namotion.Interceptor.OpcUa.Tests/Client/OpcUaRootPathResolutionTests.cs \
  src/Namotion.Interceptor.OpcUa.Tests/Client/OpcUaSessionExtensionsTests.cs \
  src/Namotion.Interceptor.OpcUa.Tests/Client/OpcUaStatusCodeClassifierTests.cs \
  src/Namotion.Interceptor.OpcUa.Tests/Client/OpcUaSubjectLoaderAttributeTests.cs \
  src/Namotion.Interceptor.OpcUa.Tests/Client/OpcUaSubjectLoaderBatchingTests.cs \
  src/Namotion.Interceptor.OpcUa.Tests/Client/OpcUaSubjectLoaderDedupTests.cs \
  src/Namotion.Interceptor.OpcUa.Tests/Client/OpcUaSubjectLoaderDictionaryReuseTests.cs \
  src/Namotion.Interceptor.OpcUa.Tests/Client/OpcUaSubjectLoaderFailureTests.cs \
  src/Namotion.Interceptor.OpcUa.Tests/Client/OpcUaSubjectLoaderTests.cs \
  src/Namotion.Interceptor.OpcUa.Tests/Client/OpcUaSubjectLoaderTestsBase.cs \
  src/Namotion.Interceptor.OpcUa.Tests/Client/OpcUaTypeResolverTests.cs \
  src/Namotion.Interceptor.OpcUa.Tests/Client/SubscriptionHealthMonitorTests.cs \
  src/Namotion.Interceptor.OpcUa.Tests/VerifyChecksTests.PublicApi.verified.txt
```

Expected: `git status --short` shows these files added or modified.

- [ ] **Step 2: Delete the two superseded test files**

```bash
git rm src/Namotion.Interceptor.OpcUa.Tests/Client/AutoHealingTests.cs \
       src/Namotion.Interceptor.OpcUa.Tests/Client/WriteErrorClassificationTests.cs
```

- [ ] **Step 3: Confirm the frozen suite does not build yet**

Run: `dotnet build src/Namotion.Interceptor.OpcUa.Tests/Namotion.Interceptor.OpcUa.Tests.csproj`
Expected: build errors for missing production types and renamed config properties. This is the red baseline; do not fix by weakening tests.

- [ ] **Step 4: Commit the freeze checkpoint**

```bash
git add src/Namotion.Interceptor.OpcUa.Tests
git commit -m "test(opcua): freeze the loader comparison acceptance suite"
```

---

### Task 2: Rename configuration properties and add the two new bounds

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa/Client/OpcUaClientConfiguration.cs`
- Test: `src/Namotion.Interceptor.OpcUa.Tests/Client/OpcUaClientConfigurationTests.cs` (already copied)

**Interfaces:**
- Produces: `MaxItemsPerSubscription` (int, 1000), `MaxReferencesPerNode` (uint, 0), `SubscriptionMaxNotificationsPerPublish` (uint, 0), `MaxBrowseContinuations` (int, 100), `MaxAttributeTraversals` (int, 100); `Validate()` throws `ArgumentException` when `MaxItemsPerSubscription`, `MaxBrowseContinuations`, or `MaxAttributeTraversals` is not positive.

- [ ] **Step 1: Run the config tests to see the failures**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "FullyQualifiedName~OpcUaClientConfigurationTests"`
Expected: build error for `MaxBrowseContinuations` / `MaxAttributeTraversals` (and any old-name usage).

- [ ] **Step 2: Rename the three properties**

Rename in `OpcUaClientConfiguration.cs`: `MaximumItemsPerSubscription` to `MaxItemsPerSubscription`, `MaximumReferencesPerNode` to `MaxReferencesPerNode`, `SubscriptionMaximumNotificationsPerPublish` to `SubscriptionMaxNotificationsPerPublish`. Keep types and defaults unchanged.

- [ ] **Step 3: Add the two new properties**

Insert after the renamed `MaxReferencesPerNode` property:

```csharp
    /// <summary>
    /// Gets or sets the maximum number of BrowseNext continuation rounds per browse. Bounds pagination depth
    /// so a server that returns a fresh continuation point forever cannot loop the loader. Default is 100.
    /// </summary>
    public int MaxBrowseContinuations { get; set; } = 100;

    /// <summary>
    /// Gets or sets the maximum attribute-traversal depth (attributes of attributes) during loading. Default is 100.
    /// </summary>
    public int MaxAttributeTraversals { get; set; } = 100;
```

- [ ] **Step 4: Update the `MaximumItemsPerSubscription` validation and add two more**

Change the existing `MaximumItemsPerSubscription` validation to use the new name, then add the two new bounds after it:

```csharp
        if (MaxItemsPerSubscription <= 0)
        {
            throw new ArgumentException(
                $"MaxItemsPerSubscription must be positive, got: {MaxItemsPerSubscription}",
                nameof(MaxItemsPerSubscription));
        }

        if (MaxBrowseContinuations <= 0)
        {
            throw new ArgumentException(
                $"MaxBrowseContinuations must be positive, got: {MaxBrowseContinuations}",
                nameof(MaxBrowseContinuations));
        }

        if (MaxAttributeTraversals <= 0)
        {
            throw new ArgumentException(
                $"MaxAttributeTraversals must be positive, got: {MaxAttributeTraversals}",
                nameof(MaxAttributeTraversals));
        }
```

- [ ] **Step 5: Update production references to the renamed properties**

```bash
grep -rn "MaximumItemsPerSubscription\|MaximumReferencesPerNode\|SubscriptionMaximumNotificationsPerPublish" src/Namotion.Interceptor.OpcUa/ | grep -v /obj/
```

Replace each hit with the new name. Leave the loader alone (it is rewritten later). Do not touch generated files under `obj/`.

- [ ] **Step 6: Run the config tests**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "FullyQualifiedName~OpcUaClientConfigurationTests"`
Expected: build may still fail elsewhere (missing classifier and plan types), but the config test compiles against the new properties. If the whole test assembly cannot build yet, defer running until Task 6 and instead build only the production project here: `dotnet build src/Namotion.Interceptor.OpcUa/Namotion.Interceptor.OpcUa.csproj` and expect it to succeed once renames are applied.

- [ ] **Step 7: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa/Client/OpcUaClientConfiguration.cs
git commit -m "feat(opcua): rename config maxima and add browse/attribute bounds"
```

---

### Task 3: Copy the shared classification and protocol layer verbatim

**Files:**
- Create: `src/Namotion.Interceptor.OpcUa/Client/OpcUaStatusCodeClassifier.cs`
- Create: `src/Namotion.Interceptor.OpcUa/Client/OpcUaTransientServiceException.cs`
- Create: `src/Namotion.Interceptor.OpcUa/Client/OpcUaBrowseName.cs`
- Create: `src/Namotion.Interceptor.OpcUa/Client/OpcUaSessionExtensions.cs`
- Modify: `src/Namotion.Interceptor.OpcUa/Client/OpcUaTypeResolver.cs` (replace)
- Modify: `src/Namotion.Interceptor.OpcUa/Client/OutboundWriter.cs` (replace)
- Modify: `src/Namotion.Interceptor.OpcUa/Client/Resilience/SubscriptionHealthMonitor.cs` (replace)

**Interfaces:**
- Produces: `OpcUaStatusCodeClassifier` (permanent whitelist of 11 codes plus transient complement; `IsTransientError`, `IsPermanentError`, `ThrowIfTransientError`, `IsBatchTooLarge`), `OpcUaTransientServiceException`, `OpcUaBrowseName.TryGetBracketContent`, `OpcUaSessionExtensions.BrowseNodesAsync`/`ReadNodesAsync`/`DistinctByResolvedNodeId`, `OpcUaTypeResolver.ResolveObjectNodeType`/`ResolveVariableTypesAsync`, and write/health classification routed through the shared classifier.

- [ ] **Step 1: Copy the seven production files verbatim from PR 313**

```bash
git checkout feature/improve-opc-ua-loader-browse-performance -- \
  src/Namotion.Interceptor.OpcUa/Client/OpcUaStatusCodeClassifier.cs \
  src/Namotion.Interceptor.OpcUa/Client/OpcUaTransientServiceException.cs \
  src/Namotion.Interceptor.OpcUa/Client/OpcUaBrowseName.cs \
  src/Namotion.Interceptor.OpcUa/Client/OpcUaSessionExtensions.cs \
  src/Namotion.Interceptor.OpcUa/Client/OpcUaTypeResolver.cs \
  src/Namotion.Interceptor.OpcUa/Client/OutboundWriter.cs \
  src/Namotion.Interceptor.OpcUa/Client/Resilience/SubscriptionHealthMonitor.cs
```

- [ ] **Step 2: Confirm no copied file references PR 313 loader internals**

```bash
grep -n "OpcUaLoadContext\|OpcUaSubjectLoader\|OpcUaAttributeLoader" \
  src/Namotion.Interceptor.OpcUa/Client/OpcUaSessionExtensions.cs \
  src/Namotion.Interceptor.OpcUa/Client/OpcUaTypeResolver.cs \
  src/Namotion.Interceptor.OpcUa/Client/OutboundWriter.cs \
  src/Namotion.Interceptor.OpcUa/Client/Resilience/SubscriptionHealthMonitor.cs
```

Expected: no matches. If a match appears, it is a loader dependency that must move to the rewrite; note it for Task 6.

- [ ] **Step 3: Build the production project**

Run: `dotnet build src/Namotion.Interceptor.OpcUa/Namotion.Interceptor.OpcUa.csproj`
Expected: the copied files compile. The old master loader still exists at this point; if it references a resolver member that changed shape, temporarily keep the loader compiling by leaving its callsite until Task 6 removes it. If it cannot compile, proceed to Task 4 and Task 6 and run these tests at the end of Task 6.

- [ ] **Step 4: Run the classifier, session-extension, type-resolver, and health tests**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "FullyQualifiedName~OpcUaStatusCodeClassifierTests|FullyQualifiedName~OpcUaSessionExtensionsTests|FullyQualifiedName~OpcUaTypeResolverTests|FullyQualifiedName~SubscriptionHealthMonitorTests"`
Expected: these groups PASS once the test assembly builds. Confirm the classifier asserts 11 permanent codes, `BadTooManyMonitoredItems` transient, and `Good`/`Uncertain` neither. If the full test assembly still cannot build (missing plan types), verify only that the production project builds and defer running until Task 6.

- [ ] **Step 5: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa/Client/OpcUaStatusCodeClassifier.cs \
        src/Namotion.Interceptor.OpcUa/Client/OpcUaTransientServiceException.cs \
        src/Namotion.Interceptor.OpcUa/Client/OpcUaBrowseName.cs \
        src/Namotion.Interceptor.OpcUa/Client/OpcUaSessionExtensions.cs \
        src/Namotion.Interceptor.OpcUa/Client/OpcUaTypeResolver.cs \
        src/Namotion.Interceptor.OpcUa/Client/OutboundWriter.cs \
        src/Namotion.Interceptor.OpcUa/Client/Resilience/SubscriptionHealthMonitor.cs
git commit -m "feat(opcua): reuse PR313 classification, batched protocol, and type resolver"
```

---

### Task 4: Root-path resolution helpers as internal seams

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa/Client/OpcUaSubjectClientSource.cs`
- Test: `src/Namotion.Interceptor.OpcUa.Tests/Client/OpcUaRootPathResolutionTests.cs` (already copied)

**Interfaces:**
- Produces: `internal Task<ReferenceDescription?> TryGetRootNodeAsync(ISession session, CancellationToken)` and `internal static ReferenceDescription? FindChildByBrowseName(ReferenceDescriptionCollection references, string browseName)` on `OpcUaSubjectClientSource`. `FindChildByBrowseName` skips references with a null `BrowseName` and returns null when no reference matches.

- [ ] **Step 1: Read PR 313's helper shapes for reference**

```bash
git show "feature/improve-opc-ua-loader-browse-performance:src/Namotion.Interceptor.OpcUa/Client/OpcUaSubjectClientSource.cs" | grep -nE "FindChildByBrowseName|TryGetRootNodeAsync|internal"
```

- [ ] **Step 2: Change `TryGetRootNodeAsync` to internal accepting `ISession`**

Change `private async Task<ReferenceDescription?> TryGetRootNodeAsync(Session session, CancellationToken cancellationToken)` to:

```csharp
    internal async Task<ReferenceDescription?> TryGetRootNodeAsync(ISession session, CancellationToken cancellationToken)
```

The caller in `StartListeningAsync` passes a concrete `Session`, which is assignable to `ISession`, so no caller change is needed. Resolve each RootPath hop by calling `FindChildByBrowseName` over the hop's references.

- [ ] **Step 3: Extract `FindChildByBrowseName`**

Add:

```csharp
    internal static ReferenceDescription? FindChildByBrowseName(ReferenceDescriptionCollection references, string browseName)
    {
        foreach (var reference in references)
        {
            if (reference.BrowseName?.Name == browseName)
            {
                return reference;
            }
        }

        return null;
    }
```

Replace the inline browse-name matching in `TryGetRootNodeAsync` with a call to this method.

- [ ] **Step 4: Run the root-path tests**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "FullyQualifiedName~OpcUaRootPathResolutionTests"`
Expected: PASS (3 tests) once the test assembly builds. If the assembly cannot build yet, build the production project and defer running until Task 6.

- [ ] **Step 5: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa/Client/OpcUaSubjectClientSource.cs
git commit -m "refactor(opcua): expose root-path resolution helpers as internal seams"
```

---

### Task 5: The load plan model

**Files:**
- Create: `src/Namotion.Interceptor.OpcUa/Client/LoadPlan/OpcUaLoadPlan.cs`

**Interfaces:**
- Produces: `internal sealed class OpcUaLoadPlan` accumulating staged subjects, claims (with smaller-node-id tie-break), and deferred value assignments, plus a single `Commit(OpcUaSubjectClientSource source)` applied later by the committer. The plan performs no durable mutation itself; discovery writes into it and commit reads from it.

- [ ] **Step 1: Create `OpcUaLoadPlan.cs`**

```csharp
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Registry.Abstractions;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Client.LoadPlan;

internal sealed class OpcUaLoadPlan
{
    private readonly IInterceptorSubject _rootSubject;
    private readonly ILogger _logger;

    private readonly List<(IInterceptorSubject Subject, IInterceptorSubjectContext ParentContext)> _stagedSubjects = new();
    private readonly List<(PropertyReference Property, NodeId NodeId, MonitoredItem MonitoredItem)> _claims = new();
    private readonly Dictionary<PropertyReference, int> _claimIndices = new(PropertyReference.Comparer);
    private readonly List<(object Source, RegisteredSubjectProperty Property, object? Value)> _stagedValues = new();
    private readonly List<(object Source, RegisteredSubjectProperty Property, object? Value)> _rootAssignments = new();

    // Discovery-only reuse maps (pure data; never mutate the live graph).
    public Dictionary<NodeId, IInterceptorSubject> SubjectsByNodeId { get; } = new();
    public HashSet<IInterceptorSubject> LoadedSubjects { get; } = new();

    public OpcUaLoadPlan(IInterceptorSubject rootSubject, ILogger logger)
    {
        _rootSubject = rootSubject;
        _logger = logger;
    }

    public void AddStagedSubject(IInterceptorSubject subject, IInterceptorSubjectContext parentContext)
        => _stagedSubjects.Add((subject, parentContext));

    public void AddClaim(PropertyReference property, NodeId nodeId, MonitoredItem monitoredItem)
    {
        if (_claimIndices.TryGetValue(property, out var index))
        {
            var existing = _claims[index];
            if (existing.NodeId != nodeId && nodeId.CompareTo(existing.NodeId) < 0)
            {
                // Deterministic tie-break: the same property reached by two browse paths keeps the smaller NodeId.
                _claims[index] = (property, nodeId, monitoredItem);
            }

            return;
        }

        _claimIndices[property] = _claims.Count;
        _claims.Add((property, nodeId, monitoredItem));
    }

    public void AddValueAssignment(object source, RegisteredSubjectProperty property, object? value)
    {
        if (ReferenceEquals(property.Subject, _rootSubject))
        {
            _rootAssignments.Add((source, property, value));
        }
        else
        {
            _stagedValues.Add((source, property, value));
        }
    }

    public IReadOnlyList<MonitoredItem> Commit(OpcUaSubjectClientSource source)
    {
        var ownership = source.Ownership;
        var nodeIdKey = source.OpcUaNodeIdKey;
        var monitoredItems = new List<MonitoredItem>(_claims.Count);
        var committedClaims = new List<(PropertyReference Property, string Key)>(_claims.Count);

        try
        {
            // Step 1: attach staged subjects into a graph that is not yet reachable from root.
            foreach (var (subject, parentContext) in _stagedSubjects)
            {
                subject.Context.AddFallbackContext(parentContext);
            }

            // Steps 2 and 3: claim ownership, stamp node-id metadata, build the monitored-item list.
            foreach (var (property, nodeId, monitoredItem) in _claims)
            {
                var alreadyOwned = property.TryGetSource(out var existing) && ReferenceEquals(existing, source);
                if (!ownership.ClaimSource(property))
                {
                    _logger.LogError(
                        "Property {Subject}.{Property} already owned by another source. Skipping OPC UA monitoring.",
                        property.Subject.GetType().Name, property.Name);
                    continue;
                }

                if (!alreadyOwned)
                {
                    committedClaims.Add((property, nodeIdKey));
                }

                property.SetPropertyData(nodeIdKey, nodeId);
                monitoredItems.Add(monitoredItem);
            }

            // Step 4: apply values to staged subjects before they become reachable from root.
            foreach (var (valueSource, property, value) in _stagedValues)
            {
                property.SetValueFromSource(valueSource, null, null, value);
            }

            // Steps 5 and 6: assign staged subjects onto root and apply root-level values.
            foreach (var (valueSource, property, value) in _rootAssignments)
            {
                property.SetValueFromSource(valueSource, null, null, value);
            }

            return monitoredItems;
        }
        catch
        {
            // Commit boundary: release the ownership and metadata this commit established; do not restore root values.
            foreach (var (property, key) in committedClaims)
            {
                try
                {
                    property.RemovePropertyData(key);
                    ownership.ReleaseSource(property);
                }
                catch (Exception releaseException)
                {
                    _logger.LogWarning(releaseException, "Failed to release claim during commit rollback.");
                }
            }

            throw;
        }
    }
}
```

- [ ] **Step 2: Build the production project**

Run: `dotnet build src/Namotion.Interceptor.OpcUa/Namotion.Interceptor.OpcUa.csproj`
Expected: builds (the old loader is still present and unchanged).

- [ ] **Step 3: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa/Client/LoadPlan/OpcUaLoadPlan.cs
git commit -m "feat(opcua): add the discovery load plan and ordered commit"
```

---

### Task 6: The discovery planner and loader orchestration (loader tests green)

**Files:**
- Create: `src/Namotion.Interceptor.OpcUa/Client/LoadPlan/OpcUaLoadPlanner.cs`
- Rewrite: `src/Namotion.Interceptor.OpcUa/Client/OpcUaSubjectLoader.cs`
- Test: `OpcUaSubjectLoaderTests`, `OpcUaSubjectLoaderBatchingTests`, `OpcUaSubjectLoaderDedupTests`, `OpcUaSubjectLoaderDictionaryReuseTests`, `OpcUaSubjectLoaderAttributeTests`

**Interfaces:**
- Consumes: `OpcUaSessionExtensions.BrowseNodesAsync`/`ReadNodesAsync`/`DistinctByResolvedNodeId`; `OpcUaTypeResolver.ResolveObjectNodeType`/`ResolveVariableTypesAsync`; `MonitoredItemFactory.Create(config, nodeId, property, rootSubject)`; `OpcUaSubjectFactory.CreateSubjectAsync`/`CreateCollectionSubjectAsync`; `DefaultSubjectFactory.Instance.CreateSubjectCollection`/`CreateSubjectDictionary`; `OpcUaBrowseName.TryGetBracketContent`; `_configuration.Mapper.TryGetMapping`/`TryGetPropertyAsync`; the mapper extensions `ResolvePropertyName`/`IsPropertyIncluded`/`TryGetValueProperty`.
- Produces: `internal sealed class OpcUaLoadPlanner` with `Task<OpcUaLoadPlan> CreatePlanAsync(IInterceptorSubject subject, ReferenceDescription rootNode, ISession session, CancellationToken)`, and the rewritten `OpcUaSubjectLoader.LoadSubjectAsync`.

- [ ] **Step 1: Run the loader test groups to capture the baseline**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "FullyQualifiedName~OpcUaSubjectLoaderTests|FullyQualifiedName~OpcUaSubjectLoaderBatchingTests|FullyQualifiedName~OpcUaSubjectLoaderDedupTests|FullyQualifiedName~OpcUaSubjectLoaderDictionaryReuseTests|FullyQualifiedName~OpcUaSubjectLoaderAttributeTests"`
Expected: build error or FAIL until the planner exists and the loader is rewritten. Note the failing method names.

- [ ] **Step 2: Create the planner skeleton**

Create `OpcUaLoadPlanner.cs` with the constructor, a per-load cross-round browse cache, and the entry point:

```csharp
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Client.LoadPlan;

internal sealed class OpcUaLoadPlanner
{
    private readonly IInterceptorSubject _rootSubject;
    private readonly OpcUaClientConfiguration _configuration;
    private readonly OpcUaSubjectClientSource _source;
    private readonly ILogger _logger;
    private readonly Dictionary<NodeId, IReadOnlyList<ReferenceDescription>> _browseCache = new();

    public OpcUaLoadPlanner(
        IInterceptorSubject rootSubject,
        OpcUaClientConfiguration configuration,
        OpcUaSubjectClientSource source,
        ILogger logger)
    {
        _rootSubject = rootSubject;
        _configuration = configuration;
        _source = source;
        _logger = logger;
    }

    public async Task<OpcUaLoadPlan> CreatePlanAsync(
        IInterceptorSubject subject, ReferenceDescription rootNode, ISession session, CancellationToken cancellationToken)
    {
        var plan = new OpcUaLoadPlan(_rootSubject, _logger);
        var rootNodeId = ExpandedNodeId.ToNodeId(rootNode.NodeId, session.NamespaceUris);
        if (rootNodeId is not null)
        {
            await PlanSubjectAsync(plan, subject, rootNodeId, session, 0, cancellationToken).ConfigureAwait(false);
        }

        return plan;
    }

    private async Task<IReadOnlyList<ReferenceDescription>?> BrowseThroughCacheAsync(
        NodeId nodeId, ISession session, CancellationToken cancellationToken)
    {
        if (_browseCache.TryGetValue(nodeId, out var cached))
        {
            return cached;
        }

        var results = await session.BrowseNodesAsync(
            [nodeId], _configuration.MaxReferencesPerNode, _configuration.MaxBrowseContinuations, _logger, cancellationToken)
            .ConfigureAwait(false);

        if (!results.TryGetValue(nodeId, out var references))
        {
            // Missing means the browse failed permanently; a transient failure surfaced as an exception above.
            return null;
        }

        _browseCache[nodeId] = references;
        return references;
    }
}
```

- [ ] **Step 3: Port discovery from PR 313 into the planner, writing into the plan**

Add `PlanSubjectAsync`, `PlanPropertyAsync`, `PlanContainerAsync`, `PlanAttributesAsync`, `MonitorValueNode`, and `ExtractDictionaryKey`, porting the browse/classify/reuse behavior from PR 313's `OpcUaSubjectLoader.cs` and `OpcUaAttributeLoader.cs`. Apply the transformation rule: every place PR 313 mutates durable state during discovery becomes a plan entry. Specifically:

- Replace `context.QueueClaim(...)` with `plan.AddClaim(...)`. Route all monitored-item creation through a single `MonitorValueNode`:

```csharp
    private void MonitorValueNode(OpcUaLoadPlan plan, RegisteredSubjectProperty property, NodeId nodeId)
    {
        if (property.Reference.TryGetSource(out var existing) && !ReferenceEquals(existing, _source))
        {
            _logger.LogWarning(
                "Property {Subject}.{Property} already owned by another source. Skipping OPC UA monitoring.",
                property.Subject.GetType().Name, property.Name);
            return;
        }

        var monitoredItem = MonitoredItemFactory.Create(_configuration, nodeId, property, _rootSubject);
        plan.AddClaim(property.Reference, nodeId, monitoredItem);
    }
```

- Replace `context.QueueOrApplySetValue(source, property, value)` with `plan.AddValueAssignment(_source, property, value)` (always deferred; never applied inline).
- Replace `context.RegisterStagedSubject(subject, parentContext)` with `plan.AddStagedSubject(subject, property.Subject.Context)` and do not call `AddFallbackContext` during discovery.
- Create child subjects with `await _configuration.SubjectFactory.CreateSubjectAsync(property, nodeReference, session, cancellationToken)` and collection elements with `await _configuration.SubjectFactory.CreateCollectionSubjectAsync(property, childNode, index, session, cancellationToken)`.
- Reuse existing subjects via `plan.SubjectsByNodeId` (same NodeId across paths), collection children by index position, and dictionary children by `Convert.ToString(index, CultureInfo.InvariantCulture)` against `ExtractDictionaryKey(childNode.BrowseName.Name)`.
- For a container whose `BrowseThroughCacheAsync` returns null (permanent browse failure), log and skip so existing items are preserved. For an empty-but-present result, rebuild empty.
- Guard cycles with `plan.LoadedSubjects`.
- Deduplicate structured targets (subject reference, collection, dictionary) with a `HashSet<RegisteredSubjectProperty>` where the first browse-order reference wins and the loser subtree is not browsed.
- Enforce the attribute depth cap:

```csharp
        if (attributeDepth > _configuration.MaxAttributeTraversals)
        {
            _logger.LogWarning(
                "Stopping OPC UA attribute traversal for {PropertyName} at depth {Depth}.",
                property.Name, attributeDepth);
            return;
        }
```

Add `private static string ExtractDictionaryKey(string browseName) => OpcUaBrowseName.TryGetBracketContent(browseName, out var content) ? content.ToString() : browseName;`.

- [ ] **Step 4: Rewrite `OpcUaSubjectLoader.LoadSubjectAsync` to orchestrate planner then commit**

```csharp
    public async Task<IReadOnlyList<MonitoredItem>> LoadSubjectAsync(
        IInterceptorSubject subject, ReferenceDescription node, ISession session, CancellationToken cancellationToken)
    {
        var planner = new LoadPlan.OpcUaLoadPlanner(_subject, _configuration, _source, _logger);
        var plan = await planner.CreatePlanAsync(subject, node, session, cancellationToken).ConfigureAwait(false);
        return plan.Commit(_source);
    }
```

Delete the master loader's recursive side-effecting private methods after moving their behavior into the planner. Keep the ctor signature unchanged.

- [ ] **Step 5: Run the core loader tests**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "FullyQualifiedName~OpcUaSubjectLoaderTests"`
Expected: PASS (12 tests).

- [ ] **Step 6: Run batching, dedup, dictionary-reuse, and attribute tests**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "FullyQualifiedName~OpcUaSubjectLoaderBatchingTests|FullyQualifiedName~OpcUaSubjectLoaderDedupTests|FullyQualifiedName~OpcUaSubjectLoaderDictionaryReuseTests|FullyQualifiedName~OpcUaSubjectLoaderAttributeTests"`
Expected: PASS (10 + 11 + 3 + 4 tests). Iterate on the planner until green; use PR 313's loader as the behavioral reference for any failing case.

- [ ] **Step 7: Run the previously deferred utility groups now that the assembly builds**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "FullyQualifiedName~OpcUaStatusCodeClassifierTests|FullyQualifiedName~OpcUaSessionExtensionsTests|FullyQualifiedName~OpcUaTypeResolverTests|FullyQualifiedName~SubscriptionHealthMonitorTests|FullyQualifiedName~OpcUaRootPathResolutionTests|FullyQualifiedName~OpcUaClientConfigurationTests"`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa/Client/LoadPlan/OpcUaLoadPlanner.cs \
        src/Namotion.Interceptor.OpcUa/Client/OpcUaSubjectLoader.cs
git commit -m "feat(opcua): discover into a pure load plan and orchestrate commit"
```

---

### Task 7: Re-point the one internal commit test and freeze the failure suite

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa.Tests/Client/OpcUaSubjectLoaderFailureTests.cs` (one method)

**Interfaces:**
- Consumes: `OpcUaLoadPlan` (the re-pointed test), the public loader surface (the other seven tests), `OpcUaTransientServiceException`, `SourceOwnershipManager`, the property-change observable.

- [ ] **Step 1: Run the failure tests to see the single build error**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "FullyQualifiedName~OpcUaSubjectLoaderFailureTests"`
Expected: build error only on `WhenApplyFailsMidway_ThenOwnershipFromPreviousLoadIsRetained` (it references the removed `OpcUaLoadContext`). The other seven use the public loader surface.

- [ ] **Step 2: Re-point `WhenApplyFailsMidway_ThenOwnershipFromPreviousLoadIsRetained` to `OpcUaLoadPlan`**

Replace the method body, preserving the exact assertions:

```csharp
    [Fact]
    public void WhenApplyFailsMidway_ThenOwnershipFromPreviousLoadIsRetained()
    {
        // Arrange: simulate a reload. "PreOwned" is already owned by this source from a
        // previous successful load; "NewlyClaimed" is claimed for the first time by this
        // commit. A root value assignment then throws mid-commit. The rollback must release
        // only the claim this commit established: releasing pre-existing ownership would
        // leave application writes unrouted until the next successful retry.
        var (_, source, subject) = CreateFixture();
        var registeredSubject = subject.TryGetRegisteredSubject()!;

        var preOwned = registeredSubject.AddProperty("PreOwned", typeof(int), _ => 0, (_, _) => { });
        var newlyClaimed = registeredSubject.AddProperty("NewlyClaimed", typeof(int), _ => 0, (_, _) => { });
        var throwing = registeredSubject.AddProperty("Throwing", typeof(int), _ => 0,
            (_, _) => throw new InvalidOperationException("Setter failure aborts commit."));

        Assert.True(source.Ownership.ClaimSource(preOwned.Reference));

        var plan = new OpcUaLoadPlan(subject, NullLogger<OpcUaSubjectClientSource>.Instance);
        plan.AddClaim(preOwned.Reference, new NodeId(9001, 2), new MonitoredItem(NullTelemetryContext.Instance));
        plan.AddClaim(newlyClaimed.Reference, new NodeId(9002, 2), new MonitoredItem(NullTelemetryContext.Instance));
        plan.AddValueAssignment(source, throwing, 42);

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => plan.Commit(source));

        // The pre-existing ownership survives the rollback; the claim newly established
        // by this commit is released.
        Assert.True(preOwned.Reference.TryGetSource(out var owner));
        Assert.Same(source, owner);
        Assert.False(newlyClaimed.Reference.TryGetSource(out _));
    }
```

Add `using Namotion.Interceptor.OpcUa.Client.LoadPlan;` to the file if `OpcUaLoadPlan` is not otherwise in scope. Leave the other seven tests unchanged.

- [ ] **Step 3: Run the failure tests**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "FullyQualifiedName~OpcUaSubjectLoaderFailureTests"`
Expected: PASS (8 tests): discovery failure leaves root at pre-load state with empty ownership and no registry orphans, nested staged rollback leaves no orphans, clean retry, claims-before-root ordering, root assignments after all browses complete, permanent child skipped and load completes, and the re-pointed mid-commit rollback.

- [ ] **Step 4: Run the whole loader group**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "FullyQualifiedName~OpcUaSubjectLoader"`
Expected: PASS across base, core, batching, dedup, dictionary-reuse, attribute, and failure tests.

- [ ] **Step 5: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa.Tests/Client/OpcUaSubjectLoaderFailureTests.cs
git commit -m "test(opcua): re-point commit-rollback test to the load plan"
```

---

### Task 8: Gate data-change callbacks until subscription setup completes

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa/Client/Connection/SubscriptionManager.cs`
- Test: `src/Namotion.Interceptor.OpcUa.Tests/Client/OpcUaSubscriptionCallbackGatingTests.cs` (new)

**Interfaces:**
- Produces: `internal void ApplyDataChange(uint clientHandle, DateTimeOffset timestamp, object? value)` gated by a callbacks-enabled flag; `internal bool AreCallbacksEnabledForTesting`; `internal void EnableCallbacksForTesting()`; `internal IDictionary<uint, RegisteredSubjectProperty> MonitoredItemsForTesting`. Callbacks are enabled only as the final step of `CreateBatchedSubscriptionsAsync`.

- [ ] **Step 1: Copy PR 313's SubscriptionManager as the shared subscribe base**

```bash
git checkout feature/improve-opc-ua-loader-browse-performance -- src/Namotion.Interceptor.OpcUa/Client/Connection/SubscriptionManager.cs
```

This gives the shared batched-subscription mechanics. The next steps add the two new behaviors on top. Update any renamed config reference inside it if the grep from Task 2 flagged one.

- [ ] **Step 2: Add the callback gate and a single funnel**

In `SubscriptionManager`, add `private volatile bool _callbacksEnabled;`. Reset it to `false` at the start of `CreateBatchedSubscriptionsAsync` (next to the shutdown-flag reset) and set `false` in `DisposeAsync`. Route the SDK callback through one method:

```csharp
    internal void ApplyDataChange(uint clientHandle, DateTimeOffset timestamp, object? value)
    {
        if (_shuttingDown || !_callbacksEnabled)
        {
            return;
        }

        if (!_monitoredItems.TryGetValue(clientHandle, out var property))
        {
            return;
        }

        var update = new PropertyUpdate
        {
            Property = property.Reference,
            Timestamp = timestamp,
            Value = _configuration.ValueConverter.ConvertToPropertyValue(value, property)
        };

        _propertyWriter.Write(
            (_source, update),
            static state => state.update.Property.SetValueFromSource(
                state._source,
                state.update.Timestamp,
                null,
                state.update.Value));
    }

    internal bool AreCallbacksEnabledForTesting => _callbacksEnabled;
    internal void EnableCallbacksForTesting() => _callbacksEnabled = true;
    internal IDictionary<uint, RegisteredSubjectProperty> MonitoredItemsForTesting => _monitoredItems;
```

Change `OnFastDataChange` to translate each notification item into an `ApplyDataChange(clientHandle, timestamp, value)` call so the SDK path and tests share the gate. Keep the existing `_shuttingDown` guard at the top of `OnFastDataChange` as well.

- [ ] **Step 3: Enable callbacks only as the final setup step**

In `CreateBatchedSubscriptionsAsync`, keep the SDK callback attachment (`subscription.FastDataChangeCallback += OnFastDataChange`) where PR 313 has it, but do not let notifications reach subjects until setup completes: set `_callbacksEnabled = true` as the last statement of `CreateBatchedSubscriptionsAsync`, after all items are added, failed items pruned, detached subjects swept (Task 9), and read-after-write registered.

- [ ] **Step 4: Write the failing test**

Create `OpcUaSubscriptionCallbackGatingTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Dynamic;
using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.OpcUa.Client.Connection;
using Namotion.Interceptor.Registry;
using Opc.Ua;
using Xunit;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

public class OpcUaSubscriptionCallbackGatingTests
{
    [Fact]
    public void WhenDataChangeArrivesBeforeSetupCompletes_ThenNotificationIsIgnored()
    {
        // Arrange: a SubscriptionManager whose monitored-item map holds one property,
        // but callbacks are not yet enabled (setup not complete).
        var harness = SubscriptionManagerTestHarness.Create();
        harness.RegisterMonitoredItem(clientHandle: 7, propertyName: "Value");

        // Act: a notification arrives before setup completes.
        harness.Manager.ApplyDataChange(7, DateTimeOffset.UtcNow, 42d);

        // Assert: the gated notification did not reach the property.
        Assert.NotEqual(42d, harness.GetValue("Value"));

        // Act: setup completes, then the notification arrives again.
        harness.Manager.EnableCallbacksForTesting();
        harness.Manager.ApplyDataChange(7, DateTimeOffset.UtcNow, 42d);

        // Assert: after enabling, the notification is applied.
        Assert.Equal(42d, harness.GetValue("Value"));
    }
}
```

Add a `SubscriptionManagerTestHarness` in a shared test-helper file `src/Namotion.Interceptor.OpcUa.Tests/Client/SubscriptionManagerTestHarness.cs`. It must construct a `SubscriptionManager` with real dependencies the same way production does. Determine the exact constructor by reading the current source once:

```bash
git show "feature/improve-opc-ua-loader-browse-performance:src/Namotion.Interceptor.OpcUa/Client/Connection/SubscriptionManager.cs" | grep -nE "public SubscriptionManager|internal SubscriptionManager|SubjectPropertyWriter|_propertyWriter"
```

The harness builds a `DynamicSubject` on `InterceptorSubjectContext.Create().WithRegistry().WithLifecycle()`, adds a dynamic double property named `Value` through its registered subject, constructs the `OpcUaSubjectClientSource` and `SubjectPropertyWriter` the same way `StartListeningAsync` does, constructs the `SubscriptionManager`, and inserts `(clientHandle -> RegisteredSubjectProperty)` into `manager.MonitoredItemsForTesting`. Expose `GetValue(name)` reading the dynamic property. Keep all seams `internal`.

- [ ] **Step 5: Run the test to verify the gate**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "FullyQualifiedName~OpcUaSubscriptionCallbackGatingTests"`
Expected: FAIL if `ApplyDataChange` does not gate on `_callbacksEnabled` (the pre-enable assertion would see 42). PASS once the gate is in place.

- [ ] **Step 6: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa/Client/Connection/SubscriptionManager.cs \
        src/Namotion.Interceptor.OpcUa.Tests/Client/OpcUaSubscriptionCallbackGatingTests.cs \
        src/Namotion.Interceptor.OpcUa.Tests/Client/SubscriptionManagerTestHarness.cs
git commit -m "feat(opcua): gate data-change callbacks until subscription setup completes"
```

---

### Task 9: Sweep detached subjects before registering read-after-write

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa/Client/Connection/SubscriptionManager.cs`
- Test: `src/Namotion.Interceptor.OpcUa.Tests/Client/OpcUaSubscriptionSweepOrderingTests.cs` (new)

**Interfaces:**
- Consumes: `subject.TryGetRegisteredSubject()` (null means detached), `ReadAfterWriteManager.RegisterProperty`, `PollingManager.RemoveItemsForSubject`.
- Produces: `internal IReadOnlyList<IInterceptorSubject> SweepDetachedSubjectsForTesting()` and `internal IReadOnlyList<uint> RegisterSurvivorsForReadAfterWriteForTesting(IReadOnlyCollection<MonitoredItem> monitoredItems)`. Subscribe runs the sweep before survivor registration.

- [ ] **Step 1: Reorder subscribe: sweep before read-after-write**

In `CreateBatchedSubscriptionsAsync`, after applying changes and pruning failed items and before enabling callbacks (Task 8), run in this order:
1. A sweep over `_monitoredItems.Values`: for each subject where `subject.TryGetRegisteredSubject() is null`, call `RemoveItemsForSubject(subject)` and `_pollingManager?.RemoveItemsForSubject(subject)`. Collect the swept subjects.
2. Register read-after-write only for survivors: iterate `subscription.MonitoredItems` and register each item that is `{ Handle: RegisteredSubjectProperty property, Status.Created: true }` and still present in `_monitoredItems` (so a swept subject's items are excluded).

Update `RegisterPropertiesWithReadAfterWriteManager` so the presence check gates registration:

```csharp
        if (item is { Handle: RegisteredSubjectProperty property, Status.Created: true } &&
            _monitoredItems.ContainsKey(item.ClientHandle))
        {
            _readAfterWriteManager.RegisterProperty(
                item.StartNodeId,
                property,
                GetRequestedSamplingInterval(property),
                TimeSpan.FromMilliseconds(item.Status.SamplingInterval));
        }
```

Expose internal seams that call the same production methods:

```csharp
    internal IReadOnlyList<IInterceptorSubject> SweepDetachedSubjectsForTesting() => SweepDetachedSubjects();
    internal IReadOnlyList<uint> RegisterSurvivorsForReadAfterWriteForTesting(IReadOnlyCollection<MonitoredItem> monitoredItems)
        => RegisterSurvivors(monitoredItems);
```

Where `SweepDetachedSubjects()` returns the swept subjects and `RegisterSurvivors(...)` returns the client handles it registered.

- [ ] **Step 2: Write the failing test**

Create `OpcUaSubscriptionSweepOrderingTests.cs`:

```csharp
using Namotion.Interceptor.Registry;
using Opc.Ua;
using Opc.Ua.Client;
using Xunit;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

public class OpcUaSubscriptionSweepOrderingTests
{
    [Fact]
    public void WhenSubjectDetachesDuringSetup_ThenItIsSweptAndNeverRegisteredForReadAfterWrite()
    {
        // Arrange: two monitored items, one whose subject stays attached and one whose
        // subject is detached from the registry before setup completes.
        var harness = SubscriptionManagerTestHarness.CreateWithReadAfterWriteSpy();
        var survivor = harness.RegisterMonitoredItem(clientHandle: 1, propertyName: "Kept");
        var detached = harness.RegisterMonitoredItemThenDetachSubject(clientHandle: 2, propertyName: "Gone");

        // Act: run the subscribe tail in production order (sweep, then register survivors).
        var swept = harness.Manager.SweepDetachedSubjectsForTesting();
        var registered = harness.Manager.RegisterSurvivorsForReadAfterWriteForTesting(harness.MonitoredItemSnapshot());

        // Assert: the detached subject was swept and never registered; the survivor was registered.
        Assert.Contains(detached.Subject, swept);
        Assert.DoesNotContain(detached.Subject, harness.ReadAfterWriteSpy.RegisteredSubjects);
        Assert.Contains(survivor.Subject, harness.ReadAfterWriteSpy.RegisteredSubjects);
        Assert.DoesNotContain(2u, registered);
        Assert.Contains(1u, registered);
    }
}
```

Extend `SubscriptionManagerTestHarness` with `CreateWithReadAfterWriteSpy()`, a recording read-after-write registrar, `RegisterMonitoredItem(clientHandle, propertyName)`, `RegisterMonitoredItemThenDetachSubject(clientHandle, propertyName)` (detaches by removing the subject from its parent so `TryGetRegisteredSubject()` returns null), and `MonitoredItemSnapshot()`. If `ReadAfterWriteManager` cannot be substituted directly, introduce a minimal `internal interface IReadAfterWriteRegistrar { void RegisterProperty(NodeId nodeId, RegisteredSubjectProperty property, int? requestedSamplingInterval, TimeSpan revisedSamplingInterval); }`, make `ReadAfterWriteManager` implement it, and have `SubscriptionManager` depend on the interface so the spy can record calls.

- [ ] **Step 3: Run the test to verify the ordering**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "FullyQualifiedName~OpcUaSubscriptionSweepOrderingTests"`
Expected: FAIL if registration runs before the sweep. PASS once sweep precedes survivor registration and detached subjects are excluded.

- [ ] **Step 4: Run both new-behavior groups and the health/read-after-write groups**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "FullyQualifiedName~OpcUaSubscriptionCallbackGatingTests|FullyQualifiedName~OpcUaSubscriptionSweepOrderingTests|FullyQualifiedName~SubscriptionHealthMonitorTests|FullyQualifiedName~ReadAfterWriteManagerTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa/Client/Connection/SubscriptionManager.cs \
        src/Namotion.Interceptor.OpcUa.Tests/Client/OpcUaSubscriptionSweepOrderingTests.cs \
        src/Namotion.Interceptor.OpcUa.Tests/Client/SubscriptionManagerTestHarness.cs
git commit -m "feat(opcua): sweep detached subjects before registering read-after-write"
```

---

### Task 10: Source wiring, full suite, public API snapshot, docs, and comparison

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa/Client/OpcUaSubjectClientSource.cs` (only if a callsite needs the new loader result or renamed config)
- Modify: `src/Namotion.Interceptor.OpcUa.Tests/VerifyChecksTests.PublicApi.verified.txt`
- Modify: `docs/connectors-opcua-client.md`
- Create: `docs/design/opcua-loader-comparison.md`

**Interfaces:** none new.

- [ ] **Step 1: Confirm the client source still drives the rewritten loader**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: build succeeds. `StartListeningAsync` still calls `LoadSubjectAsync`, then `CreateSubscriptionsAsync(monitoredItems, ...)` only when `monitoredItems.Count > 0`. Fix any HomeBlaze OPC UA client or sample reference that used a removed member or an old config name.

- [ ] **Step 2: Run the full non-integration suite**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "Category!=Integration"`
Expected: PASS. This is the frozen acceptance contract plus the two new-behavior tests.

- [ ] **Step 3: Accept the public API snapshot**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "FullyQualifiedName~VerifyChecksTests"`
Expected: PASS if the copied `verified.txt` already matches (the public surface equals PR 313's: renamed config properties, added bounds, public `OpcUaTransientServiceException`, resolver methods). If it FAILS, review the `.received.txt` diff, confirm only intended public changes and no internal type leaked to public, then:

```bash
cp src/Namotion.Interceptor.OpcUa.Tests/VerifyChecksTests.PublicApi.received.txt \
   src/Namotion.Interceptor.OpcUa.Tests/VerifyChecksTests.PublicApi.verified.txt
```

Re-run to confirm PASS.

- [ ] **Step 4: Run the OPC UA integration tests**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests`
Expected: PASS, including reconnection and subscription-health integration tests, which exercise load, subscribe (with callback gating and sweep ordering), and reconnect against the sample server. If a local certificate or port issue causes an environmental failure, capture the exact failing test and command output in the handoff.

- [ ] **Step 5: Update the connector documentation to the implemented design**

In `docs/connectors-opcua-client.md`: update any configuration name from the old maxima to `MaxItemsPerSubscription`, `MaxReferencesPerNode`, `SubscriptionMaxNotificationsPerPublish`, and document `MaxBrowseContinuations` and `MaxAttributeTraversals`. Add a concise Loading model section describing the four phases and the two new behaviors: discovery builds an in-memory plan with no durable side effects; validation deduplicates and resolves deterministic conflicts; commit claims ownership before assigning subjects onto root and does not restore root values on a mid-commit failure; subscribe sweeps detached subjects before registering read-after-write and enables data-change callbacks only after setup completes. No em dashes.

- [ ] **Step 6: Write the comparison against PR 313**

Create `docs/design/opcua-loader-comparison.md` covering the spec's Comparison Criteria: same acceptance tests pass; internal lifecycle states during loading (this branch replaces PR 313's two-phase live-mutation load context and its Dispose rollback with one pure discovery plan plus one commit that has a single claim-and-metadata release path); phase-boundary clarity (discover and validate fully retry-clean, commit and subscribe the only durable-mutation phases); browse/read batching parity (shared session extensions); and code churn outside OPC UA client internals. State plainly whether the new design is simpler. If it is larger or more fragile, recommend keeping PR 313 as the base and applying only the two new behaviors there.

- [ ] **Step 7: Verify no em dashes in the docs**

Run: `grep -n "—" docs/connectors-opcua-client.md docs/design/opcua-loader-comparison.md`
Expected: no output.

- [ ] **Step 8: Record comparison metrics**

Run:

```bash
git diff --stat master..HEAD -- src/Namotion.Interceptor.OpcUa src/Namotion.Interceptor.OpcUa.Tests docs
```

Note files changed, insertions, deletions, and the passing suites (targeted, non-integration, full) in the comparison doc.

- [ ] **Step 9: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa/Client/OpcUaSubjectClientSource.cs \
        src/Namotion.Interceptor.OpcUa.Tests/VerifyChecksTests.PublicApi.verified.txt \
        docs/connectors-opcua-client.md docs/design/opcua-loader-comparison.md
git commit -m "docs(opcua): document the four-phase loader and record the PR313 comparison"
```

---

## Self-Review notes

- Spec coverage: every required coverage area maps to a copied test file frozen in Task 1 (batching, continuation, split retry, classifier whitelist and transient including `BadTooManyMonitoredItems`, Good/Uncertain neither, config validation for both new bounds, loader failure cleanup and retryability, claim-before-root ordering, un-undoable root mutation, dynamic property/attribute dedup, attribute depth cap, cross-round browse-cache reuse, collection rebind-by-index, dictionary rebind-by-key, existing entries preserved on permanent browse failure, smaller-node-id tie-break, best-effort value read leaves property unset, health/retryability). The two behaviors absent from PR 313 get new tests in Tasks 8 and 9.
- The only test coupled to internal commit types is re-pointed once in Task 7; every other test ports verbatim.
- The loader public contract (ctor and `LoadSubjectAsync`) and `OpcUaSubjectClientSource.Ownership`/`OpcUaNodeIdKey` are preserved, so the test base compiles against the rewrite unchanged.
- Verified primitive signatures used in authored code: `MonitoredItemFactory.Create(config, nodeId, property, rootSubject)`, `OpcUaSubjectFactory.CreateSubjectAsync`/`CreateCollectionSubjectAsync`, `SetValueFromSource(source, changed, received, value)`, `SourceOwnershipManager.ClaimSource`/`ReleaseSource`, `PropertyReference.SetPropertyData`/`RemovePropertyData`/`TryGetSource`/`Comparer`, `AddFallbackContext`.
- Naming is consistent across tasks: `OpcUaLoadPlan.AddClaim`/`AddValueAssignment`/`AddStagedSubject`/`Commit`, `OpcUaLoadPlanner.CreatePlanAsync`/`PlanSubjectAsync`/`MonitorValueNode`, `SubscriptionManager.ApplyDataChange`/`SweepDetachedSubjects`/`RegisterSurvivors`.
