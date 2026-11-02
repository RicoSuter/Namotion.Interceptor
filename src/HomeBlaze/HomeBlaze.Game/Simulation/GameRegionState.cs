namespace HomeBlaze.Game.Simulation;

/// <summary>
/// The simulation's view of a region subject: the objects of the world in it and what changed since the last publish of
/// the object set. Touched only on the simulation thread.
/// </summary>
internal sealed class GameRegionState
{
    public GameRegionState(string key, int x, int y, GameRegion subject, bool isStray)
    {
        Key = key;
        X = x;
        Y = y;
        Subject = subject;
        IsStray = isStray;
    }

    /// <summary>
    /// The key of the region in <see cref="Game.Regions"/>.
    /// </summary>
    public string Key { get; }

    public int X { get; }

    public int Y { get; }

    public GameRegion Subject { get; }

    /// <summary>
    /// Whether the region is listed under a key that names no region, or listed twice; it never holds objects and the next
    /// publish removes it.
    /// </summary>
    public bool IsStray { get; }

    /// <summary>
    /// The objects of the world in the region by name.
    /// </summary>
    public Dictionary<string, GameEntity> Objects { get; } = new();

    /// <summary>
    /// Whether the last write of <see cref="Game.Regions"/> holds the region.
    /// </summary>
    public bool IsPublished { get; set; }

    /// <summary>
    /// The <see cref="GameRegionMap.ChangeCount"/> when the region last changed.
    /// </summary>
    public long LastChange { get; set; }

    /// <summary>
    /// Whether the next publish writes the region's objects, or removes the region once it is empty.
    /// </summary>
    public bool IsChanged { get; set; }

    /// <summary>
    /// The objects of the world that the region's subject may still hold although they are in another region now, by
    /// their names; null when there are none.
    /// </summary>
    public List<(string Name, GameEntity Entity)>? Leaving { get; set; }

    /// <summary>
    /// Whether the entity is in the world and in another region that changed after <paramref name="changedAfter"/>, so a
    /// write of this region keeps it until the write of its region holds it.
    /// </summary>
    public bool IsLeaving(GameEntity entity, long changedAfter = long.MinValue) =>
        entity.Region is { } region && !ReferenceEquals(region, this) && region.LastChange > changedAfter;
}
