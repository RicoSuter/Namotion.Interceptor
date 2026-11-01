using System.Collections.Concurrent;
using HomeBlaze.Game.View;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Game.Simulation;

/// <summary>
/// The view sessions of the pages of one game. Touched only on the simulation thread, except <see cref="Open"/>.
/// </summary>
internal sealed class GameViewSessions
{
    /// <summary>
    /// The sessions that may refresh every chunk of their window in one tick, which bounds the cost of a tick in which many
    /// pages join or jump; the others wait for the next tick.
    /// </summary>
    public const int MaximumFullRefreshesPerTick = 4;

    private readonly List<GameViewSession> _sessions = [];

    // Filled by any thread, drained by the simulation, so a page can open its view while the game is stopped.
    private readonly ConcurrentQueue<GameViewSession> _opened = new();
    private readonly GameSimulation _simulation;
    private readonly GameWorldState _world;
    private readonly ILogger _logger;
    private readonly Action<(GameViewSession Session, long PublishVersion)> _update;
    private int _remainingFullRefreshes;

    public GameViewSessions(GameSimulation simulation, GameWorldState world, ILogger logger)
    {
        _simulation = simulation;
        _world = world;
        _logger = logger;
        _update = Update;
    }

    /// <summary>
    /// Opens a session that delivers to the sink, in every tick, what changed within
    /// <see cref="GameViewTracker.ViewRadius"/> of the client's player, or of the origin while the client has none,
    /// together with the panel state; its first delivery holds everything in view and comes in the next tick, after a
    /// start when the game is stopped. Disposing the session ends it; setting <see cref="GameViewSession.ClientId"/> makes
    /// it follow another client. Any thread may call it.
    /// </summary>
    public GameViewSession Open(string? clientId, IGameViewSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        var session = new GameViewSession(clientId, sink);
        _opened.Enqueue(session);
        return session;
    }

    /// <summary>
    /// Takes in the opened sessions, drops the disposed ones and updates the others; a session that throws is logged and
    /// leaves the others alone.
    /// </summary>
    public void Update(long publishVersion)
    {
        while (_opened.TryDequeue(out var opened))
        {
            _sessions.Add(opened);
        }

        _remainingFullRefreshes = MaximumFullRefreshesPerTick;
        for (var i = _sessions.Count - 1; i >= 0; i--)
        {
            var session = _sessions[i];
            if (session.IsDisposed)
            {
                _sessions.RemoveAt(i);
                continue;
            }

            _simulation.RunLogged(_update, (session, publishVersion));
        }
    }

    public void RequestFullRefresh()
    {
        foreach (var session in _sessions)
        {
            session.RequestFullRefresh();
        }
    }

    private void Update((GameViewSession Session, long PublishVersion) state)
    {
        if (state.Session.Update(_world, state.PublishVersion, _remainingFullRefreshes > 0, _logger))
        {
            _remainingFullRefreshes--;
        }
    }
}
