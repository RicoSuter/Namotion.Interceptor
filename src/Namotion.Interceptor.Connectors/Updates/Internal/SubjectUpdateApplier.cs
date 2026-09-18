using System.Runtime.ExceptionServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Performance;

namespace Namotion.Interceptor.Connectors.Updates.Internal;

/// <summary>
/// Applies SubjectUpdate instances to subjects.
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
        if (string.IsNullOrEmpty(update.Root))
            return;

        if (!update.Subjects.TryGetValue(update.Root, out var rootProperties))
            return;

        var context = ContextPool.Rent();
        List<(RegisteredSubjectProperty Property, Exception Exception)>? failures = null;
        List<(Type SubjectType, string PropertyName)>? droppedStructuralProperties = null;
        try
        {
            context.Initialize(update.Subjects, subjectFactory, origin, transformValueBeforeApply);

            // Binds the root's ID, so a back reference to the root resolves to this subject.
            context.ClaimSubjectPayload(update.Root, subject);
            ApplyPropertyUpdates(subject, rootProperties, context);
            failures = context.Failures;
            droppedStructuralProperties = context.DroppedStructuralProperties;
        }
        finally
        {
            context.Clear();
            ContextPool.Return(context);
        }

        // Reported before the failures are thrown, so a batch that also failed somewhere still says which
        // children it could not hold.
        if (droppedStructuralProperties is not null)
        {
            WarnAboutDroppedStructure(subject, droppedStructuralProperties);
        }

        if (failures is null)
        {
            return;
        }

        // A single failure is rethrown as itself, with its original stack, so a caller that catches a
        // specific exception type keeps working exactly as before this change.
        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(failures[0].Exception).Throw();
        }

        throw new AggregateException(
            $"{failures.Count} property updates could not be applied: " +
            string.Join(", ", failures.Select(failure => failure.Property.Name)),
            failures.Select(failure => failure.Exception));
    }

    private static void WarnAboutDroppedStructure(
        IInterceptorSubject rootSubject, List<(Type SubjectType, string PropertyName)> droppedProperties)
    {
        SubjectUpdateLog.TryGetWarningLogger(rootSubject)?.LogWarning(
            "Dropped the incoming structure of the properties {DroppedProperties} while applying an update to " +
            "subject {SubjectType}. These properties have no setter, so the described children have nowhere to " +
            "be stored. Give them a setter, or construct the children in the receiving model itself.",
            string.Join(", ", droppedProperties.Select(droppedProperty =>
                $"{droppedProperty.SubjectType.Name}.{droppedProperty.PropertyName}")),
            rootSubject.GetType().FullName);
    }

    internal static void ApplyPropertyUpdates(
        IInterceptorSubject subject,
        Dictionary<string, SubjectPropertyUpdate> properties,
        SubjectUpdateApplyContext context)
    {
        var registry = subject.Context.GetService<ISubjectRegistry>();

        foreach (var (propertyName, propertyUpdate) in properties)
        {
            // Apply attributes first
            if (propertyUpdate.Attributes is not null)
            {
                foreach (var (attributeName, attributeUpdate) in propertyUpdate.Attributes)
                {
                    var registeredAttribute = subject.TryGetRegisteredSubject()?
                        .TryGetPropertyAttribute(propertyName, attributeName);

                    if (registeredAttribute is not null)
                    {
                        ApplyPropertyUpdate(subject, registeredAttribute.Name, attributeUpdate, context, registry);
                    }
                }
            }

            ApplyPropertyUpdate(subject, propertyName, propertyUpdate, context, registry);
        }
    }

    private static void ApplyPropertyUpdate(
        IInterceptorSubject subject,
        string propertyName,
        SubjectPropertyUpdate propertyUpdate,
        SubjectUpdateApplyContext context,
        ISubjectRegistry? registry)
    {
        var registeredProperty = subject.TryGetRegisteredProperty(propertyName, registry);
        if (registeredProperty is null)
            return;

        // No producer publishes a projection, and walking into one whose getter creates a subject on every
        // read would never end. A value update needs no check, as it already skips a property without a setter.
        if (propertyUpdate.Kind != SubjectPropertyUpdateKind.Value &&
            SubjectUpdateFactory.IsComputedSubjectProjection(registeredProperty))
        {
            return;
        }

        try
        {
            switch (propertyUpdate.Kind)
            {
                case SubjectPropertyUpdateKind.Value:
                {
                    // A producer may publish a property this model cannot write, so its value is dropped
                    // before it is even converted rather than reported as a failure. The kinds below must
                    // not drop this early: a reference or container the receiver already holds still
                    // carries the nested payloads to its subtree.
                    if (!registeredProperty.HasSetter)
                        break;

                    if (context.TransformValueBeforeApply is not null)
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
                        var sentValue = ConvertValue(rawValue, registeredProperty.Type);
                        context.TransformValueBeforeApply.Invoke(registeredProperty, propertyUpdate);
                        var value = ReferenceEquals(propertyUpdate.Value, rawValue)
                            ? sentValue
                            : ConvertValue(propertyUpdate.Value, registeredProperty.Type);
                        context.SetPropertyValue(registeredProperty, propertyUpdate.Timestamp, value, sentValue);
                    }
                    else
                    {
                        var value = ConvertValue(propertyUpdate.Value, registeredProperty.Type);
                        context.SetPropertyValue(registeredProperty, propertyUpdate.Timestamp, value);
                    }
                    break;
                }

                case SubjectPropertyUpdateKind.Object:
                    ApplyObjectUpdate(subject, registeredProperty, propertyUpdate, context);
                    break;

                case SubjectPropertyUpdateKind.Collection:
                    SubjectItemsUpdateApplier.ApplyCollectionUpdate(subject, registeredProperty, propertyUpdate, context);
                    break;

                case SubjectPropertyUpdateKind.Dictionary:
                    SubjectItemsUpdateApplier.ApplyDictionaryUpdate(subject, registeredProperty, propertyUpdate, context);
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
            // One property must not cost its siblings. Every other inbound path already catches per
            // property; this applier was the only one that abandoned the rest of the batch.
            context.RecordFailure(registeredProperty, exception);
        }
    }

    private static void ApplyObjectUpdate(
        IInterceptorSubject parent,
        RegisteredSubjectProperty property,
        SubjectPropertyUpdate propertyUpdate,
        SubjectUpdateApplyContext context)
    {
        if (propertyUpdate.Id is not { } subjectId)
        {
            // Like a value this model cannot write, a clear it cannot store is ignored rather than reported.
            if (property.HasSetter)
            {
                context.SetPropertyValue(property, propertyUpdate.Timestamp, null);
            }

            return;
        }

        var itemProperties = context.GetSubjectProperties(subjectId);

        // A replaced reference names another subject than the one held there, so that one never takes the payload.
        if (propertyUpdate.Mode != SubjectPropertyUpdateMode.Replaced &&
            property.GetValue() is IInterceptorSubject existingItem &&
            TryApplyToHeldSubject(subjectId, existingItem, itemProperties, context))
        {
            return;
        }

        // A property this model cannot write has nowhere to put the subject, so nothing is created and the
        // ID stays unbound for whichever property can hold it.
        if (!property.HasSetter)
        {
            context.RecordDroppedStructure(property);
            return;
        }

        var newItem = context.TryGetBoundSubject(subjectId, property.Type);
        if (newItem is null)
        {
            newItem = context.SubjectFactory.CreateSubject(property);
            newItem.Context.AddFallbackContext(parent.Context);

            // Claiming before recursing is what terminates a payload that references itself.
            if (context.ClaimSubjectPayload(subjectId, newItem, property.Type) == SubjectPayloadClaim.Claimed)
            {
                ApplyPropertyUpdates(newItem, itemProperties, context);
            }
        }

        context.SetPropertyValue(property, propertyUpdate.Timestamp, newItem);
    }

    /// <summary>
    /// Applies the payload of an ID to the subject a position already holds, and returns whether that
    /// subject stays at the position. It does not when it already took another ID's payload in this update,
    /// because two IDs are two source subjects.
    /// </summary>
    internal static bool TryApplyToHeldSubject(
        string subjectId,
        IInterceptorSubject heldSubject,
        Dictionary<string, SubjectPropertyUpdate> properties,
        SubjectUpdateApplyContext context)
    {
        switch (context.ClaimSubjectPayload(subjectId, heldSubject))
        {
            case SubjectPayloadClaim.Claimed:
                ApplyPropertyUpdates(heldSubject, properties, context);
                return true;

            case SubjectPayloadClaim.AlreadyClaimed:
                return true;

            default:
                return false;
        }
    }

    private static object? ConvertValue(object? value, Type targetType)
    {
        return value switch
        {
            null => null,
            JsonElement jsonElement => jsonElement.Deserialize(targetType),
            _ => value
        };
    }
}
