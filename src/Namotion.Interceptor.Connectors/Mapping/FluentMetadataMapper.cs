using System.Diagnostics.CodeAnalysis;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Connectors.Mapping;

/// <summary>
/// Supplies code-based (fluent) protocol metadata for a property from a <see cref="FluentMappingRegistry{TMetadata}"/>.
/// The drop-in analog of the per-connector attribute metadata mapper: it contributes forward metadata only,
/// and returns null on reverse because reverse lookup is owned by the path-provider mapper (segments are
/// type-level and identical forward and reverse).
/// </summary>
public class FluentMetadataMapper<TMetadata, TKey> : IReversePropertyMapper<TMetadata, TKey>
    where TMetadata : class, IPropertyMapping<TMetadata>
{
    /// <summary>The registry this mapper reads from. Available to subclasses that add resolution (e.g. type-self).</summary>
    protected FluentMappingRegistry<TMetadata> Registry { get; }

    public FluentMetadataMapper(FluentMappingRegistry<TMetadata> registry)
    {
        Registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    /// <inheritdoc />
    public virtual bool TryGetMapping(
        RegisteredSubjectProperty property,
        IInterceptorSubject rootSubject,
        [NotNullWhen(true)] out TMetadata? mapping)
    {
        if (Registry.TryGetTypeMetadata(property.Subject.GetType(), property.Name, out var metadata))
        {
            mapping = metadata;
            return true;
        }

        mapping = null;
        return false;
    }

    /// <inheritdoc />
    public ValueTask<RegisteredSubjectProperty?> TryGetPropertyAsync(
        TKey key, RegisteredSubject subject, CancellationToken cancellationToken)
        => new(result: null);
}
