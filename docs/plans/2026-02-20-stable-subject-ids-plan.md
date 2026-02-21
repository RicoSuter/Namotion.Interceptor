# Stable Subject IDs Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Enable bidirectional structural mutations via stable base62-encoded GUID subject IDs that persist across updates, replacing ephemeral counter-based IDs.

**Architecture:** Four coordinated changes: (1) lazy-generated base62 GUIDs stored in subject `Data` dictionary with registry reverse index for O(1) lookup by stable ID, (2) collection operations reference subjects by stable ID + predecessor ID instead of positional index, (3) partial updates no longer walk path-to-root — only subjects with actual changes are included, (4) wire format DTOs updated for new ID scheme.

**Tech Stack:** C# 13, .NET 9.0, System.Numerics.BigInteger for base62 encoding, xUnit + Verify for testing

**Design doc:** `docs/plans/2026-02-20-stable-subject-ids-design.md`

---

## Phase 1: Foundation (Additive, TDD)

### Task 1: Stable Subject ID Generation Utility

**Files:**
- Create: `src/Namotion.Interceptor.Registry.Tests/StableSubjectIdTests.cs`
- Modify: `src/Namotion.Interceptor.Registry/SubjectRegistryExtensions.cs`

**Step 1: Write the failing tests**

Create `src/Namotion.Interceptor.Registry.Tests/StableSubjectIdTests.cs`:

```csharp
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Registry.Tests;

public class StableSubjectIdTests
{
    [Fact]
    public void GenerateSubjectId_Returns22CharBase62String()
    {
        var id = SubjectRegistryExtensions.GenerateSubjectId();

        Assert.Equal(22, id.Length);
        Assert.All(id.ToCharArray(), c =>
            Assert.True(char.IsLetterOrDigit(c), $"Character '{c}' is not alphanumeric"));
    }

    [Fact]
    public void GenerateSubjectId_ProducesUniqueIds()
    {
        var ids = Enumerable.Range(0, 1000)
            .Select(_ => SubjectRegistryExtensions.GenerateSubjectId())
            .ToHashSet();

        Assert.Equal(1000, ids.Count);
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test src/Namotion.Interceptor.Registry.Tests --filter "StableSubjectIdTests" -v m`
Expected: FAIL — `GenerateSubjectId` does not exist

**Step 3: Implement GenerateSubjectId**

In `src/Namotion.Interceptor.Registry/SubjectRegistryExtensions.cs`, add the following at the top of the file (new using) and inside the class:

Add `using System.Numerics;` to the using statements.

Add these members to the `SubjectRegistryExtensions` class:

```csharp
internal const string SubjectIdKey = "Namotion.Interceptor.SubjectId";

public static string GenerateSubjectId()
{
    const string chars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    var value = new BigInteger(Guid.NewGuid().ToByteArray(), isUnsigned: true);
    var result = new char[22];
    for (var i = 21; i >= 0; i--)
    {
        value = BigInteger.DivRem(value, 62, out var remainder);
        result[i] = chars[(int)remainder];
    }
    return new string(result);
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test src/Namotion.Interceptor.Registry.Tests --filter "StableSubjectIdTests" -v m`
Expected: PASS (2 tests)

**Step 5: Commit**

```bash
git add src/Namotion.Interceptor.Registry/SubjectRegistryExtensions.cs src/Namotion.Interceptor.Registry.Tests/StableSubjectIdTests.cs
git commit -m "feat: add base62-encoded GUID generation for stable subject IDs"
```

---

### Task 2: Registry Reverse Index

**Files:**
- Modify: `src/Namotion.Interceptor.Registry/Abstractions/ISubjectRegistry.cs`
- Modify: `src/Namotion.Interceptor.Registry/SubjectRegistry.cs`
- Modify: `src/Namotion.Interceptor.Registry.Tests/StableSubjectIdTests.cs`

**Step 1: Write failing tests for reverse index**

Append to `src/Namotion.Interceptor.Registry.Tests/StableSubjectIdTests.cs`:

```csharp
[Fact]
public void RegisterStableId_ThenTryGetSubjectByStableId_ReturnsSubject()
{
    var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
    var person = new Models.Person(context) { FirstName = "Test" };
    var registry = context.GetService<ISubjectRegistry>();

    registry.RegisterStableId("testId123", (IInterceptorSubject)person);

    Assert.True(registry.TryGetSubjectByStableId("testId123", out var found));
    Assert.Same(person, found);
}

[Fact]
public void TryGetSubjectByStableId_WithUnknownId_ReturnsFalse()
{
    var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
    var registry = context.GetService<ISubjectRegistry>();

    Assert.False(registry.TryGetSubjectByStableId("unknown", out _));
}

[Fact]
public void RegisterStableId_WhenSubjectDetached_RemovesFromReverseIndex()
{
    var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
    var child = new Models.Person { FirstName = "Child" };
    var parent = new Models.Person(context) { FirstName = "Parent", Mother = child };
    var registry = context.GetService<ISubjectRegistry>();

    registry.RegisterStableId("childId", (IInterceptorSubject)child);
    Assert.True(registry.TryGetSubjectByStableId("childId", out _));

    // Detach by setting to null
    parent.Mother = null;

    Assert.False(registry.TryGetSubjectByStableId("childId", out _));
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test src/Namotion.Interceptor.Registry.Tests --filter "StableSubjectIdTests" -v m`
Expected: FAIL — `RegisterStableId` and `TryGetSubjectByStableId` do not exist on `ISubjectRegistry`

**Step 3: Add interface methods to ISubjectRegistry**

In `src/Namotion.Interceptor.Registry/Abstractions/ISubjectRegistry.cs`, add two methods to the interface (after the existing `TryGetRegisteredSubject` method, before the closing brace):

```csharp
/// <summary>
/// Registers a stable ID for a subject in the reverse index.
/// </summary>
/// <param name="stableId">The stable ID.</param>
/// <param name="subject">The subject.</param>
void RegisterStableId(string stableId, IInterceptorSubject subject);

/// <summary>
/// Tries to get a subject by its stable ID from the reverse index.
/// </summary>
/// <param name="stableId">The stable ID.</param>
/// <param name="subject">The subject if found.</param>
/// <returns>True if a subject with the given stable ID was found.</returns>
bool TryGetSubjectByStableId(string stableId, out IInterceptorSubject subject);
```

**Step 4: Implement reverse index in SubjectRegistry**

In `src/Namotion.Interceptor.Registry/SubjectRegistry.cs`:

Add a new field after `_knownSubjects`:
```csharp
private readonly Dictionary<string, IInterceptorSubject> _stableIdToSubject = new();
```

Add two new public methods (after `TryGetRegisteredSubject`):

```csharp
/// <inheritdoc />
public void RegisterStableId(string stableId, IInterceptorSubject subject)
{
    lock (_knownSubjects)
    {
        _stableIdToSubject[stableId] = subject;
    }
}

/// <inheritdoc />
public bool TryGetSubjectByStableId(string stableId, out IInterceptorSubject subject)
{
    lock (_knownSubjects)
    {
        return _stableIdToSubject.TryGetValue(stableId, out subject!);
    }
}
```

In the `HandleLifecycleChange` method, inside the `if (change.IsContextDetach)` block (line 88-91), add reverse index cleanup **after** `_knownSubjects.Remove(change.Subject)`:

```csharp
if (change.IsContextDetach)
{
    _knownSubjects.Remove(change.Subject);

    // Clean up stable ID reverse index
    string? stableIdToRemove = null;
    foreach (var (id, s) in _stableIdToSubject)
    {
        if (ReferenceEquals(s, change.Subject))
        {
            stableIdToRemove = id;
            break;
        }
    }
    if (stableIdToRemove is not null)
        _stableIdToSubject.Remove(stableIdToRemove);
}
```

**Step 5: Run tests to verify they pass**

Run: `dotnet test src/Namotion.Interceptor.Registry.Tests --filter "StableSubjectIdTests" -v m`
Expected: PASS (5 tests)

**Step 6: Commit**

```bash
git add src/Namotion.Interceptor.Registry/Abstractions/ISubjectRegistry.cs src/Namotion.Interceptor.Registry/SubjectRegistry.cs src/Namotion.Interceptor.Registry.Tests/StableSubjectIdTests.cs
git commit -m "feat: add reverse index to SubjectRegistry for stable ID lookups"
```

---

### Task 3: GetOrAddSubjectId + SetSubjectId Extension Methods

**Files:**
- Modify: `src/Namotion.Interceptor.Registry/SubjectRegistryExtensions.cs`
- Modify: `src/Namotion.Interceptor.Registry.Tests/StableSubjectIdTests.cs`

**Step 1: Write failing tests**

Append to `src/Namotion.Interceptor.Registry.Tests/StableSubjectIdTests.cs`:

```csharp
[Fact]
public void GetOrAddSubjectId_ReturnsSameIdOnMultipleCalls()
{
    var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
    var person = new Models.Person(context) { FirstName = "Test" };
    var subject = (IInterceptorSubject)person;

    var id1 = subject.GetOrAddSubjectId();
    var id2 = subject.GetOrAddSubjectId();

    Assert.Equal(id1, id2);
    Assert.Equal(22, id1.Length);
}

[Fact]
public void GetOrAddSubjectId_RegistersInReverseIndex()
{
    var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
    var person = new Models.Person(context) { FirstName = "Test" };
    var subject = (IInterceptorSubject)person;
    var registry = context.GetService<ISubjectRegistry>();

    var id = subject.GetOrAddSubjectId();

    Assert.True(registry.TryGetSubjectByStableId(id, out var found));
    Assert.Same(person, found);
}

[Fact]
public void SetSubjectId_AssignsKnownId()
{
    var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
    var person = new Models.Person(context) { FirstName = "Test" };
    var subject = (IInterceptorSubject)person;

    subject.SetSubjectId("myCustomId");

    var id = subject.GetOrAddSubjectId();
    Assert.Equal("myCustomId", id);
}

[Fact]
public void SetSubjectId_RegistersInReverseIndex()
{
    var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
    var person = new Models.Person(context) { FirstName = "Test" };
    var subject = (IInterceptorSubject)person;
    var registry = context.GetService<ISubjectRegistry>();

    subject.SetSubjectId("assignedId");

    Assert.True(registry.TryGetSubjectByStableId("assignedId", out var found));
    Assert.Same(person, found);
}

[Fact]
public void GetOrAddSubjectId_WithoutRegistry_StillWorks()
{
    // Context without registry — GetOrAddSubjectId should still generate an ID
    var context = InterceptorSubjectContext.Create();
    var person = new Models.Person(context) { FirstName = "Test" };
    var subject = (IInterceptorSubject)person;

    var id = subject.GetOrAddSubjectId();

    Assert.Equal(22, id.Length);
    Assert.All(id.ToCharArray(), c => Assert.True(char.IsLetterOrDigit(c)));
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test src/Namotion.Interceptor.Registry.Tests --filter "StableSubjectIdTests" -v m`
Expected: FAIL — `GetOrAddSubjectId` and `SetSubjectId` do not exist

**Step 3: Implement extension methods**

In `src/Namotion.Interceptor.Registry/SubjectRegistryExtensions.cs`, add these two methods to the class (after the existing methods, before the closing brace of the class):

```csharp
/// <summary>
/// Gets or lazily generates a stable subject ID for the given subject.
/// The ID is stored in the subject's Data dictionary and auto-registered
/// in the registry's reverse index if available.
/// </summary>
/// <param name="subject">The subject.</param>
/// <returns>The stable subject ID.</returns>
public static string GetOrAddSubjectId(this IInterceptorSubject subject)
{
    return (string)subject.Data.GetOrAdd(
        (null, SubjectIdKey),
        static (_, s) =>
        {
            var id = GenerateSubjectId();
            s.Context.TryGetService<ISubjectRegistry>()?.RegisterStableId(id, s);
            return id;
        },
        subject)!;
}

/// <summary>
/// Assigns a known stable ID to a subject (from an incoming update).
/// Auto-registers in the reverse index if available.
/// </summary>
/// <param name="subject">The subject.</param>
/// <param name="id">The stable ID to assign.</param>
public static void SetSubjectId(this IInterceptorSubject subject, string id)
{
    subject.Data[(null, SubjectIdKey)] = id;
    subject.Context.TryGetService<ISubjectRegistry>()?.RegisterStableId(id, subject);
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test src/Namotion.Interceptor.Registry.Tests --filter "StableSubjectIdTests" -v m`
Expected: PASS (10 tests)

**Step 5: Run all Registry tests to ensure no regressions**

Run: `dotnet test src/Namotion.Interceptor.Registry.Tests -v m`
Expected: All tests pass

**Step 6: Commit**

```bash
git add src/Namotion.Interceptor.Registry/SubjectRegistryExtensions.cs src/Namotion.Interceptor.Registry.Tests/StableSubjectIdTests.cs
git commit -m "feat: add GetOrAddSubjectId and SetSubjectId extension methods"
```

---

## Phase 2: Wire Format DTO Changes (Breaking)

### Task 4: Wire Format DTO Changes

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors/Updates/SubjectCollectionOperation.cs`
- Modify: `src/Namotion.Interceptor.Connectors/Updates/SubjectPropertyItemUpdate.cs`
- Modify: `src/Namotion.Interceptor.Connectors/Updates/SubjectUpdate.cs`

**Step 1: Replace SubjectCollectionOperation**

Replace the entire content of `src/Namotion.Interceptor.Connectors/Updates/SubjectCollectionOperation.cs` with:

```csharp
using System.Text.Json.Serialization;

namespace Namotion.Interceptor.Connectors.Updates;

/// <summary>
/// Represents a structural operation on a collection or dictionary.
/// All operations reference subjects by stable ID.
/// </summary>
public class SubjectCollectionOperation
{
    /// <summary>
    /// The type of operation.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    [JsonPropertyName("action")]
    public SubjectCollectionOperationType Action { get; init; }

    /// <summary>
    /// The stable ID of the target subject (required for all operations).
    /// Also the key in the Subjects dictionary for Insert operations.
    /// </summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>
    /// For Insert and Move: the stable ID of the predecessor item.
    /// Null means place at head of collection.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("afterId")]
    public string? AfterId { get; init; }

    /// <summary>
    /// Optional key for dictionary operations (Insert/Remove).
    /// Not used for collection operations.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("key")]
    public string? Key { get; init; }
}
```

**Step 2: Replace SubjectPropertyItemUpdate**

Replace the entire content of `src/Namotion.Interceptor.Connectors/Updates/SubjectPropertyItemUpdate.cs` with:

```csharp
using System.Text.Json.Serialization;

namespace Namotion.Interceptor.Connectors.Updates;

/// <summary>
/// Represents an item reference in a collection or dictionary property update.
/// For collections: array order defines collection ordering, only id is required.
/// For dictionaries: id + key identifies the entry.
/// </summary>
public readonly struct SubjectPropertyItemUpdate
{
    /// <summary>
    /// The stable subject ID for this item.
    /// References a subject in the Subjects dictionary.
    /// </summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>
    /// The dictionary key (only for Dictionary kind properties).
    /// Null for Collection kind items where ordering comes from array position.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("key")]
    public string? Key { get; init; }
}
```

**Step 3: Make SubjectUpdate.Root nullable**

In `src/Namotion.Interceptor.Connectors/Updates/SubjectUpdate.cs`, change the `Root` property (line 18-19) from:

```csharp
[JsonPropertyName("root")]
public string Root { get; init; } = string.Empty;
```

to:

```csharp
[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
[JsonPropertyName("root")]
public string? Root { get; init; }
```

**Step 4: Attempt build (will fail — expected)**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: Compilation errors in `CollectionDiffBuilder.cs`, `SubjectItemsUpdateFactory.cs`, `SubjectItemsUpdateApplier.cs`, `SubjectUpdateFactory.cs`, `SubjectUpdateApplier.cs`, and test files due to removed `Index`, `FromIndex` properties and changed `Root` type.

**Step 5: Commit (broken build — part of coordinated breaking change)**

```bash
git add src/Namotion.Interceptor.Connectors/Updates/SubjectCollectionOperation.cs src/Namotion.Interceptor.Connectors/Updates/SubjectPropertyItemUpdate.cs src/Namotion.Interceptor.Connectors/Updates/SubjectUpdate.cs
git commit -m "feat: update wire format DTOs for stable ID protocol (breaking)"
```

---

## Phase 3: Build Side (Creating Updates)

### Task 5: SubjectUpdateBuilder — Use Stable IDs

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors/Updates/Internal/SubjectUpdateBuilder.cs`

**Step 1: Replace counter-based IDs with stable IDs**

In `src/Namotion.Interceptor.Connectors/Updates/Internal/SubjectUpdateBuilder.cs`:

Add `using Namotion.Interceptor.Registry;` to the top of the file.

Remove the `_nextId` field (line 11):
```csharp
// DELETE: private int _nextId;
```

Replace the `GetOrCreateId` method (lines 29-37) with:
```csharp
public string GetOrCreateId(IInterceptorSubject subject)
{
    if (!_subjectToId.TryGetValue(subject, out var id))
    {
        id = subject.GetOrAddSubjectId();
        _subjectToId[subject] = id;
    }
    return id;
}
```

Replace the `GetOrCreateIdWithStatus` method (lines 43-53) with:
```csharp
public (string Id, bool IsNew) GetOrCreateIdWithStatus(IInterceptorSubject subject)
{
    if (_subjectToId.TryGetValue(subject, out var id))
    {
        return (id, false);
    }

    id = subject.GetOrAddSubjectId();
    _subjectToId[subject] = id;
    return (id, true);
}
```

Add a `bool includeRoot` parameter to the `Build` method and update it:
```csharp
public SubjectUpdate Build(IInterceptorSubject subject, bool includeRoot = true)
{
    ApplyTransformations();

    var update = new SubjectUpdate
    {
        Root = includeRoot ? GetOrCreateId(subject) : null,
        Subjects = Subjects
    };

    for (var i = 0; i < Processors.Length; i++)
    {
        update = Processors[i].TransformSubjectUpdate(subject, update);
    }

    return update;
}
```

Remove `PathVisited` property (line 21):
```csharp
// DELETE: public HashSet<IInterceptorSubject> PathVisited { get; } = [];
```

Update `Clear` method — remove `_nextId = 0;` and `PathVisited.Clear();`:
```csharp
public void Clear()
{
    _subjectToId.Clear();
    _propertyUpdates.Clear();
    ProcessedSubjects.Clear();
    Subjects = new();
    Processors = [];
}
```

**Step 2: Commit**

```bash
git add src/Namotion.Interceptor.Connectors/Updates/Internal/SubjectUpdateBuilder.cs
git commit -m "feat: use stable subject IDs in SubjectUpdateBuilder"
```

---

### Task 6: SubjectUpdateFactory — Remove BuildPathToRoot

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors/Updates/Internal/SubjectUpdateFactory.cs`

**Step 1: Update CreatePartialUpdateFromChanges to build without root**

In `src/Namotion.Interceptor.Connectors/Updates/Internal/SubjectUpdateFactory.cs`, change line 58 from:
```csharp
return builder.Build(rootSubject);
```
to:
```csharp
return builder.Build(rootSubject, includeRoot: false);
```

**Step 2: Remove BuildPathToRoot call from ProcessPropertyChange**

In the `ProcessPropertyChange` method (line 132), delete:
```csharp
BuildPathToRoot(changedSubject, rootSubject, builder);
```

Since `rootSubject` parameter is no longer used in `ProcessPropertyChange`, also remove the `IInterceptorSubject rootSubject` parameter from the method signature (line 98-100). Change from:
```csharp
private static void ProcessPropertyChange(
    SubjectPropertyChange change,
    IInterceptorSubject rootSubject,
    SubjectUpdateBuilder builder)
```
to:
```csharp
private static void ProcessPropertyChange(
    SubjectPropertyChange change,
    SubjectUpdateBuilder builder)
```

Update the call site in `CreatePartialUpdateFromChanges` (line 55) from:
```csharp
ProcessPropertyChange(propertyChanges[i], rootSubject, builder);
```
to:
```csharp
ProcessPropertyChange(propertyChanges[i], builder);
```

**Step 3: Delete BuildPathToRoot and related methods**

Delete these entire methods:
- `BuildPathToRoot` (lines 265-303)
- `AddCollectionOrDictionaryItemToParent` (lines 309-333)
- `AddSingleReferenceToParent` (lines 339-352)

**Step 4: Commit**

```bash
git add src/Namotion.Interceptor.Connectors/Updates/Internal/SubjectUpdateFactory.cs
git commit -m "feat: remove BuildPathToRoot, simplify partial updates"
```

---

### Task 7: CollectionDiffBuilder — ID-Based Output

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors/Updates/Internal/CollectionDiffBuilder.cs`

**Step 1: Change GetCollectionChanges output signature and implementation**

Replace the entire `GetCollectionChanges` method (lines 27-129) with:

```csharp
/// <summary>
/// Builds collection change operations using subject references (not indices).
/// The caller converts references to stable IDs.
/// </summary>
/// <param name="oldItems">The old collection items.</param>
/// <param name="newItems">The new collection items.</param>
/// <param name="removedItems">Output: items removed from old that are not in new.</param>
/// <param name="insertedItems">Output: new items with their predecessor in the new collection (null = head).</param>
/// <param name="movedItems">Output: items present in both but at different relative positions, with new predecessor.</param>
public void GetCollectionChanges(
    IReadOnlyList<IInterceptorSubject> oldItems,
    IReadOnlyList<IInterceptorSubject> newItems,
    out List<IInterceptorSubject>? removedItems,
    out List<(IInterceptorSubject item, IInterceptorSubject? afterItem)>? insertedItems,
    out List<(IInterceptorSubject item, IInterceptorSubject? afterItem)>? movedItems)
{
    removedItems = null;
    insertedItems = null;
    movedItems = null;

    // Build index maps
    _oldIndexMap.Clear();
    _newIndexMap.Clear();
    for (var i = 0; i < oldItems.Count; i++)
        _oldIndexMap[oldItems[i]] = i;
    for (var i = 0; i < newItems.Count; i++)
        _newIndexMap[newItems[i]] = i;

    // Removed items: in old but not in new
    for (var i = 0; i < oldItems.Count; i++)
    {
        var item = oldItems[i];
        if (!_newIndexMap.ContainsKey(item))
        {
            removedItems ??= [];
            removedItems.Add(item);
        }
    }

    // Inserted items: in new but not in old, with predecessor from new list
    for (var i = 0; i < newItems.Count; i++)
    {
        var item = newItems[i];
        if (!_oldIndexMap.ContainsKey(item))
        {
            var afterItem = i > 0 ? newItems[i - 1] : null;
            insertedItems ??= [];
            insertedItems.Add((item, afterItem));
        }
    }

    // Detect reordering among common items
    _oldCommonOrder.Clear();
    _newCommonOrder.Clear();
    for (var i = 0; i < oldItems.Count; i++)
    {
        if (_newIndexMap.ContainsKey(oldItems[i]))
            _oldCommonOrder.Add(oldItems[i]);
    }
    for (var i = 0; i < newItems.Count; i++)
    {
        if (_oldIndexMap.ContainsKey(newItems[i]))
            _newCommonOrder.Add(newItems[i]);
    }

    if (_oldCommonOrder.Count > 0 && !_oldCommonOrder.SequenceEqual(_newCommonOrder))
    {
        _oldCommonIndexMap.Clear();
        for (var i = 0; i < _oldCommonOrder.Count; i++)
            _oldCommonIndexMap[_oldCommonOrder[i]] = i;

        for (var i = 0; i < _newCommonOrder.Count; i++)
        {
            var item = _newCommonOrder[i];
            var oldCommonIndex = _oldCommonIndexMap[item];
            if (oldCommonIndex != i)
            {
                // Find predecessor in full new list
                var newIndex = _newIndexMap[item];
                var afterItem = newIndex > 0 ? newItems[newIndex - 1] : null;
                movedItems ??= [];
                movedItems.Add((item, afterItem));
            }
        }
    }
}
```

**Step 2: Remove CountRemovesBefore helper**

Delete the `CountRemovesBefore` method (lines 134-146) — no longer needed.

**Step 3: Remove GetNewIndex method**

Delete the `GetNewIndex` method (lines 219-220) — no longer needed since we don't track positional indices.

**Step 4: Commit**

```bash
git add src/Namotion.Interceptor.Connectors/Updates/Internal/CollectionDiffBuilder.cs
git commit -m "feat: change CollectionDiffBuilder to reference-based output"
```

---

### Task 8: SubjectItemsUpdateFactory — Stable IDs in Operations

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors/Updates/Internal/SubjectItemsUpdateFactory.cs`

**Step 1: Update BuildCollectionComplete**

Replace the `BuildCollectionComplete` method (lines 17-43) with:

```csharp
/// <summary>
/// Builds a complete collection update with all items.
/// Items array order defines collection ordering.
/// </summary>
internal static void BuildCollectionComplete(
    SubjectPropertyUpdate update,
    IEnumerable<IInterceptorSubject>? collection,
    SubjectUpdateBuilder builder)
{
    update.Kind = SubjectPropertyUpdateKind.Collection;

    if (collection is null)
        return;

    var items = collection as IReadOnlyList<IInterceptorSubject> ?? collection.ToList();
    update.Count = items.Count;
    update.Items = new List<SubjectPropertyItemUpdate>(items.Count);

    for (var i = 0; i < items.Count; i++)
    {
        var item = items[i];
        var itemId = builder.GetOrCreateId(item);
        SubjectUpdateFactory.ProcessSubjectComplete(item, builder);

        update.Items.Add(new SubjectPropertyItemUpdate
        {
            Id = itemId
        });
    }
}
```

**Step 2: Update BuildCollectionDiff**

Replace the `BuildCollectionDiff` method (lines 48-130) with:

```csharp
/// <summary>
/// Builds a diff collection update with ID-based Insert, Remove, Move operations
/// and sparse property updates for common items.
/// </summary>
internal static void BuildCollectionDiff(
    SubjectPropertyUpdate update,
    IEnumerable<IInterceptorSubject>? oldCollection,
    IEnumerable<IInterceptorSubject>? newCollection,
    SubjectUpdateBuilder builder)
{
    update.Kind = SubjectPropertyUpdateKind.Collection;

    if (newCollection is null)
        return;

    var oldItems = oldCollection as IReadOnlyList<IInterceptorSubject> ?? oldCollection?.ToList() ?? [];
    var newItems = newCollection as IReadOnlyList<IInterceptorSubject> ?? newCollection.ToList();
    update.Count = newItems.Count;

    var changeBuilder = ChangeBuilderPool.Rent();
    try
    {
        changeBuilder.GetCollectionChanges(
            oldItems, newItems,
            out var removedItems,
            out var insertedItems,
            out var movedItems);

        List<SubjectCollectionOperation>? operations = null;

        // Add Remove operations
        if (removedItems is not null)
        {
            foreach (var item in removedItems)
            {
                operations ??= [];
                operations.Add(new SubjectCollectionOperation
                {
                    Action = SubjectCollectionOperationType.Remove,
                    Id = builder.GetOrCreateId(item)
                });
            }
        }

        // Add Insert operations with afterId
        if (insertedItems is not null)
        {
            foreach (var (item, afterItem) in insertedItems)
            {
                var itemId = builder.GetOrCreateId(item);
                SubjectUpdateFactory.ProcessSubjectComplete(item, builder);

                operations ??= [];
                operations.Add(new SubjectCollectionOperation
                {
                    Action = SubjectCollectionOperationType.Insert,
                    Id = itemId,
                    AfterId = afterItem is not null ? builder.GetOrCreateId(afterItem) : null
                });
            }
        }

        // Add Move operations with afterId
        if (movedItems is not null)
        {
            foreach (var (item, afterItem) in movedItems)
            {
                operations ??= [];
                operations.Add(new SubjectCollectionOperation
                {
                    Action = SubjectCollectionOperationType.Move,
                    Id = builder.GetOrCreateId(item),
                    AfterId = afterItem is not null ? builder.GetOrCreateId(afterItem) : null
                });
            }
        }

        // Generate sparse updates for common items with property changes
        List<SubjectPropertyItemUpdate>? updates = null;
        foreach (var item in changeBuilder.GetCommonItems())
        {
            if (builder.SubjectHasUpdates(item))
            {
                var itemId = builder.GetOrCreateId(item);
                updates ??= [];
                updates.Add(new SubjectPropertyItemUpdate
                {
                    Id = itemId
                });
            }
        }

        update.Operations = operations;
        update.Items = updates;
    }
    finally
    {
        changeBuilder.Clear();
        ChangeBuilderPool.Return(changeBuilder);
    }
}
```

**Step 3: Update BuildDictionaryComplete**

Replace the `BuildDictionaryComplete` method (lines 135-162) with:

```csharp
/// <summary>
/// Builds a complete dictionary update with all entries.
/// Each item includes id + key.
/// </summary>
internal static void BuildDictionaryComplete(
    SubjectPropertyUpdate update,
    IDictionary? dictionary,
    SubjectUpdateBuilder builder)
{
    update.Kind = SubjectPropertyUpdateKind.Dictionary;

    if (dictionary is null)
        return;

    update.Count = dictionary.Count;
    update.Items = new List<SubjectPropertyItemUpdate>(dictionary.Count);

    foreach (DictionaryEntry entry in dictionary)
    {
        if (entry.Value is IInterceptorSubject item)
        {
            var itemId = builder.GetOrCreateId(item);
            SubjectUpdateFactory.ProcessSubjectComplete(item, builder);

            update.Items.Add(new SubjectPropertyItemUpdate
            {
                Id = itemId,
                Key = entry.Key.ToString()
            });
        }
    }
}
```

**Step 4: Update BuildDictionaryDiff**

Replace the `BuildDictionaryDiff` method (lines 167-263) with:

```csharp
/// <summary>
/// Builds a diff dictionary update with ID-based Insert, Remove operations
/// and sparse property updates for existing items.
/// </summary>
internal static void BuildDictionaryDiff(
    SubjectPropertyUpdate update,
    IDictionary? oldDict,
    IDictionary? newDict,
    SubjectUpdateBuilder builder)
{
    update.Kind = SubjectPropertyUpdateKind.Dictionary;

    if (newDict is null)
        return;

    update.Count = newDict.Count;

    var changeBuilder = ChangeBuilderPool.Rent();
    try
    {
        changeBuilder.GetDictionaryChanges(
            oldDict, newDict,
            out _,
            out var newItemsToProcess,
            out var removedKeys);

        List<SubjectCollectionOperation>? operations = null;

        // Add Remove operations for removed keys
        if (removedKeys is not null && oldDict is not null)
        {
            foreach (var removedKey in removedKeys)
            {
                if (oldDict[removedKey] is IInterceptorSubject oldItem)
                {
                    operations ??= [];
                    operations.Add(new SubjectCollectionOperation
                    {
                        Action = SubjectCollectionOperationType.Remove,
                        Id = builder.GetOrCreateId(oldItem),
                        Key = removedKey.ToString()
                    });
                }
            }
        }

        // Add Insert operations for new items
        if (newItemsToProcess is not null)
        {
            foreach (var (key, item) in newItemsToProcess)
            {
                var itemId = builder.GetOrCreateId(item);
                SubjectUpdateFactory.ProcessSubjectComplete(item, builder);

                operations ??= [];
                operations.Add(new SubjectCollectionOperation
                {
                    Action = SubjectCollectionOperationType.Insert,
                    Id = itemId,
                    Key = key.ToString()
                });
            }
        }

        // Check for property updates on existing items
        HashSet<object>? newKeysSet = null;
        if (newItemsToProcess is not null)
        {
            newKeysSet = new HashSet<object>(newItemsToProcess.Count);
            foreach (var (key, _) in newItemsToProcess)
                newKeysSet.Add(key);
        }

        List<SubjectPropertyItemUpdate>? updates = null;
        foreach (DictionaryEntry entry in newDict)
        {
            var key = entry.Key;
            if (entry.Value is not IInterceptorSubject item)
                continue;

            // Skip if this is a new item (already handled above)
            if (newKeysSet?.Contains(key) == true)
                continue;

            if (builder.SubjectHasUpdates(item))
            {
                var itemId = builder.GetOrCreateId(item);
                updates ??= [];
                updates.Add(new SubjectPropertyItemUpdate
                {
                    Id = itemId,
                    Key = key.ToString()
                });
            }
        }

        update.Operations = operations;
        update.Items = updates;
    }
    finally
    {
        changeBuilder.Clear();
        ChangeBuilderPool.Return(changeBuilder);
    }
}
```

**Step 5: Commit**

```bash
git add src/Namotion.Interceptor.Connectors/Updates/Internal/SubjectItemsUpdateFactory.cs
git commit -m "feat: use stable IDs in SubjectItemsUpdateFactory operations"
```

---

## Phase 4: Apply Side (Receiving Updates)

### Task 9: SubjectItemsUpdateApplier — ID-Based Apply

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors/Updates/Internal/SubjectItemsUpdateApplier.cs`

**Step 1: Add registry import**

Add `using Namotion.Interceptor.Registry;` and `using Namotion.Interceptor.Registry.Abstractions;` to the top of `src/Namotion.Interceptor.Connectors/Updates/Internal/SubjectItemsUpdateApplier.cs`.

**Step 2: Replace ApplyCollectionUpdate**

Replace the entire `ApplyCollectionUpdate` method (lines 16-137) with:

```csharp
/// <summary>
/// Applies a collection update to a property using stable ID-based operations.
/// </summary>
internal static void ApplyCollectionUpdate(
    IInterceptorSubject parent,
    RegisteredSubjectProperty property,
    SubjectPropertyUpdate propertyUpdate,
    SubjectUpdateApplyContext context)
{
    var workingItems = (property.GetValue() as IEnumerable<IInterceptorSubject>)?.ToList() ?? [];
    var structureChanged = false;

    // Apply structural operations (ID-based)
    if (propertyUpdate.Operations is { Count: > 0 })
    {
        foreach (var operation in propertyUpdate.Operations)
        {
            switch (operation.Action)
            {
                case SubjectCollectionOperationType.Remove:
                {
                    var index = FindItemIndexById(workingItems, operation.Id);
                    if (index >= 0)
                    {
                        workingItems.RemoveAt(index);
                        structureChanged = true;
                    }
                    break;
                }

                case SubjectCollectionOperationType.Insert:
                {
                    IInterceptorSubject? newItem = null;

                    // Try to reuse an existing subject by stable ID
                    var registry = parent.Context.TryGetService<ISubjectRegistry>();
                    if (registry is not null && registry.TryGetSubjectByStableId(operation.Id, out var existing))
                    {
                        newItem = existing;
                    }

                    if (newItem is null && context.Subjects.TryGetValue(operation.Id, out var itemProps))
                    {
                        newItem = CreateAndApplyItem(parent, property, 0, operation.Id, itemProps, context);
                    }

                    if (newItem is null)
                        break;

                    // Apply properties if available and not yet processed
                    if (context.Subjects.TryGetValue(operation.Id, out var props) && context.TryMarkAsProcessed(operation.Id))
                    {
                        SubjectUpdateApplier.ApplyPropertyUpdates(newItem, props, context);
                    }

                    var insertIndex = FindInsertPosition(workingItems, operation.AfterId);
                    workingItems.Insert(insertIndex, newItem);
                    structureChanged = true;
                    break;
                }

                case SubjectCollectionOperationType.Move:
                {
                    var currentIndex = FindItemIndexById(workingItems, operation.Id);
                    if (currentIndex >= 0)
                    {
                        var item = workingItems[currentIndex];
                        workingItems.RemoveAt(currentIndex);
                        var insertIndex = FindInsertPosition(workingItems, operation.AfterId);
                        workingItems.Insert(insertIndex, item);
                        structureChanged = true;
                    }
                    break;
                }
            }
        }
    }

    // Complete collection from items (when no operations = complete state)
    if (propertyUpdate.Operations is null && propertyUpdate.Items is { Count: > 0 })
    {
        var newItems = new List<IInterceptorSubject>(propertyUpdate.Items.Count);
        foreach (var itemUpdate in propertyUpdate.Items)
        {
            IInterceptorSubject? item = null;

            // Try to reuse existing subject by stable ID
            var registry = parent.Context.TryGetService<ISubjectRegistry>();
            if (registry is not null && registry.TryGetSubjectByStableId(itemUpdate.Id, out var existing))
            {
                item = existing;
            }

            if (item is null && context.Subjects.TryGetValue(itemUpdate.Id, out var itemProps))
            {
                item = CreateAndApplyItem(parent, property, newItems.Count, itemUpdate.Id, itemProps, context);
            }

            if (item is not null)
            {
                if (context.Subjects.TryGetValue(itemUpdate.Id, out var props) && context.TryMarkAsProcessed(itemUpdate.Id))
                {
                    SubjectUpdateApplier.ApplyPropertyUpdates(item, props, context);
                }
                newItems.Add(item);
            }
        }

        workingItems = newItems;
        structureChanged = true;
    }

    // Sparse property updates (items with operations present — update existing items by ID)
    if (propertyUpdate.Operations is not null && propertyUpdate.Items is { Count: > 0 })
    {
        foreach (var itemUpdate in propertyUpdate.Items)
        {
            if (context.Subjects.TryGetValue(itemUpdate.Id, out var itemProps))
            {
                var item = FindItemById(workingItems, itemUpdate.Id);
                if (item is not null && context.TryMarkAsProcessed(itemUpdate.Id))
                {
                    SubjectUpdateApplier.ApplyPropertyUpdates(item, itemProps, context);
                }
            }
        }
    }

    // Trim excess items when update declares a smaller count
    if (propertyUpdate.Count.HasValue && workingItems.Count > propertyUpdate.Count.Value)
    {
        workingItems.RemoveRange(propertyUpdate.Count.Value, workingItems.Count - propertyUpdate.Count.Value);
        structureChanged = true;
    }

    if (structureChanged)
    {
        var collection = context.SubjectFactory.CreateSubjectCollection(property.Type, workingItems);
        using (SubjectChangeContext.WithChangedTimestamp(propertyUpdate.Timestamp))
        {
            property.SetValue(collection);
        }
    }
}
```

**Step 3: Replace ApplyDictionaryUpdate**

Replace the entire `ApplyDictionaryUpdate` method (lines 142-252) with:

```csharp
/// <summary>
/// Applies a dictionary update to a property using stable ID-based operations.
/// </summary>
internal static void ApplyDictionaryUpdate(
    IInterceptorSubject parent,
    RegisteredSubjectProperty property,
    SubjectPropertyUpdate propertyUpdate,
    SubjectUpdateApplyContext context)
{
    var existingDictionary = property.GetValue() as IDictionary;
    var targetKeyType = property.Type.GenericTypeArguments[0];
    var workingDictionary = new Dictionary<object, IInterceptorSubject>();
    var structureChanged = false;

    if (existingDictionary is not null)
    {
        foreach (DictionaryEntry entry in existingDictionary)
        {
            if (entry.Value is IInterceptorSubject item)
                workingDictionary[entry.Key] = item;
        }
    }

    // Apply structural operations
    if (propertyUpdate.Operations is { Count: > 0 })
    {
        foreach (var operation in propertyUpdate.Operations)
        {
            switch (operation.Action)
            {
                case SubjectCollectionOperationType.Remove:
                    if (operation.Key is not null)
                    {
                        var key = ConvertDictionaryKey(operation.Key, targetKeyType);
                        if (workingDictionary.Remove(key))
                            structureChanged = true;
                    }
                    break;

                case SubjectCollectionOperationType.Insert:
                    if (operation.Key is not null && context.Subjects.TryGetValue(operation.Id, out var itemProps))
                    {
                        var key = ConvertDictionaryKey(operation.Key, targetKeyType);
                        IInterceptorSubject newItem;

                        // Try to reuse by stable ID
                        var registry = parent.Context.TryGetService<ISubjectRegistry>();
                        if (registry is not null && registry.TryGetSubjectByStableId(operation.Id, out var existing))
                        {
                            newItem = existing;
                            if (context.TryMarkAsProcessed(operation.Id))
                            {
                                SubjectUpdateApplier.ApplyPropertyUpdates(newItem, itemProps, context);
                            }
                        }
                        else
                        {
                            newItem = CreateAndApplyItem(parent, property, key, operation.Id, itemProps, context);
                        }

                        workingDictionary[key] = newItem;
                        structureChanged = true;
                    }
                    break;
            }
        }
    }

    // Apply sparse property updates
    if (propertyUpdate.Items is { Count: > 0 })
    {
        foreach (var collUpdate in propertyUpdate.Items)
        {
            if (collUpdate.Key is null)
                continue;

            var key = ConvertDictionaryKey(collUpdate.Key, targetKeyType);

            if (context.Subjects.TryGetValue(collUpdate.Id, out var itemProps))
            {
                if (workingDictionary.TryGetValue(key, out var existing))
                {
                    if (context.TryMarkAsProcessed(collUpdate.Id))
                    {
                        SubjectUpdateApplier.ApplyPropertyUpdates(existing, itemProps, context);
                    }
                }
                else
                {
                    // Try reuse by stable ID
                    var registry = parent.Context.TryGetService<ISubjectRegistry>();
                    IInterceptorSubject newItem;
                    if (registry is not null && registry.TryGetSubjectByStableId(collUpdate.Id, out var existingSubject))
                    {
                        newItem = existingSubject;
                        if (context.TryMarkAsProcessed(collUpdate.Id))
                        {
                            SubjectUpdateApplier.ApplyPropertyUpdates(newItem, itemProps, context);
                        }
                    }
                    else
                    {
                        newItem = CreateAndApplyItem(parent, property, key, collUpdate.Id, itemProps, context);
                    }
                    workingDictionary[key] = newItem;
                    structureChanged = true;
                }
            }
        }
    }

    // Remove dictionary entries not mentioned in update when count doesn't match
    if (propertyUpdate.Count.HasValue && workingDictionary.Count != propertyUpdate.Count.Value)
    {
        var updatedKeys = new HashSet<object>();
        if (propertyUpdate.Operations is { Count: > 0 })
        {
            foreach (var operation in propertyUpdate.Operations)
            {
                if (operation is { Action: SubjectCollectionOperationType.Insert, Key: not null })
                    updatedKeys.Add(ConvertDictionaryKey(operation.Key, targetKeyType));
            }
        }

        if (propertyUpdate.Items is { Count: > 0 })
        {
            foreach (var item in propertyUpdate.Items)
            {
                if (item.Key is not null)
                    updatedKeys.Add(ConvertDictionaryKey(item.Key, targetKeyType));
            }
        }

        var keysToRemove = workingDictionary.Keys.Where(k => !updatedKeys.Contains(k)).ToList();
        foreach (var key in keysToRemove)
            workingDictionary.Remove(key);

        if (keysToRemove.Count > 0)
            structureChanged = true;
    }

    if (structureChanged)
    {
        var dictionary = context.SubjectFactory.CreateSubjectDictionary(property.Type, workingDictionary);
        using (SubjectChangeContext.WithChangedTimestamp(propertyUpdate.Timestamp))
        {
            property.SetValue(dictionary);
        }
    }
}
```

**Step 4: Replace helper methods**

Delete `ConvertIndexToInt` (no longer needed for collection operations).

Add these new helper methods (replacing old ones as needed):

```csharp
private static int FindItemIndexById(List<IInterceptorSubject> items, string stableId)
{
    for (var i = 0; i < items.Count; i++)
    {
        if (items[i].GetOrAddSubjectId() == stableId)
            return i;
    }
    return -1;
}

private static IInterceptorSubject? FindItemById(List<IInterceptorSubject> items, string stableId)
{
    for (var i = 0; i < items.Count; i++)
    {
        if (items[i].GetOrAddSubjectId() == stableId)
            return items[i];
    }
    return null;
}

private static int FindInsertPosition(List<IInterceptorSubject> items, string? afterId)
{
    if (afterId is null)
        return 0; // Insert at head

    for (var i = 0; i < items.Count; i++)
    {
        if (items[i].GetOrAddSubjectId() == afterId)
            return i + 1; // Insert after this item
    }

    return items.Count; // afterId not found, append to end
}
```

Keep `ConvertDictionaryKey` as-is (still needed for dictionaries).

**Step 5: Update CreateAndApplyItem to call SetSubjectId**

Replace `CreateAndApplyItem` with:

```csharp
private static IInterceptorSubject CreateAndApplyItem(
    IInterceptorSubject parent,
    RegisteredSubjectProperty property,
    object indexOrKey,
    string subjectId,
    Dictionary<string, SubjectPropertyUpdate> properties,
    SubjectUpdateApplyContext context)
{
    var newItem = context.SubjectFactory.CreateCollectionSubject(property, indexOrKey);
    newItem.Context.AddFallbackContext(parent.Context);
    newItem.SetSubjectId(subjectId);
    if (context.TryMarkAsProcessed(subjectId))
    {
        SubjectUpdateApplier.ApplyPropertyUpdates(newItem, properties, context);
    }
    return newItem;
}
```

**Step 6: Commit**

```bash
git add src/Namotion.Interceptor.Connectors/Updates/Internal/SubjectItemsUpdateApplier.cs
git commit -m "feat: rewrite SubjectItemsUpdateApplier for ID-based operations"
```

---

### Task 10: SubjectUpdateApplier — Handle Nullable Root + SetSubjectId on Object Create

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors/Updates/Internal/SubjectUpdateApplier.cs`

**Step 1: Add registry import**

Add `using Namotion.Interceptor.Registry;` and `using Namotion.Interceptor.Registry.Abstractions;` to the top of the file.

**Step 2: Update ApplyUpdate to handle nullable Root**

Replace the `ApplyUpdate` method (lines 15-39) with:

```csharp
public static void ApplyUpdate(
    IInterceptorSubject subject,
    SubjectUpdate update,
    ISubjectFactory subjectFactory,
    Action<RegisteredSubjectProperty, SubjectPropertyUpdate>? transformValueBeforeApply = null)
{
    var context = ContextPool.Rent();
    try
    {
        context.Initialize(update.Subjects, subjectFactory, transformValueBeforeApply);

        if (update.Root is not null)
        {
            // Complete update or rooted partial update — apply from root
            if (!update.Subjects.TryGetValue(update.Root, out var rootProperties))
                return;

            context.TryMarkAsProcessed(update.Root);
            ApplyPropertyUpdates(subject, rootProperties, context);
        }
        else
        {
            // Partial update without root — apply each subject by stable ID lookup
            var registry = subject.Context.TryGetService<ISubjectRegistry>();
            if (registry is null)
                return;

            foreach (var (subjectId, properties) in update.Subjects)
            {
                if (!context.TryMarkAsProcessed(subjectId))
                    continue;

                if (registry.TryGetSubjectByStableId(subjectId, out var targetSubject))
                {
                    ApplyPropertyUpdates(targetSubject, properties, context);
                }
            }
        }
    }
    finally
    {
        context.Clear();
        ContextPool.Return(context);
    }
}
```

**Step 3: Add SetSubjectId call when creating new object references**

In the `ApplyObjectUpdate` method (lines 105-144), when creating a new item (the `else` branch, around line 122), add `newItem.SetSubjectId(propertyUpdate.Id);` after `newItem.Context.AddFallbackContext(parent.Context);`:

```csharp
else
{
    var newItem = context.SubjectFactory.CreateSubject(property);
    newItem.Context.AddFallbackContext(parent.Context);
    newItem.SetSubjectId(propertyUpdate.Id);

    if (context.TryMarkAsProcessed(propertyUpdate.Id))
    {
        ApplyPropertyUpdates(newItem, itemProperties, context);
    }

    using (SubjectChangeContext.WithChangedTimestamp(propertyUpdate.Timestamp))
    {
        property.SetValue(newItem);
    }
}
```

**Step 4: Commit**

```bash
git add src/Namotion.Interceptor.Connectors/Updates/Internal/SubjectUpdateApplier.cs
git commit -m "feat: handle partial updates without root + SetSubjectId on object create"
```

---

## Phase 5: Fix Compilation + Tests

### Task 11: Fix Compilation Errors

**Files:**
- Various source and test files with compilation errors from DTO changes

**Step 1: Build the entire solution**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: Compilation errors in test files referencing old `Index`, `FromIndex` properties

**Step 2: Fix compilation errors systematically**

Search for all uses of the removed/renamed fields and update them:

1. **`operation.Index`** → `operation.Id` (for collection ops) or `operation.Key` (for dictionary ops)
2. **`FromIndex`** → delete entirely (no replacement needed)
3. **`collectionUpdate.Index`** → `collectionUpdate.Id` (for collections) or `collectionUpdate.Key` (for dicts)
4. **`new SubjectCollectionOperation { Index = ... }`** → `new SubjectCollectionOperation { Id = ... }` with appropriate `Key`/`AfterId`
5. **`new SubjectPropertyItemUpdate { Index = ... }`** → `new SubjectPropertyItemUpdate { Id = ... }` with optional `Key`
6. **`update.Root` (string) comparisons** → handle `string?` (nullable)
7. **`string.IsNullOrEmpty(update.Root)`** → `update.Root is null` (where applicable)

Key files that need test fixes:
- `src/Namotion.Interceptor.Connectors.Tests/Updates/SubjectUpdateCollectionTests.cs`
- `src/Namotion.Interceptor.Connectors.Tests/Updates/SubjectUpdateDictionaryTests.cs`
- `src/Namotion.Interceptor.Connectors.Tests/Updates/SubjectUpdateExtensionsTests.cs`
- `src/Namotion.Interceptor.Connectors.Tests/Updates/SubjectUpdateTests.cs`
- `src/Namotion.Interceptor.Connectors.Tests/Updates/SubjectUpdateCycleTests.cs`
- `src/Namotion.Interceptor.WebSocket.Tests/Serialization/SubjectUpdateFlowTests.cs`

For tests that manually construct `SubjectCollectionOperation`:
- `Index = someInt` → `Id = "someStableId"` (generate or hardcode test IDs)
- `FromIndex = someInt` → delete
- Add `AfterId = "predecessorId"` or `Key = "dictKey"` as appropriate

For tests that manually construct `SubjectPropertyItemUpdate`:
- `Index = someInt` → `Id = "someStableId"`
- Add `Key = "dictKey"` for dictionary items

For tests referencing `update.Root`:
- Check if `Root` can now be null (partial updates)
- Replace `Assert.Equal("1", update.Root)` with assertions that `Root` is a 22-char base62 string or null

**Step 3: Build again**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: Clean build (0 errors, warnings OK)

**Step 4: Commit**

```bash
git add -A
git commit -m "fix: resolve all compilation errors from stable ID wire format changes"
```

---

### Task 12: Update Snapshot Tests + Write New Integration Tests

**Files:**
- Delete + regenerate: All `*.verified.*` files in test directories
- Create: `src/Namotion.Interceptor.Connectors.Tests/Updates/StableIdCollectionTests.cs`
- Create: `src/Namotion.Interceptor.Connectors.Tests/Updates/StableIdApplyTests.cs`

**Step 1: Delete all verified snapshot files**

```bash
find src -name "*.verified.*" -delete
```

**Step 2: Run all tests**

Run: `dotnet test src/Namotion.Interceptor.slnx`
Expected: Many tests fail — Verify library can't find snapshot files, creates `.received.*` files instead

**Step 3: Accept all received snapshots as new verified baselines**

```bash
find src -name "*.received.*" -exec sh -c 'mv "$1" "${1/received/verified}"' _ {} \;
```

**Step 4: Run tests again to confirm all Verify tests pass**

Run: `dotnet test src/Namotion.Interceptor.slnx`
Expected: All existing tests pass (some may still fail — fix those individually)

**Step 5: Review key snapshots for correctness**

Manually verify in regenerated `.verified.txt`/`.verified.json` files:
- Subject keys are 22-char base62 strings (not "1", "2", "3")
- Collection operations use `id` and `afterId` (not `index` and `fromIndex`)
- Dictionary items use `id` + `key` (not `index`)
- Partial updates don't include intermediate path subjects

**Step 6: Write new test file for stable ID collection operations**

Create `src/Namotion.Interceptor.Connectors.Tests/Updates/StableIdCollectionTests.cs`:

```csharp
using System.Reactive.Concurrency;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests.Updates;

public class StableIdCollectionTests
{
    [Fact]
    public void CompleteUpdate_UsesStableBase62Ids()
    {
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var node = new CycleTestNode(context) { Name = "Root" };

        var update = SubjectUpdate.CreateCompleteUpdate((IInterceptorSubject)node, []);

        Assert.NotNull(update.Root);
        Assert.Equal(22, update.Root.Length);
        Assert.All(update.Root.ToCharArray(), c => Assert.True(char.IsLetterOrDigit(c)));
    }

    [Fact]
    public void CompleteUpdate_SameSubjectGetsSameIdAcrossUpdates()
    {
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var node = new CycleTestNode(context) { Name = "Root" };

        var update1 = SubjectUpdate.CreateCompleteUpdate((IInterceptorSubject)node, []);
        var update2 = SubjectUpdate.CreateCompleteUpdate((IInterceptorSubject)node, []);

        Assert.Equal(update1.Root, update2.Root);
    }

    [Fact]
    public void CollectionInsert_UsesAfterIdOfPredecessor()
    {
        var context = InterceptorSubjectContext.Create().WithPropertyChangeObservable().WithRegistry();
        var child1 = new CycleTestNode { Name = "Child1" };
        var node = new CycleTestNode(context) { Name = "Root", Items = [child1] };

        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(c => changes.Add(c));

        var child2 = new CycleTestNode { Name = "Child2" };
        node.Items = [child1, child2];

        var update = SubjectUpdate.CreatePartialUpdateFromChanges(
            (IInterceptorSubject)node, changes.ToArray(), []);

        // Find the Items property update on the root subject
        var rootId = ((IInterceptorSubject)node).GetOrAddSubjectId();
        Assert.True(update.Subjects.ContainsKey(rootId));
        var itemsUpdate = update.Subjects[rootId]["Items"];
        Assert.NotNull(itemsUpdate.Operations);

        var insertOp = itemsUpdate.Operations.First(o => o.Action == SubjectCollectionOperationType.Insert);
        Assert.Equal(22, insertOp.Id.Length);
        // afterId should be child1's stable ID (insert after child1)
        Assert.Equal(((IInterceptorSubject)child1).GetOrAddSubjectId(), insertOp.AfterId);
    }

    [Fact]
    public void CollectionRemove_UsesStableIdOfRemovedItem()
    {
        var context = InterceptorSubjectContext.Create().WithPropertyChangeObservable().WithRegistry();
        var child1 = new CycleTestNode { Name = "Child1" };
        var child2 = new CycleTestNode { Name = "Child2" };
        var node = new CycleTestNode(context) { Name = "Root", Items = [child1, child2] };

        var child2Id = ((IInterceptorSubject)child2).GetOrAddSubjectId();

        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(c => changes.Add(c));

        node.Items = [child1];

        var update = SubjectUpdate.CreatePartialUpdateFromChanges(
            (IInterceptorSubject)node, changes.ToArray(), []);

        var rootId = ((IInterceptorSubject)node).GetOrAddSubjectId();
        var itemsUpdate = update.Subjects[rootId]["Items"];
        Assert.NotNull(itemsUpdate.Operations);

        var removeOp = itemsUpdate.Operations.First(o => o.Action == SubjectCollectionOperationType.Remove);
        Assert.Equal(child2Id, removeOp.Id);
    }

    [Fact]
    public void PartialValueUpdate_HasNoRoot()
    {
        var context = InterceptorSubjectContext.Create().WithPropertyChangeObservable().WithRegistry();
        var node = new CycleTestNode(context) { Name = "Root" };

        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(c => changes.Add(c));

        node.Name = "Updated";

        var update = SubjectUpdate.CreatePartialUpdateFromChanges(
            (IInterceptorSubject)node, changes.ToArray(), []);

        Assert.Null(update.Root);
    }

    [Fact]
    public void CollectionInsertAtHead_HasNullAfterId()
    {
        var context = InterceptorSubjectContext.Create().WithPropertyChangeObservable().WithRegistry();
        var child2 = new CycleTestNode { Name = "Child2" };
        var node = new CycleTestNode(context) { Name = "Root", Items = [child2] };

        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(c => changes.Add(c));

        var child1 = new CycleTestNode { Name = "Child1" };
        node.Items = [child1, child2];

        var update = SubjectUpdate.CreatePartialUpdateFromChanges(
            (IInterceptorSubject)node, changes.ToArray(), []);

        var rootId = ((IInterceptorSubject)node).GetOrAddSubjectId();
        var itemsUpdate = update.Subjects[rootId]["Items"];
        var insertOp = itemsUpdate.Operations!.First(o => o.Action == SubjectCollectionOperationType.Insert);

        Assert.Null(insertOp.AfterId); // inserted at head
    }
}
```

**Step 7: Write new test file for apply-side stable ID operations**

Create `src/Namotion.Interceptor.Connectors.Tests/Updates/StableIdApplyTests.cs`:

```csharp
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Connectors.Updates.Internal;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Connectors.Tests.Updates;

public class StableIdApplyTests
{
    [Fact]
    public void ApplyCollectionInsert_WithAfterId_InsertsAtCorrectPosition()
    {
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var child1 = new CycleTestNode { Name = "Child1" };
        var child2 = new CycleTestNode { Name = "Child2" };
        var node = new CycleTestNode(context) { Name = "Root", Items = [child1, child2] };

        var child1Id = ((IInterceptorSubject)child1).GetOrAddSubjectId();
        var rootId = ((IInterceptorSubject)node).GetOrAddSubjectId();
        var newChildId = SubjectRegistryExtensions.GenerateSubjectId();

        var update = new SubjectUpdate
        {
            Root = rootId,
            Subjects = new()
            {
                [rootId] = new()
                {
                    ["Items"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Collection,
                        Count = 3,
                        Operations =
                        [
                            new SubjectCollectionOperation
                            {
                                Action = SubjectCollectionOperationType.Insert,
                                Id = newChildId,
                                AfterId = child1Id
                            }
                        ]
                    }
                },
                [newChildId] = new()
                {
                    ["Name"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Value,
                        Value = "NewChild"
                    }
                }
            }
        };

        SubjectUpdateApplier.ApplyUpdate(
            (IInterceptorSubject)node, update, new DefaultSubjectFactory());

        Assert.Equal(3, node.Items.Count);
        Assert.Equal("Child1", node.Items[0].Name);
        Assert.Equal("NewChild", node.Items[1].Name);
        Assert.Equal("Child2", node.Items[2].Name);
    }

    [Fact]
    public void ApplyCollectionRemove_ByStableId_RemovesCorrectItem()
    {
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var child1 = new CycleTestNode { Name = "Child1" };
        var child2 = new CycleTestNode { Name = "Child2" };
        var child3 = new CycleTestNode { Name = "Child3" };
        var node = new CycleTestNode(context) { Name = "Root", Items = [child1, child2, child3] };

        var child2Id = ((IInterceptorSubject)child2).GetOrAddSubjectId();
        var rootId = ((IInterceptorSubject)node).GetOrAddSubjectId();

        var update = new SubjectUpdate
        {
            Root = rootId,
            Subjects = new()
            {
                [rootId] = new()
                {
                    ["Items"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Collection,
                        Count = 2,
                        Operations =
                        [
                            new SubjectCollectionOperation
                            {
                                Action = SubjectCollectionOperationType.Remove,
                                Id = child2Id
                            }
                        ]
                    }
                }
            }
        };

        SubjectUpdateApplier.ApplyUpdate(
            (IInterceptorSubject)node, update, new DefaultSubjectFactory());

        Assert.Equal(2, node.Items.Count);
        Assert.Equal("Child1", node.Items[0].Name);
        Assert.Equal("Child3", node.Items[1].Name);
    }

    [Fact]
    public void ApplyCollectionMove_ByStableId_ReordersCorrectly()
    {
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var child1 = new CycleTestNode { Name = "A" };
        var child2 = new CycleTestNode { Name = "B" };
        var child3 = new CycleTestNode { Name = "C" };
        var node = new CycleTestNode(context) { Name = "Root", Items = [child1, child2, child3] };

        var child3Id = ((IInterceptorSubject)child3).GetOrAddSubjectId();
        var rootId = ((IInterceptorSubject)node).GetOrAddSubjectId();

        // Move C to head (before A)
        var update = new SubjectUpdate
        {
            Root = rootId,
            Subjects = new()
            {
                [rootId] = new()
                {
                    ["Items"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Collection,
                        Count = 3,
                        Operations =
                        [
                            new SubjectCollectionOperation
                            {
                                Action = SubjectCollectionOperationType.Move,
                                Id = child3Id,
                                AfterId = null // place at head
                            }
                        ]
                    }
                }
            }
        };

        SubjectUpdateApplier.ApplyUpdate(
            (IInterceptorSubject)node, update, new DefaultSubjectFactory());

        Assert.Equal(3, node.Items.Count);
        Assert.Equal("C", node.Items[0].Name);
        Assert.Equal("A", node.Items[1].Name);
        Assert.Equal("B", node.Items[2].Name);
    }

    [Fact]
    public void ApplyPartialUpdate_WithoutRoot_AppliesByStableIdLookup()
    {
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var child = new CycleTestNode { Name = "OriginalName" };
        var node = new CycleTestNode(context) { Name = "Root", Items = [child] };

        var childId = ((IInterceptorSubject)child).GetOrAddSubjectId();

        var update = new SubjectUpdate
        {
            Root = null, // Partial update without root
            Subjects = new()
            {
                [childId] = new()
                {
                    ["Name"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Value,
                        Value = "UpdatedName"
                    }
                }
            }
        };

        SubjectUpdateApplier.ApplyUpdate(
            (IInterceptorSubject)node, update, new DefaultSubjectFactory());

        Assert.Equal("UpdatedName", child.Name);
    }

    [Fact]
    public void ApplyInsert_WithMissingAfterId_AppendsToEnd()
    {
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var child1 = new CycleTestNode { Name = "Child1" };
        var node = new CycleTestNode(context) { Name = "Root", Items = [child1] };

        var rootId = ((IInterceptorSubject)node).GetOrAddSubjectId();
        var newChildId = SubjectRegistryExtensions.GenerateSubjectId();

        var update = new SubjectUpdate
        {
            Root = rootId,
            Subjects = new()
            {
                [rootId] = new()
                {
                    ["Items"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Collection,
                        Count = 2,
                        Operations =
                        [
                            new SubjectCollectionOperation
                            {
                                Action = SubjectCollectionOperationType.Insert,
                                Id = newChildId,
                                AfterId = "nonexistent_id" // missing predecessor
                            }
                        ]
                    }
                },
                [newChildId] = new()
                {
                    ["Name"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Value,
                        Value = "NewChild"
                    }
                }
            }
        };

        SubjectUpdateApplier.ApplyUpdate(
            (IInterceptorSubject)node, update, new DefaultSubjectFactory());

        // Should append to end when afterId not found
        Assert.Equal(2, node.Items.Count);
        Assert.Equal("Child1", node.Items[0].Name);
        Assert.Equal("NewChild", node.Items[1].Name);
    }
}
```

**Step 8: Run all tests**

Run: `dotnet test src/Namotion.Interceptor.slnx`
Expected: All tests pass

**Step 9: Fix any remaining test failures**

Address issues found during full test run. Common patterns:
- Tests that check specific subject ID values like `"1"`, `"2"` need to assert 22-char base62 strings instead
- Tests that construct `SubjectUpdate` manually need updated field names
- Snapshot tests may need snapshot regeneration

**Step 10: Run full test suite to confirm green**

Run: `dotnet test src/Namotion.Interceptor.slnx`
Expected: All tests pass

**Step 11: Commit**

```bash
git add -A
git commit -m "test: update all tests for stable ID protocol + add new integration tests"
```

---

## Summary of All Modified Files

| # | File | Change |
|---|------|--------|
| 1 | `src/Namotion.Interceptor.Registry/SubjectRegistryExtensions.cs` | Add `GenerateSubjectId()`, `GetOrAddSubjectId()`, `SetSubjectId()`, `SubjectIdKey` constant |
| 2 | `src/Namotion.Interceptor.Registry/Abstractions/ISubjectRegistry.cs` | Add `RegisterStableId()`, `TryGetSubjectByStableId()` |
| 3 | `src/Namotion.Interceptor.Registry/SubjectRegistry.cs` | Add `_stableIdToSubject` reverse index, implement new interface methods, cleanup on detach |
| 4 | `src/Namotion.Interceptor.Connectors/Updates/SubjectCollectionOperation.cs` | Replace `Index`/`FromIndex` with `Id` (required) + `AfterId` + `Key` |
| 5 | `src/Namotion.Interceptor.Connectors/Updates/SubjectPropertyItemUpdate.cs` | Replace `Index` with `Id` (required) + `Key` (optional, for dicts) |
| 6 | `src/Namotion.Interceptor.Connectors/Updates/SubjectUpdate.cs` | Make `Root` nullable (`string?`) |
| 7 | `src/Namotion.Interceptor.Connectors/Updates/Internal/SubjectUpdateBuilder.cs` | Remove `_nextId`, use `GetOrAddSubjectId()`, add `includeRoot` param to `Build()`, remove `PathVisited` |
| 8 | `src/Namotion.Interceptor.Connectors/Updates/Internal/SubjectUpdateFactory.cs` | Remove `BuildPathToRoot` + helpers, pass `includeRoot: false` for partial updates |
| 9 | `src/Namotion.Interceptor.Connectors/Updates/Internal/CollectionDiffBuilder.cs` | Output subject references + predecessors instead of indices |
| 10 | `src/Namotion.Interceptor.Connectors/Updates/Internal/SubjectItemsUpdateFactory.cs` | Emit stable IDs in all operations, use `Id`/`Key` in items |
| 11 | `src/Namotion.Interceptor.Connectors/Updates/Internal/SubjectItemsUpdateApplier.cs` | ID-based find/insert/remove/move, stable ID reuse, `SetSubjectId` on create |
| 12 | `src/Namotion.Interceptor.Connectors/Updates/Internal/SubjectUpdateApplier.cs` | Handle nullable `Root`, apply by stable ID lookup, `SetSubjectId` on object create |

## New Test Files

| File | Purpose |
|------|---------|
| `src/Namotion.Interceptor.Registry.Tests/StableSubjectIdTests.cs` | Unit tests for ID generation, reverse index, GetOrAdd/Set extension methods |
| `src/Namotion.Interceptor.Connectors.Tests/Updates/StableIdCollectionTests.cs` | Build-side tests for ID-based collection operations |
| `src/Namotion.Interceptor.Connectors.Tests/Updates/StableIdApplyTests.cs` | Apply-side tests for ID-based operations + partial updates |
