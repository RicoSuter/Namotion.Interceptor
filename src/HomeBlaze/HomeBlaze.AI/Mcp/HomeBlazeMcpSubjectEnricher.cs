using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Registry.Abstractions;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Metadata;

namespace HomeBlaze.AI.Mcp;

internal class HomeBlazeMcpSubjectEnricher : IMcpSubjectEnricher
{
    private readonly Lazy<(HashSet<Type> Concrete, HashSet<Type> Interfaces)> _typeCache;
    private readonly bool _isReadOnly;
    private readonly IMcpAccessPolicy? _accessPolicy;

    /// <param name="typeProviders">The providers of the types named in <c>$type</c> and <c>$interfaces</c>.</param>
    /// <param name="isReadOnly">Whether <c>$methods</c> leaves out operations.</param>
    /// <param name="accessPolicy">Decides which methods <c>$methods</c> lists: only those it allows to read; all without one.</param>
    public HomeBlazeMcpSubjectEnricher(IEnumerable<IMcpTypeProvider> typeProviders, bool isReadOnly, IMcpAccessPolicy? accessPolicy = null)
    {
        _isReadOnly = isReadOnly;
        _accessPolicy = accessPolicy;
        _typeCache = new Lazy<(HashSet<Type>, HashSet<Type>)>(() =>
        {
            var allTypes = typeProviders.SelectMany(p => p.GetTypes()).ToList();
            return (
                new HashSet<Type>(allTypes.Where(t => !t.IsInterface).Select(t => t.Type)),
                new HashSet<Type>(allTypes.Where(t => t.IsInterface).Select(t => t.Type))
            );
        });
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

        var methods = McpMethodInvoker.GetReadableMethods(subject, _accessPolicy)
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
