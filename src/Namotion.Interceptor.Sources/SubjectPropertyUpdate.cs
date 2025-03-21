using System.Collections;
using System.Text.Json.Serialization;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Sources;

public class SubjectPropertyUpdate
{
    public string? Type { get; init; }
    
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public IDictionary<string, SubjectPropertyUpdate>? Attributes { get; internal set; }

    public object? Value { get; internal set; }
    
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SubjectPropertyUpdateKind Kind { get; internal set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public SubjectUpdate? Item { get; internal set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public List<SubjectPropertyCollectionUpdate>? Collection { get; internal set; }

    public static SubjectPropertyUpdate Create<T>(T value)
    {
        return new SubjectPropertyUpdate
        {
            Type = typeof(T).Name,
            Kind = SubjectPropertyUpdateKind.Value,
            Value = value
        };
    }
    
    public static SubjectPropertyUpdate Create(SubjectUpdate itemUpdate)
    {
        return new SubjectPropertyUpdate
        {
            Kind = SubjectPropertyUpdateKind.Item,
            Item = itemUpdate
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
    
    public static SubjectPropertyUpdate Create(RegisteredSubject subject, string propertyName, RegisteredSubjectProperty property)
    {
        var attributes = subject.Properties
            .Where(p => 
                p.Value.HasGetter && p.Value.HasPropertyAttributes(propertyName))
            .ToDictionary(
                p => p.Value.Attribute.AttributeName,
                p => Create(subject, p.Key, p.Value));

        var propertyUpdate = new SubjectPropertyUpdate
        {
            Type = property.Type.Name,
            Attributes = attributes.Count != 0 ? attributes : null
        };
        
        propertyUpdate.ApplyValue(property.GetValue());
        return propertyUpdate;
    }
    
    /// <summary>
    /// Adds a complete update of the given value to the property update.
    /// </summary>
    /// <param name="value">The value to apply.</param>
    internal void ApplyValue(object? value)
    {
        if (value is IDictionary dictionary && dictionary.Values.OfType<IInterceptorSubject>().Any())
        {
            // TODO: Fix dictionary handling logic (how to detect dict?)
            
            Kind = SubjectPropertyUpdateKind.Collection;
            Collection = dictionary.Keys
                .OfType<object>()
                .Select((key) => new SubjectPropertyCollectionUpdate
                {
                    Item = SubjectUpdate.CreateCompleteUpdate((IInterceptorSubject)dictionary[key]!),
                    Index = key
                })
                .ToList();
        }
        else if (value is IEnumerable<IInterceptorSubject> collection)
        {
            Kind = SubjectPropertyUpdateKind.Collection;
            Collection = collection
                .Select((itemSubject, index) => new SubjectPropertyCollectionUpdate
                {
                    Item = SubjectUpdate.CreateCompleteUpdate(itemSubject),
                    Index = index
                })
                .ToList();
        }
        else if (value is IInterceptorSubject itemSubject)
        {
            Kind = SubjectPropertyUpdateKind.Item;
            Item = SubjectUpdate.CreateCompleteUpdate(itemSubject);
        }
        else
        {
            Kind = SubjectPropertyUpdateKind.Value;
            Value = value;
        }
    }
}