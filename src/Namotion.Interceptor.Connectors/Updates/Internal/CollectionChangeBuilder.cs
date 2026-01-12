using System.Collections;

namespace Namotion.Interceptor.Connectors.Updates.Internal;

/// <summary>
/// Builds collection change operations (Insert/Remove/Move) by comparing old and new collections.
/// Designed to be pooled and reused.
/// </summary>
internal sealed class CollectionChangeBuilder
{
    // Reusable containers
    private readonly Dictionary<IInterceptorSubject, int> _oldIndexMap = new();
    private readonly Dictionary<IInterceptorSubject, int> _newIndexMap = new();
    private readonly List<IInterceptorSubject> _oldCommonOrder = [];
    private readonly List<IInterceptorSubject> _newCommonOrder = [];
    private readonly HashSet<object> _oldKeys = [];

    /// <summary>
    /// Builds collection change operations.
    /// </summary>
    /// <param name="oldItems">The old collection items.</param>
    /// <param name="newItems">The new collection items.</param>
    /// <param name="operations">Output: structural operations (Insert/Remove/Move).</param>
    /// <param name="newItemsToProcess">Output: items that are new and need full processing.</param>
    /// <param name="reorderedItems">Output: items that were reordered (for Move operations).</param>
    public void BuildCollectionChanges(
        IReadOnlyList<IInterceptorSubject> oldItems,
        IReadOnlyList<IInterceptorSubject> newItems,
        out List<SubjectCollectionOperation>? operations,
        out List<(int index, IInterceptorSubject item)> newItemsToProcess,
        out List<(int oldIndex, int newIndex, IInterceptorSubject item)> reorderedItems)
    {
        operations = null;
        newItemsToProcess = [];
        reorderedItems = [];

        // Build index maps
        _oldIndexMap.Clear();
        _newIndexMap.Clear();
        for (var i = 0; i < oldItems.Count; i++)
            _oldIndexMap[oldItems[i]] = i;
        for (var i = 0; i < newItems.Count; i++)
            _newIndexMap[newItems[i]] = i;

        // Generate Remove operations (process from highest index first)
        for (var i = oldItems.Count - 1; i >= 0; i--)
        {
            var item = oldItems[i];
            if (!_newIndexMap.ContainsKey(item))
            {
                operations ??= [];
                operations.Insert(0, new SubjectCollectionOperation
                {
                    Action = SubjectCollectionOperationType.Remove,
                    Index = i
                });
            }
        }

        // Generate Insert operations for new items
        for (var i = 0; i < newItems.Count; i++)
        {
            var item = newItems[i];
            if (!_oldIndexMap.ContainsKey(item))
            {
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
            var oldCommonIndexMap = new Dictionary<IInterceptorSubject, int>(_oldCommonOrder.Count);
            for (var i = 0; i < _oldCommonOrder.Count; i++)
                oldCommonIndexMap[_oldCommonOrder[i]] = i;

            for (var i = 0; i < _newCommonOrder.Count; i++)
            {
                var item = _newCommonOrder[i];
                var oldCommonIndex = oldCommonIndexMap[item];
                if (oldCommonIndex != i)
                {
                    reorderedItems.Add((_oldIndexMap[item], _newIndexMap[item], item));
                }
            }
        }
    }

    /// <summary>
    /// Builds dictionary change operations.
    /// </summary>
    public void BuildDictionaryChanges(
        IDictionary? oldDict,
        IDictionary newDict,
        out List<SubjectCollectionOperation>? operations,
        out List<(object key, IInterceptorSubject item)> newItemsToProcess,
        out HashSet<object> removedKeys)
    {
        operations = null;
        newItemsToProcess = [];

        _oldKeys.Clear();
        if (oldDict is not null)
        {
            foreach (var key in oldDict.Keys)
                _oldKeys.Add(key);
        }
        removedKeys = new HashSet<object>(_oldKeys);

        foreach (DictionaryEntry entry in newDict)
        {
            var key = entry.Key;
            if (entry.Value is not IInterceptorSubject item)
                continue;

            if (_oldKeys.Contains(key))
            {
                removedKeys.Remove(key);
            }
            else
            {
                newItemsToProcess.Add((key, item));
            }
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
        _oldCommonOrder.Clear();
        _newCommonOrder.Clear();
        _oldKeys.Clear();
    }
}
