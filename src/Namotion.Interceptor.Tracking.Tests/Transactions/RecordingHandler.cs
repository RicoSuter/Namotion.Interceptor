using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Tracking.Tests.Transactions;

internal sealed class RecordingHandler : ITransactionHandler
{
    public List<(PropertyReference Property, object? Value, ChangeOrigin Origin)> Captured { get; } = [];

    public Func<PropertyReference, object?, Exception?>? RejectCapture { get; set; }

    public List<(RecordingHandler Handler, object? Value)>? CaptureLog { get; init; }

    public int CommittingCalls { get; private set; }

    public Action<SubjectTransaction, SubjectPropertyChange[]>? OnCommittingAction { get; set; }

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

    public void OnCapturing<TProperty>(SubjectTransaction transaction, PropertyReference property, TProperty newValue, ChangeOrigin origin)
    {
        if (RejectCapture?.Invoke(property, newValue) is { } exception)
        {
            throw exception;
        }

        Captured.Add((property, newValue, origin));
        CaptureLog?.Add((this, newValue));
    }

    public void OnCommitting(SubjectTransaction transaction, ReadOnlySpan<SubjectPropertyChange> changes)
    {
        CommittingCalls++;
        OnCommittingAction?.Invoke(transaction, changes.ToArray());
    }
}
