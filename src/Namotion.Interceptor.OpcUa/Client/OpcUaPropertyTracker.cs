using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Sources;

namespace Namotion.Interceptor.OpcUa.Client;

/// <summary>
/// Property tracker for OPC UA client sources.
/// Extends <see cref="SourcePropertyTracker"/> with OPC UA-specific cleanup behavior.
/// </summary>
internal sealed class OpcUaPropertyTracker : SourcePropertyTracker
{
    private readonly OpcUaSubjectClientSource _opcUaSource;

    public OpcUaPropertyTracker(OpcUaSubjectClientSource source, ILogger? logger = null)
        : base(source, logger)
    {
        _opcUaSource = source;
    }

    /// <inheritdoc />
    protected override void OnSubjectDetaching(IInterceptorSubject subject)
    {
        // Skip cleanup during reconnection (subscriptions being transferred)
        if (_opcUaSource.IsReconnecting)
        {
            return;
        }

        _opcUaSource.RemoveItemsForSubject(subject);
    }

    /// <inheritdoc />
    protected override void OnPropertyUntracked(PropertyReference property)
    {
        // Remove OPC UA node data before removing source
        property.RemovePropertyData(_opcUaSource.OpcUaNodeIdKey);
        base.OnPropertyUntracked(property);
    }
}
