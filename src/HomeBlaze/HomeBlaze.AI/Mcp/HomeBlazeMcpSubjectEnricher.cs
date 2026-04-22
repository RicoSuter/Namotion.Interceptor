using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Registry.Abstractions;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.Services;

namespace HomeBlaze.AI.Mcp;

public class HomeBlazeMcpSubjectEnricher : IMcpSubjectEnricher
{
    private readonly Lazy<(HashSet<Type> Concrete, HashSet<Type> Interfaces)> _typeCache;
    private readonly bool _isReadOnly;

    public HomeBlazeMcpSubjectEnricher(IEnumerable<IMcpTypeProvider> typeProviders, bool isReadOnly)
    {
        _isReadOnly = isReadOnly;
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

        var methods = subject.GetAllMethods()
            .Where(method => !_isReadOnly || method.Kind == MethodKind.Query)
            .Select(method => method.MethodName)
            .ToArray();

        if (methods.Length > 0)
        {
            metadata["$methods"] = methods;
        }

        return metadata;
    }
}
