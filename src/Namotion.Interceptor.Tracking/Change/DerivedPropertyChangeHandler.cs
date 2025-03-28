﻿using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Tracking.Change;

/// <summary>
/// Handles derived properties and triggers change events and recalculations when dependent properties are changed.
/// </summary>
public class DerivedPropertyChangeHandler : IReadInterceptor, IWriteInterceptor, ILifecycleHandler
{
    [ThreadStatic]
    private static Stack<HashSet<PropertyReference>>? _currentTouchedProperties;
    
    public void Attach(SubjectLifecycleChange change)
    {
        foreach (var property in change
            .Subject.Properties.Where(p => p.Value.IsDerived()))
        {
            var propertyReference = new PropertyReference(change.Subject, property.Key);

            TryStartRecordTouchedProperties();

            var result = property.Value.GetValue?.Invoke(change.Subject);
            propertyReference.SetLastKnownValue(result);

            StoreRecordedTouchedProperties(propertyReference);
            TouchProperty(propertyReference);
        }
    }

    public void Detach(SubjectLifecycleChange change)
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
            return result;
        
        lock (usedByProperties)
        {
            foreach (var usedByProperty in usedByProperties)
            {
                var oldValue = usedByProperty.GetLastKnownValue();

                TryStartRecordTouchedProperties();

                var newValue = usedByProperty
                    .Subject
                    .Properties[usedByProperty.Name]
                    .GetValue?
                    .Invoke(usedByProperty.Subject);

                StoreRecordedTouchedProperties(usedByProperty);
                TouchProperty(usedByProperty);

                usedByProperty.SetLastKnownValue(newValue);
                usedByProperty.SetWriteTimestamp(
                    context.Property.TryGetWriteTimestamp() 
                    ?? SubjectMutationContext.GetCurrentTimestamp());

                var executor = usedByProperty.Subject.Context as IInterceptorExecutor;
                executor?.SetProperty(usedByProperty.Subject, usedByProperty.Name, newValue, () => oldValue, delegate {});
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
