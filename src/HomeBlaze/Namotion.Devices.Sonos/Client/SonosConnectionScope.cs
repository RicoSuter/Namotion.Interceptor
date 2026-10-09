using Namotion.Devices.Sonos.Events;

namespace Namotion.Devices.Sonos.Client;

/// <summary>
/// What one connection attempt owns: its HttpClient, which the connections and the event listener borrow, the event
/// listener, the connections to the seed and the devices, and the cancellation its teardown triggers.
/// </summary>
/// <remarks>
/// Not thread-safe: <see cref="SonosSystem"/> reads and changes <see cref="Connections"/> and <see cref="Seed"/> only
/// under its connections lock, and only while the scope is its current one.
/// </remarks>
internal sealed class SonosConnectionScope : IAsyncDisposable
{
    internal SonosConnectionScope(HttpClient httpClient, SonosEventListener eventListener)
    {
        HttpClient = httpClient;
        EventListener = eventListener;
    }

    internal HttpClient HttpClient { get; }

    internal SonosEventListener EventListener { get; }

    /// <summary>
    /// Cancelled first on teardown, so work running on a caller's token stops instead of writing state afterwards.
    /// </summary>
    internal CancellationTokenSource Cancellation { get; } = new();

    /// <summary>
    /// The connections to the players and satellites in the topology, by RINCON id.
    /// </summary>
    internal Dictionary<string, SonosConnection> Connections { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The connection topology is read through; null until a seed is selected.
    /// </summary>
    internal SonosConnection? Seed { get; set; }

    /// <summary>
    /// Disposes the event listener, which waits for its running handlers, then the connections and the HttpClient.
    /// Unsubscribing the events is up to the caller, before.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        // Sources linked to it before only unregister from it when they are disposed.
        Cancellation.Dispose();
        await EventListener.DisposeAsync();

        foreach (var connection in Connections.Values)
        {
            connection.Dispose();
        }

        Seed?.Dispose();
        HttpClient.Dispose();
    }
}
