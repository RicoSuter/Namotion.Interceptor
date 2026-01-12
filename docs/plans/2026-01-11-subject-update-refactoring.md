# Subject Update File Reorganization Plan

**Date:** 2026-01-11
**Status:** Ready for implementation
**Goal:** Reorganize `Updates/` folder by feature (Values, Items, Collections) with create+apply logic together, making code easier to understand in isolation while keeping public APIs unchanged.

## Target Structure

```
Updates/
├── SubjectUpdate.cs                      (data + public Create methods)
├── SubjectPropertyUpdate.cs              (data only)
├── SubjectUpdateExtensions.cs            (public Apply - thin dispatcher)
├── SubjectPropertyUpdateKind.cs          (enum - unchanged)
├── SubjectPropertyCollectionUpdate.cs    (data - unchanged)
├── SubjectCollectionOperation.cs         (data - unchanged)
├── SubjectCollectionOperationType.cs     (enum - unchanged)
├── ISubjectUpdateProcessor.cs            (interface - unchanged)
├── SubjectPropertyUpdateReference.cs     (internal helper - renamed from PropertyUpdateTransformationContext.cs)
├── Performance/
│   └── SubjectUpdatePools.cs             (unchanged)
├── Values/
│   └── SubjectValueUpdateLogic.cs        (internal: create + apply)
├── Items/
│   └── SubjectItemUpdateLogic.cs         (internal: create + apply)
├── Collections/
│   └── SubjectCollectionUpdateLogic.cs   (internal: create + apply + diff)
```

## Files to Delete After Migration

- `SubjectUpdateFactory.cs` - logic distributed to feature files
- `SubjectCollectionDiffFactory.cs` - merged into Collections/

## Implementation Batches

### Batch 1: Create Feature Folders and Stub Files

1. Create `Values/SubjectValueUpdateLogic.cs` with empty internal static class
2. Create `Items/SubjectItemUpdateLogic.cs` with empty internal static class
3. Create `Collections/SubjectCollectionUpdateLogic.cs` with empty internal static class
4. Verify build succeeds

### Batch 2: Extract Values Logic

**From `SubjectPropertyUpdate.cs`:**
- Move value-related logic from `ApplyValue` method (the `else` branch for non-subject properties)

**From `SubjectUpdateExtensions.cs`:**
- Move `ConvertValueToTargetType` helper
- Move value apply logic from `ApplySubjectPropertyUpdate` switch case `SubjectPropertyUpdateKind.Value`

**To `Values/SubjectValueUpdateLogic.cs`:**
```csharp
internal static class SubjectValueUpdateLogic
{
    // Create
    internal static void ApplyValueToUpdate(SubjectPropertyUpdate update, object? value, DateTimeOffset? timestamp)

    // Apply
    internal static void ApplyValueFromUpdate(RegisteredSubjectProperty property, SubjectPropertyUpdate update, ...)

    // Helpers
    internal static object? ConvertValueToTargetType(object? value, Type targetType)
}
```

Verify build + tests pass.

### Batch 3: Extract Items Logic

**From `SubjectPropertyUpdate.cs`:**
- Move item-related logic from `ApplyValue` method (the `IsSubjectReference` branch)

**From `SubjectUpdateExtensions.cs`:**
- Move item apply logic from `ApplySubjectPropertyUpdate` switch case `SubjectPropertyUpdateKind.Item`

**To `Items/SubjectItemUpdateLogic.cs`:**
```csharp
internal static class SubjectItemUpdateLogic
{
    // Create
    internal static void ApplyItemToUpdate(SubjectPropertyUpdate update, IInterceptorSubject? item, ...)

    // Apply
    internal static void ApplyItemFromUpdate(IInterceptorSubject subject, RegisteredSubjectProperty property, SubjectPropertyUpdate update, ...)
}
```

Verify build + tests pass.

### Batch 4: Extract Collections Logic

**From `SubjectPropertyUpdate.cs`:**
- Move `ApplyValueWithDiff` method
- Move `CreateDictionaryCollectionUpdates` helper
- Move `CreateEnumerableCollectionUpdates` helper
- Move collection-related logic from `ApplyValue` method

**From `SubjectUpdateExtensions.cs`:**
- Move `ApplyCollectionUpdate` method
- Move `ApplyDictionaryUpdate` method
- Move `ConvertIndexToInt` helper
- Move `ConvertDictionaryKey` helper

**From `SubjectCollectionDiffFactory.cs`:**
- Move `CreateArrayDiff` method
- Move `CreateDictionaryDiff` method

**To `Collections/SubjectCollectionUpdateLogic.cs`:**
```csharp
internal static class SubjectCollectionUpdateLogic
{
    // Create (complete)
    internal static void ApplyCollectionToUpdate(SubjectPropertyUpdate update, IEnumerable<IInterceptorSubject>? collection, ...)
    internal static void ApplyDictionaryToUpdate(SubjectPropertyUpdate update, IDictionary? dictionary, ...)

    // Create (diff)
    internal static void ApplyCollectionDiffToUpdate(SubjectPropertyUpdate update, object? oldValue, object? newValue, ...)

    // Apply
    internal static void ApplyCollectionFromUpdate(IInterceptorSubject subject, RegisteredSubjectProperty property, SubjectPropertyUpdate update, ...)
    internal static void ApplyDictionaryFromUpdate(IInterceptorSubject subject, RegisteredSubjectProperty property, SubjectPropertyUpdate update, ...)

    // Diff algorithms (from SubjectCollectionDiffFactory)
    private static (List<SubjectCollectionOperation>?, List<SubjectPropertyCollectionUpdate>?) CreateArrayDiff(...)
    private static (List<SubjectCollectionOperation>?, List<SubjectPropertyCollectionUpdate>?) CreateDictionaryDiff(...)

    // Helpers
    private static int ConvertIndexToInt(object index)
    private static object ConvertDictionaryKey(object key)
}
```

Verify build + tests pass.

### Batch 5: Simplify SubjectPropertyUpdate.cs

After extraction, `SubjectPropertyUpdate.cs` should only contain:
- Properties (Kind, Value, Timestamp, Item, Operations, Collection, Count, Attributes, ExtensionData)
- Public static `Create` factory methods (these stay as convenience API)
- `CreateCompleteUpdate` internal method - refactor to delegate to feature logic

The file should shrink from ~307 LOC to ~100 LOC.

Verify build + tests pass.

### Batch 6: Simplify SubjectUpdateFactory.cs

Refactor `SubjectUpdateFactory.cs` to:
- Keep `CreateComplete` and `CreatePartialFromChanges` as orchestrators
- Delegate property-specific logic to feature files
- Keep helper methods: `GetOrCreateSubjectUpdate`, `GetOrCreateSubjectPropertyUpdate`, `IsPropertyIncluded`, `ApplyTransformations`

Consider: Can this file be simplified further or merged elsewhere? If it's small enough, it could stay. If not, distribute remaining logic.

Verify build + tests pass.

### Batch 7: Simplify SubjectUpdateExtensions.cs

Refactor to thin dispatcher:
```csharp
private static void ApplySubjectPropertyUpdate(...)
{
    switch (propertyUpdate.Kind)
    {
        case SubjectPropertyUpdateKind.Value:
            SubjectValueUpdateLogic.ApplyValueFromUpdate(...);
            break;
        case SubjectPropertyUpdateKind.Item:
            SubjectItemUpdateLogic.ApplyItemFromUpdate(...);
            break;
        case SubjectPropertyUpdateKind.Collection:
            if (registeredProperty.IsSubjectDictionary)
                SubjectCollectionUpdateLogic.ApplyDictionaryFromUpdate(...);
            else
                SubjectCollectionUpdateLogic.ApplyCollectionFromUpdate(...);
            break;
    }
}
```

The file should shrink from ~445 LOC to ~150 LOC.

Verify build + tests pass.

### Batch 8: Cleanup

1. Delete `SubjectCollectionDiffFactory.cs` (merged into Collections/)
2. Rename `PropertyUpdateTransformationContext.cs` to `SubjectPropertyUpdateReference.cs` (clearer name)
3. Review if `SubjectUpdateFactory.cs` can be deleted or further simplified
4. Run all tests
5. Verify no public API changes (same extension methods, same static Create methods)

## Validation Checklist

- [ ] All 267 tests pass (242 Connectors + 25 WebSocket)
- [ ] Public API unchanged:
  - [ ] `SubjectUpdate.CreateCompleteUpdate(...)` works
  - [ ] `SubjectUpdate.CreatePartialUpdateFromChanges(...)` works
  - [ ] `subject.ApplySubjectUpdate(...)` works
  - [ ] `subject.ApplySubjectUpdateFromSource(...)` works
- [ ] No new public types exposed
- [ ] File sizes reduced:
  - [ ] `SubjectPropertyUpdate.cs`: 307 → ~100 LOC
  - [ ] `SubjectUpdateExtensions.cs`: 445 → ~150 LOC
- [ ] Each feature file is self-contained and understandable in isolation

## Rollback Plan

If issues arise, revert all changes - this is a pure refactoring with no functional changes.
