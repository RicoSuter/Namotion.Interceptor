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
    /// Gets or sets an async predicate that is called when an unknown (not statically typed) OPC UA node or variable is found during browsing.
    /// If the function returns true, the node is added as a dynamic property to the given subject.
    /// Default is add all missing as dynamic properties.
    /// </summary>
    public Func<ReferenceDescription, CancellationToken, Task<bool>>? ShouldAddDynamicProperties { get; init; } = 
        (_, _) => Task.FromResult(true);
    
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
                OperationTimeout = 15000,
                MaxStringLength = 1_048_576,
                MaxByteStringLength = 1_048_576,
                MaxMessageSize = 4_194_304,
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
}