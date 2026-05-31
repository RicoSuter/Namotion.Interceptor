using System;
using System.Linq.Expressions;
using Namotion.Interceptor.Connectors.Mapping;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.Mqtt.Mapping;

/// <summary>
/// Code-based MQTT mapping configuration, root-scoped. The public entry point for configuring MQTT topics
/// and metadata in code instead of via attributes. Build the mapper pair with <see cref="CreateMappers"/>
/// or use the AddMqttSubject* DI overloads' <c>configureFluent</c> callback.
/// </summary>
public sealed class MqttFluentMapping<TRoot>
    where TRoot : IInterceptorSubject
{
    internal FluentMappingRegistry<MqttPropertyMapping> Registry { get; } = new();

    /// <summary>Begins configuring members of <typeparamref name="T"/>.</summary>
    public MqttFluentTypeBuilder<TRoot, T> ForType<T>() => new(this);

    /// <summary>
    /// Builds the fluent mapper pair (a path-provider mapper over a <see cref="FluentPathProvider"/> and a
    /// metadata mapper) to splice into an <see cref="MqttCompositeMapper"/>.
    /// </summary>
    public IReversePropertyMapper<MqttPropertyMapping, MqttLookupKey>[] CreateMappers(char pathSeparator = '/')
        =>
        [
            new MqttPathProviderMapper(new FluentPathProvider(Registry, pathSeparator)),
            new FluentMetadataMapper<MqttPropertyMapping, MqttLookupKey>(Registry)
        ];
}

/// <summary>Type-scoped MQTT fluent builder; chains within a type and into the next type.</summary>
public sealed class MqttFluentTypeBuilder<TRoot, T>
    where TRoot : IInterceptorSubject
{
    private readonly MqttFluentMapping<TRoot> _owner;

    internal MqttFluentTypeBuilder(MqttFluentMapping<TRoot> owner) => _owner = owner;

    /// <summary>Configures a single member of <typeparamref name="T"/>.</summary>
    public MqttFluentTypeBuilder<TRoot, T> Map<TValue>(
        Expression<Func<T, TValue>> selector,
        Action<MqttFluentPropertyBuilder> configure)
    {
        var member = ExpressionPathHelper.GetSingleMemberName(selector.Body);
        var builder = new MqttFluentPropertyBuilder();
        configure(builder);
        var (segment, metadata) = builder.Build();
        _owner.Registry.AddType(typeof(T), member, segment, metadata);
        return this;
    }

    /// <summary>Switches configuration to another type.</summary>
    public MqttFluentTypeBuilder<TRoot, TOther> ForType<TOther>() => _owner.ForType<TOther>();
}
