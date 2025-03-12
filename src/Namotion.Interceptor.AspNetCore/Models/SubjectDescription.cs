using System.Collections.Generic;
using System.Linq;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Attributes;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.AspNetCore.Models;

public class SubjectDescription
{
    public required string Type { get; init; }

    public Dictionary<string, SubjectPropertyDescription> Properties { get; } = new();
    
    public static SubjectDescription Create(IInterceptorSubject subject, ISubjectRegistry registry)
    {
        var description = new SubjectDescription
        {
            Type = subject.GetType().Name
        };

        if (registry.KnownSubjects.TryGetValue(subject, out var registeredSubject))
        {
            foreach (var property in registeredSubject.Properties
                         .Where(p => p.Value.HasGetter &&
                                     p.Value.Attributes.OfType<PropertyAttributeAttribute>().Any() == false))
            {
                var propertyName = property.GetJsonPropertyName();
                var value = property.Value.GetValue();

                description.Properties[propertyName] = SubjectPropertyDescription.Create(registry, registeredSubject, property.Key, property.Value, value);
            }
        }

        return description;
    }
    
    // TODO(perf): Clean up and test!!!
    
    public static SubjectDescription CreatePartialFromChanges(IEnumerable<PropertyChangedContext> propertyChanges)
    {
        var knownSubjectDescriptions = new Dictionary<IInterceptorSubject, SubjectDescription>();
        IInterceptorSubject? root = null;

        foreach (var change in propertyChanges)
        {
            var registry = change.Property.Subject.Context.GetService<ISubjectRegistry>();

            var parent = knownSubjectDescriptions.ContainsKey(change.Property.Subject) ?
                knownSubjectDescriptions[change.Property.Subject] :
                new SubjectDescription { Type = change.Property.Subject.GetType().Name };

            knownSubjectDescriptions[change.Property.Subject] = parent;

            var registeredSubject = registry.KnownSubjects[change.Property.Subject];
            var propertyName = new KeyValuePair<string, RegisteredSubjectProperty>(change.Property.Name, registeredSubject.Properties[change.Property.Name]).GetJsonPropertyName();
            parent.Properties[propertyName] = SubjectPropertyDescription.Create(
                registry, registeredSubject, change.Property.Name, registeredSubject.Properties[change.Property.Name], change.NewValue);

            while (registeredSubject.Parents.Any())
            {
                var p = registeredSubject.Parents.First();
                registeredSubject = Foo(knownSubjectDescriptions, p, registry, registeredSubject.Subject);
            }

            root = registeredSubject.Subject;
        }

        return knownSubjectDescriptions[root!];
    }

    private static RegisteredSubject Foo(Dictionary<IInterceptorSubject, SubjectDescription> subjectDescriptions, PropertyReference property, ISubjectRegistry registry, IInterceptorSubject child)
    {
        var parent = subjectDescriptions.ContainsKey(property.Subject) ?
            subjectDescriptions[property.Subject] :
            new SubjectDescription { Type = property.Subject.GetType().Name };

        subjectDescriptions[property.Subject] = parent;

        var registeredSubject = registry.KnownSubjects[property.Subject];
        parent.Properties[property.Name] = SubjectPropertyDescription.Create(
            registry, registeredSubject, property.Name, registeredSubject.Properties[property.Name], child);

        return registeredSubject;
    }
}