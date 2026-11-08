namespace HomeBlaze.Authentication.Blazor.Tests;

/// <summary>
/// Holds the requests to <see cref="AuthenticationTestHost.HoldPath"/> after the session check and before their response
/// starts, until <see cref="Release"/>.
/// </summary>
internal sealed class RequestHold
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once a request reached the endpoint.</summary>
    public Task Entered => _entered.Task;

    public void Release() => _released.TrySetResult();

    public Task HoldAsync()
    {
        _entered.TrySetResult();
        return _released.Task;
    }
}
