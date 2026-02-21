using System.Collections;

namespace Namotion.Interceptor.Connectors.Updates.Internal;

/// <summary>
/// Builds collection change operations (Insert/Remove/Move) by comparing old and new collections.
/// Designed to be pooled and reused.
/// </summary>
internal sealed class CollectionDiffBuilder
{
    // Reusable containers
    private readonly Dictionary<IInterceptorSubject, int> _oldIndexMap = new();
    private readonly Dictionary<IInterceptorSubject, int> _newIndexMap = new();
    private readonly Dictionary<IInterceptorSubject, int> _oldCommonIndexMap = new();
    private readonly List<IInterceptorSubject> _oldCommonOrder = [];
    private readonly List<IInterceptorSubject> _newCommonOrder = [];
    private readonly HashSet<object> _oldKeys = [];

    /// <summary>
    /// Builds collection change operations using subject references (not indices).
    /// The caller converts references to stable IDs.
    /// </summary>
    /// <param name="oldItems">The old collection items.</param>
    /// <param name="newItems">The new collection items.</param>
    /// <param name="removedItems">Output: items removed from old that are not in new.</param>
    /// <param name="insertedItems">Output: new items with their predecessor in the new collection (null = head).</param>
    /// <param name="movedItems">Output: items present in both but at different relative positions, with new predecessor.</param>
    public void GetCollectionChanges(
        IReadOnlyList<IInterceptorSubject> oldItems,
        IReadOnlyList<IInterceptorSubject> newItems,
        out List<IInterceptorSubject>? removedItems,
        out List<(IInterceptorSubject item, IInterceptorSubject? afterItem)>? insertedItems,
        out List<(IInterceptorSubject item, IInterceptorSubject? afterItem)>? movedItems)
    {
        removedItems = null;
        insertedItems = null;
        movedItems = null;

        // Build index maps
        _oldIndexMap.Clear();
        _newIndexMap.Clear();
        for (var i = 0; i < oldItems.Count; i++)
            _oldIndexMap[oldItems[i]] = i;
        for (var i = 0; i < newItems.Count; i++)
            _newIndexMap[newItems[i]] = i;

        // Removed items: in old but not in new
        for (var i = 0; i < oldItems.Count; i++)
        {
            var item = oldItems[i];
            if (!_newIndexMap.ContainsKey(item))
            {
                removedItems ??= [];
                removedItems.Add(item);
            }
        }

        // Inserted items: in new but not in old, with predecessor from new list
        for (var i = 0; i < newItems.Count; i++)
        {
            var item = newItems[i];
            if (!_oldIndexMap.ContainsKey(item))
            {
                var afterItem = i > 0 ? newItems[i - 1] : null;
                insertedItems ??= [];
                insertedItems.Add((item, afterItem));
            }
        }

        // Detect reordering among common items
        _oldCommonOrder.Clear();
        _newCommonOrder.Clear();
        for (var i = 0; i < oldItems.Count; i++)
        {
            if (_newIndexMap.ContainsKey(oldItems[i]))
                _oldCommonOrder.Add(oldItems[i]);
        }
        for (var i = 0; i < newItems.Count; i++)
        {
            if (_oldIndexMap.ContainsKey(newItems[i]))
                _newCommonOrder.Add(newItems[i]);
        }

        if (_oldCommonOrder.Count > 0 && !_oldCommonOrder.SequenceEqual(_newCommonOrder))
        {
            _oldCommonIndexMap.Clear();
            for (var i = 0; i < _oldCommonOrder.Count; i++)
                _oldCommonIndexMap[_oldCommonOrder[i]] = i;

            for (var i = 0; i < _newCommonOrder.Count; i++)
            {
                var item = _newCommonOrder[i];
                var oldCommonIndex = _oldCommonIndexMap[item];
                if (oldCommonIndex != i)
                {
                    // Find predecessor in full new list
                    var newIndex = _newIndexMap[item];
                    var afterItem = newIndex > 0 ? newItems[newIndex - 1] : null;
                    movedItems ??= [];
                    movedItems.Add((item, afterItem));
                }
            }
        }
    }

    /// <summary>
    /// Builds dictionary change operations.
    /// </summary>
    /// <param name="oldDictionary">The old dictionary.</param>
    /// <param name="newDictionary">The new dictionary.</param>
    /// <param name="operations">Output: structural operations (Insert/Remove), or null if none.</param>
    /// <param name="newItemsToProcess">Output: items that are new and need full processing, or null if none.</param>
    /// <param name="removedKeys">Output: keys that were removed, or null if none.</param>
    public void GetDictionaryChanges(
        IDictionary? oldDictionary,
        IDictionary newDictionary,
        out List<SubjectCollectionOperation>? operations,
        out List<(object key, IInterceptorSubject item)>? newItemsToProcess,
        out HashSet<object>? removedKeys)
    {
        operations = null;
        newItemsToProcess = null;
        removedKeys = null;

        _oldKeys.Clear();
        if (oldDictionary is not null)
        {
            foreach (var key in oldDictionary.Keys)
                _oldKeys.Add(key);
        }

        // Track removed keys - start with all old keys, remove ones that still exist with same value
        HashSet<object>? keysToRemove = _oldKeys.Count > 0 ? [.._oldKeys] : null;

        foreach (DictionaryEntry entry in newDictionary)
        {
            var key = entry.Key;
            if (entry.Value is not IInterceptorSubject newItem)
                continue;

            if (_oldKeys.Contains(key))
            {
                // Key exists in both - check if VALUE changed (different object reference)
                var oldValue = oldDictionary![key];
                if (ReferenceEquals(oldValue, newItem))
                {
                    // Same object - key is not removed, not a new item
                    keysToRemove?.Remove(key);
                }
                else
                {
                    // Different object at same key - treat as Remove + Insert
                    // Key stays in keysToRemove (will generate Remove)
                    // Add to newItemsToProcess (will generate Insert)
                    newItemsToProcess ??= [];
                    newItemsToProcess.Add((key, newItem));
                }
            }
            else
            {
                // New key
                newItemsToProcess ??= [];
                newItemsToProcess.Add((key, newItem));
            }
        }

        // Only return removedKeys if there are actually keys to remove
        if (keysToRemove is { Count: > 0 })
        {
            removedKeys = keysToRemove;
        }
    }

    /// <summary>
    /// Gets items that exist in both old and new collections.
    /// </summary>
    public IReadOnlyList<IInterceptorSubject> GetCommonItems() => _newCommonOrder;

    /// <summary>
    /// Clears the builder for reuse. Call before returning to pool.
    /// </summary>
    public void Clear()
    {
        _oldIndexMap.Clear();
        _newIndexMap.Clear();
        _oldCommonIndexMap.Clear();
        _oldCommonOrder.Clear();
        _newCommonOrder.Clear();
        _oldKeys.Clear();
    }
}
