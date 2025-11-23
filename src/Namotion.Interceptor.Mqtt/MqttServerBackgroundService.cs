using System;
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
using Namotion.Interceptor.Sources;
using Namotion.Interceptor.Sources.Paths;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Mqtt
{
    public class MqttServerBackgroundService : BackgroundService
    {
        private readonly string _serverClientId = "Server_" + Guid.NewGuid().ToString("N");

        private readonly IInterceptorSubject _subject;
        private readonly IInterceptorSubjectContext _context;
        private readonly ISourcePathProvider _pathProvider;
        private readonly ILogger _logger;
        private readonly TimeSpan? _bufferTime;

        private int _numberOfClients;
        private MqttServer? _mqttServer;

        public int Port { get; set; } = 1883;

        public bool IsListening { get; private set; }

        public int? NumberOfClients => _numberOfClients;

        public MqttServerBackgroundService(IInterceptorSubject subject,
            ISourcePathProvider pathProvider,
            ILogger<MqttServerBackgroundService> logger,
            TimeSpan? bufferTime = null)
        {
            _subject = subject;
            _context = subject.Context;
            _pathProvider = pathProvider;
            _logger = logger;
            _bufferTime = bufferTime;
        }

        internal bool IsPropertyIncluded(RegisteredSubjectProperty property)
        {
            return _pathProvider.IsPropertyIncluded(property);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var changeQueueProcessor = new ChangeQueueProcessor(
                _context,
                IsPropertyIncluded,
                WriteChangesAsync,
                sourceToIgnore: this,
                _logger,
                _bufferTime);

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

                    // Process change queue until cancellation
                    await changeQueueProcessor.ProcessAsync(stoppingToken);

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

        private async ValueTask WriteChangesAsync(ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken)
        {
            for (var i = 0; i < changes.Length; i++)
            {
                var change = changes.Span[i];
                var registeredProperty = change.Property.TryGetRegisteredProperty();
                if (registeredProperty is not { HasChildSubjects: false })
                {
                    continue;
                }

                var path = registeredProperty.TryGetSourcePath(_pathProvider, _subject);
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
                    .GetSourcePaths(_pathProvider, _subject) ?? [])
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

            try
            {
                var document = JsonDocument.Parse(payload);
                _subject.UpdatePropertyValueFromSourcePath(path,
                    DateTimeOffset.UtcNow, // TODO: What timestamp to use here?
                    (property, _) => document.Deserialize(property.Type),
                    _pathProvider, this);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to deserialize MQTT payload.");
            }

            return Task.CompletedTask;
        }

        private Task ClientDisconnectedAsync(ClientDisconnectedEventArgs arg)
        {
            Interlocked.Decrement(ref _numberOfClients);
            return Task.CompletedTask;
        }
    }
}
