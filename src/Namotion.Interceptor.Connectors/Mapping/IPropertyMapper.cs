using System.Diagnostics.CodeAnalysis;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Connectors.Mapping;

public interface IPropertyMapper<TMapping>
{
    bool TryGetMapping(
        RegisteredSubjectProperty property,
        [NotNullWhen(true)] out TMapping? mapping);
}
