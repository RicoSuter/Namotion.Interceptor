using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Server;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Connectors.Paths;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Mqtt
{
    public class MqttServerConnector : BackgroundService, ISubjectDownstreamConnector
    {
        private readonly string _serverClientId = "Server_" + Guid.NewGuid().ToString("N");

        private readonly IInterceptorSubject _subject;
        private readonly IConnectorPathProvider _connectorPathProvider;
        private readonly ILogger _logger;

        private int _numberOfClients;
        private MqttServer? _mqttServer;

        private ConnectorUpdateBuffer? _updateBuffer;

        public int Port { get; set; } = 1883;

        public bool IsListening { get; private set; }

        public int? NumberOfClients => _numberOfClients;

        public MqttServerConnector(IInterceptorSubject subject,
            IConnectorPathProvider connectorPathProvider,
            ILogger<MqttServerConnector> logger)
        {
            _subject = subject;
            _connectorPathProvider = connectorPathProvider;
            _logger = logger;
        }

        public bool IsPropertyIncluded(RegisteredSubjectProperty property)
        {
            return _connectorPathProvider.IsPropertyIncluded(property);
        }

        public Task<IDisposable?> StartListeningAsync(ConnectorUpdateBuffer updateBuffer, CancellationToken cancellationToken)
        {
            _updateBuffer = updateBuffer;
            return Task.FromResult<IDisposable?>(null);
        }

        public int WriteBatchSize => 0;

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _mqttServer = new MqttServerFactory()
                .CreateMqttServer(new MqttServerOptions
                {
                    DefaultEndpointOptions =
                    {
                        IsEnabled = true,
                        Port = Port
                    }
                });

            _mqttServer.ClientConnectedAsync += ClientConnectedAsync;
            _mqttServer.ClientDisconnectedAsync += ClientDisconnectedAsync;
            _mqttServer.InterceptingPublishAsync += InterceptingPublishAsync;

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await _mqttServer.StartAsync();
                    IsListening = true;

                    await Task.Delay(Timeout.Infinite, stoppingToken);
                    await _mqttServer.StopAsync();

                    IsListening = false;
                }
                catch (Exception ex)
                {
                    IsListening = false;

                    if (ex is TaskCanceledException) return;

                    _logger.LogError(ex, "Error in MQTT server.");
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
            }
        }

        public async ValueTask WriteToSourceAsync(ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken)
        {
            for (var i = 0; i < changes.Length; i++)
            {
                var change = changes.Span[i];
                var registeredProperty = change.Property.TryGetRegisteredProperty();
                if (registeredProperty is not { HasChildSubjects: false })
                {
                    continue;
                }

                var path = registeredProperty.TryGetSourcePath(_connectorPathProvider, _subject);
                if (path is not null)
                {
                    await PublishPropertyValueAsync(path, change.GetNewValue<object?>(), cancellationToken);
                }
            }
        }

        private Task ClientConnectedAsync(ClientConnectedEventArgs arg)
        {
            Interlocked.Increment(ref _numberOfClients);

            Task.Run(async () =>
            {
                await Task.Delay(1000);
                foreach (var (path, property) in _subject
                    .TryGetRegisteredSubject()?
                    .GetAllProperties()
                    .Where(p => !p.HasChildSubjects)
                    .GetSourcePaths(_connectorPathProvider, _subject) ?? [])
                {
                    // TODO: Send only to new client
                    await PublishPropertyValueAsync(path, property.GetValue(), CancellationToken.None);
                }
            });

            return Task.CompletedTask;
        }

        private async ValueTask PublishPropertyValueAsync(string path, object? value, CancellationToken cancellationToken)
        {
            await _mqttServer!.InjectApplicationMessage(
                new InjectedMqttApplicationMessage(
                    new MqttApplicationMessage
                    {
                        Topic = path,
                        ContentType = "application/json",
                        PayloadSegment = new ArraySegment<byte>(
                            JsonSerializer.SerializeToUtf8Bytes(value)),
                    })
                {
                    SenderClientId = _serverClientId
                }, cancellationToken);
        }

        private Task InterceptingPublishAsync(InterceptingPublishEventArgs args)
        {
            if (args.ClientId == _serverClientId)
            {
                return Task.CompletedTask;
            }

            var path = args.ApplicationMessage.Topic;
            var payload = Encoding.UTF8.GetString(args.ApplicationMessage.Payload);

            var state = (connector: this, path, payload);
            _updateBuffer?.ApplyUpdate(state, static s =>
            {
                try
                {
                    var document = JsonDocument.Parse(s.payload);
                    s.connector._subject.UpdatePropertyValueFromSourcePath(s.path,
                        DateTimeOffset.UtcNow, // TODO: What timestamp to use here?
                        (property, _) => document.Deserialize(property.Type),
                        s.connector._connectorPathProvider, s.connector);
                }
                catch (Exception ex)
                {
                    s.connector._logger.LogError(ex, "Failed to deserialize MQTT payload.");
                }
            });

            return Task.CompletedTask;
        }

        private Task ClientDisconnectedAsync(ClientDisconnectedEventArgs arg)
        {
            Interlocked.Decrement(ref _numberOfClients);
            return Task.CompletedTask;
        }
    }
}
