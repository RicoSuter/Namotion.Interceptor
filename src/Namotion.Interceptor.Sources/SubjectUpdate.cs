using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Attributes;
using Namotion.Interceptor.Sources.Extensions;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Sources;

public class SubjectUpdate
{
    public string? Type { get; init; }

    // TODO: Convert to read only
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
                .Where(p => p.Value is { HasGetter: true, IsAttribute: false }))
            {
                subjectUpdate.Properties[property.Key] = SubjectPropertyUpdate.Create(registeredSubject, property.Key, property.Value);
            }
        }

        return subjectUpdate;
    }

    public static SubjectUpdate CreatePartialUpdateFromChanges(IInterceptorSubject subject, IEnumerable<PropertyChangedContext> propertyChanges)
    {
        // TODO: Verify correctness of the CreatePartialUpdateFromChanges method

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
                var subjectUpdate = GetOrCreateSubjectUpdate(propertySubject, knownSubjectDescriptions);

                var registeredProperty = property.GetRegisteredProperty();
                if (registeredProperty.IsAttribute)
                {
                    // handle attribute changes
                    var attributeUpdate = new SubjectPropertyUpdate();
                    attributeUpdate.ApplyPropertyValue(change.NewValue);
                    
                    PropertyAttributeAttribute attribute;
                    var currentRegisteredProperty = registeredProperty;
                    do
                    {
                        attribute = currentRegisteredProperty.Attribute;

                        var childAttributeUpdate = attributeUpdate;
                        attributeUpdate = GetOrCreateSubjectPropertyUpdate(registeredSubject, attribute.PropertyName, knownSubjectDescriptions);
                        attributeUpdate.Attributes ??= new Dictionary<string, SubjectPropertyUpdate>();
                        attributeUpdate.Attributes[attribute.AttributeName] = childAttributeUpdate;
                    
                        currentRegisteredProperty = registeredSubject.Properties[attribute.PropertyName];
                    } while (currentRegisteredProperty.IsAttribute);
                    
                    var propertyUpdate = GetOrCreateSubjectPropertyUpdate(registeredSubject, attribute.PropertyName, knownSubjectDescriptions);
                    subjectUpdate.Properties[attribute.PropertyName] = propertyUpdate;
                }
                else
                {
                    // handle property changes
                    var propertyName = property.Name;
                 
                    var propertyUpdate = GetOrCreateSubjectPropertyUpdate(registeredSubject, propertyName, knownSubjectDescriptions);
                    propertyUpdate.ApplyPropertyValue(change.NewValue);
                 
                    subjectUpdate.Properties[propertyName] = propertyUpdate;
                }

                property = registeredSubject.Parents.FirstOrDefault();
                if (property.Subject is not null)
                {
                    registry = property.Subject.Context.GetService<ISubjectRegistry>();
                    registeredSubject = registry.KnownSubjects[property.Subject];

                    CreateParentSubjectDescription(property, propertySubject, knownSubjectDescriptions);
                }
            } while (property.Subject is not null && property.Subject != subject && registeredSubject.Parents.Any());
        }

        return update;
    }

    private static void CreateParentSubjectDescription(
        PropertyReference parentProperty,
        IInterceptorSubject childSubject,
        Dictionary<IInterceptorSubject, SubjectUpdate> knownSubjectDescriptions)
    {
        var parentSubjectDescription = GetOrCreateSubjectUpdate(parentProperty.Subject, knownSubjectDescriptions);
        var property = GetOrCreateProperty(parentSubjectDescription, parentProperty.Name);

        var registry = parentProperty.Subject.Context.GetService<ISubjectRegistry>();
        var parentRegisteredSubject = registry.KnownSubjects[parentProperty.Subject];

        var children = parentRegisteredSubject.Properties[parentProperty.Name].Children;
        if (children.Any(c => c.Index is not null))
        {
            property.Action = SubjectPropertyUpdateAction.UpdateCollection;
            property.Collection = children
                .Select(s => new SubjectPropertyCollectionUpdate
                {
                    Item = GetOrCreateSubjectUpdate(s.Subject, knownSubjectDescriptions),
                    Index = s.Index ?? throw new InvalidOperationException("Index must not be null.")
                })
                .ToList();
        }
        else
        {
            property.Action = SubjectPropertyUpdateAction.UpdateItem;
            property.Item = GetOrCreateSubjectUpdate(childSubject, knownSubjectDescriptions);
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

    private static SubjectUpdate GetOrCreateSubjectUpdate(
        IInterceptorSubject subject, 
        Dictionary<IInterceptorSubject, SubjectUpdate> knownSubjectDescriptions)
    {
        var parentSubjectDescription = knownSubjectDescriptions.TryGetValue(subject, out var description) ? description : new SubjectUpdate();

        knownSubjectDescriptions[subject] = parentSubjectDescription;
        return parentSubjectDescription;
    }

    private static SubjectPropertyUpdate GetOrCreateSubjectPropertyUpdate(
        RegisteredSubject registeredSubject, string propertyName,
        Dictionary<IInterceptorSubject, SubjectUpdate> knownSubjectDescriptions)
    {
        var subjectUpdate = GetOrCreateSubjectUpdate(registeredSubject.Subject, knownSubjectDescriptions);
        if (subjectUpdate.Properties.TryGetValue(propertyName, out var existingSubjectUpdate))
        {
            return existingSubjectUpdate;
        }
        
        var propertyUpdate = new SubjectPropertyUpdate();
        subjectUpdate.Properties[propertyName] = propertyUpdate;
        return propertyUpdate;
    }
}