using System;
using System.Linq.Expressions;
using Namotion.Interceptor.Connectors.Mapping;

namespace Namotion.Interceptor.Mqtt.Mapping;

/// <summary>
/// Builds a code-based <see cref="MqttFluentMapper"/>, root-scoped. Configure members per type with
/// <see cref="ForType{T}"/> and <c>Map(...)</c>, then call <see cref="Build"/> to produce the mapper and
/// compose it into an <see cref="MqttCompositeMapper"/> (typically after the attribute mappers so fluent wins).
/// </summary>
public sealed class MqttFluentMapperBuilder<TRoot>
    where TRoot : IInterceptorSubject
{
    internal FluentMappingRegistry<MqttPropertyMapping> Registry { get; } = new();

    /// <summary>Begins configuring members of <typeparamref name="T"/>.</summary>
    public MqttFluentTypeBuilder<TRoot, T> ForType<T>() => new(this);

    /// <summary>Builds the fluent mapper from the configured registrations.</summary>
    public MqttFluentMapper Build(char pathSeparator = '/') => new(Registry, pathSeparator);
}

/// <summary>Type-scoped MQTT fluent builder; chains within a type, into the next type, and into <see cref="Build"/>.</summary>
public sealed class MqttFluentTypeBuilder<TRoot, TSubject>
    where TRoot : IInterceptorSubject
{
    private readonly MqttFluentMapperBuilder<TRoot> _owner;

    internal MqttFluentTypeBuilder(MqttFluentMapperBuilder<TRoot> owner) => _owner = owner;

    /// <summary>Configures a single member of <typeparamref name="TSubject"/>.</summary>
    public MqttFluentTypeBuilder<TRoot, TSubject> Map<TValue>(
        Expression<Func<TSubject, TValue>> selector,
        Action<MqttFluentPropertyBuilder> configure)
    {
        var member = ExpressionPathHelper.GetSingleMemberName(selector.Body);
        var builder = new MqttFluentPropertyBuilder();
        configure(builder);
        var (segment, metadata) = builder.Build();
        _owner.Registry.AddPropertyMetadata(typeof(TSubject), member, segment, metadata);
        return this;
    }

    /// <summary>Switches configuration to another type.</summary>
    public MqttFluentTypeBuilder<TRoot, TOther> ForType<TOther>() => _owner.ForType<TOther>();

    /// <summary>Builds the fluent mapper (delegates to the owner).</summary>
    public MqttFluentMapper Build(char pathSeparator = '/') => _owner.Build(pathSeparator);
}
