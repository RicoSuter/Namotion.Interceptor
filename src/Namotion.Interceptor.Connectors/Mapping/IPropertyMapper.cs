using System.Diagnostics.CodeAnalysis;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Connectors.Mapping;

/// <summary>
/// Maps subject properties to connector-specific representations.
/// </summary>
public interface IPropertyMapper<TMapping>
{
    /// <summary>
    /// Attempts to map a property to its connector-specific representation.
    /// </summary>
    /// <param name="property">The registered property to look up a mapping for.</param>
    /// <param name="rootSubject">The source's root subject, used for computing relative paths.</param>
    /// <param name="mapping">The resulting mapping when found.</param>
    bool TryGetMapping(
        RegisteredSubjectProperty property,
        IInterceptorSubject rootSubject,
        [NotNullWhen(true)] out TMapping? mapping);

    /// <summary>
    /// Gets a number that increases after every change of the answers of this mapper, and never decreases. 0, the
    /// default, means the answers never change.
    /// </summary>
    /// <remarks>
    /// A mapper whose answers change while a connector uses it raises it after each change: connectors that cache mappings
    /// serve an entry only while it is unchanged.
    /// </remarks>
    long Version => 0;
}
