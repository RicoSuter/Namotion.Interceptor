using System;
using System.Threading;
using System.Threading.Tasks;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Connectors.Diagnostics;
using Namotion.Interceptor.Connectors.Monitoring;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Benchmark;

/// <summary>
/// A source that accepts every write without doing any I/O, so a benchmark measures only the transaction path.
/// </summary>
internal sealed class BenchmarkSource : ISubjectSource
{
    public BenchmarkSource(IInterceptorSubject rootSubject)
    {
        RootSubject = rootSubject;
    }

    public IInterceptorSubject RootSubject { get; }

    public int WriteBatchSize => 0;

    public ValueTask<WriteResult> WriteChangesAsync(
        ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken)
    {
        return new ValueTask<WriteResult>(WriteResult.Success);
    }

    public Task<Action?> LoadInitialStateAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult<Action?>(null);
    }

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
}
