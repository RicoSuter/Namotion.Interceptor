using System.Collections;
using System.Collections.Generic;
using System.Linq;
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
    public List<SubjectDescription>? Subjects { get; set; }
    
    public static SubjectPropertyDescription Create(ISubjectRegistry registry, RegisteredSubject parent, 
        string propertyName, RegisteredSubjectProperty property, object? value)
    {
        var attributes = parent.Properties
            .Where(p => p.Value.HasGetter &&
                        p.Value.Attributes.OfType<PropertyAttributeAttribute>().Any(a => a.PropertyName == propertyName))
            .ToDictionary(
                p => p.Value.Attributes.OfType<PropertyAttributeAttribute>().Single().AttributeName,
                p => Create(registry, parent, p.Key, p.Value, p.Value.GetValue()));

        var description = new SubjectPropertyDescription
        {
            Type = property.Type.Name,
            Attributes = attributes.Any() ? attributes : null
        };

        if (value is IInterceptorSubject childSubject)
        {
            description.Subject = SubjectDescription.Create(childSubject, registry);
        }
        else if (value is ICollection collection && collection.OfType<IInterceptorSubject>().Any())
        {
            description.Subjects = collection
                .OfType<IInterceptorSubject>()
                .Select(arrayProxyItem => SubjectDescription.Create(arrayProxyItem, registry))
                .ToList();
        }
        else
        {
            description.Value = value;
        }

        return description;
    }
}