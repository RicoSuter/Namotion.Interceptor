using HomeBlaze.Game.Tests.Testing;

namespace HomeBlaze.Game.Nature.Tests;

public class TreeToolTests
{
    private readonly GameTestContext _context = new();
    private readonly TreeTool _tool;

    public TreeToolTests()
    {
        _tool = _context.AddTool("tree-tool", new TreeTool(new Random(876)));
    }

    [Fact]
    public async Task WhenAPlayerPlantsATreeWithoutAVariant_ThenARandomTreeStandsInFront()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");

        // Act
        await _tool.PlantTreeAsync(null, new GameCaller(player.ClientId), CancellationToken.None);

        // Assert
        var tree = Assert.IsType<Tree>(Assert.Single(_context.Game.Objects).Value);
        Assert.Equal((GamePlayer.Size / 2, 2), (tree.X + tree.Width / 2, tree.Y));
        Assert.InRange(tree.Variant, 0, Tree.VariantCount - 1);
        Assert.InRange(tree.Rotation, 0, 359);
        Assert.InRange(tree.Scale, Tree.MinimumScale, Tree.MaximumScale);
        Assert.Equal(1, _context.Root.WriteCount);
        Assert.Same(tree, Assert.Single(_context.Game.GetTargetObjects(player)));
    }

    [Fact]
    public async Task WhenAVariantIsGiven_ThenTheTreeHasIt()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");

        // Act
        await _tool.PlantTreeAsync(5, new GameCaller(player.ClientId), CancellationToken.None);

        // Assert
        Assert.Equal(5, Assert.IsType<Tree>(Assert.Single(_context.Game.Objects).Value).Variant);
    }

    [Theory]
    [InlineData(0, 4)]
    [InlineData(6, 2)]
    public async Task WhenATreeOfAVariantIsPlanted_ThenItsWholeTrunkStandsInFrontCenteredOnThePlayer(int variant, int smallestSize)
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");

        // Act
        await _tool.PlantTreeAsync(variant, new GameCaller(player.ClientId), CancellationToken.None);

        // Assert
        var tree = Assert.IsType<Tree>(Assert.Single(_context.Game.Objects).Value);
        Assert.InRange(tree.Width, smallestSize, smallestSize * 2);
        Assert.Equal((GamePlayer.Size / 2, 2), (tree.X + tree.Width / 2, tree.Y));
    }

    [Fact]
    public async Task WhenManyTreesArePlanted_ThenRotationAndScaleVaryWithinTheirRanges()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        var rotations = new HashSet<int>();
        var scales = new HashSet<int>();

        // Act
        for (var index = 0; index < 20; index++)
        {
            await _tool.PlantTreeAsync(null, new GameCaller(player.ClientId), CancellationToken.None);
            var tree = Assert.IsType<Tree>(Assert.Single(_context.Game.Objects).Value);
            rotations.Add(tree.Rotation);
            scales.Add(tree.Scale);
            await tree.DemolishAsync(CancellationToken.None);
        }

        // Assert
        Assert.All(rotations, rotation => Assert.InRange(rotation, 0, 359));
        Assert.All(scales, scale => Assert.InRange(scale, Tree.MinimumScale, Tree.MaximumScale));
        Assert.True(rotations.Count > 1);
        Assert.True(scales.Count > 1);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(Tree.VariantCount)]
    public async Task WhenTheVariantIsOutOfRange_ThenPlantingIsRefused(int variant)
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _tool.PlantTreeAsync(variant, new GameCaller(player.ClientId), CancellationToken.None));
        Assert.Empty(_context.Game.Objects);
    }

    [Fact]
    public async Task WhenSomethingIsInTheWay_ThenPlantingIsRefused()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        _context.AddWall(1, 3, width: 1);

        // Act
        var exception = await Record.ExceptionAsync(() => _tool.PlantTreeAsync(null, new GameCaller(player.ClientId), CancellationToken.None));

        // Assert
        Assert.Equal("Something is in the way.", Assert.IsType<InvalidOperationException>(exception).Message);
        Assert.Single(_context.Game.Objects);
    }

    [Fact]
    public async Task WhenThereIsNoActor_ThenPlantingIsRefused()
    {
        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => _tool.PlantTreeAsync(null, null, CancellationToken.None));
    }
}
