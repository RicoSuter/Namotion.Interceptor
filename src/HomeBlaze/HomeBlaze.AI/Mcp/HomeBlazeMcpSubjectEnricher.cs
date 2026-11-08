using Namotion.Interceptor;
using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Registry.Abstractions;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.Services;
using Namotion.Interceptor.Mcp;
using Namotion.Interceptor.Registry;
using Microsoft.Extensions.DependencyInjection;
using HomeBlaze.Services.Components;
using HomeBlaze.Components.Abstractions.Attributes;

namespace HomeBlaze.AI.Mcp;

internal class HomeBlazeMcpSubjectEnricher : IMcpSubjectEnricher
{
    private readonly Lazy<(HashSet<Type> Concrete, HashSet<Type> Interfaces)> _typeCache;
    private readonly McpServerConfiguration _configuration;
    private readonly Func<IInterceptorSubject, string?>? _getPageUrl;

    /// <param name="typeProviders">The providers of the types named in <c>$type</c> and <c>$interfaces</c>.</param>
    /// <param name="configuration">The configuration whose access policy decides which methods <c>$methods</c> lists and
    /// whether <c>$title</c> and <c>$icon</c> are given, when they are registered members: only those it allows to read.</param>
    /// <param name="getPageUrl">Gets the address of the page of a subject, such as <c>/pages/House/Haus.md</c>, or null
    /// for a subject without a page of its own; null for no addresses.</param>
    public HomeBlazeMcpSubjectEnricher(
        IEnumerable<IMcpTypeProvider> typeProviders,
        McpServerConfiguration configuration,
        Func<IInterceptorSubject, string?>? getPageUrl = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _configuration = configuration;
        _getPageUrl = getPageUrl;
        _typeCache = new Lazy<(HashSet<Type>, HashSet<Type>)>(() =>
        {
            var allTypes = typeProviders.SelectMany(p => p.GetTypes()).ToList();
            return (
                new HashSet<Type>(allTypes.Where(t => !t.IsInterface).Select(t => t.Type)),
                new HashSet<Type>(allTypes.Where(t => t.IsInterface).Select(t => t.Type))
            );
        });
    }

    // The address of the page of a subject that has a page component, as the navigation links it. Without the component
    // registry, as in a host without pages, no subject has one.
    internal static Func<IInterceptorSubject, string?>? CreatePageUrlGetter(
        IServiceProvider serviceProvider, McpServerConfiguration configuration, Func<IInterceptorSubject> rootSubjectProvider)
    {
        if (serviceProvider.GetService<SubjectComponentRegistry>() is not { } componentRegistry ||
            serviceProvider.GetService<SubjectPathResolver>() is not { } pathResolver)
        {
            return null;
        }

        return CreatePageUrlGetter(
            subject => componentRegistry.HasComponent(subject.GetType(), SubjectComponentType.Page),
            pathResolver, configuration, rootSubjectProvider);
    }

    // The address names every property on the canonical path, which may differ from the path the tools build for the
    // subject, such as for a subject with several parents. A subject therefore gets an address only when the canonical
    // path resolves back to it as the tools resolve a path, so the address names nothing hidden from the tools.
    internal static Func<IInterceptorSubject, string?> CreatePageUrlGetter(
        Func<IInterceptorSubject, bool> hasPage,
        ISubjectPathResolver pathResolver,
        McpServerConfiguration configuration,
        Func<IInterceptorSubject> rootSubjectProvider)
    {
        return subject => hasPage(subject) &&
            pathResolver.GetPath(subject, PathStyle.Canonical) is { } path &&
            IsReadableAtPath(configuration, rootSubjectProvider, subject, path)
                ? string.Concat("/pages", path)
                : null;
    }

    private static bool IsReadableAtPath(
        McpServerConfiguration configuration, Func<IInterceptorSubject> rootSubjectProvider, IInterceptorSubject subject, string path)
    {
        var root = rootSubjectProvider();
        if (ReferenceEquals(root, subject))
        {
            return true;
        }

        return root.TryGetRegisteredSubject() is { } registeredRoot &&
            ReferenceEquals(configuration.TryGetReadableSubject(registeredRoot, path)?.Subject, subject);
    }

    public IDictionary<string, object?> GetSubjectEnrichments(RegisteredSubject subject)
    {
        var metadata = new Dictionary<string, object?>();
        AddTitleAndIcon(subject, metadata);

        if (_getPageUrl?.Invoke(subject.Subject) is { } pageUrl)
        {
            metadata["$url"] = pageUrl;
        }

        var (knownConcreteTypes, knownInterfaceTypes) = _typeCache.Value;
        var subjectType = subject.Subject.GetType();

        if (knownConcreteTypes.Contains(subjectType))
        {
            metadata["$type"] = subjectType.FullName;
        }

        var interfaces = subjectType.GetInterfaces()
            .Where(i => knownInterfaceTypes.Contains(i))
            .Select(i => i.FullName!)
            .ToList();

        if (interfaces.Count > 0)
        {
            metadata["$interfaces"] = interfaces;
        }

        var methods = subject.GetAllMethods(ExclusionTarget.Agents)
            .Where(method => IsReadable(subject, method))
            .Select(method => method.PropertyName)
            .ToArray();

        if (methods.Length > 0)
        {
            metadata["$methods"] = methods;
        }

        return metadata;
    }

    private void AddTitleAndIcon(RegisteredSubject subject, Dictionary<string, object?> metadata)
    {
        if (subject.Subject is ITitleProvider titleProvider && IsReadable(subject, nameof(ITitleProvider.Title)))
        {
            metadata["$title"] = titleProvider.Title;
        }

        if (subject.Subject is IIconProvider iconProvider && IsReadable(subject, nameof(IIconProvider.IconName)))
        {
            metadata["$icon"] = iconProvider.IconName;
        }
    }

    private bool IsReadable(RegisteredSubject subject, MethodMetadata method)
        => subject.TryGetProperty(method.PropertyName) is { } property && _configuration.IsAllowed(property, McpAccessActions.Read);

    // A title or icon that is a registered member, such as a derived title, is shown only when the policy allows to read it.
    private bool IsReadable(RegisteredSubject subject, string memberName)
        => subject.TryGetProperty(memberName) is not { } member ||
            _configuration.IsAllowed(member, McpAccessActions.Read);
}
