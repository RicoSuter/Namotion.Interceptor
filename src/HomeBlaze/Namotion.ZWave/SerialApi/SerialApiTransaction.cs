namespace Namotion.ZWave.SerialApi;

/// <summary>
/// A request queued in the driver. Canceling its token completes it at once; the driver skips it when it is still queued.
/// </summary>
internal sealed class SerialApiTransaction
{
    private readonly TaskCompletionSource<DataFrame> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenRegistration _cancellationRegistration;

    public SerialApiTransaction(byte function, byte[] frame, bool expectsResponse, CancellationToken cancellationToken)
    {
        Function = function;
        Frame = frame;
        ExpectsResponse = expectsResponse;
        if (cancellationToken.CanBeCanceled)
        {
            _cancellationRegistration = cancellationToken.Register(
                static (state, token) => ((SerialApiTransaction)state!)._result.TrySetCanceled(token),
                this);
        }
    }

    public byte Function { get; }

    public byte[] Frame { get; }

    public bool ExpectsResponse { get; }

    public Task<DataFrame> Task => _result.Task;

    public bool IsCompleted => _result.Task.IsCompleted;

    public void Complete(DataFrame response)
    {
        _result.TrySetResult(response);
        _cancellationRegistration.Dispose();
    }

    public void Fail(Exception exception)
    {
        _result.TrySetException(exception);
        _cancellationRegistration.Dispose();
    }
}
