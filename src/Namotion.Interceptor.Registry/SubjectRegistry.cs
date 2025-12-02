using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Registry;

public class SubjectRegistry : ISubjectRegistry, ILifecycleHandler, IPropertyLifecycleHandler
{
    private readonly ConcurrentDictionary<IInterceptorSubject, RegisteredSubject> _knownSubjects = new();
    private readonly Lock _writeLock = new();

    /// <inheritdoc />
    public IReadOnlyDictionary<IInterceptorSubject, RegisteredSubject> KnownSubjects => _knownSubjects;

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RegisteredSubject? TryGetRegisteredSubject(IInterceptorSubject subject)
    {
        return _knownSubjects.GetValueOrDefault(subject);
    }

    /// <inheritdoc />
    void ILifecycleHandler.AttachSubject(SubjectLifecycleChange change)
    {
        lock (_writeLock)
        {
            var subject = _knownSubjects.GetOrAdd(change.Subject, RegisteredSubject.Create);

            if (change.Property is not null)
            {
                var registeredSubject = _knownSubjects.GetOrAdd(
                    change.Property.Value.Subject,
                    RegisteredSubject.Create);

                var property = registeredSubject.TryGetProperty(change.Property.Value.Name) ??
                    throw new InvalidOperationException($"Property '{change.Property.Value.Name}' not found.");

                subject.AddParent(property, change.Index);

                property.AddChild(new SubjectPropertyChild
                {
                    Index = change.Index,
                    Subject = change.Subject,
                });
            }
        }
    }

    void IPropertyLifecycleHandler.AttachProperty(SubjectPropertyLifecycleChange change)
    {
        var property = TryGetRegisteredProperty(change.Property);
        if (property is not null)
        {
            // handle property initializers from attributes
            foreach (var attribute in property.ReflectionAttributes.OfType<ISubjectPropertyInitializer>())
            {
                attribute.InitializeProperty(property);
            }

            // handle property initializers from context
            foreach (var initializer in change.Subject.Context.GetServices<ISubjectPropertyInitializer>())
            {
                initializer.InitializeProperty(property);
            }
        }
    }

    void ILifecycleHandler.DetachSubject(SubjectLifecycleChange change)
    {
        lock (_writeLock)
        {
            var registeredSubject = _knownSubjects.GetValueOrDefault(change.Subject);
            if (registeredSubject is null)
            {
                return;
            }

            // Always update parent-child relationships when a property reference is removed,
            // regardless of reference count. This ensures the Children collection stays accurate
            // even when the subject has multiple references from different properties.
            if (change.Property is not null)
            {
                var property = TryGetRegisteredProperty(change.Property.Value);
                if (property is not null)
                {
                    // Remove parent relationship from the child subject (only when it is returned anyway)
                    if (change.ReferenceCount != 0)
                    {
                        registeredSubject.RemoveParent(property, change.Index);
                    }

                    // Remove child from the parent property's Children collection
                    property.RemoveChild(new SubjectPropertyChild
                    {
                        Subject = change.Subject,
                        Index = change.Index
                    });
                }
            }

            // Only remove the subject from the registry when its reference count reaches zero
            if (change.ReferenceCount == 0)
            {
                _knownSubjects.TryRemove(change.Subject, out _);
                registeredSubject.Return();
            }
        }
    }

    void IPropertyLifecycleHandler.DetachProperty(SubjectPropertyLifecycleChange change)
    {
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private RegisteredSubjectProperty? TryGetRegisteredProperty(PropertyReference property)
    {
        return TryGetRegisteredSubject(property.Subject)?.TryGetProperty(property.Name);
    }
}
