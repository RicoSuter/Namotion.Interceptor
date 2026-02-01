using System.Collections;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Connectors;

public static class SubjectFactoryExtensions
{
    /// <summary>
    /// Appends subjects to an existing collection, returning a new collection of the same type.
    /// </summary>
    public static T AppendSubjectsToCollection<T>(
        this ISubjectFactory factory,
        T existingCollection,
        params IInterceptorSubject[] subjectsToAppend)
        where T : IEnumerable<IInterceptorSubject?>
    {
        var propertyType = existingCollection.GetType();
        var combined = existingCollection.Concat(subjectsToAppend);
        return (T)factory.CreateSubjectCollection(propertyType, combined);
    }

    /// <summary>
    /// Removes subjects at specified indices from a collection, returning a new collection of the same type.
    /// </summary>
    public static T RemoveSubjectsFromCollection<T>(
        this ISubjectFactory factory,
        T existingCollection,
        params int[] indicesToRemove)
        where T : IEnumerable<IInterceptorSubject?>
    {
        var propertyType = existingCollection.GetType();
        var toRemove = new HashSet<int>(indicesToRemove);
        var filtered = existingCollection.Where((_, index) => !toRemove.Contains(index));
        return (T)factory.CreateSubjectCollection(propertyType, filtered);
    }

    /// <summary>
    /// Appends entries to an existing dictionary, returning a new dictionary of the same type.
    /// </summary>
    public static T AppendEntriesToDictionary<T>(
        this ISubjectFactory factory,
        T existingDictionary,
        params KeyValuePair<object, IInterceptorSubject>[] entriesToAdd)
        where T : IDictionary
    {
        var dictionaryType = existingDictionary.GetType();
        var newDictionary = (IDictionary)Activator.CreateInstance(dictionaryType)!;

        foreach (DictionaryEntry entry in existingDictionary)
        {
            newDictionary[entry.Key] = entry.Value;
        }

        foreach (var entry in entriesToAdd)
        {
            newDictionary[entry.Key] = entry.Value;
        }

        return (T)newDictionary;
    }

    /// <summary>
    /// Removes entries by key from a dictionary, returning a new dictionary of the same type.
    /// </summary>
    public static T RemoveEntriesFromDictionary<T>(
        this ISubjectFactory factory,
        T existingDictionary,
        params object[] keysToRemove)
        where T : IDictionary
    {
        var dictionaryType = existingDictionary.GetType();
        var newDictionary = (IDictionary)Activator.CreateInstance(dictionaryType)!;
        var removeSet = new HashSet<object>(keysToRemove);

        foreach (DictionaryEntry entry in existingDictionary)
        {
            if (!removeSet.Contains(entry.Key))
            {
                newDictionary[entry.Key] = entry.Value;
            }
        }

        return (T)newDictionary;
    }

    public static IInterceptorSubject CreateSubjectForReferenceProperty(this ISubjectFactory subjectFactory, RegisteredSubjectProperty property)
    {
        var serviceProvider = property.Parent.Subject.Context.TryGetService<IServiceProvider>();
        return subjectFactory.CreateSubject(property.Type, serviceProvider);
    }

    public static IInterceptorSubject CreateSubjectForCollectionOrDictionaryProperty(this ISubjectFactory subjectFactory, RegisteredSubjectProperty property)
    {
        Type? itemType;
        if (property.Type.IsArray)
        {
            itemType = property.Type.GetElementType();
        }
        else if (property.Type.GenericTypeArguments.Length == 2)
        {
            // Dictionary<TKey, TValue> - use the value type (index 1)
            itemType = property.Type.GenericTypeArguments[1];
        }
        else
        {
            // List<T>, ICollection<T>, etc. - use the element type (index 0)
            itemType = property.Type.GenericTypeArguments[0];
        }

        var serviceProvider = property.Parent.Subject.Context.TryGetService<IServiceProvider>();
        return subjectFactory.CreateSubject(
            itemType ?? throw new InvalidOperationException("Unknown collection element type"),
            serviceProvider);
    }
}
