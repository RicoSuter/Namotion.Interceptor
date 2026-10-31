using HomeBlaze.Game.Visuals;

namespace HomeBlaze.Game.Objects.Buildings;

/// <summary>
/// The textured boxes of walls and door lintels.
/// </summary>
internal static class WallTextures
{
    private static readonly string BricksUrl = GameAssets.Url(typeof(WallTextures), "objects/buildings/textures/bricks.jpg");
    private static readonly string ConcreteUrl = GameAssets.Url(typeof(WallTextures), "objects/buildings/textures/concrete.jpg");
    private static readonly string WoodUrl = GameAssets.Url(typeof(WallTextures), "objects/buildings/textures/wood.jpg");

    public static GameVisualPart CreateBox(WallMaterial material, GameVisualVector offset, GameVisualVector size) => new()
    {
        Shape = GameVisualShape.Box,
        Offset = offset,
        Size = size,
        Texture = GetUrl(material),
        TextureScale = GetScale(material)
    };

    // A value outside the enum, possible in a loaded file, looks like bricks.
    private static string GetUrl(WallMaterial material) => material switch
    {
        WallMaterial.Concrete => ConcreteUrl,
        WallMaterial.Wood => WoodUrl,
        _ => BricksUrl
    };

    // Tiles per repeat, so that bricks show 2 m, concrete 2.5 m and planks 1.5 m per repeat.
    private static double GetScale(WallMaterial material) => material switch
    {
        WallMaterial.Concrete => 10,
        WallMaterial.Wood => 6,
        _ => 8
    };
}
