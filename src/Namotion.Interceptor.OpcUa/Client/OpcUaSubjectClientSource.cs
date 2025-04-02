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
    private const string OpcVariableKey = "OpcVariable";

    private readonly TSubject _subject;
    private readonly string _serverUrl;
    private readonly ISourcePathProvider _sourcePathProvider;
    private readonly ILogger _logger;
    private readonly string? _rootName;
    private readonly Dictionary<uint, RegisteredSubjectProperty> _monitoredItems = new();

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
            await using var stream = typeof(OpcUaSubjectServerSourceExtensions).Assembly
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

                var endpointConfiguration = EndpointConfiguration.Create(application.ApplicationConfiguration);
                var endpointDescription = CoreClientUtils.SelectEndpoint(application.ApplicationConfiguration, _serverUrl, false);
                var endpoint = new ConfiguredEndpoint(null, endpointDescription, endpointConfiguration);

                var cancellationTokenSource = new CancellationTokenSource();
                using var session = await Session.Create(
                    application.ApplicationConfiguration,
                    endpoint,
                    false,
                    "MyOpcUaClient",
                    60000,
                    new UserIdentity(), // Use anonymous authentication; adjust if needed
                    null, stoppingToken);
                
                _session = session;
                _session.SessionClosing += (sender, args) =>
                {
                    cancellationTokenSource.Cancel();
                };

                // Browse the Root folder
                var (_, _, references, _) = await session.BrowseAsync(
                    null,
                    null,
                    [ObjectIds.ObjectsFolder],
                    0u,
                    BrowseDirection.Forward,
                    ReferenceTypeIds.HierarchicalReferences,
                    true,
                    (uint)NodeClass.Variable | (uint)NodeClass.Object | (uint)NodeClass.Method,
                    stoppingToken);

                var rootNode = references.SelectMany(c => c).FirstOrDefault(r => r.BrowseName == _rootName);
                if (rootNode is not null)
                {
                    var subscription = new Subscription(session.DefaultSubscription)
                    {
                        PublishingEnabled = true,
                        PublishingInterval = 0, // TODO: Set to a reasonable value
                        DisableMonitoredItemCache = true,
                        MinLifetimeInterval = 60_000,
                    };

                    if (session.AddSubscription(subscription))
                    {
                        await subscription.CreateAsync(stoppingToken);
                        subscription.FastDataChangeCallback += FastDataChangeCallback;

                        await LoadSubjectAsync(_subject, rootNode, subscription, _rootName ?? string.Empty, stoppingToken);
                        await subscription.ApplyChangesAsync(stoppingToken);
                    }
                    else
                    {
                        throw new InvalidOperationException("Failed to add subscription.");
                    }
                }

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, cancellationTokenSource.Token);
                await Task.Delay(-1, linked.Token);
                _session = null;
            }
            catch (Exception ex)
            {
                _session = null;
                Console.WriteLine($"Exception: {ex.Message}");
                await Task.Delay(1000, stoppingToken);
            }
        }
    }

    private void FastDataChangeCallback(Subscription subscription, DataChangeNotification notification, IList<string> stringtable)
    {
        var changes = notification
            .MonitoredItems
            .Select(i =>
            {
                var property = _monitoredItems[i.ClientHandle];
                
                var value = i.Value.Value;
                if (typeof(decimal).IsAssignableTo(property.Type))
                {
                    value = Convert.ToDecimal(value);
                }
                
                return new SubjectPropertyChange
                {
                    Property = property,
                    NewValue = value,
                    OldValue = null,
                    Timestamp = i.Value.SourceTimestamp
                };
            })
            .ToList();
        
        _dispatcher?.EnqueueSubjectUpdate(() =>
        {
            foreach (var change in changes)
            {
                _logger.LogInformation("Received notification for {Path} with value {Value}.", change.Property.Name, change.NewValue);

                try
                {
                    SubjectMutationContext.ApplyChangesWithTimestamp(change.Timestamp,
                        () => change.Property.SetValueFromSource(this, change.NewValue));
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Failed to apply changes for {Path}.", change.Property.Name);
                }
            }
        });
    }

    private async Task LoadSubjectAsync(IInterceptorSubject subject, ReferenceDescription node, Subscription subscription, string prefix, CancellationToken cancellationToken)
    {
        var registeredSubject = subject.TryGetRegisteredSubject();
        if (registeredSubject is not null && _session is not null)
        {
            foreach (var (_, property) in registeredSubject.Properties)
            {
                var propertyName = GetPropertyName(property);
                if (propertyName is not null)
                {
                    // TODO: Do the same in the server
                    if (property.IsSubjectReference)
                    {
                        var collectionPath = prefix + PathDelimiter + propertyName;
                        var children = property.Children;
                        if (children.Any())
                        {
                            await LoadSubjectAsync(children.Single().Subject, node, subscription, collectionPath, cancellationToken);
                        }
                        else
                        {
                            var (_, _ , nodeProperties, _) = await _session.BrowseAsync(
                                null,
                                null,
                                [new NodeId(node.NodeId.Identifier, node.NodeId.NamespaceIndex)],
                                0u,
                                BrowseDirection.Forward,
                                ReferenceTypeIds.HierarchicalReferences,
                                true,
                                (uint)NodeClass.Variable | (uint)NodeClass.Object | (uint)NodeClass.Method, 
                                cancellationToken);
                            
                            var nodeProperty = nodeProperties
                                .SelectMany(p => p)
                                .SingleOrDefault(p => 
                                    (string)p.NodeId.Identifier == collectionPath && p.NodeId.NamespaceIndex == node.NodeId.NamespaceIndex);

                            if (nodeProperty is not null)
                            {
                                var newSubject = DefaultSubjectFactory.Instance.CreateSubject(property, null);
                                newSubject.Context.AddFallbackContext(subject.Context);
                                await LoadSubjectAsync(newSubject, nodeProperty, subscription, collectionPath, cancellationToken);
                                property.SetValueFromSource(this, newSubject);
                            }
                        }
                    }
                    else if (property.IsSubjectCollection)
                    {
                        await LoadSubjectCollectionAsync(property.Children, node, subscription, 
                            prefix + propertyName + PathDelimiter + propertyName, cancellationToken);
                    }
                    else if (property.IsSubjectDictionary)
                    {
                        // TODO: Implement dictionary support
                    }
                    else
                    {
                        MonitorValueNode(prefix + PathDelimiter + propertyName, property, node, subscription);
                    }
                }
            }
        }
    }

    private async Task LoadSubjectCollectionAsync(ICollection<SubjectPropertyChild> children, ReferenceDescription collectionNode, 
        Subscription subscription, string fullPath, CancellationToken cancellationToken)
    {
        var i = 0;
        foreach (var child in children)
        {
            await LoadSubjectAsync(child.Subject, collectionNode, subscription, fullPath + $"[{i}]" + PathDelimiter, cancellationToken);
            i++;
        }
    }
    
    private void MonitorValueNode(string fullPath, RegisteredSubjectProperty property, ReferenceDescription node, Subscription subscription)
    {
        if (property.HasSetter)
        {
            var nodeId = new NodeId(fullPath, node.NodeId.NamespaceIndex);
            var monitoredItem = new MonitoredItem
            {
                StartNodeId = nodeId,
                MonitoringMode = MonitoringMode.Reporting,
                AttributeId = Opc.Ua.Attributes.Value,
                DisplayName = fullPath,
                SamplingInterval = 0, // TODO: Set to a reasonable value
                // QueueSize = 10,
                // DiscardOldest = true
            };
            
            _monitoredItems[monitoredItem.ClientHandle] = property;
            
            property.Property.SetPropertyData(OpcVariableKey, nodeId);
            subscription.AddItem(monitoredItem);

            _logger.LogInformation("Subscribed to '{Path}'", nodeId);
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
        if (_session is null)
            return;
            
        foreach (var change in changes)
        {
            if (change.Property.TryGetPropertyData(OpcVariableKey, out var value) && 
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
                    _logger.LogError("Failed to write some variables.");

                    // var badVariableStatusCodes = writeResponse.Results
                    //     .Where(StatusCode.IsBad)
                    //     .ToDictionary(wr => , wr => wr.Code);
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