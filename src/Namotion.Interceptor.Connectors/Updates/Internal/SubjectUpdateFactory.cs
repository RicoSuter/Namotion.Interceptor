using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Performance;

namespace Namotion.Interceptor.Connectors.Updates.Internal;

/// <summary>
/// Factory for creating <see cref="SubjectUpdate"/> instances.
/// Uses a flat structure where all subjects are stored in a single dictionary
/// and referenced by string IDs, eliminating cycle detection complexity.
/// </summary>
internal static class SubjectUpdateFactory
{
    private static readonly ObjectPool<SubjectUpdateBuilder> BuilderPool = new(() => new SubjectUpdateBuilder());

    /// <summary>
    /// Creates a complete update with all properties for the given subject.
    /// </summary>
    public static SubjectUpdate CreateCompleteUpdate(
        IInterceptorSubject subject,
        ISubjectUpdateProcessor[] processors)
    {
        var builder = BuilderPool.Rent();
        try
        {
            builder.Initialize(subject, processors, isPartialUpdate: false);
            ProcessSubjectComplete(subject, builder);
            return builder.Build(subject);
        }
        finally
        {
            builder.Clear();
            BuilderPool.Return(builder);
        }
    }

    /// <summary>
    /// Creates a partial update from property changes.
    /// </summary>
    public static SubjectUpdate CreatePartialUpdateFromChanges(
        IInterceptorSubject rootSubject,
        ReadOnlySpan<SubjectPropertyChange> propertyChanges,
        ISubjectUpdateProcessor[] processors)
    {
        var builder = BuilderPool.Rent();
        try
        {
            builder.Initialize(rootSubject, processors, isPartialUpdate: true);
            propertyChanges = builder.MergeChanges(propertyChanges);

            // Subject-holding changes run first, so that the subjects they complete are built before the value
            // changes write their captured values over them: a batch arrives in any order.
            var valueChanges = builder.ValueChanges;
            for (var i = 0; i < propertyChanges.Length; i++)
            {
                var property = propertyChanges[i].Property.TryGetRegisteredProperty();
                if (property is null)
                {
                    // The subject left the graph after the change was captured. A change that attaches it again
                    // carries its complete state.
                    SubjectUpdateDiagnostics.RecordDroppedOutboundChange();
                    continue;
                }

                if (!builder.IsPublished(property) || !builder.IsReachable(property.Parent))
                    continue;

                if (property.CanContainSubjects)
                    ProcessStructuralChange(propertyChanges[i], property, builder);
                else
                    valueChanges.Add((i, property));
            }

            foreach (var (index, property) in valueChanges)
            {
                ProcessValueChange(propertyChanges[index], property, builder);
            }

            return builder.Build(rootSubject);
        }
        finally
        {
            builder.Clear();
            BuilderPool.Return(builder);
        }
    }

    /// <summary>
    /// Adds the complete state of <paramref name="subject"/> to the update unless it already holds it. A partial
    /// update never completes its root, which every receiver holds.
    /// </summary>
    internal static void ProcessSubjectComplete(IInterceptorSubject subject, SubjectUpdateBuilder builder)
    {
        if (builder.IsComplete(subject) || (builder.IsPartialUpdate && ReferenceEquals(subject, builder.RootSubject)))
            return;

        var properties = builder.MarkComplete(subject);
        var registeredSubject = subject.TryGetRegisteredSubject();
        if (registeredSubject is null)
        {
            // A property holds it while its attach is still in progress, it was written into the property in place,
            // or the context has no registry: leaving it out would lose every member listed next to it.
            ProcessSubjectFromMetadata(subject, properties, builder);
            return;
        }

        foreach (var property in registeredSubject.Properties)
        {
            if (!property.HasGetter || property.IsAttribute || !builder.IsPublished(property))
                continue;

            // Replaces what a change stated for the property: the complete state is the current one, and a
            // structural change it replaces may list members as known that are new to this receiver.
            var propertyUpdate = CreatePropertyUpdate(property, builder);
            properties[property.Name] = propertyUpdate;
            builder.TrackPropertyUpdate(propertyUpdate, property, properties);
        }
    }

    /// <summary>
    /// Adds the complete state of a subject without Registry metadata from its own property metadata. Processors
    /// cannot filter these properties and no attributes are added, since both are described by Registry metadata.
    /// </summary>
    private static void ProcessSubjectFromMetadata(
        IInterceptorSubject subject,
        Dictionary<string, SubjectPropertyUpdate> properties,
        SubjectUpdateBuilder builder)
    {
        SubjectUpdateDiagnostics.RecordMetadataFallbackSerialization();

        foreach (var (propertyName, metadata) in subject.Properties)
        {
            if (metadata.GetValue is null || IsComputedSubjectProjection(in metadata))
                continue;

            var update = new SubjectPropertyUpdate
            {
                Timestamp = new PropertyReference(subject, propertyName).TryGetWriteTimestamp()
            };

            BuildCurrentValue(update, metadata.Type, metadata.GetValue(subject), property: null, builder);
            properties[propertyName] = update;
        }
    }

    /// <summary>
    /// A derived property that holds subjects and has no setter is a computed projection: it owns nothing,
    /// so neither complete payloads nor changes publish it, and the applier ignores an update naming it.
    /// </summary>
    internal static bool IsComputedSubjectProjection(in SubjectPropertyMetadata metadata)
        => metadata is { IsDerived: true, SetValue: null } && metadata.Type.CanContainSubjects();

    /// <inheritdoc cref="IsComputedSubjectProjection(in SubjectPropertyMetadata)"/>
    internal static bool IsComputedSubjectProjection(RegisteredSubjectProperty property)
        => property.CanContainSubjects && IsComputedSubjectProjection(property.Reference.Metadata);

    private static void ProcessStructuralChange(
        SubjectPropertyChange change,
        RegisteredSubjectProperty property,
        SubjectUpdateBuilder builder)
    {
        if (builder.IsComplete(property.Parent.Subject))
        {
            // Its complete payload already states the current structure, with every member complete.
            return;
        }

        // Read as object: a typed read of the change storage throws for a struct-typed collection such as
        // ImmutableArray<T>, and the builders below normalize the raw value.
        var newValue = change.GetNewValue<object?>();
        var item = newValue as IInterceptorSubject;
        if (property.IsSubjectReference && item is not null && HasLeftGraph(item, property))
        {
            // The change that took it out of the graph again states the current value.
            return;
        }

        // Written into the update the property may already have, which can hold its attributes.
        var (update, parent) = GetOrCreatePropertyUpdate(property, builder);
        if (property.IsSubjectReference)
        {
            update.Kind = SubjectPropertyUpdateKind.Object;
            if (item is not null)
            {
                if (!ReferenceEquals(item, change.GetOldValue<object?>()))
                    StateMember(item, property, isChange: true, builder);

                update.Id = builder.GetOrCreateId(item);
            }
        }
        else
        {
            SubjectItemsUpdateFactory.BuildItems(update, property.IsSubjectDictionary, property,
                newValue, change.GetOldValue<object?>(), isChange: true, builder);
        }

        update.Timestamp = change.ChangedTimestamp;
        builder.TrackPropertyUpdate(update, property, parent);
    }

    private static void ProcessValueChange(
        SubjectPropertyChange change,
        RegisteredSubjectProperty property,
        SubjectUpdateBuilder builder)
    {
        var (update, parent) = GetOrCreatePropertyUpdate(property, builder);
        update.Kind = SubjectPropertyUpdateKind.Value;
        update.Value = change.GetNewValue<object?>();
        update.Timestamp = change.ChangedTimestamp;
        builder.TrackPropertyUpdate(update, property, parent);
    }

    /// <summary>
    /// Gets or creates the update of <paramref name="property"/> in its subject's entry, nested in the update
    /// of the property it is attached to for an attribute, together with the dictionary holding it.
    /// </summary>
    private static (SubjectPropertyUpdate Update, Dictionary<string, SubjectPropertyUpdate> Parent) GetOrCreatePropertyUpdate(
        RegisteredSubjectProperty property,
        SubjectUpdateBuilder builder)
    {
        var parent = property.IsAttribute
            ? GetOrCreatePropertyUpdate(property.GetAttributedProperty(), builder).Update.Attributes ??= []
            : builder.GetOrCreateProperties(builder.GetOrCreateId(property.Parent.Subject));

        var name = GetUpdateName(property);
        if (!parent.TryGetValue(name, out var update))
        {
            update = new SubjectPropertyUpdate();
            parent[name] = update;
        }

        return (update, parent);
    }

    private static string GetUpdateName(RegisteredSubjectProperty property)
        => property.IsAttribute ? property.AttributeMetadata.AttributeName : property.Name;

    /// <summary>
    /// Creates the update of the current value of <paramref name="property"/>.
    /// </summary>
    private static SubjectPropertyUpdate CreatePropertyUpdate(
        RegisteredSubjectProperty property,
        SubjectUpdateBuilder builder)
    {
        var update = new SubjectPropertyUpdate { Timestamp = property.Reference.TryGetWriteTimestamp() };
        BuildCurrentValue(update, property.Type, property.GetValue(), property, builder);
        update.Attributes = CreateAttributeUpdates(property, builder);
        return update;
    }

    /// <summary>
    /// States <paramref name="value"/>, the current value of a property of <paramref name="type"/>, which
    /// <paramref name="property"/> is unless the owner has no Registry metadata.
    /// </summary>
    private static void BuildCurrentValue(
        SubjectPropertyUpdate update,
        Type type,
        object? value,
        RegisteredSubjectProperty? property,
        SubjectUpdateBuilder builder)
    {
        if (type.IsSubjectReferenceType())
        {
            update.Kind = SubjectPropertyUpdateKind.Object;
            if (value is IInterceptorSubject item)
            {
                StateMember(item, property, isChange: false, builder);
                update.Id = builder.GetOrCreateId(item);
            }
        }
        else if (type.CanContainSubjects())
        {
            SubjectItemsUpdateFactory.BuildItems(update, type.IsSubjectDictionaryType(), property,
                value, previousValue: null, isChange: false, builder);
        }
        else
        {
            update.Kind = SubjectPropertyUpdateKind.Value;
            update.Value = value;
        }
    }

    /// <summary>
    /// Adds the complete state of <paramref name="item"/>, a subject <paramref name="property"/> newly holds,
    /// when the receiver may not hold it yet, and returns whether the item is stated at all. A change completes
    /// it, unless it is the subject owning the property, and leaves it out when it has no Registry metadata and
    /// the property no longer holds it: it left the graph after the change was captured. A complete payload of
    /// a partial update completes it only through its first published parent: held there before, it is known,
    /// and added there in this batch, it is completed where it was added. A payload built from metadata has no
    /// <paramref name="property"/>, which is never the first published parent.
    /// </summary>
    internal static bool StateMember(
        IInterceptorSubject item,
        RegisteredSubjectProperty? property,
        bool isChange,
        SubjectUpdateBuilder builder)
    {
        if (isChange)
        {
            if (ReferenceEquals(item, property!.Parent.Subject))
                return true;

            if (HasLeftGraph(item, property))
                return false;
        }
        else if (!builder.IsExpandedThrough(item, property))
        {
            return true;
        }

        ProcessSubjectComplete(item, builder);
        return true;
    }

    /// <summary>
    /// Whether <paramref name="item"/>, named by a captured change of <paramref name="property"/>, left the graph
    /// since: it has no Registry metadata and the property no longer holds it. One the property still holds is
    /// being attached.
    /// </summary>
    private static bool HasLeftGraph(IInterceptorSubject item, RegisteredSubjectProperty property)
        => item.TryGetRegisteredSubject() is null && !IsHeldBy(property, item);

    private static bool IsHeldBy(RegisteredSubjectProperty property, IInterceptorSubject item)
    {
        var value = property.GetValue();
        if (property.IsSubjectReference)
            return ReferenceEquals(value, item);

        if (value is null)
            return false;

        if (property.IsSubjectDictionary)
        {
            foreach (System.Collections.DictionaryEntry entry in SubjectValueConvert.ToSubjectDictionary(value))
            {
                if (ReferenceEquals(entry.Value, item))
                    return true;
            }

            return false;
        }

        var members = SubjectValueConvert.ToSubjectList(value);
        for (var i = 0; i < members.Count; i++)
        {
            if (ReferenceEquals(members[i], item))
                return true;
        }

        return false;
    }

    private static Dictionary<string, SubjectPropertyUpdate>? CreateAttributeUpdates(
        RegisteredSubjectProperty property,
        SubjectUpdateBuilder builder)
    {
        Dictionary<string, SubjectPropertyUpdate>? attributes = null;

        foreach (var attribute in property.Attributes)
        {
            if (!attribute.HasGetter || IsComputedSubjectProjection(attribute) || !builder.IsIncluded(attribute))
                continue;

            var attributeUpdate = CreatePropertyUpdate(attribute, builder);
            attributes ??= new Dictionary<string, SubjectPropertyUpdate>();
            attributes[attribute.AttributeMetadata.AttributeName] = attributeUpdate;
            builder.TrackPropertyUpdate(attributeUpdate, attribute, attributes);
        }

        return attributes;
    }
}
