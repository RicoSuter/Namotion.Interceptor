using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Registry.Abstractions;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Client.LoadPlan;

internal sealed class OpcUaLoadPlan
{
    private readonly IInterceptorSubject _rootSubject;
    private readonly ILogger _logger;

    private readonly List<(IInterceptorSubject Subject, IInterceptorSubjectContext ParentContext)> _stagedSubjects = new();
    private readonly List<(PropertyReference Property, NodeId NodeId, MonitoredItem MonitoredItem)> _claims = new();
    private readonly Dictionary<PropertyReference, int> _claimIndices = new(PropertyReference.Comparer);
    private readonly List<(object Source, RegisteredSubjectProperty Property, object? Value)> _stagedValues = new();
    private readonly List<(object Source, RegisteredSubjectProperty Property, object? Value)> _rootAssignments = new();

    public OpcUaLoadPlan(IInterceptorSubject rootSubject, ILogger logger)
    {
        _rootSubject = rootSubject;
        _logger = logger;
    }

    public void AddStagedSubject(IInterceptorSubject subject, IInterceptorSubjectContext parentContext)
        => _stagedSubjects.Add((subject, parentContext));

    public void AddClaim(PropertyReference property, NodeId nodeId, MonitoredItem monitoredItem)
    {
        if (_claimIndices.TryGetValue(property, out var index))
        {
            var existing = _claims[index];
            if (existing.NodeId != nodeId && nodeId.CompareTo(existing.NodeId) < 0)
            {
                // Deterministic tie-break: the same property reached by two browse paths keeps the smaller NodeId.
                _claims[index] = (property, nodeId, monitoredItem);
            }

            return;
        }

        _claimIndices[property] = _claims.Count;
        _claims.Add((property, nodeId, monitoredItem));
    }

    public void AddValueAssignment(object source, RegisteredSubjectProperty property, object? value)
    {
        if (ReferenceEquals(property.Subject, _rootSubject))
        {
            _rootAssignments.Add((source, property, value));
        }
        else
        {
            _stagedValues.Add((source, property, value));
        }
    }

    public IReadOnlyList<MonitoredItem> Commit(OpcUaSubjectClientSource source)
    {
        var ownership = source.Ownership;
        var nodeIdKey = source.OpcUaNodeIdKey;
        var monitoredItems = new List<MonitoredItem>(_claims.Count);
        var committedClaims = new List<(PropertyReference Property, string Key)>(_claims.Count);

        try
        {
            // Step 1: attach staged subjects into a graph that is not yet reachable from root.
            foreach (var (subject, parentContext) in _stagedSubjects)
            {
                subject.Context.AddFallbackContext(parentContext);
            }

            // Steps 2 and 3: claim ownership, stamp node-id metadata, build the monitored-item list.
            foreach (var (property, nodeId, monitoredItem) in _claims)
            {
                var alreadyOwned = property.TryGetSource(out var existing) && ReferenceEquals(existing, source);
                if (!ownership.ClaimSource(property))
                {
                    _logger.LogError(
                        "Property {Subject}.{Property} already owned by another source. Skipping OPC UA monitoring.",
                        property.Subject.GetType().Name, property.Name);
                    continue;
                }

                if (!alreadyOwned)
                {
                    committedClaims.Add((property, nodeIdKey));
                }

                property.SetPropertyData(nodeIdKey, nodeId);
                monitoredItems.Add(monitoredItem);
            }

            // Step 4: apply values to staged subjects before they become reachable from root.
            foreach (var (valueSource, property, value) in _stagedValues)
            {
                property.SetValueFromSource(valueSource, null, null, value);
            }

            // Steps 5 and 6: assign staged subjects onto root and apply root-level values.
            foreach (var (valueSource, property, value) in _rootAssignments)
            {
                property.SetValueFromSource(valueSource, null, null, value);
            }

            return monitoredItems;
        }
        catch
        {
            // Commit boundary: release the ownership and metadata this commit established. Root values
            // assigned above, and node-id stamps written onto properties this source already owned from a
            // previous load, are not restored; the next retry (or reconnect ClearAll) reconciles them.
            foreach (var (property, key) in committedClaims)
            {
                try
                {
                    property.RemovePropertyData(key);
                    ownership.ReleaseSource(property);
                }
                catch (Exception releaseException)
                {
                    _logger.LogWarning(releaseException, "Failed to release claim during commit rollback.");
                }
            }

            throw;
        }
    }
}
