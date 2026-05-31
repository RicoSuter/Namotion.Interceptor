using System.Linq.Expressions;
using Namotion.Interceptor.Connectors.Mapping;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.OpcUa.Mapping;

/// <summary>
/// Code-based OPC UA mapping configuration, root-scoped. The public entry point for configuring OPC UA
/// nodes and metadata in code instead of via attributes. Build the mapper pair with
/// <see cref="CreateMappers"/> or use the AddOpcUaSubject* DI overloads' <c>configureFluent</c> callback.
/// </summary>
public sealed class OpcUaFluentMapping<TRoot>
    where TRoot : IInterceptorSubject
{
    internal FluentMappingRegistry<OpcUaPropertyMapping> Registry { get; } = new();

    /// <summary>Begins configuring members of <typeparamref name="T"/>.</summary>
    public OpcUaFluentTypeBuilder<TRoot, T> ForType<T>() => new(this);

    /// <summary>Builds the fluent mapper pair to splice into an <see cref="OpcUaCompositeMapper"/>.</summary>
    public IReversePropertyMapper<OpcUaPropertyMapping, OpcUaLookupKey>[] CreateMappers(char pathSeparator = '.')
        =>
        [
            new OpcUaPathProviderMapper(new FluentPathProvider(Registry, pathSeparator)),
            new OpcUaFluentMetadataMapper(Registry)
        ];
}

/// <summary>Type-scoped OPC UA fluent builder; chains within a type and into the next type.</summary>
public sealed class OpcUaFluentTypeBuilder<TRoot, T>
    where TRoot : IInterceptorSubject
{
    private readonly OpcUaFluentMapping<TRoot> _owner;

    internal OpcUaFluentTypeBuilder(OpcUaFluentMapping<TRoot> owner) => _owner = owner;

    /// <summary>Configures a single member of <typeparamref name="T"/>.</summary>
    public OpcUaFluentTypeBuilder<TRoot, T> Map<TValue>(
        Expression<Func<T, TValue>> selector,
        Action<OpcUaFluentPropertyBuilder> configure)
    {
        var member = ExpressionPathHelper.GetSingleMemberName(selector.Body);
        var builder = new OpcUaFluentPropertyBuilder();
        configure(builder);
        var (segment, metadata) = builder.Build();
        _owner.Registry.AddType(typeof(T), member, segment, metadata);
        return this;
    }

    /// <summary>Configures class-level (type-self) node metadata for <typeparamref name="T"/>.</summary>
    public OpcUaFluentTypeBuilder<TRoot, T> Configure(Action<OpcUaFluentPropertyBuilder> configure)
    {
        var builder = new OpcUaFluentPropertyBuilder();
        configure(builder);
        var (_, metadata) = builder.Build();
        _owner.Registry.AddTypeSelf(typeof(T), metadata);
        return this;
    }

    /// <summary>Switches configuration to another type.</summary>
    public OpcUaFluentTypeBuilder<TRoot, TOther> ForType<TOther>() => _owner.ForType<TOther>();
}
