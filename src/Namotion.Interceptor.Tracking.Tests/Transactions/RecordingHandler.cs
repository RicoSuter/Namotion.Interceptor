using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Tracking.Tests.Transactions;

internal sealed class RecordingHandler : ITransactionHandler
{
    private readonly Lock _captureLock = new();

    public List<(PropertyReference Property, object? Value, ChangeOrigin Origin)> Captured { get; } = [];

    public Func<PropertyReference, object?, Exception?>? RejectCapture { get; set; }

    public List<(RecordingHandler Handler, object? Value)>? CaptureLog { get; init; }

    public int CommittingCalls => Stages.Count;

    public List<TransactionCheckStage> Stages { get; } = [];

    public Action<SubjectTransaction, SubjectPropertyChange[]>? OnCommittingAction { get; set; }

    /// <summary>
    /// Gets or sets whether <see cref="OnCommitting"/> throws an <see cref="InvalidOperationException"/> with the
    /// message "rejected" after it runs <see cref="OnCommittingAction"/>.
    /// </summary>
    public bool RejectCommit { get; set; }

    /// <summary>
    /// Rejects the second <see cref="OnCommitting"/> call, the pre-apply check when a writer is registered, with a
    /// <see cref="SubjectTransactionException"/> that reports the change of <paramref name="propertyName"/> and an
    /// <see cref="InvalidOperationException"/> with <paramref name="message"/>.
    /// </summary>
    public void RejectPreApplyCheck(string propertyName, string message)
    {
        OnCommittingAction = (_, changes) =>
        {
            if (CommittingCalls == 2)
            {
                var rejected = changes.Single(change => change.Property.Name == propertyName);
                throw new SubjectTransactionException("invalid", [], [rejected], [new InvalidOperationException(message)]);
            }
        };
    }

    /// <summary>
    /// Rejects the second <see cref="OnCommitting"/> call, the pre-apply check when a writer is registered, by throwing
    /// <paramref name="exception"/>.
    /// </summary>
    public void RejectPreApplyCheck(Exception exception)
    {
        OnCommittingAction = (_, _) =>
        {
            if (CommittingCalls == 2)
            {
                throw exception;
            }
        };
    }

    public void OnCapturing<TProperty>(in TransactionCaptureContext<TProperty> context)
    {
        if (RejectCapture?.Invoke(context.Property, context.NewValue) is { } exception)
        {
            throw exception;
        }

        // Writes from concurrent flows can capture at the same time.
        lock (_captureLock)
        {
            Captured.Add((context.Property, context.NewValue, context.Origin));
            CaptureLog?.Add((this, context.NewValue));
        }
    }

    public void OnCommitting(in TransactionCheckContext context)
    {
        Stages.Add(context.Stage);
        OnCommittingAction?.Invoke(context.Transaction, context.Changes.ToArray());
        if (RejectCommit)
        {
            throw new InvalidOperationException("rejected");
        }
    }
}
