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
        typeProvider.AddTypes([typeof(Game), typeof(GamePlayer), typeof(Wall), typeof(Door), typeof(Tree), typeof(Animal), typeof(WallTool), typeof(TreeTool)]);
        var serializer = new ConfigurableSubjectSerializer(typeProvider, new ServiceCollection().AddLogging().BuildServiceProvider());
        _game = Assert.IsType<Game>(serializer.Deserialize(File.ReadAllText(RepositoryPaths.Get(DemoWorldPath))));
    }

    [Fact]
    public void WhenTheDemoWorldLoads_ThenItHasAHouseAShedAForestAndTheTools()
    {
        // Act
        var walls = _game.GetObjects().Values.OfType<Wall>().ToArray();
        var doors = _game.GetObjects().Values.OfType<Door>().ToArray();
        var forest = _game.GetObjects()
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
    public void WhenTheDemoWorldLoads_ThenEveryObjectIsInTheRegionOfItsPosition()
    {
        // Act
        var misplaced = _game.Regions
            .SelectMany(pair => pair.Value.Objects.Select(gameObject => (Key: pair.Key, Name: gameObject.Key, Object: gameObject.Value)))
            .Where(entry => GetRegionKey(entry.Object) != entry.Key)
            .Select(entry => entry.Name)
            .ToArray();

        // Assert
        Assert.Empty(misplaced);
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
        var keepClear = _game.GetObjects().Values.OfType<Door>().Select(GetApproach).Append(new GameArea(-2, -2, 6, 6)).ToArray();

        // Act
        var blocking = _game.GetObjects().Values.OfType<Tree>().Where(tree => keepClear.Any(area => area.Intersects(tree.Area))).ToArray();

        // Assert
        Assert.Empty(blocking);
    }

    [Fact]
    public void WhenTheDemoWorldLoads_ThenTheAnimalsStandOnFreeTiles()
    {
        // Arrange
        var animals = _game.GetObjects().Values.OfType<Animal>().ToArray();
        var others = _game.GetObjects().Values.Where(gameObject => gameObject.IsMovementBlocking).ToArray();

        // Act
        var overlapping = animals
            .Where(animal => others.Any(other => !ReferenceEquals(other, animal) && other.Area.Intersects(animal.Area)))
            .ToArray();

        // Assert
        Assert.Equal(4, animals.Length);
        Assert.Equal([AnimalVariant.Deer, AnimalVariant.Stag], animals.Select(animal => animal.Variant).Distinct().Order());
        Assert.Empty(overlapping);
        Assert.All(animals, animal => Assert.True(animal.HomeX is not null && animal.HomeY is not null && animal.WanderRadius > 0));
    }

    // The door and 3 tiles on both sides of it, where a player stands to walk through.
    private static GameArea GetApproach(Door door) => door.Width >= door.Depth
        ? new GameArea(door.X, door.Y - 3, door.Width, door.Depth + 6)
        : new GameArea(door.X - 3, door.Y, door.Width + 6, door.Depth);

    // Every position of a player's footprint that walking reaches from the spawn point at 0, 0 with every door passable.
    private HashSet<(int X, int Y)> FindReachablePositions()
    {
        var index = new GameWorldIndex();
        foreach (var gameObject in _game.GetObjects().Values.Where(gameObject => gameObject is not Door))
        {
            index.AddOrUpdate(EntityFactory.Object(gameObject));
        }

        var reachable = new HashSet<(int X, int Y)> { (0, 0) };
        var queue = new Queue<(int X, int Y)>();
        queue.Enqueue((0, 0));
        while (queue.TryDequeue(out var position))
        {
            foreach (var (offsetX, offsetY) in Steps)
            {
                var next = (X: position.X + offsetX, Y: position.Y + offsetY);
                if (IsWithinArea(next) && !index.IsBlocked(new GameArea(next.X, next.Y, GamePlayer.Size, GamePlayer.Size), ignored: null) && reachable.Add(next))
                {
                    queue.Enqueue(next);
                }
            }
        }

        return reachable;
    }

    private static bool IsWithinArea((int X, int Y) position) =>
        position.X is >= -70 and <= 120 && position.Y is >= -40 and <= 80;

    private static string GetRegionKey(GameObject gameObject)
    {
        var (regionX, regionY) = GameRegion.GetCoordinates(gameObject.X, gameObject.Y);
        return GameRegion.GetKey(regionX, regionY);
    }
}
