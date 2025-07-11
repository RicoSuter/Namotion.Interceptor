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
    public IDictionary<string, SubjectPropertyUpdate>? Attributes { get; internal set; }

    /// <summary>
    /// Gets the type of the property value.
    /// </summary>
    public string? Type { get; init; }

    /// <summary>
    /// Gets the value of the property update.
    /// </summary>
    public object? Value { get; private set; }
    
    /// <summary>
    /// Gets the timestamp of the property update or null if unknown.
    /// </summary>
    public DateTimeOffset? Timestamp { get; private set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public SubjectUpdate? Item { get; internal set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public List<SubjectPropertyCollectionUpdate>? Collection { get; internal set; }
    
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
    
    public static SubjectPropertyUpdate Create(params IEnumerable<SubjectPropertyCollectionUpdate> collectionUpdates)
    {
        return new SubjectPropertyUpdate
        {
            Kind = SubjectPropertyUpdateKind.Collection,
            Collection = collectionUpdates.ToList()
        };
    }
    
    public static SubjectPropertyUpdate CreateCompleteUpdate(RegisteredSubject subject, string propertyName, RegisteredSubjectProperty property, Dictionary<IInterceptorSubject, SubjectUpdate> knownSubjectUpdates)
    {
        var attributes = subject.Properties
            .Where(p => 
                p.Value.HasGetter && p.Value.IsAttributeForProperty(propertyName))
            .ToDictionary(
                p => p.Value.AttributeMetadata.AttributeName,
                p => CreateCompleteUpdate(subject, p.Key, p.Value, knownSubjectUpdates));

        var propertyUpdate = new SubjectPropertyUpdate
        {
            Type = property.Type.Name,
            Attributes = attributes.Count != 0 ? attributes : null
        };
        
        propertyUpdate.ApplyValue(property, property.Property.TryGetWriteTimestamp(), property.GetValue(), knownSubjectUpdates);
        return propertyUpdate;
    }

    /// <summary>
    /// Adds a complete update of the given value to the property update.
    /// </summary>
    /// <param name="property">The property.</param>
    /// <param name="timestamp">The timestamp of the value change.</param>
    /// <param name="value">The value to apply.</param>
    /// <param name="knownSubjectUpdates">The known subject updates.</param>
    internal void ApplyValue(RegisteredSubjectProperty property, DateTimeOffset? timestamp, object? value, Dictionary<IInterceptorSubject, SubjectUpdate> knownSubjectUpdates)
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
                        Item = item is not null ? SubjectUpdate.CreateCompleteUpdate(item, knownSubjectUpdates) : null,
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
                    Item = SubjectUpdate.CreateCompleteUpdate(itemSubject, knownSubjectUpdates),
                    Index = index
                })
                .ToList() : null;
        }
        else if (property.IsSubjectReference)
        {
            Kind = SubjectPropertyUpdateKind.Item;
            Item = value is IInterceptorSubject itemSubject ? SubjectUpdate.CreateCompleteUpdate(itemSubject, knownSubjectUpdates) : null;
        }
        else
        {
            Kind = SubjectPropertyUpdateKind.Value;
            Value = value;
        }
    }
}