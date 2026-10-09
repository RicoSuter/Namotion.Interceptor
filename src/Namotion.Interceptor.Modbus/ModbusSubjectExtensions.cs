using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Modbus;
using Namotion.Interceptor.Modbus.Client;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Creates and registers Modbus client sources.
/// </summary>
public static class ModbusSubjectExtensions
{
    /// <summary>
    /// Creates a Modbus client source for <paramref name="subject"/>. Attach it through a hosted service factory, such as
    /// <c>subject.AttachHostedService(() => subject.CreateModbusClientSource(...))</c>, which hands the instance to the
    /// hosting handler to start, stop and dispose. A source used directly must be started, stopped and disposed by its owner.
    /// </summary>
    /// <exception cref="ArgumentException">A <paramref name="configuration"/> value is out of range.</exception>
    /// <exception cref="InvalidOperationException">The subject's context has no lifecycle tracking (<c>WithLifecycle()</c>).</exception>
    public static ModbusSubjectClientSource CreateModbusClientSource(
        this IInterceptorSubject subject, ModbusClientConfiguration configuration, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);
        return new ModbusSubjectClientSource(subject, configuration, logger);
    }

    /// <summary>
    /// Registers a hosted Modbus client source for the <typeparamref name="TSubject"/> singleton, resolvable as
    /// <see cref="ModbusSubjectClientSource"/>. The configuration is validated when the source is resolved, and
    /// resolving fails unless the subject's context has lifecycle tracking (<c>WithLifecycle()</c>).
    /// </summary>
    /// <exception cref="InvalidOperationException">An unnamed Modbus client source is already registered.</exception>
    public static IServiceCollection AddModbusSubjectClientSource<TSubject>(
        this IServiceCollection services, string host, int port = 502)
        where TSubject : IInterceptorSubject
    {
        return services.AddModbusSubjectClientSource(
            serviceProvider => serviceProvider.GetRequiredService<TSubject>(),
            _ => new ModbusClientConfiguration { Host = host, Port = port });
    }

    /// <summary>
    /// Registers a hosted Modbus client source, resolvable as <see cref="ModbusSubjectClientSource"/>.
    /// The configuration is validated when the source is resolved, and resolving fails unless the subject's context
    /// has lifecycle tracking (<c>WithLifecycle()</c>).
    /// </summary>
    /// <exception cref="InvalidOperationException">An unnamed Modbus client source is already registered.</exception>
    public static IServiceCollection AddModbusSubjectClientSource(
        this IServiceCollection services,
        Func<IServiceProvider, IInterceptorSubject> subjectSelector,
        Func<IServiceProvider, ModbusClientConfiguration> configurationProvider)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(subjectSelector);
        ArgumentNullException.ThrowIfNull(configurationProvider);

        if (services.Any(descriptor => descriptor.ServiceType == typeof(ModbusSubjectClientSource) && descriptor.ServiceKey is null))
        {
            throw new InvalidOperationException(
                "An unnamed ModbusSubjectClientSource is already registered. " +
                "Use AddKeyedModbusSubjectClientSource to register multiple sources with distinct names.");
        }

        var key = RegisterClientSourceCore(services, subjectSelector, configurationProvider);
        services.AddSingleton(serviceProvider => serviceProvider.GetRequiredKeyedService<ModbusSubjectClientSource>(key));
        return services;
    }

    /// <summary>
    /// Registers a hosted Modbus client source, resolvable as a keyed <see cref="ModbusSubjectClientSource"/>
    /// under <paramref name="name"/>. The configuration is validated when the source is resolved, and resolving
    /// fails unless the subject's context has lifecycle tracking (<c>WithLifecycle()</c>).
    /// </summary>
    /// <exception cref="InvalidOperationException">A Modbus client source with this name is already registered.</exception>
    public static IServiceCollection AddKeyedModbusSubjectClientSource(
        this IServiceCollection services,
        string name,
        Func<IServiceProvider, IInterceptorSubject> subjectSelector,
        Func<IServiceProvider, ModbusClientConfiguration> configurationProvider)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(subjectSelector);
        ArgumentNullException.ThrowIfNull(configurationProvider);

        if (services.Any(descriptor => descriptor.ServiceType == typeof(ModbusSubjectClientSource) && name.Equals(descriptor.ServiceKey)))
        {
            throw new InvalidOperationException($"A ModbusSubjectClientSource with name '{name}' is already registered.");
        }

        var key = RegisterClientSourceCore(services, subjectSelector, configurationProvider);
        services.AddKeyedSingleton(name, (serviceProvider, _) => serviceProvider.GetRequiredKeyedService<ModbusSubjectClientSource>(key));
        return services;
    }

    private static string RegisterClientSourceCore(
        IServiceCollection services,
        Func<IServiceProvider, IInterceptorSubject> subjectSelector,
        Func<IServiceProvider, ModbusClientConfiguration> configurationProvider)
    {
        var key = Guid.NewGuid().ToString();
        services
            .AddKeyedSingleton(key, (serviceProvider, _) => configurationProvider(serviceProvider))
            .AddKeyedSingleton(key, (serviceProvider, _) => subjectSelector(serviceProvider))
            .AddKeyedSingleton(key, (serviceProvider, _) => new ModbusSubjectClientSource(
                serviceProvider.GetRequiredKeyedService<IInterceptorSubject>(key),
                serviceProvider.GetRequiredKeyedService<ModbusClientConfiguration>(key),
                serviceProvider.GetRequiredService<ILogger<ModbusSubjectClientSource>>()))
            .AddSingleton<IHostedService>(serviceProvider => serviceProvider.GetRequiredKeyedService<ModbusSubjectClientSource>(key));
        return key;
    }
}
