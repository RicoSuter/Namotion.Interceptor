using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Registry;

public class SubjectRegistry : ISubjectRegistry, ILifecycleHandler, IPropertyLifecycleHandler
{
    private readonly Lock _lock = new();
    private readonly Dictionary<IInterceptorSubject, RegisteredSubject> _knownSubjects = new();
    
    /// <inheritdoc />
    public IReadOnlyDictionary<IInterceptorSubject, RegisteredSubject> KnownSubjects
    {
        get
        {
            lock (_knownSubjects)
                return _knownSubjects.ToImmutableDictionary();
        }
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RegisteredSubject? TryGetRegisteredSubject(IInterceptorSubject subject)
    {
        lock (_knownSubjects)
        {
            return _knownSubjects.GetValueOrDefault(subject);
        }
    }

    /// <inheritdoc />
    public void ExecuteSubjectUpdate(Action update)
    {
        // TODO: Use this method in every property read/write to ensure thread safety
        lock (_lock)
        {
            update();
        }
    }

    /// <inheritdoc />
    void ILifecycleHandler.AttachSubject(SubjectLifecycleChange change)
    {
        lock (_knownSubjects)
        {
            if (!_knownSubjects.TryGetValue(change.Subject, out var subject))
            {
                subject = RegisterSubject(change.Subject);
            }

            if (change.Property is not null)
            {
                if (!_knownSubjects.TryGetValue(change.Property.Value.Subject, out var registeredSubject))
                {
                    registeredSubject = RegisterSubject(change.Property.Value.Subject);
                }

                var property = registeredSubject.TryGetProperty(change.Property.Value.Name) ?? 
                    throw new InvalidOperationException($"Property '{change.Property.Value.Name}' not found.");
                    
                subject
                    .AddParent(property, change.Index);
                
                property
                    .AddChild(new SubjectPropertyChild
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

    private RegisteredSubject RegisterSubject(IInterceptorSubject subject)
    {
        var registeredSubject = RegisteredSubject.Create(subject);
        _knownSubjects[subject] = registeredSubject;
        return registeredSubject;
    }

    void ILifecycleHandler.DetachSubject(SubjectLifecycleChange change)
    {
        lock (_knownSubjects)
        {
            if (change.ReferenceCount == 0)
            {
                var registeredSubject = TryGetRegisteredSubject(change.Subject);
                if (registeredSubject is null)
                {
                    return;
                }

                if (change.Property is not null)
                {
                    var property = TryGetRegisteredProperty(change.Property.Value);
                    property?
                        .Parent
                        .RemoveParent(property, change.Index);
                    
                    property?
                        .RemoveChild(new SubjectPropertyChild
                        {
                            Subject = change.Subject,
                            Index = change.Index
                        });
                }

                _knownSubjects.Remove(change.Subject);
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
