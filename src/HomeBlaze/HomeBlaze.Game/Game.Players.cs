using HomeBlaze.Game.Abstractions;
using HomeBlaze.Game.Clients;
using HomeBlaze.Game.World;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Game;

public partial class Game
{
    /// <summary>
    /// The most characters a name that a player gives its client may have.
    /// </summary>
    internal const int MaximumNameLength = 50;

    /// <summary>
    /// The message with which renaming is refused for a caller that is not a player of this game.
    /// </summary>
    internal const string NoRenameActorMessage = "Only a player in the game can change their name.";

    /// <summary>
    /// Gets the name of the client of the caller's player, or null while that client is anonymous or the caller is not a
    /// player of this game.
    /// </summary>
    internal string? GetClientName(GameCaller? caller)
    {
        if (GetActor(caller) is not { } player || _clients.GetClients() is not { } clients)
        {
            return null;
        }

        foreach (var client in clients)
        {
            if (client.Id == player.ClientId)
            {
                return client.IsNamed ? client.Title : null;
            }
        }

        return null;
    }

    /// <summary>
    /// Names the client of the caller's player, which keeps the player when its browser closes; the player takes the
    /// name with the next reconcile, which this requests.
    /// </summary>
    /// <exception cref="ArgumentException">The name is empty or longer than <see cref="MaximumNameLength"/> after trimming.</exception>
    /// <exception cref="InvalidOperationException">The caller is not a player of this game, or its client is gone.</exception>
    internal async Task RenameAsync(GameCaller? caller, string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var trimmed = name.Trim();
        if (trimmed.Length > MaximumNameLength)
        {
            throw new ArgumentException($"A name has at most {MaximumNameLength} characters.", nameof(name));
        }

        var player = GetActor(caller) ?? throw new InvalidOperationException(NoRenameActorMessage);
        await _clients.RenameAsync(player.ClientId, trimmed, cancellationToken);

        // Not every directory reports the rename as a client change.
        SignalReconcile();
    }

    /// <summary>
    /// Brings <see cref="Players"/> in line with the clients: a client that is connected or named has a player, which
    /// joins on a free position; does nothing while there is no installation.
    /// </summary>
    internal void ReconcilePlayers()
    {
        var clients = _clients.GetClients();
        if (clients is null)
        {
            return;
        }

        lock (_lock)
        {
            SynchronizePlayerSet(clients);
            ReleaseVoiceOfReplacedPlayers();

            var index = GetIndex();
            foreach (var client in clients)
            {
                if (Players.TryGetValue(client.Id, out var player))
                {
                    UpdatePlayer(player, client, index);
                }
            }
        }
    }

    // Callers hold _lock. Replaces Players only when a player is added or removed.
    private void SynchronizePlayerSet(IReadOnlyList<GameClient> clients)
    {
        var current = CurrentPlayers;
        var next = AddMissingPlayers(clients, RemoveStalePlayers(clients, current));
        if (!ReferenceEquals(next, current))
        {
            ReplacePlayers(next);
            RequestIndexRebuild();
            _isSaveRequested = true;
        }
    }

    // Returns the dictionary itself when nothing changes, so that a copy only exists for a change.
    private static Dictionary<string, GamePlayer> RemoveStalePlayers(IReadOnlyList<GameClient> clients, Dictionary<string, GamePlayer> players)
    {
        var result = players;
        foreach (var id in players.Keys)
        {
            if (!ShouldHavePlayer(clients, id))
            {
                if (ReferenceEquals(result, players))
                {
                    result = new Dictionary<string, GamePlayer>(players);
                }

                result.Remove(id);
            }
        }

        return result;
    }

    private static Dictionary<string, GamePlayer> AddMissingPlayers(IReadOnlyList<GameClient> clients, Dictionary<string, GamePlayer> players)
    {
        var result = players;
        foreach (var client in clients)
        {
            if ((client.IsNamed || client.IsConnected) && !result.ContainsKey(client.Id))
            {
                if (ReferenceEquals(result, players))
                {
                    result = new Dictionary<string, GamePlayer>(players);
                }

                result[client.Id] = new GamePlayer();
            }
        }

        return result;
    }

    // Callers hold _lock.
    private void UpdatePlayer(GamePlayer player, GameClient client, GameWorldIndex index)
    {
        var isJoining = client.IsConnected && !player.IsConnected;
        player.ClientTitle = client.Title;
        player.IsConnected = client.IsConnected;
        if (!client.IsConnected)
        {
            ResetVoice(player);
        }

        if (isJoining)
        {
            var position = SpawnFinder.FindFreePosition(player.Area, area => !index.IsBlocked(area, player));
            player.X = position.X;
            player.Y = position.Y;
            _isSaveRequested = true;
            _logger.LogDebug("{Client} joined the game {Game} at {X}, {Y}", client.Title, Title, position.X, position.Y);
        }

        index.AddOrUpdate(player);
    }

    private static bool ShouldHavePlayer(IReadOnlyList<GameClient> clients, string id)
    {
        foreach (var client in clients)
        {
            if (client.Id == id)
            {
                return client.IsNamed || client.IsConnected;
            }
        }

        return false;
    }
}
