using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Tracking.Tests.Transactions;

/// <summary>
/// Accepts every change except the configured failed or unwritten ones, records its calls and reverts, and optionally
/// marks accepted snapshot slots as confirmed in place, as the writer contract requires. Reports the reverts of the
/// configured properties as failed, and optionally completes a revert only when the commit timeout cancels it.
/// </summary>
internal sealed class CountingTransactionWriter : ITransactionWriter
{
    public int WriteCalls { get; private set; }

    /// <summary>Gets the property names of every change passed to the writer, in call order.</summary>
    public List<string> ReceivedPropertyNames { get; } = [];

    public Action? DuringWrite { get; set; }

    public HashSet<string> FailedPropertyNames { get; } = [];

    public HashSet<string> UnwrittenPropertyNames { get; } = [];

    public object? ConfirmingSource { get; set; }

    public HashSet<string> FailedRevertPropertyNames { get; } = [];

    public bool RevertsUntilCanceled { get; init; }

    /// <summary>Gets every change passed to a revert, also those whose revert is reported as failed.</summary>
    public List<SubjectPropertyChange> Reverted { get; } = [];

    public ValueTask<SourceWriteResult> WriteToSourcesAsync(
        Memory<SubjectPropertyChange> changes,
        TransactionRequirement requirement,
        CancellationToken cancellationToken)
    {
        WriteCalls++;
        foreach (var change in changes.Span)
        {
            ReceivedPropertyNames.Add(change.Property.Name);
        }

        DuringWrite?.Invoke();
        if (ConfirmingSource is not null)
        {
            // Marks accepted slots in place, as the writer contract requires.
            var span = changes.Span;
            for (var index = 0; index < span.Length; index++)
            {
                if (IsWritten(span[index]))
                {
                    span[index] = span[index].WithOrigin(ChangeOrigin.Confirmed(ConfirmingSource));
                }
            }
        }

        var all = changes.ToArray();
        var failed = all.Where(change => FailedPropertyNames.Contains(change.Property.Name)).ToArray();
        var written = all.Where(IsWritten).ToArray();
        Exception[] errors = failed.Length > 0 ? [new InvalidOperationException("source write failed")] : [];
        return new(new SourceWriteResult(written, failed, errors, RevertState: null));
    }

    public async ValueTask<SourceRevertResult> RevertSourceWritesAsync(
        IReadOnlyList<SubjectPropertyChange> written,
        object? revertState,
        CancellationToken cancellationToken)
    {
        Reverted.AddRange(written);
        if (RevertsUntilCanceled)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        var failed = written.Where(change => FailedRevertPropertyNames.Contains(change.Property.Name)).ToArray();
        Exception[] errors = failed.Length > 0 ? [new InvalidOperationException("revert failed")] : [];
        return new SourceRevertResult(failed, errors);
    }

    private bool IsWritten(SubjectPropertyChange change)
    {
        return !FailedPropertyNames.Contains(change.Property.Name) &&
            !UnwrittenPropertyNames.Contains(change.Property.Name);
    }
}
