using System.Collections;
using Namotion.Interceptor.Registry.Performance;

namespace Namotion.Interceptor.Connectors.Updates.Internal;

/// <summary>
/// Builds collection and dictionary updates for <see cref="SubjectUpdate"/> instances.
/// Handles both complete updates (full snapshot) and diff updates (changes only).
/// </summary>
internal static class SubjectItemsUpdateFactory
{
    private static readonly ObjectPool<CollectionDiffBuilder> ChangeBuilderPool = new(() => new CollectionDiffBuilder());

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
}
