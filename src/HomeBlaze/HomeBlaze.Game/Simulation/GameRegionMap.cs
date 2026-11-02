namespace HomeBlaze.Game.Simulation;

/// <summary>
/// The regions of one game as its simulation owns them: which region holds each object of the world, and which regions
/// the next publish of the object set writes. Touched only on the simulation thread.
/// </summary>
internal sealed class GameRegionMap
{
    private readonly Dictionary<(int X, int Y), GameRegionState> _regions = new();
    private readonly Dictionary<GameRegion, GameRegionState> _regionsBySubject = new(ReferenceEqualityComparer.Instance);
    private readonly List<GameRegionState> _changed = [];
    private readonly List<GameRegionState> _strays = [];
    private bool _isRegionSetWriteRequested;

    /// <summary>
    /// The regions by their coordinates, including emptied ones the next publish removes.
    /// </summary>
    public IReadOnlyDictionary<(int X, int Y), GameRegionState> Regions => _regions;

    /// <summary>
    /// The regions whose objects changed since the last publish of the object set, in the order they changed.
    /// </summary>
    public IReadOnlyList<GameRegionState> Changed => _changed;

    /// <summary>
    /// The regions listed under a key that names no region, or listed twice, which the next publish removes.
    /// </summary>
    public IReadOnlyList<GameRegionState> Strays => _strays;

    /// <summary>
    /// Whether the next publish writes <see cref="Game.Regions"/> although no region was added or emptied, as it does
    /// after a take-over, whose set a publish in between may have overwritten.
    /// </summary>
    public bool IsRegionSetWriteRequested => _isRegionSetWriteRequested;

    /// <summary>
    /// Whether the object set changed since its last publish.
    /// </summary>
    public bool IsChanged => _changed.Count > 0 || _strays.Count > 0 || _isRegionSetWriteRequested;

    /// <summary>
    /// Returns the region of a region subject of the world, or null.
    /// </summary>
    public GameRegionState? Find(GameRegion subject) => _regionsBySubject.GetValueOrDefault(subject);

    /// <summary>
    /// Puts the object into the region of its area and marks that region changed.
    /// </summary>
    public void Add(string name, GameEntity entity) => MarkChanged(Assign(name, entity));

    /// <summary>
    /// Takes the object out of its region and marks that region changed.
    /// </summary>
    public void Remove(string name, GameEntity entity)
    {
        var region = entity.Region!;
        region.Objects.Remove(name);
        entity.Region = null;
        MarkChanged(region);
    }

    /// <summary>
    /// Moves the object to the region of its area when it moved across a region boundary; both regions change, and the
    /// former keeps the object until the next publish wrote the new one.
    /// </summary>
    public void Update(string name, GameEntity entity)
    {
        var region = entity.Region!;
        var (x, y) = GameRegion.GetCoordinates(entity.Area.X, entity.Area.Y);
        if (region.X == x && region.Y == y)
        {
            return;
        }

        region.Objects.Remove(name);
        AddLeaving(region, name, entity);
        MarkChanged(region);
        Add(name, entity);
    }

    /// <summary>
    /// Forgets every region, as a take-over of the whole set does before it adds the taken-over ones.
    /// </summary>
    public void Clear()
    {
        _regions.Clear();
        _regionsBySubject.Clear();
        _changed.Clear();
        _strays.Clear();
    }

    /// <summary>
    /// Adds a region subject of a taken-over set as published under its key: as a region when the key names one and no
    /// other key listed the subject, otherwise as a stray.
    /// </summary>
    /// <returns>The region, or null when the subject was listed before, whose objects are taken already.</returns>
    public GameRegionState? AddPublished(string key, GameRegion subject)
    {
        var isListedBefore = _regionsBySubject.ContainsKey(subject);
        if (isListedBefore || !GameRegion.TryParseKey(key, out var x, out var y))
        {
            var stray = new GameRegionState(key, 0, 0, subject, isStray: true) { IsPublished = true };
            _strays.Add(stray);
            if (isListedBefore)
            {
                return null;
            }

            _regionsBySubject[subject] = stray;
            return stray;
        }

        var region = new GameRegionState(key, x, y, subject, isStray: false) { IsPublished = true };
        _regions[(x, y)] = region;
        _regionsBySubject[subject] = region;
        return region;
    }

    /// <summary>
    /// Puts the object into the region of its area, creating the region when there is none; only a created region is
    /// marked changed.
    /// </summary>
    public GameRegionState Assign(string name, GameEntity entity)
    {
        var coordinates = GameRegion.GetCoordinates(entity.Area.X, entity.Area.Y);
        if (!_regions.TryGetValue(coordinates, out var region))
        {
            region = new GameRegionState(GameRegion.GetKey(coordinates.X, coordinates.Y), coordinates.X, coordinates.Y, new GameRegion(), isStray: false);
            _regions[coordinates] = region;
            _regionsBySubject[region.Subject] = region;
            MarkChanged(region);
        }

        region.Objects[name] = entity;
        entity.Region = region;
        return region;
    }

    /// <summary>
    /// Records that the region's subject holds the object of the world although it is in another region now.
    /// </summary>
    public static void AddLeaving(GameRegionState region, string name, GameEntity entity) => (region.Leaving ??= []).Add((name, entity));

    /// <summary>
    /// Makes the next publish write the region's objects, or remove it once it is empty; a stray is removed anyway.
    /// </summary>
    public void MarkChanged(GameRegionState region)
    {
        if (!region.IsChanged && !region.IsStray)
        {
            region.IsChanged = true;
            _changed.Add(region);
        }
    }

    /// <summary>
    /// Makes the next publish write <see cref="Game.Regions"/>.
    /// </summary>
    public void RequestRegionSetWrite() => _isRegionSetWriteRequested = true;

    /// <summary>
    /// Records that a publish wrote every changed region and the region set: drops the emptied regions and the strays.
    /// </summary>
    public void OnPublished()
    {
        foreach (var region in _changed)
        {
            region.IsChanged = false;
            region.Leaving = null;
            if (region.Objects.Count > 0)
            {
                region.IsPublished = true;
            }
            else
            {
                _regions.Remove((region.X, region.Y));
                _regionsBySubject.Remove(region.Subject);
            }
        }

        foreach (var stray in _strays)
        {
            // A subject listed twice is registered under its first key, which stays.
            if (_regionsBySubject.TryGetValue(stray.Subject, out var registered) && ReferenceEquals(registered, stray))
            {
                _regionsBySubject.Remove(stray.Subject);
            }
        }

        _changed.Clear();
        _strays.Clear();
        _isRegionSetWriteRequested = false;
    }
}
