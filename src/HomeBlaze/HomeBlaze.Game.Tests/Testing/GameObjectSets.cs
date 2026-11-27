namespace HomeBlaze.Game.Tests.Testing;

/// <summary>
/// Reads and writes the objects of a game across its regions.
/// </summary>
internal static class GameObjectSets
{
    /// <summary>
    /// Gets the objects of all regions of the game by name, as its subjects show them.
    /// </summary>
    /// <exception cref="ArgumentException">Two regions hold an object of the same name.</exception>
    public static Dictionary<string, GameObject> GetObjects(this Game game)
    {
        var objects = new Dictionary<string, GameObject>();
        foreach (var region in game.Regions.Values)
        {
            foreach (var (name, gameObject) in region.Objects)
            {
                objects.Add(name, gameObject);
            }
        }

        return objects;
    }

    /// <summary>
    /// Writes the objects to the game in new regions by the position of their south-west corner, as a storage that loads
    /// them does.
    /// </summary>
    public static void SetObjects(this Game game, Dictionary<string, GameObject> objects) => game.Regions = GameRegion.CreateRegions(objects);
}
