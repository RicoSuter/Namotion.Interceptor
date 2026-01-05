using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Registry;

public class SubjectRegistry : ISubjectRegistry, ILifecycleHandler, IPropertyLifecycleHandler
{
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
    void ILifecycleHandler.OnLifecycleEvent(SubjectLifecycleChange change)
    {
        lock (_knownSubjects)
        {
            if (change.IsContextAttach || change.IsPropertyReferenceAdded)
            {
                if (!_knownSubjects.TryGetValue(change.Subject, out var registeredSubject))
                {
                    registeredSubject = RegisterSubject(change.Subject);
                }

                if (change is { IsPropertyReferenceAdded: true, Property: not null })
                {
                    if (!_knownSubjects.TryGetValue(change.Property.Value.Subject, out var parentRegisteredSubject))
                    {
                        parentRegisteredSubject = RegisterSubject(change.Property.Value.Subject);
                    }

                    var property = parentRegisteredSubject.TryGetProperty(change.Property.Value.Name) ??
                        throw new InvalidOperationException($"Property '{change.Property.Value.Name}' not found.");

                    registeredSubject.AddParent(property, change.Index);
                    property.AddChild(new SubjectPropertyChild
                    {
                        Index = change.Index,
                        Subject = change.Subject,
                    });
                }

                return;
            }

            if (change.IsPropertyReferenceRemoved)
            {
                var registeredSubject = _knownSubjects.GetValueOrDefault(change.Subject);
                if (registeredSubject is not null && change.Property is not null)
                {
                    var property = _knownSubjects
                        .GetValueOrDefault(change.Property.Value.Subject)?
                        .TryGetProperty(change.Property.Value.Name);

                    if (property is not null)
                    {
                        registeredSubject.RemoveParent(property, change.Index);
                        property.RemoveChild(new SubjectPropertyChild
                        {
                            Subject = change.Subject,
                            Index = change.Index
                        });
                    }
                }
            }
            
            if (change.IsContextDetach)
            {
                _knownSubjects.Remove(change.Subject);
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
        var registeredSubject = new RegisteredSubject(subject);
        _knownSubjects[subject] = registeredSubject;
        return registeredSubject;
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
