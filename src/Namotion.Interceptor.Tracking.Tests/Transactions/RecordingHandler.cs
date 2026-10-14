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
