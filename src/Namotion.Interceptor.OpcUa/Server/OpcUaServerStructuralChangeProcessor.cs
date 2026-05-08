using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking.Lifecycle;
using Opc.Ua;

namespace Namotion.Interceptor.OpcUa.Server;

internal sealed class OpcUaServerStructuralChangeProcessor : OpcUaStructuralChangeProcessor
{
    private readonly CustomNodeManager _nodeManager;
    private readonly object _nodeManagerLock;
    private readonly bool _fireModelChangeEvents;
    private readonly string _nodeIdDataKey;

    public OpcUaServerStructuralChangeProcessor(
        CustomNodeManager nodeManager,
        object nodeManagerLock,
        bool fireModelChangeEvents,
        ILogger logger)
        : base(logger)
    {
        _nodeManager = nodeManager;
        _nodeManagerLock = nodeManagerLock;
        _fireModelChangeEvents = fireModelChangeEvents;
        _nodeIdDataKey = nodeManager.SubjectNodeIdDataKey;
    }

    public Task RunAsync(CancellationToken cancellationToken) => ProcessLoopAsync(cancellationToken);

    protected override Task ProcessEventAsync(StructuralChangeEvent evt, CancellationToken cancellationToken)
    {
        lock (_nodeManagerLock)
        {
            if (evt.Verb == StructuralChangeVerb.Add)
            {
                ProcessAdd(evt);
            }
            else
            {
                ProcessRemove(evt);
            }
        }

        return Task.CompletedTask;
    }

    private void ProcessAdd(StructuralChangeEvent evt)
    {
        if (evt.Subject is null || evt.Property is null)
        {
            return;
        }

        var lifecycleChange = new SubjectLifecycleChange
        {
            Subject = evt.Subject,
            Property = evt.Property.Reference,
            Index = evt.Index,
            ReferenceCount = 0
        };

        var createdNode = _nodeManager.CreateDynamicSubjectNodes(lifecycleChange);
        if (createdNode is not null)
        {
            _nodeManager.ClearChangeMasksForSubject(evt.Subject);
            if (_fireModelChangeEvents)
            {
                _nodeManager.FireModelChangeEvent(ModelChangeStructureVerbMask.NodeAdded, createdNode.NodeId);
            }
        }
    }

    private void ProcessRemove(StructuralChangeEvent evt)
    {
        if (evt.Subject is null)
        {
            return;
        }

        var nodeId = evt.AffectedNodeId;
        _nodeManager.RemoveSubjectNodes(evt.Subject);

        if (nodeId is not null && _fireModelChangeEvents)
        {
            _nodeManager.FireModelChangeEvent(ModelChangeStructureVerbMask.NodeDeleted, nodeId);
        }
    }

    protected override bool TryGetNodeIdForSubject(IInterceptorSubject subject, out NodeId? nodeId)
    {
        if (subject.TryGetData(_nodeIdDataKey, out var obj) && obj is NodeId id)
        {
            nodeId = id;
            return true;
        }

        var registered = subject.TryGetRegisteredSubject();
        if (registered is not null)
        {
            return _nodeManager.TryGetNodeId(registered, out nodeId);
        }

        nodeId = null;
        return false;
    }
}
