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
using Namotion.Interceptor.Sources;
using Namotion.Interceptor.Sources.Paths;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Mqtt.Server;

/// <summary>
/// Background service that hosts an MQTT broker and publishes property changes.
/// </summary>
public class MqttSubjectServerBackgroundService : BackgroundService, IAsyncDisposable
{
    // NOTE: We cannot pool UserProperties here because InjectApplicationMessages queues messages
    // asynchronously. The server may still be serializing packets after this method returns,
    // which would cause a race condition if we returned the lists to a pool.

    private readonly string _serverClientId;
    private readonly IInterceptorSubject _subject;
    private readonly IInterceptorSubjectContext _context;
    private readonly MqttServerConfiguration _configuration;
    private readonly ILogger _logger;

    private readonly ConcurrentDictionary<PropertyReference, string?> _propertyToTopic = new();
    private readonly ConcurrentDictionary<string, PropertyReference?> _pathToProperty = new();

    private LifecycleInterceptor? _lifecycleInterceptor;
    
    private readonly List<Task> _runningInitialStateTasks = [];
    private readonly Lock _initialStateTasksLock = new();

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
        _lifecycleInterceptor = _context.TryGetLifecycleInterceptor();
        if (_lifecycleInterceptor is not null)
        {
            _lifecycleInterceptor.SubjectDetached += OnSubjectDetached;
        }

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

        var messagesPool = ArrayPool<InjectedMqttApplicationMessage>.Shared;
        var messages = messagesPool.Rent(length);
        var messageCount = 0;

        try
        {
            var changesSpan = changes.Span;
            var timestampPropertyName = _configuration.SourceTimestampPropertyName;

            // Build all messages first
            for (var i = 0; i < length; i++)
            {
                var change = changesSpan[i];
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

                // Create new list for each message - cannot pool because server queues messages asynchronously
                if (timestampPropertyName is not null)
                {
                    message.UserProperties =
                    [
                        new MqttUserProperty(
                            timestampPropertyName,
                            _configuration.SourceTimestampConverter(change.ChangedTimestamp))
                    ];
                }

                messages[messageCount++] = new InjectedMqttApplicationMessage(message)
                {
                    SenderClientId = _serverClientId
                };
            }

            if (messageCount > 0)
            {
#if USE_LOCAL_MQTTNET
                await server.InjectApplicationMessages(
                    new ArraySegment<InjectedMqttApplicationMessage>(messages, 0, messageCount),
                    cancellationToken).ConfigureAwait(false);
#else
                for (var i = 0; i < messageCount; i++)
                {
                    await server.InjectApplicationMessage(messages[i], cancellationToken).ConfigureAwait(false);
                }
#endif
            }
        }
        finally
        {
            messagesPool.Return(messages);
        }
    }

    private string? TryGetTopicForProperty(PropertyReference propertyReference, RegisteredSubjectProperty property)
    {
        if (_propertyToTopic.TryGetValue(propertyReference, out var cachedTopic))
        {
            return cachedTopic;
        }

        // Slow path: compute topic and add to cache
        var path = property.TryGetSourcePath(_configuration.PathProvider, _subject);
        var topic = path is null ? null : MqttHelper.BuildTopic(path, _configuration.TopicPrefix);

        // Add first, then validate (guarantees no memory leak)
        if (_propertyToTopic.TryAdd(propertyReference, topic))
        {
            var registeredSubject = propertyReference.Subject.TryGetRegisteredSubject();
            if (registeredSubject is null || registeredSubject.ReferenceCount <= 0)
            {
                _propertyToTopic.TryRemove(propertyReference, out _);
            }
        }

        return topic;
    }

    private PropertyReference? TryGetPropertyForTopic(string path)
    {
        // Fast path: return cached value immediately (single lookup)
        if (_pathToProperty.TryGetValue(path, out var cachedProperty))
        {
            return cachedProperty;
        }

        // Slow path: compute property reference and add to cache
        var (property, _) = _subject.TryGetPropertyFromSourcePath(path, _configuration.PathProvider);
        var propertyReference = property?.Reference;

        // Add first, then validate (guarantees no memory leak)
        if (_pathToProperty.TryAdd(path, propertyReference))
        {
            if (propertyReference is { } propRef)
            {
                var registeredSubject = propRef.Subject.TryGetRegisteredSubject();
                if (registeredSubject is null || registeredSubject.ReferenceCount <= 0)
                {
                    // Subject detached - remove the entry we just added
                    _pathToProperty.TryRemove(path, out _);
                }
            }
        }

        return propertyReference;
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

        lock (_initialStateTasksLock)
        {
            // Clean up completed tasks to prevent memory leak
            _runningInitialStateTasks.RemoveAll(t => t.IsCompleted);
            _runningInitialStateTasks.Add(task);
        }

        return Task.CompletedTask;
    }

    private async Task PublishInitialStateAsync()
    {
        try
        {
            // Wait for the client to complete subscription setup before sending initial values.
            // This delay is configurable; set to zero to rely on retained messages only.
            var delay = _configuration.InitialStateDelay;
            if (delay <= TimeSpan.Zero)
            {
                return;
            }

            await Task.Delay(delay).ConfigureAwait(false);

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

    private void OnSubjectDetached(SubjectLifecycleChange change)
    {
        // Clean up cache entries for detached subjects
        foreach (var kvp in _propertyToTopic)
        {
            if (kvp.Key.Subject == change.Subject)
            {
                _propertyToTopic.TryRemove(kvp.Key, out _);
            }
        }

        foreach (var kvp in _pathToProperty)
        {
            if (kvp.Value?.Subject == change.Subject)
            {
                _pathToProperty.TryRemove(kvp.Key, out _);
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        if (_lifecycleInterceptor is not null)
        {
            _lifecycleInterceptor.SubjectDetached -= OnSubjectDetached;
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
                Task[] tasksSnapshot;
                lock (_initialStateTasksLock)
                {
                    tasksSnapshot = _runningInitialStateTasks.ToArray();
                }
                await Task.WhenAll(tasksSnapshot).ConfigureAwait(false);
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
