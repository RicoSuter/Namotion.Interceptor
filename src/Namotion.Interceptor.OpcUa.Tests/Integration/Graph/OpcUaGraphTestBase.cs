using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Hosting;
using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.OpcUa.Server;
using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Validation;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration.Graph;

/// <summary>
/// Configuration options for graph sync server setup.
/// </summary>
public class GraphServerOptions
{
    public bool EnableLiveSync { get; set; } = true;
    public bool EnableExternalNodeManagement { get; set; } = false;
    public OpcUaTypeRegistry? TypeRegistry { get; set; }
    public TimeSpan BufferTime { get; set; } = TimeSpan.FromMilliseconds(50);
}

/// <summary>
/// Configuration options for graph sync client setup.
/// </summary>
public class GraphClientOptions
{
    public bool EnableLiveSync { get; set; } = true;
    public bool EnableRemoteNodeManagement { get; set; } = false;
    public bool EnableModelChangeEvents { get; set; } = true;
    public bool EnablePeriodicResync { get; set; } = false;
    public TimeSpan PeriodicResyncInterval { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan BufferTime { get; set; } = TimeSpan.FromMilliseconds(50);
}

/// <summary>
/// Encapsulates a running server for graph sync tests.
/// </summary>
public class GraphServerContext : IAsyncDisposable
{
    public required IHost Host { get; init; }
    public required TestRoot Root { get; init; }
    public required IInterceptorSubjectContext Context { get; init; }
    public required PortLease Port { get; init; }
    public required TestLogger Logger { get; init; }
    public OpcUaServerDiagnostics? Diagnostics { get; init; }

    public async ValueTask DisposeAsync()
    {
        await Host.StopAsync();
        Host.Dispose();
        Port.Dispose();
    }
}

/// <summary>
/// Encapsulates a running client for graph sync tests.
/// </summary>
public class GraphClientContext : IAsyncDisposable
{
    public required IHost Host { get; init; }
    public required TestRoot Root { get; init; }
    public required IInterceptorSubjectContext Context { get; init; }
    public OpcUaClientDiagnostics? Diagnostics { get; init; }

    public async ValueTask DisposeAsync()
    {
        await Host.StopAsync();
        Host.Dispose();
    }
}

/// <summary>
/// Encapsulates a running server with OPC UA client session for browsing tests.
/// </summary>
public class GraphServerWithSessionContext : IAsyncDisposable
{
    public required IHost Host { get; init; }
    public required TestRoot Root { get; init; }
    public required IInterceptorSubjectContext Context { get; init; }
    public required PortLease Port { get; init; }
    public required TestLogger Logger { get; init; }
    public required ISession Session { get; init; }
    public OpcUaServerDiagnostics? Diagnostics { get; init; }

    public async ValueTask DisposeAsync()
    {
        await Session.CloseAsync();
        Session.Dispose();
        await Host.StopAsync();
        Host.Dispose();
        Port.Dispose();
    }
}

/// <summary>
/// Encapsulates a running server+client pair for bidirectional sync tests.
/// </summary>
public class GraphBidirectionalContext : IAsyncDisposable
{
    public required IHost ServerHost { get; init; }
    public required TestRoot ServerRoot { get; init; }
    public required IInterceptorSubjectContext ServerContext { get; init; }
    public required IHost ClientHost { get; init; }
    public required TestRoot ClientRoot { get; init; }
    public required IInterceptorSubjectContext ClientContext { get; init; }
    public required PortLease Port { get; init; }
    public required TestLogger Logger { get; init; }
    public OpcUaServerDiagnostics? ServerDiagnostics { get; init; }
    public OpcUaClientDiagnostics? ClientDiagnostics { get; init; }

    public async ValueTask DisposeAsync()
    {
        await ClientHost.StopAsync();
        ClientHost.Dispose();
        await ServerHost.StopAsync();
        ServerHost.Dispose();
        Port.Dispose();
    }
}

/// <summary>
/// Base class for OPC UA graph synchronization integration tests.
/// Provides shared infrastructure for server, client, and bidirectional test setups.
/// </summary>
[Trait("Category", "Integration")]
public abstract class OpcUaGraphTestBase
{
    protected readonly ITestOutputHelper Output;
    protected readonly TestLogger Logger;

    protected OpcUaGraphTestBase(ITestOutputHelper output)
    {
        Output = output;
        Logger = new TestLogger(output);
    }

    #region Server Setup

    /// <summary>
    /// Creates and starts a server with graph sync enabled.
    /// </summary>
    protected async Task<GraphServerContext> StartServerAsync(
        GraphServerOptions? options = null)
    {
        options ??= new GraphServerOptions();
        var port = await OpcUaTestPortPool.AcquireAsync();

        var builder = Host.CreateApplicationBuilder();

        builder.Services.Configure<HostOptions>(opts =>
        {
            opts.ShutdownTimeout = TimeSpan.FromSeconds(5);
        });

        builder.Services.AddLogging(logging =>
        {
            logging.ClearProviders();
            logging.SetMinimumLevel(LogLevel.Debug);
            logging.AddXunit(Logger, "Server", LogLevel.Information);
        });

        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithDataAnnotationValidation()
            .WithHostedServices(builder.Services);

        var root = new TestRoot(context)
        {
            Connected = true,
            Name = "GraphSyncTest",
            Number = 1m,
            People = [],
            PeopleByName = new Dictionary<string, TestPerson>()
        };

        builder.Services.AddSingleton(root);
        builder.Services.AddOpcUaSubjectServer(
            sp => sp.GetRequiredService<TestRoot>(),
            sp =>
            {
                var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
                var telemetryContext = DefaultTelemetry.Create(b =>
                    b.Services.AddSingleton(loggerFactory));

                return new OpcUaServerConfiguration
                {
                    RootName = "Root",
                    BaseAddress = port.BaseAddress,
                    ValueConverter = new OpcUaValueConverter(),
                    TelemetryContext = telemetryContext,
                    CleanCertificateStore = true,
                    AutoAcceptUntrustedCertificates = true,
                    CertificateStoreBasePath = port.CertificateStoreBasePath,
                    EnableLiveSync = options.EnableLiveSync,
                    EnableExternalNodeManagement = options.EnableExternalNodeManagement,
                    TypeRegistry = options.TypeRegistry,
                    BufferTime = options.BufferTime
                };
            });

        var host = builder.Build();

        var serverService = host.Services
            .GetServices<IHostedService>()
            .OfType<OpcUaSubjectServerBackgroundService>()
            .FirstOrDefault();

        await host.StartAsync();
        Logger.Log($"Server started with EnableLiveSync={options.EnableLiveSync}");

        // Wait for server to be ready
        await Task.Delay(500);

        return new GraphServerContext
        {
            Host = host,
            Root = root,
            Context = context,
            Port = port,
            Logger = Logger,
            Diagnostics = serverService?.Diagnostics
        };
    }

    /// <summary>
    /// Creates and starts a server with graph sync enabled and an OPC UA client session for browsing.
    /// </summary>
    protected async Task<GraphServerWithSessionContext> StartServerWithSessionAsync(
        GraphServerOptions? options = null)
    {
        options ??= new GraphServerOptions();
        var serverCtx = await StartServerAsync(options);

        // Create OPC UA client session for browsing
        var clientConfig = CreateBrowseClientConfiguration(serverCtx.Port.CertificateStoreBasePath);
        await clientConfig.ValidateAsync(ApplicationType.Client);

        var endpointConfiguration = EndpointConfiguration.Create(clientConfig);
        var serverUri = new Uri(serverCtx.Port.ServerUrl);

        using var discoveryClient = await DiscoveryClient.CreateAsync(
            clientConfig,
            serverUri,
            endpointConfiguration);

        var endpoints = await discoveryClient.GetEndpointsAsync(null);

        var endpointDescription = CoreClientUtils.SelectEndpoint(
            clientConfig,
            serverUri,
            endpoints,
            useSecurity: false,
            NullTelemetryContext.Instance);

        var endpoint = new ConfiguredEndpoint(null, endpointDescription, endpointConfiguration);

        var sessionFactory = new DefaultSessionFactory(NullTelemetryContext.Instance);
        var session = await sessionFactory.CreateAsync(
            clientConfig,
            endpoint,
            updateBeforeConnect: false,
            checkDomain: false,
            sessionName: "BrowseClient",
            sessionTimeout: 60000,
            identity: new UserIdentity(new AnonymousIdentityToken()),
            preferredLocales: null);

        Logger.Log("Browse session connected");

        return new GraphServerWithSessionContext
        {
            Host = serverCtx.Host,
            Root = serverCtx.Root,
            Context = serverCtx.Context,
            Port = serverCtx.Port,
            Logger = serverCtx.Logger,
            Session = session,
            Diagnostics = serverCtx.Diagnostics
        };
    }

    private static ApplicationConfiguration CreateBrowseClientConfiguration(string certificateStoreBasePath)
    {
        return new ApplicationConfiguration
        {
            ApplicationName = "BrowseClient",
            ApplicationType = ApplicationType.Client,
            ApplicationUri = "urn:BrowseClient",
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = "Directory",
                    StorePath = $"{certificateStoreBasePath}/browse-own",
                    SubjectName = "CN=BrowseClient"
                },
                TrustedIssuerCertificates = new CertificateTrustList
                {
                    StoreType = "Directory",
                    StorePath = $"{certificateStoreBasePath}/issuer"
                },
                TrustedPeerCertificates = new CertificateTrustList
                {
                    StoreType = "Directory",
                    StorePath = $"{certificateStoreBasePath}/trusted"
                },
                RejectedCertificateStore = new CertificateTrustList
                {
                    StoreType = "Directory",
                    StorePath = $"{certificateStoreBasePath}/rejected"
                },
                AutoAcceptUntrustedCertificates = true
            },
            TransportQuotas = new TransportQuotas
            {
                OperationTimeout = 30000
            },
            ClientConfiguration = new ClientConfiguration
            {
                DefaultSessionTimeout = 60000
            }
        };
    }

    #endregion

    #region Client Setup

    /// <summary>
    /// Creates and starts a client with graph sync enabled, connecting to the specified server.
    /// </summary>
    protected async Task<GraphClientContext> StartClientAsync(
        PortLease port,
        GraphClientOptions? options = null)
    {
        options ??= new GraphClientOptions();

        var builder = Host.CreateApplicationBuilder();

        builder.Services.Configure<HostOptions>(opts =>
        {
            opts.ShutdownTimeout = TimeSpan.FromSeconds(5);
        });

        builder.Services.AddLogging(logging =>
        {
            logging.ClearProviders();
            logging.SetMinimumLevel(LogLevel.Debug);
            logging.AddXunit(Logger, "Client", LogLevel.Information);
        });

        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithDataAnnotationValidation()
            .WithHostedServices(builder.Services);

        var root = new TestRoot(context)
        {
            Connected = false,
            Name = "",
            Number = 0m,
            People = [],
            PeopleByName = new Dictionary<string, TestPerson>()
        };

        builder.Services.AddSingleton(root);
        builder.Services.AddOpcUaSubjectClientSource(
            sp => sp.GetRequiredService<TestRoot>(),
            sp =>
            {
                var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
                var telemetryContext = DefaultTelemetry.Create(b =>
                    b.Services.AddSingleton(loggerFactory));

                return new OpcUaClientConfiguration
                {
                    ServerUrl = port.ServerUrl,
                    RootName = "Root",
                    TypeResolver = new OpcUaTypeResolver(sp.GetRequiredService<ILogger<OpcUaTypeResolver>>()),
                    ValueConverter = new OpcUaValueConverter(),
                    SubjectFactory = new OpcUaSubjectFactory(DefaultSubjectFactory.Instance),
                    TelemetryContext = telemetryContext,
                    CertificateStoreBasePath = $"{port.CertificateStoreBasePath}/client",
                    EnableLiveSync = options.EnableLiveSync,
                    EnableRemoteNodeManagement = options.EnableRemoteNodeManagement,
                    EnableModelChangeEvents = options.EnableModelChangeEvents,
                    EnablePeriodicResync = options.EnablePeriodicResync,
                    PeriodicResyncInterval = options.PeriodicResyncInterval,
                    BufferTime = options.BufferTime
                };
            });

        var host = builder.Build();

        var clientSource = host.Services
            .GetServices<IHostedService>()
            .OfType<OpcUaSubjectClientSource>()
            .FirstOrDefault();

        await host.StartAsync();
        Logger.Log($"Client started with EnableLiveSync={options.EnableLiveSync}, " +
                   $"EnableModelChangeEvents={options.EnableModelChangeEvents}, " +
                   $"EnablePeriodicResync={options.EnablePeriodicResync}");

        // Wait for client to connect and sync
        await AsyncTestHelpers.WaitUntilAsync(
            () => root.Connected,
            timeout: TimeSpan.FromSeconds(30),
            message: "Client should connect and sync Connected property");

        Logger.Log("Client connected and synced");

        return new GraphClientContext
        {
            Host = host,
            Root = root,
            Context = context,
            Diagnostics = clientSource?.Diagnostics
        };
    }

    #endregion

    #region Bidirectional Setup

    /// <summary>
    /// Creates and starts a server+client pair with graph sync enabled on both sides.
    /// </summary>
    protected async Task<GraphBidirectionalContext> StartBidirectionalAsync(
        GraphServerOptions? serverOptions = null,
        GraphClientOptions? clientOptions = null)
    {
        serverOptions ??= new GraphServerOptions();
        clientOptions ??= new GraphClientOptions();

        var port = await OpcUaTestPortPool.AcquireAsync();

        // Start server
        var serverBuilder = Host.CreateApplicationBuilder();

        serverBuilder.Services.Configure<HostOptions>(opts =>
        {
            opts.ShutdownTimeout = TimeSpan.FromSeconds(5);
        });

        serverBuilder.Services.AddLogging(logging =>
        {
            logging.ClearProviders();
            logging.SetMinimumLevel(LogLevel.Debug);
            logging.AddXunit(Logger, "Server", LogLevel.Information);
        });

        var serverContext = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithDataAnnotationValidation()
            .WithHostedServices(serverBuilder.Services);

        var serverRoot = new TestRoot(serverContext)
        {
            Connected = true,
            Name = "BidirectionalTest",
            Number = 1m,
            People = [],
            PeopleByName = new Dictionary<string, TestPerson>()
        };

        serverBuilder.Services.AddSingleton(serverRoot);
        serverBuilder.Services.AddOpcUaSubjectServer(
            sp => sp.GetRequiredService<TestRoot>(),
            sp =>
            {
                var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
                var telemetryContext = DefaultTelemetry.Create(b =>
                    b.Services.AddSingleton(loggerFactory));

                return new OpcUaServerConfiguration
                {
                    RootName = "Root",
                    BaseAddress = port.BaseAddress,
                    ValueConverter = new OpcUaValueConverter(),
                    TelemetryContext = telemetryContext,
                    CleanCertificateStore = true,
                    AutoAcceptUntrustedCertificates = true,
                    CertificateStoreBasePath = port.CertificateStoreBasePath,
                    EnableLiveSync = serverOptions.EnableLiveSync,
                    EnableExternalNodeManagement = serverOptions.EnableExternalNodeManagement,
                    TypeRegistry = serverOptions.TypeRegistry,
                    BufferTime = serverOptions.BufferTime
                };
            });

        var serverHost = serverBuilder.Build();

        var serverService = serverHost.Services
            .GetServices<IHostedService>()
            .OfType<OpcUaSubjectServerBackgroundService>()
            .FirstOrDefault();

        await serverHost.StartAsync();
        Logger.Log($"Server started with EnableLiveSync={serverOptions.EnableLiveSync}");

        // Wait for server to be ready
        await Task.Delay(500);

        // Start client
        var clientBuilder = Host.CreateApplicationBuilder();

        clientBuilder.Services.Configure<HostOptions>(opts =>
        {
            opts.ShutdownTimeout = TimeSpan.FromSeconds(5);
        });

        clientBuilder.Services.AddLogging(logging =>
        {
            logging.ClearProviders();
            logging.SetMinimumLevel(LogLevel.Debug);
            logging.AddXunit(Logger, "Client", LogLevel.Information);
        });

        var clientContext = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithDataAnnotationValidation()
            .WithHostedServices(clientBuilder.Services);

        var clientRoot = new TestRoot(clientContext)
        {
            Connected = false,
            Name = "",
            Number = 0m,
            People = [],
            PeopleByName = new Dictionary<string, TestPerson>()
        };

        clientBuilder.Services.AddSingleton(clientRoot);
        clientBuilder.Services.AddOpcUaSubjectClientSource(
            sp => sp.GetRequiredService<TestRoot>(),
            sp =>
            {
                var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
                var telemetryContext = DefaultTelemetry.Create(b =>
                    b.Services.AddSingleton(loggerFactory));

                return new OpcUaClientConfiguration
                {
                    ServerUrl = port.ServerUrl,
                    RootName = "Root",
                    TypeResolver = new OpcUaTypeResolver(sp.GetRequiredService<ILogger<OpcUaTypeResolver>>()),
                    ValueConverter = new OpcUaValueConverter(),
                    SubjectFactory = new OpcUaSubjectFactory(DefaultSubjectFactory.Instance),
                    TelemetryContext = telemetryContext,
                    CertificateStoreBasePath = $"{port.CertificateStoreBasePath}/client",
                    EnableLiveSync = clientOptions.EnableLiveSync,
                    EnableRemoteNodeManagement = clientOptions.EnableRemoteNodeManagement,
                    EnableModelChangeEvents = clientOptions.EnableModelChangeEvents,
                    EnablePeriodicResync = clientOptions.EnablePeriodicResync,
                    PeriodicResyncInterval = clientOptions.PeriodicResyncInterval,
                    BufferTime = clientOptions.BufferTime
                };
            });

        var clientHost = clientBuilder.Build();

        var clientSource = clientHost.Services
            .GetServices<IHostedService>()
            .OfType<OpcUaSubjectClientSource>()
            .FirstOrDefault();

        await clientHost.StartAsync();
        Logger.Log($"Client started with EnableLiveSync={clientOptions.EnableLiveSync}");

        // Wait for client to connect and sync
        await AsyncTestHelpers.WaitUntilAsync(
            () => clientRoot.Connected,
            timeout: TimeSpan.FromSeconds(30),
            message: "Client should connect and sync Connected property");

        Logger.Log("Client connected and synced");

        return new GraphBidirectionalContext
        {
            ServerHost = serverHost,
            ServerRoot = serverRoot,
            ServerContext = serverContext,
            ClientHost = clientHost,
            ClientRoot = clientRoot,
            ClientContext = clientContext,
            Port = port,
            Logger = Logger,
            ServerDiagnostics = serverService?.Diagnostics,
            ClientDiagnostics = clientSource?.Diagnostics
        };
    }

    #endregion

    #region Browse Helpers

    /// <summary>
    /// Browses child nodes of a parent node.
    /// </summary>
    protected static async Task<IReadOnlyList<ReferenceDescription>> BrowseChildNodesAsync(
        ISession session,
        NodeId parentNodeId)
    {
        var browseDescription = new BrowseDescriptionCollection
        {
            new BrowseDescription
            {
                NodeId = parentNodeId,
                BrowseDirection = BrowseDirection.Forward,
                ReferenceTypeId = ReferenceTypeIds.HierarchicalReferences,
                IncludeSubtypes = true,
                NodeClassMask = (uint)(NodeClass.Object | NodeClass.Variable),
                ResultMask = (uint)BrowseResultMask.All
            }
        };

        var response = await session.BrowseAsync(
            null,
            null,
            0,
            browseDescription,
            CancellationToken.None);

        if (response.Results.Count > 0 && StatusCode.IsGood(response.Results[0].StatusCode))
        {
            return response.Results[0].References;
        }

        return Array.Empty<ReferenceDescription>();
    }

    /// <summary>
    /// Finds the Root node under ObjectsFolder.
    /// </summary>
    protected static async Task<NodeId> FindRootNodeIdAsync(ISession session)
    {
        var objectsChildren = await BrowseChildNodesAsync(session, ObjectIds.ObjectsFolder);
        var rootRef = objectsChildren.FirstOrDefault(c => c.BrowseName.Name == "Root");
        if (rootRef == null)
        {
            throw new InvalidOperationException("Root node not found under ObjectsFolder");
        }
        return ExpandedNodeId.ToNodeId(rootRef.NodeId, session.NamespaceUris);
    }

    /// <summary>
    /// Finds a named child node under a parent node.
    /// </summary>
    protected static async Task<NodeId> FindChildNodeIdAsync(ISession session, NodeId parentNodeId, string childName)
    {
        var children = await BrowseChildNodesAsync(session, parentNodeId);
        var childRef = children.FirstOrDefault(c => c.BrowseName.Name == childName);
        if (childRef == null)
        {
            throw new InvalidOperationException($"Child node '{childName}' not found under parent");
        }
        return ExpandedNodeId.ToNodeId(childRef.NodeId, session.NamespaceUris);
    }

    /// <summary>
    /// Reads a value from a node.
    /// </summary>
    protected static async Task<DataValue> ReadValueAsync(ISession session, NodeId nodeId)
    {
        var nodesToRead = new ReadValueIdCollection
        {
            new ReadValueId
            {
                NodeId = nodeId,
                AttributeId = Opc.Ua.Attributes.Value
            }
        };

        var response = await session.ReadAsync(
            null,
            0,
            TimestampsToReturn.Both,
            nodesToRead,
            CancellationToken.None);

        if (response.Results.Count > 0)
        {
            return response.Results[0];
        }

        return new DataValue(StatusCodes.BadNodeIdUnknown);
    }

    #endregion
}
