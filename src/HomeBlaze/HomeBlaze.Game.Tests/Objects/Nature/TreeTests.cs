using HomeBlaze.Game.Tests.Testing;

namespace HomeBlaze.Game.Tests.Objects.Nature;

public class TreeTests
{
    private readonly GameTestContext _context = new();

    [Fact]
    public void WhenATreeIsCreated_ThenItsTrunkHasTheSizeOfItsVariantAndBlocksMovementButNotSound()
    {
        // Arrange
        var tree = new Tree();

        // Act
        ((GameObject)tree).Width = 5;
        ((GameObject)tree).Depth = 7;

        // Assert
        Assert.Equal((6, 6), (tree.Width, tree.Depth));
        Assert.True(tree.IsMovementBlocking);
        Assert.False(tree.IsSoundBlocking);
        Assert.True(tree.IsVisible);
        Assert.Equal(24, tree.Height);
    }

    [Fact]
    public void WhenTheVariantIsOutsideTheTable_ThenTheTreeLooksLikeVariantZero()
    {
        // Arrange
        var unknown = new Tree { Variant = 99 };
        var first = new Tree { Variant = 0 };

        // Act
        var visual = unknown.Visual;

        // Assert
        Assert.Equal(first.Visual, visual);
        Assert.Equal((first.Width, first.Depth, first.Height), (unknown.Width, unknown.Depth, unknown.Height));
    }

    [Theory]
    [InlineData(0, 100, 6)]
    [InlineData(0, 80, 4)]
    [InlineData(0, 120, 8)]
    [InlineData(1, 100, 4)]
    [InlineData(3, 120, 4)]
    [InlineData(4, 100, 4)]
    [InlineData(5, 100, 2)]
    [InlineData(6, 80, 2)]
    [InlineData(6, 120, 2)]
    [InlineData(0, 0, 4)]
    public void WhenAVariantHasAScale_ThenTheTrunkCoversTheScaledEvenSizeOfItsVariant(int variant, int scale, int size)
    {
        // Act
        var tree = new Tree { Variant = variant, Scale = scale };

        // Assert
        Assert.Equal((size, size), (tree.Width, tree.Depth));
    }

    [Fact]
    public void WhenTheTreeIsTurnedAndScaled_ThenItsModelFollows()
    {
        // Arrange
        var tree = new Tree { Variant = 4, Rotation = 90, Scale = 110 };

        // Act
        var part = Assert.Single(tree.Visual.Parts);

        // Assert
        var height = 22 * (110 / 100.0);
        var crown = 13 * (110 / 100.0);
        Assert.Equal(4, tree.Width);
        Assert.Equal(GameVisualShape.Model, part.Shape);
        Assert.Equal("_content/HomeBlaze.Game/objects/nature/models/pine_tree_1.glb", part.Model);
        Assert.Equal(new GameVisualVector(crown, height, crown), part.Size);
        Assert.Equal(new GameVisualVector((4 - crown) / 2, 0, (4 - crown) / 2), part.Offset);
        Assert.Equal(new GameVisualPoint(2, 2), part.Pivot);
        Assert.Equal(90, part.Rotation);
        Assert.Equal(GameVisualEffect.Wind, part.Effect);
        Assert.Equal((int)Math.Ceiling(height), tree.Height);
    }

    [Theory]
    [InlineData(-90, 270)]
    [InlineData(360, 0)]
    [InlineData(450, 90)]
    [InlineData(int.MinValue, 232)]
    public void WhenTheRotationIsOutsideAFullTurn_ThenTheModelTurnsByTheSameAngleWithinIt(int rotation, int expected)
    {
        // Arrange
        var tree = new Tree { Rotation = rotation };

        // Act
        var part = Assert.Single(tree.Visual.Parts);

        // Assert
        Assert.Equal(expected, part.Rotation);
    }

    [Theory]
    [InlineData(0, Tree.MinimumScale)]
    [InlineData(1000, Tree.MaximumScale)]
    public void WhenTheScaleIsOutsideTheRange_ThenTheTreeLooksLikeTheNearestLimit(int scale, int limit)
    {
        // Arrange
        var outside = new Tree { Scale = scale };
        var nearest = new Tree { Scale = limit };

        // Act
        var visual = outside.Visual;

        // Assert
        Assert.Equal(nearest.Visual, visual);
        Assert.Equal((nearest.Width, nearest.Height), (outside.Width, outside.Height));
    }

    [Fact]
    public void WhenTheTreeIsUnchanged_ThenItsVisualIsTheSameInstance()
    {
        // Arrange
        var tree = new Tree { Variant = 2 };
        var first = tree.Visual;

        // Act
        var second = tree.Visual;

        // Assert
        Assert.Same(first, second);
    }

    [Fact]
    public void WhenTheVariantsAreListed_ThenEveryModelFileExists()
    {
        // Act
        var paths = Tree.Variants.Select(variant => $"src/HomeBlaze/HomeBlaze.Game/wwwroot/objects/nature/models/{variant.File}").ToArray();

        // Assert
        Assert.Equal(Tree.VariantCount, Tree.Variants.Length);
        Assert.All(paths, path => Assert.True(File.Exists(RepositoryPaths.Get(path))));
    }

    [Fact]
    public void WhenATreeStandsInFront_ThenThePlayerCannotStepIntoIt()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        _context.AddObject("tree", new Tree { X = 0, Y = 2 });

        // Act
        _context.TimeProvider.Advance(Game.StepInterval);
        var result = _context.Game.TryMove(player, GameDirection.North, turnOnly: false);

        // Assert
        Assert.False(result.IsAccepted);
        Assert.Equal((0, 0), (player.X, player.Y));
    }

    [Theory]
    [InlineData(-3, false)]
    [InlineData(3, false)]
    [InlineData(-4, true)]
    [InlineData(4, true)]
    public void WhenAPlayerStepsAlongsideTheTrunk_ThenOnlyAStepIntoItsEdgeIsRefused(int playerX, bool isAccepted)
    {
        // Arrange
        // The trunk covers -2 to 3 from west to east.
        var player = _context.AddConnectedPlayer("tab-1", x: playerX);
        _context.AddObject("tree", new Tree { X = -2, Y = 2, Variant = 0 });

        // Act
        _context.TimeProvider.Advance(Game.StepInterval);
        var result = _context.Game.TryMove(player, GameDirection.North, turnOnly: false);

        // Assert
        Assert.Equal(isAccepted, result.IsAccepted);
    }

    [Fact]
    public async Task WhenATreeIsDemolished_ThenItIsRemovedAndWritten()
    {
        // Arrange
        var tree = _context.AddObject("tree", new Tree { X = 10, Y = 10 });

        // Act
        await tree.DemolishAsync(CancellationToken.None);

        // Assert
        Assert.Empty(_context.Game.Objects);
        Assert.Equal(1, _context.Root.WriteCount);
    }
}
