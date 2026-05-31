using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.OpcUa;
using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.OpcUa.Mapping;
using Namotion.Interceptor.OpcUa.Server;
using Namotion.Interceptor.Registry.Paths;
using Opc.Ua;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

public static class OpcUaSubjectExtensions
{
    /// <summary>
    /// Creates an OPC UA server hosted service for the given subject.
    /// The server exposes the subject's properties as OPC UA nodes.
    /// </summary>
    /// <param name="subject">The subject to expose via OPC UA.</param>
    /// <param name="configuration">The OPC UA server configuration.</param>
    /// <param name="logger">The logger instance.</param>
    /// <returns>An <see cref="IOpcUaSubjectServer"/> that runs the OPC UA server. Call <see cref="IHostedService.StartAsync"/> to start it.</returns>
    public static IOpcUaSubjectServer CreateOpcUaServer(
        this IInterceptorSubject subject,
        OpcUaServerConfiguration configuration,
        ILogger logger)
    {
        return new OpcUaSubjectServer(subject, configuration, logger);
    }

    /// <summary>
    /// Creates an OPC UA client source hosted service for the given subject.
    /// The client synchronizes the subject's properties with an OPC UA server.
    /// </summary>
    /// <param name="subject">The subject to synchronize with OPC UA.</param>
    /// <param name="configuration">The OPC UA client configuration.</param>
    /// <param name="logger">The logger instance.</param>
    /// <returns>An <see cref="IOpcUaSubjectClientSource"/> that runs the OPC UA client. Call <see cref="IHostedService.StartAsync"/> to start it.</returns>
    public static IOpcUaSubjectClientSource CreateOpcUaClientSource(
        this IInterceptorSubject subject,
        OpcUaClientConfiguration configuration,
        ILogger logger)
    {
        return new OpcUaSubjectClientSource(subject, configuration, logger);
    }

    public static IServiceCollection AddOpcUaSubjectClientSource<TSubject>(
        this IServiceCollection services,
        string serverUrl,
        string connectorName,
        string[]? rootPath = null)
        where TSubject : IInterceptorSubject
    {
        return services.AddOpcUaSubjectClientSource(
            sp => sp.GetRequiredService<TSubject>(),
            sp => CreateDefaultClientConfiguration(sp, serverUrl, connectorName, rootPath));
    }

    public static IServiceCollection AddOpcUaSubjectClientSource(
        this IServiceCollection services,
        Func<IServiceProvider, IInterceptorSubject> subjectSelector,
        Func<IServiceProvider, OpcUaClientConfiguration> configurationProvider)
    {
        GuardDuplicateUnkeyed<IOpcUaSubjectClientSource>(services);
        var key = Guid.NewGuid().ToString();
        RegisterClientSourceCore(services, key, subjectSelector, configurationProvider);
        services.AddSingleton<IOpcUaSubjectClientSource>(sp =>
            sp.GetRequiredKeyedService<OpcUaSubjectClientSource>(key));
        return services;
    }

    public static IServiceCollection AddKeyedOpcUaSubjectClientSource<TSubject>(
        this IServiceCollection services,
        string name,
        string serverUrl,
        string connectorName,
        string[]? rootPath = null)
        where TSubject : IInterceptorSubject
    {
        return services.AddKeyedOpcUaSubjectClientSource(
            name,
            sp => sp.GetRequiredService<TSubject>(),
            sp => CreateDefaultClientConfiguration(sp, serverUrl, connectorName, rootPath));
    }

    public static IServiceCollection AddKeyedOpcUaSubjectClientSource(
        this IServiceCollection services,
        string name,
        Func<IServiceProvider, IInterceptorSubject> subjectSelector,
        Func<IServiceProvider, OpcUaClientConfiguration> configurationProvider)
    {
        GuardDuplicateKeyed<IOpcUaSubjectClientSource>(services, name);
        var key = Guid.NewGuid().ToString();
        RegisterClientSourceCore(services, key, subjectSelector, configurationProvider);
        services.AddKeyedSingleton<IOpcUaSubjectClientSource>(name, (sp, _) =>
            sp.GetRequiredKeyedService<OpcUaSubjectClientSource>(key));
        return services;
    }

    public static IServiceCollection AddOpcUaSubjectServer<TSubject>(
        this IServiceCollection services,
        string connectorName,
        string? rootName = null)
        where TSubject : IInterceptorSubject
    {
        return services.AddOpcUaSubjectServer(
            sp => sp.GetRequiredService<TSubject>(),
            sp => CreateDefaultServerConfiguration(sp, connectorName, rootName));
    }

    public static IServiceCollection AddOpcUaSubjectServer(
        this IServiceCollection services,
        Func<IServiceProvider, IInterceptorSubject> subjectSelector,
        Func<IServiceProvider, OpcUaServerConfiguration> configurationProvider)
    {
        GuardDuplicateUnkeyed<IOpcUaSubjectServer>(services);
        var key = Guid.NewGuid().ToString();
        RegisterServerCore(services, key, subjectSelector, configurationProvider);
        services.AddSingleton<IOpcUaSubjectServer>(sp =>
            sp.GetRequiredKeyedService<OpcUaSubjectServer>(key));
        return services;
    }

    public static IServiceCollection AddKeyedOpcUaSubjectServer<TSubject>(
        this IServiceCollection services,
        string name,
        string connectorName,
        string? rootName = null)
        where TSubject : IInterceptorSubject
    {
        return services.AddKeyedOpcUaSubjectServer(
            name,
            sp => sp.GetRequiredService<TSubject>(),
            sp => CreateDefaultServerConfiguration(sp, connectorName, rootName));
    }

    public static IServiceCollection AddKeyedOpcUaSubjectServer(
        this IServiceCollection services,
        string name,
        Func<IServiceProvider, IInterceptorSubject> subjectSelector,
        Func<IServiceProvider, OpcUaServerConfiguration> configurationProvider)
    {
        GuardDuplicateKeyed<IOpcUaSubjectServer>(services, name);
        var key = Guid.NewGuid().ToString();
        RegisterServerCore(services, key, subjectSelector, configurationProvider);
        services.AddKeyedSingleton<IOpcUaSubjectServer>(name, (sp, _) =>
            sp.GetRequiredKeyedService<OpcUaSubjectServer>(key));
        return services;
    }

    private static void RegisterClientSourceCore(
        IServiceCollection services,
        string key,
        Func<IServiceProvider, IInterceptorSubject> subjectSelector,
        Func<IServiceProvider, OpcUaClientConfiguration> configurationProvider)
    {
        services
            .AddKeyedSingleton(key, (sp, _) => configurationProvider(sp))
            .AddKeyedSingleton(key, (sp, _) => subjectSelector(sp))
            .AddKeyedSingleton(key, (sp, _) =>
            {
                var subject = sp.GetRequiredKeyedService<IInterceptorSubject>(key);
                return new OpcUaSubjectClientSource(
                    subject,
                    sp.GetRequiredKeyedService<OpcUaClientConfiguration>(key),
                    sp.GetRequiredService<ILogger<OpcUaSubjectClientSource>>());
            })
            .AddSingleton<IHostedService>(sp => sp.GetRequiredKeyedService<OpcUaSubjectClientSource>(key));
    }

    private static void RegisterServerCore(
        IServiceCollection services,
        string key,
        Func<IServiceProvider, IInterceptorSubject> subjectSelector,
        Func<IServiceProvider, OpcUaServerConfiguration> configurationProvider)
    {
        services
            .AddKeyedSingleton(key, (sp, _) => configurationProvider(sp))
            .AddKeyedSingleton(key, (sp, _) => subjectSelector(sp))
            .AddKeyedSingleton(key, (sp, _) =>
            {
                var subject = sp.GetRequiredKeyedService<IInterceptorSubject>(key);
                return new OpcUaSubjectServer(
                    subject,
                    sp.GetRequiredKeyedService<OpcUaServerConfiguration>(key),
                    sp.GetRequiredService<ILogger<OpcUaSubjectServer>>());
            })
            .AddSingleton<IHostedService>(sp => sp.GetRequiredKeyedService<OpcUaSubjectServer>(key));
    }

    private static OpcUaClientConfiguration CreateDefaultClientConfiguration(
        IServiceProvider sp, string serverUrl, string connectorName, string[]? rootPath)
    {
        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
        var telemetryContext = DefaultTelemetry.Create(builder =>
            builder.Services.AddSingleton(loggerFactory));

        return new OpcUaClientConfiguration
        {
            ServerUrl = serverUrl,
            RootPath = rootPath,
            TypeResolver = new OpcUaTypeResolver(sp.GetRequiredService<ILogger<OpcUaTypeResolver>>()),
            ValueConverter = new OpcUaValueConverter(),
            SubjectFactory = new OpcUaSubjectFactory(DefaultSubjectFactory.Instance),
            TelemetryContext = telemetryContext,
            Mapper = new OpcUaCompositeMapper(
                new OpcUaPathProviderMapper(new AttributeBasedPathProvider(connectorName)),
                new OpcUaAttributeMapper(connectorName))
        };
    }

    private static OpcUaServerConfiguration CreateDefaultServerConfiguration(
        IServiceProvider sp, string connectorName, string? rootName)
    {
        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
        var telemetryContext = DefaultTelemetry.Create(builder =>
            builder.Services.AddSingleton(loggerFactory));

        return new OpcUaServerConfiguration
        {
            RootName = rootName,
            ValueConverter = new OpcUaValueConverter(),
            TelemetryContext = telemetryContext,
            Mapper = new OpcUaCompositeMapper(
                new OpcUaPathProviderMapper(new AttributeBasedPathProvider(connectorName)),
                new OpcUaAttributeMapper(connectorName))
        };
    }

    private static void GuardDuplicateUnkeyed<TService>(IServiceCollection services)
    {
        if (services.Any(d => d.ServiceType == typeof(TService) && d.ServiceKey is null))
        {
            throw new InvalidOperationException(
                $"An unnamed {typeof(TService).Name} is already registered. " +
                $"Use AddKeyed* overloads to register multiple instances with distinct names.");
        }
    }

    private static void GuardDuplicateKeyed<TService>(IServiceCollection services, string name)
    {
        if (services.Any(d => d.ServiceType == typeof(TService) && name.Equals(d.ServiceKey)))
        {
            throw new InvalidOperationException(
                $"A {typeof(TService).Name} with name '{name}' is already registered.");
        }
    }
}
