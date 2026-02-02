using System.Collections;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Applies structural changes from external sources to the C# model.
/// This is the symmetric counterpart to <see cref="GraphChangePublisher"/>:
/// - <see cref="GraphChangePublisher"/>: Observes C# model changes and calls abstract methods for sending to external systems.
/// - <see cref="GraphChangeApplier"/>: Receives external changes and applies them to the C# model (collections, references, dictionaries).
///
/// All methods include a source parameter for loop prevention using SetValueFromSource on properties.
/// </summary>
public class GraphChangeApplier
{
    private readonly ISubjectFactory _subjectFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="GraphChangeApplier"/> class.
    /// </summary>
    /// <param name="subjectFactory">The subject factory used for collection manipulation.</param>
    public GraphChangeApplier(ISubjectFactory? subjectFactory = null)
    {
        _subjectFactory = subjectFactory ?? DefaultSubjectFactory.Instance;
    }

    /// <summary>
    /// Adds a subject to a collection property.
    /// </summary>
    /// <param name="property">The collection property to add to.</param>
    /// <param name="subject">The subject to add.</param>
    /// <param name="source">The source of the change (for loop prevention).</param>
    /// <param name="index">Optional index hint (not used for append, but may be used for ordered insertion in the future).</param>
    /// <returns>True if the subject was added successfully, false otherwise.</returns>
    public bool AddToCollection(RegisteredSubjectProperty property, IInterceptorSubject subject, object? source, object? index = null)
    {
        if (!property.IsSubjectCollection)
        {
            return false;
        }

        var currentCollection = property.GetValue() as IEnumerable<IInterceptorSubject?>;
        if (currentCollection is null)
        {
            return false;
        }

        var newCollection = _subjectFactory.AppendSubjectsToCollection(currentCollection, subject);
        property.SetValueFromSource(source!, DateTimeOffset.UtcNow, null, newCollection);
        return true;
    }

    /// <summary>
    /// Removes a subject from a collection property by reference.
    /// </summary>
    /// <param name="property">The collection property to remove from.</param>
    /// <param name="subject">The subject to remove.</param>
    /// <param name="source">The source of the change (for loop prevention).</param>
    /// <returns>True if the subject was found and removed, false otherwise.</returns>
    public bool RemoveFromCollection(RegisteredSubjectProperty property, IInterceptorSubject subject, object? source)
    {
        if (!property.IsSubjectCollection)
        {
            return false;
        }

        var currentCollection = property.GetValue() as IEnumerable<IInterceptorSubject?>;
        if (currentCollection is null)
        {
            return false;
        }

        // Find the index of the subject
        var list = currentCollection.ToList();
        var index = -1;
        for (var i = 0; i < list.Count; i++)
        {
            if (ReferenceEquals(list[i], subject))
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            return false;
        }

        var newCollection = _subjectFactory.RemoveSubjectsFromCollection(currentCollection, index);
        property.SetValueFromSource(source!, DateTimeOffset.UtcNow, null, newCollection);
        return true;
    }

    /// <summary>
    /// Removes a subject from a collection property by index.
    /// </summary>
    /// <param name="property">The collection property to remove from.</param>
    /// <param name="index">The index of the subject to remove.</param>
    /// <param name="source">The source of the change (for loop prevention).</param>
    /// <returns>True if the subject at the index was removed, false otherwise.</returns>
    public bool RemoveFromCollectionByIndex(RegisteredSubjectProperty property, int index, object? source)
    {
        if (!property.IsSubjectCollection)
        {
            return false;
        }

        var currentCollection = property.GetValue() as IEnumerable<IInterceptorSubject?>;
        if (currentCollection is null)
        {
            return false;
        }

        var list = currentCollection.ToList();
        if (index < 0 || index >= list.Count)
        {
            return false;
        }

        var newCollection = _subjectFactory.RemoveSubjectsFromCollection(currentCollection, index);
        property.SetValueFromSource(source!, DateTimeOffset.UtcNow, null, newCollection);
        return true;
    }

    /// <summary>
    /// Adds a subject to a dictionary property with the specified key.
    /// </summary>
    /// <param name="property">The dictionary property to add to.</param>
    /// <param name="key">The key for the new entry.</param>
    /// <param name="subject">The subject to add.</param>
    /// <param name="source">The source of the change (for loop prevention).</param>
    /// <returns>True if the subject was added successfully, false otherwise.</returns>
    public bool AddToDictionary(RegisteredSubjectProperty property, object key, IInterceptorSubject subject, object? source)
    {
        if (!property.IsSubjectDictionary)
        {
            return false;
        }

        var currentDictionary = property.GetValue() as IDictionary;
        if (currentDictionary is null)
        {
            return false;
        }

        var newDictionary = _subjectFactory.AppendEntriesToDictionary(
            currentDictionary,
            new KeyValuePair<object, IInterceptorSubject>(key, subject));
        property.SetValueFromSource(source!, DateTimeOffset.UtcNow, null, newDictionary);
        return true;
    }

    /// <summary>
    /// Removes an entry from a dictionary property by key.
    /// </summary>
    /// <param name="property">The dictionary property to remove from.</param>
    /// <param name="key">The key of the entry to remove.</param>
    /// <param name="source">The source of the change (for loop prevention).</param>
    /// <returns>True if the entry was found and removed, false otherwise.</returns>
    public bool RemoveFromDictionary(RegisteredSubjectProperty property, object key, object? source)
    {
        if (!property.IsSubjectDictionary)
        {
            return false;
        }

        var currentDictionary = property.GetValue() as IDictionary;
        if (currentDictionary is null)
        {
            return false;
        }

        if (!currentDictionary.Contains(key))
        {
            return false;
        }

        var newDictionary = _subjectFactory.RemoveEntriesFromDictionary(currentDictionary, key);
        property.SetValueFromSource(source!, DateTimeOffset.UtcNow, null, newDictionary);
        return true;
    }

    /// <summary>
    /// Sets a reference property to the specified subject (or null to clear).
    /// </summary>
    /// <param name="property">The reference property to set.</param>
    /// <param name="subject">The subject to set (or null to clear the reference).</param>
    /// <param name="source">The source of the change (for loop prevention).</param>
    /// <returns>True if the reference was set successfully, false otherwise.</returns>
    public bool SetReference(RegisteredSubjectProperty property, IInterceptorSubject? subject, object? source)
    {
        if (!property.IsSubjectReference)
        {
            return false;
        }

        property.SetValueFromSource(source!, DateTimeOffset.UtcNow, null, subject);
        return true;
    }
}
