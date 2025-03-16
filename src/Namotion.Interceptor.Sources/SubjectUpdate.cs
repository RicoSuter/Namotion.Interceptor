using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Attributes;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Sources;

public class SubjectUpdate
{
    public string? Type { get; init; }

    public Dictionary<string, SubjectPropertyUpdate> Properties { get; init; } = new();

    public static SubjectUpdate CreateCompleteUpdate(IInterceptorSubject subject)
    {
        var subjectUpdate = new SubjectUpdate
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
                var value = property.Value.GetValue();
                subjectUpdate.Properties[property.Key] = SubjectPropertyUpdate.Create(
                    registeredSubject, property.Key, property.Value, value);
            }
        }

        return subjectUpdate;
    }

    public static SubjectUpdate CreatePartialUpdateFromChanges(IInterceptorSubject subject, IEnumerable<PropertyChangedContext> propertyChanges)
    {
        var update = new SubjectUpdate();
        var knownSubjectDescriptions = new Dictionary<IInterceptorSubject, SubjectUpdate>
        {
            [subject] = update
        };

        foreach (var change in propertyChanges)
        {
            var property = change.Property;
            var registry = property.Subject.Context.GetService<ISubjectRegistry>();
            var registeredSubject = registry.KnownSubjects[property.Subject];

            do
            {           
                var propertySubject = property.Subject;
                var subjectDescription = GetOrCreateSubjectDescription(propertySubject, knownSubjectDescriptions);

                var propertyName = property.Name;
                subjectDescription.Properties[propertyName] = SubjectPropertyUpdate.Create(
                    registeredSubject, propertyName,
                    registeredSubject.Properties[propertyName],
                    change.NewValue);
                
                property = registeredSubject.Parents.FirstOrDefault();
                if (property.Subject is not null)
                {
                    registry = property.Subject.Context.GetService<ISubjectRegistry>();
                    registeredSubject = registry.KnownSubjects[property.Subject];

                    CreateParentSubjectDescription(property, propertySubject, knownSubjectDescriptions);
                }
            } 
            while (property.Subject is not null && property.Subject != subject && registeredSubject.Parents.Any());
        }

        return update;
    }

    private static void CreateParentSubjectDescription(
        PropertyReference parentProperty,
        IInterceptorSubject childSubject,
        Dictionary<IInterceptorSubject, SubjectUpdate> knownSubjectDescriptions)
    {
        var parentSubjectDescription = GetOrCreateSubjectDescription(parentProperty.Subject, knownSubjectDescriptions);
        var property = GetOrCreateProperty(parentSubjectDescription, parentProperty.Name);

        var registry = parentProperty.Subject.Context.GetService<ISubjectRegistry>();
        var parentRegisteredSubject = registry.KnownSubjects[parentProperty.Subject];

        var children = parentRegisteredSubject.Properties[parentProperty.Name].Children;
        if (children.Any(c => c.Index is not null))
        {
            property.Items = children
                .Select(s => new SubjectPropertyCollectionUpdate
                {
                    Item = GetOrCreateSubjectDescription(s.Subject, knownSubjectDescriptions),
                    Index = s.Index
                })
                .ToList();
        }
        else
        {
            property.Item = GetOrCreateSubjectDescription(childSubject, knownSubjectDescriptions);
        }
    }

    private static SubjectPropertyUpdate GetOrCreateProperty(SubjectUpdate parentSubjectUpdate, string propertyName)
    {
        if (!parentSubjectUpdate.Properties.TryGetValue(propertyName, out var property))
        {
            property = new SubjectPropertyUpdate();
            parentSubjectUpdate.Properties[propertyName] = property;
        }

        return property;
    }

    private static SubjectUpdate GetOrCreateSubjectDescription(
        IInterceptorSubject subject, Dictionary<IInterceptorSubject, SubjectUpdate> knownSubjectDescriptions)
    {
        var parentSubjectDescription = knownSubjectDescriptions.TryGetValue(subject, out var description) ? description : new SubjectUpdate();

        knownSubjectDescriptions[subject] = parentSubjectDescription;
        return parentSubjectDescription;
    }
}