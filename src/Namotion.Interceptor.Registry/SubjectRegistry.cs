using System.Collections.Immutable;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Registry;

// TODO: Add lots of tests!

internal class SubjectRegistry : ISubjectRegistry, ILifecycleHandler
{
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
    /// <param name="update"></param>
    /// <exception cref="InvalidOperationException"></exception>
    void ILifecycleHandler.Attach(SubjectLifecycleUpdate update)
    {
        lock (_knownSubjects)
        {
            if (!_knownSubjects.TryGetValue(update.Subject, out var subject))
            {
                subject = RegisterSubject(update.Subject);
            }

            if (update.Property is not null)
            {
                if (!_knownSubjects.ContainsKey(update.Property.Value.Subject))
                {
                    // parent of property not yet registered
                    RegisterSubject(update.Property.Value.Subject);
                }

                var property = TryGetRegisteredProperty(update.Property.Value) ?? 
                    throw new InvalidOperationException($"Property '{update.Property.Value.Name}' not found.");
                    
                subject
                    .AddParent(property);
                
                property
                    .AddChild(new SubjectPropertyChild
                    {
                        Index = update.Index,
                        Subject = update.Subject,
                    });
            }

            foreach (var property in subject.Properties)
            {
                foreach (var attribute in property.Value.Attributes.OfType<ISubjectPropertyInitializer>())
                {
                    attribute.InitializeProperty(property.Value, update.Index);
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

    void ILifecycleHandler.Detach(SubjectLifecycleUpdate update)
    {
        lock (_knownSubjects)
        {
            if (update.ReferenceCount == 0)
            {
                if (update.Property is not null)
                {
                    var property = TryGetRegisteredProperty(update.Property.Value);
                    property?
                        .Parent
                        .RemoveParent(property);
                    
                    property?
                        .RemoveChild(new SubjectPropertyChild
                        {
                            Subject = update.Subject,
                            Index = update.Index
                        });
                }

                _knownSubjects.Remove(update.Subject);
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
}
