using HomeBlaze.Game.View;

namespace HomeBlaze.Game.Tests.Testing;

/// <summary>
/// Records the updates delivered to it; thread-safe, because a running game delivers from its own thread.
/// </summary>
internal sealed class RecordingViewSink : IGameViewSink
{
    private readonly Lock _lock = new();
    private readonly List<(GameViewUpdate Update, GamePanelState Panel)> _deliveries = [];
    private volatile bool _isBusy;

    public bool IsBusy
    {
        get => _isBusy;
        set => _isBusy = value;
    }

    /// <summary>
    /// Called inside each delivery, on the thread that delivers.
    /// </summary>
    public Action? Delivered { get; set; }

    public IReadOnlyList<(GameViewUpdate Update, GamePanelState Panel)> Deliveries
    {
        get
        {
            lock (_lock)
            {
                return [.. _deliveries];
            }
        }
    }

    /// <summary>
    /// The updates of the deliveries that changed the view.
    /// </summary>
    public IReadOnlyList<GameViewUpdate> Updates => [.. Deliveries.Select(delivery => delivery.Update).Where(update => !update.IsEmpty)];

    /// <summary>
    /// The panel state of the last delivery.
    /// </summary>
    public GamePanelState Panel => Deliveries[^1].Panel;

    public void Clear()
    {
        lock (_lock)
        {
            _deliveries.Clear();
        }
    }

    public void Deliver(GameViewUpdate update, GamePanelState panel)
    {
        lock (_lock)
        {
            _deliveries.Add((update, panel));
        }

        Delivered?.Invoke();
    }
}
