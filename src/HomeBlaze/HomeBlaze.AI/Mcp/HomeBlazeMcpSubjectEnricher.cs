using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Registry.Abstractions;
using HomeBlaze.Abstractions;

namespace HomeBlaze.AI.Mcp;

public class HomeBlazeMcpSubjectEnricher : IMcpSubjectEnricher
{
    private readonly IEnumerable<IMcpTypeProvider> _typeProviders;
    private volatile HashSet<Type>? _knownConcreteTypes;
    private volatile HashSet<Type>? _knownInterfaceTypes;

    public HomeBlazeMcpSubjectEnricher(IEnumerable<IMcpTypeProvider> typeProviders)
    {
        _typeProviders = typeProviders;
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

        EnsureTypeCachesBuilt();

        var subjectType = subject.Subject.GetType();

        if (_knownConcreteTypes!.Contains(subjectType))
        {
            metadata["$type"] = subjectType.FullName;
        }

        var interfaces = subjectType.GetInterfaces()
            .Where(i => _knownInterfaceTypes!.Contains(i))
            .Select(i => i.FullName!)
            .ToList();

        if (interfaces.Count > 0)
        {
            metadata["$interfaces"] = interfaces;
        }

        return metadata;
    }

    private void EnsureTypeCachesBuilt()
    {
        if (_knownConcreteTypes is not null)
            return;

        var allTypes = _typeProviders.SelectMany(p => p.GetTypes()).ToList();
        _knownConcreteTypes = new HashSet<Type>(allTypes.Where(t => !t.IsInterface).Select(t => t.Type));
        _knownInterfaceTypes = new HashSet<Type>(allTypes.Where(t => t.IsInterface).Select(t => t.Type));
    }
}
