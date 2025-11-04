using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Sources.Updates.Performance;
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
        Dictionary<SubjectPropertyUpdate, SubjectPropertyUpdateReference>? propertyUpdates = null;
        var knownSubjectUpdates = SubjectUpdatePools.RentKnownSubjectUpdates();
        try
        {
            if (processors.Length > 0)
            {
                propertyUpdates = SubjectUpdatePools.RentPropertyUpdates();
            }

            var update = CreateCompleteUpdate(subject, withCycleCheck: true, processors, knownSubjectUpdates, propertyUpdates);
            if (processors.Length > 0 && propertyUpdates is not null && propertyUpdates.Count > 0)
            {
                ApplyTransformations(knownSubjectUpdates, propertyUpdates, processors);
            }

            return update;
        }
        finally
        {
            SubjectUpdatePools.ReturnPropertyUpdates(propertyUpdates);
            SubjectUpdatePools.ReturnKnownSubjectUpdates(knownSubjectUpdates);
        }
    }

    /// <summary>
    /// Creates a complete update with all objects and properties for the given subject as root.
    /// </summary>
    /// <param name="subject">The root subject.</param>
    /// <param name="withCycleCheck"></param>
    /// <param name="processors">The update processors to filter and transform updates.</param>
    /// <param name="knownSubjectUpdates">The known subject updates.</param>
    /// <param name="propertyUpdates">The list to collect property updates for transformation.</param>
    /// <returns>The update.</returns>
    internal static SubjectUpdate CreateCompleteUpdate(IInterceptorSubject subject,
        bool withCycleCheck,
        ReadOnlySpan<ISubjectUpdateProcessor> processors,
        Dictionary<IInterceptorSubject, SubjectUpdate> knownSubjectUpdates, 
        Dictionary<SubjectPropertyUpdate, SubjectPropertyUpdateReference>? propertyUpdates)
    {
        if (withCycleCheck && knownSubjectUpdates.TryGetValue(subject, out _))
        {
            // Stop cycles with empty update
            return new SubjectUpdate();
        }

        if (knownSubjectUpdates.TryGetValue(subject, out var update))
        {
            // Stop here when already generated in previous step
            return update;
        }

        var subjectUpdate = GetOrCreateSubjectUpdate(subject, knownSubjectUpdates);

        var registeredSubject = subject.TryGetRegisteredSubject();
        if (registeredSubject is not null)
        {
            var properties = registeredSubject.Properties;
            for (var index = 0; index < properties.Length; index++)
            {
                var property = registeredSubject.Properties[index];
                if (property is { HasGetter: true, IsAttribute: false } && IsPropertyIncluded(property, processors))
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
        var propertyUpdates = processors.Length > 0 ? SubjectUpdatePools.RentPropertyUpdates() : null;
        var knownSubjectUpdates = SubjectUpdatePools.RentKnownSubjectUpdates();
        var processedParentPaths = SubjectUpdatePools.RentProcessedParentPaths();
        try
        {
            var update = GetOrCreateSubjectUpdate(subject, knownSubjectUpdates);
            for (var index = 0; index < propertyChanges.Length; index++)
            {
                var change = propertyChanges[index];
                
                var subjectUpdate = GetOrCreateSubjectUpdate(change.Property.Subject, knownSubjectUpdates);
                var registeredProperty = change.Property.GetRegisteredProperty();

                if (!IsPropertyIncluded(registeredProperty, processors))
                {
                    continue;
                }

                if (!registeredProperty.IsAttribute)
                {
                    // handle property changes
                    var propertyUpdate = GetOrCreateSubjectPropertyUpdate(registeredProperty, knownSubjectUpdates, propertyUpdates);
                
                    propertyUpdate.ApplyValue(
                        registeredProperty, change.ChangedTimestamp, change.GetNewValue<object?>(), 
                        withCycleCheck: false, processors, knownSubjectUpdates, propertyUpdates);
                    
                    subjectUpdate.Properties[registeredProperty.Name] = propertyUpdate;
                }
                else
                {
                    // handle attribute changes
                    var (rootPropertyUpdate, rootPropertyName) = GetOrCreateSubjectAttributeUpdate(
                        registeredProperty.GetAttributedProperty(),
                        registeredProperty.AttributeMetadata.AttributeName,
                        registeredProperty, change, processors,
                        knownSubjectUpdates, propertyUpdates);

                    subjectUpdate.Properties[rootPropertyName] = rootPropertyUpdate;
                }

                CreateParentSubjectUpdatePath(registeredProperty.Parent, subject, knownSubjectUpdates, propertyUpdates, processedParentPaths);
            }

            if (propertyUpdates is not null)
            {
                ApplyTransformations(knownSubjectUpdates, propertyUpdates, processors);
            }

            return update;
        }
        finally
        {
            SubjectUpdatePools.ReturnPropertyUpdates(propertyUpdates);
            SubjectUpdatePools.ReturnProcessedParentPaths(processedParentPaths);
            SubjectUpdatePools.ReturnKnownSubjectUpdates(knownSubjectUpdates);
        }
    }

    private static void CreateParentSubjectUpdatePath(RegisteredSubject registeredSubject, IInterceptorSubject rootSubject, Dictionary<IInterceptorSubject, SubjectUpdate> knownSubjectUpdates, Dictionary<SubjectPropertyUpdate, SubjectPropertyUpdateReference>? propertyUpdates, HashSet<IInterceptorSubject> processedParentPaths)
    {
        while (true)
        {
            if (registeredSubject.Subject == rootSubject)
            {
                // Avoid cycles: If we are already in root, then we do not need to traverse further
                return;
            }

            if (!processedParentPaths.Add(registeredSubject.Subject))
            {
                // Already processed
                return;
            }

            var parentProperty = registeredSubject.Parents.FirstOrDefault().Property;
            if (parentProperty?.Parent is not { } parentRegisteredSubject)
            {
                return;
            }

            var parentSubjectPropertyUpdate = GetOrCreateSubjectPropertyUpdate(parentProperty, knownSubjectUpdates, propertyUpdates);
            var children = parentRegisteredSubject.TryGetProperty(parentProperty.Name)?.Children;

            if (children is not null && HasIndexedChildren(children))
            {
                var collectionUpdates = new List<SubjectPropertyCollectionUpdate>(children.Count);
                foreach (var child in children)
                {
                    collectionUpdates.Add(new SubjectPropertyCollectionUpdate { Item = GetOrCreateSubjectUpdate(child.Subject, knownSubjectUpdates), Index = child.Index ?? throw new InvalidOperationException("Index must not be null.") });
                }

                parentSubjectPropertyUpdate.Kind = SubjectPropertyUpdateKind.Collection;
                parentSubjectPropertyUpdate.Collection = collectionUpdates;
            }
            else
            {
                parentSubjectPropertyUpdate.Kind = SubjectPropertyUpdateKind.Item;
                parentSubjectPropertyUpdate.Item = GetOrCreateSubjectUpdate(registeredSubject.Subject, knownSubjectUpdates);
            }

            registeredSubject = parentRegisteredSubject;
        }
    }

    private static (SubjectPropertyUpdate propertyUpdate, string propertyName) GetOrCreateSubjectAttributeUpdate(
        RegisteredSubjectProperty property,
        string attributeName,
        RegisteredSubjectProperty? changeProperty,
        SubjectPropertyChange? change,
        ReadOnlySpan<ISubjectUpdateProcessor> processors,
        Dictionary<IInterceptorSubject, SubjectUpdate> knownSubjectUpdates,
        Dictionary<SubjectPropertyUpdate, SubjectPropertyUpdateReference>? propertyUpdates)
    {
        // Find the root property by walking up the attribute chain
        var rootProperty = property;
        while (rootProperty.IsAttribute)
        {
            rootProperty = rootProperty.GetAttributedProperty();
        }
        
        // Get or create the root property update (this already calls TryAdd)
        var rootPropertyUpdate = GetOrCreateSubjectPropertyUpdate(
            rootProperty.Parent.Subject.TryGetRegisteredProperty(rootProperty.Name)!, 
            knownSubjectUpdates, propertyUpdates);
        
        // Build the attribute chain from root down to the target attribute
        var currentUpdate = rootPropertyUpdate;
        
        if (property.IsAttribute)
        {
            // Recursive helper to build the chain without allocations
            currentUpdate = BuildAttributeChainRecursive(property, rootPropertyUpdate, propertyUpdates);
        }
        
        // Create the final attribute update
        var finalAttributeUpdate = GetOrCreateSubjectAttributeUpdate(currentUpdate, attributeName);
        
        // Track final attribute update in propertyUpdates for transformation (exactly once)
        propertyUpdates?.TryAdd(finalAttributeUpdate, 
            new SubjectPropertyUpdateReference(changeProperty ?? property, currentUpdate.Attributes!));

        // Apply value if needed
        if (changeProperty is not null && change.HasValue)
        {
            finalAttributeUpdate.ApplyValue(
                changeProperty, change.Value.ChangedTimestamp, change.Value.GetNewValue<object?>(), 
                withCycleCheck: false, processors, knownSubjectUpdates, propertyUpdates: propertyUpdates);
        }

        return (rootPropertyUpdate, rootProperty.Name);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static SubjectPropertyUpdate BuildAttributeChainRecursive(
        RegisteredSubjectProperty property, SubjectPropertyUpdate rootUpdate,
        Dictionary<SubjectPropertyUpdate, SubjectPropertyUpdateReference>? propertyUpdates)
    {
        if (!property.IsAttribute)
        {
            return rootUpdate;
        }

        var parentProperty = property.GetAttributedProperty();
        var parentUpdate = BuildAttributeChainRecursive(parentProperty, rootUpdate, propertyUpdates);
        
        var attrName = property.AttributeMetadata.AttributeName;
        var attributeUpdate = GetOrCreateSubjectAttributeUpdate(parentUpdate, attrName);
        
        propertyUpdates?.TryAdd(attributeUpdate, new SubjectPropertyUpdateReference(property, parentUpdate.Attributes!));
        return attributeUpdate;
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsPropertyIncluded(RegisteredSubjectProperty property, ReadOnlySpan<ISubjectUpdateProcessor> processors)
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
    private static bool HasIndexedChildren(ICollection<SubjectPropertyChild> children)
    {
        foreach (var child in children)
        {
            if (child.Index is not null)
            {
                return true;
            }
        }
        return false;
    }
}