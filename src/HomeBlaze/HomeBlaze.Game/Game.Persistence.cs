using HomeBlaze.Storage.Abstractions;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Tracking.Parent;

namespace HomeBlaze.Game;

public partial class Game
{
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
