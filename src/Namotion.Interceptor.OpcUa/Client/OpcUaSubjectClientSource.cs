using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Sources;
using Namotion.Interceptor.Tracking.Change;
using Opc.Ua;
using Opc.Ua.Client;
using System.Collections.Concurrent;
using Namotion.Interceptor.OpcUa.Attributes;

namespace Namotion.Interceptor.OpcUa.Client;

internal class OpcUaSubjectClientSource : BackgroundService, ISubjectSource
{
    private const string OpcVariableKey = "OpcVariable";

    private const uint NodeClassMask = (uint)NodeClass.Variable | (uint)NodeClass.Object;

    private readonly IInterceptorSubject _subject;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<uint, RegisteredSubjectProperty> _monitoredItems = new();
    private readonly List<Subscription> _activeSubscriptions = [];
    private readonly List<PropertyReference> _propertiesWithOpcData = [];

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
                _logger.LogInformation("Connecting to OPC UA server at {ServerUrl}...", _configuration.ServerUrl);
                
                var application = _configuration.CreateApplicationInstance();
                await application.CheckApplicationInstanceCertificates(false);

                var endpointConfiguration = EndpointConfiguration.Create(application.ApplicationConfiguration);
                var endpointDescription = CoreClientUtils.SelectEndpoint(application.ApplicationConfiguration, _configuration.ServerUrl, false);
                var endpoint = new ConfiguredEndpoint(null, endpointDescription, endpointConfiguration);

                _session = await Session.Create(
                    application.ApplicationConfiguration,
                    endpoint,
                    false,
                    application.ApplicationName,
                    60000,
                    new UserIdentity(),
                    null, stoppingToken);

                var cancellationTokenSource = new CancellationTokenSource();
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, cancellationTokenSource.Token);
                
                _session.KeepAlive += (_, e) =>
                {
                    if (!ServiceResult.IsGood(e.Status))
                    {
                        _logger.LogWarning("KeepAlive failed with status: {Status}. Connection may be lost.", e.Status);
                        if (e.CurrentState == ServerState.Unknown || e.CurrentState == ServerState.Failed)
                        {
                            _logger.LogError("Server connection lost. Triggering reconnect...");
                            cancellationTokenSource.Cancel();
                        }
                    }
                };
                
                _session.SessionClosing += (_, _) =>
                {
                    _logger.LogWarning("Session closing event received. Triggering reconnect...");
                    cancellationTokenSource.Cancel();
                };

                _monitoredItems.Clear();
                _activeSubscriptions.Clear();

                _logger.LogInformation("Connected to OPC UA server successfully.");

                var rootNode = await TryGetRootNodeAsync(linked.Token);
                if (rootNode is not null)
                {
                    var monitoredItems = new List<MonitoredItem>();
                    await LoadSubjectAsync(_subject, rootNode, monitoredItems, linked.Token);

                    if (monitoredItems.Count > 0)
                    {
                        await ReadAndApplyInitialValuesAsync(monitoredItems, _session, linked.Token);
                        await CreateBatchedSubscriptionsAsync(monitoredItems, _session, linked.Token);
                    }
                    
                    _logger.LogInformation("OPC UA client initialization complete. Monitoring {Count} items.", monitoredItems.Count);
                }

                await Task.Delay(Timeout.Infinite, linked.Token);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("OPC UA client is stopping.");
                break;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("OPC UA session was cancelled. Will attempt to reconnect.");
            }
            catch (ServiceResultException ex)
            {
                _logger.LogError(ex, "OPC UA service error: {Message}. Will attempt to reconnect.", ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to connect to OPC UA server: {Message}. Will attempt to reconnect.", ex.Message);
            }
            finally
            {
                CleanupSubscriptions();
                CleanUpProperties();

                var session = _session;
                if (session is not null)
                {
                    _session = null;
                    try
                    {
                        try
                        {
                            await session.CloseAsync(stoppingToken);
                        }
                        finally
                        {
                            session.Dispose();
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error while disposing session.");
                    }
                }
            }

            if (!stoppingToken.IsCancellationRequested)
            {
                var reconnectDelay = _configuration.ReconnectDelay;
                _logger.LogInformation("Waiting {Delay} before reconnecting...", reconnectDelay);
                await Task.Delay(reconnectDelay, stoppingToken);
            }
        }
        
        _logger.LogInformation("OPC UA client has stopped.");
    }

    private async Task<ReferenceDescription?> TryGetRootNodeAsync(CancellationToken cancellationToken)
    {
        if (_configuration.RootName is not null)
        {
            foreach (var reference in await BrowseNodeAsync(ObjectIds.ObjectsFolder, cancellationToken))
            {
                if (reference.BrowseName.Name == _configuration.RootName)
                {
                    return reference;
                }
            }

            return null;
        }

        return new ReferenceDescription
        {
            NodeId = new ExpandedNodeId(ObjectIds.ObjectsFolder),
            BrowseName = new QualifiedName("Objects", 0)
        };
    }

    private void CleanupSubscriptions()
    {
        foreach (var subscription in _activeSubscriptions)
        {
            try
            {
                subscription.FastDataChangeCallback -= FastDataChangeCallback;
                subscription.Delete(true);
            }
            catch { /* ignore cleanup exceptions */ }
        }
        _activeSubscriptions.Clear();
    }

    private void CleanUpProperties()
    {
        foreach (var property in _propertiesWithOpcData)
        {
            try
            {
                property.SetPropertyData(OpcVariableKey, null);
            }
            catch { /* ignore cleanup exceptions */ }
        }

        _propertiesWithOpcData.Clear();
    }

    private async Task CreateBatchedSubscriptionsAsync(List<MonitoredItem> monitoredItems, Session session, CancellationToken cancellationToken)
    {
        var itemCount = monitoredItems.Count;
        var maximumItemsPerSubscription = _configuration.MaximumItemsPerSubscription;
        for (var i = 0; i < itemCount; i += maximumItemsPerSubscription)
        {
            var subscription = new Subscription(session.DefaultSubscription)
            {
                PublishingEnabled = true,
                PublishingInterval = 0, // TODO: Set to a reasonable value
                DisableMonitoredItemCache = true,
                MinLifetimeInterval = 60_000,
            };

            if (!session.AddSubscription(subscription))
            {
                throw new InvalidOperationException("Failed to add subscription.");
            }

            await subscription.CreateAsync(cancellationToken);
            subscription.FastDataChangeCallback += FastDataChangeCallback;
            _activeSubscriptions.Add(subscription);

            var batchEnd = Math.Min(i + maximumItemsPerSubscription, itemCount);
            for (var j = i; j < batchEnd; j++)
            {
                var item = monitoredItems[j];
                subscription.AddItem(item);

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

            var removed = await FilterOutFailedMonitoredItemsAsync(subscription, cancellationToken);
            if (removed > 0)
            {
                _logger.LogWarning("Removed {Removed} monitored items that failed to create in subscription {Id}.", removed, subscription.Id);
            }
            
            _logger.LogInformation("Created subscription {SubscriptionId} with {Count} monitored items.", subscription.Id, subscription.MonitoredItems.Count());
        }
    }

    private async Task<int> FilterOutFailedMonitoredItemsAsync(Subscription subscription, CancellationToken cancellationToken)
    {
        List<MonitoredItem>? itemsToRemove = null;
        
        foreach (var monitoredItem in subscription.MonitoredItems)
        {
            var statusCode = monitoredItem.Status?.Error?.StatusCode ?? StatusCodes.Good;
            var hasFailed = !monitoredItem.Created || StatusCode.IsBad(statusCode);
            if (hasFailed)
            {
                itemsToRemove ??= [];
                itemsToRemove.Add(monitoredItem);

                _monitoredItems.TryRemove(monitoredItem.ClientHandle, out _);

                _logger.LogError("Monitored item creation failed for {DisplayName} (Handle={Handle}): {Status}", 
                    monitoredItem.DisplayName, monitoredItem.ClientHandle, statusCode);
            }
        }

        if (itemsToRemove?.Count > 0)
        {
            foreach (var monitoredItem in itemsToRemove)
            {
                subscription.RemoveItem(monitoredItem);
            }

            try
            {
                await subscription.ApplyChangesAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ApplyChanges after removing failed items still failed. Continuing with remaining items.");
            }

            return itemsToRemove.Count;
        }

        return 0;
    }

    private async Task ReadAndApplyInitialValuesAsync(List<MonitoredItem> monitoredItems, Session session, CancellationToken cancellationToken)
    {
        var itemCount = monitoredItems.Count;
        var readValues = new ReadValueId[itemCount];
        
        for (var i = 0; i < itemCount; i++)
        {
            readValues[i] = new ReadValueId
            {
                NodeId = monitoredItems[i].StartNodeId,
                AttributeId = Opc.Ua.Attributes.Value
            };
        }

        try
        {
            var readResponse = await session.ReadAsync(null, 0, TimestampsToReturn.Source, readValues, cancellationToken);
            var resultCount = Math.Min(readResponse.Results.Count, itemCount);

            for (var i = 0; i < resultCount; i++)
            {
                if (StatusCode.IsGood(readResponse.Results[i].StatusCode))
                {
                    var dataValue = readResponse.Results[i];
                    var monitoredItem = monitoredItems[i];
                    if (monitoredItem.Handle is RegisteredSubjectProperty property)
                    {
                        var value = _configuration.ValueConverter.ConvertToPropertyValue(dataValue.Value, property.Type);
                        property.SetValueFromSource(this, dataValue.SourceTimestamp, value);
                    }
                }
            }

            _logger.LogInformation("Successfully read initial values of {Count} nodes.", itemCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read initial values for monitored items.");
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

    private async Task LoadSubjectAsync(IInterceptorSubject subject, ReferenceDescription node, List<MonitoredItem> monitoredItems, CancellationToken cancellationToken)
    {
        var registeredSubject = subject.TryGetRegisteredSubject();
        if (registeredSubject is null || _session is null)
        {
            return;
        }

        var nodeId = ExpandedNodeId.ToNodeId(node.NodeId, _session.NamespaceUris);
        foreach (var nodeRef in await BrowseNodeAsync(nodeId, cancellationToken))
        {
            var property = FindSubjectProperty(registeredSubject, nodeRef);
            if (property is null)
            {
                if (registeredSubject.Properties.Any(p => p.Name == nodeRef.BrowseName.Name))
                {
                    continue;
                }

                var addAsDynamic = _configuration.ShouldAddDynamicProperties is not null &&
                    await _configuration.ShouldAddDynamicProperties(nodeRef, cancellationToken);

                if (!addAsDynamic)
                {
                    continue;
                }

                // Infer CLR type from OPC UA variable metadata if possible
                var inferredType = await _configuration.TypeResolver.GetTypeForNodeAsync(_session, nodeRef, cancellationToken);
                if (inferredType == typeof(object))
                {
                    continue;
                }

                object? value = null;
                property = registeredSubject.AddProperty(
                    nodeRef.BrowseName.Name,
                    inferredType,
                    _ => value,
                    (_, o) => value = o,
                    new OpcUaNodeAttribute(
                        nodeRef.BrowseName.Name, 
                        _session.NamespaceUris.GetString(nodeRef.BrowseName.NamespaceIndex),
                        sourceName: null) // TODO: Allow custom source name (config, from source provider)?
                    {
                        NodeIdentifier = nodeRef.NodeId.Identifier.ToString(),
                        NodeNamespaceUri = nodeRef.NodeId.NamespaceUri
                    });
            }

            var propertyName = property.ResolvePropertyName(_configuration.SourcePathProvider);
            if (propertyName is not null)
            {
                var childNodeId = ExpandedNodeId.ToNodeId(nodeRef.NodeId, _session.NamespaceUris);
                
                if (property.IsSubjectReference)
                {
                    await LoadSubjectReferenceAsync(property, node, nodeRef, subject, monitoredItems, cancellationToken);
                }
                else if (property.IsSubjectCollection)
                {
                    await LoadSubjectCollectionAsync(property, childNodeId, monitoredItems, cancellationToken);
                }
                else if (property.IsSubjectDictionary)
                {
                    // TODO: Implement dictionary support
                }
                else
                {
                    MonitorValueNode(childNodeId, property, monitoredItems);
                }
            }
        }
    }

    private async Task LoadSubjectReferenceAsync(
        RegisteredSubjectProperty property, 
        ReferenceDescription node, 
        ReferenceDescription nodeRef,
        IInterceptorSubject subject,
        List<MonitoredItem> monitoredItems, 
        CancellationToken cancellationToken)
    {
        var existingSubject = property.Children.SingleOrDefault();
        if (existingSubject.Subject is not null)
        {
            await LoadSubjectAsync(existingSubject.Subject, node, monitoredItems, cancellationToken);
        }
        else
        {
            // Create new subject instance
            var newSubject = await _configuration.SubjectFactory.CreateSubjectAsync(property, nodeRef, _session!, cancellationToken);
            newSubject.Context.AddFallbackContext(subject.Context);
            await LoadSubjectAsync(newSubject, nodeRef, monitoredItems, cancellationToken);
            property.SetValueFromSource(this, null, newSubject);
        }
    }

    private async Task LoadSubjectCollectionAsync(
        RegisteredSubjectProperty property,
        NodeId childNodeId,
        List<MonitoredItem> monitoredItems,
        CancellationToken cancellationToken)
    {
        var childNodes = await BrowseNodeAsync(childNodeId, cancellationToken);

        var children = new List<(ReferenceDescription Node, IInterceptorSubject Subject)>(childNodes.Count);
        for (var i = 0; i < childNodes.Count; i++)
        {
            var childNode = childNodes[i];

            var childSubject = property.Children.Count > i ? property.Children.ElementAt(i).Subject : null;
            childSubject ??= DefaultSubjectFactory.Instance.CreateCollectionSubject(property, i);
            
            children.Add((childNode, childSubject));
        }

        var collection = DefaultSubjectFactory.Instance
            .CreateSubjectCollection(property.Type, children.Select(c => c.Subject));

        property.SetValue(collection);

        foreach (var child in children)
        {
            await LoadSubjectAsync(child.Subject, child.Node, monitoredItems, cancellationToken);
        }
    }

    private RegisteredSubjectProperty? FindSubjectProperty(RegisteredSubject registeredSubject, ReferenceDescription nodeRef)
    {
        var nodeId = nodeRef.NodeId.Identifier.ToString();
        var nodeNamespaceUri = _session!.NamespaceUris.GetString(nodeRef.NodeId.NamespaceIndex);
        foreach (var property in registeredSubject.Properties)
        {
            var opcUaNodeAttribute = property.TryGetOpcUaNodeAttribute();
            if (opcUaNodeAttribute is not null && opcUaNodeAttribute.NodeIdentifier == nodeId)
            {
                var propertyNodeNamespaceUri = opcUaNodeAttribute.NodeNamespaceUri 
                   ?? _configuration.DefaultNamespaceUri
                   ?? throw new InvalidOperationException("No default namespace URI configured.");

                if (propertyNodeNamespaceUri == nodeNamespaceUri)
                {
                    return property;
                }
            }
        }

        return _configuration.SourcePathProvider.TryGetPropertyFromSegment(registeredSubject, nodeRef.BrowseName.Name);
    }

    private void MonitorValueNode(NodeId nodeId, RegisteredSubjectProperty property, List<MonitoredItem> monitoredItems)
    {
        var monitoredItem = new MonitoredItem
        {
            StartNodeId = nodeId,
            MonitoringMode = MonitoringMode.Reporting,
            AttributeId = Opc.Ua.Attributes.Value,
            // DisplayName = nodeId.Identifier.ToString(),
            SamplingInterval = 0,

            // Delay ClientHandle mapping until after the item is added to a subscription.
            // Store the property on the item itself for later reference.
            Handle = property

            // QueueSize = 10, // TODO: Set to a reasonable value
            // DiscardOldest = true
        };

        property.Reference.SetPropertyData(OpcVariableKey, nodeId);
        _propertiesWithOpcData.Add(property.Reference);
        monitoredItems.Add(monitoredItem);

        _logger.LogInformation("Prepared monitoring for '{Path}'", nodeId);
    }

    public Task<Action?> LoadCompleteSourceStateAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult<Action?>(null);
    }

    public async Task WriteToSourceAsync(IEnumerable<SubjectPropertyChange> changes, CancellationToken cancellationToken)
    {
        if (_session is null)
        {
            // TODO: What to do here? Store and write with the next write?
            return;
        }

        var writeValues = new WriteValueCollection();
        foreach (var change in changes)
        {
            if (change.Property.TryGetPropertyData(OpcVariableKey, out var v) && v is NodeId nodeId)
            {
                var registeredProperty = change.Property.GetRegisteredProperty();
                if (registeredProperty.HasSetter)
                {
                    var value = _configuration.ValueConverter.ConvertToNodeValue(change.NewValue, registeredProperty.Type);
                    writeValues.Add(new WriteValue
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
                    });
                }
            }
        }
        
        if (writeValues.Count == 0)
        {
            return;
        }
        
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

    private async Task<ReferenceDescriptionCollection> BrowseNodeAsync(
        NodeId nodeId, 
        CancellationToken cancellationToken)
    {
        var (_, _, nodeProperties, _) = await _session!.BrowseAsync(
            null,
            null,
            [nodeId],
            0u,
            BrowseDirection.Forward,
            ReferenceTypeIds.HierarchicalReferences,
            true,
            NodeClassMask,
            cancellationToken);

        return nodeProperties[0];
    }
}
