using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Attributes;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Sources.Updates;

public record SubjectUpdate
{
    /// <summary>
    /// Gets the type of the subject.
    /// </summary>
    public string? Type { get; init; }

    /// <summary>
    /// Gets a dictionary of property updates.
    /// The dictionary is mutable so that additional updates can be attached.
    /// </summary>
    public IDictionary<string, SubjectPropertyUpdate> Properties { get; init; }
        = new Dictionary<string, SubjectPropertyUpdate>();

    /// <summary>
    /// Creates a complete update with all objects and properties for the given subject as root.
    /// </summary>
    /// <param name="subject">The root subject.</param>
    /// <param name="propertyFilter">The property filter to exclude certain properties in the update.</param>
    /// <param name="getPropertyValue">The get property value which can be used to transform or read value not directly from the property.</param>
    /// <returns>The update.</returns>
    public static SubjectUpdate CreateCompleteUpdate(IInterceptorSubject subject, 
        Func<RegisteredSubjectProperty, bool>? propertyFilter = null, 
        Func<RegisteredSubjectProperty, object?>? getPropertyValue = null)
    {
        return CreateCompleteUpdate(subject, propertyFilter, getPropertyValue, new Dictionary<IInterceptorSubject, SubjectUpdate>());
    }
    
    /// <summary>
    /// Creates a complete update with all objects and properties for the given subject as root.
    /// </summary>
    /// <param name="subject">The root subject.</param>
    /// <param name="propertyFilter">The property filter to exclude certain properties in the update.</param>
    /// <param name="getPropertyValue">The get property value which can be used to transform or read value not directly from the property.</param>
    /// <param name="knownSubjectUpdates">The known subject updates.</param>
    /// <returns>The update.</returns>
    internal static SubjectUpdate CreateCompleteUpdate(IInterceptorSubject subject,
        Func<RegisteredSubjectProperty, bool>? propertyFilter,
        Func<RegisteredSubjectProperty, object?>? getPropertyValue, 
        Dictionary<IInterceptorSubject, SubjectUpdate> knownSubjectUpdates)
    {
        var subjectUpdate = GetOrCreateSubjectUpdate(subject, knownSubjectUpdates);

        var registeredSubject = subject.TryGetRegisteredSubject();
        if (registeredSubject is not null)
        {
            foreach (var property in registeredSubject.Properties
                .Where(p => p.Value is { HasGetter: true, IsAttribute: false } && propertyFilter?.Invoke(p.Value) != false))
            {
                subjectUpdate.Properties[property.Key] = SubjectPropertyUpdate.CreateCompleteUpdate(
                    registeredSubject, property.Key, property.Value, propertyFilter, getPropertyValue, knownSubjectUpdates);
            }
        }

        return subjectUpdate;
    }

    /// <summary>
    /// Creates a partial update from the given property changes.
    /// Only directly or indirectly needed objects and properties are added.
    /// </summary>
    /// <param name="subject">The root subject.</param>
    /// <param name="propertyChanges">The changes to look up within the object graph.</param>
    /// <param name="propertyFilter">The property filter to exclude certain properties in the update.</param>
    /// <param name="getPropertyValue">The get property value which can be used to transform or read value not directly from the property.</param>
    /// <returns>The update.</returns>
    public static SubjectUpdate CreatePartialUpdateFromChanges(
        IInterceptorSubject subject, IEnumerable<SubjectPropertyChange> propertyChanges,
        Func<RegisteredSubjectProperty, bool>? propertyFilter = null,
        Func<RegisteredSubjectProperty, object?>? getPropertyValue = null)
    {
        var knownSubjectUpdates = new Dictionary<IInterceptorSubject, SubjectUpdate>();
        var update = GetOrCreateSubjectUpdate(subject, knownSubjectUpdates);

        foreach (var change in propertyChanges)
        {
            var property = change.Property;
            var registeredSubject = property.Subject.TryGetRegisteredSubject()
                ?? throw new InvalidOperationException("Registered subject not found.");

            var propertySubject = property.Subject;
            var subjectUpdate = GetOrCreateSubjectUpdate(propertySubject, knownSubjectUpdates);

            var registeredProperty = property.GetRegisteredProperty();
            if (registeredProperty.IsAttribute)
            {
                // handle attribute changes
                var attributeUpdate = new SubjectPropertyUpdate();
                attributeUpdate.ApplyValue(registeredProperty, change.Timestamp, change.NewValue, propertyFilter, getPropertyValue, knownSubjectUpdates);

                PropertyAttributeAttribute attribute;
                var currentRegisteredProperty = registeredProperty;
                do
                {
                    attribute = currentRegisteredProperty.AttributeMetadata;

                    var childAttributeUpdate = attributeUpdate;
                    attributeUpdate = GetOrCreateSubjectPropertyUpdate(propertySubject, attribute.PropertyName, knownSubjectUpdates);
                    attributeUpdate.Attributes ??= new Dictionary<string, SubjectPropertyUpdate>();
                    attributeUpdate.Attributes[attribute.AttributeName] = childAttributeUpdate;

                    currentRegisteredProperty = registeredSubject.Properties[attribute.PropertyName];
                } while (currentRegisteredProperty.IsAttribute);

                var propertyUpdate = GetOrCreateSubjectPropertyUpdate(propertySubject, attribute.PropertyName, knownSubjectUpdates);
                subjectUpdate.Properties[attribute.PropertyName] = propertyUpdate;
            }
            else
            {
                // handle property changes
                var propertyName = property.Name;

                var propertyUpdate = GetOrCreateSubjectPropertyUpdate(propertySubject, propertyName, knownSubjectUpdates);
                propertyUpdate.ApplyValue(registeredProperty, change.Timestamp, change.NewValue, propertyFilter, getPropertyValue, knownSubjectUpdates);

                subjectUpdate.Properties[propertyName] = propertyUpdate;
            }

            CreateParentSubjectUpdatePath(registeredSubject, knownSubjectUpdates);
        }

        return update;
    }

    private static void CreateParentSubjectUpdatePath(
        RegisteredSubject registeredSubject,
        Dictionary<IInterceptorSubject, SubjectUpdate> knownSubjectUpdates)
    {
        var parentProperty = registeredSubject.Parents.FirstOrDefault().Property?.Property ?? null;
        if (parentProperty?.Subject is { } parentPropertySubject)
        {
            var parentSubjectPropertyUpdate = GetOrCreateSubjectPropertyUpdate(parentPropertySubject, parentProperty.Value.Name, knownSubjectUpdates);

            var parentRegisteredSubject = parentPropertySubject.TryGetRegisteredSubject()
                ?? throw new InvalidOperationException("Registered subject not found.");

            var children = parentRegisteredSubject.Properties[parentProperty.Value.Name].Children;
            if (children.Any(c => c.Index is not null))
            {
                parentSubjectPropertyUpdate.Kind = SubjectPropertyUpdateKind.Collection;
                parentSubjectPropertyUpdate.Collection = children
                    .Select(s => new SubjectPropertyCollectionUpdate
                    {
                        Item = GetOrCreateSubjectUpdate(s.Subject, knownSubjectUpdates),
                        Index = s.Index ?? throw new InvalidOperationException("Index must not be null.")
                    })
                    .ToList();
            }
            else
            {
                parentSubjectPropertyUpdate.Kind = SubjectPropertyUpdateKind.Item;
                parentSubjectPropertyUpdate.Item = GetOrCreateSubjectUpdate(registeredSubject.Subject, knownSubjectUpdates);
            }

            if (parentPropertySubject.TryGetRegisteredSubject() is { } parentPropertyRegisteredSubject)
            {
                CreateParentSubjectUpdatePath(parentPropertyRegisteredSubject, knownSubjectUpdates);
            }
        }
    }

    private static SubjectPropertyUpdate GetOrCreateSubjectPropertyUpdate(
        IInterceptorSubject subject, string propertyName,
        Dictionary<IInterceptorSubject, SubjectUpdate> knownSubjectUpdates)
    {
        var subjectUpdate = GetOrCreateSubjectUpdate(subject, knownSubjectUpdates);
        if (subjectUpdate.Properties.TryGetValue(propertyName, out var existingSubjectUpdate))
        {
            return existingSubjectUpdate;
        }

        var propertyUpdate = new SubjectPropertyUpdate();
        subjectUpdate.Properties[propertyName] = propertyUpdate;
        return propertyUpdate;
    }

    private static SubjectUpdate GetOrCreateSubjectUpdate(
        IInterceptorSubject subject,
        Dictionary<IInterceptorSubject, SubjectUpdate> knownSubjectUpdates)
    {
        var subjectUpdate = knownSubjectUpdates.TryGetValue(subject, out var knownSubjectUpdate)
            ? knownSubjectUpdate
            : new SubjectUpdate
            {
                Type = subject.GetType().Name
            };

        knownSubjectUpdates[subject] = subjectUpdate;
        return subjectUpdate;
    }
}