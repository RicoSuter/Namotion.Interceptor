using System.Collections.Immutable;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Registry;

// TODO: Add lots of tests!

internal class SubjectRegistry : ISubjectRegistry, ILifecycleHandler
{
    private readonly Lock _lock = new();
    private readonly Dictionary<IInterceptorSubject, RegisteredSubject> _knownSubjects = new();
    
    /// <summary>
    /// Gets all known registered subjects.
    /// </summary>
    public IReadOnlyDictionary<IInterceptorSubject, RegisteredSubject> KnownSubjects
    {
        get
        {
            lock (_knownSubjects)
                return _knownSubjects.ToImmutableDictionary();
        }
    }

    /// <summary>
    /// Callback which is called when a subject is attached .
    /// </summary>
    /// <param name="change"></param>
    /// <exception cref="InvalidOperationException">Changed property not found.</exception>
    void ILifecycleHandler.Attach(SubjectLifecycleChange change)
    {
        lock (_knownSubjects)
        {
            if (!_knownSubjects.TryGetValue(change.Subject, out var subject))
            {
                subject = RegisterSubject(change.Subject);
            }

            if (change.Property is not null)
            {
                if (!_knownSubjects.ContainsKey(change.Property.Value.Subject))
                {
                    // parent of property not yet registered
                    RegisterSubject(change.Property.Value.Subject);
                }

                var property = TryGetRegisteredProperty(change.Property.Value) ?? 
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

            foreach (var property in subject.Properties)
            {
                foreach (var attribute in property.Value.Attributes.OfType<ISubjectPropertyInitializer>())
                {
                    attribute.InitializeProperty(property.Value, change.Index);
                }
            }
        }
    }

    private RegisteredSubject RegisterSubject(IInterceptorSubject subject)
    {
        var registeredSubject = new RegisteredSubject(subject, subject
            .Properties
            .Select(p => new RegisteredSubjectProperty(new PropertyReference(subject, p.Key))
            {
                Type = p.Value.Type,
                Attributes = p.Value.Attributes
            }));

        _knownSubjects[subject] = registeredSubject;
        return registeredSubject;
    }

    void ILifecycleHandler.Detach(SubjectLifecycleChange change)
    {
        lock (_knownSubjects)
        {
            if (change.ReferenceCount == 0)
            {
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
            }
        }
    }
    
    private RegisteredSubjectProperty? TryGetRegisteredProperty(PropertyReference property)
    {
        if (_knownSubjects.TryGetValue(property.Subject, out var registeredSubject) &&
            registeredSubject.Properties.TryGetValue(property.Name, out var result))
        {
            return result;
        }

        return null;
    }

    public void EnqueueSubjectUpdate(Action update)
    {
        // TODO: Use this method in every property read/write to ensure thread safety
        
        lock (_lock)
        {
            update();
        }
    }
}
