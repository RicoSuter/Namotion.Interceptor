using HomeBlaze.Game.Abstractions;
using HomeBlaze.Game.View;
using HomeBlaze.Game.World;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Game.Simulation;

/// <summary>
/// The simulation's side of a page's view: the window around the page's player, what the page knows of it, and the
/// chunks changed while the page was busy. Touched only on the simulation thread, except <see cref="Dispose"/>,
/// <see cref="IsDisposed"/> and <see cref="ClientId"/>.
/// </summary>
internal sealed class GameViewSession : IDisposable
{
    private readonly GameViewTracker _tracker = new();
    private readonly IGameViewSink _sink;

    // The changed chunks of the ticks the sink was busy in, limited to the window the page knows.
    private readonly ChangedChunks _pendingChunks = new();
    private readonly List<GameEntity> _targetResults = [];

    private string? _clientId;
    private int _isDisposed;
    private bool _isFullRefreshRequested = true;
    private PlayerEntity? _self;
    private GamePanelState _panel = GamePanelState.Empty;

    // Below every version, so the first update is delivered even when nothing is in view.
    private long _publishVersion = -1;

    // What the position text and the targets of the panel were computed from.
    private GameArea _panelArea;
    private GameDirection _panelFacing;
    private bool _isPanelSelfVisible;

    public GameViewSession(string? clientId, IGameViewSink sink)
    {
        _clientId = clientId;
        _sink = sink;
    }

    /// <summary>
    /// The client whose player the view follows, or null to see the window around the origin. Any thread may set it; the
    /// next tick follows it.
    /// </summary>
    public string? ClientId
    {
        get => Volatile.Read(ref _clientId);
        set => Volatile.Write(ref _clientId, value);
    }

    /// <summary>
    /// Whether the page disposed the session; any thread may read it.
    /// </summary>
    public bool IsDisposed => Volatile.Read(ref _isDisposed) == 1;

    /// <summary>
    /// Ends the session; the simulation drops it in its next tick. Any thread may call it.
    /// </summary>
    public void Dispose() => Interlocked.Exchange(ref _isDisposed, 1);

    /// <summary>
    /// Makes the next update refresh every chunk of the window.
    /// </summary>
    public void RequestFullRefresh() => _isFullRefreshRequested = true;

    /// <summary>
    /// Delivers what changed in the window since the last delivery, with the panel state, unless the sink is busy, a full
    /// refresh is due but not allowed, or nothing changed; call once per tick, after the world changes of the tick were
    /// applied and before <see cref="GameWorldIndex.Changes"/> is cleared.
    /// </summary>
    /// <param name="world">The world state.</param>
    /// <param name="publishVersion">The version of the last publish; a new one is delivered so the page reads the subjects again.</param>
    /// <param name="canFullRefresh">Whether this tick still allows a refresh of every chunk of the window; without it a
    /// session that needs one waits like one whose sink is busy.</param>
    /// <param name="logger">Warns about object types with an invalid visual.</param>
    /// <returns>Whether the session refreshed every chunk of its window.</returns>
    public bool Update(GameWorldState world, long publishVersion, bool canFullRefresh, ILogger logger)
    {
        var changes = world.Index.Changes;
        if (_sink.IsBusy)
        {
            CollectPending(changes);
            return false;
        }

        var self = FindSelf(world);
        var window = self is null ? GameViewWindow.Around(0, 0) : GameViewWindow.Around(self.Area.X, self.Area.Y);
        var isFullRefresh = IsFullRefreshDue(window);
        if (isFullRefresh && !canFullRefresh)
        {
            // The full refresh reads every chunk of the window, so the changes meanwhile need not be collected. It stays
            // due also when its reason goes away, as for a player that jumps away and back before it ran.
            _isFullRefreshRequested = true;
            return false;
        }

        var chunks = GetChangedChunks(changes);
        var update = chunks.Count > 0 || isFullRefresh || _tracker.Window != window
            ? _tracker.UpdateChunks(world.Index, chunks, window, self, isFullRefresh, logger)
            : GameViewTracker.Empty;
        var panel = GetPanel(world, self, chunks, isFullRefresh);
        _isFullRefreshRequested = false;
        _pendingChunks.Clear();
        DeliverWhenChanged(update, panel, publishVersion);
        return isFullRefresh;
    }

    // The first update, a different own player and a window that jumped refresh every chunk.
    private bool IsFullRefreshDue(GameViewWindow window) =>
        _isFullRefreshRequested || !_tracker.HasWindow || window.IsFarFrom(_tracker.Window);

    private void DeliverWhenChanged(GameViewUpdate update, GamePanelState panel, long publishVersion)
    {
        if (update.IsEmpty && ReferenceEquals(panel, _panel) && publishVersion == _publishVersion)
        {
            return;
        }

        _panel = panel;
        _publishVersion = publishVersion;
        _sink.Deliver(update, panel);
    }

    // The changes of this tick, joined to the ones collected while the sink was busy.
    private ChangedChunks GetChangedChunks(ChangedChunks changes)
    {
        if (_pendingChunks.Count == 0)
        {
            return changes;
        }

        CollectPending(changes);
        return _pendingChunks;
    }

    // A different own player, or none, changes IsSelf, so every chunk is refreshed.
    private PlayerEntity? FindSelf(GameWorldState world)
    {
        var self = ClientId is { } clientId && world.Players.TryGetValue(clientId, out var player) ? player : null;
        if (!ReferenceEquals(self, _self))
        {
            _self = self;
            _isFullRefreshRequested = true;
        }

        return self;
    }

    // Only chunks of the window the page knows matter: a chunk of the window that the page enters later is refreshed
    // then anyway.
    private void CollectPending(ChangedChunks changes)
    {
        if (!_tracker.HasWindow)
        {
            return;
        }

        var window = _tracker.Window;
        foreach (var chunk in changes)
        {
            if (window.Contains(chunk))
            {
                _pendingChunks.Add(chunk);
            }
        }
    }

    // Returns the delivered panel while nothing it shows changed, so an unchanged panel allocates nothing.
    private GamePanelState GetPanel(GameWorldState world, PlayerEntity? self, ChangedChunks chunks, bool isFullRefresh)
    {
        if (self is null)
        {
            return GamePanelState.Empty;
        }

        var isPositionChanged = IsPositionChanged(self);
        var targets = isPositionChanged || isFullRefresh || IsInFrontChanged(self, chunks)
            ? GetTargets(world, self, _panel.Targets)
            : _panel.Targets;
        if (!isPositionChanged && ReferenceEquals(targets, _panel.Targets))
        {
            return _panel;
        }

        _panelArea = self.Area;
        _panelFacing = self.Facing;
        _isPanelSelfVisible = self.IsVisible;
        var positionText = self.IsVisible
            ? $"X {self.Area.X} · Y {self.Area.Y} · facing {self.Facing.GetShortName()}"
            : GamePanelState.NoPosition;
        return new GamePanelState(positionText, targets, self.Facing, HasPlayer: true, self.ClientId);
    }

    // The panel without a player has no client id, so a player that appears always counts as changed.
    private bool IsPositionChanged(PlayerEntity self) =>
        _panel.PlayerClientId != self.ClientId || self.Area != _panelArea || self.Facing != _panelFacing || self.IsVisible != _isPanelSelfVisible;

    // Whether a chunk that the tiles in front of the player touch changed.
    private static bool IsInFrontChanged(PlayerEntity self, ChangedChunks chunks)
    {
        if (chunks.Count == 0 || !self.Area.TryMove(self.Facing.GetOffsetX(), self.Facing.GetOffsetY(), out var shifted))
        {
            return false;
        }

        var minimum = GameWorldIndex.GetChunk(shifted.X, shifted.Y);
        var maximum = GameWorldIndex.GetChunk(shifted.Right, shifted.Top);
        for (var y = minimum.Y; y <= maximum.Y; y++)
        {
            for (var x = minimum.X; x <= maximum.X; x++)
            {
                if (chunks.Contains((x, y)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    // Keeps the former list while the same objects stand in front.
    private IReadOnlyList<GameObject> GetTargets(GameWorldState world, PlayerEntity self, IReadOnlyList<GameObject> former)
    {
        _targetResults.Clear();
        world.GetTargets(self, _targetResults);
        try
        {
            if (HasSameSubjects(_targetResults, former))
            {
                return former;
            }

            var targets = new GameObject[_targetResults.Count];
            for (var i = 0; i < targets.Length; i++)
            {
                targets[i] = _targetResults[i].Subject;
            }

            return targets;
        }
        finally
        {
            _targetResults.Clear();
        }
    }

    private static bool HasSameSubjects(List<GameEntity> entities, IReadOnlyList<GameObject> subjects)
    {
        if (entities.Count != subjects.Count)
        {
            return false;
        }

        for (var i = 0; i < entities.Count; i++)
        {
            if (!ReferenceEquals(entities[i].Subject, subjects[i]))
            {
                return false;
            }
        }

        return true;
    }
}
