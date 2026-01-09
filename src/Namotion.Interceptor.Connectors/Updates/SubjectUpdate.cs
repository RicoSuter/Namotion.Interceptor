using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using Namotion.Interceptor.Connectors.Updates.Performance;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Updates;

public class SubjectUpdate
{
    [ThreadStatic]
    private static int _referenceIdCounter;

    /// <summary>
    /// Gets or sets the unique ID of the subject (only set if there is a reference pointing to it).
    /// </summary>
    public int? Id { get; set; }

    /// <summary>
    /// Gets or sets the reference ID of an already existing subject.
    /// </summary>
    public int? Reference { get; set; }

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
        _referenceIdCounter = 0;

        Dictionary<SubjectPropertyUpdate, SubjectPropertyUpdateReference>? propertyUpdates = null;
        var knownSubjectUpdates = SubjectUpdatePools.RentKnownSubjectUpdates();
        var currentPath = SubjectUpdatePools.RentCurrentPath();
        try
        {
            if (processors.Length > 0)
            {
                propertyUpdates = SubjectUpdatePools.RentPropertyUpdates();
            }

            var update = GetOrCreateCompleteUpdate(subject, processors, knownSubjectUpdates, propertyUpdates, currentPath);
            if (processors.Length > 0 && propertyUpdates is not null && propertyUpdates.Count > 0)
            {
                ApplyTransformations(knownSubjectUpdates, propertyUpdates, processors);
            }

            return update;
        }
        finally
        {
            SubjectUpdatePools.ReturnCurrentPath(currentPath);
            SubjectUpdatePools.ReturnPropertyUpdates(propertyUpdates);
            SubjectUpdatePools.ReturnKnownSubjectUpdates(knownSubjectUpdates);
        }
    }

    /// <summary>
    /// Creates a complete update with all objects and properties for the given subject as root.
    /// </summary>
    /// <param name="subject">The root subject.</param>
    /// <param name="processors">The update processors to filter and transform updates.</param>
    /// <param name="knownSubjectUpdates">The known subject updates.</param>
    /// <param name="propertyUpdates">The list to collect property updates for transformation.</param>
    /// <param name="currentPath">The set of subjects in the current traversal path (for cycle detection).</param>
    /// <returns>The update.</returns>
    internal static SubjectUpdate GetOrCreateCompleteUpdate(IInterceptorSubject subject,
        ReadOnlySpan<ISubjectUpdateProcessor> processors,
        Dictionary<IInterceptorSubject, SubjectUpdate> knownSubjectUpdates,
        Dictionary<SubjectPropertyUpdate, SubjectPropertyUpdateReference>? propertyUpdates,
        HashSet<IInterceptorSubject> currentPath)
    {
        if (knownSubjectUpdates.TryGetValue(subject, out var existingUpdate))
        {
            if (currentPath.Contains(subject))
            {
                // Cycle detected - create reference to break the cycle
                existingUpdate.Id ??= ++_referenceIdCounter;
                return new SubjectUpdate
                {
                    Reference = existingUpdate.Id
                };
            }

            // Subject already processed, not in current path - return existing update (no cycle)
            return existingUpdate;
        }

        var subjectUpdate = GetOrCreateSubjectUpdate(subject, knownSubjectUpdates);
        currentPath.Add(subject);

        try
        {
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
                            property, processors, knownSubjectUpdates, propertyUpdates, currentPath);

                        subjectUpdate.Properties[property.Name] = propertyUpdate;

                        propertyUpdates?.TryAdd(propertyUpdate, new SubjectPropertyUpdateReference(property, subjectUpdate.Properties));
                    }
                }
            }

            return subjectUpdate;
        }
        finally
        {
            currentPath.Remove(subject);
        }
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
        _referenceIdCounter = 0;

        var propertyUpdates = processors.Length > 0 ? SubjectUpdatePools.RentPropertyUpdates() : null;
        var knownSubjectUpdates = SubjectUpdatePools.RentKnownSubjectUpdates();
        var processedParentPaths = SubjectUpdatePools.RentProcessedParentPaths();
        var currentPath = SubjectUpdatePools.RentCurrentPath();
        try
        {
            var update = GetOrCreateSubjectUpdate(subject, knownSubjectUpdates);

            // Add root to currentPath - this is key for detecting cycles back to root
            currentPath.Add(subject);

            for (var index = 0; index < propertyChanges.Length; index++)
            {
                var change = propertyChanges[index];

                var subjectUpdate = GetOrCreateSubjectUpdate(change.Property.Subject, knownSubjectUpdates);
                var registeredProperty = change.Property.TryGetRegisteredProperty();
                if (registeredProperty is null)
                {
                    continue; // property not registered because subject has been detached since update is detected (change not valid anymore)
                }

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
                        processors, knownSubjectUpdates, propertyUpdates, currentPath);

                    subjectUpdate.Properties[registeredProperty.Name] = propertyUpdate;
                }
                else
                {
                    // handle attribute changes
                    var (rootPropertyUpdate, rootPropertyName) = GetOrCreateSubjectAttributeUpdate(
                        registeredProperty.GetAttributedProperty(),
                        registeredProperty.AttributeMetadata.AttributeName,
                        registeredProperty, change, processors,
                        knownSubjectUpdates, propertyUpdates, currentPath);

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
            SubjectUpdatePools.ReturnCurrentPath(currentPath);
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

            var parentProperty = registeredSubject.Parents.Length > 0
                ? registeredSubject.Parents[0].Property
                : null;
            if (parentProperty?.Parent is not { } parentRegisteredSubject)
            {
                return;
            }

            var parentSubjectPropertyUpdate = GetOrCreateSubjectPropertyUpdate(parentProperty, knownSubjectUpdates, propertyUpdates);
            var children = parentRegisteredSubject.TryGetProperty(parentProperty.Name)?.Children;

            if (children is { } childrenValue && HasIndexedChildren(childrenValue))
            {
                var collectionUpdates = new List<SubjectPropertyCollectionUpdate>(childrenValue.Length);
                foreach (var child in childrenValue)
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
        Dictionary<SubjectPropertyUpdate, SubjectPropertyUpdateReference>? propertyUpdates,
        HashSet<IInterceptorSubject>? currentPath = null)
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
                processors, knownSubjectUpdates, propertyUpdates: propertyUpdates, currentPath);
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static SubjectPropertyUpdate GetOrCreateSubjectAttributeUpdate(
        SubjectPropertyUpdate propertyUpdate, string attributeName)
    {
        propertyUpdate.Attributes ??= new Dictionary<string, SubjectPropertyUpdate>();
        ref var attributeUpdate = ref CollectionsMarshal.GetValueRefOrAddDefault(propertyUpdate.Attributes, attributeName, out var exists);
        if (!exists)
        {
            attributeUpdate = new SubjectPropertyUpdate();
        }

        return attributeUpdate!;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static SubjectPropertyUpdate GetOrCreateSubjectPropertyUpdate(
        RegisteredSubjectProperty property,
        Dictionary<IInterceptorSubject, SubjectUpdate> knownSubjectUpdates,
        Dictionary<SubjectPropertyUpdate, SubjectPropertyUpdateReference>? propertyUpdates)
    {
        var subjectUpdate = GetOrCreateSubjectUpdate(property.Subject, knownSubjectUpdates);
        ref var propertyUpdate = ref CollectionsMarshal.GetValueRefOrAddDefault(subjectUpdate.Properties, property.Name, out var exists);
        if (!exists)
        {
            propertyUpdate = new SubjectPropertyUpdate();
            propertyUpdates?.TryAdd(propertyUpdate, new SubjectPropertyUpdateReference(property, subjectUpdate.Properties));
        }

        return propertyUpdate!;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static SubjectUpdate GetOrCreateSubjectUpdate(
        IInterceptorSubject subject,
        Dictionary<IInterceptorSubject, SubjectUpdate> knownSubjectUpdates)
    {
        ref var subjectUpdate = ref CollectionsMarshal.GetValueRefOrAddDefault(knownSubjectUpdates, subject, out var exists);
        if (!exists)
        {
            subjectUpdate = new SubjectUpdate();
        }

        return subjectUpdate!;
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
    private static bool HasIndexedChildren(ImmutableArray<SubjectPropertyChild> children)
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
