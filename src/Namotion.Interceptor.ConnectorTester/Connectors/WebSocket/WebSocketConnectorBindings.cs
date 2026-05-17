using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Namotion.Interceptor.ConnectorTester.Configuration;
using Namotion.Interceptor.ConnectorTester.Logging;
using Namotion.Interceptor.ConnectorTester.Model;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.WebSocket;

namespace Namotion.Interceptor.ConnectorTester.Connectors.WebSocket;

public sealed class WebSocketConnectorBindings : IConnectorBindings
{
    public ConnectorKind Kind => ConnectorKind.WebSocket;
    public int DefaultPort => 8080;

    public void RegisterServer(IServiceCollection services, TestNode root, int port)
    {
        services.AddSingleton(root);
        services.AddWebSocketSubjectServer(
            _ => root,
            config =>
            {
                config.Port = port;
                config.PathProvider = new AttributeBasedPathProvider("ws");
            });
    }

    public void RegisterClient(IServiceCollection services, TestNode root, ParticipantConfiguration participantConfiguration, int port)
    {
        services.AddSingleton<IHostedService>(_ => new ParticipantContext.Setter(participantConfiguration.Name));
        services.AddWebSocketSubjectClientSource(
            _ => root,
            config =>
            {
                config.ServerUri = new Uri($"ws://localhost:{port}/ws");
                config.ReconnectDelay = TimeSpan.FromSeconds(1);
                config.MaxReconnectDelay = TimeSpan.FromSeconds(10);
                config.PathProvider = new AttributeBasedPathProvider("ws");
                config.WriteRetryQueueSize = 10_000;
            });
    }
}
