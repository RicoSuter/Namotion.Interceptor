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
        var property = TryGetRegisteredSubject(change.Property.Subject)?.TryGetProperty(change.Property.Name);
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
        var registeredSubject = new RegisteredSubject(subject);
        _knownSubjects[subject] = registeredSubject;
        return registeredSubject;
    }

    void ILifecycleHandler.DetachSubject(SubjectLifecycleChange change)
    {
        if (change is { ReferenceCount: 0, Property: not null })
        {
            lock (_knownSubjects)
            {
                // TODO(perf): Use concurrent dictionary in registry?

                var property = change.Property.Value;
                if (_knownSubjects.TryGetValue(property.Subject, out var subject))
                {
                    var registeredProperty = subject.TryGetProperty(property.Name);
                    registeredProperty?
                        .Parent
                        .RemoveParent(registeredProperty, change.Index);
                    
                    registeredProperty?
                        .RemoveChild(new SubjectPropertyChild
                        {
                            Subject = change.Subject,
                            Index = change.Index
                        });
                }

                _knownSubjects.Remove(change.Subject);
            }
        }
    }

    void IPropertyLifecycleHandler.DetachProperty(SubjectPropertyLifecycleChange change)
    {
    }
}