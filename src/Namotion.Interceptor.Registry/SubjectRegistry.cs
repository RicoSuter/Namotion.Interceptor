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
            if (!_knownSubjects.TryGetValue(context.Subject, out var metadata))
            {
                metadata = RegisterSubject(context.Subject);
            }

            if (context.Property is not null)
            {
                metadata.AddParent(context.Property.Value);
                
                if (!_knownSubjects.ContainsKey(context.Property.Value.Subject))
                {
                    // parent of property not yet registered
                    RegisterSubject(context.Property.Value.Subject);
                }

                var property = _knownSubjects
                    .TryGetProperty(context.Property.Value) ?? 
                    throw new InvalidOperationException($"Property '{context.Property.Value.Name}' not found.");
                    
                property
                    .AddChild(new SubjectPropertyChild
                    {
                        Subject = context.Subject,
                        Index = context.Index
                    });
            }

            foreach (var property in metadata.Properties)
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
        var metadata = new RegisteredSubject(subject, subject
            .Properties
            .Select(p => new RegisteredSubjectProperty(new PropertyReference(subject, p.Key))
            {
                Type = p.Value.Type,
                Attributes = p.Value.Attributes
            }));

        _knownSubjects[subject] = metadata;
        return metadata;
    }

    public void Detach(LifecycleContext context)
    {
        lock (_knownSubjects)
        {
            if (context.ReferenceCount == 0)
            {
                if (context.Property is not null)
                {
                    var metadata = _knownSubjects[context.Subject];
                    metadata.RemoveParent(context.Property.Value);

                    _knownSubjects
                        .TryGetProperty(context.Property.Value)?
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
