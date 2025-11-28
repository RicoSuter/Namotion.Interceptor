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

    // TODO(memory): Might lead to memory leaks
    private readonly ConcurrentDictionary<PropertyReference, string?> _propertyToTopic = new();
    private readonly ConcurrentDictionary<string, PropertyReference?> _pathToProperty = new();
    private readonly ConcurrentBag<Task> _runningInitialStateTasks = new();

    private int _numberOfClients;
    private int _disposed;
    private int _isListening;
    private MqttServer? _mqttServer;

    /// <summary>
    /// Gets whether the MQTT server is listening.
    /// </summary>
    public bool IsListening => Volatile.Read(ref _isListening) == 1;

    /// <summary>
    /// Gets the number of connected clients.
    /// </summary>
    public int NumberOfClients => Volatile.Read(ref _numberOfClients);

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
                Volatile.Write(ref _isListening, 1);

                _logger.LogInformation("MQTT server started on port {Port}.", _configuration.BrokerPort);

                try
                {
                    using var changeQueueProcessor = new ChangeQueueProcessor(
                        source: this,
                        _context,
                        propertyFilter: IsPropertyIncluded,
                        writeHandler: WriteChangesAsync,
                        _configuration.BufferTime,
                        _logger);

                    await changeQueueProcessor.ProcessAsync(stoppingToken).ConfigureAwait(false);
                }
                finally
                {
                    await _mqttServer.StopAsync().ConfigureAwait(false);
                    Volatile.Write(ref _isListening, 0);
                }
            }
            catch (Exception ex)
            {
                Volatile.Write(ref _isListening, 0);

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
        var userPropertiesArray = _configuration.SourceTimestampPropertyName is not null
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

                var topic = TryGetTopicForProperty(change.Property, registeredProperty);
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

                if (userPropertiesArray is not null)
                {
                    var userProps = UserPropertiesPool.Rent();
                    userProps.Clear();
                    userProps.Add(new MqttUserProperty(
                        _configuration.SourceTimestampPropertyName!,
                        _configuration.SourceTimestampConverter(change.ChangedTimestamp)));
                    message.UserProperties = userProps;
                    userPropertiesArray[messageCount] = userProps;
                }

                messages[messageCount++] = new InjectedMqttApplicationMessage(message)
                {
                    SenderClientId = _serverClientId
                };
            }

            // TODO(nuget): Use batch API for better performance
            // if (messageCount > 0)
            // {
            //     await server.InjectApplicationMessages(
            //         new ArraySegment<InjectedMqttApplicationMessage>(messages, 0, messageCount),
            //         cancellationToken).ConfigureAwait(false);
            // }

            // TODO(nuget): Keep this as fallback for older NuGet versions
            for (var i = 0; i < messageCount; i++)
            {
                await server.InjectApplicationMessage(messages[i], cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            // Return user property lists to pool
            if (userPropertiesArray is not null)
            {
                for (var i = 0; i < messageCount; i++)
                {
                    if (userPropertiesArray[i] is { } list)
                    {
                        UserPropertiesPool.Return(list);
                    }
                }
                userPropsArrayPool.Return(userPropertiesArray);
            }

            changesPool.Return(changesArray);
            messagesPool.Return(messages);
        }
    }

    private string? TryGetTopicForProperty(PropertyReference propertyReference, RegisteredSubjectProperty property)
    {
        return _propertyToTopic.GetOrAdd(propertyReference, static (_, state) =>
        {
            var (p, pathProvider, subject, topicPrefix) = state;
            var path = p.TryGetSourcePath(pathProvider, subject);
            return path is null ? null : MqttHelper.BuildTopic(path, topicPrefix);
        }, (property, _configuration.PathProvider, _subject, _configuration.TopicPrefix))!;
    }

    private PropertyReference? TryGetPropertyForTopic(string path)
    {
        return _pathToProperty.GetOrAdd(path, static (p, state) =>
        {
            var (subject, pathProvider) = state;
            var (property, _) = subject.TryGetPropertyFromSourcePath(p, pathProvider);
            return property?.Reference;
        }, (_subject, _configuration.PathProvider));
    }

    private Task ClientConnectedAsync(ClientConnectedEventArgs arg)
    {
        var count = Interlocked.Increment(ref _numberOfClients);
        _logger.LogInformation("Client {ClientId} connected. Total clients: {Count}.", arg.ClientId, count);

        // Publish all current property values to new client
        var task = Task.Run(async () =>
        {
            try
            {
                if (Interlocked.CompareExchange(ref _disposed, 0, 0) == 0)
                {
                    await PublishInitialStateAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to publish initial state to client {ClientId}.", arg.ClientId);
            }
        });

        _runningInitialStateTasks.Add(task);

        return Task.CompletedTask;
    }

    private async Task PublishInitialStateAsync()
    {
        try
        {
            // Wait briefly to allow the client to complete subscription setup
            // before sending initial property values, ensuring no messages are lost.
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
                var topic = MqttHelper.BuildTopic(path, _configuration.TopicPrefix);

                var payload = _configuration.ValueConverter.Serialize(
                    property.GetValue(),
                    property.Type);

                var message = new MqttApplicationMessage
                {
                    Topic = topic,
                    PayloadSegment = new ArraySegment<byte>(payload),
                    ContentType = "application/json",
                    QualityOfServiceLevel = _configuration.DefaultQualityOfService,
                    Retain = _configuration.UseRetainedMessages
                };

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
        // Skip messages published by this server (injected messages may have null/empty ClientId)
        if (string.IsNullOrEmpty(args.ClientId) || args.ClientId == _serverClientId)
        {
            return Task.CompletedTask;
        }

        var topic = args.ApplicationMessage.Topic;
        var path = MqttHelper.StripTopicPrefix(topic, _configuration.TopicPrefix);

        if (TryGetPropertyForTopic(path) is not { } propertyReference)
        {
            return Task.CompletedTask;
        }

        var registeredProperty = propertyReference.TryGetRegisteredProperty();
        if (registeredProperty is null)
        {
            return Task.CompletedTask;
        }

        try
        {
            var payload = args.ApplicationMessage.Payload;
            var value = _configuration.ValueConverter.Deserialize(payload, registeredProperty.Type);

            var receivedTimestamp = DateTimeOffset.UtcNow;
            var sourceTimestamp = MqttHelper.ExtractSourceTimestamp(
                args.ApplicationMessage.UserProperties,
                _configuration.SourceTimestampPropertyName) ?? receivedTimestamp;

            propertyReference.SetValueFromSource(this, sourceTimestamp, receivedTimestamp, value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deserialize MQTT payload for topic {Topic}.", topic);
        }

        return Task.CompletedTask;
    }

    private Task ClientDisconnectedAsync(ClientDisconnectedEventArgs arg)
    {
        var count = Interlocked.Decrement(ref _numberOfClients);
        _logger.LogInformation("Client {ClientId} disconnected. Total clients: {Count}.", arg.ClientId, count);
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

            // Wait for all running initial state tasks to complete
            try
            {
                await Task.WhenAll(_runningInitialStateTasks.ToArray()).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error waiting for initial state tasks to complete.");
            }

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

        // Clear caches to allow GC of subject references
        _propertyToTopic.Clear();
        _pathToProperty.Clear();

        Volatile.Write(ref _isListening, 0);
        Dispose();
    }
}
