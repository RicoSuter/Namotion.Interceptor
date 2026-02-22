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

        // Apply structural operations (ID-based)
        if (propertyUpdate.Operations is { Count: > 0 })
        {
            var idRegistry = parent.Context.GetService<ISubjectIdRegistry>();
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
                        // Idempotent: skip if item already exists in collection (echo protection)
                        if (FindItemIndexById(workingItems, operation.Id) >= 0)
                            break;

                        IInterceptorSubject? newItem = null;

                        // Try to reuse an existing subject by subject ID
                        if (idRegistry.TryGetSubjectById(operation.Id, out var existing))
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
            var idRegistry = parent.Context.GetService<ISubjectIdRegistry>();
            var newItems = new List<IInterceptorSubject>(propertyUpdate.Items.Count);
            foreach (var itemUpdate in propertyUpdate.Items)
            {
                IInterceptorSubject? item = null;

                // Try to reuse existing subject by subject ID
                if (idRegistry.TryGetSubjectById(itemUpdate.Id, out var existing))
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
            var idRegistry = parent.Context.GetService<ISubjectIdRegistry>();
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

                            // Try to reuse by subject ID
                            if (idRegistry.TryGetSubjectById(operation.Id, out var existing))
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
            var idRegistry = parent.Context.GetService<ISubjectIdRegistry>();
            foreach (var collUpdate in propertyUpdate.Items)
            {
                if (collUpdate.Key is null)
                    continue;

                var key = ConvertDictionaryKey(collUpdate.Key, targetKeyType);

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
                        // Try reuse by subject ID
                        IInterceptorSubject newItem;
                        if (idRegistry.TryGetSubjectById(collUpdate.Id, out var existingSubject))
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

    private static object ConvertDictionaryKey(object key, Type targetKeyType)
        => DictionaryKeyConverter.Convert(key, targetKeyType);

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
}
