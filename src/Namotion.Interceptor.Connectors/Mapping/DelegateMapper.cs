using System.Diagnostics.CodeAnalysis;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Connectors.Mapping;

/// <summary>
/// Wraps a delegate for simple one-off mappers.
/// </summary>
public class DelegateMapper<TMapping> : IPropertyMapper<TMapping>
    where TMapping : class
{
    private readonly Func<RegisteredSubjectProperty, IInterceptorSubject, TMapping?> _selector;

    public DelegateMapper(Func<RegisteredSubjectProperty, IInterceptorSubject, TMapping?> selector)
    {
        _selector = selector ?? throw new ArgumentNullException(nameof(selector));
    }

    public bool TryGetMapping(
        RegisteredSubjectProperty property,
        IInterceptorSubject rootSubject,
        [NotNullWhen(true)] out TMapping? mapping)
    {
        mapping = _selector(property, rootSubject);
        return mapping is not null;
    }
}
