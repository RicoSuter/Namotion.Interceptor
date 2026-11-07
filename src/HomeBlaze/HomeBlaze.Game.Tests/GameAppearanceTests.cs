using HomeBlaze.Game.People;
using HomeBlaze.Game.Tests.Testing;
using HomeBlaze.Game.View;

namespace HomeBlaze.Game.Tests;

public class GameAppearanceTests
{
    private readonly GameTestContext _context = new();

    [Fact]
    public void WhenAPlayerHasNoAppearance_ThenItsViewShowsTheDefaults()
    {
        // Arrange
        var sink = new RecordingViewSink();
        _context.AddConnectedPlayer("tab-1");
        using var view = _context.OpenView("tab-1", sink);

        // Act
        _context.Tick();

        // Assert
        var self = sink.LastSelf();
        Assert.Equal(GamePeople.GetDefault("tab-1"), self.Payload.Character);
        Assert.Equal(GameColors.FromId("tab-1"), self.Payload.Color);
    }

    [Fact]
    public void WhenThePersonAndColorAreWrittenFromOutside_ThenTheViewShowsThemAfterATick()
    {
        // Arrange
        var sink = new RecordingViewSink();
        var player = _context.AddConnectedPlayer("tab-1");
        using var view = _context.OpenView("tab-1", sink);
        _context.Tick();

        // Act
        player.Character = "female-casual";
        player.Color = "#112233";
        _context.Tick();

        // Assert
        var self = sink.LastSelf();
        Assert.Equal("female-casual", self.Payload.Character);
        Assert.Equal("#112233", self.Payload.Color);
    }

    [Fact]
    public void WhenAnInvalidPersonIsWritten_ThenTheViewShowsTheDefault()
    {
        // Arrange
        var sink = new RecordingViewSink();
        var player = _context.AddConnectedPlayer("tab-1");
        using var view = _context.OpenView("tab-1", sink);
        player.Character = "male-shirt";
        _context.Tick();

        // Act
        player.Character = "robot";
        _context.Tick();

        // Assert
        Assert.Equal(GamePeople.GetDefault("tab-1"), sink.LastSelf().Payload.Character);
    }
}
