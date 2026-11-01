using HomeBlaze.Abstractions.Messaging;
using HomeBlaze.Game.Abstractions;

namespace HomeBlaze.Game;

public partial class Game
{
    /// <summary>
    /// The time a player waits between two messages it sends.
    /// </summary>
    internal static readonly TimeSpan MessageInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Gets the connected player of this game the caller acts as, or null when it acts as none, for example from the
    /// subject page, MCP or an agent.
    /// </summary>
    internal GamePlayer? GetActor(GameCaller? caller)
    {
        if (caller is null)
        {
            return null;
        }

        lock (_lock)
        {
            return Players.TryGetValue(caller.ClientId, out var player) && player.IsConnected ? player : null;
        }
    }

    /// <exception cref="InvalidOperationException">The sender sent a message less than <see cref="MessageInterval"/> ago.</exception>
    internal Task SendMessageAsync(GamePlayer recipient, GamePlayer? sender, string message, CancellationToken cancellationToken)
    {
        if (sender is not null)
        {
            TakeMessageSlot(sender);
        }

        var notification = new Notification
        {
            Title = sender is null ? "Message" : $"Message from {sender.Title}",
            Message = message
        };
        return _clients.SendNotificationAsync(recipient.ClientId, notification, cancellationToken);
    }

    private void TakeMessageSlot(GamePlayer sender)
    {
        lock (_lock)
        {
            var now = _timeProvider.GetTimestamp();
            if (sender.LastMessageTimestamp is { } last && _timeProvider.GetElapsedTime(last, now) < MessageInterval)
            {
                throw new InvalidOperationException("Wait a moment before sending another message.");
            }

            sender.LastMessageTimestamp = now;
        }
    }
}
