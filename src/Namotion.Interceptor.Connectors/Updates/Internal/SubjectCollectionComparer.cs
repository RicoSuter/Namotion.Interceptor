using System.Collections;

namespace Namotion.Interceptor.Connectors.Updates.Internal;

/// <summary>
/// Builds collection change operations (Insert/Remove/Move) by comparing old and new collections.
/// Designed to be pooled and reused.
/// </summary>
internal sealed class SubjectCollectionComparer
{
    // Reusable containers
    private readonly Dictionary<IInterceptorSubject, int> _oldIndexMap = new();
    private readonly Dictionary<IInterceptorSubject, int> _newIndexMap = new();
    private readonly Dictionary<IInterceptorSubject, int> _oldCommonIndexMap = new();
    private readonly List<IInterceptorSubject> _oldCommonOrder = [];
    private readonly List<IInterceptorSubject> _newCommonOrder = [];
    private readonly HashSet<object> _oldKeys = [];

    /// <summary>
    /// Builds collection change operations.
    /// </summary>
    /// <param name="oldItems">The old collection items.</param>
    /// <param name="newItems">The new collection items.</param>
    /// <param name="operations">Output: structural operations (Insert/Remove/Move), or null if none.</param>
    /// <param name="newItemsToProcess">Output: items that are new and need full processing, or null if none.</param>
    /// <param name="reorderedItems">Output: items that were reordered (for Move operations), or null if none.</param>
    public void GetCollectionChanges(
        IReadOnlyList<IInterceptorSubject> oldItems,
        IReadOnlyList<IInterceptorSubject> newItems,
        out List<SubjectCollectionOperation>? operations,
        out List<(int index, IInterceptorSubject item)>? newItemsToProcess,
        out List<(int oldIndex, int newIndex, IInterceptorSubject item)>? reorderedItems)
    {
        operations = null;
        newItemsToProcess = null;
        reorderedItems = null;

        // Build index maps
        _oldIndexMap.Clear();
        _newIndexMap.Clear();
        for (var i = 0; i < oldItems.Count; i++)
            _oldIndexMap[oldItems[i]] = i;
        for (var i = 0; i < newItems.Count; i++)
            _newIndexMap[newItems[i]] = i;

        // Generate Remove operations (process from the highest index first, then reverse for correct order)
        for (var i = oldItems.Count - 1; i >= 0; i--)
        {
            var item = oldItems[i];
            if (!_newIndexMap.ContainsKey(item))
            {
                operations ??= [];
                operations.Add(new SubjectCollectionOperation
                {
                    Action = SubjectCollectionOperationType.Remove,
                    Index = i
                });
            }
        }
        // Reverse to get ascending index order (O(n) once instead of O(n²) from Insert(0,...))
        operations?.Reverse();

        // Generate Insert operations for new items
        for (var i = 0; i < newItems.Count; i++)
        {
            var item = newItems[i];
            if (!_oldIndexMap.ContainsKey(item))
            {
                newItemsToProcess ??= [];
                newItemsToProcess.Add((i, item));
            }
        }

        // Build common order lists to detect reordering
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

        // Detect reordering
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
                    reorderedItems ??= [];
                    reorderedItems.Add((_oldIndexMap[item], _newIndexMap[item], item));
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

        // Track removed keys - start with all old keys, remove ones that still exist
        HashSet<object>? keysToRemove = _oldKeys.Count > 0 ? [.._oldKeys] : null;

        foreach (DictionaryEntry entry in newDictionary)
        {
            var key = entry.Key;
            if (entry.Value is not IInterceptorSubject item)
                continue;

            if (_oldKeys.Contains(key))
            {
                keysToRemove?.Remove(key);
            }
            else
            {
                newItemsToProcess ??= [];
                newItemsToProcess.Add((key, item));
            }
        }

        // Only return removedKeys if there are actually keys to remove
        if (keysToRemove is { Count: > 0 })
        {
            removedKeys = keysToRemove;
        }
    }

    /// <summary>
    /// Gets the new index for an item.
    /// </summary>
    public int GetNewIndex(IInterceptorSubject item) =>
        _newIndexMap.GetValueOrDefault(item, -1);

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
