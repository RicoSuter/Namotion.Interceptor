using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.OpcUa;
using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.OpcUa.Server;
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
    /// <returns>An <see cref="IOpcUaSubjectServer"/> that runs the OPC UA server.</returns>
    public static IOpcUaSubjectServer CreateOpcUaServer(
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
    /// <returns>An <see cref="IOpcUaSubjectClientSource"/> that runs the OPC UA client.</returns>
    public static IOpcUaSubjectClientSource CreateOpcUaClientSource(
        this IInterceptorSubject subject,
        OpcUaClientConfiguration configuration,
        ILogger logger)
    {
        return new OpcUaSubjectClientSource(subject, configuration, logger);
    }

    public static OpcUaClientRegistration AddOpcUaSubjectClientSource<TSubject>(
        this IServiceCollection serviceCollection,
        string serverUrl,
        string sourceName,
        string? pathPrefix = null,
        string? rootName = null)
        where TSubject : IInterceptorSubject
    {
        return serviceCollection.AddOpcUaSubjectClientSource(
            serverUrl,
            sourceName,
            sp => sp.GetRequiredService<TSubject>(),
            pathPrefix,
            rootName);
    }

    public static OpcUaClientRegistration AddOpcUaSubjectClientSource(
        this IServiceCollection serviceCollection,
        string serverUrl,
        string sourceName,
        Func<IServiceProvider, IInterceptorSubject> subjectSelector,
        string? pathPrefix = null,
        string? rootName = null)
    {
        return serviceCollection.AddOpcUaSubjectClientSource(subjectSelector, sp =>
        {
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var telemetryContext = DefaultTelemetry.Create(builder =>
                builder.Services.AddSingleton(loggerFactory));

            return new OpcUaClientConfiguration
            {
                ServerUrl = serverUrl,
                RootName = rootName,
                TypeResolver = new OpcUaTypeResolver(sp.GetRequiredService<ILogger<OpcUaTypeResolver>>()),
                ValueConverter = new OpcUaValueConverter(),
                SubjectFactory = new OpcUaSubjectFactory(DefaultSubjectFactory.Instance),
                TelemetryContext = telemetryContext
            };
        });
    }

    public static OpcUaClientRegistration AddOpcUaSubjectClientSource(
        this IServiceCollection serviceCollection,
        Func<IServiceProvider, IInterceptorSubject> subjectSelector,
        Func<IServiceProvider, OpcUaClientConfiguration> configurationProvider)
    {
        var key = Guid.NewGuid().ToString();
        serviceCollection
            .AddKeyedSingleton(key, (sp, _) => configurationProvider(sp))
            .AddKeyedSingleton(key, (sp, _) => subjectSelector(sp))
            .AddKeyedSingleton<IOpcUaSubjectClientSource>(key, (sp, _) =>
            {
                var subject = sp.GetRequiredKeyedService<IInterceptorSubject>(key);
                return new OpcUaSubjectClientSource(
                    subject,
                    sp.GetRequiredKeyedService<OpcUaClientConfiguration>(key),
                    sp.GetRequiredService<ILogger<OpcUaSubjectClientSource>>());
            })
            .AddSingleton<IHostedService>(sp => sp.GetRequiredKeyedService<IOpcUaSubjectClientSource>(key))
            .AddSingleton<IHostedService>(sp =>
            {
                var configuration = sp.GetRequiredKeyedService<OpcUaClientConfiguration>(key);
                var subject = sp.GetRequiredKeyedService<IInterceptorSubject>(key);
                var clientSource = (OpcUaSubjectClientSource)sp.GetRequiredKeyedService<IOpcUaSubjectClientSource>(key);
                return new SubjectSourceBackgroundService(
                    clientSource,
                    subject.Context,
                    sp.GetRequiredService<ILogger<SubjectSourceBackgroundService>>(),
                    configuration.BufferTime,
                    configuration.RetryTime,
                    configuration.WriteRetryQueueSize);
            });

        return new OpcUaClientRegistration(key);
    }

    public static OpcUaServerRegistration AddOpcUaSubjectServer<TSubject>(
        this IServiceCollection serviceCollection,
        string sourceName,
        string? pathPrefix = null,
        string? rootName = null)
        where TSubject : IInterceptorSubject
    {
        return serviceCollection.AddOpcUaSubjectServer(
            sourceName,
            sp => sp.GetRequiredService<TSubject>(),
            pathPrefix,
            rootName);
    }

    public static OpcUaServerRegistration AddOpcUaSubjectServer(
        this IServiceCollection serviceCollection,
        string sourceName,
        Func<IServiceProvider, IInterceptorSubject> subjectSelector,
        string? pathPrefix = null,
        string? rootName = null)
    {
        return serviceCollection.AddOpcUaSubjectServer(subjectSelector, sp =>
        {
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var telemetryContext = DefaultTelemetry.Create(builder =>
                builder.Services.AddSingleton(loggerFactory));

            return new OpcUaServerConfiguration
            {
                RootName = rootName,
                ValueConverter = new OpcUaValueConverter(),
                TelemetryContext = telemetryContext
            };
        });
    }

    public static OpcUaServerRegistration AddOpcUaSubjectServer(
        this IServiceCollection serviceCollection,
        Func<IServiceProvider, IInterceptorSubject> subjectSelector,
        Func<IServiceProvider, OpcUaServerConfiguration> configurationProvider)
    {
        var key = Guid.NewGuid().ToString();
        serviceCollection
            .AddKeyedSingleton(key, (sp, _) => configurationProvider(sp))
            .AddKeyedSingleton(key, (sp, _) => subjectSelector(sp))
            .AddKeyedSingleton<IOpcUaSubjectServer>(key, (sp, _) =>
            {
                var subject = sp.GetRequiredKeyedService<IInterceptorSubject>(key);
                return new OpcUaSubjectServerBackgroundService(
                    subject,
                    sp.GetRequiredKeyedService<OpcUaServerConfiguration>(key),
                    sp.GetRequiredService<ILogger<OpcUaSubjectServerBackgroundService>>());
            })
            .AddSingleton<IHostedService>(sp => sp.GetRequiredKeyedService<IOpcUaSubjectServer>(key));

        return new OpcUaServerRegistration(key);
    }
}
