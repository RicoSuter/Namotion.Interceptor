using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Tracking.Change;

/// <summary>
/// Handles derived properties and triggers change events and recalculations when dependent properties are changed.
/// </summary>
public class DerivedPropertyChangeHandler : IReadInterceptor, IWriteInterceptor, IPropertyLifecycleHandler
{
    [ThreadStatic]
    private static Stack<HashSet<PropertyReference>>? _currentTouchedProperties;
    
    public void AttachProperty(SubjectPropertyLifecycleChange change)
    {
        if (change.Property.Metadata.IsDerived)
        {
            TryStartRecordTouchedProperties();

            var result = change.Property.Metadata.GetValue?.Invoke(change.Subject);
            change.Property.SetLastKnownValue(result);

            StoreRecordedTouchedProperties(change.Property);
            TouchProperty(change.Property);
        }
    }

    public void DetachProperty(SubjectPropertyLifecycleChange change)
    {
    }

    public object? ReadProperty(ReadPropertyInterception context, Func<ReadPropertyInterception, object?> next)
    {
        var result = next(context);
        TouchProperty(context.Property);
        return result;
    }

    public object? WriteProperty(WritePropertyInterception context, Func<WritePropertyInterception, object?> next)
    {
        var result = next.Invoke(context);

        var usedByProperties = context.Property.GetUsedByProperties();
        if (usedByProperties.Count == 0)
        {
            return result;
        }

        lock (usedByProperties)
        {
            foreach (var usedByProperty in usedByProperties)
            {
                if (usedByProperty == context.Property)
                {
                    continue;
                }
                
                var oldValue = usedByProperty.GetLastKnownValue();

                TryStartRecordTouchedProperties();

                var newValue = usedByProperty.Metadata.GetValue?.Invoke(usedByProperty.Subject);

                StoreRecordedTouchedProperties(usedByProperty);
                TouchProperty(usedByProperty);

                usedByProperty.SetLastKnownValue(newValue);
                usedByProperty.SetWriteTimestamp(
                    context.Property.TryGetWriteTimestamp() 
                    ?? SubjectMutationContext.GetCurrentTimestamp());

                // trigger change event (derived change has local process as source (null))
                SubjectMutationContext.ApplyChangesWithSource(null, () =>
                    usedByProperty.SetPropertyValueWithInterception(newValue, () => oldValue, delegate {})
                );
            }
        }

        return result;
    }

    private static void TryStartRecordTouchedProperties()
    {
        _currentTouchedProperties ??= new Stack<HashSet<PropertyReference>>();
        _currentTouchedProperties.Push([]);
    }

    private static void StoreRecordedTouchedProperties(PropertyReference property)
    {
        var newProperties = _currentTouchedProperties!.Pop();

        var previouslyRequiredProperties = property.GetRequiredProperties();
        foreach (var previouslyRequiredProperty in previouslyRequiredProperties.Except(newProperties))
        {
            var usedByProperties = previouslyRequiredProperty.GetUsedByProperties();
            lock (usedByProperties)
                usedByProperties.Remove(previouslyRequiredProperty);
        }

        property.SetRequiredProperties(newProperties);

        foreach (var newlyRequiredProperty in newProperties.Except(previouslyRequiredProperties))
        {
            var usedByProperties = newlyRequiredProperty.GetUsedByProperties();
            lock (usedByProperties)
                usedByProperties.Add(new PropertyReference(property.Subject, property.Name));
        }
    }

    private static void TouchProperty(PropertyReference property)
    {
        if (_currentTouchedProperties?.TryPeek(out var touchedProperties) == true)
        {
            touchedProperties.Add(property);
        }
        else
        {
            _currentTouchedProperties = null;
        }
    }
    
    public void AttachTo(IInterceptorSubject subject)
    {
    }

    public void DetachFrom(IInterceptorSubject subject)
    {
    }
}
