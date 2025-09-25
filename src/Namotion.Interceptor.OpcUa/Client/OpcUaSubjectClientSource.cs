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
using System.Collections.Concurrent;

namespace Namotion.Interceptor.OpcUa.Client;

internal class OpcUaSubjectClientSource : BackgroundService, ISubjectSource
{
    private const int MaxItemsPerSubscription = 1000;

    private const string PathDelimiter = ".";
    private const string OpcVariableKey = "OpcVariable";

    private readonly IInterceptorSubject _subject;
    private readonly string _serverUrl;
    private readonly ISourcePathProvider _sourcePathProvider;
    private readonly ILogger _logger;
    private readonly string? _rootName;
    private readonly ConcurrentDictionary<uint, RegisteredSubjectProperty> _monitoredItems = new();
    private readonly List<Subscription> _activeSubscriptions = new();

    private ISubjectMutationDispatcher? _dispatcher;
    private Session? _session;

    public OpcUaSubjectClientSource(
        IInterceptorSubject subject,
        string serverUrl,
        ISourcePathProvider sourcePathProvider,
        ILogger<OpcUaSubjectClientSource> logger,
        string? rootName)
    {
        _subject = subject;
        _serverUrl = serverUrl;
        _sourcePathProvider = sourcePathProvider;
        _logger = logger;
        _rootName = rootName;
    }

    public bool IsPropertyIncluded(RegisteredSubjectProperty property)
    {
        return _sourcePathProvider.IsPropertyIncluded(property);
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
                var application = new ApplicationInstance
                {
                    ApplicationName = "Namotion.Interceptor.Client",
                    ApplicationType = ApplicationType.Client
                };

                application.ApplicationConfiguration = BuildClientConfiguration(application.ApplicationName);

                // Ensure app certificate and validator are ready
                await application.CheckApplicationInstanceCertificates(false);

                var endpointConfiguration = EndpointConfiguration.Create(application.ApplicationConfiguration);
                var endpointDescription = CoreClientUtils.SelectEndpoint(application.ApplicationConfiguration, _serverUrl, false);
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
                    _rootName is not null ?
                    references
                        .SelectMany(c => c)
                        .FirstOrDefault(r => r.BrowseName.Name == _rootName) :
                    new ReferenceDescription
                    {
                        NodeId = new ExpandedNodeId(ObjectIds.ObjectsFolder),
                        BrowseName = new QualifiedName("Objects", 0)
                    };

                if (rootNode is not null)
                {
                    var monitoredItems = new List<MonitoredItem>();
                    await LoadSubjectAsync(_subject, rootNode, monitoredItems, _rootName ?? string.Empty, linked.Token);

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

    private static ApplicationConfiguration BuildClientConfiguration(string applicationName)
    {
        var host = System.Net.Dns.GetHostName();
        var applicationUri = $"urn:{host}:Namotion.Interceptor:{applicationName}";

        var config = new ApplicationConfiguration
        {
            ApplicationName = applicationName,
            ApplicationType = ApplicationType.Client,
            ApplicationUri = applicationUri,
            ProductUri = "urn:Namotion.Interceptor",
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = "Directory",
                    StorePath = "pki/own",
                    SubjectName = $"CN={applicationName}, O=Namotion"
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
            }
        };

        // Initialize certificate validator
        config.CertificateValidator = new CertificateValidator();
        config.CertificateValidator.Update(config);

        return config;
    }

    private async Task CreateBatchedSubscriptionsAsync(List<MonitoredItem> monitoredItems, Session session, CancellationToken cancellationToken)
    {
        for (var i = 0; i < monitoredItems.Count; i += MaxItemsPerSubscription)
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
                             .Take(MaxItemsPerSubscription))
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
                        var value = ConvertToPropertyValue(dataValue, property);
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
                    NewValue = ConvertToPropertyValue(i.Value, property),
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

    private static object? ConvertToPropertyValue(DataValue dataValue, RegisteredSubjectProperty property)
    {
        var value = dataValue.Value;

        var targetType = Nullable.GetUnderlyingType(property.Type) ?? property.Type;

        // Handle decimal conversion for scalar values (including nullable)
        if (targetType == typeof(decimal))
        {
            if (value is not null)
            {
                value = Convert.ToDecimal(value);
            }
        }
        // Handle decimal array conversion
        else if (property.Type.IsArray && property.Type.GetElementType() == typeof(decimal))
        {
            if (value is double[] doubleArray)
            {
                value = doubleArray.Select(d => (decimal)d).ToArray();
            }
        }

        return value;
    }

    private async Task LoadSubjectAsync(IInterceptorSubject subject, ReferenceDescription node, List<MonitoredItem> monitoredItems, string prefix, CancellationToken cancellationToken)
    {
        var registeredSubject = subject.TryGetRegisteredSubject();
        if (registeredSubject is not null && _session is not null)
        {
            foreach (var property in registeredSubject.Properties)
            {
                var propertyName = GetPropertyName(property);
                if (propertyName is not null)
                {
                    var collectionPath = JoinPath(prefix, propertyName);
                    // TODO: Do the same in the server
                    if (property.IsSubjectReference)
                    {
                        var children = property.Children;
                        if (children.Any())
                        {
                            await LoadSubjectAsync(children.Single().Subject, node, monitoredItems, collectionPath, cancellationToken);
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
                                await LoadSubjectAsync(newSubject, nodeProperty, monitoredItems, collectionPath, cancellationToken);
                                property.SetValueFromSource(this, null, newSubject);
                            }
                        }
                    }
                    else if (property.IsSubjectCollection)
                    {
                        var (_, _ , nodeProperties, _) = await _session.BrowseAsync(
                            null,
                            null,
                            [new NodeId(collectionPath, node.NodeId.NamespaceIndex)],
                            0u,
                            BrowseDirection.Forward,
                            ReferenceTypeIds.HierarchicalReferences,
                            true,
                            (uint)NodeClass.Variable | (uint)NodeClass.Object | (uint)NodeClass.Method, 
                            cancellationToken);

                        var childSubjectList = nodeProperties
                            .SelectMany(p => p)
                            .Select(p => new
                            {
                                Node = p, // TODO: Use ISubjectFactory to create the subject
                                Subject = (IInterceptorSubject)Activator.CreateInstance(
                                    property.Type.IsArray ? property.Type.GetElementType()! : property.Type.GenericTypeArguments[0])!
                            })
                            .ToList();
                        
                        var childSubjectArray = Array.CreateInstance(property.Type.GetElementType()!, childSubjectList.Count);
                        for (var arrayIndex = 0; arrayIndex < childSubjectList.Count; arrayIndex++)
                        {
                            childSubjectArray.SetValue(childSubjectList[arrayIndex].Subject, arrayIndex);
                        }

                        property.SetValue(childSubjectArray);

                        var pathIndex = 0;
                        foreach (var child in childSubjectList)
                        {
                            var fullPath = JoinPath(prefix, propertyName) + $"[{pathIndex}]";
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
                        MonitorValueNode(JoinPath(prefix, propertyName), property, node, monitoredItems);
                    }
                }
            }
        }
    }

    private static string JoinPath(string prefix, string segment)
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
            var propertyName = _sourcePathProvider.TryGetPropertySegment(property);
            if (propertyName is null)
                return null;
            
            // TODO: Create property reference node instead of __?
            return GetPropertyName(attributedProperty) + "__" + propertyName;
        }
        
        return _sourcePathProvider.TryGetPropertySegment(property);
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
                else if (val is Array && val.GetType().GetElementType() == typeof(decimal))
                {
                    // Convert decimal[] to double[] for OPC UA write
                    var dec = (decimal[])val;
                    val = dec.Select(d => (double)d).ToArray();
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
