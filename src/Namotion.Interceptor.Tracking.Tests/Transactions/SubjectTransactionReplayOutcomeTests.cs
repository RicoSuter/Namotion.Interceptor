using System.Reactive.Concurrency;
using System.Reactive.Linq;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Tracking.Tests.Transactions;

public class SubjectTransactionReplayOutcomeTests
{
    public static TheoryData<ReplayFailure, bool, TransactionFailureHandling> FailureCases()
    {
        var cases = new TheoryData<ReplayFailure, bool, TransactionFailureHandling>();
        foreach (var failure in new[] { ReplayFailure.Cancel, ReplayFailure.HookThrows, ReplayFailure.ObserverThrows, ReplayFailure.Suppress })
        {
            foreach (var withWriter in new[] { false, true })
            {
                foreach (var policy in new[] { TransactionFailureHandling.Rollback, TransactionFailureHandling.BestEffort })
                {
                    cases.Add(failure, withWriter, policy);
                }
            }
        }
        return cases;
    }

    [Theory]
    [MemberData(nameof(FailureCases))]
    public async Task WhenReplayFails_ThenMutationAndSourceAreCompensatedAccordingToPolicy(
        ReplayFailure failure, bool withWriter, TransactionFailureHandling policy)
    {
        // Arrange
        var context = CreateContext(withWriter, out var writer);
        context.AddService<IWriteInterceptor>(new SuppressingReplayInterceptor());
        var successful = new ReplayOutcomeSubject(context) { Value = "old" };
        var failing = new ReplayOutcomeSubject(context) { Value = "old" };
        using var subscription = context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(change =>
        {
            if (failure == ReplayFailure.ObserverThrows && ReferenceEquals(change.Property.Subject, failing) &&
                change.GetNewValue<string>() == "new")
            {
                throw new InvalidOperationException("Observer failed after assignment.");
            }
        });
        using var transaction = await context.BeginTransactionAsync(policy);
        successful.Value = "new";
        failing.Value = "new";
        failing.Failure = failure;

        // Act
        var exception = await Assert.ThrowsAsync<SubjectTransactionException>(() => transaction.CommitAsync(default).AsTask());

        // Assert
        var expectedSuccessfulValue = policy == TransactionFailureHandling.Rollback ? "old" : "new";
        Assert.Equal(expectedSuccessfulValue, successful.Value);
        Assert.Equal("old", failing.Value);
        Assert.Contains(exception.FailedChanges, change => ReferenceEquals(change.Property.Subject, failing));
        Assert.DoesNotContain(exception.AppliedChanges, change => ReferenceEquals(change.Property.Subject, failing));
        Assert.Equal(policy == TransactionFailureHandling.Rollback ? 0 : 1, exception.AppliedChanges.Count);
        Assert.NotEmpty(exception.Errors);
        Assert.Empty(transaction.GetPendingChanges());
        if (withWriter)
        {
            Assert.Equal(expectedSuccessfulValue, writer.Values[new PropertyReference(successful, nameof(successful.Value))]);
            Assert.Equal("old", writer.Values[new PropertyReference(failing, nameof(failing.Value))]);
        }
    }

    [Theory]
    [InlineData(false, TransactionFailureHandling.Rollback)]
    [InlineData(true, TransactionFailureHandling.Rollback)]
    [InlineData(false, TransactionFailureHandling.BestEffort)]
    [InlineData(true, TransactionFailureHandling.BestEffort)]
    public async Task WhenReplayAcceptsUnchangedValue_ThenCommitSucceeds(bool withWriter, TransactionFailureHandling policy)
    {
        // Arrange
        var context = CreateContext(withWriter, out var writer);
        var subject = new ReplayOutcomeSubject(context) { Value = "old" };
        using var transaction = await context.BeginTransactionAsync(policy);
        subject.Value = "new";
        subject.Failure = ReplayFailure.AcceptUnchanged;

        // Act
        await transaction.CommitAsync(default);

        // Assert
        Assert.Equal("old", subject.Value);
        Assert.Empty(transaction.GetPendingChanges());
        Assert.Equal(0, subject.RestoreAttempts);
        if (withWriter)
        {
            // Source/model reconciliation after a transforming hook is tracked separately in #369.
            Assert.Equal("new", writer.Values[new PropertyReference(subject, nameof(subject.Value))]);
        }
    }

    [Theory]
    [InlineData(false, TransactionFailureHandling.Rollback)]
    [InlineData(true, TransactionFailureHandling.Rollback)]
    [InlineData(false, TransactionFailureHandling.BestEffort)]
    [InlineData(true, TransactionFailureHandling.BestEffort)]
    public async Task WhenCompensationIsCanceled_ThenFailureIncludesOriginalChange(bool withWriter, TransactionFailureHandling policy)
    {
        // Arrange
        var context = CreateContext(withWriter, out var writer);
        var subject = new ReplayOutcomeSubject(context) { Value = "old" };
        using var transaction = await context.BeginTransactionAsync(policy);
        subject.Value = "new";
        subject.Failure = ReplayFailure.HookThrows;
        subject.CancelRestore = true;

        // Act
        var exception = await Assert.ThrowsAsync<SubjectTransactionException>(() => transaction.CommitAsync(default).AsTask());

        // Assert
        Assert.Equal("new", subject.Value);
        Assert.Equal(1, subject.RestoreAttempts);
        Assert.Empty(exception.AppliedChanges);
        Assert.Equal(2, exception.FailedChanges.Count);
        Assert.All(exception.FailedChanges, change =>
        {
            Assert.Same(subject, change.Property.Subject);
            Assert.Equal("old", change.GetOldValue<string>());
            Assert.Equal("new", change.GetNewValue<string>());
        });
        Assert.Equal(2, exception.Errors.Count);
        if (withWriter)
        {
            Assert.Equal("old", writer.Values[new PropertyReference(subject, nameof(subject.Value))]);
        }
    }

    [Fact]
    public void WhenAcceptedReplayDoesNotMutate_ThenAnotherFailureDoesNotCompensateIt()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var unchanged = new ReplayOutcomeSubject(context) { Value = "old", Failure = ReplayFailure.AcceptUnchanged, CancelRestore = true };
        var failing = new ReplayOutcomeSubject(context) { Value = "old", Failure = ReplayFailure.HookThrows };
        SubjectPropertyChange[] changes = [CreateChange(unchanged), CreateChange(failing)];

        // Act
        var result = SubjectPropertyChangeOperations.ApplyLocalChanges(changes, exclude: null, TransactionFailureHandling.Rollback);

        // Assert
        Assert.Equal("old", unchanged.Value);
        Assert.Equal("old", failing.Value);
        Assert.Same(failing, Assert.Single(result.Failed).Property.Subject);
        Assert.Single(result.Errors);
    }

    [Theory]
    [InlineData(TransactionFailureHandling.Rollback)]
    [InlineData(TransactionFailureHandling.BestEffort)]
    public void WhenLargeBatchFailsAfterAssignment_ThenEveryRequiredPropertyIsRestored(TransactionFailureHandling policy)
    {
        // Arrange
        var subjects = Enumerable.Range(0, 512).Select(_ => new ReplayOutcomeSubject { Value = "old" }).ToArray();
        var changes = subjects.Select(CreateChange).ToArray();
        subjects[256].Failure = ReplayFailure.HookThrows;

        // Act
        var result = SubjectPropertyChangeOperations.ApplyLocalChanges(changes, exclude: null, policy);

        // Assert
        Assert.Single(result.Failed);
        Assert.Single(result.Errors);
        for (var index = 0; index < subjects.Length; index++)
        {
            Assert.Equal(policy == TransactionFailureHandling.Rollback || index == 256 ? "old" : "new", subjects[index].Value);
            Assert.Equal(policy == TransactionFailureHandling.Rollback || index == 256 ? 1 : 0, subjects[index].RestoreAttempts);
        }
    }

    [Fact]
    public void WhenLargeBatchReusesBookkeeping_ThenExcludedPropertiesAreNotCompensated()
    {
        // Arrange
        var subjects = Enumerable.Range(0, 512).Select(_ => new ReplayOutcomeSubject { Value = "old" }).ToArray();
        var changes = subjects.Select(CreateChange).ToArray();
        SubjectPropertyChangeOperations.ApplyLocalChanges(changes, exclude: null, TransactionFailureHandling.Rollback);
        subjects[127].Value = "preserved";
        subjects[256].Failure = ReplayFailure.HookThrows;

        // Act
        var result = SubjectPropertyChangeOperations.ApplyLocalChanges(changes, [changes[127]], TransactionFailureHandling.Rollback);

        // Assert
        Assert.Single(result.Failed);
        Assert.Single(result.Errors);
        Assert.Equal("preserved", subjects[127].Value);
        Assert.Equal(0, subjects[127].RestoreAttempts);
        Assert.DoesNotContain(result.Successful, change => ReferenceEquals(change.Property.Subject, subjects[127]));
    }

    [Fact]
    public void WhenLargeSuccessfulBatchIsReplayed_ThenBookkeepingDoesNotAddPerBatchAllocation()
    {
        // Arrange
        var subjects = Enumerable.Range(0, 512).Select(_ => new ReplayOutcomeSubject { Value = "old" }).ToArray();
        var changes = subjects.Select(CreateChange).ToArray();
        for (var iteration = 0; iteration < 16; iteration++)
        {
            ReplayBatch(changes, split: false);
            ReplayBatch(changes, split: true);
        }

        // Act
        var beforeLarge = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < 16; iteration++) ReplayBatch(changes, split: false);
        var largeAllocation = GC.GetAllocatedBytesForCurrentThread() - beforeLarge;
        var beforeSplit = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < 16; iteration++) ReplayBatch(changes, split: true);
        var splitAllocation = GC.GetAllocatedBytesForCurrentThread() - beforeSplit;

        // Assert
        Assert.True(largeAllocation <= splitAllocation,
            $"A 512-change replay allocated {largeAllocation} bytes; equivalent 256-change replays allocated {splitAllocation} bytes.");
        Assert.All(subjects, subject => Assert.Equal("new", subject.Value));
    }

    private static void ReplayBatch(SubjectPropertyChange[] changes, bool split)
    {
        if (split)
        {
            SubjectPropertyChangeOperations.ApplyLocalChanges(changes.AsSpan(0, 256), exclude: null, TransactionFailureHandling.Rollback);
            SubjectPropertyChangeOperations.ApplyLocalChanges(changes.AsSpan(256), exclude: null, TransactionFailureHandling.Rollback);
        }
        else
        {
            SubjectPropertyChangeOperations.ApplyLocalChanges(changes, exclude: null, TransactionFailureHandling.Rollback);
        }
    }

    private static SubjectPropertyChange CreateChange(ReplayOutcomeSubject subject) => SubjectPropertyChange.Create(
        new PropertyReference(subject, nameof(subject.Value)), ChangeOrigin.Local, DateTimeOffset.UtcNow, null, "old", "new");

    private static IInterceptorSubjectContext CreateContext(bool withWriter, out ReplayOutcomeWriter writer)
    {
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithTransactions();
        writer = new ReplayOutcomeWriter();
        if (withWriter) context.AddService<ITransactionWriter>(writer);
        return context;
    }
}

public enum ReplayFailure
{
    None,
    Cancel,
    HookThrows,
    ObserverThrows,
    Suppress,
    AcceptUnchanged
}

[InterceptorSubject]
public partial class ReplayOutcomeSubject
{
    public partial string? Value { get; set; }
    public ReplayFailure Failure { get; set; }
    public bool CancelRestore { get; set; }
    public int RestoreAttempts { get; private set; }

    partial void OnValueChanging(ref string? newValue, ref bool cancel)
    {
        if (newValue == "old")
        {
            if (Value == "new") RestoreAttempts++;
            cancel = CancelRestore;
        }
        if (newValue == "new")
        {
            cancel = Failure == ReplayFailure.Cancel;
            if (Failure == ReplayFailure.AcceptUnchanged) newValue = "old";
        }
    }

    partial void OnValueChanged(string? newValue)
    {
        if (Failure == ReplayFailure.HookThrows && newValue == "new")
        {
            throw new InvalidOperationException("Hook failed after assignment.");
        }
    }
}

[RunsBefore(typeof(SubjectTransactionInterceptor))]
internal sealed class SuppressingReplayInterceptor : IWriteInterceptor
{
    public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
    {
        if (context.Property.Subject is ReplayOutcomeSubject { Failure: ReplayFailure.Suppress } && Equals(context.NewValue, "new")) return;
        next(ref context);
    }
}

internal sealed class ReplayOutcomeWriter : ITransactionWriter
{
    public Dictionary<PropertyReference, string?> Values { get; } = [];

    public ValueTask<SourceWriteResult> WriteToSourcesAsync(Memory<SubjectPropertyChange> changes, TransactionRequirement requirement, CancellationToken cancellationToken)
    {
        var written = changes.ToArray();
        foreach (var change in written) Values[change.Property] = change.GetNewValue<string?>();
        return new ValueTask<SourceWriteResult>(new SourceWriteResult(written, [], [], null));
    }

    public ValueTask<SourceRevertResult> RevertSourceWritesAsync(IReadOnlyList<SubjectPropertyChange> written, object? revertState, CancellationToken cancellationToken)
    {
        foreach (var change in written) Values[change.Property] = change.GetOldValue<string?>();
        return new ValueTask<SourceRevertResult>(new SourceRevertResult([], []));
    }
}
