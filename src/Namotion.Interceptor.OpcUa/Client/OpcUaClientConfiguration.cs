using Namotion.Interceptor.Sources.Paths;
using Opc.Ua;
using Opc.Ua.Configuration;

namespace Namotion.Interceptor.OpcUa.Client;

public class OpcUaClientConfiguration
{
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
    /// Gets the delay before attempting to reconnect after a disconnect. Default is 5 seconds.
    /// </summary>
    public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets the maximum number of write operations to buffer when disconnected. Default is 1000.
    /// When the session is disconnected, write operations are queued up to this limit.
    /// Once reconnected, queued writes are flushed to the server in order (FIFO).
    /// Set to 0 to disable write buffering (writes will be dropped when disconnected).
    /// </summary>
    public int WriteQueueSize { get; init; } = 1000;

    /// <summary>
    /// Gets a value indicating whether to enable automatic healing of failed monitored items. Default is true.
    /// When enabled, the subscription manager periodically retries creation of failed items
    /// that may succeed later (e.g., BadTooManyMonitoredItems when resources free up).
    /// Items with permanent errors (BadNodeIdUnknown) are not retried.
    /// </summary>
    public bool EnableAutoHealing { get; init; } = true;

    /// <summary>
    /// Gets the interval for subscription health checks and auto-healing attempts. Default is 10 seconds.
    /// Failed monitored items (excluding design-time errors like BadNodeIdUnknown) are retried at this interval.
    /// Only used when EnableAutoHealing is true.
    /// </summary>
    public TimeSpan SubscriptionHealthCheckInterval { get; init; } = TimeSpan.FromSeconds(10);
    
    /// <summary>
    /// Gets or sets an async predicate that is called when an unknown (not statically typed) OPC UA node or variable is found during browsing.
    /// If the function returns true, the node is added as a dynamic property to the given subject.
    /// Default is add all missing as dynamic properties.
    /// </summary>
    public Func<ReferenceDescription, CancellationToken, Task<bool>>? ShouldAddDynamicProperty { get; init; } = 
        static (_, _) => Task.FromResult(true);
    
    /// <summary>
    /// Gets the source path provider used to map between OPC UA node browse names and C# property names.
    /// This provider determines which properties are included and how their names are translated.
    /// </summary>
    public required ISourcePathProvider SourcePathProvider { get; init; }

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
    public TimeSpan? BufferTime { get; set; }
    
    /// <summary>
    /// Gets or sets the retry time (default: 10s).
    /// </summary>
    public TimeSpan? RetryTime { get; set; }

    /// <summary>
    /// Gets or sets the default sampling interval in milliseconds for monitored items when not specified on the [OpcUaNode] attribute (default: 0).
    /// </summary>
    public int DefaultSamplingInterval { get; set; }

    /// <summary>
    /// Gets or sets the default queue size for monitored items when not specified on the [OpcUaNode] attribute (default: 10).
    /// </summary>
    public uint DefaultQueueSize { get; set; } = 10;

    /// <summary>
    /// Gets or sets whether the server should discard the oldest value in the queue when the queue is full for monitored items (default: true).
    /// </summary>
    public bool DefaultDiscardOldest { get; set; } = true;

    /// <summary>
    /// Gets or sets the default publishing interval for subscriptions in milliseconds (default: 0).
    /// Larger values reduce overhead by batching more notifications per publish.
    /// </summary>
    public int DefaultPublishingInterval { get; set; } = 0;

    /// <summary>
    /// Gets or sets the subscription keep-alive count (default: 10).
    /// </summary>
    public uint SubscriptionKeepAliveCount { get; set; } = 10;

    /// <summary>
    /// Gets or sets the subscription lifetime count (default: 100).
    /// </summary>
    public uint SubscriptionLifetimeCount { get; set; } = 100;

    /// <summary>
    /// Gets or sets the subscription priority (default: 0 = server default).
    /// </summary>
    public byte SubscriptionPriority { get; set; } = 0;

    /// <summary>
    /// Gets or sets the maximum notifications per publish that the client requests (default: 0 = server default).
    /// </summary>
    public uint SubscriptionMaximumNotificationsPerPublish { get; set; } = 0;

    /// <summary>
    /// Gets or sets the maximum references per node to read per browse request. 0 uses server default.
    /// </summary>
    public uint MaximumReferencesPerNode { get; set; } = 0;

    public virtual ApplicationInstance CreateApplicationInstance()
    {
        var application = new ApplicationInstance
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
                    StorePath = "pki/own",
                    SubjectName = $"CN={ApplicationName}, O=Namotion"
                },
                TrustedPeerCertificates = new CertificateTrustList
                {
                    StoreType = "Directory",
                    StorePath = "pki/trusted"
                },
                RejectedCertificateStore = new CertificateTrustList
                {
                    StoreType = "Directory",
                    StorePath = "pki/rejected"
                },
                AutoAcceptUntrustedCertificates = true,
                AddAppCertToTrustedStore = true,
            },
            TransportQuotas = new TransportQuotas
            {
                OperationTimeout = 60000,
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
            CertificateValidator = new CertificateValidator()
        };

        config.CertificateValidator.Update(config);

        application.ApplicationConfiguration = config;
        return application;
    }

    /// <summary>
    /// Validates configuration values and throws ArgumentException if invalid.
    /// Call this method during initialization to fail fast with clear error messages.
    /// </summary>
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(ServerUrl);
        ArgumentNullException.ThrowIfNull(SourcePathProvider);
        ArgumentNullException.ThrowIfNull(TypeResolver);
        ArgumentNullException.ThrowIfNull(ValueConverter);
        ArgumentNullException.ThrowIfNull(SubjectFactory);

        if (WriteQueueSize < 0)
        {
            throw new ArgumentException(
                $"WriteQueueSize must be non-negative, got: {WriteQueueSize}",
                nameof(WriteQueueSize));
        }

        const int MaxWriteQueueSize = 10000;
        if (WriteQueueSize > MaxWriteQueueSize)
        {
            throw new ArgumentException(
                $"WriteQueueSize must not exceed {MaxWriteQueueSize} (got: {WriteQueueSize})",
                nameof(WriteQueueSize));
        }

        if (EnableAutoHealing)
        {
            var minInterval = TimeSpan.FromSeconds(5);
            if (SubscriptionHealthCheckInterval < minInterval)
            {
                throw new ArgumentException(
                    $"SubscriptionHealthCheckInterval must be at least {minInterval.TotalSeconds}s when EnableAutoHealing is true (got: {SubscriptionHealthCheckInterval.TotalSeconds}s)",
                    nameof(SubscriptionHealthCheckInterval));
            }
        }

        if (MaximumItemsPerSubscription <= 0)
        {
            throw new ArgumentException(
                $"MaximumItemsPerSubscription must be positive, got: {MaximumItemsPerSubscription}",
                nameof(MaximumItemsPerSubscription));
        }
    }
}