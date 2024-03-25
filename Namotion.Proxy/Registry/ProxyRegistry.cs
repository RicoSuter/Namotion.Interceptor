using Namotion.Proxy.Abstractions;
using Namotion.Proxy.Registry.Abstractions;
using System.Collections.Immutable;
using System.Reactive.Subjects;
using System.Reflection;

namespace Namotion.Proxy.Registry;

// TODO: Add lots of tests!

internal class ProxyRegistry : IProxyRegistry, IProxyLifecycleHandler
{
    private Subject<ProxyPropertyChanged> _subject = new();
    private Dictionary<IProxy, ProxyMetadata> _knownProxies = new();

    public IReadOnlyDictionary<IProxy, ProxyMetadata> KnownProxies
    {
        get
        {
            lock (_knownProxies)
                return _knownProxies.ToImmutableDictionary();
        }
    }

    public void OnProxyAttached(ProxyLifecycleContext context)
    {
        lock (_knownProxies)
        {
            if (!_knownProxies.TryGetValue(context.Proxy, out var metadata))
            {
                metadata = new ProxyMetadata
                {
                    Proxy = context.Proxy
                };

                foreach (var p in context.Proxy.Properties)
                {
                    metadata.AddProperty(p.Key,
                        p.Value.Info.PropertyType,
                        p.Value.GetValue is not null ? () => p.Value.GetValue.Invoke(context.Proxy) : null,
                        p.Value.SetValue is not null ? (value) => p.Value.SetValue.Invoke(context.Proxy, value) : null,
                        p.Value.Info.GetCustomAttributes().ToArray());
                }

                _knownProxies[context.Proxy] = metadata;
            }

            if (context.Property != default)
            {
                metadata.AddParent(context.Property);

                _knownProxies
                    .TryGetProperty(context.Property)?
                    .AddChild(new ProxyPropertyChild
                    {
                        Proxy = context.Proxy,
                        Index = context.Index
                    });
            }

            foreach (var property in metadata.Properties.ToArray())
            {
                foreach (var attribute in property.Value.Attributes.OfType<IProxyPropertyInitializer>())
                {
                    attribute.InitializeProperty(property.Value, context.Index, context.Context);
                }
            }
        }
    }

    public void OnProxyDetached(ProxyLifecycleContext context)
    {
        lock (_knownProxies)
        {
            if (context.ReferenceCount == 0)
            {
                if (context.Property != default)
                {
                    var metadata = _knownProxies[context.Proxy];
                    metadata.RemoveParent(context.Property);

                    _knownProxies
                        .TryGetProperty(context.Property)?
                        .RemoveChild(new ProxyPropertyChild
                        {
                            Proxy = context.Proxy,
                            Index = context.Index
                        });
                }

                _knownProxies.Remove(context.Proxy);
            }
        }
    }
}
