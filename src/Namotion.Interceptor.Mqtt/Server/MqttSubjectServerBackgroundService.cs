using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Packets;
using MQTTnet.Server;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Performance;
using Namotion.Interceptor.Sources;
using Namotion.Interceptor.Sources.Paths;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Mqtt.Server;

/// <summary>
/// Background service that hosts an MQTT broker and publishes property changes.
/// </summary>
public class MqttSubjectServerBackgroundService : BackgroundService, IAsyncDisposable
{
    private static readonly ObjectPool<List<MqttUserProperty>> UserPropertiesPool
        = new(() => new List<MqttUserProperty>(1));

    private readonly string _serverClientId;
    private readonly IInterceptorSubject _subject;
    private readonly IInterceptorSubjectContext _context;
    private readonly MqttServerConfiguration _configuration;
    private readonly ILogger _logger;

    // Topic cache to avoid string allocations on hot path
    private readonly ConcurrentDictionary<RegisteredSubjectProperty, string> _topicCache = new();

    private int _numberOfClients;
    private int _disposed;
    private MqttServer? _mqttServer;

    /// <summary>
    /// Gets whether the MQTT server is listening.
    /// </summary>
    public bool IsListening { get; private set; }

    /// <summary>
    /// Gets the number of connected clients.
    /// </summary>
    public int NumberOfClients => _numberOfClients;

    public MqttSubjectServerBackgroundService(
        IInterceptorSubject subject,
        MqttServerConfiguration configuration,
        ILogger<MqttSubjectServerBackgroundService> logger)
    {
        _subject = subject ?? throw new ArgumentNullException(nameof(subject));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _context = subject.Context;
        _serverClientId = _configuration.ClientId;

        configuration.Validate();
    }

    private bool IsPropertyIncluded(RegisteredSubjectProperty property) =>
        _configuration.PathProvider.IsPropertyIncluded(property);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var changeQueueProcessor = new ChangeQueueProcessor(
            source: this,
            _context,
            propertyFilter: IsPropertyIncluded,
            writeHandler: WriteChangesAsync,
            _configuration.BufferTime,
            _logger);

        var options = new MqttServerOptionsBuilder()
            .WithDefaultEndpoint()
            .WithDefaultEndpointPort(_configuration.BrokerPort)
            .WithMaxPendingMessagesPerClient(_configuration.MaxPendingMessagesPerClient)
            .Build();

        _mqttServer = new MqttServerFactory().CreateMqttServer(options);

        _mqttServer.ClientConnectedAsync += ClientConnectedAsync;
        _mqttServer.ClientDisconnectedAsync += ClientDisconnectedAsync;
        _mqttServer.InterceptingPublishAsync += InterceptingPublishAsync;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _mqttServer.StartAsync().ConfigureAwait(false);
                IsListening = true;

                _logger.LogInformation("MQTT server started on port {Port}.", _configuration.BrokerPort);

                try
                {
                    await changeQueueProcessor.ProcessAsync(stoppingToken).ConfigureAwait(false);
                }
                finally
                {
                    await _mqttServer.StopAsync().ConfigureAwait(false);
                    IsListening = false;
                }
            }
            catch (Exception ex)
            {
                IsListening = false;

                if (ex is TaskCanceledException or OperationCanceledException)
                {
                    return;
                }

                _logger.LogError(ex, "Error in MQTT server.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask WriteChangesAsync(ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken)
    {
        var length = changes.Length;
        if (length == 0) return;

        var server = _mqttServer;
        if (server is null) return;

        // Rent arrays from pool to avoid allocations
        var changesPool = ArrayPool<SubjectPropertyChange>.Shared;
        var messagesPool = ArrayPool<InjectedMqttApplicationMessage>.Shared;
        var userPropsArrayPool = ArrayPool<List<MqttUserProperty>?>.Shared;

        var changesArray = changesPool.Rent(length);
        var messages = messagesPool.Rent(length);
        var userPropsArray = _configuration.SourceTimestampPropertyName is not null
            ? userPropsArrayPool.Rent(length)
            : null;
        var messageCount = 0;

        try
        {
            // Copy changes to rented array
            changes.Span.CopyTo(changesArray);

            // Build all messages first
            for (var i = 0; i < length; i++)
            {
                var change = changesArray[i];
                var registeredProperty = change.Property.TryGetRegisteredProperty();
                if (registeredProperty is not { HasChildSubjects: false })
                {
                    continue;
                }

                var topic = GetCachedTopic(registeredProperty);
                if (topic is null) continue;

                byte[] payload;
                try
                {
                    payload = _configuration.ValueConverter.Serialize(
                        change.GetNewValue<object?>(),
                        registeredProperty.Type);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to serialize value for topic {Topic}.", topic);
                    continue;
                }

                // Create message directly without builder to reduce allocations
                var message = new MqttApplicationMessage
                {
                    Topic = topic,
                    PayloadSegment = new ArraySegment<byte>(payload),
                    QualityOfServiceLevel = _configuration.DefaultQualityOfService,
                    Retain = _configuration.UseRetainedMessages
                };

                if (userPropsArray is not null)
                {
                    var userProps = UserPropertiesPool.Rent();
                    userProps.Clear();
                    userProps.Add(new MqttUserProperty(
                        _configuration.SourceTimestampPropertyName!,
                        change.ChangedTimestamp.ToUnixTimeMilliseconds().ToString()));
                    message.UserProperties = userProps;
                    userPropsArray[messageCount] = userProps;
                }

                messages[messageCount++] = new InjectedMqttApplicationMessage(message)
                {
                    SenderClientId = _serverClientId
                };
            }

            // Publish messages sequentially (less async overhead than Task.WhenAll)
            for (var i = 0; i < messageCount; i++)
            {
                await server.InjectApplicationMessage(messages[i], cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            // Return user property lists to pool
            if (userPropsArray is not null)
            {
                for (var i = 0; i < messageCount; i++)
                {
                    if (userPropsArray[i] is { } list)
                    {
                        UserPropertiesPool.Return(list);
                    }
                }
                userPropsArrayPool.Return(userPropsArray);
            }

            changesPool.Return(changesArray);
            messagesPool.Return(messages);
        }
    }

    private string? GetCachedTopic(RegisteredSubjectProperty property)
    {
        return _topicCache.GetOrAdd(property, static (p, state) =>
        {
            var (pathProvider, subject, topicPrefix) = state;
            var path = p.TryGetSourcePath(pathProvider, subject);
            if (path is null) return null!;

            return topicPrefix is null
                ? path
                : string.Concat(topicPrefix, "/", path);
        }, (_configuration.PathProvider, _subject, _configuration.TopicPrefix))!;
    }

    private Task ClientConnectedAsync(ClientConnectedEventArgs arg)
    {
        Interlocked.Increment(ref _numberOfClients);
        _logger.LogInformation("Client {ClientId} connected. Total clients: {Count}", arg.ClientId, _numberOfClients);

        // Publish all current property values to new client
        _ = PublishInitialStateAsync();

        return Task.CompletedTask;
    }

    private async Task PublishInitialStateAsync()
    {
        try
        {
            await Task.Delay(500).ConfigureAwait(false);

            var properties = _subject
                .TryGetRegisteredSubject()?
                .GetAllProperties()
                .Where(p => !p.HasChildSubjects)
                .GetSourcePaths(_configuration.PathProvider, _subject);

            if (properties is null) return;

            var server = _mqttServer;
            if (server is null) return;

            foreach (var (path, property) in properties)
            {
                var topic = _configuration.TopicPrefix is null
                    ? path
                    : string.Concat(_configuration.TopicPrefix, "/", path);

                var payload = _configuration.ValueConverter.Serialize(
                    property.GetValue(),
                    property.Type);

                var message = new MqttApplicationMessageBuilder()
                    .WithTopic(topic)
                    .WithPayload(payload)
                    .WithContentType("application/json")
                    .WithQualityOfServiceLevel(_configuration.DefaultQualityOfService)
                    .WithRetainFlag(_configuration.UseRetainedMessages)
                    .Build();

                await server.InjectApplicationMessage(
                    new InjectedMqttApplicationMessage(message) { SenderClientId = _serverClientId },
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish initial state to client.");
        }
    }

    private Task InterceptingPublishAsync(InterceptingPublishEventArgs args)
    {
        if (args.ClientId == _serverClientId)
        {
            return Task.CompletedTask;
        }

        var topic = args.ApplicationMessage.Topic;

        // Strip topic prefix if present
        var path = topic;
        if (_configuration.TopicPrefix is not null && topic.StartsWith(_configuration.TopicPrefix + "/", StringComparison.Ordinal))
        {
            path = topic.Substring(_configuration.TopicPrefix.Length + 1);
        }

        try
        {
            var payload = args.ApplicationMessage.Payload;
            var converter = _configuration.ValueConverter;
            _subject.UpdatePropertyValueFromSourcePath(
                path,
                DateTimeOffset.UtcNow,
                (property, _) => converter.Deserialize(payload, property.Type),
                _configuration.PathProvider,
                this);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deserialize MQTT payload for topic {Topic}.", topic);
        }

        return Task.CompletedTask;
    }

    private Task ClientDisconnectedAsync(ClientDisconnectedEventArgs arg)
    {
        Interlocked.Decrement(ref _numberOfClients);
        _logger.LogInformation("Client {ClientId} disconnected. Total clients: {Count}", arg.ClientId, _numberOfClients);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        var server = _mqttServer;
        if (server is not null)
        {
            server.ClientConnectedAsync -= ClientConnectedAsync;
            server.ClientDisconnectedAsync -= ClientDisconnectedAsync;
            server.InterceptingPublishAsync -= InterceptingPublishAsync;

            if (IsListening)
            {
                try
                {
                    await server.StopAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error stopping MQTT server.");
                }
            }

            server.Dispose();
            _mqttServer = null;
        }

        IsListening = false;
        Dispose();
    }
}
