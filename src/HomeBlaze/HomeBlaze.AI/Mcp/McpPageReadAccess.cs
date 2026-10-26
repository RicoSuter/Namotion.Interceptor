using Namotion.Interceptor;
using Namotion.Interceptor.Mcp;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace HomeBlaze.AI.Mcp;

/// <summary>
/// The read check of page expressions under the access policy of a tool configuration: a property may be read when
/// its path from the root resolves back to it through the readable resolution of the tools, so every property on the
/// path must be readable and every subject it passes shown, as for get_property. Unlike the tools, every member is
/// named on a path, so that an expression may read a member the tools do not list, such as a derived title.
/// </summary>
internal sealed class McpPageReadAccess
{
    private readonly McpServerConfiguration _configuration;
    private readonly PathProviderBase _pathProvider;

    private McpPageReadAccess(McpServerConfiguration configuration, PathProviderBase pathProvider)
    {
        _configuration = configuration;
        _pathProvider = pathProvider;
    }

    /// <summary>
    /// Creates the check of the access policy of <paramref name="configuration"/>; null without a policy, which reads
    /// every property.
    /// </summary>
    /// <exception cref="InvalidOperationException">The path provider of the configuration does not extend <see cref="PathProviderBase"/>.</exception>
    public static McpPageReadAccess? TryCreate(McpServerConfiguration configuration)
    {
        if (configuration.AccessPolicy is not { } accessPolicy)
        {
            return null;
        }

        var pathProvider = new MemberPathProvider(configuration.PathProvider as PathProviderBase
            ?? throw new InvalidOperationException("PathProvider must extend PathProviderBase."));
        return new McpPageReadAccess(new McpServerConfiguration { PathProvider = pathProvider, AccessPolicy = accessPolicy }, pathProvider);
    }

    /// <summary>
    /// Gets the check for one invocation from <paramref name="root"/>, which caches its answer per property and may
    /// be called concurrently.
    /// </summary>
    public Func<RegisteredSubjectProperty, bool> CreateCheck(IInterceptorSubject root) => new Invocation(this, root).CanRead;

    private bool CanRead(RegisteredSubjectProperty property, IInterceptorSubject root)
    {
        // A property without a written path cannot be resolved, so it fails closed.
        return root.TryGetRegisteredSubject() is { } registeredRoot &&
            property.TryGetPath(_pathProvider, root) is { } path &&
            _configuration.TryGetReadableProperty(registeredRoot, path) is { Index: null } resolved &&
            ReferenceEquals(resolved.Property, property);
    }

    private sealed class Invocation(McpPageReadAccess access, IInterceptorSubject root)
    {
        private readonly Dictionary<RegisteredSubjectProperty, bool> _canRead = [];
        private readonly Lock _lock = new();

        public bool CanRead(RegisteredSubjectProperty property)
        {
            lock (_lock)
            {
                if (_canRead.TryGetValue(property, out var cached))
                {
                    return cached;
                }
            }

            // Outside the lock, as resolving a path reads values, which may evaluate expressions.
            var canRead = access.CanRead(property, root);
            lock (_lock)
            {
                _canRead[property] = canRead;
            }

            return canRead;
        }
    }

    // The segments of the tools, and the name of a member the tools do not name; every member is included.
    private sealed class MemberPathProvider(PathProviderBase inner) : PathProviderBase
    {
        public override char PathSeparator => inner.PathSeparator;

        public override char IndexOpen => inner.IndexOpen;

        public override char IndexClose => inner.IndexClose;

        public override bool IsPropertyIncluded(RegisteredSubjectProperty property) => true;

        public override string? TryGetPropertySegment(RegisteredSubjectProperty property)
            => inner.TryGetPropertySegment(property) ?? property.Name;
    }
}
