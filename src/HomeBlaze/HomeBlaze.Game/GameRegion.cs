using System.ComponentModel;
using System.Globalization;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Game.Abstractions;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Game;

/// <summary>
/// The objects of a game within one region of <see cref="Size"/> by <see cref="Size"/> tiles; the game keeps every object
/// in the region of its south-west corner.
/// </summary>
[Description("The objects of a game within one region of 64 by 64 tiles")]
[DisplayName("Region")]
[ExcludeFromCreation]
[InterceptorSubject]
public partial class GameRegion : IConfigurable
{
    /// <summary>
    /// The width and depth of a region in tiles, 16 m.
    /// </summary>
    public const int Size = 1 << SizeShift;

    private const int SizeShift = 6;

    /// <summary>
    /// The objects by name; a name is unique across all regions of the game.
    /// </summary>
    [Configuration]
    public partial Dictionary<string, GameObject> Objects { get; set; }

    public GameRegion()
    {
        Objects = new Dictionary<string, GameObject>();
    }

    partial void OnObjectsChanging(ref Dictionary<string, GameObject> newValue, ref bool cancel)
    {
        newValue ??= new Dictionary<string, GameObject>();
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Gets the coordinates of the region that holds the tile; the arithmetic shift floors negative coordinates.
    /// </summary>
    internal static (int X, int Y) GetCoordinates(int x, int y) => (x >> SizeShift, y >> SizeShift);

    /// <summary>
    /// Gets the key of the region in <see cref="Game.Regions"/>, its coordinates as "{X}_{Y}".
    /// </summary>
    internal static string GetKey(int regionX, int regionY) => string.Create(CultureInfo.InvariantCulture, $"{regionX}_{regionY}");

    /// <summary>
    /// Gets the coordinates of a key that <see cref="GetKey"/> returns; false for any other key.
    /// </summary>
    internal static bool TryParseKey(string key, out int regionX, out int regionY)
    {
        var separator = key.IndexOf('_');
        if (separator > 0
            && int.TryParse(key.AsSpan(0, separator), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out regionX)
            && int.TryParse(key.AsSpan(separator + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out regionY)
            && key == GetKey(regionX, regionY))
        {
            return true;
        }

        (regionX, regionY) = (0, 0);
        return false;
    }

    /// <summary>
    /// Groups the objects into new regions by the position of their south-west corner, as <see cref="Game.Regions"/>
    /// holds them; for tests, benchmarks and worlds built in code.
    /// </summary>
    internal static Dictionary<string, GameRegion> CreateRegions(IEnumerable<KeyValuePair<string, GameObject>> objects)
    {
        var regions = new Dictionary<(int X, int Y), Dictionary<string, GameObject>>();
        foreach (var (name, gameObject) in objects)
        {
            var coordinates = GetCoordinates(gameObject.X, gameObject.Y);
            if (!regions.TryGetValue(coordinates, out var regionObjects))
            {
                regionObjects = new Dictionary<string, GameObject>();
                regions[coordinates] = regionObjects;
            }

            regionObjects[name] = gameObject;
        }

        var result = new Dictionary<string, GameRegion>(regions.Count);
        foreach (var ((regionX, regionY), regionObjects) in regions)
        {
            result[GetKey(regionX, regionY)] = new GameRegion { Objects = regionObjects };
        }

        return result;
    }
}
