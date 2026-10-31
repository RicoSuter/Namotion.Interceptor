using HomeBlaze.Game.Tests.Testing;

namespace HomeBlaze.Game.Tests.Objects.Nature;

public class TreeTests
{
    private readonly GameTestContext _context = new();

    [Fact]
    public void WhenATreeIsCreated_ThenItsTrunkHasAFixedSizeAndBlocksMovementButNotSound()
    {
        // Arrange
        var tree = new Tree();

        // Act
        ((GameObject)tree).Width = 5;
        ((GameObject)tree).Depth = 6;

        // Assert
        Assert.Equal((Tree.Size, Tree.Size), (tree.Width, tree.Depth));
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
        Assert.Equal(first.Height, unknown.Height);
    }

    [Fact]
    public void WhenTheTreeIsTurnedAndScaled_ThenItsModelFollows()
    {
        // Arrange
        var tree = new Tree { Variant = 4, Rotation = 90, Scale = 110 };

        // Act
        var part = Assert.Single(tree.Visual.Parts);

        // Assert
        var size = 22 * (110 / 100.0);
        Assert.Equal(GameVisualShape.Model, part.Shape);
        Assert.Equal("_content/HomeBlaze.Game/objects/nature/models/pine_tree_1.glb", part.Model);
        Assert.Equal(new GameVisualVector(size, size, size), part.Size);
        Assert.Equal(new GameVisualVector((Tree.Size - size) / 2, 0, (Tree.Size - size) / 2), part.Offset);
        Assert.Equal(new GameVisualPoint(1, 1), part.Pivot);
        Assert.Equal(90, part.Rotation);
        Assert.Equal(GameVisualEffect.Wind, part.Effect);
        Assert.Equal((int)Math.Ceiling(size), tree.Height);
    }

    [Fact]
    public void WhenTheScaleIsOutsideTheRange_ThenTheTreeKeepsWithinIt()
    {
        // Arrange
        var tiny = new Tree { Scale = 0 };
        var smallest = new Tree { Scale = Tree.MinimumScale };

        // Act
        var visual = tiny.Visual;

        // Assert
        Assert.Equal(smallest.Visual, visual);
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
