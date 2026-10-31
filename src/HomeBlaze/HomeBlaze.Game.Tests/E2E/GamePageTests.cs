using System.Globalization;
using System.Text.RegularExpressions;
using HomeBlaze.E2E.Tests.Infrastructure;
using HomeBlaze.Game.Voice;
using HomeBlaze.Installation;
using Microsoft.Playwright;
using Namotion.Interceptor.Testing;

namespace HomeBlaze.Game.Tests.E2E;

/// <remarks>
/// Every installation sees all circuits of the shared server, and the circuits of closed browser contexts stay connected
/// for minutes, so the players of earlier tests may stand in a new game. The tests read where their own player joined
/// and assert relative to it.
/// </remarks>
[Collection(nameof(GameE2ECollection))]
[Trait("Category", "Integration")]
public class GamePageTests
{
    private const int Timeout = 30000;
    private const string SelfSuffix = " (you)";
    private const string ReceivedFramesAttribute = "data-voice-received-frames";
    private const string SentFramesAttribute = "data-voice-sent-frames";
    private const string TrimmedAttribute = "data-voice-trimmed";
    private const string UnderrunsAttribute = "data-voice-underruns";

    private readonly PlaywrightFixture _fixture;

    public GamePageTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task WhenTheGamePageOpens_ThenThePlayerJoinsFacingNorth()
    {
        // Arrange
        await using var world = await GameWorld.CreateAsync(_fixture);

        // Act
        var (page, player) = await world.JoinAsync();

        // Assert
        await Expect(page.GetByTestId("game-position")).ToHaveTextAsync(FormatPosition(player.X, player.Y, "N"), new() { Timeout = Timeout });
        await Expect(page.GetByTestId("game-nearby-player").Filter(new() { HasText = SelfSuffix })).ToHaveCountAsync(1, new() { Timeout = Timeout });
        Assert.True(player.IsConnected);
        Assert.Equal(GameDirection.North, player.Facing);
    }

    [Fact]
    public async Task WhenArrowRightIsPressed_ThenThePlayerStepsEast()
    {
        // Arrange
        await using var world = await GameWorld.CreateAsync(_fixture);
        var (page, player) = await world.JoinAsync();
        var (x, y) = (player.X, player.Y);

        // Act
        await page.Keyboard.PressAsync("ArrowRight");

        // Assert
        await Expect(page.GetByTestId("game-position")).ToHaveTextAsync(FormatPosition(x + 1, y, "E"), new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenShiftAndAnArrowArePressed_ThenThePlayerOnlyTurns()
    {
        // Arrange
        await using var world = await GameWorld.CreateAsync(_fixture);
        var (page, player) = await world.JoinAsync();
        var (x, y) = (player.X, player.Y);

        // Act
        await page.Keyboard.PressAsync("Shift+ArrowLeft");

        // Assert
        await Expect(page.GetByTestId("game-position")).ToHaveTextAsync(FormatPosition(x, y, "W"), new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenTheEastPadButtonIsTappedOnAPhone_ThenThePlayerStepsEast()
    {
        // Arrange
        await using var world = await GameWorld.CreateAsync(_fixture);
        var (page, player) = await world.JoinAsync(new() { ViewportSize = new() { Width = 390, Height = 844 }, HasTouch = true, IsMobile = true });
        var (x, y) = (player.X, player.Y);

        // Act
        await page.GetByTestId("game-pad-east").TapAsync(new() { Timeout = Timeout });

        // Assert
        await Expect(page.GetByTestId("game-position")).ToHaveTextAsync(FormatPosition(x + 1, y, "E"), new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenWalkingIntoAWall_ThenThePositionStays()
    {
        // Arrange
        await using var world = await GameWorld.CreateAsync(_fixture);
        var (page, player) = await world.JoinAsync();
        var (x, y) = (player.X, player.Y);

        // Right in front of the player, who faces north.
        world.Game.Objects = new Dictionary<string, GameObject> { ["wall"] = new Wall { X = x - 1, Y = y + GamePlayer.Size } };
        await Expect(page.GetByTestId("game-target-title")).ToHaveTextAsync("Wall", new() { Timeout = Timeout });

        // Act
        await page.Keyboard.PressAsync("ArrowUp");
        // The circuit answers the turn after the step, so the turn shows only once the step was decided.
        await page.Keyboard.PressAsync("Shift+ArrowLeft");

        // Assert
        await Expect(page.GetByTestId("game-position")).ToHaveTextAsync(FormatPosition(x, y, "W"), new() { Timeout = Timeout });
        Assert.Equal((x, y), (player.X, player.Y));
    }

    [Fact]
    public async Task WhenAWallIsBuiltAndDemolished_ThenItAppearsAndDisappears()
    {
        // Arrange
        await using var world = await GameWorld.CreateAsync(_fixture);
        var (page, player) = await world.JoinAsync();

        // Act
        await page.GetByTestId("game-action").Filter(new() { HasText = "Build wall" }).ClickAsync(new() { Timeout = Timeout });
        await AsyncTestHelpers.WaitUntilAsync(() => world.Game.Objects.Count == 1, message: "The wall was not built");
        var wall = Assert.IsType<Wall>(Assert.Single(world.Game.Objects.Values));
        await page.GetByTestId("game-target-action").Filter(new() { HasText = "Demolish" }).ClickAsync(new() { Timeout = Timeout });

        // Assert
        Assert.Equal(player.Y + GamePlayer.Size, wall.Y);
        await AsyncTestHelpers.WaitUntilAsync(() => world.Game.Objects.Count == 0, message: "The wall was not demolished");
        await Expect(page.GetByTestId("game-looking-at")).ToHaveCountAsync(0, new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenAClosedDoorIsOpenedWithItsNumberKey_ThenThePlayerStepsThroughIt()
    {
        // Arrange
        await using var world = await GameWorld.CreateAsync(_fixture);
        var (page, player) = await world.JoinAsync();
        var (x, y) = (player.X, player.Y);

        // Right in front of the player, who faces north.
        var door = new Door { X = x - 1, Y = y + GamePlayer.Size };
        world.Game.Objects = new Dictionary<string, GameObject> { ["door"] = door };
        await Expect(page.GetByTestId("game-target-title")).ToHaveTextAsync("Door", new() { Timeout = Timeout });
        await Expect(page.GetByTestId("game-target-action")).ToHaveTextAsync(["1Open"], new() { Timeout = Timeout });

        // Act
        await page.Keyboard.PressAsync("1");
        await Expect(page.GetByTestId("game-target-action")).ToHaveTextAsync(["1Close"], new() { Timeout = Timeout });
        for (var step = 0; step < 3; step++)
        {
            await page.Keyboard.PressAsync("ArrowUp");
        }

        // Assert
        Assert.True(door.IsOpen);
        await Expect(page.GetByTestId("game-position")).ToHaveTextAsync(FormatPosition(x, y + 3, "N"), new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenTwoBrowsersPlay_ThenTheySeeEachOtherAndOneCanMessageTheOther()
    {
        // Arrange
        await using var world = await GameWorld.CreateAsync(_fixture);
        var (first, firstPlayer) = await world.JoinAsync();
        var (second, secondPlayer) = await world.JoinAsync();

        // Right in front of the first player, who faces north.
        secondPlayer.X = firstPlayer.X;
        secondPlayer.Y = firstPlayer.Y + GamePlayer.Size;
        await Expect(first.GetByTestId("game-nearby-player").Filter(new() { HasText = secondPlayer.Title })).ToHaveCountAsync(1, new() { Timeout = Timeout });
        await Expect(second.GetByTestId("game-nearby-player").Filter(new() { HasText = firstPlayer.Title })).ToHaveCountAsync(1, new() { Timeout = Timeout });
        await Expect(first.GetByTestId("game-target-title")).ToHaveTextAsync(secondPlayer.Title!, new() { Timeout = Timeout });

        // Act
        await first.GetByTestId("game-target-action").Filter(new() { HasText = "Send message" }).ClickAsync(new() { Timeout = Timeout });
        await first.Locator(".mud-dialog input, .mud-dialog textarea").First.FillAsync("Hello from the game", new() { Timeout = Timeout });
        await first.GetByRole(AriaRole.Button, new() { Name = "Execute" }).ClickAsync(new() { Timeout = Timeout });

        // Assert
        await Expect(second.GetByTestId("client-notification-message")).ToHaveTextAsync("Hello from the game", new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenTwoPlayersTurnOnMicrophoneAndSpeaker_ThenTheListenerPlaysTheTalkersFramesAndCountsTrimsAndUnderruns()
    {
        // Arrange
        await using var browser = await MediaBrowser.LaunchAsync();
        await using var world = await GameWorld.CreateAsync(_fixture);
        var (talker, talkerPlayer) = await world.JoinAsync(CreateMicrophoneContext(), browser.Browser);
        var (listener, listenerPlayer) = await world.JoinAsync(CreateMicrophoneContext(), browser.Browser);

        // One meter east of the listener, well within the hearing range.
        talkerPlayer.X = listenerPlayer.X + 4;
        talkerPlayer.Y = listenerPlayer.Y;

        // Act
        await TurnOnVoiceAsync(talker);
        await TurnOnVoiceAsync(listener);

        // Assert
        await WaitForCounterAsync(talker, SentFramesAttribute, 25);
        await WaitForCounterAsync(listener, ReceivedFramesAttribute, 100);
        // Only present, not 0: headless Chromium renders audio in callbacks of 4096 samples (about 85 ms), so the playback
        // buffer peaks near its maximum and trims although the frames arrive steadily.
        var panel = listener.GetByTestId("game-panel");
        await Expect(panel).ToHaveAttributeAsync(TrimmedAttribute, new Regex("^[0-9]+$"), new() { Timeout = Timeout });
        await Expect(panel).ToHaveAttributeAsync(UnderrunsAttribute, new Regex("^[0-9]+$"), new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenTheTalkerMovesOutOfRange_ThenNearbyDropsItAndItsFramesStop()
    {
        // Arrange
        await using var browser = await MediaBrowser.LaunchAsync();
        await using var world = await GameWorld.CreateAsync(_fixture);
        var (talker, talkerPlayer) = await world.JoinAsync(CreateMicrophoneContext(), browser.Browser);
        var (listener, listenerPlayer) = await world.JoinAsync(CreateMicrophoneContext(), browser.Browser);
        talkerPlayer.X = listenerPlayer.X + 4;
        talkerPlayer.Y = listenerPlayer.Y;
        await TurnOnVoiceAsync(talker);
        await TurnOnVoiceAsync(listener);
        var talkerEntry = listener.GetByTestId("game-nearby-player").Filter(new() { HasText = talkerPlayer.Title });
        await Expect(talkerEntry).ToHaveCountAsync(1, new() { Timeout = Timeout });
        await Expect(talkerEntry.GetByTestId("game-nearby-microphone-off")).ToHaveCountAsync(0, new() { Timeout = Timeout });
        await WaitForCounterAsync(listener, ReceivedFramesAttribute, 25);

        // Act
        // 25 m east: beyond the hearing range of 10 m, but still within the view of 50 m.
        talkerPlayer.X = listenerPlayer.X + 100;

        // Assert
        await Expect(talkerEntry).ToHaveCountAsync(0, new() { Timeout = Timeout });
        var received = await ReadCounterAsync(listener, ReceivedFramesAttribute);
        var sent = await ReadCounterAsync(talker, SentFramesAttribute);
        await WaitForCounterAsync(talker, SentFramesAttribute, sent + 50);
        Assert.InRange(await ReadCounterAsync(listener, ReceivedFramesAttribute), received, received + GameVoiceSettings.MaximumPendingSends);
    }

    [Fact]
    public async Task WhenAnotherPageOfThePlayerTakesTheMicrophoneOver_ThenThePageTurnsItsMicrophoneOff()
    {
        // Arrange
        await using var browser = await MediaBrowser.LaunchAsync();
        await using var world = await GameWorld.CreateAsync(_fixture);
        var (page, player) = await world.JoinAsync(CreateMicrophoneContext(), browser.Browser);
        var microphone = page.GetByTestId("game-microphone");
        await microphone.ClickAsync(new() { Timeout = Timeout });
        await Expect(microphone).ToHaveAttributeAsync("aria-pressed", "true", new() { Timeout = Timeout });

        // Act
        // Page ids count up from 1, so no page of the server has this one.
        Assert.True(world.Game.SetMicrophone(new GameCaller(player), pageId: -1, isOn: true));

        // Assert
        await Expect(microphone).ToHaveAttributeAsync("aria-pressed", "false", new() { Timeout = Timeout });
        Assert.True(player.IsMicrophoneOn);
    }

    [Fact]
    public async Task WhenThePageLosesItsSpeakerInTheGame_ThenThePageTurnsItsSpeakerOff()
    {
        // Arrange
        await using var browser = await MediaBrowser.LaunchAsync();
        await using var world = await GameWorld.CreateAsync(_fixture);
        var (page, player) = await world.JoinAsync(CreateMicrophoneContext(), browser.Browser);
        var audio = page.GetByTestId("game-audio");
        await audio.ClickAsync(new() { Timeout = Timeout });
        await Expect(audio).ToHaveAttributeAsync("aria-pressed", "true", new() { Timeout = Timeout });
        var pageId = Assert.Single(player.AudioPageIds!);

        // Act
        Assert.True(world.Game.SetAudio(new GameCaller(player), pageId, isOn: false));

        // Assert
        await Expect(audio).ToHaveAttributeAsync("aria-pressed", "false", new() { Timeout = Timeout });
        Assert.False(player.IsAudioOn);
    }

    [Fact]
    public async Task WhenAPageWithVoiceOnCloses_ThenItsTogglesDropAndTheListenerKeepsItsSpeaker()
    {
        // Arrange
        await using var browser = await MediaBrowser.LaunchAsync();
        await using var world = await GameWorld.CreateAsync(_fixture);
        var (talker, talkerPlayer) = await world.JoinAsync(CreateMicrophoneContext(), browser.Browser);
        var (listener, listenerPlayer) = await world.JoinAsync(CreateMicrophoneContext(), browser.Browser);
        talkerPlayer.X = listenerPlayer.X + 4;
        talkerPlayer.Y = listenerPlayer.Y;
        await TurnOnVoiceAsync(talker);
        await TurnOnVoiceAsync(listener);
        var isTalkerOn = talkerPlayer.IsMicrophoneOn && talkerPlayer.IsAudioOn;

        // Act
        await talker.CloseAsync();

        // Assert
        Assert.True(isTalkerOn);
        await AsyncTestHelpers.WaitUntilAsync(() => !talkerPlayer.IsMicrophoneOn && !talkerPlayer.IsAudioOn,
            message: "The toggles of the closed page stayed on");
        // The listener's page heard of the change once Nearby no longer shows the talker with its microphone on.
        var talkerWithMicrophone = listener.GetByTestId("game-nearby-player")
            .Filter(new() { HasText = talkerPlayer.Title, HasNot = listener.GetByTestId("game-nearby-microphone-off") });
        await Expect(talkerWithMicrophone).ToHaveCountAsync(0, new() { Timeout = Timeout });
        await Expect(listener.GetByTestId("game-audio")).ToHaveAttributeAsync("aria-pressed", "true", new() { Timeout = Timeout });
        Assert.True(listenerPlayer.IsAudioOn);
    }

    private static string FormatPosition(int x, int y, string facing) => $"X {x} · Y {y} · facing {facing}";

    private static BrowserNewContextOptions CreateMicrophoneContext() => new() { Permissions = ["microphone"] };

    private static async Task TurnOnVoiceAsync(IPage page)
    {
        foreach (var testId in new[] { "game-microphone", "game-audio" })
        {
            var button = page.GetByTestId(testId);
            await button.ClickAsync(new() { Timeout = Timeout });
            await Expect(button).ToHaveAttributeAsync("aria-pressed", "true", new() { Timeout = Timeout });
        }
    }

    // game-voice.js counts the frames on the panel, which Playwright polls without a fixed wait.
    private static Task WaitForCounterAsync(IPage page, string attribute, int minimum) =>
        page.WaitForFunctionAsync(
            "([attribute, minimum]) => Number(document.querySelector('[data-testid=\"game-panel\"]')?.getAttribute(attribute) ?? 0) >= minimum",
            new object[] { attribute, minimum },
            new() { Timeout = Timeout });

    private static async Task<int> ReadCounterAsync(IPage page, string attribute) =>
        int.Parse(await page.GetByTestId("game-panel").GetAttributeAsync(attribute) ?? "0", CultureInfo.InvariantCulture);

    private static ILocatorAssertions Expect(ILocator locator) => Assertions.Expect(locator);

    /// <summary>
    /// An installation and a game under the root, and the browser contexts that play it; disposing removes all of them.
    /// </summary>
    private sealed class GameWorld : IAsyncDisposable
    {
        // Far enough that no player of a test reaches it, and apart so that the moved players do not overlap.
        private const int AsideOffset = 100_000;
        private const int AsideSpacing = 4;

        private readonly PlaywrightFixture _fixture;
        private readonly List<IBrowserContext> _contexts = [];
        private readonly List<GamePlayer> _ownPlayers = [];
        private readonly string _installationName;
        private readonly HomeBlazeInstallation _installation;
        private readonly string _gameName;

        private GameWorld(PlaywrightFixture fixture, string installationName, HomeBlazeInstallation installation, string gameName, Game game)
        {
            _fixture = fixture;
            _installationName = installationName;
            _installation = installation;
            _gameName = gameName;
            Game = game;
        }

        public Game Game { get; }

        public static async Task<GameWorld> CreateAsync(PlaywrightFixture fixture)
        {
            var (installationName, installation) = await fixture.AddSubjectToRootAsync<HomeBlazeInstallation>();
            var (gameName, game) = await fixture.AddSubjectToRootAsync<Game>();
            return new GameWorld(fixture, installationName, installation, gameName, game);
        }

        /// <summary>
        /// Opens the game in a new browser context, which is a new client, waits until its player joined, and moves the
        /// players of earlier tests far away so that they cannot block it.
        /// </summary>
        /// <param name="options">The options of the browser context, or null for the defaults.</param>
        /// <param name="browser">The browser to open the game in, or null for the shared one.</param>
        public async Task<(IPage Page, GamePlayer Player)> JoinAsync(BrowserNewContextOptions? options = null, IBrowser? browser = null)
        {
            var context = await (browser ?? _fixture.Browser).NewContextAsync(options);
            _contexts.Add(context);
            var page = await context.NewPageAsync();
            await page.GotoAsync($"{_fixture.ServerAddress}pages/{_gameName}");
            var position = page.GetByTestId("game-position");
            await Assertions.Expect(position).Not.ToHaveTextAsync("–", new() { Timeout = Timeout });
            await page.GetByTestId("game-canvas").ClickAsync(new() { Timeout = Timeout });

            var player = await FindPlayerAsync(page, await position.InnerTextAsync());
            _ownPlayers.Add(player);
            MoveOtherPlayersAside();
            return (page, player);
        }

        // The panel shows the title and the position of the page's own player, which together identify it.
        private async Task<GamePlayer> FindPlayerAsync(IPage page, string positionText)
        {
            var self = page.GetByTestId("game-nearby-player").Filter(new() { HasText = SelfSuffix });
            var selfText = (await self.InnerTextAsync(new() { Timeout = Timeout })).Trim();
            var title = selfText[..^SelfSuffix.Length];

            var parts = positionText.Split(" · ");
            var x = int.Parse(parts[0]["X ".Length..], CultureInfo.InvariantCulture);
            var y = int.Parse(parts[1]["Y ".Length..], CultureInfo.InvariantCulture);
            return Game.Players.Values.Single(player => player.Title == title && player.X == x && player.Y == y);
        }

        private void MoveOtherPlayersAside()
        {
            var index = 0;
            foreach (var player in Game.Players.Values)
            {
                if (!_ownPlayers.Contains(player))
                {
                    player.X = AsideOffset + index * AsideSpacing;
                    player.Y = AsideOffset;
                    index++;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var context in _contexts)
            {
                await context.CloseAsync();
            }

            _fixture.RemoveSubjectFromRoot(_gameName, Game);
            _fixture.RemoveSubjectFromRoot(_installationName, _installation);
        }
    }

    /// <summary>
    /// A Chromium whose microphone is a test tone and that grants it without a dialog; the shared browser has neither.
    /// </summary>
    private sealed class MediaBrowser : IAsyncDisposable
    {
        private readonly IPlaywright _playwright;

        private MediaBrowser(IPlaywright playwright, IBrowser browser)
        {
            _playwright = playwright;
            Browser = browser;
        }

        public IBrowser Browser { get; }

        public static async Task<MediaBrowser> LaunchAsync()
        {
            var playwright = await Playwright.CreateAsync();
            try
            {
                var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                {
                    Headless = true,
                    Args = ["--use-fake-ui-for-media-stream", "--use-fake-device-for-media-stream"],
                });
                return new MediaBrowser(playwright, browser);
            }
            catch
            {
                playwright.Dispose();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Browser.CloseAsync();
            _playwright.Dispose();
        }
    }
}
