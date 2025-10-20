using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Sources.Updates;

public class SubjectUpdate
{
    /// <summary>
    /// Gets the type of the subject.
    /// </summary>
    public string? Type { get; init; }

    /// <summary>
    /// Gets a dictionary of property updates.
    /// The dictionary is mutable so that additional updates can be attached.
    /// </summary>
    public Dictionary<string, SubjectPropertyUpdate> Properties { get; init; } = new();

    /// <summary>
    /// Gets or sets custom extension data added by the transformPropertyUpdate function.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, object>? ExtensionData { get; set; }

    /// <summary>
    /// Creates a complete update with all objects and properties for the given subject as root.
    /// </summary>
    /// <param name="subject">The root subject.</param>
    /// <param name="processors">The update processors to filter and transform updates.</param>
    /// <returns>The update.</returns>
    public static SubjectUpdate CreateCompleteUpdate(IInterceptorSubject subject, ReadOnlySpan<ISubjectUpdateProcessor> processors)
    {
        Dictionary<SubjectPropertyUpdate, SubjectPropertyUpdateReference>? propertyUpdates = processors.Length > 0 ? [] : null;

        var knownSubjectUpdates = new Dictionary<IInterceptorSubject, SubjectUpdate>();
        var update = CreateCompleteUpdateInternal(subject, processors, knownSubjectUpdates, propertyUpdates);
      
        if (processors.Length > 0 && propertyUpdates is not null)
        {
            ApplyTransformations(knownSubjectUpdates, propertyUpdates, processors);
        }
        
        return update;
    }

    /// <summary>
    /// Creates a complete update with all objects and properties for the given subject as root.
    /// </summary>
    /// <param name="subject">The root subject.</param>
    /// <param name="processors">The update processors to filter and transform updates.</param>
    /// <param name="knownSubjectUpdates">The known subject updates.</param>
    /// <param name="propertyUpdates">The list to collect property updates for transformation.</param>
    /// <returns>The update.</returns>
    internal static SubjectUpdate CreateCompleteUpdate(IInterceptorSubject subject,
        ReadOnlySpan<ISubjectUpdateProcessor> processors,
        Dictionary<IInterceptorSubject, SubjectUpdate> knownSubjectUpdates, 
        Dictionary<SubjectPropertyUpdate, SubjectPropertyUpdateReference>? propertyUpdates)
    {
        return CreateCompleteUpdateInternal(subject, processors, knownSubjectUpdates, propertyUpdates);
    }

    private static SubjectUpdate CreateCompleteUpdateInternal(IInterceptorSubject subject,
        ReadOnlySpan<ISubjectUpdateProcessor> processors, 
        Dictionary<IInterceptorSubject, SubjectUpdate> knownSubjectUpdates,
        Dictionary<SubjectPropertyUpdate, SubjectPropertyUpdateReference>? propertyUpdates)
    {
        if (knownSubjectUpdates.ContainsKey(subject))
        {
            // Avoid cycles: If subject already has an update then we have a cycle and break it here
            return new SubjectUpdate();
        }
        
        var subjectUpdate = GetOrCreateSubjectUpdate(subject, knownSubjectUpdates);

        var registeredSubject = subject.TryGetRegisteredSubject();
        if (registeredSubject is not null)
        {
            var properties = registeredSubject.Properties;
            for (var index = 0; index < properties.Length; index++)
            {
                var property = registeredSubject.Properties[index];
                if (property is { HasGetter: true, IsAttribute: false } && IsIncluded(processors, property))
                {
                    var propertyUpdate = SubjectPropertyUpdate.CreateCompleteUpdate(
                        property, processors, knownSubjectUpdates, propertyUpdates);

                    subjectUpdate.Properties[property.Name] = propertyUpdate;

                    propertyUpdates?.TryAdd(propertyUpdate, new SubjectPropertyUpdateReference(property, subjectUpdate.Properties));
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
    /// <param name="processors">The update processors to filter and transform updates.</param>
    /// <returns>The update.</returns>
    public static SubjectUpdate CreatePartialUpdateFromChanges(
        IInterceptorSubject subject, 
        ReadOnlySpan<SubjectPropertyChange> propertyChanges,
        ReadOnlySpan<ISubjectUpdateProcessor> processors)
    {
        Dictionary<SubjectPropertyUpdate, SubjectPropertyUpdateReference>? propertyUpdates = processors.Length > 0 ? [] : null;
        
        var knownSubjectUpdates = new Dictionary<IInterceptorSubject, SubjectUpdate>();
        var update = GetOrCreateSubjectUpdate(subject, knownSubjectUpdates);

        for (var index = 0; index < propertyChanges.Length; index++)
        {
            var change = propertyChanges[index];
            
            var subjectUpdate = GetOrCreateSubjectUpdate(change.Property.Subject, knownSubjectUpdates);
            var registeredProperty = change.Property.GetRegisteredProperty();

            if (!IsIncluded(processors, registeredProperty))
            {
                continue;
            }

            if (registeredProperty.IsAttribute)
            {
                // handle attribute changes
                var (_, rootPropertyUpdate, rootPropertyName) = GetOrCreateSubjectAttributeUpdate(
                    registeredProperty.GetAttributedProperty(),
                    registeredProperty.AttributeMetadata.AttributeName,
                    registeredProperty, change, processors,
                    knownSubjectUpdates, propertyUpdates);

                subjectUpdate.Properties[rootPropertyName] = rootPropertyUpdate;
            }
            else
            {
                // handle property changes
                var propertyUpdate = GetOrCreateSubjectPropertyUpdate(registeredProperty, knownSubjectUpdates, propertyUpdates);
                propertyUpdate.ApplyValue(registeredProperty, change.Timestamp, change.GetNewValue<object?>(), processors, knownSubjectUpdates, propertyUpdates);
                subjectUpdate.Properties[registeredProperty.Name] = propertyUpdate;
            }

            CreateParentSubjectUpdatePath(registeredProperty.Parent, subject, knownSubjectUpdates, propertyUpdates);
        }

        if (propertyUpdates is not null)
        {
            ApplyTransformations(knownSubjectUpdates, propertyUpdates, processors);
        }

        return update;
    }

    private static void CreateParentSubjectUpdatePath(
        RegisteredSubject registeredSubject,
        IInterceptorSubject rootSubject,
        Dictionary<IInterceptorSubject, SubjectUpdate> knownSubjectUpdates,
        Dictionary<SubjectPropertyUpdate, SubjectPropertyUpdateReference>? propertyUpdates)
    {
        if (registeredSubject.Subject == rootSubject)
        {
            // Avoid cycles: If we are already in root, then we do not need to traverse further
            return;
        }

        var parentProperty = registeredSubject.Parents.FirstOrDefault().Property ?? null;
        if (parentProperty?.Parent is { } parentRegisteredSubject)
        {
            var parentSubjectPropertyUpdate = GetOrCreateSubjectPropertyUpdate(parentProperty, knownSubjectUpdates, propertyUpdates);
            
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

            CreateParentSubjectUpdatePath(parentRegisteredSubject, rootSubject, knownSubjectUpdates, propertyUpdates);
        }
    }

    private static (SubjectPropertyUpdate attributeUpdate, SubjectPropertyUpdate propertyUpdate, string propertyName) 
        GetOrCreateSubjectAttributeUpdate(
            RegisteredSubjectProperty property, 
            string attributeName,
            RegisteredSubjectProperty? changeProperty, 
            SubjectPropertyChange? change, 
            ReadOnlySpan<ISubjectUpdateProcessor> processors,
            Dictionary<IInterceptorSubject, SubjectUpdate> knownSubjectUpdates,
            Dictionary<SubjectPropertyUpdate, SubjectPropertyUpdateReference>? propertyUpdates)
    {
        if (property.IsAttribute)
        {
            var (parentAttributeUpdate, parentPropertyUpdate, parentPropertyName) = GetOrCreateSubjectAttributeUpdate(
                property.GetAttributedProperty(), 
                property.AttributeMetadata.AttributeName, 
                null, null, processors, 
                knownSubjectUpdates, propertyUpdates);
            
            var attributeUpdate = GetOrCreateSubjectAttributeUpdate(parentAttributeUpdate, attributeName, propertyUpdates);
            if (changeProperty is not null && change.HasValue)
            {
                attributeUpdate.ApplyValue(changeProperty, change.Value.Timestamp, change.Value.GetNewValue<object?>(), processors, knownSubjectUpdates, propertyUpdates);
            }
            
            if (propertyUpdates is not null && parentAttributeUpdate.Attributes is not null)
            {
                var parentAttrProperty = property.GetAttributedProperty();
                propertyUpdates[parentAttributeUpdate] = new SubjectPropertyUpdateReference(parentAttrProperty, parentAttributeUpdate.Attributes);
            }

            return (attributeUpdate, parentPropertyUpdate, parentPropertyName);
        }
        else
        {
            var propertyUpdate = GetOrCreateSubjectPropertyUpdate(
                property.Parent.Subject.TryGetRegisteredProperty(property.Name)!, 
                knownSubjectUpdates, propertyUpdates);

            var attributeUpdate = GetOrCreateSubjectAttributeUpdate(propertyUpdate, attributeName, propertyUpdates);
           
            if (changeProperty is not null && change.HasValue)
            {
                attributeUpdate.ApplyValue(changeProperty, change.Value.Timestamp, change.Value.GetNewValue<object?>(), processors, knownSubjectUpdates, propertyUpdates);
            }

            if (propertyUpdates is not null)
            {
                var subjectUpdate = GetOrCreateSubjectUpdate(property.Parent.Subject, knownSubjectUpdates);
                propertyUpdates[propertyUpdate] = new SubjectPropertyUpdateReference(property, subjectUpdate.Properties);
            }

            return (attributeUpdate, propertyUpdate, property.Name);
        }
    }

    private static SubjectPropertyUpdate GetOrCreateSubjectAttributeUpdate(
        SubjectPropertyUpdate propertyUpdate, string attributeName,
        Dictionary<SubjectPropertyUpdate, SubjectPropertyUpdateReference>? propertyUpdates)
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
        RegisteredSubjectProperty property,
        Dictionary<IInterceptorSubject, SubjectUpdate> knownSubjectUpdates,
        Dictionary<SubjectPropertyUpdate, SubjectPropertyUpdateReference>? propertyUpdates)
    {
        var subjectUpdate = GetOrCreateSubjectUpdate(property.Subject, knownSubjectUpdates);
        if (subjectUpdate.Properties.TryGetValue(property.Name, out var existingSubjectUpdate))
        {
            return existingSubjectUpdate;
        }

        var propertyUpdate = new SubjectPropertyUpdate();
        subjectUpdate.Properties[property.Name] = propertyUpdate;
        propertyUpdates?.TryAdd(propertyUpdate, new SubjectPropertyUpdateReference(property, subjectUpdate.Properties));
        return propertyUpdate;
    }

    private static SubjectUpdate GetOrCreateSubjectUpdate(
        IInterceptorSubject subject,
        Dictionary<IInterceptorSubject, SubjectUpdate> knownSubjectUpdates)
    {
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

    private static void ApplyTransformations(
        Dictionary<IInterceptorSubject, SubjectUpdate> knownSubjectUpdates,
        Dictionary<SubjectPropertyUpdate, SubjectPropertyUpdateReference> propertyUpdates,
        ReadOnlySpan<ISubjectUpdateProcessor> processors)
    {
        foreach (var update in propertyUpdates)
        {
            for (var i = 0; i < processors.Length; i++)
            {
                var processor = processors[i];
                var transformed = processor.TransformSubjectPropertyUpdate(update.Value.Property, update.Key);
                if (transformed != update.Key)
                {
                    update.Value.ParentCollection[update.Value.Property.Name] = transformed;
                }
            }
        }

        foreach (var (subject, subjectUpdate) in knownSubjectUpdates)
        {
            for (var index = 0; index < processors.Length; index++)
            {
                var processor = processors[index];
                var transformed = processor.TransformSubjectUpdate(subject, subjectUpdate);
                if (transformed != subjectUpdate)
                {
                    knownSubjectUpdates[subject] = transformed;
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsIncluded(ReadOnlySpan<ISubjectUpdateProcessor> processors, RegisteredSubjectProperty property)
    {
        for (var index = 0; index < processors.Length; index++)
        {
            var processor = processors[index];
            if (!processor.IsIncluded(property))
            {
                return false;
            }
        }

        return true;
    }
}