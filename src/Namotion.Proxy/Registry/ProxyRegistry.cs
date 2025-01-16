using Namotion.Proxy.Abstractions;
using Namotion.Proxy.Registry.Abstractions;
using System.Collections.Immutable;
using Namotion.Interception.Lifecycle.Abstractions;
using Namotion.Interceptor;

namespace Namotion.Proxy.Registry;

// TODO: Add lots of tests!

internal class ProxyRegistry : IProxyRegistry, ILifecycleHandler
{
    private readonly IInterceptorContext _context;
    private readonly Dictionary<IInterceptorSubject, RegisteredProxy> _knownProxies = new();

    public ProxyRegistry(IInterceptorContext context)
    {
        _context = context;
    }
    
    public IReadOnlyDictionary<IInterceptorSubject, RegisteredProxy> KnownProxies
    {
        get
        {
            lock (_knownProxies)
                return _knownProxies.ToImmutableDictionary();
        }
    }

    public void AddChild(LifecycleContext context)
    {
        lock (_knownProxies)
        {
            if (!_knownProxies.TryGetValue(context.Subject, out var metadata))
            {
                metadata = new RegisteredProxy(context.Subject, context.Subject
                    .Properties
                    .Select(p => new RegisteredProxyProperty(new PropertyReference(context.Subject, p.Key))
                    {
                        Type = p.Value.Type,
                        Attributes = p.Value.Attributes
                    }));

                _knownProxies[context.Subject] = metadata;
            }

            if (context.Property != default)
            {
                metadata.AddParent(context.Property);

                _knownProxies
                    .TryGetProperty(context.Property)?
                    .AddChild(new ProxyPropertyChild
                    {
                        Proxy = context.Subject,
                        Index = context.Index
                    });
            }

            foreach (var property in metadata.Properties)
            {
                foreach (var attribute in property.Value.Attributes.OfType<IProxyPropertyInitializer>())
                {
                    attribute.InitializeProperty(property.Value, context.Index, _context);
                }
            }
        }
    }

    public void RemoveChild(LifecycleContext context)
    {
        lock (_knownProxies)
        {
            if (context.ReferenceCount == 0)
            {
                if (context.Property != default)
                {
                    var metadata = _knownProxies[context.Subject];
                    metadata.RemoveParent(context.Property);

                    _knownProxies
                        .TryGetProperty(context.Property)?
                        .RemoveChild(new ProxyPropertyChild
                        {
                            Proxy = context.Subject,
                            Index = context.Index
                        });
                }

                _knownProxies.Remove(context.Subject);
            }
        }
    }
}
