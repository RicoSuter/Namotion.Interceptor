using System.Collections;
using System.Text.Json;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Updates;

public static class SubjectUpdateExtensions
{
    /// <summary>
    /// Applies all values of the source update data to a subject and optionally creates missing child subjects (e.g. using DefaultSubjectFactory.Instance).
    /// </summary>
    /// <param name="subject">The subject.</param>
    /// <param name="update">The update data.</param>
    /// <param name="source">The source the update data is coming from (used for change tracking to prevent echo back).</param>
    /// <param name="subjectFactory">The subject factory to create missing subjects, null to ignore updates on missing subjects.</param>
    /// <param name="transformValueBeforeApply">The function to transform the update before applying it.</param>
    public static void ApplySubjectUpdateFromSource(
        this IInterceptorSubject subject,
        SubjectUpdate update,
        object source, ISubjectFactory? subjectFactory,
        Action<RegisteredSubjectProperty, SubjectPropertyUpdate>? transformValueBeforeApply = null)
    {
        var receivedTimestamp = DateTimeOffset.UtcNow;

        subject.ApplySubjectPropertyUpdate(update,
            (registeredProperty, propertyUpdate) =>
            {
                transformValueBeforeApply?.Invoke(registeredProperty, propertyUpdate);
                var value = ConvertValueToTargetType(propertyUpdate.Value, registeredProperty.Type);
                registeredProperty.SetValueFromSource(source, propertyUpdate.Timestamp, receivedTimestamp, value);
            },
            subjectFactory);
    }

    /// <summary>
    /// Applies all values of the update data to a subject and optionally creates missing child subjects (e.g. using DefaultSubjectFactory.Instance).
    /// </summary>
    /// <param name="subject">The subject.</param>
    /// <param name="update">The update data.</param>
    /// <param name="subjectFactory">The subject factory to create missing subjects, null to ignore updates on missing subjects.</param>
    /// <param name="transformValueBeforeApply">The function to transform the update before applying it.</param>
    public static void ApplySubjectUpdate(
        this IInterceptorSubject subject,
        SubjectUpdate update,
        ISubjectFactory? subjectFactory,
        Action<RegisteredSubjectProperty, SubjectPropertyUpdate>? transformValueBeforeApply = null)
    {
        subject.ApplySubjectPropertyUpdate(update,
            (registeredProperty, propertyUpdate) =>
            {
                transformValueBeforeApply?.Invoke(registeredProperty, propertyUpdate);
                var value = ConvertValueToTargetType(propertyUpdate.Value, registeredProperty.Type);
                registeredProperty.SetValue(value);
            },
            subjectFactory ?? DefaultSubjectFactory.Instance);
    }

    /// <summary>
    /// Converts a value (potentially a JsonElement from deserialization) to the target property type.
    /// </summary>
    private static object? ConvertValueToTargetType(object? value, Type targetType)
    {
        if (value is null)
            return null;

        if (value is not JsonElement jsonElement)
            return value;

        return jsonElement.Deserialize(targetType);
    }

    /// <summary>
    /// Converts a collection index (potentially a JsonElement from deserialization) to int.
    /// </summary>
    private static int ConvertIndexToInt(object index)
    {
        if (index is int intIndex)
            return intIndex;

        if (index is JsonElement jsonElement)
            return jsonElement.GetInt32();

        return Convert.ToInt32(index);
    }

    /// <summary>
    /// Applies all values of the update data to a subject property and optionally creates missing child subjects (e.g. using DefaultSubjectFactory.Instance).
    /// </summary>
    /// <param name="subject">The subject.</param>
    /// <param name="update">The update data.</param>
    /// <param name="applyValuePropertyUpdate">The action to apply a given update to the property value.</param>
    /// <param name="subjectFactory">The subject factory to create missing subjects, null to ignore updates on missing subjects.</param>
    /// <param name="registry">The optional registry. Might need to be passed because it is not yet accessible via subject.</param>
    public static void ApplySubjectPropertyUpdate(
        this IInterceptorSubject subject,
        SubjectUpdate update,
        Action<RegisteredSubjectProperty, SubjectPropertyUpdate> applyValuePropertyUpdate,
        ISubjectFactory? subjectFactory,
        ISubjectRegistry? registry = null)
    {
        foreach (var (propertyName, propertyUpdate) in update.Properties)
        {
            if (propertyUpdate.Attributes is not null)
            {
                foreach (var (attributeName, attributeUpdate) in propertyUpdate.Attributes)
                {
                    var registeredAttribute = subject
                        .TryGetRegisteredSubject()?
                        .TryGetPropertyAttribute(propertyName, attributeName)
                            ?? throw new InvalidOperationException("Attribute not found on property.");

                    ApplySubjectPropertyUpdate(subject, registeredAttribute.Name, attributeUpdate, applyValuePropertyUpdate, subjectFactory, registry);
                }
            }

            ApplySubjectPropertyUpdate(subject, propertyName, propertyUpdate, applyValuePropertyUpdate, subjectFactory, registry);
        }
    }

    private static void ApplySubjectPropertyUpdate(
        IInterceptorSubject subject, string propertyName,
        SubjectPropertyUpdate propertyUpdate,
        Action<RegisteredSubjectProperty, SubjectPropertyUpdate> applyValuePropertyUpdate,
        ISubjectFactory? subjectFactory,
        ISubjectRegistry? registry)
    {
        var registeredProperty = subject.TryGetRegisteredProperty(propertyName, registry);
        if (registeredProperty is null)
            return;

        switch (propertyUpdate.Kind)
        {
            case SubjectPropertyUpdateKind.Value:
                using (SubjectChangeContext.WithChangedTimestamp(propertyUpdate.Timestamp))
                {
                    applyValuePropertyUpdate.Invoke(registeredProperty, propertyUpdate);
                }

                break;

            case SubjectPropertyUpdateKind.Item:
                if (propertyUpdate.Item is not null)
                {
                    if (registeredProperty.GetValue() is IInterceptorSubject existingItem)
                    {
                        // update existing item
                        existingItem.ApplySubjectPropertyUpdate(propertyUpdate.Item, applyValuePropertyUpdate, subjectFactory);
                    }
                    else
                    {
                        // create new item
                        var item = subjectFactory?.CreateSubject(registeredProperty);
                        if (item != null)
                        {
                            item.Context.AddFallbackContext(subject.Context);

                            var parentRegistry = subject.Context.GetService<ISubjectRegistry>();
                            item.ApplySubjectPropertyUpdate(propertyUpdate.Item, applyValuePropertyUpdate, subjectFactory, parentRegistry);

                            using (SubjectChangeContext.WithChangedTimestamp(propertyUpdate.Timestamp))
                            {
                                registeredProperty.SetValue(item);
                            }
                        }
                    }
                }
                else
                {
                    // set item to null
                    using (SubjectChangeContext.WithChangedTimestamp(propertyUpdate.Timestamp))
                    {
                        registeredProperty.SetValue(null);
                    }
                }
                break;

            case SubjectPropertyUpdateKind.Collection:
                if (registeredProperty.IsSubjectDictionary)
                {
                    ApplyDictionaryUpdate(subject, registeredProperty, propertyUpdate, applyValuePropertyUpdate, subjectFactory);
                }
                else if (registeredProperty.IsSubjectCollection)
                {
                    ApplyCollectionUpdate(subject, registeredProperty, propertyUpdate, applyValuePropertyUpdate, subjectFactory);
                }
                else
                {
                    throw new InvalidOperationException("Collection update received for a property that is not a collection or dictionary.");
                }
                break;
        }
    }

    /// <summary>
    /// Applies collection updates using two-phase approach:
    /// Phase 1: Apply structural operations (Remove, Insert, Move)
    /// Phase 2: Apply sparse property updates by final index
    /// </summary>
    private static void ApplyCollectionUpdate(
        IInterceptorSubject subject,
        RegisteredSubjectProperty registeredProperty,
        SubjectPropertyUpdate propertyUpdate,
        Action<RegisteredSubjectProperty, SubjectPropertyUpdate> applyValuePropertyUpdate,
        ISubjectFactory? subjectFactory)
    {
        var existingItems = (registeredProperty.GetValue() as ICollection)?.Cast<IInterceptorSubject>().ToList()
            ?? new List<IInterceptorSubject>();
        var parentRegistry = subject.Context.GetService<ISubjectRegistry>();
        var structureChanged = false;

        // Phase 1: Apply structural operations in order
        if (propertyUpdate.Operations is { Count: > 0 })
        {
            foreach (var operation in propertyUpdate.Operations)
            {
                var index = ConvertIndexToInt(operation.Index);

                switch (operation.Action)
                {
                    case SubjectCollectionOperationType.Remove:
                        if (index >= 0 && index < existingItems.Count)
                        {
                            existingItems.RemoveAt(index);
                            structureChanged = true;
                        }
                        break;

                    case SubjectCollectionOperationType.Insert:
                        if (subjectFactory is not null && operation.Item is not null)
                        {
                            var newItem = subjectFactory.CreateCollectionSubject(registeredProperty, index);
                            newItem.Context.AddFallbackContext(subject.Context);
                            newItem.ApplySubjectPropertyUpdate(operation.Item, applyValuePropertyUpdate, subjectFactory, parentRegistry);

                            if (index >= existingItems.Count)
                            {
                                existingItems.Add(newItem);
                            }
                            else
                            {
                                existingItems.Insert(index, newItem);
                            }
                            structureChanged = true;
                        }
                        break;

                    case SubjectCollectionOperationType.Move:
                        if (operation.FromIndex.HasValue)
                        {
                            var fromIndex = operation.FromIndex.Value;
                            if (fromIndex >= 0 && fromIndex < existingItems.Count && index >= 0)
                            {
                                var item = existingItems[fromIndex];
                                existingItems.RemoveAt(fromIndex);
                                if (index >= existingItems.Count)
                                {
                                    existingItems.Add(item);
                                }
                                else
                                {
                                    existingItems.Insert(index, item);
                                }
                                structureChanged = true;
                            }
                        }
                        break;
                }
            }
        }

        // Phase 2: Apply property updates by final index
        // For "complete updates" (no Operations), also create items that don't exist yet
        if (propertyUpdate.Collection is { Count: > 0 })
        {
            foreach (var updateItem in propertyUpdate.Collection)
            {
                if (updateItem.Item is null)
                {
                    continue;
                }

                var index = ConvertIndexToInt(updateItem.Index);

                if (index >= 0 && index < existingItems.Count)
                {
                    // Update existing item at final index
                    existingItems[index].ApplySubjectPropertyUpdate(updateItem.Item, applyValuePropertyUpdate, subjectFactory);
                }
                else if (index >= 0 && subjectFactory is not null)
                {
                    // Create new item for index beyond existing collection (complete update case)
                    // This handles backward compatibility when Operations is empty but Collection has all items
                    var newItem = subjectFactory.CreateCollectionSubject(registeredProperty, index);
                    newItem.Context.AddFallbackContext(subject.Context);
                    newItem.ApplySubjectPropertyUpdate(updateItem.Item, applyValuePropertyUpdate, subjectFactory, parentRegistry);

                    // Expand list to accommodate the index
                    while (existingItems.Count < index)
                    {
                        existingItems.Add(null!);
                    }

                    if (index >= existingItems.Count)
                    {
                        existingItems.Add(newItem);
                    }
                    else
                    {
                        existingItems[index] = newItem;
                    }
                    structureChanged = true;
                }
            }
        }

        // Update collection if structure changed
        if (structureChanged && subjectFactory is not null)
        {
            var collection = subjectFactory.CreateSubjectCollection(registeredProperty.Type, existingItems);
            using (SubjectChangeContext.WithChangedTimestamp(propertyUpdate.Timestamp))
            {
                registeredProperty.SetValue(collection);
            }
        }
    }

    /// <summary>
    /// Applies dictionary updates using two-phase approach:
    /// Phase 1: Apply structural operations (Remove, Insert)
    /// Phase 2: Apply sparse property updates by key
    /// </summary>
    private static void ApplyDictionaryUpdate(
        IInterceptorSubject subject,
        RegisteredSubjectProperty registeredProperty,
        SubjectPropertyUpdate propertyUpdate,
        Action<RegisteredSubjectProperty, SubjectPropertyUpdate> applyValuePropertyUpdate,
        ISubjectFactory? subjectFactory)
    {
        var existingDict = registeredProperty.GetValue() as IDictionary;
        var parentRegistry = subject.Context.GetService<ISubjectRegistry>();
        var structureChanged = false;

        // Create a working copy of the dictionary
        var workingDict = new Dictionary<object, IInterceptorSubject>();
        if (existingDict is not null)
        {
            foreach (DictionaryEntry entry in existingDict)
            {
                if (entry.Value is IInterceptorSubject item)
                {
                    workingDict[entry.Key] = item;
                }
            }
        }

        // Phase 1: Apply structural operations
        if (propertyUpdate.Operations is { Count: > 0 })
        {
            foreach (var operation in propertyUpdate.Operations)
            {
                var key = ConvertDictionaryKey(operation.Index);

                switch (operation.Action)
                {
                    case SubjectCollectionOperationType.Remove:
                        if (workingDict.Remove(key))
                        {
                            structureChanged = true;
                        }
                        break;

                    case SubjectCollectionOperationType.Insert:
                        if (subjectFactory is not null && operation.Item is not null)
                        {
                            var newItem = subjectFactory.CreateCollectionSubject(registeredProperty, key);
                            newItem.Context.AddFallbackContext(subject.Context);
                            newItem.ApplySubjectPropertyUpdate(operation.Item, applyValuePropertyUpdate, subjectFactory, parentRegistry);
                            workingDict[key] = newItem;
                            structureChanged = true;
                        }
                        break;

                    // Move is not applicable for dictionaries
                }
            }
        }

        // Phase 2: Apply property updates by key
        // For "complete updates" (no Operations), also create items that don't exist yet
        if (propertyUpdate.Collection is { Count: > 0 })
        {
            foreach (var updateItem in propertyUpdate.Collection)
            {
                if (updateItem.Item is null)
                {
                    continue;
                }

                var key = ConvertDictionaryKey(updateItem.Index);

                if (workingDict.TryGetValue(key, out var existingItem))
                {
                    // Update existing item at key
                    existingItem.ApplySubjectPropertyUpdate(updateItem.Item, applyValuePropertyUpdate, subjectFactory);
                }
                else if (subjectFactory is not null)
                {
                    // Create new item for key that doesn't exist (complete update case)
                    // This handles backward compatibility when Operations is empty but Collection has all items
                    var newItem = subjectFactory.CreateCollectionSubject(registeredProperty, key);
                    newItem.Context.AddFallbackContext(subject.Context);
                    newItem.ApplySubjectPropertyUpdate(updateItem.Item, applyValuePropertyUpdate, subjectFactory, parentRegistry);
                    workingDict[key] = newItem;
                    structureChanged = true;
                }
            }
        }

        // Update dictionary if structure changed
        if (structureChanged && subjectFactory is not null)
        {
            var dictionary = subjectFactory.CreateSubjectDictionary(registeredProperty.Type, workingDict);
            using (SubjectChangeContext.WithChangedTimestamp(propertyUpdate.Timestamp))
            {
                registeredProperty.SetValue(dictionary);
            }
        }
    }

    /// <summary>
    /// Converts a dictionary key (potentially a JsonElement from deserialization) to string.
    /// </summary>
    private static object ConvertDictionaryKey(object key)
    {
        if (key is JsonElement jsonElement)
        {
            return jsonElement.GetString() ?? jsonElement.ToString();
        }

        return key;
    }
}
