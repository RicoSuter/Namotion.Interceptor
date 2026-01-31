using System.Collections;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Connectors.Updates.Internal;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Base class for processing structural property changes (add/remove subjects).
/// Handles branching on property type and collection diffing.
/// Note: Source filtering (loop prevention) is handled by ChangeQueueProcessor, not here.
/// </summary>
public abstract class StructuralChangeProcessor
{
    private readonly CollectionDiffBuilder _diffBuilder = new();

    /// <summary>
    /// Process a property change, branching on property type.
    /// </summary>
    /// <returns>True if the change was processed as a structural change, false for value properties.</returns>
    public async Task<bool> ProcessPropertyChangeAsync(SubjectPropertyChange change, RegisteredSubjectProperty property)
    {

        if (property.IsSubjectReference)
        {
            var oldSubject = change.TryGetOldValue<IInterceptorSubject?>(out var oldValue) ? oldValue : null;
            var newSubject = change.TryGetNewValue<IInterceptorSubject?>(out var newValue) ? newValue : null;

            if (oldSubject is not null && !ReferenceEquals(oldSubject, newSubject))
                await OnSubjectRemovedAsync(property, oldSubject, index: null);
            if (newSubject is not null && !ReferenceEquals(oldSubject, newSubject))
                await OnSubjectAddedAsync(property, newSubject, index: null);

            return true;
        }
        else if (property.IsSubjectCollection)
        {
            // TODO: Might need to support other collection types as well (need to check other places)
            var oldCollection = change.TryGetOldValue<IReadOnlyList<IInterceptorSubject>>(out var oldList) && oldList is not null
                ? oldList
                : Array.Empty<IInterceptorSubject>();
            var newCollection = change.TryGetNewValue<IReadOnlyList<IInterceptorSubject>>(out var newList) && newList is not null
                ? newList
                : Array.Empty<IInterceptorSubject>();

            _diffBuilder.GetCollectionChanges(oldCollection, newCollection,
                out var operations, out var newItems, out _);

            // Process removes (descending order)
            if (operations is not null)
            {
                foreach (var operation in operations)
                {
                    if (operation.Action == SubjectCollectionOperationType.Remove)
                        await OnSubjectRemovedAsync(property, oldCollection[(int)operation.Index], operation.Index);
                }
            }

            // Process adds
            if (newItems is not null)
            {
                foreach (var (index, subject) in newItems)
                    await OnSubjectAddedAsync(property, subject, index);
            }

            // Reorders ignored - order is connector-specific (OPC UA: no-op)
            // TODO: Support reordering for connectors which need this?

            return true;
        }
        else if (property.IsSubjectDictionary)
        {
            var oldDictionary = change.TryGetOldValue<IDictionary>(out var oldDict) ? oldDict : null;
            var newDictionary = change.TryGetNewValue<IDictionary>(out var newDict) ? newDict : new Dictionary<object, object>();

            _diffBuilder.GetDictionaryChanges(oldDictionary, newDictionary,
                out _, out var newItems, out var removedKeys);

            var oldChildren = property.Children.ToDictionary(child => child.Index!, child => child.Subject);
            if (removedKeys is not null)
            {
                foreach (var key in removedKeys)
                {
                    if (oldChildren.TryGetValue(key, out var subject))
                        await OnSubjectRemovedAsync(property, subject, key);
                }
            }

            if (newItems is not null)
            {
                foreach (var (key, subject) in newItems)
                    await OnSubjectAddedAsync(property, (IInterceptorSubject)subject, key);
            }

            return true;
        }
        else
        {
            // Value changes are not structural - return false to signal caller should handle them
            return false;
        }
    }

    /// <summary>
    /// Called when a subject is added to a property.
    /// </summary>
    protected abstract Task OnSubjectAddedAsync(RegisteredSubjectProperty property, IInterceptorSubject subject, object? index);

    /// <summary>
    /// Called when a subject is removed from a property.
    /// </summary>
    protected abstract Task OnSubjectRemovedAsync(RegisteredSubjectProperty property, IInterceptorSubject subject, object? index);

}
