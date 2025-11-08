using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Sources;
using Namotion.Interceptor.Tracking.Change;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Client;

internal class OpcUaSubjectClientSource : BackgroundService, ISubjectSource
{
    private const string OpcVariableKey = "OpcVariable";
    private const int DefaultChunkSize = 512;

    private readonly IInterceptorSubject _subject;
    private readonly ILogger _logger;
    private readonly List<PropertyReference> _propertiesWithOpcData = [];

    private Session? _session;

    private readonly OpcUaClientConfiguration _configuration;
    private readonly OpcUaSubjectLoader _subjectLoader;
    private readonly OpcUaSubscriptionManager _subscriptionManager;

    public OpcUaSubjectClientSource(
        IInterceptorSubject subject,
        OpcUaClientConfiguration configuration,
        ILogger<OpcUaSubjectClientSource> logger)
    {
        _subject = subject;
        _logger = logger;
        _configuration = configuration;
        _subjectLoader = new OpcUaSubjectLoader(configuration, _propertiesWithOpcData, this, logger);
        _subscriptionManager = new OpcUaSubscriptionManager(configuration, logger);
    }

    public bool IsPropertyIncluded(RegisteredSubjectProperty property)
    {
        return _configuration.SourcePathProvider.IsPropertyIncluded(property);
    }

    public Task<IDisposable?> StartListeningAsync(ISubjectUpdater updater, CancellationToken cancellationToken)
    {
        _subscriptionManager.SetUpdater(updater);
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

                _subscriptionManager.Clear();
                _propertiesWithOpcData.Clear();

                _logger.LogInformation("Connected to OPC UA server successfully.");

                var rootNode = await TryGetRootNodeAsync(linked.Token);
                if (rootNode is not null)
                {
                    var monitoredItems = await _subjectLoader.LoadSubjectAsync(_subject, rootNode, _session, linked.Token);
                    if (monitoredItems.Count > 0)
                    {
                        await ReadAndApplyInitialValuesAsync(monitoredItems, linked.Token);
                        await _subscriptionManager.CreateBatchedSubscriptionsAsync(monitoredItems, _session, linked.Token);
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
                _subscriptionManager.Cleanup();
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

    private async Task ReadAndApplyInitialValuesAsync(IReadOnlyList<MonitoredItem> monitoredItems, CancellationToken cancellationToken)
    {
        var itemCount = monitoredItems.Count;
        if (itemCount == 0 || _session is null)
        {
            return;
        }

        try
        {
            var result = new Dictionary<RegisteredSubjectProperty, DataValue>();

            var chunkSize = (int)(_session.OperationLimits?.MaxNodesPerRead ?? DefaultChunkSize);
            chunkSize = chunkSize == 0 ? int.MaxValue : chunkSize;

            for (var offset = 0; offset < itemCount; offset += chunkSize)
            {
                var take = Math.Min(chunkSize, itemCount - offset);
                var readValues = new ReadValueIdCollection(take);
                for (var i = 0; i < take; i++)
                {
                    readValues.Add(new ReadValueId
                    {
                        NodeId = monitoredItems[offset + i].StartNodeId,
                        AttributeId = Opc.Ua.Attributes.Value
                    });
                }

                var readResponse = await _session.ReadAsync(null, 0, TimestampsToReturn.Source, readValues, cancellationToken);
                var resultCount = Math.Min(readResponse.Results.Count, readValues.Count);
                for (var i = 0; i < resultCount; i++)
                {
                    if (StatusCode.IsGood(readResponse.Results[i].StatusCode))
                    {
                        var dataValue = readResponse.Results[i];
                        var monitoredItem = monitoredItems[offset + i];
                        if (monitoredItem.Handle is RegisteredSubjectProperty property)
                        {
                            result[property] = dataValue;
                        }
                    }
                }
            }
        
            foreach (var (property, dataValue) in result)
            {
                var value = _configuration.ValueConverter.ConvertToPropertyValue(dataValue.Value, property);
                property.SetValueFromSource(this, dataValue.SourceTimestamp, value);
            }

            _logger.LogInformation("Successfully read initial values of {Count} nodes.", itemCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read initial values for monitored items.");
            throw;
        }
    }

    public Task<Action?> LoadCompleteSourceStateAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult<Action?>(null);
    }

    public async ValueTask WriteToSourceAsync(IReadOnlyCollection<SubjectPropertyChange> changes, CancellationToken cancellationToken)
    {
        if (_session is null || changes.Count == 0)
        {
            // TODO: What to do when session is null? Store and write with the next write?
            return;
        }

        var chunkSize = (int)(_session.OperationLimits?.MaxNodesPerWrite ?? DefaultChunkSize);
        chunkSize = chunkSize == 0 ? int.MaxValue : chunkSize;
        
        var changeList = changes as IList<SubjectPropertyChange> ?? changes.ToList();
        for (var offset = 0; offset < changeList.Count; offset += chunkSize)
        {
            var take = Math.Min(chunkSize, changeList.Count - offset);
            var writeValues = new WriteValueCollection(take);
            for (var i = 0; i < take; i++)
            {
                var change = changeList[offset + i];
                if (change.Property.TryGetPropertyData(OpcVariableKey, out var v) && v is NodeId nodeId)
                {
                    var registeredProperty = change.Property.GetRegisteredProperty();
                    if (registeredProperty.HasSetter)
                    {
                        var value = _configuration.ValueConverter.ConvertToNodeValue(change.GetNewValue<object?>(), registeredProperty);
                        writeValues.Add(new WriteValue
                        {
                            NodeId = nodeId,
                            AttributeId = Opc.Ua.Attributes.Value,
                            Value = new DataValue
                            {
                                Value = value,
                                StatusCode = StatusCodes.Good,
                                //ServerTimestamp = DateTime.UtcNow,
                                SourceTimestamp = change.ChangedTimestamp.UtcDateTime
                            }
                        });
                    }
                }
            }
            
            if (writeValues.Count == 0)
            {
                continue;
            }
            
            var writeResponse = await _session.WriteAsync(null, writeValues, cancellationToken);
            if (writeResponse.Results.Any(StatusCode.IsBad))
            {
                _logger.LogError("Failed to write some variables (chunk starting at {Offset}).", offset);
            }
        }
    }

    private async Task<ReferenceDescriptionCollection> BrowseNodeAsync(
        NodeId nodeId, 
        CancellationToken cancellationToken)
    {
        const uint nodeClassMask = (uint)NodeClass.Variable | (uint)NodeClass.Object;
        
        var (_, _, nodeProperties, _) = await _session!.BrowseAsync(
            null,
            null,
            [nodeId],
            0u,
            BrowseDirection.Forward,
            ReferenceTypeIds.HierarchicalReferences,
            true,
            nodeClassMask,
            cancellationToken);

        return nodeProperties[0];
    }
}
