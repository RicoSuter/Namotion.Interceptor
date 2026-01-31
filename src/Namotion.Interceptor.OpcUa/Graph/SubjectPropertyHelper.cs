using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.OpcUa.Graph;

/// <summary>
/// Helper methods for manipulating subject properties (collections, dictionaries, references).
/// Works at the model level without OPC UA dependencies.
/// </summary>
internal static class SubjectPropertyHelper
{
    // TODO: Maybe transform into extension methods with subject factory as parameter? Methods should use DefaultSubjectFactory internally
    // to create a new copy of collection or dictionary (will then support any collection/dict type automatically)

    /// <summary>
    /// Adds a subject to a collection property by creating a new array with the subject appended.
    /// </summary>
    /// <param name="property">The registered collection property to add to.</param>
    /// <param name="subject">The subject to add to the collection.</param>
    /// <param name="source">The source of the change (for change tracking).</param>
    /// <param name="changedTimestamp">Optional timestamp when the value was changed at the source.</param>
    /// <param name="receivedTimestamp">Optional timestamp when the value was received.</param>
    /// <returns>True if the subject was added successfully, false otherwise.</returns>
    public static bool AddToCollection(
        RegisteredSubjectProperty property,
        IInterceptorSubject subject,
        object? source,
        DateTimeOffset? changedTimestamp = null,
        DateTimeOffset? receivedTimestamp = null)
    {
        // TODO: Need to check whether we should move this and other methods of this class to same place or into DefaultSubjectFactory,
        // also should support other collection types (all methods in this class!), not only arrays (same as in DefaultSubjectFactory)

        // Get current array value
        var currentValue = property.GetValue();
        if (currentValue is not Array currentArray)
        {
            return false;
        }

        var elementType = currentArray.GetType().GetElementType();
        if (elementType is null)
        {
            return false;
        }

        // Create new array with space for the new item
        var newLength = currentArray.Length + 1;
        var newArray = Array.CreateInstance(elementType, newLength);

        // Copy existing elements
        for (var i = 0; i < currentArray.Length; i++)
        {
            newArray.SetValue(currentArray.GetValue(i), i);
        }

        // Add the new element at the end
        newArray.SetValue(subject, currentArray.Length);

        // Set the new array value (this attaches the subject and registers it)
        // Use SetValueFromSource to prevent changes from being mirrored back to server
        if (source is not null)
        {
            property.SetValueFromSource(source, changedTimestamp, receivedTimestamp, newArray);
        }
        else
        {
            property.SetValue(newArray);
        }

        return true;
    }

    /// <summary>
    /// Removes subjects from a collection property at the specified indices.
    /// Creates a new array with the subjects at the specified indices removed.
    /// </summary>
    /// <param name="property">The registered collection property to remove from.</param>
    /// <param name="indicesToRemove">The indices of subjects to remove (will be processed in any order).</param>
    /// <param name="source">The source of the change (for change tracking).</param>
    /// <param name="changedTimestamp">Optional timestamp when the value was changed at the source.</param>
    /// <param name="receivedTimestamp">Optional timestamp when the value was received.</param>
    /// <returns>True if subjects were removed successfully, false otherwise.</returns>
    public static bool RemoveFromCollectionByIndices(
        RegisteredSubjectProperty property,
        IReadOnlyList<int> indicesToRemove,
        object? source,
        DateTimeOffset? changedTimestamp = null,
        DateTimeOffset? receivedTimestamp = null)
    {
        if (indicesToRemove.Count == 0)
        {
            return true;
        }

        // Get current array value
        var currentValue = property.GetValue();
        if (currentValue is not Array currentArray)
        {
            return false;
        }

        var elementType = currentArray.GetType().GetElementType();
        if (elementType is null)
        {
            return false;
        }

        // Create set of indices to remove
        var removeSet = new HashSet<int>(indicesToRemove);

        // Create new array without removed elements
        var newLength = currentArray.Length - indicesToRemove.Count;
        if (newLength < 0)
        {
            newLength = 0;
        }

        var newArray = Array.CreateInstance(elementType, newLength);
        var newIndex = 0;

        for (var i = 0; i < currentArray.Length; i++)
        {
            if (!removeSet.Contains(i))
            {
                newArray.SetValue(currentArray.GetValue(i), newIndex++);
            }
        }

        // Set the new array value
        // Use SetValueFromSource to prevent changes from being mirrored back to server
        if (source is not null)
        {
            property.SetValueFromSource(source, changedTimestamp, receivedTimestamp, newArray);
        }
        else
        {
            property.SetValue(newArray);
        }

        return true;
    }

    /// <summary>
    /// Adds a subject to a dictionary property with the specified key.
    /// Creates a new dictionary with the entry added.
    /// </summary>
    /// <param name="property">The registered dictionary property to add to.</param>
    /// <param name="key">The key for the new entry.</param>
    /// <param name="subject">The subject to add to the dictionary.</param>
    /// <param name="source">The source of the change (for change tracking).</param>
    /// <param name="changedTimestamp">Optional timestamp when the value was changed at the source.</param>
    /// <param name="receivedTimestamp">Optional timestamp when the value was received.</param>
    /// <returns>True if the subject was added successfully, false otherwise.</returns>
    public static bool AddToDictionary(
        RegisteredSubjectProperty property,
        object key,
        IInterceptorSubject subject,
        object? source,
        DateTimeOffset? changedTimestamp = null,
        DateTimeOffset? receivedTimestamp = null)
    {
        // Get current dictionary value
        var currentValue = property.GetValue();
        if (currentValue is null)
        {
            return false;
        }

        var dictType = currentValue.GetType();
        if (!dictType.IsGenericType || dictType.GetGenericTypeDefinition() != typeof(Dictionary<,>))
        {
            return false;
        }

        // Cast to IDictionary to read existing entries
        if (currentValue is not System.Collections.IDictionary currentDict)
        {
            return false;
        }

        // Create new dictionary with existing entries plus new one
        var keyType = dictType.GetGenericArguments()[0];
        var valueType = dictType.GetGenericArguments()[1];
        var newDictType = typeof(Dictionary<,>).MakeGenericType(keyType, valueType);
        var newDict = Activator.CreateInstance(newDictType) as System.Collections.IDictionary;

        if (newDict is null)
        {
            return false;
        }

        // Copy existing entries
        foreach (System.Collections.DictionaryEntry entry in currentDict)
        {
            newDict[entry.Key] = entry.Value;
        }

        // Add the new entry
        newDict[key] = subject;

        // Set the new dictionary value (this attaches the subject and registers it)
        // Use SetValueFromSource to prevent mirroring back to server
        if (source is not null)
        {
            property.SetValueFromSource(source, changedTimestamp, receivedTimestamp, newDict);
        }
        else
        {
            property.SetValue(newDict);
        }

        return true;
    }

    /// <summary>
    /// Removes entries from a dictionary property by keys.
    /// Creates a new dictionary with the entries removed.
    /// </summary>
    /// <param name="property">The registered dictionary property to remove from.</param>
    /// <param name="keysToRemove">The keys of entries to remove.</param>
    /// <param name="source">The source of the change (for change tracking).</param>
    /// <param name="changedTimestamp">Optional timestamp when the value was changed at the source.</param>
    /// <param name="receivedTimestamp">Optional timestamp when the value was received.</param>
    /// <returns>True if entries were removed successfully, false otherwise.</returns>
    public static bool RemoveFromDictionary(
        RegisteredSubjectProperty property,
        IReadOnlyList<string> keysToRemove,
        object? source,
        DateTimeOffset? changedTimestamp = null,
        DateTimeOffset? receivedTimestamp = null)
    {
        if (keysToRemove.Count == 0)
        {
            return true;
        }

        // Get current dictionary value
        var currentValue = property.GetValue();
        if (currentValue is null)
        {
            return false;
        }

        var dictType = currentValue.GetType();
        if (!dictType.IsGenericType || dictType.GetGenericTypeDefinition() != typeof(Dictionary<,>))
        {
            return false;
        }

        // Cast to IDictionary to manipulate
        if (currentValue is not System.Collections.IDictionary currentDict)
        {
            return false;
        }

        // Create new dictionary without removed keys
        var keyType = dictType.GetGenericArguments()[0];
        var valueType = dictType.GetGenericArguments()[1];
        var newDictType = typeof(Dictionary<,>).MakeGenericType(keyType, valueType);
        var newDict = Activator.CreateInstance(newDictType) as System.Collections.IDictionary;

        if (newDict is null)
        {
            return false;
        }

        var removeSet = new HashSet<string>(keysToRemove);

        foreach (System.Collections.DictionaryEntry entry in currentDict)
        {
            if (entry.Key is null)
            {
                continue;
            }

            var keyString = entry.Key.ToString() ?? "";
            if (!removeSet.Contains(keyString))
            {
                newDict[entry.Key] = entry.Value;
            }
        }

        // Set the new dictionary value - use SetValueFromSource to prevent mirroring back to server
        if (source is not null)
        {
            property.SetValueFromSource(source, changedTimestamp, receivedTimestamp, newDict);
        }
        else
        {
            property.SetValue(newDict);
        }

        return true;
    }

    /// <summary>
    /// Sets a reference property to the specified subject or null.
    /// Uses SetValueFromSource when source is provided to prevent changes from being mirrored back.
    /// </summary>
    /// <param name="property">The registered reference property to set.</param>
    /// <param name="subject">The subject to set as the reference value, or null to clear.</param>
    /// <param name="source">The source of the change (for change tracking).</param>
    /// <param name="changedTimestamp">Optional timestamp when the value was changed at the source.</param>
    /// <param name="receivedTimestamp">Optional timestamp when the value was received.</param>
    public static void SetReference(
        RegisteredSubjectProperty property,
        IInterceptorSubject? subject,
        object? source,
        DateTimeOffset? changedTimestamp = null,
        DateTimeOffset? receivedTimestamp = null)
    {
        // TODO: Remove method and use SetValueFromSource directly, source are never null at call sites, no?
        if (source is not null)
        {
            property.SetValueFromSource(source, changedTimestamp, receivedTimestamp, subject);
        }
        else
        {
            property.SetValue(subject);
        }
    }
}
