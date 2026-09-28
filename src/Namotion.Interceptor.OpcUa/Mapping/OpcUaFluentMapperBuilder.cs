using System.Linq.Expressions;
using Namotion.Interceptor.Connectors.Mapping;

namespace Namotion.Interceptor.OpcUa.Mapping;

/// <summary>
/// Builds a code-based <see cref="OpcUaFluentMapper"/>, root-scoped. Configure members per type with
/// <see cref="ForType{T}"/> and <c>Map(...)</c>/<c>Configure(...)</c>, then call <see cref="Build"/> to
/// produce the mapper and compose it into an <see cref="OpcUaCompositeMapper"/> (typically after the
/// attribute mappers so fluent wins).
/// </summary>
public sealed class OpcUaFluentMapperBuilder<TRoot>
    where TRoot : IInterceptorSubject
{
    internal FluentMappingRegistry<OpcUaPropertyMapping> Registry { get; } = new();

    /// <summary>Begins configuring members of <typeparamref name="T"/>.</summary>
    public OpcUaFluentTypeBuilder<TRoot, T> ForType<T>() => new(this);

    /// <summary>Builds the fluent mapper from the configured registrations.</summary>
    public OpcUaFluentMapper Build(char pathSeparator = '.') => new(Registry, pathSeparator);
}

/// <summary>Type-scoped OPC UA fluent builder; chains within a type, into the next type, and into <see cref="Build"/>.</summary>
public sealed class OpcUaFluentTypeBuilder<TRoot, TSubject>
    where TRoot : IInterceptorSubject
{
    private readonly OpcUaFluentMapperBuilder<TRoot> _owner;

    internal OpcUaFluentTypeBuilder(OpcUaFluentMapperBuilder<TRoot> owner) => _owner = owner;

    /// <summary>Configures a single member of <typeparamref name="TSubject"/>.</summary>
    public OpcUaFluentTypeBuilder<TRoot, TSubject> Map<TValue>(
        Expression<Func<TSubject, TValue>> selector,
        Action<OpcUaFluentPropertyBuilder> configure)
    {
        var member = ExpressionPathHelper.GetSingleMemberName(selector.Body);
        var builder = new OpcUaFluentPropertyBuilder();
        configure(builder);
        var (segment, metadata) = builder.Build();
        _owner.Registry.AddPropertyMetadata(typeof(TSubject), member, segment, metadata);
        return this;
    }

    /// <summary>
    /// Configures class-level (type-self) node metadata for <typeparamref name="TSubject"/>. Only node metadata is
    /// registered here; a <c>BrowseName(...)</c> call inside <paramref name="configure"/> contributes its
    /// metadata but no path segment (type-self has no member to attach a segment to).
    /// </summary>
    public OpcUaFluentTypeBuilder<TRoot, TSubject> Configure(Action<OpcUaFluentPropertyBuilder> configure)
    {
        var builder = new OpcUaFluentPropertyBuilder();
        configure(builder);
        var (_, metadata) = builder.Build();
        _owner.Registry.AddTypeMetadata(typeof(TSubject), metadata);
        return this;
    }

    /// <summary>Switches configuration to another type.</summary>
    public OpcUaFluentTypeBuilder<TRoot, TOther> ForType<TOther>() => _owner.ForType<TOther>();

    /// <summary>Builds the fluent mapper (delegates to the owner).</summary>
    public OpcUaFluentMapper Build(char pathSeparator = '.') => _owner.Build(pathSeparator);
}
