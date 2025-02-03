using System.Collections.Immutable;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Registry;

// TODO: Add lots of tests!

internal class SubjectRegistry : ISubjectRegistry, ILifecycleHandler
{
    private readonly Dictionary<IInterceptorSubject, RegisteredSubject> _knownProxies = new();
    
    public IReadOnlyDictionary<IInterceptorSubject, RegisteredSubject> KnownSubjects
    {
        get
        {
            lock (_knownProxies)
                return _knownProxies.ToImmutableDictionary();
        }
    }

    public void Attach(LifecycleContext context)
    {
        lock (_knownProxies)
        {
            if (!_knownProxies.TryGetValue(context.Subject, out var metadata))
            {
                metadata = new RegisteredSubject(context.Subject, context.Subject
                    .Properties
                    .Select(p => new RegisteredSubjectProperty(new PropertyReference(context.Subject, p.Key))
                    {
                        Type = p.Value.Type,
                        Attributes = p.Value.Attributes
                    }));

                _knownProxies[context.Subject] = metadata;
            }

            if (context.Property is not null)
            {
                metadata.AddParent(context.Property.Value);

                _knownProxies
                    .TryGetProperty(context.Property.Value)?
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

    public void Detach(LifecycleContext context)
    {
        lock (_knownProxies)
        {
            if (context.ReferenceCount == 0)
            {
                if (context.Property is not null)
                {
                    var metadata = _knownProxies[context.Subject];
                    metadata.RemoveParent(context.Property.Value);

                    _knownProxies
                        .TryGetProperty(context.Property.Value)?
                        .RemoveChild(new SubjectPropertyChild
                        {
                            Subject = context.Subject,
                            Index = context.Index
                        });
                }

                _knownProxies.Remove(context.Subject);
            }
        }
    }
}
