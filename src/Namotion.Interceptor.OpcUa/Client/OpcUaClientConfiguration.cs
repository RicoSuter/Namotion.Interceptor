using Namotion.Interceptor.OpcUa.Attributes;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;

namespace Namotion.Interceptor.OpcUa.Client;

public class OpcUaClientConfiguration
{
    private ISessionFactory? _resolvedSessionFactory;

    /// <summary>
    /// Gets the OPC UA server endpoint URL to connect to (e.g., "opc.tcp://localhost:4840").
    /// </summary>
    public required string ServerUrl { get; init; }

    /// <summary>
    /// Gets the optional root node name to start browsing from under the Objects folder.
    /// If not specified, browsing starts from the ObjectsFolder root.
    /// </summary>
    public string? RootName { get; init; }
    
    /// <summary>
    /// Gets the OPC UA client application name used for identification and certificate generation.
    /// Default is "Namotion.Interceptor.Client".
    /// </summary>
    public string ApplicationName { get; init; } = "Namotion.Interceptor.Client";
    
    /// <summary>
    /// Gets the default namespace URI to use when a [OpcUaNode] attribute defines a NodeIdentifier but no NodeNamespaceUri.
    /// </summary>
    public string? DefaultNamespaceUri { get; init; }
    
    /// <summary>
    /// Gets the maximum number of monitored items per subscription. Default is 1000.
    /// </summary>
    public int MaximumItemsPerSubscription { get; init; } = 1000;

    /// <summary>
    /// Gets the maximum number of write operations to queue for retry when disconnected. Default is 1000.
    /// When the session is disconnected, write operations are queued up to this limit.
    /// Once reconnected, queued writes are flushed to the server in order (FIFO).
    /// Set to 0 to disable write retry queue (writes will be dropped when disconnected).
    /// </summary>
    public int WriteRetryQueueSize { get; init; } = 1000;

    /// <summary>
    /// Gets or sets the interval for subscription health checks and auto-healing attempts. Default is 5 seconds.
    /// Failed monitored items (excluding design-time errors like BadNodeIdUnknown) are retried at this interval.
    /// This also determines how quickly the health check loop picks up work deferred from SDK reconnection.
    /// </summary>
    public TimeSpan SubscriptionHealthCheckInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets or sets the maximum time to wait for SDK reconnection before forcing a manual reconnection.
    /// If the SDK's reconnect handler hasn't succeeded within this duration, stall detection triggers
    /// a full session reset and manual reconnection attempt.
    /// Default is 30 seconds. Use lower values (e.g., 15s) for faster recovery in tests.
    /// </summary>
    public TimeSpan MaxReconnectDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets an async predicate that is called when an unknown (not statically typed) OPC UA node or variable is found during browsing.
    /// If the function returns true, the node is added as a dynamic property to the given subject.
    /// Default is add all missing as dynamic properties.
    /// </summary>
    public Func<ReferenceDescription, CancellationToken, Task<bool>>? ShouldAddDynamicProperty { get; init; } = 
        static (_, _) => Task.FromResult(true);
    
    /// <summary>
    /// Gets the path provider used to map between OPC UA node browse names and C# property names.
    /// This provider determines which properties are included and how their names are translated.
    /// </summary>
    public required PathProviderBase PathProvider { get; init; }

    /// <summary>
    /// Gets the type resolver used to infer C# types from OPC UA nodes during dynamic property discovery.
    /// </summary>
    public required OpcUaTypeResolver TypeResolver { get; init; }
    
    /// <summary>
    /// Gets the value converter used to convert between OPC UA node values and C# property values.
    /// Handles type conversions such as decimal to double for OPC UA compatibility.
    /// </summary>
    public required OpcUaValueConverter ValueConverter { get; init; }
    
    /// <summary>
    /// Gets the subject factory used to create interceptor subject instances for OPC UA object nodes.
    /// </summary>
    public required OpcUaSubjectFactory SubjectFactory { get; init; }

    /// <summary>
    /// Gets or sets the time window to buffer incoming changes (default: 8ms).
    /// </summary>
    public TimeSpan? BufferTime { get; init; }

    /// <summary>
    /// Gets or sets the retry time (default: 10s).
    /// </summary>
    public TimeSpan? RetryTime { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Gets or sets the default sampling interval in milliseconds for monitored items when not specified on the [OpcUaNode] attribute.
    /// When null (default), uses the OPC UA library default (-1 = server decides).
    /// Set to 0 for exception-based monitoring (immediate reporting on every change).
    /// </summary>
    public int? DefaultSamplingInterval { get; init; }

    /// <summary>
    /// Gets or sets the default queue size for monitored items when not specified on the [OpcUaNode] attribute.
    /// When null (default), uses the OPC UA library default (1).
    /// </summary>
    public uint? DefaultQueueSize { get; init; }

    /// <summary>
    /// Gets or sets whether the server should discard the oldest value in the queue when the queue is full for monitored items.
    /// When null (default), uses the OPC UA library default (true).
    /// </summary>
    public bool? DefaultDiscardOldest { get; init; }

    /// <summary>
    /// Gets or sets the default data change trigger for monitored items when not specified on the [OpcUaNode] attribute.
    /// When null (default), uses the OPC UA library default (StatusValue).
    /// </summary>
    public DataChangeTrigger? DefaultDataChangeTrigger { get; init; }

    /// <summary>
    /// Gets or sets the default deadband type for monitored items when not specified on the [OpcUaNode] attribute.
    /// When null (default), uses the OPC UA library default (None).
    /// Use Absolute or Percent for analog values to filter small changes.
    /// </summary>
    public DeadbandType? DefaultDeadbandType { get; init; }

    /// <summary>
    /// Gets or sets the default deadband value for monitored items when not specified on the [OpcUaNode] attribute.
    /// When null (default), uses the OPC UA library default (0.0).
    /// The interpretation depends on DeadbandType: absolute units for Absolute, percentage for Percent.
    /// </summary>
    public double? DefaultDeadbandValue { get; init; }

    /// <summary>
    /// Gets or sets the buffer time added to the revised sampling interval when scheduling
    /// read-after-writes after writes. This ensures the PLC has time to respond before
    /// the read occurs. Only applies when SamplingInterval = 0 was requested but the
    /// server revised it to a non-zero value.
    /// Default: 50 milliseconds.
    /// </summary>
    public TimeSpan ReadAfterWriteBuffer { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Gets or sets the default publishing interval for subscriptions in milliseconds (default: 0).
    /// Larger values reduce overhead by batching more notifications per publish.
    /// </summary>
    public int DefaultPublishingInterval { get; init; } = 0;

    /// <summary>
    /// Gets or sets the subscription keep-alive count (default: 10).
    /// </summary>
    public uint SubscriptionKeepAliveCount { get; init; } = 10;

    /// <summary>
    /// Gets or sets the subscription lifetime count (default: 100).
    /// </summary>
    public uint SubscriptionLifetimeCount { get; init; } = 100;

    /// <summary>
    /// Gets or sets the subscription priority (default: 0 = server default).
    /// </summary>
    public byte SubscriptionPriority { get; init; } = 0;

    /// <summary>
    /// Gets or sets the maximum notifications per publish that the client requests (default: 0 = server default).
    /// </summary>
    public uint SubscriptionMaximumNotificationsPerPublish { get; init; } = 0;

    /// <summary>
    /// Gets or sets whether to process subscription messages sequentially (in order).
    /// When true, callbacks are invoked one at a time in sequence order, reducing throughput but guaranteeing order.
    /// When false (default), messages may be processed in parallel for higher throughput.
    /// Only enable this if your application requires strict ordering of property updates.
    /// Default is false for optimal performance.
    /// </summary>
    public bool SubscriptionSequentialPublishing { get; init; } = false;

    /// <summary>
    /// Gets or sets the minimum number of publish requests the client keeps outstanding at all times.
    /// Higher values improve reliability during traffic spikes and brief network issues by ensuring
    /// multiple requests are always in flight. The OPC Foundation's reference client uses 3.
    /// Default is 3 for optimal reliability.
    /// </summary>
    public int MinPublishRequestCount { get; init; } = 3;

    /// <summary>
    /// Gets or sets the maximum references per node to read per browse request. 0 uses server default.
    /// </summary>
    public uint MaximumReferencesPerNode { get; init; } = 0;

    /// <summary>
    /// Gets or sets whether to enable automatic polling fallback when subscriptions are not supported.
    /// When enabled, items that fail subscription creation automatically fall back to periodic polling.
    /// Default is true.
    /// </summary>
    public bool EnablePollingFallback { get; init; } = true;

    /// <summary>
    /// Gets or sets whether to enable automatic read-after-writes after writes.
    /// When enabled and SamplingInterval=0 is requested but the server revises it to non-zero,
    /// a read is automatically scheduled after each write to ensure the written value is read back.
    /// This compensates for servers that don't support true exception-based monitoring.
    /// Default is true.
    /// </summary>
    public bool EnableReadAfterWrite { get; init; } = true;

    /// <summary>
    /// Gets or sets the base path for certificate stores.
    /// Default is "pki". Change this to isolate certificate stores for parallel test execution.
    /// </summary>
    public string CertificateStoreBasePath { get; init; } = "pki";

    /// <summary>
    /// Gets or sets the polling interval for items that don't support subscriptions.
    /// Only used when EnablePollingFallback is true.
    /// Default is 1000ms (1 second).
    /// </summary>
    public TimeSpan PollingInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Gets or sets the maximum batch size for polling read operations.
    /// Larger batches reduce network calls but increase latency.
    /// Default is 100 items per batch.
    /// </summary>
    public int PollingBatchSize { get; init; } = 100;

    /// <summary>
    /// Gets or sets the timeout to wait for the polling manager to complete during disposal.
    /// If the polling task does not complete within this timeout, it will be abandoned.
    /// Default is 10 seconds.
    /// </summary>
    public TimeSpan PollingDisposalTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets or sets the number of consecutive failures before the circuit breaker opens
    /// for background read operations (polling fallback and read-after-writes).
    /// When the circuit breaker opens, reads are suspended temporarily to prevent resource exhaustion.
    /// Default is 5 failures.
    /// </summary>
    public int PollingCircuitBreakerThreshold { get; init; } = 5;

    /// <summary>
    /// Gets or sets the cooldown period after the circuit breaker opens before attempting
    /// to resume background read operations (polling fallback and read-after-writes).
    /// Default is 30 seconds.
    /// </summary>
    public TimeSpan PollingCircuitBreakerCooldown { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets the OPC UA session timeout.
    /// This determines how long the server will maintain the session after losing communication.
    /// Default is 60 seconds.
    /// </summary>
    public TimeSpan SessionTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Gets or sets the keep-alive interval for the OPC UA session.
    /// This determines how often the client sends keep-alive messages to detect disconnection.
    /// Shorter intervals allow faster disconnection detection but increase network traffic.
    /// Default is 5 seconds.
    /// </summary>
    public TimeSpan KeepAliveInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets or sets the operation timeout for OPC UA requests.
    /// This determines how long to wait for a response before timing out.
    /// Shorter timeouts allow faster disconnection detection but may cause false positives on slow networks.
    /// Default is 60 seconds.
    /// </summary>
    public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Gets or sets the maximum time the reconnect handler will attempt to reconnect before giving up.
    /// Default is 60 seconds.
    /// </summary>
    public TimeSpan ReconnectHandlerTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Gets or sets the interval between reconnection attempts when connection is lost.
    /// Default is 5 seconds.
    /// </summary>
    public TimeSpan ReconnectInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets or sets whether to use security (signing and encryption) when connecting to the OPC UA server.
    /// When true, the client will prefer secure endpoints with message signing and encryption.
    /// When false, the client will prefer unsecured endpoints (faster, but no protection).
    /// Default is false for development convenience. Set to true for production deployments
    /// that require secure communication.
    /// </summary>
    public bool UseSecurity { get; init; } = false;

    /// <summary>
    /// Gets or sets the telemetry context for OPC UA operations.
    /// Defaults to NullTelemetryContext for minimal overhead.
    /// For DI integration, use DefaultTelemetry.Create(builder => builder.Services.AddSingleton(loggerFactory)).
    /// </summary>
    public ITelemetryContext TelemetryContext { get; init; } = NullTelemetryContext.Instance;

    /// <summary>
    /// Gets or sets the session factory for creating OPC UA sessions.
    /// If not specified, a DefaultSessionFactory using the configured TelemetryContext is created automatically.
    /// </summary>
    public ISessionFactory? SessionFactory { get; init; }

    /// <summary>
    /// Gets or sets the timeout for session disposal during shutdown.
    /// If the session doesn't close gracefully within this timeout, it will be forcefully disposed.
    /// Default is 5 seconds.
    /// </summary>
    public TimeSpan SessionDisposalTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets the actual session factory, creating a default one using TelemetryContext if not explicitly set.
    /// The default factory is cached after first access (thread-safe).
    /// </summary>
    public ISessionFactory ActualSessionFactory => SessionFactory ?? LazyInitializer.EnsureInitialized(
        ref _resolvedSessionFactory, () => new DefaultSessionFactory(TelemetryContext))!;

    /// <summary>
    /// Creates and configures an OPC UA application instance for the client.
    /// Override this method to customize application configuration, security settings, or certificate handling.
    /// </summary>
    /// <returns>A configured <see cref="ApplicationInstance"/> ready for connecting to OPC UA servers.</returns>
    public virtual async Task<ApplicationInstance> CreateApplicationInstanceAsync()
    {
        var application = new ApplicationInstance(TelemetryContext)
        {
            ApplicationName = ApplicationName,
            ApplicationType = ApplicationType.Client
        };

        var host = System.Net.Dns.GetHostName();
        var applicationUri = $"urn:{host}:Namotion.Interceptor:{ApplicationName}";

        var config = new ApplicationConfiguration
        {
            ApplicationName = ApplicationName,
            ApplicationType = ApplicationType.Client,
            ApplicationUri = applicationUri,
            ProductUri = "urn:Namotion.Interceptor",
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = "Directory",
                    StorePath = $"{CertificateStoreBasePath}/own",
                    SubjectName = $"CN={ApplicationName}, O=Namotion"
                },
                TrustedPeerCertificates = new CertificateTrustList
                {
                    StoreType = "Directory",
                    StorePath = $"{CertificateStoreBasePath}/trusted"
                },
                RejectedCertificateStore = new CertificateTrustList
                {
                    StoreType = "Directory",
                    StorePath = $"{CertificateStoreBasePath}/rejected"
                },
                AutoAcceptUntrustedCertificates = true,
                AddAppCertToTrustedStore = true,
            },
            TransportQuotas = new TransportQuotas
            {
                OperationTimeout = (int)OperationTimeout.TotalMilliseconds,
                MaxStringLength = 4_194_304,
                MaxByteStringLength = 16_777_216,
                MaxMessageSize = 16_777_216,
                ChannelLifetime = 600000,
                SecurityTokenLifetime = 3600000
            },
            ClientConfiguration = new ClientConfiguration
            {
                DefaultSessionTimeout = 60000
            },
            DisableHiResClock = true,
            TraceConfiguration = new TraceConfiguration
            {
                OutputFilePath = "Logs/UaClient.log",
                TraceMasks = 0
            },
            CertificateValidator = new CertificateValidator(TelemetryContext)
        };

        await config.CertificateValidator.UpdateAsync(config).ConfigureAwait(false);

        application.ApplicationConfiguration = config;
        return application;
    }

    /// <summary>
    /// Creates a MonitoredItem for the given property and node ID using this configuration's defaults.
    /// Attribute-level overrides (SamplingInterval, QueueSize, DiscardOldest, DataChangeTrigger, DeadbandType, DeadbandValue)
    /// are applied if present on the property.
    /// </summary>
    /// <param name="nodeId">The OPC UA node ID to monitor.</param>
    /// <param name="property">The property to associate with the monitored item.</param>
    /// <returns>A configured MonitoredItem ready to be added to a subscription.</returns>
    internal MonitoredItem CreateMonitoredItem(NodeId nodeId, RegisteredSubjectProperty property)
    {
        var opcUaNodeAttribute = property.TryGetOpcUaNodeAttribute();
        var item = new MonitoredItem(TelemetryContext)
        {
            StartNodeId = nodeId,
            AttributeId = Opc.Ua.Attributes.Value,
            MonitoringMode = MonitoringMode.Reporting,
            Handle = property
        };

        // Apply sampling/queue settings (only if specified)
        var samplingInterval = opcUaNodeAttribute?.SamplingInterval ?? DefaultSamplingInterval;
        if (samplingInterval.HasValue)
        {
            item.SamplingInterval = samplingInterval.Value;
        }

        var queueSize = opcUaNodeAttribute?.QueueSize ?? DefaultQueueSize;
        if (queueSize.HasValue)
        {
            item.QueueSize = queueSize.Value;
        }

        var discardOldest = opcUaNodeAttribute?.DiscardOldest ?? DefaultDiscardOldest;
        if (discardOldest.HasValue)
        {
            item.DiscardOldest = discardOldest.Value;
        }

        // Apply filter (only if any filter option is specified)
        var filter = CreateDataChangeFilter(opcUaNodeAttribute);
        if (filter != null)
        {
            item.Filter = filter;
        }

        return item;
    }

    /// <summary>
    /// Creates a DataChangeFilter based on the attribute and configuration defaults.
    /// Returns null if no filter options are specified (uses OPC UA library defaults).
    /// </summary>
    private DataChangeFilter? CreateDataChangeFilter(OpcUaNodeAttribute? attribute)
    {
        var trigger = attribute?.DataChangeTrigger ?? DefaultDataChangeTrigger;
        var deadbandType = attribute?.DeadbandType ?? DefaultDeadbandType;
        var deadbandValue = attribute?.DeadbandValue ?? DefaultDeadbandValue;

        // Only create filter if at least one option is specified
        if (!trigger.HasValue && !deadbandType.HasValue && !deadbandValue.HasValue)
        {
            return null;
        }

        return new DataChangeFilter
        {
            Trigger = trigger ?? DataChangeTrigger.StatusValue,
            DeadbandType = (uint)(deadbandType ?? DeadbandType.None),
            DeadbandValue = deadbandValue ?? 0.0
        };
    }

    /// <summary>
    /// Validates configuration values and throws ArgumentException if invalid.
    /// Call this method during initialization to fail fast with clear error messages.
    /// </summary>
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(ServerUrl);
        ArgumentNullException.ThrowIfNull(PathProvider);
        ArgumentNullException.ThrowIfNull(TypeResolver);
        ArgumentNullException.ThrowIfNull(ValueConverter);
        ArgumentNullException.ThrowIfNull(SubjectFactory);

        if (WriteRetryQueueSize < 0)
        {
            throw new ArgumentException(
                $"WriteRetryQueueSize must be non-negative, got: {WriteRetryQueueSize}",
                nameof(WriteRetryQueueSize));
        }

        if (SubscriptionHealthCheckInterval < TimeSpan.FromSeconds(1))
        {
            throw new ArgumentException(
                $"SubscriptionHealthCheckInterval must be at least {TimeSpan.FromSeconds(1).TotalSeconds}s (got: {SubscriptionHealthCheckInterval.TotalSeconds}s)",
                nameof(SubscriptionHealthCheckInterval));
        }

        if (MaximumItemsPerSubscription <= 0)
        {
            throw new ArgumentException(
                $"MaximumItemsPerSubscription must be positive, got: {MaximumItemsPerSubscription}",
                nameof(MaximumItemsPerSubscription));
        }

        if (EnablePollingFallback)
        {
            if (PollingInterval < TimeSpan.FromMilliseconds(100))
            {
                throw new ArgumentException(
                    $"PollingInterval must be at least {TimeSpan.FromMilliseconds(100).TotalMilliseconds}ms when EnablePollingFallback is true (got: {PollingInterval.TotalMilliseconds}ms)",
                    nameof(PollingInterval));
            }

            if (PollingBatchSize <= 0)
            {
                throw new ArgumentException(
                    $"PollingBatchSize must be positive, got: {PollingBatchSize}",
                    nameof(PollingBatchSize));
            }

            if (PollingCircuitBreakerThreshold <= 0)
            {
                throw new ArgumentException(
                    $"PollingCircuitBreakerThreshold must be positive, got: {PollingCircuitBreakerThreshold}",
                    nameof(PollingCircuitBreakerThreshold));
            }

            if (PollingCircuitBreakerCooldown < TimeSpan.FromSeconds(1))
            {
                throw new ArgumentException(
                    $"PollingCircuitBreakerCooldown must be at least {TimeSpan.FromSeconds(1).TotalSeconds}s when EnablePollingFallback is true (got: {PollingCircuitBreakerCooldown.TotalSeconds}s)",
                    nameof(PollingCircuitBreakerCooldown));
            }
        }

        if (SessionTimeout < TimeSpan.FromSeconds(1))
        {
            throw new ArgumentException(
                $"SessionTimeout must be at least 1000ms, got: {SessionTimeout}",
                nameof(SessionTimeout));
        }

        if (ReconnectHandlerTimeout < TimeSpan.FromSeconds(1))
        {
            throw new ArgumentException(
                $"ReconnectHandlerTimeout must be at least 1000ms, got: {ReconnectHandlerTimeout}",
                nameof(ReconnectHandlerTimeout));
        }

        if (ReconnectInterval < TimeSpan.FromSeconds(0.1))
        {
            throw new ArgumentException(
                $"ReconnectInterval must be at least 100ms, got: {ReconnectInterval}",
                nameof(ReconnectInterval));
        }

        if (MaxReconnectDuration < TimeSpan.FromSeconds(5))
        {
            throw new ArgumentException(
                $"MaxReconnectDuration must be at least 5 seconds, got: {MaxReconnectDuration.TotalSeconds}s",
                nameof(MaxReconnectDuration));
        }

        if (MinPublishRequestCount < 1)
        {
            throw new ArgumentException(
                $"MinPublishRequestCount must be at least 1, got: {MinPublishRequestCount}",
                nameof(MinPublishRequestCount));
        }

        if (ReadAfterWriteBuffer < TimeSpan.Zero)
        {
            throw new ArgumentException(
                $"ReadAfterWriteBuffer must be non-negative, got: {ReadAfterWriteBuffer}",
                nameof(ReadAfterWriteBuffer));
        }

    }
}