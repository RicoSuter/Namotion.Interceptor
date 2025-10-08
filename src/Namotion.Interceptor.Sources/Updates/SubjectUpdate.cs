using System.Text.Json.Serialization;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
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
    /// <param name="processor">The update processor to filter and transform updates.</param>
    /// <returns>The update.</returns>
    public static SubjectUpdate CreateCompleteUpdate(IInterceptorSubject subject, ISubjectUpdateProcessor? processor = null)
    {
        var knownSubjectUpdates = new Dictionary<IInterceptorSubject, SubjectUpdate>();
        List<(RegisteredSubjectProperty, SubjectPropertyUpdate, IDictionary<string, SubjectPropertyUpdate>)>? propertyUpdatesToTransform = processor is not null ? new() : null;
        
        var update = CreateCompleteUpdateInternal(subject, processor, knownSubjectUpdates, propertyUpdatesToTransform);
        
        if (processor is not null && propertyUpdatesToTransform is not null)
        {
            ApplyTransformations(knownSubjectUpdates, propertyUpdatesToTransform, processor);
        }
        
        return update;
    }

    /// <summary>
    /// Creates a complete update with all objects and properties for the given subject as root.
    /// </summary>
    /// <param name="subject">The root subject.</param>
    /// <param name="processor">The update processor to filter and transform updates.</param>
    /// <param name="knownSubjectUpdates">The known subject updates.</param>
    /// <param name="propertyUpdatesToTransform"></param>
    /// <returns>The update.</returns>
    internal static SubjectUpdate CreateCompleteUpdate(IInterceptorSubject subject,
        ISubjectUpdateProcessor? processor,
        Dictionary<IInterceptorSubject, SubjectUpdate> knownSubjectUpdates, List<(RegisteredSubjectProperty, SubjectPropertyUpdate, IDictionary<string, SubjectPropertyUpdate>)>? propertyUpdatesToTransform)
    {
        // This is called from ApplyValue for nested subjects - don't collect/apply transformations here
        return CreateCompleteUpdateInternal(subject, processor, knownSubjectUpdates, propertyUpdatesToTransform);
    }

    private static SubjectUpdate CreateCompleteUpdateInternal(IInterceptorSubject subject,
        ISubjectUpdateProcessor? processor, 
        Dictionary<IInterceptorSubject, SubjectUpdate> knownSubjectUpdates,
        List<(RegisteredSubjectProperty, SubjectPropertyUpdate, IDictionary<string, SubjectPropertyUpdate>)>? propertyUpdatesToTransform)
    {
        var subjectUpdate = GetOrCreateSubjectUpdate(subject, knownSubjectUpdates);

        var registeredSubject = subject.TryGetRegisteredSubject();
        if (registeredSubject is not null)
        {
            foreach (var property in registeredSubject.Properties
                .Where(p => p is { HasGetter: true, IsAttribute: false } && processor?.IsIncluded(p) != false))
            {
                var propertyUpdate = SubjectPropertyUpdate.CreateCompleteUpdate(
                    property, processor, knownSubjectUpdates, propertyUpdatesToTransform);

                subjectUpdate.Properties[property.Name] = propertyUpdate;
                
                // Collect for transformation
                if (propertyUpdatesToTransform is not null)
                {
                    propertyUpdatesToTransform.Add((property, propertyUpdate, subjectUpdate.Properties));
                }
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
    /// <param name="processor">The update processor to filter and transform updates.</param>
    /// <returns>The update.</returns>
    public static SubjectUpdate CreatePartialUpdateFromChanges(
        IInterceptorSubject subject, 
        IEnumerable<SubjectPropertyChange> propertyChanges,
        ISubjectUpdateProcessor? processor = null)
    {
        var knownSubjectUpdates = new Dictionary<IInterceptorSubject, SubjectUpdate>();
        List<(RegisteredSubjectProperty, SubjectPropertyUpdate, IDictionary<string, SubjectPropertyUpdate>)>? propertyUpdatesToTransform = processor is not null ? new() : null;
        
        var update = GetOrCreateSubjectUpdate(subject, knownSubjectUpdates);

        foreach (var change in propertyChanges)
        {
            var property = change.Property;
            var registeredSubject = property.Subject.TryGetRegisteredSubject()
                ?? throw new InvalidOperationException("Registered subject not found.");

            var propertySubject = property.Subject;
            var subjectUpdate = GetOrCreateSubjectUpdate(propertySubject, knownSubjectUpdates);
            
            var registeredProperty = property.GetRegisteredProperty();
            if (processor?.IsIncluded(registeredProperty) == false)
            {
                continue;
            }

            if (registeredProperty.IsAttribute)
            {
                // handle attribute changes
                var (_, rootPropertyUpdate, rootPropertyName) = GetOrCreateSubjectAttributeUpdate(
                    registeredProperty.GetAttributedProperty(), 
                    registeredProperty.AttributeMetadata.AttributeName, 
                    registeredProperty, change, processor,
                    knownSubjectUpdates, propertyUpdatesToTransform);
                
                subjectUpdate.Properties[rootPropertyName] = rootPropertyUpdate;
            }
            else
            {
                // handle property changes
                var propertyName = property.Name;
                var propertyUpdate = GetOrCreateSubjectPropertyUpdate(propertySubject, propertyName, knownSubjectUpdates);
                propertyUpdate.ApplyValue(registeredProperty, change.Timestamp, change.GetNewValue<object?>(), processor, knownSubjectUpdates, propertyUpdatesToTransform);
                subjectUpdate.Properties[propertyName] = propertyUpdate;
                
                // Collect for transformation
                if (propertyUpdatesToTransform is not null)
                {
                    propertyUpdatesToTransform.Add((registeredProperty, propertyUpdate, subjectUpdate.Properties));
                }
            }

            CreateParentSubjectUpdatePath(registeredSubject, knownSubjectUpdates);
        }

        if (processor is not null && propertyUpdatesToTransform is not null)
        {
            ApplyTransformations(knownSubjectUpdates, propertyUpdatesToTransform, processor);
        }

        return update;
    }

    private static void ApplyTransformations(
        Dictionary<IInterceptorSubject, SubjectUpdate> knownSubjectUpdates,
        List<(RegisteredSubjectProperty Property, SubjectPropertyUpdate Update, IDictionary<string, SubjectPropertyUpdate> ParentDict)> propertyUpdatesToTransform,
        ISubjectUpdateProcessor processor)
    {
        // Transform all property updates from the cached list (no traversal needed)
        foreach (var (property, propertyUpdate, parentDict) in propertyUpdatesToTransform)
        {
            var transformed = processor.TransformSubjectPropertyUpdate(property, propertyUpdate);
            if (transformed != propertyUpdate)
            {
                parentDict[property.Name] = transformed;
            }
        }

        // Transform all subject updates
        foreach (var (subject, subjectUpdate) in knownSubjectUpdates)
        {
            var transformed = processor.TransformSubjectUpdate(subject, subjectUpdate);
            if (transformed != subjectUpdate)
            {
                knownSubjectUpdates[subject] = transformed;
            }
        }
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
            ISubjectUpdateProcessor? processor,
            Dictionary<IInterceptorSubject, SubjectUpdate> knownSubjectUpdates,
            List<(RegisteredSubjectProperty, SubjectPropertyUpdate, IDictionary<string, SubjectPropertyUpdate>)>? propertyUpdatesToTransform)
    {
        if (property.IsAttribute)
        {
            var (parentAttributeUpdate, parentPropertyUpdate, parentPropertyName) = GetOrCreateSubjectAttributeUpdate(
                property.GetAttributedProperty(), 
                property.AttributeMetadata.AttributeName, 
                null, null, processor, 
                knownSubjectUpdates, propertyUpdatesToTransform);
            
            var attributeUpdate = GetOrCreateSubjectAttributeUpdate(parentAttributeUpdate, attributeName);
            if (changeProperty is not null && change.HasValue)
            {
                attributeUpdate.ApplyValue(changeProperty, change.Value.Timestamp, change.Value.GetNewValue<object?>(), processor, knownSubjectUpdates, propertyUpdatesToTransform);
            }
            
            // Collect the parent attribute for transformation so its nested attributes get transformed
            if (propertyUpdatesToTransform is not null && parentAttributeUpdate.Attributes is not null)
            {
                var parentAttrProperty = property.GetAttributedProperty();
                propertyUpdatesToTransform.Add((parentAttrProperty, parentAttributeUpdate, parentPropertyUpdate.Attributes ?? new Dictionary<string, SubjectPropertyUpdate>()));
            }

            return (attributeUpdate, parentPropertyUpdate, parentPropertyName);
        }
        else
        {
            var propertyUpdate = GetOrCreateSubjectPropertyUpdate(
                property.Parent.Subject, property.Name, knownSubjectUpdates);

            var attributeUpdate = GetOrCreateSubjectAttributeUpdate(propertyUpdate, attributeName);
            if (changeProperty is not null && change.HasValue)
            {
                attributeUpdate.ApplyValue(changeProperty, change.Value.Timestamp, change.Value.GetNewValue<object?>(), processor, knownSubjectUpdates, propertyUpdatesToTransform);
            }
            
            // Collect the main property (firstName) that contains the attributes dictionary
            // This allows the Transform method to transform all attribute keys in the Attributes dict
            if (propertyUpdatesToTransform is not null)
            {
                var subjectUpdate = GetOrCreateSubjectUpdate(property.Parent.Subject, knownSubjectUpdates);
                propertyUpdatesToTransform.Add((property, propertyUpdate, subjectUpdate.Properties));
            }

            return (attributeUpdate, propertyUpdate, property.Name);
        }
    }

    private static SubjectPropertyUpdate GetOrCreateSubjectAttributeUpdate(
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
        // Avoid double lookup and unnecessary dictionary churn
        if (knownSubjectUpdates.TryGetValue(subject, out var subjectUpdate))
        {
            return subjectUpdate;
        }
        subjectUpdate = new SubjectUpdate
        {
            Type = subject.GetType().Name
        };
        knownSubjectUpdates[subject] = subjectUpdate;
        return subjectUpdate;
    }
}