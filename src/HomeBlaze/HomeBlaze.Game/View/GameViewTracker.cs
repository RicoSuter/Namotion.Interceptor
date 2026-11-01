using HomeBlaze.Game.Abstractions;
using HomeBlaze.Game.Simulation;
using HomeBlaze.Game.World;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Game.View;

/// <summary>
/// Remembers what one page knows of the chunks in its window and turns the chunks it is given into the changes the page
/// lacks. Not thread-safe: only the simulation thread uses it.
/// </summary>
internal sealed class GameViewTracker
{
    /// <summary>
    /// The tiles in each direction a page sees, 50 m.
    /// </summary>
    public const int ViewRadius = 200;

    private const int MaximumPooledLists = 64;

    /// <summary>
    /// The update without changes; shared, so an unchanged view allocates nothing.
    /// </summary>
    public static readonly GameViewUpdate Empty = new([], []);

    // Per known entity its view and the number of window chunks whose list holds it; it leaves the view at zero.
    private readonly Dictionary<GameEntity, KnownEntity> _known = new(ReferenceEqualityComparer.Instance);

    // The visible entities of each window chunk as of its last refresh; chunks without any have no list.
    private readonly Dictionary<(long X, long Y), List<GameEntity>> _chunks = new();
    private readonly Stack<List<GameEntity>> _pooledLists = new();

    // Scratch space of one update.
    private readonly List<GameEntity> _queried = [];
    private readonly HashSet<GameEntity> _previous = new(ReferenceEqualityComparer.Instance);
    private readonly List<GameEntity> _uncounted = [];
    private List<GameViewObject>? _upserted;

    private readonly Dictionary<string, string> _playerColors = new();
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
    /// Brings the page in line with the visible entities of the changed chunks inside the window and of the chunks that
    /// entered the window, forgets the chunks that left it, and returns what changed. The first update, a full refresh
    /// and a window that moved by more than one chunk refresh every chunk of the window.
    /// </summary>
    /// <param name="index">The chunk index to read the chunks from.</param>
    /// <param name="changes">The chunks changed since the last update; chunks outside the window are ignored.</param>
    /// <param name="window">The window the page sees now.</param>
    /// <param name="self">The page's own player, or null.</param>
    /// <param name="isFullRefresh">Whether to refresh every chunk of the window, as after a change of the own player.</param>
    /// <param name="logger">Warns about object types with an invalid visual.</param>
    public GameViewUpdate UpdateChunks(GameWorldIndex index, ChangedChunks changes, GameViewWindow window, PlayerEntity? self,
        bool isFullRefresh, ILogger logger)
    {
        var previousWindow = Window;
        var hadWindow = _hasWindow;
        isFullRefresh |= !hadWindow || window.IsFarFrom(previousWindow);
        Window = window;
        _hasWindow = true;
        _upserted = null;
        var context = new RefreshContext(index, self, logger);
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
            RefreshChangedAndEntering(changes, window, previousWindow, context);
        }

        var upserted = _upserted;
        _upserted = null;
        var removed = RemoveUncounted();
        return upserted is null && removed is null ? Empty : new GameViewUpdate(upserted ?? [], removed ?? []);
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

    // A changed chunk that entered the window is refreshed with the entering ones, so none is refreshed twice.
    private void RefreshChangedAndEntering(ChangedChunks changes, GameViewWindow window, GameViewWindow previousWindow, RefreshContext context)
    {
        foreach (var chunk in changes)
        {
            if (window.Contains(chunk) && previousWindow.Contains(chunk))
            {
                RefreshChunk(chunk, context);
            }
        }

        ForEachChunkOutside(window, previousWindow, isEntering: true, context);
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

            if (list is null)
            {
                list = _pooledLists.Count > 0 ? _pooledLists.Pop() : [];
                _chunks[chunk] = list;
            }

            list.Add(entity);
            UpsertWhenChanged(entity, isEntering: !_previous.Remove(entity), context);
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
        var known = _known[entity];
        if (--known.ChunkCount == 0)
        {
            _uncounted.Add(entity);
        }
    }

    // An entity spanning several refreshed chunks is compared in each, and upserted only in the first.
    private void UpsertWhenChanged(GameEntity entity, bool isEntering, RefreshContext context)
    {
        var isSelf = ReferenceEquals(entity, context.Self);
        if (!_known.TryGetValue(entity, out var known))
        {
            known = new KnownEntity(CreateView(entity, $"o{++_nextId}", isSelf, context.Logger));
            _known.Add(entity, known);
            (_upserted ??= []).Add(known.View);
        }
        else if (!IsUnchanged(known.View, entity, isSelf))
        {
            known.View = CreateView(entity, known.View.Id, isSelf, context.Logger);
            (_upserted ??= []).Add(known.View);
        }

        if (isEntering)
        {
            known.ChunkCount++;
        }
    }

    // An entity uncounted to zero may have been counted again by a later chunk of the same update.
    private List<string>? RemoveUncounted()
    {
        if (_uncounted.Count == 0)
        {
            return null;
        }

        List<string>? removed = null;
        foreach (var entity in _uncounted)
        {
            if (_known.TryGetValue(entity, out var known) && known.ChunkCount == 0)
            {
                _known.Remove(entity);
                (removed ??= []).Add(known.View.Id);
            }
        }

        _uncounted.Clear();
        return removed;
    }

    private bool IsUnchanged(GameViewObject known, GameEntity entity, bool isSelf) =>
        HasSameArea(known, entity) && HasSameState(known, entity, isSelf) && HasSameLook(known, entity);

    // A cached visual is the same instance, so the reference check spares the value comparison; neither allocates.
    private bool HasSameLook(GameViewObject known, GameEntity entity) =>
        known.IsMovementBlocking == entity.IsMovementBlocking && HasSameLabel(known, entity) && Equals(known.Visual, GetVisual(entity));

    private static bool HasSameArea(GameViewObject known, GameEntity entity) =>
        known.X == entity.Area.X && known.Y == entity.Area.Y && known.Width == entity.Area.Width && known.Depth == entity.Area.Depth;

    private static bool HasSameState(GameViewObject known, GameEntity entity, bool isSelf) =>
        known.Height == entity.Height && known.IsSelf == isSelf && known.Facing == GetFacing(entity) && HasSameVoice(known, entity);

    private static bool HasSameVoice(GameViewObject known, GameEntity entity) =>
        known.IsMicrophoneOn == IsMicrophoneOn(entity) && known.IsAudioOn == IsAudioOn(entity);

    private bool HasSameLabel(GameViewObject known, GameEntity entity) =>
        known.Title == entity.Title && known.Color == GetColor(entity);

    private GameViewObject CreateView(GameEntity entity, string id, bool isSelf, ILogger logger)
    {
        var visual = GetVisual(entity);
        var area = entity.Area;
        return new(id, GetKind(entity), area.X, area.Y, area.Width, area.Depth, entity.Height, GetFacing(entity), entity.Title,
            GetColor(entity), entity.IsMovementBlocking, isSelf, IsMicrophoneOn(entity), IsAudioOn(entity), visual,
            GetParts(entity, visual, logger));
    }

    private static GameVisual? GetVisual(GameEntity entity) => entity is PlayerEntity ? null : entity.Visual;

    private static IReadOnlyList<GameViewPart> GetParts(GameEntity entity, GameVisual? visual, ILogger logger) =>
        entity is PlayerEntity ? [] : GameViewPart.FromVisual(GameVisualValidator.GetValidVisual(entity.Subject, visual, logger));

    private static string GetKind(GameEntity entity) =>
        entity is PlayerEntity ? GameViewObject.PlayerKind : GameViewObject.ObjectKind;

    private static int GetFacing(GameEntity entity) => entity is PlayerEntity player ? (int)player.Facing : 0;

    private static bool IsMicrophoneOn(GameEntity entity) => entity is PlayerEntity { IsMicrophoneOn: true };

    private static bool IsAudioOn(GameEntity entity) => entity is PlayerEntity { IsAudioOn: true };

    // Other objects take their colors from their visual.
    private string GetColor(GameEntity entity) => entity is PlayerEntity player ? GetPlayerColor(player.ClientId) : string.Empty;

    private string GetPlayerColor(string clientId)
    {
        if (!_playerColors.TryGetValue(clientId, out var color))
        {
            color = GameColors.FromId(clientId);
            _playerColors[clientId] = color;
        }

        return color;
    }

    private sealed class KnownEntity(GameViewObject view)
    {
        public GameViewObject View { get; set; } = view;

        public int ChunkCount { get; set; }
    }

    private readonly record struct RefreshContext(GameWorldIndex Index, PlayerEntity? Self, ILogger Logger);
}
