using HomeBlaze.Game.Tests.Testing;
using HomeBlaze.Game.World;
using HomeBlaze.Services;
using Microsoft.Extensions.DependencyInjection;

namespace HomeBlaze.Game.Tests;

public class DemoWorldTests
{
    private const string DemoWorldPath = "src/HomeBlaze/HomeBlaze/Data/Files/Demo/Game.json";

    // A position inside each room where a player fits, as the south-west corner of its footprint.
    private static readonly (string Name, int X, int Y)[] Rooms =
    [
        ("living room", -18, 18),
        ("hall", 2, 18),
        ("kitchen", 18, 18),
        ("bedroom", -18, 38),
        ("second bedroom", 2, 38),
        ("bathroom", 18, 38),
        ("shed", -43, 16)
    ];

    private static readonly (int X, int Y)[] Steps = [(1, 0), (-1, 0), (0, 1), (0, -1)];

    private readonly Game _game;

    public DemoWorldTests()
    {
        var typeProvider = new TypeProvider();
        typeProvider.AddTypes([typeof(Game), typeof(GamePlayer), typeof(Wall), typeof(Door), typeof(Tree), typeof(WallTool), typeof(TreeTool)]);
        var serializer = new ConfigurableSubjectSerializer(typeProvider, new ServiceCollection().AddLogging().BuildServiceProvider());
        _game = Assert.IsType<Game>(serializer.Deserialize(File.ReadAllText(RepositoryPaths.Get(DemoWorldPath))));
    }

    [Fact]
    public void WhenTheDemoWorldLoads_ThenItHasAHouseAShedAForestAndTheTools()
    {
        // Act
        var walls = _game.Objects.Values.OfType<Wall>().ToArray();
        var doors = _game.Objects.Values.OfType<Door>().ToArray();
        var forest = _game.Objects
            .Where(pair => pair.Key.StartsWith("forest-", StringComparison.Ordinal))
            .Select(pair => Assert.IsType<Tree>(pair.Value))
            .ToArray();

        // Assert
        Assert.True(walls.Length >= 20);
        Assert.Contains(walls, wall => wall.Material == WallMaterial.Concrete);
        Assert.Contains(walls, wall => wall.Material == WallMaterial.Wood);
        Assert.InRange(doors.Length, 6, 10);
        Assert.All(doors, door => Assert.InRange(Math.Max(door.Width, door.Depth), 4, 6));
        Assert.InRange(forest.Length, 25, 40);
        Assert.All(forest, tree => Assert.True(tree.X > 27, "The forest stands east of the house."));
        Assert.Equal(Enumerable.Range(0, Tree.VariantCount), forest.Select(tree => tree.Variant).Distinct().Order());
        Assert.IsType<WallTool>(_game.Tools["wall-tool"]);
        Assert.IsType<TreeTool>(_game.Tools["tree-tool"]);
    }

    [Fact]
    public void WhenWalkingFromTheSpawnPoint_ThenEveryRoomIsReachableThroughItsDoors()
    {
        // Act
        var reachable = FindReachablePositions();

        // Assert
        Assert.All(Rooms, room => Assert.True(reachable.Contains((room.X, room.Y)), $"The {room.Name} cannot be reached."));
    }

    [Fact]
    public void WhenTheTreesStand_ThenNoneBlocksADoorOrTheSpawnPoint()
    {
        // Arrange
        var keepClear = _game.Objects.Values.OfType<Door>().Select(GetApproach).Append(new Footprint(-2, -2, 6, 6)).ToArray();

        // Act
        var blocking = _game.Objects.Values.OfType<Tree>().Where(tree => keepClear.Any(area => area.Intersects(tree.Footprint))).ToArray();

        // Assert
        Assert.Empty(blocking);
    }

    // The door and 3 tiles on both sides of it, where a player stands to walk through.
    private static Footprint GetApproach(Door door) => door.Width >= door.Depth
        ? new Footprint(door.X, door.Y - 3, door.Width, door.Depth + 6)
        : new Footprint(door.X - 3, door.Y, door.Width + 6, door.Depth);

    // Every position of a player's footprint that walking reaches from the spawn point at 0, 0 with every door passable.
    private HashSet<(int X, int Y)> FindReachablePositions()
    {
        var index = new GameWorldIndex();
        foreach (var gameObject in _game.Objects.Values.Where(gameObject => gameObject is not Door))
        {
            index.AddOrUpdate(gameObject);
        }

        var reachable = new HashSet<(int X, int Y)> { (0, 0) };
        var queue = new Queue<(int X, int Y)>();
        queue.Enqueue((0, 0));
        while (queue.TryDequeue(out var position))
        {
            foreach (var (offsetX, offsetY) in Steps)
            {
                var next = (X: position.X + offsetX, Y: position.Y + offsetY);
                if (IsWithinArea(next) && !index.IsBlocked(new Footprint(next.X, next.Y, GamePlayer.Size, GamePlayer.Size), ignored: null) && reachable.Add(next))
                {
                    queue.Enqueue(next);
                }
            }
        }

        return reachable;
    }

    private static bool IsWithinArea((int X, int Y) position) =>
        position.X is >= -70 and <= 120 && position.Y is >= -40 and <= 80;
}
