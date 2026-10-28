using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Tracking.Tests.Transactions;

/// <summary>
/// Accepts every write and reports the reverts of the named properties as failed.
/// </summary>
internal sealed class FailingRevertTransactionWriter(params string[] failedRevertPropertyNames) : ITransactionWriter
{
    public ValueTask<SourceWriteResult> WriteToSourcesAsync(
        Memory<SubjectPropertyChange> changes,
        TransactionRequirement requirement,
        CancellationToken cancellationToken)
    {
        return new(new SourceWriteResult(changes.ToArray(), [], [], RevertState: null));
    }

    public ValueTask<SourceRevertResult> RevertSourceWritesAsync(
        IReadOnlyList<SubjectPropertyChange> written,
        object? revertState,
        CancellationToken cancellationToken)
    {
        var failed = written.Where(change => failedRevertPropertyNames.Contains(change.Property.Name)).ToArray();
        return new(new SourceRevertResult(failed, [new InvalidOperationException("revert failed")]));
    }
}
