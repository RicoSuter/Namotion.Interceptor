using Namotion.Interceptor.Connectors.Transactions;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.ConnectorTester.Engine.Mutation;

/// <summary>
/// Classifies, counts and logs the failed commits of the mutation strategies, which commit with
/// <see cref="TransactionFailureHandling.BestEffort"/>.
/// </summary>
/// <remarks>
/// A source write failure is recognized by its shape, not by the exception a connector threw: every error is a
/// <see cref="SourceTransactionWriteException"/> and together they account for every failed change. A source reports
/// any exception from its connector that way, so a connector defect while writing counts as a source write failure;
/// the first failure of each distinct set of exception types is logged per cycle to surface it. A transaction writer
/// that throws, a local apply or revert failure and a validation rejection add errors of other types, so they are
/// unexpected. Under BestEffort a source write is reverted only after a local apply failed, so a source revert failure
/// always comes with an unexpected error.
/// </remarks>
public static class CommitFailureClassifier
{
    /// <summary>
    /// Returns the category of <paramref name="exception"/>, thrown by a BestEffort commit.
    /// </summary>
    public static CommitFailureKind Classify(SubjectTransactionException exception)
    {
        if (exception is SubjectTransactionConflictException)
        {
            return CommitFailureKind.Conflict;
        }

        return IsSourceWriteFailure(exception) ? CommitFailureKind.SourceWriteFailure : CommitFailureKind.Unexpected;
    }

    /// <summary>
    /// Counts <paramref name="exception"/> in its category and logs it when it is the first failure of its category
    /// and exception types in the cycle: as an error when unexpected, and as a warning otherwise.
    /// </summary>
    public static void Record(
        SubjectTransactionException exception,
        MutationCounters counters,
        ILogger logger,
        string participantName)
    {
        var kind = Classify(exception);
        counters.IncrementFailedCommit(kind);

        var errorTypes = string.Join(", ", exception.Errors
            .Select(error => error.InnerException is { } innerException
                ? $"{error.GetType().Name}({innerException.GetType().Name})"
                : error.GetType().Name)
            .Distinct()
            .Order());

        if (counters.TryMarkFirstCommitFailure($"{kind}: {exception.GetType().Name} [{errorTypes}]"))
        {
            logger.Log(
                kind == CommitFailureKind.Unexpected ? LogLevel.Error : LogLevel.Warning,
                exception,
                "[{Participant}] First {Kind} commit failure of this kind in the cycle: {ExceptionType} with errors: {Errors}",
                participantName,
                kind,
                exception.GetType().Name,
                string.Join("; ", exception.Errors.Select(DescribeError)));
        }
    }

    private static bool IsSourceWriteFailure(SubjectTransactionException exception)
    {
        if (exception.Errors.Count == 0)
        {
            return false;
        }

        var sourceFailedChangeCount = 0;
        foreach (var error in exception.Errors)
        {
            if (error is not SourceTransactionWriteException writeException)
            {
                return false;
            }

            sourceFailedChangeCount += writeException.FailedChanges.Count;
        }

        return sourceFailedChangeCount == exception.FailedChanges.Count;
    }

    private static string DescribeError(Exception error)
    {
        return error.InnerException is { } innerException
            ? $"{error.GetType().Name}: {error.Message} ({innerException.GetType().Name}: {innerException.Message})"
            : $"{error.GetType().Name}: {error.Message}";
    }
}
