# PR Cleanup: feature/opc-ua-full-sync Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Fix 17 verified issues in the OPC UA bidirectional sync PR before merge.

**Architecture:** The OPC UA module enables bidirectional synchronization between C# model objects and OPC UA nodes. Key components: `CustomNodeManager` (server-side node management), `OpcUaSubjectClientSource` (client-side tracking), `GraphChangePublisher`/`GraphChangeReceiver` (structural change handling).

**Tech Stack:** .NET 9.0, C# 13, OPC UA SDK, xUnit integration tests

---

## Task 1: Add Lock Protection to ClearPropertyData (C1)

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa/Server/CustomNodeManager.cs:69-93`

**Step 1: Read the current implementation**

The `ClearPropertyData` method iterates over subjects without acquiring `_structureLock`, creating a thread-safety issue when called during server shutdown while structural changes may be in progress.

**Step 2: Add lock protection**

```csharp
public void ClearPropertyData()
{
    _structureLock.Wait();
    try
    {
        var rootSubject = _subject.TryGetRegisteredSubject();
        if (rootSubject != null)
        {
            foreach (var property in rootSubject.Properties)
            {
                property.Reference.RemovePropertyData(OpcUaSubjectServerBackgroundService.OpcVariableKey);
                ClearAttributePropertyData(property);
            }
        }

        foreach (var subject in _subjectRegistry.GetAllSubjects())
        {
            var registeredSubject = subject.TryGetRegisteredSubject();
            if (registeredSubject != null)
            {
                foreach (var property in registeredSubject.Properties)
                {
                    property.Reference.RemovePropertyData(OpcUaSubjectServerBackgroundService.OpcVariableKey);
                    ClearAttributePropertyData(property);
                }
            }
        }
    }
    finally
    {
        _structureLock.Release();
    }
}
```

**Step 3: Build to verify**

Run: `dotnet build src/Namotion.Interceptor.OpcUa`
Expected: Build successful

**Step 4: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa/Server/CustomNodeManager.cs
git commit -m "$(cat <<'EOF'
fix(opc-ua): add lock protection to ClearPropertyData

Wrap ClearPropertyData method body with _structureLock to prevent
thread-safety issues when called during server shutdown while
structural changes may be in progress.

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: Add GC Clarifying Comment (C2)

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa/Server/OpcUaServerNodeCreator.cs:364`

**Step 1: Add comment explaining GC behavior**

Before line 364, add:

```csharp
// Note: No explicit unsubscription needed - when the node is removed via DeleteNode()
// and all references are cleared (registry + property data), the node and handler
// become unreachable together and are GC'd as a unit.
variableNode.StateChanged += (_, _, changes) =>
```

**Step 2: Build to verify**

Run: `dotnet build src/Namotion.Interceptor.OpcUa`
Expected: Build successful

**Step 3: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa/Server/OpcUaServerNodeCreator.cs
git commit -m "$(cat <<'EOF'
docs(opc-ua): clarify StateChanged handler GC behavior

Add comment explaining why explicit unsubscription is not needed -
node and handler are GC'd together when all references are cleared.

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: Improve Lost Updates Log Message (C3)

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa/Server/OpcUaServerGraphChangePublisher.cs:90-93`

**Step 1: Update catch block log message**

Replace lines 90-93:

```csharp
catch (Exception ex)
{
    _logger.LogWarning(ex,
        "Failed to emit GeneralModelChangeEvent with {Count} changes. " +
        "Changes lost - clients may need to resync.",
        changesToEmit.Count);
}
```

**Step 2: Build to verify**

Run: `dotnet build src/Namotion.Interceptor.OpcUa`
Expected: Build successful

**Step 3: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa/Server/OpcUaServerGraphChangePublisher.cs
git commit -m "$(cat <<'EOF'
fix(opc-ua): improve log message for lost model change events

Make log message explicit that changes are lost on exception and
clients may need to resync. Requeueing adds complexity and server
restart clears queue anyway.

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 4: Remove Stale Pre-computed Index (H3)

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa/Server/OpcUaServerGraphChangeReceiver.cs:344-357`

**Step 1: Remove pre-computed index calculation**

In `TryAddToCollectionAsync`, remove lines 344-345 and update the return:

```csharp
private async Task<(RegisteredSubjectProperty? Property, object? Index)> TryAddToCollectionAsync(
    RegisteredSubjectProperty property,
    IInterceptorSubject subject,
    string? containerPropertyName,
    QualifiedName browseName,
    string? propertyName)
{
    var nodeConfiguration = _nodeMapper.TryGetNodeConfiguration(property);
    var collectionStructure = nodeConfiguration?.CollectionStructure ?? CollectionNodeStructure.Container;

    // Container mode requires coming through a container folder
    if (containerPropertyName is null && collectionStructure == CollectionNodeStructure.Container)
    {
        return (null, null);
    }

    // In Flat mode, verify the browse name matches this property's pattern
    if (containerPropertyName is null && collectionStructure == CollectionNodeStructure.Flat)
    {
        if (!OpcUaHelper.TryParseCollectionIndex(browseName.Name, propertyName, out _))
        {
            return (null, null);
        }
    }

    var elementType = GetCollectionElementType(property.Type);
    if (elementType is null || !elementType.IsInstanceOfType(subject))
    {
        return (null, null);
    }

    // Note: Don't pre-compute index here - it can become stale during concurrent changes.
    // CreateSubjectNode will compute the actual index from property.Children.Length - 1.

    var addedSubject = await _graphChangeApplier.AddToCollectionAsync(
        property,
        () => Task.FromResult(subject),
        _source).ConfigureAwait(false);

    if (addedSubject is not null)
    {
        _logger.LogDebug(
            "External AddNode: Added subject to collection property '{PropertyName}'.",
            property.Name);
        return (property, null);  // CreateSubjectNode computes index from property.Children.Length - 1
    }

    return (null, null);
}
```

**Step 2: Build to verify**

Run: `dotnet build src/Namotion.Interceptor.OpcUa`
Expected: Build successful

**Step 3: Run tests**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "Category=Integration"`
Expected: All tests pass

**Step 4: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa/Server/OpcUaServerGraphChangeReceiver.cs
git commit -m "$(cat <<'EOF'
fix(opc-ua): remove stale pre-computed collection index

Pre-computed index can become stale during concurrent C# model changes.
Let CreateSubjectNode compute the actual index from property.Children.Length - 1.

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 5: Add Deep Nested Sync for Client→Server (F1)

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa/Client/OpcUaClientGraphChangeSender.cs:143`
- Create test: `src/Namotion.Interceptor.OpcUa.Tests/Integration/Graph/ClientToServerNestedPropertyTests.cs` (add test)

**Step 1: Add recursion for nested references**

After line 142 in `OnSubjectAddedAsync`, add:

```csharp
// Recursively create nodes for nested reference properties (deep sync)
if (_configuration.EnableGraphChangePublishing)
{
    var registeredSubject = subject.TryGetRegisteredSubject();
    if (registeredSubject is not null)
    {
        foreach (var nestedProperty in registeredSubject.Properties)
        {
            if (nestedProperty.IsSubjectReference)
            {
                var nestedSubject = nestedProperty.GetValue() as IInterceptorSubject;
                if (nestedSubject is not null && !_source.IsSubjectTracked(nestedSubject))
                {
                    await OnSubjectAddedAsync(nestedProperty, nestedSubject, null, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }
    }
}
```

**Step 2: Build to verify**

Run: `dotnet build src/Namotion.Interceptor.OpcUa`
Expected: Build successful

**Step 3: Add integration test**

Add to `ClientToServerNestedPropertyTests.cs`:

```csharp
[Fact]
public async Task AssignReferenceWithNestedObject_ServerReceivesBothLevels()
{
    var clientArea = Client!.Root!.ClientToServerNestedProperty;
    var serverArea = ServerFixture.ServerRoot.ClientToServerNestedProperty;

    // Use unique test identifier
    var testId = Guid.NewGuid().ToString("N")[..8];
    var firstName = $"DeepNest_{testId}";
    var city = $"City_{testId}";

    Logger.Log($"Test starting with testId: {testId}");

    // Create person with nested address (both created on client)
    var person = new NestedPerson(Client.Context)
    {
        FirstName = firstName,
        LastName = "Test",
        Address = new NestedAddress(Client.Context) { City = city, ZipCode = "12345" }
    };
    clientArea.Person = person;
    Logger.Log($"Client assigned Person with nested Address.City={city}");

    // Assert server receives BOTH person AND nested address
    await AsyncTestHelpers.WaitUntilAsync(
        () =>
        {
            var serverPerson = serverArea.Person;
            var serverCity = serverPerson?.Address?.City;
            Logger.Log($"Polling server: Person.FirstName={serverPerson?.FirstName ?? "null"}, Address.City={serverCity ?? "null"}");
            return serverCity == city;
        },
        timeout: TimeSpan.FromSeconds(60),
        pollInterval: TimeSpan.FromMilliseconds(500),
        message: "Server should receive both person and nested address");

    Assert.Equal(firstName, serverArea.Person?.FirstName);
    Assert.Equal(city, serverArea.Person?.Address?.City);
    Logger.Log("Client->Server deep nested sync verified");
}
```

**Step 4: Run test**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "FullyQualifiedName~AssignReferenceWithNestedObject"`
Expected: Test passes

**Step 5: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa/Client/OpcUaClientGraphChangeSender.cs
git add src/Namotion.Interceptor.OpcUa.Tests/Integration/Graph/ClientToServerNestedPropertyTests.cs
git commit -m "$(cat <<'EOF'
feat(opc-ua): add deep nested sync for client-to-server

When a subject with nested reference properties is added on the client,
recursively create nodes for all nested references to ensure deep sync.

Adds integration test for AssignReferenceWithNestedObject scenario.

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 6: Add Depth Limit to Recursive Attributes (M1)

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa/Server/OpcUaServerNodeCreator.cs:276-299`

**Step 1: Add depth parameter and check**

Update `CreateAttributeNodes` signature and add depth check:

```csharp
/// <summary>
/// Creates attribute nodes for a property.
/// </summary>
/// <param name="parentNode">The parent node.</param>
/// <param name="property">The property to create attributes for.</param>
/// <param name="parentPath">The parent path for NodeId generation.</param>
/// <param name="depth">Current recursion depth (default 0, max 10).</param>
public void CreateAttributeNodes(NodeState parentNode, RegisteredSubjectProperty property, string parentPath, int depth = 0)
{
    // Prevent infinite recursion in case of circular attribute references
    if (depth > 10)
    {
        _logger.LogWarning(
            "CreateAttributeNodes: Maximum depth (10) exceeded for property '{PropertyName}'. Stopping recursion.",
            property.Name);
        return;
    }

    foreach (var attribute in property.Attributes)
    {
        var attributeConfiguration = _nodeMapper.TryGetNodeConfiguration(attribute);
        if (attributeConfiguration is null)
            continue;

        var attributeName = attributeConfiguration.BrowseName ?? attribute.BrowseName;
        var attributePath = parentPath + PathDelimiter + attributeName;
        var referenceTypeId = _nodeFactory.GetReferenceTypeId(_nodeManager, attributeConfiguration) ?? ReferenceTypeIds.HasProperty;

        // Create variable node for attribute
        var attributeNode = CreateVariableNodeForAttribute(
            attributeName,
            attribute,
            parentNode.NodeId,
            attributePath,
            referenceTypeId);

        // Recursive: attributes can have attributes
        CreateAttributeNodes(attributeNode, attribute, attributePath, depth + 1);
    }
}
```

**Step 2: Update call site in CreateVariableNode (line 269)**

```csharp
CreateAttributeNodes(variableNode, property, parentPath + propertyName, 0);
```

**Step 3: Build to verify**

Run: `dotnet build src/Namotion.Interceptor.OpcUa`
Expected: Build successful

**Step 4: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa/Server/OpcUaServerNodeCreator.cs
git commit -m "$(cat <<'EOF'
fix(opc-ua): add depth limit to recursive attribute creation

Prevent infinite recursion in case of circular attribute references
by limiting CreateAttributeNodes to 10 levels deep.

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 7: Document Fire-and-Forget Dispose (M2)

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa/Client/Connection/SessionManager.cs:547-561`

**Step 1: Add XML documentation**

Update the comment at line 542-546:

```csharp
/// <summary>
/// Satisfies IDisposable for interface compatibility.
/// Delegates to DisposeAsync() via fire-and-forget to ensure cleanup.
/// SubjectSourceBackgroundService checks for IAsyncDisposable first, so this is never called in normal operation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Limitation:</b> Fire-and-forget disposal means errors during cleanup are logged but not propagated.
/// In rare edge cases (e.g., process termination during disposal), some subscriptions may not be
/// cleanly removed from the server. The server will eventually clean up orphaned subscriptions
/// via session timeout.
/// </para>
/// </remarks>
void IDisposable.Dispose()
```

**Step 2: Build to verify**

Run: `dotnet build src/Namotion.Interceptor.OpcUa`
Expected: Build successful

**Step 3: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa/Client/Connection/SessionManager.cs
git commit -m "$(cat <<'EOF'
docs(opc-ua): document fire-and-forget dispose limitation

Add XML remarks explaining the limitation of fire-and-forget disposal
and that server will clean up orphaned subscriptions via session timeout.

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 8: Document Sync Lock Requirement (M3)

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa/Client/OpcUaSubjectClientSource.cs:102-119`

**Step 1: Add comment explaining sync lock**

Before line 102 (`RemoveItemsForSubject` method), add:

```csharp
/// <summary>
/// Removes tracked items for a detached subject.
/// </summary>
/// <remarks>
/// Uses synchronous Wait() instead of WaitAsync() because this method is called from
/// the synchronous SubjectDetaching event handler. The lock duration is brief (only
/// unregistration and local cleanup), so blocking is acceptable here.
/// </remarks>
private void RemoveItemsForSubject(IInterceptorSubject subject, PropertyReference? property, object? index)
```

**Step 2: Build to verify**

Run: `dotnet build src/Namotion.Interceptor.OpcUa`
Expected: Build successful

**Step 3: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa/Client/OpcUaSubjectClientSource.cs
git commit -m "$(cat <<'EOF'
docs(opc-ua): document sync lock requirement in RemoveItemsForSubject

Explain why synchronous Wait() is used instead of WaitAsync() -
method is called from sync event handler and lock duration is brief.

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 9: Document Lock on Node (M4)

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa/Server/OpcUaServerNodeCreator.cs:370`

**Step 1: Add comment explaining lock on node**

Before the `lock (variableNode)` at line 370:

```csharp
// Lock on variableNode is acceptable here because:
// 1. Duration is brief (just reading two properties)
// 2. The node is internal to OPC UA SDK and not exposed for external locking
// 3. OPC UA SDK itself uses this pattern for node state synchronization
lock (variableNode)
```

**Step 2: Build to verify**

Run: `dotnet build src/Namotion.Interceptor.OpcUa`
Expected: Build successful

**Step 3: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa/Server/OpcUaServerNodeCreator.cs
git commit -m "$(cat <<'EOF'
docs(opc-ua): document why lock on node is acceptable

Explain that lock duration is brief, node is internal to SDK,
and this matches OPC UA SDK's own synchronization pattern.

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 10: Extract Common Browse Continuation Logic (M5)

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa/OpcUaHelper.cs:198-323`

**Step 1: Extract common helper method**

Replace the duplicate continuation logic with a shared helper:

```csharp
/// <summary>
/// Browses nodes and handles continuation points for paginated results.
/// </summary>
private static async Task<ReferenceDescriptionCollection> BrowseWithContinuationAsync(
    ISession session,
    BrowseDescriptionCollection browseDescriptions,
    CancellationToken cancellationToken,
    uint maxReferencesPerNode = 0)
{
    var results = new ReferenceDescriptionCollection();

    var response = await session.BrowseAsync(
        null,
        null,
        maxReferencesPerNode,
        browseDescriptions,
        cancellationToken).ConfigureAwait(false);

    if (response.Results.Count > 0 && StatusCode.IsGood(response.Results[0].StatusCode))
    {
        results.AddRange(response.Results[0].References);

        var continuationPoint = response.Results[0].ContinuationPoint;
        while (continuationPoint is { Length: > 0 })
        {
            var nextResponse = await session.BrowseNextAsync(
                null, false,
                [continuationPoint], cancellationToken).ConfigureAwait(false);

            if (nextResponse.Results.Count > 0 && StatusCode.IsGood(nextResponse.Results[0].StatusCode))
            {
                var browseResult = nextResponse.Results[0];
                if (browseResult.References is { Count: > 0 } nextReferences)
                {
                    foreach (var reference in nextReferences)
                    {
                        results.Add(reference);
                    }
                }
                continuationPoint = browseResult.ContinuationPoint;
            }
            else
            {
                break;
            }
        }
    }

    return results;
}

/// <summary>
/// Browses inverse (parent) references of a given node, handling continuation points for paginated results.
/// </summary>
public static Task<ReferenceDescriptionCollection> BrowseInverseReferencesAsync(
    ISession session,
    NodeId nodeId,
    CancellationToken cancellationToken)
{
    var browseDescriptions = new BrowseDescriptionCollection
    {
        new BrowseDescription
        {
            NodeId = nodeId,
            BrowseDirection = BrowseDirection.Inverse,
            ReferenceTypeId = ReferenceTypeIds.HierarchicalReferences,
            IncludeSubtypes = true,
            NodeClassMask = (uint)(NodeClass.Object | NodeClass.Variable),
            ResultMask = (uint)BrowseResultMask.All
        }
    };

    return BrowseWithContinuationAsync(session, browseDescriptions, cancellationToken);
}

/// <summary>
/// Browses child nodes of a given node, handling continuation points for paginated results.
/// </summary>
public static Task<ReferenceDescriptionCollection> BrowseNodeAsync(
    ISession session,
    NodeId nodeId,
    CancellationToken cancellationToken,
    uint maxReferencesPerNode = 0)
{
    const uint nodeClassMask = (uint)NodeClass.Variable | (uint)NodeClass.Object;

    var browseDescriptions = new BrowseDescriptionCollection
    {
        new BrowseDescription
        {
            NodeId = nodeId,
            BrowseDirection = BrowseDirection.Forward,
            ReferenceTypeId = ReferenceTypeIds.HierarchicalReferences,
            IncludeSubtypes = true,
            NodeClassMask = nodeClassMask,
            ResultMask = (uint)BrowseResultMask.All
        }
    };

    return BrowseWithContinuationAsync(session, browseDescriptions, cancellationToken, maxReferencesPerNode);
}
```

**Step 2: Build to verify**

Run: `dotnet build src/Namotion.Interceptor.OpcUa`
Expected: Build successful

**Step 3: Run tests**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "Category=Integration"`
Expected: All tests pass

**Step 4: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa/OpcUaHelper.cs
git commit -m "$(cat <<'EOF'
refactor(opc-ua): extract common browse continuation logic

DRY: Extract BrowseWithContinuationAsync to share pagination logic
between BrowseInverseReferencesAsync and BrowseNodeAsync.

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 11: Document Unused Validation Methods (M6)

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa/Server/OpcUaServerExternalNodeValidator.cs:43,120`

**Step 1: Add XML documentation**

Update the class documentation:

```csharp
/// <summary>
/// Helper class for external node management operations.
/// Provides methods to validate and process AddNodes/DeleteNodes requests
/// based on the server configuration and type registry.
/// </summary>
/// <remarks>
/// <para>
/// <b>Note:</b> The ValidateAddNodes and ValidateDeleteNodes methods are reserved for future
/// integration with custom NodeManager implementations that need batch validation before processing.
/// Currently, validation is performed inline in OpcUaServerGraphChangeReceiver.
/// </para>
/// </remarks>
public class OpcUaServerExternalNodeValidator
```

**Step 2: Build to verify**

Run: `dotnet build src/Namotion.Interceptor.OpcUa`
Expected: Build successful

**Step 3: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa/Server/OpcUaServerExternalNodeValidator.cs
git commit -m "$(cat <<'EOF'
docs(opc-ua): document reserved validation methods

Mark ValidateAddNodes/ValidateDeleteNodes as reserved for future
integration with custom NodeManager batch validation.

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 12: Fix O(n²) Item Removal in SubscriptionManager (L1)

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa/Client/Connection/SubscriptionManager.cs:285-288,343-346`

**Step 1: Use HashSet for processed items**

In `AddMonitoredItemsAsync`, replace the List.Remove with HashSet:

```csharp
public async Task AddMonitoredItemsAsync(
    IReadOnlyList<MonitoredItem> monitoredItems,
    Session session,
    CancellationToken cancellationToken)
{
    if (monitoredItems.Count == 0)
    {
        return;
    }

    var itemsToAdd = new HashSet<MonitoredItem>(monitoredItems);
    var maximumItemsPerSubscription = _configuration.MaximumItemsPerSubscription;

    // Try to add to existing subscriptions first
    foreach (var subscription in _subscriptions.Keys)
    {
        if (itemsToAdd.Count == 0)
        {
            break;
        }

        var availableSpace = maximumItemsPerSubscription - (int)subscription.MonitoredItemCount;
        if (availableSpace <= 0)
        {
            continue;
        }

        var itemsForThisSubscription = itemsToAdd.Take(availableSpace).ToList();
        foreach (var item in itemsForThisSubscription)
        {
            subscription.AddItem(item);

            if (item.Handle is RegisteredSubjectProperty property)
            {
                _monitoredItems[item.ClientHandle] = property;
            }
        }

        try
        {
            await subscription.ApplyChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ServiceResultException sre)
        {
            _logger.LogWarning(sre, "ApplyChanges failed when adding monitored items for live sync.");
        }

        await FilterOutFailedMonitoredItemsAsync(subscription, cancellationToken).ConfigureAwait(false);
        RegisterPropertiesWithReadAfterWriteManager(subscription);

        foreach (var item in itemsForThisSubscription)
        {
            itemsToAdd.Remove(item);  // O(1) with HashSet
        }
    }

    // Create new subscriptions for remaining items
    while (itemsToAdd.Count > 0)
    {
        // ... rest of method unchanged, but use itemsToAdd.Take() and Remove() same pattern
```

**Step 2: Build to verify**

Run: `dotnet build src/Namotion.Interceptor.OpcUa`
Expected: Build successful

**Step 3: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa/Client/Connection/SubscriptionManager.cs
git commit -m "$(cat <<'EOF'
perf(opc-ua): use HashSet for O(1) item removal in SubscriptionManager

Replace List<MonitoredItem>.Remove (O(n)) with HashSet<MonitoredItem>.Remove (O(1))
to fix O(n²) complexity when adding many monitored items.

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 13: Extract Magic Number to Named Constant (L2)

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa/Client/OpcUaClientGraphChangeReceiver.cs:682`

**Step 1: Add named constant at class level**

Add near the top of the class:

```csharp
/// <summary>
/// Maximum depth when traversing the OPC UA hierarchy to find a parent subject.
/// Prevents infinite loops in case of circular references or deeply nested structures.
/// </summary>
private const int MaxParentSearchDepth = 10;
```

**Step 2: Replace magic number at line 682**

```csharp
for (var depth = 0; depth < MaxParentSearchDepth; depth++)
```

**Step 3: Build to verify**

Run: `dotnet build src/Namotion.Interceptor.OpcUa`
Expected: Build successful

**Step 4: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa/Client/OpcUaClientGraphChangeReceiver.cs
git commit -m "$(cat <<'EOF'
refactor(opc-ua): extract maxDepth to named constant

Replace magic number 10 with MaxParentSearchDepth constant
with documentation explaining its purpose.

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 14: Add Null Check for child.Index (L3)

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa/Server/OpcUaServerNodeCreator.cs:212,222`

**Step 1: Add null check in CreateCollectionObjectNode**

Replace lines 210-213:

```csharp
foreach (var child in children)
{
    if (child.Index is null)
    {
        _logger.LogWarning(
            "CreateCollectionObjectNode: Skipping child with null index in collection property.");
        continue;
    }
    CreateCollectionChildNode(property, child.Subject, child.Index, propertyName, parentPath, parentNodeId, nodeConfiguration);
}
```

And similarly for line 220-223:

```csharp
foreach (var child in children)
{
    if (child.Index is null)
    {
        _logger.LogWarning(
            "CreateCollectionObjectNode: Skipping child with null index in collection property.");
        continue;
    }
    CreateCollectionChildNode(property, child.Subject, child.Index, propertyName, parentPath, containerNode.NodeId, nodeConfiguration);
}
```

**Step 2: Build to verify**

Run: `dotnet build src/Namotion.Interceptor.OpcUa`
Expected: Build successful

**Step 3: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa/Server/OpcUaServerNodeCreator.cs
git commit -m "$(cat <<'EOF'
fix(opc-ua): add null check for child.Index in collection creation

Replace null-forgiving operator with explicit null check and warning log
to handle edge case of children with null indices.

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 15: Use PathDelimiter Constant (L5)

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa/Server/CustomNodeManager.cs`

**Step 1: Replace hardcoded "." with PathDelimiter**

Search for any hardcoded `"."` used as path delimiter and replace with `PathDelimiter` constant.

In `GetParentNodeIdAndPath` (line 477):

```csharp
var path = parentNode.NodeId.Identifier is string stringId
    ? stringId + PathDelimiter
    : string.Empty;
```

**Step 2: Build to verify**

Run: `dotnet build src/Namotion.Interceptor.OpcUa`
Expected: Build successful

**Step 3: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa/Server/CustomNodeManager.cs
git commit -m "$(cat <<'EOF'
refactor(opc-ua): use PathDelimiter constant consistently

Replace hardcoded "." with PathDelimiter constant for consistency.

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 16: Use Static Empty Dictionary (S5)

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors/GraphChangePublisher.cs:77`

**Step 1: Add static empty dictionary**

Add at class level:

```csharp
private static readonly IReadOnlyDictionary<object, object> EmptyDictionary =
    new Dictionary<object, object>();
```

Replace line 77:

```csharp
var newDictionary = change.TryGetNewValue<IDictionary>(out var newDict) ? newDict : (IDictionary)EmptyDictionary;
```

**Step 2: Build to verify**

Run: `dotnet build src/Namotion.Interceptor.Connectors`
Expected: Build successful

**Step 3: Commit**

```bash
git add src/Namotion.Interceptor.Connectors/GraphChangePublisher.cs
git commit -m "$(cat <<'EOF'
perf(connectors): use static empty dictionary to avoid allocation

Replace new Dictionary<object, object>() with cached static instance.

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 17: Iterate Directly Instead of ToDictionary (S6)

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors/GraphChangePublisher.cs:82`

**Step 1: Replace ToDictionary with direct iteration**

Replace lines 82-90:

```csharp
// Build lookup from old children for removal check
Dictionary<object, IInterceptorSubject>? oldChildren = null;
if (removedKeys is not null)
{
    oldChildren = new Dictionary<object, IInterceptorSubject>();
    foreach (var child in property.Children)
    {
        if (child.Index is not null)
        {
            oldChildren[child.Index] = child.Subject;
        }
    }
}

if (removedKeys is not null && oldChildren is not null)
{
    foreach (var key in removedKeys)
    {
        if (oldChildren.TryGetValue(key, out var subject))
            await OnSubjectRemovedAsync(property, subject, key, cancellationToken);
    }
}
```

**Step 2: Build to verify**

Run: `dotnet build src/Namotion.Interceptor.Connectors`
Expected: Build successful

**Step 3: Commit**

```bash
git add src/Namotion.Interceptor.Connectors/GraphChangePublisher.cs
git commit -m "$(cat <<'EOF'
perf(connectors): avoid ToDictionary allocation per call

Build dictionary lazily only when needed for removal check,
avoiding allocation when there are no removed keys.

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>
EOF
)"
```

---

## Final Verification

**Step 1: Full build**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: Build successful with no warnings

**Step 2: Run all tests**

Run: `dotnet test src/Namotion.Interceptor.slnx`
Expected: All tests pass

**Step 3: Run integration tests specifically**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "Category=Integration"`
Expected: All integration tests pass, including the new `AssignReferenceWithNestedObject_ServerReceivesBothLevels`

---

## Summary

| Task | Issue | Type | Status |
|------|-------|------|--------|
| 1 | C1 | Thread safety | Lock protection |
| 2 | C2 | Documentation | GC comment |
| 3 | C3 | Logging | Improved message |
| 4 | H3 | Race condition | Remove stale index |
| 5 | F1 | Feature | Deep nested sync |
| 6 | M1 | Safety | Depth limit |
| 7 | M2 | Documentation | Dispose limitation |
| 8 | M3 | Documentation | Sync lock |
| 9 | M4 | Documentation | Lock on node |
| 10 | M5 | DRY | Extract helper |
| 11 | M6 | Documentation | Reserved methods |
| 12 | L1 | Performance | O(1) removal |
| 13 | L2 | Readability | Named constant |
| 14 | L3 | Safety | Null check |
| 15 | L5 | Consistency | Use constant |
| 16 | S5 | Performance | Static dictionary |
| 17 | S6 | Performance | Lazy allocation |
