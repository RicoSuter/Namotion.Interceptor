using HomeBlaze.Game.Clients;
using HomeBlaze.Game.World;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Game;

public partial class Game
{
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
            ResetVoiceOfRemovedPlayers(current, next);
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
            var position = SpawnFinder.FindFreePosition(player.Footprint, area => !index.IsBlocked(area, player));
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
