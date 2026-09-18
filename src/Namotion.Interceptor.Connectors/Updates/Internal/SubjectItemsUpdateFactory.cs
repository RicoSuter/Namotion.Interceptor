using System.Collections;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Connectors.Updates.Internal;

/// <summary>
/// Builds collection and dictionary updates for <see cref="SubjectUpdate"/> instances. The items always
/// state the complete membership. Collection and dictionary values arrive as <see cref="object"/> and are
/// normalized through <see cref="SubjectValueConvert"/>, so read-only and immutable property types that
/// implement neither <see cref="IDictionary"/> nor <see cref="IEnumerable{T}"/> of subject are serialized
/// with their real contents instead of being seen as empty.
/// </summary>
internal static class SubjectItemsUpdateFactory
{
    /// <summary>
    /// States the members of <paramref name="value"/>, a <c>null</c> value as no items. A member that is not a
    /// member of <paramref name="previousValue"/> is stated through
    /// <see cref="SubjectUpdateFactory.StateMember"/>, and every member is when there is no previous value.
    /// </summary>
    internal static void BuildItems(
        SubjectPropertyUpdate update,
        bool isDictionary,
        RegisteredSubjectProperty? property,
        object? value,
        object? previousValue,
        bool isChange,
        SubjectUpdateBuilder builder)
    {
        update.Kind = isDictionary ? SubjectPropertyUpdateKind.Dictionary : SubjectPropertyUpdateKind.Collection;
        if (value is null)
            return;

        if (isDictionary)
            BuildDictionaryItems(update, property, value, previousValue, isChange, builder);
        else
            BuildCollectionItems(update, property, value, previousValue, isChange, builder);
    }

    private static void BuildCollectionItems(
        SubjectPropertyUpdate update,
        RegisteredSubjectProperty? property,
        object value,
        object? previousValue,
        bool isChange,
        SubjectUpdateBuilder builder)
    {
        HashSet<IInterceptorSubject>? previousItems = null;
        if (previousValue is not null)
        {
            previousItems = builder.PreviousItems;
            var items = SubjectValueConvert.ToSubjectList(previousValue);
            for (var i = 0; i < items.Count; i++)
            {
                previousItems.Add(items[i]);
            }
        }

        try
        {
            var newItems = SubjectValueConvert.ToSubjectList(value);
            update.Items = new List<SubjectPropertyItemUpdate>(newItems.Count);
            for (var i = 0; i < newItems.Count; i++)
            {
                var item = newItems[i];
                if (previousItems?.Contains(item) != true)
                    SubjectUpdateFactory.StateMember(item, property, isChange, builder);

                update.Items.Add(new SubjectPropertyItemUpdate { Id = builder.GetOrCreateId(item) });
            }
        }
        finally
        {
            previousItems?.Clear();
        }
    }

    private static void BuildDictionaryItems(
        SubjectPropertyUpdate update,
        RegisteredSubjectProperty? property,
        object value,
        object? previousValue,
        bool isChange,
        SubjectUpdateBuilder builder)
    {
        var previousDictionary = previousValue is not null
            ? SubjectValueConvert.ToSubjectDictionary(previousValue)
            : null;

        var dictionary = SubjectValueConvert.ToSubjectDictionary(value);
        update.Items = new List<SubjectPropertyItemUpdate>(dictionary.Count);
        foreach (DictionaryEntry entry in dictionary)
        {
            if (entry.Value is not IInterceptorSubject item)
                continue;

            var isPreviousMember = previousDictionary is not null &&
                previousDictionary.Contains(entry.Key) &&
                ReferenceEquals(previousDictionary[entry.Key], item);

            if (!isPreviousMember)
                SubjectUpdateFactory.StateMember(item, property, isChange, builder);

            update.Items.Add(new SubjectPropertyItemUpdate
            {
                Id = builder.GetOrCreateId(item),
                Key = entry.Key.ToString()
            });
        }
    }
}
