using System.Collections;
using Namotion.Interceptor.Tracking.Performance;

namespace Namotion.Interceptor.Connectors.Updates.Internal;

/// <summary>
/// Builds collection and dictionary updates for <see cref="SubjectUpdate"/> instances.
/// Handles both complete updates (full snapshot) and diff updates (changes only).
/// </summary>
internal static class SubjectItemsUpdateFactory
{
    private static readonly ObjectPool<CollectionDiffBuilder> ChangeBuilderPool = new(() => new CollectionDiffBuilder());

    /// <summary>
    /// Emits one Insert operation per new item and completes its payload. A collection indexes by
    /// position and a dictionary by key, which is the only difference between the two diff paths, so
    /// <typeparamref name="TIndex"/> carries it and each instantiation boxes exactly what the index
    /// assignment boxed before.
    /// </summary>
    private static void AddInsertOperations<TIndex>(
        List<(TIndex Index, IInterceptorSubject Item)>? newItems,
        ref List<SubjectCollectionOperation>? operations,
        SubjectUpdateBuilder builder)
    {
        if (newItems is null)
            return;

        foreach (var (index, item) in newItems)
        {
            var itemId = builder.GetOrCreateId(item);
            SubjectUpdateFactory.ProcessSubjectComplete(item, builder);

            operations ??= [];
            operations.Add(new SubjectCollectionOperation
            {
                Action = SubjectCollectionOperationType.Insert,
                Index = index!,
                Id = itemId
            });
        }
    }

    /// <summary>
    /// Builds a complete collection update with all items.
    /// </summary>
    internal static void BuildCollectionComplete(
        SubjectPropertyUpdate update,
        object? collectionValue,
        SubjectUpdateBuilder builder)
    {
        update.Kind = SubjectPropertyUpdateKind.Collection;

        if (collectionValue is null)
            return;

        var items = SubjectValueConvert.ToSubjectList(collectionValue);
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
        object? oldCollectionValue,
        object? newCollectionValue,
        SubjectUpdateBuilder builder)
    {
        update.Kind = SubjectPropertyUpdateKind.Collection;

        if (newCollectionValue is null)
            return;

        var oldItems = oldCollectionValue is not null ? SubjectValueConvert.ToSubjectList(oldCollectionValue) : (IReadOnlyList<IInterceptorSubject>)[];
        var newItems = SubjectValueConvert.ToSubjectList(newCollectionValue);
        update.Count = newItems.Count;

        var changeBuilder = ChangeBuilderPool.Rent();
        try
        {
            changeBuilder.GetCollectionChanges(
                oldItems, newItems,
                out var operations,
                out var newItemsToProcess,
                out var reorderedItems);

            AddInsertOperations(newItemsToProcess, ref operations, builder);

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
            foreach (var item in changeBuilder.GetRetainedItems())
            {
                if (builder.TryGetIdWithUpdates(item) is { } itemId)
                {
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
        object? dictionaryValue,
        SubjectUpdateBuilder builder)
    {
        update.Kind = SubjectPropertyUpdateKind.Dictionary;

        if (dictionaryValue is null)
            return;

        var dictionary = SubjectValueConvert.ToSubjectDictionary(dictionaryValue);
        update.Items = new List<SubjectPropertyItemUpdate>(dictionary.Count);

        foreach (DictionaryEntry entry in dictionary)
        {
            if (entry.Value is not IInterceptorSubject subject) continue;

            var itemId = builder.GetOrCreateId(subject);
            SubjectUpdateFactory.ProcessSubjectComplete(subject, builder);

            update.Items.Add(new SubjectPropertyItemUpdate
            {
                Index = entry.Key,
                Id = itemId
            });
        }

        update.Count = update.Items.Count;
    }

    /// <summary>
    /// Builds a diff dictionary update with Insert, Remove operations and sparse property updates.
    /// </summary>
    internal static void BuildDictionaryDiff(
        SubjectPropertyUpdate update,
        object? oldDictionaryValue,
        object? newDictionaryValue,
        SubjectUpdateBuilder builder)
    {
        update.Kind = SubjectPropertyUpdateKind.Dictionary;

        if (newDictionaryValue is null)
            return;

        var oldDictionary = oldDictionaryValue is not null ? SubjectValueConvert.ToSubjectDictionary(oldDictionaryValue) : null;
        var newDictionary = SubjectValueConvert.ToSubjectDictionary(newDictionaryValue);

        var changeBuilder = ChangeBuilderPool.Rent();
        try
        {
            changeBuilder.GetDictionaryChanges(
                oldDictionary, newDictionary,
                out var operations,
                out var newItemsToProcess,
                out var removedKeys);

            // Subject-only count, matching the apply side's filtered view and Collection semantics:
            // every subject-valued entry in newDictionary is either a new insert or a retained common item.
            update.Count = (newItemsToProcess?.Count ?? 0) + changeBuilder.GetRetainedDictionaryItems().Count;

            // Add Remove operations before the Insert operations: a value replaced at an existing key
            // appears in both lists, and the apply side walks the operations sequentially, so an Insert
            // emitted first would be undone by the Remove that follows it.
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

            AddInsertOperations(newItemsToProcess, ref operations, builder);

            // Generate sparse updates for entries retained by reference (common items).
            // changeBuilder already partitioned new-vs-retained during GetDictionaryChanges,
            // so iterate that output instead of re-walking newDictionary.
            List<SubjectPropertyItemUpdate>? updates = null;
            foreach (var (key, item) in changeBuilder.GetRetainedDictionaryItems())
            {
                if (builder.TryGetIdWithUpdates(item) is not { } itemId)
                    continue;

                updates ??= [];
                updates.Add(new SubjectPropertyItemUpdate
                {
                    Index = key,
                    Id = itemId
                });
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
