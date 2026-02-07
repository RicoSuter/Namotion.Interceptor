using System.Collections;
using System.Globalization;
using System.Text.Json;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Updates.Internal;

/// <summary>
/// Applies collection and dictionary updates from <see cref="SubjectUpdate"/> instances.
/// Handles structural operations (Insert, Remove, Move) and sparse property updates.
/// </summary>
internal static class SubjectItemsUpdateApplier
{
    /// <summary>
    /// Applies a collection (array/list) update to a property.
    /// </summary>
    internal static void ApplyCollectionUpdate(
        IInterceptorSubject parent,
        RegisteredSubjectProperty property,
        SubjectPropertyUpdate propertyUpdate,
        SubjectUpdateApplyContext context)
    {
        var workingItems = (property.GetValue() as IEnumerable<IInterceptorSubject>)?.ToList() ?? [];
        var structureChanged = false;

        // Apply structural operations in two phases:
        // Phase 1: Remove and Insert operations (applied sequentially)
        // Phase 2: Move operations (applied atomically using snapshot)
        if (propertyUpdate.Operations is { Count: > 0 })
        {
            // Phase 1: Apply Remove and Insert operations sequentially
            // Removes should be in descending order so they don't affect each other's indices
            foreach (var operation in propertyUpdate.Operations)
            {
                var index = ConvertIndexToInt(operation.Index);
                switch (operation.Action)
                {
                    case SubjectCollectionOperationType.Remove:
                        if (index >= 0 && index < workingItems.Count)
                        {
                            workingItems.RemoveAt(index);
                            structureChanged = true;
                        }
                        break;

                    case SubjectCollectionOperationType.Insert:
                        if (operation.Id is not null && context.Subjects.TryGetValue(operation.Id, out var itemProps))
                        {
                            var newItem = CreateAndApplyItem(parent, property, index, operation.Id, itemProps, context);
                            if (index >= workingItems.Count)
                                workingItems.Add(newItem);
                            else
                                workingItems.Insert(index, newItem);
                            structureChanged = true;
                        }
                        break;
                }
            }

            // Phase 2: Apply Move operations atomically using snapshot
            // Move indices reference the state after removes/inserts, and moves are applied simultaneously
            var hasMoves = propertyUpdate.Operations.Any(op => op.Action == SubjectCollectionOperationType.Move);
            if (hasMoves)
            {
                var snapshot = workingItems.ToArray();
                foreach (var operation in propertyUpdate.Operations)
                {
                    if (operation.Action == SubjectCollectionOperationType.Move && operation.FromIndex.HasValue)
                    {
                        var toIndex = ConvertIndexToInt(operation.Index);
                        var fromIndex = operation.FromIndex.Value;
                        if (fromIndex >= 0 && fromIndex < snapshot.Length && toIndex >= 0 && toIndex < workingItems.Count)
                        {
                            workingItems[toIndex] = snapshot[fromIndex];
                            structureChanged = true;
                        }
                    }
                }
            }
        }

        // Apply sparse property updates
        if (propertyUpdate.Items is { Count: > 0 })
        {
            foreach (var collectionUpdate in propertyUpdate.Items)
            {
                var index = ConvertIndexToInt(collectionUpdate.Index);

                // Validate index against declared count - if count is specified, index must be < count
                if (propertyUpdate.Count.HasValue && index >= propertyUpdate.Count.Value)
                {
                    throw new InvalidOperationException(
                        $"Invalid collection update: index {index} is out of bounds for declared count {propertyUpdate.Count.Value}. " +
                        "The index in a sparse update must be less than the declared count.");
                }

                if (collectionUpdate.Id is not null &&
                    context.Subjects.TryGetValue(collectionUpdate.Id, out var itemProps))
                {
                    if (index >= 0 && index < workingItems.Count)
                    {
                        // Update existing item
                        if (context.TryMarkAsProcessed(collectionUpdate.Id))
                        {
                            SubjectUpdateApplier.ApplyPropertyUpdates(workingItems[index], itemProps, context);
                        }
                    }
                    else if (index >= 0 && index <= workingItems.Count)
                    {
                        // Create new item at append position (for complete updates rebuilding the collection)
                        var newItem = CreateAndApplyItem(parent, property, index, collectionUpdate.Id, itemProps, context);
                        if (index >= workingItems.Count)
                            workingItems.Add(newItem);
                        else
                            workingItems[index] = newItem;
                        structureChanged = true;
                    }
                }
            }
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

    /// <summary>
    /// Applies a dictionary update to a property.
    /// </summary>
    internal static void ApplyDictionaryUpdate(
        IInterceptorSubject parent,
        RegisteredSubjectProperty property,
        SubjectPropertyUpdate propertyUpdate,
        SubjectUpdateApplyContext context)
    {
        var existingDictionary = property.GetValue() as IDictionary;
        var workingDictionary = new Dictionary<string, IInterceptorSubject>();
        var structureChanged = false;

        if (existingDictionary is not null)
        {
            foreach (DictionaryEntry entry in existingDictionary)
            {
                if (entry.Value is IInterceptorSubject item)
                    workingDictionary[Convert.ToString(entry.Key, CultureInfo.InvariantCulture)!] = item;
            }
        }

        // Apply structural operations
        if (propertyUpdate.Operations is { Count: > 0 })
        {
            foreach (var operation in propertyUpdate.Operations)
            {
                var key = ConvertDictionaryKey(operation.Index);
                switch (operation.Action)
                {
                    case SubjectCollectionOperationType.Remove:
                        if (workingDictionary.Remove(key))
                            structureChanged = true;
                        break;

                    case SubjectCollectionOperationType.Insert:
                        if (operation.Id is not null && context.Subjects.TryGetValue(operation.Id, out var itemProps))
                        {
                            var newItem = CreateAndApplyItem(parent, property, key, operation.Id, itemProps, context);
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
                var key = ConvertDictionaryKey(collUpdate.Index);

                if (collUpdate.Id is not null &&
                    context.Subjects.TryGetValue(collUpdate.Id, out var itemProps))
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
                        var newItem = CreateAndApplyItem(parent, property, key, collUpdate.Id, itemProps, context);
                        workingDictionary[key] = newItem;
                        structureChanged = true;
                    }
                }
            }
        }

        if (structureChanged)
        {
            var dictionary = context.SubjectFactory.CreateSubjectDictionary(property.Type, workingDictionary.ToDictionary(kvp => (object)kvp.Key, kvp => kvp.Value));
            using (SubjectChangeContext.WithChangedTimestamp(propertyUpdate.Timestamp))
            {
                property.SetValue(dictionary);
            }
        }
    }

    private static int ConvertIndexToInt(object index) => index switch
    {
        int i => i,
        JsonElement json => json.GetInt32(),
        _ => Convert.ToInt32(index)
    };

    private static string ConvertDictionaryKey(object key)
    {
        return key is JsonElement jsonElement
            ? jsonElement.GetString() ?? jsonElement.ToString()
            : Convert.ToString(key, CultureInfo.InvariantCulture)!;
    }

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
        if (context.TryMarkAsProcessed(subjectId))
        {
            SubjectUpdateApplier.ApplyPropertyUpdates(newItem, properties, context);
        }
        return newItem;
    }
}
