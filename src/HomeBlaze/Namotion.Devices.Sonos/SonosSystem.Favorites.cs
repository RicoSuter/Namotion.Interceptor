using Microsoft.Extensions.Logging;
using Namotion.Devices.Sonos.Parsing;

namespace Namotion.Devices.Sonos;

public partial class SonosSystem
{
    // The favorites reads requested by ContentDirectory events and not yet done; a read that completes resets it
    // unless another request came in meanwhile, so a burst of events costs one read after the one in flight.
    private int _favoritesRefreshRequests;

    // 1 while a favorites refresh runs, so events start at most one.
    private int _isFavoritesRefreshRunning;

    // Whether the last read of favorites that events asked for failed, so the next poll reads them once more. Guarded
    // by _reconcileLock.
    private bool _retriesFavoritesRead;

    /// <summary>
    /// Whether a favorites read requested by an event is pending or running. Tests use it.
    /// </summary>
    internal bool HasPendingFavoritesRefresh =>
        Volatile.Read(ref _favoritesRefreshRequests) != 0 || Volatile.Read(ref _isFavoritesRefreshRunning) != 0;

    // One handler, and so one record of the last update ids, per subscription.
    private Action<string> CreateContentDirectoryHandler()
    {
        var updateIds = new FavoritesUpdateIds();
        return body => OnContentDirectoryEvent(updateIds, body);
    }

    // Runs on the listener's thread, so it only records the request; the read runs on its own.
    private void OnContentDirectoryEvent(FavoritesUpdateIds updateIds, string body)
    {
        var change = UpnpEventParser.ParseContentDirectory(body);
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "Sonos ContentDirectory event: FavoritesUpdateID {FavoritesUpdateId}, ContainerUpdateIDs {ContainerUpdateIds}, SystemUpdateID {SystemUpdateId}.",
                change.FavoritesUpdateId, change.ContainerUpdateIds, change.SystemUpdateId);
        }

        if (updateIds.Record(change))
        {
            RequestFavoritesRefresh();
        }
    }

    private void RequestFavoritesRefresh()
    {
        Interlocked.Increment(ref _favoritesRefreshRequests);
        if (Interlocked.CompareExchange(ref _isFavoritesRefreshRunning, 1, 0) == 0)
        {
            _ = RunFavoritesRefreshAsync();
        }
    }

    private async Task RunFavoritesRefreshAsync()
    {
        // Leaves the event's thread before taking the reconcile lock.
        await Task.Yield();
        do
        {
            try
            {
                await RefreshRequestedFavoritesAsync();
            }
            catch (Exception exception)
            {
                // Disconnected, disposed or torn down: the first reconciliation of the next connection reads them.
                Interlocked.Exchange(ref _favoritesRefreshRequests, 0);
                _logger.LogDebug(exception, "Reading the Sonos favorites after an event stopped.");
            }

            Volatile.Write(ref _isFavoritesRefreshRunning, 0);
        }
        while (Volatile.Read(ref _favoritesRefreshRequests) != 0 && Interlocked.CompareExchange(ref _isFavoritesRefreshRunning, 1, 0) == 0);
    }

    // Under the reconcile lock, like every favorites read: a poll that reads them meanwhile also clears the requests
    // made before it started, so a burst during a reconciliation costs no read of its own.
    private async Task RefreshRequestedFavoritesAsync()
    {
        using var scopeCancellation = CreateScopeCancellation(CancellationToken.None);
        await _reconcileLock.WaitAsync(scopeCancellation.Token);
        try
        {
            if (Volatile.Read(ref _favoritesRefreshRequests) != 0)
            {
                await RefreshFavoritesAsync(Players.Values.Where(player => player.IsInTopology).ToArray(), scopeCancellation.Token);
            }
        }
        finally
        {
            _reconcileLock.Release();
        }
    }

    /// <summary>
    /// The update ids of the favorites the last ContentDirectory event of a subscription reported.
    /// </summary>
    private sealed class FavoritesUpdateIds
    {
        private readonly Lock _lock = new();
        private bool _hasEvent;
        private string? _favoritesUpdateId;
        private string? _favoritesContainerUpdateId;

        /// <summary>
        /// Records the ids of an event and returns whether the favorites may have changed: it is the first event, which
        /// carries the full state, or FavoritesUpdateID or the update id of the favorites container changed.
        /// </summary>
        internal bool Record(ContentDirectoryChange change)
        {
            lock (_lock)
            {
                var isChanged = !_hasEvent;
                _hasEvent = true;
                if (change.FavoritesUpdateId is { } favoritesUpdateId && favoritesUpdateId != _favoritesUpdateId)
                {
                    isChanged = true;
                    _favoritesUpdateId = favoritesUpdateId;
                }

                if (change.FavoritesContainerUpdateId is { } containerUpdateId && containerUpdateId != _favoritesContainerUpdateId)
                {
                    isChanged = true;
                    _favoritesContainerUpdateId = containerUpdateId;
                }

                return isChanged;
            }
        }
    }
}
