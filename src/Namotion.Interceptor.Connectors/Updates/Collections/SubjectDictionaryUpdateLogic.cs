using System.Collections;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Updates.Collections;

/// <summary>
/// Internal logic for creating and applying dictionary collection property updates.
/// </summary>
internal static class SubjectDictionaryUpdateLogic
{
    /// <summary>
    /// Applies a dictionary to a property update (create side for complete updates).
    /// Sets Kind to Collection and creates all item updates.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void ApplyToUpdate(
        SubjectPropertyUpdate update,
        IDictionary? dictionary,
        ReadOnlySpan<ISubjectUpdateProcessor> processors,
        Dictionary<IInterceptorSubject, SubjectUpdate> knownSubjectUpdates,
        Dictionary<SubjectPropertyUpdate, SubjectPropertyUpdateReference>? propertyUpdates,
        HashSet<IInterceptorSubject> currentPath)
    {
        update.Kind = SubjectPropertyUpdateKind.Collection;
        update.Collection = dictionary is not null
            ? CreateCollectionUpdates(dictionary, processors, knownSubjectUpdates, propertyUpdates, currentPath)
            : null;
    }

    /// <summary>
    /// Applies a dictionary diff to a property update (create side for partial updates).
    /// Produces structural Operations (Remove, Insert) and sparse Collection updates.
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

        var oldDict = oldValue as IDictionary;
        var newDict = newValue as IDictionary;

        var (operations, updates) = CreateDiff(
            oldDict, newDict, processors, knownSubjectUpdates, propertyUpdates, currentPath);

        update.Operations = operations;
        update.Collection = updates;
        update.Count = newDict?.Count;
    }

    /// <summary>
    /// Applies dictionary updates using two-phase approach:
    /// Phase 1: Apply structural operations (Remove, Insert)
    /// Phase 2: Apply sparse property updates by key
    /// </summary>
    internal static void ApplyFromUpdate(
        IInterceptorSubject subject,
        RegisteredSubjectProperty registeredProperty,
        SubjectPropertyUpdate propertyUpdate,
        Action<RegisteredSubjectProperty, SubjectPropertyUpdate> applyValuePropertyUpdate,
        ISubjectFactory? subjectFactory)
    {
        var existingDict = registeredProperty.GetValue() as IDictionary;
        var parentRegistry = subject.Context.GetService<ISubjectRegistry>();
        var structureChanged = false;

        // Create a working copy of the dictionary
        var workingDict = new Dictionary<object, IInterceptorSubject>();
        if (existingDict is not null)
        {
            foreach (DictionaryEntry entry in existingDict)
            {
                if (entry.Value is IInterceptorSubject item)
                {
                    workingDict[entry.Key] = item;
                }
            }
        }

        // Phase 1: Apply structural operations
        if (propertyUpdate.Operations is { Count: > 0 })
        {
            foreach (var operation in propertyUpdate.Operations)
            {
                var key = ConvertKey(operation.Index);

                switch (operation.Action)
                {
                    case SubjectCollectionOperationType.Remove:
                        if (workingDict.Remove(key))
                        {
                            structureChanged = true;
                        }
                        break;

                    case SubjectCollectionOperationType.Insert:
                        if (subjectFactory is not null && operation.Item is not null)
                        {
                            var newItem = subjectFactory.CreateCollectionSubject(registeredProperty, key);
                            newItem.Context.AddFallbackContext(subject.Context);
                            newItem.ApplySubjectPropertyUpdate(operation.Item, applyValuePropertyUpdate, subjectFactory, parentRegistry);
                            workingDict[key] = newItem;
                            structureChanged = true;
                        }
                        break;

                    // Move is not applicable for dictionaries
                }
            }
        }

        // Phase 2: Apply property updates by key
        // For "complete updates" (no Operations), also create items that don't exist yet
        if (propertyUpdate.Collection is { Count: > 0 })
        {
            foreach (var updateItem in propertyUpdate.Collection)
            {
                if (updateItem.Item is null)
                {
                    continue;
                }

                var key = ConvertKey(updateItem.Index);

                if (workingDict.TryGetValue(key, out var existingItem))
                {
                    // Update existing item at key
                    existingItem.ApplySubjectPropertyUpdate(updateItem.Item, applyValuePropertyUpdate, subjectFactory);
                }
                else if (subjectFactory is not null)
                {
                    // Create new item for key that doesn't exist (complete update case)
                    var newItem = subjectFactory.CreateCollectionSubject(registeredProperty, key);
                    newItem.Context.AddFallbackContext(subject.Context);
                    newItem.ApplySubjectPropertyUpdate(updateItem.Item, applyValuePropertyUpdate, subjectFactory, parentRegistry);
                    workingDict[key] = newItem;
                    structureChanged = true;
                }
            }
        }

        // Update dictionary if structure changed
        if (structureChanged && subjectFactory is not null)
        {
            var dictionary = subjectFactory.CreateSubjectDictionary(registeredProperty.Type, workingDict);
            using (SubjectChangeContext.WithChangedTimestamp(propertyUpdate.Timestamp))
            {
                registeredProperty.SetValue(dictionary);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static List<SubjectPropertyCollectionUpdate> CreateCollectionUpdates(
        IDictionary dictionary,
        ReadOnlySpan<ISubjectUpdateProcessor> processors,
        Dictionary<IInterceptorSubject, SubjectUpdate> knownSubjectUpdates,
        Dictionary<SubjectPropertyUpdate, SubjectPropertyUpdateReference>? propertyUpdates,
        HashSet<IInterceptorSubject> currentPath)
    {
        var collectionUpdates = new List<SubjectPropertyCollectionUpdate>(dictionary.Count);
        foreach (DictionaryEntry entry in dictionary)
        {
            var item = entry.Value as IInterceptorSubject;
            collectionUpdates.Add(new SubjectPropertyCollectionUpdate
            {
                Item = item is not null
                    ? SubjectUpdateFactory.GetOrCreateCompleteUpdate(item, processors, knownSubjectUpdates, propertyUpdates, currentPath)
                    : null,
                Index = entry.Key
            });
        }

        return collectionUpdates;
    }

    private static (List<SubjectCollectionOperation>? operations, List<SubjectPropertyCollectionUpdate>? updates)
        CreateDiff(
            IDictionary? oldDictionary,
            IDictionary? newDictionary,
            ReadOnlySpan<ISubjectUpdateProcessor> processors,
            Dictionary<IInterceptorSubject, SubjectUpdate> knownSubjectUpdates,
            Dictionary<SubjectPropertyUpdate, SubjectPropertyUpdateReference>? propertyUpdates,
            HashSet<IInterceptorSubject> currentPath)
    {
        List<SubjectCollectionOperation>? operations = null;
        List<SubjectPropertyCollectionUpdate>? updates = null;

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
                operations.Add(new SubjectCollectionOperation
                {
                    Action = SubjectCollectionOperationType.Insert,
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
            operations.Add(new SubjectCollectionOperation
            {
                Action = SubjectCollectionOperationType.Remove,
                Index = removedKey
            });
        }

        return (operations, updates);
    }

    private static object ConvertKey(object key)
    {
        if (key is JsonElement jsonElement)
        {
            return jsonElement.GetString() ?? jsonElement.ToString();
        }

        return key;
    }
}
