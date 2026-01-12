using System.Collections;
using System.Text.Json;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Updates;

/// <summary>
/// Extension methods for applying SubjectUpdate to subjects.
/// </summary>
public static class SubjectUpdateExtensions
{
    /// <summary>
    /// Applies update from an external source with source tracking.
    /// </summary>
    /// <param name="subject">The subject.</param>
    /// <param name="update">The update data.</param>
    /// <param name="source">The source the update data is coming from (used for change tracking to prevent echo back).</param>
    /// <param name="subjectFactory">The subject factory to create missing subjects, null to ignore updates on missing subjects.</param>
    /// <param name="transformValueBeforeApply">The function to transform the update before applying it.</param>
    public static void ApplySubjectUpdateFromSource(
        this IInterceptorSubject subject,
        SubjectUpdate update,
        object source,
        ISubjectFactory? subjectFactory,
        Action<RegisteredSubjectProperty, SubjectPropertyUpdate>? transformValueBeforeApply = null)
    {
        var receivedTimestamp = DateTimeOffset.UtcNow;

        subject.ApplySubjectUpdate(update, subjectFactory, (property, propertyUpdate) =>
        {
            transformValueBeforeApply?.Invoke(property, propertyUpdate);
            var value = ConvertValue(propertyUpdate.Value, property.Type);
            property.SetValueFromSource(source, propertyUpdate.Timestamp, receivedTimestamp, value);
        });
    }

    /// <summary>
    /// Applies update to a subject.
    /// </summary>
    /// <param name="subject">The subject.</param>
    /// <param name="update">The update data.</param>
    /// <param name="subjectFactory">The subject factory to create missing subjects, null to ignore updates on missing subjects.</param>
    /// <param name="applyValueOverride">The function to apply an update to a property.</param>
    public static void ApplySubjectUpdate(
        this IInterceptorSubject subject,
        SubjectUpdate update,
        ISubjectFactory? subjectFactory,
        Action<RegisteredSubjectProperty, SubjectPropertyUpdate>? applyValueOverride = null)
    {
        if (string.IsNullOrEmpty(update.Root))
            return;

        if (!update.Subjects.TryGetValue(update.Root, out var rootProperties))
            return;

        var context = new ApplyContext(
            update.Subjects,
            subjectFactory ?? DefaultSubjectFactory.Instance,
            applyValueOverride ?? ((property, propertyUpdate) =>
            {
                using (SubjectChangeContext.WithChangedTimestamp(propertyUpdate.Timestamp))
                {
                    var value = ConvertValue(propertyUpdate.Value, property.Type);
                    property.SetValue(value);
                }
            }));

        // Mark root as processed to prevent cycles
        context.TryMarkAsProcessed(update.Root);
        ApplyProperties(subject, rootProperties, context);
    }

    private static void ApplyProperties(
        IInterceptorSubject subject,
        Dictionary<string, SubjectPropertyUpdate> properties,
        ApplyContext context)
    {
        var registry = subject.Context.GetService<ISubjectRegistry>();

        foreach (var (propertyName, propertyUpdate) in properties)
        {
            // Apply attributes first
            if (propertyUpdate.Attributes is not null)
            {
                foreach (var (attrName, attrUpdate) in propertyUpdate.Attributes)
                {
                    var registeredAttr = subject.TryGetRegisteredSubject()?
                        .TryGetPropertyAttribute(propertyName, attrName);

                    if (registeredAttr is not null)
                    {
                        ApplyPropertyUpdate(subject, registeredAttr.Name, attrUpdate, context, registry);
                    }
                }
            }

            ApplyPropertyUpdate(subject, propertyName, propertyUpdate, context, registry);
        }
    }

    private static void ApplyPropertyUpdate(
        IInterceptorSubject subject,
        string propertyName,
        SubjectPropertyUpdate propertyUpdate,
        ApplyContext context,
        ISubjectRegistry? registry)
    {
        var registeredProperty = subject.TryGetRegisteredProperty(propertyName, registry);
        if (registeredProperty is null)
            return;

        switch (propertyUpdate.Kind)
        {
            case SubjectPropertyUpdateKind.Value:
                // Let the ApplyValue callback handle change context (it may use SetValueFromSource which sets source)
                context.ApplyValue(registeredProperty, propertyUpdate);
                break;

            case SubjectPropertyUpdateKind.Item:
                ApplyItemUpdate(subject, registeredProperty, propertyUpdate, context);
                break;

            case SubjectPropertyUpdateKind.Collection:
                if (registeredProperty.IsSubjectDictionary)
                    ApplyDictionaryUpdate(subject, registeredProperty, propertyUpdate, context);
                else
                    ApplyCollectionUpdate(subject, registeredProperty, propertyUpdate, context);
                break;
        }
    }

    private static void ApplyItemUpdate(
        IInterceptorSubject parent,
        RegisteredSubjectProperty property,
        SubjectPropertyUpdate propertyUpdate,
        ApplyContext context)
    {
        if (propertyUpdate.Id is not null &&
            context.Subjects.TryGetValue(propertyUpdate.Id, out var itemProperties))
        {
            var existingItem = property.GetValue() as IInterceptorSubject;

            if (existingItem is not null)
            {
                // Update existing (skip if already processed to break cycles)
                if (context.TryMarkAsProcessed(propertyUpdate.Id))
                {
                    ApplyProperties(existingItem, itemProperties, context);
                }
            }
            else
            {
                // Create new
                var newItem = context.SubjectFactory.CreateSubject(property);
                if (newItem is not null)
                {
                    newItem.Context.AddFallbackContext(parent.Context);

                    // Mark as processed before applying to break cycles
                    if (context.TryMarkAsProcessed(propertyUpdate.Id))
                    {
                        ApplyProperties(newItem, itemProperties, context);
                    }

                    using (SubjectChangeContext.WithChangedTimestamp(propertyUpdate.Timestamp))
                    {
                        property.SetValue(newItem);
                    }
                }
            }
        }
        else
        {
            // Set to null
            using (SubjectChangeContext.WithChangedTimestamp(propertyUpdate.Timestamp))
            {
                property.SetValue(null);
            }
        }
    }

    private static void ApplyCollectionUpdate(
        IInterceptorSubject parent,
        RegisteredSubjectProperty property,
        SubjectPropertyUpdate propertyUpdate,
        ApplyContext context)
    {
        var existingItems = (property.GetValue() as IEnumerable<IInterceptorSubject>)?
            .ToList() ?? new List<IInterceptorSubject>();
        var originalItems = new List<IInterceptorSubject>(existingItems);
        var workingItems = new List<IInterceptorSubject>(existingItems);
        var structureChanged = false;

        // Phase 1: Structural operations
        if (propertyUpdate.Operations is { Count: > 0 })
        {
            // Separate move operations from insert/remove
            var hasOnlyMoves = propertyUpdate.Operations.All(op => op.Action == SubjectCollectionOperationType.Move);

            if (hasOnlyMoves && propertyUpdate.Operations.Count > 0)
            {
                // Build new array atomically using move operations
                // Each move specifies: item from originalItems[FromIndex] goes to workingItems[Index]
                var newItems = new IInterceptorSubject[originalItems.Count];
                foreach (var operation in propertyUpdate.Operations)
                {
                    var toIndex = ConvertIndexToInt(operation.Index);
                    var fromIndex = operation.FromIndex!.Value;
                    if (fromIndex >= 0 && fromIndex < originalItems.Count && toIndex >= 0 && toIndex < newItems.Length)
                    {
                        newItems[toIndex] = originalItems[fromIndex];
                        structureChanged = true;
                    }
                }
                workingItems = newItems.ToList();
            }
            else
            {
                // Apply operations sequentially for insert/remove
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
                            if (operation.Id is not null && context.Subjects.TryGetValue(operation.Id, out var itemProps))
                            {
                                var newItem = context.SubjectFactory.CreateCollectionSubject(property, index);
                                newItem.Context.AddFallbackContext(parent.Context);

                                if (context.TryMarkAsProcessed(operation.Id))
                                {
                                    ApplyProperties(newItem, itemProps, context);
                                }

                                if (index >= workingItems.Count)
                                    workingItems.Add(newItem);
                                else
                                    workingItems.Insert(index, newItem);
                                structureChanged = true;
                            }
                            break;

                        case SubjectCollectionOperationType.Move:
                            // Mixed moves with other ops - apply sequentially (less accurate)
                            if (operation.FromIndex.HasValue)
                            {
                                var from = operation.FromIndex.Value;
                                if (from >= 0 && from < workingItems.Count && index >= 0)
                                {
                                    var item = workingItems[from];
                                    workingItems.RemoveAt(from);
                                    if (index >= workingItems.Count)
                                        workingItems.Add(item);
                                    else
                                        workingItems.Insert(index, item);
                                    structureChanged = true;
                                }
                            }
                            break;
                    }
                }
            }
        }

        // Phase 2: Sparse property updates
        if (propertyUpdate.Collection is { Count: > 0 })
        {
            foreach (var collUpdate in propertyUpdate.Collection)
            {
                var index = ConvertIndexToInt(collUpdate.Index);

                if (collUpdate.Id is not null &&
                    context.Subjects.TryGetValue(collUpdate.Id, out var itemProps))
                {
                    if (index >= 0 && index < workingItems.Count)
                    {
                        // Update existing (skip if already processed to break cycles)
                        if (context.TryMarkAsProcessed(collUpdate.Id))
                        {
                            ApplyProperties(workingItems[index], itemProps, context);
                        }
                    }
                    else if (index >= 0)
                    {
                        // Create new (complete update case)
                        var newItem = context.SubjectFactory.CreateCollectionSubject(property, index);
                        newItem.Context.AddFallbackContext(parent.Context);

                        if (context.TryMarkAsProcessed(collUpdate.Id))
                        {
                            ApplyProperties(newItem, itemProps, context);
                        }

                        while (workingItems.Count < index)
                            workingItems.Add(null!);

                        if (index >= workingItems.Count)
                            workingItems.Add(newItem);
                        else
                            workingItems[index] = newItem;
                        structureChanged = true;
                    }
                }
            }
        }

        if (structureChanged)
        {
            var collection = context.SubjectFactory.CreateSubjectCollection(property.Type, workingItems);
            using (SubjectChangeContext.WithChangedTimestamp(propertyUpdate.Timestamp))
            {
                property.SetValue(collection);
            }
        }
    }

    private static void ApplyDictionaryUpdate(
        IInterceptorSubject parent,
        RegisteredSubjectProperty property,
        SubjectPropertyUpdate propertyUpdate,
        ApplyContext context)
    {
        var existingDict = property.GetValue() as IDictionary;
        var workingDict = new Dictionary<object, IInterceptorSubject>();
        var structureChanged = false;

        // Copy existing
        if (existingDict is not null)
        {
            foreach (DictionaryEntry entry in existingDict)
            {
                if (entry.Value is IInterceptorSubject item)
                    workingDict[entry.Key] = item;
            }
        }

        // Phase 1: Structural operations
        if (propertyUpdate.Operations is { Count: > 0 })
        {
            foreach (var operation in propertyUpdate.Operations)
            {
                var key = ConvertDictKey(operation.Index);

                switch (operation.Action)
                {
                    case SubjectCollectionOperationType.Remove:
                        if (workingDict.Remove(key))
                            structureChanged = true;
                        break;

                    case SubjectCollectionOperationType.Insert:
                        if (operation.Id is not null && context.Subjects.TryGetValue(operation.Id, out var itemProps))
                        {
                            var newItem = context.SubjectFactory.CreateCollectionSubject(property, key);
                            newItem.Context.AddFallbackContext(parent.Context);

                            if (context.TryMarkAsProcessed(operation.Id))
                            {
                                ApplyProperties(newItem, itemProps, context);
                            }

                            workingDict[key] = newItem;
                            structureChanged = true;
                        }
                        break;
                }
            }
        }

        // Phase 2: Sparse property updates
        if (propertyUpdate.Collection is { Count: > 0 })
        {
            foreach (var collUpdate in propertyUpdate.Collection)
            {
                var key = ConvertDictKey(collUpdate.Index);

                if (collUpdate.Id is not null &&
                    context.Subjects.TryGetValue(collUpdate.Id, out var itemProps))
                {
                    if (workingDict.TryGetValue(key, out var existing))
                    {
                        // Update existing (skip if already processed to break cycles)
                        if (context.TryMarkAsProcessed(collUpdate.Id))
                        {
                            ApplyProperties(existing, itemProps, context);
                        }
                    }
                    else
                    {
                        // Create new (complete update case)
                        var newItem = context.SubjectFactory.CreateCollectionSubject(property, key);
                        newItem.Context.AddFallbackContext(parent.Context);

                        if (context.TryMarkAsProcessed(collUpdate.Id))
                        {
                            ApplyProperties(newItem, itemProps, context);
                        }

                        workingDict[key] = newItem;
                        structureChanged = true;
                    }
                }
            }
        }

        if (structureChanged)
        {
            var dict = context.SubjectFactory.CreateSubjectDictionary(property.Type, workingDict);
            using (SubjectChangeContext.WithChangedTimestamp(propertyUpdate.Timestamp))
            {
                property.SetValue(dict);
            }
        }
    }

    private static object? ConvertValue(object? value, Type targetType)
    {
        if (value is null)
            return null;

        if (value is JsonElement jsonElement)
        {
            return jsonElement.ValueKind switch
            {
                JsonValueKind.String => jsonElement.GetString(),
                JsonValueKind.Number when targetType == typeof(int) || targetType == typeof(int?) => jsonElement.GetInt32(),
                JsonValueKind.Number when targetType == typeof(long) || targetType == typeof(long?) => jsonElement.GetInt64(),
                JsonValueKind.Number when targetType == typeof(float) || targetType == typeof(float?) => jsonElement.GetSingle(),
                JsonValueKind.Number when targetType == typeof(double) || targetType == typeof(double?) => jsonElement.GetDouble(),
                JsonValueKind.Number when targetType == typeof(decimal) || targetType == typeof(decimal?) => jsonElement.GetDecimal(),
                JsonValueKind.Number => jsonElement.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => value
            };
        }

        return value;
    }

    private static int ConvertIndexToInt(object index)
    {
        if (index is int intIndex)
            return intIndex;

        if (index is JsonElement jsonElement)
            return jsonElement.GetInt32();

        return Convert.ToInt32(index);
    }

    private static object ConvertDictKey(object key)
    {
        if (key is JsonElement jsonElement)
            return jsonElement.GetString() ?? jsonElement.ToString();
        return key;
    }

    private sealed class ApplyContext
    {
        public Dictionary<string, Dictionary<string, SubjectPropertyUpdate>> Subjects { get; }
        public ISubjectFactory SubjectFactory { get; }
        public Action<RegisteredSubjectProperty, SubjectPropertyUpdate> ApplyValue { get; }
        private HashSet<string> ProcessedSubjectIds { get; } = [];

        public ApplyContext(
            Dictionary<string, Dictionary<string, SubjectPropertyUpdate>> subjects,
            ISubjectFactory subjectFactory,
            Action<RegisteredSubjectProperty, SubjectPropertyUpdate> applyValue)
        {
            Subjects = subjects;
            SubjectFactory = subjectFactory;
            ApplyValue = applyValue;
        }

        public bool TryMarkAsProcessed(string subjectId)
            => ProcessedSubjectIds.Add(subjectId);
    }
}
