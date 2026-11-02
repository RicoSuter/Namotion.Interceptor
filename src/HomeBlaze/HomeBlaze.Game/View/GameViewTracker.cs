using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HomeBlaze.Game.Abstractions;
using HomeBlaze.Game.Simulation;
using HomeBlaze.Game.World;

namespace HomeBlaze.Game.View;

/// <summary>
/// Remembers what one page knows of the entities in its window and turns the changed entities and the chunks that enter
/// the window into the changes the page lacks. Not thread-safe: only the simulation thread uses it.
/// </summary>
internal sealed class GameViewTracker
{
    /// <summary>
    /// The tiles in each direction a page sees, 50 m.
    /// </summary>
    public const int ViewRadius = 200;

    private const int MaximumPooledLists = 64;

    // Bounds the first allocation of an upserted list, as most changed entities near a window are not in it.
    private const int MaximumUpsertedCapacity = 64;

    /// <summary>
    /// The update without changes; shared, so an unchanged view allocates nothing.
    /// </summary>
    public static readonly GameViewUpdate Empty = new([], []);

    // Per known entity what the page was sent of it and the number of window chunks whose list holds it; it leaves the
    // view at zero. Updated in place through refs, which no add or remove of an entry may outlive.
    private readonly Dictionary<GameEntity, KnownEntity> _known = new(ReferenceEqualityComparer.Instance);

    // The known entities of each window chunk: those whose known area covers it. Chunks without any have no list.
    private readonly Dictionary<(long X, long Y), List<GameEntity>> _chunks = new();
    private readonly Stack<List<GameEntity>> _pooledLists = new();

    // Scratch space of one update.
    private readonly List<GameEntity> _queried = [];
    private readonly HashSet<GameEntity> _previous = new(ReferenceEqualityComparer.Instance);
    private readonly List<GameEntity> _uncounted = [];
    private List<GameViewEntry>? _upserted;

    // The ids of the entities known before a reset, which the next update removes.
    private List<string>? _resetIds;

    // The initial capacity of the upserted list of an update, so one that upserts each of a few changed entities does
    // not grow it.
    private int _upsertedCapacity;

    private int _nextId;
    private bool _hasWindow;

    /// <summary>
    /// The window of the last update; meaningless before the first.
    /// </summary>
    public GameViewWindow Window { get; private set; }

    /// <summary>
    /// Whether an update ran, so <see cref="Window"/> holds its window.
    /// </summary>
    public bool HasWindow => _hasWindow;

    /// <summary>
    /// The number of entities the page knows.
    /// </summary>
    public int Count => _known.Count;

    /// <summary>
    /// The number of times an entity was compared with what the page knows of it, over all updates.
    /// </summary>
    public long ComparisonCount { get; private set; }

    /// <summary>
    /// Forgets everything the page knows, after an update that may have been left half done or did not reach the page;
    /// the next update removes the former ids and reads every chunk of its window, adding the entities with new ids.
    /// </summary>
    public void Reset()
    {
        _resetIds ??= [];
        foreach (var known in _known.Values)
        {
            _resetIds.Add(known.Id);
        }

        _known.Clear();
        foreach (var list in _chunks.Values)
        {
            list.Clear();
            if (_pooledLists.Count < MaximumPooledLists)
            {
                _pooledLists.Push(list);
            }
        }

        _chunks.Clear();
        _queried.Clear();
        _previous.Clear();
        _uncounted.Clear();
        _upserted = null;
        _hasWindow = false;
    }

    /// <summary>
    /// Brings the page in line with the changed entities inside the window and the visible entities of the chunks that
    /// entered the window, forgets the chunks that left it, and returns what changed. The first update, a full refresh
    /// and a window that moved by more than one chunk read every chunk of the window instead of the changed entities.
    /// </summary>
    /// <param name="index">The chunk index to read the entities from.</param>
    /// <param name="changes">The entities changed since the last update, each with the indexed area and visibility it had
    /// at the last update; entities outside the window are skipped.</param>
    /// <param name="window">The window the page sees now.</param>
    /// <param name="self">The page's own player, or null.</param>
    /// <param name="isFullRefresh">Whether to read every chunk of the window, as after a change of the own player.</param>
    /// <param name="payloads">Gives the payloads of the upserted entities, shared with the other pages.</param>
    public GameViewUpdate Update(GameWorldIndex index, ReadOnlySpan<GameEntityChange> changes, GameViewWindow window, PlayerEntity? self,
        bool isFullRefresh, GameViewPayloads payloads)
    {
        var previousWindow = Window;
        var hadWindow = _hasWindow;
        isFullRefresh |= !hadWindow || window.IsFarFrom(previousWindow);
        Window = window;
        _hasWindow = true;
        _upserted = null;
        _upsertedCapacity = 0;
        var context = new RefreshContext(index, self, payloads);
        if (hadWindow && previousWindow != window)
        {
            // The chunks of the former window outside the new one.
            ForEachChunkOutside(previousWindow, window, isEntering: false, context);
        }

        if (isFullRefresh)
        {
            RefreshAll(window, context);
        }
        else
        {
            SyncChangedAndEntering(changes, window, previousWindow, context);
        }

        var upserted = _upserted;
        _upserted = null;
        var removed = RemoveUncounted();
        return upserted is null && removed is null ? Empty : new GameViewUpdate(upserted ?? [], removed ?? []);
    }

    // The changed entities first, within the chunks of both windows, which the page knows; the entering chunks are read
    // whole after them, so an entity that moved into one of them is counted there once.
    private void SyncChangedAndEntering(ReadOnlySpan<GameEntityChange> changes, GameViewWindow window, GameViewWindow previousWindow,
        RefreshContext context)
    {
        _upsertedCapacity = Math.Min(changes.Length, MaximumUpsertedCapacity);
        var established = window.Intersect(previousWindow);
        foreach (ref readonly var change in changes)
        {
            Sync(change, established, context);
        }

        ForEachChunkOutside(window, previousWindow, isEntering: true, context);
    }

    private void RefreshAll(GameViewWindow window, RefreshContext context)
    {
        for (var y = window.Minimum.Y; y <= window.Maximum.Y; y++)
        {
            for (var x = window.Minimum.X; x <= window.Maximum.X; x++)
            {
                RefreshChunk((x, y), context);
            }
        }
    }

    // Brings what the page knows of a changed entity in line with the index within the established chunks. An entity
    // that ends up only in entering chunks is uncounted here and counted again by their refresh.
    private void Sync(in GameEntityChange change, GameViewWindow established, RefreshContext context)
    {
        var entity = change.Entity;
        var isInEstablished = established.Holds(entity, context.Index);

        // The page knows an entity by the area it had at the last update, so one that was outside the established
        // chunks then and is now is unknown to the page and stays so.
        if (!isInEstablished && !established.HeldBefore(change))
        {
            return;
        }

        ref var known = ref CollectionsMarshal.GetValueRefOrNullRef(_known, entity);
        if (!Unsafe.IsNullRef(ref known))
        {
            SyncKnown(entity, ref known, isInEstablished, established, context);
        }
        else if (isInEstablished)
        {
            AddKnown(entity, established, context);
        }
    }

    private void SyncKnown(GameEntity entity, ref KnownEntity known, bool isInEstablished, GameViewWindow established,
        RefreshContext context)
    {
        var area = entity.Area;
        var knownArea = known.Area;
        if (isInEstablished && knownArea == area)
        {
            if (known.ChunkCount > 0)
            {
                UpsertWhenChanged(entity, ref known, context);
            }

            return;
        }

        LeaveChunks(entity, knownArea, isInEstablished ? area : null, established, ref known);
        if (isInEstablished)
        {
            EnterChunks(entity, area, knownArea, established, ref known);
        }

        if (known.ChunkCount == 0)
        {
            _uncounted.Add(entity);
            return;
        }

        UpsertWhenChanged(entity, ref known, context);
    }

    private void AddKnown(GameEntity entity, GameViewWindow established, RefreshContext context)
    {
        ref var known = ref CollectionsMarshal.GetValueRefOrAddDefault(_known, entity, out _);
        known = new KnownEntity { Id = NextId() };
        EnterChunks(entity, entity.Area, null, established, ref known);
        Upsert(entity, ref known, ReferenceEquals(entity, context.Self), context);
    }

    // Takes the entity out of the lists of the established chunks its known area covers and the area does not.
    private void LeaveChunks(GameEntity entity, GameArea knownArea, GameArea? area, GameViewWindow established, ref KnownEntity known)
    {
        var chunks = GameViewWindow.Of(knownArea).Intersect(established);
        var kept = area is { } current ? GameViewWindow.Of(current) : GameViewWindow.None;
        for (var y = chunks.Minimum.Y; y <= chunks.Maximum.Y; y++)
        {
            for (var x = chunks.Minimum.X; x <= chunks.Maximum.X; x++)
            {
                if (kept.Contains((x, y)))
                {
                    continue;
                }

                if (RemoveFromList((x, y), entity))
                {
                    known.ChunkCount--;
                }
            }
        }
    }

    // Adds the entity to the lists of the established chunks the area covers and the known area does not.
    private void EnterChunks(GameEntity entity, GameArea area, GameArea? knownArea, GameViewWindow established, ref KnownEntity known)
    {
        var chunks = GameViewWindow.Of(area).Intersect(established);
        var held = knownArea is { } former ? GameViewWindow.Of(former) : GameViewWindow.None;
        for (var y = chunks.Minimum.Y; y <= chunks.Maximum.Y; y++)
        {
            for (var x = chunks.Minimum.X; x <= chunks.Maximum.X; x++)
            {
                if (held.Contains((x, y)))
                {
                    continue;
                }

                GetOrCreateList((x, y)).Add(entity);
                known.ChunkCount++;
            }
        }
    }

    // Visits the chunks of the area that the excluded window does not contain, row by row, so the work follows the
    // size of the difference rather than of the window.
    private void ForEachChunkOutside(GameViewWindow area, GameViewWindow excluded, bool isEntering, RefreshContext context)
    {
        for (var y = area.Minimum.Y; y <= area.Maximum.Y; y++)
        {
            if (y < excluded.Minimum.Y || y > excluded.Maximum.Y)
            {
                VisitRow(y, area.Minimum.X, area.Maximum.X, isEntering, context);
                continue;
            }

            VisitRow(y, area.Minimum.X, Math.Min(area.Maximum.X, excluded.Minimum.X - 1), isEntering, context);
            VisitRow(y, Math.Max(area.Minimum.X, excluded.Maximum.X + 1), area.Maximum.X, isEntering, context);
        }
    }

    private void VisitRow(long y, long minimumX, long maximumX, bool isEntering, RefreshContext context)
    {
        for (var x = minimumX; x <= maximumX; x++)
        {
            if (isEntering)
            {
                RefreshChunk((x, y), context);
            }
            else
            {
                ForgetChunk((x, y));
            }
        }
    }

    // Takes the chunk's visible entities from the index, counts those that entered it, uncounts those that left it and
    // upserts the changed ones.
    private void RefreshChunk((long X, long Y) chunk, RefreshContext context)
    {
        _queried.Clear();
        context.Index.QueryChunk(chunk, _queried);
        _chunks.TryGetValue(chunk, out var list);
        _previous.Clear();
        if (list is not null)
        {
            foreach (var entity in list)
            {
                _previous.Add(entity);
            }

            list.Clear();
        }

        foreach (var entity in _queried)
        {
            if (!entity.IsVisible)
            {
                continue;
            }

            list ??= GetOrCreateList(chunk);
            list.Add(entity);
            RefreshEntity(entity, isEntering: !_previous.Remove(entity), context);
        }

        foreach (var entity in _previous)
        {
            Uncount(entity);
        }

        _previous.Clear();
        _queried.Clear();
        if (list is { Count: 0 })
        {
            ReleaseList(chunk, list);
        }
    }

    // An entity spanning several refreshed chunks is compared in each, and upserted only in the first.
    private void RefreshEntity(GameEntity entity, bool isEntering, RefreshContext context)
    {
        ref var known = ref CollectionsMarshal.GetValueRefOrAddDefault(_known, entity, out var exists);
        if (!exists)
        {
            known = new KnownEntity { Id = NextId() };
            Upsert(entity, ref known, ReferenceEquals(entity, context.Self), context);
        }
        else
        {
            UpsertWhenChanged(entity, ref known, context);
        }

        if (isEntering)
        {
            known.ChunkCount++;
        }
    }

    private void ForgetChunk((long X, long Y) chunk)
    {
        if (!_chunks.TryGetValue(chunk, out var list))
        {
            return;
        }

        foreach (var entity in list)
        {
            Uncount(entity);
        }

        list.Clear();
        ReleaseList(chunk, list);
    }

    private bool RemoveFromList((long X, long Y) chunk, GameEntity entity)
    {
        if (!_chunks.TryGetValue(chunk, out var list) || !list.Remove(entity))
        {
            return false;
        }

        if (list.Count == 0)
        {
            ReleaseList(chunk, list);
        }

        return true;
    }

    private List<GameEntity> GetOrCreateList((long X, long Y) chunk)
    {
        if (!_chunks.TryGetValue(chunk, out var list))
        {
            list = _pooledLists.Count > 0 ? _pooledLists.Pop() : [];
            _chunks[chunk] = list;
        }

        return list;
    }

    private void ReleaseList((long X, long Y) chunk, List<GameEntity> list)
    {
        _chunks.Remove(chunk);
        if (_pooledLists.Count < MaximumPooledLists)
        {
            _pooledLists.Push(list);
        }
    }

    private void Uncount(GameEntity entity)
    {
        ref var known = ref CollectionsMarshal.GetValueRefOrNullRef(_known, entity);
        if (--known.ChunkCount == 0)
        {
            _uncounted.Add(entity);
        }
    }

    // An entity uncounted to zero may have been counted again later in the same update.
    private List<string>? RemoveUncounted()
    {
        var removed = _resetIds;
        _resetIds = null;
        if (_uncounted.Count == 0)
        {
            return removed;
        }

        foreach (var entity in _uncounted)
        {
            if (_known.TryGetValue(entity, out var known) && known.ChunkCount == 0)
            {
                _known.Remove(entity);
                (removed ??= []).Add(known.Id);
            }
        }

        _uncounted.Clear();
        return removed;
    }

    private void UpsertWhenChanged(GameEntity entity, ref KnownEntity known, RefreshContext context)
    {
        ComparisonCount++;
        var isSelf = ReferenceEquals(entity, context.Self);
        if (!known.IsSameAs(entity, isSelf))
        {
            Upsert(entity, ref known, isSelf, context);
        }
    }

    private void Upsert(GameEntity entity, ref KnownEntity known, bool isSelf, RefreshContext context)
    {
        known.TakeOver(entity, isSelf);
        (_upserted ??= new List<GameViewEntry>(_upsertedCapacity)).Add(new GameViewEntry(known.Id, isSelf, context.Payloads.Get(entity)));
    }

    private string NextId() => $"o{++_nextId}";

    /// <summary>
    /// What the page was sent of an entity, as far as it decides whether to send it again.
    /// </summary>
    private struct KnownEntity
    {
        public string Id { get; init; }

        public GameArea Area { get; private set; }

        public GameVisual? Visual { get; private set; }

        public string? Title { get; private set; }

        public int Height { get; private set; }

        public int Facing { get; private set; }

        public int ChunkCount { get; set; }

        public bool IsMovementBlocking { get; private set; }

        public bool IsSelf { get; private set; }

        public bool IsMicrophoneOn { get; private set; }

        public bool IsAudioOn { get; private set; }

        public string? Character { get; private set; }

        public string? Color { get; private set; }

        public string? Animation { get; private set; }

        public (int Mode, int Sequence) AnimationRun { get; private set; }

        // A cached visual is the same instance, so the reference check spares the value comparison; neither allocates.
        public readonly bool IsSameAs(GameEntity entity, bool isSelf) =>
            HasSamePlacement(entity) && HasSameState(entity, isSelf) && HasSamePlayerState(entity) &&
            Equals(Visual, GameViewPayloads.GetVisual(entity));

        public void TakeOver(GameEntity entity, bool isSelf)
        {
            Area = entity.Area;
            Visual = GameViewPayloads.GetVisual(entity);
            Title = entity.Title;
            Height = entity.Height;
            Facing = GameViewPayloads.GetFacing(entity);
            IsMovementBlocking = entity.IsMovementBlocking;
            IsSelf = isSelf;
            IsMicrophoneOn = GameViewPayloads.IsMicrophoneOn(entity);
            IsAudioOn = GameViewPayloads.IsAudioOn(entity);
            Character = GameViewPayloads.GetCharacter(entity);
            Color = GameViewPayloads.GetColor(entity);
            Animation = GameViewPayloads.GetAnimation(entity);
            AnimationRun = GameViewPayloads.GetAnimationRun(entity);
        }

        private readonly bool HasSamePlacement(GameEntity entity) =>
            Area == entity.Area && Height == entity.Height && Facing == GameViewPayloads.GetFacing(entity);

        private readonly bool HasSameState(GameEntity entity, bool isSelf) =>
            IsSelf == isSelf && IsMovementBlocking == entity.IsMovementBlocking && Title == entity.Title;

        private readonly bool HasSamePlayerState(GameEntity entity) => HasSameAppearance(entity) && HasSameVoice(entity) && HasSameAnimation(entity);

        private readonly bool HasSameAppearance(GameEntity entity) =>
            Character == GameViewPayloads.GetCharacter(entity) && Color == GameViewPayloads.GetColor(entity);

        private readonly bool HasSameAnimation(GameEntity entity) =>
            Animation == GameViewPayloads.GetAnimation(entity) && AnimationRun == GameViewPayloads.GetAnimationRun(entity);

        private readonly bool HasSameVoice(GameEntity entity) =>
            IsMicrophoneOn == GameViewPayloads.IsMicrophoneOn(entity) && IsAudioOn == GameViewPayloads.IsAudioOn(entity);
    }

    private readonly record struct RefreshContext(GameWorldIndex Index, PlayerEntity? Self, GameViewPayloads Payloads);
}
