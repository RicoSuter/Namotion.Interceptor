using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Server;
using Namotion.Interceptor.Sources;
using Namotion.Interceptor.Sources.Paths;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Mqtt
{
    public class MqttSubjectServerSource<TSubject> : BackgroundService, ISubjectSource
        where TSubject : IInterceptorSubject
    {
        private readonly string _serverClientId = "Server_" + Guid.NewGuid().ToString("N");

        private readonly TSubject _subject;
        private readonly ISourcePathProvider _sourcePathProvider;
        private readonly ILogger _logger;

        private int _numberOfClients = 0;
        private MqttServer? _mqttServer;

        private ISubjectSourceManager? _manager;

        public int Port { get; set; } = 1883;

        public bool IsListening { get; private set; }

        public int? NumberOfClients => _numberOfClients;

        public IInterceptorSubject Subject => _subject;

        public MqttSubjectServerSource(TSubject subject,
            ISourcePathProvider sourcePathProvider,
            ILogger<MqttSubjectServerSource<TSubject>> logger)
        {
            _subject = subject;
            _sourcePathProvider = sourcePathProvider;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _mqttServer = new MqttFactory()
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

        public Task<IDisposable?> InitializeAsync(ISubjectSourceManager manager, CancellationToken cancellationToken)
        {
            _manager = manager;
            return Task.FromResult<IDisposable?>(null);
        }

        public async Task WriteToSourceAsync(IEnumerable<PropertyChangedContext> updates, CancellationToken cancellationToken)
        {
            foreach (var (path, change) in updates.ConvertToSourcePaths(_sourcePathProvider, _subject))
            {
                await PublishPropertyValueAsync(path, change.NewValue, cancellationToken);
            }
        }

        private Task ClientConnectedAsync(ClientConnectedEventArgs arg)
        {
            _numberOfClients++;

            Task.Run(async () =>
            {
                await Task.Delay(1000);
                foreach (var (path, value, _) in SubjectUpdate
                    .CreateCompleteUpdate(_subject)
                    .ConvertToSourcePaths(_subject, _sourcePathProvider))
                {
                    // TODO: Send only to new client
                    await PublishPropertyValueAsync(path, value, CancellationToken.None);
                }
            });

            return Task.CompletedTask;
        }

        private async Task PublishPropertyValueAsync(string path, object? value, CancellationToken cancellationToken)
        {
            await _mqttServer!.InjectApplicationMessage(
                new InjectedMqttApplicationMessage(
                    new MqttApplicationMessage
                    {
                        Topic = path,
                        ContentType = "application/json",
                        PayloadSegment = new ArraySegment<byte>(
                            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))),
                    }) { SenderClientId = _serverClientId }, cancellationToken);
        }

        private Task InterceptingPublishAsync(InterceptingPublishEventArgs args)
        {
            if (args.ClientId == _serverClientId)
            {
                return Task.CompletedTask;
            }

            try
            {
                var path = args.ApplicationMessage.Topic;

                var payload = Encoding.UTF8.GetString(args.ApplicationMessage.PayloadSegment);
                var document = JsonDocument.Parse(payload);
                
                _manager?.EnqueueSubjectUpdate(() =>
                {
                    _subject.ApplyValueFromSource(path, (property, _) => document.Deserialize(property.Type), _sourcePathProvider);
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to deserialize MQTT payload.");
            }

            return Task.CompletedTask;
        }

        private Task ClientDisconnectedAsync(ClientDisconnectedEventArgs arg)
        {
            _numberOfClients--;
            return Task.CompletedTask;
        }
    }
}