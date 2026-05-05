using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Packets;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Connectors.Paths;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Performance;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Mqtt.Client;

/// <summary>
/// MQTT client source that subscribes to an MQTT broker and synchronizes properties.
/// </summary>
internal sealed class MqttSubjectClientSource : SubjectSourceBase, IFaultInjectable, IAsyncDisposable
{
    // Pool for UserProperties lists to avoid allocations on hot path
    private static readonly ObjectPool<List<MqttUserProperty>> UserPropertiesPool
        = new(() => new List<MqttUserProperty>(1));

    private readonly IInterceptorSubject _subject;
    private readonly MqttClientConfiguration _configuration;
    private readonly ILogger _logger;

    private readonly MqttClientFactory _factory;

    private readonly ConcurrentDictionary<string, PropertyReference?> _topicToProperty = new();
    private readonly ConcurrentDictionary<PropertyReference, string?> _propertyToTopic = new();

    private readonly SourceOwnershipManager _ownership;

    private volatile IMqttClient? _client;
    private volatile SubjectPropertyWriter? _propertyWriter;
    private volatile MqttConnectionMonitor? _connectionMonitor;

    private int _disposed;
    private volatile bool _isForceKill;
    private volatile CancellationTokenSource? _forceKillCts;

    public MqttSubjectClientSource(
        IInterceptorSubject subject,
        MqttClientConfiguration configuration,
        ILogger<MqttSubjectClientSource> logger)
        : base(subject.Context, logger, configuration.BufferTime, configuration.RetryTime, configuration.WriteRetryQueueSize)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);

        _subject = subject;
        _configuration = configuration;
        _logger = logger;

        _factory = new MqttClientFactory();
        _ownership = new SourceOwnershipManager(
            this,
            onSubjectDetaching: CleanupTopicCachesForSubject);

        configuration.Validate();
    }

    private void CleanupTopicCachesForSubject(IInterceptorSubject subject)
    {
        // Clean up topic caches for the detached subject
        // TODO(perf): O(n) scan over all cached entries per detached subject.
        // Consider adding a reverse index for O(1) cleanup if profiling shows this as a bottleneck.
        foreach (var kvp in _propertyToTopic)
        {
            if (kvp.Key.Subject == subject)
            {
                _propertyToTopic.TryRemove(kvp.Key, out _);
            }
        }

        foreach (var kvp in _topicToProperty)
        {
            if (kvp.Value?.Subject == subject)
            {
                _topicToProperty.TryRemove(kvp.Key, out _);
            }
        }
    }

    /// <inheritdoc />
    public override IInterceptorSubject RootSubject => _subject;

    /// <inheritdoc />
    public override int WriteBatchSize => 0; // No server-imposed limit for MQTT

    /// <inheritdoc />
    protected override async Task<IAsyncDisposable?> StartListeningAsync(SubjectPropertyWriter propertyWriter, CancellationToken cancellationToken)
    {
        _propertyWriter = propertyWriter;
        _logger.LogInformation("Connecting to MQTT broker at {Host}:{Port}.", _configuration.BrokerHost, _configuration.BrokerPort);

        IMqttClient? client = null;
        MqttConnectionMonitor? connectionMonitor = null;
        CancellationTokenSource? monitorCts = null;
        Task? monitorTask = null;
        try
        {
            client = _factory.CreateMqttClient();
            _client = client;
            client.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;
            client.DisconnectedAsync += OnDisconnectedAsync;

            await client.ConnectAsync(GetClientOptions(), cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Connected to MQTT broker successfully.");

            await SubscribeToPropertiesAsync(cancellationToken).ConfigureAwait(false);

            connectionMonitor = new MqttConnectionMonitor(
                client,
                _configuration,
                GetClientOptions,
                async ct => await OnReconnectedAsync(ct).ConfigureAwait(false),
                () =>
                {
                    _propertyWriter?.StartBuffering();
                    return Task.CompletedTask;
                }, _logger);
            _connectionMonitor = connectionMonitor;

            // Spawn the connection-monitor loop tied to listen lifetime.
            // Disposing the returned lifetime cancels and awaits this task.
            // Kill faults cancel the inner forceKillCts so the monitor exits and
            // the kill-restart wrapper re-enters MonitorConnectionAsync (preserving
            // the previous BackgroundService.ExecuteAsync behavior).
            monitorCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var monitorForLifetime = connectionMonitor;
            var clientForLifetime = client;
            monitorTask = Task.Run(() => RunMonitorWithKillRestartAsync(monitorForLifetime, monitorCts.Token), CancellationToken.None);

            return new MqttConnectionLifetime(this, clientForLifetime, monitorForLifetime, monitorCts, monitorTask, _logger);
        }
        catch
        {
            await CleanupOnListenFailureAsync(client, connectionMonitor, monitorCts, monitorTask).ConfigureAwait(false);
            throw;
        }
    }

    private async Task RunMonitorWithKillRestartAsync(MqttConnectionMonitor connectionMonitor, CancellationToken stoppingToken)
    {
        // Preserves the previous ExecuteAsync kill-restart loop: the outer loop
        // re-enters MonitorConnectionAsync after a Kill cancels _forceKillCts.
        // stoppingToken (from the lifetime) breaks out for good on host shutdown
        // or when the listen lifetime is disposed by the base retry path.
        while (!stoppingToken.IsCancellationRequested)
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            _forceKillCts = cts;
            var linkedToken = cts.Token;

            try
            {
                await connectionMonitor.MonitorConnectionAsync(linkedToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException) when (_isForceKill)
            {
                _logger.LogWarning("MQTT client force-killed. Restarting...");
            }
            finally
            {
                _isForceKill = false;
                _forceKillCts = null;
                cts.Dispose();
            }
        }
    }

    private async Task CleanupOnListenFailureAsync(
        IMqttClient? client,
        MqttConnectionMonitor? connectionMonitor,
        CancellationTokenSource? monitorCts,
        Task? monitorTask)
    {
        if (monitorCts is not null)
        {
            try { await monitorCts.CancelAsync().ConfigureAwait(false); } catch { /* ignore */ }
        }
        if (monitorTask is not null)
        {
            try { await monitorTask.ConfigureAwait(false); } catch { /* logged elsewhere or ignored on cleanup path */ }
        }
        if (monitorCts is not null)
        {
            try { monitorCts.Dispose(); } catch { /* ignore */ }
        }
        if (connectionMonitor is not null)
        {
            try { await connectionMonitor.DisposeAsync().ConfigureAwait(false); } catch { /* ignore */ }
            _connectionMonitor = null;
        }
        if (client is not null)
        {
            client.ApplicationMessageReceivedAsync -= OnMessageReceivedAsync;
            client.DisconnectedAsync -= OnDisconnectedAsync;
            try
            {
                if (client.IsConnected)
                {
                    await client.DisconnectAsync().ConfigureAwait(false);
                }
            }
            catch { /* ignore on cleanup */ }
            try { client.Dispose(); } catch { /* ignore */ }
            _client = null;
        }
    }

    /// <inheritdoc />
    protected override Task<Action?> LoadInitialStateAsync(CancellationToken cancellationToken)
    {
        // Retained messages are received through the normal message handler: No separate initial load needed/possible
        return Task.FromResult<Action?>(null);
    }

    /// <inheritdoc />
    protected override async ValueTask<WriteResult> WriteChangesToSourceAsync(ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken)
    {
        try
        {
            var client = _client;
            if (client is null || !client.IsConnected)
            {
                return WriteResult.Failure(changes, new InvalidOperationException("MQTT client is not connected."));
            }

            var length = changes.Length;
            if (length == 0) return WriteResult.Success;

            var messagesPool = ArrayPool<MqttApplicationMessage>.Shared;
            var userPropsArrayPool = ArrayPool<List<MqttUserProperty>?>.Shared;
            var changeIndicesPool = ArrayPool<int>.Shared;

            var messages = messagesPool.Rent(length);
            var changeIndices = changeIndicesPool.Rent(length); // Track which original change index each message corresponds to
            var userPropertiesArray = _configuration.SourceTimestampPropertyName is not null
                ? userPropsArrayPool.Rent(length)
                : null;

            var messageCount = 0;
            try
            {
                var changesSpan = changes.Span;

                // Build all messages first
                for (var i = 0; i < length; i++)
                {
                    var change = changesSpan[i];
                    var property = change.Property.TryGetRegisteredProperty();
                    if (property is null || property.CanContainSubjects)
                    {
                        continue;
                    }

                    var topic = TryGetTopicForProperty(change.Property, property);
                    if (topic is null) continue;

                    byte[] payload;
                    try
                    {
                        payload = _configuration.ValueConverter.Serialize(
                            change.GetNewValue<object?>(),
                            property.Type);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to serialize value for property {PropertyName}.", property.Name);
                        continue;
                    }

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
                            _configuration.SourceTimestampSerializer(change.ChangedTimestamp)));
                        message.UserProperties = userProps;
                        userPropertiesArray[messageCount] = userProps;
                    }

                    changeIndices[messageCount] = i;
                    messages[messageCount++] = message;
                }

                if (messageCount <= 0)
                    return WriteResult.Success;

                Exception? publishException = null;
                var failedStartIndex = messageCount; // Index where failures start (in message space)

#if USE_LOCAL_MQTTNET
                try
                {
                    await client.PublishMessagesAsync(
                        new ArraySegment<MqttApplicationMessage>(messages, 0, messageCount),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    publishException = ex;
                    failedStartIndex = 0; // All failed
                }
#else
                for (var i = 0; i < messageCount; i++)
                {
                    try
                    {
                        await client.PublishAsync(messages[i], cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        publishException = ex;
                        failedStartIndex = i; // This and all remaining failed
                        break;
                    }
                }
#endif

                if (publishException is not null)
                {
                    // Build failed changes array from the failed message indices
                    var failedCount = messageCount - failedStartIndex;
                    var failedChanges = new SubjectPropertyChange[failedCount];
                    for (var i = 0; i < failedCount; i++)
                    {
                        failedChanges[i] = changes.Span[changeIndices[failedStartIndex + i]];
                    }
                    return failedStartIndex > 0
                        ? WriteResult.PartialFailure(failedChanges, publishException)
                        : WriteResult.Failure(failedChanges, publishException);
                }

                return WriteResult.Success;
            }
            finally
            {
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

                changeIndicesPool.Return(changeIndices);
                messagesPool.Return(messages);
            }
        }
        catch (Exception ex)
        {
            return WriteResult.Failure(changes, ex);
        }
    }

    private async Task SubscribeToPropertiesAsync(CancellationToken cancellationToken)
    {
        var registeredSubject = _subject.TryGetRegisteredSubject();
        if (registeredSubject is null)
        {
            _logger.LogWarning("Subject is not registered. No MQTT subscriptions will be created.");
            return;
        }

        var properties = registeredSubject
            .GetAllProperties()
            .Where(p => !p.CanContainSubjects && _configuration.PathProvider.IsPropertyIncluded(p))
            .ToList();

        if (properties.Count == 0)
        {
            _logger.LogWarning("No MQTT properties found to subscribe.");
            return;
        }

        var subscribeOptionsBuilder = _factory.CreateSubscribeOptionsBuilder();

        foreach (var property in properties)
        {
            var topic = TryGetTopicForProperty(property.Reference, property);
            if (topic is null) continue;

            if (!_ownership.ClaimSource(property.Reference))
            {
                _logger.LogError(
                    "Property {Subject}.{Property} already owned by another source. Skipping MQTT subscription.",
                    property.Subject.GetType().Name, property.Name);
                continue;
            }

            _topicToProperty[topic] = property.Reference;
            subscribeOptionsBuilder.WithTopicFilter(f => f
                .WithTopic(topic)
                .WithQualityOfServiceLevel(_configuration.DefaultQualityOfService));
        }

        await _client!.SubscribeAsync(subscribeOptionsBuilder.Build(), cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Subscribed to {Count} MQTT topics.", properties.Count);
    }

    private string? TryGetTopicForProperty(PropertyReference propertyReference, RegisteredSubjectProperty property)
    {
        if (_propertyToTopic.TryGetValue(propertyReference, out var cachedTopic))
        {
            return cachedTopic;
        }

        var path = property.TryGetPath(_configuration.PathProvider, _subject);
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

    private PropertyReference? TryGetPropertyForTopic(string topic)
    {
        if (_topicToProperty.TryGetValue(topic, out var cachedProperty))
        {
            return cachedProperty;
        }

        var path = MqttHelper.StripTopicPrefix(topic, _configuration.TopicPrefix);
        var (property, _) = _subject.TryGetPropertyFromPath(path, _configuration.PathProvider);
        var propertyReference = property?.Reference;

        // Add first, then validate (guarantees no memory leak)
        if (_topicToProperty.TryAdd(topic, propertyReference))
        {
            if (propertyReference is { } propRef)
            {
                var registeredSubject = propRef.Subject.TryGetRegisteredSubject();
                if (registeredSubject is null || registeredSubject.ReferenceCount <= 0)
                {
                    _topicToProperty.TryRemove(topic, out _);
                }
            }
        }

        return propertyReference;
    }

    internal Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        var topic = e.ApplicationMessage.Topic;
        if (TryGetPropertyForTopic(topic) is not { } propertyReference)
        {
            return Task.CompletedTask;
        }

        var registeredProperty = propertyReference.TryGetRegisteredProperty();
        if (registeredProperty is null)
        {
            return Task.CompletedTask;
        }

        object? value;
        try
        {
            var payload = e.ApplicationMessage.Payload;
            value = _configuration.ValueConverter.Deserialize(payload, registeredProperty.Type);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deserialize MQTT message for topic {Topic}.", topic);
            return Task.CompletedTask;
        }

        var propertyWriter = _propertyWriter;
        if (propertyWriter is null)
        {
            return Task.CompletedTask;
        }

        // Extract timestamps
        var receivedTimestamp = DateTimeOffset.UtcNow;
        var sourceTimestamp = MqttHelper.ExtractSourceTimestamp(
            e.ApplicationMessage.UserProperties,
            _configuration.SourceTimestampPropertyName,
            _configuration.SourceTimestampDeserializer) ?? receivedTimestamp;

        // Use static delegate to avoid allocations on hot path
        propertyWriter.Write(
            (propertyReference, value, this, sourceTimestamp, receivedTimestamp),
            static state => state.propertyReference.SetValueFromSource(state.Item3, state.sourceTimestamp, state.receivedTimestamp, state.value));

        return Task.CompletedTask;
    }

    internal Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs e)
    {
        if (Interlocked.CompareExchange(ref _disposed, 0, 0) == 1)
        {
            return Task.CompletedTask;
        }

        _logger.LogWarning(e.Exception, "MQTT client disconnected. Reason: {Reason}.", e.Reason);
        _connectionMonitor?.SignalReconnectNeeded();

        return Task.CompletedTask;
    }

    private async Task OnReconnectedAsync(CancellationToken cancellationToken)
    {
        await SubscribeToPropertiesAsync(cancellationToken).ConfigureAwait(false);
        if (_propertyWriter is not null)
        {
            await _propertyWriter.LoadInitialStateAndResumeAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    async Task IFaultInjectable.InjectFaultAsync(FaultType faultType, CancellationToken cancellationToken)
    {
        switch (faultType)
        {
            case FaultType.Kill:
                _isForceKill = true;
                try { _forceKillCts?.Cancel(); }
                catch (ObjectDisposedException) { /* CTS disposed between loop iterations */ }
                break;

            case FaultType.Disconnect:
                var client = _client;
                if (client is not null && client.IsConnected)
                {
                    await client.DisconnectAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(faultType), faultType, null);
        }
    }

    private MqttClientOptions GetClientOptions()
    {
        var options = new MqttClientOptionsBuilder()
            .WithTcpServer(_configuration.BrokerHost, _configuration.BrokerPort)
            .WithClientId(_configuration.ClientId)
            .WithCleanSession(_configuration.CleanSession)
            .WithKeepAlivePeriod(_configuration.KeepAliveInterval)
            .WithTimeout(_configuration.ConnectTimeout);

        if (_configuration.UseTls)
        {
            options.WithTlsOptions(o => o.UseTls());
        }

        if (_configuration.Username is not null)
        {
            options.WithCredentials(_configuration.Username, _configuration.Password);
        }

        var clientOptions = options.Build();
#if USE_LOCAL_MQTTNET
        clientOptions.AcknowledgeQoS1OnReceive = true;
#endif
        return clientOptions;
    }

    /// <summary>
    /// Called by <see cref="MqttConnectionLifetime.DisposeAsync"/> after it has fully torn down
    /// the listen-time resources (monitor task, monitor, client). Resets source-level fields so
    /// the next listen iteration starts clean.
    /// </summary>
    internal void OnListenLifetimeDisposed()
    {
        _client = null;
        _connectionMonitor = null;
        _propertyWriter = null;
        _isForceKill = false;
        _forceKillCts = null;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        if (_connectionMonitor is not null)
        {
            await _connectionMonitor.DisposeAsync().ConfigureAwait(false);
        }

        var client = _client;
        if (client is not null)
        {
            client.ApplicationMessageReceivedAsync -= OnMessageReceivedAsync;
            client.DisconnectedAsync -= OnDisconnectedAsync;

            if (client.IsConnected)
            {
                try
                {
                    await client.DisconnectAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error disconnecting MQTT client.");
                }
            }

            client.Dispose();
            _client = null;
        }

        _ownership.Dispose();
        _topicToProperty.Clear();
        _propertyToTopic.Clear();

        Dispose();
    }
}