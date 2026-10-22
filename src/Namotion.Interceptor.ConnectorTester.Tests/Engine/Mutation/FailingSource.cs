using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Connectors.Diagnostics;
using Namotion.Interceptor.Connectors.Monitoring;
using Namotion.Interceptor.ConnectorTester.Model;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.ConnectorTester.Tests.Engine.Mutation;

/// <summary>
/// Fails every write, as a disconnected connector would under chaos.
/// </summary>
internal sealed class FailingSource : ISubjectSource
{
    public required IInterceptorSubject RootSubject { get; init; }

    public int WriteBatchSize => 0;

    public SourceState State => SourceState.Synchronizing;

    public DateTimeOffset StateChangeTime { get; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? LastSynchronizedAt => null;

    public SourceDiagnostics Diagnostics { get; } = new(new SourceMetrics());

    ConnectorDiagnostics ISubjectConnector.Diagnostics => Diagnostics;

    public event EventHandler<SourceEvent>? StateChanged
    {
        add { }
        remove { }
    }

    /// <summary>
    /// Makes a new failing source own every value property the mutation strategies write on <paramref name="node"/>.
    /// </summary>
    public static void AttachTo(TestNode node)
    {
        var source = new FailingSource { RootSubject = node };
        new PropertyReference(node, nameof(TestNode.StringValue)).SetSource(source);
        new PropertyReference(node, nameof(TestNode.DecimalValue)).SetSource(source);
        new PropertyReference(node, nameof(TestNode.IntValue)).SetSource(source);
        new PropertyReference(node, nameof(TestNode.LongValue)).SetSource(source);
    }

    public ValueTask<WriteResult> WriteChangesAsync(ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken)
        => new(WriteResult.Failure(changes, new InvalidOperationException("Source is not connected")));

    public Task<Action?> LoadInitialStateAsync(CancellationToken cancellationToken)
        => Task.FromResult<Action?>(null);
}
