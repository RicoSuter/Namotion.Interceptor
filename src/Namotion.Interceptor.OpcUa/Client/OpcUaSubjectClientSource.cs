using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Sources;
using Namotion.Interceptor.Tracking.Change;
using Opc.Ua;
using Opc.Ua.Client;
using System.Collections.Concurrent;
using Namotion.Interceptor.Sources.Paths.Attributes;

namespace Namotion.Interceptor.OpcUa.Client;

internal class OpcUaSubjectClientSource : BackgroundService, ISubjectSource
{
    private const string PathDelimiter = ".";
    private const string OpcVariableKey = "OpcVariable";

    private readonly IInterceptorSubject _subject;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<uint, RegisteredSubjectProperty> _monitoredItems = new();
    private readonly List<Subscription> _activeSubscriptions = [];

    private ISubjectMutationDispatcher? _dispatcher;
    private Session? _session;

    private readonly OpcUaClientConfiguration _configuration;

    public OpcUaSubjectClientSource(
        IInterceptorSubject subject,
        OpcUaClientConfiguration configuration,
        ILogger<OpcUaSubjectClientSource> logger)
    {
        _subject = subject;
        _logger = logger;

        _configuration = configuration;
    }

    public bool IsPropertyIncluded(RegisteredSubjectProperty property)
    {
        return _configuration.SourcePathProvider.IsPropertyIncluded(property);
    }

    public Task<IDisposable?> StartListeningAsync(ISubjectMutationDispatcher dispatcher, CancellationToken cancellationToken)
    {
        _dispatcher = dispatcher;
        return Task.FromResult<IDisposable?>(null);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var application = _configuration.CreateApplicationInstance();
                await application.CheckApplicationInstanceCertificates(false);

                var endpointConfiguration = EndpointConfiguration.Create(application.ApplicationConfiguration);
                var endpointDescription = CoreClientUtils.SelectEndpoint(application.ApplicationConfiguration, _configuration.ServerUrl, false);
                var endpoint = new ConfiguredEndpoint(null, endpointDescription, endpointConfiguration);

                using var session = await Session.Create(
                    application.ApplicationConfiguration,
                    endpoint,
                    false,
                    application.ApplicationName,
                    60000,
                    new UserIdentity(),
                    null, stoppingToken);

                var cancellationTokenSource = new CancellationTokenSource();
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, cancellationTokenSource.Token);
                
                _session = session;
                _session.SessionClosing += (_, _) =>
                {
                    cancellationTokenSource.Cancel();
                };

                // Clear client-handle map on (re)connect
                _monitoredItems.Clear();
                _activeSubscriptions.Clear();

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
                    linked.Token);

                var rootNode = 
                    _configuration.RootName is not null ?
                    references
                        .SelectMany(c => c)
                        .FirstOrDefault(r => r.BrowseName.Name == _configuration.RootName) :
                    new ReferenceDescription
                    {
                        NodeId = new ExpandedNodeId(ObjectIds.ObjectsFolder),
                        BrowseName = new QualifiedName("Objects", 0)
                    };

                if (rootNode is not null)
                {
                    var monitoredItems = new List<MonitoredItem>();
                    await LoadSubjectAsync(_subject, rootNode, monitoredItems, _configuration.RootName ?? string.Empty, linked.Token);

                    if (monitoredItems.Count > 0)
                    {
                        await ReadAndApplyInitialValuesAsync(monitoredItems, session, linked.Token);
                        await CreateBatchedSubscriptionsAsync(monitoredItems, session, linked.Token);
                    }
                }

                await Task.Delay(-1, linked.Token);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to connect to OPC UA server.");
                await Task.Delay(1000, stoppingToken);
            }
            finally
            {
                // Explicitly detach callbacks to avoid holding references
                foreach (var subscription in _activeSubscriptions)
                {
                    try
                    {
                        subscription.FastDataChangeCallback -= FastDataChangeCallback;
                    }
                    catch { /* ignore cleanup exceptions */ }
                }
                _activeSubscriptions.Clear();
                _session = null;
            }
        }
    }

    private async Task CreateBatchedSubscriptionsAsync(List<MonitoredItem> monitoredItems, Session session, CancellationToken cancellationToken)
    {
        for (var i = 0; i < monitoredItems.Count; i += _configuration.MaxItemsPerSubscription)
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
                await subscription.CreateAsync(cancellationToken);
                subscription.FastDataChangeCallback += FastDataChangeCallback;
                _activeSubscriptions.Add(subscription);

                foreach (var item in monitoredItems
                    .Skip(i)
                    .Take(_configuration.MaxItemsPerSubscription))
                {
                    // Add the item to the subscription first so the SDK assigns a ClientHandle
                    subscription.AddItem(item);

                    // Map the assigned ClientHandle to our property for fast callbacks
                    if (item.Handle is RegisteredSubjectProperty p)
                    {
                        _monitoredItems[item.ClientHandle] = p;
                    }
                }

                try
                {
                    await subscription.ApplyChangesAsync(cancellationToken);
                }
                catch (ServiceResultException sre)
                {
                    _logger.LogWarning(sre, "ApplyChanges failed for a batch; attempting to keep valid monitored items by removing failed ones.");
                }

                // Remove any items that failed to create, keep the rest
                var removed = await FilterOutFailedMonitoredItemsAsync(subscription, cancellationToken);
                if (removed > 0)
                {
                    _logger.LogWarning("Removed {Removed} monitored items that failed to create in subscription {Id}.", removed, subscription.Id);
                }
            }
            else
            {
                throw new InvalidOperationException("Failed to add subscription.");
            }
        }
    }

    private async Task<int> FilterOutFailedMonitoredItemsAsync(Subscription subscription, CancellationToken cancellationToken)
    {
        var toRemove = new List<MonitoredItem>();
        foreach (var mi in subscription.MonitoredItems.ToList())
        {
            var statusCode = mi.Status?.Error?.StatusCode ?? StatusCodes.Good;
            var failed = !mi.Created || StatusCode.IsBad(statusCode);
            if (failed)
            {
                toRemove.Add(mi);
                _monitoredItems.TryRemove(mi.ClientHandle, out _);
                _logger.LogError("Monitored item creation failed for {DisplayName} (Handle={Handle}): {Status}", mi.DisplayName, mi.ClientHandle, statusCode);
            }
        }

        if (toRemove.Count > 0)
        {
            foreach (var mi in toRemove)
            {
                subscription.RemoveItem(mi);
            }

            try
            {
                await subscription.ApplyChangesAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ApplyChanges after removing failed items still failed. Continuing with remaining items.");
            }
        }

        return toRemove.Count;
    }

    private async Task ReadAndApplyInitialValuesAsync(List<MonitoredItem> monitoredItems, Session session, CancellationToken cancellationToken)
    {
        var readValues = monitoredItems
            .Select(item => new ReadValueId
            {
                NodeId = item.StartNodeId,
                AttributeId = Opc.Ua.Attributes.Value
            })
            .ToArray();

        try
        {
            var readResponse = await session.ReadAsync(null, 0, TimestampsToReturn.Source, readValues, cancellationToken);

            for (int idx = 0; idx < readResponse.Results.Count && idx < monitoredItems.Count; idx++)
            {
                if (StatusCode.IsGood(readResponse.Results[idx].StatusCode))
                {
                    var dataValue = readResponse.Results[idx];
                    var monitoredItem = monitoredItems[idx];

                    if (monitoredItem.Handle is RegisteredSubjectProperty property)
                    {
                        var value = _configuration.ValueConverter.ConvertToPropertyValue(dataValue.Value, property.Type);
                        property.SetValueFromSource(this, dataValue.SourceTimestamp, value);
                    }
                }
            }

            _logger.LogInformation("Successfully read initial values for {Count} monitored items", monitoredItems.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read initial values for monitored items");
        }
    }

    private void FastDataChangeCallback(Subscription subscription, DataChangeNotification notification, IList<string> stringtable)
    {
        var changes = new List<SubjectPropertyChange>();
        foreach (var i in notification.MonitoredItems)
        {
            if (_monitoredItems.TryGetValue(i.ClientHandle, out var property))
            {
                changes.Add(new SubjectPropertyChange
                {
                    Property = property,
                    NewValue = _configuration.ValueConverter.ConvertToPropertyValue(i.Value.Value, property.Type),
                    OldValue = null,
                    Timestamp = i.Value.SourceTimestamp
                });
            }
        }
        
        if (changes.Count == 0)
        {
            return;
        }
        
        _dispatcher?.EnqueueSubjectUpdate(() =>
        {
            foreach (var change in changes)
            {
                try
                {
                    change.Property.SetValueFromSource(this, change.Timestamp, change.NewValue);
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Failed to apply change for {Path}.", change.Property.Name);
                }
            }
        });
    }

    private async Task LoadSubjectAsync(IInterceptorSubject subject, ReferenceDescription node, List<MonitoredItem> monitoredItems, string prefix, CancellationToken cancellationToken)
    {
        var registeredSubject = subject.TryGetRegisteredSubject();
        if (registeredSubject is not null && _session is not null)
        {
            var nodeId = ExpandedNodeId.ToNodeId(node.NodeId, _session.NamespaceUris);
            var (_, _ , nodeProperties, _) = await _session.BrowseAsync(
                null,
                null,
                [nodeId],
                0u,
                BrowseDirection.Forward,
                ReferenceTypeIds.HierarchicalReferences,
                true,
                (uint)NodeClass.Variable | (uint)NodeClass.Object | (uint)NodeClass.Method,
                cancellationToken);
            
            foreach (var nodeRef in nodeProperties.SelectMany(p => p))
            {
                var property = _configuration.SourcePathProvider.TryGetPropertyFromSegment(registeredSubject, nodeRef.BrowseName.Name);
                if (property is null)
                {
                    if (!_configuration.AddDynamicProperties)
                        continue;
                    
                    // Infer CLR type from OPC UA variable metadata if possible
                    var inferredType = await _configuration.TypeResolver.GetTypeForNodeAsync(_session, nodeRef, cancellationToken);
                    if (inferredType == typeof(object))
                        continue;

                    object? value = null;
                    property = registeredSubject.AddProperty(
                        nodeRef.BrowseName.Name,
                        inferredType,
                        _ => value,
                        (_, o) => value = o,
                        new SourcePathAttribute("opc", nodeRef.BrowseName.Name));
                }
                
                var propertyName = GetPropertyName(property);
                if (propertyName is not null)
                {
                    // TODO: Do the same in the server
                    if (property.IsSubjectReference)
                    {
                        var collectionPath = CombinePath(prefix, propertyName);

                        var children = property.Children;
                        if (children.Count != 0)
                        {
                            await LoadSubjectAsync(children.Single().Subject, node, monitoredItems, collectionPath, cancellationToken);
                        }
                        else
                        {
                            var newSubject = DefaultSubjectFactory.Instance.CreateSubject(property, null);
                            newSubject.Context.AddFallbackContext(subject.Context);
                            await LoadSubjectAsync(newSubject, nodeRef, monitoredItems, collectionPath, cancellationToken);
                            property.SetValueFromSource(this, null, newSubject);
                        }
                    }
                    else if (property.IsSubjectCollection)
                    {
                        // TODO: Reuse property.Children?

                        var childNodeId = ExpandedNodeId.ToNodeId(nodeRef.NodeId, _session.NamespaceUris);
                        var (_, _ , childNodeProperties, _) = await _session.BrowseAsync(
                            null,
                            null,
                            [childNodeId],
                            0u,
                            BrowseDirection.Forward,
                            ReferenceTypeIds.HierarchicalReferences,
                            true,
                            (uint)NodeClass.Variable | (uint)NodeClass.Object,
                            cancellationToken);
                        
                        var childSubjectList = childNodeProperties
                            .SelectMany(p => p)
                            .Select(p => new
                            {
                                Node = p, // TODO: Use ISubjectFactory to create the subject
                                Subject = (IInterceptorSubject)Activator.CreateInstance(
                                    property.Type.IsArray ? property.Type.GetElementType()! : property.Type.GenericTypeArguments[0])!
                            })
                            .ToList();

                        var collection = DefaultSubjectFactory.Instance.CreateSubjectCollection(property, childSubjectList.Select(p => p.Subject));
                        property.SetValue(collection);

                        var pathIndex = 0;
                        foreach (var child in childSubjectList)
                        {
                            var fullPath = CombinePath(prefix, propertyName) + $"[{pathIndex}]";
                            await LoadSubjectAsync(child.Subject, child.Node, monitoredItems, fullPath, cancellationToken);
                            pathIndex++;
                        }
                    }
                    else if (property.IsSubjectDictionary)
                    {
                        // TODO: Implement dictionary support
                    }
                    else
                    {
                        MonitorValueNode(CombinePath(prefix, propertyName), property, node, monitoredItems);
                    }
                }
            }
        }
    }

    private static string CombinePath(string prefix, string segment)
    {
        return string.IsNullOrEmpty(prefix) ? segment : prefix + PathDelimiter + segment;
    }

    private void MonitorValueNode(string fullPath, RegisteredSubjectProperty property, ReferenceDescription node, List<MonitoredItem> monitoredItems)
    {
        // Monitor reads for all properties; write capability is enforced on server side via AccessLevel
        var nodeId = new NodeId(fullPath, node.NodeId.NamespaceIndex);
        var monitoredItem = new MonitoredItem
        {
            StartNodeId = nodeId,
            MonitoringMode = MonitoringMode.Reporting,
            AttributeId = Opc.Ua.Attributes.Value,
            DisplayName = fullPath,
            SamplingInterval = 0,

            // Delay ClientHandle mapping until after the item is added to a subscription.
            // Store the property on the item itself for later reference.
            Handle = property

            // QueueSize = 10, // TODO: Set to a reasonable value
            // DiscardOldest = true
        };

        property.Reference.SetPropertyData(OpcVariableKey, nodeId);
        monitoredItems.Add(monitoredItem);

        _logger.LogInformation("Prepared monitoring for '{Path}'", nodeId);
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
            var propertyName = _configuration.SourcePathProvider.TryGetPropertySegment(property);
            if (propertyName is null)
                return null;
            
            // TODO: Create property reference node instead of __?
            return GetPropertyName(attributedProperty) + "__" + propertyName;
        }
        
        return _configuration.SourcePathProvider.TryGetPropertySegment(property);
    }

    public async Task WriteToSourceAsync(IEnumerable<SubjectPropertyChange> changes, CancellationToken cancellationToken)
    {
        if (_session is null)
            return;

        foreach (var change in changes)
        {
            if (change.Property.TryGetPropertyData(OpcVariableKey, out var v) && v is NodeId nodeId)
            {
                var registeredProperty = change.Property.GetRegisteredProperty();
                var (value, _) = _configuration.ValueConverter.ConvertToNodeValue(change.NewValue, registeredProperty.Type);
                var writeValue = new WriteValue
                {
                    NodeId = nodeId,
                    AttributeId = Opc.Ua.Attributes.Value,
                    Value = new DataValue
                    {
                        Value = value,
                        StatusCode = StatusCodes.Good,
                        //ServerTimestamp = DateTime.UtcNow,
                        SourceTimestamp = change.Timestamp.UtcDateTime
                    }
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