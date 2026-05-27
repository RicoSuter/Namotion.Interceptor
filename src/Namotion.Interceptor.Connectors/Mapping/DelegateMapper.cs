using System.Diagnostics.CodeAnalysis;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Connectors.Mapping;

public class DelegateMapper<TMapping> : IPropertyMapper<TMapping>
{
    private readonly Func<RegisteredSubjectProperty, TMapping?> _selector;

    public DelegateMapper(Func<RegisteredSubjectProperty, TMapping?> selector)
    {
        _selector = selector ?? throw new ArgumentNullException(nameof(selector));
    }

    public bool TryGetMapping(
        RegisteredSubjectProperty property,
        [NotNullWhen(true)] out TMapping? mapping)
    {
        mapping = _selector(property);
        return mapping is not null;
    }
}
