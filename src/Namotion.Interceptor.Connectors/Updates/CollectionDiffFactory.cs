using System.Collections;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Connectors.Updates;

/// <summary>
/// Factory for creating collection/dictionary diff updates.
/// Produces two separate outputs:
/// - Operations: structural changes (Remove, Insert, Move) - ordered
/// - Updates: sparse property updates by final index
/// </summary>
internal static class CollectionDiffFactory
{
    /// <summary>
    /// Creates a diff between old and new array/list collections.
    /// Returns structural operations and property updates separately.
    /// </summary>
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

        // Handle null case - consumer uses Count=0
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
                    // Item moved - add Move operation (no item data, just indices)
                    operations ??= [];
                    operations.Add(new CollectionOperation
                    {
                        Action = CollectionOperationType.Move,
                        FromIndex = oldIndex,
                        Index = newIndex
                    });
                }

                // Check for property changes - add to updates if any
                var itemUpdate = SubjectUpdateFactory.GetOrCreateCompleteUpdate(
                    newItem, processors, knownSubjectUpdates, propertyUpdates, currentPath);

                if (itemUpdate.Properties.Count > 0 || itemUpdate.Reference.HasValue)
                {
                    updates ??= [];
                    updates.Add(new SubjectPropertyCollectionUpdate
                    {
                        Index = newIndex,  // FINAL index after operations
                        Item = itemUpdate
                    });
                }
            }
            else
            {
                // New item - Insert operation with full item data
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

        // Find removed items (in old but not processed)
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

    /// <summary>
    /// Creates a diff between old and new dictionaries.
    /// Returns structural operations and property updates separately.
    /// Move is not applicable for dictionaries.
    /// </summary>
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

        // Handle null case - consumer uses Count=0
        if (newDictionary is null)
        {
            return (null, null);
        }

        var oldKeys = new HashSet<object>(
            oldDictionary?.Keys.Cast<object>() ?? Enumerable.Empty<object>());

        // Process new dictionary
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
                // New key - Insert operation with full item data
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
