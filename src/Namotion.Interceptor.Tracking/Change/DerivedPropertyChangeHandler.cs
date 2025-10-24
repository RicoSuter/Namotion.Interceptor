using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Tracking.Change;

/// <summary>
/// Handles derived properties and triggers change events and recalculations when dependent properties are changed.
/// Requires LifecycleInterceptor to be added after this interceptor.
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

    public TProperty ReadProperty<TProperty>(ref PropertyReadContext context, ReadInterceptionDelegate<TProperty> next)
    {
        var result = next(ref context);
        TouchProperty(context.Property);
        return result;
    }

    public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
    {
        next(ref context);

        var usedByProperties = context.Property.GetUsedByProperties();
        if (usedByProperties.Count == 0) // TODO(ts): Here we have a potential race condition with not locking usedByProperties (find lock free solution)
        {
            return;
        }
        
        // Read timestamp from property which has been set by lifecycle interceptor before
        var timestamp = context.Property.TryGetWriteTimestamp() 
            ?? SubjectMutationContext.GetChangedTimestamp();

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
                usedByProperty.SetWriteTimestamp(timestamp);

                // Trigger change event (derived change has local process as source (null))
                SubjectMutationContext.ApplyChangesWithSource(null, () =>
                    usedByProperty.SetPropertyValueWithInterception(newValue, _ => oldValue, delegate { })
                );
            }
        }
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
}
