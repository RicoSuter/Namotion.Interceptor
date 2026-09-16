using System.Collections.Concurrent;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Tracking.Tests.Transactions;

public class SubjectTransactionReplaySupportTests
{
    [Theory]
    [InlineData(false, TransactionFailureHandling.Rollback, TransactionLocking.Exclusive)]
    [InlineData(true, TransactionFailureHandling.Rollback, TransactionLocking.Exclusive)]
    [InlineData(false, TransactionFailureHandling.BestEffort, TransactionLocking.Exclusive)]
    [InlineData(true, TransactionFailureHandling.BestEffort, TransactionLocking.Exclusive)]
    [InlineData(false, TransactionFailureHandling.Rollback, TransactionLocking.Optimistic)]
    [InlineData(true, TransactionFailureHandling.Rollback, TransactionLocking.Optimistic)]
    [InlineData(false, TransactionFailureHandling.BestEffort, TransactionLocking.Optimistic)]
    [InlineData(true, TransactionFailureHandling.BestEffort, TransactionLocking.Optimistic)]
    public async Task WhenAnyPendingPropertyLacksReplaySupport_ThenCommitFailsBeforeAnyWrites(
        bool withWriter, TransactionFailureHandling policy, TransactionLocking locking)
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithTransactions();
        var writer = new ReplayOutcomeWriter();
        if (withWriter) context.AddService<ITransactionWriter>(writer);
        var supported = new ReplayOutcomeSubject(context) { Value = "old" };
        var unsupported = new ManualReplaySubject(context) { Value = "old" };
        using var transaction = await context.BeginTransactionAsync(policy, locking: locking);
        supported.Value = "new";
        unsupported.Value = "new";

        // Act
        var exception = await Assert.ThrowsAsync<NotSupportedException>(() => transaction.CommitAsync(default).AsTask());

        // Assert
        Assert.Contains(nameof(ManualReplaySubject), exception.Message);
        Assert.Contains(nameof(ManualReplaySubject.Value), exception.Message);
        Assert.Empty(writer.Values);
        Assert.Equal(2, transaction.GetPendingChanges().Count);
        transaction.Dispose();
        Assert.Equal("old", supported.Value);
        Assert.Equal("old", unsupported.Value);
        Assert.Equal(0, supported.RestoreAttempts);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenGeneratedSetterMetadataIsReplaced_ThenPreflightRejectsWithoutCallingWriter(bool withWriter)
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithTransactions();
        var writer = new ReplayOutcomeWriter();
        if (withWriter) context.AddService<ITransactionWriter>(writer);
        var subject = new ReplayOutcomeSubject(context) { Value = "old" };
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.Rollback);
        subject.Value = "new";
        ReplaceSetter(subject);

        // Act
        var exception = await Assert.ThrowsAsync<NotSupportedException>(() => transaction.CommitAsync(default).AsTask());

        // Assert
        Assert.Contains(nameof(ReplayOutcomeSubject), exception.Message);
        Assert.Empty(writer.Values);
        transaction.Dispose();
        Assert.Equal("old", subject.Value);
    }

    [Theory]
    [InlineData(TransactionFailureHandling.Rollback)]
    [InlineData(TransactionFailureHandling.BestEffort)]
    public async Task WhenMetadataChangesWhileWriterAwaits_ThenReplayRejectsAndSourceIsCompensated(TransactionFailureHandling policy)
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithTransactions();
        var writer = new SuspendedReplayWriter();
        context.AddService<ITransactionWriter>(writer);
        var subject = new ReplayOutcomeSubject(context) { Value = "old" };
        using var transaction = await context.BeginTransactionAsync(policy);
        subject.Value = "new";

        // Act
        var commit = transaction.CommitAsync(default).AsTask();
        try
        {
            var startedOrCompleted = await Task.WhenAny(writer.Started.Task, commit).WaitAsync(TimeSpan.FromSeconds(10));
            await startedOrCompleted;
            Assert.True(writer.Started.Task.IsCompletedSuccessfully);
            ReplaceSetter(subject);
        }
        finally
        {
            writer.Resume.TrySetResult();
        }
        var exception = await Assert.ThrowsAsync<SubjectTransactionException>(() => commit);

        // Assert
        Assert.Contains(exception.Errors, error => error is NotSupportedException);
        Assert.Equal("old", writer.SourceValue);
        Assert.Equal(1, writer.RevertCalls);
        Assert.Equal("old", subject.Value);
        Assert.Equal(0, subject.RestoreAttempts);
    }

    [Theory]
    [InlineData(TransactionLocking.Exclusive)]
    [InlineData(TransactionLocking.Optimistic)]
    public async Task WhenUnsupportedMetadataIsRestored_ThenFailedPreflightCanBeRetried(TransactionLocking locking)
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithTransactions();
        var subject = new ReplayOutcomeSubject(context) { Value = "old" };
        var metadata = ((IInterceptorSubject)subject).Properties[nameof(subject.Value)];
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.Rollback, locking: locking);
        subject.Value = "new";
        ReplaceSetter(subject);
        await Assert.ThrowsAsync<NotSupportedException>(() => transaction.CommitAsync(default).AsTask());
        ((IInterceptorSubject)subject).AddProperties(metadata);

        // Act
        await transaction.CommitAsync(default);

        // Assert
        Assert.Equal("new", subject.Value);
        Assert.Empty(transaction.GetPendingChanges());
    }

    private static void ReplaceSetter(IInterceptorSubject subject)
    {
        var metadata = subject.Properties[nameof(ReplayOutcomeSubject.Value)];
        subject.AddProperties(new SubjectPropertyMetadata(metadata.Name, metadata.Type, metadata.Attributes,
            metadata.GetValue, (_, _) => throw new InvalidOperationException("Replacement setter must not run."),
            metadata.IsIntercepted, metadata.IsDynamic));
    }
}

internal sealed class SuspendedReplayWriter : ITransactionWriter
{
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string? SourceValue { get; private set; }
    public int RevertCalls { get; private set; }

    public async ValueTask<SourceWriteResult> WriteToSourcesAsync(Memory<SubjectPropertyChange> changes,
        TransactionRequirement requirement, CancellationToken cancellationToken)
    {
        var written = changes.ToArray();
        SourceValue = written[0].GetNewValue<string>();
        Started.SetResult();
        await Resume.Task;
        return new SourceWriteResult(written, [], [], null);
    }

    public ValueTask<SourceRevertResult> RevertSourceWritesAsync(IReadOnlyList<SubjectPropertyChange> written,
        object? revertState, CancellationToken cancellationToken)
    {
        RevertCalls++;
        SourceValue = written[0].GetOldValue<string>();
        return new ValueTask<SourceRevertResult>(new SourceRevertResult([], []));
    }
}

internal sealed class ManualReplaySubject : IInterceptorSubject
{
    private static readonly IReadOnlyDictionary<string, SubjectPropertyMetadata> Metadata =
        new Dictionary<string, SubjectPropertyMetadata>
        {
            [nameof(Value)] = new(nameof(Value), typeof(string), [],
                subject => ((ManualReplaySubject)subject).Value,
                (subject, value) => ((ManualReplaySubject)subject).Value = (string?)value,
                isIntercepted: true, isDynamic: false)
        };

    private readonly InterceptorExecutor _executor;
    private string? _value;

    public ManualReplaySubject(IInterceptorSubjectContext context)
    {
        _executor = new InterceptorExecutor(this);
        _executor.AddFallbackContext(context);
    }

    public string? Value
    {
        get => _executor.GetPropertyValue(nameof(Value), subject => ((ManualReplaySubject)subject)._value);
        set => _executor.SetPropertyValue(nameof(Value), value, _value, (subject, newValue) => ((ManualReplaySubject)subject)._value = newValue);
    }

    public object SyncRoot { get; } = new();
    public IInterceptorSubjectContext Context => _executor;
    public ConcurrentDictionary<(string? property, string key), object?> Data { get; } = new();
    public IReadOnlyDictionary<string, SubjectPropertyMetadata> Properties => Metadata;
    public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties) => throw new NotSupportedException();
}
