# Game Style, People and Emotes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the HomeBlaze game one consistent look (style E), let players choose a low poly person and shirt color, add emotes through a generic player animation API, and render only as much as the device and situation need.

**Architecture:** Server side, `GamePlayer` gains `Character`, `Color` (configuration) and `Animation` (state); the simulation's `PlayerEntity` holds the resolved appearance and the animation with a sequence number, and the view payload carries them. `IGameWorld.PlayAnimation` is the extension point, `EmoteTool` its first user. Browser side, a new `game-render.js` owns lights, shadows, the post-processing chain (outlines, SSAO2, MSAA, sharpen), quality presets and the render pause; `game-entities.js` renders the new people and plays their animations.

**Tech Stack:** C# 14 / .NET 10, Namotion.Interceptor subjects, Blazor + MudBlazor, Babylon.js 9.31 (vendored), xUnit, Playwright (E2E), three.js 0.160 in a headless browser for the one-off asset conversion.

**Spec:** `src/HomeBlaze/docs/superpowers/specs/2026-11-02-game-style-design.md`

**Branch:** `feat/homeblaze-game-style` (on top of `feat/homeblaze-game`, PR #876). Push after every task.

**Standing rules for every task:**
- Never stage anything under `src/HomeBlaze/HomeBlaze/Data` except `Data/Files/Demo/Game.json` when a task regenerates it, and that file only via `git hash-object -w` plus `git update-index --cacheinfo` (the running app rewrites the working copy).
- Build and test in Debug only (`-c Debug`); a Release app is running from this worktree.
- Commit messages end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- Test naming `When<Condition>_Then<Expected>`, `// Arrange` / `// Act` / `// Assert`, no `Task.Delay`.
- No em dashes in docs; US spelling; no HomeBlaze mentions outside `src/HomeBlaze`.

---

## File structure

| File | Responsibility | Task |
|---|---|---|
| `HomeBlaze.Game/People/GamePeople.cs` (new) | The six person names, the default per client id, resolving a stored value | 1 |
| `HomeBlaze.Game/View/GameColors.cs` | `Hash`, `FromId`, `Resolve` | 1 |
| `HomeBlaze.Game/GamePlayer.cs` | `Character`, `Color`, `Animation` properties | 1, 2 |
| `HomeBlaze.Game/Simulation/PlayerEntity.cs` | Resolved appearance and animation fields | 1, 2 |
| `HomeBlaze.Game/Simulation/GameWorldState.cs` | Appearance refresh, animation start, clear on step, timeout and disconnect | 1, 2 |
| `HomeBlaze.Game/Game.Facade.cs` | Outside writes of `Character` and `Color` | 1 |
| `HomeBlaze.Game/Simulation/GameWriteInterceptor.cs` | Refuses outside writes of `Animation` | 2 |
| `HomeBlaze.Game/Simulation/GamePublisher.cs` | Publishes `Animation` | 2 |
| `HomeBlaze.Game/View/GameViewPayload.cs`, `GameViewPayloads.cs` | `Character`, resolved `Color`, `Animation`, `AnimationMode`, `AnimationSequence` | 1, 2 |
| `HomeBlaze.Game.Abstractions/IGameWorld.cs` | `PlayAnimation` | 2 |
| `HomeBlaze.Game/Game.World.cs` | `PlayAnimation` implementation | 2 |
| `HomeBlaze.Game/Tools/EmoteTool.cs` (new) | Clap, Sit, Jump | 3 |
| `HomeBlaze.Game/Game.Players.cs` | `EditAsync` (name, person, color) | 4 |
| `HomeBlaze.Game/Components/GamePlayerDialog.razor` (replaces `GameNameDialog.razor`) | Edit me dialog | 4 |
| `HomeBlaze.Game/tools/convert-people.js`, `tools/make-textures.js`, `tools/README.md` (new) | Reproducible asset generation | 5 |
| `HomeBlaze.Game/wwwroot/models/people/*` (new) | Six people, previews, LICENSE | 5 |
| `HomeBlaze.Game/wwwroot/textures/grass.png`, `HomeBlaze.Game.Buildings/wwwroot/textures/*.png` | Drawn textures | 5 |
| `HomeBlaze.Game/wwwroot/game-entities.js` | People rendering and animations, shadow and outline registration | 6, 7 |
| `HomeBlaze.Game/wwwroot/game-render.js` (new) | Lights, shadows, post-processing, quality, render pause | 7, 8 |
| `HomeBlaze.Game/wwwroot/game.js` | Uses `game-render.js` | 7, 8 |
| `HomeBlaze.Game/Components/GamePage.Panel.cs`, `GamePage.razor` | Quality dropdown, edit dialog entry | 4, 8 |
| `HomeBlaze.Game.Tests/...` | Unit and E2E tests | every task |
| READMEs, demo world, PR body | Documentation | 9 |

All paths below are relative to `src/HomeBlaze/` unless they start with `src/`.

---

### Task 1: Player appearance on the server

**Files:**
- Create: `HomeBlaze.Game/People/GamePeople.cs`
- Modify: `HomeBlaze.Game/View/GameColors.cs`, `HomeBlaze.Game/GamePlayer.cs`, `HomeBlaze.Game/Simulation/PlayerEntity.cs`, `HomeBlaze.Game/Simulation/GameWorldState.cs:612-640` (both `new PlayerEntity(` sites), `HomeBlaze.Game/Game.Facade.cs:124-138`, `HomeBlaze.Game/View/GameViewPayload.cs`, `HomeBlaze.Game/View/GameViewPayloads.cs:55-95`
- Test: `HomeBlaze.Game.Tests/People/GamePeopleTests.cs` (new), `HomeBlaze.Game.Tests/View/GameColorsTests.cs` (new or extend), `HomeBlaze.Game.Tests/GameAppearanceTests.cs` (new)

- [ ] **Step 1: Write failing tests for `GamePeople` and `GameColors`**

```csharp
// HomeBlaze.Game.Tests/People/GamePeopleTests.cs
using HomeBlaze.Game.People;

namespace HomeBlaze.Game.Tests.People;

public class GamePeopleTests
{
    [Fact]
    public void WhenTheDefaultIsAskedTwiceForOneClient_ThenItIsTheSamePerson()
    {
        // Act
        var first = GamePeople.GetDefault("tab-1");
        var second = GamePeople.GetDefault("tab-1");

        // Assert
        Assert.Equal(first, second);
        Assert.Contains(first, GamePeople.Names);
    }

    [Fact]
    public void WhenManyClientsJoin_ThenEveryPersonIsUsed()
    {
        // Act
        var used = Enumerable.Range(0, 200).Select(index => GamePeople.GetDefault($"tab-{index}")).ToHashSet();

        // Assert
        Assert.Equal(GamePeople.Names.Length, used.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("robot")]
    [InlineData("../male-casual")]
    public void WhenTheStoredPersonIsNotOneOfThePeople_ThenTheDefaultIsUsed(string? stored)
    {
        // Act
        var resolved = GamePeople.Resolve(stored, "tab-1");

        // Assert
        Assert.Equal(GamePeople.GetDefault("tab-1"), resolved);
    }

    [Fact]
    public void WhenTheStoredPersonIsOneOfThePeople_ThenItIsUsed()
    {
        // Act
        var resolved = GamePeople.Resolve("female-tank-top", "tab-1");

        // Assert
        Assert.Equal("female-tank-top", resolved);
    }
}
```

```csharp
// HomeBlaze.Game.Tests/View/GameColorsTests.cs
using HomeBlaze.Game.View;

namespace HomeBlaze.Game.Tests.View;

public class GameColorsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("red")]
    [InlineData("#12345")]
    [InlineData("#1234567")]
    [InlineData("#12345g")]
    public void WhenTheStoredColorIsNotAHexColor_ThenTheColorOfTheIdIsUsed(string? stored)
    {
        // Act
        var resolved = GameColors.Resolve(stored, "tab-1");

        // Assert
        Assert.Equal(GameColors.FromId("tab-1"), resolved);
    }

    [Fact]
    public void WhenTheStoredColorIsAHexColor_ThenItIsUsedInLowerCase()
    {
        // Act
        var resolved = GameColors.Resolve("#A1B2C3", "tab-1");

        // Assert
        Assert.Equal("#a1b2c3", resolved);
    }
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Game.Tests -c Debug --filter "FullyQualifiedName~GamePeopleTests|FullyQualifiedName~GameColorsTests"`
Expected: build error, `GamePeople` and `GameColors.Resolve` do not exist.

- [ ] **Step 3: Implement `GamePeople` and extend `GameColors`**

In `GameColors.cs`, extract the FNV-1a loop of `FromId` into `internal static uint Hash(string value)` (same algorithm, same results, so existing colors stay), keep `FromId` using it, and add:

```csharp
    /// <summary>
    /// Gets the stored color in lower case when it is a <c>#rrggbb</c> color, else the color of the id.
    /// </summary>
    public static string Resolve(string? stored, string id) =>
        stored is { Length: 7 } && stored[0] == '#' && stored.AsSpan(1).ContainsAnyExcept("0123456789abcdefABCDEF") is false
            ? stored.ToLowerInvariant()
            : FromId(id);
```

```csharp
// HomeBlaze.Game/People/GamePeople.cs
using System.Collections.Immutable;
using HomeBlaze.Game.View;

namespace HomeBlaze.Game.People;

/// <summary>
/// The people a player can look like; each name is a model under <c>wwwroot/models/people/</c>.
/// </summary>
internal static class GamePeople
{
    public static readonly ImmutableArray<string> Names =
        ["male-casual", "male-shirt", "male-long-sleeve", "female-casual", "female-alternative", "female-tank-top"];

    /// <summary>
    /// Gets the person a client looks like until its player chooses one; stays the same across sessions.
    /// </summary>
    public static string GetDefault(string clientId) =>
        // A suffix decorrelates the person from the default color, which hashes the bare id.
        Names[(int)(GameColors.Hash(clientId + ":person") % (uint)Names.Length)];

    /// <summary>
    /// Gets the stored person when it is one of <see cref="Names"/>, else the default of the client.
    /// </summary>
    public static string Resolve(string? stored, string clientId) =>
        stored is not null && Names.Contains(stored) ? stored : GetDefault(clientId);
}
```

- [ ] **Step 4: Run the tests to see them pass**

Run: same as Step 2. Expected: PASS.

- [ ] **Step 5: Write failing tests for the player properties and the payload**

```csharp
// HomeBlaze.Game.Tests/GameAppearanceTests.cs
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
        var self = sink.LastEntry("tab-1");
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
        var self = sink.LastEntry("tab-1");
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

        // Act
        player.Character = "robot";
        _context.Tick();

        // Assert
        Assert.Equal(GamePeople.GetDefault("tab-1"), sink.LastEntry("tab-1").Payload.Character);
    }
}
```

If `RecordingViewSink` has no `LastEntry(string id)` helper, add one to `HomeBlaze.Game.Tests/Testing/RecordingViewSink.cs` that returns the newest upserted `GameViewEntry` whose id matches; look at how existing view tests in `HomeBlaze.Game.Tests/Simulation/GameViewSessionTests.cs` read entries and reuse that shape.

- [ ] **Step 6: Run them to see them fail**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Game.Tests -c Debug --filter "FullyQualifiedName~GameAppearanceTests"`
Expected: build error, `GamePlayer.Character` and `GameViewPayload.Character` do not exist.

- [ ] **Step 7: Implement the properties, entity fields, refresh and payload**

`GamePlayer.cs`, next to `Facing`:

```csharp
    /// <summary>
    /// The person the player looks like, one of the people under <c>wwwroot/models/people/</c>; null or an unknown name
    /// shows the default person of its client.
    /// </summary>
    [Configuration]
    public partial string? Character { get; set; }

    /// <summary>
    /// The shirt color as <c>#rrggbb</c>; null or an invalid value shows the default color of its client.
    /// </summary>
    [Configuration]
    public partial string? Color { get; set; }
```

`PlayerEntity.cs`: add `public string Character { get; set; } = string.Empty;` and `public string Color { get; set; } = string.Empty;` plus

```csharp
    /// <summary>
    /// Reads the person and color from the subject; returns whether the resolved values changed.
    /// </summary>
    public bool ReadAppearance()
    {
        var character = GamePeople.Resolve(Subject.Character, ClientId);
        var color = GameColors.Resolve(Subject.Color, ClientId);
        if (character == Character && color == Color)
        {
            return false;
        }

        (Character, Color) = (character, color);
        return true;
    }
```

`GameWorldState.cs`: call `entity.ReadAppearance();` right after both `new PlayerEntity(...)` constructions (lines 612 and 627), and add

```csharp
    /// <summary>
    /// Reads the appearance of a player after an outside write; views see a change in this tick.
    /// </summary>
    public void RefreshAppearance(GamePlayer subject)
    {
        if (EntitiesBySubject.TryGetValue(subject, out var entity) && entity is PlayerEntity player && player.ReadAppearance())
        {
            Index.MarkChanged(player);
            IsSaveRequested = true;
        }
    }
```

`Game.Facade.cs` `OnPlayerWritten`, add:

```csharp
            case nameof(GamePlayer.Character):
            case nameof(GamePlayer.Color):
                _simulation.Post(() => _world.RefreshAppearance(player));
                break;
```

`GameViewPayload.cs`: add `string Character` after `string Color` (the record's positional order is the JSON order; the browser reads by name). `GameViewPayloads.cs`: pass `player.Character` and `player.Color` for players and `string.Empty` for objects; delete `_playerColors` and `GetPlayerColor`, which the entity's resolved color replaces.

Check `IsSaveRequested` exists on `GameWorldState` (it is set in `Game.Facade.cs:98`); if the name differs, use the same member that line uses.

- [ ] **Step 8: Run the whole game test project**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Game.Tests -c Debug --filter "Category!=Integration"`
Expected: PASS (all existing tests plus the new ones). `GameSerializationTests` may need the two new properties in an expected JSON; update the expectation, not the serializer.

- [ ] **Step 9: Commit and push**

```bash
git add src/HomeBlaze/HomeBlaze.Game src/HomeBlaze/HomeBlaze.Game.Tests
git commit -m "feat: let game players choose a person and a shirt color

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

---

### Task 2: Generic player animation

**Files:**
- Modify: `HomeBlaze.Game.Abstractions/IGameWorld.cs`, `HomeBlaze.Game.Abstractions.Tests/VerifyChecksTests.PublicApi.verified.txt`, `HomeBlaze.Game/Game.World.cs`, `HomeBlaze.Game/GamePlayer.cs`, `HomeBlaze.Game/Simulation/PlayerEntity.cs`, `HomeBlaze.Game/Simulation/GameWorldState.cs` (`TryMove` at 278, disconnect at 699 and 725), `HomeBlaze.Game/Simulation/GameWriteInterceptor.cs`, `HomeBlaze.Game/Simulation/GamePublisher.cs:107-124`, `HomeBlaze.Game/View/GameViewPayload.cs`, `HomeBlaze.Game/View/GameViewPayloads.cs`, `HomeBlaze.Game/GameSimulationSettings.cs`
- Test: `HomeBlaze.Game.Tests/GamePlayerAnimationTests.cs` (new), `HomeBlaze.Game.Tests/Simulation/GameWriteInterceptorTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
// HomeBlaze.Game.Tests/GamePlayerAnimationTests.cs
using HomeBlaze.Game.Abstractions;
using HomeBlaze.Game.Tests.Testing;

namespace HomeBlaze.Game.Tests;

public class GamePlayerAnimationTests
{
    private readonly GameTestContext _context = new();

    [Fact]
    public void WhenAPlayerPlaysAnAnimation_ThenItsSubjectShowsItAfterThePublish()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");

        // Act
        _context.Game.PlayAnimation(new GameCaller("tab-1"), "Clapping", GameVisualAnimationMode.Once);
        _context.Publish();

        // Assert
        Assert.Equal("Clapping", player.Animation);
    }

    [Fact]
    public void WhenTheSameAnimationPlaysTwice_ThenTheViewSeesTwoSequences()
    {
        // Arrange
        var sink = new RecordingViewSink();
        _context.AddConnectedPlayer("tab-1");
        using var view = _context.OpenView("tab-1", sink);
        var caller = new GameCaller("tab-1");

        // Act
        _context.Game.PlayAnimation(caller, "Clapping", GameVisualAnimationMode.Once);
        _context.Tick();
        var first = sink.LastEntry("tab-1").Payload.AnimationSequence;
        _context.Game.PlayAnimation(caller, "Clapping", GameVisualAnimationMode.Once);
        _context.Tick();

        // Assert
        Assert.NotEqual(first, sink.LastEntry("tab-1").Payload.AnimationSequence);
    }

    [Fact]
    public void WhenAOnceAnimationRunsLongerThanTheLimit_ThenItIsCleared()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        _context.Game.PlayAnimation(new GameCaller("tab-1"), "Jump", GameVisualAnimationMode.Once);

        // Act
        _context.Tick((int)(GameSimulationSettings.AnimationLimit / GameSimulationSettings.TickInterval) + 1);
        _context.Publish();

        // Assert
        Assert.Null(player.Animation);
    }

    [Fact]
    public async Task WhenAPlayerStepsDuringALoopAnimation_ThenItIsCleared()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        _context.Game.PlayAnimation(new GameCaller("tab-1"), "Sitting", GameVisualAnimationMode.Loop);
        _context.Tick((int)(GameSimulationSettings.AnimationLimit / GameSimulationSettings.TickInterval) + 1);

        // Act
        await _context.StepAsync("tab-1", GameDirection.East);
        _context.Publish();

        // Assert
        Assert.Null(player.Animation);
    }

    [Fact]
    public void WhenALoopAnimationRunsLongerThanTheLimit_ThenItStays()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        _context.Game.PlayAnimation(new GameCaller("tab-1"), "Sitting", GameVisualAnimationMode.Loop);

        // Act
        _context.Tick((int)(GameSimulationSettings.AnimationLimit / GameSimulationSettings.TickInterval) + 1);
        _context.Publish();

        // Assert
        Assert.Equal("Sitting", player.Animation);
    }

    [Fact]
    public void WhenThereIsNoPlayerForTheCaller_ThenPlayingThrows()
    {
        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => _context.Game.PlayAnimation(null, "Clapping", GameVisualAnimationMode.Once));
        Assert.Throws<InvalidOperationException>(() => _context.Game.PlayAnimation(new GameCaller("nobody"), "Clapping", GameVisualAnimationMode.Once));
    }

    [Theory]
    [InlineData("Idle")]
    [InlineData("Walk")]
    [InlineData("")]
    public void WhenAReservedOrEmptyAnimationIsPlayed_ThenPlayingThrows(string animation)
    {
        // Arrange
        _context.AddConnectedPlayer("tab-1");

        // Act & Assert
        Assert.Throws<ArgumentException>(() => _context.Game.PlayAnimation(new GameCaller("tab-1"), animation, GameVisualAnimationMode.Once));
    }

    [Fact]
    public void WhenThePlayerDisconnects_ThenItsAnimationIsCleared()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        _context.Game.PlayAnimation(new GameCaller("tab-1"), "Sitting", GameVisualAnimationMode.Loop);
        _context.Publish();

        // Act
        _context.Clients.Disconnect("tab-1");
        _context.Reconcile();
        _context.Publish();

        // Assert
        Assert.Null(player.Animation);
    }
}
```

Use whatever method `FakeClientDirectory` offers to disconnect a client (read `HomeBlaze.Game.Tests/Testing/FakeClientDirectory.cs`); the voice tests that check "reset when the client disconnects" show the exact sequence.

In `GameWriteInterceptorTests.WhenAStateOfAPlayerIsWrittenThroughTheRegistry_ThenTheWriteIsRefused` add `[InlineData(nameof(GamePlayer.Animation), "Clapping")]`.

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Game.Tests -c Debug --filter "FullyQualifiedName~GamePlayerAnimationTests|FullyQualifiedName~GameWriteInterceptorTests"`
Expected: build error, `PlayAnimation`, `GamePlayer.Animation` and `AnimationLimit` do not exist.

- [ ] **Step 3: Add the contract**

`IGameWorld.cs`:

```csharp
    /// <summary>
    /// Plays an animation of the person on the caller's player. <see cref="GameVisualAnimationMode.Once"/> returns to idle
    /// when the animation ends; <see cref="GameVisualAnimationMode.Loop"/> holds until the player's next step, which also
    /// ends a running animation. An animation the person does not have shows nothing.
    /// </summary>
    /// <param name="caller">The acting player; only the game page provides one.</param>
    /// <param name="animation">The name of the animation in the person's model, such as <c>Clapping</c>.</param>
    /// <param name="mode">Whether the animation plays once or loops.</param>
    /// <exception cref="ArgumentException">The animation is empty, <c>Idle</c> or <c>Walk</c>.</exception>
    /// <exception cref="InvalidOperationException">There is no player for the caller.</exception>
    void PlayAnimation(GameCaller? caller, string animation, GameVisualAnimationMode mode);
```

Run `dotnet test src/HomeBlaze/HomeBlaze.Game.Abstractions.Tests -c Debug`, then replace `VerifyChecksTests.PublicApi.verified.txt` with the `.received.txt` the failing test writes, and check its diff shows only the new method.

- [ ] **Step 4: Implement**

`GameSimulationSettings.cs`: add `public static readonly TimeSpan AnimationLimit = TimeSpan.FromSeconds(5);` with a summary "The longest a once animation shows on the server; the browser plays it to its end either way."

`GamePlayer.cs`:

```csharp
    /// <summary>
    /// The animation the player plays right now, such as <c>Clapping</c>, or null. Only the game sets it; it is not saved.
    /// </summary>
    [State]
    public partial string? Animation { get; internal set; }
```

Mark it so it is not serialized the same way `IsMicrophoneOn` is (check how `IsMicrophoneOn` is excluded from the saved file and copy that attribute or mechanism).

`PlayerEntity.cs`:

```csharp
    public string? Animation { get; set; }

    public GameVisualAnimationMode AnimationMode { get; set; }

    // Counts every start, so the same animation twice in a row plays twice.
    public int AnimationSequence { get; set; }

    public long AnimationStartedAt { get; set; }
```

`GameWorldState.cs`:

```csharp
    public void StartAnimation(PlayerEntity player, string animation, GameVisualAnimationMode mode)
    {
        player.Animation = animation;
        player.AnimationMode = mode;
        player.AnimationSequence++;
        player.AnimationStartedAt = _timeProvider.GetTimestamp();
        Index.MarkChanged(player);
        RequestPublish(player);
    }

    public void ClearAnimation(PlayerEntity player)
    {
        if (player.Animation is null)
        {
            return;
        }

        player.Animation = null;
        Index.MarkChanged(player);
        RequestPublish(player);
    }

    // Runs once per tick from the tick handler that already walks the players (find it next to the voice or rate work).
    public void ExpireAnimations()
    {
        foreach (var player in Players.Values)
        {
            if (player is { Animation: not null, AnimationMode: GameVisualAnimationMode.Once } &&
                _timeProvider.GetElapsedTime(player.AnimationStartedAt) >= GameSimulationSettings.AnimationLimit)
            {
                ClearAnimation(player);
            }
        }
    }
```

Call `ClearAnimation(player)` in `TryMove` when a step (not a turn only) is accepted, and next to both `ResetVoice(player)` calls on disconnect (lines 699 and 725). Call `ExpireAnimations()` once per tick from the place the simulation ticks the world state (search `GameWorldState` for the method the tick handler calls each tick, for example where rate limiters or `RefreshChangedSubjects` run, and add it there).

`Game.World.cs`:

```csharp
    /// <inheritdoc />
    public void PlayAnimation(GameCaller? caller, string animation, GameVisualAnimationMode mode)
    {
        ArgumentException.ThrowIfNullOrEmpty(animation);
        if (animation is "Idle" or "Walk")
        {
            throw new ArgumentException("Idle and Walk are played by the game.", nameof(animation));
        }

        if (GetActor(caller) is null)
        {
            throw new InvalidOperationException(NoActorMessage);
        }

        var clientId = caller!.ClientId;
        _simulation.Post(() =>
        {
            if (_world.Players.TryGetValue(clientId, out var player) && player.IsConnected)
            {
                _world.StartAnimation(player, animation, mode);
            }
        });
    }
```

`Post` runs inline when already on the simulation thread; check `GameSimulation.Post`'s contract and keep this shape either way (tests tick after calling).

`GameWriteInterceptor.IsRefused`: add `case nameof(GamePlayer.Animation):` to the `return true` group.

`GamePublisher.Publish`: after `IsAudioOn`, add

```csharp
                SetWriting(subject, nameof(GamePlayer.Animation));
                subject.Animation = player.Animation;
```

`GameViewPayload.cs`: add `string? Animation, int AnimationMode, int AnimationSequence` after `bool IsAudioOn`. `GameViewPayloads.cs`: pass `player.Animation, (int)player.AnimationMode, player.AnimationSequence` for players and `null, 0, 0` for objects.

- [ ] **Step 5: Run all game tests**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Game.Tests -c Debug --filter "Category!=Integration"` and `dotnet test src/HomeBlaze/HomeBlaze.Game.Abstractions.Tests -c Debug`
Expected: PASS.

- [ ] **Step 6: Commit and push**

```bash
git add src/HomeBlaze/HomeBlaze.Game.Abstractions src/HomeBlaze/HomeBlaze.Game.Abstractions.Tests src/HomeBlaze/HomeBlaze.Game src/HomeBlaze/HomeBlaze.Game.Tests
git commit -m "feat: let game objects play animations on the acting player

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

---

### Task 3: Emote tool

**Files:**
- Create: `HomeBlaze.Game/Tools/EmoteTool.cs`
- Modify: `/tmp/.../scratchpad/world/make_world.py` (the demo world generator; path in the controller's notes), `HomeBlaze/Data/Files/Demo/Game.json` (regenerated, staged via `update-index`)
- Test: `HomeBlaze.Game.Tests/Tools/EmoteToolTests.cs` (new)

- [ ] **Step 1: Write failing tests**

```csharp
// HomeBlaze.Game.Tests/Tools/EmoteToolTests.cs
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
```

- [ ] **Step 2: Run to see them fail**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Game.Tests -c Debug --filter "FullyQualifiedName~EmoteToolTests"`
Expected: build error, `EmoteTool` does not exist.

- [ ] **Step 3: Implement**

```csharp
// HomeBlaze.Game/Tools/EmoteTool.cs
using HomeBlaze.Game.Abstractions;
using Microsoft.AspNetCore.Mvc;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Game.Tools;

/// <summary>
/// Lets players clap, sit and jump; everyone nearby sees it.
/// </summary>
[InterceptorSubject]
public partial class EmoteTool : GameTool
{
    /// <summary>Claps once.</summary>
    /// <param name="caller">The acting player.</param>
    /// <exception cref="InvalidOperationException">There is no player or the tool is not part of a game.</exception>
    [Operation(Title = "Clap", Icon = "SignLanguage", Position = 1, Description = "Claps your hands once.")]
    public void Clap([FromServices] GameCaller? caller) =>
        World.PlayAnimation(caller, "Clapping", GameVisualAnimationMode.Once);

    /// <summary>Sits down until the next step.</summary>
    /// <param name="caller">The acting player.</param>
    /// <exception cref="InvalidOperationException">There is no player or the tool is not part of a game.</exception>
    [Operation(Title = "Sit", Icon = "Chair", Position = 2, Description = "Sits down until you walk on.")]
    public void Sit([FromServices] GameCaller? caller) =>
        World.PlayAnimation(caller, "Sitting", GameVisualAnimationMode.Loop);

    /// <summary>Jumps once.</summary>
    /// <param name="caller">The acting player.</param>
    /// <exception cref="InvalidOperationException">There is no player or the tool is not part of a game.</exception>
    [Operation(Title = "Jump", Icon = "Height", Position = 3, Description = "Jumps once.")]
    public void Jump([FromServices] GameCaller? caller) =>
        World.PlayAnimation(caller, "Jump", GameVisualAnimationMode.Once);
}
```

Copy the exact `using` lines, the `[FromServices]` attribute namespace and the operation attribute shape from `HomeBlaze.Game.Nature/TreeTool.cs`; the tree tool is the reference for a tool written against the abstractions. `GameTool`'s constructor is protected; check whether tools need a parameterless public constructor for `$type` loading as `TreeTool` has.

- [ ] **Step 4: Run tests**

Run: same as Step 2. Expected: PASS.

- [ ] **Step 5: Add the tool to the demo world**

In `make_world.py`, add `EMOTE_TOOL = "HomeBlaze.Game.Tools.EmoteTool"` and `"emote-tool": {"$type": EMOTE_TOOL}` to `tools`. Regenerate `Game.json` with the script, run `DemoWorldTests` (`--filter "FullyQualifiedName~DemoWorldTests"`), then stage it:

```bash
blob=$(git hash-object -w src/HomeBlaze/HomeBlaze/Data/Files/Demo/Game.json)
git update-index --cacheinfo 100644,$blob,src/HomeBlaze/HomeBlaze/Data/Files/Demo/Game.json
```

- [ ] **Step 6: Commit and push**

```bash
git add src/HomeBlaze/HomeBlaze.Game src/HomeBlaze/HomeBlaze.Game.Tests
git commit -m "feat: add an emote tool to clap, sit and jump in the game

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

---

### Task 4: Edit me dialog

**Files:**
- Create: `HomeBlaze.Game/Components/GamePlayerDialog.razor`
- Delete: `HomeBlaze.Game/Components/GameNameDialog.razor`
- Modify: `HomeBlaze.Game/Game.Players.cs:50-75`, `HomeBlaze.Game/Components/GamePage.Panel.cs:86-110`, `HomeBlaze.Game/Components/GamePage.razor` (pencil tooltip "Edit me")
- Test: `HomeBlaze.Game.Tests/GamePlayerActionsTests.cs` (edit tests), E2E in Task 9

- [ ] **Step 1: Write failing tests for `Game.EditAsync`**

Add to `GamePlayerActionsTests.cs` (it already tests `RenameAsync`; mirror those tests' arrangement):

```csharp
    [Fact]
    public async Task WhenAPlayerEditsItsLookWithoutAName_ThenThePersonAndColorChangeAndTheClientStaysAnonymous()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");

        // Act
        await _context.Game.EditAsync(new GameCaller("tab-1"), name: null, "male-shirt", "#00ff00", CancellationToken.None);
        _context.Publish();

        // Assert
        Assert.Equal(("male-shirt", "#00ff00"), (player.Character, player.Color));
        Assert.False(_context.Clients.IsNamed("tab-1"));
    }

    [Fact]
    public async Task WhenAPlayerEditsItsNameAndLook_ThenAllThreeChange()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");

        // Act
        await _context.Game.EditAsync(new GameCaller("tab-1"), "Rico", "female-casual", "#123456", CancellationToken.None);
        _context.Reconcile();
        _context.Publish();

        // Assert
        Assert.Equal(("Rico", "female-casual", "#123456"), (player.ClientTitle, player.Character, player.Color));
    }

    [Fact]
    public async Task WhenTheCallerIsNotAPlayer_ThenEditingThrows()
    {
        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _context.Game.EditAsync(new GameCaller("nobody"), null, "male-shirt", null, CancellationToken.None));
    }
```

Use the `FakeClientDirectory` member that tells whether a client is named (add an `IsNamed(string id)` helper to the fake if none exists).

- [ ] **Step 2: Run to see them fail**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Game.Tests -c Debug --filter "FullyQualifiedName~GamePlayerActionsTests"`
Expected: build error, `EditAsync` does not exist.

- [ ] **Step 3: Implement `EditAsync`**

In `Game.Players.cs`, next to `RenameAsync`:

```csharp
    /// <summary>
    /// Changes the caller's player: renames its client when <paramref name="name"/> is not empty, and sets the person
    /// and shirt color, where null shows the default of the client.
    /// </summary>
    /// <exception cref="ArgumentException">The name is longer than <see cref="MaximumNameLength"/> after trimming.</exception>
    /// <exception cref="InvalidOperationException">The caller is not a player of this game, or its client is gone.</exception>
    internal async Task EditAsync(GameCaller? caller, string? name, string? character, string? color, CancellationToken cancellationToken)
    {
        if (GetActor(caller) is not { } player)
        {
            throw new InvalidOperationException(NoRenameActorMessage);
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            await RenameAsync(caller, name, cancellationToken);
        }

        // Ordinary subject writes; the facade hands them to the simulation like any outside write.
        player.Character = character;
        player.Color = color;
    }
```

- [ ] **Step 4: Build the dialog**

`GamePlayerDialog.razor` replaces `GameNameDialog.razor`. Keep its structure (MudDialog, `ShowAsync` static helper, Enter saves) and extend it:

```razor
@using HomeBlaze.Game.People
<MudDialog data-testid="game-player-dialog">
    <DialogContent>
        <MudTextField @bind-Value="_name" Label="Name" Immediate="true" AutoFocus="true" MaxLength="@Game.MaximumNameLength"
                      OnKeyDown="OnKeyDown" data-testid="game-name-input" />
        <MudColorPicker @bind-Value="_color" Label="Shirt color" ColorPickerView="ColorPickerView.Palette" ShowAlpha="false"
                        data-testid="game-color-input" />
        <MudText Typo="Typo.caption" Class="mt-4">Person</MudText>
        <div class="game-people">
            @foreach (var person in GamePeople.Names)
            {
                <button type="button" class="@(person == _character ? "game-person selected" : "game-person")"
                        @onclick="() => _character = person" title="@person" data-testid="game-person-@person">
                    <img src="_content/HomeBlaze.Game/models/people/@(person).png" alt="@person" />
                </button>
            }
        </div>
    </DialogContent>
    <DialogActions>
        <MudButton OnClick="Cancel" data-testid="game-player-cancel">Cancel</MudButton>
        <MudButton Color="Color.Primary" OnClick="Save" data-testid="game-player-save">Save</MudButton>
    </DialogActions>
</MudDialog>
```

The code section takes `Name`, `Character` (resolved) and `Color` (resolved hex) parameters, returns a `GamePlayerEdit(string? Name, string Character, string Color)` record (define it in the same file's `@code` or a small `.cs` next to it) or null on cancel, and `ShowAsync(IDialogService, string? name, string character, string color)` opens it titled "Edit me". Convert MudBlazor's `MudColor` to `#rrggbb` with `_color.Value[..7]` (check the `MudColor.Value` format in the installed MudBlazor version and adjust). Add CSS for `.game-people` (a wrap of 64 px square buttons, selected one with a primary outline) to `GamePage.razor.css` or a `GamePlayerDialog.razor.css`.

In `GamePage.Panel.cs`, `RenameAsync` becomes `EditAsync`: read the resolved values from the own player's latest view payload (the panel already knows `_player`; use `GamePeople.Resolve(_player.Character, _player.ClientId)` and `GameColors.Resolve(_player.Color, _player.ClientId)`), show the dialog, call `game.EditAsync(caller, edit.Name, edit.Character, edit.Color, CancellationToken.None)`. Keep the existing error dialog title constant, renamed to "Edit me". Update the pencil's tooltip and `data-testid` stays `game-rename` only if existing E2E tests use it; search `HomeBlaze.Game.Tests/E2E` and keep test ids stable.

- [ ] **Step 5: Run game tests and the rename E2E test**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Game.Tests -c Debug --filter "Category!=Integration"` then `dotnet test src/HomeBlaze/HomeBlaze.Game.Tests -c Debug --filter "FullyQualifiedName~WhenThePlayerChangesItsName"`
Expected: PASS. Update the E2E test's dialog test ids if they changed (`game-name-dialog` becomes `game-player-dialog`, `game-name-save` becomes `game-player-save`).

- [ ] **Step 6: Commit and push**

```bash
git add src/HomeBlaze/HomeBlaze.Game src/HomeBlaze/HomeBlaze.Game.Tests
git commit -m "feat: let players edit their name, person and shirt color in one dialog

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

(The previews referenced here are produced in Task 5; until then the images are missing, which is fine for this commit.)

---

### Task 5: Assets (people, previews, drawn textures)

**Files:**
- Create: `HomeBlaze.Game/tools/README.md`, `HomeBlaze.Game/tools/convert-people.js`, `HomeBlaze.Game/tools/make-textures.js`
- Create: `HomeBlaze.Game/wwwroot/models/people/{male-casual,male-shirt,male-long-sleeve,female-casual,female-alternative,female-tank-top}.glb` and `.png`, `HomeBlaze.Game/wwwroot/models/people/LICENSE.md`
- Create: `HomeBlaze.Game/wwwroot/textures/grass.png`; delete `grass.jpg`; update `HomeBlaze.Game/wwwroot/textures/LICENSE.md`
- Create: `HomeBlaze.Game.Buildings/wwwroot/textures/{bricks,concrete,wood}.png`; delete the `.jpg` files; update `LICENSE.md` and `HomeBlaze.Game.Buildings/WallTextures.cs`
- Delete: `HomeBlaze.Game/wwwroot/models/Casual_2.glb`; update `HomeBlaze.Game/wwwroot/models/LICENSE.md`
- Modify: the `.csproj` files only if they list assets explicitly (check; static web assets normally pick up `wwwroot` by glob)

- [ ] **Step 1: Write the conversion script**

`convert-people.js` is a Node script that drives Playwright (resolved from `$PLAYWRIGHT_PACKAGE`, documented in the README as the `.playwright/package` folder of any built test project) to load three.js 0.160.0 from jsDelivr in a blank page, and for each FBX: parse with `FBXLoader`, convert every material to `MeshStandardMaterial({ name, color: m.color.clone().convertLinearToSRGB(), metalness: 0, roughness: 1, flatShading: true })`, keep only the clips whose names end with `_Idle`, `_Walk`, `_Clapping`, `_Sitting`, `_Jump` and rename them to `Idle`, `Walk`, `Clapping`, `Sitting`, `Jump`, export a binary glTF with `GLTFExporter`, and render a 128 x 128 PNG preview (front view, transparent background, the Idle pose at time 0) with a `WebGLRenderer` and `canvas.toDataURL`. The working converter from the brainstorming is at `/tmp/claude-1000/-home-rico-GitHub-Namotion-Interceptor--claude-worktrees-bridge-cse-011ShuL5XgwWZqRmSZFhkZve/3d51e483-5513-56d7-b4a2-5748ff2f1414/scratchpad/fbx-convert.js`; start from it and add the clip filter, renaming and preview.

Arguments: `node convert-people.js <fbx-folder> <out-folder>`; the FBX files come from `https://github.com/beep2bleep/FreeAssetsByKenneyNLandQuaternius` at `FreeModels by Quaternius[Patreon]/Characters and Animals/Animated Men Characters - Feb 2019/FBX/` and `.../Animated Women Characters - Feb 2019/FBX/`; output names are the kebab-case names of `GamePeople.Names` (`Male_Casual.fbx` to `male-casual.glb`). Downloads go into their own empty scratch folder, never into the repository.

Check the size of each output: it should be under 400 KB. If it is not, also drop the morph or unused attributes the exporter writes (look at the glTF JSON with a short script) or quantize positions; record what was done in the README.

- [ ] **Step 2: Write the texture script**

`make-textures.js` drives the same headless browser and draws each texture on a 256 x 256 canvas, then writes PNGs:
- `bricks.png`: fill `#e0866a`, lines `rgba(120,50,35,.45)` width 5, 8 rows, bricks a quarter wide, every other row offset by an eighth.
- `concrete.png`: fill `#d9d4ca`, a border line `rgba(110,105,95,.4)` and six small dots.
- `wood.png`: fill `#c99668`, four vertical seams `rgba(95,60,30,.45)` width 5 and ten short grain strokes width 2.
- `grass.png`: fill `#9cc77f` with the meter grid: a border stroke `rgba(80,110,60,.35)` width 8, half outside, so neighbors join to one line.

These are the exact values of the style lab (`style-lab-v6.html` in the brainstorm folder, function `lineTexture` with `pastel = true`). Write them into `wwwroot/textures/` of the right project.

- [ ] **Step 3: Run both scripts and wire up the files**

Run the scripts, copy the outputs into place, delete the old `.jpg` files and `Casual_2.glb`. In `WallTextures.cs` change the three URLs to `.png`; keep `GetScale` unless the drawn bricks look wrong at the current 8 tiles per repeat (the lab used 8). In `game-entities.js` change `textures/grass.jpg` to `textures/grass.png` in `createGroundMaterial` and remove the canvas grid stroke there (the PNG has it).

`models/people/LICENSE.md`: source URLs (the Quaternius pack pages and the mirror repository with commit), CC0 1.0, and the conversion (script name, three.js version, material change, clip filter). `textures/LICENSE.md` in both projects: our own work, CC0 1.0, generated by `HomeBlaze.Game/tools/make-textures.js`. `models/LICENSE.md` loses the Casual_2 paragraph.

- [ ] **Step 4: Build and check the assets are served**

Run: `dotnet build src/HomeBlaze/HomeBlaze -c Debug` (expect 0 errors), then `dotnet test src/HomeBlaze/HomeBlaze.Game.Buildings.Tests -c Debug` (texture URL tests may assert `.jpg`; update them to `.png`).

- [ ] **Step 5: Commit and push**

```bash
git add src/HomeBlaze/HomeBlaze.Game src/HomeBlaze/HomeBlaze.Game.Buildings src/HomeBlaze/HomeBlaze.Game.Buildings.Tests
git commit -m "feat: add low poly people and drawn textures to the game

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

---

### Task 6: People and animations in the browser

**Files:**
- Modify: `HomeBlaze.Game/wwwroot/game-entities.js` (`createPlayer` at ~460, `hasShapeChanged` at ~663, the walking state at ~600, `dispose` at ~870, `createEntities` where `character` is loaded at ~145, `upsert` at ~804)

- [ ] **Step 1: Load people per name**

Replace the single `character` container with a cache:

```js
// One container per person, loaded on first use and kept for the view's lifetime.
const people = new Map();

function loadPerson(name) {
    let loading = people.get(name);
    if (!loading) {
        loading = BABYLON.LoadAssetContainerAsync(`${options.baseUrl}models/people/${name}.glb`, scene).catch(error => {
            console.warn(`The person ${name} failed to load`, error);
            return null;
        });
        people.set(name, loading);
    }

    return loading;
}
```

`createPlayer` becomes async-tolerant: create the holder, label and an invisible placeholder synchronously, then `loadPerson(view.character).then(container => ...)` instantiates the model (skip when `entity.isDisposed`), recolors the material named `${prefix}Shirt` with `view.color` (keep today's albedo/diffuse branch), finds `Idle` and `Walk` groups (constants `IDLE_ANIMATION = 'Idle'`, `WALKING_ANIMATION = 'Walk'`; delete `SHIRT_MATERIAL = 'LightBrown'` and use `'Shirt'`), starts idle, and applies the current animation state (Step 2). A failed load leaves the label and shows a gray box of the player's size, counted in `stats.failures` like failed object models. `dispose` disposes every loaded person container.

`hasShapeChanged` adds `|| previous.character !== view.character`. Color stays in the comparison (a recolor recreates the player, as today).

- [ ] **Step 2: Play animations from the view**

Add to the player entity `animations` object: `emote: null, emoteSequence: 0`. In `upsert` for players (both create and update paths) call `applyAnimation(entity, view)`:

```js
// Plays the animation the server reports: once animations play to their end and return to idle, loop animations hold
// until the server clears them. A new sequence restarts the same animation.
function applyAnimation(entity, view) {
    const animations = entity.animations;
    if (!animations || !entity.instance) {
        return;
    }

    if (!view.animation) {
        if (animations.emote) {
            animations.emote.stop();
            animations.emote = null;
            if (!animations.isWalking) {
                animations.idle?.start(true);
            }
        }

        animations.emoteSequence = view.animationSequence;
        return;
    }

    if (animations.emote && animations.emoteSequence === view.animationSequence) {
        return;
    }

    const group = entity.instance.animationGroups.find(candidate => candidate.name === `${view.id}-${view.animation}`);
    animations.emoteSequence = view.animationSequence;
    if (!group) {
        return;
    }

    animations.emote?.stop();
    animations.idle?.stop();
    animations.walking?.stop();
    const isOnce = view.animationMode === ONCE_ANIMATION_MODE;
    group.start(!isOnce, 1);
    animations.emote = group;
    stats.emotes++;
    updateStats();
    if (isOnce) {
        group.onAnimationGroupEndObservable.addOnce(() => {
            if (animations.emote === group) {
                animations.emote = null;
                if (!animations.isWalking) {
                    animations.idle?.start(true);
                }
            }
        });
    }
}
```

`setWalking(entity, true)` stops a running emote first. Add `emotes: 0` to `stats` and `element.dataset.gameEmotes = String(stats.emotes)` to `updateStats` (the E2E test of Task 9 reads it). Reset/stop logic must follow the existing "reset() before stop()" rule the file documents for frozen poses.

- [ ] **Step 3: Tune the walk speed**

With the app running in Debug on another port is not possible while the Release app runs, so tune with the style lab instead: open the people view, set Walk, and compare the feet against a 1.4 m/s glide. Set `WALKING_SPEED_RATIO` so one cycle covers about two steps of one tile at the step interval; the Quaternius walk cycle is 1.04 s.

- [ ] **Step 4: Run the E2E tests that touch players**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Game.Tests -c Debug --filter "Category=Integration"`
Expected: PASS (21 tests). Watch for console warnings about people failing to load.

- [ ] **Step 5: Commit and push**

```bash
git add src/HomeBlaze/HomeBlaze.Game/wwwroot/game-entities.js
git commit -m "feat: render the chosen person of each player and play its animations

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

Restart the app afterwards (see the controller's restart procedure) so the user sees the new people.

---

### Task 7: Style E rendering

**Files:**
- Create: `HomeBlaze.Game/wwwroot/game-render.js`
- Modify: `HomeBlaze.Game/wwwroot/game.js:135-170` (scene setup, render), `HomeBlaze.Game/wwwroot/game-entities.js` (shadow casters, outline exclusion, blob removal, fade)

- [ ] **Step 1: Create `game-render.js` with lights, shadows and the post-processing chain**

```js
// Lights, shadows and the post-processing of the game's look: soft sun shadows that follow the own player, ink outlines
// from one geometry buffer that corner shading shares, multisampling and a light sharpen.
const SKY_COLOR = '#f3e6d3';
const SUN_COLOR = '#fff1d6';
// The shadow map covers this many tiles around the camera target, so its resolution does not spread over the view radius.
const SHADOW_EXTENT = 120;
const OUTLINE_STRENGTH = 0.55;
const OUTLINE_WIDTH = 1.5;
const INK_DARKNESS = 0.6;

const OUTLINE_SHADER = `
    precision highp float;
    varying vec2 vUV;
    uniform sampler2D textureSampler;
    uniform sampler2D depthSampler;
    uniform sampler2D normalSampler;
    uniform vec2 texel;
    uniform float thickness;
    uniform float strength;
    uniform vec3 inkColor;
    float depthAt(vec2 uv) { return texture2D(depthSampler, uv).r; }
    vec3 normalAt(vec2 uv) { return texture2D(normalSampler, uv).rgb; }
    void main(void) {
        vec3 color = texture2D(textureSampler, vUV).rgb;
        vec2 o = texel * thickness;
        float d = depthAt(vUV);
        float depthEdge = (abs(depthAt(vUV - vec2(o.x, 0.0)) - d) + abs(depthAt(vUV + vec2(o.x, 0.0)) - d)
            + abs(depthAt(vUV + vec2(0.0, o.y)) - d) + abs(depthAt(vUV - vec2(0.0, o.y)) - d)) / max(d, 0.0001);
        vec3 n = normalAt(vUV);
        float normalEdge = (1.0 - dot(n, normalAt(vUV - vec2(o.x, 0.0)))) + (1.0 - dot(n, normalAt(vUV + vec2(0.0, o.y))));
        float edge = max(smoothstep(0.02, 0.05, depthEdge), smoothstep(0.2, 0.45, normalEdge));
        if (d <= 0.0) edge = 0.0;
        gl_FragColor = vec4(mix(color, inkColor, edge * strength), 1.0);
    }`;

export function createRenderer(scene, camera) {
    const BABYLON = window.BABYLON;
    const sky = BABYLON.Color3.FromHexString(SKY_COLOR);
    scene.clearColor = new BABYLON.Color4(sky.r, sky.g, sky.b, 1);
    scene.fogColor = sky;

    const hemisphere = new BABYLON.HemisphericLight('sky', new BABYLON.Vector3(0.2, 1, -0.3), scene);
    hemisphere.intensity = 0.55;
    hemisphere.groundColor = new BABYLON.Color3(0.35, 0.4, 0.3);
    const sun = new BABYLON.DirectionalLight('sun', new BABYLON.Vector3(-0.5, -1, 0.7), scene);
    sun.intensity = 1.0;
    sun.diffuse = BABYLON.Color3.FromHexString(SUN_COLOR);
    sun.shadowFrustumSize = SHADOW_EXTENT;
    sun.shadowMinZ = 1;
    sun.shadowMaxZ = 400;

    const shadows = new BABYLON.ShadowGenerator(2048, sun);
    shadows.usePercentageCloserFiltering = true;
    shadows.filteringQuality = BABYLON.ShadowGenerator.QUALITY_MEDIUM;
    shadows.darkness = 0.35;
    shadows.bias = 0.002;

    const geometry = scene.enableGeometryBufferRenderer();
    const depthIndex = geometry.getTextureIndex(BABYLON.GeometryBufferRenderer.DEPTH_TEXTURE_TYPE);
    const normalIndex = geometry.getTextureIndex(BABYLON.GeometryBufferRenderer.NORMAL_TEXTURE_TYPE);
    // Only meshes in this list draw into the geometry buffer, so labels and faded meshes never get outlines.
    geometry.renderList = [];

    BABYLON.Effect.ShadersStore.gameOutlineFragmentShader = OUTLINE_SHADER;
    const outline = new BABYLON.PostProcess('outline', 'gameOutline', ['texel', 'thickness', 'strength', 'inkColor'],
        ['depthSampler', 'normalSampler'], 1.0, camera);
    outline.samples = 4;
    const ink = [0.25 + (0.06 - 0.25) * INK_DARKNESS, 0.16 + (0.05 - 0.16) * INK_DARKNESS, 0.12 + (0.05 - 0.12) * INK_DARKNESS];
    outline.onApply = effect => {
        effect.setFloat2('texel', 1 / outline.width, 1 / outline.height);
        effect.setFloat('thickness', OUTLINE_WIDTH);
        effect.setFloat('strength', OUTLINE_STRENGTH);
        effect.setFloat3('inkColor', ink[0], ink[1], ink[2]);
        effect.setTexture('depthSampler', geometry.getGBuffer().textures[depthIndex]);
        effect.setTexture('normalSampler', geometry.getGBuffer().textures[normalIndex]);
    };

    // Forced onto the geometry buffer, so it does not switch the scene to a prepass the outline cannot read.
    const ambientOcclusion = new BABYLON.SSAO2RenderingPipeline('ssao', scene, { ssaoRatio: 0.5, blurRatio: 0.5 }, [camera], true);
    ambientOcclusion.radius = 3;
    ambientOcclusion.totalStrength = 1.3;
    ambientOcclusion.base = 0.15;
    ambientOcclusion.samples = 8;
    ambientOcclusion.maxZ = 250;

    const pipeline = new BABYLON.DefaultRenderingPipeline('look', true, scene, [camera]);
    pipeline.fxaaEnabled = false;
    pipeline.sharpenEnabled = true;
    pipeline.sharpen.edgeAmount = 0.35;
    pipeline.imageProcessingEnabled = true;
    pipeline.imageProcessing.contrast = 1.1;
    pipeline.imageProcessing.exposure = 1.05;

    return {
        sun,
        ambientOcclusion,
        // Called by the entities for every mesh that should cast shadows and get outlines.
        addMesh(mesh) {
            shadows.addShadowCaster(mesh, false);
            mesh.receiveShadows = true;
            geometry.renderList.push(mesh);
        },
        removeMesh(mesh) {
            shadows.removeShadowCaster(mesh, false);
            const index = geometry.renderList.indexOf(mesh);
            if (index >= 0) {
                geometry.renderList.splice(index, 1);
            }
        },
        // A faded mesh leaves the geometry buffer, so the outline does not draw the wall it no longer shows.
        setOutlined(mesh, isOutlined) {
            const index = geometry.renderList.indexOf(mesh);
            if (isOutlined && index < 0) {
                geometry.renderList.push(mesh);
            } else if (!isOutlined && index >= 0) {
                geometry.renderList.splice(index, 1);
            }
        },
        receiveShadows(mesh) {
            mesh.receiveShadows = true;
        },
        // Keeps the shadow frustum centered on what the camera looks at.
        follow(target) {
            sun.position.copyFrom(target).subtractInPlace(sun.direction.scale(200));
        },
    };
}
```

Verify `GeometryBufferRenderer.renderList` exists in Babylon 9.31 (`grep -o "renderList" HomeBlaze.Game/wwwroot/lib/babylonjs/babylon.js | head` and read the property near `GeometryBufferRenderer`). If it does not, exclude labels and faded meshes with whatever the class offers (for example `excludedSkinnedMeshesFromVelocity` is not it; look for an `isReady`/`renderList`/`customRenderFunction` hook) and keep the same three methods as the interface.

- [ ] **Step 2: Use it in `game.js`**

In `initialize`, replace the sky, fog color and the two lights with `this.renderer = createRenderer(this.scene, this.camera);` (keep fog mode, start and end), pass `renderer: this.renderer` into `createEntities(this.scene, { ...this.options, renderer: this.renderer, statsElement: this.canvas })`, and in `render()` call `this.renderer.follow(this.cameraTarget)` before `scene.render()`. Import `createRenderer` from `./game-render.js` the way `game.js` imports `createEntities`.

- [ ] **Step 3: Register meshes in `game-entities.js`**

- Every box part and model mesh: `options.renderer.addMesh(mesh)` when created, `removeMesh` when disposed.
- Every player model mesh: the same, once the person loaded.
- The ground: `options.renderer.receiveShadows(ground)` only (it does not cast and needs no outline; it stays out of the geometry buffer, so the grid lines never get outlines).
- Labels and target markers: never registered.
- Fading: where `entity.isFaded` flips, call `options.renderer.setOutlined(mesh, !entity.isFaded)` for each mesh of the entity.
- Delete the blob disc, `blobMaterial` and their dispose code.

- [ ] **Step 4: Check in the running app**

Restart the app (controller procedure) and open the demo page; take a Playwright screenshot of the house and the forest (`scratchpad/shot.js` pattern) and compare against the style lab's E view: outlines on walls, people and trees, none on names; shadows under everything; a faded wall without outline. Fix what differs.

- [ ] **Step 5: Run the E2E tests**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Game.Tests -c Debug --filter "Category=Integration"`
Expected: PASS. The fade test (`WhenAWallWithAnOpenDoorStandsBetweenTheCameraAndThePlayer_ThenTheDoorFades`) must still pass.

- [ ] **Step 6: Commit and push**

```bash
git add src/HomeBlaze/HomeBlaze.Game/wwwroot
git commit -m "feat: give the game a diorama look with soft shadows, corner shading and ink outlines

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

---

### Task 8: Quality presets and the render pause

**Files:**
- Modify: `HomeBlaze.Game/wwwroot/game-render.js`, `HomeBlaze.Game/wwwroot/game.js`, `HomeBlaze.Game/Components/GamePage.razor`, `HomeBlaze.Game/Components/GamePage.Panel.cs`, `HomeBlaze.Game/Components/GameViewConnection.cs` (or whichever class holds the JS module reference; read `GamePage.razor.cs` first)

- [ ] **Step 1: Presets in `game-render.js`**

```js
export const QUALITY_PRESETS = {
    low: { resolution: 1, frameCap: 30, ambientOcclusion: 0 },
    medium: { resolution: 1.5, frameCap: 60, ambientOcclusion: 0.5 },
    high: { resolution: 2, frameCap: 60, ambientOcclusion: 1 },
};
const QUALITY_KEY = 'homeblaze.game.quality';
const UNFOCUSED_FRAME_CAP = 10;
// Auto drops one level when frames stay this much above the cap's budget for this long.
const SLOW_FRAME_FACTOR = 1.5;
const SLOW_DURATION_MS = 5000;

// Storage can be missing or throw in private windows; the page then works with Auto for this session.
export function readQuality() {
    try {
        return localStorage.getItem(QUALITY_KEY) ?? 'auto';
    } catch {
        return 'auto';
    }
}

export function writeQuality(value) {
    try {
        localStorage.setItem(QUALITY_KEY, value);
    } catch {
        // Kept for this session only.
    }
}
```

Add to the object `createRenderer` returns:

```js
        // Applies a preset name or 'auto'; returns the preset in effect.
        setQuality(choice) {
            const level = choice === 'auto' ? autoLevel : choice;
            const preset = QUALITY_PRESETS[level] ?? QUALITY_PRESETS.medium;
            current = { choice, level, ...preset };
            scene.getEngine().setHardwareScalingLevel(1 / Math.min(window.devicePixelRatio || 1, preset.resolution));
            ambientOcclusion.ssaoRatio = preset.ambientOcclusion || 0.5;
            ambientOcclusion.totalStrength = preset.ambientOcclusion > 0 ? 1.3 : 0;
            return current;
        },
        // Called once per animation frame; returns whether this frame should render.
        shouldRender(now, isFocused) {
            const cap = isFocused ? current.frameCap : UNFOCUSED_FRAME_CAP;
            if (now - lastRender < 1000 / cap - 2) {
                return false;
            }

            const frameTime = now - lastRender;
            lastRender = now;
            if (current.choice === 'auto' && isFocused) {
                trackAuto(frameTime, cap, now);
            }

            return true;
        },
```

with closure state `let current; let lastRender = 0; let autoLevel = matchMedia('(pointer: coarse)').matches ? 'low' : 'medium'; let slowSince = 0;` and

```js
    // Steps down at most to low and never back up within the session.
    function trackAuto(frameTime, cap, now) {
        if (frameTime > (1000 / cap) * SLOW_FRAME_FACTOR) {
            slowSince ||= now;
            if (now - slowSince > SLOW_DURATION_MS && autoLevel !== 'low') {
                autoLevel = autoLevel === 'high' ? 'medium' : 'low';
                slowSince = 0;
                api.setQuality('auto');
            }
        } else {
            slowSince = 0;
        }
    }
```

(name the returned object `api` so `trackAuto` can call it). `ssaoRatio` cannot change after creation in every Babylon version; if setting it has no effect, recreate the SSAO pipeline in `setQuality` when the ratio changes, disposing the old one first.

- [ ] **Step 2: Render loop and pause in `game.js`**

In `initialize`: `this.renderer.setQuality(readQuality());`, and replace `this.engine.runRenderLoop(() => this.render());` with a loop that skips frames:

```js
        this.isFocused = document.hasFocus();
        this.onFocus = () => { this.isFocused = true; };
        this.onBlur = () => { this.isFocused = false; };
        window.addEventListener('focus', this.onFocus);
        window.addEventListener('blur', this.onBlur);
        // The browser stops animation frames in hidden tabs; the loop also stops explicitly, so it resumes cleanly.
        this.onVisibilityChange = () => {
            if (document.hidden) {
                this.engine.stopRenderLoop();
                this.canvas.dataset.gameRendering = 'paused';
            } else {
                this.lastFrame = performance.now();
                this.engine.runRenderLoop(this.loop);
                this.canvas.dataset.gameRendering = 'running';
            }
        };
        document.addEventListener('visibilitychange', this.onVisibilityChange);
        this.loop = () => {
            if (this.renderer.shouldRender(performance.now(), this.isFocused)) {
                this.render();
            }
        };
        this.canvas.dataset.gameRendering = 'running';
        this.engine.runRenderLoop(this.loop);
```

Remove the three listeners in the class's dispose method. View updates (`applyUpdate`) keep running while paused, since they come from Blazor, not the frame loop. Add `setQuality(choice)` to the class: `writeQuality(choice); return this.renderer.setQuality(choice).choice;` and a `getQuality()` returning `readQuality()`, and expose both through the module functions the Blazor side calls (follow how `GameViewConnection` or `GamePage.razor.cs` invokes the JS instance today).

- [ ] **Step 3: The dropdown**

In `GamePage.razor`, in the panel under the position section, add a `MudSelect<string>` labeled "Quality" with items Auto, Low, Medium, High (`data-testid="game-quality"`), bound to a field initialized from `getQuality()` after the JS module starts, and calling `setQuality` on change. Keep the panel's styling (read the voice buttons section for the markup conventions).

- [ ] **Step 4: Manual check**

Restart the app, open the page, switch each preset and watch `data-game-rendering` and the frame rate (the FPS readout at the top left exists already). Hide the tab and check `data-game-rendering="paused"` via Playwright.

- [ ] **Step 5: Commit and push**

```bash
git add src/HomeBlaze/HomeBlaze.Game
git commit -m "feat: add quality presets to the game and stop rendering in the background

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

---

### Task 9: E2E tests, docs and the pull request

**Files:**
- Modify: `HomeBlaze.Game.Tests/E2E/GamePageTests.cs`
- Modify: `HomeBlaze.Game/README.md`, `HomeBlaze.Game.Abstractions/README.md`
- Delete: `src/HomeBlaze/docs/superpowers/specs/2026-11-02-game-style-design.md`, `src/HomeBlaze/docs/superpowers/plans/2026-11-02-game-style.md` (folded into the READMEs)

- [ ] **Step 1: Write the E2E tests**

Add to `GamePageTests.cs`, using the existing `GameWorld` helper:

```csharp
    [Fact]
    public async Task WhenTheQualityIsChanged_ThenItStaysAfterAReload()
    {
        // Arrange
        await using var world = await GameWorld.CreateAsync(_fixture);
        var (page, _) = await world.JoinAsync();

        // Act
        await page.GetByTestId("game-quality").ClickAsync();
        await page.GetByRole(AriaRole.Option, new() { Name = "Low" }).ClickAsync();
        await page.ReloadAsync();

        // Assert
        await Expect(page.GetByTestId("game-quality")).ToContainTextAsync("Low", new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenAPlayerEditsItsPersonAndColor_ThenTheyStayAfterAReload()
    {
        // Arrange
        await using var world = await GameWorld.CreateAsync(_fixture);
        var (page, player) = await world.JoinAsync();

        // Act
        await page.GetByTestId("game-rename").ClickAsync();
        await page.GetByTestId("game-person-female-tank-top").ClickAsync();
        await page.GetByTestId("game-player-save").ClickAsync();
        await AsyncTestHelpers.WaitUntilAsync(() => player.Character == "female-tank-top");
        await page.ReloadAsync();

        // Assert
        await page.GetByTestId("game-rename").ClickAsync();
        await Expect(page.GetByTestId("game-person-female-tank-top")).ToHaveClassAsync(new Regex("selected"), new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenAPlayerClaps_ThenAnotherPageSeesTheAnimation()
    {
        // Arrange
        await using var world = await GameWorld.CreateAsync(_fixture);
        var (clapper, _) = await world.JoinAsync();
        var (watcher, _) = await world.JoinAsync();
        var watcherCanvas = watcher.GetByTestId("game-canvas");

        // Act
        await clapper.GetByTestId("game-action").Filter(new() { HasText = "Clap" }).ClickAsync();

        // Assert
        await Expect(watcherCanvas).ToHaveAttributeAsync("data-game-emotes", new Regex("^[1-9]"), new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenTheTabIsHidden_ThenRenderingPauses()
    {
        // Arrange
        await using var world = await GameWorld.CreateAsync(_fixture);
        var (page, _) = await world.JoinAsync();
        var canvas = page.GetByTestId("game-canvas");
        await Expect(canvas).ToHaveAttributeAsync("data-game-rendering", "running", new() { Timeout = Timeout });

        // Act
        await page.EvaluateAsync("() => { Object.defineProperty(document, 'hidden', { value: true, configurable: true }); document.dispatchEvent(new Event('visibilitychange')); }");

        // Assert
        await Expect(canvas).ToHaveAttributeAsync("data-game-rendering", "paused", new() { Timeout = Timeout });
    }
```

Check the actual test ids for the pencil (`game-rename` or similar) and the panel's action buttons in `GamePage.razor` and adjust. The clapper and the watcher must be within the view radius of each other; `JoinAsync` places players near each other already (other E2E tests rely on that).

- [ ] **Step 2: Run all E2E tests**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Game.Tests -c Debug --filter "Category=Integration"`
Expected: PASS (25 tests).

- [ ] **Step 3: Update the READMEs**

`HomeBlaze.Game/README.md`: a "Look" section (style, palette, shadows, outlines, corner shading, what stays out of the geometry buffer and why), "Quality" (presets table, Auto, storage), "Rendering pause", "People" (names, defaults, conversion script, licenses), "Emotes" (`PlayAnimation`, `GamePlayer.Animation`, the 5 s limit, the emote tool), and the asset scripts under `tools/`. Update the asset and license list (Casual_2 out, people and drawn textures in). Replace mentions of blob shadows and photo textures.

`HomeBlaze.Game.Abstractions/README.md`: document `PlayAnimation` with the emote tool as the example of a custom emote, and add a short "Look" note for object authors (flat pastel colors or drawn textures; outlines and shading come from the core; avoid photo textures).

Delete the spec and plan files.

- [ ] **Step 4: Full verification**

Run, all in Debug: `dotnet build src/Namotion.Interceptor.slnx -c Debug` (0 errors), and the test projects `HomeBlaze.Game.Tests` (unit and E2E), `HomeBlaze.Game.Buildings.Tests`, `HomeBlaze.Game.Nature.Tests`, `HomeBlaze.Game.Abstractions.Tests`, `HomeBlaze.Components.Tests`, `HomeBlaze.Services.Tests`. Record counts.

- [ ] **Step 5: Commit, push and open the draft PR**

Commit the tests and docs. Take before (`feat/homeblaze-game`) and after screenshots of the demo house and forest. Write the PR body from `.github/pull_request_template.md` (summary, Contract, Breaking changes: `Casual_2` and the photo textures are removed, `GameViewPayload` gains fields, `IGameWorld` gains `PlayAnimation` so implementers outside the core must add it; Diff composition from `pwsh scripts/diff-composition.ps1 -Range "feat/homeblaze-game...HEAD"`; Verification with counts), then:

```bash
gh pr create --draft --base feat/homeblaze-game --head feat/homeblaze-game-style \
  --title "feat: give the HomeBlaze game one look, choosable people and emotes" \
  --label "area: homeblaze" --label "type: enhancement" --body-file <body file>
```
