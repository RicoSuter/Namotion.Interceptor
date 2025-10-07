using System.Text.Json.Serialization;
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
    /// Gets or sets custom extension data added by the transformPropertyUpdate function.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, object>? ExtensionData { get; set; }

    /// <summary>
    /// Creates a complete update with all objects and properties for the given subject as root.
    /// </summary>
    /// <param name="subject">The root subject.</param>
    /// <param name="propertyFilter">The predicate to exclude properties from the update.</param>
    /// <param name="transformPropertyUpdate">The function to transform or exclude (return null) subject property updates..</param>
    /// <returns>The update.</returns>
    public static SubjectUpdate CreateCompleteUpdate(IInterceptorSubject subject, 
        Func<RegisteredSubjectProperty, bool>? propertyFilter = null,
        Func<RegisteredSubjectProperty, SubjectPropertyUpdate, SubjectPropertyUpdate>? transformPropertyUpdate = null)
    {
        return CreateCompleteUpdate(subject, propertyFilter, transformPropertyUpdate, new Dictionary<IInterceptorSubject, SubjectUpdate>());
    }
    
    /// <summary>
    /// Creates a complete update with all objects and properties for the given subject as root.
    /// </summary>
    /// <param name="subject">The root subject.</param>
    /// <param name="propertyFilter">The predicate to exclude properties from the update.</param>
    /// <param name="transformPropertyUpdate">The function to transform or exclude (return null) subject property updates..</param>
    /// <param name="knownSubjectUpdates">The known subject updates.</param>
    /// <returns>The update.</returns>
    internal static SubjectUpdate CreateCompleteUpdate(IInterceptorSubject subject,
        Func<RegisteredSubjectProperty, bool>? propertyFilter,
        Func<RegisteredSubjectProperty, SubjectPropertyUpdate, SubjectPropertyUpdate>? transformPropertyUpdate, 
        Dictionary<IInterceptorSubject, SubjectUpdate> knownSubjectUpdates)
    {
        var subjectUpdate = GetOrCreateSubjectUpdate(subject, knownSubjectUpdates);

        var registeredSubject = subject.TryGetRegisteredSubject();
        if (registeredSubject is not null)
        {
            foreach (var property in registeredSubject.Properties
                .Where(p => p is { HasGetter: true, IsAttribute: false } && propertyFilter?.Invoke(p) != false))
            {
                var propertyUpdate = SubjectPropertyUpdate.CreateCompleteUpdate(
                    property, propertyFilter, transformPropertyUpdate, knownSubjectUpdates);

                subjectUpdate.Properties[property.Name] = 
                    transformPropertyUpdate is not null ? transformPropertyUpdate(property, propertyUpdate) : propertyUpdate;
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
    /// <param name="propertyFilter">The predicate to exclude properties from the update.</param>
    /// <param name="transformPropertyUpdate">The function to transform or exclude (return null) subject property updates..</param>
    /// <returns>The update.</returns>
    public static SubjectUpdate CreatePartialUpdateFromChanges(
        IInterceptorSubject subject, IEnumerable<SubjectPropertyChange> propertyChanges,
        Func<RegisteredSubjectProperty, bool>? propertyFilter = null,
        Func<RegisteredSubjectProperty, SubjectPropertyUpdate, SubjectPropertyUpdate>? transformPropertyUpdate = null)
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
            if (propertyFilter?.Invoke(registeredProperty) == false)
            {
                continue;
            }

            if (registeredProperty.IsAttribute)
            {
                // handle attribute changes
                var (_, rootPropertyUpdate, rootPropertyName) = GetOrCreateSubjectAttributeUpdate(
                    registeredProperty.GetAttributedProperty(), 
                    registeredProperty.AttributeMetadata.AttributeName, 
                    registeredProperty, change, propertyFilter, transformPropertyUpdate,
                    knownSubjectUpdates);
                
                subjectUpdate.Properties[rootPropertyName] = rootPropertyUpdate;
            }
            else
            {
                // handle property changes
                var propertyName = property.Name;
                var propertyUpdate = GetOrCreateSubjectPropertyUpdate(propertySubject, propertyName, knownSubjectUpdates);
                propertyUpdate.ApplyValue(registeredProperty, change.Timestamp, change.GetNewValue<object?>(), propertyFilter, transformPropertyUpdate, knownSubjectUpdates);
                subjectUpdate.Properties[propertyName] = transformPropertyUpdate is not null ? transformPropertyUpdate(registeredProperty, propertyUpdate) : propertyUpdate;
            }

            CreateParentSubjectUpdatePath(registeredSubject, knownSubjectUpdates);
        }

        return update;
    }

    private static void CreateParentSubjectUpdatePath(
        RegisteredSubject registeredSubject,
        Dictionary<IInterceptorSubject, SubjectUpdate> knownSubjectUpdates)
    {
        var parentProperty = registeredSubject.Parents.FirstOrDefault().Property ?? null;
        if (parentProperty?.Subject is { } parentPropertySubject)
        {
            var parentSubjectPropertyUpdate = GetOrCreateSubjectPropertyUpdate(parentPropertySubject, parentProperty.Name, knownSubjectUpdates);

            var parentRegisteredSubject = parentPropertySubject.TryGetRegisteredSubject()
                ?? throw new InvalidOperationException("Registered subject not found.");

            var children = parentRegisteredSubject.TryGetProperty(parentProperty.Name)?.Children;
            if (children?.Any(c => c.Index is not null) == true)
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

    private static (SubjectPropertyUpdate attributeUpdate, SubjectPropertyUpdate propertyUpdate, string propertyName) 
        GetOrCreateSubjectAttributeUpdate(
            RegisteredSubjectProperty property, 
            string attributeName,
            RegisteredSubjectProperty? changeProperty, 
            SubjectPropertyChange? change, 
            Func<RegisteredSubjectProperty, bool>? propertyFilter, 
            Func<RegisteredSubjectProperty, SubjectPropertyUpdate, SubjectPropertyUpdate>? transformPropertyUpdate,
            Dictionary<IInterceptorSubject, SubjectUpdate> knownSubjectUpdates)
    {
        if (property.IsAttribute)
        {
            var (parentAttributeUpdate, parentPropertyUpdate, parentPropertyName) = GetOrCreateSubjectAttributeUpdate(
                property.GetAttributedProperty(), 
                property.AttributeMetadata.AttributeName, 
                null, null, propertyFilter, transformPropertyUpdate, 
                knownSubjectUpdates);
            
            var attributeUpdate = OrCreateSubjectAttributeUpdate(parentAttributeUpdate, attributeName);
            if (changeProperty is not null && change.HasValue)
            {
                attributeUpdate.ApplyValue(changeProperty, change.Value.Timestamp, change.Value.GetNewValue<object?>(), propertyFilter, transformPropertyUpdate, knownSubjectUpdates);
                attributeUpdate = transformPropertyUpdate is not null ? transformPropertyUpdate(changeProperty, attributeUpdate) : attributeUpdate;
                parentAttributeUpdate.Attributes![attributeName] = attributeUpdate;
            }

            return (attributeUpdate, parentPropertyUpdate, parentPropertyName);
        }
        else
        {
            var propertyUpdate = GetOrCreateSubjectPropertyUpdate(
                property.Parent.Subject, property.Name, knownSubjectUpdates);

            var attributeUpdate = OrCreateSubjectAttributeUpdate(propertyUpdate, attributeName);
            if (changeProperty is not null && change.HasValue)
            {
                attributeUpdate.ApplyValue(changeProperty, change.Value.Timestamp, change.Value.GetNewValue<object?>(), propertyFilter, transformPropertyUpdate, knownSubjectUpdates);
                attributeUpdate = transformPropertyUpdate is not null ? transformPropertyUpdate(changeProperty, attributeUpdate) : attributeUpdate;
                propertyUpdate.Attributes![attributeName] = attributeUpdate;
            }

            return (attributeUpdate, propertyUpdate, property.Name);
        }
    }

    private static SubjectPropertyUpdate OrCreateSubjectAttributeUpdate(
        SubjectPropertyUpdate propertyUpdate, string attributeName)
    {
        propertyUpdate.Attributes ??= new Dictionary<string, SubjectPropertyUpdate>();
        if (propertyUpdate.Attributes.TryGetValue(attributeName, out var existingSubjectUpdate))
        {
            return existingSubjectUpdate;
        }

        var attributeUpdate = new SubjectPropertyUpdate();
        propertyUpdate.Attributes[attributeName] = attributeUpdate;
        return attributeUpdate;
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