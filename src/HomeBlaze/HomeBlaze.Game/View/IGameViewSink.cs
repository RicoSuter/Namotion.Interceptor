namespace HomeBlaze.Game.View;

/// <summary>
/// Where a view session delivers its updates; implemented by the page.
/// </summary>
internal interface IGameViewSink
{
    /// <summary>
    /// Whether the previous update is still on its way to the browser; the session skips the page meanwhile and collects
    /// what changed for the next update. Read on the simulation thread, so it must not block.
    /// </summary>
    bool IsBusy { get; }

    /// <summary>
    /// Takes an update and the panel state that goes with it; called on the simulation thread, must not block. The next
    /// update carries only what changed since this one.
    /// </summary>
    void Deliver(GameViewUpdate update, GamePanelState panel);
}
