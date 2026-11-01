namespace HomeBlaze.Game.Simulation;

/// <summary>
/// Writes the simulation's state to the facade subjects: the changed players' values and the player set of the game at
/// most every <see cref="GameSimulationSettings.PublishInterval"/>, and the object set of the game whenever it changed.
/// Used only on the simulation thread.
/// </summary>
internal sealed class GamePublisher(Game game, GameWorldState world, TimeProvider timeProvider)
{
    private long _lastPublishTimestamp;
    private bool _hasPublished;

    // The write the publisher makes right now, which the write interceptor ignores; a write that an observer makes
    // meanwhile has another subject, property or set and counts.
    private object? _writingSubject;
    private string? _writingProperty;
    private object? _writingSet;

    private long _version;

    /// <summary>
    /// Whether publishing waits, as it does while a save writes the subjects.
    /// </summary>
    public bool IsPaused { get; set; }

    /// <summary>
    /// Increases with every publish that wrote something; any thread may read it.
    /// </summary>
    public long Version => Volatile.Read(ref _version);

    /// <summary>
    /// Whether the write is the one the publisher makes right now, of a player's value or of a set of the game.
    /// </summary>
    public bool IsOwnWrite(object subject, string property, object? set) =>
        ReferenceEquals(subject, _writingSubject) && property == _writingProperty && ReferenceEquals(set, _writingSet);

    /// <summary>
    /// Publishes everything when <see cref="GameSimulationSettings.PublishInterval"/> passed since the last publish, and
    /// otherwise a changed object set only, unless paused.
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
            return TryPublishObjectSet();
        }

        return Publish();
    }

    /// <summary>
    /// Writes the object set when it changed, unless paused, so that placed objects are attached to the game in the tick
    /// that adds them.
    /// </summary>
    /// <returns>Whether the set was written.</returns>
    public bool TryPublishObjectSet()
    {
        if (IsPaused || !world.IsObjectSetChanged)
        {
            return false;
        }

        try
        {
            WriteObjectSet();
        }
        finally
        {
            SetWriting(null, null);
            Interlocked.Increment(ref _version);
        }

        return true;
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
        try
        {
            foreach (var player in world.PendingPublishes)
            {
                var subject = player.Subject;
                SetWriting(subject, nameof(GamePlayer.X));
                subject.X = player.Area.X;
                SetWriting(subject, nameof(GamePlayer.Y));
                subject.Y = player.Area.Y;
                SetWriting(subject, nameof(GamePlayer.Facing));
                subject.Facing = player.Facing;
                SetWriting(subject, nameof(GamePlayer.IsConnected));
                subject.IsConnected = player.IsConnected;
                SetWriting(subject, nameof(GamePlayer.ClientTitle));
                subject.ClientTitle = player.ClientTitle;
                SetWriting(subject, nameof(GamePlayer.IsMicrophoneOn));
                subject.IsMicrophoneOn = player.IsMicrophoneOn;
                SetWriting(subject, nameof(GamePlayer.IsAudioOn));
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

                SetWriting(game, nameof(Game.Players), players);
                game.Players = players;
            }

            if (world.IsObjectSetChanged)
            {
                WriteObjectSet();
            }
        }
        finally
        {
            SetWriting(null, null);
            Interlocked.Increment(ref _version);
        }

        return true;
    }

    private void WriteObjectSet()
    {
        world.IsObjectSetChanged = false;
        var objects = new Dictionary<string, Abstractions.GameObject>(world.Objects.Count);
        foreach (var (name, entity) in world.Objects)
        {
            objects[name] = entity.Subject;
        }

        SetWriting(game, nameof(Game.Objects), objects);
        game.Objects = objects;
        world.OnObjectSetPublished();
    }

    private void SetWriting(object? subject, string? property, object? set = null)
    {
        _writingSubject = subject;
        _writingProperty = property;
        _writingSet = set;
    }
}
