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
            var subjectDescription = GetOrCreateSubjectDescription(change.Property.Subject, knownSubjectDescriptions);

            var registry = change.Property.Subject.Context.GetService<ISubjectRegistry>();
            var registeredSubject = registry.KnownSubjects[change.Property.Subject];
            
            var propertyName = jsonSerializerOptions.PropertyNamingPolicy?.ConvertName(change.Property.Name) ?? change.Property.Name;
            subjectDescription.Properties[propertyName] = SubjectPropertyDescription.Create(
                registeredSubject, change.Property.Name, 
                registeredSubject.Properties[change.Property.Name], 
                change.NewValue, jsonSerializerOptions);

            var childSubject = change.Property.Subject;
            while (registeredSubject.Parents.Any())
            {
                var parentProperty = registeredSubject.Parents.First();

                registeredSubject = registry.KnownSubjects[parentProperty.Subject];
                childSubject = CreateParentSubjectDescription(
                    parentProperty, childSubject, knownSubjectDescriptions, jsonSerializerOptions);
            }

            roots.Add(registeredSubject.Subject);
        }

        return roots.Select(r => knownSubjectDescriptions[r]);
    }

    private static IInterceptorSubject CreateParentSubjectDescription(
        PropertyReference parentProperty, 
        IInterceptorSubject childSubject, 
        Dictionary<IInterceptorSubject, SubjectDescription> knownSubjectDescriptions, 
        JsonSerializerOptions jsonSerializerOptions)
    {
        var propertyName = jsonSerializerOptions.PropertyNamingPolicy?.ConvertName(parentProperty.Name) ?? parentProperty.Name;

        var parentSubjectDescription = GetOrCreateSubjectDescription(parentProperty.Subject, knownSubjectDescriptions);
        var property = GetOrCreateProperty(parentSubjectDescription, propertyName, parentProperty.Metadata.Type.Name);

        var registry = parentProperty.Subject.Context.GetService<ISubjectRegistry>();
        var parentRegisteredSubject = registry.KnownSubjects[parentProperty.Subject];
       
        var children = parentRegisteredSubject.Properties[parentProperty.Name].Children;
        if (children.Any(c => c.Index is not null))
        {
            property.Subjects = children
                .Select(s => GetOrCreateSubjectDescription(s.Subject, knownSubjectDescriptions))
                .ToList();
        }
        else
        {
            property.Subject = GetOrCreateSubjectDescription(childSubject, knownSubjectDescriptions);
        }
        
        return parentProperty.Subject;
    }

    private static SubjectPropertyDescription GetOrCreateProperty(SubjectDescription parentSubjectDescription, string propertyName, string propertyType)
    {
        if (!parentSubjectDescription.Properties.TryGetValue(propertyName, out var property))
        {
            property = new SubjectPropertyDescription { Type = propertyType };
            parentSubjectDescription.Properties[propertyName] = property;
        }
        
        return property;
    }

    private static SubjectDescription GetOrCreateSubjectDescription(
        IInterceptorSubject subject, Dictionary<IInterceptorSubject, SubjectDescription> knownSubjectDescriptions)
    {
        var parentSubjectDescription = knownSubjectDescriptions.TryGetValue(subject, out var description) ?
            description :
            new SubjectDescription { Type = subject.GetType().Name };
        
        knownSubjectDescriptions[subject] = parentSubjectDescription;
        return parentSubjectDescription;
    }
}