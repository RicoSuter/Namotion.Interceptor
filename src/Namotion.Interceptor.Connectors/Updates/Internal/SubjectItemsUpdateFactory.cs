using System.Collections;

namespace Namotion.Interceptor.Connectors.Updates.Internal;

/// <summary>
/// Builds collection and dictionary updates for <see cref="SubjectUpdate"/> instances.
/// Handles both complete updates (full snapshot) and diff updates (changes only).
/// Collection and dictionary values arrive as <see cref="object"/> and are normalized through
/// <see cref="SubjectValueConvert"/>, so read-only and immutable property types that implement
/// neither <see cref="IDictionary"/> nor <see cref="IEnumerable{T}"/> of subject are serialized
/// with their real contents instead of being seen as empty.
/// </summary>
internal static class SubjectItemsUpdateFactory
{
    /// <summary>
    /// Builds a complete collection update with all items.
    /// Items array order defines collection ordering.
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
    /// Builds a collection update with complete item ordering but only full properties for newly inserted items.
    /// Uses the old collection to determine which items are new (need full properties) vs existing (just ID reference).
    /// This avoids computing diff operations (Move/Insert/Remove) which can be incorrect when old values are stale
    /// (e.g., from the retry queue after reconnection or after ChangeQueueProcessor deduplication).
    /// </summary>
    internal static void BuildCollectionUpdate(
        SubjectPropertyUpdate update,
        object? oldCollectionValue,
        object newCollectionValue,
        SubjectUpdateBuilder builder)
    {
        update.Kind = SubjectPropertyUpdateKind.Collection;

        var newItems = SubjectValueConvert.ToSubjectList(newCollectionValue);
        update.Items = new List<SubjectPropertyItemUpdate>(newItems.Count);

        // Items in the old collection are assumed known to receivers (via Welcome or previous updates)
        HashSet<IInterceptorSubject>? oldItemSet = null;
        if (oldCollectionValue is not null)
        {
            var oldItems = SubjectValueConvert.ToSubjectList(oldCollectionValue);
            for (var i = 0; i < oldItems.Count; i++)
            {
                oldItemSet ??= new(ReferenceEqualityComparer.Instance);
                oldItemSet.Add(oldItems[i]);
            }
        }

        for (var i = 0; i < newItems.Count; i++)
        {
            var item = newItems[i];
            var itemId = builder.GetOrCreateId(item);

            // Only include full properties for items not in the old collection
            // (they are new and receivers need full data to create them)
            if (oldItemSet is null || !oldItemSet.Contains(item))
            {
                SubjectUpdateFactory.ProcessSubjectComplete(item, builder);
            }

            update.Items.Add(new SubjectPropertyItemUpdate
            {
                Id = itemId
            });
        }
    }

    /// <summary>
    /// Builds a complete dictionary update with all entries.
    /// Each item includes id + key.
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
    /// Builds a dictionary update with complete entries but only full properties for newly added items.
    /// Uses the old dictionary to determine which items are new (need full properties) vs existing (just ID + key).
    /// This avoids computing diff operations (Insert/Remove) which can be incorrect when old values are stale.
    /// </summary>
    internal static void BuildDictionaryUpdate(
        SubjectPropertyUpdate update,
        object? oldDictionaryValue,
        object newDictionaryValue,
        SubjectUpdateBuilder builder)
    {
        update.Kind = SubjectPropertyUpdateKind.Dictionary;

        var oldDictionary = oldDictionaryValue is not null
            ? SubjectValueConvert.ToSubjectDictionary(oldDictionaryValue)
            : null;

        var newDictionary = SubjectValueConvert.ToSubjectDictionary(newDictionaryValue);
        update.Items = new List<SubjectPropertyItemUpdate>(newDictionary.Count);

        foreach (DictionaryEntry entry in newDictionary)
        {
            if (entry.Value is IInterceptorSubject item)
            {
                var itemId = builder.GetOrCreateId(item);

                // Only include full properties for items not in the old dictionary
                var isExisting = oldDictionary is not null
                    && oldDictionary.Contains(entry.Key)
                    && ReferenceEquals(oldDictionary[entry.Key], item);

                if (!isExisting)
                {
                    SubjectUpdateFactory.ProcessSubjectComplete(item, builder);
                }

                update.Items.Add(new SubjectPropertyItemUpdate
                {
                    Id = itemId,
                    Key = entry.Key.ToString()
                });
            }
        }
    }
}
