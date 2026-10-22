using System.ComponentModel.DataAnnotations;
using Xunit;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors.Transactions;
using Namotion.Interceptor.ConnectorTester.Engine.Mutation;
using Namotion.Interceptor.ConnectorTester.Model;
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
    public void WhenOnlySourceWritesFailed_ThenFailureIsSourceWriteFailure()
    {
        // Arrange
        var intChange = CreateChange(nameof(TestNode.IntValue));
        var longChange = CreateChange(nameof(TestNode.LongValue));
        var exception = CreateException(
            [intChange, longChange],
            CreateSourceWriteError(intChange),
            CreateSourceWriteError(longChange));

        // Act
        var kind = CommitFailureClassifier.Classify(exception);

        // Assert
        Assert.Equal(CommitFailureKind.SourceWriteFailure, kind);
    }

    [Fact]
    public void WhenTransactionWriterThrew_ThenFailureIsUnexpected()
    {
        // Arrange
        var change = CreateChange(nameof(TestNode.IntValue));
        var exception = CreateException([change], new NullReferenceException());

        // Act
        var kind = CommitFailureClassifier.Classify(exception);

        // Assert
        Assert.Equal(CommitFailureKind.Unexpected, kind);
    }

    [Fact]
    public void WhenValidationRejectedTheCommit_ThenFailureIsUnexpected()
    {
        // Arrange
        var change = CreateChange(nameof(TestNode.IntValue));
        var exception = CreateException([change], new ValidationException("invalid"));

        // Act
        var kind = CommitFailureClassifier.Classify(exception);

        // Assert
        Assert.Equal(CommitFailureKind.Unexpected, kind);
    }

    [Fact]
    public void WhenLocalApplyFailedBesideSourceWrite_ThenFailureIsUnexpected()
    {
        // Arrange
        var sourceChange = CreateChange(nameof(TestNode.IntValue));
        var localChange = CreateChange(nameof(TestNode.LongValue));
        var exception = CreateException(
            [sourceChange, localChange],
            CreateSourceWriteError(sourceChange),
            new InvalidOperationException("Setter failed."));

        // Act
        var kind = CommitFailureClassifier.Classify(exception);

        // Assert
        Assert.Equal(CommitFailureKind.Unexpected, kind);
    }

    [Fact]
    public void WhenMoreChangesFailedThanTheSourceWritesReport_ThenFailureIsUnexpected()
    {
        // Arrange
        var sourceChange = CreateChange(nameof(TestNode.IntValue));
        var otherChange = CreateChange(nameof(TestNode.LongValue));
        var exception = CreateException([sourceChange, otherChange], CreateSourceWriteError(sourceChange));

        // Act
        var kind = CommitFailureClassifier.Classify(exception);

        // Assert
        Assert.Equal(CommitFailureKind.Unexpected, kind);
    }

    [Fact]
    public void WhenCommitFailedWithoutErrors_ThenFailureIsUnexpected()
    {
        // Arrange
        var exception = CreateException([CreateChange(nameof(TestNode.IntValue))]);

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
        Assert.Equal([LogLevel.Warning, LogLevel.Error], logger.Levels);
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
        Assert.Equal(2, logger.Levels.Count);
    }

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

    private sealed class RecordingLogger : ILogger
    {
        public List<LogLevel> Levels { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Levels.Add(logLevel);
        }
    }
}
