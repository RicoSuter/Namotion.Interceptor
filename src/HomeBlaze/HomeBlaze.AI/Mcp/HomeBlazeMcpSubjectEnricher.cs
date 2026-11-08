using Namotion.Interceptor;
using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Registry.Abstractions;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.Services;
using Namotion.Interceptor.Registry.Paths;
using Microsoft.Extensions.DependencyInjection;
using HomeBlaze.Services.Components;
using HomeBlaze.Components.Abstractions.Attributes;

namespace HomeBlaze.AI.Mcp;

internal class HomeBlazeMcpSubjectEnricher : IMcpSubjectEnricher
{
    private readonly Lazy<(HashSet<Type> Concrete, HashSet<Type> Interfaces)> _typeCache;
    private readonly bool _isReadOnly;
    private readonly Func<IInterceptorSubject, string?>? _getPageUrl;

    /// <param name="typeProviders">The types that the enrichments name.</param>
    /// <param name="isReadOnly">Whether the server writes nothing.</param>
    /// <param name="getPageUrl">Gets the address of the page of a subject, such as <c>/pages/House/Haus.md</c>, or null
    /// for a subject without a page of its own; null for no addresses.</param>
    public HomeBlazeMcpSubjectEnricher(IEnumerable<IMcpTypeProvider> typeProviders, bool isReadOnly, Func<IInterceptorSubject, string?>? getPageUrl = null)
    {
        _isReadOnly = isReadOnly;
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
    internal static Func<IInterceptorSubject, string?>? CreatePageUrlGetter(IServiceProvider serviceProvider)
    {
        if (serviceProvider.GetService<SubjectComponentRegistry>() is not { } componentRegistry ||
            serviceProvider.GetService<SubjectPathResolver>() is not { } pathResolver)
        {
            return null;
        }

        return subject => componentRegistry.HasComponent(subject.GetType(), SubjectComponentType.Page) &&
            pathResolver.GetPath(subject, PathStyle.Canonical) is { } path
                ? string.Concat("/pages", path)
                : null;
    }

    public IDictionary<string, object?> GetSubjectEnrichments(RegisteredSubject subject)
    {
        var metadata = new Dictionary<string, object?>();

        if (subject.Subject is ITitleProvider titleProvider)
        {
            metadata["$title"] = titleProvider.Title;
        }

        if (subject.Subject is IIconProvider iconProvider)
        {
            metadata["$icon"] = iconProvider.IconName;
        }

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
            .Where(method => !_isReadOnly || method.Kind == MethodKind.Query)
            .Select(method => method.PropertyName)
            .ToArray();

        if (methods.Length > 0)
        {
            metadata["$methods"] = methods;
        }

        return metadata;
    }
}
