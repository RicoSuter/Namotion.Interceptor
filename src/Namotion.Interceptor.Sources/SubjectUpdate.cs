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

    public static SubjectUpdate CreatePartialUpdateFromChange(PropertyChangedContext propertyChange)
    {
        return CreatePartialUpdateFromChanges([propertyChange]).Single();
    }

    public static IEnumerable<SubjectUpdate> CreatePartialUpdateFromChanges(
        IEnumerable<PropertyChangedContext> propertyChanges)
    {
        var roots = new HashSet<IInterceptorSubject>();
        var knownSubjectDescriptions = new Dictionary<IInterceptorSubject, SubjectUpdate>();

        foreach (var change in propertyChanges)
        {
            var subjectDescription = GetOrCreateSubjectDescription(change.Property.Subject, knownSubjectDescriptions);

            var registry = change.Property.Subject.Context.GetService<ISubjectRegistry>();
            var registeredSubject = registry.KnownSubjects[change.Property.Subject];

            var propertyName = change.Property.Name;
            subjectDescription.Properties[propertyName] = SubjectPropertyUpdate.Create(
                registeredSubject, propertyName,
                registeredSubject.Properties[propertyName],
                change.NewValue);

            var childSubject = change.Property.Subject;
            while (registeredSubject.Parents.Any())
            {
                var parentProperty = registeredSubject.Parents.First();

                registeredSubject = registry.KnownSubjects[parentProperty.Subject];
                childSubject = CreateParentSubjectDescription(
                    parentProperty, childSubject, knownSubjectDescriptions);
            }

            roots.Add(registeredSubject.Subject);
        }

        return roots.Select(r => knownSubjectDescriptions[r]);
    }

    private static IInterceptorSubject CreateParentSubjectDescription(
        PropertyReference parentProperty,
        IInterceptorSubject childSubject,
        Dictionary<IInterceptorSubject, SubjectUpdate> knownSubjectDescriptions)
    {
        var parentSubjectDescription = GetOrCreateSubjectDescription(parentProperty.Subject, knownSubjectDescriptions);
        var property = GetOrCreateProperty(parentSubjectDescription, parentProperty.Name, parentProperty.Metadata.Type.Name);

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

        return parentProperty.Subject;
    }

    private static SubjectPropertyUpdate GetOrCreateProperty(SubjectUpdate parentSubjectUpdate, string propertyName, string propertyType)
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