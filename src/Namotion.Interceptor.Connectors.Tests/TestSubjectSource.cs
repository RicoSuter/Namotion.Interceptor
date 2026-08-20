using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests;

/// <summary>
/// Test subclass of <see cref="SubjectSourceBase"/> that lets tests configure each protected hook via delegates.
/// </summary>
public sealed class TestSubjectSource : SubjectSourceBase
{
    private readonly IInterceptorSubject _subject;

    public TestSubjectSource(
        IInterceptorSubject subject,
        IInterceptorSubjectContext context,
        ILogger logger,
        TimeSpan? bufferTime = null,
        TimeSpan? retryTime = null,
        int writeRetryQueueSize = 1000,
        TimeSpan? teardownFlushTimeout = null)
        : base(context, logger, bufferTime, retryTime, writeRetryQueueSize, teardownFlushTimeout)
    {
        _subject = subject;
    }

    public override IInterceptorSubject RootSubject => _subject;

    /// <summary>Exposes the protected ReportConnectionLost seam for tests.</summary>
    public void SimulateConnectionLost() => ReportConnectionLost();

    /// <summary>Exposes the protected BeginResume seam for tests, returning the epoch it opened.</summary>
    public int BeginResumeForTest() => BeginResume();

    /// <summary>Exposes the protected CompleteResumeAsync seam for tests.</summary>
    public Task CompleteResumeForTestAsync(int resumeEpoch, CancellationToken cancellationToken) =>
        CompleteResumeAsync(resumeEpoch, cancellationToken);

    /// <summary>Exposes the protected AbortResume seam for tests, returning whether it cleared the gate.</summary>
    public bool AbortResumeForTest(int resumeEpoch) => AbortResume(resumeEpoch);

    /// <summary>Exposes the protected CurrentResumeEpoch seam for tests.</summary>
    public int CurrentResumeEpochForTest => CurrentResumeEpoch;

    /// <summary>Exposes the protected ParkChangesForRetry seam for tests.</summary>
    public void ParkChangesForRetryForTest(ReadOnlySpan<SubjectPropertyChange> changes) =>
        ParkChangesForRetry(changes);

    public int WriteBatchSizeOverride { get; init; }

    public override int WriteBatchSize => WriteBatchSizeOverride;

    public Func<SubjectPropertyWriter, CancellationToken, Task<IAsyncDisposable?>>? StartListeningOverride { get; init; }

    public Exception? StartListeningFailure { get; init; }

    public Func<CancellationToken, Task<Action?>>? LoadInitialStateOverride { get; init; }

    public Func<ReadOnlyMemory<SubjectPropertyChange>, CancellationToken, ValueTask<WriteResult>>? WriteChangesOverride { get; init; }

    protected override Task<IAsyncDisposable?> StartListeningAsync(SubjectPropertyWriter propertyWriter, CancellationToken cancellationToken)
    {
        if (StartListeningFailure is not null)
        {
            throw StartListeningFailure;
        }

        return StartListeningOverride is not null
            ? StartListeningOverride(propertyWriter, cancellationToken)
            : Task.FromResult<IAsyncDisposable?>(null);
    }

    public override Task<Action?> LoadInitialStateAsync(CancellationToken cancellationToken)
        => LoadInitialStateOverride is not null
            ? LoadInitialStateOverride(cancellationToken)
            : Task.FromResult<Action?>(null);

    public override ValueTask<WriteResult> WriteChangesAsync(ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken)
        => WriteChangesOverride?.Invoke(changes, cancellationToken) ?? ValueTask.FromResult(WriteResult.Success);
}
