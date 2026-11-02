namespace HomeBlaze.Game.Simulation;

/// <summary>
/// Writes the simulation's state to the facade subjects: the changed players' values and the player set of the game at
/// most every <see cref="GameSimulationSettings.PublishInterval"/>, and the changed regions and the region set of the
/// game, together the object set, in the tick they changed. Used only on the simulation thread.
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
    /// Whether the write is the one the publisher makes right now, of a player's value, a set of the game or the objects
    /// of a region.
    /// </summary>
    public bool IsOwnWrite(object subject, string property, object? set) =>
        ReferenceEquals(subject, _writingSubject) && property == _writingProperty && ReferenceEquals(set, _writingSet);

    /// <summary>
    /// Counts the publishes that wrote every change of the object set; touched only on the simulation thread.
    /// </summary>
    public long ObjectSetVersion { get; private set; }

    /// <summary>
    /// Publishes the players' values and the player set when <see cref="GameSimulationSettings.PublishInterval"/> passed
    /// since the last publish, unless paused; the object set is left to <see cref="TryPublishObjectSet"/>.
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

        return Publish(includesObjectSet: false);
    }

    /// <summary>
    /// Writes the changed regions and the region set when they changed, unless paused, so that placed objects are attached
    /// to the game in the tick that adds them.
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
    public bool Publish() => Publish(includesObjectSet: true);

    private bool Publish(bool includesObjectSet)
    {
        var isObjectSetDue = includesObjectSet && world.IsObjectSetChanged;
        if (world.PendingPublishes.Count == 0 && !world.IsPlayerSetChanged && !isObjectSetDue)
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

            if (isObjectSetDue)
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

    // Writes the changed regions in an order that keeps an object that moved to another region attached throughout: the
    // regions with new objects first, holding on to the objects that left them, then the region set with the new regions,
    // then the regions without the objects that left them, and last the region set without the emptied regions. The
    // changes are cleared only once every write went through, so a write that an observer fails is retried next tick.
    private void WriteObjectSet()
    {
        var regions = world.Regions;
        var hasNewRegions = false;
        var hasLeavingObjects = false;
        var hasRemovedRegions = regions.Strays.Count > 0 || regions.IsRegionSetWriteRequested;
        foreach (var region in regions.Changed)
        {
            if (region.Objects.Count == 0)
            {
                hasRemovedRegions |= region.IsPublished;
                continue;
            }

            hasNewRegions |= !region.IsPublished;
            hasLeavingObjects |= WriteRegion(region, includesLeaving: true);
        }

        if (hasNewRegions)
        {
            WriteRegionSet(includesRemoved: hasRemovedRegions);
        }

        if (hasLeavingObjects)
        {
            foreach (var region in regions.Changed)
            {
                if (region.Objects.Count > 0 && CountLeaving(region) > 0)
                {
                    WriteRegion(region, includesLeaving: false);
                }
            }
        }

        if (hasRemovedRegions)
        {
            WriteRegionSet(includesRemoved: false);
        }

        regions.OnPublished();
        ObjectSetVersion++;
        world.OnObjectSetPublished();
    }

    private static int CountLeaving(GameRegionState region)
    {
        if (region.Leaving is not { } leaving)
        {
            return 0;
        }

        var count = 0;
        foreach (var (_, entity) in leaving)
        {
            count += region.IsLeaving(entity) ? 1 : 0;
        }

        return count;
    }

    // Returns whether the write held on to objects that left the region.
    private bool WriteRegion(GameRegionState region, bool includesLeaving)
    {
        var leaving = includesLeaving ? region.Leaving : null;
        var leavingCount = leaving is null ? 0 : CountLeaving(region);
        var objects = new Dictionary<string, Abstractions.GameObject>(region.Objects.Count + leavingCount);
        foreach (var (name, entity) in region.Objects)
        {
            objects[name] = entity.Subject;
        }

        if (leaving is not null && leavingCount > 0)
        {
            foreach (var (name, entity) in leaving)
            {
                if (region.IsLeaving(entity))
                {
                    objects[name] = entity.Subject;
                }
            }
        }

        var subject = region.Subject;
        SetWriting(subject, nameof(GameRegion.Objects), objects);
        subject.Objects = objects;
        return leavingCount > 0;
    }

    // With the removed regions, the set also holds the published regions that are empty now and the strays.
    private void WriteRegionSet(bool includesRemoved)
    {
        var regions = world.Regions;
        var set = new Dictionary<string, GameRegion>(regions.Regions.Count + (includesRemoved ? regions.Strays.Count : 0));
        foreach (var region in regions.Regions.Values)
        {
            if (region.Objects.Count > 0 || (includesRemoved && region.IsPublished))
            {
                set[region.Key] = region.Subject;
            }
        }

        if (includesRemoved)
        {
            foreach (var stray in regions.Strays)
            {
                set.TryAdd(stray.Key, stray.Subject);
            }
        }

        SetWriting(game, nameof(Game.Regions), set);
        game.Regions = set;
    }

    private void SetWriting(object? subject, string? property, object? set = null)
    {
        _writingSubject = subject;
        _writingProperty = property;
        _writingSet = set;
    }
}
