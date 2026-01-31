using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.OpcUa.Graph;

/// <summary>
/// Helper methods for manipulating subject properties (collections, dictionaries, references).
/// Works at the model level without OPC UA dependencies.
/// </summary>
internal static class SubjectPropertyHelper
{
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
        // TOOD: Need to check whether we should move this and other methods of this class to same place or into DefaultSubjectFactory,
        // also should support other collection types, not only arrays (same as in DefaultSubjectFactory)
        
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
}
