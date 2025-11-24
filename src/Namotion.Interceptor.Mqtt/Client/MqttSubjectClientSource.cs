using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Packets;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Sources;
using Namotion.Interceptor.Sources.Paths;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Mqtt.Client;

/// <summary>
/// MQTT client source that subscribes to an MQTT broker and synchronizes properties.
/// </summary>
internal sealed class MqttSubjectClientSource : BackgroundService, ISubjectSource, IAsyncDisposable
{
    private readonly IInterceptorSubject _subject;
    private readonly MqttClientConfiguration _configuration;
    private readonly ILogger _logger;

    private readonly MqttClientFactory _factory;

    // TODO(memory): Might lead to memory leaks
    private readonly ConcurrentDictionary<string, PropertyReference> _topicToProperty = new();
    private readonly ConcurrentDictionary<PropertyReference, string?> _propertyToTopic = new();

    private IMqttClient? _client;
    private SubjectPropertyWriter? _propertyWriter;
    private MqttConnectionMonitor? _connectionMonitor;

    private int _disposed;
    private volatile bool _isStarted;

    public MqttSubjectClientSource(
        IInterceptorSubject subject,
        MqttClientConfiguration configuration,
        ILogger<MqttSubjectClientSource> logger)
    {
        _subject = subject ?? throw new ArgumentNullException(nameof(subject));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _factory = new MqttClientFactory();

        configuration.Validate();
    }

    /// <inheritdoc />
    public bool IsPropertyIncluded(RegisteredSubjectProperty property) =>
        _configuration.PathProvider.IsPropertyIncluded(property);

    /// <inheritdoc />
    public int WriteBatchSize => 0; // No server-imposed limit for MQTT

    /// <inheritdoc />
    public async Task<IDisposable?> StartListeningAsync(SubjectPropertyWriter propertyWriter, CancellationToken cancellationToken)
    {
        _propertyWriter = propertyWriter;
        _logger.LogInformation("Connecting to MQTT broker at {Host}:{Port}.", _configuration.BrokerHost, _configuration.BrokerPort);

        _client = _factory.CreateMqttClient();
        _client.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;
        _client.DisconnectedAsync += OnDisconnectedAsync;

        await _client.ConnectAsync(BuildClientOptions(), cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Connected to MQTT broker successfully.");

        await SubscribeToPropertiesAsync(cancellationToken).ConfigureAwait(false);

        _connectionMonitor = new MqttConnectionMonitor(
            _client,
            _configuration,
            _logger,
            BuildClientOptions,
            async ct => await OnReconnectedAsync(ct).ConfigureAwait(false),
            async () =>
            {
                _propertyWriter?.StartBuffering();
                await Task.CompletedTask;
            });

        _isStarted = true;

        return new MqttConnection(async () =>
        {
            if (_client?.IsConnected == true)
            {
                await _client.DisconnectAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        });
    }

    /// <inheritdoc />
    public Task<Action?> LoadInitialStateAsync(CancellationToken cancellationToken)
    {
        // Retained messages are received through the normal message handler: No separate initial load needed/possible
        return Task.FromResult<Action?>(null);
    }

    /// <inheritdoc />
    public async ValueTask WriteChangesAsync(ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken)
    {
        var client = _client;
        if (client is null || !client.IsConnected)
        {
            throw new InvalidOperationException("MQTT client is not connected.");
        }

        var length = changes.Length;
        if (length == 0) return;

        // Rent array from pool for messages
        var messagesPool = ArrayPool<MqttApplicationMessage>.Shared;
        var messages = messagesPool.Rent(length);
        var messageCount = 0;

        try
        {
            var changesSpan = changes.Span;

            // Build all messages first
            for (var i = 0; i < length; i++)
            {
                var change = changesSpan[i];
                var property = change.Property.TryGetRegisteredProperty();
                if (property is null || property.HasChildSubjects)
                {
                    continue;
                }

                var topic = GetCachedTopic(change.Property, property);
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

                if (_configuration.SourceTimestampPropertyName is not null)
                {
                    message.UserProperties =
                    [
                        new MqttUserProperty(
                            _configuration.SourceTimestampPropertyName,
                            change.ChangedTimestamp.ToUnixTimeMilliseconds().ToString())
                    ];
                }

                messages[messageCount++] = message;
            }

            // Publish messages sequentially (less async overhead than Task.WhenAll)
            for (var i = 0; i < messageCount; i++)
            {
                await client.PublishAsync(messages[i], cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            messagesPool.Return(messages);
        }
    }

    private async Task SubscribeToPropertiesAsync(CancellationToken cancellationToken)
    {
        var registeredSubject = _subject.TryGetRegisteredSubject();
        if (registeredSubject is null)
        {
            return;
        }

        var properties = registeredSubject
            .GetAllProperties()
            .Where(p => !p.HasChildSubjects && IsPropertyIncluded(p))
            .ToList();

        if (properties.Count == 0)
        {
            _logger.LogWarning("No MQTT properties found to subscribe.");
            return;
        }

        var subscribeOptionsBuilder = _factory!.CreateSubscribeOptionsBuilder();

        foreach (var property in properties)
        {
            var topic = GetCachedTopic(property.Reference, property);
            if (topic is null) continue;

            _topicToProperty[topic] = property.Reference;
            subscribeOptionsBuilder.WithTopicFilter(f => f
                .WithTopic(topic)
                .WithQualityOfServiceLevel(_configuration.DefaultQualityOfService));
        }

        await _client!.SubscribeAsync(subscribeOptionsBuilder.Build(), cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Subscribed to {Count} MQTT topics.", properties.Count);
    }

    private string? GetCachedTopic(PropertyReference propertyReference, RegisteredSubjectProperty property)
    {
        return _propertyToTopic.GetOrAdd(propertyReference, static (_, state) =>
        {
            var (p, pathProvider, subject, topicPrefix) = state;
            var path = p.TryGetSourcePath(pathProvider, subject);
            return path is null ? null : MqttHelper.BuildTopic(path, topicPrefix);
        }, (property, _configuration.PathProvider, _subject, _configuration.TopicPrefix));
    }

    private Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        var topic = e.ApplicationMessage.Topic;
        if (!_topicToProperty.TryGetValue(topic, out var propertyReference))
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
            _configuration.SourceTimestampPropertyName) ?? receivedTimestamp;

        // Use static delegate to avoid allocations on hot path
        propertyWriter.Write(
            (propertyReference, value, this, sourceTimestamp, receivedTimestamp),
            static state => state.propertyReference.SetValueFromSource(state.Item3, state.sourceTimestamp, state.receivedTimestamp, state.value));

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait until StartListeningAsync has been called
        while (!_isStarted && !stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(100, stoppingToken).ConfigureAwait(false);
        }

        if (_connectionMonitor is not null)
        {
            await _connectionMonitor.MonitorConnectionAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs e)
    {
        if (Interlocked.CompareExchange(ref _disposed, 0, 0) == 1)
        {
            return Task.CompletedTask;
        }

        _logger.LogWarning(e.Exception, "MQTT client disconnected. Reason: {Reason}", e.Reason);

        // Signal the connection monitor (hybrid approach)
        _connectionMonitor?.SignalReconnectNeeded();

        return Task.CompletedTask;
    }

    private async Task OnReconnectedAsync(CancellationToken cancellationToken)
    {
        // Resubscribe to topics
        await SubscribeToPropertiesAsync(cancellationToken).ConfigureAwait(false);

        // Complete initialization to flush retry queue
        if (_propertyWriter is not null)
        {
            await _propertyWriter.CompleteInitializationAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private MqttClientOptions BuildClientOptions()
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

        return options.Build();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        // Dispose connection monitor
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

        // Dispose base BackgroundService
        Dispose();
    }
}