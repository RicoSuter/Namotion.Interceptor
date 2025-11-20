using Namotion.Interceptor.Sources.Paths;
using Opc.Ua;
using Opc.Ua.Configuration;
using Opc.Ua.Export;

namespace Namotion.Interceptor.OpcUa.Server;

public class OpcUaServerConfiguration
{
    /// <summary>
    /// Gets the optional root folder name to create under the Objects folder for organizing server nodes.
    /// If not specified, nodes are created directly under the ObjectsFolder.
    /// </summary>
    public string? RootName { get; init; }
    
    /// <summary>
    /// Gets the OPC UA server application name used for identification and certificate generation.
    /// Default is "Namotion.Interceptor.Server".
    /// </summary>
    public string ApplicationName { get; init; } = "Namotion.Interceptor.Server";

    /// <summary>
    /// Gets the primary namespace URI for the OPC UA server used to identify custom nodes.
    /// Default is "http://namotion.com/Interceptor/".
    /// </summary>
    public string NamespaceUri { get; init; } = "http://namotion.com/Interceptor/";
    
    /// <summary>
    /// Gets the source path provider used to map between OPC UA node browse names and C# property names.
    /// This provider determines which properties are included and how their names are translated.
    /// </summary>
    public required ISourcePathProvider SourcePathProvider { get; init; }

    /// <summary>
    /// Gets the value converter used to convert between OPC UA node values and C# property values.
    /// Handles type conversions such as decimal to double for OPC UA compatibility.
    /// </summary>
    public required OpcUaValueConverter ValueConverter { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether to clean up old certificates from the
    /// application certificate store on connect. Defaults to true.
    /// </summary>
    public bool CleanCertificateStore { get; init; } = true;

    /// <summary>
    /// Gets or sets the time window to buffer incoming changes (default: 8ms).
    /// </summary>
    public TimeSpan? BufferTime { get; init; }
    
    /// <summary>
    /// Gets or sets the retry time (default: 10s).
    /// </summary>
    public TimeSpan? RetryTime { get; init; }

    /// <summary>
    /// Gets or sets the telemetry context for OPC UA operations.
    /// Defaults to a telemetry context with trace-based logging via Utils.LoggingProvider.
    /// For DI integration, use DefaultTelemetry.Create(builder => builder.Services.AddSingleton(loggerFactory)).
    /// </summary>
    public ITelemetryContext TelemetryContext { get; init; } = DefaultTelemetry.Create(_ => { });

    public virtual async Task<ApplicationInstance> CreateApplicationInstanceAsync()
    {
        var application = new ApplicationInstance(TelemetryContext)
        {
            ApplicationName = ApplicationName,
            ApplicationType = ApplicationType.Server
        };

        var host = System.Net.Dns.GetHostName();
        var applicationUri = $"urn:{host}:Namotion.Interceptor:{ApplicationName}";

        var config = new ApplicationConfiguration
        {
            ApplicationName = ApplicationName,
            ApplicationType = ApplicationType.Server,
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
                TrustedIssuerCertificates = new CertificateTrustList
                {
                    StoreType = "Directory",
                    StorePath = "pki/issuer"
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
                AutoAcceptUntrustedCertificates = false,
                AddAppCertToTrustedStore = false,
                SendCertificateChain = true,
                RejectSHA1SignedCertificates = false, // allow for interoperability tests
                MinimumCertificateKeySize = 2048
            },
            TransportQuotas = new TransportQuotas
            {
                OperationTimeout = 600000,
                MaxStringLength = 4_194_304,
                MaxByteStringLength = 16_777_216,
                MaxMessageSize = 16_777_216,
                MaxArrayLength = 1_000_000,
                MaxBufferSize = 65_535,
                ChannelLifetime = 300000,
                SecurityTokenLifetime = 3_600_000
            },
            ServerConfiguration = new ServerConfiguration
            {
                // Base addresses kept minimal (tcp only). Add https if required later.
                BaseAddresses = { "opc.tcp://localhost:4840/" },
                SecurityPolicies =
                [
                    new ServerSecurityPolicy { SecurityMode = MessageSecurityMode.Sign, SecurityPolicyUri = SecurityPolicies.Basic256Sha256 },
                    new ServerSecurityPolicy { SecurityMode = MessageSecurityMode.SignAndEncrypt, SecurityPolicyUri = SecurityPolicies.Basic256Sha256 },
                    new ServerSecurityPolicy { SecurityMode = MessageSecurityMode.Sign, SecurityPolicyUri = SecurityPolicies.Aes128_Sha256_RsaOaep },
                    new ServerSecurityPolicy { SecurityMode = MessageSecurityMode.SignAndEncrypt, SecurityPolicyUri = SecurityPolicies.Aes128_Sha256_RsaOaep },
                    new ServerSecurityPolicy { SecurityMode = MessageSecurityMode.Sign, SecurityPolicyUri = SecurityPolicies.Aes256_Sha256_RsaPss },
                    new ServerSecurityPolicy { SecurityMode = MessageSecurityMode.SignAndEncrypt, SecurityPolicyUri = SecurityPolicies.Aes256_Sha256_RsaPss },
                    new ServerSecurityPolicy { SecurityMode = MessageSecurityMode.None, SecurityPolicyUri = SecurityPolicies.None }
                ],
                UserTokenPolicies =
                [
                    new UserTokenPolicy(UserTokenType.Anonymous) { SecurityPolicyUri = SecurityPolicies.None },
                    new UserTokenPolicy(UserTokenType.UserName) { SecurityPolicyUri = SecurityPolicies.Basic256Sha256 },
                    new UserTokenPolicy(UserTokenType.Certificate) { SecurityPolicyUri = SecurityPolicies.Basic256Sha256 }
                ],
                DiagnosticsEnabled = true,
                MaxSessionCount = 100,
                MinSessionTimeout = 10_000,
                MaxSessionTimeout = 3_600_000,
                MaxBrowseContinuationPoints = 100,
                MaxQueryContinuationPoints = 10,
                MaxHistoryContinuationPoints = 100,
                MaxRequestAge = 600000,
                MinPublishingInterval = 50,
                MaxPublishingInterval = 3_600_000,
                PublishingResolution = 25,
                MaxSubscriptionLifetime = 3_600_000,
                MaxMessageQueueSize = 10_000,
                MaxNotificationQueueSize = 10_000,
                MaxNotificationsPerPublish = 10_000,
                MinMetadataSamplingInterval = 1000,
                MaxEventQueueSize = 10_000,
                AuditingEnabled = true,
                // From XML -> minimal operation limits relevant for typical interactions.
                OperationLimits = new OperationLimits
                {
                    MaxNodesPerRead = 4000,
                    MaxNodesPerWrite = 4000,
                    MaxNodesPerMethodCall = 1000,
                    MaxNodesPerBrowse = 4000,
                    MaxNodesPerTranslateBrowsePathsToNodeIds = 2000,
                    MaxMonitoredItemsPerCall = 4000
                },
                // Minimal capability list (DA) to reflect XML ServerCapabilities.
                ServerCapabilities = ["DA"]
            },
            DisableHiResClock = true,
            TraceConfiguration = new TraceConfiguration
            {
                OutputFilePath = "Logs/OpcUaServer.log",
                TraceMasks = 519, // Security, errors, service result exceptions & trace
                DeleteOnLoad = true
            },
            CertificateValidator = new CertificateValidator(TelemetryContext)
        };

        // Register the certificate validator with the configuration.
        await config.CertificateValidator.UpdateAsync(config).ConfigureAwait(false);

        application.ApplicationConfiguration = config;
        return application;
    }

    public virtual string[] GetNamespaceUris()
    {
        return [
            NamespaceUri,
            "http://opcfoundation.org/UA/",
            "http://opcfoundation.org/UA/DI/",
            "http://opcfoundation.org/UA/PADIM",
            "http://opcfoundation.org/UA/Machinery/",
            "http://opcfoundation.org/UA/Machinery/ProcessValues"
        ];
    }

    public virtual void LoadPredefinedNodes(NodeStateCollection collection, ISystemContext context)
    {
        LoadNodeSetFromEmbeddedResource<OpcUaServerConfiguration>("NodeSets.Opc.Ua.NodeSet2.xml", collection, context);
        LoadNodeSetFromEmbeddedResource<OpcUaServerConfiguration>("NodeSets.Opc.Ua.Di.NodeSet2.xml", collection, context);
        LoadNodeSetFromEmbeddedResource<OpcUaServerConfiguration>("NodeSets.Opc.Ua.PADIM.NodeSet2.xml", collection, context);
        LoadNodeSetFromEmbeddedResource<OpcUaServerConfiguration>("NodeSets.Opc.Ua.Machinery.NodeSet2.xml", collection, context);
        LoadNodeSetFromEmbeddedResource<OpcUaServerConfiguration>("NodeSets.Opc.Ua.Machinery.ProcessValues.NodeSet2.xml", collection, context);
    } 

    protected void LoadNodeSetFromEmbeddedResource<TAssemblyType>(string name, NodeStateCollection nodes, ISystemContext context)
    {
        var assembly = typeof(TAssemblyType).Assembly;
        using var stream = assembly.GetManifestResourceStream($"{assembly.FullName!.Split(',')[0]}.{name}");

        var nodeSet = UANodeSet.Read(stream ?? throw new ArgumentException("Embedded resource could not be found.", nameof(name)));
        nodeSet.Import(context, nodes);
    }
}