using System.Text.Json.Serialization;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Sources.Extensions;

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
        
        propertyUpdate.ApplyPropertyValue(property.GetValue());
        return propertyUpdate;
    }
}