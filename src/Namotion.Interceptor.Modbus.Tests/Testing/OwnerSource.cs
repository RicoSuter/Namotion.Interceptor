using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Connectors.Diagnostics;
using Namotion.Interceptor.Connectors.Monitoring;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Modbus.Tests.Testing;

/// <summary>An inert source that only owns properties, for tests that need a claim without a running source.</summary>
internal sealed class OwnerSource(IInterceptorSubject rootSubject) : ISubjectSource
{
    public IInterceptorSubject RootSubject { get; } = rootSubject;

    public int WriteBatchSize => 0;

    public SourceState State => SourceState.Synchronized;

    public DateTimeOffset StateChangeTime { get; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? LastSynchronizedAt => null;

    public SourceDiagnostics Diagnostics { get; } = new(new SourceMetrics());

    ConnectorDiagnostics ISubjectConnector.Diagnostics => Diagnostics;

    public event EventHandler<SourceEvent>? StateChanged
    {
        add { /* The state never changes, so there is nothing to raise. */ }
        remove { /* Nothing is subscribed. */ }
    }

    public Task<Action?> LoadInitialStateAsync(CancellationToken cancellationToken)
        => Task.FromResult<Action?>(null);

    public ValueTask<WriteResult> WriteChangesAsync(ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken)
        => new(WriteResult.Success);
}
