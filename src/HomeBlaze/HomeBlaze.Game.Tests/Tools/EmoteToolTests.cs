using HomeBlaze.Game.Abstractions;
using HomeBlaze.Game.Tests.Testing;
using HomeBlaze.Game.Tools;

namespace HomeBlaze.Game.Tests.Tools;

public class EmoteToolTests
{
    private readonly GameTestContext _context = new();

    public static TheoryData<Action<EmoteTool, GameCaller>, string, GameVisualAnimationMode> Emotes => new()
    {
        { (tool, caller) => tool.Clap(caller), "Clapping", GameVisualAnimationMode.Once },
        { (tool, caller) => tool.Sit(caller), "Sitting", GameVisualAnimationMode.Loop },
        { (tool, caller) => tool.Jump(caller), "Jump", GameVisualAnimationMode.Once },
    };

    [Theory]
    [MemberData(nameof(Emotes))]
    public void WhenAnEmoteIsInvoked_ThenThePlayerPlaysItsAnimation(
        Action<EmoteTool, GameCaller> emote, string animation, GameVisualAnimationMode mode)
    {
        // Arrange
        var sink = new RecordingViewSink();
        var player = _context.AddConnectedPlayer("tab-1");
        using var view = _context.OpenView("tab-1", sink);
        var tool = _context.AddTool("emotes", new EmoteTool());

        // Act
        emote(tool, new GameCaller("tab-1"));
        _context.Tick();

        // Assert
        var payload = sink.LastSelf().Payload;
        Assert.Equal(animation, player.Animation);
        Assert.Equal(animation, payload.Animation);
        Assert.Equal((int)mode, payload.AnimationMode);
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
