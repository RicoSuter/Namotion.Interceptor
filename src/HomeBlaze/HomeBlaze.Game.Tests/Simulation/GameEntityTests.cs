using HomeBlaze.Game.Simulation;
using HomeBlaze.Game.Tests.Testing;

namespace HomeBlaze.Game.Tests.Simulation;

public class GameEntityTests
{
    [Fact]
    public void WhenTheSubjectIsReadForTheFirstTime_ThenItsValuesAreCopied()
    {
        // Arrange
        var door = new Door { X = 3, Y = 4 };
        var entity = new GameEntity(door);

        // Act
        var isChanged = entity.ReadFromSubject();

        // Assert
        Assert.True(isChanged);
        Assert.Equal(door.Area, entity.Area);
        Assert.Equal(door.Height, entity.Height);
        Assert.Equal(door.IsMovementBlocking, entity.IsMovementBlocking);
        Assert.Equal(door.IsSoundBlocking, entity.IsSoundBlocking);
        Assert.Equal(door.IsVisible, entity.IsVisible);
        Assert.Same(door.Visual, entity.Visual);
        Assert.Equal(door.Title, entity.Title);
    }

    [Fact]
    public void WhenTheSubjectIsUnchanged_ThenReadingAgainReportsNoChange()
    {
        // Arrange
        var entity = new GameEntity(new Wall { X = 1, Y = 2 });
        entity.ReadFromSubject();

        // Act
        var isChanged = entity.ReadFromSubject();

        // Assert
        Assert.False(isChanged);
    }

    [Fact]
    public void WhenOnlyAnEqualVisualInstanceIsReturned_ThenReadingReportsNoChange()
    {
        // Arrange
        var subject = new VisualObject { CustomVisual = GameVisual.Box(1, 2, 1) };
        var entity = new GameEntity(subject);
        entity.ReadFromSubject();

        // Act
        subject.CustomVisual = GameVisual.Box(1, 2, 1);
        var isChanged = entity.ReadFromSubject();

        // Assert
        Assert.False(isChanged);
    }

    [Fact]
    public void WhenTheSubjectChanges_ThenReadingReportsTheChangeAndCopiesIt()
    {
        // Arrange
        var door = new Door { X = 0, Y = 0 };
        var entity = new GameEntity(door);
        entity.ReadFromSubject();

        // Act
        door.IsOpen = true;
        var isChanged = entity.ReadFromSubject();

        // Assert
        Assert.True(isChanged);
        Assert.False(entity.IsMovementBlocking);
        Assert.Same(door.Visual, entity.Visual);
    }

    [Fact]
    public void WhenAPlayerEntityIsConnectedAndDisconnected_ThenItBlocksAndIsVisibleOnlyWhileConnected()
    {
        // Arrange
        var entity = new PlayerEntity(new GamePlayer(), "client");

        // Act
        entity.SetConnected(true);
        var isConnectedBlocking = entity.IsMovementBlocking && entity.IsVisible;
        entity.SetConnected(false);

        // Assert
        Assert.True(isConnectedBlocking);
        Assert.False(entity.IsConnected);
        Assert.False(entity.IsMovementBlocking);
        Assert.False(entity.IsVisible);
        Assert.Equal(new GameArea(0, 0, GamePlayer.Size, GamePlayer.Size), entity.Area);
        Assert.Equal(GamePlayer.BodyHeight, entity.Height);
        Assert.Null(entity.Visual);
    }
}
