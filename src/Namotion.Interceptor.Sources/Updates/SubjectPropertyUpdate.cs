using System.Text.Json.Serialization;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Sources.Updates;

public record SubjectPropertyUpdate
{
    /// <summary>
    /// Gets the kind of entity which is updated.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SubjectPropertyUpdateKind Kind { get; internal set; }
    
    /// <summary>
    /// Gets the updated attributes.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Dictionary<string, SubjectPropertyUpdate>? Attributes { get; internal set; }

    /// <summary>
    /// Gets the type of the property value.
    /// </summary>
    public string? Type { get; init; }

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
    /// Gets or sets custom extension data added by the transformPropertyUpdate function.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, object>? ExtensionData { get; set; }
    
    public static SubjectPropertyUpdate Create<T>(T value, DateTimeOffset? timestamp = null)
    {
        return new SubjectPropertyUpdate
        {
            Type = typeof(T).Name,
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
        ISubjectUpdateProcessor[] processors,
        Dictionary<IInterceptorSubject, SubjectUpdate> knownSubjectUpdates,
        List<SubjectPropertyUpdateReference>? propertyUpdates)
    {
        var allAttributes = property.Attributes;
        
        var attributes = allAttributes.Length > 0 ? allAttributes
            .Where(p => p.HasGetter)
            .ToDictionary(
                p => p.AttributeMetadata.AttributeName,
                p => CreateCompleteUpdate(p, processors, knownSubjectUpdates, propertyUpdates)) : null;

        var propertyUpdate = new SubjectPropertyUpdate
        {
            Type = property.Type.Name,
            Attributes = attributes?.Count != 0 ? attributes : null
        };
        
        if (propertyUpdates is not null && attributes?.Count > 0)
        {
            foreach (var (attrName, attrUpdate) in attributes)
            {
                var attrProperty = property.Attributes.FirstOrDefault(a => a.AttributeMetadata.AttributeName == attrName);
                if (attrProperty is not null)
                {
                    propertyUpdates.Add(new SubjectPropertyUpdateReference(attrProperty, attrUpdate, propertyUpdate.Attributes!));
                }
            }
        }
        
        propertyUpdate.ApplyValue(property, property.Reference.TryGetWriteTimestamp(), property.GetValue(), 
            processors, knownSubjectUpdates, propertyUpdates);

        return propertyUpdate;
    }
    
    /// <summary>
    /// Adds a complete update of the given value to the property update.
    /// </summary>
    /// <param name="property">The property.</param>
    /// <param name="timestamp">The timestamp of the value change.</param>
    /// <param name="value">The value to apply.</param>
    /// <param name="processors">The update processors to filter and transform updates.</param>
    /// <param name="knownSubjectUpdates">The known subject updates.</param>
    /// <param name="propertyUpdates">Optional list to collect property updates for transformation.</param>
    internal void ApplyValue(RegisteredSubjectProperty property, DateTimeOffset? timestamp, object? value, 
        ISubjectUpdateProcessor[] processors,
        Dictionary<IInterceptorSubject, SubjectUpdate> knownSubjectUpdates,
        List<SubjectPropertyUpdateReference>? propertyUpdates = null)
    {
        Timestamp = timestamp;

        if (property.IsSubjectDictionary)
        {
            Kind = SubjectPropertyUpdateKind.Collection;
            Collection = value is IReadOnlyDictionary<string, IInterceptorSubject?> dictionary ? dictionary
                .Keys
                .Select(key =>
                {
                    var item = dictionary[key];
                    return new SubjectPropertyCollectionUpdate
                    {
                        Item = item is not null ? SubjectUpdate.CreateCompleteUpdate(item, processors, knownSubjectUpdates, propertyUpdates) : null,
                        Index = key
                    };
                })
                .ToList() : null;
        }
        else if (property.IsSubjectCollection)
        {
            Kind = SubjectPropertyUpdateKind.Collection;
            Collection = value is IEnumerable<IInterceptorSubject> collection ? collection
                .Select((itemSubject, index) => new SubjectPropertyCollectionUpdate
                {
                    Item = SubjectUpdate.CreateCompleteUpdate(itemSubject, processors, knownSubjectUpdates, propertyUpdates),
                    Index = index
                })
                .ToList() : null;
        }
        else if (property.IsSubjectReference)
        {
            Kind = SubjectPropertyUpdateKind.Item;
            Item = value is IInterceptorSubject itemSubject ? SubjectUpdate.CreateCompleteUpdate(itemSubject, processors, knownSubjectUpdates, propertyUpdates) : null;
        }
        else
        {
            Kind = SubjectPropertyUpdateKind.Value;
            Value = value;
        }
    }
}