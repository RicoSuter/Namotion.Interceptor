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
        int writeRetryQueueSize = 1000)
        : base(context, logger, bufferTime, retryTime, writeRetryQueueSize)
    {
        _subject = subject;
    }

    public override IInterceptorSubject RootSubject => _subject;

    public int WriteBatchSizeOverride { get; init; }

    public override int WriteBatchSize => WriteBatchSizeOverride;

    public Func<SubjectPropertyWriter, CancellationToken, Task<IAsyncDisposable?>>? StartListeningOverride { get; init; }

    public Func<CancellationToken, Task<Action?>>? LoadInitialStateOverride { get; init; }

    public Func<ReadOnlyMemory<SubjectPropertyChange>, CancellationToken, ValueTask<WriteResult>>? WriteChangesOverride { get; init; }

    protected override Task<IAsyncDisposable?> StartListeningAsync(SubjectPropertyWriter propertyWriter, CancellationToken cancellationToken)
        => StartListeningOverride is not null
            ? StartListeningOverride(propertyWriter, cancellationToken)
            : Task.FromResult<IAsyncDisposable?>(null);

    public override Task<Action?> LoadInitialStateAsync(CancellationToken cancellationToken)
        => LoadInitialStateOverride is not null
            ? LoadInitialStateOverride(cancellationToken)
            : Task.FromResult<Action?>(null);

    public override ValueTask<WriteResult> WriteChangesAsync(ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken)
        => WriteChangesOverride?.Invoke(changes, cancellationToken) ?? ValueTask.FromResult(WriteResult.Success);

    public Func<ChangeQueueProcessorConfiguration>? CreateChangeQueueConfigurationOverride { get; init; }

    protected override ChangeQueueProcessorConfiguration CreateChangeQueueConfiguration()
        => CreateChangeQueueConfigurationOverride is not null
            ? CreateChangeQueueConfigurationOverride()
            : base.CreateChangeQueueConfiguration();
}
