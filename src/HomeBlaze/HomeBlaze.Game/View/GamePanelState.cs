using HomeBlaze.Game.Abstractions;

namespace HomeBlaze.Game.View;

/// <summary>
/// What the panel of a page shows next to its view, as of the update it came with.
/// </summary>
/// <param name="PositionText">The position and facing of the page's player, or <see cref="NoPosition"/> while it is not
/// in the world.</param>
/// <param name="Targets">The visible objects in the tiles in front of the player, from south-west to north-east.</param>
/// <param name="Facing">The facing of the player, or null without one.</param>
/// <param name="HasPlayer">Whether the page's client has a player in the game.</param>
/// <param name="PlayerClientId">The client id of that player, or null without one.</param>
internal sealed record GamePanelState(
    string PositionText,
    IReadOnlyList<GameObject> Targets,
    GameDirection? Facing,
    bool HasPlayer,
    string? PlayerClientId)
{
    /// <summary>
    /// The position text of a page whose player is not in the world.
    /// </summary>
    public const string NoPosition = "–";

    /// <summary>
    /// The panel of a page without a player.
    /// </summary>
    public static GamePanelState Empty { get; } = new(NoPosition, [], null, false, null);
}
