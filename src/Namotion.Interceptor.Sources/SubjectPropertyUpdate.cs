using System.Text.Json.Serialization;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Sources.Extensions;

namespace Namotion.Interceptor.Sources;

public enum SubjectPropertyUpdateAction
{
    None,
    UpdateValue,
    UpdateItem,
    UpdateCollection
}

public class SubjectPropertyUpdate
{
    public string? Type { get; init; }

    public object? Value { get; internal set; }
    
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SubjectPropertyUpdateAction Action { get; internal set; }
    
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Dictionary<string, SubjectPropertyUpdate>? Attributes { get; internal set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public SubjectUpdate? Item { get; internal set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public List<SubjectPropertyCollectionUpdate>? Collection { get; internal set; }
    
    public static SubjectPropertyUpdate Create(RegisteredSubject subject, string propertyName, RegisteredSubjectProperty property, object? value)
    {
        var attributes = subject.Properties
            .Where(p => 
                p.Value.HasGetter && p.Value.HasPropertyAttributes(propertyName))
            .ToDictionary(
                p => p.Value.Attribute.AttributeName,
                p => Create(subject, p.Key, p.Value, p.Value.GetValue()));

        var propertyUpdate = new SubjectPropertyUpdate
        {
            Type = property.Type.Name,
            Attributes = attributes.Count != 0 ? attributes : null
        };

        propertyUpdate.ApplyRegisteredPropertyValue(property, value);
        return propertyUpdate;
    }
}