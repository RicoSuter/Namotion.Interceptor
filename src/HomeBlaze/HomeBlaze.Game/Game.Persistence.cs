using HomeBlaze.Storage.Abstractions;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Tracking.Parent;

namespace HomeBlaze.Game;

public partial class Game
{
    // Guarded by _lock: set by moves, joins and failed writes, cleared by the flush that writes them.
    private bool _isSaveRequested;

    internal bool HasPendingSave
    {
        get
        {
            lock (_lock)
            {
                return _isSaveRequested;
            }
        }
    }

    /// <summary>
    /// Writes the configuration when a move or join happened since the last write; a failed write stays pending, so the
    /// next flush tries again.
    /// </summary>
    internal async Task FlushPendingSaveAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (!_isSaveRequested)
            {
                return;
            }

            _isSaveRequested = false;
        }

        await WriteOrRequestSaveAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        try
        {
            await FlushPendingSaveAsync(cancellationToken);
        }
        catch (OperationCanceledException exception)
        {
            _logger.LogWarning(exception, "The game {Game} stopped before it was saved and loses its latest changes on restart", Title);
        }
    }

    /// <summary>
    /// Writes the configuration and requests a save for the next flush when that fails or is canceled.
    /// </summary>
    private async Task WriteOrRequestSaveAsync(CancellationToken cancellationToken)
    {
        var isWritten = false;
        try
        {
            isWritten = await WriteConfigurationAsync(cancellationToken);
        }
        finally
        {
            if (!isWritten)
            {
                lock (_lock)
                {
                    _isSaveRequested = true;
                }
            }
        }
    }

    /// <summary>
    /// Writes the configuration through the storage that holds the game; logs instead of throwing when that fails.
    /// </summary>
    /// <returns>False when a storage failed to write, so that writing again may succeed.</returns>
    private async Task<bool> WriteConfigurationAsync(CancellationToken cancellationToken)
    {
        var writer = this.TryGetFirstParent<IConfigurationWriter>();
        if (writer is null)
        {
            // No storage holds the game, which writing again does not change.
            _logger.LogWarning("The game {Game} was not saved and loses its changes on restart", Title);
            return true;
        }

        try
        {
            if (await writer.WriteConfigurationAsync(this, cancellationToken))
            {
                return true;
            }

            _logger.LogWarning("The game {Game} was not saved and is saved again later", Title);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Saving the game {Game} failed and is tried again later", Title);
        }

        return false;
    }
}
