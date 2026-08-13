namespace Namotion.Interceptor.Connectors;

/// <summary>
/// One iteration of a connector's restart loop: the cancellation that iteration's work runs under,
/// linked to the connector's stopping token, together with the flag that says an injected
/// <see cref="FaultType.Kill"/> cancelled it.
/// </summary>
/// <remarks>
/// The flag belongs to the iteration rather than to the connector because a kill can arrive after the
/// iteration it was meant for has already torn down. A connector-level flag stays set across that
/// boundary, and the next iteration then reads a kill that never reached it and swallows a genuine
/// fault as an injected one. Keeping the flag here means only the iteration whose token source was
/// actually cancelled can read itself as killed.
/// <para>
/// A loop creates one attempt per iteration, runs its work under <see cref="Token"/>, and disposes it
/// in a <c>finally</c>. The connector publishes the current attempt in a <c>volatile</c> field so that
/// <see cref="IFaultInjectable.InjectFaultAsync"/> can reach it, and clears that field before the
/// disposal so a kill arriving from then on finds no attempt rather than a disposed one.
/// </para>
/// </remarks>
public sealed class ConnectorRunAttempt : IDisposable
{
    private readonly CancellationTokenSource _cancellation;

    private volatile bool _wasForceKilled;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConnectorRunAttempt"/> class whose cancellation is
    /// linked to the connector's stopping token.
    /// </summary>
    /// <param name="stoppingToken">The connector's stopping token.</param>
    public ConnectorRunAttempt(CancellationToken stoppingToken)
    {
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
    }

    /// <summary>
    /// Gets the token this attempt's work runs under. Read it once at the start of the iteration: the
    /// attempt is disposed when the iteration ends and reading it afterwards throws.
    /// </summary>
    public CancellationToken Token => _cancellation.Token;

    /// <summary>
    /// Gets a value indicating whether <see cref="ForceKillAsync"/> cancelled this attempt. Use it to
    /// tell an injected cancellation from a genuine failure, after the connector's stopping token has
    /// been ruled out.
    /// </summary>
    public bool WasForceKilled => _wasForceKilled;

    /// <summary>
    /// Marks this attempt as force-killed and cancels it, in that order, so the loop cannot reach its
    /// kill check before the mark is visible. An attempt that is already disposed is left unmarked: the
    /// loop is then between attempts and this kill reached nothing.
    /// </summary>
    public async Task ForceKillAsync()
    {
        _wasForceKilled = true;
        try
        {
            await _cancellation.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            _wasForceKilled = false;
        }
    }

    /// <summary>
    /// Cancels this attempt without marking it as force-killed, for a loop that ends its own iteration,
    /// such as one stopping a sibling task once the first of them has completed.
    /// </summary>
    public Task CancelAsync() => _cancellation.CancelAsync();

    /// <inheritdoc />
    public void Dispose() => _cancellation.Dispose();
}
