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
/// Uses factory pattern for add operations to avoid creating subjects when validation fails.
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
    /// Adds a subject to a collection property using a factory to create the subject only if validation passes.
    /// </summary>
    /// <param name="property">The collection property to add to.</param>
    /// <param name="subjectFactory">Factory function that creates the subject (only called if validation passes).</param>
    /// <param name="source">The source of the change (for loop prevention).</param>
    /// <returns>The created subject if added successfully, null otherwise.</returns>
    public async Task<IInterceptorSubject?> AddToCollectionAsync(
        RegisteredSubjectProperty property,
        Func<Task<IInterceptorSubject>> subjectFactory,
        object source)
    {
        if (!property.IsSubjectCollection)
        {
            return null;
        }

        var currentCollection = property.GetValue() as IEnumerable<IInterceptorSubject?>;
        if (currentCollection is null)
        {
            return null;
        }

        // Validation passed - now create the subject
        var subject = await subjectFactory().ConfigureAwait(false);

        var newCollection = _subjectFactory.AppendSubjectsToCollection(currentCollection, subject);
        property.SetValueFromSource(source, DateTimeOffset.UtcNow, null, newCollection);
        return subject;
    }

    /// <summary>
    /// Removes a subject from a collection property by reference.
    /// </summary>
    /// <param name="property">The collection property to remove from.</param>
    /// <param name="subject">The subject to remove.</param>
    /// <param name="source">The source of the change (for loop prevention).</param>
    /// <returns>True if the subject was found and removed, false otherwise.</returns>
    public bool RemoveFromCollection(RegisteredSubjectProperty property, IInterceptorSubject subject, object source)
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
        property.SetValueFromSource(source, DateTimeOffset.UtcNow, null, newCollection);
        return true;
    }

    /// <summary>
    /// Removes a subject from a collection property by index.
    /// </summary>
    /// <param name="property">The collection property to remove from.</param>
    /// <param name="index">The index of the subject to remove.</param>
    /// <param name="source">The source of the change (for loop prevention).</param>
    /// <returns>True if the subject at the index was removed, false otherwise.</returns>
    public bool RemoveFromCollectionByIndex(RegisteredSubjectProperty property, int index, object source)
    {
        if (!property.IsSubjectCollection)
        {
            return false;
        }

        if (property.GetValue() is not IEnumerable<IInterceptorSubject?> currentCollection)
        {
            return false;
        }

        var list = currentCollection.ToList();
        if (index < 0 || index >= list.Count)
        {
            return false;
        }

        var newCollection = _subjectFactory.RemoveSubjectsFromCollection(currentCollection, index);
        property.SetValueFromSource(source, DateTimeOffset.UtcNow, null, newCollection);
        return true;
    }

    /// <summary>
    /// Adds a subject to a dictionary property with the specified key using a factory to create the subject only if validation passes.
    /// </summary>
    /// <param name="property">The dictionary property to add to.</param>
    /// <param name="key">The key for the new entry.</param>
    /// <param name="subjectFactory">Factory function that creates the subject (only called if validation passes).</param>
    /// <param name="source">The source of the change (for loop prevention).</param>
    /// <returns>The created subject if added successfully, null otherwise.</returns>
    public async Task<IInterceptorSubject?> AddToDictionaryAsync(
        RegisteredSubjectProperty property,
        object key,
        Func<Task<IInterceptorSubject>> subjectFactory,
        object source)
    {
        if (!property.IsSubjectDictionary)
        {
            return null;
        }

        var currentDictionary = property.GetValue() as IDictionary;
        if (currentDictionary is null)
        {
            return null;
        }

        // Validation passed - now create the subject
        var subject = await subjectFactory().ConfigureAwait(false);

        var newDictionary = _subjectFactory.AppendEntriesToDictionary(
            currentDictionary,
            new KeyValuePair<object, IInterceptorSubject>(key, subject));
        property.SetValueFromSource(source, DateTimeOffset.UtcNow, null, newDictionary);
        return subject;
    }

    /// <summary>
    /// Removes an entry from a dictionary property by key.
    /// </summary>
    /// <param name="property">The dictionary property to remove from.</param>
    /// <param name="key">The key of the entry to remove.</param>
    /// <param name="source">The source of the change (for loop prevention).</param>
    /// <returns>True if the entry was found and removed, false otherwise.</returns>
    public bool RemoveFromDictionary(RegisteredSubjectProperty property, object key, object source)
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
        property.SetValueFromSource(source, DateTimeOffset.UtcNow, null, newDictionary);
        return true;
    }

    /// <summary>
    /// Sets a reference property to a subject using a factory to create the subject only if validation passes.
    /// </summary>
    /// <param name="property">The reference property to set.</param>
    /// <param name="subjectFactory">Factory function that creates the subject (only called if validation passes).</param>
    /// <param name="source">The source of the change (for loop prevention).</param>
    /// <returns>The created subject if set successfully, null otherwise.</returns>
    public async Task<IInterceptorSubject?> SetReferenceAsync(
        RegisteredSubjectProperty property,
        Func<Task<IInterceptorSubject>> subjectFactory,
        object source)
    {
        if (!property.IsSubjectReference)
        {
            return null;
        }

        // Validation passed - now create the subject
        var subject = await subjectFactory().ConfigureAwait(false);

        property.SetValueFromSource(source, DateTimeOffset.UtcNow, null, subject);
        return subject;
    }

    /// <summary>
    /// Clears a reference property (sets it to null).
    /// </summary>
    /// <param name="property">The reference property to clear.</param>
    /// <param name="source">The source of the change (for loop prevention).</param>
    /// <returns>True if the reference was cleared successfully, false otherwise.</returns>
    public bool RemoveReference(RegisteredSubjectProperty property, object source)
    {
        if (!property.IsSubjectReference)
        {
            return false;
        }

        property.SetValueFromSource(source, DateTimeOffset.UtcNow, null, null);
        return true;
    }
}
