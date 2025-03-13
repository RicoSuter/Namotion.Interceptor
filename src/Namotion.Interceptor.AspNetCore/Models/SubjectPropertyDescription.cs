using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Interceptor.AspNetCore.Models;

public class SubjectPropertyDescription
{
    public required string Type { get; init; }

    public object? Value { get; internal set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public IReadOnlyDictionary<string, SubjectPropertyDescription>? Attributes { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public SubjectDescription? Subject { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public List<SubjectPropertyChildDescription>? Children { get; set; }
    
    public static SubjectPropertyDescription Create(RegisteredSubject parent, 
        string propertyName, RegisteredSubjectProperty property, object? value, JsonSerializerOptions jsonSerializerOptions)
    {
        var attributes = parent.Properties
            .Where(p => p.Value.HasGetter &&
                        p.Value.Attributes.OfType<PropertyAttributeAttribute>().Any(a => a.PropertyName == propertyName))
            .ToDictionary(
                p => p.Value.Attributes.OfType<PropertyAttributeAttribute>().Single().AttributeName,
                p => Create(parent, p.Key, p.Value, p.Value.GetValue(), jsonSerializerOptions));

        var description = new SubjectPropertyDescription
        {
            Type = property.Type.Name,
            Attributes = attributes.Any() ? attributes : null
        };

        var children = property.Children;
        if (children.Any(c => c.Index is not null))
        {
            description.Children = children
                .Select(s => new SubjectPropertyChildDescription
                {
                    Subject = SubjectDescription.Create(s.Subject, jsonSerializerOptions),
                    Index = s.Index
                })
                .ToList();
        }
        else if (value is IInterceptorSubject childSubject)
        {
            description.Subject = SubjectDescription.Create(childSubject, jsonSerializerOptions);
        }
        else
        {
            description.Value = value;
        }

        return description;
    }
}