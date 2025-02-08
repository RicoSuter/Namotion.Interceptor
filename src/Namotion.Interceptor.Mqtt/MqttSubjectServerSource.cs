using System;
using System.Collections.Concurrent;
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
using Namotion.Interceptor.Sources;

namespace Namotion.Interceptor.Mqtt
{
    public class MqttSubjectServerSource<TSubject> : BackgroundService, ISubjectSource
        where TSubject : IInterceptorSubject
    {
        private readonly TSubject _subject;
        private readonly ISourcePathProvider _sourcePathProvider;
        private readonly ILogger _logger;

        private int _numberOfClients = 0;
        private MqttServer? _mqttServer;

        private Action<PropertyPathReference>? _propertyUpdateAction;
        
        private readonly ConcurrentDictionary<PropertyReference, object?> _state = new();

        public int Port { get; set; } = 1883;

        public bool IsListening { get; private set; }

        public int? NumberOfClients => _numberOfClients;

        // TODO: Inject IInterceptorContext<TProxy> so that multiple contexts are supported.
        public MqttSubjectServerSource(
            TSubject subject,
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

        public Task<IDisposable?> InitializeAsync(IEnumerable<PropertyPathReference> properties, Action<PropertyPathReference> propertyUpdateAction, CancellationToken cancellationToken)
        {
            _propertyUpdateAction = propertyUpdateAction;
            return Task.FromResult<IDisposable?>(null);
        }

        public Task<IEnumerable<PropertyPathReference>> ReadAsync(IEnumerable<PropertyPathReference> properties, CancellationToken cancellationToken)
        {
            var propertyPaths = properties
                .Select(p => p.Path)
                .ToList();

            return Task.FromResult<IEnumerable<PropertyPathReference>>(_state
                .Where(s => propertyPaths.Contains(_sourcePathProvider.TryGetSourcePropertyPath(s.Key) ?? string.Empty))
                .Select(s => new PropertyPathReference(s.Key, null!, s.Value))
                .ToList());
        }

        public async Task WriteAsync(IEnumerable<PropertyPathReference> propertyChanges, CancellationToken cancellationToken)
        {
            foreach (var property in propertyChanges)
            {
                await _mqttServer!.InjectApplicationMessage(
                    new InjectedMqttApplicationMessage(
                        new MqttApplicationMessage
                        {
                            Topic = property.Path,
                            ContentType = "application/json",
                            PayloadSegment = new ArraySegment<byte>(
                               Encoding.UTF8.GetBytes(JsonSerializer.Serialize(property.Value)))
                        }), cancellationToken);
            }
        }

        public string? TryGetSourcePropertyPath(PropertyReference property)
        {
            return _sourcePathProvider.TryGetSourcePropertyPath(property);
        }

        private Task ClientConnectedAsync(ClientConnectedEventArgs arg)
        {
            _numberOfClients++;

            Task.Run(async () =>
            {
                await Task.Delay(1000);
                foreach (var property in _subject
                    .Context
                    .GetService<ISubjectRegistry>()
                    .GetProperties() // TODO: Only properties of proxy and children
                    .Where(p => p.HasGetter))
                {
                    await PublishPropertyValueAsync(property.GetValue(), property.Property);
                }
            });

            return Task.CompletedTask;
        }

        private async Task PublishPropertyValueAsync(object? value, PropertyReference property)
        {
            var sourcePath = _sourcePathProvider.TryGetSourcePropertyPath(property);
            if (sourcePath != null)
            {
                await _mqttServer!.InjectApplicationMessage(new InjectedMqttApplicationMessage(new MqttApplicationMessage
                {
                    Topic = sourcePath,
                    ContentType = "application/json",
                    PayloadSegment = new ArraySegment<byte>(
                        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)))
                }));
            }
        }

        private Task InterceptingPublishAsync(InterceptingPublishEventArgs args)
        {
            try
            {
                var sourcePath = args.ApplicationMessage.Topic;
                var property = _subject
                    .Context
                    .GetService<ISubjectRegistry>()
                    .GetProperties()  // TODO: Only properties of proxy and children
                    .SingleOrDefault(p => _sourcePathProvider.TryGetSourcePropertyPath(p.Property) == sourcePath);

                if (property is not null)
                {
                    var payload = Encoding.UTF8.GetString(args.ApplicationMessage.PayloadSegment);
                    var document = JsonDocument.Parse(payload);
                    var value = document.Deserialize(property.Type);

                    _state[property.Property] = value;
                    _propertyUpdateAction?.Invoke(new PropertyPathReference(property.Property, sourcePath, value));
                }
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