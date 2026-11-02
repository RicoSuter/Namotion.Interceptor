using HomeBlaze.Game.Tests.Testing;

namespace HomeBlaze.Game.Tests;

public class GameActionsTests
{
    private readonly GameTestContext _context = new();

    [Fact]
    public void WhenTheCallersClientHasNoPlayerInTheGame_ThenThereIsNoActor()
    {
        // Arrange
        _context.AddConnectedPlayer("tab-1");

        // Act
        var actor = _context.Game.GetActor(new GameCaller("tab-2"));

        // Assert
        Assert.Null(actor);
    }

    [Fact]
    public void WhenTheCallersPlayerIsConnected_ThenItIsTheActor()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");

        // Act
        var actor = _context.Game.GetActor(new GameCaller("tab-1"));

        // Assert
        Assert.Same(player, actor);
    }

    [Fact]
    public void WhenTheCallersPlayerIsDisconnected_ThenThereIsNoActor()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1", isNamed: true);
        _context.Clients.Set("tab-1", isNamed: true, isConnected: false);
        _context.Reconcile();
        Assert.False(player.IsConnected);

        // Act
        var actor = _context.Game.GetActor(new GameCaller("tab-1"));

        // Assert
        Assert.Null(actor);
    }

    [Fact]
    public async Task WhenAWallIsDemolished_ThenItIsRemovedAndWritten()
    {
        // Arrange
        var wall = _context.AddWall(5, 5);

        await _context.SaveAsync();
        var writesBefore = _context.Root.WriteCount;

        // Act
        await _context.RunAsync(wall.DemolishAsync(CancellationToken.None));
        _context.Publish();
        await _context.SaveAsync();

        // Assert
        Assert.Empty(_context.Game.GetObjects());
        Assert.Equal(writesBefore + 1, _context.Root.WriteCount);
    }

    [Fact]
    public async Task WhenAPlayerSendsAMessage_ThenTheOtherClientGetsItWithTheSender()
    {
        // Arrange
        var sender = _context.AddConnectedPlayer("tab-1");
        _context.Clients.Set("tab-1", isNamed: true, isConnected: true, title: "Kitchen");
        _context.Reconcile();
        var recipient = _context.AddConnectedPlayer("tab-2", x: 0, y: 2);

        // Act
        await _context.RunAsync(recipient.SendMessageAsync("  Hello  ", new GameCaller(sender.ClientId), CancellationToken.None));

        // Assert
        var (clientId, notification) = Assert.Single(_context.Clients.Notifications);
        Assert.Equal("tab-2", clientId);
        Assert.Equal("Message from Kitchen", notification.Title);
        Assert.Equal("Hello", notification.Message);
    }

    [Fact]
    public async Task WhenTheCallerIsNotAPlayerOfTheGame_ThenSendingIsRefused()
    {
        // Arrange
        var recipient = _context.AddConnectedPlayer("tab-1");

        // Act
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _context.RunAsync(recipient.SendMessageAsync("Hello", new GameCaller("tab-2"), CancellationToken.None)));

        // Assert
        Assert.Equal("Only a player in the game can send messages.", exception.Message);
        Assert.Empty(_context.Clients.Notifications);
    }

    [Fact]
    public async Task WhenThereIsNoCaller_ThenTheMessageIsSentWithoutASender()
    {
        // Arrange
        var recipient = _context.AddConnectedPlayer("tab-1");

        // Act
        await _context.RunAsync(recipient.SendMessageAsync("Hello", null, CancellationToken.None));

        // Assert
        var (clientId, notification) = Assert.Single(_context.Clients.Notifications);
        Assert.Equal("tab-1", clientId);
        Assert.Equal("Message", notification.Title);
    }

    [Fact]
    public async Task WhenAMessageIsTooLong_ThenSendingIsRefused()
    {
        // Arrange
        var sender = _context.AddConnectedPlayer("tab-1");
        var recipient = _context.AddConnectedPlayer("tab-2", x: 0, y: 2);
        var message = new string('a', GamePlayer.MaximumMessageLength + 1);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => _context.RunAsync(recipient.SendMessageAsync(message, new GameCaller(sender.ClientId), CancellationToken.None)));
        Assert.Empty(_context.Clients.Notifications);
    }

    [Fact]
    public async Task WhenAPlayerSendsTwoMessagesWithinTheInterval_ThenTheSecondIsRefused()
    {
        // Arrange
        var sender = _context.AddConnectedPlayer("tab-1");
        var recipient = _context.AddConnectedPlayer("tab-2", x: 0, y: 2);
        var caller = new GameCaller(sender.ClientId);
        var first = recipient.SendMessageAsync("First", caller, CancellationToken.None);
        _context.Tick();
        await first;

        // Act
        var second = recipient.SendMessageAsync("Second", caller, CancellationToken.None);
        _context.Tick();
        var exception = await Record.ExceptionAsync(() => second);
        _context.TimeProvider.Advance(Game.MessageInterval);
        var third = recipient.SendMessageAsync("Third", caller, CancellationToken.None);
        _context.Tick();
        await third;

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Equal(new[] { "First", "Third" }, _context.Clients.Notifications.Select(notification => notification.Notification.Message));
    }
}
