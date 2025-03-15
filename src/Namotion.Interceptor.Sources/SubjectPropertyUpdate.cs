using System.Text.Json.Serialization;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Interceptor.Sources;

public class SubjectPropertyUpdate
{
    public string? Type { get; init; }

    public object? Value { get; internal set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public IReadOnlyDictionary<string, SubjectPropertyUpdate>? Attributes { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public SubjectUpdate? Item { get; internal set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public List<SubjectPropertyCollectionUpdate>? Items { get; internal set; }
    
    public static SubjectPropertyUpdate Create(RegisteredSubject parent, string propertyName, RegisteredSubjectProperty property, object? value)
    {
        var attributes = parent.Properties
            .Where(p => p.Value.HasGetter &&
                        p.Value.Attributes.OfType<PropertyAttributeAttribute>().Any(a => a.PropertyName == propertyName))
            .ToDictionary(
                p => p.Value.Attributes.OfType<PropertyAttributeAttribute>().Single().AttributeName,
                p => Create(parent, p.Key, p.Value, p.Value.GetValue()));

        var description = new SubjectPropertyUpdate
        {
            Type = property.Type.Name,
            Attributes = attributes.Count != 0 ? attributes : null
        };

        var children = property.Children;
        if (children.Any(c => c.Index is not null))
        {
            description.Items = children
                .Select(s => new SubjectPropertyCollectionUpdate
                {
                    Item = SubjectUpdate.CreateCompleteUpdate(s.Subject),
                    Index = s.Index
                })
                .ToList();
        }
        else if (value is IInterceptorSubject childSubject)
        {
            description.Item = SubjectUpdate.CreateCompleteUpdate(childSubject);
        }
        else
        {
            description.Value = value;
        }

        return description;
    }
}