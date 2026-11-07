namespace HomeBlaze.Authentication;

/// <summary>
/// Runs the changes of the role store and the user store one at a time, so that a change validates against roles and
/// users no other change modifies meanwhile, such as a role deleted while it is assigned. Not reentrant. One instance
/// is shared by both stores.
/// </summary>
public sealed class AuthorizationChangeGate
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    /// <summary>Waits until no other change runs; every successful wait must be followed by one <see cref="Release"/>.</summary>
    public Task WaitAsync(CancellationToken cancellationToken) => _semaphore.WaitAsync(cancellationToken);

    /// <summary>Lets the next change run.</summary>
    public void Release() => _semaphore.Release();
}
