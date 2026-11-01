using System.Collections.Immutable;
using HomeBlaze.Game.Abstractions;
using HomeBlaze.Game.Clients;
using HomeBlaze.Game.Voice;
using HomeBlaze.Game.World;

namespace HomeBlaze.Game.Simulation;

/// <summary>
/// The world of one game as its simulation owns it: players, objects, the chunk index and what changed since the last
/// publish and save. Touched only on the simulation thread, except <see cref="Snapshot"/> and
/// <see cref="PlayerPositions"/>, which any thread may read.
/// </summary>
internal sealed class GameWorldState
{
    /// <summary>
    /// The title of a player whose client has none.
    /// </summary>
    public const string DefaultPlayerTitle = "Player";

    /// <summary>
    /// The message with which an object of another game is refused.
    /// </summary>
    public const string NotPartOfGameMessage = "The object is not part of this game.";

    private readonly TimeProvider _timeProvider;
    private readonly GameVoiceMixer _voiceMixer;
    private readonly List<PlayerEntity> _pendingPublishes = [];
    private readonly HashSet<string> _changedPositions = [];
    private readonly List<GameArea> _targetTiles = new(3);

    // Objects added since the last publish of the object set; writes to them go unseen until the set attaches them.
    private readonly List<GameObject> _unpublishedObjects = [];

    // Objects placed or added by a replacement that the last publish of the object set attached, whose OnAddedToWorld
    // is due.
    private readonly List<GameObject> _publishedNewObjects = [];

    // Swapped in whole by the simulation thread and read by any thread; a published instance never changes.
    private volatile OccupancySnapshot _snapshot = OccupancySnapshot.Empty;
    private volatile ImmutableDictionary<string, PlayerPosition> _playerPositions = ImmutableDictionary<string, PlayerPosition>.Empty;

    public GameWorldState(TimeProvider timeProvider, GameVoiceMixer voiceMixer)
    {
        _timeProvider = timeProvider;
        _voiceMixer = voiceMixer;
    }

    /// <summary>
    /// The players by client id.
    /// </summary>
    public Dictionary<string, PlayerEntity> Players { get; } = new();

    /// <summary>
    /// The objects by name.
    /// </summary>
    public Dictionary<string, GameEntity> Objects { get; } = new();

    /// <summary>
    /// The entity of every player and object subject of the world.
    /// </summary>
    public Dictionary<GameObject, GameEntity> EntitiesBySubject { get; } = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// The name of every object subject of the world.
    /// </summary>
    public Dictionary<GameObject, string> NamesBySubject { get; } = new(ReferenceEqualityComparer.Instance);

    public GameWorldIndex Index { get; } = new();

    /// <summary>
    /// The subjects written since the last <see cref="RefreshChangedSubjects"/>, whose entities are read again.
    /// </summary>
    public HashSet<GameObject> ChangedSubjects { get; } = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Whether the set of objects changed since the last publish of <see cref="Game.Objects"/>.
    /// </summary>
    public bool IsObjectSetChanged { get; set; }

    /// <summary>
    /// Whether the set of players changed since the last publish of <see cref="Game.Players"/>.
    /// </summary>
    public bool IsPlayerSetChanged { get; set; }

    /// <summary>
    /// Whether something changed that the next save writes.
    /// </summary>
    public bool IsSaveRequested { get; set; }

    /// <summary>
    /// Whether a voice switch changed since the last volume table, which the index version does not show.
    /// </summary>
    public bool IsVoiceChanged { get; set; } = true;

    /// <summary>
    /// The players whose facade values changed since the last publish, removed players included, in the order they changed.
    /// </summary>
    public IReadOnlyList<PlayerEntity> PendingPublishes => _pendingPublishes;

    /// <summary>
    /// The movement blocking areas as of the last <see cref="PublishSnapshot"/>; any thread may read it.
    /// </summary>
    public OccupancySnapshot Snapshot => _snapshot;

    /// <summary>
    /// The objects of the world placed or added by a replacement that a publish of the object set attached since the
    /// game last cleared this list, in the order added. Removing an object takes it out; a take-over clears the list and
    /// reports the objects it keeps as added instead.
    /// </summary>
    public List<GameObject> PublishedNewObjects => _publishedNewObjects;

    /// <summary>
    /// The players by client id as of the last <see cref="PublishSnapshot"/>; any thread may read it.
    /// </summary>
    public ImmutableDictionary<string, PlayerPosition> PlayerPositions => _playerPositions;

    /// <summary>
    /// Returns the entity of an object or player subject of the world, or null.
    /// </summary>
    public GameEntity? FindEntity(GameObject subject) => EntitiesBySubject.GetValueOrDefault(subject);

    /// <summary>
    /// Makes the next publish write the player's facade values.
    /// </summary>
    public void RequestPublish(PlayerEntity player)
    {
        if (!player.IsPublishPending)
        {
            player.IsPublishPending = true;
            _pendingPublishes.Add(player);
        }
    }

    /// <summary>
    /// Reads the objects added since the last publish of the object set again in the next tick, because a write to them
    /// before the publish attached them to the game did not reach the write interceptor.
    /// </summary>
    public void OnObjectSetPublished()
    {
        foreach (var subject in _unpublishedObjects)
        {
            ChangedSubjects.Add(subject);
            _publishedNewObjects.Add(subject);
        }

        _unpublishedObjects.Clear();
    }

    /// <summary>
    /// Clears the pending publishes after the publisher wrote them.
    /// </summary>
    public void ClearPendingPublishes()
    {
        foreach (var player in _pendingPublishes)
        {
            player.IsPublishPending = false;
        }

        _pendingPublishes.Clear();
    }

    /// <summary>
    /// Takes over the objects as they are, without overlap checks: keeps the entities of subjects that stay, reads every
    /// subject again and indexes it. A subject listed under several names is taken once.
    /// </summary>
    /// <returns>The subjects that entered the world, including kept ones placed or added by a replacement whose
    /// <see cref="PublishedNewObjects"/> entry is still due, and those that left it.</returns>
    public (List<GameObject> Added, List<GameObject> Removed) TakeOverObjects(Dictionary<string, GameObject> objects)
    {
        var added = new List<GameObject>();
        var removed = new List<GameObject>();

        // The taken-over subjects come from a set written to the game, which attached them, so none waits for a publish.
        var pendingObjects = GetPendingNewObjects();
        _unpublishedObjects.Clear();
        _publishedNewObjects.Clear();
        var next = new Dictionary<string, GameEntity>(objects.Count);
        var names = new Dictionary<GameObject, string>(objects.Count, ReferenceEqualityComparer.Instance);
        foreach (var (name, subject) in objects)
        {
            // A player subject belongs to Players, and a subject under a second name is the same object.
            if (subject is null or GamePlayer || names.ContainsKey(subject))
            {
                continue;
            }

            if (!NamesBySubject.ContainsKey(subject) || !EntitiesBySubject.TryGetValue(subject, out var entity))
            {
                entity = new GameEntity(subject);
                added.Add(subject);
            }
            else if (pendingObjects?.Contains(subject) == true)
            {
                added.Add(subject);
            }

            next[name] = entity;
            names[subject] = name;
        }

        foreach (var entity in Objects.Values)
        {
            if (!names.ContainsKey(entity.Subject))
            {
                Index.Remove(entity);
                EntitiesBySubject.Remove(entity.Subject);
                removed.Add(entity.Subject);
            }
        }

        Objects.Clear();
        NamesBySubject.Clear();
        foreach (var (name, entity) in next)
        {
            Objects[name] = entity;
            NamesBySubject[entity.Subject] = name;
            EntitiesBySubject[entity.Subject] = entity;
            entity.ReadFromSubject();
            Index.AddOrUpdate(entity);
        }

        IsObjectSetChanged = true;
        return (added, removed);
    }

    /// <summary>
    /// Takes over the players as they are: keeps the entity of a subject that stays under its client id, and creates
    /// one at the subject's position and facing, disconnected and with voice off, for every other subject. Releases the
    /// voice of every entity that leaves. The players connect with the next <see cref="Reconcile"/>.
    /// </summary>
    /// <param name="players">The players by client id.</param>
    /// <param name="isRestart">Whether the game starts again after a stop: a kept entity then takes the subject's
    /// position and facing, which hold what the last publish wrote plus the writes made while the game was stopped, and
    /// turns its voice off, because the pages that switched it may have closed meanwhile.</param>
    public void TakeOverPlayers(Dictionary<string, GamePlayer> players, bool isRestart = false)
    {
        var keptEntities = GetKeptPlayers(players);

        // Leaving entities go first, so a subject that moved to another client id is published with its new entity last.
        foreach (var entity in Players.Values)
        {
            if (!keptEntities.Contains(entity))
            {
                RemovePlayerEntity(entity);
            }
        }

        var entities = new List<PlayerEntity>(players.Count);
        var subjects = new HashSet<GamePlayer>(ReferenceEqualityComparer.Instance);
        foreach (var (clientId, subject) in players)
        {
            if (IsTakeable(clientId, subject, subjects))
            {
                entities.Add(Players.TryGetValue(clientId, out var existing) && keptEntities.Contains(existing)
                    ? existing
                    : CreatePlayerFromSubject(subject, clientId));
            }
        }

        Players.Clear();
        foreach (var entity in entities)
        {
            AddTakenPlayer(entity, isRestart && keptEntities.Contains(entity));
        }

        IsPlayerSetChanged = true;
    }

    /// <summary>
    /// Brings the players in line with the clients: a client that is connected or named has a player, which joins on a
    /// free position; titles and connection follow the clients.
    /// </summary>
    /// <returns>The players that joined, or null when none did.</returns>
    public List<PlayerEntity>? Reconcile(IReadOnlyList<GameClient> clients)
    {
        RemoveStalePlayers(clients);
        foreach (var client in clients)
        {
            if (ShouldHavePlayer(client) && !Players.ContainsKey(client.Id))
            {
                AddPlayer(new GamePlayer(), client.Id);
            }
        }

        List<PlayerEntity>? joined = null;
        foreach (var client in clients)
        {
            if (Players.TryGetValue(client.Id, out var entity) && UpdatePlayer(entity, client))
            {
                (joined ??= []).Add(entity);
            }
        }

        return joined;
    }

    /// <summary>
    /// Turns the player toward <paramref name="direction"/> and, unless <paramref name="turnOnly"/>, steps one tile that
    /// way when the step is allowed. A step is checked only against movement blocking entities that do not already
    /// overlap the player, so a player can always step out of what it stands in.
    /// </summary>
    public MoveResult TryMove(PlayerEntity player, GameDirection direction, bool turnOnly)
    {
        if (!Enum.IsDefined(direction))
        {
            return CreateMoveResult(player, false);
        }

        var rateLimiter = turnOnly
            ? player.TurnRateLimiter ??= new RateLimiter(_timeProvider, Game.StepInterval, Game.StepBurst)
            : player.StepRateLimiter ??= new RateLimiter(_timeProvider, Game.StepInterval, Game.StepBurst);
        if (!rateLimiter.TryTake())
        {
            return CreateMoveResult(player, false);
        }

        if (player.Facing != direction)
        {
            player.Facing = direction;
            OnPositionChanged(player);
            Index.MarkChanged(player);
        }

        if (turnOnly || !TryGetStep(player, direction, out var moved))
        {
            return CreateMoveResult(player, turnOnly);
        }

        player.Area = moved;
        Index.AddOrUpdate(player);
        OnPositionChanged(player);
        return CreateMoveResult(player, true);
    }

    /// <summary>
    /// Moves the player of the subject to the position, without checks, as an outside write of its position asks.
    /// </summary>
    public void Teleport(GamePlayer subject, int? x, int? y)
    {
        if (FindEntity(subject) is not PlayerEntity player)
        {
            return;
        }

        var area = player.Area;
        player.Area = area with { X = x ?? area.X, Y = y ?? area.Y };
        Index.AddOrUpdate(player);
        OnPositionChanged(player);
    }

    /// <summary>
    /// Turns the player of the subject, as an outside write of its facing asks.
    /// </summary>
    public void Turn(GamePlayer subject, GameDirection facing)
    {
        if (FindEntity(subject) is not PlayerEntity player || player.Facing == facing)
        {
            return;
        }

        player.Facing = facing;
        Index.MarkChanged(player);
        OnPositionChanged(player);
    }

    /// <summary>
    /// Adds the visible entities other than the player in the tiles in front of it to <paramref name="results"/>, which
    /// must be empty, from south-west to north-east.
    /// </summary>
    public void GetTargets(PlayerEntity player, List<GameEntity> results) => TargetArea.GetTargets(Index, player, _targetTiles, results);

    /// <summary>
    /// Adds the object under a new name.
    /// </summary>
    /// <exception cref="ArgumentException">The object is already part of the world or has an invalid size.</exception>
    /// <exception cref="InvalidOperationException">The object blocks movement and a movement blocking entity covers its area.</exception>
    public void Place(GameObject gameObject)
    {
        var entity = CreateObjectEntity(gameObject, nameof(gameObject));
        if (entity.IsMovementBlocking && Index.IsBlocked(entity.Area, ignored: null))
        {
            throw new InvalidOperationException(GameMessages.Blocked);
        }

        AddObject(entity);
        IsObjectSetChanged = true;
        IsSaveRequested = true;
    }

    /// <exception cref="InvalidOperationException">The object is not part of the world.</exception>
    public void Remove(GameObject gameObject)
    {
        if (!NamesBySubject.ContainsKey(gameObject))
        {
            throw new InvalidOperationException(NotPartOfGameMessage);
        }

        RemoveObject(gameObject);
        IsObjectSetChanged = true;
        IsSaveRequested = true;
    }

    /// <summary>
    /// Replaces the object by the replacements under new names in one step; changes nothing when refused.
    /// </summary>
    /// <exception cref="ArgumentException">A replacement is null, listed twice, already part of the world or of invalid size.</exception>
    /// <exception cref="InvalidOperationException">The object is not part of the world, or a movement blocking replacement
    /// overlaps a movement blocking entity other than the replaced one or another replacement.</exception>
    public void Replace(GameObject gameObject, IReadOnlyList<GameObject> replacements)
    {
        if (!NamesBySubject.ContainsKey(gameObject))
        {
            throw new InvalidOperationException(NotPartOfGameMessage);
        }

        var replaced = EntitiesBySubject[gameObject];
        var entities = new GameEntity[replacements.Count];
        for (var i = 0; i < replacements.Count; i++)
        {
            var entity = CreateObjectEntity(replacements[i], nameof(replacements));
            if (entity.IsMovementBlocking && Index.IsBlocked(entity.Area, replaced))
            {
                throw new InvalidOperationException(GameMessages.Blocked);
            }

            ValidateAgainstEarlierReplacements(entities, i, entity, nameof(replacements));
            entities[i] = entity;
        }

        RemoveObject(gameObject);
        foreach (var entity in entities)
        {
            AddObject(entity);
        }

        IsObjectSetChanged = true;
        IsSaveRequested = true;
    }

    /// <summary>
    /// Reads every changed object subject of the world again and indexes it; a changed object is saved with the next save.
    /// </summary>
    public void RefreshChangedSubjects()
    {
        if (ChangedSubjects.Count == 0)
        {
            return;
        }

        foreach (var subject in ChangedSubjects)
        {
            RefreshSubject(subject);
        }

        ChangedSubjects.Clear();
    }

    /// <summary>
    /// Reads the subject again and indexes it when it is an object of the world; a changed object is saved with the next save.
    /// </summary>
    public void RefreshSubject(GameObject subject)
    {
        if (NamesBySubject.ContainsKey(subject) && EntitiesBySubject.TryGetValue(subject, out var entity))
        {
            entity.ReadFromSubject();
            Index.AddOrUpdate(entity);
            IsSaveRequested = true;
        }
    }

    /// <summary>
    /// Publishes <see cref="Snapshot"/> and <see cref="PlayerPositions"/> for the changes since the last call; reads
    /// <see cref="GameWorldIndex.Changes"/> without clearing it.
    /// </summary>
    public void PublishSnapshot()
    {
        _snapshot = _snapshot.With(Index, Index.Changes);
        if (_changedPositions.Count == 0)
        {
            return;
        }

        var builder = _playerPositions.ToBuilder();
        foreach (var clientId in _changedPositions)
        {
            if (Players.TryGetValue(clientId, out var player))
            {
                builder[clientId] = new PlayerPosition(player.Subject, player.Area, player.Facing, player.IsConnected);
            }
            else
            {
                builder.Remove(clientId);
            }
        }

        _changedPositions.Clear();
        _playerPositions = builder.ToImmutable();
    }

    /// <summary>
    /// Turns the voice of the player off and releases its talker in the mixer.
    /// </summary>
    public void ResetVoice(PlayerEntity player)
    {
        if (player.MicrophonePageId is null && player.AudioPageIds is not { Count: > 0 })
        {
            return;
        }

        player.MicrophonePageId = null;
        player.AudioPageIds?.Clear();
        player.IsMicrophoneOn = false;
        player.IsAudioOn = false;
        _voiceMixer.SetMicrophonePage(player.ClientId, null);
        OnVoiceChanged(player);
    }

    /// <summary>
    /// Records a change of the player's voice switches for the volume table, the views and the next publish.
    /// </summary>
    public void OnVoiceChanged(PlayerEntity player)
    {
        IsVoiceChanged = true;
        Index.MarkChanged(player);
        RequestPublish(player);
    }

    private static bool ShouldHavePlayer(GameClient client) => client.IsNamed || client.IsConnected;

    // A subject listed under a second client id is taken under the first only.
    private static bool IsTakeable(string clientId, GamePlayer? subject, HashSet<GamePlayer> subjects) =>
        subject is not null && !string.IsNullOrEmpty(clientId) && subjects.Add(subject);

    private static MoveResult CreateMoveResult(PlayerEntity player, bool isAccepted) =>
        new(isAccepted, player.Area.X, player.Area.Y, player.Facing);

    private static void ValidateAgainstEarlierReplacements(GameEntity[] entities, int position, GameEntity entity, string parameterName)
    {
        for (var i = 0; i < position; i++)
        {
            var earlier = entities[i];
            if (ReferenceEquals(earlier.Subject, entity.Subject))
            {
                throw new ArgumentException("A replacement is listed twice.", parameterName);
            }

            if (earlier.IsMovementBlocking && entity.IsMovementBlocking && earlier.Area.Intersects(entity.Area))
            {
                throw new InvalidOperationException(GameMessages.Blocked);
            }
        }
    }

    // The type name in lowercase, a dash and 8 random hex digits.
    private string CreateObjectName(GameObject gameObject)
    {
        var prefix = gameObject.GetType().Name.ToLowerInvariant();
        string name;
        do
        {
            name = $"{prefix}-{Guid.NewGuid():N}"[..(prefix.Length + 9)];
        }
        while (Objects.ContainsKey(name));

        return name;
    }

    private void RemoveStalePlayers(IReadOnlyList<GameClient> clients)
    {
        var clientsById = new Dictionary<string, GameClient>(clients.Count);
        foreach (var client in clients)
        {
            clientsById[client.Id] = client;
        }

        List<string>? stale = null;
        foreach (var clientId in Players.Keys)
        {
            if (!clientsById.TryGetValue(clientId, out var client) || !ShouldHavePlayer(client))
            {
                (stale ??= []).Add(clientId);
            }
        }

        foreach (var clientId in stale ?? [])
        {
            Players.Remove(clientId, out var entity);
            RemovePlayerEntity(entity!);
            IsPlayerSetChanged = true;
            IsSaveRequested = true;
        }
    }

    // The subject gets its client id before it is published, because the id is its key in Players.
    private void AddPlayer(GamePlayer subject, string clientId)
    {
        subject.ClientId = clientId;
        var entity = new PlayerEntity(subject, clientId) { Facing = subject.Facing, Title = DefaultPlayerTitle };
        Players[clientId] = entity;
        EntitiesBySubject[subject] = entity;
        RequestPublish(entity);
        IsPlayerSetChanged = true;
        IsSaveRequested = true;
    }

    private PlayerEntity CreatePlayerFromSubject(GamePlayer subject, string clientId)
    {
        if (subject.ClientId != clientId)
        {
            subject.ClientId = clientId;
        }

        var entity = new PlayerEntity(subject, clientId)
        {
            Area = new GameArea(subject.X, subject.Y, GamePlayer.Size, GamePlayer.Size),
            Facing = subject.Facing,
            Title = DefaultPlayerTitle
        };
        RequestPublish(entity);
        return entity;
    }

    // The entities whose subject stays under the same client id.
    private HashSet<PlayerEntity> GetKeptPlayers(Dictionary<string, GamePlayer> players)
    {
        var subjects = new HashSet<GamePlayer>(ReferenceEqualityComparer.Instance);
        var kept = new HashSet<PlayerEntity>(ReferenceEqualityComparer.Instance);
        foreach (var (clientId, subject) in players)
        {
            if (IsTakeable(clientId, subject, subjects) && Players.TryGetValue(clientId, out var existing) && ReferenceEquals(existing.Subject, subject))
            {
                kept.Add(existing);
            }
        }

        return kept;
    }

    private void AddTakenPlayer(PlayerEntity player, bool isKeptOverRestart)
    {
        if (isKeptOverRestart)
        {
            ReadPosition(player);
            ResetVoice(player);
        }

        Players[player.ClientId] = player;
        EntitiesBySubject[player.Subject] = player;
        Index.AddOrUpdate(player);
        _changedPositions.Add(player.ClientId);
    }

    private void ReadPosition(PlayerEntity player)
    {
        var subject = player.Subject;
        var area = player.Area with { X = subject.X, Y = subject.Y };
        if (area != player.Area || subject.Facing != player.Facing)
        {
            player.Area = area;
            player.Facing = subject.Facing;
            Index.MarkChanged(player);
            OnPositionChanged(player);
        }
    }

    // Returns whether the player joined.
    private bool UpdatePlayer(PlayerEntity player, GameClient client)
    {
        var isJoining = client.IsConnected && !player.IsConnected;
        if (player.ClientTitle != client.Title)
        {
            player.ClientTitle = client.Title;
            player.Title = string.IsNullOrEmpty(client.Title) ? DefaultPlayerTitle : client.Title;
            RequestPublish(player);
        }

        if (player.IsConnected != client.IsConnected)
        {
            player.SetConnected(client.IsConnected);
            OnPositionChanged(player);
        }

        if (!client.IsConnected)
        {
            ResetVoice(player);
        }

        if (isJoining)
        {
            player.Area = SpawnFinder.FindFreePosition(player.Area, area => !Index.IsBlocked(area, player));
            OnPositionChanged(player);
        }

        Index.AddOrUpdate(player);
        return isJoining;
    }

    // Removes the entity of a player that left or whose subject was replaced; its subject shows it disconnected after
    // the next publish.
    private void RemovePlayerEntity(PlayerEntity player)
    {
        Index.Remove(player);
        if (EntitiesBySubject.TryGetValue(player.Subject, out var current) && ReferenceEquals(current, player))
        {
            EntitiesBySubject.Remove(player.Subject);
        }

        ResetVoice(player);
        if (player.IsConnected)
        {
            player.SetConnected(false);
            RequestPublish(player);
        }

        _changedPositions.Add(player.ClientId);
    }

    private void OnPositionChanged(PlayerEntity player)
    {
        RequestPublish(player);
        _changedPositions.Add(player.ClientId);
        IsSaveRequested = true;
    }

    private bool TryGetStep(PlayerEntity player, GameDirection direction, out GameArea moved)
    {
        var area = player.Area;
        var offsetX = direction.GetOffsetX();
        var offsetY = direction.GetOffsetY();
        if (!area.TryMove(offsetX, offsetY, out moved) || Index.IsBlockedExceptOverlapping(moved, player))
        {
            return false;
        }

        if (!direction.IsDiagonal())
        {
            return true;
        }

        var isHorizontalBlocked = !area.TryMove(offsetX, 0, out var horizontal) || Index.IsBlockedExceptOverlapping(horizontal, player);
        var isVerticalBlocked = !area.TryMove(0, offsetY, out var vertical) || Index.IsBlockedExceptOverlapping(vertical, player);
        return !(isHorizontalBlocked && isVerticalBlocked);
    }

    private GameEntity CreateObjectEntity(GameObject? gameObject, string parameterName)
    {
        if (gameObject is null)
        {
            throw new ArgumentNullException(parameterName);
        }

        if (EntitiesBySubject.ContainsKey(gameObject))
        {
            throw new ArgumentException("The object is already part of this game.", parameterName);
        }

        var entity = new GameEntity(gameObject);
        entity.ReadFromSubject();
        if (!GameWorldIndex.HasValidSize(entity.Area, entity.Height))
        {
            throw new ArgumentException($"The width, depth and height of an object are from 1 to {GameObject.MaximumSize} tiles.", parameterName);
        }

        return entity;
    }

    private HashSet<GameObject>? GetPendingNewObjects()
    {
        if (_unpublishedObjects.Count == 0 && _publishedNewObjects.Count == 0)
        {
            return null;
        }

        var pendingObjects = new HashSet<GameObject>(_unpublishedObjects, ReferenceEqualityComparer.Instance);
        pendingObjects.UnionWith(_publishedNewObjects);
        return pendingObjects;
    }

    private void AddObject(GameEntity entity)
    {
        _unpublishedObjects.Add(entity.Subject);
        var name = CreateObjectName(entity.Subject);
        Objects[name] = entity;
        NamesBySubject[entity.Subject] = name;
        EntitiesBySubject[entity.Subject] = entity;
        Index.AddOrUpdate(entity);
    }

    private void RemoveObject(GameObject gameObject)
    {
        // Both usually empty: the game empties them with the next publish and the tick after it.
        _unpublishedObjects.Remove(gameObject);
        _publishedNewObjects.Remove(gameObject);
        NamesBySubject.Remove(gameObject, out var name);
        EntitiesBySubject.Remove(gameObject, out var entity);
        Objects.Remove(name!);
        Index.Remove(entity!);
    }

    /// <summary>
    /// A player's position as threads other than the simulation's read it.
    /// </summary>
    public readonly record struct PlayerPosition(GamePlayer Subject, GameArea Area, GameDirection Facing, bool IsConnected);
}
