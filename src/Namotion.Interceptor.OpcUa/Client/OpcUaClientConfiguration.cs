using Namotion.Interceptor.Sources.Paths;
using Opc.Ua;
using Opc.Ua.Configuration;

namespace Namotion.Interceptor.OpcUa.Client;

public class OpcUaClientConfiguration
{
    public required string ServerUrl { get; init; }

    public string? RootName { get; init; }
    
    public required ISourcePathProvider SourcePathProvider { get; init; }

    public string ApplicationName { get; init; } = "Namotion.Interceptor.Client";
    
    public int MaxItemsPerSubscription { get; init; } = 1000;
    
    public bool AddDynamicProperties { get; init; } = true;

    public required OpcUaTypeResolver TypeResolver { get; init; }
    
    public required OpcUaDataValueConverter ValueConverter { get; init; }

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