using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Registry;

public class SubjectRegistry : ISubjectRegistry, ILifecycleHandler, IReferenceLifecycleHandler, IPropertyLifecycleHandler
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
    void ILifecycleHandler.OnSubjectAttached(SubjectLifecycleChange change)
    {
        lock (_knownSubjects)
        {
            if (!_knownSubjects.ContainsKey(change.Subject))
            {
                RegisterSubject(change.Subject);
            }
        }
    }

    /// <inheritdoc />
    void ILifecycleHandler.OnSubjectDetached(SubjectLifecycleChange change)
    {
        lock (_knownSubjects)
        {
            _knownSubjects.Remove(change.Subject);
        }
    }

    /// <inheritdoc />
    void IReferenceLifecycleHandler.OnSubjectAttachedToProperty(SubjectLifecycleChange change)
    {
        lock (_knownSubjects)
        {
            if (!_knownSubjects.TryGetValue(change.Subject, out var subject))
            {
                subject = RegisterSubject(change.Subject);
            }

            if (!_knownSubjects.TryGetValue(change.Property!.Value.Subject, out var registeredSubject))
            {
                registeredSubject = RegisterSubject(change.Property.Value.Subject);
            }

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

    /// <inheritdoc />
    void IReferenceLifecycleHandler.OnSubjectDetachedFromProperty(SubjectLifecycleChange change)
    {
        lock (_knownSubjects)
        {
            var registeredSubject = _knownSubjects.GetValueOrDefault(change.Subject);

            var property = TryGetRegisteredProperty(change.Property!.Value);
            if (property is not null)
            {
                // Remove parent relationship from the child subject (if still tracked)
                registeredSubject?.RemoveParent(property, change.Index);

                // Remove child from the parent property's Children collection
                property.RemoveChild(new SubjectPropertyChild
                {
                    Subject = change.Subject,
                    Index = change.Index
                });
            }
        }
    }

    void IPropertyLifecycleHandler.OnPropertyAttached(SubjectPropertyLifecycleChange change)
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

    void IPropertyLifecycleHandler.OnPropertyDetached(SubjectPropertyLifecycleChange change)
    {
    }

    private RegisteredSubject RegisterSubject(IInterceptorSubject subject)
    {
        var registeredSubject = new RegisteredSubject(subject);
        _knownSubjects[subject] = registeredSubject;
        return registeredSubject;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private RegisteredSubjectProperty? TryGetRegisteredProperty(PropertyReference property)
    {
        return TryGetRegisteredSubject(property.Subject)?.TryGetProperty(property.Name);
    }
}
