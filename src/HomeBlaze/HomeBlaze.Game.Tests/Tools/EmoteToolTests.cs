using HomeBlaze.Game.Abstractions;
using HomeBlaze.Game.Tests.Testing;
using HomeBlaze.Game.Tools;

namespace HomeBlaze.Game.Tests.Tools;

public class EmoteToolTests
{
    private readonly GameTestContext _context = new();

    [Theory]
    [InlineData(nameof(EmoteTool.Clap), "Clapping")]
    [InlineData(nameof(EmoteTool.Sit), "Sitting")]
    [InlineData(nameof(EmoteTool.Jump), "Jump")]
    public void WhenAnEmoteIsInvoked_ThenThePlayerPlaysItsAnimation(string operation, string animation)
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        var tool = _context.AddTool("emotes", new EmoteTool());
        var caller = new GameCaller("tab-1");

        // Act
        typeof(EmoteTool).GetMethod(operation)!.Invoke(tool, [caller]);
        _context.Publish();

        // Assert
        Assert.Equal(animation, player.Animation);
    }

    [Fact]
    public void WhenAnEmoteIsInvokedWithoutAPlayer_ThenItThrows()
    {
        // Arrange
        var tool = _context.AddTool("emotes", new EmoteTool());

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => tool.Clap(null));
    }
}
