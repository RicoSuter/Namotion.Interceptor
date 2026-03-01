using System.Collections;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Connectors.Updates.Internal;

/// <summary>
/// Applies collection and dictionary updates from <see cref="SubjectUpdate"/> instances.
/// Handles structural operations (Insert, Remove, Move) and sparse property updates.
/// </summary>
internal static class SubjectItemsUpdateApplier
{
    /// <summary>
    /// Applies a collection update to a property using subject ID-based operations.
    /// </summary>
    internal static void ApplyCollectionUpdate(
        IInterceptorSubject parent,
        RegisteredSubjectProperty property,
        SubjectPropertyUpdate propertyUpdate,
        SubjectUpdateApplyContext context)
    {
        var workingItems = (property.GetValue() as IEnumerable<IInterceptorSubject>)?.ToList() ?? [];
        var structureChanged = false;
        var idRegistry = parent.Context.GetService<ISubjectIdRegistry>();

        // Apply structural operations (ID-based)
        if (propertyUpdate.Operations is { Count: > 0 })
        {
            var idIndex = BuildIdIndex(workingItems);
            foreach (var operation in propertyUpdate.Operations)
            {
                if (string.IsNullOrEmpty(operation.Id))
                    continue;

                switch (operation.Action)
                {
                    case SubjectCollectionOperationType.Remove:
                    {
                        var index = FindItemIndexById(idIndex, operation.Id);
                        if (index >= 0)
                        {
                            workingItems.RemoveAt(index);
                            RemoveFromIdIndex(idIndex, operation.Id, index);
                            structureChanged = true;
                        }
                        break;
                    }

                    case SubjectCollectionOperationType.Insert:
                    {
                        // Idempotent: skip if item already exists in collection (echo protection)
                        if (FindItemIndexById(idIndex, operation.Id) >= 0)
                            break;

                        var newItem = ResolveOrCreateSubject(
                            parent, property, 0, operation.Id, idRegistry, context);
                        if (newItem is null)
                            break;

                        var insertIndex = FindInsertPosition(workingItems, idIndex, operation.AfterId);
                        workingItems.Insert(insertIndex, newItem);
                        InsertIntoIdIndex(idIndex, operation.Id, insertIndex);
                        structureChanged = true;
                        break;
                    }

                    case SubjectCollectionOperationType.Move:
                    {
                        // No-op when moving an item after itself
                        if (operation.Id == operation.AfterId)
                            break;

                        var currentIndex = FindItemIndexById(idIndex, operation.Id);
                        if (currentIndex >= 0)
                        {
                            var item = workingItems[currentIndex];
                            workingItems.RemoveAt(currentIndex);
                            RemoveFromIdIndex(idIndex, operation.Id, currentIndex);
                            var insertIndex = FindInsertPosition(workingItems, idIndex, operation.AfterId);
                            workingItems.Insert(insertIndex, item);
                            InsertIntoIdIndex(idIndex, operation.Id, insertIndex);
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
                var item = ResolveOrCreateSubject(
                    parent, property, newItems.Count, itemUpdate.Id, idRegistry, context);
                if (item is not null)
                {
                    newItems.Add(item);
                }
            }

            workingItems = newItems;
            structureChanged = true;
        }

        // Sparse property updates (items with operations present -- update existing items by ID)
        if (propertyUpdate.Operations is not null && propertyUpdate.Items is { Count: > 0 })
        {
            var sparseIdIndex = BuildIdIndex(workingItems);
            foreach (var itemUpdate in propertyUpdate.Items)
            {
                if (context.Subjects.TryGetValue(itemUpdate.Id, out var itemProps))
                {
                    var index = FindItemIndexById(sparseIdIndex, itemUpdate.Id);
                    if (index >= 0 && context.TryMarkAsProcessed(itemUpdate.Id))
                    {
                        SubjectUpdateApplier.ApplyPropertyUpdates(workingItems[index], itemProps, context);
                    }
                }
            }
        }

        // Trim excess items when a complete update declares a smaller count.
        // Only apply for complete updates (no operations) to avoid removing items
        // added by other participants in a multi-source scenario.
        if (propertyUpdate.Operations is null &&
            propertyUpdate.Count.HasValue && workingItems.Count > propertyUpdate.Count.Value)
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

    /// <summary>
    /// Applies a dictionary update to a property using subject ID-based operations.
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
        var idRegistry = parent.Context.GetService<ISubjectIdRegistry>();

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
                if (string.IsNullOrEmpty(operation.Id))
                    continue;

                switch (operation.Action)
                {
                    case SubjectCollectionOperationType.Remove:
                        if (operation.Key is not null)
                        {
                            var key = DictionaryKeyConverter.Convert(operation.Key, targetKeyType);
                            if (workingDictionary.Remove(key))
                                structureChanged = true;
                        }
                        break;

                    case SubjectCollectionOperationType.Insert:
                        if (operation.Key is not null && context.Subjects.TryGetValue(operation.Id, out var itemProps))
                        {
                            var key = DictionaryKeyConverter.Convert(operation.Key, targetKeyType);
                            var newItem = ResolveExistingOrCreateSubject(
                                parent, property, key, operation.Id, itemProps, idRegistry, context);
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

                var key = DictionaryKeyConverter.Convert(collUpdate.Key, targetKeyType);

                if (context.Subjects.TryGetValue(collUpdate.Id, out var itemProps))
                {
                    if (workingDictionary.TryGetValue(key, out var existing))
                    {
                        // Set subject ID on pre-existing dictionary item to match the sender's ID.
                        existing.SetSubjectId(collUpdate.Id);
                        if (context.TryMarkAsProcessed(collUpdate.Id))
                        {
                            SubjectUpdateApplier.ApplyPropertyUpdates(existing, itemProps, context);
                        }
                    }
                    else
                    {
                        var newItem = ResolveExistingOrCreateSubject(
                            parent, property, key, collUpdate.Id, itemProps, idRegistry, context);
                        workingDictionary[key] = newItem;
                        structureChanged = true;
                    }
                }
            }
        }

        // Remove dictionary entries not mentioned in update when count doesn't match.
        // Only apply for complete updates (no operations) to avoid removing entries
        // added by other participants in a multi-source scenario.
        if (propertyUpdate.Operations is null &&
            propertyUpdate.Count.HasValue && workingDictionary.Count != propertyUpdate.Count.Value)
        {
            var updatedKeys = new HashSet<object>();
            if (propertyUpdate.Items is { Count: > 0 })
            {
                foreach (var item in propertyUpdate.Items)
                {
                    if (item.Key is not null)
                        updatedKeys.Add(DictionaryKeyConverter.Convert(item.Key, targetKeyType));
                }
            }

            List<object>? keysToRemove = null;
            foreach (var key in workingDictionary.Keys)
            {
                if (!updatedKeys.Contains(key))
                {
                    keysToRemove ??= [];
                    keysToRemove.Add(key);
                }
            }

            if (keysToRemove is not null)
            {
                foreach (var key in keysToRemove)
                    workingDictionary.Remove(key);
                structureChanged = true;
            }
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

    /// <summary>
    /// Builds a subject ID → index lookup dictionary for O(1) item lookups.
    /// Must be rebuilt after any structural modification (Insert, Remove, Move).
    /// </summary>
    private static Dictionary<string, int> BuildIdIndex(List<IInterceptorSubject> items)
    {
        var index = new Dictionary<string, int>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            index[items[i].GetOrAddSubjectId()] = i;
        }
        return index;
    }

    private static int FindItemIndexById(Dictionary<string, int> idIndex, string stableId)
    {
        return idIndex.GetValueOrDefault(stableId, -1);
    }

    private static int FindInsertPosition(List<IInterceptorSubject> items, Dictionary<string, int> idIndex, string? afterId)
    {
        if (afterId is null)
            return 0; // Insert at head

        if (idIndex.TryGetValue(afterId, out var index))
            return index + 1; // Insert after this item

        return items.Count; // afterId not found, append to end
    }

    /// <summary>
    /// Incrementally updates the ID index after removing an item at the given position.
    /// Decrements indices of all items after the removed position.
    /// </summary>
    private static void RemoveFromIdIndex(Dictionary<string, int> idIndex, string removedId, int removedPosition)
    {
        idIndex.Remove(removedId);
        foreach (var key in idIndex.Keys)
        {
            if (idIndex[key] > removedPosition)
                idIndex[key]--;
        }
    }

    /// <summary>
    /// Incrementally updates the ID index after inserting an item at the given position.
    /// Increments indices of all items at or after the inserted position, then adds the new entry.
    /// </summary>
    private static void InsertIntoIdIndex(Dictionary<string, int> idIndex, string insertedId, int insertedPosition)
    {
        foreach (var key in idIndex.Keys)
        {
            if (idIndex[key] >= insertedPosition)
                idIndex[key]++;
        }
        idIndex[insertedId] = insertedPosition;
    }

    /// <summary>
    /// Resolves an existing subject by ID, or creates a new one from the update context.
    /// Returns null if the subject cannot be resolved or created.
    /// Used by collection operations (Insert) and complete collection rebuilds.
    /// </summary>
    private static IInterceptorSubject? ResolveOrCreateSubject(
        IInterceptorSubject parent,
        RegisteredSubjectProperty property,
        object indexOrKey,
        string subjectId,
        ISubjectIdRegistry idRegistry,
        SubjectUpdateApplyContext context)
    {
        if (idRegistry.TryGetSubjectById(subjectId, out var existing))
        {
            ApplyPropertiesIfAvailable(existing, subjectId, context);
            return existing;
        }

        if (context.Subjects.ContainsKey(subjectId))
        {
            var newItem = CreateSubjectItem(parent, property, indexOrKey, subjectId, context);
            ApplyPropertiesIfAvailable(newItem, subjectId, context);
            return newItem;
        }

        return null;
    }

    /// <summary>
    /// Resolves an existing subject by ID, or creates a new one.
    /// Always returns a non-null subject. Used by dictionary operations where the
    /// subject properties are guaranteed to exist in the context.
    /// </summary>
    private static IInterceptorSubject ResolveExistingOrCreateSubject(
        IInterceptorSubject parent,
        RegisteredSubjectProperty property,
        object indexOrKey,
        string subjectId,
        Dictionary<string, SubjectPropertyUpdate> subjectProperties,
        ISubjectIdRegistry idRegistry,
        SubjectUpdateApplyContext context)
    {
        var subject = idRegistry.TryGetSubjectById(subjectId, out var existing)
            ? existing
            : CreateSubjectItem(parent, property, indexOrKey, subjectId, context);

        if (context.TryMarkAsProcessed(subjectId))
        {
            SubjectUpdateApplier.ApplyPropertyUpdates(subject, subjectProperties, context);
        }

        return subject;
    }

    /// <summary>
    /// Applies property updates to a subject if properties are available and not yet processed.
    /// </summary>
    private static void ApplyPropertiesIfAvailable(
        IInterceptorSubject subject,
        string subjectId,
        SubjectUpdateApplyContext context)
    {
        if (context.Subjects.TryGetValue(subjectId, out var properties) &&
            context.TryMarkAsProcessed(subjectId))
        {
            SubjectUpdateApplier.ApplyPropertyUpdates(subject, properties, context);
        }
    }

    /// <summary>
    /// Creates a new subject item and assigns its ID. Does not apply properties.
    /// </summary>
    private static IInterceptorSubject CreateSubjectItem(
        IInterceptorSubject parent,
        RegisteredSubjectProperty property,
        object indexOrKey,
        string subjectId,
        SubjectUpdateApplyContext context)
    {
        var newItem = context.SubjectFactory.CreateCollectionSubject(property, indexOrKey);
        newItem.Context.AddFallbackContext(parent.Context);
        newItem.SetSubjectId(subjectId);
        return newItem;
    }
}
