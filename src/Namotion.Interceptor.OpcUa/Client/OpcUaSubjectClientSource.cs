using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Sources;
using Namotion.Interceptor.Sources.Paths;
using Namotion.Interceptor.Tracking.Change;
using Opc.Ua;
using Opc.Ua.Configuration;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Client;

internal class OpcUaSubjectClientSource<TSubject> : BackgroundService, ISubjectSource, IDisposable
    where TSubject : IInterceptorSubject
{
    private const string PathDelimiter = ".";

    internal const string OpcVariableKey = "OpcVariable";

    private readonly TSubject _subject;
    private readonly string _serverUrl;
    private readonly ISourcePathProvider _sourcePathProvider;
    private readonly ILogger _logger;
    private readonly string? _rootName;

    private ISubjectMutationDispatcher? _dispatcher;
    private Session? _session;

    public OpcUaSubjectClientSource(
        TSubject subject,
        string serverUrl,
        ISourcePathProvider sourcePathProvider,
        ILogger<OpcUaSubjectClientSource<TSubject>> logger,
        string? rootName)
    {
        _subject = subject;
        _serverUrl = serverUrl;
        _sourcePathProvider = sourcePathProvider;
        _logger = logger;
        _rootName = rootName;
    }

    public IInterceptorSubject Subject => _subject;

    public Task<IDisposable?> StartListeningAsync(ISubjectMutationDispatcher dispatcher, CancellationToken cancellationToken)
    {
        _dispatcher = dispatcher;
        return Task.FromResult<IDisposable?>(null);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using var stream = typeof(OpcUaSubjectServerSourceExtensions).Assembly
                .GetManifestResourceStream("Namotion.Interceptor.OpcUa.MyOpcUaServer.Config.xml");

            var application = new ApplicationInstance
            {
                ApplicationName = "MyOpcUaServer",
                ApplicationType = ApplicationType.Client,
                ApplicationConfiguration = await ApplicationConfiguration.Load(
                    stream, ApplicationType.Server, typeof(ApplicationConfiguration), false),
            };

            application.ApplicationConfiguration.ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = 60000 };

            try
            {
                await application.CheckApplicationInstanceCertificate(true, CertificateFactory.DefaultKeySize);

                // Create the OPC UA client
                var endpointConfiguration = EndpointConfiguration.Create(application.ApplicationConfiguration);

                var endpointDescription = CoreClientUtils.SelectEndpoint(application.ApplicationConfiguration, 
                    _serverUrl, false);

                // var endpointConfiguration = EndpointConfiguration.Create(applicationConfiguration);

                // var endpoint2 = CoreClientUtils.SelectEndpoint(_serverUrl, false);
                var endpoint = new ConfiguredEndpoint(null, endpointDescription, endpointConfiguration);

                using var session = await Session.Create(
                    application.ApplicationConfiguration,
                    endpoint,
                    false,
                    "MyOpcUaClient2",
                    60000,
                    new UserIdentity(), // Use anonymous authentication; adjust if needed
                    null, stoppingToken);
             
                Console.WriteLine("Session created and connected.");

                _session = session;

                // Browse the Root folder
                ReferenceDescriptionCollection references;
                Byte[] continuationPoint;
                session.Browse(
                    null,
                    null,
                    ObjectIds.ObjectsFolder,
                    0u,
                    BrowseDirection.Forward,
                    ReferenceTypeIds.HierarchicalReferences,
                    true,
                    (uint)NodeClass.Variable | (uint)NodeClass.Object | (uint)NodeClass.Method,
                    out continuationPoint,
                    out references);

                var rootNode = references.FirstOrDefault(r => r.BrowseName == _rootName);
                if (rootNode is not null)
                {
                    var subscription = new Subscription(session.DefaultSubscription)
                    {
                        PublishingEnabled = true,
                        PublishingInterval = 250,
                        DisableMonitoredItemCache = true,
                        MinLifetimeInterval = 60_000,
                    };

                    ProcessSubject(_subject, rootNode, session, subscription, _rootName + PathDelimiter);

                    session.AddSubscription(subscription);
                    await subscription.CreateAsync(stoppingToken); // Subscribes to all added items
                }

                await Task.Delay(-1, stoppingToken); // Keep the session alive
                //
                // Console.WriteLine("Browsing nodes:");
                // foreach (var rd in references)
                // {
                //     Console.WriteLine($"{rd.DisplayName}: {rd.BrowseName}");
                // }
                //
                // // Reading a specific node
                // NodeId nodeId = new NodeId("YourVariableNodeID", 2); // Adjust as needed
                // DataValue value = session.ReadValue(nodeId);
                // Console.WriteLine($"Value of {nodeId}: {value.Value}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception: {ex.Message}");
            }
        }
    }

    private void ProcessSubject(IInterceptorSubject subject, ReferenceDescription rootNode, Session session, Subscription subscription, string prefix)
    {
        var registeredSubject = subject.TryGetRegisteredSubject();
        if (registeredSubject is not null)
        {
            foreach (var (_, property) in registeredSubject.Properties)
            {
                var propertyName = GetPropertyName(property);
                if (propertyName is not null)
                {
                    // TODO: Do the same in the server
                    var children = property.Children;
                    if (property.Type.IsAssignableTo(typeof(IInterceptorSubject)))
                    {
                        if (children.Any())
                            ProcessSubject(children.Single().Subject, rootNode, session, subscription, prefix + propertyName);
                    }
                    else if (property.Type.IsAssignableTo(typeof(IEnumerable<IInterceptorSubject>)))
                    {
                        CreateArrayObjectNode(children, rootNode, session, subscription, prefix + propertyName + PathDelimiter + propertyName);
                    }
                    else if (property.Type.IsAssignableTo(typeof(IReadOnlyDictionary<string, IInterceptorSubject>)))
                    {
                        
                    }
                    else
                    {
                        CreateVariableNode(prefix + propertyName, property, rootNode, subscription);
                    }
                }
            }
        }
    }

    private void CreateArrayObjectNode(ICollection<SubjectPropertyChild> children, ReferenceDescription rootNode, 
        Session session, Subscription subscription, string prefix)
    {
        var i = 0;
        foreach (var child in children)
        {
            ProcessSubject(child.Subject, rootNode, session, subscription, prefix + $"[{i}]" + PathDelimiter);
            i++;
        }
    }

    private void CreateVariableNode(string fullPath, RegisteredSubjectProperty property, ReferenceDescription rootNode, Subscription subscription)
    {
        // var sourcePath = property.TryGetSourcePath(_sourcePathProvider, _subject);
        var nodeId = new NodeId(fullPath, rootNode.NodeId.NamespaceIndex);
        // var node = session.ReadNode(nodeId);
        
        if (property.HasSetter)
        {
            property.Property.SetPropertyData(OpcVariableKey, nodeId);

            var monitoredItem = new MonitoredItem
            {
                StartNodeId = nodeId,
                MonitoringMode = MonitoringMode.Reporting,
                AttributeId = Opc.Ua.Attributes.Value,
                DisplayName = fullPath,
                // SamplingInterval = 1000,
                // QueueSize = 10,
                // DiscardOldest = true
            };
        
            monitoredItem.Notification += (_, args) =>
            {
                if (args.NotificationValue is MonitoredItemNotification notification)
                {
                    _dispatcher?.EnqueueSubjectUpdate(() =>
                    { 
                        _logger.LogInformation("Received notification for {Path} with value {Value}", fullPath, notification.Value.Value);
                        SubjectMutationContext.ApplyChangesWithTimestamp(notification.Value.SourceTimestamp,
                            () => property.SetValueFromSource(this, notification.Value.Value));
                    });
                }
            };
        
            _logger.LogInformation("Subscribed to {Path}", fullPath);
    
            subscription.AddItem(monitoredItem);
        }
    }

    public Task<Action?> LoadCompleteSourceStateAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult<Action?>(null);
    }
    
    private string? GetPropertyName(RegisteredSubjectProperty property)
    {
        if (property.IsAttribute)
        {
            var attributedProperty = property.GetAttributedProperty();
            var propertyName = _sourcePathProvider.TryGetPropertyName(property);
            if (propertyName is null)
                return null;
            
            return GetPropertyName(attributedProperty) + "__" + propertyName;
        }
        
        return _sourcePathProvider.TryGetPropertyName(property);
    }

    public async Task WriteToSourceAsync(IEnumerable<SubjectPropertyChange> changes, CancellationToken cancellationToken)
    {
        foreach (var change in changes)
        {
            if (_session is not null &&
                change.Property.TryGetPropertyData(OpcVariableKey, out var value) && 
                value is NodeId nodeId)
            {

                var val = change.NewValue;
                if (val?.GetType() == typeof(decimal))
                {
                    val = Convert.ToDouble(val);
                }
                
                var valueToWrite = new DataValue
                {
                    Value = val,
                    StatusCode = StatusCodes.Good,
                    //ServerTimestamp = DateTime.UtcNow,
                    SourceTimestamp = change.Timestamp.UtcDateTime
                };

                var writeValue = new WriteValue
                {
                    NodeId = nodeId,
                    AttributeId = Opc.Ua.Attributes.Value,
                    Value = valueToWrite
                };

                var writeValues = new WriteValueCollection { writeValue };
                var writeResponse = await _session.WriteAsync(null, writeValues, cancellationToken);
                
                if (writeResponse.Results.Any(StatusCode.IsBad))
                {
                    // var badVariableStatusCodes = writeResponse.Results
                    //     .Where(StatusCode.IsBad)
                    //     .ToDictionary(wr => , wr => wr.Code);
                    //
                    // _logger.LogError("Failed to write variables: {@Variables}", badVariableStatusCodes);
                    //
                    // throw new ConnectionException(_connectionOptions,
                    //     $"Unexpected Error during writing variables. Variables: {badVariableStatusCodes.Keys.Join(Environment.NewLine)}")
                    // {
                    //     Data = { ["BadVariableStatusCodeDictionary"] = badVariableStatusCodes }
                    // };
                }
            }
        }
    }
}