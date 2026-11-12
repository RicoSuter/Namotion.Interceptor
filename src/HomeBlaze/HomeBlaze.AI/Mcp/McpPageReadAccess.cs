using System.Diagnostics.CodeAnalysis;
using HomeBlaze.Authorization;
using HomeBlaze.Authorization.Abstractions;
using HomeBlaze.Services;
using HomeBlaze.Services.Metadata;
using Namotion.Interceptor;
using Namotion.Interceptor.Mcp;
using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace HomeBlaze.AI.Mcp;

/// <summary>
/// The permissions of the reader of a query, such as a page, under the access policy of a tool configuration: a property
/// may be read when its path from the root resolves back to it through the readable resolution of the tools, so every
/// property on the path must be readable and every subject it passes readable as well, as for get_property. Unlike the
/// tools, every member is named on a path, so that an expression may read a member the tools do not list, such as a
/// derived title; a member that holds metadata, of its subject or of a property, only when the tools list it. A subject
/// is shown when the tools list it. A member of a subject, and a subject, below a subject of an excluded type of the
/// configuration is neither readable nor shown. A readable value is shown as the tools return it, after the value
/// filters of the configuration.
/// </summary>
internal sealed class McpPageReadAccess
{
    private readonly McpServerConfiguration _configuration;
    private readonly McpServerConfiguration _memberConfiguration;
    private readonly PathProviderBase _memberPathProvider;

    private McpPageReadAccess(McpServerConfiguration configuration, McpServerConfiguration memberConfiguration, PathProviderBase memberPathProvider)
    {
        _configuration = configuration;
        _memberConfiguration = memberConfiguration;
        _memberPathProvider = memberPathProvider;
    }

    /// <summary>
    /// Creates the permissions of the access policy of <paramref name="configuration"/>.
    /// </summary>
    public static McpPageReadAccess Create(McpServerConfiguration configuration)
    {
        var pathProvider = new MemberPathProvider(configuration.PathProvider);
        return new McpPageReadAccess(
            configuration,
            new McpServerConfiguration { PathProvider = pathProvider, AccessPolicy = configuration.AccessPolicy },
            pathProvider);
    }

    /// <summary>
    /// Gets the permissions for one invocation from <paramref name="root"/>, which cache their answers per member and
    /// subject and may be asked concurrently. They only read: they never write, run or administer, and hold no role.
    /// </summary>
    public IUserPermissions CreatePermissions(IInterceptorSubject root) => new Permissions(this, root);

    private bool CanRead(RegisteredSubjectProperty property, IInterceptorSubject root)
    {
        if ((property.IsMetadata() && !_configuration.PathProvider.IsPropertyEffectivelyIncluded(property)) ||
            IsOfExcludedType(property.Parent, root, _configuration.ExcludeTypes))
        {
            return false;
        }

        // A property for which no path is built cannot be resolved, so it fails closed.
        return root.TryGetRegisteredSubject() is { } registeredRoot &&
            property.TryGetPath(_memberPathProvider, root) is { } path &&
            _memberConfiguration.TryGetReadableProperty(registeredRoot, path) is { Index: null } resolved &&
            ReferenceEquals(resolved.Property, property);
    }

    // A subject is shown when the tools build a path for it, which is exactly when search lists it, and browse lists it
    // in the tree, which leaves out what is below a subject of an excluded type.
    private bool CanShow(RegisteredSubject subject, IInterceptorSubject root) =>
        ReferenceEquals(subject.Subject, root) ||
        (root.TryGetRegisteredSubject() is { } registeredRoot &&
            !IsOfExcludedType(subject, root, _configuration.ExcludeTypes) &&
            _configuration.TryGetSubjectPath(registeredRoot, subject) is not null);

    /// <summary>
    /// Gets whether the subject or one of the subjects that hold it below <paramref name="top"/>, through the first parent
    /// of each, is of one of <paramref name="excludeTypes"/>.
    /// </summary>
    public static bool IsOfExcludedType(RegisteredSubject subject, IInterceptorSubject top, Type[] excludeTypes)
    {
        if (excludeTypes.Length == 0)
        {
            return false;
        }

        foreach (var current in new FirstParentChain(subject))
        {
            if (ReferenceEquals(current.Subject, top))
            {
                return false;
            }

            var type = current.Subject.GetType();
            foreach (var excludedType in excludeTypes)
            {
                if (excludedType.IsAssignableFrom(type))
                {
                    return true;
                }
            }
        }

        return false;
    }

    // The value the tools return for the member, so that a page shows no more than get_property.
    private object? GetValue(RegisteredSubjectProperty member) => _configuration.ApplyValueFilters(member, member.GetValue());

    private sealed class Permissions(McpPageReadAccess access, IInterceptorSubject root) : IUserPermissions
    {
        private readonly McpPageReadAccess _access = access;
        private readonly IInterceptorSubject _root = root;
        private readonly Dictionary<RegisteredSubjectProperty, bool> _canRead = [];
        private readonly Dictionary<RegisteredSubject, bool> _canShow = [];
        private readonly Lock _lock = new();

        /// <summary>Never raised: the policy of an invocation does not change.</summary>
        public event Action? Changed
        {
            add
            {
                // Nothing to subscribe to: the answers never change.
            }

            remove
            {
                // Nothing was subscribed.
            }
        }

        public bool CanRead(RegisteredSubjectProperty member) =>
            GetCached(_canRead, member, static (access, property, root) => access.CanRead(property, root));

        public object? GetValue(RegisteredSubjectProperty member) => _access.GetValue(member);

        // A query only reads, so its reader neither writes nor runs anything, whatever the policy allows.
        public bool CanWrite(RegisteredSubjectProperty member) => false;

        public bool CanInvoke(RegisteredSubjectProperty member) => false;

        public bool CanAdminister(RegisteredSubjectProperty member) => false;

        public bool CanReadHistory(RegisteredSubjectProperty member) =>
            CanRead(member) && _access._configuration.IsAllowed(member, AuthorizationActions.History.Name);

        public bool CanShow(RegisteredSubject subject) =>
            GetCached(_canShow, subject, static (access, shown, root) => access.CanShow(shown, root));

        public bool HoldsAllRoles(IReadOnlyCollection<string> roles) => false;

        public bool CanActThrough(IReadOnlyCollection<string> roles) => false;

        public IReadOnlyList<string> GetRolesWithDenyRulesMissingFrom(IReadOnlyCollection<string> roles) => [];

        private bool GetCached<TKey>(
            Dictionary<TKey, bool> cache, TKey key, Func<McpPageReadAccess, TKey, IInterceptorSubject, bool> decide)
            where TKey : notnull
        {
            lock (_lock)
            {
                if (cache.TryGetValue(key, out var cached))
                {
                    return cached;
                }
            }

            // Outside the lock, as resolving a path reads values, which may evaluate expressions.
            var allowed = decide(_access, key, _root);
            lock (_lock)
            {
                cache[key] = allowed;
            }

            return allowed;
        }
    }

    // The segments of the tools, and the name of a member the tools do not name; every member is included.
    private sealed class MemberPathProvider(PathProviderBase inner) : PathProviderBase
    {
        public override char PathSeparator => inner.PathSeparator;

        public override char IndexOpen => inner.IndexOpen;

        public override char IndexClose => inner.IndexClose;

        public override bool TryGetPropertySegment(RegisteredSubjectProperty property, [NotNullWhen(true)] out string? segment)
        {
            if (!inner.TryGetPropertySegment(property, out segment))
            {
                segment = property.Name;
            }

            return true;
        }
    }
}
