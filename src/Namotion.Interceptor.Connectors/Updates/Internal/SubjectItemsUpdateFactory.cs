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
                Index = i,
                Id = itemId
            });
        }
    }

    /// <summary>
    /// Builds a diff collection update with Insert, Remove, Move operations and sparse property updates.
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
                out var operations,
                out var newItemsToProcess,
                out var reorderedItems);

            // Add Insert operations for new items
            if (newItemsToProcess is not null)
            {
                foreach (var (index, item) in newItemsToProcess)
                {
                    var itemId = builder.GetOrCreateId(item);
                    SubjectUpdateFactory.ProcessSubjectComplete(item, builder);

                    operations ??= [];
                    operations.Add(new SubjectCollectionOperation
                    {
                        Action = SubjectCollectionOperationType.Insert,
                        Index = index,
                        Id = itemId
                    });
                }
            }

            // Add Move operations for reordered items
            if (reorderedItems is not null)
            {
                foreach (var (oldIndex, newIndex, _) in reorderedItems)
                {
                    operations ??= [];
                    operations.Add(new SubjectCollectionOperation
                    {
                        Action = SubjectCollectionOperationType.Move,
                        FromIndex = oldIndex,
                        Index = newIndex
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
                    var newIndex = changeBuilder.GetNewIndex(item);
                    updates ??= [];
                    updates.Add(new SubjectPropertyItemUpdate
                    {
                        Index = newIndex,
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
                    Index = entry.Key,
                    Id = itemId
                });
            }
        }
    }

    /// <summary>
    /// Builds a diff dictionary update with Insert, Remove operations and sparse property updates.
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
                out var operations,
                out var newItemsToProcess,
                out var removedKeys);

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
                        Index = key,
                        Id = itemId
                    });
                }
            }

            // Add Remove operations
            if (removedKeys is not null)
            {
                foreach (var removedKey in removedKeys)
                {
                    operations ??= [];
                    operations.Add(new SubjectCollectionOperation
                    {
                        Action = SubjectCollectionOperationType.Remove,
                        Index = removedKey
                    });
                }
            }

            // Check for property updates on existing items
            // Build HashSet for O(1) lookup instead of O(n) Any() per iteration
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
                        Index = key,
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
}
