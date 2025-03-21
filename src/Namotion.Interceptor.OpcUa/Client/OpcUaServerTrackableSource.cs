﻿//using Microsoft.Extensions.Hosting;
//using Microsoft.Extensions.Logging;

//using System.Reactive.Linq;

//using Namotion.Interceptable.Sources.Abstractions;
//using Opc.Ua;
//using Opc.Ua.Configuration;

//using Microsoft.Extensions.DependencyInjection;
//using Opc.Ua.Server;

//namespace Namotion.Interceptable.OpcUa.Server;

//internal class OpcUaClientTrackableSource<TProxy> : BackgroundService, ISubjectSource, IDisposable
//    where TProxy : IInterceptorCollection
//{
//    internal const string OpcVariableKey = "OpcVariable";

//    private readonly IInterceptorContext _context;
//    private readonly TProxy _proxy;
//    private readonly string _serverUrl;
//    private readonly ILogger _logger;
//    private readonly string? _rootName;

//    internal ISourcePathProvider SourcePathProvider { get; }

//    public OpcUaClientTrackableSource(
//        TProxy interceptable,
//        string serverUrl,
//        ISourcePathProvider sourcePathProvider,
//        ILogger<OpcUaClientTrackableSource<TProxy>> logger,
//        string? rootName)
//    {
//        _context = interceptable.Context ??
//            throw new InvalidOperationException($"Context is not set on {nameof(TProxy)}.");

//        _proxy = interceptable;
//        _serverUrl = serverUrl;
//        _logger = logger;
//        _rootName = rootName;

//        SourcePathProvider = sourcePathProvider;
//    }

//    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
//    {
//        while (!stoppingToken.IsCancellationRequested)
//        {
//            using var stream = typeof(OpcUaSubjectServerSourceExtensions).Assembly
//                .GetManifestResourceStream("Namotion.Interceptable.OpcUa.MyOpcUaServer.Config.xml");

//            var application = new ApplicationInstance
//            {
//                ApplicationName = "MyOpcUaServer",
//                ApplicationType = ApplicationType.Client,
//                ApplicationConfiguration = await ApplicationConfiguration.Load(
//                    stream, ApplicationType.Server, typeof(ApplicationConfiguration), false)
//            };

//            try
//            {
//                await application.CheckApplicationInstanceCertificate(true, CertificateFactory.DefaultKeySize);

//                // Create the OPC UA client
//                var endpointURL = "opc.tcp://localhost:4840"; // Change to your server's endpoint
//                var endpointConfiguration = EndpointConfiguration.Create(application.ApplicationConfiguration);
//                var endpoint = new ConfiguredEndpoint(null, new EndpointDescription(endpointURL), endpointConfiguration);

//                using (var session = await Session.Create(
//                    application.ApplicationConfiguration,
//                    endpoint,
//                    false,
//                    "MyOpcUaClient",
//                    60000,
//                    new UserIdentity(), // Use anonymous authentication; adjust if needed
//                    null))
//                {
//                    Console.WriteLine("Session created and connected.");

//                    // Browse the Root folder
//                    ReferenceDescriptionCollection references;
//                    Byte[] continuationPoint;
//                    session.Browse(
//                        null,
//                        null,
//                        ObjectIds.ObjectsFolder,
//                        0u,
//                        BrowseDirection.Forward,
//                        ReferenceTypeIds.HierarchicalReferences,
//                        true,
//                        (uint)NodeClass.Variable | (uint)NodeClass.Object | (uint)NodeClass.Method,
//                        out continuationPoint,
//                        out references);

//                    Console.WriteLine("Browsing nodes:");
//                    foreach (var rd in references)
//                    {
//                        Console.WriteLine($"{rd.DisplayName}: {rd.BrowseName}");
//                    }

//                    // Reading a specific node
//                    NodeId nodeId = new NodeId("YourVariableNodeID", 2); // Adjust as needed
//                    DataValue value = session.ReadValue(nodeId);
//                    Console.WriteLine($"Value of {nodeId}: {value.Value}");
//                }
//            }
//            catch (Exception ex)
//            {
//                Console.WriteLine($"Exception: {ex.Message}");
//            }



//            //_server = new OpcUaSubjectServer<TProxy>(_proxy, this, _rootName);

//            //await application.CheckApplicationInstanceCertificate(true, CertificateFactory.DefaultKeySize);
//            //await application.Start(_server);

//            //await Task.Delay(-1, stoppingToken);
//            //catch (Exception ex)
//            //{
//            //    application.Stop();

//            //    if (ex is not TaskCanceledException)
//            //    {
//            //        _logger.LogError(ex, "Failed to start OPC UA server.");
//            //        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
//            //    }
//            //}
//        }
//    }

//    public Task<IDisposable?> InitializeAsync(IEnumerable<PropertyPathReference> properties, Action<PropertyPathReference> propertyUpdateAction, CancellationToken cancellationToken)
//    {
//        return Task.FromResult<IDisposable?>(null);
//    }

//    public Task<IEnumerable<PropertyPathReference>> ReadAsync(IEnumerable<PropertyPathReference> properties, CancellationToken cancellationToken)
//    {
//        return Task.FromResult<IEnumerable<PropertyPathReference>>(properties
//            .Where(p => p.Property.TryGetPropertyData(OpcUaSubjectServerSource<TProxy>.OpcVariableKey, out var _))
//            .Select(property => (property, node: property.Property.GetPropertyData(OpcUaSubjectServerSource<TProxy>.OpcVariableKey) as BaseDataVariableState))
//            .Where(p => p.node is not null)
//            .Select(p => new PropertyPathReference(p.property.Property, p.property.Path,
//                p.property.Property.Metadata.Type == typeof(decimal) ? Convert.ToDecimal(p.node!.Value) : p.node!.Value))
//            .ToList());
//    }

//    public Task WriteAsync(IEnumerable<PropertyPathReference> propertyChanges, CancellationToken cancellationToken)
//    {
//        //foreach (var property in propertyChanges
//        //    .Where(p => p.Property.TryGetPropertyData(OpcUaSubjectServerSource<TProxy>.OpcVariableKey, out var _)))
//        //{
//        //    var node = property.Property.GetPropertyData(OpcUaSubjectServerSource<TProxy>.OpcVariableKey) as BaseDataVariableState;
//        //    if (node is not null)
//        //    {
//        //        var actualValue = property.Value;
//        //        if (actualValue is decimal)
//        //        {
//        //            actualValue = Convert.ToDouble(actualValue);
//        //        }

//        //        node.Value = actualValue;
//        //        node.ClearChangeMasks(_server?.CurrentInstance.DefaultSystemContext, false);
//        //    }
//        //}

//        return Task.CompletedTask;
//    }

//    public string? GetSourcePropertyPath(PropertyReference property)
//    {
//        return SourcePathProvider.GetSourcePropertyPath(property);
//    }
//}
