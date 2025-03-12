using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Attributes;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.AspNetCore.Models;

public class SubjectDescription
{
    public required string Type { get; init; }

    public Dictionary<string, SubjectPropertyDescription> Properties { get; } = new();
    
    public static SubjectDescription Create(IInterceptorSubject subject, JsonSerializerOptions jsonSerializerOptions)
    {
        var description = new SubjectDescription
        {
            Type = subject.GetType().Name
        };

        var registry = subject.Context.GetService<ISubjectRegistry>();
        if (registry.KnownSubjects.TryGetValue(subject, out var registeredSubject))
        {
            foreach (var property in registeredSubject.Properties
                         .Where(p => p.Value.HasGetter &&
                                     p.Value.Attributes.OfType<PropertyAttributeAttribute>().Any() == false))
            {
                var propertyName = property.GetJsonPropertyName(jsonSerializerOptions);
                var value = property.Value.GetValue();

                description.Properties[propertyName] = SubjectPropertyDescription.Create(
                    registeredSubject, property.Key, property.Value, value, jsonSerializerOptions);
            }
        }

        return description;
    }
    
    public static IEnumerable<SubjectDescription> CreatePartialsFromChanges(
        IEnumerable<PropertyChangedContext> propertyChanges, JsonSerializerOptions jsonSerializerOptions)
    {
        var roots = new HashSet<IInterceptorSubject>();
        var knownSubjectDescriptions = new Dictionary<IInterceptorSubject, SubjectDescription>();

        foreach (var change in propertyChanges)
        {
            var parentSubjectDescription = GetOrCreateSubjectDescription(change.Property, knownSubjectDescriptions);

            var registry = change.Property.Subject.Context.GetService<ISubjectRegistry>();
            var registeredSubject = registry.KnownSubjects[change.Property.Subject];
            
            var propertyName = JsonNamingPolicy.CamelCase.ConvertName(change.Property.Name);
            parentSubjectDescription.Properties[propertyName] = SubjectPropertyDescription.Create(
                registeredSubject, change.Property.Name, 
                registeredSubject.Properties[change.Property.Name], 
                change.NewValue, jsonSerializerOptions);

            while (registeredSubject.Parents.Any())
            {
                var parentProperty = registeredSubject.Parents.First();
                (registeredSubject, parentSubjectDescription) = CreateParentSubjectDescription(
                    parentProperty, parentSubjectDescription, knownSubjectDescriptions);
            }

            roots.Add(registeredSubject.Subject);
        }

        return roots.Select(r => knownSubjectDescriptions[r]);
    }

    private static (RegisteredSubject, SubjectDescription) CreateParentSubjectDescription(
        PropertyReference parentProperty, 
        SubjectDescription childSubjectDescription, 
        Dictionary<IInterceptorSubject, SubjectDescription> knownSubjectDescriptions)
    {
        var parentSubjectDescription = GetOrCreateSubjectDescription(parentProperty, knownSubjectDescriptions);

        var registry = parentProperty.Subject.Context.GetService<ISubjectRegistry>();
        var registeredSubject = registry.KnownSubjects[parentProperty.Subject];
        parentSubjectDescription.Properties[parentProperty.Name] = new SubjectPropertyDescription
        {
            Type = parentProperty.Subject.GetType().Name,
            Subject = childSubjectDescription
        };
        
        return (registeredSubject, parentSubjectDescription);
    }

    private static SubjectDescription GetOrCreateSubjectDescription(
        PropertyReference property, Dictionary<IInterceptorSubject, SubjectDescription> knownSubjectDescriptions)
    {
        var parentSubjectDescription = knownSubjectDescriptions.TryGetValue(property.Subject, out var description) ?
            description :
            new SubjectDescription { Type = property.Subject.GetType().Name };
        
        knownSubjectDescriptions[property.Subject] = parentSubjectDescription;
        return parentSubjectDescription;
    }
}