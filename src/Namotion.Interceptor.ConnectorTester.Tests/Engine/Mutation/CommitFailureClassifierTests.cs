using Xunit;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Connectors.Transactions;
using Namotion.Interceptor.ConnectorTester.Engine.Mutation;
using Namotion.Interceptor.ConnectorTester.Model;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.ConnectorTester.Tests.Engine.Mutation;

public class CommitFailureClassifierTests
{
    private readonly TestNode _node = new();
    private readonly FailingSource _source;

    public CommitFailureClassifierTests()
    {
        _source = new FailingSource { RootSubject = _node };
    }

    [Fact]
    public void WhenCommitConflicts_ThenFailureIsConflict()
    {
        // Arrange
        var exception = new SubjectTransactionConflictException([CreateChange(nameof(TestNode.IntValue)).Property]);

        // Act
        var kind = CommitFailureClassifier.Classify(exception);

        // Assert
        Assert.Equal(CommitFailureKind.Conflict, kind);
    }

    [Fact]
    public async Task WhenSourcesFailFullyAndPartiallyInCommitWithLocalChanges_ThenFailureIsSourceWriteFailure()
    {
        // Arrange
        var context = CreateTransactionContext();
        var node = new TestNode(context);
        new PropertyReference(node, nameof(TestNode.IntValue)).SetSource(new FailingSource { RootSubject = node });
        var partiallyFailingSource = new FailingSource
        {
            RootSubject = node,
            Fails = change => change.Property.Name == nameof(TestNode.LongValue)
        };
        new PropertyReference(node, nameof(TestNode.LongValue)).SetSource(partiallyFailingSource);
        new PropertyReference(node, nameof(TestNode.DecimalValue)).SetSource(partiallyFailingSource);
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        node.IntValue = 1;
        node.LongValue = 2;
        node.DecimalValue = 3;
        node.StringValue = "local";
        var exception = await Assert.ThrowsAsync<SubjectTransactionException>(() => transaction.CommitAsync(CancellationToken.None).AsTask());

        // Act
        var kind = CommitFailureClassifier.Classify(exception);

        // Assert
        Assert.Equal(CommitFailureKind.SourceWriteFailure, kind);
        Assert.Equal(
            [nameof(TestNode.IntValue), nameof(TestNode.LongValue)],
            exception.FailedChanges.Select(change => change.Property.Name).Order());
        Assert.Equal(3m, node.DecimalValue);
        Assert.Equal("local", node.StringValue);
    }

    public enum UnexpectedFailure
    {
        TransactionWriterThrew,
        LocalApplyFailedBesideSourceWrite,
        MoreChangesFailedThanTheSourceWritesReport,
        NoErrorsOrFailedChanges
    }

    [Theory]
    [InlineData(UnexpectedFailure.TransactionWriterThrew)]
    [InlineData(UnexpectedFailure.LocalApplyFailedBesideSourceWrite)]
    [InlineData(UnexpectedFailure.MoreChangesFailedThanTheSourceWritesReport)]
    [InlineData(UnexpectedFailure.NoErrorsOrFailedChanges)]
    public void WhenFailureIsNotOnlySourceWriteFailures_ThenFailureIsUnexpected(UnexpectedFailure failure)
    {
        // Arrange
        var sourceChange = CreateChange(nameof(TestNode.IntValue));
        var otherChange = CreateChange(nameof(TestNode.LongValue));
        var exception = failure switch
        {
            UnexpectedFailure.TransactionWriterThrew => CreateException([sourceChange], new NullReferenceException()),
            UnexpectedFailure.LocalApplyFailedBesideSourceWrite => CreateException(
                [sourceChange, otherChange],
                CreateSourceWriteError(sourceChange),
                new InvalidOperationException("Setter failed.")),
            UnexpectedFailure.MoreChangesFailedThanTheSourceWritesReport => CreateException(
                [sourceChange, otherChange],
                CreateSourceWriteError(sourceChange)),
            _ => CreateException([])
        };

        // Act
        var kind = CommitFailureClassifier.Classify(exception);

        // Assert
        Assert.Equal(CommitFailureKind.Unexpected, kind);
    }

    [Fact]
    public void WhenSameFailureIsRecordedTwice_ThenItIsCountedTwiceAndLoggedOnce()
    {
        // Arrange
        var counters = new MutationCounters();
        var logger = new RecordingLogger();
        var change = CreateChange(nameof(TestNode.IntValue));

        // Act
        CommitFailureClassifier.Record(CreateException([change], CreateSourceWriteError(change)), counters, logger, "client");
        CommitFailureClassifier.Record(CreateException([change], CreateSourceWriteError(change)), counters, logger, "client");
        CommitFailureClassifier.Record(CreateException([change], new NullReferenceException()), counters, logger, "client");

        // Assert
        Assert.Equal(2, counters.SourceWriteFailedCommitCount);
        Assert.Equal(1, counters.UnexpectedFailedCommitCount);
        Assert.Equal([LogLevel.Warning, LogLevel.Error], logger.Entries.Select(entry => entry.Level));
    }

    [Fact]
    public void WhenCountersAreReset_ThenFirstFailureIsLoggedAgain()
    {
        // Arrange
        var counters = new MutationCounters();
        var logger = new RecordingLogger();
        var change = CreateChange(nameof(TestNode.IntValue));
        CommitFailureClassifier.Record(CreateException([change], CreateSourceWriteError(change)), counters, logger, "client");

        // Act
        counters.Reset();
        CommitFailureClassifier.Record(CreateException([change], CreateSourceWriteError(change)), counters, logger, "client");

        // Assert
        Assert.Equal(1, counters.SourceWriteFailedCommitCount);
        Assert.Equal(2, logger.Entries.Count);
    }

    private static IInterceptorSubjectContext CreateTransactionContext()
        => InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithSourceTransactions();

    private SubjectPropertyChange CreateChange(string propertyName)
    {
        return SubjectPropertyChange.Create(
            new PropertyReference(_node, propertyName), ChangeOrigin.Local, DateTimeOffset.UtcNow, null, 0, 1);
    }

    private SourceTransactionWriteException CreateSourceWriteError(SubjectPropertyChange change)
    {
        return new SourceTransactionWriteException(_source, [change], new InvalidOperationException("Source is not connected"));
    }

    private static SubjectTransactionException CreateException(IReadOnlyList<SubjectPropertyChange> failedChanges, params Exception[] errors)
    {
        return new SubjectTransactionException("One or more changes failed.", [], failedChanges, errors);
    }
}
