# Game Voice Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Players of `HomeBlaze.Game` talk to each other with their microphones: a player hears the players within 10 m (40 tiles) whose straight line to them touches no wall and no closed door, with the volume fading linearly to silence at 10 m, and the Nearby list shows exactly those players. This implements `src/HomeBlaze/docs/superpowers/specs/2026-10-31-game-voice-design.md`.

**Architecture:** A pure `World/HearingRange` decides who hears whom. The game builds an immutable `GameVolumeTable` under its `_lock` at most every 100 ms when the index or a toggle changed, and publishes it through a `volatile` field. A `GameVoiceMixer` with its own lock queues 16 kHz PCM frames per speaker in pooled ring buffers and, on a 20 ms `PeriodicTimer` loop inside the game's background service, sums for every listener the frames of the speakers in its table row and hands a new frame to the listener's page sinks. Pages capture and play with `getUserMedia` and two `AudioWorklet` processors in `game-voice.js` and `game-voice-worklet.js`, moving frames as `byte[]` over the existing Blazor connection.

**Tech Stack:** .NET 10, C# 14, Namotion.Interceptor source generator, Blazor Server with MudBlazor 9, Web Audio (`AudioWorklet`, `getUserMedia`), xUnit 2.9 with `FakeTimeProvider`, Playwright 1.63 Chromium.

---

## Conventions

Every task relies on these.

**Build and test.** Run everything from the worktree root `/home/rico/GitHub/Namotion.Interceptor/.claude/worktrees/bridge-cse_011ShuL5XgwWZqRmSZFhkZve`.

- Build: `dotnet build src/HomeBlaze/HomeBlaze.Game.Tests -c Release`
- Unit tests: `dotnet test src/HomeBlaze/HomeBlaze.Game.Tests --filter "FullyQualifiedName!~E2E"`
- One unit test class: `dotnet test src/HomeBlaze/HomeBlaze.Game.Tests --filter "FullyQualifiedName~HearingRangeTests"`
- E2E tests (Playwright Chromium, already installed for the existing game E2E tests): `dotnet test src/HomeBlaze/HomeBlaze.Game.Tests --filter "FullyQualifiedName~E2E"`
- Sonar runs with warnings as errors. Rules that fired in this library before: S134 (nesting deeper than 3), S1541 (cyclomatic complexity above 10), S1067 (more than 3 `&&`/`||`/`?:` in one expression), S1144 (unused private member, disabled in test projects), S1186 (empty method). Also active: S3358 (nested ternary), S3776 (cognitive complexity), S1200 (class coupling), S107 (parameter count). Keep methods small, split conditions into helpers, never nest ternaries. When a Sonar rule fires and the fix would cost allocations or clarity, stop and report instead of adding a suppression; exceptions need the user's approval (see `AGENTS.md`).
- Tests follow `When<Condition>_Then<ExpectedBehavior>` with `// Arrange`, `// Act`, `// Assert` (`// Act & Assert` only for exception tests). No `Task.Delay` or `Thread.Sleep` in tests: poll with `AsyncTestHelpers.WaitUntilAsync` or Playwright's own waiting.
- Code, comments and docs use US English, no em dashes, no hard wrapping in markdown, and comments only for the why a reader cannot derive.

**Git.**

- Never stage or commit anything under `src/HomeBlaze/HomeBlaze/Data`: local skip-worktree files there hold the user's private world. Use `git add` with the explicit paths each task lists, never `git add -A` or `git add .`.
- Commit messages use `feat:`, `test:` or `docs:` and end with the trailer line `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`, written as a second `-m` so git keeps a blank line before it. No other AI attribution anywhere.
- After every task: `git push`.

**Running app.**

- Never build walls, demolish, open or close doors, or otherwise change the demo world while verifying in the running app.
- After each task that leaves something runnable (marked "Runnable" below), the controller, not the task's implementer, restarts the app: first stop it in its own call with `pkill -f "[u]rls http://192.168.1.201"`, then start it in the background from `src/HomeBlaze/HomeBlaze` with `ASPNETCORE_ENVIRONMENT=Development dotnet run -c Release --no-launch-profile --urls "http://192.168.1.201:5280;https://192.168.1.201:5281"`. Kestrel serves https with the ASP.NET development certificate. The game page is `/pages/Demo/Game`.

## Setup (once, controller)

- [ ] **Step 1: Make sure the development certificate exists**

Run: `dotnet dev-certs https --check || dotnet dev-certs https`
Expected: either "A valid certificate was found" or the certificate is created. The certificate is issued for `localhost`, so a browser on the LAN shows a certificate warning for `https://192.168.1.201:5281` once; accept it. After that the page is a secure context, which voice needs.

## Spec decisions this plan makes

The spec leaves these open; the plan resolves them as follows, and the README in Task 8 documents the result.

- **"Touches" in the line walk** means the closed segment touches the closed tile, also only at an edge or a corner. Player centers are tile corners (even footprints), so a line between two players in one row runs along a grid line and touches both rows. A wall that only touches the line blocks, consistent with the corner rule of the spec and the diagonal rule of movement.
- **Exact arithmetic.** The walk works in half tiles with `long`, which makes it exact; the distance is a `double`. The walk still starts at the player with the smaller id, as the spec asks.
- **Dirty flag.** `GameWorldIndex` gets a `Version` that increases with every effective change, so every move, join, leave, wall and door change (also from outside, through the lazy index rebuild) marks the table dirty without a call at each site. Toggles set a separate `_isVolumeTableDirty` flag.
- **Shrinking queues.** A shrinking start size only lowers the delay if the queue level follows it, so a speaker queue and the browser's playback buffer keep at most their start size plus one growth step and drop the oldest audio beyond that, in addition to the hard maximum.
- **Frame rate burst.** The frame rate token bucket refills at `FrameRateTolerance` times the frame rate with a burst of `ServerQueueMaximum` frames.
- **Step limiter.** `World/StepRateLimiter` becomes the general `World/RateLimiter(TimeProvider, TimeSpan interval, int burst)`, used for steps, turns and frames; `Game.StepBurst` holds the step burst of 3.
- **Sinks send through the renderer.** The page's sink hands each frame to the circuit's dispatcher with `InvokeAsync`, because JS interop with `byte[]` is not documented as thread-safe; this costs a closure per sent frame besides the frame itself.
- **Takeover and reset in the page.** When another page of the player takes the microphone over, or a disconnect resets the toggles, the page notices on the next `VolumeTableChanged` through `Game.GetVoicePages` and stops its capture or playback.
- **Error texts beyond the spec.** A capture that fails for another reason shows "The microphone could not be started", a failing playback "The speaker could not be started". Buttons get the tooltips "Turn the microphone on (M)" and similar; before the voice module loaded they are disabled without the HTTPS tooltip.
- **Test counter.** Besides `data-voice-received-frames` the panel exposes `data-voice-sent-frames`, so the E2E test can prove that frames stop arriving without a fixed wait.
- **Task order.** The volume table comes before the player toggles, which mark it dirty, and the E2E tests are written with the page tasks they verify instead of in a task of their own.

## File structure

Create:

| File | Responsibility |
|---|---|
| `src/HomeBlaze/HomeBlaze.Game/Voice/GameVoiceSettings.cs` | Every voice timing and size, the one place to tune them |
| `src/HomeBlaze/HomeBlaze.Game/World/HearingRange.cs` | The only definition of who hears whom and how loud |
| `src/HomeBlaze/HomeBlaze.Game/Voice/GameVolumeTable.cs` | Immutable rows of audible players per listener and their builder; `GameVolumeEntry` |
| `src/HomeBlaze/HomeBlaze.Game/Game.Volume.cs` | Rebuilds and publishes the volume table, raises `VolumeTableChanged` |
| `src/HomeBlaze/HomeBlaze.Game/World/RateLimiter.cs` | Token bucket, renamed and generalized from `StepRateLimiter.cs` |
| `src/HomeBlaze/HomeBlaze.Game/Voice/GameVoiceCodec.cs` | PCM16 little-endian encode and decode seam |
| `src/HomeBlaze/HomeBlaze.Game/Voice/SpeakerQueue.cs` | Adaptive pooled ring buffer of one speaker's frames |
| `src/HomeBlaze/HomeBlaze.Game/Voice/IGameVoiceSink.cs` | One page of a listener with the speaker on |
| `src/HomeBlaze/HomeBlaze.Game/Voice/GameVoiceMixer.cs` | Queues, microphone pages, sinks and the per-listener mix |
| `src/HomeBlaze/HomeBlaze.Game/Game.Voice.cs` | Toggles, reset on disconnect, mixer ownership, voice loop |
| `src/HomeBlaze/HomeBlaze.Game/wwwroot/game-voice.js` | Audio context, capture, playback, codec seam, per view id |
| `src/HomeBlaze/HomeBlaze.Game/wwwroot/game-voice-worklet.js` | Capture and playback `AudioWorklet` processors |
| `src/HomeBlaze/HomeBlaze.Game.Tests/World/HearingRangeTests.cs` | Hearing rule tests |
| `src/HomeBlaze/HomeBlaze.Game.Tests/GameVolumeTableTests.cs` | Volume table of the game |
| `src/HomeBlaze/HomeBlaze.Game.Tests/Voice/GameVoiceCodecTests.cs` | Codec tests |
| `src/HomeBlaze/HomeBlaze.Game.Tests/Voice/SpeakerQueueTests.cs` | Adaptive queue tests |
| `src/HomeBlaze/HomeBlaze.Game.Tests/Voice/GameVoiceMixerTests.cs` | Mixer tests |
| `src/HomeBlaze/HomeBlaze.Game.Tests/GameVoiceTests.cs` | Toggles, ownership, reset, tick |
| `src/HomeBlaze/HomeBlaze.Game.Tests/Testing/VoiceFrames.cs` | Builds and reads test frames |
| `src/HomeBlaze/HomeBlaze.Game.Tests/Testing/RecordingVoiceSink.cs` | Thread-safe sink that records frames |
| `src/HomeBlaze/HomeBlaze.Game.Tests/World/RateLimiterTests.cs` | Renamed from `StepRateLimiterTests.cs` |

Modify:

| File | Change |
|---|---|
| `src/HomeBlaze/HomeBlaze.Game/World/GameWorldIndex.cs` | `IsSoundBlocked(x, y)`, `Version` |
| `src/HomeBlaze/HomeBlaze.Game/GamePlayer.cs` | `IsMicrophoneOn`, `IsAudioOn`, voice pages, `RateLimiter` types |
| `src/HomeBlaze/HomeBlaze.Game/Game.cs` | Creates the mixer |
| `src/HomeBlaze/HomeBlaze.Game/Game.Movement.cs` | `RateLimiter`, `StepBurst` |
| `src/HomeBlaze/HomeBlaze.Game/Game.Players.cs` | Resets voice on disconnect and removal |
| `src/HomeBlaze/HomeBlaze.Game/Game.Runtime.cs` | Runs the voice loop beside the housekeeping loop |
| `src/HomeBlaze/HomeBlaze.Game/View/GameViewObject.cs` | `IsMicrophoneOn`, `IsAudioOn` |
| `src/HomeBlaze/HomeBlaze.Game/View/GameViewTracker.cs` | Compares and sends the voice fields; `GetPlayerColor` |
| `src/HomeBlaze/HomeBlaze.Game/Components/GamePage.razor` | Voice buttons, sink, JS interop, Nearby from the table |
| `src/HomeBlaze/HomeBlaze.Game/Components/GamePage.razor.css` | Voice and Nearby styles |
| `src/HomeBlaze/HomeBlaze.Game/wwwroot/game-input.js` | Keys M and N |
| `src/HomeBlaze/HomeBlaze.Game/README.md` | Voice documentation |
| `src/HomeBlaze/HomeBlaze.Game.Tests/World/GameWorldIndexTests.cs` | Sound blocking and version tests |
| `src/HomeBlaze/HomeBlaze.Game.Tests/GameRuntimeTests.cs` | `Game.StepBurst`; voice loop test |
| `src/HomeBlaze/HomeBlaze.Game.Tests/View/GameViewTrackerTests.cs` | Voice fields, player color |
| `src/HomeBlaze/HomeBlaze.Game.Tests/GameSerializationTests.cs` | Toggles are not saved |
| `src/HomeBlaze/HomeBlaze.Game.Tests/Testing/GameTestContext.cs` | `AddConnectedPlayer(..., isNamed)` |
| `src/HomeBlaze/HomeBlaze.Game.Tests/E2E/GamePageTests.cs` | Voice E2E tests, media browser |

Delete in Task 8: `src/HomeBlaze/docs/superpowers/` (this plan and the spec).

---

## Task 1: Hearing range

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Game/Voice/GameVoiceSettings.cs`
- Create: `src/HomeBlaze/HomeBlaze.Game/World/HearingRange.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Game/World/GameWorldIndex.cs`
- Test: `src/HomeBlaze/HomeBlaze.Game.Tests/World/HearingRangeTests.cs`
- Test: `src/HomeBlaze/HomeBlaze.Game.Tests/World/GameWorldIndexTests.cs`

- [ ] **Step 1: Write the failing index tests**

Add these two tests at the end of the class in `src/HomeBlaze/HomeBlaze.Game.Tests/World/GameWorldIndexTests.cs`, before its last `}`:

```csharp
    [Fact]
    public void WhenAWallOrAClosedDoorCoversATile_ThenTheTileBlocksSound()
    {
        // Arrange
        var index = new GameWorldIndex();
        index.AddOrUpdate(new Wall { X = 0, Y = 0, Width = 4, Depth = 1 });
        index.AddOrUpdate(new Door { X = 10, Y = 0 });

        // Act
        var isWallBlocked = index.IsSoundBlocked(3, 0);
        var isDoorBlocked = index.IsSoundBlocked(11, 0);

        // Assert
        Assert.True(isWallBlocked);
        Assert.True(isDoorBlocked);
    }

    [Fact]
    public void WhenAnOpenDoorAPlayerOrNothingCoversATile_ThenTheTileLetsSoundThrough()
    {
        // Arrange
        var index = new GameWorldIndex();
        index.AddOrUpdate(new Door { X = 0, Y = 0, IsOpen = true });
        index.AddOrUpdate(new GamePlayer { X = 10, Y = 0, IsConnected = true });

        // Act
        var isOpenDoorBlocked = index.IsSoundBlocked(1, 0);
        var isPlayerBlocked = index.IsSoundBlocked(10, 0);
        var isEmptyBlocked = index.IsSoundBlocked(50, 50);

        // Assert
        Assert.False(isOpenDoorBlocked);
        Assert.False(isPlayerBlocked);
        Assert.False(isEmptyBlocked);
    }
```

- [ ] **Step 2: Write the failing hearing range tests**

Create `src/HomeBlaze/HomeBlaze.Game.Tests/World/HearingRangeTests.cs`:

```csharp
using HomeBlaze.Game.World;

namespace HomeBlaze.Game.Tests.World;

public class HearingRangeTests
{
    private readonly GameWorldIndex _index = new();

    private static GamePlayer CreatePlayer(string clientId, int x, int y) =>
        new() { ClientId = clientId, IsConnected = true, X = x, Y = y };

    [Fact]
    public void WhenThePlayersStandOnTheSameTiles_ThenTheVolumeIsFull()
    {
        // Arrange
        var first = CreatePlayer("a", 0, 0);
        var second = CreatePlayer("b", 0, 0);

        // Act
        var volume = HearingRange.GetVolume(first, second, _index);

        // Assert
        Assert.Equal(1.0, volume, 6);
    }

    [Fact]
    public void WhenThePlayersAre20TilesApart_ThenTheVolumeIsHalf()
    {
        // Arrange
        var first = CreatePlayer("a", 0, 0);
        var second = CreatePlayer("b", 20, 0);

        // Act
        var volume = HearingRange.GetVolume(first, second, _index);

        // Assert
        Assert.Equal(0.5, volume, 6);
    }

    [Fact]
    public void WhenThePlayersAreJustWithinTheRadius_ThenTheyHearEachOtherFaintly()
    {
        // Arrange
        var first = CreatePlayer("a", 0, 0);
        var second = CreatePlayer("b", 39, 0);

        // Act
        var volume = HearingRange.GetVolume(first, second, _index);

        // Assert
        Assert.Equal(0.025, volume, 6);
    }

    [Theory]
    [InlineData(40, 0)]
    [InlineData(41, 0)]
    [InlineData(30, 30)]
    public void WhenThePlayersAreAtLeastTheRadiusApart_ThenTheyDoNotHearEachOther(int x, int y)
    {
        // Arrange
        var first = CreatePlayer("a", 0, 0);
        var second = CreatePlayer("b", x, y);

        // Act
        var volume = HearingRange.GetVolume(first, second, _index);

        // Assert
        Assert.Equal(0.0, volume);
    }

    [Fact]
    public void WhenAWallStandsBetween_ThenTheyDoNotHearEachOther()
    {
        // Arrange
        _index.AddOrUpdate(new Wall { X = 10, Y = -1, Width = 1, Depth = 4 });
        var first = CreatePlayer("a", 0, 0);
        var second = CreatePlayer("b", 20, 0);

        // Act
        var volume = HearingRange.GetVolume(first, second, _index);

        // Assert
        Assert.Equal(0.0, volume);
    }

    [Fact]
    public void WhenAClosedDoorStandsBetween_ThenTheyDoNotHearEachOther()
    {
        // Arrange
        _index.AddOrUpdate(new Door { X = 10, Y = -1, Width = 1, Depth = 4 });
        var first = CreatePlayer("a", 0, 0);
        var second = CreatePlayer("b", 20, 0);

        // Act
        var volume = HearingRange.GetVolume(first, second, _index);

        // Assert
        Assert.Equal(0.0, volume);
    }

    [Fact]
    public void WhenAnOpenDoorStandsBetween_ThenTheVoicePasses()
    {
        // Arrange
        _index.AddOrUpdate(new Door { X = 10, Y = -1, Width = 1, Depth = 4, IsOpen = true });
        var first = CreatePlayer("a", 0, 0);
        var second = CreatePlayer("b", 20, 0);

        // Act
        var volume = HearingRange.GetVolume(first, second, _index);

        // Assert
        Assert.Equal(0.5, volume, 6);
    }

    [Fact]
    public void WhenAnotherPlayerStandsBetween_ThenTheVoicePasses()
    {
        // Arrange
        _index.AddOrUpdate(CreatePlayer("c", 10, 0));
        var first = CreatePlayer("a", 0, 0);
        var second = CreatePlayer("b", 20, 0);

        // Act
        var volume = HearingRange.GetVolume(first, second, _index);

        // Assert
        Assert.Equal(0.5, volume, 6);
    }

    [Fact]
    public void WhenTheLineRunsAlongTheFaceOfAWall_ThenTheWallBlocks()
    {
        // Arrange
        // The centers lie on the line between rows 0 and 1, which touches this wall in row 0.
        _index.AddOrUpdate(new Wall { X = 10, Y = 0, Width = 1, Depth = 1 });
        var first = CreatePlayer("a", 0, 0);
        var second = CreatePlayer("b", 20, 0);

        // Act
        var volume = HearingRange.GetVolume(first, second, _index);

        // Assert
        Assert.Equal(0.0, volume);
    }

    [Fact]
    public void WhenADiagonalTouchesTheCornerOfAWall_ThenTheWallBlocks()
    {
        // Arrange
        // The line from 1, 1 to 11, 11 passes exactly through the corner 6, 6 of this tile.
        _index.AddOrUpdate(new Wall { X = 6, Y = 5, Width = 1, Depth = 1 });
        var first = CreatePlayer("a", 0, 0);
        var second = CreatePlayer("b", 10, 10);

        // Act
        var volume = HearingRange.GetVolume(first, second, _index);

        // Assert
        Assert.Equal(0.0, volume);
    }

    [Fact]
    public void WhenADiagonalPassesNextToTheCornerOfAWall_ThenTheVoicePasses()
    {
        // Arrange
        _index.AddOrUpdate(new Wall { X = 7, Y = 5, Width = 1, Depth = 1 });
        var first = CreatePlayer("a", 0, 0);
        var second = CreatePlayer("b", 10, 10);

        // Act
        var volume = HearingRange.GetVolume(first, second, _index);

        // Assert
        Assert.Equal(1 - Math.Sqrt(200) / HearingRange.HearingRadius, volume, 6);
    }

    [Fact]
    public void WhenAWallOverlapsAPlayer_ThenItDoesNotBlockThatPlayersVoice()
    {
        // Arrange
        _index.AddOrUpdate(new Wall { X = 1, Y = 0, Width = 1, Depth = 2 });
        var first = CreatePlayer("a", 0, 0);
        var second = CreatePlayer("b", 20, 0);

        // Act
        var volume = HearingRange.GetVolume(first, second, _index);

        // Assert
        Assert.Equal(0.5, volume, 6);
    }

    [Theory]
    [InlineData(0, 0, 10, 10)]
    [InlineData(3, -7, 17, 12)]
    [InlineData(-5, 4, 9, -11)]
    [InlineData(0, 0, 13, 4)]
    public void WhenTheOrderIsSwapped_ThenTheVolumeIsTheSame(int firstX, int firstY, int secondX, int secondY)
    {
        // Arrange
        _index.AddOrUpdate(new Wall { X = 6, Y = 5, Width = 1, Depth = 1 });
        _index.AddOrUpdate(new Wall { X = 7, Y = 2, Width = 1, Depth = 3 });
        var first = CreatePlayer("a", firstX, firstY);
        var second = CreatePlayer("b", secondX, secondY);

        // Act
        var forward = HearingRange.GetVolume(first, second, _index);
        var backward = HearingRange.GetVolume(second, first, _index);

        // Assert
        Assert.Equal(forward, backward);
    }

    [Fact]
    public void WhenThePlayersStandAtTheUpperIntLimit_ThenTheVolumeIsComputedWithoutOverflow()
    {
        // Arrange
        var first = CreatePlayer("a", int.MaxValue - 1, int.MaxValue - 1);
        var second = CreatePlayer("b", int.MaxValue - 21, int.MaxValue - 1);

        // Act
        var volume = HearingRange.GetVolume(first, second, _index);

        // Assert
        Assert.Equal(0.5, volume, 6);
    }

    [Fact]
    public void WhenAWallStandsBetweenPlayersAtTheUpperIntLimit_ThenItBlocks()
    {
        // Arrange
        _index.AddOrUpdate(new Wall { X = int.MaxValue - 11, Y = int.MaxValue - 3, Width = 1, Depth = 4 });
        var first = CreatePlayer("a", int.MaxValue - 1, int.MaxValue - 1);
        var second = CreatePlayer("b", int.MaxValue - 21, int.MaxValue - 1);

        // Act
        var volume = HearingRange.GetVolume(first, second, _index);

        // Assert
        Assert.Equal(0.0, volume);
    }

    [Fact]
    public void WhenThePlayersStandAtOppositeIntLimits_ThenTheyDoNotHearEachOther()
    {
        // Arrange
        var first = CreatePlayer("a", int.MinValue, int.MinValue);
        var second = CreatePlayer("b", int.MaxValue - 1, int.MaxValue - 1);

        // Act
        var volume = HearingRange.GetVolume(first, second, _index);

        // Assert
        Assert.Equal(0.0, volume);
    }

    [Fact]
    public void WhenTheAreaOfAPlayerIsTaken_ThenItReachesTheRadiusBeyondTheFootprint()
    {
        // Act
        var area = HearingRange.GetArea(new Footprint(0, 0, 2, 2));
        var clamped = HearingRange.GetArea(new Footprint(int.MaxValue - 1, int.MinValue, 2, 2));

        // Assert
        Assert.Equal(new Footprint(-40, -40, 82, 82), area);
        Assert.Equal(((long)int.MaxValue, (long)int.MinValue), (clamped.Right, (long)clamped.Y));
    }
}
```

- [ ] **Step 3: Run the tests to see them fail**

Run: `dotnet build src/HomeBlaze/HomeBlaze.Game.Tests -c Release`
Expected: build fails with `CS0103`/`CS0117` because `HearingRange` and `GameWorldIndex.IsSoundBlocked` do not exist.

- [ ] **Step 4: Add the settings**

Create `src/HomeBlaze/HomeBlaze.Game/Voice/GameVoiceSettings.cs`:

```csharp
namespace HomeBlaze.Game.Voice;

/// <summary>
/// Every timing and size of voice in the game, the one place to tune them. The page passes the browser's values to
/// game-voice.js, which holds no copy.
/// </summary>
internal static class GameVoiceSettings
{
    /// <summary>
    /// Samples per second of every frame.
    /// </summary>
    public const int SampleRate = 16000;

    /// <summary>
    /// Samples in one frame of <see cref="FrameDuration"/>.
    /// </summary>
    public const int SamplesPerFrame = SampleRate / 50;

    /// <summary>
    /// Bytes in one frame of 16-bit samples.
    /// </summary>
    public const int FrameBytes = SamplesPerFrame * 2;

    /// <summary>
    /// Frames a speaker queue needs before it plays.
    /// </summary>
    public const int ServerQueueStart = 1;

    /// <summary>
    /// Frames beyond which a speaker queue drops the oldest.
    /// </summary>
    public const int ServerQueueMaximum = 6;

    /// <summary>
    /// Frames a buffer grows by after it ran dry.
    /// </summary>
    public const int BufferGrowth = 1;

    /// <summary>
    /// Sends to a page in flight beyond which new frames to it are dropped.
    /// </summary>
    public const int MaximumPendingSends = 10;

    /// <summary>
    /// Allowed incoming frame rate as a multiple of the nominal rate.
    /// </summary>
    public const double FrameRateTolerance = 1.5;

    /// <summary>
    /// Length of one frame.
    /// </summary>
    public static readonly TimeSpan FrameDuration = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// Audio the browser buffers before it plays.
    /// </summary>
    public static readonly TimeSpan PlaybackBufferStart = TimeSpan.FromMilliseconds(40);

    /// <summary>
    /// Largest buffer the browser grows to.
    /// </summary>
    public static readonly TimeSpan PlaybackBufferMaximum = TimeSpan.FromMilliseconds(120);

    /// <summary>
    /// Time without running dry before a buffer shrinks by one frame.
    /// </summary>
    public static readonly TimeSpan BufferShrinkDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Shortest time between two rebuilds of the volume table.
    /// </summary>
    public static readonly TimeSpan VolumeTableInterval = TimeSpan.FromMilliseconds(100);
}
```

- [ ] **Step 5: Add sound blocking to the index**

In `src/HomeBlaze/HomeBlaze.Game/World/GameWorldIndex.cs`, insert before the line `    private static bool HasValidSize(GameObject gameObject) =>`:

```csharp
    /// <summary>
    /// Whether a blocking object that is not a player covers the tile, which stops sound: walls and closed doors do,
    /// open doors and players do not.
    /// </summary>
    public bool IsSoundBlocked(long x, long y)
    {
        if (!_chunks.TryGetValue((x >> ChunkShift, y >> ChunkShift), out var objects))
        {
            return false;
        }

        foreach (var gameObject in objects)
        {
            var entry = _entries[gameObject];
            if (entry.IsBlocking && gameObject is not GamePlayer && entry.Footprint.Contains(x, y))
            {
                return true;
            }
        }

        return false;
    }

```

- [ ] **Step 6: Add the hearing range**

Create `src/HomeBlaze/HomeBlaze.Game/World/HearingRange.cs`:

```csharp
namespace HomeBlaze.Game.World;

/// <summary>
/// The only definition of who hears whom: players hear each other while their footprint centers are less than
/// <see cref="HearingRadius"/> apart and the straight line between the centers touches no tile that blocks sound.
/// </summary>
internal static class HearingRange
{
    /// <summary>
    /// The distance in tiles at which a voice has faded to silence, 10 m.
    /// </summary>
    public const int HearingRadius = 40;

    /// <summary>
    /// Gets the volume from 0 to 1 at which the players hear each other, falling linearly with the distance between
    /// their centers; the same for both orders.
    /// </summary>
    public static double GetVolume(GamePlayer first, GamePlayer second, GameWorldIndex index)
    {
        if (string.CompareOrdinal(first.ClientId, second.ClientId) > 0)
        {
            (first, second) = (second, first);
        }

        var firstFootprint = first.Footprint;
        var secondFootprint = second.Footprint;
        var start = GetDoubledCenter(firstFootprint);
        var end = GetDoubledCenter(secondFootprint);
        double deltaX = end.X - start.X;
        double deltaY = end.Y - start.Y;
        var distance = Math.Sqrt(deltaX * deltaX + deltaY * deltaY) / 2;
        if (distance >= HearingRadius || IsLineBlocked(start, end, firstFootprint, secondFootprint, index))
        {
            return 0;
        }

        return 1 - distance / HearingRadius;
    }

    /// <summary>
    /// Gets the area that every footprint of a player who may hear a player with this footprint intersects.
    /// </summary>
    public static Footprint GetArea(Footprint footprint)
    {
        var minimumX = Math.Max(int.MinValue, (long)footprint.X - HearingRadius);
        var maximumX = Math.Min(int.MaxValue, footprint.Right + HearingRadius);
        var minimumY = Math.Max(int.MinValue, (long)footprint.Y - HearingRadius);
        var maximumY = Math.Min(int.MaxValue, footprint.Top + HearingRadius);
        return new Footprint((int)minimumX, (int)minimumY, (int)(maximumX - minimumX + 1), (int)(maximumY - minimumY + 1));
    }

    // In half tiles, so the center of every footprint is a whole number and the walk stays exact.
    private static (long X, long Y) GetDoubledCenter(Footprint footprint) =>
        (2L * footprint.X + footprint.Width, 2L * footprint.Y + footprint.Depth);

    // Walks every tile that the closed segment touches, also only at an edge or a corner, so sound slips neither
    // between two walls that touch at a corner nor along the face of a wall. Called only within the hearing radius,
    // which keeps the products in GetTouchedRows far inside the long range.
    private static bool IsLineBlocked((long X, long Y) start, (long X, long Y) end, Footprint first, Footprint second, GameWorldIndex index)
    {
        var lastColumn = FloorDivide(Math.Max(start.X, end.X), 2);
        for (var column = CeilingDivide(Math.Min(start.X, end.X), 2) - 1; column <= lastColumn; column++)
        {
            var (firstRow, lastRow) = GetTouchedRows(column, start, end);
            for (var row = firstRow; row <= lastRow; row++)
            {
                if (!first.Contains(column, row) && !second.Contains(column, row) && index.IsSoundBlocked(column, row))
                {
                    return true;
                }
            }
        }

        return false;
    }

    // The rows of the column whose closed tiles the segment touches; a tile spans 2 half tiles.
    private static (long First, long Last) GetTouchedRows(long column, (long X, long Y) start, (long X, long Y) end)
    {
        var deltaX = end.X - start.X;
        if (deltaX == 0)
        {
            return (CeilingDivide(Math.Min(start.Y, end.Y), 2) - 1, FloorDivide(Math.Max(start.Y, end.Y), 2));
        }

        // Where the segment enters and leaves the column, as heights over a positive denominator.
        var deltaY = end.Y - start.Y;
        var sign = Math.Sign(deltaX);
        var left = Math.Max(2 * column, Math.Min(start.X, end.X));
        var right = Math.Min(2 * column + 2, Math.Max(start.X, end.X));
        var atLeft = (start.Y * deltaX + (left - start.X) * deltaY) * sign;
        var atRight = (start.Y * deltaX + (right - start.X) * deltaY) * sign;
        var denominator = 2 * deltaX * sign;
        return (CeilingDivide(Math.Min(atLeft, atRight), denominator) - 1, FloorDivide(Math.Max(atLeft, atRight), denominator));
    }

    // Rounds toward negative infinity for a positive denominator.
    private static long FloorDivide(long numerator, long denominator) =>
        numerator >= 0 ? numerator / denominator : -((denominator - 1 - numerator) / denominator);

    private static long CeilingDivide(long numerator, long denominator) => -FloorDivide(-numerator, denominator);
}
```

- [ ] **Step 7: Run the tests to see them pass**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Game.Tests --filter "FullyQualifiedName~HearingRangeTests|FullyQualifiedName~GameWorldIndexTests"`
Expected: all pass. Then run the whole unit suite: `dotnet test src/HomeBlaze/HomeBlaze.Game.Tests --filter "FullyQualifiedName!~E2E"`, expected all pass.

- [ ] **Step 8: Commit and push**

```bash
git add src/HomeBlaze/HomeBlaze.Game/Voice/GameVoiceSettings.cs src/HomeBlaze/HomeBlaze.Game/World/HearingRange.cs src/HomeBlaze/HomeBlaze.Game/World/GameWorldIndex.cs src/HomeBlaze/HomeBlaze.Game.Tests/World/HearingRangeTests.cs src/HomeBlaze/HomeBlaze.Game.Tests/World/GameWorldIndexTests.cs
git commit -m "feat: decide who hears whom in the game" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

---

## Task 2: Volume table

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Game/Voice/GameVolumeTable.cs`
- Create: `src/HomeBlaze/HomeBlaze.Game/Game.Volume.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Game/World/GameWorldIndex.cs`
- Test: `src/HomeBlaze/HomeBlaze.Game.Tests/World/GameWorldIndexTests.cs`
- Test: `src/HomeBlaze/HomeBlaze.Game.Tests/GameVolumeTableTests.cs`

- [ ] **Step 1: Write the failing index version tests**

Add at the end of the class in `src/HomeBlaze/HomeBlaze.Game.Tests/World/GameWorldIndexTests.cs`, before its last `}`:

```csharp
    [Fact]
    public void WhenObjectsAreAddedMovedRemovedAndCleared_ThenTheVersionIncreasesEachTime()
    {
        // Arrange
        var index = new GameWorldIndex();
        var wall = new Wall();
        var versions = new List<long> { index.Version };

        // Act
        index.AddOrUpdate(wall);
        versions.Add(index.Version);
        wall.X = 10;
        index.AddOrUpdate(wall);
        versions.Add(index.Version);
        index.Remove(wall);
        versions.Add(index.Version);
        index.Clear();
        versions.Add(index.Version);

        // Assert
        Assert.All(versions.Zip(versions.Skip(1)), pair => Assert.True(pair.Second > pair.First));
    }

    [Fact]
    public void WhenAnUnchangedObjectIsUpdatedOrAnUnknownOneRemoved_ThenTheVersionStays()
    {
        // Arrange
        var index = new GameWorldIndex();
        var wall = new Wall();
        index.AddOrUpdate(wall);
        var version = index.Version;

        // Act
        index.AddOrUpdate(wall);
        index.Remove(new Wall());

        // Assert
        Assert.Equal(version, index.Version);
    }
```

- [ ] **Step 2: Write the failing volume table tests**

Create `src/HomeBlaze/HomeBlaze.Game.Tests/GameVolumeTableTests.cs`:

```csharp
using HomeBlaze.Game.Tests.Testing;
using HomeBlaze.Game.Voice;

namespace HomeBlaze.Game.Tests;

public class GameVolumeTableTests
{
    private readonly GameTestContext _context = new();

    private static float? FindVolume(GameVolumeTable table, string listenerId, string speakerId)
    {
        foreach (var entry in table.GetRow(listenerId))
        {
            if (entry.PlayerId == speakerId)
            {
                return entry.Volume;
            }
        }

        return null;
    }

    private MoveResult Step(GamePlayer player, GameDirection direction)
    {
        _context.TimeProvider.Advance(Game.StepInterval);
        return _context.Game.TryMove(player, direction, turnOnly: false);
    }

    [Fact]
    public void WhenTwoPlayersStandWithinRange_ThenEachHearsTheOther()
    {
        // Arrange
        _context.AddConnectedPlayer("tab-1");
        _context.AddConnectedPlayer("tab-2", x: 10);

        // Act
        var isRebuilt = _context.Game.UpdateVolumeTable();

        // Assert
        var table = _context.Game.VolumeTable;
        Assert.True(isRebuilt);
        Assert.Equal(0.75f, FindVolume(table, "tab-1", "tab-2"));
        Assert.Equal(0.75f, FindVolume(table, "tab-2", "tab-1"));
    }

    [Fact]
    public void WhenAPlayerIsOutOfRange_ThenBothRowsExistAndAreEmpty()
    {
        // Arrange
        _context.AddConnectedPlayer("tab-1");
        _context.AddConnectedPlayer("tab-2", x: 100);

        // Act
        _context.Game.UpdateVolumeTable();

        // Assert
        var table = _context.Game.VolumeTable;
        Assert.True(table.HasRow("tab-1"));
        Assert.True(table.HasRow("tab-2"));
        Assert.Equal(0, table.GetRow("tab-1").Length);
        Assert.Equal(0, table.GetRow("tab-2").Length);
    }

    [Fact]
    public void WhenAPlayerIsOffline_ThenItHasNoRow()
    {
        // Arrange
        _context.AddConnectedPlayer("tab-1");
        _context.Clients.Set("kitchen", isNamed: true, isConnected: false);
        _context.Game.ReconcilePlayers();

        // Act
        _context.Game.UpdateVolumeTable();

        // Assert
        var table = _context.Game.VolumeTable;
        Assert.False(table.HasRow("kitchen"));
        Assert.Null(FindVolume(table, "tab-1", "kitchen"));
    }

    [Fact]
    public void WhenAPlayerStepsBehindAWall_ThenThePairLeavesTheTable()
    {
        // Arrange
        _context.AddConnectedPlayer("tab-1");
        var second = _context.AddConnectedPlayer("tab-2", x: 10);
        _context.AddWall(5, 2, width: 1, depth: 4);
        _context.Game.UpdateVolumeTable();
        var before = FindVolume(_context.Game.VolumeTable, "tab-1", "tab-2");

        // Act
        Step(second, GameDirection.North);
        Step(second, GameDirection.North);
        Step(second, GameDirection.North);
        var isRebuilt = _context.Game.UpdateVolumeTable();

        // Assert
        Assert.NotNull(before);
        Assert.True(isRebuilt);
        Assert.Equal((10, 3), (second.X, second.Y));
        Assert.Null(FindVolume(_context.Game.VolumeTable, "tab-1", "tab-2"));
        Assert.Null(FindVolume(_context.Game.VolumeTable, "tab-2", "tab-1"));
    }

    [Fact]
    public async Task WhenADoorBetweenThemCloses_ThenThePairLeavesTheTable()
    {
        // Arrange
        _context.AddConnectedPlayer("tab-1");
        _context.AddConnectedPlayer("tab-2", y: 10);
        var door = _context.AddDoor(-1, 5, isOpen: true);
        _context.Game.UpdateVolumeTable();
        var before = FindVolume(_context.Game.VolumeTable, "tab-1", "tab-2");

        // Act
        await door.CloseAsync(CancellationToken.None);
        _context.TimeProvider.Advance(GameVoiceSettings.VolumeTableInterval);
        _context.Game.UpdateVolumeTable();

        // Assert
        Assert.Equal(0.75f, before);
        Assert.Null(FindVolume(_context.Game.VolumeTable, "tab-1", "tab-2"));
    }

    [Fact]
    public void WhenTheTableWasJustRebuilt_ThenAChangeWaitsForTheInterval()
    {
        // Arrange
        _context.AddConnectedPlayer("tab-1");
        var second = _context.AddConnectedPlayer("tab-2", x: 10);
        _context.Game.UpdateVolumeTable();
        second.X = 12;
        _context.Game.RequestIndexRebuild();

        // Act
        var isRebuiltAtOnce = _context.Game.UpdateVolumeTable();
        _context.TimeProvider.Advance(GameVoiceSettings.VolumeTableInterval);
        var isRebuiltAfterInterval = _context.Game.UpdateVolumeTable();

        // Assert
        Assert.False(isRebuiltAtOnce);
        Assert.True(isRebuiltAfterInterval);
        Assert.Equal(0.7f, FindVolume(_context.Game.VolumeTable, "tab-1", "tab-2"));
    }

    [Fact]
    public void WhenNothingChanged_ThenTheTableIsNotRebuilt()
    {
        // Arrange
        _context.AddConnectedPlayer("tab-1");
        _context.Game.UpdateVolumeTable();
        var table = _context.Game.VolumeTable;
        _context.TimeProvider.Advance(GameVoiceSettings.VolumeTableInterval);

        // Act
        var isRebuilt = _context.Game.UpdateVolumeTable();

        // Assert
        Assert.False(isRebuilt);
        Assert.Same(table, _context.Game.VolumeTable);
    }

    [Fact]
    public void WhenTheTableIsRebuilt_ThenVolumeTableChangedIsRaised()
    {
        // Arrange
        _context.AddConnectedPlayer("tab-1");
        var raised = 0;
        _context.Game.VolumeTableChanged += () => raised++;

        // Act
        _context.Game.UpdateVolumeTable();
        _context.Game.UpdateVolumeTable();

        // Assert
        Assert.Equal(1, raised);
    }
}
```

- [ ] **Step 3: Run the tests to see them fail**

Run: `dotnet build src/HomeBlaze/HomeBlaze.Game.Tests -c Release`
Expected: build fails because `GameWorldIndex.Version`, `GameVolumeTable`, `Game.UpdateVolumeTable`, `Game.VolumeTable` and `Game.VolumeTableChanged` do not exist.

- [ ] **Step 4: Add the index version**

In `src/HomeBlaze/HomeBlaze.Game/World/GameWorldIndex.cs`:

Replace

```csharp
    public int Count => _entries.Count;

    public void Clear()
    {
        _chunks.Clear();
        _entries.Clear();
    }
```

with

```csharp
    public int Count => _entries.Count;

    /// <summary>
    /// Increases with every change of the indexed objects, so a reader can tell whether anything moved, appeared,
    /// disappeared or changed its blocking since it last looked.
    /// </summary>
    public long Version { get; private set; }

    public void Clear()
    {
        _chunks.Clear();
        _entries.Clear();
        Version++;
    }
```

Replace

```csharp
        _entries[gameObject] = entry;
        AddToChunks(gameObject, entry.Footprint);
    }
```

with

```csharp
        _entries[gameObject] = entry;
        AddToChunks(gameObject, entry.Footprint);
        Version++;
    }
```

Replace

```csharp
        if (_entries.Remove(gameObject, out var existing))
        {
            RemoveFromChunks(gameObject, existing.Footprint);
        }
```

with

```csharp
        if (_entries.Remove(gameObject, out var existing))
        {
            RemoveFromChunks(gameObject, existing.Footprint);
            Version++;
        }
```

- [ ] **Step 5: Add the volume table**

Create `src/HomeBlaze/HomeBlaze.Game/Voice/GameVolumeTable.cs`:

```csharp
using HomeBlaze.Game.World;

namespace HomeBlaze.Game.Voice;

/// <summary>
/// A player that a listener hears, and how loud, above 0 up to 1.
/// </summary>
internal readonly record struct GameVolumeEntry(string PlayerId, float Volume);

/// <summary>
/// For every connected player, the players it hears and their volume, by <see cref="HearingRange"/>. Never changed
/// after it is built, so any thread may read it.
/// </summary>
internal sealed class GameVolumeTable
{
    public static readonly GameVolumeTable Empty = new(new Dictionary<string, GameVolumeEntry[]>());

    private readonly Dictionary<string, GameVolumeEntry[]> _rows;

    /// <param name="rows">The rows by player id, which the table owns from then on.</param>
    public GameVolumeTable(Dictionary<string, GameVolumeEntry[]> rows)
    {
        _rows = rows;
    }

    /// <summary>
    /// Whether the player has a row, which every connected player has, also when it hears nobody.
    /// </summary>
    public bool HasRow(string playerId) => _rows.ContainsKey(playerId);

    /// <summary>
    /// Gets the players the player hears; empty for a player without a row.
    /// </summary>
    public ReadOnlySpan<GameVolumeEntry> GetRow(string playerId) =>
        _rows.TryGetValue(playerId, out var row) ? row : ReadOnlySpan<GameVolumeEntry>.Empty;

    /// <summary>
    /// Builds the table from rows that are lists.
    /// </summary>
    public static GameVolumeTable Create(Dictionary<string, List<GameVolumeEntry>> rows)
    {
        var arrays = new Dictionary<string, GameVolumeEntry[]>(rows.Count);
        foreach (var (playerId, row) in rows)
        {
            arrays[playerId] = [.. row];
        }

        return new GameVolumeTable(arrays);
    }

    /// <summary>
    /// Builds the table for the connected players, comparing only players within the hearing radius, which the index
    /// finds. The index must be up to date; the caller guards it.
    /// </summary>
    public static GameVolumeTable Build(Dictionary<string, GamePlayer> players, GameWorldIndex index)
    {
        var rows = new Dictionary<string, List<GameVolumeEntry>>();
        foreach (var player in players.Values)
        {
            if (player.IsConnected)
            {
                rows[player.ClientId] = [];
            }
        }

        var candidates = new List<GameObject>();
        foreach (var player in players.Values)
        {
            if (player.IsConnected && rows.TryGetValue(player.ClientId, out var row))
            {
                candidates.Clear();
                index.Query(HearingRange.GetArea(player.Footprint), candidates);
                AddAudible(player, row, candidates, rows, index);
            }
        }

        return Create(rows);
    }

    // Computes each pair once, from the player with the smaller id, and adds it to both rows.
    private static void AddAudible(GamePlayer listener, List<GameVolumeEntry> listenerRow, List<GameObject> candidates,
        Dictionary<string, List<GameVolumeEntry>> rows, GameWorldIndex index)
    {
        foreach (var candidate in candidates)
        {
            if (candidate is not GamePlayer speaker || string.CompareOrdinal(listener.ClientId, speaker.ClientId) >= 0
                || !rows.TryGetValue(speaker.ClientId, out var speakerRow))
            {
                continue;
            }

            var volume = (float)HearingRange.GetVolume(listener, speaker, index);
            if (volume > 0)
            {
                listenerRow.Add(new GameVolumeEntry(speaker.ClientId, volume));
                speakerRow.Add(new GameVolumeEntry(listener.ClientId, volume));
            }
        }
    }
}
```

Create `src/HomeBlaze/HomeBlaze.Game/Game.Volume.cs`:

```csharp
using HomeBlaze.Game.Voice;
using HomeBlaze.Game.World;

namespace HomeBlaze.Game;

public partial class Game
{
    // Replaced under _lock only and read without it; a published table never changes.
    private volatile GameVolumeTable _volumeTable = GameVolumeTable.Empty;

    // Guarded by _lock: set by the voice toggles, which do not change the index.
    private bool _isVolumeTableDirty = true;

    // Guarded by _lock: the index version and the time of the last rebuild.
    private long _volumeTableIndexVersion = -1;
    private long? _lastVolumeTableRebuild;

    /// <summary>
    /// For every connected player, the players it hears and how loud.
    /// </summary>
    internal GameVolumeTable VolumeTable => _volumeTable;

    /// <summary>
    /// Raised after a new <see cref="VolumeTable"/> was published, outside the game's lock; handlers must not block.
    /// </summary>
    internal event Action? VolumeTableChanged;

    /// <summary>
    /// Rebuilds the volume table when the index changed or a toggle was switched and at least
    /// <see cref="GameVoiceSettings.VolumeTableInterval"/> passed since the last rebuild, then raises
    /// <see cref="VolumeTableChanged"/>.
    /// </summary>
    /// <returns>Whether the table was rebuilt.</returns>
    internal bool UpdateVolumeTable()
    {
        if (!TryRebuildVolumeTable())
        {
            return false;
        }

        VolumeTableChanged?.Invoke();
        return true;
    }

    private bool TryRebuildVolumeTable()
    {
        lock (_lock)
        {
            var index = GetIndex();
            var now = _timeProvider.GetTimestamp();
            if (!IsVolumeTableDue(index, now))
            {
                return false;
            }

            _volumeTable = GameVolumeTable.Build(Players, index);
            _isVolumeTableDirty = false;
            _volumeTableIndexVersion = index.Version;
            _lastVolumeTableRebuild = now;
            return true;
        }
    }

    // Callers hold _lock.
    private bool IsVolumeTableDue(GameWorldIndex index, long now)
    {
        if (!_isVolumeTableDirty && index.Version == _volumeTableIndexVersion)
        {
            return false;
        }

        return _lastVolumeTableRebuild is not { } last
            || _timeProvider.GetElapsedTime(last, now) >= GameVoiceSettings.VolumeTableInterval;
    }
}
```

- [ ] **Step 6: Run the tests to see them pass**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Game.Tests --filter "FullyQualifiedName~GameVolumeTableTests|FullyQualifiedName~GameWorldIndexTests"`
Expected: all pass. Then the whole unit suite: `dotnet test src/HomeBlaze/HomeBlaze.Game.Tests --filter "FullyQualifiedName!~E2E"`, expected all pass.

If S1200 (class coupling) fires on `Game`, stop and report it; do not suppress it.

- [ ] **Step 7: Commit and push**

```bash
git add src/HomeBlaze/HomeBlaze.Game/Voice/GameVolumeTable.cs src/HomeBlaze/HomeBlaze.Game/Game.Volume.cs src/HomeBlaze/HomeBlaze.Game/World/GameWorldIndex.cs src/HomeBlaze/HomeBlaze.Game.Tests/World/GameWorldIndexTests.cs src/HomeBlaze/HomeBlaze.Game.Tests/GameVolumeTableTests.cs
git commit -m "feat: keep a table of who hears whom in the game" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

---

## Task 3: Voice primitives and mixer

**Files:**
- Rename: `src/HomeBlaze/HomeBlaze.Game/World/StepRateLimiter.cs` to `src/HomeBlaze/HomeBlaze.Game/World/RateLimiter.cs`
- Rename: `src/HomeBlaze/HomeBlaze.Game.Tests/World/StepRateLimiterTests.cs` to `src/HomeBlaze/HomeBlaze.Game.Tests/World/RateLimiterTests.cs`
- Create: `src/HomeBlaze/HomeBlaze.Game/Voice/GameVoiceCodec.cs`
- Create: `src/HomeBlaze/HomeBlaze.Game/Voice/SpeakerQueue.cs`
- Create: `src/HomeBlaze/HomeBlaze.Game/Voice/IGameVoiceSink.cs`
- Create: `src/HomeBlaze/HomeBlaze.Game/Voice/GameVoiceMixer.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Game/GamePlayer.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Game/Game.Movement.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Game.Tests/GameRuntimeTests.cs`
- Create: `src/HomeBlaze/HomeBlaze.Game.Tests/Testing/VoiceFrames.cs`
- Create: `src/HomeBlaze/HomeBlaze.Game.Tests/Testing/RecordingVoiceSink.cs`
- Test: `src/HomeBlaze/HomeBlaze.Game.Tests/Voice/GameVoiceCodecTests.cs`
- Test: `src/HomeBlaze/HomeBlaze.Game.Tests/Voice/SpeakerQueueTests.cs`
- Test: `src/HomeBlaze/HomeBlaze.Game.Tests/Voice/GameVoiceMixerTests.cs`

- [ ] **Step 1: Rename the rate limiter files**

```bash
git mv src/HomeBlaze/HomeBlaze.Game/World/StepRateLimiter.cs src/HomeBlaze/HomeBlaze.Game/World/RateLimiter.cs
git mv src/HomeBlaze/HomeBlaze.Game.Tests/World/StepRateLimiterTests.cs src/HomeBlaze/HomeBlaze.Game.Tests/World/RateLimiterTests.cs
```

- [ ] **Step 2: Write the failing rate limiter tests**

Replace the whole content of `src/HomeBlaze/HomeBlaze.Game.Tests/World/RateLimiterTests.cs` with:

```csharp
using HomeBlaze.Game.World;
using Microsoft.Extensions.Time.Testing;

namespace HomeBlaze.Game.Tests.World;

public class RateLimiterTests
{
    [Fact]
    public void WhenFourStepsComeAtOnce_ThenTheBurstAllowsThreeOfThem()
    {
        // Arrange
        var limiter = new RateLimiter(new FakeTimeProvider(), Game.StepInterval, Game.StepBurst);

        // Act
        var results = new[] { limiter.TryTake(), limiter.TryTake(), limiter.TryTake(), limiter.TryTake() };

        // Assert
        Assert.Equal(new[] { true, true, true, false }, results);
    }

    [Fact]
    public void WhenOneIntervalPasses_ThenOneMoreStepIsAllowed()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var limiter = new RateLimiter(timeProvider, Game.StepInterval, Game.StepBurst);
        limiter.TryTake();
        limiter.TryTake();
        limiter.TryTake();

        // Act
        timeProvider.Advance(Game.StepInterval);
        var first = limiter.TryTake();
        var second = limiter.TryTake();

        // Assert
        Assert.True(first);
        Assert.False(second);
    }

    [Fact]
    public void WhenMoreTimePassesThanTheBurstHolds_ThenTheTokensStopAtTheBurst()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var limiter = new RateLimiter(timeProvider, TimeSpan.FromMilliseconds(10), burst: 1);
        limiter.TryTake();

        // Act
        timeProvider.Advance(TimeSpan.FromMilliseconds(25));
        var results = new[] { limiter.TryTake(), limiter.TryTake() };

        // Assert
        Assert.Equal(new[] { true, false }, results);
    }
}
```

In `src/HomeBlaze/HomeBlaze.Game.Tests/GameRuntimeTests.cs`, replace

```csharp
            for (var step = 0; step < StepRateLimiter.Burst; step++)
```

with

```csharp
            for (var step = 0; step < Game.StepBurst; step++)
```

and remove the now unused line `using HomeBlaze.Game.World;` at the top of that file.

- [ ] **Step 3: Write the test helpers**

Create `src/HomeBlaze/HomeBlaze.Game.Tests/Testing/VoiceFrames.cs`:

```csharp
using System.Buffers.Binary;
using HomeBlaze.Game.Voice;

namespace HomeBlaze.Game.Tests.Testing;

/// <summary>
/// Builds and reads voice frames of 16-bit little-endian samples.
/// </summary>
internal static class VoiceFrames
{
    /// <summary>
    /// Creates a frame whose samples all have the value.
    /// </summary>
    public static byte[] Create(short sample)
    {
        var frame = new byte[GameVoiceSettings.FrameBytes];
        for (var index = 0; index < GameVoiceSettings.SamplesPerFrame; index++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(frame.AsSpan(index * 2), sample);
        }

        return frame;
    }

    public static short ReadSample(byte[] frame, int index) =>
        BinaryPrimitives.ReadInt16LittleEndian(frame.AsSpan(index * 2));
}
```

Create `src/HomeBlaze/HomeBlaze.Game.Tests/Testing/RecordingVoiceSink.cs`:

```csharp
using HomeBlaze.Game.Voice;

namespace HomeBlaze.Game.Tests.Testing;

/// <summary>
/// Records the frames sent to it; thread-safe, because the voice loop of a running game sends from its own thread.
/// </summary>
internal sealed class RecordingVoiceSink : IGameVoiceSink
{
    private readonly Lock _lock = new();
    private readonly List<byte[]> _frames = [];

    public IReadOnlyList<byte[]> Frames
    {
        get
        {
            lock (_lock)
            {
                return [.. _frames];
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _frames.Count;
            }
        }
    }

    public void Send(byte[] frame)
    {
        lock (_lock)
        {
            _frames.Add(frame);
        }
    }
}
```

- [ ] **Step 4: Write the failing codec tests**

Create `src/HomeBlaze/HomeBlaze.Game.Tests/Voice/GameVoiceCodecTests.cs`:

```csharp
using HomeBlaze.Game.Tests.Testing;
using HomeBlaze.Game.Voice;

namespace HomeBlaze.Game.Tests.Voice;

public class GameVoiceCodecTests
{
    [Fact]
    public void WhenAFrameIsDecoded_ThenItsSamplesAreReadAsLittleEndian()
    {
        // Arrange
        var frame = new byte[GameVoiceSettings.FrameBytes];
        frame[0] = 0x34;
        frame[1] = 0x12;
        frame[2] = 0xFF;
        frame[3] = 0xFF;
        var samples = new short[GameVoiceSettings.SamplesPerFrame];

        // Act
        GameVoiceCodec.Decode(frame, samples);

        // Assert
        Assert.Equal((short)0x1234, samples[0]);
        Assert.Equal((short)-1, samples[1]);
        Assert.Equal((short)0, samples[2]);
    }

    [Fact]
    public void WhenAFrameHasTheWrongSize_ThenDecodingThrows()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => GameVoiceCodec.Decode(new byte[10], new short[GameVoiceSettings.SamplesPerFrame]));
    }

    [Fact]
    public void WhenSamplesAreEncoded_ThenTheyAreClampedAndWrittenAsLittleEndian()
    {
        // Arrange
        var samples = new int[GameVoiceSettings.SamplesPerFrame];
        samples[0] = 0x1234;
        samples[1] = 40000;
        samples[2] = -40000;

        // Act
        var frame = GameVoiceCodec.Encode(samples);

        // Assert
        Assert.Equal(GameVoiceSettings.FrameBytes, frame.Length);
        Assert.Equal(new byte[] { 0x34, 0x12 }, frame[..2]);
        Assert.Equal(short.MaxValue, VoiceFrames.ReadSample(frame, 1));
        Assert.Equal(short.MinValue, VoiceFrames.ReadSample(frame, 2));
    }
}
```

- [ ] **Step 5: Write the failing speaker queue tests**

Create `src/HomeBlaze/HomeBlaze.Game.Tests/Voice/SpeakerQueueTests.cs`:

```csharp
using HomeBlaze.Game.Tests.Testing;
using HomeBlaze.Game.Voice;
using Microsoft.Extensions.Time.Testing;

namespace HomeBlaze.Game.Tests.Voice;

public class SpeakerQueueTests
{
    private readonly FakeTimeProvider _timeProvider = new();
    private readonly short[] _frame = new short[GameVoiceSettings.SamplesPerFrame];

    private short? Dequeue(SpeakerQueue queue) => queue.TryDequeue(_frame) ? _frame[0] : null;

    private static void FillToStart(SpeakerQueue queue)
    {
        for (var index = 0; index < queue.StartFrames; index++)
        {
            queue.Enqueue(VoiceFrames.Create(0));
        }
    }

    private void DrainUntilDry(SpeakerQueue queue)
    {
        var isPlaying = true;
        while (isPlaying)
        {
            isPlaying = queue.TryDequeue(_frame);
        }
    }

    // Lets the queue run dry while playing until its start size is the given one.
    private void GrowTo(SpeakerQueue queue, int startFrames)
    {
        while (queue.StartFrames < startFrames)
        {
            FillToStart(queue);
            DrainUntilDry(queue);
        }
    }

    [Fact]
    public void WhenTheQueueIsEmpty_ThenItDoesNotPlay()
    {
        // Arrange
        using var queue = new SpeakerQueue(_timeProvider);

        // Act
        var sample = Dequeue(queue);

        // Assert
        Assert.Null(sample);
        Assert.False(queue.IsPlaying);
        Assert.Equal(GameVoiceSettings.ServerQueueStart, queue.StartFrames);
    }

    [Fact]
    public void WhenTheQueueHoldsItsStartSize_ThenItPlaysTheFramesInOrder()
    {
        // Arrange
        using var queue = new SpeakerQueue(_timeProvider);
        queue.Enqueue(VoiceFrames.Create(1));
        queue.Enqueue(VoiceFrames.Create(2));

        // Act
        var samples = new[] { Dequeue(queue), Dequeue(queue) };

        // Assert
        Assert.Equal(new short?[] { 1, 2 }, samples);
    }

    [Fact]
    public void WhenItRunsDryWhilePlaying_ThenItGrowsAndWaitsForTheLargerStartSize()
    {
        // Arrange
        using var queue = new SpeakerQueue(_timeProvider);
        queue.Enqueue(VoiceFrames.Create(1));
        Dequeue(queue);

        // Act
        var dry = Dequeue(queue);
        queue.Enqueue(VoiceFrames.Create(2));
        var beforeStart = Dequeue(queue);
        queue.Enqueue(VoiceFrames.Create(3));
        var afterStart = Dequeue(queue);

        // Assert
        Assert.Null(dry);
        Assert.Equal(GameVoiceSettings.ServerQueueStart + GameVoiceSettings.BufferGrowth, queue.StartFrames);
        Assert.Null(beforeStart);
        Assert.Equal((short?)2, afterStart);
    }

    [Fact]
    public void WhenMoreFramesArriveThanOneGrowthStepAboveTheStart_ThenTheOldestAreDropped()
    {
        // Arrange
        using var queue = new SpeakerQueue(_timeProvider);

        // Act
        queue.Enqueue(VoiceFrames.Create(1));
        queue.Enqueue(VoiceFrames.Create(2));
        queue.Enqueue(VoiceFrames.Create(3));

        // Assert
        Assert.Equal(GameVoiceSettings.ServerQueueStart + GameVoiceSettings.BufferGrowth, queue.Count);
        Assert.Equal(new short?[] { 2, 3 }, new[] { Dequeue(queue), Dequeue(queue) });
    }

    [Fact]
    public void WhenTheStartSizeReachedTheMaximum_ThenTheQueueKeepsAtMostTheMaximum()
    {
        // Arrange
        using var queue = new SpeakerQueue(_timeProvider);
        GrowTo(queue, GameVoiceSettings.ServerQueueMaximum);

        // Act
        for (short sample = 1; sample <= GameVoiceSettings.ServerQueueMaximum + 2; sample++)
        {
            queue.Enqueue(VoiceFrames.Create(sample));
        }

        // Assert
        Assert.Equal(GameVoiceSettings.ServerQueueMaximum, queue.StartFrames);
        Assert.Equal(GameVoiceSettings.ServerQueueMaximum, queue.Count);
        Assert.Equal((short?)3, Dequeue(queue));
    }

    [Fact]
    public void WhenItPlaysWithoutRunningDryForTheShrinkDelay_ThenTheStartSizeShrinks()
    {
        // Arrange
        using var queue = new SpeakerQueue(_timeProvider);
        GrowTo(queue, 2);
        FillToStart(queue);

        // Act
        _timeProvider.Advance(GameVoiceSettings.BufferShrinkDelay);
        Dequeue(queue);

        // Assert
        Assert.Equal(GameVoiceSettings.ServerQueueStart, queue.StartFrames);
    }

    [Fact]
    public void WhenItRanDryLessThanTheShrinkDelayAgo_ThenTheStartSizeStays()
    {
        // Arrange
        using var queue = new SpeakerQueue(_timeProvider);
        GrowTo(queue, 2);
        FillToStart(queue);

        // Act
        _timeProvider.Advance(GameVoiceSettings.BufferShrinkDelay - TimeSpan.FromMilliseconds(1));
        Dequeue(queue);

        // Assert
        Assert.Equal(2, queue.StartFrames);
    }
}
```

- [ ] **Step 6: Write the failing mixer tests**

Create `src/HomeBlaze/HomeBlaze.Game.Tests/Voice/GameVoiceMixerTests.cs`:

```csharp
using HomeBlaze.Game.Tests.Testing;
using HomeBlaze.Game.Voice;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace HomeBlaze.Game.Tests.Voice;

public class GameVoiceMixerTests
{
    private readonly FakeTimeProvider _timeProvider = new();
    private readonly RecordingVoiceSink _sink = new();
    private readonly GameVoiceMixer _mixer;

    public GameVoiceMixerTests()
    {
        _mixer = new GameVoiceMixer(_timeProvider, NullLogger.Instance);
    }

    private static GameVolumeTable CreateTable(params (string Listener, string Speaker, float Volume)[] entries)
    {
        var rows = new Dictionary<string, List<GameVolumeEntry>>();
        foreach (var (listener, speaker, volume) in entries)
        {
            if (!rows.TryGetValue(listener, out var row))
            {
                row = [];
                rows[listener] = row;
            }

            row.Add(new GameVolumeEntry(speaker, volume));
        }

        return GameVolumeTable.Create(rows);
    }

    private void Speak(string playerId, short sample, int pageId = 1)
    {
        _mixer.SetMicrophonePage(playerId, pageId);
        _mixer.Receive(playerId, pageId, VoiceFrames.Create(sample));
    }

    [Fact]
    public void WhenTwoSpeakersAreAudible_ThenTheListenerGetsTheirSumScaledByVolume()
    {
        // Arrange
        using var registration = _mixer.RegisterSink("listener", _sink);
        Speak("first", 1000);
        Speak("second", 2000);
        var table = CreateTable(("listener", "first", 1f), ("listener", "second", 0.5f));

        // Act
        _mixer.Mix(table);

        // Assert
        var frame = Assert.Single(_sink.Frames);
        Assert.Equal(GameVoiceSettings.FrameBytes, frame.Length);
        Assert.Equal(2000, VoiceFrames.ReadSample(frame, 0));
        Assert.Equal(2000, VoiceFrames.ReadSample(frame, GameVoiceSettings.SamplesPerFrame - 1));
    }

    [Theory]
    [InlineData(30000, 32767)]
    [InlineData(-30000, -32768)]
    public void WhenTheSumLeavesThe16BitRange_ThenItIsClamped(int sample, int expected)
    {
        // Arrange
        using var registration = _mixer.RegisterSink("listener", _sink);
        Speak("first", (short)sample);
        Speak("second", (short)sample);

        // Act
        _mixer.Mix(CreateTable(("listener", "first", 1f), ("listener", "second", 1f)));

        // Assert
        Assert.Equal(expected, VoiceFrames.ReadSample(Assert.Single(_sink.Frames), 0));
    }

    [Fact]
    public void WhenTheListenerSpeaks_ThenItDoesNotHearItself()
    {
        // Arrange
        using var registration = _mixer.RegisterSink("listener", _sink);
        Speak("listener", 1000);
        Speak("other", 2000);

        // Act
        _mixer.Mix(CreateTable(("listener", "listener", 1f), ("listener", "other", 1f)));

        // Assert
        Assert.Equal(2000, VoiceFrames.ReadSample(Assert.Single(_sink.Frames), 0));
    }

    [Fact]
    public void WhenTheListenerHearsNobody_ThenItsSinkGetsNothing()
    {
        // Arrange
        using var registration = _mixer.RegisterSink("listener", _sink);
        Speak("speaker", 1000);

        // Act
        _mixer.Mix(CreateTable(("other", "speaker", 1f)));

        // Assert
        Assert.Empty(_sink.Frames);
    }

    [Fact]
    public void WhenTheSpeakerHasNoMicrophonePage_ThenItsFramesAreDropped()
    {
        // Arrange
        using var registration = _mixer.RegisterSink("listener", _sink);
        _mixer.Receive("speaker", 1, VoiceFrames.Create(1000));

        // Act
        _mixer.Mix(CreateTable(("listener", "speaker", 1f)));

        // Assert
        Assert.Empty(_sink.Frames);
    }

    [Fact]
    public void WhenTheMicrophonePageIsCleared_ThenTheQueuedFramesAreDropped()
    {
        // Arrange
        using var registration = _mixer.RegisterSink("listener", _sink);
        Speak("speaker", 1000);

        // Act
        _mixer.SetMicrophonePage("speaker", null);
        _mixer.Mix(CreateTable(("listener", "speaker", 1f)));

        // Assert
        Assert.Empty(_sink.Frames);
    }

    [Fact]
    public void WhenAFrameHasTheWrongSize_ThenItIsDropped()
    {
        // Arrange
        using var registration = _mixer.RegisterSink("listener", _sink);
        _mixer.SetMicrophonePage("speaker", 1);
        _mixer.Receive("speaker", 1, new byte[GameVoiceSettings.FrameBytes - 1]);

        // Act
        _mixer.Mix(CreateTable(("listener", "speaker", 1f)));

        // Assert
        Assert.Empty(_sink.Frames);
    }

    [Fact]
    public void WhenFramesComeFasterThanTheTolerance_ThenTheExcessIsDropped()
    {
        // Arrange
        using var registration = _mixer.RegisterSink("listener", _sink);
        _mixer.SetMicrophonePage("speaker", 1);
        var table = CreateTable(("listener", "speaker", 1f));

        // The clock stands still, so the bucket holds only its burst and refuses the last frame.
        for (short sample = 1; sample <= GameVoiceSettings.ServerQueueMaximum + 1; sample++)
        {
            _mixer.Receive("speaker", 1, VoiceFrames.Create(sample));
        }

        // Act
        _mixer.Mix(table);
        _mixer.Mix(table);

        // Assert
        // The queue keeps the newest two frames it accepted: its start size of one plus one growth step.
        var samples = _sink.Frames.Select(frame => (int)VoiceFrames.ReadSample(frame, 0));
        Assert.Equal(new[] { GameVoiceSettings.ServerQueueMaximum - 1, GameVoiceSettings.ServerQueueMaximum }, samples);
    }

    [Fact]
    public void WhenAnotherPageBecomesTheMicrophonePage_ThenOnlyItsFramesCount()
    {
        // Arrange
        using var registration = _mixer.RegisterSink("listener", _sink);
        _mixer.SetMicrophonePage("speaker", 1);
        _mixer.SetMicrophonePage("speaker", 2);
        _mixer.Receive("speaker", 1, VoiceFrames.Create(1000));
        _mixer.Receive("speaker", 2, VoiceFrames.Create(2000));

        // Act
        _mixer.Mix(CreateTable(("listener", "speaker", 1f)));

        // Assert
        Assert.Equal(2000, VoiceFrames.ReadSample(Assert.Single(_sink.Frames), 0));
    }

    [Fact]
    public void WhenASinkThrows_ThenTheOtherSinksStillGetTheFrameAndTheNextMixRuns()
    {
        // Arrange
        using var failing = _mixer.RegisterSink("listener", new ThrowingVoiceSink());
        using var registration = _mixer.RegisterSink("listener", _sink);
        var table = CreateTable(("listener", "speaker", 1f));
        Speak("speaker", 1000);

        // Act
        var exception = Record.Exception(() => _mixer.Mix(table));
        Speak("speaker", 1000);
        _mixer.Mix(table);

        // Assert
        Assert.Null(exception);
        Assert.Equal(2, _sink.Count);
    }

    [Fact]
    public void WhenNoSpeakerHasAFrame_ThenNothingIsSent()
    {
        // Arrange
        using var registration = _mixer.RegisterSink("listener", _sink);
        _mixer.SetMicrophonePage("speaker", 1);

        // Act
        _mixer.Mix(CreateTable(("listener", "speaker", 1f)));

        // Assert
        Assert.Empty(_sink.Frames);
    }

    [Fact]
    public void WhenTheSinkIsUnregistered_ThenItGetsNothing()
    {
        // Arrange
        var registration = _mixer.RegisterSink("listener", _sink);
        Speak("speaker", 1000);

        // Act
        registration.Dispose();
        _mixer.Mix(CreateTable(("listener", "speaker", 1f)));

        // Assert
        Assert.Empty(_sink.Frames);
    }

    private sealed class ThrowingVoiceSink : IGameVoiceSink
    {
        public void Send(byte[] frame) => throw new InvalidOperationException("The page is gone.");
    }
}
```

- [ ] **Step 7: Run the tests to see them fail**

Run: `dotnet build src/HomeBlaze/HomeBlaze.Game.Tests -c Release`
Expected: build fails because `RateLimiter`, `Game.StepBurst`, `GameVoiceCodec`, `SpeakerQueue`, `IGameVoiceSink` and `GameVoiceMixer` do not exist.

- [ ] **Step 8: Generalize the rate limiter**

Replace the whole content of `src/HomeBlaze/HomeBlaze.Game/World/RateLimiter.cs` with:

```csharp
namespace HomeBlaze.Game.World;

/// <summary>
/// A token bucket that refills one token per interval up to a burst, so network jitter does not refuse what a client
/// sent in time. Not thread-safe: its owner guards it with a lock.
/// </summary>
internal sealed class RateLimiter(TimeProvider timeProvider, TimeSpan interval, int burst)
{
    private double _tokens = burst;
    private long _lastTimestamp = timeProvider.GetTimestamp();

    public bool TryTake()
    {
        var now = timeProvider.GetTimestamp();
        var elapsed = timeProvider.GetElapsedTime(_lastTimestamp, now);
        _lastTimestamp = now;
        _tokens = Math.Min(burst, _tokens + elapsed / interval);
        if (_tokens < 1)
        {
            return false;
        }

        _tokens -= 1;
        return true;
    }
}
```

In `src/HomeBlaze/HomeBlaze.Game/GamePlayer.cs`, replace

```csharp
    internal StepRateLimiter? StepRateLimiter { get; set; }

    internal StepRateLimiter? TurnRateLimiter { get; set; }
```

with

```csharp
    internal RateLimiter? StepRateLimiter { get; set; }

    internal RateLimiter? TurnRateLimiter { get; set; }
```

In `src/HomeBlaze/HomeBlaze.Game/Game.Movement.cs`, replace

```csharp
public partial class Game
{
    /// <summary>
    /// Turns the player toward <paramref name="direction"/> and, unless <paramref name="turnOnly"/>, steps one tile that
```

with

```csharp
public partial class Game
{
    /// <summary>
    /// The steps, and separately the turns, a player may take at once after a pause; one more each
    /// <see cref="StepInterval"/>.
    /// </summary>
    internal const int StepBurst = 3;

    /// <summary>
    /// Turns the player toward <paramref name="direction"/> and, unless <paramref name="turnOnly"/>, steps one tile that
```

and replace

```csharp
            var rateLimiter = turnOnly
                ? player.TurnRateLimiter ??= new StepRateLimiter(_timeProvider)
                : player.StepRateLimiter ??= new StepRateLimiter(_timeProvider);
```

with

```csharp
            var rateLimiter = turnOnly
                ? player.TurnRateLimiter ??= new RateLimiter(_timeProvider, StepInterval, StepBurst)
                : player.StepRateLimiter ??= new RateLimiter(_timeProvider, StepInterval, StepBurst);
```

- [ ] **Step 9: Add the codec**

Create `src/HomeBlaze/HomeBlaze.Game/Voice/GameVoiceCodec.cs`:

```csharp
using System.Buffers.Binary;

namespace HomeBlaze.Game.Voice;

/// <summary>
/// Turns frames into samples and back: 16-bit little-endian PCM, the one place in C# to change for another codec.
/// </summary>
internal static class GameVoiceCodec
{
    /// <summary>
    /// Decodes a frame into its samples.
    /// </summary>
    /// <exception cref="ArgumentException">The frame does not have exactly <see cref="GameVoiceSettings.FrameBytes"/>
    /// bytes, or the samples have room for less than a frame.</exception>
    public static void Decode(ReadOnlySpan<byte> frame, Span<short> samples)
    {
        if (frame.Length != GameVoiceSettings.FrameBytes || samples.Length < GameVoiceSettings.SamplesPerFrame)
        {
            throw new ArgumentException($"A frame has exactly {GameVoiceSettings.FrameBytes} bytes.", nameof(frame));
        }

        for (var index = 0; index < GameVoiceSettings.SamplesPerFrame; index++)
        {
            samples[index] = BinaryPrimitives.ReadInt16LittleEndian(frame[(index * 2)..]);
        }
    }

    /// <summary>
    /// Encodes summed samples into a new frame, clamping each to the 16-bit range.
    /// </summary>
    public static byte[] Encode(ReadOnlySpan<int> samples)
    {
        var frame = new byte[samples.Length * 2];
        for (var index = 0; index < samples.Length; index++)
        {
            var sample = (short)Math.Clamp(samples[index], short.MinValue, short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(frame.AsSpan(index * 2), sample);
        }

        return frame;
    }
}
```

- [ ] **Step 10: Add the speaker queue**

Create `src/HomeBlaze/HomeBlaze.Game/Voice/SpeakerQueue.cs`:

```csharp
using System.Buffers;

namespace HomeBlaze.Game.Voice;

/// <summary>
/// The frames of one speaker waiting for the mixer, in a pooled ring buffer. It plays once it holds its start size,
/// grows the start size by <see cref="GameVoiceSettings.BufferGrowth"/> when it runs dry while playing, and shrinks it
/// by one frame after <see cref="GameVoiceSettings.BufferShrinkDelay"/> without running dry. It keeps at most its start
/// size plus one growth step, and never more than <see cref="GameVoiceSettings.ServerQueueMaximum"/>, dropping the
/// oldest frames. Not thread-safe: the mixer guards it with its lock.
/// </summary>
internal sealed class SpeakerQueue : IDisposable
{
    private const int Samples = GameVoiceSettings.SamplesPerFrame;
    private const int Capacity = GameVoiceSettings.ServerQueueMaximum;

    private readonly TimeProvider _timeProvider;
    private short[]? _buffer = ArrayPool<short>.Shared.Rent(Capacity * Samples);
    private int _head;
    private long _lastDryTimestamp;

    public SpeakerQueue(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
        _lastDryTimestamp = timeProvider.GetTimestamp();
    }

    /// <summary>
    /// The frames waiting.
    /// </summary>
    public int Count { get; private set; }

    /// <summary>
    /// The frames the queue needs before it plays again.
    /// </summary>
    public int StartFrames { get; private set; } = GameVoiceSettings.ServerQueueStart;

    public bool IsPlaying { get; private set; }

    /// <summary>
    /// Adds a frame of exactly <see cref="GameVoiceSettings.FrameBytes"/> bytes.
    /// </summary>
    public void Enqueue(ReadOnlySpan<byte> frame)
    {
        if (_buffer is not { } buffer)
        {
            throw new ObjectDisposedException(nameof(SpeakerQueue));
        }

        var limit = Math.Min(Capacity, StartFrames + GameVoiceSettings.BufferGrowth);
        while (Count >= limit)
        {
            DropOldest();
        }

        var slot = (_head + Count) % Capacity;
        GameVoiceCodec.Decode(frame, buffer.AsSpan(slot * Samples, Samples));
        Count++;
        IsPlaying |= Count >= StartFrames;
    }

    /// <summary>
    /// Copies the oldest frame into <paramref name="destination"/>; returns false while the queue does not play, which
    /// includes the call that finds it empty and makes it run dry.
    /// </summary>
    public bool TryDequeue(Span<short> destination)
    {
        if (!IsPlaying || _buffer is not { } buffer)
        {
            return false;
        }

        var now = _timeProvider.GetTimestamp();
        if (Count == 0)
        {
            RunDry(now);
            return false;
        }

        buffer.AsSpan(_head * Samples, Samples).CopyTo(destination);
        DropOldest();
        ShrinkWhenSteady(now);
        return true;
    }

    public void Dispose()
    {
        if (_buffer is { } buffer)
        {
            _buffer = null;
            ArrayPool<short>.Shared.Return(buffer);
        }
    }

    private void DropOldest()
    {
        _head = (_head + 1) % Capacity;
        Count--;
    }

    private void RunDry(long now)
    {
        IsPlaying = false;
        StartFrames = Math.Min(Capacity, StartFrames + GameVoiceSettings.BufferGrowth);
        _lastDryTimestamp = now;
    }

    private void ShrinkWhenSteady(long now)
    {
        if (StartFrames > GameVoiceSettings.ServerQueueStart
            && _timeProvider.GetElapsedTime(_lastDryTimestamp, now) >= GameVoiceSettings.BufferShrinkDelay)
        {
            StartFrames--;
            _lastDryTimestamp = now;
        }
    }
}
```

- [ ] **Step 11: Add the sink interface**

Create `src/HomeBlaze/HomeBlaze.Game/Voice/IGameVoiceSink.cs`:

```csharp
namespace HomeBlaze.Game.Voice;

/// <summary>
/// One page of a listener with the speaker on.
/// </summary>
internal interface IGameVoiceSink
{
    /// <summary>
    /// Sends a mixed frame to the page. Called on the mixer's tick, so it returns without waiting for the browser. The
    /// sinks of one listener share the frame and never change it.
    /// </summary>
    void Send(byte[] frame);
}
```

- [ ] **Step 12: Add the mixer**

Create `src/HomeBlaze/HomeBlaze.Game/Voice/GameVoiceMixer.cs`:

```csharp
using HomeBlaze.Game.World;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Game.Voice;

/// <summary>
/// Mixes the voices of one game individually for every listener. Its own lock guards the speaker queues, microphone
/// pages and sinks; the game calls it while holding the game's lock, and the mixer never calls into the game.
/// </summary>
internal sealed class GameVoiceMixer
{
    private readonly Lock _lock = new();
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    // Guarded by _lock.
    private readonly Dictionary<string, Speaker> _speakers = new();
    private readonly Dictionary<string, IGameVoiceSink[]> _sinks = new();

    // Used only by Mix, which one loop runs at a time.
    private readonly Dictionary<string, short[]> _frames = new();
    private readonly Stack<short[]> _spareFrames = new();
    private readonly List<KeyValuePair<string, IGameVoiceSink[]>> _listeners = [];
    private readonly int[] _sum = new int[GameVoiceSettings.SamplesPerFrame];

    public GameVoiceMixer(TimeProvider timeProvider, ILogger logger)
    {
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Sets the only page whose frames count for the player, or none, which drops the player's queued frames.
    /// </summary>
    public void SetMicrophonePage(string playerId, int? pageId)
    {
        lock (_lock)
        {
            if (pageId is not { } page)
            {
                RemoveSpeaker(playerId);
            }
            else if (_speakers.TryGetValue(playerId, out var speaker))
            {
                speaker.MicrophonePageId = page;
            }
            else
            {
                _speakers[playerId] = new Speaker(page, _timeProvider);
            }
        }
    }

    /// <summary>
    /// Queues a frame of the player; drops it unless it has exactly <see cref="GameVoiceSettings.FrameBytes"/> bytes,
    /// comes from the player's microphone page and stays within <see cref="GameVoiceSettings.FrameRateTolerance"/>
    /// times the frame rate.
    /// </summary>
    public void Receive(string playerId, int pageId, ReadOnlySpan<byte> frame)
    {
        if (frame.Length != GameVoiceSettings.FrameBytes)
        {
            return;
        }

        lock (_lock)
        {
            if (_speakers.TryGetValue(playerId, out var speaker) && speaker.MicrophonePageId == pageId && speaker.RateLimiter.TryTake())
            {
                speaker.Queue.Enqueue(frame);
            }
        }
    }

    /// <summary>
    /// Adds a page of the player that plays what the player hears; disposing the result removes it again.
    /// </summary>
    public IDisposable RegisterSink(string playerId, IGameVoiceSink sink)
    {
        lock (_lock)
        {
            _sinks[playerId] = _sinks.TryGetValue(playerId, out var sinks) ? [.. sinks, sink] : [sink];
        }

        return new SinkRegistration(this, playerId, sink);
    }

    /// <summary>
    /// Takes one frame from every playing speaker and sends every listener with sinks the sum of the frames of the
    /// speakers in its row of the table, scaled by their volume and without its own; sends nothing to a listener that
    /// hears no frame. Exceptions of sinks are logged.
    /// </summary>
    public void Mix(GameVolumeTable table)
    {
        TakeFrames();
        if (_frames.Count == 0)
        {
            return;
        }

        foreach (var (listenerId, sinks) in _listeners)
        {
            MixFor(listenerId, sinks, table);
        }
    }

    // Copies the frames out under the lock, so summing and sending run outside it.
    private void TakeFrames()
    {
        foreach (var frame in _frames.Values)
        {
            _spareFrames.Push(frame);
        }

        _frames.Clear();
        _listeners.Clear();
        lock (_lock)
        {
            foreach (var (playerId, speaker) in _speakers)
            {
                TakeFrame(playerId, speaker.Queue);
            }

            _listeners.AddRange(_sinks);
        }
    }

    // Callers hold _lock.
    private void TakeFrame(string playerId, SpeakerQueue queue)
    {
        var buffer = _spareFrames.TryPop(out var spare) ? spare : new short[GameVoiceSettings.SamplesPerFrame];
        if (queue.TryDequeue(buffer))
        {
            _frames[playerId] = buffer;
        }
        else
        {
            _spareFrames.Push(buffer);
        }
    }

    private void MixFor(string listenerId, IGameVoiceSink[] sinks, GameVolumeTable table)
    {
        if (!TrySum(listenerId, table.GetRow(listenerId)))
        {
            return;
        }

        // A new array per listener, because the sinks send it asynchronously.
        var frame = GameVoiceCodec.Encode(_sum);
        foreach (var sink in sinks)
        {
            Send(sink, frame);
        }
    }

    private bool TrySum(string listenerId, ReadOnlySpan<GameVolumeEntry> row)
    {
        Array.Clear(_sum);
        var isAudible = false;
        foreach (var entry in row)
        {
            if (entry.PlayerId != listenerId && _frames.TryGetValue(entry.PlayerId, out var frame))
            {
                AddScaled(frame, entry.Volume);
                isAudible = true;
            }
        }

        return isAudible;
    }

    private void AddScaled(short[] frame, float volume)
    {
        for (var index = 0; index < _sum.Length; index++)
        {
            _sum[index] += (int)(frame[index] * volume);
        }
    }

    private void Send(IGameVoiceSink sink, byte[] frame)
    {
        try
        {
            sink.Send(frame);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Sending a voice frame to a page failed");
        }
    }

    // Callers hold _lock. The queue returns its buffer to the pool, which is safe because Mix reads queues only under the lock.
    private void RemoveSpeaker(string playerId)
    {
        if (_speakers.Remove(playerId, out var speaker))
        {
            speaker.Queue.Dispose();
        }
    }

    private void UnregisterSink(string playerId, IGameVoiceSink sink)
    {
        lock (_lock)
        {
            if (!_sinks.TryGetValue(playerId, out var sinks))
            {
                return;
            }

            var remaining = Array.FindAll(sinks, candidate => !ReferenceEquals(candidate, sink));
            if (remaining.Length == 0)
            {
                _sinks.Remove(playerId);
            }
            else
            {
                _sinks[playerId] = remaining;
            }
        }
    }

    private sealed class Speaker(int microphonePageId, TimeProvider timeProvider)
    {
        public int MicrophonePageId { get; set; } = microphonePageId;

        public SpeakerQueue Queue { get; } = new(timeProvider);

        // A burst beyond what the queue keeps would be dropped there anyway.
        public RateLimiter RateLimiter { get; } = new(timeProvider,
            GameVoiceSettings.FrameDuration / GameVoiceSettings.FrameRateTolerance, GameVoiceSettings.ServerQueueMaximum);
    }

    private sealed class SinkRegistration(GameVoiceMixer mixer, string playerId, IGameVoiceSink sink) : IDisposable
    {
        private int _isDisposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _isDisposed, 1) == 0)
            {
                mixer.UnregisterSink(playerId, sink);
            }
        }
    }
}
```

- [ ] **Step 13: Run the tests to see them pass**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Game.Tests --filter "FullyQualifiedName~RateLimiterTests|FullyQualifiedName~GameVoiceCodecTests|FullyQualifiedName~SpeakerQueueTests|FullyQualifiedName~GameVoiceMixerTests"`
Expected: all pass. Then the whole unit suite: `dotnet test src/HomeBlaze/HomeBlaze.Game.Tests --filter "FullyQualifiedName!~E2E"`, expected all pass, including the movement and runtime tests that now use `RateLimiter`.

- [ ] **Step 14: Commit and push**

The two renames are already staged by `git mv`.

```bash
git add src/HomeBlaze/HomeBlaze.Game/World/RateLimiter.cs src/HomeBlaze/HomeBlaze.Game/GamePlayer.cs src/HomeBlaze/HomeBlaze.Game/Game.Movement.cs src/HomeBlaze/HomeBlaze.Game/Voice/GameVoiceCodec.cs src/HomeBlaze/HomeBlaze.Game/Voice/SpeakerQueue.cs src/HomeBlaze/HomeBlaze.Game/Voice/IGameVoiceSink.cs src/HomeBlaze/HomeBlaze.Game/Voice/GameVoiceMixer.cs src/HomeBlaze/HomeBlaze.Game.Tests/World/RateLimiterTests.cs src/HomeBlaze/HomeBlaze.Game.Tests/GameRuntimeTests.cs src/HomeBlaze/HomeBlaze.Game.Tests/Testing/VoiceFrames.cs src/HomeBlaze/HomeBlaze.Game.Tests/Testing/RecordingVoiceSink.cs src/HomeBlaze/HomeBlaze.Game.Tests/Voice/GameVoiceCodecTests.cs src/HomeBlaze/HomeBlaze.Game.Tests/Voice/SpeakerQueueTests.cs src/HomeBlaze/HomeBlaze.Game.Tests/Voice/GameVoiceMixerTests.cs
git commit -m "feat: mix the voices of the game for every listener" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

---

## Task 4: Player voice state

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Game/Game.Voice.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Game/GamePlayer.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Game/Game.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Game/Game.Players.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Game/View/GameViewObject.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Game/View/GameViewTracker.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Game.Tests/Testing/GameTestContext.cs`
- Test: `src/HomeBlaze/HomeBlaze.Game.Tests/GameVoiceTests.cs`
- Test: `src/HomeBlaze/HomeBlaze.Game.Tests/View/GameViewTrackerTests.cs`
- Test: `src/HomeBlaze/HomeBlaze.Game.Tests/GameSerializationTests.cs`

- [ ] **Step 1: Let tests add named players**

In `src/HomeBlaze/HomeBlaze.Game.Tests/Testing/GameTestContext.cs`, replace

```csharp
    /// <summary>
    /// Adds a connected anonymous client and moves its player to the position.
    /// </summary>
    public GamePlayer AddConnectedPlayer(string clientId, int x = 0, int y = 0)
    {
        Clients.Set(clientId, isNamed: false, isConnected: true);
```

with

```csharp
    /// <summary>
    /// Adds a connected client, anonymous unless <paramref name="isNamed"/>, and moves its player to the position.
    /// </summary>
    public GamePlayer AddConnectedPlayer(string clientId, int x = 0, int y = 0, bool isNamed = false)
    {
        Clients.Set(clientId, isNamed, isConnected: true);
```

- [ ] **Step 2: Write the failing game voice tests**

Create `src/HomeBlaze/HomeBlaze.Game.Tests/GameVoiceTests.cs`:

```csharp
using HomeBlaze.Game.Tests.Testing;
using HomeBlaze.Game.Voice;

namespace HomeBlaze.Game.Tests;

public class GameVoiceTests
{
    private readonly GameTestContext _context = new();

    private static GameCaller Caller(GamePlayer player) => new(player);

    // One tick of the mixer with the table as it is now.
    private void Mix()
    {
        _context.Game.UpdateVolumeTable();
        _context.Game.VoiceMixer.Mix(_context.Game.VolumeTable);
    }

    private void Disconnect(string clientId, bool isNamed)
    {
        _context.Clients.Set(clientId, isNamed, isConnected: false);
        _context.Game.ReconcilePlayers();
    }

    [Fact]
    public void WhenThePlayerTurnsTheMicrophoneOn_ThenItIsOnAndItsFramesReachTheListener()
    {
        // Arrange
        var speaker = _context.AddConnectedPlayer("tab-1");
        _context.AddConnectedPlayer("tab-2", x: 10);
        var sink = new RecordingVoiceSink();
        using var registration = _context.Game.VoiceMixer.RegisterSink("tab-2", sink);

        // Act
        var isAccepted = _context.Game.SetMicrophone(Caller(speaker), pageId: 1, isOn: true);
        _context.Game.VoiceMixer.Receive("tab-1", 1, VoiceFrames.Create(1000));
        Mix();

        // Assert
        Assert.True(isAccepted);
        Assert.True(speaker.IsMicrophoneOn);
        Assert.Equal(750, VoiceFrames.ReadSample(Assert.Single(sink.Frames), 0));
    }

    [Fact]
    public void WhenASecondPageTurnsTheMicrophoneOn_ThenOnlyItsFramesCount()
    {
        // Arrange
        var speaker = _context.AddConnectedPlayer("tab-1");
        _context.AddConnectedPlayer("tab-2", x: 10);
        var sink = new RecordingVoiceSink();
        using var registration = _context.Game.VoiceMixer.RegisterSink("tab-2", sink);
        _context.Game.SetMicrophone(Caller(speaker), pageId: 1, isOn: true);

        // Act
        _context.Game.SetMicrophone(Caller(speaker), pageId: 2, isOn: true);
        _context.Game.VoiceMixer.Receive("tab-1", 1, VoiceFrames.Create(1000));
        _context.Game.VoiceMixer.Receive("tab-1", 2, VoiceFrames.Create(2000));
        Mix();

        // Assert
        Assert.Equal(1500, VoiceFrames.ReadSample(Assert.Single(sink.Frames), 0));
    }

    [Fact]
    public void WhenAnOlderPageTurnsTheMicrophoneOff_ThenTheLatestPageKeepsIt()
    {
        // Arrange
        var speaker = _context.AddConnectedPlayer("tab-1");
        _context.Game.SetMicrophone(Caller(speaker), pageId: 1, isOn: true);
        _context.Game.SetMicrophone(Caller(speaker), pageId: 2, isOn: true);

        // Act
        _context.Game.SetMicrophone(Caller(speaker), pageId: 1, isOn: false);

        // Assert
        Assert.True(speaker.IsMicrophoneOn);
        Assert.Equal((false, false), _context.Game.GetVoicePages(speaker, 1));
        Assert.Equal((true, false), _context.Game.GetVoicePages(speaker, 2));
    }

    [Fact]
    public void WhenTwoPagesTurnTheSpeakerOn_ThenAudioStaysOnUntilBothTurnItOff()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        _context.Game.SetAudio(Caller(player), pageId: 1, isOn: true);
        _context.Game.SetAudio(Caller(player), pageId: 2, isOn: true);

        // Act
        _context.Game.SetAudio(Caller(player), pageId: 1, isOn: false);
        var isOnWithOnePage = player.IsAudioOn;
        _context.Game.SetAudio(Caller(player), pageId: 2, isOn: false);

        // Assert
        Assert.True(isOnWithOnePage);
        Assert.False(player.IsAudioOn);
    }

    [Fact]
    public void WhenTheCallerPlaysAnotherGame_ThenTheTogglesAreRefused()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        var stranger = new GameTestContext().AddConnectedPlayer("tab-1");

        // Act
        var isMicrophoneAccepted = _context.Game.SetMicrophone(Caller(stranger), pageId: 1, isOn: true);
        var isAudioAccepted = _context.Game.SetAudio(Caller(stranger), pageId: 1, isOn: true);

        // Assert
        Assert.False(isMicrophoneAccepted);
        Assert.False(isAudioAccepted);
        Assert.False(player.IsMicrophoneOn || player.IsAudioOn);
        Assert.False(stranger.IsMicrophoneOn || stranger.IsAudioOn);
    }

    [Fact]
    public void WhenADisconnectedPlayerTurnsTheMicrophoneOn_ThenItIsRefusedButTurningOffWorks()
    {
        // Arrange
        var kitchen = _context.AddConnectedPlayer("kitchen", isNamed: true);
        Disconnect("kitchen", isNamed: true);

        // Act
        var isTurnedOn = _context.Game.SetMicrophone(Caller(kitchen), pageId: 1, isOn: true);
        var isTurnedOff = _context.Game.SetMicrophone(Caller(kitchen), pageId: 1, isOn: false);

        // Assert
        Assert.False(isTurnedOn);
        Assert.True(isTurnedOff);
        Assert.False(kitchen.IsMicrophoneOn);
    }

    [Fact]
    public void WhenTheClientDisconnects_ThenTheTogglesAreResetAndQueuedFramesDropped()
    {
        // Arrange
        var kitchen = _context.AddConnectedPlayer("kitchen", isNamed: true);
        _context.AddConnectedPlayer("tab-2", x: 10);
        var sink = new RecordingVoiceSink();
        using var registration = _context.Game.VoiceMixer.RegisterSink("tab-2", sink);
        _context.Game.SetMicrophone(Caller(kitchen), pageId: 1, isOn: true);
        _context.Game.SetAudio(Caller(kitchen), pageId: 1, isOn: true);
        _context.Game.VoiceMixer.Receive("kitchen", 1, VoiceFrames.Create(1000));

        // Act
        Disconnect("kitchen", isNamed: true);
        Mix();

        // Assert
        Assert.False(kitchen.IsMicrophoneOn);
        Assert.False(kitchen.IsAudioOn);
        Assert.Equal((false, false), _context.Game.GetVoicePages(kitchen, 1));
        Assert.Empty(sink.Frames);
    }

    [Fact]
    public void WhenAnAnonymousPlayerLeaves_ThenItsFramesAreDropped()
    {
        // Arrange
        var speaker = _context.AddConnectedPlayer("tab-1");
        _context.AddConnectedPlayer("tab-2", x: 10);
        var sink = new RecordingVoiceSink();
        using var registration = _context.Game.VoiceMixer.RegisterSink("tab-2", sink);
        _context.Game.SetMicrophone(Caller(speaker), pageId: 1, isOn: true);

        // Act
        Disconnect("tab-1", isNamed: false);
        _context.Game.VoiceMixer.Receive("tab-1", 1, VoiceFrames.Create(1000));
        Mix();

        // Assert
        Assert.False(_context.Game.Players.ContainsKey("tab-1"));
        Assert.False(speaker.IsMicrophoneOn);
        Assert.Empty(sink.Frames);
    }

    [Fact]
    public void WhenAToggleChanges_ThenTheVolumeTableIsRebuilt()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        _context.Game.UpdateVolumeTable();
        _context.TimeProvider.Advance(GameVoiceSettings.VolumeTableInterval);
        var isRebuiltWithoutChange = _context.Game.UpdateVolumeTable();

        // Act
        _context.Game.SetAudio(Caller(player), pageId: 1, isOn: true);
        var isRebuilt = _context.Game.UpdateVolumeTable();

        // Assert
        Assert.False(isRebuiltWithoutChange);
        Assert.True(isRebuilt);
    }
}
```

Add at the end of the class in `src/HomeBlaze/HomeBlaze.Game.Tests/View/GameViewTrackerTests.cs`, before its last `}`:

```csharp
    [Fact]
    public void WhenAPlayerTurnsItsMicrophoneOn_ThenItsViewIsUpdated()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        _context.Game.UpdateView(_tracker, player);

        // Act
        _context.Game.SetMicrophone(new GameCaller(player), pageId: 1, isOn: true);
        var update = _context.Game.UpdateView(_tracker, player);

        // Assert
        var view = Assert.Single(update.Upserted);
        Assert.True(view.IsMicrophoneOn);
        Assert.False(view.IsAudioOn);
    }
```

Add at the end of the class in `src/HomeBlaze/HomeBlaze.Game.Tests/GameSerializationTests.cs`, before its last `}`:

```csharp
    [Fact]
    public void WhenAPlayerHasItsVoiceOn_ThenTheTogglesAreNotSaved()
    {
        // Arrange
        var game = new Game(Microsoft.Extensions.Logging.Abstractions.NullLogger<Game>.Instance)
        {
            Players = new Dictionary<string, GamePlayer> { ["kitchen"] = new GamePlayer { IsMicrophoneOn = true, IsAudioOn = true } }
        };

        // Act
        var json = _serializer.Serialize(game);

        // Assert
        Assert.DoesNotContain("isMicrophoneOn", json);
        Assert.DoesNotContain("isAudioOn", json);
    }
```

- [ ] **Step 3: Run the tests to see them fail**

Run: `dotnet build src/HomeBlaze/HomeBlaze.Game.Tests -c Release`
Expected: build fails because `GamePlayer.IsMicrophoneOn`, `GamePlayer.IsAudioOn`, `Game.SetMicrophone`, `Game.SetAudio`, `Game.GetVoicePages`, `Game.VoiceMixer` and `GameViewObject.IsMicrophoneOn` do not exist.

- [ ] **Step 4: Add the player state**

In `src/HomeBlaze/HomeBlaze.Game/GamePlayer.cs`, replace

```csharp
    [State(IsDiscrete = true, Position = 3)]
    public partial bool IsConnected { get; internal set; }
```

with

```csharp
    [State(IsDiscrete = true, Position = 3)]
    public partial bool IsConnected { get; internal set; }

    /// <summary>
    /// Whether a page of the player has its microphone on; only the player's own pages switch it.
    /// </summary>
    [State(IsDiscrete = true, Position = 4)]
    public partial bool IsMicrophoneOn { get; internal set; }

    /// <summary>
    /// Whether a page of the player has its speaker on; only the player's own pages switch it.
    /// </summary>
    [State(IsDiscrete = true, Position = 5)]
    public partial bool IsAudioOn { get; internal set; }
```

replace

```csharp
    internal long? LastMessageTimestamp { get; set; }
```

with

```csharp
    internal long? LastMessageTimestamp { get; set; }

    // Not intercepted: the page whose microphone counts and the pages with the speaker on, kept by the game under its lock.
    internal int? MicrophonePageId { get; set; }

    internal HashSet<int>? AudioPageIds { get; set; }
```

and replace

```csharp
        IsConnected = false;
    }
```

with

```csharp
        IsConnected = false;
        IsMicrophoneOn = false;
        IsAudioOn = false;
    }
```

- [ ] **Step 5: Add the toggles**

Create `src/HomeBlaze/HomeBlaze.Game/Game.Voice.cs`:

```csharp
using HomeBlaze.Game.Voice;

namespace HomeBlaze.Game;

public partial class Game
{
    private readonly GameVoiceMixer _voiceMixer;

    /// <summary>
    /// Mixes the voices of the players; a page passes it the frames of its microphone and registers its speaker.
    /// </summary>
    internal GameVoiceMixer VoiceMixer => _voiceMixer;

    /// <summary>
    /// Turns the microphone of the caller's player on for the page, which makes it the only page whose frames count, or
    /// off, which only acts when the page is that page. The caller must pass only the player of its own client.
    /// </summary>
    /// <returns>False when the player is not a player of this game, or is disconnected while turning on.</returns>
    internal bool SetMicrophone(GameCaller caller, int pageId, bool isOn)
    {
        lock (_lock)
        {
            var player = caller.Player;
            if (!CanSetVoice(player, isOn))
            {
                return false;
            }

            var page = player.MicrophonePageId;
            if (isOn)
            {
                page = pageId;
            }
            else if (page == pageId)
            {
                page = null;
            }

            if (page != player.MicrophonePageId)
            {
                player.MicrophonePageId = page;
                player.IsMicrophoneOn = page is not null;
                _voiceMixer.SetMicrophonePage(player.ClientId, page);
                _isVolumeTableDirty = true;
            }

            return true;
        }
    }

    /// <summary>
    /// Turns the speaker of the page on or off; the player's audio is on while any of its pages has it on. The caller
    /// must pass only the player of its own client.
    /// </summary>
    /// <returns>False when the player is not a player of this game, or is disconnected while turning on.</returns>
    internal bool SetAudio(GameCaller caller, int pageId, bool isOn)
    {
        lock (_lock)
        {
            var player = caller.Player;
            if (!CanSetVoice(player, isOn))
            {
                return false;
            }

            var pages = player.AudioPageIds ??= [];
            var isChanged = isOn ? pages.Add(pageId) : pages.Remove(pageId);
            if (isChanged && player.IsAudioOn != (pages.Count > 0))
            {
                player.IsAudioOn = pages.Count > 0;
                _isVolumeTableDirty = true;
            }

            return true;
        }
    }

    /// <summary>
    /// Gets whether the page is the player's microphone page and whether it has the speaker on.
    /// </summary>
    internal (bool IsMicrophonePage, bool IsAudioPage) GetVoicePages(GamePlayer player, int pageId)
    {
        lock (_lock)
        {
            return (player.MicrophonePageId == pageId, player.AudioPageIds?.Contains(pageId) == true);
        }
    }

    // Callers hold _lock. Turning off works while disconnected, so a closing page always removes its share.
    private bool CanSetVoice(GamePlayer player, bool isOn) =>
        IsPlayerOfThisGame(player) && (player.IsConnected || !isOn);

    // Callers hold _lock. Turns the voice of a player off that disconnected or left the game.
    private void ResetVoice(GamePlayer player)
    {
        if (player.MicrophonePageId is null && player.AudioPageIds is not { Count: > 0 })
        {
            return;
        }

        player.MicrophonePageId = null;
        player.AudioPageIds?.Clear();
        player.IsMicrophoneOn = false;
        player.IsAudioOn = false;
        _voiceMixer.SetMicrophonePage(player.ClientId, null);
        _isVolumeTableDirty = true;
    }

    // Callers hold _lock.
    private void ResetVoiceOfRemovedPlayers(Dictionary<string, GamePlayer> current, Dictionary<string, GamePlayer> next)
    {
        foreach (var (id, player) in current)
        {
            if (!next.ContainsKey(id))
            {
                ResetVoice(player);
            }
        }
    }
}
```

In `src/HomeBlaze/HomeBlaze.Game/Game.cs`, replace

```csharp
        _timeProvider = timeProvider;
        _logger = logger;
        Name = string.Empty;
```

with

```csharp
        _timeProvider = timeProvider;
        _logger = logger;
        _voiceMixer = new GameVoiceMixer(timeProvider, logger);
        Name = string.Empty;
```

and add `using HomeBlaze.Game.Voice;` after `using HomeBlaze.Game.Clients;` at the top of `Game.cs`.

In `src/HomeBlaze/HomeBlaze.Game/Game.Players.cs`, replace

```csharp
        if (!ReferenceEquals(next, current))
        {
            ReplacePlayers(next);
```

with

```csharp
        if (!ReferenceEquals(next, current))
        {
            ResetVoiceOfRemovedPlayers(current, next);
            ReplacePlayers(next);
```

and replace

```csharp
        player.IsConnected = client.IsConnected;
        if (isJoining)
```

with

```csharp
        player.IsConnected = client.IsConnected;
        if (!client.IsConnected)
        {
            ResetVoice(player);
        }

        if (isJoining)
```

- [ ] **Step 6: Send the toggles to the browser**

In `src/HomeBlaze/HomeBlaze.Game/View/GameViewObject.cs`, replace

```csharp
    bool IsOpen,
    bool IsSelf)
```

with

```csharp
    bool IsOpen,
    bool IsSelf,
    bool IsMicrophoneOn,
    bool IsAudioOn)
```

In `src/HomeBlaze/HomeBlaze.Game/View/GameViewTracker.cs`, replace

```csharp
        known.Height == gameObject.Height && known.IsSelf == isSelf && known.Facing == GetFacing(gameObject);
```

with

```csharp
        known.Height == gameObject.Height && known.IsSelf == isSelf && known.Facing == GetFacing(gameObject) && HasSameVoice(known, gameObject);

    private static bool HasSameVoice(GameViewObject known, GameObject gameObject) =>
        known.IsMicrophoneOn == IsMicrophoneOn(gameObject) && known.IsAudioOn == IsAudioOn(gameObject);
```

replace

```csharp
            GetFacing(gameObject), GetTitle(gameObject), GetColor(gameObject), gameObject.IsBlocking, IsOpen(gameObject), isSelf);
```

with

```csharp
            GetFacing(gameObject), GetTitle(gameObject), GetColor(gameObject), gameObject.IsBlocking, IsOpen(gameObject), isSelf,
            IsMicrophoneOn(gameObject), IsAudioOn(gameObject));
```

and replace

```csharp
    private static bool IsOpen(GameObject gameObject) => gameObject is Door { IsOpen: true };
```

with

```csharp
    private static bool IsOpen(GameObject gameObject) => gameObject is Door { IsOpen: true };

    private static bool IsMicrophoneOn(GameObject gameObject) => gameObject is GamePlayer { IsMicrophoneOn: true };

    private static bool IsAudioOn(GameObject gameObject) => gameObject is GamePlayer { IsAudioOn: true };
```

- [ ] **Step 7: Run the tests to see them pass**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Game.Tests --filter "FullyQualifiedName~GameVoiceTests|FullyQualifiedName~GameViewTrackerTests|FullyQualifiedName~GameSerializationTests"`
Expected: all pass. Then the whole unit suite: `dotnet test src/HomeBlaze/HomeBlaze.Game.Tests --filter "FullyQualifiedName!~E2E"`, expected all pass, including `WhenNothingChanged_ThenTheUpdateAllocatesNothing`.

- [ ] **Step 8: Commit and push**

```bash
git add src/HomeBlaze/HomeBlaze.Game/Game.Voice.cs src/HomeBlaze/HomeBlaze.Game/GamePlayer.cs src/HomeBlaze/HomeBlaze.Game/Game.cs src/HomeBlaze/HomeBlaze.Game/Game.Players.cs src/HomeBlaze/HomeBlaze.Game/View/GameViewObject.cs src/HomeBlaze/HomeBlaze.Game/View/GameViewTracker.cs src/HomeBlaze/HomeBlaze.Game.Tests/Testing/GameTestContext.cs src/HomeBlaze/HomeBlaze.Game.Tests/GameVoiceTests.cs src/HomeBlaze/HomeBlaze.Game.Tests/View/GameViewTrackerTests.cs src/HomeBlaze/HomeBlaze.Game.Tests/GameSerializationTests.cs
git commit -m "feat: let only the own pages switch a player's microphone and speaker" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

---

## Task 5: Voice loop

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze.Game/Game.Voice.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Game/Game.Runtime.cs`
- Test: `src/HomeBlaze/HomeBlaze.Game.Tests/GameRuntimeTests.cs`
- Test: `src/HomeBlaze/HomeBlaze.Game.Tests/GameVoiceTests.cs`

Runnable: yes. The controller restarts the app; nothing is visible yet, so it only checks that the game page still loads and moves.

- [ ] **Step 1: Write the failing loop tests**

Add at the end of the class in `src/HomeBlaze/HomeBlaze.Game.Tests/GameRuntimeTests.cs`, before its last `}`:

```csharp
    [Fact]
    public async Task WhenTheGameRuns_ThenTheVoiceLoopMixesEveryFrameDuration()
    {
        // Arrange
        var speaker = _context.AddConnectedPlayer("tab-1");
        _context.AddConnectedPlayer("tab-2", x: 20);
        var sink = new RecordingVoiceSink();
        using var registration = _context.Game.VoiceMixer.RegisterSink("tab-2", sink);
        _context.Game.SetMicrophone(new GameCaller(speaker), pageId: 1, isOn: true);
        await _context.Game.StartAsync(CancellationToken.None);
        try
        {
            // Act
            await AsyncTestHelpers.WaitUntilAsync(() =>
            {
                _context.Game.VoiceMixer.Receive("tab-1", 1, VoiceFrames.Create(1000));
                _context.TimeProvider.Advance(GameVoiceSettings.FrameDuration);
                return sink.Count > 0;
            }, message: "The voice loop did not mix the frame");

            // Assert
            Assert.Equal(500, VoiceFrames.ReadSample(sink.Frames[0], 0));
        }
        finally
        {
            await _context.Game.StopAsync(CancellationToken.None);
        }
    }
```

and add `using HomeBlaze.Game.Voice;` after `using HomeBlaze.Game.Tests.Testing;` at the top of that file.

Add at the end of the class in `src/HomeBlaze/HomeBlaze.Game.Tests/GameVoiceTests.cs`, before its last `}`:

```csharp
    [Fact]
    public void WhenAVolumeTableChangedHandlerThrows_ThenTheTickSurvivesAndTheNextTickMixes()
    {
        // Arrange
        var speaker = _context.AddConnectedPlayer("tab-1");
        _context.AddConnectedPlayer("tab-2", x: 20);
        var sink = new RecordingVoiceSink();
        using var registration = _context.Game.VoiceMixer.RegisterSink("tab-2", sink);
        _context.Game.SetMicrophone(Caller(speaker), pageId: 1, isOn: true);
        _context.Game.VoiceMixer.Receive("tab-1", 1, VoiceFrames.Create(1000));
        var calls = 0;
        _context.Game.VolumeTableChanged += () =>
        {
            if (++calls == 1)
            {
                throw new InvalidOperationException("The handler failed.");
            }
        };

        // Act
        var exception = Record.Exception(() => _context.Game.TickVoice());
        _context.Game.TickVoice();

        // Assert
        Assert.Null(exception);
        Assert.Equal(500, VoiceFrames.ReadSample(Assert.Single(sink.Frames), 0));
    }
```

- [ ] **Step 2: Run the tests to see them fail**

Run: `dotnet build src/HomeBlaze/HomeBlaze.Game.Tests -c Release`
Expected: build fails because `Game.TickVoice` does not exist. (Without it, the runtime test would time out.)

- [ ] **Step 3: Add the tick and the loop**

In `src/HomeBlaze/HomeBlaze.Game/Game.Voice.cs`, replace

```csharp
using HomeBlaze.Game.Voice;

namespace HomeBlaze.Game;
```

with

```csharp
using HomeBlaze.Game.Voice;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Game;
```

and replace

```csharp
    /// <summary>
    /// Gets whether the page is the player's microphone page and whether it has the speaker on.
    /// </summary>
```

with

```csharp
    /// <summary>
    /// Rebuilds the volume table when it is due and mixes one frame for every listener; logs instead of throwing, so
    /// voice never stops the loop.
    /// </summary>
    internal void TickVoice()
    {
        try
        {
            UpdateVolumeTable();
            _voiceMixer.Mix(_volumeTable);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "A voice tick of the game {Game} failed", Title);
        }
    }

    // Ticks every frame duration until canceled.
    private async Task RunVoiceLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(GameVoiceSettings.FrameDuration, _timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                TickVoice();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The game stops.
        }
    }

    /// <summary>
    /// Gets whether the page is the player's microphone page and whether it has the speaker on.
    /// </summary>
```

In `src/HomeBlaze/HomeBlaze.Game/Game.Runtime.cs`, replace

```csharp
        RequestIndexRebuild();

        // One wait outlives the passes that a delay ends; a new wait per pass would leave the old one queued to take the next signal.
        Task? signalTask = null;
```

with

```csharp
        RequestIndexRebuild();

        // Stopped together with the housekeeping loop, also when that one ends on an unexpected exception.
        using var voiceStopping = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var voiceLoop = RunVoiceLoopAsync(voiceStopping.Token);
        try
        {
            await RunHousekeepingLoopAsync(stoppingToken);
        }
        finally
        {
            await voiceStopping.CancelAsync();
            await voiceLoop;
        }
    }

    private async Task RunHousekeepingLoopAsync(CancellationToken stoppingToken)
    {
        // One wait outlives the passes that a delay ends; a new wait per pass would leave the old one queued to take the next signal.
        Task? signalTask = null;
```

The rest of the former loop body stays as it is and now forms `RunHousekeepingLoopAsync`.

- [ ] **Step 4: Run the tests to see them pass**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Game.Tests --filter "FullyQualifiedName~GameRuntimeTests|FullyQualifiedName~GameVoiceTests"`
Expected: all pass, including `WhenAnObjectIsMovedWhileTheGameHoldsItsLock_ThenTheMoveDoesNotWaitForTheGame`: the voice tick only reads under `_lock` and never writes, so it holds no change observable gate. Then the whole unit suite: `dotnet test src/HomeBlaze/HomeBlaze.Game.Tests --filter "FullyQualifiedName!~E2E"`, expected all pass.

- [ ] **Step 5: Commit and push**

```bash
git add src/HomeBlaze/HomeBlaze.Game/Game.Voice.cs src/HomeBlaze/HomeBlaze.Game/Game.Runtime.cs src/HomeBlaze/HomeBlaze.Game.Tests/GameRuntimeTests.cs src/HomeBlaze/HomeBlaze.Game.Tests/GameVoiceTests.cs
git commit -m "feat: run a voice loop beside the game loop" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

---

## Task 6: Browser and page voice

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Game/wwwroot/game-voice.js`
- Create: `src/HomeBlaze/HomeBlaze.Game/wwwroot/game-voice-worklet.js`
- Modify: `src/HomeBlaze/HomeBlaze.Game/wwwroot/game-input.js`
- Modify: `src/HomeBlaze/HomeBlaze.Game/Components/GamePage.razor`
- Modify: `src/HomeBlaze/HomeBlaze.Game/Components/GamePage.razor.css`
- Test: `src/HomeBlaze/HomeBlaze.Game.Tests/E2E/GamePageTests.cs`

Runnable: yes. After the restart the controller checks, without touching the world: on `https://192.168.1.201:5281/pages/Demo/Game` the Voice buttons are enabled and turning the microphone on asks for permission; on `http://192.168.1.201:5280/pages/Demo/Game` both buttons are disabled with the tooltip "Voice needs HTTPS".

- [ ] **Step 1: Write the failing E2E test**

In `src/HomeBlaze/HomeBlaze.Game.Tests/E2E/GamePageTests.cs`:

Replace

```csharp
using HomeBlaze.E2E.Tests.Infrastructure;
using HomeBlaze.Installation;
```

with

```csharp
using HomeBlaze.E2E.Tests.Infrastructure;
using HomeBlaze.Game.Voice;
using HomeBlaze.Installation;
```

Replace

```csharp
    private const string SelfSuffix = " (you)";
```

with

```csharp
    private const string SelfSuffix = " (you)";
    private const string ReceivedFramesAttribute = "data-voice-received-frames";
    private const string SentFramesAttribute = "data-voice-sent-frames";
```

Replace

```csharp
    private static string FormatPosition(int x, int y, string facing) => $"X {x} · Y {y} · facing {facing}";
```

with

```csharp
    [Fact]
    public async Task WhenTwoPlayersTurnOnMicrophoneAndSpeaker_ThenTheListenerPlaysTheSpeakersFrames()
    {
        // Arrange
        await using var browser = await MediaBrowser.LaunchAsync();
        await using var world = await GameWorld.CreateAsync(_fixture);
        var (speaker, speakerPlayer) = await world.JoinAsync(CreateMicrophoneContext(), browser.Browser);
        var (listener, listenerPlayer) = await world.JoinAsync(CreateMicrophoneContext(), browser.Browser);

        // One meter east of the listener, well within the hearing range.
        speakerPlayer.X = listenerPlayer.X + 4;
        speakerPlayer.Y = listenerPlayer.Y;

        // Act
        await TurnOnVoiceAsync(speaker);
        await TurnOnVoiceAsync(listener);

        // Assert
        await WaitForCounterAsync(listener, ReceivedFramesAttribute, 25);
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
```

Replace

```csharp
        /// </summary>
        public async Task<(IPage Page, GamePlayer Player)> JoinAsync(BrowserNewContextOptions? options = null)
        {
            var context = await _fixture.Browser.NewContextAsync(options);
```

with

```csharp
        /// </summary>
        /// <param name="options">The options of the browser context, or null for the defaults.</param>
        /// <param name="browser">The browser to open the game in, or null for the shared one.</param>
        public async Task<(IPage Page, GamePlayer Player)> JoinAsync(BrowserNewContextOptions? options = null, IBrowser? browser = null)
        {
            var context = await (browser ?? _fixture.Browser).NewContextAsync(options);
```

Replace the end of the file

```csharp
            _fixture.RemoveSubjectFromRoot(_gameName, Game);
            _fixture.RemoveSubjectFromRoot(_installationName, _installation);
        }
    }
}
```

with

```csharp
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
            var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true,
                Args = ["--use-fake-ui-for-media-stream", "--use-fake-device-for-media-stream"],
            });
            return new MediaBrowser(playwright, browser);
        }

        public async ValueTask DisposeAsync()
        {
            await Browser.CloseAsync();
            _playwright.Dispose();
        }
    }
}
```

`ReadCounterAsync` and `SentFramesAttribute` are used by the test of Task 7; tests have S1144 disabled.

- [ ] **Step 2: Run the test to see it fail**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Game.Tests --filter "FullyQualifiedName~GamePageTests.WhenTwoPlayersTurnOnMicrophoneAndSpeaker"`
Expected: FAIL with a Playwright `TimeoutException` waiting for `GetByTestId("game-microphone")`.

- [ ] **Step 3: Add the worklet processors**

Create `src/HomeBlaze/HomeBlaze.Game/wwwroot/game-voice-worklet.js`:

```js
// Runs on the audio rendering thread. Both processors work with frames at the voice sample rate and resample from and
// to the rate of the audio context, which the global sampleRate gives.

class CaptureProcessor extends AudioWorkletProcessor {
    constructor(options) {
        super();
        const { sampleRate: voiceRate, frameSamples } = options.processorOptions;
        this.step = sampleRate / voiceRate;
        // Two one-pole low-pass stages below the voice rate's Nyquist frequency keep higher content from folding back.
        this.alpha = 1 - Math.exp(-2 * Math.PI * 0.45 * voiceRate / sampleRate);
        this.firstStage = 0;
        this.secondStage = 0;
        this.previous = 0;
        // Where the next output sample lies, in input samples from the start of the current block.
        this.position = 0;
        this.frame = new Int16Array(frameSamples);
        this.length = 0;
    }

    process(inputs) {
        const input = inputs[0]?.[0];
        if (input) {
            this.consume(input);
        }

        return true;
    }

    consume(input) {
        for (let index = 0; index < input.length; index++) {
            this.firstStage += this.alpha * (input[index] - this.firstStage);
            this.secondStage += this.alpha * (this.firstStage - this.secondStage);
            while (this.position <= index) {
                this.push(this.previous + (this.secondStage - this.previous) * (this.position - index + 1));
                this.position += this.step;
            }

            this.previous = this.secondStage;
        }

        this.position -= input.length;
    }

    push(value) {
        this.frame[this.length++] = Math.max(-32768, Math.min(32767, Math.round(value * 32768)));
        if (this.length === this.frame.length) {
            // The transfer detaches the buffer, so the next frame needs a new one.
            this.port.postMessage(this.frame, [this.frame.buffer]);
            this.frame = new Int16Array(this.frame.length);
            this.length = 0;
        }
    }
}

class PlaybackProcessor extends AudioWorkletProcessor {
    constructor(options) {
        super();
        const settings = options.processorOptions;
        this.step = settings.sampleRate / sampleRate;
        this.startSamples = settings.bufferStartSamples;
        this.maximumSamples = settings.bufferMaximumSamples;
        this.growthSamples = settings.bufferGrowthSamples;
        this.shrinkDelay = settings.bufferShrinkDelaySeconds;
        this.targetSamples = this.startSamples;
        this.capacity = this.maximumSamples + settings.frameSamples;
        this.ring = new Float32Array(this.capacity);
        this.readIndex = 0;
        this.available = 0;
        this.fraction = 0;
        this.isPlaying = false;
        this.lastDry = currentTime;
        this.port.onmessage = event => this.enqueue(event.data);
    }

    // Keeps the delay near the target: drops the oldest audio beyond one growth step above it, and beyond the maximum.
    enqueue(samples) {
        const limit = Math.min(this.maximumSamples, this.targetSamples + this.growthSamples);
        const excess = this.available + samples.length - limit;
        if (excess > 0) {
            this.skip(excess);
        }

        let writeIndex = (this.readIndex + this.available) % this.capacity;
        for (let index = 0; index < samples.length; index++) {
            this.ring[writeIndex] = samples[index];
            writeIndex = (writeIndex + 1) % this.capacity;
        }

        this.available += samples.length;
        if (!this.isPlaying && this.available >= this.targetSamples) {
            this.isPlaying = true;
        }
    }

    process(inputs, outputs) {
        const output = outputs[0][0];
        for (let index = 0; index < output.length; index++) {
            output[index] = this.isPlaying ? this.next() : 0;
        }

        this.shrinkWhenSteady();
        return true;
    }

    next() {
        // Interpolating needs the sample after the current one.
        if (this.available < 2) {
            this.runDry();
            return 0;
        }

        const current = this.ring[this.readIndex];
        const following = this.ring[(this.readIndex + 1) % this.capacity];
        const value = current + (following - current) * this.fraction;
        this.fraction += this.step;
        const consumed = Math.floor(this.fraction);
        this.fraction -= consumed;
        this.skip(consumed);
        return value;
    }

    runDry() {
        this.isPlaying = false;
        this.targetSamples = Math.min(this.maximumSamples, this.targetSamples + this.growthSamples);
        this.lastDry = currentTime;
    }

    shrinkWhenSteady() {
        if (this.isPlaying && this.targetSamples > this.startSamples && currentTime - this.lastDry >= this.shrinkDelay) {
            this.targetSamples = Math.max(this.startSamples, this.targetSamples - this.growthSamples);
            this.lastDry = currentTime;
        }
    }

    skip(count) {
        const skipped = Math.min(count, this.available);
        this.readIndex = (this.readIndex + skipped) % this.capacity;
        this.available -= skipped;
    }
}

registerProcessor('game-voice-capture', CaptureProcessor);
registerProcessor('game-voice-playback', PlaybackProcessor);
```

- [ ] **Step 4: Add the voice module**

Create `src/HomeBlaze/HomeBlaze.Game/wwwroot/game-voice.js`:

```js
// The pages of a tab share this module, so each one owns its voice by the id it passes.
const voices = new Map();
const WORKLET_URL = new URL('./game-voice-worklet.js', import.meta.url).href;
const STARTED = { isStarted: true, error: null };
const NOT_STARTED = { isStarted: false, error: 'failed' };

/** Encodes the 16-bit samples of a frame as little-endian bytes; the one place in JavaScript to change for another codec. */
export function encodeFrame(samples) {
    const bytes = new Uint8Array(samples.length * 2);
    const view = new DataView(bytes.buffer);
    for (let index = 0; index < samples.length; index++) {
        view.setInt16(index * 2, samples[index], true);
    }

    return bytes;
}

/** Decodes the little-endian bytes of a frame into samples from -1 to 1. */
export function decodeFrame(frame) {
    const view = new DataView(frame.buffer, frame.byteOffset, frame.byteLength);
    const samples = new Float32Array(frame.byteLength >> 1);
    for (let index = 0; index < samples.length; index++) {
        samples[index] = view.getInt16(index * 2, true) / 32768;
    }

    return samples;
}

/** Prepares the voice of the view; returns false where the browser offers no microphone or audio worklet, as on plain HTTP. */
export function initialize(viewId, dotNet, options) {
    dispose(viewId);
    const isSupported = window.isSecureContext
        && typeof navigator.mediaDevices?.getUserMedia === 'function'
        && typeof AudioWorkletNode === 'function';
    if (isSupported) {
        voices.set(viewId, new GameVoice(dotNet, options));
    }

    return isSupported;
}

/** Asks for the microphone and starts sending frames; returns { isStarted, error } with error 'denied', 'not-found' or 'failed'. */
export async function startCapture(viewId) {
    const voice = voices.get(viewId);
    return voice ? await voice.startCapture() : NOT_STARTED;
}

export function stopCapture(viewId) {
    voices.get(viewId)?.stopCapture();
}

/** Starts playing the frames that play() passes; returns { isStarted, error }. */
export async function startPlayback(viewId) {
    const voice = voices.get(viewId);
    return voice ? await voice.startPlayback() : NOT_STARTED;
}

export function stopPlayback(viewId) {
    voices.get(viewId)?.stopPlayback();
}

/** Resumes a suspended audio context; browsers allow it only from a click. */
export function resume(viewId) {
    voices.get(viewId)?.resume();
}

export function play(viewId, frame) {
    voices.get(viewId)?.play(frame);
}

export function dispose(viewId) {
    const voice = voices.get(viewId);
    if (voice) {
        voices.delete(viewId);
        voice.dispose();
    }
}

function toError(error) {
    switch (error?.name) {
        case 'NotAllowedError':
        case 'SecurityError':
            return 'denied';
        case 'NotFoundError':
        case 'OverconstrainedError':
            return 'not-found';
        default:
            return 'failed';
    }
}

function stopTracks(stream) {
    for (const track of stream.getTracks()) {
        track.stop();
    }
}

class GameVoice {
    constructor(dotNet, options) {
        this.dotNet = dotNet;
        this.options = options;
        this.contextLoading = null;
        this.context = null;
        this.capture = null;
        this.playback = null;
        this.sentFrames = 0;
        this.receivedFrames = 0;
        this.isDisposed = false;
    }

    // Created on the first click of either button, because browsers start audio only from a user action.
    async ensureContext() {
        this.contextLoading ??= this.createContext();
        try {
            this.context = await this.contextLoading;
        } catch (error) {
            this.contextLoading = null;
            throw error;
        }

        this.resume();
        return this.context;
    }

    async createContext() {
        const context = new AudioContext();
        try {
            await context.audioWorklet.addModule(WORKLET_URL);
        } catch (error) {
            context.close().catch(() => { });
            throw error;
        }

        if (this.isDisposed) {
            context.close().catch(() => { });
            throw new Error('The voice was disposed while it started.');
        }

        context.onstatechange = () => this.reportState();
        return context;
    }

    resume() {
        const context = this.context;
        if (context && context.state !== 'running' && context.state !== 'closed') {
            context.resume().then(() => this.reportState(), () => this.reportState());
        }
    }

    reportState() {
        const state = this.context?.state;
        if (!this.isDisposed && state && state !== 'closed') {
            this.dotNet.invokeMethodAsync('OnAudioStateChanged', state !== 'running').catch(() => { });
        }
    }

    async startCapture() {
        let stream = null;
        try {
            const context = await this.ensureContext();
            stream = await navigator.mediaDevices.getUserMedia({
                audio: { channelCount: 1, echoCancellation: true, noiseSuppression: true, autoGainControl: true },
            });
            if (this.isDisposed) {
                stopTracks(stream);
                return NOT_STARTED;
            }

            this.stopCapture();
            const source = context.createMediaStreamSource(stream);
            const node = new AudioWorkletNode(context, 'game-voice-capture', {
                numberOfInputs: 1,
                numberOfOutputs: 1,
                processorOptions: { sampleRate: this.options.sampleRate, frameSamples: this.options.frameSamples },
            });
            node.port.onmessage = event => this.send(event.data);
            source.connect(node);
            // The processor outputs only silence; the connection keeps the browser running it.
            node.connect(context.destination);
            this.capture = { stream, source, node };
            return STARTED;
        } catch (error) {
            if (stream) {
                stopTracks(stream);
            }

            return { isStarted: false, error: toError(error) };
        }
    }

    send(samples) {
        this.sentFrames++;
        this.options.element?.setAttribute('data-voice-sent-frames', String(this.sentFrames));
        this.dotNet.invokeMethodAsync('ReceiveMicrophoneFrame', encodeFrame(samples)).catch(() => { });
    }

    stopCapture() {
        const capture = this.capture;
        if (!capture) {
            return;
        }

        this.capture = null;
        capture.node.port.onmessage = null;
        capture.source.disconnect();
        capture.node.disconnect();
        stopTracks(capture.stream);
    }

    async startPlayback() {
        try {
            const context = await this.ensureContext();
            if (this.isDisposed) {
                return NOT_STARTED;
            }

            if (!this.playback) {
                const options = this.options;
                this.playback = new AudioWorkletNode(context, 'game-voice-playback', {
                    numberOfInputs: 0,
                    numberOfOutputs: 1,
                    outputChannelCount: [1],
                    processorOptions: {
                        sampleRate: options.sampleRate,
                        frameSamples: options.frameSamples,
                        bufferStartSamples: options.bufferStartSamples,
                        bufferMaximumSamples: options.bufferMaximumSamples,
                        bufferGrowthSamples: options.bufferGrowthSamples,
                        bufferShrinkDelaySeconds: options.bufferShrinkDelaySeconds,
                    },
                });
                this.playback.connect(context.destination);
            }

            this.reportState();
            return STARTED;
        } catch {
            return NOT_STARTED;
        }
    }

    stopPlayback() {
        this.playback?.disconnect();
        this.playback = null;
    }

    play(frame) {
        if (!this.playback) {
            return;
        }

        const samples = decodeFrame(frame);
        this.playback.port.postMessage(samples, [samples.buffer]);
        this.receivedFrames++;
        this.options.element?.setAttribute('data-voice-received-frames', String(this.receivedFrames));
    }

    dispose() {
        this.isDisposed = true;
        this.stopCapture();
        this.stopPlayback();
        this.context?.close().catch(() => { });
        this.context = null;
    }
}
```

- [ ] **Step 5: Add the keys**

In `src/HomeBlaze/HomeBlaze.Game/wwwroot/game-input.js`, replace

```js
                dotNet.invokeMethodAsync('InvokeTargetAction', number - 1).catch(() => { });
            }
        }
    }
```

with

```js
                dotNet.invokeMethodAsync('InvokeTargetAction', number - 1).catch(() => { });
            }
        }

        if ((event.code === 'KeyM' || event.code === 'KeyN') && !event.repeat) {
            event.preventDefault();
            dotNet.invokeMethodAsync(event.code === 'KeyM' ? 'ToggleMicrophone' : 'ToggleAudio').catch(() => { });
        }
    }
```

The check for Ctrl, Alt, Meta and `isInputBlocked()` at the top of `onKeyDown` already covers these keys, so they are ignored while typing in an input or with a dialog open.

- [ ] **Step 6: Add the voice to the page**

In `src/HomeBlaze/HomeBlaze.Game/Components/GamePage.razor`:

Replace

```razor
@using HomeBlaze.Game.View
@using HomeBlaze.Services
@using HomeBlaze.Services.Clients
```

with

```razor
@using HomeBlaze.Game.View
@using HomeBlaze.Game.Voice
@using HomeBlaze.Services
@using HomeBlaze.Services.Clients
@using Microsoft.Extensions.Logging
```

Replace

```razor
@inject IServiceProvider ServiceProvider
```

with

```razor
@inject IServiceProvider ServiceProvider
@inject ILogger<GamePage> Logger
```

Replace

```razor
    <aside class="game-panel" data-testid="game-panel">
```

with

```razor
    <aside @ref="_panel" class="game-panel" data-testid="game-panel">
```

Replace

```razor
                <div data-testid="game-position">@_positionText</div>
            </section>
```

with

```razor
                <div data-testid="game-position">@_positionText</div>
            </section>
            <section class="game-voice">
                <h5>Voice</h5>
                <div class="game-voice-buttons">
                    <MudTooltip Text="@MicrophoneTooltip">
                        <MudIconButton Icon="@(_isMicrophoneOn ? Icons.Material.Filled.Mic : Icons.Material.Filled.MicOff)"
                                       Variant="@(_isMicrophoneOn ? Variant.Filled : Variant.Text)"
                                       Color="@(_isMicrophoneOn ? Color.Primary : Color.Default)"
                                       Size="Size.Small" Disabled="@IsVoiceDisabled"
                                       aria-label="@MicrophoneTooltip" aria-pressed="@(_isMicrophoneOn ? "true" : "false")"
                                       data-testid="game-microphone" OnClick="ToggleMicrophoneAsync" />
                    </MudTooltip>
                    <MudTooltip Text="@AudioTooltip">
                        <MudIconButton Icon="@(_isAudioOn ? Icons.Material.Filled.VolumeUp : Icons.Material.Filled.VolumeOff)"
                                       Variant="@(_isAudioOn ? Variant.Filled : Variant.Text)"
                                       Color="@(_isAudioOn ? Color.Primary : Color.Default)"
                                       Size="Size.Small" Disabled="@IsVoiceDisabled"
                                       aria-label="@AudioTooltip" aria-pressed="@(_isAudioOn ? "true" : "false")"
                                       data-testid="game-audio" OnClick="ToggleAudioAsync" />
                    </MudTooltip>
                </div>
            </section>
```

Replace

```razor
            </div>
        </div>
        @if (_targets.Count > 0)
```

with

```razor
            </div>
        </div>
        @if (VoiceMessage is { } voiceMessage)
        {
            <MudAlert Severity="Severity.Warning" Dense="true" Class="game-voice-message" data-testid="game-voice-message">@voiceMessage</MudAlert>
        }
        @if (_targets.Count > 0)
```

Replace

```csharp
    private const string ModulePath = "./_content/HomeBlaze.Game/game.js";
```

with

```csharp
    private const string ModulePath = "./_content/HomeBlaze.Game/game.js";
    private const string VoiceModulePath = "./_content/HomeBlaze.Game/game-voice.js";
    private const string InsecureMessage = "Voice needs HTTPS";
    private const string ResumeMessage = "Tap the speaker to resume";
```

Replace

```csharp
    private ElementReference _pad;
```

with

```csharp
    private ElementReference _pad;
    private ElementReference _panel;
```

Replace

```csharp
    private bool _isViewBroken;
```

with

```csharp
    private bool _isViewBroken;
    private IJSObjectReference? _voiceModule;
    // Null until game-voice.js answered, false where the browser offers no voice.
    private bool? _isVoiceSupported;
    private bool _isMicrophoneOn;
    private bool _isAudioOn;
    private bool _isAudioSuspended;
    private bool _isVoiceBusy;
    private string? _voiceError;
    private IDisposable? _sinkRegistration;
    private int _pendingSends;
```

Replace

```csharp
                .Subscribe(_ => ScheduleFlush());
            await FlushAsync();
        }
```

with

```csharp
                .Subscribe(_ => ScheduleFlush());
            await FlushAsync();
            await StartVoiceAsync();
        }
```

Replace

```csharp
        return Task.CompletedTask;
    }

    private async Task InvokeActionAsync(IInterceptorSubject owner, MethodMetadata method)
```

with

```csharp
        return Task.CompletedTask;
    }

    /// <summary>
    /// Passes a frame of this page's microphone to the mixer, which drops it unless this page is the microphone page.
    /// </summary>
    [JSInvokable]
    public void ReceiveMicrophoneFrame(byte[] frame)
    {
        if (Game is { } game && _player is { } player)
        {
            game.VoiceMixer.Receive(player.ClientId, _viewId, frame);
        }
    }

    /// <summary>
    /// Toggles the microphone from the M key.
    /// </summary>
    [JSInvokable]
    public Task ToggleMicrophone() => ToggleMicrophoneAsync();

    /// <summary>
    /// Toggles the speaker from the N key.
    /// </summary>
    [JSInvokable]
    public Task ToggleAudio() => ToggleAudioAsync();

    /// <summary>
    /// Shows whether the browser suspended the audio context, which only a click resumes.
    /// </summary>
    [JSInvokable]
    public void OnAudioStateChanged(bool isSuspended)
    {
        _isAudioSuspended = isSuspended;
        StateHasChanged();
    }

    private bool IsVoiceDisabled => _isVoiceSupported != true || _isVoiceBusy || _player is null;

    private string? VoiceMessage => _isAudioSuspended && (_isAudioOn || _isMicrophoneOn) ? ResumeMessage : _voiceError;

    private string MicrophoneTooltip => (_isVoiceSupported, _isMicrophoneOn) switch
    {
        (false, _) => InsecureMessage,
        (_, true) => "Turn the microphone off (M)",
        _ => "Turn the microphone on (M)"
    };

    private string AudioTooltip => (_isVoiceSupported, _isAudioOn) switch
    {
        (false, _) => InsecureMessage,
        (_, true) => "Turn the speaker off (N)",
        _ => "Turn the speaker on (N)"
    };

    // Voice is optional: a browser that cannot load it keeps the game and leaves the buttons disabled.
    private async Task StartVoiceAsync()
    {
        try
        {
            var module = await JsRuntime.InvokeAsync<IJSObjectReference>("import", _lifetime.Token, VoiceModulePath);
            if (_isDisposed)
            {
                await module.DisposeAsync();
                return;
            }

            _voiceModule = module;
            _isVoiceSupported = await module.InvokeAsync<bool>("initialize", _lifetime.Token, _viewId, _reference, CreateVoiceOptions());
        }
        catch (JSException exception)
        {
            Logger.LogDebug(exception, "The voice of the game could not be loaded");
        }

        if (!_isDisposed)
        {
            StateHasChanged();
        }
    }

    private object CreateVoiceOptions() => new
    {
        element = _panel,
        sampleRate = GameVoiceSettings.SampleRate,
        frameSamples = GameVoiceSettings.SamplesPerFrame,
        bufferStartSamples = ToSamples(GameVoiceSettings.PlaybackBufferStart),
        bufferMaximumSamples = ToSamples(GameVoiceSettings.PlaybackBufferMaximum),
        bufferGrowthSamples = GameVoiceSettings.BufferGrowth * GameVoiceSettings.SamplesPerFrame,
        bufferShrinkDelaySeconds = GameVoiceSettings.BufferShrinkDelay.TotalSeconds,
    };

    private static int ToSamples(TimeSpan duration) => (int)Math.Round(duration.TotalSeconds * GameVoiceSettings.SampleRate);

    private bool CanToggleVoice() => !_isDisposed && !_isVoiceBusy && _isVoiceSupported == true;

    private async Task ToggleMicrophoneAsync()
    {
        if (!CanToggleVoice() || _voiceModule is not { } module || Game is not { } game || _player is not { } player)
        {
            return;
        }

        await RunVoiceToggleAsync(() => _isMicrophoneOn ? StopMicrophoneAsync(module, game, player) : StartMicrophoneAsync(module, game, player));
    }

    private async Task ToggleAudioAsync()
    {
        if (!CanToggleVoice() || _voiceModule is not { } module || Game is not { } game || _player is not { } player)
        {
            return;
        }

        if (_isAudioOn && _isAudioSuspended)
        {
            await TryInvokeVoiceAsync(module, "resume");
            return;
        }

        await RunVoiceToggleAsync(() => _isAudioOn ? StopAudioAsync(module, game, player) : StartAudioAsync(module, game, player));
    }

    private async Task RunVoiceToggleAsync(Func<Task> toggle)
    {
        _isVoiceBusy = true;
        _voiceError = null;
        StateHasChanged();
        try
        {
            await toggle();
        }
        finally
        {
            _isVoiceBusy = false;
        }

        if (!_isDisposed)
        {
            StateHasChanged();
        }
    }

    // The game hears the page only once the browser captures.
    private async Task StartMicrophoneAsync(IJSObjectReference module, Game game, GamePlayer player)
    {
        var result = await TryStartVoiceAsync(module, "startCapture");
        if (!result.IsStarted)
        {
            _voiceError = GetMicrophoneError(result.Error);
            return;
        }

        if (game.SetMicrophone(new GameCaller(player), _viewId, isOn: true))
        {
            _isMicrophoneOn = true;
            return;
        }

        await TryInvokeVoiceAsync(module, "stopCapture");
    }

    private async Task StopMicrophoneAsync(IJSObjectReference module, Game game, GamePlayer player)
    {
        _isMicrophoneOn = false;
        game.SetMicrophone(new GameCaller(player), _viewId, isOn: false);
        await TryInvokeVoiceAsync(module, "stopCapture");
    }

    private async Task StartAudioAsync(IJSObjectReference module, Game game, GamePlayer player)
    {
        var result = await TryStartVoiceAsync(module, "startPlayback");
        if (!result.IsStarted)
        {
            _voiceError = "The speaker could not be started";
            return;
        }

        var registration = game.VoiceMixer.RegisterSink(player.ClientId, new VoiceSink(this));
        if (game.SetAudio(new GameCaller(player), _viewId, isOn: true))
        {
            _sinkRegistration = registration;
            _isAudioOn = true;
            return;
        }

        registration.Dispose();
        await TryInvokeVoiceAsync(module, "stopPlayback");
    }

    private async Task StopAudioAsync(IJSObjectReference module, Game game, GamePlayer player)
    {
        _isAudioOn = false;
        game.SetAudio(new GameCaller(player), _viewId, isOn: false);
        _sinkRegistration?.Dispose();
        _sinkRegistration = null;
        await TryInvokeVoiceAsync(module, "stopPlayback");
    }

    private static string GetMicrophoneError(string? error) => error switch
    {
        "denied" => "Microphone access was denied",
        "not-found" => "No microphone was found",
        _ => "The microphone could not be started"
    };

    // Voice never breaks the page: a call that fails only leaves the voice of this page off.
    private async Task<VoiceStartResult> TryStartVoiceAsync(IJSObjectReference module, string identifier)
    {
        try
        {
            return await module.InvokeAsync<VoiceStartResult>(identifier, _lifetime.Token, _viewId);
        }
        catch (JSException exception)
        {
            Logger.LogDebug(exception, "The voice call {Identifier} failed", identifier);
        }
        catch (Exception exception) when (exception is OperationCanceledException or JSDisconnectedException or ObjectDisposedException)
        {
            // The page went away.
        }

        return new VoiceStartResult(false, null);
    }

    private async Task TryInvokeVoiceAsync(IJSObjectReference module, string identifier)
    {
        try
        {
            await module.InvokeVoidAsync(identifier, _lifetime.Token, _viewId);
        }
        catch (JSException exception)
        {
            Logger.LogDebug(exception, "The voice call {Identifier} failed", identifier);
        }
        catch (Exception exception) when (exception is OperationCanceledException or JSDisconnectedException or ObjectDisposedException)
        {
            // The page went away.
        }
    }

    // Called on the voice loop: hands the frame to the renderer and drops it while too many sends to the browser are pending.
    private void SendVoiceFrame(byte[] frame)
    {
        if (_isDisposed || _voiceModule is not { } module)
        {
            return;
        }

        if (Interlocked.Increment(ref _pendingSends) > GameVoiceSettings.MaximumPendingSends)
        {
            Interlocked.Decrement(ref _pendingSends);
            return;
        }

        _ = PlayAsync(module, frame);
    }

    private async Task PlayAsync(IJSObjectReference module, byte[] frame)
    {
        try
        {
            // JS interop with byte arrays runs on the circuit's dispatcher, not on the voice loop.
            await InvokeAsync(() => module.InvokeVoidAsync("play", _lifetime.Token, _viewId, frame).AsTask());
        }
        catch (Exception exception)
        {
            Logger.LogDebug(exception, "Playing a voice frame failed");
        }
        finally
        {
            Interlocked.Decrement(ref _pendingSends);
        }
    }

    // Removes only this page's share of the player's toggles; the other pages of the player keep theirs.
    private void StopVoiceInGame()
    {
        if (Game is not { } game)
        {
            return;
        }

        _sinkRegistration?.Dispose();
        _sinkRegistration = null;
        if (_player is { } player)
        {
            var caller = new GameCaller(player);
            game.SetMicrophone(caller, _viewId, isOn: false);
            game.SetAudio(caller, _viewId, isOn: false);
        }
    }

    private async Task InvokeActionAsync(IInterceptorSubject owner, MethodMetadata method)
```

Replace

```csharp
        _isDisposed = true;
        _subscription?.Dispose();
        try
        {
            await _lifetime.CancelAsync();
            if (_module is { } module)
```

with

```csharp
        _isDisposed = true;
        _subscription?.Dispose();
        StopVoiceInGame();
        try
        {
            await _lifetime.CancelAsync();
            if (_voiceModule is { } voiceModule)
            {
                await voiceModule.InvokeVoidAsync("dispose", _viewId);
                await voiceModule.DisposeAsync();
            }

            if (_module is { } module)
```

Replace

```csharp
    private sealed record StartResult(bool IsStarted, string? Error);
```

with

```csharp
    private sealed record StartResult(bool IsStarted, string? Error);

    private sealed record VoiceStartResult(bool IsStarted, string? Error);

    // The page's speaker as the mixer sees it.
    private sealed class VoiceSink(GamePage page) : IGameVoiceSink
    {
        public void Send(byte[] frame) => page.SendVoiceFrame(frame);
    }
```

- [ ] **Step 7: Style the voice controls**

Append to the end of `src/HomeBlaze/HomeBlaze.Game/Components/GamePage.razor.css`:

```css

.game-panel .game-voice {
    flex: 0 0 auto;
    margin-bottom: 0;
}

.game-voice-buttons {
    display: flex;
    gap: 4px;
}

::deep .game-voice-message {
    margin: -8px 0 16px;
}
```

- [ ] **Step 8: Run the tests to see them pass**

Run: `dotnet build src/HomeBlaze/HomeBlaze.Game.Tests -c Release`, expected success without warnings.
Run: `dotnet test src/HomeBlaze/HomeBlaze.Game.Tests --filter "FullyQualifiedName~GamePageTests.WhenTwoPlayersTurnOnMicrophoneAndSpeaker"`, expected PASS.
Run: `dotnet test src/HomeBlaze/HomeBlaze.Game.Tests --filter "FullyQualifiedName~E2E"`, expected all game E2E tests pass.
Run: `dotnet test src/HomeBlaze/HomeBlaze.Game.Tests --filter "FullyQualifiedName!~E2E"`, expected all pass.

If the speaker sends no frames in headless Chromium (its `data-voice-sent-frames` stays absent), the headless shell does not render Web Audio on this machine: launch the media browser with `Channel = "chromium"` in `BrowserTypeLaunchOptions` (the new headless mode of full Chromium) and report it.

- [ ] **Step 9: Commit and push**

```bash
git add src/HomeBlaze/HomeBlaze.Game/wwwroot/game-voice.js src/HomeBlaze/HomeBlaze.Game/wwwroot/game-voice-worklet.js src/HomeBlaze/HomeBlaze.Game/wwwroot/game-input.js src/HomeBlaze/HomeBlaze.Game/Components/GamePage.razor src/HomeBlaze/HomeBlaze.Game/Components/GamePage.razor.css src/HomeBlaze/HomeBlaze.Game.Tests/E2E/GamePageTests.cs
git commit -m "feat: talk in the game with microphone and speaker buttons" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

---

## Task 7: Nearby by hearing range

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze.Game/View/GameViewTracker.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Game/Components/GamePage.razor`
- Modify: `src/HomeBlaze/HomeBlaze.Game/Components/GamePage.razor.css`
- Test: `src/HomeBlaze/HomeBlaze.Game.Tests/View/GameViewTrackerTests.cs`
- Test: `src/HomeBlaze/HomeBlaze.Game.Tests/E2E/GamePageTests.cs`

Runnable: yes. After the restart the controller checks, without touching the world, that Nearby lists the own player with a crossed-out microphone and speaker, and that they disappear when the buttons are turned on.

The existing E2E tests stay valid: they look for the own player, or for a second player standing right in front of the first, which the hearing range covers.

- [ ] **Step 1: Write the failing tests**

Add at the end of the class in `src/HomeBlaze/HomeBlaze.Game.Tests/View/GameViewTrackerTests.cs`, before its last `}`:

```csharp
    [Fact]
    public void WhenThePlayerColorIsRequested_ThenItIsTheColorOfItsViewObject()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        var view = Assert.Single(_context.Game.UpdateView(_tracker, player).Upserted);

        // Act
        var color = _tracker.GetPlayerColor(player);

        // Assert
        Assert.Equal(view.Color, color);
        Assert.Equal(GameColors.FromId("tab-1"), color);
    }
```

In `src/HomeBlaze/HomeBlaze.Game.Tests/E2E/GamePageTests.cs`, replace

```csharp
    private static string FormatPosition(int x, int y, string facing) => $"X {x} · Y {y} · facing {facing}";
```

with

```csharp
    [Fact]
    public async Task WhenTheSpeakerMovesOutOfRange_ThenNearbyDropsItAndItsFramesStop()
    {
        // Arrange
        await using var browser = await MediaBrowser.LaunchAsync();
        await using var world = await GameWorld.CreateAsync(_fixture);
        var (speaker, speakerPlayer) = await world.JoinAsync(CreateMicrophoneContext(), browser.Browser);
        var (listener, listenerPlayer) = await world.JoinAsync(CreateMicrophoneContext(), browser.Browser);
        speakerPlayer.X = listenerPlayer.X + 4;
        speakerPlayer.Y = listenerPlayer.Y;
        await TurnOnVoiceAsync(speaker);
        await TurnOnVoiceAsync(listener);
        var speakerEntry = listener.GetByTestId("game-nearby-player").Filter(new() { HasText = speakerPlayer.Title });
        await Expect(speakerEntry).ToHaveCountAsync(1, new() { Timeout = Timeout });
        await Expect(speakerEntry.GetByTestId("game-nearby-microphone-off")).ToHaveCountAsync(0, new() { Timeout = Timeout });
        await WaitForCounterAsync(listener, ReceivedFramesAttribute, 25);

        // Act
        // 25 m east: beyond the hearing range of 10 m, but still within the view of 50 m.
        speakerPlayer.X = listenerPlayer.X + 100;

        // Assert
        await Expect(speakerEntry).ToHaveCountAsync(0, new() { Timeout = Timeout });
        var received = await ReadCounterAsync(listener, ReceivedFramesAttribute);
        var sent = await ReadCounterAsync(speaker, SentFramesAttribute);
        await WaitForCounterAsync(speaker, SentFramesAttribute, sent + 50);
        Assert.InRange(await ReadCounterAsync(listener, ReceivedFramesAttribute), received, received + GameVoiceSettings.MaximumPendingSends);
    }

    private static string FormatPosition(int x, int y, string facing) => $"X {x} · Y {y} · facing {facing}";
```

- [ ] **Step 2: Run the tests to see them fail**

Run: `dotnet build src/HomeBlaze/HomeBlaze.Game.Tests -c Release`
Expected: build fails because `GameViewTracker.GetPlayerColor` does not exist. After Step 3 alone, `dotnet test src/HomeBlaze/HomeBlaze.Game.Tests --filter "FullyQualifiedName~GamePageTests.WhenTheSpeakerMovesOutOfRange"` fails because Nearby still lists every player in view, so the speaker entry stays.

- [ ] **Step 3: Expose the player color**

In `src/HomeBlaze/HomeBlaze.Game/View/GameViewTracker.cs`, replace

```csharp
    private string GetColor(GameObject gameObject)
    {
        if (gameObject is not GamePlayer player)
        {
            return gameObject is Door ? GameColors.Door : GameColors.Wall;
        }

        if (!_playerColors.TryGetValue(player.ClientId, out var color))
```

with

```csharp
    private string GetColor(GameObject gameObject) => gameObject switch
    {
        GamePlayer player => GetPlayerColor(player),
        Door => GameColors.Door,
        _ => GameColors.Wall
    };

    /// <summary>
    /// Gets the color of the player, the one its view object carries.
    /// </summary>
    public string GetPlayerColor(GamePlayer player)
    {
        if (!_playerColors.TryGetValue(player.ClientId, out var color))
```

- [ ] **Step 4: Build Nearby from the volume table**

In `src/HomeBlaze/HomeBlaze.Game/Components/GamePage.razor`:

Replace

```razor
            @foreach (var player in _nearby)
            {
                <div class="game-nearby" data-testid="game-nearby-player">
                    <span class="game-dot" style="background: @player.Color"></span>
                    <span>@(player.IsSelf ? $"{player.Title} (you)" : player.Title)</span>
                </div>
            }
```

with

```razor
            @foreach (var player in _nearby)
            {
                <div class="@(player.IsSelf || _isAudioOn ? "game-nearby" : "game-nearby game-nearby-dimmed")" data-testid="game-nearby-player">
                    <span class="game-dot" style="background: @player.Color"></span>
                    <span class="game-nearby-title">@(player.IsSelf ? $"{player.Title} (you)" : player.Title)</span>
                    @if (!player.IsMicrophoneOn)
                    {
                        <span class="game-nearby-icon" title="Microphone off" data-testid="game-nearby-microphone-off">
                            <MudIcon Icon="@Icons.Material.Filled.MicOff" Size="Size.Small" />
                        </span>
                    }
                    @if (!player.IsAudioOn)
                    {
                        <span class="game-nearby-icon" title="Speaker off" data-testid="game-nearby-audio-off">
                            <MudIcon Icon="@Icons.Material.Filled.VolumeOff" Size="Size.Small" />
                        </span>
                    }
                </div>
            }
```

Replace

```csharp
    private IReadOnlyList<GameViewObject> _nearby = [];
```

with

```csharp
    private IReadOnlyList<NearbyEntry> _nearby = [];
    private int _isVolumeTableChanged;
```

Replace

```csharp
                .Subscribe(_ => ScheduleFlush());
```

with

```csharp
                .Subscribe(_ => ScheduleFlush());
            game.VolumeTableChanged += OnVolumeTableChanged;
```

Replace

```csharp
        var update = game.UpdateView(_tracker, _player);
        if (!update.IsEmpty && !await TryApplyUpdateAsync(module, update))
        {
            return;
        }

        if (!update.IsEmpty)
        {
            RefreshNearby();
        }

        RefreshActions(game);
        StateHasChanged();
    }
```

with

```csharp
        var update = game.UpdateView(_tracker, _player);
        if (!update.IsEmpty && !await TryApplyUpdateAsync(module, update))
        {
            return;
        }

        await RefreshPanelAsync(game, hasViewChanged: !update.IsEmpty);
        StateHasChanged();
    }

    // Nearby follows both the view, which carries the toggles, and the volume table.
    private async Task RefreshPanelAsync(Game game, bool hasViewChanged)
    {
        var isVolumeTableChanged = Interlocked.Exchange(ref _isVolumeTableChanged, 0) == 1;
        if (hasViewChanged)
        {
            RefreshPosition();
        }

        if (hasViewChanged || isVolumeTableChanged)
        {
            RefreshNearby(game);
        }

        if (isVolumeTableChanged)
        {
            await SynchronizeVoiceAsync(game);
        }

        RefreshActions(game);
    }

    // Raised on the voice loop, so it only schedules a flush.
    private void OnVolumeTableChanged()
    {
        Volatile.Write(ref _isVolumeTableChanged, 1);
        ScheduleFlush();
    }

    // Another page of the player took the microphone over, or a disconnect reset the toggles of every page.
    private async Task SynchronizeVoiceAsync(Game game)
    {
        if ((!_isMicrophoneOn && !_isAudioOn) || _voiceModule is not { } module)
        {
            return;
        }

        var (isMicrophonePage, isAudioPage) = _player is { } player ? game.GetVoicePages(player, _viewId) : (false, false);
        if (_isMicrophoneOn && !isMicrophonePage)
        {
            _isMicrophoneOn = false;
            await TryInvokeVoiceAsync(module, "stopCapture");
        }

        if (_isAudioOn && !isAudioPage)
        {
            _isAudioOn = false;
            _sinkRegistration?.Dispose();
            _sinkRegistration = null;
            await TryInvokeVoiceAsync(module, "stopPlayback");
        }
    }
```

Replace the whole former method

```csharp
    private void RefreshNearby()
    {
        GameViewObject? self = null;
        var nearby = new List<GameViewObject>();
        foreach (var view in _tracker.Known)
        {
            if (view.Kind == GameViewObject.PlayerKind)
            {
                nearby.Add(view);
                self = view.IsSelf ? view : self;
            }
        }

        nearby.Sort(static (first, second) => first.IsSelf != second.IsSelf
            ? second.IsSelf.CompareTo(first.IsSelf)
            : StringComparer.CurrentCulture.Compare(first.Title, second.Title));
        _nearby = nearby;
        _positionText = self is null ? NoPosition : $"X {self.X} · Y {self.Y} · facing {((GameDirection)self.Facing).GetShortName()}";
    }
```

with

```csharp
    private void RefreshPosition()
    {
        GameViewObject? self = null;
        foreach (var view in _tracker.Known)
        {
            if (view.IsSelf)
            {
                self = view;
                break;
            }
        }

        _positionText = self is null ? NoPosition : $"X {self.X} · Y {self.Y} · facing {((GameDirection)self.Facing).GetShortName()}";
    }

    // The own player and the players it hears, by the same rule that mixes the voices.
    private void RefreshNearby(Game game)
    {
        if (_player is not { } player)
        {
            _nearby = [];
            return;
        }

        var players = game.Players;
        var nearby = new List<NearbyEntry> { CreateNearbyEntry(player, isSelf: true) };
        foreach (var entry in game.VolumeTable.GetRow(player.ClientId))
        {
            if (players.TryGetValue(entry.PlayerId, out var other))
            {
                nearby.Add(CreateNearbyEntry(other, isSelf: false));
            }
        }

        nearby.Sort(static (first, second) => first.IsSelf != second.IsSelf
            ? second.IsSelf.CompareTo(first.IsSelf)
            : StringComparer.CurrentCulture.Compare(first.Title, second.Title));
        _nearby = nearby;
    }

    private NearbyEntry CreateNearbyEntry(GamePlayer player, bool isSelf) =>
        new(player.Title ?? string.Empty, _tracker.GetPlayerColor(player), isSelf, player.IsMicrophoneOn, player.IsAudioOn);
```

Replace

```csharp
    private void StopVoiceInGame()
    {
        if (Game is not { } game)
        {
            return;
        }
```

with

```csharp
    private void StopVoiceInGame()
    {
        if (Game is not { } game)
        {
            return;
        }

        game.VolumeTableChanged -= OnVolumeTableChanged;
```

Replace

```csharp
    private sealed record VoiceStartResult(bool IsStarted, string? Error);
```

with

```csharp
    private sealed record VoiceStartResult(bool IsStarted, string? Error);

    private sealed record NearbyEntry(string Title, string Color, bool IsSelf, bool IsMicrophoneOn, bool IsAudioOn);
```

- [ ] **Step 5: Style Nearby**

Append to the end of `src/HomeBlaze/HomeBlaze.Game/Components/GamePage.razor.css`:

```css

.game-nearby-title {
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
}

.game-nearby-icon {
    display: inline-flex;
    color: var(--mud-palette-text-secondary);
}

.game-nearby-dimmed {
    opacity: .5;
}
```

- [ ] **Step 6: Run the tests to see them pass**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Game.Tests --filter "FullyQualifiedName!~E2E"`, expected all pass.
Run: `dotnet test src/HomeBlaze/HomeBlaze.Game.Tests --filter "FullyQualifiedName~E2E"`, expected all game E2E tests pass, including `WhenTheSpeakerMovesOutOfRange_ThenNearbyDropsItAndItsFramesStop`, `WhenTheGamePageOpens_ThenThePlayerJoinsFacingNorth` and `WhenTwoBrowsersPlay_ThenTheySeeEachOtherAndOneCanMessageTheOther`.

- [ ] **Step 7: Commit and push**

```bash
git add src/HomeBlaze/HomeBlaze.Game/View/GameViewTracker.cs src/HomeBlaze/HomeBlaze.Game/Components/GamePage.razor src/HomeBlaze/HomeBlaze.Game/Components/GamePage.razor.css src/HomeBlaze/HomeBlaze.Game.Tests/View/GameViewTrackerTests.cs src/HomeBlaze/HomeBlaze.Game.Tests/E2E/GamePageTests.cs
git commit -m "feat: list the players in hearing range as nearby in the game" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

---

## Task 8: Documentation and pull request

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze.Game/README.md`
- Delete: `src/HomeBlaze/docs/superpowers/` (the spec and this plan)
- Modify (outside the repository): `/tmp/claude-1000/-home-rico-GitHub-Namotion-Interceptor--claude-worktrees-bridge-cse-011ShuL5XgwWZqRmSZFhkZve/3d51e483-5513-56d7-b4a2-5748ff2f1414/scratchpad/pr-voice-body.md`

- [ ] **Step 1: Update the overview and the folder table**

In `src/HomeBlaze/HomeBlaze.Game/README.md`:

Replace

```markdown
The page renders the world with Babylon.js as a tilted perspective scene centered on the own player. Several `Game` subjects may exist; each is an independent world with its own players.
```

with

```markdown
The page renders the world with Babylon.js as a tilted perspective scene centered on the own player. Players within 10 m in line of sight talk to each other with their microphones; the server mixes the audio for every listener. Several `Game` subjects may exist; each is an independent world with its own players.
```

Replace

```markdown
| `Game*.cs` | The `Game` subject, split by concern: `Game.cs` (properties, copy-on-write helpers), `Game.Runtime.cs` (loop, change observer), `Game.Index.cs`, `Game.Players.cs`, `Game.Movement.cs`, `Game.Actions.cs`, `Game.Persistence.cs`, `Game.View.cs` |
```

with

```markdown
| `Game*.cs` | The `Game` subject, split by concern: `Game.cs` (properties, copy-on-write helpers), `Game.Runtime.cs` (loops, change observer), `Game.Index.cs`, `Game.Players.cs`, `Game.Movement.cs`, `Game.Actions.cs`, `Game.Persistence.cs`, `Game.View.cs`, `Game.Volume.cs` (volume table), `Game.Voice.cs` (toggles, voice loop) |
```

Replace

```markdown
| `World/` | Pure grid logic: `Footprint`, `GameWorldIndex`, `SpawnFinder`, `StepRateLimiter`, `TargetArea`, `WallPlacement` |
| `View/` | `GameViewTracker` and the objects and updates sent to the browser |
```

with

```markdown
| `World/` | Pure grid logic: `Footprint`, `GameWorldIndex`, `HearingRange`, `RateLimiter`, `SpawnFinder`, `TargetArea`, `WallPlacement` |
| `View/` | `GameViewTracker` and the objects and updates sent to the browser |
| `Voice/` | `GameVoiceSettings`, `GameVoiceCodec`, `GameVolumeTable`, `SpeakerQueue`, `GameVoiceMixer`, `IGameVoiceSink` |
```

Replace

```markdown
| `wwwroot/` | `game.js` (lifecycle, Babylon loader, camera), `game-input.js` (keyboard, pad, prediction), `game-entities.js` (meshes, tweens, ground, target marker), `lib/`, `models/`, `textures/` |
```

with

```markdown
| `wwwroot/` | `game.js` (lifecycle, Babylon loader, camera), `game-input.js` (keyboard, pad, prediction), `game-entities.js` (meshes, tweens, ground, target marker), `game-voice.js` (audio context, capture, playback, codec seam), `game-voice-worklet.js` (capture and playback processors), `lib/`, `models/`, `textures/` |
```

- [ ] **Step 2: Update the model and movement**

Replace

```markdown
| | `ClientId`, `ClientTitle`, `IsConnected` | State | `ClientId` is set from the dictionary key in `OnPlayersChanging` |
```

with

```markdown
| | `ClientId`, `ClientTitle`, `IsConnected` | State | `ClientId` is set from the dictionary key in `OnPlayersChanging` |
| | `IsMicrophoneOn`, `IsAudioOn` | State | Switched only by the player's own pages, not persisted, reset when the client disconnects |
```

Replace

```markdown
- A token bucket per player (`StepRateLimiter`, burst 3, one token per `StepInterval` of 180 ms) limits steps, a second one limits turns. A refused take changes nothing.
```

with

```markdown
- A token bucket per player (`RateLimiter`, burst `StepBurst` of 3, one token per `StepInterval` of 180 ms) limits steps, a second one limits turns. A refused take changes nothing.
```

- [ ] **Step 3: Add the voice section**

Insert this section right before the line `## Threading invariants`:

```markdown
## Voice

Players talk to each other with their microphones. The server decides who hears whom and mixes the audio for every listener in C#; the browser only captures and plays it with its native audio APIs.

Voice needs a secure context (HTTPS or `localhost`), because browsers offer `getUserMedia` and `AudioWorklet` nowhere else. On plain HTTP both buttons are disabled with the tooltip "Voice needs HTTPS". For manual testing on the LAN the app also listens on HTTPS with the ASP.NET development certificate, for example `--urls "http://192.168.1.201:5280;https://192.168.1.201:5281"`.

Hearing range (`World/HearingRange`, the only definition of who hears whom):

- A player hears another when their footprint centers are less than `HearingRadius` (40 tiles, 10 m) apart and the straight line between the centers touches no tile that blocks sound. The volume falls linearly from 1 up close to 0 at 40 tiles.
- `GameWorldIndex.IsSoundBlocked` blocks a tile with a blocking object that is not a player. Walls and closed doors block, open doors and players do not, and wall height does not matter, so a low railing blocks like a full wall.
- The walk takes every tile that the closed segment touches, also only at an edge or a corner. Sound therefore passes neither between two walls that touch at a corner nor along the face of a wall; a line between two players in one row runs on the grid line between two rows and touches both. Tiles inside either player's footprint are skipped.
- It computes in half tiles with `long` arithmetic, which is exact, so both orders give the same volume and footprints near the `int` limits are safe. The walk starts at the player with the smaller id.

Audio and transport:

- 16 kHz, mono, signed 16-bit little-endian PCM in frames of 20 ms (320 samples, 640 bytes), about 256 kbit/s each way per active player.
- Frames travel over the Blazor connection: the browser calls `ReceiveMicrophoneFrame(byte[])` of the page, the page calls `play(viewId, frame)`. Blazor transfers both as binary, and the circuit identifies the player, so no extra endpoint exists.
- `GameVoiceCodec` in C# and `encodeFrame`/`decodeFrame` in `game-voice.js` are the only places that know the encoding, so Opus can replace it without touching the mixer, the range rule or the UI.

Toggles: only the player's own pages switch `IsMicrophoneOn` and `IsAudioOn`, through the internal `Game.SetMicrophone(caller, pageId, isOn)` and `Game.SetAudio(caller, pageId, isOn)`. Like `TryMove` they are not operations, so MCP, agents and other clients can read but not change them. Both take `_lock`, accept only a player of this game, and refuse turning on while it is disconnected. The game keeps per player the microphone page, the page that turned the microphone on last and the only one whose frames count, and the set of pages with the speaker on; turning off removes only the calling page's share. A disconnect or removal of the player resets both and drops its queued frames. The page id is the page's view id.

Volume table (`Voice/GameVolumeTable`, `Game.Volume.cs`):

- An immutable snapshot with a row for every connected player, regardless of its toggles, listing the players it hears and their volume above 0. Nearby uses it too.
- `GameVolumeTable.Build` compares only players within the hearing radius, which the chunk index finds, and computes each pair once.
- The voice loop rebuilds it under `_lock` when the index `Version` changed (moves, joins, leaves, walls, doors, also from outside through the lazy index rebuild) or a toggle set `_isVolumeTableDirty`, and at least `VolumeTableInterval` passed since the last rebuild. It publishes the table through a `volatile` field and then raises `VolumeTableChanged` outside the lock.

Mixer (`Voice/GameVoiceMixer`, one per game, with its own lock):

- `Receive(playerId, pageId, frame)` drops a frame that does not have exactly 640 bytes, does not come from the player's microphone page, or exceeds `FrameRateTolerance` times the frame rate (a `RateLimiter` with a burst of `ServerQueueMaximum`). Otherwise it decodes the samples into the speaker's `SpeakerQueue`, a pooled ring buffer, so steady speech allocates nothing per incoming frame.
- A speaker queue plays once it holds its start size, grows the start size by `BufferGrowth` up to `ServerQueueMaximum` when it runs dry while playing, and shrinks it by one frame after `BufferShrinkDelay` without running dry. It keeps at most its start size plus one growth step, never more than `ServerQueueMaximum`, and drops the oldest frames beyond that, so a shrinking start size lowers the delay.
- `RegisterSink(playerId, sink)` adds a page with the speaker on and returns an `IDisposable` that removes it; a player can have several.
- The voice loop of the background service ticks every `FrameDuration` on a `PeriodicTimer`. `Mix` takes one frame per playing speaker under the mixer lock, then, outside it, sums for every listener with sinks the frames of the speakers in its table row, scaled by their volume and never its own, into a reused `int` buffer, clamps to 16 bits and hands a new `byte[]` to each of the listener's sinks. Silence is not sent. A failing sink or tick is logged and the next tick runs.

Settings (`Voice/GameVoiceSettings`, the one place for every timing):

| Constant | Value | Meaning |
|---|---|---|
| `SampleRate` | 16000 | Samples per second of every frame |
| `FrameDuration` | 20 ms | Length of one frame; 320 samples |
| `ServerQueueStart` | 1 frame | Frames a speaker queue needs before it plays |
| `ServerQueueMaximum` | 6 frames | Frames beyond which the oldest are dropped |
| `PlaybackBufferStart` | 40 ms | Audio the browser buffers before it plays |
| `PlaybackBufferMaximum` | 120 ms | Largest buffer the browser grows to |
| `BufferGrowth` | 1 frame | Growth of a buffer after it ran dry |
| `BufferShrinkDelay` | 5 s | Time without running dry before a buffer shrinks by one frame |
| `VolumeTableInterval` | 100 ms | Shortest time between two rebuilds of the volume table |
| `MaximumPendingSends` | 10 | Sends to a page in flight beyond which new frames to it are dropped |
| `FrameRateTolerance` | 1.5 | Allowed incoming frame rate as a multiple of the nominal rate |

The page passes the browser's values to `game-voice.js` when it starts, so JavaScript holds no copy. Expected delay from mouth to ear: about 100 to 130 ms on the LAN and 150 to 220 ms over the internet, plus whatever Bluetooth headsets add.

Page: the **Voice** buttons sit next to Position, microphone (`Mic`/`MicOff`) and speaker (`VolumeUp`/`VolumeOff`), filled with the primary color while on, and toggled by M and N too. Turning the microphone on starts capturing first and calls `SetMicrophone` only once that succeeded. Turning the speaker on starts playback, registers the page's sink and calls `SetAudio`. The sink counts sends in flight, drops frames while `MaximumPendingSends` are pending, and hands each frame to the circuit's dispatcher with `InvokeAsync`, because JS interop with byte arrays is not documented as thread-safe; a failing send is logged at debug level. Disposing the page stops capturing, removes its sink and turns off only its own share. On `VolumeTableChanged` the page refreshes Nearby and, through `Game.GetVoicePages`, stops its capture or playback when another page took the microphone over or a disconnect reset the toggles. Errors appear under the buttons: "Microphone access was denied", "No microphone was found", "The microphone could not be started", "The speaker could not be started", and "Tap the speaker to resume" while the browser suspended the audio context; tapping the speaker then resumes it.

Browser (`game-voice.js`, per view id like `game.js`):

| Call | Direction | Purpose |
|---|---|---|
| `initialize(viewId, dotNet, options)` | .NET to JS | Returns whether the browser offers voice. Options: `element` (the panel), `sampleRate`, `frameSamples`, `bufferStartSamples`, `bufferMaximumSamples`, `bufferGrowthSamples`, `bufferShrinkDelaySeconds` |
| `startCapture(viewId)`, `stopCapture(viewId)` | .NET to JS | Asks for the microphone with echo cancellation, noise suppression and automatic gain, and starts sending frames; returns `{ isStarted, error }` with `denied`, `not-found` or `failed` |
| `startPlayback(viewId)`, `stopPlayback(viewId)` | .NET to JS | Starts or stops the playback processor |
| `resume(viewId)` | .NET to JS | Resumes a suspended audio context; browsers allow it only from a click |
| `play(viewId, frame)` | .NET to JS | Queues a mixed frame |
| `dispose(viewId)` | .NET to JS | Stops everything and closes the audio context |
| `ReceiveMicrophoneFrame(frame)` | JS to .NET | One captured frame |
| `ToggleMicrophone()`, `ToggleAudio()` | JS to .NET | Keys M and N |
| `OnAudioStateChanged(isSuspended)` | JS to .NET | The audio context was suspended or resumed |

- The `AudioContext` is created on the first click of either button, because browsers start audio only from a user action, and every later click resumes it.
- The capture processor low-passes and resamples the microphone to 16 kHz, converts it to 16 bits and posts a frame every 320 samples.
- The playback processor resamples frames to the context rate and plays from a buffer that starts at `PlaybackBufferStart`, grows by one frame up to `PlaybackBufferMaximum` when it runs dry, shrinks after `BufferShrinkDelay` without running dry, keeps at most one growth step above its target, and plays silence while empty.
- For tests the panel carries `data-voice-sent-frames` and `data-voice-received-frames`, the frames sent and played.

```

- [ ] **Step 4: Update threading, protocol, input, panel, testing and follow-ups**

Replace

```markdown
`_lock` guards `Players`, `Objects`, the positions and door states of their objects, the index, `_pendingPlayers`, `_pendingObjects`, `_isSaveRequested` and the per-player rate limiters and message timestamps.
```

with

```markdown
`_lock` guards `Players`, `Objects`, the positions and door states of their objects, the index, `_pendingPlayers`, `_pendingObjects`, `_isSaveRequested`, the per-player rate limiters and message timestamps, the per-player microphone page and speaker pages, and the volume table's dirty flag, index version and rebuild time.
```

Replace

```markdown
Everything else the page needs (`TryMove`, `GetTargetObjects`, `UpdateView`) takes the lock itself.
```

with

```markdown
Everything else the page needs (`TryMove`, `GetTargetObjects`, `UpdateView`, `SetMicrophone`, `SetAudio`, `GetVoicePages`) takes the lock itself.

Voice adds three rules. The volume table is built only by the voice loop under `_lock` and published through a `volatile` field, so the mixer and pages read it without the lock. The mixer's own lock guards its queues, microphone pages and sinks, and the lock order is `_lock` before the mixer lock: the game tells the mixer about microphone pages while holding `_lock`, and the mixer never calls into the game. Sinks run on the voice loop outside both locks, and `VolumeTableChanged` handlers run there outside `_lock`; neither may block, so the page only hands frames to its dispatcher and schedules a flush. Change observers keep not taking `_lock`.
```

Replace

```markdown
| `InvokeTargetAction(index)` | JS to .NET | Invokes the numbered action of the number key |
```

with

```markdown
| `InvokeTargetAction(index)` | JS to .NET | Invokes the numbered action of the number key |

Voice has its own module, `game-voice.js`; see Voice.
```

Replace

```markdown
A `JSException` from `applyUpdate` marks the view broken and stops the subscription.
```

with

```markdown
A `JSException` from `applyUpdate` marks the view broken and stops the subscription. `VolumeTableChanged` schedules a flush too, which rebuilds Nearby from the new table.
```

Replace

```markdown
- Arrow keys and WASD combine into 8 directions (W plus D is north east) and repeat once per step interval while held. Shift with a direction sends one turn-only step. Number keys 1 to 9 invoke the numbered actions.
```

with

```markdown
- Arrow keys and WASD combine into 8 directions (W plus D is north east) and repeat once per step interval while held. Shift with a direction sends one turn-only step. Number keys 1 to 9 invoke the numbered actions. M toggles the microphone and N the speaker.
```

Replace

```markdown
Side panel (240 px, below the canvas under the `Md` breakpoint): **Position** with the pad, **Looking at** (only while targets exist, actions numbered 1 to 9 across all targets), **Actions** (operations of the `Game`, disabled without a player) and **Nearby** (players in view, the own one first).
```

with

```markdown
Side panel (240 px, below the canvas under the `Md` breakpoint): **Position** with the pad, **Voice** (microphone and speaker buttons, errors below them), **Looking at** (only while targets exist, actions numbered 1 to 9 across all targets), **Actions** (operations of the `Game`, disabled without a player) and **Nearby** (the own player first, then the players in its row of the volume table, each with a crossed-out microphone or speaker while that one is off, dimmed while the own speaker is off).
```

Replace

```markdown
| `FrozenTimeProvider` | A stopped clock that runs a callback inside its next reading, which lets a test act while the game holds its lock |
```

with

```markdown
| `FrozenTimeProvider` | A stopped clock that runs a callback inside its next reading, which lets a test act while the game holds its lock |
| `VoiceFrames` | Builds and reads frames of 16-bit little-endian samples |
| `RecordingVoiceSink` | A thread-safe sink that records the frames the mixer sends |

The voice E2E tests launch their own Chromium with `--use-fake-ui-for-media-stream --use-fake-device-for-media-stream`, so the microphone is a test tone that needs no permission dialog; `127.0.0.1` is a secure context. They read the counters on the panel and prove that frames stop by waiting for the speaker to send 50 more while the listener's count stays within `MaximumPendingSends`.
```

Replace

```markdown
- Only one character model, and only walls and doors as object types.
```

with

```markdown
- Only one character model, and only walls and doors as object types.
- `MaximumPendingSends` of 10 drops frames to a page once its round trip exceeds about 200 ms, because each send waits for the browser to acknowledge it.
- Echo cancellation is up to the browser; without headphones some browsers let a listener's speaker reach its own microphone.

Voice follow-ups:

- Opus compression (Concentus on the server, WebCodecs in the browser) to cut the bandwidth about tenfold.
- Sound traveling around corners and through openings by path length instead of line of sight.
- Walls that muffle instead of block.
- Stereo panning by the speaker's position relative to the listener.
- A speaking indicator in the 3D scene, which needs audio levels from the server.
```

- [ ] **Step 5: Delete the working documents**

```bash
git rm -r src/HomeBlaze/docs/superpowers
```

- [ ] **Step 6: Verify**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Game.Tests --filter "FullyQualifiedName!~E2E"` and note the count of passed tests.
Run: `dotnet test src/HomeBlaze/HomeBlaze.Game.Tests --filter "FullyQualifiedName~E2E"` and note the count of passed tests.
Run: `grep -n "StepRateLimiter\|superpowers" src/HomeBlaze/HomeBlaze.Game/README.md`, expected no output.

- [ ] **Step 7: Commit and push**

```bash
git add src/HomeBlaze/HomeBlaze.Game/README.md
git commit -m "docs: document voice in the game" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

The `git rm` of Step 5 is already staged and goes into this commit.

- [ ] **Step 8: Update the pull request**

Run: `pwsh scripts/diff-composition.ps1 -Range "origin/feat/homeblaze-game...HEAD"` and keep its output.

Write `/tmp/claude-1000/-home-rico-GitHub-Namotion-Interceptor--claude-worktrees-bridge-cse-011ShuL5XgwWZqRmSZFhkZve/3d51e483-5513-56d7-b4a2-5748ff2f1414/scratchpad/pr-voice-body.md` with this content, pasting the script output under "Diff composition" and the two test counts into the Verification lines. It drops the former "Working spec" bullet, because the spec now lives in the README, and the former "Generated with" footer, which `AGENTS.md` forbids:

```markdown
Adds voice to `HomeBlaze.Game`. Players switch their microphone and their speaker on and off in the panel or with M and N. A player hears the players within 10 m whose straight line to them touches no wall and no closed door, with the volume fading linearly to silence at 10 m. The server mixes the audio individually for every listener in C#. The browser only captures and plays it with `getUserMedia` and `AudioWorklet`, sending 16 kHz PCM frames over the existing Blazor connection. The Nearby list shows exactly the players the same rule makes audible.

Based on #876; this pull request only shows the voice changes.

- Voice needs a secure context (HTTPS or `localhost`), because browsers hide the microphone and audio worklet APIs on plain HTTP; there both buttons are disabled.
- `World/StepRateLimiter` became the general `World/RateLimiter`, which also limits incoming frames.
- Steady speech allocates nothing per incoming frame, because frames are decoded into pooled ring buffers. Every sent frame allocates its 640-byte array and the closure that hands it to the circuit's dispatcher, accepted for thread-safe JS interop. Each active player costs about 256 kbit/s each way.

## Contract

- A player hears another while their footprint centers are less than 40 tiles apart and the straight line between them touches no wall or closed door, also only at an edge or a corner; the volume falls linearly to 0 at 40 tiles and is the same in both directions.
- Only the player's own pages switch `IsMicrophoneOn` and `IsAudioOn`; MCP, agents and other clients can read but not change them. Both are not saved and reset when the client disconnects.
- Only the page that turned the microphone on last sends frames that count; a page's speaker share lasts until that page turns it off or closes.
- Nearby lists exactly the players of the own row of the volume table, which follows a change within `VolumeTableInterval` (100 ms) plus the page's flush delay.
- Voice drops rather than queues: frames beyond a queue's limits, beyond 1.5 times the frame rate, or beyond 3 sends pending to a page are discarded, and silence is not sent.
- Voice never stops the game: a failing tick or sink is logged and the next tick runs.

## Breaking changes

None.

## Diff composition

## Verification

- [x] Documentation updated
- [x] Unit tests: HomeBlaze.Game.Tests without E2E, all passed
- [x] Integration tests: HomeBlaze.Game.Tests E2E, all passed
- [ ] ~~Benchmarks~~ no hot path of the libraries changes
- [ ] ~~Load tests~~ not a connector
- [ ] ~~Chaos tests~~ not a connector
```

Then run: `gh pr edit 878 --body-file /tmp/claude-1000/-home-rico-GitHub-Namotion-Interceptor--claude-worktrees-bridge-cse-011ShuL5XgwWZqRmSZFhkZve/3d51e483-5513-56d7-b4a2-5748ff2f1414/scratchpad/pr-voice-body.md`
Expected: the URL of PR #878. Check with `gh pr view 878 --json body -q .body | head -5` that the new summary is there.
