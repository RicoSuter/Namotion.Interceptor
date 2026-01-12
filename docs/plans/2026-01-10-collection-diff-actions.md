# Collection Diff Actions Implementation Plan

**Date:** 2026-01-10
**Status:** Draft (Revised)
**Context:** Sparse collection updates with structural operations separated from property updates

## Problem Statement

Currently, collection property updates send all elements (even unchanged ones as `{}`), resulting in large payloads when only a single property within a collection item changes.

Additionally, the current structure cannot express:
- Item removals (ambiguous with `item: null`)
- Item moves/reordering (identity would be lost)
- Collection size changes

## Design Decision

After brainstorming, we decided on **Option C: Hybrid approach** with structural operations separate from property updates.

### Why Option C?

| Approach | Chosen | Reason |
|----------|--------|--------|
| Action per item (original) | No | Move includes full object data, wasteful |
| ID-based tracking | No | Leaks into consumer code, can't hide in apply methods |
| **Structural + Updates separate** | **Yes** | Smallest JSON, cleanest separation, best performance |

**Key insight**: Move should NOT include item data - it's just a positional instruction. Property updates reference items by their FINAL position after structural ops are applied.

### Benefits of Option C

1. **Smaller JSON**: Move has no item data, just `from` → `to` indices
2. **Faster**: Structural ops are cheap index manipulations
3. **Cleaner**: Clear separation of concerns
4. **Simpler apply logic**: Phase 1 = structural, Phase 2 = property updates

## Data Model

### 1. CollectionOperation (New)

Represents a structural change to a collection (Remove, Insert, Move).

```csharp
// File: src/Namotion.Interceptor.Connectors/Updates/CollectionOperation.cs

namespace Namotion.Interceptor.Connectors.Updates;

/// <summary>
/// Represents a structural operation on a collection (not property updates).
/// </summary>
public class CollectionOperation
{
    /// <summary>
    /// The type of structural operation.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public required CollectionOperationType Action { get; init; }

    /// <summary>
    /// Target index (int for arrays) or key (object for dictionaries).
    /// For Remove: the index/key to remove.
    /// For Insert: the index/key where to insert.
    /// For Move: the destination index.
    /// </summary>
    public required object Index { get; init; }

    /// <summary>
    /// Source index for Move action. Arrays only.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? FromIndex { get; init; }

    /// <summary>
    /// The item to insert. Only for Insert action.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SubjectUpdate? Item { get; init; }
}

/// <summary>
/// Types of structural operations on collections.
/// </summary>
public enum CollectionOperationType
{
    /// <summary>
    /// Remove item at index/key. For arrays, subsequent items shift down.
    /// </summary>
    Remove = 0,

    /// <summary>
    /// Insert new item at index/key. For arrays, subsequent items shift up.
    /// </summary>
    Insert = 1,

    /// <summary>
    /// Move item from FromIndex to Index. Arrays only. Identity preserved.
    /// </summary>
    Move = 2
}
```

### 2. SubjectPropertyUpdate (Modified)

```csharp
// File: src/Namotion.Interceptor.Connectors/Updates/SubjectPropertyUpdate.cs
// Add new properties with proper JSON serialization attributes:

/// <summary>
/// Structural operations (Remove, Insert, Move) to apply in order.
/// Applied BEFORE property updates in Collection.
/// </summary>
[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
[JsonInclude]
public List<CollectionOperation>? Operations { get; internal set; }

/// <summary>
/// Sparse property updates by FINAL index/key (after Operations applied).
/// </summary>
[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]  // Changed: null = not serialized
[JsonInclude]
public IReadOnlyCollection<SubjectPropertyCollectionUpdate>? Collection { get; internal set; }

/// <summary>
/// Total count of the collection/dictionary after all operations.
/// </summary>
[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
[JsonInclude]
public int? Count { get; internal set; }
```

### 3. SubjectPropertyCollectionUpdate (Simplified)

Now only for property updates, no Action field needed.

```csharp
// File: src/Namotion.Interceptor.Connectors/Updates/SubjectPropertyCollectionUpdate.cs

namespace Namotion.Interceptor.Connectors.Updates;

/// <summary>
/// Represents a property update for a collection item at a specific index/key.
/// Index refers to the FINAL position after structural Operations are applied.
/// </summary>
public class SubjectPropertyCollectionUpdate
{
    /// <summary>
    /// The target index (int for arrays) or key (object for dictionaries).
    /// This is the FINAL index after structural operations are applied.
    /// </summary>
    public required object Index { get; init; }

    /// <summary>
    /// The property updates for the item at this index.
    /// </summary>
    public SubjectUpdate? Item { get; init; }
}
```

### 4. Remove CollectionUpdateAction Enum

Delete `CollectionUpdateAction.cs` - replaced by `CollectionOperationType`.

## JSON Examples

### Scenario: Remove index 0, item at 1→0, change its firstName

**Before (action per item - wasteful):**
```json
{
  "Collection": [
    { "Index": 0, "Action": "Move", "FromIndex": 1, "Item": { "Properties": { "firstName": { "Value": "X" } } } },
    { "Index": 0, "Action": "Remove" }
  ],
  "Count": 1
}
```

**After (Option C - efficient):**
```json
{
  "Operations": [
    { "Action": "Remove", "Index": 0 }
  ],
  "Collection": [
    { "Index": 0, "Item": { "Properties": { "firstName": { "Value": "X" } } } }
  ],
  "Count": 1
}
```

Note: No Move needed! Remove at index 0 shifts item from 1→0 automatically. The property update references the FINAL index 0.

### Scenario: Pure reorder [A,B,C] → [C,A,B], only B has property changes

**Before (action per item - includes full objects):**
```json
{
  "Collection": [
    { "Index": 0, "Action": "Move", "FromIndex": 2, "Item": { "Properties": { ...C full... } } },
    { "Index": 1, "Action": "Move", "FromIndex": 0, "Item": { "Properties": { ...A full... } } },
    { "Index": 2, "Action": "Move", "FromIndex": 1, "Item": { "Properties": { ...B with changes... } } }
  ]
}
```

**After (Option C - just indices + changed props):**
```json
{
  "Operations": [
    { "Action": "Move", "FromIndex": 2, "Index": 0 },
    { "Action": "Move", "FromIndex": 1, "Index": 2 }
  ],
  "Collection": [
    { "Index": 2, "Item": { "Properties": { ...B changes only... } } }
  ],
  "Count": 3
}
```

Much smaller! Move has no item data.

## Architecture

### File Structure

```
src/Namotion.Interceptor.Connectors/Updates/
├── SubjectUpdate.cs                    - Data model only
├── SubjectPropertyUpdate.cs            - Data model + Operations, Collection, Count
├── SubjectPropertyCollectionUpdate.cs  - Simplified (Index + Item only)
├── CollectionOperation.cs              - NEW: Structural operation
├── CollectionOperationType.cs          - NEW: Enum (Remove, Insert, Move)
├── SubjectUpdateFactory.cs             - Factory methods
├── CollectionDiffFactory.cs            - Diff algorithms (produces Operations + Collection)
└── Performance/
    └── SubjectUpdatePools.cs           - Object pooling
```

### CollectionDiffFactory

Produces two separate outputs:
1. `Operations` - ordered list of structural changes
2. `Collection` - sparse property updates by final index

```csharp
// File: CollectionDiffFactory.cs
namespace Namotion.Interceptor.Connectors.Updates;

internal static class CollectionDiffFactory
{
    public static (List<CollectionOperation>? operations, List<SubjectPropertyCollectionUpdate>? updates)
        CreateArrayDiff(
            IEnumerable<IInterceptorSubject>? oldCollection,
            IEnumerable<IInterceptorSubject>? newCollection,
            ReadOnlySpan<ISubjectUpdateProcessor> processors,
            Dictionary<IInterceptorSubject, SubjectUpdate> knownSubjectUpdates,
            Dictionary<SubjectPropertyUpdate, SubjectPropertyUpdateReference>? propertyUpdates,
            HashSet<IInterceptorSubject> currentPath)
    {
        List<CollectionOperation>? operations = null;
        List<SubjectPropertyCollectionUpdate>? updates = null;

        if (newCollection is null)
        {
            return (null, null); // Consumer uses Count=0
        }

        var oldItems = oldCollection?.ToList() ?? [];
        var newItems = newCollection.ToList();

        // Build lookup: subject instance -> old index
        var oldIndexMap = new Dictionary<IInterceptorSubject, int>();
        for (var i = 0; i < oldItems.Count; i++)
        {
            oldIndexMap[oldItems[i]] = i;
        }

        var processedOldItems = new HashSet<IInterceptorSubject>();

        // Track what ends up where after operations
        // This helps us compute final indices for property updates

        // Phase 1: Identify structural changes
        for (var newIndex = 0; newIndex < newItems.Count; newIndex++)
        {
            var newItem = newItems[newIndex];

            if (oldIndexMap.TryGetValue(newItem, out var oldIndex))
            {
                processedOldItems.Add(newItem);

                if (oldIndex != newIndex)
                {
                    // Item moved
                    operations ??= [];
                    operations.Add(new CollectionOperation
                    {
                        Action = CollectionOperationType.Move,
                        FromIndex = oldIndex,
                        Index = newIndex
                    });
                }

                // Check for property changes (Update)
                var itemUpdate = SubjectUpdateFactory.GetOrCreateCompleteUpdate(
                    newItem, processors, knownSubjectUpdates, propertyUpdates, currentPath);

                if (itemUpdate.Properties.Count > 0 || itemUpdate.Reference.HasValue)
                {
                    updates ??= [];
                    updates.Add(new SubjectPropertyCollectionUpdate
                    {
                        Index = newIndex,  // FINAL index
                        Item = itemUpdate
                    });
                }
            }
            else
            {
                // New item - Insert
                operations ??= [];
                operations.Add(new CollectionOperation
                {
                    Action = CollectionOperationType.Insert,
                    Index = newIndex,
                    Item = SubjectUpdateFactory.GetOrCreateCompleteUpdate(
                        newItem, processors, knownSubjectUpdates, propertyUpdates, currentPath)
                });
            }
        }

        // Find removed items
        for (var oldIndex = 0; oldIndex < oldItems.Count; oldIndex++)
        {
            if (!processedOldItems.Contains(oldItems[oldIndex]))
            {
                operations ??= [];
                operations.Add(new CollectionOperation
                {
                    Action = CollectionOperationType.Remove,
                    Index = oldIndex
                });
            }
        }

        return (operations, updates);
    }

    public static (List<CollectionOperation>? operations, List<SubjectPropertyCollectionUpdate>? updates)
        CreateDictionaryDiff(
            IDictionary? oldDictionary,
            IDictionary? newDictionary,
            ReadOnlySpan<ISubjectUpdateProcessor> processors,
            Dictionary<IInterceptorSubject, SubjectUpdate> knownSubjectUpdates,
            Dictionary<SubjectPropertyUpdate, SubjectPropertyUpdateReference>? propertyUpdates,
            HashSet<IInterceptorSubject> currentPath)
    {
        List<CollectionOperation>? operations = null;
        List<SubjectPropertyCollectionUpdate>? updates = null;

        if (newDictionary is null)
        {
            return (null, null);
        }

        var oldKeys = new HashSet<object>(
            oldDictionary?.Keys.Cast<object>() ?? Enumerable.Empty<object>());

        foreach (DictionaryEntry entry in newDictionary)
        {
            var key = entry.Key;
            var newItem = entry.Value as IInterceptorSubject;

            if (oldKeys.Contains(key))
            {
                oldKeys.Remove(key);

                // Key exists - check for property updates
                if (newItem is not null)
                {
                    var itemUpdate = SubjectUpdateFactory.GetOrCreateCompleteUpdate(
                        newItem, processors, knownSubjectUpdates, propertyUpdates, currentPath);

                    if (itemUpdate.Properties.Count > 0 || itemUpdate.Reference.HasValue)
                    {
                        updates ??= [];
                        updates.Add(new SubjectPropertyCollectionUpdate
                        {
                            Index = key,
                            Item = itemUpdate
                        });
                    }
                }
            }
            else
            {
                // New key - Insert
                operations ??= [];
                operations.Add(new CollectionOperation
                {
                    Action = CollectionOperationType.Insert,
                    Index = key,
                    Item = newItem is not null
                        ? SubjectUpdateFactory.GetOrCreateCompleteUpdate(
                            newItem, processors, knownSubjectUpdates, propertyUpdates, currentPath)
                        : null
                });
            }
        }

        // Remaining keys were removed
        foreach (var removedKey in oldKeys)
        {
            operations ??= [];
            operations.Add(new CollectionOperation
            {
                Action = CollectionOperationType.Remove,
                Index = removedKey
            });
        }

        return (operations, updates);
    }
}
```

## Apply Logic

Two-phase application:

1. **Phase 1: Apply structural operations** (in order)
   - Remove (process from end to preserve indices)
   - Insert (process from start)
   - Move (complex - track item references)

2. **Phase 2: Apply property updates** (by final index)

```csharp
// In SubjectUpdateExtensions.cs

private static void ApplyCollectionUpdate(...)
{
    var workingItems = existingItems.ToList();

    // Phase 1: Structural operations
    if (propertyUpdate.Operations is { Count: > 0 })
    {
        // Sort removes by index descending (remove from end first)
        var removes = propertyUpdate.Operations
            .Where(o => o.Action == CollectionOperationType.Remove)
            .OrderByDescending(o => (int)o.Index)
            .ToList();

        foreach (var remove in removes)
        {
            var index = (int)remove.Index;
            if (index < workingItems.Count)
                workingItems.RemoveAt(index);
        }

        // Process moves (use original items for identity)
        var moves = propertyUpdate.Operations
            .Where(o => o.Action == CollectionOperationType.Move)
            .ToList();

        // ... move logic preserving object identity ...

        // Process inserts (sorted by index ascending)
        var inserts = propertyUpdate.Operations
            .Where(o => o.Action == CollectionOperationType.Insert)
            .OrderBy(o => (int)o.Index)
            .ToList();

        foreach (var insert in inserts)
        {
            var index = (int)insert.Index;
            var newItem = CreateNewItem(insert.Item);
            workingItems.Insert(index, newItem);
        }
    }

    // Phase 2: Property updates (by final index)
    if (propertyUpdate.Collection is { Count: > 0 })
    {
        foreach (var update in propertyUpdate.Collection)
        {
            var index = (int)update.Index;
            if (index < workingItems.Count && update.Item is not null)
            {
                ApplySubjectPropertyUpdate(workingItems[index], update.Item, ...);
            }
        }
    }

    // Apply count truncation
    if (propertyUpdate.Count.HasValue && workingItems.Count > propertyUpdate.Count.Value)
    {
        workingItems.RemoveRange(propertyUpdate.Count.Value,
            workingItems.Count - propertyUpdate.Count.Value);
    }

    // Set final collection
    registeredProperty.SetValue(CreateCollection(workingItems));
}
```

## TypeScript Changes

### Types

```typescript
export type CollectionOperationType = "Remove" | "Insert" | "Move";

export interface CollectionOperation {
    action: CollectionOperationType;
    index: number | string;
    fromIndex?: number;  // For Move only
    item?: SubjectUpdate;  // For Insert only
}

export interface SubjectPropertyCollectionUpdate {
    index: number | string;  // Final index after operations
    item?: SubjectUpdate;
}

export interface SubjectPropertyUpdate {
    kind?: SubjectPropertyUpdateKind;
    value?: any;
    timestamp?: string;
    operations?: CollectionOperation[];  // Structural changes
    collection?: SubjectPropertyCollectionUpdate[];  // Property updates
    count?: number;
    item?: SubjectUpdate;
    attributes?: { [key: string]: SubjectPropertyUpdate };
    [key: string]: any;
}
```

### Apply Logic (variables.facade.ts)

```typescript
private handleCollection(update: SubjectPropertyUpdate, ...) {
    const workingItems = [...oldItems];

    // Phase 1: Structural operations
    if (update.operations?.length) {
        // Removes (from end first)
        const removes = update.operations
            .filter(o => o.action === 'Remove')
            .sort((a, b) => (b.index as number) - (a.index as number));

        for (const remove of removes) {
            workingItems.splice(remove.index as number, 1);
        }

        // Moves (preserve identity)
        const moves = update.operations.filter(o => o.action === 'Move');
        // ... move logic ...

        // Inserts (from start)
        const inserts = update.operations
            .filter(o => o.action === 'Insert')
            .sort((a, b) => (a.index as number) - (b.index as number));

        for (const insert of inserts) {
            const newItem = {};
            if (insert.item) {
                this.updatePropertyVariables(newItem, insert.item, ...);
            }
            workingItems.splice(insert.index as number, 0, newItem);
        }
    }

    // Phase 2: Property updates (by final index)
    if (update.collection?.length) {
        for (const upd of update.collection) {
            const index = upd.index as number;
            if (index < workingItems.length && upd.item) {
                this.updatePropertyVariables(workingItems[index], upd.item, ...);
            }
        }
    }

    // Apply count truncation
    if (update.count !== undefined && workingItems.length > update.count) {
        workingItems.length = update.count;
    }

    // Update the collection
    this.setCollection(obj, propertyName, workingItems);
}
```

## Test Structure

Tests organized by functionality, each file under 300 lines.

### File Structure

```
src/Namotion.Interceptor.Connectors.Tests/
├── SubjectUpdateCollectionTests.cs    - Collection diff & apply tests (~250 LOC)
├── SubjectUpdateDictionaryTests.cs    - Dictionary diff & apply tests (~200 LOC)
├── SubjectUpdateTests.cs              - Existing tests (refactored)
├── SubjectUpdateCycleTests.cs         - Existing cycle tests
└── Extensions/
    └── SubjectUpdateExtensionsTests.cs - Apply logic tests
```

### SubjectUpdateCollectionTests.cs (~250 LOC)

```csharp
namespace Namotion.Interceptor.Connectors.Tests;

public class SubjectUpdateCollectionTests
{
    // === INSERT Tests ===

    [Fact]
    public async Task WhenItemAddedAtEnd_ThenInsertOperationGenerated()

    [Fact]
    public async Task WhenItemAddedAtStart_ThenInsertOperationGenerated()

    [Fact]
    public async Task WhenMultipleItemsAdded_ThenMultipleInsertOperationsGenerated()

    // === REMOVE Tests ===

    [Fact]
    public async Task WhenItemRemovedFromEnd_ThenRemoveOperationGenerated()

    [Fact]
    public async Task WhenItemRemovedFromStart_ThenRemoveOperationGenerated()

    [Fact]
    public async Task WhenAllItemsRemoved_ThenCountIsZero()

    // === MOVE Tests ===

    [Fact]
    public async Task WhenItemMovedForward_ThenMoveOperationGenerated()

    [Fact]
    public async Task WhenItemMovedBackward_ThenMoveOperationGenerated()

    [Fact]
    public async Task WhenItemsSwapped_ThenMoveOperationsGenerated()

    // === UPDATE Tests (property changes) ===

    [Fact]
    public async Task WhenItemPropertyChanged_ThenCollectionUpdateGenerated()

    [Fact]
    public async Task WhenNoPropertyChanges_ThenNoCollectionUpdateGenerated()

    // === COMBINED Tests ===

    [Fact]
    public async Task WhenRemoveAndPropertyChange_ThenBothGenerated()

    [Fact]
    public async Task WhenInsertAndMove_ThenBothGenerated()

    // === APPLY Tests ===

    [Fact]
    public async Task WhenApplyingInsert_ThenItemCreatedAtIndex()

    [Fact]
    public async Task WhenApplyingRemove_ThenItemRemovedAndShifted()

    [Fact]
    public async Task WhenApplyingMove_ThenIdentityPreserved()

    [Fact]
    public async Task WhenApplyingPropertyUpdate_ThenItemUpdatedInPlace()

    // === COMBINED OPERATIONS + UPDATES Tests ===

    [Fact]
    public async Task WhenRemoveAndPropertyChangeOnRemaining_ThenOperationsAndUpdatesGenerated()
    // Old: [A, B, C], New: [A, C] with C.Name changed
    // Expected: Operations=[Remove(1)], Collection=[{Index:1, Item:{...C changes...}}]

    [Fact]
    public async Task WhenInsertAndPropertyChangeOnExisting_ThenOperationsAndUpdatesGenerated()
    // Old: [A, B], New: [A, X, B] with B.Name changed
    // Expected: Operations=[Insert(1)], Collection=[{Index:2, Item:{...B changes...}}]

    [Fact]
    public async Task WhenMoveAndPropertyChange_ThenOperationsAndUpdatesGenerated()
    // Old: [A, B, C], New: [C, A, B] with B.Name changed
    // Expected: Operations=[Move(2→0), Move(1→2)], Collection=[{Index:2, Item:{...B changes...}}]

    [Fact]
    public async Task WhenMultipleOperationsAndMultipleUpdates_ThenAllGenerated()
    // Old: [A, B, C, D], New: [X, C, A] with C.Name and A.Name changed
    // Expected: Operations=[Remove(1), Remove(3), Insert(0), Move(2→1), Move(0→2)]
    //           Collection=[{Index:1, Item:{...C...}}, {Index:2, Item:{...A...}}]

    // === EDGE CASES ===

    [Fact]
    public async Task WhenOldCollectionNull_ThenAllInsertsGenerated()

    [Fact]
    public async Task WhenNewCollectionNull_ThenCountZero()

    [Fact]
    public async Task WhenBothEmpty_ThenNoOperationsGenerated()
}
```

### SubjectUpdateDictionaryTests.cs (~200 LOC)

```csharp
namespace Namotion.Interceptor.Connectors.Tests;

public class SubjectUpdateDictionaryTests
{
    // === INSERT Tests ===

    [Fact]
    public async Task WhenKeyAdded_ThenInsertOperationGenerated()

    [Fact]
    public async Task WhenMultipleKeysAdded_ThenMultipleInsertOperationsGenerated()

    // === REMOVE Tests ===

    [Fact]
    public async Task WhenKeyRemoved_ThenRemoveOperationGenerated()

    [Fact]
    public async Task WhenAllKeysRemoved_ThenCountIsZero()

    // === UPDATE Tests ===

    [Fact]
    public async Task WhenValuePropertyChanged_ThenCollectionUpdateGenerated()

    [Fact]
    public async Task WhenNoPropertyChanges_ThenNoCollectionUpdateGenerated()

    // === NO MOVE ===

    [Fact]
    public async Task WhenDictionaryIterated_ThenNoMoveOperations()

    // === APPLY Tests ===

    [Fact]
    public async Task WhenApplyingDictionaryInsert_ThenKeyAddedWithValue()

    [Fact]
    public async Task WhenApplyingDictionaryRemove_ThenKeyRemoved()

    [Fact]
    public async Task WhenApplyingDictionaryUpdate_ThenValuePropertiesUpdated()

    // === COMBINED OPERATIONS + UPDATES Tests ===

    [Fact]
    public async Task WhenInsertAndPropertyChangeOnExisting_ThenOperationsAndUpdatesGenerated()
    // Old: {a: A, b: B}, New: {a: A, b: B, c: C} with B.Name changed
    // Expected: Operations=[Insert("c")], Collection=[{Index:"b", Item:{...B changes...}}]

    [Fact]
    public async Task WhenRemoveAndPropertyChangeOnRemaining_ThenOperationsAndUpdatesGenerated()
    // Old: {a: A, b: B, c: C}, New: {a: A, c: C} with C.Name changed
    // Expected: Operations=[Remove("b")], Collection=[{Index:"c", Item:{...C changes...}}]

    [Fact]
    public async Task WhenMultipleOperationsAndMultipleUpdates_ThenAllGenerated()
    // Old: {a: A, b: B}, New: {a: A, c: C, d: D} with A.Name changed
    // Expected: Operations=[Remove("b"), Insert("c"), Insert("d")], Collection=[{Index:"a", Item:{...A...}}]

    // === EDGE CASES ===

    [Fact]
    public async Task WhenOldDictionaryNull_ThenAllInsertsGenerated()

    [Fact]
    public async Task WhenNewDictionaryNull_ThenCountZero()
}
```

### SubjectUpdateTests.cs (Existing - Refactored)

Keep existing tests, but move collection/dictionary specific tests to new files.

## Already Written Code (Migration Required)

The following code was already written for the original action-per-item design and needs migration:

### Files to Migrate

| File | Current State | Required Changes |
|------|---------------|------------------|
| `SubjectUpdateFactory.cs` | ✅ Extracted, working | Update to use new diff signature |
| `CollectionDiffFactory.cs` | ❌ Wrong design | Rewrite to return `(operations, updates)` tuple |
| `CollectionUpdateAction.cs` | ❌ Wrong enum | Delete, replace with `CollectionOperationType` |
| `SubjectPropertyCollectionUpdate.cs` | ❌ Has Action, FromIndex | Remove Action, FromIndex properties |
| `SubjectPropertyUpdate.cs` | ⚠️ Has Count, ApplyValueWithDiff | Add `Operations`, update JSON attributes |

### Verified Snapshots to Update

The following snapshot files need updating after implementation:

```
src/Namotion.Interceptor.Connectors.Tests/
├── SubjectUpdateTests.WhenUpdateContainsIncrementalChanges_ThenAllOfThemAreApplied.verified.txt
├── SubjectUpdateTests.WhenGeneratingPartialSubjectDescription_ThenResultCollectionIsCorrect.verified.txt
├── SubjectUpdateCycleTests.WhenModelHasRefsCollectionsDictionariesWithCycles_ThenCreatePartialUpdateSucceeds.verified.txt
└── (other snapshot files affected by collection/dictionary changes)
```

**Strategy**: After implementation, run tests and accept new snapshots that correctly reflect the Option C format.

## Implementation Batches

### Batch 1: Clean Up & New Data Model
- [ ] Delete `CollectionUpdateAction.cs`
- [ ] Create `CollectionOperation.cs` with `CollectionOperationType` enum
- [ ] Simplify `SubjectPropertyCollectionUpdate.cs`:
  - Remove `Action` property
  - Remove `FromIndex` property
  - Keep only `Index` and `Item`
- [ ] Update `SubjectPropertyUpdate.cs`:
  - Add `Operations` property with `[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]`
  - Update `Collection` to use `[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]`
  - Keep `Count` with existing attribute

### Batch 2: Diff Factory
- [ ] Rewrite `CollectionDiffFactory.CreateArrayDiff`:
  - Change return type to `(List<CollectionOperation>? operations, List<SubjectPropertyCollectionUpdate>? updates)`
  - Generate `Operations` for structural changes (Remove, Insert, Move)
  - Generate `Collection` for property updates only (by final index)
- [ ] Rewrite `CollectionDiffFactory.CreateDictionaryDiff`:
  - Same tuple return type
  - No Move operations for dictionaries
- [ ] Update `SubjectPropertyUpdate.ApplyValueWithDiff`:
  - Call new diff methods
  - Assign both `Operations` and `Collection` results
- [ ] Update `SubjectUpdateFactory` integration

### Batch 3: Tests for Diff
- [ ] Create `SubjectUpdateCollectionTests.cs` (~250 LOC):
  - Insert tests
  - Remove tests
  - Move tests
  - Update (property change) tests
  - Combined operations + updates tests
  - Edge cases
- [ ] Create `SubjectUpdateDictionaryTests.cs` (~200 LOC):
  - Insert tests
  - Remove tests
  - Update tests
  - Combined operations + updates tests
  - Edge cases
- [ ] Run tests, update verified snapshots

### Batch 4: Apply Logic
- [ ] Update `SubjectUpdateExtensions.ApplyCollectionUpdate`:
  - Phase 1: Apply structural operations (Remove→Insert→Move order)
  - Phase 2: Apply property updates (by final index)
- [ ] Update `SubjectUpdateExtensions.ApplyDictionaryUpdate`:
  - Phase 1: Apply structural operations (Remove→Insert)
  - Phase 2: Apply property updates (by key)
- [ ] Write apply logic tests
- [ ] Write identity preservation tests

### Batch 5: TypeScript
- [ ] Update types in `variables-http.service.ts`:
  - Add `CollectionOperationType` type
  - Add `CollectionOperation` interface
  - Update `SubjectPropertyUpdate` interface
  - Update `SubjectPropertyCollectionUpdate` interface
- [ ] Update `handleCollection` in `variables.facade.ts`:
  - Phase 1: Process `operations` array
  - Phase 2: Process `collection` array
- [ ] Test frontend integration

### Batch 6: Final Verification
- [ ] Run all C# tests
- [ ] Accept/update verified snapshots
- [ ] Manual end-to-end testing
- [ ] Verify JSON output is minimal (no empty arrays/nulls)

## Test Coverage Requirements

| Component | Minimum Coverage |
|-----------|-----------------|
| `CreateArrayDiff` | 100% |
| `CreateDictionaryDiff` | 100% |
| `ApplyCollectionUpdate` (arrays) | 100% |
| `ApplyDictionaryUpdate` | 100% |
| Identity preservation | 100% |
| Serialization/Deserialization | 100% |

## Migration Notes

- **Breaking change**: `action` field removed from `SubjectPropertyCollectionUpdate`
- **New field**: `operations` array for structural changes
- **Backward compatible**: Old clients not expecting `operations` will ignore it
- Default behavior without `operations` = property updates only (same as before)

## Open Questions

1. ~~Should Move include item data?~~ **No** - just indices.
2. ~~Should we use action per item or separate structural ops?~~ **Separate** - Option C.
3. Order of operations in `Operations` array? **As-is** or should we normalize to Remove→Insert→Move?
