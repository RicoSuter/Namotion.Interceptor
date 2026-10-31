using HomeBlaze.Abstractions;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Tracking.Parent;

namespace HomeBlaze.Game.Tools;

/// <summary>
/// A tool of a game in <see cref="Game.Tools"/>, whose operations the game page lists for every player, for example to
/// build or plant objects. An operation takes a nullable <c>[FromServices] GameCaller</c> parameter for the acting player.
/// </summary>
[InterceptorSubject]
public abstract partial class GameTool : IConfigurable
{
    public virtual Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Gets the game whose <see cref="Game.Tools"/> holds this tool.
    /// </summary>
    /// <exception cref="InvalidOperationException">The tool is not part of a game.</exception>
    protected Game GetGame() => this.TryGetFirstParent<Game>() ?? throw new InvalidOperationException("The tool is not part of a game.");
}
