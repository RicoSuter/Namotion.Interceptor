using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Connectors.Updates;

public class SubjectPropertyUpdate
{
    /// <summary>
    /// Gets the kind of entity which is updated.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SubjectPropertyUpdateKind Kind { get; internal set; }

    /// <summary>
    /// Gets or sets the value of the property update if kind is Value.
    /// </summary>
    public object? Value { get; set; }

    /// <summary>
    /// Gets the timestamp of the property update or null if unknown.
    /// </summary>
    public DateTimeOffset? Timestamp { get; private set; }

    /// <summary>
    /// Gets the item if kind is Item.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public SubjectUpdate? Item { get; internal set; }

    /// <summary>
    /// Gets or sets the all items of the collection if kind is Collection.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public IReadOnlyCollection<SubjectPropertyCollectionUpdate>? Collection { get; internal set; }

    /// <summary>
    /// Gets the updated attributes.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Dictionary<string, SubjectPropertyUpdate>? Attributes { get; internal set; }

    /// <summary>
    /// Gets or sets custom extension data added by the transformPropertyUpdate function.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, object>? ExtensionData { get; set; }

    public static SubjectPropertyUpdate Create<T>(T value, DateTimeOffset? timestamp = null)
    {
        return new SubjectPropertyUpdate
        {
            Kind = SubjectPropertyUpdateKind.Value,
            Value = value,
            Timestamp = timestamp
        };
    }

    public static SubjectPropertyUpdate Create(SubjectUpdate itemUpdate, DateTimeOffset? timestamp = null)
    {
        return new SubjectPropertyUpdate
        {
            Kind = SubjectPropertyUpdateKind.Item,
            Item = itemUpdate,
            Timestamp = timestamp
        };
    }

    public static SubjectPropertyUpdate Create(params IReadOnlyList<SubjectPropertyCollectionUpdate> collectionUpdates)
    {
        return new SubjectPropertyUpdate
        {
            Kind = SubjectPropertyUpdateKind.Collection,
            Collection = collectionUpdates
        };
    }

    internal static SubjectPropertyUpdate CreateCompleteUpdate(RegisteredSubjectProperty property,
        ReadOnlySpan<ISubjectUpdateProcessor> processors,
        Dictionary<IInterceptorSubject, SubjectUpdate> knownSubjectUpdates,
        Dictionary<SubjectPropertyUpdate, SubjectPropertyUpdateReference>? propertyUpdates)
    {
        var propertyUpdate = new SubjectPropertyUpdate
        {
            Attributes = CreateAttributeUpdates(property, processors, knownSubjectUpdates, propertyUpdates)
        };

        propertyUpdate.ApplyValue(
            property, property.Reference.TryGetWriteTimestamp(), property.GetValue(),
            createReferenceUpdate: true, processors, knownSubjectUpdates, propertyUpdates);

        return propertyUpdate;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Dictionary<string, SubjectPropertyUpdate>? CreateAttributeUpdates(RegisteredSubjectProperty property,
        ReadOnlySpan<ISubjectUpdateProcessor> processors,
        Dictionary<IInterceptorSubject, SubjectUpdate> knownSubjectUpdates,
        Dictionary<SubjectPropertyUpdate, SubjectPropertyUpdateReference>? propertyUpdates)
    {
        Dictionary<string, SubjectPropertyUpdate>? attributes = null;
        foreach (var attribute in property.Attributes)
        {
            if (attribute.HasGetter)
            {
                attributes ??= new Dictionary<string, SubjectPropertyUpdate>();

                var attributeUpdate = CreateCompleteUpdate(attribute, processors, knownSubjectUpdates, propertyUpdates);
                attributes[attribute.AttributeMetadata.AttributeName] = attributeUpdate;

                propertyUpdates?.TryAdd(attributeUpdate, new SubjectPropertyUpdateReference(attribute, attributes));
            }
        }

        return attributes?.Count > 0 ? attributes : null;
    }

    /// <summary>
    /// Adds a complete update of the given value to the property update.
    /// </summary>
    /// <param name="property">The property.</param>
    /// <param name="timestamp">The timestamp of the value change.</param>
    /// <param name="value">The value to apply.</param>
    /// <param name="createReferenceUpdate">Create update with reference instead of returning existing update.</param>
    /// <param name="processors">The update processors to filter and transform updates.</param>
    /// <param name="knownSubjectUpdates">The known subject updates.</param>
    /// <param name="propertyUpdates">Optional list to collect property updates for transformation.</param>
    internal void ApplyValue(RegisteredSubjectProperty property, DateTimeOffset? timestamp, object? value,
        bool createReferenceUpdate,
        ReadOnlySpan<ISubjectUpdateProcessor> processors,
        Dictionary<IInterceptorSubject, SubjectUpdate> knownSubjectUpdates,
        Dictionary<SubjectPropertyUpdate, SubjectPropertyUpdateReference>? propertyUpdates = null)
    {
        Timestamp = timestamp;

        if (property.IsSubjectDictionary)
        {
            Kind = SubjectPropertyUpdateKind.Collection;
            Collection = value is IReadOnlyDictionary<string, IInterceptorSubject?> dictionary
                ? CreateDictionaryCollectionUpdates(dictionary, createReferenceUpdate, processors, knownSubjectUpdates, propertyUpdates)
                : null;
        }
        else if (property.IsSubjectCollection)
        {
            Kind = SubjectPropertyUpdateKind.Collection;
            Collection = value is IEnumerable<IInterceptorSubject> collection
                ? CreateEnumerableCollectionUpdates(collection, createReferenceUpdate, processors, knownSubjectUpdates, propertyUpdates)
                : null;
        }
        else if (property.IsSubjectReference)
        {
            Kind = SubjectPropertyUpdateKind.Item;
            // Always use createReferenceUpdate: true for subject references to avoid circular object references.
            // The cycle is broken at the reference level (e.g., child.Father -> Reference), not at collections.
            Item = value is IInterceptorSubject itemSubject ?
                SubjectUpdate.GetOrCreateCompleteUpdate(itemSubject, createReferenceUpdate: true, processors, knownSubjectUpdates, propertyUpdates) :
                null;
        }
        else
        {
            Kind = SubjectPropertyUpdateKind.Value;
            Value = value;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static List<SubjectPropertyCollectionUpdate> CreateDictionaryCollectionUpdates(IReadOnlyDictionary<string, IInterceptorSubject?> dictionary,
        bool createReferenceUpdate,
        ReadOnlySpan<ISubjectUpdateProcessor> processors,
        Dictionary<IInterceptorSubject, SubjectUpdate> knownSubjectUpdates,
        Dictionary<SubjectPropertyUpdate, SubjectPropertyUpdateReference>? propertyUpdates)
    {
        var collectionUpdates = new List<SubjectPropertyCollectionUpdate>(dictionary.Count);
        foreach (var key in dictionary.Keys)
        {
            var item = dictionary[key];
            collectionUpdates.Add(new SubjectPropertyCollectionUpdate
            {
                Item = item is not null ?
                    SubjectUpdate.GetOrCreateCompleteUpdate(item, createReferenceUpdate, processors, knownSubjectUpdates, propertyUpdates) :
                    null,
                Index = key
            });
        }

        return collectionUpdates;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static List<SubjectPropertyCollectionUpdate> CreateEnumerableCollectionUpdates(IEnumerable<IInterceptorSubject> enumerable,
        bool createReferenceUpdate,
        ReadOnlySpan<ISubjectUpdateProcessor> processors,
        Dictionary<IInterceptorSubject, SubjectUpdate> knownSubjectUpdates,
        Dictionary<SubjectPropertyUpdate, SubjectPropertyUpdateReference>? propertyUpdates)
    {
        if (enumerable is ICollection<IInterceptorSubject> collection)
        {
            var index = 0;
            var collectionUpdates = new List<SubjectPropertyCollectionUpdate>(collection.Count);
            foreach (var itemSubject in collection)
            {
                collectionUpdates.Add(new SubjectPropertyCollectionUpdate
                {
                    Item = SubjectUpdate.GetOrCreateCompleteUpdate(itemSubject, createReferenceUpdate, processors, knownSubjectUpdates, propertyUpdates),
                    Index = index++
                });
            }

            return collectionUpdates;
        }
        else
        {
            var index = 0;
            var collectionUpdates = new List<SubjectPropertyCollectionUpdate>();
            foreach (var itemSubject in enumerable)
            {
                collectionUpdates.Add(new SubjectPropertyCollectionUpdate
                {
                    Item = SubjectUpdate.GetOrCreateCompleteUpdate(itemSubject, createReferenceUpdate, processors, knownSubjectUpdates, propertyUpdates),
                    Index = index++
                });
            }

            return collectionUpdates;
        }
    }
}
