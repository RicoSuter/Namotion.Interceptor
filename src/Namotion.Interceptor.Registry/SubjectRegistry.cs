using System.Collections.Immutable;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Registry;

// TODO: Add lots of tests!

internal class SubjectRegistry : ISubjectRegistry, ILifecycleHandler
{
    private readonly Dictionary<IInterceptorSubject, RegisteredSubject> _knownSubjects = new();
    
    public IReadOnlyDictionary<IInterceptorSubject, RegisteredSubject> KnownSubjects
    {
        get
        {
            lock (_knownSubjects)
                return _knownSubjects.ToImmutableDictionary();
        }
    }

    public void Attach(LifecycleContext context)
    {
        lock (_knownSubjects)
        {
            if (!_knownSubjects.TryGetValue(context.Subject, out var subject))
            {
                subject = RegisterSubject(context.Subject);
            }

            if (context.Property is not null)
            {
                if (!_knownSubjects.ContainsKey(context.Property.Value.Subject))
                {
                    // parent of property not yet registered
                    RegisterSubject(context.Property.Value.Subject);
                }

                var property = _knownSubjects
                    .TryGetRegisteredProperty(context.Property.Value) ?? 
                    throw new InvalidOperationException($"Property '{context.Property.Value.Name}' not found.");
                    
                subject
                    .AddParent(property);
                
                property
                    .AddChild(new SubjectPropertyChild
                    {
                        Index = context.Index,
                        Subject = context.Subject,
                    });
            }

            foreach (var property in subject.Properties)
            {
                foreach (var attribute in property.Value.Attributes.OfType<ISubjectPropertyInitializer>())
                {
                    attribute.InitializeProperty(property.Value, context.Index);
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

    public void Detach(LifecycleContext context)
    {
        lock (_knownSubjects)
        {
            if (context.ReferenceCount == 0)
            {
                if (context.Property is not null)
                {
                    var property = _knownSubjects.TryGetRegisteredProperty(context.Property.Value);
                    property?
                        .Parent
                        .RemoveParent(property);
                    
                    property?
                        .RemoveChild(new SubjectPropertyChild
                        {
                            Subject = context.Subject,
                            Index = context.Index
                        });
                }

                _knownSubjects.Remove(context.Subject);
            }
        }
    }
}
