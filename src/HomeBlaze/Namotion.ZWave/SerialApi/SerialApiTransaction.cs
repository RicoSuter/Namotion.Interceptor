namespace Namotion.ZWave.SerialApi;

/// <summary>
/// A request queued in the driver. Canceling its token completes it at once; the driver skips it when it is still queued.
/// While it is in flight, the send loop starts its attempts and the read loop completes their acknowledgement and the response.
/// </summary>
internal sealed class SerialApiTransaction
{
    private readonly TaskCompletionSource<DataFrame> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<DataFrame>? _response;
    private readonly CancellationTokenRegistration _cancellationRegistration;
    private volatile TaskCompletionSource<FrameKind>? _acknowledgement;

    // Read and written only by the read loop, which handles the ACK and the response in their order on the line.
    private bool _acknowledged;

    public SerialApiTransaction(byte function, byte[] frame, bool expectsResponse, CancellationToken cancellationToken)
    {
        Function = function;
        Frame = frame;
        if (expectsResponse)
        {
            _response = new TaskCompletionSource<DataFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        if (cancellationToken.CanBeCanceled)
        {
            _cancellationRegistration = cancellationToken.Register(
                static (state, token) => ((SerialApiTransaction)state!)._result.TrySetCanceled(token),
                this);
        }
    }

    public byte Function { get; }

    public byte[] Frame { get; }

    /// <summary>
    /// The caller's result.
    /// </summary>
    public Task<DataFrame> Task => _result.Task;

    public bool IsCompleted => _result.Task.IsCompleted;

    /// <summary>
    /// The controller's response frame, or <c>null</c> for a command that is only acknowledged.
    /// </summary>
    public Task<DataFrame>? Response => _response?.Task;

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

    /// <summary>
    /// Starts a write attempt; the returned task completes with the ACK, NAK or CAN that answers it.
    /// Called by the send loop before it writes the frame.
    /// </summary>
    public Task<FrameKind> StartAttempt()
    {
        var acknowledgement = new TaskCompletionSource<FrameKind>(TaskCreationOptions.RunContinuationsAsynchronously);
        _acknowledgement = acknowledgement;
        return acknowledgement.Task;
    }

    /// <summary>
    /// Completes the current attempt with an ACK, NAK or CAN; an ACK lets the response be accepted. Called by the read loop.
    /// </summary>
    public bool TryAcknowledge(FrameKind kind)
    {
        if (_acknowledgement?.TrySetResult(kind) != true)
        {
            return false;
        }

        _acknowledged |= kind == FrameKind.Ack;
        return true;
    }

    /// <summary>
    /// Completes the response when it answers this request's function and follows its ACK. The controller always
    /// acknowledges before it responds, so a response read before the ACK is stale. Called by the read loop.
    /// </summary>
    public bool TryRespond(DataFrame response) =>
        _acknowledged && response.Function == Function && _response?.TrySetResult(response) == true;

    /// <summary>
    /// Ends the current attempt and the response wait with an exception.
    /// </summary>
    public void FailExchange(Exception exception)
    {
        _acknowledgement?.TrySetException(exception);
        _response?.TrySetException(exception);
    }
}
