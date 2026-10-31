using HomeBlaze.Storage.Abstractions;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Tracking.Parent;

namespace HomeBlaze.Game;

public partial class Game
{
    // Guarded by _lock: set by moves and joins, cleared by the flush that writes them.
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
    /// Writes the configuration when a move or join happened since the last write.
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

        await WriteConfigurationAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await FlushPendingSaveAsync(cancellationToken);
    }

    /// <summary>
    /// Writes the configuration through the storage that holds the game; logs instead of throwing when that fails.
    /// </summary>
    private async Task WriteConfigurationAsync(CancellationToken cancellationToken)
    {
        try
        {
            var writer = this.TryGetFirstParent<IConfigurationWriter>();
            if (writer is null || !await writer.WriteConfigurationAsync(this, cancellationToken))
            {
                _logger.LogWarning("The game {Game} was not saved and loses its changes on restart", Title);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Saving the game {Game} failed", Title);
        }
    }
}
