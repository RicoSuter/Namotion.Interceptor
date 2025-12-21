using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Tracking.Lifecycle;
using Opc.Ua;

namespace Namotion.Interceptor.OpcUa.Sync;

/// <summary>
/// Base class for OPC UA sync strategies providing common Subject ↔ NodeId mapping logic.
/// Thread-safe via ConcurrentDictionary for all mapping operations.
/// </summary>
internal abstract class OpcUaSyncStrategyBase : IOpcUaSyncStrategy
{
    private readonly ConcurrentDictionary<NodeId, IInterceptorSubject> _nodeIdToSubject = new();
    private readonly ConcurrentDictionary<IInterceptorSubject, NodeId> _subjectToNodeId = new();

    protected readonly OpcUaConfigurationBase Configuration;
    protected readonly ILogger Logger;

    protected NodeId? RootNodeId { get; private set; }
    protected IInterceptorSubject? RootSubject { get; private set; }

    protected OpcUaSyncStrategyBase(OpcUaConfigurationBase configuration, ILogger logger)
    {
        Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public virtual void Initialize(IInterceptorSubject rootSubject, NodeId rootNodeId)
    {
        RootSubject = rootSubject;
        RootNodeId = rootNodeId;
        RegisterMapping(rootSubject, rootNodeId);
    }

    /// <inheritdoc />
    public abstract Task OnSubjectAttachedAsync(SubjectLifecycleChange change, CancellationToken cancellationToken);

    /// <inheritdoc />
    public abstract Task OnSubjectDetachedAsync(SubjectLifecycleChange change, CancellationToken cancellationToken);

    /// <inheritdoc />
    public abstract Task OnRemoteNodeAddedAsync(ReferenceDescription node, NodeId parentNodeId, CancellationToken cancellationToken);

    /// <inheritdoc />
    public virtual Task OnRemoteNodeRemovedAsync(NodeId nodeId, CancellationToken cancellationToken)
    {
        Logger.LogDebug("Remote node removed: {NodeId}. Removing local subject...", nodeId);

        var subject = FindSubject(nodeId);
        if (subject is null)
        {
            return Task.CompletedTask;
        }

        try
        {
            // Allow derived classes to perform additional cleanup
            OnBeforeSubjectRemoved(subject);

            // Detach from parent property
            var registeredSubject = subject.TryGetRegisteredSubject();
            if (registeredSubject is not null && registeredSubject.Parents.Length > 0)
            {
                DetachSubjectFromParent(subject, registeredSubject.Parents);
            }

            Logger.LogInformation(
                "Removed subject {SubjectType} for deleted node {NodeId}",
                subject.GetType().Name,
                nodeId);

            UnregisterMapping(subject);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to detach subject for removed node {NodeId}", nodeId);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Called before a subject is removed, allowing derived classes to perform additional cleanup.
    /// </summary>
    protected virtual void OnBeforeSubjectRemoved(IInterceptorSubject subject)
    {
        // Default: no additional cleanup needed
    }

    /// <inheritdoc />
    public virtual Task<ReferenceDescriptionCollection> BrowseNodeAsync(NodeId nodeId, CancellationToken cancellationToken)
    {
        // Default: return empty collection. Server-side doesn't need to browse.
        // Client-side overrides this to actually browse the remote server.
        return Task.FromResult(new ReferenceDescriptionCollection());
    }

    /// <summary>
    /// Gets the source object used to set property values during detachment.
    /// </summary>
    protected abstract object DetachmentSource { get; }

    /// <inheritdoc />
    public void EnsureUnregistered(IInterceptorSubject subject)
    {
        UnregisterMapping(subject);
    }

    /// <inheritdoc />
    public void ClearAllMappings()
    {
        _nodeIdToSubject.Clear();
        _subjectToNodeId.Clear();
    }

    /// <summary>
    /// Registers a bidirectional mapping between a subject and its NodeId.
    /// Thread-safe.
    /// </summary>
    protected void RegisterMapping(IInterceptorSubject subject, NodeId nodeId)
    {
        _nodeIdToSubject[nodeId] = subject;
        _subjectToNodeId[subject] = nodeId;
    }

    /// <summary>
    /// Unregisters the mapping for a subject.
    /// Thread-safe and idempotent.
    /// </summary>
    protected void UnregisterMapping(IInterceptorSubject subject)
    {
        if (_subjectToNodeId.TryRemove(subject, out var nodeId))
        {
            _nodeIdToSubject.TryRemove(nodeId, out _);
        }
    }

    /// <summary>
    /// Clears all dynamic mappings while preserving the root mapping.
    /// Used during client reconnection to reset state without losing root.
    /// </summary>
    protected void ClearDynamicMappings()
    {
        foreach (var kvp in _nodeIdToSubject)
        {
            if (kvp.Key != RootNodeId)
            {
                UnregisterMapping(kvp.Value);
            }
        }
    }

    /// <summary>
    /// Finds the subject mapped to a NodeId, or null if not found.
    /// </summary>
    protected IInterceptorSubject? FindSubject(NodeId nodeId)
    {
        return _nodeIdToSubject.GetValueOrDefault(nodeId);
    }

    /// <summary>
    /// Finds the NodeId mapped to a subject, or null if not found.
    /// </summary>
    protected NodeId? FindNodeId(IInterceptorSubject subject)
    {
        return _subjectToNodeId.GetValueOrDefault(subject);
    }

    /// <summary>
    /// Attempts to find the property on the parent subject that corresponds to the given node.
    /// Uses the PathProvider to match browse names to property names.
    /// </summary>
    protected RegisteredSubjectProperty? FindPropertyForNode(RegisteredSubject parentSubject, ReferenceDescription node)
    {
        return Configuration.PathProvider.TryGetPropertyFromSegment(parentSubject, node.BrowseName.Name);
    }

    /// <summary>
    /// Detaches a subject from its parent property by setting the property to null.
    /// </summary>
    protected void DetachSubjectFromParent(IInterceptorSubject subject, ImmutableArray<SubjectPropertyParent> parents)
    {
        foreach (var parentRef in parents)
        {
            var property = parentRef.Property;
            if (property.IsSubjectReference)
            {
                var currentValue = property.GetValue();
                if (ReferenceEquals(currentValue, subject))
                {
                    property.SetValueFromSource(DetachmentSource, null, null);
                    Logger.LogDebug(
                        "Detached subject {SubjectType} from parent property {PropertyName}",
                        subject.GetType().Name,
                        property.Name);
                    return;
                }
            }
            else if (property.IsSubjectCollection || property.IsSubjectDictionary)
            {
                Logger.LogDebug(
                    "Subject {SubjectType} is in a collection/dictionary - lifecycle cleanup will handle removal",
                    subject.GetType().Name);
            }
        }
    }
}
