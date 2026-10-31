using HomeBlaze.Game.Visuals;

namespace HomeBlaze.Game;

/// <summary>
/// The textured boxes of walls and door lintels.
/// </summary>
internal static class WallTextures
{
    // One repeat of the bricks texture shows 2 m by 2 m.
    private const double BricksScale = 8;
    private static readonly string BricksUrl = GameAssets.Url(typeof(WallTextures), "objects/buildings/textures/bricks.jpg");

    public static GameVisualPart CreateBricks(GameVisualVector offset, GameVisualVector size) => new()
    {
        Shape = GameVisualShape.Box,
        Offset = offset,
        Size = size,
        Texture = BricksUrl,
        TextureScale = BricksScale
    };
}
