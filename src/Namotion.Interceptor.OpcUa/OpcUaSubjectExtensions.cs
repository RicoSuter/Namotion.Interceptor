using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.OpcUa;
using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.OpcUa.Server;
using Namotion.Interceptor.Sources;
using Namotion.Interceptor.Sources.Paths;

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
    /// <returns>An IHostedService that runs the OPC UA server.</returns>
    public static IHostedService CreateOpcUaServer(
        this IInterceptorSubject subject,
        OpcUaServerConfiguration configuration,
        ILogger logger)
    {
        return new OpcUaSubjectServerBackgroundService(subject, configuration, logger);
    }

    /// <summary>
    /// Creates an OPC UA client source hosted service for the given subject.
    /// The client synchronizes the subject's properties with an OPC UA server.
    /// </summary>
    /// <param name="subject">The subject to synchronize with OPC UA.</param>
    /// <param name="configuration">The OPC UA client configuration.</param>
    /// <param name="logger">The logger instance.</param>
    /// <returns>An IHostedService that runs the OPC UA client.</returns>
    public static IHostedService CreateOpcUaClientSource(
        this IInterceptorSubject subject,
        OpcUaClientConfiguration configuration,
        ILogger logger)
    {
        return new OpcUaSubjectClientSource(subject, configuration, logger);
    }

    /// <summary>
    /// Adds an OPC UA client for the specified subject type with optional configuration.
    /// </summary>
    /// <param name="serviceCollection">The service collection.</param>
    /// <param name="serverUrl">The OPC UA server endpoint URL.</param>
    /// <param name="sourceName">The source name used for path mapping.</param>
    /// <param name="rootName">Optional root node name under ObjectsFolder.</param>
    /// <param name="configure">Optional configuration callback.</param>
    public static IServiceCollection AddOpcUaSubjectClient<TSubject>(
        this IServiceCollection serviceCollection,
        string serverUrl,
        string sourceName,
        string? rootName = null,
        Action<OpcUaClientConfiguration>? configure = null)
        where TSubject : IInterceptorSubject
    {
        return serviceCollection.AddOpcUaSubjectClient(
            sp => sp.GetRequiredService<TSubject>(),
            sp =>
            {
                var configuration = new OpcUaClientConfiguration
                {
                    ServerUrl = serverUrl,
                    RootName = rootName,
                    PathProvider = new AttributeBasedSourcePathProvider(sourceName, ".", null),
                    TypeResolver = new OpcUaTypeResolver(sp.GetRequiredService<ILogger<OpcUaTypeResolver>>()),
                    ValueConverter = new OpcUaValueConverter(),
                    SubjectFactory = new OpcUaSubjectFactory(DefaultSubjectFactory.Instance)
                };
                configure?.Invoke(configuration);
                return configuration;
            });
    }

    public static IServiceCollection AddOpcUaSubjectClient(
        this IServiceCollection serviceCollection,
        Func<IServiceProvider, IInterceptorSubject> subjectSelector,
        Func<IServiceProvider, OpcUaClientConfiguration> configurationProvider)
    {
        var key = Guid.NewGuid().ToString();
        return serviceCollection
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
            .AddSingleton<IHostedService>(sp => sp.GetRequiredKeyedService<OpcUaSubjectClientSource>(key))
            .AddSingleton<IHostedService>(sp =>
            {
                var configuration = sp.GetRequiredKeyedService<OpcUaClientConfiguration>(key);
                var subject = sp.GetRequiredKeyedService<IInterceptorSubject>(key);
                return new SubjectSourceBackgroundService(
                    sp.GetRequiredKeyedService<OpcUaSubjectClientSource>(key),
                    subject.Context,
                    sp.GetRequiredService<ILogger<SubjectSourceBackgroundService>>(),
                    configuration.BufferTime,
                    configuration.RetryTime,
                    configuration.WriteRetryQueueSize);
            });
    }

    /// <summary>
    /// Adds an OPC UA server for the specified subject type with optional configuration.
    /// </summary>
    /// <param name="serviceCollection">The service collection.</param>
    /// <param name="sourceName">The source name used for path mapping.</param>
    /// <param name="rootName">Optional root folder name under ObjectsFolder.</param>
    /// <param name="configure">Optional configuration callback.</param>
    public static IServiceCollection AddOpcUaSubjectServer<TSubject>(
        this IServiceCollection serviceCollection,
        string sourceName,
        string? rootName = null,
        Action<OpcUaServerConfiguration>? configure = null)
        where TSubject : IInterceptorSubject
    {
        return serviceCollection.AddOpcUaSubjectServer(
            sp => sp.GetRequiredService<TSubject>(),
            _ =>
            {
                var configuration = new OpcUaServerConfiguration
                {
                    RootName = rootName,
                    PathProvider = new AttributeBasedSourcePathProvider(sourceName, ".", null),
                    ValueConverter = new OpcUaValueConverter()
                };
                configure?.Invoke(configuration);
                return configuration;
            });
    }

    public static IServiceCollection AddOpcUaSubjectServer(
        this IServiceCollection serviceCollection,
        Func<IServiceProvider, IInterceptorSubject> subjectSelector,
        Func<IServiceProvider, OpcUaServerConfiguration> configurationProvider)
    {
        var key = Guid.NewGuid().ToString();
        return serviceCollection
            .AddKeyedSingleton(key, (sp, _) => configurationProvider(sp))
            .AddKeyedSingleton(key, (sp, _) => subjectSelector(sp))
            .AddKeyedSingleton(key, (sp, _) =>
            {
                var subject = sp.GetRequiredKeyedService<IInterceptorSubject>(key);
                return new OpcUaSubjectServerBackgroundService(
                    subject,
                    sp.GetRequiredKeyedService<OpcUaServerConfiguration>(key),
                    sp.GetRequiredService<ILogger<OpcUaSubjectServerBackgroundService>>());
            })
            .AddSingleton<IHostedService>(sp => sp.GetRequiredKeyedService<OpcUaSubjectServerBackgroundService>(key));
    }
}
