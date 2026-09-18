using System.Runtime.ExceptionServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Performance;

namespace Namotion.Interceptor.Connectors.Updates.Internal;

/// <summary>
/// Applies SubjectUpdate instances to subjects, resolving subjects by stable ID.
/// </summary>
internal static class SubjectUpdateApplier
{
    private static readonly ObjectPool<SubjectUpdateApplyContext> ContextPool = new(() => new SubjectUpdateApplyContext());

    public static void ApplyUpdate(
        IInterceptorSubject subject,
        SubjectUpdate update,
        ISubjectFactory subjectFactory,
        ChangeOrigin origin,
        Action<RegisteredSubjectProperty, SubjectPropertyUpdate>? transformValueBeforeApply = null)
    {
        var context = ContextPool.Rent();
        List<(PropertyReference Property, Exception Exception)>? failures;
        List<(Type SubjectType, string PropertyName)>? droppedStructuralProperties;
        Exception? detachFailure = null;
        try
        {
            context.Initialize(subject, update, subjectFactory, origin, transformValueBeforeApply);

            // Keeps a subject that moves between properties within this update attached and registered, and so
            // resolvable by its ID.
            var batchScope = subject.Context.TryGetLifecycleInterceptor()?.CreateBatchScope(subject.Context);
            try
            {
                ApplySubjects(subject, update, context);
            }
            finally
            {
                try
                {
                    batchScope?.Dispose();
                }
                catch (Exception exception) when (context.Failures is not null)
                {
                    // A lifecycle handler failing on a deferred detach must not hide the property failures.
                    detachFailure = exception;
                }
            }

            failures = context.Failures;
            droppedStructuralProperties = context.DroppedStructuralProperties;
        }
        finally
        {
            context.Clear();
            ContextPool.Return(context);
        }

        if (droppedStructuralProperties is not null)
        {
            SubjectUpdateLog.TryGetWarningLogger(subject)?.LogWarning(
                "Dropped the incoming structure of the properties {DroppedProperties} while applying an update to " +
                "subject {SubjectType}. These properties have no setter, so the described children have nowhere to " +
                "be stored. Give them a setter, or construct the children in the receiving model itself.",
                SubjectUpdateLog.DescribeProperties(droppedStructuralProperties),
                subject.GetType().FullName);
        }

        if (failures is null)
        {
            return;
        }

        // A single failure is rethrown as itself, with its original stack, for a caller that catches a
        // specific exception type.
        if (failures.Count == 1 && detachFailure is null)
        {
            ExceptionDispatchInfo.Capture(failures[0].Exception).Throw();
        }

        var exceptions = failures.Select(failure => failure.Exception);
        throw new AggregateException(
            $"{failures.Count} property updates could not be applied: " +
            string.Join(", ", failures.Select(failure => failure.Property.Name)),
            detachFailure is null ? exceptions : exceptions.Append(detachFailure));
    }

    private static void ApplySubjects(IInterceptorSubject rootSubject, SubjectUpdate update, SubjectUpdateApplyContext context)
    {
        if (update.Root is not null)
        {
            // The sender's root ID is not the local root's, so a reference back to the root resolves only
            // through this binding.
            context.BindSubject(update.Root, rootSubject);
            context.ApplySubjectPayload(rootSubject, update.Root);
        }

        // An entry whose subject is not resolvable yet may be created by a structural property applied later.
        foreach (var (subjectId, properties) in update.Subjects)
        {
            if (context.TryResolveSubject(subjectId, out var subject) && context.TryMarkAsProcessed(subjectId))
            {
                ApplyPropertyUpdates(subject, subjectId, properties, context);
            }
        }

        // Before the entries are settled: a subject-holding attribute can create the subject an entry addresses.
        ApplyDeferredAttributeUpdates(context);

        foreach (var (subjectId, properties) in update.Subjects)
        {
            if (!context.TryMarkAsProcessed(subjectId))
                continue;

            if (context.TryResolveSubject(subjectId, out var subject))
            {
                ApplyPropertyUpdates(subject, subjectId, properties, context);
            }
            else if (!context.IsIgnored(subjectId))
            {
                // Neither held here nor created by this update. The next update carrying the subject's complete
                // state converges it.
                context.RecordDroppedSubject(subjectId);
            }
        }

        ApplyDeferredAttributeUpdates(context);
    }

    internal static void ApplyPropertyUpdates(
        IInterceptorSubject subject,
        string subjectId,
        Dictionary<string, SubjectPropertyUpdate> properties,
        SubjectUpdateApplyContext context)
    {
        RegisteredSubject? registeredSubject = null;
        foreach (var (propertyName, propertyUpdate) in properties)
        {
            if (propertyUpdate.Attributes is not null)
            {
                registeredSubject ??= subject.TryGetRegisteredSubject();
                if (registeredSubject is not null)
                {
                    ApplyAttributeUpdates(registeredSubject, propertyName, propertyUpdate.Attributes, context);
                }
                else
                {
                    // Attribute names resolve through the registry, which knows a created subject only once
                    // the update has rooted it.
                    context.DeferAttributeUpdates(subject, subjectId, propertyName, propertyUpdate.Attributes);
                }
            }

            ApplyPropertyUpdate(subject, propertyName, propertyUpdate, context);
        }
    }

    private static void ApplyAttributeUpdates(
        RegisteredSubject subject,
        string propertyName,
        Dictionary<string, SubjectPropertyUpdate> attributes,
        SubjectUpdateApplyContext context)
    {
        foreach (var (attributeName, attributeUpdate) in attributes)
        {
            var registeredAttribute = subject.TryGetPropertyAttribute(propertyName, attributeName);
            if (registeredAttribute is not null)
            {
                ApplyPropertyUpdate(subject.Subject, registeredAttribute.Name, attributeUpdate, context);
            }
            else
            {
                SkipUndeclaredProperty(attributeUpdate, context);
            }
        }
    }

    private static void ApplyDeferredAttributeUpdates(SubjectUpdateApplyContext context)
    {
        // Applying an attribute can create subjects that queue their own attribute updates.
        while (context.TryDequeueDeferredAttributeUpdates(out var entry))
        {
            if (entry.Subject.TryGetRegisteredSubject() is { } registeredSubject)
            {
                ApplyAttributeUpdates(registeredSubject, entry.PropertyName, entry.Attributes, context);
            }
            else
            {
                // The subject never entered the graph, so its attributes have no registered property to go to.
                context.RecordDroppedSubject(entry.SubjectId);
            }
        }
    }

    private static void SkipUndeclaredProperty(SubjectPropertyUpdate propertyUpdate, SubjectUpdateApplyContext context)
    {
        SubjectUpdateDiagnostics.RecordUnknownInboundProperty();
        context.IgnoreNamedSubjects(propertyUpdate);
    }

    /// <summary>
    /// Applies a single property update using the subject's own property metadata, which a subject has
    /// even while it is not registered, such as a created subject populated before it is rooted.
    /// </summary>
    private static void ApplyPropertyUpdate(
        IInterceptorSubject subject,
        string propertyName,
        SubjectPropertyUpdate propertyUpdate,
        SubjectUpdateApplyContext context)
    {
        if (!subject.Properties.TryGetValue(propertyName, out var metadata))
        {
            SkipUndeclaredProperty(propertyUpdate, context);
            return;
        }

        // No producer publishes a projection, and walking into one whose getter creates a subject on every
        // read would never end.
        if (SubjectUpdateFactory.IsComputedSubjectProjection(metadata))
        {
            context.IgnoreNamedSubjects(propertyUpdate);
            return;
        }

        var property = new PropertyReference(subject, propertyName);
        try
        {
            switch (propertyUpdate.Kind)
            {
                case SubjectPropertyUpdateKind.Value:
                    ApplyValueUpdate(property, in metadata, propertyUpdate, context);
                    break;

                case SubjectPropertyUpdateKind.Object:
                    ApplyObjectUpdate(property, in metadata, propertyUpdate, context);
                    break;

                case SubjectPropertyUpdateKind.Collection:
                    SubjectItemsUpdateApplier.ApplyCollectionUpdate(property, in metadata, propertyUpdate, context);
                    break;

                case SubjectPropertyUpdateKind.Dictionary:
                    SubjectItemsUpdateApplier.ApplyDictionaryUpdate(property, in metadata, propertyUpdate, context);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // Cancellation is not an apply failure. Let a shutdown unwind now rather than surfacing
            // at the end of the batch as though this property were bad.
            throw;
        }
        catch (Exception exception)
        {
            // One property must not cost its siblings: the failure is reported once the batch is done.
            context.RecordFailure(property, exception);
        }
    }

    private static void ApplyValueUpdate(
        PropertyReference property,
        in SubjectPropertyMetadata metadata,
        SubjectPropertyUpdate propertyUpdate,
        SubjectUpdateApplyContext context)
    {
        // A producer may publish a value this model computes itself, so it is dropped before it is even
        // converted rather than reported as a failure.
        if (metadata.SetValue is null)
        {
            return;
        }

        var registeredProperty = context.TransformValueBeforeApply is not null
            ? property.Subject.TryGetRegisteredProperty(property.Name)
            : null;

        if (context.TransformValueBeforeApply is not null && registeredProperty is not null)
        {
            // Convert once BEFORE the transform runs; this converted instance is the value the
            // source semantically sent and doubles as the origin's survival evidence. If the
            // transform does not replace propertyUpdate.Value (reference unchanged), reuse that
            // same instance as the written value too: converting a JSON value twice yields two
            // reference-distinct instances for reference types (int[], DTOs), which fail the
            // reference-equality survival check and wrongly demote a genuine unchanged source
            // write to Local, defeating echo suppression. Only re-convert when the transform
            // substituted a new value, so a locally corrected value differs from the evidence
            // and the origin correctly demotes to Local.
            var rawValue = propertyUpdate.Value;
            var sentValue = ConvertValue(rawValue, metadata.Type);
            context.TransformValueBeforeApply.Invoke(registeredProperty, propertyUpdate);
            var value = ReferenceEquals(propertyUpdate.Value, rawValue)
                ? sentValue
                : ConvertValue(propertyUpdate.Value, metadata.Type);
            context.SetPropertyValue(property, propertyUpdate.Timestamp, value, sentValue);
        }
        else
        {
            var value = ConvertValue(propertyUpdate.Value, metadata.Type);
            context.SetPropertyValue(property, propertyUpdate.Timestamp, value);
        }
    }

    private static void ApplyObjectUpdate(
        PropertyReference property,
        in SubjectPropertyMetadata metadata,
        SubjectPropertyUpdate propertyUpdate,
        SubjectUpdateApplyContext context)
    {
        if (metadata.SetValue is null)
        {
            ApplyObjectUpdateToHeldSubject(property, in metadata, propertyUpdate, context);
            return;
        }

        if (propertyUpdate.Id is not { } subjectId)
        {
            context.SetPropertyValue(property, propertyUpdate.Timestamp, null);
            return;
        }

        // Resolved from the subjects this apply bound and the registry, never from the backing store, which
        // a concurrent structural write may already have changed before its lifecycle processing.
        var existingSubject = ResolveSubject(subjectId, metadata.Type, context);
        if (existingSubject is null)
        {
            var createdSubject = context.TryCreateSubject(subjectId, metadata.Type);
            if (createdSubject is not null)
            {
                context.SetPropertyValue(property, propertyUpdate.Timestamp, createdSubject);
            }

            return;
        }

        context.SetPropertyValue(property, propertyUpdate.Timestamp, existingSubject);
        context.ApplySubjectPayload(existingSubject, subjectId);
    }

    /// <summary>
    /// Applies an object update to a property this model cannot write: the subject it holds takes the payload
    /// of the subject the update names, adopting its ID unless that ID names another subject here.
    /// </summary>
    private static void ApplyObjectUpdateToHeldSubject(
        PropertyReference property,
        in SubjectPropertyMetadata metadata,
        SubjectPropertyUpdate propertyUpdate,
        SubjectUpdateApplyContext context)
    {
        if (propertyUpdate.Id is not { } subjectId)
        {
            return;
        }

        var heldSubject = metadata.GetValue?.Invoke(property.Subject) as IInterceptorSubject;
        if (!context.TryResolveSubject(subjectId, out var subject) &&
            heldSubject is not null &&
            context.TryAdoptSubject(subjectId, heldSubject))
        {
            subject = heldSubject;
        }

        if (heldSubject is null || !ReferenceEquals(subject, heldSubject))
        {
            context.DropStructure(property, propertyUpdate);
            return;
        }

        context.ApplySubjectPayload(heldSubject, subjectId);
    }

    /// <summary>
    /// Resolves the subject an ID names, or returns <c>null</c> when it is unknown here.
    /// </summary>
    /// <exception cref="InvalidOperationException">The subject does not fit <paramref name="declaredType"/>.</exception>
    internal static IInterceptorSubject? ResolveSubject(string subjectId, Type declaredType, SubjectUpdateApplyContext context)
    {
        if (!context.TryResolveSubject(subjectId, out var subject))
        {
            return null;
        }

        if (!declaredType.IsInstanceOfType(subject))
        {
            throw new InvalidOperationException(
                $"The update names subject '{subjectId}', a {subject.GetType().FullName}, for a position of type {declaredType.FullName}.");
        }

        return subject;
    }

    internal static object? ConvertValue(object? value, Type targetType)
    {
        return value switch
        {
            null => null,
            JsonElement jsonElement => jsonElement.Deserialize(targetType),
            _ => value
        };
    }
}
