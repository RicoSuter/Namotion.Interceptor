using System.Buffers;
using System.Collections;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Connectors.Updates.Internal;

/// <summary>
/// Applies collection and dictionary updates from <see cref="SubjectUpdate"/> instances.
/// Handles structural operations (Insert, Remove, Move) and sparse property updates.
/// </summary>
internal static class SubjectItemsUpdateApplier
{
    /// <summary>
    /// Applies a collection (array/list) update to a property.
    /// </summary>
    internal static void ApplyCollectionUpdate(
        IInterceptorSubject parent,
        RegisteredSubjectProperty property,
        SubjectPropertyUpdate propertyUpdate,
        SubjectUpdateApplyContext context)
    {
        // A container this model cannot write can only receive updates for the items it already holds:
        // a new item has nowhere to be stored, so none is created, and a structure change is reported as
        // dropped instead of written. The working list is still walked, because that is what maps an
        // incoming index onto the item the producer meant.
        var canWriteContainer = property.HasSetter;

        var existingValue = property.GetValue();
        var workingItems = SubjectValueConvert.ToSubjectMutableList(existingValue);
        var isComplete = IsCompleteCollectionMembership(propertyUpdate, context);
        var structureChanged = isComplete &&
            (existingValue is null || workingItems.Count != propertyUpdate.Count);
        if (isComplete)
        {
            // Complete membership names every position once, so padding with nulls lets each item be written
            // at its own index whatever the arrival order, and every null is overwritten by the item that
            // owns its slot.
            var count = propertyUpdate.Count!.Value;
            if (workingItems.Count > count)
                workingItems.RemoveRange(count, workingItems.Count - count);

            while (workingItems.Count < count)
                workingItems.Add(null!);
        }

        // Apply structural operations in two phases:
        // Phase 1: Remove and Insert operations (applied sequentially)
        // Phase 2: Move operations (applied atomically using snapshot)
        if (propertyUpdate.Operations is { Count: > 0 })
        {
            // Phase 1: Apply Remove and Insert operations sequentially
            // Removes should be in descending order so they don't affect each other's indices
            foreach (var operation in propertyUpdate.Operations)
            {
                var index = ConvertIndexToInt(operation.Index);
                switch (operation.Action)
                {
                    case SubjectCollectionOperationType.Remove:
                        if (index >= 0 && index < workingItems.Count)
                        {
                            workingItems.RemoveAt(index);
                            structureChanged = true;
                        }
                        break;

                    case SubjectCollectionOperationType.Insert:
                        if (operation.Id is not null)
                        {
                            var itemProperties = context.GetSubjectProperties(operation.Id);
                            if (canWriteContainer)
                            {
                                var newItem = CreateAndApplyItem(parent, property, index, operation.Id, itemProperties, context);
                                if (index >= workingItems.Count)
                                    workingItems.Add(newItem);
                                else
                                    workingItems.Insert(index, newItem);
                            }
                            structureChanged = true;
                        }
                        break;
                }
            }

            // Phase 2: Apply Move operations atomically using snapshot
            // Move indices reference the state after removes/inserts, and moves are applied simultaneously
            var hasMoves = propertyUpdate.Operations.Any(op => op.Action == SubjectCollectionOperationType.Move);
            if (hasMoves)
            {
                var snapshot = workingItems.ToArray();
                foreach (var operation in propertyUpdate.Operations)
                {
                    if (operation is { Action: SubjectCollectionOperationType.Move, FromIndex: not null })
                    {
                        var toIndex = ConvertIndexToInt(operation.Index);
                        var fromIndex = operation.FromIndex.Value;
                        if (fromIndex >= 0 && fromIndex < snapshot.Length && toIndex >= 0 && toIndex < workingItems.Count)
                        {
                            workingItems[toIndex] = snapshot[fromIndex];
                            structureChanged = true;
                        }
                    }
                }
            }
        }

        // Apply sparse property updates
        if (propertyUpdate.Items is { Count: > 0 })
        {
            foreach (var collectionUpdate in propertyUpdate.Items)
            {
                var index = ConvertIndexToInt(collectionUpdate.Index);

                // Validate index against declared count - if count is specified, index must be < count
                if (propertyUpdate.Count.HasValue && index >= propertyUpdate.Count.Value)
                {
                    throw new InvalidOperationException(
                        $"Invalid collection update: index {index} is out of bounds for declared count {propertyUpdate.Count.Value}. " +
                        "The index in a sparse update must be less than the declared count.");
                }

                if (collectionUpdate.Id is not null)
                {
                    var itemProperties = context.GetSubjectProperties(collectionUpdate.Id);
                    if (index >= 0 && index < workingItems.Count &&
                        workingItems[index] is { } existingItem &&
                        SubjectUpdateApplier.TryApplyToHeldSubject(collectionUpdate.Id, existingItem, itemProperties, context))
                    {
                        continue;
                    }

                    if (index >= 0 && index <= workingItems.Count)
                    {
                        if (canWriteContainer)
                        {
                            var newItem = CreateAndApplyItem(parent, property, index, collectionUpdate.Id, itemProperties, context);
                            if (index >= workingItems.Count)
                                workingItems.Add(newItem);
                            else
                                workingItems[index] = newItem;
                        }
                        structureChanged = true;
                    }
                }
            }
        }

        if (structureChanged)
        {
            if (canWriteContainer)
            {
                var collection = context.SubjectFactory.CreateSubjectCollection(property.Type, workingItems);
                context.SetPropertyValue(property, propertyUpdate.Timestamp, collection);
            }
            else
            {
                context.RecordDroppedStructure(property);
            }
        }
    }

    /// <summary>
    /// Applies a dictionary update to a property.
    /// </summary>
    internal static void ApplyDictionaryUpdate(
        IInterceptorSubject parent,
        RegisteredSubjectProperty property,
        SubjectPropertyUpdate propertyUpdate,
        SubjectUpdateApplyContext context)
    {
        // See the collection path for what a container this model cannot write can receive.
        var canWriteContainer = property.HasSetter;

        var targetKeyType = property.Type.GetDictionaryKeyAndValueTypes().Key;
        var workingDictionary = new Dictionary<object, IInterceptorSubject>();

        var existingValue = property.GetValue();
        var completeKeys = TryGetCompleteDictionaryMembership(propertyUpdate, context, targetKeyType, workingDictionary);
        try
        {
            // The working dictionary omits null and non-subject entries, so the original container is counted.
            var structureChanged = completeKeys is not null && (existingValue is null ||
                (existingValue is ICollection collection
                    ? collection.Count
                    : ((IEnumerable)existingValue).Cast<object>().Count()) != propertyUpdate.Count);

            if (existingValue is not null)
            {
                foreach (DictionaryEntry entry in SubjectValueConvert.ToSubjectDictionary(existingValue))
                {
                    if (entry.Value is not IInterceptorSubject subject)
                        continue;

                    if (completeKeys is null)
                    {
                        workingDictionary[entry.Key] = subject;
                        continue;
                    }

                    // Complete membership seeded an entry for every listed key, so an unlisted key has none.
                    ref var member = ref CollectionsMarshal.GetValueRefOrNullRef(workingDictionary, entry.Key);
                    if (Unsafe.IsNullRef(ref member))
                        structureChanged = true;
                    else
                        member = subject;
                }
            }

            // Apply structural operations
            if (propertyUpdate.Operations is { Count: > 0 })
            {
                foreach (var operation in propertyUpdate.Operations)
                {
                    var key = ConvertDictionaryKey(operation.Index, targetKeyType);
                    switch (operation.Action)
                    {
                        case SubjectCollectionOperationType.Remove:
                            if (workingDictionary.Remove(key))
                                structureChanged = true;
                            break;

                        case SubjectCollectionOperationType.Insert:
                            if (operation.Id is not null)
                            {
                                var itemProperties = context.GetSubjectProperties(operation.Id);
                                if (canWriteContainer)
                                {
                                    workingDictionary[key] = CreateAndApplyItem(parent, property, key, operation.Id, itemProperties, context);
                                }
                                structureChanged = true;
                            }
                            break;
                    }
                }
            }

            // Apply sparse property updates
            if (propertyUpdate.Items is { Count: > 0 } items)
            {
                for (var i = 0; i < items.Count; i++)
                {
                    var itemUpdate = items[i];
                    var key = completeKeys is not null
                        ? completeKeys[i]
                        : ConvertDictionaryKey(itemUpdate.Index, targetKeyType);

                    if (itemUpdate.Id is not null)
                    {
                        var itemProperties = context.GetSubjectProperties(itemUpdate.Id);
                        if (workingDictionary.GetValueOrDefault(key) is { } existingItem &&
                            SubjectUpdateApplier.TryApplyToHeldSubject(itemUpdate.Id, existingItem, itemProperties, context))
                        {
                            continue;
                        }

                        if (canWriteContainer)
                        {
                            workingDictionary[key] = CreateAndApplyItem(parent, property, key, itemUpdate.Id, itemProperties, context);
                        }
                        structureChanged = true;
                    }
                }
            }

            if (structureChanged)
            {
                if (canWriteContainer)
                {
                    var dictionary = context.SubjectFactory.CreateSubjectDictionary(property.Type, workingDictionary);
                    context.SetPropertyValue(property, propertyUpdate.Timestamp, dictionary);
                }
                else
                {
                    context.RecordDroppedStructure(property);
                }
            }
        }
        finally
        {
            if (completeKeys is { Length: > 0 })
                ArrayPool<object>.Shared.Return(completeKeys, clearArray: true);
        }
    }

    /// <summary>
    /// Whether an update marked <see cref="SubjectPropertyUpdateMode.Complete"/> states a whole membership:
    /// no operations, a count equal to the number of items, and an ID with a payload on every item. The
    /// callers check the positions or keys.
    /// </summary>
    private static bool HasCompleteMembershipShape(SubjectPropertyUpdate update, SubjectUpdateApplyContext context)
    {
        if (update.Mode != SubjectPropertyUpdateMode.Complete ||
            update.Count is not { } count ||
            update.Operations is { Count: > 0 } ||
            (update.Items?.Count ?? 0) != count)
        {
            return false;
        }

        if (update.Items is { } items)
        {
            for (var i = 0; i < items.Count; i++)
            {
                if (items[i].Id is not { } subjectId || !context.Subjects.ContainsKey(subjectId))
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether a collection update states a whole membership whose indices are distinct and cover every
    /// position from zero through the count.
    /// </summary>
    private static bool IsCompleteCollectionMembership(SubjectPropertyUpdate update, SubjectUpdateApplyContext context)
    {
        if (!HasCompleteMembershipShape(update, context))
            return false;

        var count = update.Count!.Value;
        if (count == 0)
            return true;

        Span<bool> covered = count <= 256 ? stackalloc bool[count] : new bool[count];
        covered.Clear();

        var items = update.Items!;
        for (var i = 0; i < items.Count; i++)
        {
            var index = ConvertIndexToInt(items[i].Index);
            if ((uint)index >= (uint)count || covered[index])
                return false;

            covered[index] = true;
        }

        return true;
    }

    /// <summary>
    /// Returns the keys of a dictionary update that states a whole membership, converted to
    /// <paramref name="keyType"/> in the order of its items, and seeds <paramref name="workingDictionary"/>
    /// with a <c>null</c> member per key, which the existing entries and then the items fill. Returns
    /// <c>null</c> and leaves the working dictionary empty when the update does not state a whole membership,
    /// which includes a duplicate converted key. A returned array that is not empty is rented from
    /// <see cref="ArrayPool{T}.Shared"/> and holds its keys in the first slots only.
    /// </summary>
    private static object[]? TryGetCompleteDictionaryMembership(
        SubjectPropertyUpdate update,
        SubjectUpdateApplyContext context,
        Type keyType,
        Dictionary<object, IInterceptorSubject> workingDictionary)
    {
        if (!HasCompleteMembershipShape(update, context))
            return null;

        var count = update.Count!.Value;
        if (count == 0)
            return [];

        var items = update.Items!;
        var keys = ArrayPool<object>.Shared.Rent(count);
        for (var i = 0; i < count; i++)
        {
            var key = ConvertDictionaryKey(items[i].Index, keyType);
            if (!workingDictionary.TryAdd(key, null!))
            {
                workingDictionary.Clear();
                ArrayPool<object>.Shared.Return(keys, clearArray: true);
                return null;
            }

            keys[i] = key;
        }

        return keys;
    }

    private static int ConvertIndexToInt(object index) => index switch
    {
        int i => i,
        JsonElement json => json.GetInt32(),
        _ => Convert.ToInt32(index)
    };

    private static object ConvertDictionaryKey(object key, Type targetKeyType)
        => DictionaryKeyConverter.Convert(key, targetKeyType);

    private static IInterceptorSubject CreateAndApplyItem(
        IInterceptorSubject parent,
        RegisteredSubjectProperty property,
        object indexOrKey,
        string subjectId,
        Dictionary<string, SubjectPropertyUpdate> properties,
        SubjectUpdateApplyContext context)
    {
        // The item type costs a lookup, so it is resolved only for an ID that is already bound.
        Type? itemType = null;
        if (context.IsBound(subjectId))
        {
            itemType = GetItemType(property);
            if (context.TryGetBoundSubject(subjectId, itemType) is { } boundItem)
            {
                return boundItem;
            }
        }

        var newItem = context.SubjectFactory.CreateCollectionSubject(property, indexOrKey);
        newItem.Context.AddFallbackContext(parent.Context);

        // Claiming before recursing is what terminates a payload that references itself.
        if (context.ClaimSubjectPayload(subjectId, newItem, itemType) == SubjectPayloadClaim.Claimed)
        {
            SubjectUpdateApplier.ApplyPropertyUpdates(newItem, properties, context);
        }

        return newItem;
    }

    /// <summary>
    /// The declared type of one item of a subject container, which is what an instance has to satisfy
    /// before it can enter that container.
    /// </summary>
    private static Type GetItemType(RegisteredSubjectProperty property)
        => property.Type.IsArray
            ? property.Type.GetElementType()!
            : property.IsSubjectDictionary
                ? property.Type.GetDictionaryKeyAndValueTypes().Value
                : property.Type.GetCollectionElementType();
}
