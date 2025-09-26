using Namotion.Interceptor.Sources.Paths;
using Opc.Ua;
using Opc.Ua.Configuration;

namespace Namotion.Interceptor.OpcUa.Server;

public class OpcUaServerConfiguration
{
    public string ApplicationName { get; init; } = "Namotion.Interceptor.Server";

    public string? RootName { get; init; }
    
    public required ISourcePathProvider SourcePathProvider { get; init; }

    public required OpcUaDataValueConverter ValueConverter { get; init; }
    
    public virtual ApplicationInstance CreateApplicationInstance()
    {
        var application = new ApplicationInstance
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
                MaxStringLength = 1_048_576,
                MaxByteStringLength = 1_048_576,
                MaxMessageSize = 4_194_304,
                MaxArrayLength = 65_535,
                MaxBufferSize = 65_535,
                ChannelLifetime = 300000,
                SecurityTokenLifetime = 3_600_000
            },
            ServerConfiguration = new ServerConfiguration
            {
                // Base addresses kept minimal (tcp only). Add https if required later.
                BaseAddresses = { "opc.tcp://localhost:4840/" },
                SecurityPolicies = new ServerSecurityPolicyCollection
                {
                    // Order matches typical preference: strong -> none at end.
                    new ServerSecurityPolicy { SecurityMode = MessageSecurityMode.Sign, SecurityPolicyUri = SecurityPolicies.Basic256Sha256 },
                    new ServerSecurityPolicy { SecurityMode = MessageSecurityMode.SignAndEncrypt, SecurityPolicyUri = SecurityPolicies.Basic256Sha256 },
                    new ServerSecurityPolicy { SecurityMode = MessageSecurityMode.Sign, SecurityPolicyUri = SecurityPolicies.Aes128_Sha256_RsaOaep },
                    new ServerSecurityPolicy { SecurityMode = MessageSecurityMode.SignAndEncrypt, SecurityPolicyUri = SecurityPolicies.Aes128_Sha256_RsaOaep },
                    new ServerSecurityPolicy { SecurityMode = MessageSecurityMode.Sign, SecurityPolicyUri = SecurityPolicies.Aes256_Sha256_RsaPss },
                    new ServerSecurityPolicy { SecurityMode = MessageSecurityMode.SignAndEncrypt, SecurityPolicyUri = SecurityPolicies.Aes256_Sha256_RsaPss },
                    new ServerSecurityPolicy { SecurityMode = MessageSecurityMode.None, SecurityPolicyUri = SecurityPolicies.None }
                },
                UserTokenPolicies = new UserTokenPolicyCollection
                {
                    new UserTokenPolicy(UserTokenType.Anonymous) { SecurityPolicyUri = SecurityPolicies.None },
                    new UserTokenPolicy(UserTokenType.UserName) { SecurityPolicyUri = SecurityPolicies.Basic256Sha256 },
                    new UserTokenPolicy(UserTokenType.Certificate) { SecurityPolicyUri = SecurityPolicies.Basic256Sha256 }
                },
                DiagnosticsEnabled = true,
                MaxSessionCount = 100,
                MinSessionTimeout = 10_000,
                MaxSessionTimeout = 3_600_000,
                MaxBrowseContinuationPoints = 10,
                MaxQueryContinuationPoints = 10,
                MaxHistoryContinuationPoints = 100,
                MaxRequestAge = 600000,
                MinPublishingInterval = 100,
                MaxPublishingInterval = 3_600_000,
                PublishingResolution = 50,
                MaxSubscriptionLifetime = 3_600_000,
                MaxMessageQueueSize = 100,
                MaxNotificationQueueSize = 100,
                MaxNotificationsPerPublish = 1000,
                MinMetadataSamplingInterval = 1000,
                MaxEventQueueSize = 10_000,
                AuditingEnabled = true,
                // From XML -> minimal operation limits relevant for typical interactions.
                OperationLimits = new OperationLimits
                {
                    MaxNodesPerRead = 1000,
                    MaxNodesPerWrite = 1000,
                    MaxNodesPerMethodCall = 250,
                    MaxNodesPerBrowse = 2500,
                    MaxNodesPerTranslateBrowsePathsToNodeIds = 1000,
                    MaxMonitoredItemsPerCall = 1000
                },
                // Minimal capability list (DA) to reflect XML ServerCapabilities.
                ServerCapabilities = new StringCollection { "DA" }
            },
            DisableHiResClock = true,
            TraceConfiguration = new TraceConfiguration
            {
                OutputFilePath = "Logs/OpcUaServer.log",
                TraceMasks = 519, // Security, errors, service result exceptions & trace
                DeleteOnLoad = true
            },
            CertificateValidator = new CertificateValidator()
        };

        // Register the certificate validator with the configuration.
        config.CertificateValidator.Update(config);

        application.ApplicationConfiguration = config;
        return application;
    }
}