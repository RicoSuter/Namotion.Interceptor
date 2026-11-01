using HomeBlaze.Game.Tests.Testing;

namespace HomeBlaze.Game.Buildings.Tests;

public class WallToolTests
{
    private readonly GameTestContext _context = new();
    private readonly WallTool _tool;

    // Runs the operation's world command and publishes, so the subjects show its result.
    private async Task RunAsync(Task task)
    {
        await _context.RunAsync(task);
        _context.Publish();
    }

    public WallToolTests()
    {
        _tool = _context.AddTool("wall-tool", new WallTool());
    }

    [Fact]
    public async Task WhenAPlayerBuildsAWallWithoutParameters_ThenADefaultBrickWallStandsAcrossInFront()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        await _context.SaveAsync();
        var writesBefore = _context.Root.WriteCount;

        // Act
        await RunAsync(_tool.BuildWallAsync(null, null, null, new GameCaller(player.ClientId), CancellationToken.None));

        // Assert
        await _context.SaveAsync();
        var wall = Assert.IsType<Wall>(Assert.Single(_context.Game.Objects).Value);
        Assert.Equal((-1, 2, 4, 1, 10, WallMaterial.Bricks), (wall.X, wall.Y, wall.Width, wall.Depth, wall.Height, wall.Material));
        Assert.Equal(writesBefore + 1, _context.Root.WriteCount);
        Assert.Same(wall, Assert.Single(await _context.RunAsync(_context.Game.GetTargetsAsync(player.ClientId))));
    }

    [Theory]
    [InlineData(GameDirection.North, -3, 2, 8, 1)]
    [InlineData(GameDirection.East, 2, -3, 1, 8)]
    [InlineData(GameDirection.South, -3, -1, 8, 1)]
    [InlineData(GameDirection.West, -1, -3, 1, 8)]
    public async Task WhenThePlayerFacesADirection_ThenTheWallStandsAcrossItCenteredOnThePlayer(GameDirection facing, int x, int y, int width, int depth)
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        await _context.StepAsync(player.ClientId, facing, turnOnly: true);

        // Act
        await RunAsync(_tool.BuildWallAsync(8, null, null, new GameCaller(player.ClientId), CancellationToken.None));

        // Assert
        var wall = Assert.IsType<Wall>(Assert.Single(_context.Game.Objects).Value);
        Assert.Equal((x, y, width, depth), (wall.X, wall.Y, wall.Width, wall.Depth));
    }

    [Fact]
    public async Task WhenHeightAndMaterialAreGiven_ThenTheWallHasThem()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");

        // Act
        await RunAsync(_tool.BuildWallAsync(8, 12, WallMaterial.Concrete, new GameCaller(player.ClientId), CancellationToken.None));

        // Assert
        var wall = Assert.IsType<Wall>(Assert.Single(_context.Game.Objects).Value);
        Assert.Equal((8, 12, WallMaterial.Concrete), (wall.Width, wall.Height, wall.Material));
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(WallTool.MaximumLength + 1, null)]
    [InlineData(null, 0)]
    [InlineData(null, WallTool.MaximumHeight + 1)]
    public async Task WhenAParameterIsOutOfRange_ThenBuildingIsRefused(int? length, int? height)
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => RunAsync(_tool.BuildWallAsync(length, height, null, new GameCaller(player.ClientId), CancellationToken.None)));
        Assert.Empty(_context.Game.Objects);
    }

    [Fact]
    public async Task WhenTheMaterialIsUndefined_ThenBuildingIsRefused()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => RunAsync(_tool.BuildWallAsync(null, null, (WallMaterial)7, new GameCaller(player.ClientId), CancellationToken.None)));
    }

    [Fact]
    public async Task WhenSomethingIsInTheWay_ThenBuildingIsRefused()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        _context.AddWall(1, 2, width: 1);

        // Act
        var exception = await Record.ExceptionAsync(() => RunAsync(_tool.BuildWallAsync(null, null, null, new GameCaller(player.ClientId), CancellationToken.None)));

        // Assert
        Assert.Equal("Something is in the way.", Assert.IsType<InvalidOperationException>(exception).Message);
        Assert.Single(_context.Game.Objects);
    }

    [Fact]
    public async Task WhenThePlayerFacesDiagonally_ThenBuildingIsRefused()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        await _context.StepAsync(player.ClientId, GameDirection.NorthEast, turnOnly: true);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(_tool.BuildWallAsync(null, null, null, new GameCaller(player.ClientId), CancellationToken.None)));
    }

    [Fact]
    public async Task WhenThereIsNoActor_ThenBuildingIsRefused()
    {
        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(_tool.BuildWallAsync(null, null, null, null, CancellationToken.None)));
    }

    [Fact]
    public async Task WhenObjectsWereReplacedFromOutside_ThenBuildingAWallKeepsThem()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        var existing = new Wall { X = 20, Y = 20 };
        _context.Game.Objects = new Dictionary<string, GameObject> { ["loaded"] = existing };
        _context.Tick();

        // Act
        await RunAsync(_tool.BuildWallAsync(null, null, null, new GameCaller(player.ClientId), CancellationToken.None));

        // Assert
        Assert.Equal(2, _context.Game.Objects.Count);
        Assert.Same(existing, _context.Game.Objects["loaded"]);
    }

    [Fact]
    public async Task WhenTheToolIsNotPartOfAGame_ThenBuildingIsRefused()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(new WallTool().BuildWallAsync(null, null, null, new GameCaller(player.ClientId), CancellationToken.None)));
    }
}
