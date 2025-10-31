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

    public Task<IDisposable?> StartListeningAsync(ISubjectMutationDispatcher dispatcher, CancellationToken cancellationToken)
    {
        _subscriptionManager.SetDispatcher(dispatcher);
        return Task.FromResult<IDisposable?>(null);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _logger.LogInformation("Connecting to OPC UA server at {ServerUrl}...", _configuration.ServerUrl);
                
                var application = await _configuration.CreateApplicationInstanceAsync(stoppingToken);
                await application.CheckApplicationInstanceCertificatesAsync(false, null, stoppingToken);

                var endpointConfiguration = EndpointConfiguration.Create(application.ApplicationConfiguration);
#pragma warning disable CS0618 // Type or member is obsolete
                var endpointDescription = await CoreClientUtils.SelectEndpointAsync(application.ApplicationConfiguration, _configuration.ServerUrl, false, 5 * 1000, new DefaultTelemetry(), stoppingToken);
#pragma warning restore CS0618 // Type or member is obsolete
                var endpoint = new ConfiguredEndpoint(null, endpointDescription, endpointConfiguration);

#pragma warning disable CS0618 // Type or member is obsolete
                _session = await Session.CreateAsync(
                    application.ApplicationConfiguration,
                    null,
                    endpoint,
                    false,
                    false,
                    application.ApplicationName,
                    60000,
                    new UserIdentity(),
                    [], 
                    stoppingToken);
#pragma warning restore CS0618 // Type or member is obsolete

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
                await _subscriptionManager.CleanupAsync(stoppingToken);
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
            var readResponse = await _session!.ReadAsync(null, 0, TimestampsToReturn.Source, readValues, cancellationToken);
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
                    var value = _configuration.ValueConverter.ConvertToNodeValue(change.GetNewValue<object?>(), registeredProperty.Type);
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
