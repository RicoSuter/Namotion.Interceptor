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
        List<string>? droppedStructuralProperties = null;
        try
        {
            context.Initialize(update.Subjects, subjectFactory, origin, transformValueBeforeApply);
            context.TryClaimSubjectPayload(update.Root, subject);
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

    /// <summary>
    /// Reports the properties whose structural payload was dropped. A dropped value converges the moment
    /// the receiver computes or receives it again, but a child the model cannot store never appears, so
    /// this is the only notice a reader gets.
    /// </summary>
    private static void WarnAboutDroppedStructure(IInterceptorSubject rootSubject, List<string> droppedProperties)
    {
        SubjectUpdateLog.TryGetWarningLogger(rootSubject)?.LogWarning(
            "Dropped the incoming structure of the properties {DroppedProperties} of subject {SubjectType} " +
            "because they have no setter, so the described children have nowhere to be stored. Give these " +
            "properties a setter, or construct the children in the receiving model itself.",
            string.Join(", ", droppedProperties), rootSubject.GetType().FullName);
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

        try
        {
            switch (propertyUpdate.Kind)
            {
                case SubjectPropertyUpdateKind.Value:
                {
                    // Whether a property is writable is a fact about this model rather than the
                    // producer's, and a producer may legitimately publish one for a receiver to display,
                    // so an unwritable property is an expected shape: it is dropped before the value is
                    // even converted rather than reported as a failure. The kinds below cannot drop the
                    // update this early, because a reference or container the receiver already holds
                    // still has to carry the nested payloads through to its subtree.
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
        if (propertyUpdate.Id is not null)
        {
            var itemProperties = context.GetSubjectProperties(propertyUpdate.Id);
            if (property.GetValue() is IInterceptorSubject existingItem)
            {
                if (context.TryClaimSubjectPayload(propertyUpdate.Id, existingItem))
                {
                    ApplyPropertyUpdates(existingItem, itemProperties, context);
                }
            }
            // An empty property this model cannot write has nowhere to put the subject, so nothing is
            // created and the ID stays unbound for whichever property can hold it.
            else if (!property.HasSetter)
            {
                context.RecordDroppedStructure(property);
            }
            else
            {
                // One ID is one subject within an update, so a reference to an ID another property
                // already bound points at that same subject rather than a second copy of it. This is
                // what carries a back reference to the root, whose payload the root itself applied.
                // Two properties of unrelated subject types may still name one ID, and the bound
                // instance then fits only one of them, so a type mismatch gets its own instance
                // instead of a write that throws.
                var newItem = context.TryGetBoundSubject(propertyUpdate.Id);
                if (newItem is null || !property.Type.IsInstanceOfType(newItem))
                {
                    newItem = context.SubjectFactory.CreateSubject(property);
                    newItem.Context.AddFallbackContext(parent.Context);

                    // Claiming before recursing is what terminates a payload that references itself.
                    if (context.TryClaimSubjectPayload(propertyUpdate.Id, newItem))
                    {
                        ApplyPropertyUpdates(newItem, itemProperties, context);
                    }
                }

                context.SetPropertyValue(property, propertyUpdate.Timestamp, newItem);
            }
        }
        else if (property.HasSetter)
        {
            context.SetPropertyValue(property, propertyUpdate.Timestamp, null);
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
