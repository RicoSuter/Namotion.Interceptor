using System.Collections;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Updates.Collections;

/// <summary>
/// Internal logic for creating and applying array/list collection property updates.
/// </summary>
internal static class SubjectCollectionUpdateLogic
{
    /// <summary>
    /// Applies an array/list collection to a property update (create side for complete updates).
    /// Sets Kind to Collection and creates all item updates.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void ApplyToUpdate(
        SubjectPropertyUpdate update,
        IEnumerable<IInterceptorSubject>? collection,
        ReadOnlySpan<ISubjectUpdateProcessor> processors,
        Dictionary<IInterceptorSubject, SubjectUpdate> knownSubjectUpdates,
        Dictionary<SubjectPropertyUpdate, SubjectPropertyUpdateReference>? propertyUpdates,
        HashSet<IInterceptorSubject> currentPath)
    {
        update.Kind = SubjectPropertyUpdateKind.Collection;
        update.Collection = collection is not null
            ? CreateCollectionUpdates(collection, processors, knownSubjectUpdates, propertyUpdates, currentPath)
            : null;
    }

    /// <summary>
    /// Applies an array/list diff to a property update (create side for partial updates).
    /// Produces structural Operations (Remove, Insert, Move) and sparse Collection updates.
    /// </summary>
    internal static void ApplyDiffToUpdate(
        SubjectPropertyUpdate update,
        object? oldValue,
        object? newValue,
        ReadOnlySpan<ISubjectUpdateProcessor> processors,
        Dictionary<IInterceptorSubject, SubjectUpdate> knownSubjectUpdates,
        Dictionary<SubjectPropertyUpdate, SubjectPropertyUpdateReference>? propertyUpdates,
        HashSet<IInterceptorSubject> currentPath)
    {
        update.Kind = SubjectPropertyUpdateKind.Collection;

        var oldCollection = oldValue as IEnumerable<IInterceptorSubject>;
        var newCollection = newValue as IEnumerable<IInterceptorSubject>;

        var (operations, updates) = CreateDiff(
            oldCollection, newCollection, processors, knownSubjectUpdates, propertyUpdates, currentPath);

        update.Operations = operations;
        update.Collection = updates;
        update.Count = (newCollection as ICollection<IInterceptorSubject>)?.Count ?? newCollection?.Count();
    }

    /// <summary>
    /// Applies array/list updates using two-phase approach:
    /// Phase 1: Apply structural operations (Remove, Insert, Move)
    /// Phase 2: Apply sparse property updates by final index
    /// </summary>
    internal static void ApplyFromUpdate(
        IInterceptorSubject subject,
        RegisteredSubjectProperty registeredProperty,
        SubjectPropertyUpdate propertyUpdate,
        Action<RegisteredSubjectProperty, SubjectPropertyUpdate> applyValuePropertyUpdate,
        ISubjectFactory? subjectFactory)
    {
        var existingItems = (registeredProperty.GetValue() as ICollection)?.Cast<IInterceptorSubject>().ToList()
            ?? new List<IInterceptorSubject>();
        var parentRegistry = subject.Context.GetService<ISubjectRegistry>();
        var structureChanged = false;

        // Phase 1: Apply structural operations in order
        if (propertyUpdate.Operations is { Count: > 0 })
        {
            foreach (var operation in propertyUpdate.Operations)
            {
                var index = ConvertIndexToInt(operation.Index);

                switch (operation.Action)
                {
                    case SubjectCollectionOperationType.Remove:
                        if (index >= 0 && index < existingItems.Count)
                        {
                            existingItems.RemoveAt(index);
                            structureChanged = true;
                        }
                        break;

                    case SubjectCollectionOperationType.Insert:
                        if (subjectFactory is not null && operation.Item is not null)
                        {
                            var newItem = subjectFactory.CreateCollectionSubject(registeredProperty, index);
                            newItem.Context.AddFallbackContext(subject.Context);
                            newItem.ApplySubjectPropertyUpdate(operation.Item, applyValuePropertyUpdate, subjectFactory, parentRegistry);

                            if (index >= existingItems.Count)
                            {
                                existingItems.Add(newItem);
                            }
                            else
                            {
                                existingItems.Insert(index, newItem);
                            }
                            structureChanged = true;
                        }
                        break;

                    case SubjectCollectionOperationType.Move:
                        if (operation.FromIndex.HasValue)
                        {
                            var fromIndex = operation.FromIndex.Value;
                            if (fromIndex >= 0 && fromIndex < existingItems.Count && index >= 0)
                            {
                                var item = existingItems[fromIndex];
                                existingItems.RemoveAt(fromIndex);
                                if (index >= existingItems.Count)
                                {
                                    existingItems.Add(item);
                                }
                                else
                                {
                                    existingItems.Insert(index, item);
                                }
                                structureChanged = true;
                            }
                        }
                        break;
                }
            }
        }

        // Phase 2: Apply property updates by final index
        // For "complete updates" (no Operations), also create items that don't exist yet
        if (propertyUpdate.Collection is { Count: > 0 })
        {
            foreach (var updateItem in propertyUpdate.Collection)
            {
                if (updateItem.Item is null)
                {
                    continue;
                }

                var index = ConvertIndexToInt(updateItem.Index);

                if (index >= 0 && index < existingItems.Count)
                {
                    // Update existing item at final index
                    existingItems[index].ApplySubjectPropertyUpdate(updateItem.Item, applyValuePropertyUpdate, subjectFactory);
                }
                else if (index >= 0 && subjectFactory is not null)
                {
                    // Create new item for index beyond existing collection (complete update case)
                    var newItem = subjectFactory.CreateCollectionSubject(registeredProperty, index);
                    newItem.Context.AddFallbackContext(subject.Context);
                    newItem.ApplySubjectPropertyUpdate(updateItem.Item, applyValuePropertyUpdate, subjectFactory, parentRegistry);

                    // Expand list to accommodate the index
                    while (existingItems.Count < index)
                    {
                        existingItems.Add(null!);
                    }

                    if (index >= existingItems.Count)
                    {
                        existingItems.Add(newItem);
                    }
                    else
                    {
                        existingItems[index] = newItem;
                    }
                    structureChanged = true;
                }
            }
        }

        // Update collection if structure changed
        if (structureChanged && subjectFactory is not null)
        {
            var collection = subjectFactory.CreateSubjectCollection(registeredProperty.Type, existingItems);
            using (SubjectChangeContext.WithChangedTimestamp(propertyUpdate.Timestamp))
            {
                registeredProperty.SetValue(collection);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static List<SubjectPropertyCollectionUpdate> CreateCollectionUpdates(
        IEnumerable<IInterceptorSubject> enumerable,
        ReadOnlySpan<ISubjectUpdateProcessor> processors,
        Dictionary<IInterceptorSubject, SubjectUpdate> knownSubjectUpdates,
        Dictionary<SubjectPropertyUpdate, SubjectPropertyUpdateReference>? propertyUpdates,
        HashSet<IInterceptorSubject> currentPath)
    {
        if (enumerable is ICollection<IInterceptorSubject> collection)
        {
            var index = 0;
            var collectionUpdates = new List<SubjectPropertyCollectionUpdate>(collection.Count);
            foreach (var itemSubject in collection)
            {
                collectionUpdates.Add(new SubjectPropertyCollectionUpdate
                {
                    Item = SubjectUpdateFactory.GetOrCreateCompleteUpdate(itemSubject, processors, knownSubjectUpdates, propertyUpdates, currentPath),
                    Index = index++
                });
            }

            return collectionUpdates;
        }
        else
        {
            var index = 0;
            var collectionUpdates = new List<SubjectPropertyCollectionUpdate>();
            foreach (var itemSubject in enumerable)
            {
                collectionUpdates.Add(new SubjectPropertyCollectionUpdate
                {
                    Item = SubjectUpdateFactory.GetOrCreateCompleteUpdate(itemSubject, processors, knownSubjectUpdates, propertyUpdates, currentPath),
                    Index = index++
                });
            }

            return collectionUpdates;
        }
    }

    private static (List<SubjectCollectionOperation>? operations, List<SubjectPropertyCollectionUpdate>? updates)
        CreateDiff(
            IEnumerable<IInterceptorSubject>? oldCollection,
            IEnumerable<IInterceptorSubject>? newCollection,
            ReadOnlySpan<ISubjectUpdateProcessor> processors,
            Dictionary<IInterceptorSubject, SubjectUpdate> knownSubjectUpdates,
            Dictionary<SubjectPropertyUpdate, SubjectPropertyUpdateReference>? propertyUpdates,
            HashSet<IInterceptorSubject> currentPath)
    {
        List<SubjectCollectionOperation>? operations = null;
        List<SubjectPropertyCollectionUpdate>? updates = null;

        if (newCollection is null)
        {
            return (null, null);
        }

        var oldItems = oldCollection?.ToList() ?? [];
        var newItems = newCollection.ToList();

        // Build lookup: subject instance -> old index (using reference equality)
        var oldIndexMap = new Dictionary<IInterceptorSubject, int>();
        for (var i = 0; i < oldItems.Count; i++)
        {
            oldIndexMap[oldItems[i]] = i;
        }

        var processedOldItems = new HashSet<IInterceptorSubject>();

        // Process new collection
        for (var newIndex = 0; newIndex < newItems.Count; newIndex++)
        {
            var newItem = newItems[newIndex];

            if (oldIndexMap.TryGetValue(newItem, out var oldIndex))
            {
                processedOldItems.Add(newItem);

                if (oldIndex != newIndex)
                {
                    operations ??= [];
                    operations.Add(new SubjectCollectionOperation
                    {
                        Action = SubjectCollectionOperationType.Move,
                        FromIndex = oldIndex,
                        Index = newIndex
                    });
                }

                var itemUpdate = SubjectUpdateFactory.GetOrCreateCompleteUpdate(
                    newItem, processors, knownSubjectUpdates, propertyUpdates, currentPath);

                if (itemUpdate.Properties.Count > 0 || itemUpdate.Reference.HasValue)
                {
                    updates ??= [];
                    updates.Add(new SubjectPropertyCollectionUpdate
                    {
                        Index = newIndex,
                        Item = itemUpdate
                    });
                }
            }
            else
            {
                operations ??= [];
                operations.Add(new SubjectCollectionOperation
                {
                    Action = SubjectCollectionOperationType.Insert,
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
                operations.Add(new SubjectCollectionOperation
                {
                    Action = SubjectCollectionOperationType.Remove,
                    Index = oldIndex
                });
            }
        }

        return (operations, updates);
    }

    private static int ConvertIndexToInt(object index)
    {
        if (index is int intIndex)
            return intIndex;

        if (index is JsonElement jsonElement)
            return jsonElement.GetInt32();

        return Convert.ToInt32(index);
    }
}
