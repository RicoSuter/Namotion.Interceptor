using System.Collections;
using Namotion.Interceptor.Registry.Performance;

namespace Namotion.Interceptor.Connectors.Updates.Internal;

/// <summary>
/// Builds collection and dictionary updates for <see cref="SubjectUpdate"/> instances.
/// Handles both complete updates (full snapshot) and diff updates (changes only).
/// </summary>
internal static class CollectionUpdateBuilder
{
    private static readonly ObjectPool<CollectionChangeBuilder> ChangeBuilderPool = new(() => new CollectionChangeBuilder());

    /// <summary>
    /// Builds a complete collection update with all items.
    /// </summary>
    internal static void BuildCollectionComplete(
        SubjectPropertyUpdate update,
        IEnumerable<IInterceptorSubject>? collection,
        UpdateContext context)
    {
        update.Kind = SubjectPropertyUpdateKind.Collection;

        if (collection is null)
            return;

        var items = collection.ToList();
        update.Count = items.Count;
        update.Collection = new List<SubjectPropertyCollectionUpdate>(items.Count);

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var itemId = context.GetOrCreateId(item);
            SubjectUpdateFactory.ProcessSubjectComplete(item, context);

            update.Collection.Add(new SubjectPropertyCollectionUpdate
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
        UpdateContext context)
    {
        update.Kind = SubjectPropertyUpdateKind.Collection;

        if (newCollection is null)
            return;

        var oldItems = oldCollection?.ToList() ?? [];
        var newItems = newCollection.ToList();
        update.Count = newItems.Count;

        var changeBuilder = ChangeBuilderPool.Rent();
        try
        {
            changeBuilder.BuildCollectionChanges(
                oldItems, newItems,
                out var operations,
                out var newItemsToProcess,
                out var reorderedItems);

            // Add Insert operations for new items
            foreach (var (index, item) in newItemsToProcess)
            {
                var itemId = context.GetOrCreateId(item);
                SubjectUpdateFactory.ProcessSubjectComplete(item, context);

                operations ??= [];
                operations.Add(new SubjectCollectionOperation
                {
                    Action = SubjectCollectionOperationType.Insert,
                    Index = index,
                    Id = itemId
                });
            }

            // Add Move operations for reordered items
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

            // Generate sparse updates for common items with property changes
            List<SubjectPropertyCollectionUpdate>? updates = null;
            foreach (var item in changeBuilder.GetCommonItems())
            {
                if (context.SubjectHasUpdates(item))
                {
                    var itemId = context.GetOrCreateId(item);
                    var newIndex = changeBuilder.GetNewIndex(item);
                    updates ??= [];
                    updates.Add(new SubjectPropertyCollectionUpdate
                    {
                        Index = newIndex,
                        Id = itemId
                    });
                }
            }

            update.Operations = operations;
            update.Collection = updates;
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
        UpdateContext context)
    {
        update.Kind = SubjectPropertyUpdateKind.Collection;

        if (dictionary is null)
            return;

        update.Count = dictionary.Count;
        update.Collection = new List<SubjectPropertyCollectionUpdate>(dictionary.Count);

        foreach (DictionaryEntry entry in dictionary)
        {
            if (entry.Value is IInterceptorSubject item)
            {
                var itemId = context.GetOrCreateId(item);
                SubjectUpdateFactory.ProcessSubjectComplete(item, context);

                update.Collection.Add(new SubjectPropertyCollectionUpdate
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
        UpdateContext context)
    {
        update.Kind = SubjectPropertyUpdateKind.Collection;

        if (newDict is null)
            return;

        update.Count = newDict.Count;

        var changeBuilder = ChangeBuilderPool.Rent();
        try
        {
            changeBuilder.BuildDictionaryChanges(
                oldDict, newDict,
                out var operations,
                out var newItemsToProcess,
                out var removedKeys);

            // Add Insert operations for new items
            foreach (var (key, item) in newItemsToProcess)
            {
                var itemId = context.GetOrCreateId(item);
                SubjectUpdateFactory.ProcessSubjectComplete(item, context);

                operations ??= [];
                operations.Add(new SubjectCollectionOperation
                {
                    Action = SubjectCollectionOperationType.Insert,
                    Index = key,
                    Id = itemId
                });
            }

            // Add Remove operations
            foreach (var removedKey in removedKeys)
            {
                operations ??= [];
                operations.Add(new SubjectCollectionOperation
                {
                    Action = SubjectCollectionOperationType.Remove,
                    Index = removedKey
                });
            }

            // Check for property updates on existing items
            List<SubjectPropertyCollectionUpdate>? updates = null;
            foreach (DictionaryEntry entry in newDict)
            {
                var key = entry.Key;
                var item = entry.Value as IInterceptorSubject;
                if (item is null)
                    continue;

                // Skip if this is a new item (already handled above)
                var isNewItem = newItemsToProcess.Any(n => Equals(n.key, key));
                if (isNewItem)
                    continue;

                if (context.SubjectHasUpdates(item))
                {
                    var itemId = context.GetOrCreateId(item);
                    updates ??= [];
                    updates.Add(new SubjectPropertyCollectionUpdate
                    {
                        Index = key,
                        Id = itemId
                    });
                }
            }

            update.Operations = operations;
            update.Collection = updates;
        }
        finally
        {
            changeBuilder.Clear();
            ChangeBuilderPool.Return(changeBuilder);
        }
    }
}
