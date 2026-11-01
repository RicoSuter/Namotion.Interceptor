namespace HomeBlaze.Game.Simulation;

/// <summary>
/// Writes the simulation's state to the facade subjects: the changed players' values and the player and object sets of
/// the game. Used only on the simulation thread.
/// </summary>
internal sealed class GamePublisher(Game game, GameWorldState world, TimeProvider timeProvider)
{
    private long _lastPublishTimestamp;
    private bool _hasPublished;

    /// <summary>
    /// Whether a publish writes right now, so the write interceptor ignores these writes.
    /// </summary>
    public bool IsPublishing { get; private set; }

    /// <summary>
    /// Whether publishing waits, as it does while a save writes the subjects.
    /// </summary>
    public bool IsPaused { get; set; }

    /// <summary>
    /// Publishes when <see cref="GameSimulationSettings.PublishInterval"/> passed since the last publish, unless paused.
    /// </summary>
    /// <returns>Whether anything was written.</returns>
    public bool TryPublish()
    {
        if (IsPaused)
        {
            return false;
        }

        var now = timeProvider.GetTimestamp();
        if (_hasPublished && timeProvider.GetElapsedTime(_lastPublishTimestamp, now) < GameSimulationSettings.PublishInterval)
        {
            return false;
        }

        return Publish();
    }

    /// <summary>
    /// Writes everything pending now, also while paused.
    /// </summary>
    /// <returns>Whether anything was written.</returns>
    public bool Publish()
    {
        if (world.PendingPublishes.Count == 0 && !world.IsPlayerSetChanged && !world.IsObjectSetChanged)
        {
            return false;
        }

        _lastPublishTimestamp = timeProvider.GetTimestamp();
        _hasPublished = true;
        IsPublishing = true;
        try
        {
            foreach (var player in world.PendingPublishes)
            {
                var subject = player.Subject;
                subject.X = player.Area.X;
                subject.Y = player.Area.Y;
                subject.Facing = player.Facing;
                subject.IsConnected = player.IsConnected;
                subject.ClientTitle = player.ClientTitle;
                subject.IsMicrophoneOn = player.IsMicrophoneOn;
                subject.IsAudioOn = player.IsAudioOn;
            }

            world.ClearPendingPublishes();
            if (world.IsPlayerSetChanged)
            {
                world.IsPlayerSetChanged = false;
                var players = new Dictionary<string, GamePlayer>(world.Players.Count);
                foreach (var (clientId, player) in world.Players)
                {
                    players[clientId] = player.Subject;
                }

                game.Players = players;
            }

            if (world.IsObjectSetChanged)
            {
                world.IsObjectSetChanged = false;
                var objects = new Dictionary<string, Abstractions.GameObject>(world.Objects.Count);
                foreach (var (name, entity) in world.Objects)
                {
                    objects[name] = entity.Subject;
                }

                game.Objects = objects;
            }
        }
        finally
        {
            IsPublishing = false;
        }

        return true;
    }
}
