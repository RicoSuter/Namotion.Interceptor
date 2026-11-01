# Game Simulation Redesign Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move the HomeBlaze game core to "the simulation owns the state, subjects are the facade", split the game into an abstractions package, the core and two extension libraries, and prove 200 players and 100k objects with a benchmark.

**Architecture:** One `GameSimulation` per game runs a fixed 50 ms tick on plain state (`GameEntity`, `PlayerEntity`, a chunk index), fed by a lock-free queue of actions. Subjects are published from it every 250 ms; outside writes are reported to it by a write interceptor that never waits. Object types live in `HomeBlaze.Game.Buildings` and `HomeBlaze.Game.Nature` and use only `HomeBlaze.Game.Abstractions` (`GameObject`, `GameTool`, `IGameWorld`, visuals).

**Tech Stack:** .NET 10, C# 14, Namotion.Interceptor (`[InterceptorSubject]`, `IWriteInterceptor`, registry, parent tracking), Blazor Server, MudBlazor, Babylon.js, xUnit, `Microsoft.Extensions.TimeProvider.Testing`, Playwright, BenchmarkDotNet, PublicApiGenerator + Verify.

**Spec:** `src/HomeBlaze/docs/superpowers/specs/2026-11-01-game-simulation-design.md`. Read it before any task.

---

## Ground rules for every task

- Branch `feat/homeblaze-game-simulation`. Push after every task: `git push -u origin feat/homeblaze-game-simulation`.
- Build and test only with `-c Debug`; a Release build would replace the binaries of the app the user is testing.
  - Build: `dotnet build src/HomeBlaze/HomeBlaze.Game.Tests -c Debug`
  - Unit tests: `dotnet test src/HomeBlaze/HomeBlaze.Game.Tests -c Debug --filter "Category!=Integration"`
  - E2E tests: `dotnet test src/HomeBlaze/HomeBlaze.Game.Tests -c Debug --filter "Category=Integration"`
  - From Task 3 on also the new test projects (`HomeBlaze.Game.Buildings.Tests`, `HomeBlaze.Game.Nature.Tests`, `HomeBlaze.Game.Abstractions.Tests`).
- Never stage anything under `src/HomeBlaze/HomeBlaze/Data` except the generated demo world `src/HomeBlaze/HomeBlaze/Data/Files/Demo/Game.json`, and stage that one only through the index: `git update-index --cacheinfo 100644,$(git hash-object -w <generated file>),src/HomeBlaze/HomeBlaze/Data/Files/Demo/Game.json`, because the running app rewrites the working copy. `Installation.json` stays local.
- No HomeBlaze mentions outside `src/HomeBlaze`. US English, no em dashes, no hard wrapping in markdown, comments only for the why a reader cannot derive, XML docs state the contract.
- Tests: `When<Condition>_Then<Expected>`, `// Arrange`, `// Act`, `// Assert`, no `Task.Delay`/`Thread.Sleep`; advance the fake clock and run ticks instead.
- Commit messages end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- External assets must be free (CC0 preferred) with a `LICENSE.md` next to them.
- After a task that leaves the app runnable, the controller restarts the app (HTTP 5280, HTTPS 5281): stop it with `pkill -f "[u]rls http://192.168.1.201"` in its own call, then `git checkout -- src/HomeBlaze/HomeBlaze/Data/Files/Demo/Game.json`, then start it again in Release as before.

## File structure (end state)

```
src/HomeBlaze/
├── HomeBlaze.Game.Abstractions/            # the extension contract, namespace HomeBlaze.Game.Abstractions
│   ├── GameObject.cs  GameTool.cs  IGameWorld.cs
│   ├── GameArea.cs  GameSpot.cs  GameCaller.cs  GameDirection.cs
│   ├── Visuals/GameVisual.cs GameVisualPart.cs GameVisualShape.cs GameVisualEffect.cs
│   │           GameVisualAnimationMode.cs GameVisualVector.cs GameVisualPoint.cs GameVisualCache.cs GameAssets.cs
│   └── README.md                            # the author guide
├── HomeBlaze.Game.Abstractions.Tests/       # VerifyChecksTests.PublicApi
├── HomeBlaze.Game/
│   ├── Game.cs Game.World.cs Game.Players.cs Game.Persistence.cs Game.Voice.cs
│   ├── GamePlayer.cs  GameSimulationSettings.cs
│   ├── Simulation/ GameSimulation.cs GameEntity.cs PlayerEntity.cs GameWorldState.cs GameSchedules.cs
│   │               GameWriteInterceptor.cs OccupancySnapshot.cs GameViewSession.cs GamePublisher.cs
│   ├── World/      GameWorldIndex.cs ChangedChunks.cs FrontPlacement.cs HearingRange.cs SoundPathSearch.cs
│   │               SpawnFinder.cs TargetArea.cs RateLimiter.cs
│   ├── View/       GameViewTracker.cs GameViewObject.cs GameViewPart.cs GameViewUpdate.cs GameColors.cs GameVisualValidator.cs
│   ├── Voice/      (unchanged files) GameVolumeTable.cs
│   ├── Clients/    (unchanged)
│   └── Components/ GamePage.razor GamePage.razor.cs GameViewConnection.cs GameVoiceConnection.cs GameNameDialog.razor
├── HomeBlaze.Game.Buildings/   Wall.cs Door.cs WallTool.cs WallMaterial.cs WallTextures.cs wwwroot/objects/buildings/textures/*
├── HomeBlaze.Game.Buildings.Tests/
├── HomeBlaze.Game.Nature/      Tree.cs TreeTool.cs TreeVariant.cs Animal.cs AnimalVariant.cs wwwroot/objects/nature/models/*
├── HomeBlaze.Game.Nature.Tests/
└── HomeBlaze.Game.Benchmark/   Program.cs SimulationTickBenchmark.cs BenchmarkWorld.cs
```

Asset paths inside the extension libraries stay as they are today (`wwwroot/objects/buildings/textures/...`, `wwwroot/objects/nature/models/...`), so only the assembly part of their URLs changes.

---

### Task 1: The abstractions project with the value types

Moves the types every extension needs and that do not depend on the game into `HomeBlaze.Game.Abstractions`. No behavior changes.

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Game.Abstractions/HomeBlaze.Game.Abstractions.csproj`
- Move (git mv, then change the namespace to `HomeBlaze.Game.Abstractions`): `HomeBlaze.Game/GameDirection.cs`, `HomeBlaze.Game/Visuals/{GameVisual,GameVisualPart,GameVisualShape,GameVisualEffect,GameVisualVector,GameVisualPoint,GameVisualCache,GameAssets}.cs`
- Create: `HomeBlaze.Game.Abstractions/GameArea.cs` (replaces `HomeBlaze.Game/World/Footprint.cs`, which is deleted), `GameSpot.cs` (replaces `HomeBlaze.Game/Tools/GamePlacement.cs`, deleted), `Visuals/GameVisualAnimationMode.cs`
- Move: `HomeBlaze.Game/Visuals/GameVisualValidator.cs` to `HomeBlaze.Game/View/GameVisualValidator.cs` (stays internal in the core)
- Create: `src/HomeBlaze/HomeBlaze.Game.Abstractions.Tests/HomeBlaze.Game.Abstractions.Tests.csproj`, `VerifyChecksTests.cs`, `VerifyChecksTests.PublicApi.verified.txt`
- Modify: `src/Namotion.Interceptor.slnx` (add both projects next to the game projects), `HomeBlaze.Game/HomeBlaze.Game.csproj` (reference the abstractions), all users of the moved types (`using HomeBlaze.Game.Abstractions;`), `HomeBlaze.Game.Tests/HomeBlaze.Game.Tests.csproj` (global `<Using Include="HomeBlaze.Game.Abstractions" />`, drop `HomeBlaze.Game.Visuals`)
- Move tests: `HomeBlaze.Game.Tests/World/FootprintTests.cs` becomes `GameAreaTests.cs` (rename `Footprint` to `GameArea` inside)

- [ ] **Step 1: Create the project**

`HomeBlaze.Game.Abstractions.csproj`, modeled on `HomeBlaze.History.Abstractions.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

    <ItemGroup>
        <InternalsVisibleTo Include="HomeBlaze.Game.Abstractions.Tests" />
    </ItemGroup>

    <ItemGroup>
        <ProjectReference Include="..\HomeBlaze.Abstractions\HomeBlaze.Abstractions.csproj" />
        <ProjectReference Include="..\..\Namotion.Interceptor.Generator\Namotion.Interceptor.Generator.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
        <ProjectReference Include="..\..\Namotion.Interceptor\Namotion.Interceptor.csproj" />
        <ProjectReference Include="..\..\Namotion.Interceptor.Tracking\Namotion.Interceptor.Tracking.csproj" />
        <PackageReference Include="SonarAnalyzer.CSharp" PrivateAssets="all" />
    </ItemGroup>

</Project>
```

Drop the `InternalsVisibleTo` if nothing internal remains to test; the public API test needs none.

- [ ] **Step 2: Move the value types**

Run from `src/HomeBlaze`:

```bash
mkdir -p HomeBlaze.Game.Abstractions/Visuals
git mv HomeBlaze.Game/GameDirection.cs HomeBlaze.Game.Abstractions/GameDirection.cs
for f in GameVisual GameVisualPart GameVisualShape GameVisualEffect GameVisualVector GameVisualPoint GameVisualCache GameAssets; do git mv HomeBlaze.Game/Visuals/$f.cs HomeBlaze.Game.Abstractions/Visuals/$f.cs; done
git mv HomeBlaze.Game/Visuals/GameVisualValidator.cs HomeBlaze.Game/View/GameVisualValidator.cs
```

Set `namespace HomeBlaze.Game.Abstractions;` in all moved files (the validator gets `HomeBlaze.Game.View`). `GameDirectionExtensions` becomes `public static class` with `GetOffsetX`, `GetOffsetY`, `IsDiagonal`; `GetShortName` is only used by the page, so move it into a new internal `HomeBlaze.Game/View/GameDirectionNames.cs` as an internal extension method.

- [ ] **Step 3: Write `GameArea`, replacing `Footprint`**

```csharp
namespace HomeBlaze.Game.Abstractions;

/// <summary>
/// The tiles from <see cref="X"/> to <see cref="Right"/> and from <see cref="Y"/> to <see cref="Top"/>; a tile is 25 cm,
/// X grows to the east and Y to the north.
/// </summary>
public readonly record struct GameArea(int X, int Y, int Width, int Depth)
{
    public long Right => (long)X + Width - 1;

    public long Top => (long)Y + Depth - 1;

    public bool Intersects(GameArea other) =>
        X <= other.Right && other.X <= Right && Y <= other.Top && other.Y <= Top;

    public bool Contains(long x, long y) => x >= X && x <= Right && y >= Y && y <= Top;

    /// <summary>
    /// Moves the area by the offset; returns false when a tile of the result would leave the <see cref="int"/> range.
    /// </summary>
    public bool TryMove(int offsetX, int offsetY, out GameArea moved)
    {
        var x = (long)X + offsetX;
        var y = (long)Y + offsetY;
        if (x < int.MinValue || y < int.MinValue || x + Width - 1 > int.MaxValue || y + Depth - 1 > int.MaxValue)
        {
            moved = default;
            return false;
        }

        moved = this with { X = (int)x, Y = (int)y };
        return true;
    }
}
```

Delete `World/Footprint.cs` and replace every `Footprint` in the core and its tests with `GameArea` (`grep -rl "Footprint" HomeBlaze.Game HomeBlaze.Game.Tests`); keep member names such as `GameObject.Footprint` for now, Task 2 renames them to `Area`.

- [ ] **Step 4: Write `GameSpot`, replacing `GamePlacement`**

```csharp
namespace HomeBlaze.Game.Abstractions;

/// <summary>
/// Where an object fits right in front of a player: the south-west corner of its area, its size in tiles along X (width)
/// and Y (depth), and the direction the player faces.
/// </summary>
public readonly record struct GameSpot(int X, int Y, int Width, int Depth, GameDirection Facing)
{
    public GameArea Area => new(X, Y, Width, Depth);
}
```

Delete `Tools/GamePlacement.cs`; `Game.GetPlacementInFront` returns `GameSpot` for now (renamed in Task 2).

- [ ] **Step 5: Add the animation mode (C# only; the browser follows in Task 11)**

`Visuals/GameVisualAnimationMode.cs`:

```csharp
namespace HomeBlaze.Game.Abstractions;

/// <summary>
/// How the animation of a model part plays.
/// </summary>
public enum GameVisualAnimationMode
{
    /// <summary>
    /// Plays the animation in a loop.
    /// </summary>
    Loop,

    /// <summary>
    /// Plays the animation once whenever the part's animation changes and holds its last frame.
    /// </summary>
    Once
}
```

In `GameVisualPart` add after `Animation`:

```csharp
    /// <summary>
    /// How <see cref="Animation"/> plays; ignored without an animation.
    /// </summary>
    public GameVisualAnimationMode AnimationMode { get; init; }
```

In `HomeBlaze.Game/View/GameViewPart.cs` carry it to the browser as `int AnimationMode` next to `Animation`, and add a test to `View/GameViewTrackerTests.cs` (or the existing `GameViewPart` test file if one exists) that a part with `AnimationMode = Once` produces a view part with `AnimationMode == 1`.

- [ ] **Step 6: Public API snapshot**

`HomeBlaze.Game.Abstractions.Tests.csproj`: copy `HomeBlaze.History.Abstractions.Tests` (package references `PublicApiGenerator`, `Verify.Xunit`, xunit, test SDK; project reference to `HomeBlaze.Game.Abstractions`). `VerifyChecksTests.cs`, following the file of that project:

```csharp
using PublicApiGenerator;

namespace HomeBlaze.Game.Abstractions.Tests;

public class VerifyChecksTests
{
    [Fact]
    public Task PublicApi()
    {
        // Arrange
        var assembly = typeof(GameArea).Assembly;

        // Act
        var publicApi = assembly.GeneratePublicApi();

        // Assert
        return Verify(publicApi);
    }
}
```

Run `dotnet test src/HomeBlaze/HomeBlaze.Game.Abstractions.Tests -c Debug`, inspect the received file, and accept it by renaming `.received.txt` to `.verified.txt`.

- [ ] **Step 7: Build and run the tests**

Run: `dotnet build src/HomeBlaze/HomeBlaze.Game.Tests -c Debug` and the unit tests of `HomeBlaze.Game.Tests` and `HomeBlaze.Game.Abstractions.Tests`.
Expected: build succeeds with warnings as errors, all tests pass (the count of `HomeBlaze.Game.Tests` is unchanged, 407 plus the new animation mode test).

- [ ] **Step 8: Commit and push**

```bash
git add -A src/HomeBlaze/HomeBlaze.Game.Abstractions src/HomeBlaze/HomeBlaze.Game.Abstractions.Tests src/HomeBlaze/HomeBlaze.Game src/HomeBlaze/HomeBlaze.Game.Tests src/Namotion.Interceptor.slnx
git commit -m "refactor: move the game's value types into HomeBlaze.Game.Abstractions"
git push -u origin feat/homeblaze-game-simulation
```

---

### Task 2: `GameObject`, `GameTool` and `IGameWorld` in the abstractions

Moves the base classes into the abstractions and gives object types the final API (`World`, direct writes, `IsFree`), still on today's lock-based core. `Schedule` and `OnAddedToWorld` follow in Task 7.

**Files:**
- Move: `HomeBlaze.Game/GameObject.cs` to `HomeBlaze.Game.Abstractions/GameObject.cs`; `HomeBlaze.Game/Tools/GameTool.cs` to `HomeBlaze.Game.Abstractions/GameTool.cs`; `HomeBlaze.Game/GameCaller.cs` to `HomeBlaze.Game.Abstractions/GameCaller.cs`
- Create: `HomeBlaze.Game.Abstractions/IGameWorld.cs`
- Delete: `HomeBlaze.Game/GameObjectSnapshot.cs`
- Modify: `HomeBlaze.Game/Game.cs` (implements `IGameWorld`), `Game.Objects.cs`, `Game.Placement.cs`, `Game.Actions.cs`, `GamePlayer.cs`, `Components/GameCallerServiceProvider.cs`, `Components/GamePage.razor`, all object types in `Objects/`, `World/GameWorldIndex.cs`, `View/GameViewTracker.cs`, `View/GameVisualValidator.cs`
- Tests: `GameObjectsTests.cs`, `GamePlacementTests.cs`, `GameActionsTests.cs`, `GameCallerServiceProviderTests.cs`, `Objects/**`, `Testing/*`

- [ ] **Step 1: Write `IGameWorld`**

```csharp
namespace HomeBlaze.Game.Abstractions;

/// <summary>
/// What an object or tool may ask of the game it is part of; reached through <see cref="GameObject.World"/> and
/// <see cref="GameTool.World"/>.
/// </summary>
public interface IGameWorld
{
    /// <summary>
    /// Gets the area right in front of the caller's player, <paramref name="across"/> tiles across its facing direction
    /// and <paramref name="along"/> tiles along it, centered on the player across the facing (an odd remainder goes east
    /// or north). Does not check whether the area is free.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A size is outside 1 to <see cref="GameObject.MaximumSize"/>.</exception>
    /// <exception cref="InvalidOperationException">The caller is not a player of this game, faces diagonally, or the area
    /// would leave the <see cref="int"/> range.</exception>
    GameSpot GetSpotInFront(GameCaller? caller, int across, int along);

    /// <summary>
    /// Whether no movement blocking object other than <paramref name="except"/> overlaps the area. Callable from any
    /// thread; outside the game's own processing it answers from the state of the last tick.
    /// </summary>
    bool IsFree(GameArea area, GameObject? except = null);

    /// <summary>
    /// Adds the object under a name of its type name in lowercase, a dash and 8 random hex digits.
    /// Canceling only stops the save; the object stays placed.
    /// </summary>
    /// <exception cref="ArgumentException">The object is already part of this game, or its size is outside 1 to
    /// <see cref="GameObject.MaximumSize"/>.</exception>
    /// <exception cref="InvalidOperationException">The object blocks movement and overlaps a movement blocking object.</exception>
    Task PlaceAsync(GameObject gameObject, CancellationToken cancellationToken);

    /// <summary>
    /// Removes the object. Canceling only stops the save; the object stays removed.
    /// </summary>
    /// <exception cref="InvalidOperationException">The object is not part of this game.</exception>
    Task RemoveAsync(GameObject gameObject, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the object by the replacements in one step; no player sees an intermediate state. Canceling only stops
    /// the save; the replacement stays applied.
    /// </summary>
    /// <exception cref="ArgumentException">A replacement is listed twice, already part of this game, or of invalid size.</exception>
    /// <exception cref="InvalidOperationException">The object is not part of this game, or a movement blocking replacement
    /// overlaps a movement blocking object other than the replaced one or another replacement; nothing changes then.</exception>
    Task ReplaceAsync(GameObject gameObject, IReadOnlyList<GameObject> replacements, CancellationToken cancellationToken);
}
```

The message texts stay those of today: `"Only a player in the game can place objects."`, `"Face north, east, south or west to place objects."`, `"Something is in the way."`, `"The object is not part of this game."`. Add a public static class `GameMessages` in the abstractions with `public const string Blocked = "Something is in the way.";` so object types throw the same text as the core (`throw new InvalidOperationException(GameMessages.Blocked)`); the core's `BlockedMessage` uses it.

- [ ] **Step 2: Rework `GameObject` in the abstractions**

Keep everything of today's `GameObject` except: namespace `HomeBlaze.Game.Abstractions`; `internal Footprint Footprint` becomes `public GameArea Area => new(X, Y, Width, Depth);`; `internal DefaultVisual` becomes `public GameVisual DefaultVisual` (the core's validator falls back to it; it stays cached); `internal Game? ParentGame` and `protected Game GetGame()` are replaced by:

```csharp
    /// <summary>
    /// The game this object is part of.
    /// </summary>
    /// <exception cref="InvalidOperationException">The object is not part of a game.</exception>
    protected IGameWorld World =>
        this.TryGetFirstParent<IGameWorld>() ?? throw new InvalidOperationException("The object is not part of a game.");
```

`ApplyConfigurationAsync` becomes `public virtual Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;` (the core already rebuilds its index on every outside write through `OnPropertyChanged`, and from Task 6 on the write interceptor reports them).

`GameTool` gets the same `protected IGameWorld World` (message "The tool is not part of a game.") instead of `GetGame()`.

- [ ] **Step 3: Make `GameCaller` independent of `GamePlayer`**

```csharp
namespace HomeBlaze.Game.Abstractions;

/// <summary>
/// Who invokes an operation of a game object or tool; operations take it as a nullable <c>[FromServices]</c> parameter,
/// which only the game page provides. Null means the operation runs without a player, for example from MCP or an agent.
/// </summary>
public sealed class GameCaller
{
    public GameCaller(string clientId)
    {
        ClientId = clientId;
    }

    /// <summary>
    /// The id of the browser client whose page invokes the operation.
    /// </summary>
    public string ClientId { get; }
}
```

The core resolves the player by `ClientId` (`Players.TryGetValue(caller.ClientId, ...)` and `IsConnected`); `GameCallerServiceProvider` and `GamePage` create `new GameCaller(player.ClientId)`. `Game.ResolveActor` becomes `internal GamePlayer? GetActor(GameCaller? caller)`; `GamePlayer.SendMessageAsync` uses it.

- [ ] **Step 4: Implement `IGameWorld` on `Game`**

`public partial class Game : BackgroundService, IConfigurable, ITitleProvider, IIconProvider, IGameWorld`.
- `GetPlacementInFront` becomes `public GameSpot GetSpotInFront(GameCaller? caller, int across, int along)` with today's body.
- `public bool IsFree(GameArea area, GameObject? except = null)`: under `_lock`, `!GetIndex().IsBlocked(area, except)`.
- `PlaceAsync(GameObject gameObject, CancellationToken)`: today's body without the caller check and with the name prefix `gameObject.GetType().Name.ToLowerInvariant()`.
- `ReplaceAsync(GameObject, IReadOnlyList<GameObject>, CancellationToken)`: today's body with the same prefix per replacement.
- Delete `UpdateAsync`, `ApplyChange` and `GameObjectSnapshot.cs`.
- Rename `Footprint` members to `Area` throughout the core (`GameWorldIndex`, `TargetArea`, `SpawnFinder`, `HearingRange`, `FrontPlacement`, view tracker).

- [ ] **Step 5: Port the object types to the new API**

In `Objects/Buildings` and `Objects/Nature`:
- `GetGame().RemoveAsync(this, ct)` becomes `World.RemoveAsync(this, ct)`.
- Tools: `var spot = World.GetSpotInFront(caller, across, along);` and `World.PlaceAsync(gameObject, ct)`.
- `Wall.AddDoorAsync`: `var spot = caller is null ? (GameSpot?)null : World.GetSpotInFront(caller, doorWidth, 1);` and center the door on `2L * (isAlongX ? spot.X : spot.Y) + doorWidth` (doubled center of the spot) when there is a spot, on the middle of the wall otherwise; the clamping stays. `ReplaceAsync(this, [wall, door, wall], ct)`.
- `Door.OpenAsync` becomes `public void Open()`: throws "The door is already open." when open, else `IsOpen = true`.
- `Door.CloseAsync` becomes `public void Close()`: throws "The door is already closed." when closed; throws `new InvalidOperationException(GameMessages.Blocked)` when `!World.IsFree(Area, this)`; else `IsOpen = false`.

- [ ] **Step 6: Adapt the tests**

- `GameTestContext`: no API change needed yet; it keeps `RequestIndexRebuild`.
- Rewrite the tests of `UpdateAsync` in `GameObjectsTests.cs` as tests of the door operations and of direct writes: a direct write of `Wall.X` from outside is picked up by the index (existing outside change tests cover it).
- Add to `Objects/Buildings/DoorTests.cs`:

```csharp
    [Fact]
    public void WhenAPlayerStandsInTheDoorway_ThenCloseIsRefusedAndTheDoorStaysOpen()
    {
        // Arrange
        var door = _context.AddDoor(0, 4, isOpen: true);
        _context.AddConnectedPlayer("tab-1", x: 1, y: 3);

        // Act
        var exception = Assert.Throws<InvalidOperationException>(door.Close);

        // Assert
        Assert.Equal("Something is in the way.", exception.Message);
        Assert.True(door.IsOpen);
    }
```

(Pick coordinates so the player's 2 by 2 area overlaps the door's area; check `AddDoor`'s defaults.)
- `GamePlacementTests`: `PlaceAsync` no longer needs a caller; the "no actor" refusal moves to `GetSpotInFront`.
- Add to `GamePlacementTests`: `WhenPlacingATree_ThenItsNameStartsWithTreeAndADash` asserting `Assert.Matches("^tree-[0-9a-f]{8}$", name)`.
- `WallDoorTests`: `AddDoor` without a caller centers on the wall; with a caller as today.

- [ ] **Step 7: Build, test, accept the public API snapshot, commit and push**

Run the build, `HomeBlaze.Game.Tests` unit tests and `HomeBlaze.Game.Abstractions.Tests`; accept the new snapshot. Then:

```bash
git add -A src/HomeBlaze
git reset -q src/HomeBlaze/HomeBlaze/Data
git commit -m "refactor: give game objects the IGameWorld API from the abstractions"
git push
```

---

### Task 3: `HomeBlaze.Game.Buildings` and `HomeBlaze.Game.Nature`

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Game.Buildings/HomeBlaze.Game.Buildings.csproj`, `src/HomeBlaze/HomeBlaze.Game.Nature/HomeBlaze.Game.Nature.csproj`, `src/HomeBlaze/HomeBlaze.Game.Buildings.Tests/HomeBlaze.Game.Buildings.Tests.csproj`, `src/HomeBlaze/HomeBlaze.Game.Nature.Tests/HomeBlaze.Game.Nature.Tests.csproj`
- Move: `HomeBlaze.Game/Objects/Buildings/*.cs` to `HomeBlaze.Game.Buildings/`, `HomeBlaze.Game/Objects/Nature/*.cs` to `HomeBlaze.Game.Nature/`, `HomeBlaze.Game/wwwroot/objects/buildings/**` to `HomeBlaze.Game.Buildings/wwwroot/objects/buildings/**`, `HomeBlaze.Game/wwwroot/objects/nature/**` to `HomeBlaze.Game.Nature/wwwroot/objects/nature/**`, `HomeBlaze.Game.Tests/Objects/Buildings/*` and `.../Nature/*` to the new test projects
- Modify: `src/Namotion.Interceptor.slnx`, `HomeBlaze/HomeBlaze.csproj`, `HomeBlaze/Program.cs`, `HomeBlaze.Game.Tests/HomeBlaze.Game.Tests.csproj`, `HomeBlaze.Game.Tests/DemoWorldTests.cs`, `HomeBlaze.Game/README.md` (asset table paths), the demo world generator in the scratchpad (`world/make_world.py`), `HomeBlaze/Data/Files/Demo/Game.json` (regenerated, staged via the index)

- [ ] **Step 1: Create the extension projects**

`HomeBlaze.Game.Buildings.csproj` (Nature the same with its name):

```xml
<Project Sdk="Microsoft.NET.Sdk.Razor">

    <ItemGroup>
        <ProjectReference Include="..\HomeBlaze.Abstractions\HomeBlaze.Abstractions.csproj" />
        <ProjectReference Include="..\HomeBlaze.Game.Abstractions\HomeBlaze.Game.Abstractions.csproj" />
        <ProjectReference Include="..\..\Namotion.Interceptor.Generator\Namotion.Interceptor.Generator.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
        <ProjectReference Include="..\..\Namotion.Interceptor\Namotion.Interceptor.csproj" />
        <PackageReference Include="SonarAnalyzer.CSharp" PrivateAssets="all" />
    </ItemGroup>

</Project>
```

The Razor SDK is only for the `wwwroot` static web assets; there are no components. If the Razor SDK warns about a missing `_Imports.razor`, ignore it; do not add components.

- [ ] **Step 2: Move the sources and assets**

```bash
cd src/HomeBlaze
mkdir -p HomeBlaze.Game.Buildings/wwwroot/objects HomeBlaze.Game.Nature/wwwroot/objects
for f in HomeBlaze.Game/Objects/Buildings/*.cs; do git mv "$f" HomeBlaze.Game.Buildings/; done
for f in HomeBlaze.Game/Objects/Nature/*.cs; do git mv "$f" HomeBlaze.Game.Nature/; done
git mv HomeBlaze.Game/wwwroot/objects/buildings HomeBlaze.Game.Buildings/wwwroot/objects/buildings
git mv HomeBlaze.Game/wwwroot/objects/nature HomeBlaze.Game.Nature/wwwroot/objects/nature
```

Namespaces become `HomeBlaze.Game.Buildings` and `HomeBlaze.Game.Nature`. The types must compile against the abstractions only; any compile error naming a core type (`Game`, `GamePlayer`, internals) means the type still uses the core and must use `World` instead. `GameAssets.Url(typeof(Tree), ...)` already yields `_content/HomeBlaze.Game.Nature/...`.

- [ ] **Step 3: Register the libraries in the host**

`HomeBlaze/HomeBlaze.csproj`: add project references to both libraries. `Program.cs` after the `HomeBlaze.Game` line:

```csharp
    .AddAssembly(typeof(HomeBlaze.Game.Buildings.Wall).Assembly)                            // HomeBlaze.Game.Buildings
    .AddAssembly(typeof(HomeBlaze.Game.Nature.Tree).Assembly)                               // HomeBlaze.Game.Nature
```

- [ ] **Step 4: Create the extension test projects and move the tests**

Each test project references its library and `HomeBlaze.Game` (to run against a real game) and copies the package references of `HomeBlaze.Game.Tests` without Playwright. The tests need `GameTestContext` and friends: move `HomeBlaze.Game.Tests/Testing/{GameTestContext,FakeClientDirectory,TestRoot,FrozenTimeProvider}.cs` into a new shared folder `src/HomeBlaze/HomeBlaze.Game.Tests/Testing/` that the extension test projects link with `<Compile Include="..\HomeBlaze.Game.Tests\Testing\GameTestContext.cs" Link="Testing\GameTestContext.cs" />` (one line per file). `GameTestContext` keeps `AddWall` and `AddDoor`, so every project that links it also references `HomeBlaze.Game.Buildings` (the Nature tests too; a test project may reference more than the library it tests). `InternalsVisibleTo` for the extension test projects is added to `HomeBlaze.Game.csproj`, because the shared context calls internal members (`ReconcilePlayers`, the internal constructor); this follows the existing exception for test projects.

```bash
mkdir -p HomeBlaze.Game.Buildings.Tests HomeBlaze.Game.Nature.Tests
for f in HomeBlaze.Game.Tests/Objects/Buildings/*.cs; do git mv "$f" HomeBlaze.Game.Buildings.Tests/; done
for f in HomeBlaze.Game.Tests/Objects/Nature/*.cs; do git mv "$f" HomeBlaze.Game.Nature.Tests/; done
```

- [ ] **Step 5: Keep the core tests on the extension types**

`HomeBlaze.Game.Tests` references both extension projects (its movement, hearing and E2E tests use walls, doors and trees, and `DemoWorldTests` loads the demo world). Global usings: `HomeBlaze.Game.Buildings`, `HomeBlaze.Game.Nature`.

- [ ] **Step 6: Regenerate the demo world**

In the scratchpad generator `world/make_world.py`, change the `$type` strings to `HomeBlaze.Game.Buildings.Wall, HomeBlaze.Game.Buildings`, `HomeBlaze.Game.Buildings.Door, HomeBlaze.Game.Buildings`, `HomeBlaze.Game.Nature.Tree, HomeBlaze.Game.Nature`, and the tools to `HomeBlaze.Game.Buildings.WallTool, HomeBlaze.Game.Buildings` and `HomeBlaze.Game.Nature.TreeTool, HomeBlaze.Game.Nature` (check the exact `$type` format in the current `Game.json` first and keep it). Run it into the scratchpad, then stage it via the index (see ground rules). `DemoWorldTests` must load it and find 20 walls, 7 doors, 39 trees.

- [ ] **Step 7: Build, run all game tests including E2E, commit and push**

Run the build, the unit tests of all four game test projects, and the E2E tests of `HomeBlaze.Game.Tests` (20 tests; the E2E server must serve `_content/HomeBlaze.Game.Nature/...` models, which the existing `data-game-model-failures` assertions check).

```bash
git add -A src/HomeBlaze/HomeBlaze.Game* src/HomeBlaze/HomeBlaze/HomeBlaze.csproj src/HomeBlaze/HomeBlaze/Program.cs src/Namotion.Interceptor.slnx
git update-index --cacheinfo 100644,$(git hash-object -w <scratchpad>/world/Game.json),src/HomeBlaze/HomeBlaze/Data/Files/Demo/Game.json
git commit -m "refactor: move walls, doors and trees into HomeBlaze.Game.Buildings and HomeBlaze.Game.Nature"
git push
```

Controller: restart the app.

---

### Task 4: The simulation core

A standalone `GameSimulation` with the action queue, the tick, schedules and failure logging, unit-tested without a world. Nothing uses it yet.

**Files:**
- Create: `HomeBlaze.Game/GameSimulationSettings.cs`, `HomeBlaze.Game/Simulation/GameSimulation.cs`, `HomeBlaze.Game/Simulation/GameSchedules.cs`
- Test: `HomeBlaze.Game.Tests/Simulation/GameSimulationTests.cs`, `HomeBlaze.Game.Tests/Simulation/GameSchedulesTests.cs`

- [ ] **Step 1: Write the settings**

```csharp
namespace HomeBlaze.Game;

/// <summary>
/// The intervals of the game simulation; fixed in code for now, like <see cref="Voice.GameVoiceSettings"/>.
/// </summary>
internal static class GameSimulationSettings
{
    /// <summary>
    /// The time between two ticks of the simulation.
    /// </summary>
    public static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// The shortest time between two publishes of the simulation's state to the subjects.
    /// </summary>
    public static readonly TimeSpan PublishInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// The shortest time between two saves of a changed world.
    /// </summary>
    public static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(10);
}
```

`GameVoiceSettings.VolumeTableInterval` (100 ms) stays where it is.

- [ ] **Step 2: Write the failing tests of the core**

`GameSimulationTests.cs`:

```csharp
using HomeBlaze.Game.Simulation;
using HomeBlaze.Game.Tests.Testing;
using Microsoft.Extensions.Time.Testing;

namespace HomeBlaze.Game.Tests.Simulation;

public class GameSimulationTests
{
    private readonly FakeTimeProvider _timeProvider = new();
    private readonly RecordingLogger _logger = new();

    private GameSimulation CreateSimulation() => new(_timeProvider, _logger);

    [Fact]
    public async Task WhenAnActionIsInvoked_ThenItRunsInTheNextTickOnTheSimulationThread()
    {
        // Arrange
        var simulation = CreateSimulation();
        var wasOnSimulation = false;
        var task = simulation.InvokeAsync(() => wasOnSimulation = GameSimulation.Current == simulation, CancellationToken.None);

        // Act
        Assert.False(task.IsCompleted);
        simulation.RunTick();

        // Assert
        Assert.True(await task);
        Assert.True(wasOnSimulation);
        Assert.Null(GameSimulation.Current);
    }

    [Fact]
    public void WhenActionsArePosted_ThenTheyRunInOrder()
    {
        // Arrange
        var simulation = CreateSimulation();
        var order = new List<int>();
        simulation.Post(() => order.Add(1));
        simulation.Post(() => order.Add(2));

        // Act
        simulation.RunTick();

        // Assert
        Assert.Equal([1, 2], order);
    }

    [Fact]
    public async Task WhenAnActionThrows_ThenOnlyItsTaskFailsAndLaterActionsRun()
    {
        // Arrange
        var simulation = CreateSimulation();
        var failing = simulation.InvokeAsync<int>(() => throw new InvalidOperationException("broken"), CancellationToken.None);
        var later = simulation.InvokeAsync(() => 42, CancellationToken.None);

        // Act
        simulation.RunTick();

        // Assert
        Assert.Equal("broken", (await Assert.ThrowsAsync<InvalidOperationException>(() => failing)).Message);
        Assert.Equal(42, await later);
    }

    [Fact]
    public async Task WhenAnInvocationIsCanceledBeforeItsTick_ThenItDoesNotRun()
    {
        // Arrange
        var simulation = CreateSimulation();
        using var cancellation = new CancellationTokenSource();
        var hasRun = false;
        var task = simulation.InvokeAsync(() => hasRun = true, cancellation.Token);

        // Act
        await cancellation.CancelAsync();
        simulation.RunTick();

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.False(hasRun);
    }

    [Fact]
    public void WhenAPostedActionThrows_ThenItIsLoggedAndTheTickGoesOn()
    {
        // Arrange
        var simulation = CreateSimulation();
        var hasRun = false;
        simulation.Post(() => throw new InvalidOperationException("broken"));
        simulation.Post(() => hasRun = true);

        // Act
        simulation.RunTick();

        // Assert
        Assert.True(hasRun);
        Assert.Contains(_logger.Entries, entry => entry.Level == LogLevel.Warning);
    }

    [Fact]
    public void WhenTickHandlersAreRegistered_ThenTheyRunAfterTheActionsInRegistrationOrder()
    {
        // Arrange
        var simulation = CreateSimulation();
        var order = new List<string>();
        simulation.AddTickHandler(() => order.Add("first"));
        simulation.AddTickHandler(() => order.Add("second"));
        simulation.Post(() => order.Add("action"));

        // Act
        simulation.RunTick();

        // Assert
        Assert.Equal(["action", "first", "second"], order);
    }
}
```

`RecordingLogger.Entries` holds `(LogLevel Level, string Message)`; add `using Microsoft.Extensions.Logging;` to both test files.

`GameSchedulesTests.cs`:

```csharp
using HomeBlaze.Game.Simulation;
using HomeBlaze.Game.Tests.Testing;
using Microsoft.Extensions.Time.Testing;

namespace HomeBlaze.Game.Tests.Simulation;

public class GameSchedulesTests
{
    private readonly FakeTimeProvider _timeProvider = new();
    private readonly RecordingLogger _logger = new();

    [Fact]
    public void WhenTheDelayPassed_ThenTheActionRunsInTheNextTickBeforeThePostedActions()
    {
        // Arrange
        var simulation = new GameSimulation(_timeProvider, _logger);
        var order = new List<string>();
        simulation.Post(() => simulation.Schedule(owner: this, TimeSpan.FromSeconds(1), () => order.Add("scheduled")));
        simulation.RunTick();
        simulation.Post(() => order.Add("posted"));

        // Act
        _timeProvider.Advance(TimeSpan.FromSeconds(1));
        simulation.RunTick();

        // Assert
        Assert.Equal(["scheduled", "posted"], order);
    }

    [Fact]
    public void WhenTheDelayHasNotPassed_ThenTheActionWaits()
    {
        // Arrange
        var simulation = new GameSimulation(_timeProvider, _logger);
        var hasRun = false;
        simulation.Post(() => simulation.Schedule(this, TimeSpan.FromSeconds(1), () => hasRun = true));
        simulation.RunTick();

        // Act
        _timeProvider.Advance(TimeSpan.FromMilliseconds(999));
        simulation.RunTick();

        // Assert
        Assert.False(hasRun);
    }

    [Fact]
    public void WhenTheScheduleIsDisposed_ThenTheActionNeverRuns()
    {
        // Arrange
        var simulation = new GameSimulation(_timeProvider, _logger);
        var hasRun = false;
        IDisposable? schedule = null;
        simulation.Post(() => schedule = simulation.Schedule(this, TimeSpan.FromSeconds(1), () => hasRun = true));
        simulation.RunTick();

        // Act
        schedule!.Dispose();
        _timeProvider.Advance(TimeSpan.FromSeconds(1));
        simulation.RunTick();

        // Assert
        Assert.False(hasRun);
    }

    [Fact]
    public void WhenTheOwnersSchedulesAreCanceled_ThenNoneOfThemRuns()
    {
        // Arrange
        var simulation = new GameSimulation(_timeProvider, _logger);
        var owner = new object();
        var count = 0;
        simulation.Post(() =>
        {
            simulation.Schedule(owner, TimeSpan.FromSeconds(1), () => count++);
            simulation.Schedule(owner, TimeSpan.FromSeconds(2), () => count++);
            simulation.Schedule(this, TimeSpan.FromSeconds(1), () => count += 10);
        });
        simulation.RunTick();

        // Act
        simulation.Post(() => simulation.CancelSchedules(owner));
        simulation.RunTick();
        _timeProvider.Advance(TimeSpan.FromSeconds(2));
        simulation.RunTick();

        // Assert
        Assert.Equal(10, count);
    }

    [Fact]
    public void WhenAScheduledActionThrows_ThenItIsLoggedAndTheTickGoesOn()
    {
        // Arrange
        var simulation = new GameSimulation(_timeProvider, _logger);
        var hasRun = false;
        simulation.Post(() =>
        {
            simulation.Schedule(this, TimeSpan.Zero, () => throw new InvalidOperationException("broken"));
            simulation.Schedule(this, TimeSpan.Zero, () => hasRun = true);
        });
        simulation.RunTick();

        // Act
        simulation.RunTick();

        // Assert
        Assert.True(hasRun);
        Assert.Contains(_logger.Entries, entry => entry.Level == LogLevel.Warning);
    }

    [Fact]
    public void WhenScheduledOutsideTheSimulationThread_ThenItThrows()
    {
        // Arrange
        var simulation = new GameSimulation(_timeProvider, _logger);

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => simulation.Schedule(this, TimeSpan.Zero, () => { }));
    }
}
```

- [ ] **Step 3: Run the tests to see them fail**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Game.Tests -c Debug --filter "FullyQualifiedName~Simulation"`
Expected: compile errors, `GameSimulation` does not exist.

- [ ] **Step 4: Implement `GameSchedules`**

```csharp
namespace HomeBlaze.Game.Simulation;

/// <summary>
/// The actions waiting for their due time, by owner so that removing an owner cancels them. Not thread-safe: only the
/// simulation thread uses it.
/// </summary>
internal sealed class GameSchedules
{
    private readonly PriorityQueue<Entry, (long DueTimestamp, long Sequence)> _queue = new();
    private readonly Dictionary<object, List<Entry>> _byOwner = new(ReferenceEqualityComparer.Instance);
    private long _sequence;

    public IDisposable Add(object owner, long dueTimestamp, Action action)
    {
        var entry = new Entry(this, owner, action);
        _queue.Enqueue(entry, (dueTimestamp, _sequence++));
        if (!_byOwner.TryGetValue(owner, out var entries))
        {
            entries = [];
            _byOwner[owner] = entries;
        }

        entries.Add(entry);
        return entry;
    }

    public void Cancel(object owner)
    {
        if (_byOwner.Remove(owner, out var entries))
        {
            foreach (var entry in entries)
            {
                entry.IsCanceled = true;
            }
        }
    }

    /// <summary>
    /// Takes the next action due at or before the timestamp; canceled entries are skipped.
    /// </summary>
    public bool TryTakeDue(long nowTimestamp, out Action action)
    {
        while (_queue.TryPeek(out var entry, out var priority) && priority.DueTimestamp <= nowTimestamp)
        {
            _queue.Dequeue();
            if (entry.IsCanceled)
            {
                continue;
            }

            Forget(entry);
            action = entry.Action;
            return true;
        }

        action = null!;
        return false;
    }

    private void Forget(Entry entry)
    {
        if (_byOwner.TryGetValue(entry.Owner, out var entries))
        {
            entries.Remove(entry);
            if (entries.Count == 0)
            {
                _byOwner.Remove(entry.Owner);
            }
        }
    }

    // Disposing from any thread only sets the flag, which the simulation thread reads when the entry comes due.
    private sealed class Entry(GameSchedules schedules, object owner, Action action) : IDisposable
    {
        private volatile bool _isCanceled;

        public object Owner { get; } = owner;

        public Action Action { get; } = action;

        public bool IsCanceled
        {
            get => _isCanceled;
            set => _isCanceled = value;
        }

        public void Dispose() => _isCanceled = true;
    }
}
```

A disposed entry stays in `_byOwner` until it comes due or its owner is canceled; that bounded leak is fine because removed owners clear their list.

- [ ] **Step 5: Implement `GameSimulation`**

```csharp
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Game.Simulation;

/// <summary>
/// Runs the actions of one game one after another on a fixed tick. Any thread may post or invoke actions; the actions,
/// schedules and tick handlers run on the thread that runs the tick, which <see cref="Current"/> marks meanwhile.
/// </summary>
internal sealed class GameSimulation
{
    [ThreadStatic]
    private static GameSimulation? t_current;

    private readonly Channel<Action> _actions = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions { SingleReader = true });
    private readonly GameSchedules _schedules = new();
    private readonly List<Action> _tickHandlers = [];
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private bool _isFailing;

    public GameSimulation(TimeProvider timeProvider, ILogger logger)
    {
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// The simulation whose tick runs on this thread, or null.
    /// </summary>
    public static GameSimulation? Current => t_current;

    public bool IsCurrent => ReferenceEquals(t_current, this);

    /// <summary>
    /// Queues the action for the next tick; a throwing action is logged.
    /// </summary>
    public void Post(Action action) => _actions.Writer.TryWrite(action);

    /// <summary>
    /// Queues the function for the next tick and completes with its result or exception. Canceling before the tick drops it.
    /// </summary>
    public Task<T> InvokeAsync<T>(Func<T> function, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellationToken);
                return;
            }

            try
            {
                completion.TrySetResult(function());
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        });
        return completion.Task;
    }

    /// <summary>
    /// Runs the action on the simulation thread at the first tick at or after the delay; call on the simulation thread.
    /// </summary>
    /// <exception cref="InvalidOperationException">Called outside the simulation thread.</exception>
    public IDisposable Schedule(object owner, TimeSpan delay, Action action)
    {
        EnsureCurrent();
        return _schedules.Add(owner, _timeProvider.GetTimestamp() + (long)(delay.TotalSeconds * _timeProvider.TimestampFrequency), action);
    }

    /// <summary>
    /// Cancels every schedule of the owner; call on the simulation thread.
    /// </summary>
    public void CancelSchedules(object owner)
    {
        EnsureCurrent();
        _schedules.Cancel(owner);
    }

    /// <summary>
    /// Adds a step that every tick runs after the schedules and actions, in the order added; call before the first tick.
    /// </summary>
    public void AddTickHandler(Action handler) => _tickHandlers.Add(handler);

    /// <summary>
    /// Runs one tick on the calling thread: the due schedules, the queued actions, then the tick handlers.
    /// </summary>
    public void RunTick()
    {
        t_current = this;
        try
        {
            var now = _timeProvider.GetTimestamp();
            while (_schedules.TryTakeDue(now, out var scheduled))
            {
                RunLogged(scheduled);
            }

            while (_actions.Reader.TryRead(out var action))
            {
                RunLogged(action);
            }

            foreach (var handler in _tickHandlers)
            {
                RunLogged(handler);
            }
        }
        finally
        {
            t_current = null;
        }
    }

    /// <summary>
    /// Ticks every <see cref="GameSimulationSettings.TickInterval"/> until canceled.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(GameSimulationSettings.TickInterval, _timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                RunTick();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The game stops.
        }
    }

    // Warns when actions start failing and stays at debug level while they keep failing, so a broken object type does
    // not flood the log at the tick rate.
    private void RunLogged(Action action)
    {
        try
        {
            action();
            _isFailing = false;
        }
        catch (Exception exception)
        {
            _logger.Log(_isFailing ? LogLevel.Debug : LogLevel.Warning, exception, "An action of the game simulation failed");
            _isFailing = true;
        }
    }

    private void EnsureCurrent()
    {
        if (!IsCurrent)
        {
            throw new InvalidOperationException("Call this on the simulation thread.");
        }
    }
}
```

The test of `InvokeAsync<int>(() => throw ...)` compiles because the lambda converts to `Func<int>`.

- [ ] **Step 6: Run the tests until they pass**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Game.Tests -c Debug --filter "FullyQualifiedName~Simulation"`
Expected: all pass.

- [ ] **Step 7: Commit and push**

```bash
git add src/HomeBlaze/HomeBlaze.Game/GameSimulationSettings.cs src/HomeBlaze/HomeBlaze.Game/Simulation src/HomeBlaze/HomeBlaze.Game.Tests/Simulation
git commit -m "feat: add the game simulation core with actions, schedules and tick handlers"
git push
```

---

### Task 5: Entities, the chunk index and the occupancy snapshot

Standalone building blocks of the world state. Nothing in `Game` uses them yet, except that `GameWorldIndex` is generalized in a way the current core can keep using.

**Files:**
- Create: `HomeBlaze.Game/Simulation/GameEntity.cs`, `HomeBlaze.Game/Simulation/PlayerEntity.cs`, `HomeBlaze.Game/World/ChangedChunks.cs`, `HomeBlaze.Game/Simulation/OccupancySnapshot.cs`
- Modify: `HomeBlaze.Game/World/GameWorldIndex.cs`
- Test: `HomeBlaze.Game.Tests/World/GameWorldIndexTests.cs`, `HomeBlaze.Game.Tests/World/ChangedChunksTests.cs`, `HomeBlaze.Game.Tests/Simulation/OccupancySnapshotTests.cs`

- [ ] **Step 1: Write `GameEntity` and `PlayerEntity`**

```csharp
using HomeBlaze.Game.Abstractions;

namespace HomeBlaze.Game.Simulation;

/// <summary>
/// The simulation's view of something in the world: an object read from its subject, or a player whose state the
/// simulation owns. Touched only on the simulation thread.
/// </summary>
internal class GameEntity
{
    public GameEntity(GameObject subject)
    {
        Subject = subject;
    }

    /// <summary>
    /// The subject this entity belongs to; its facade.
    /// </summary>
    public GameObject Subject { get; }

    public GameArea Area { get; set; }

    public int Height { get; set; }

    public bool IsMovementBlocking { get; set; }

    public bool IsSoundBlocking { get; set; }

    public bool IsVisible { get; set; }

    /// <summary>
    /// The visual of an object, null for a player.
    /// </summary>
    public GameVisual? Visual { get; set; }

    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Reads area, height, blocking, visibility, visual and title from the subject; returns whether anything changed.
    /// </summary>
    public bool ReadFromSubject()
    {
        var subject = Subject;
        var area = subject.Area;
        var height = subject.Height;
        var isMovementBlocking = subject.IsMovementBlocking;
        var isSoundBlocking = subject.IsSoundBlocking;
        var isVisible = subject.IsVisible;
        var visual = subject.Visual;
        var title = (subject as HomeBlaze.Abstractions.ITitleProvider)?.Title ?? string.Empty;
        if (area == Area && height == Height && isMovementBlocking == IsMovementBlocking && isSoundBlocking == IsSoundBlocking
            && isVisible == IsVisible && Equals(visual, Visual) && title == Title)
        {
            return false;
        }

        (Area, Height, IsMovementBlocking, IsSoundBlocking, IsVisible, Visual, Title) =
            (area, height, isMovementBlocking, isSoundBlocking, isVisible, visual, title);
        return true;
    }
}
```

```csharp
using HomeBlaze.Game.Abstractions;
using HomeBlaze.Game.World;

namespace HomeBlaze.Game.Simulation;

/// <summary>
/// A player as the simulation owns it; <see cref="GamePlayer"/> is its facade, which the simulation publishes to.
/// </summary>
internal sealed class PlayerEntity : GameEntity
{
    public PlayerEntity(GamePlayer subject, string clientId)
        : base(subject)
    {
        ClientId = clientId;
        Area = new GameArea(0, 0, GamePlayer.Size, GamePlayer.Size);
        Height = GamePlayer.BodyHeight;
        Visual = null;
    }

    public new GamePlayer Subject => (GamePlayer)base.Subject;

    public string ClientId { get; }

    public GameDirection Facing { get; set; }

    public bool IsConnected { get; set; }

    public bool IsMicrophoneOn { get; set; }

    public bool IsAudioOn { get; set; }

    public RateLimiter? StepRateLimiter { get; set; }

    public RateLimiter? TurnRateLimiter { get; set; }

    public long? LastMessageTimestamp { get; set; }

    public int? MicrophonePageId { get; set; }

    public HashSet<int>? AudioPageIds { get; set; }

    /// <summary>
    /// Whether a value the facade shows changed since the last publish.
    /// </summary>
    public bool IsPublishPending { get; set; }

    /// <summary>
    /// Sets connected; a connected player blocks movement and is visible.
    /// </summary>
    public void SetConnected(bool isConnected)
    {
        IsConnected = isConnected;
        IsMovementBlocking = isConnected;
        IsVisible = isConnected;
    }
}
```

- [ ] **Step 2: Generalize `GameWorldIndex` to entities with changed chunks**

Change the index so it is keyed by `GameEntity` and takes the entity's current fields instead of reading a `GameObject`:
- `public void AddOrUpdate(GameEntity entity)` builds its entry from `entity.Area`, `entity.IsMovementBlocking`, `entity.IsSoundBlocking`, `entity.IsVisible`, plus `entity.Visual` and `entity.Title` for change detection of the view (an entry change that only touches the visual or title still marks the chunks as changed but keeps the chunk lists).
- `public void Remove(GameEntity entity)`.
- `public void Query(GameArea area, List<GameEntity> results)`, `public bool IsBlocked(GameArea area, GameEntity? ignored)`, plus `public bool IsBlockedExceptOverlapping(GameArea area, GameEntity self)`: blocked only by movement blocking entities that do not already overlap `self.Area` (the step-out rule).
- `public void QueryChunk((long X, long Y) chunk, List<GameEntity> results)` and `public static (long X, long Y) GetChunk(long x, long y)` for the views.
- Every add, move, removal and entry change records the chunks of the old and new area in a `ChangedChunks` instance the index owns: `public ChangedChunks Changes { get; }`.
- `HasValidSize` takes a `GameArea` and a height.

Until Task 6, `Game` keeps using the index: wrap each `GameObject` in a `GameEntity` stored in a `Dictionary<GameObject, GameEntity>` inside `Game.Index.cs`, refreshed with `ReadFromSubject()` in `GetIndex()`'s rebuild and in `AddOrUpdate` calls (player entities are plain `GameEntity` there; the core gets `PlayerEntity` in Task 6). Keep this adapter minimal; Task 6 deletes it.

`ChangedChunks`:

```csharp
namespace HomeBlaze.Game.World;

/// <summary>
/// The chunks whose entities changed since the last <see cref="Clear"/>. Not thread-safe: only the simulation thread uses it.
/// </summary>
internal sealed class ChangedChunks
{
    private readonly HashSet<(long X, long Y)> _chunks = new();

    public IReadOnlyCollection<(long X, long Y)> Chunks => _chunks;

    public int Count => _chunks.Count;

    public bool Contains((long X, long Y) chunk) => _chunks.Contains(chunk);

    public void Add((long X, long Y) chunk) => _chunks.Add(chunk);

    public void Clear() => _chunks.Clear();
}
```

Add the area's chunks with a loop over `GetChunk(area.X, area.Y)` to `GetChunk(area.Right, area.Top)`.

- [ ] **Step 3: Write the occupancy snapshot**

```csharp
using System.Collections.Immutable;
using HomeBlaze.Game.Abstractions;
using HomeBlaze.Game.World;

namespace HomeBlaze.Game.Simulation;

/// <summary>
/// An immutable copy of the movement blocking areas per chunk, which any thread may read. The simulation builds the next
/// snapshot from the previous one by replacing only the changed chunks.
/// </summary>
internal sealed class OccupancySnapshot
{
    public static readonly OccupancySnapshot Empty = new(ImmutableDictionary<(long X, long Y), ImmutableArray<(GameArea Area, GameObject Subject)>>.Empty);

    private readonly ImmutableDictionary<(long X, long Y), ImmutableArray<(GameArea Area, GameObject Subject)>> _chunks;

    private OccupancySnapshot(ImmutableDictionary<(long X, long Y), ImmutableArray<(GameArea Area, GameObject Subject)>> chunks)
    {
        _chunks = chunks;
    }

    /// <summary>
    /// Whether no movement blocking area other than one of <paramref name="except"/> overlaps the area.
    /// </summary>
    public bool IsFree(GameArea area, GameObject? except)
    {
        var (minimumX, minimumY) = GameWorldIndex.GetChunk(area.X, area.Y);
        var (maximumX, maximumY) = GameWorldIndex.GetChunk(area.Right, area.Top);
        for (var chunkX = minimumX; chunkX <= maximumX; chunkX++)
        {
            for (var chunkY = minimumY; chunkY <= maximumY; chunkY++)
            {
                if (!_chunks.TryGetValue((chunkX, chunkY), out var entries))
                {
                    continue;
                }

                foreach (var (blockingArea, subject) in entries)
                {
                    if (!ReferenceEquals(subject, except) && blockingArea.Intersects(area))
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Returns a snapshot with the changed chunks copied from the index; this one stays unchanged.
    /// </summary>
    public OccupancySnapshot With(GameWorldIndex index, ChangedChunks changes, List<GameEntity> scratch)
    {
        if (changes.Count == 0)
        {
            return this;
        }

        var builder = _chunks.ToBuilder();
        foreach (var chunk in changes.Chunks)
        {
            scratch.Clear();
            index.QueryChunk(chunk, scratch);
            var entries = ImmutableArray.CreateBuilder<(GameArea, GameObject)>();
            foreach (var entity in scratch)
            {
                if (entity.IsMovementBlocking)
                {
                    entries.Add((entity.Area, entity.Subject));
                }
            }

            if (entries.Count == 0)
            {
                builder.Remove(chunk);
            }
            else
            {
                builder[chunk] = entries.ToImmutable();
            }
        }

        return new OccupancySnapshot(builder.ToImmutable());
    }
}
```

Use `IsFree`'s chunk loop only for areas up to `GameObject.MaximumSize`; reject larger areas with `ArgumentOutOfRangeException` in `Game.IsFree` (Task 6).

- [ ] **Step 4: Tests**

`ChangedChunksTests`: adding the same chunk twice counts once; `Clear` empties.

`GameWorldIndexTests` (adapt the existing tests to entities, then add):

```csharp
    [Fact]
    public void WhenAnEntityMovesToAnotherChunk_ThenBothChunksAreChanged()
    {
        // Arrange
        var index = new GameWorldIndex();
        var entity = CreateEntity(new GameArea(0, 0, 2, 2), isMovementBlocking: true);
        index.AddOrUpdate(entity);
        index.Changes.Clear();

        // Act
        entity.Area = new GameArea(16, 0, 2, 2);
        index.AddOrUpdate(entity);

        // Assert
        Assert.True(index.Changes.Contains((0, 0)));
        Assert.True(index.Changes.Contains((1, 0)));
    }

    [Fact]
    public void WhenAMovementBlockingEntityAlreadyOverlapsThePlayer_ThenItDoesNotBlockTheStepOut()
    {
        // Arrange
        var index = new GameWorldIndex();
        var door = CreateEntity(new GameArea(0, 0, 4, 1), isMovementBlocking: true);
        var player = CreateEntity(new GameArea(1, 0, 2, 2), isMovementBlocking: true);
        index.AddOrUpdate(door);
        index.AddOrUpdate(player);

        // Act
        var isBlocked = index.IsBlockedExceptOverlapping(new GameArea(1, 1, 2, 2), player);

        // Assert
        Assert.False(isBlocked);
    }
```

with a `CreateEntity` helper building a `GameEntity` over a test `GameObject` (reuse `Testing/MovementBlocker.cs`) and setting the fields directly.

`OccupancySnapshotTests`: an empty snapshot is free everywhere; after `With` over a changed chunk with a blocking entity, `IsFree` over its area is false, over it with `except` set to its subject true, and over a neighbor area true; a chunk whose blocking entity moved away is free in the next snapshot while the previous snapshot still reports it occupied (immutability).

- [ ] **Step 5: Run all game unit tests, commit and push**

Run the unit tests of all game test projects. Expected: all pass (behavior unchanged).

```bash
git add src/HomeBlaze/HomeBlaze.Game src/HomeBlaze/HomeBlaze.Game.Tests
git commit -m "feat: add simulation entities, changed chunks and the occupancy snapshot"
git push
```

---

### Task 6: Run the game on the simulation

The switch. `Game` owns a `GameSimulation` and a `GameWorldState`; movement, players, the object API, the volume table, voice switches, publishing, the write interceptor and saving run on it. The lock and the outside-change machinery are deleted. Views are still pulled by the page for now (Task 9 replaces that), but from the simulation.

This task does not compile in between; work through the steps, then build and fix. Commit once at the end (or in several commits once each compiles).

**Files:**
- Create: `HomeBlaze.Game/Simulation/GameWorldState.cs`, `HomeBlaze.Game/Simulation/GameWriteInterceptor.cs`, `HomeBlaze.Game/Simulation/GamePublisher.cs`, `HomeBlaze.Game/Game.World.cs`
- Rewrite: `HomeBlaze.Game/Game.cs`, `Game.Players.cs`, `Game.Persistence.cs`, `Game.Voice.cs`, `GamePlayer.cs`
- Delete: `Game.Index.cs`, `Game.Movement.cs`, `Game.Objects.cs`, `Game.Placement.cs`, `Game.Actions.cs`, `Game.Runtime.cs`, `Game.View.cs`, `Game.Volume.cs` (their logic moves into `GameWorldState` and `Game.World.cs`)
- Modify: `World/HearingRange.cs`, `World/SoundPathSearch.cs`, `World/SpawnFinder.cs`, `World/TargetArea.cs`, `Voice/GameVolumeTable.cs`, `View/GameViewTracker.cs`, `Components/GamePage.razor`, `Components/GameCallerServiceProvider.cs`
- Tests: `Testing/GameTestContext.cs` and every test that touched the removed members; new `Simulation/GameWorldStateTests.cs`, `Simulation/GameWriteInterceptorTests.cs`, `GamePublishTests.cs`

- [ ] **Step 1: Move the consumers to entities**

- `HearingRange.GetVolume(PlayerEntity first, PlayerEntity second, GameWorldIndex index, SoundPathSearch search)`; everything reading `GamePlayer.X/Y/Footprint` reads `entity.Area` instead. `SoundPathSearch` queries sound blocking entities.
- `GameVolumeTable.Build(IReadOnlyCollection<PlayerEntity> players, GameWorldIndex index, List<GameEntity> candidates, SoundPathSearch search)`; rows keyed by client id as today, connected players only.
- `GameViewTracker.Update(IReadOnlyList<GameEntity> entities, PlayerEntity? self, ILogger logger)`: compares and builds `GameViewObject` from the entity fields (`Area`, `Height`, `Facing` and voice flags for a `PlayerEntity`, `Visual`, `Title`, `IsMovementBlocking`); keyed by entity. `GetPlayerColor(string clientId)`. `GameVisualValidator.GetValidVisual` takes the subject for the warn-once key and the entity's visual, falling back to `subject.DefaultVisual`.
- `SpawnFinder` and `TargetArea` take `GameArea`.
- Adapt `HearingRangeTests`, `GameVolumeTableTests`, `GameViewTrackerTests` to build entities directly (a small `Testing/EntityFactory.cs` with `Player(string clientId, int x, int y)` and `Object(GameObject subject)` helpers that call `ReadFromSubject`).

- [ ] **Step 2: Write `GameWorldState`**

`internal sealed class GameWorldState`, used only on the simulation thread, owning:
- `Dictionary<string, PlayerEntity> Players` (by client id), `Dictionary<string, GameEntity> Objects` (by name), `Dictionary<GameObject, GameEntity> EntitiesBySubject`, `Dictionary<GameObject, string> NamesBySubject`;
- `GameWorldIndex Index`;
- `HashSet<GameObject> ChangedSubjects` (subjects to read again), `bool IsObjectSetChanged`, `bool IsPlayerSetChanged`, `bool IsSaveRequested`;
- `OccupancySnapshot Snapshot` (volatile field, read by `IsFree` from any thread).

Methods, each a port of today's logic from the deleted partials, minus locking:
- `TakeOverObjects(Dictionary<string, GameObject> objects)`: rebuilds `Objects`, `EntitiesBySubject`, `NamesBySubject` and the index entries of objects without overlap checks; returns the added and removed subjects so the game can call `OnAddedToWorld` and cancel schedules (Task 7).
- `TakeOverPlayers(Dictionary<string, GamePlayer> players)`: creates a `PlayerEntity` for each subject from its `X`, `Y`, `Facing` (positions of loaded players), keeps voice off and disconnected; reconcile runs right after.
- `Reconcile(IReadOnlyList<GameClient> clients, Func<GamePlayer> createPlayer)`: today's `ReconcilePlayers` on entities (add missing players, remove stale ones, update titles and connection, spawn joining players with `SpawnFinder`); sets `IsPlayerSetChanged` and `IsPublishPending`.
- `TryMove(PlayerEntity player, GameDirection direction, bool turnOnly, TimeProvider timeProvider)`: today's `TryMove` and `TryGetStep`, with `IsBlockedExceptOverlapping` instead of `IsBlocked` for each candidate area.
- `GetTargets(PlayerEntity player, List<GameEntity> results)`: today's `GetTargetObjects`.
- `Place`, `Remove`, `Replace`: today's validation and dictionary updates, on entities; names as in Task 2.
- `RefreshChangedSubjects()`: for each subject in `ChangedSubjects` that is an object of this world, `ReadFromSubject()` and `Index.AddOrUpdate`; clears the set.
- `PublishSnapshot()`: `Snapshot = Snapshot.With(Index, Index.Changes, scratch)`.

Write `Simulation/GameWorldStateTests.cs` with at least: a take-over of overlapping objects keeps all of them; reconcile spawns a joining player on a free position; a step into a wall is refused but turns; a player inside a closed door steps out of it; placing onto a blocking object throws `Something is in the way.`; replace is atomic; `RefreshChangedSubjects` picks up a door that was opened by a direct write (the index no longer blocks its area).

- [ ] **Step 3: Write the write interceptor**

```csharp
using HomeBlaze.Game.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Parent;

namespace HomeBlaze.Game.Simulation;

/// <summary>
/// Reports writes to games and their objects to the game's simulation; one instance serves every game of a context.
/// Never waits for a simulation, so a write from a change observer cannot deadlock with the simulation's own writes.
/// </summary>
internal sealed class GameWriteInterceptor : IWriteInterceptor
{
    public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
    {
        var subject = context.Property.Subject;
        if (subject is not (GameObject or Game))
        {
            next(ref context);
            return;
        }

        var current = GameSimulation.Current;
        if (current is null && subject is GamePlayer && IsSimulationOwned(context.Property.Name))
        {
            throw new InvalidOperationException($"Only the game sets {context.Property.Name}.");
        }

        next(ref context);

        var game = subject as Game ?? subject.TryGetFirstParent<Game>();
        if (game is null)
        {
            return;
        }

        game.OnWritten(subject, context.Property.Name, context.NewValue, isOnSimulation: current is not null && game.IsSimulation(current));
    }

    private static bool IsSimulationOwned(string name) => name is nameof(GamePlayer.ClientId) or nameof(GamePlayer.ClientTitle)
        or nameof(GamePlayer.IsConnected) or nameof(GamePlayer.IsMicrophoneOn) or nameof(GamePlayer.IsAudioOn);
}
```

Register it in `Game.ExecuteAsync` before the first tick: `((IInterceptorSubject)this).Context.TryAddService<IWriteInterceptor>(() => new GameWriteInterceptor(), existing => existing is GameWriteInterceptor);`. Check how `TryAddService` is declared (`InterceptorSubjectContext.TryAddService<TService>(Func<TService>, Func<TService, bool>)`) and whether the context interface exposes it; if only the concrete class has it, cast as the storage tests do with `AddService`.

`Game.OnWritten(IInterceptorSubject subject, string name, object? value, bool isOnSimulation)` (internal; `TProperty` boxes only on these game writes):
- the simulation's own publish writes are skipped by a `_isPublishing` flag the publisher sets on the simulation thread;
- on the simulation thread: `_world.ChangedSubjects.Add(gameObject)` for objects;
- elsewhere: `Game.Players` with a dictionary value posts `TakeOverPlayers(value)` then reconcile; `Game.Objects` posts `TakeOverObjects(value)`; `GamePlayer.X`/`Y`/`Facing` post a teleport or turn with the value for that player's entity (look up by subject); any other write to a `GameObject` posts `ChangedSubjects.Add(subject)`.

Tests in `Simulation/GameWriteInterceptorTests.cs` (through `GameTestContext`, see Step 7): an outside write of `Door.IsOpen` is seen by the index after one tick; an outside write of `GamePlayer.IsConnected` through the registry throws and leaves it unchanged; an outside replacement of `Game.Objects` written while a place is queued keeps both (place lands after the take-over in queue order) and the publish after it does not drop the replacement; an outside write of `GamePlayer.X` moves the player's entity at the next tick; a write from a synchronous change observer on another thread completes without waiting for a tick (call the write, assert it returned and the value landed, then run the tick).

- [ ] **Step 4: Write the publisher**

`GamePublisher` (simulation thread only), run as a tick handler: when `PublishInterval` passed since the last publish and anything is pending, set `_isPublishing`, write for each `PlayerEntity` with `IsPublishPending` its `X`, `Y`, `Facing`, `IsConnected`, `ClientTitle`, `IsMicrophoneOn`, `IsAudioOn` (internal setters), write `Game.Players` with a new dictionary of the player subjects when `IsPlayerSetChanged`, write `Game.Objects` with a new dictionary of the object subjects when `IsObjectSetChanged`, clear the flags, clear `_isPublishing`. A save in progress pauses publishing (Step 6).

`GamePublishTests.cs`: a step updates the entity right away but `GamePlayer.X` only after the publish interval; two steps within one interval publish once with the latest value; a placed object appears in `Game.Objects` after the publish; `ClientId` is set on new player subjects before they are published (it is the dictionary key and the facade's identity).

- [ ] **Step 5: Wire `Game`**

`Game.cs` keeps the configuration properties, the constructors and the `On*Changing` normalizations (without the pending dictionaries and without the lock). It creates `_simulation = new GameSimulation(timeProvider, logger)` and `_world = new GameWorldState(...)` and registers the tick handlers in this order: refresh changed subjects, publish the occupancy snapshot, rebuild the volume table (at most every `GameVoiceSettings.VolumeTableInterval` when the index changed or a voice switch changed, as `TryRebuildVolumeTable` does today, raising `VolumeTableChanged` after publishing the table), raise `WorldChanged` when the index changed (the page's pull trigger until Task 9), publish, save.

`ExecuteAsync`: register the interceptor, post the initial take-over of `Players` and `Objects`, then run `_simulation.RunAsync(stoppingToken)` together with the voice loop (unchanged) and a housekeeping loop that only posts `Reconcile` when signaled by a client change or every `HousekeepingInterval` (the subscription to client changes through `_clients.IsClientChange` stays, but its handler only calls `SignalReconcile`).

`Game.World.cs` implements `IGameWorld` and the internal page API on the simulation:

```csharp
    public bool IsFree(GameArea area, GameObject? except = null) =>
        _simulation.IsCurrent
            ? !_world.Index.IsBlocked(area, except is null ? null : _world.FindEntity(except))
            : _world.Snapshot.IsFree(area, except);

    public async Task PlaceAsync(GameObject gameObject, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(gameObject);
        await _simulation.InvokeAsync(() =>
        {
            _world.Place(gameObject);
            return true;
        }, cancellationToken);
    }
```

`RemoveAsync` and `ReplaceAsync` follow the same shape. Saving is the tick's job: `_world.Place` and the other world commands set `IsSaveRequested`, and the cancellation token only drops a command that has not run yet. `GameWorldState.FindEntity(GameObject)` returns the entity of an object or player subject, or null. `GetSpotInFront` must answer synchronously: when called on the simulation thread it reads the entity directly; elsewhere it reads the caller's player area and facing from a per-tick immutable copy `PlayerPositions` (a `ImmutableDictionary<string, (GameArea Area, GameDirection Facing, bool IsConnected)>` swapped in with the occupancy snapshot) so operations on page threads never wait. Validation and messages as in Task 2.

Internal page API (all return tasks that complete in the next tick):
- `Task<MoveResult> StepAsync(string clientId, GameDirection direction, bool turnOnly)`;
- `Task<bool> SetMicrophoneAsync(string clientId, int pageId, bool isOn)`, `Task<bool> SetAudioAsync(string clientId, int pageId, bool isOn)`, `Task<(bool IsMicrophonePage, bool IsAudioPage)> GetVoicePagesAsync(string clientId, int pageId)`;
- `Task<GameViewUpdate> UpdateViewAsync(GameViewTracker tracker, string? clientId)` (full window query as today; Task 9 replaces it);
- `Task<IReadOnlyList<GameObject>> GetTargetsAsync(string clientId)`;
- `Task SendMessageAsync(GamePlayer recipient, GameCaller? sender, string message, CancellationToken)`: takes the message slot on the simulation (`InvokeAsync`), then sends outside it;
- `RenameAsync` and `GetClientName` as today, with the actor resolved through `PlayerPositions`.

Voice (`Game.Voice.cs`): `SetMicrophone`/`SetAudio` bodies move into `InvokeAsync` actions on `PlayerEntity`; `_voicePlayers` and `ReleaseVoiceOfReplacedPlayers` disappear, because `Reconcile` and `TakeOverPlayers` release the mixer talker of every removed or disconnected player entity directly (`_voiceMixer.SetMicrophonePage(clientId, null)`); the mixer keeps its own lock and loop. Update the mixer's class comment: the game calls it from the simulation thread.

`GamePlayer`: remove `StepRateLimiter`, `TurnRateLimiter`, `LastMessageTimestamp`, `MicrophonePageId`, `AudioPageIds`; `IsMovementBlocking`/`IsVisible` stay derived from `IsConnected` (the facade; the simulation uses the entity). `SendMessageAsync` calls `game.SendMessageAsync(this, caller, trimmed, ct)`.

- [ ] **Step 6: Saving**

`Game.Persistence.cs`: a tick handler starts a save when `SaveInterval` passed since the last save start and `_world.IsSaveRequested` (set by every move, join, world command, take-over and object change): it forces a publish first, clears the flag, sets `_isSaving`, and runs `WriteConfigurationAsync` (unchanged) on the thread pool; the publisher skips while `_isSaving`; completion clears `_isSaving` through a posted action and requests a save again when the write failed. `StopAsync` waits for a running save, then runs a final publish and save directly (the simulation loop has stopped, so the thread that stops is the only one touching the state; mark it current with a `RunExclusive(Action)` helper on `GameSimulation` that sets `t_current` around the action).

Tests in `GamePersistenceTests.cs` (adapt): moves within 10 s save once at the interval; nothing saves without changes; a failed write is retried at the next interval; publishing pauses while a save runs (a step during the save publishes after it completes); stopping saves pending changes.

- [ ] **Step 7: Adapt the test context and the page**

`GameTestContext`:

```csharp
    /// <summary>
    /// Advances the clock by one tick interval and runs one tick on the test thread.
    /// </summary>
    public void Tick(int count = 1)
    {
        for (var i = 0; i < count; i++)
        {
            TimeProvider.Advance(GameSimulationSettings.TickInterval);
            Game.RunTickForTest();
        }
    }

    /// <summary>
    /// Runs ticks until the task completes, at most 100.
    /// </summary>
    public async Task<T> RunAsync<T>(Task<T> task)
    {
        for (var i = 0; i < 100 && !task.IsCompleted; i++)
        {
            Tick();
        }

        return await task;
    }

    public async Task RunAsync(Task task)
    {
        for (var i = 0; i < 100 && !task.IsCompleted; i++)
        {
            Tick();
        }

        await task;
    }

    /// <summary>
    /// Runs ticks until the publish interval passed, so subjects show the simulation's state.
    /// </summary>
    public void Publish() => Tick((int)(GameSimulationSettings.PublishInterval / GameSimulationSettings.TickInterval) + 1);
```

`Game.RunTickForTest()` is internal and calls `_simulation.RunTick()`; the constructor of `GameTestContext` calls `Game.StartForTest()` (internal: registers the interceptor and posts the take-overs, without starting loops) and ticks once. `AddConnectedPlayer` sets the client, ticks (reconcile), then writes `player.X`/`Y` (a teleport through the interceptor) and ticks again. `AddObject` and the building helpers write `Game.Objects` and tick.

Adapt every test of the deleted members: `TryMove` becomes `await _context.RunAsync(_context.Game.StepAsync(player.ClientId, direction, turnOnly))` after advancing by `Game.StepInterval`; `ReconcilePlayers()` becomes `_context.Game.SignalReconcile(); _context.Tick();` (or an internal `PostReconcile()`); assertions on `GamePlayer.X` call `_context.Publish()` first; tests of `_pendingPlayers`/copy-on-write races become the interceptor take-over tests of Step 3; tests of `RequestIndexRebuild` are deleted.

`GamePage.razor` (interim, until Task 9): `Step` becomes `public async Task<StepResult?> Step(...)` awaiting `StepAsync`; the page subscribes to `game.WorldChanged` instead of the context's property change observable and calls `UpdateViewAsync`; voice toggles await the new async methods; the caller is `new GameCaller(clientId)`.

- [ ] **Step 8: Delete the old core and check nothing refers to it**

Delete the partials listed above. Then:

Run: `grep -rn "_lock\|_pendingPlayers\|_pendingObjects\|CurrentPlayers\|CurrentObjects\|RequestIndexRebuild\|IsHeldByCurrentThread\|IsOutsideChange\|UpdateView(" src/HomeBlaze/HomeBlaze.Game src/HomeBlaze/HomeBlaze.Game.Tests`
Expected: no matches except the mixer's own `_lock` in `Voice/GameVoiceMixer.cs` and `TalkerQueue`.

- [ ] **Step 9: Build and run every game test including E2E**

Run the build, the unit tests of all game test projects and the E2E tests. Expected: all pass; the E2E tests prove movement, voice, placing, doors, renaming and the view on the real page.

- [ ] **Step 10: Commit and push**

```bash
git add -A src/HomeBlaze/HomeBlaze.Game src/HomeBlaze/HomeBlaze.Game.Tests src/HomeBlaze/HomeBlaze.Game.Buildings.Tests src/HomeBlaze/HomeBlaze.Game.Nature.Tests
git commit -m "refactor: run the game on a simulation that owns the world state"
git push
```

Controller: restart the app and ask the user to try walking, doors, building and voice.

---

### Task 7: `Schedule` and `OnAddedToWorld`

**Files:**
- Modify: `HomeBlaze.Game.Abstractions/IGameWorld.cs`, `HomeBlaze.Game.Abstractions/GameObject.cs`, `HomeBlaze.Game/Game.World.cs`, `HomeBlaze.Game/Simulation/GameWorldState.cs`
- Test: `HomeBlaze.Game.Tests/GameScheduleTests.cs`, public API snapshot

- [ ] **Step 1: Extend the contract**

`IGameWorld`:

```csharp
    /// <summary>
    /// Runs the action on the game's own thread at the first tick at or after the delay. Disposing the result cancels it,
    /// and removing the object cancels all its schedules. Schedules are not saved; set them up again in
    /// <see cref="GameObject.OnAddedToWorld"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The object is not part of this game.</exception>
    IDisposable Schedule(GameObject gameObject, TimeSpan delay, Action action);
```

`internal` members of the abstractions are invisible to the core (no `InternalsVisibleTo` between libraries), so the core reaches the protected hook through an interface that `GameObject` implements explicitly. It stays off the members object authors see.

`HomeBlaze.Game.Abstractions/IGameObjectLifecycle.cs`:

```csharp
namespace HomeBlaze.Game.Abstractions;

/// <summary>
/// What a game calls on its objects; object types override the protected members of <see cref="GameObject"/> instead.
/// </summary>
public interface IGameObjectLifecycle
{
    /// <summary>
    /// Called on the game's own thread when the object enters the world.
    /// </summary>
    void OnAddedToWorld();
}
```

`GameObject` implements it:

```csharp
public abstract partial class GameObject : IConfigurable, IGameObjectLifecycle
{
    void IGameObjectLifecycle.OnAddedToWorld() => OnAddedToWorld();

    /// <summary>
    /// Called on the game's own thread when the object enters the world: when the game starts or loads, and when the
    /// object is placed or added by a replacement. The place to set up schedules.
    /// </summary>
    protected virtual void OnAddedToWorld()
    {
    }
}
```

- [ ] **Step 2: Implement**

`Game.Schedule`: when called on the simulation thread, check the object is part of this world (`_world.NamesBySubject`), then `_simulation.Schedule(gameObject, delay, action)`; when called elsewhere (an operation on a page thread), post the registration and return a disposable that cancels it whether or not it was registered yet (a small `PendingSchedule` class holding a volatile canceled flag and the inner disposable). Every scheduled action is wrapped so that afterwards the written subjects are refreshed in the same tick (writes on the simulation thread already add to `ChangedSubjects`; the refresh handler runs after the schedules).

`GameWorldState`: `Remove`, `Replace` and `TakeOverObjects` return the removed subjects; the game calls `_simulation.CancelSchedules(subject)` for each. `Place`, `Replace`, `TakeOverObjects` (also the initial one) return the added subjects; the game calls `((IGameObjectLifecycle)subject).OnAddedToWorld()` for each, logged and throttled through the simulation's failure logging when it throws.

- [ ] **Step 3: Tests**

`GameScheduleTests.cs` with a test object `ScheduledObject : GameObject` (in `Testing/`) whose operation-like method schedules a counter increment and whose `OnAddedToWorld` records calls:
- `WhenAScheduleIsDue_ThenTheActionRunsAndItsWritesReachTheIndex` (a schedule that opens a door; after the due tick `IsFree` over the door is true);
- `WhenTheObjectIsRemoved_ThenItsSchedulesAreCanceled`;
- `WhenScheduledFromAPageThreadAndDisposedBeforeTheTick_ThenTheActionNeverRuns`;
- `WhenTheGameStarts_ThenOnAddedToWorldRunsForEveryLoadedObject`;
- `WhenAnObjectIsPlacedOrAddedByAReplacement_ThenOnAddedToWorldRuns`;
- `WhenOnAddedToWorldThrows_ThenTheObjectStaysAndTheTickGoesOn`;
- `WhenSchedulingAnObjectOfAnotherGame_ThenItThrows`.

- [ ] **Step 4: Build, test, accept the snapshot, commit and push**

```bash
git add -A src/HomeBlaze/HomeBlaze.Game.Abstractions src/HomeBlaze/HomeBlaze.Game.Abstractions.Tests src/HomeBlaze/HomeBlaze.Game src/HomeBlaze/HomeBlaze.Game.Tests
git commit -m "feat: let game objects schedule actions and set them up when they enter the world"
git push
```

---

### Task 8: Door `AutoCloseDelay`

**Files:**
- Modify: `HomeBlaze.Game.Buildings/Door.cs`
- Test: `HomeBlaze.Game.Buildings.Tests/DoorTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
    [Fact]
    public void WhenADoorWithAutoCloseIsOpened_ThenItClosesAfterTheDelay()
    {
        // Arrange
        var door = _context.AddDoor(0, 10);
        door.AutoCloseDelay = TimeSpan.FromSeconds(5);
        _context.Tick();

        // Act
        door.Open();
        _context.Tick();
        _context.TimeProvider.Advance(TimeSpan.FromSeconds(5));
        _context.Tick();

        // Assert
        Assert.False(door.IsOpen);
    }

    [Fact]
    public void WhenSomethingStandsInTheDoorwayAtTheDelay_ThenTheDoorTriesAgainAfterTheDelay()
    {
        // Arrange
        var door = _context.AddDoor(0, 4);
        door.AutoCloseDelay = TimeSpan.FromSeconds(5);
        _context.Tick();
        door.Open();
        var player = _context.AddConnectedPlayer("tab-1", x: 1, y: 3);

        // Act
        _context.TimeProvider.Advance(TimeSpan.FromSeconds(5));
        _context.Tick();
        var isOpenWhileOccupied = door.IsOpen;
        player.X = 20;
        _context.Tick();
        _context.TimeProvider.Advance(TimeSpan.FromSeconds(5));
        _context.Tick();

        // Assert
        Assert.True(isOpenWhileOccupied);
        Assert.False(door.IsOpen);
    }

    [Fact]
    public void WhenAnOpenDoorWithAutoCloseIsLoaded_ThenItClosesAfterTheDelay()
    {
        // Arrange
        var door = new Door { X = 0, Y = 10, IsOpen = true, AutoCloseDelay = TimeSpan.FromSeconds(5) };

        // Act
        _context.AddObject("door-loaded", door);
        _context.TimeProvider.Advance(TimeSpan.FromSeconds(5));
        _context.Tick();

        // Assert
        Assert.False(door.IsOpen);
    }

    [Fact]
    public void WhenAutoCloseIsZero_ThenTheDoorStaysOpen()
    {
        // Arrange
        var door = _context.AddDoor(0, 10);
        _context.Tick();

        // Act
        door.Open();
        _context.Tick();
        _context.TimeProvider.Advance(TimeSpan.FromMinutes(1));
        _context.Tick();

        // Assert
        Assert.True(door.IsOpen);
    }
```

`AddObject` writes `Game.Objects`, which the game takes over in the next tick and calls `OnAddedToWorld` for the new door. `player.X = 20` is a teleport through the write interceptor.

- [ ] **Step 2: Implement**

```csharp
    /// <summary>
    /// The time after which an opened door closes itself; zero keeps it open.
    /// </summary>
    [Configuration]
    public partial TimeSpan AutoCloseDelay { get; set; }

    public void Open()
    {
        if (IsOpen)
        {
            throw new InvalidOperationException("The door is already open.");
        }

        IsOpen = true;
        ScheduleAutoClose();
    }

    protected override void OnAddedToWorld()
    {
        if (IsOpen)
        {
            ScheduleAutoClose();
        }
    }

    private void ScheduleAutoClose()
    {
        if (AutoCloseDelay > TimeSpan.Zero)
        {
            World.Schedule(this, AutoCloseDelay, CloseOrTryAgain);
        }
    }

    // Runs on the game's thread; a doorway that is still occupied is tried again after another delay.
    private void CloseOrTryAgain()
    {
        if (!IsOpen)
        {
            return;
        }

        if (World.IsFree(Area, this))
        {
            IsOpen = false;
        }
        else
        {
            ScheduleAutoClose();
        }
    }
```

Initialize `AutoCloseDelay = TimeSpan.Zero` in the constructor. Check that the subject browser edits a `TimeSpan` (`HomeBlaze.Components/Editors/PropertyEditor.razor` handles it); add `[Range]`-like validation only if the editor allows negative values: reject them in `OnAutoCloseDelayChanging` by clamping to zero.

- [ ] **Step 3: Run the tests, commit and push**

```bash
git add src/HomeBlaze/HomeBlaze.Game.Buildings src/HomeBlaze/HomeBlaze.Game.Buildings.Tests
git commit -m "feat: let doors close themselves after a configurable delay"
git push
```

Controller: restart the app.

---

### Task 9: View sessions with chunk interest and backpressure

**Files:**
- Create: `HomeBlaze.Game/Simulation/GameViewSession.cs`, `HomeBlaze.Game/View/IGameViewSink.cs`, `HomeBlaze.Game/View/GamePanelState.cs`
- Modify: `HomeBlaze.Game/View/GameViewTracker.cs`, `HomeBlaze.Game/Game.World.cs` (session API; remove `UpdateViewAsync`, `GetTargetsAsync` and `WorldChanged`), `Components/GamePage.razor`
- Test: `HomeBlaze.Game.Tests/Simulation/GameViewSessionTests.cs`, adapt `View/GameViewTrackerTests.cs`

- [ ] **Step 1: The sink and the panel state**

```csharp
namespace HomeBlaze.Game.View;

/// <summary>
/// Where a view session delivers its updates; implemented by the page.
/// </summary>
internal interface IGameViewSink
{
    /// <summary>
    /// Whether the previous update is still on its way to the browser; the session skips the page meanwhile.
    /// </summary>
    bool IsBusy { get; }

    /// <summary>
    /// Takes an update; called on the simulation thread, must not block.
    /// </summary>
    void Deliver(GameViewUpdate update, GamePanelState panel);
}
```

`GamePanelState` is an immutable record: `string PositionText` (as `RefreshPosition` builds it today), `IReadOnlyList<GameObject> Targets`, `GameDirection? Facing`, `bool HasPlayer`, `string? PlayerClientId`. Nearby stays driven by `VolumeTableChanged` and the volume table as today.

- [ ] **Step 2: Write the failing session tests**

`GameViewSessionTests` against `GameTestContext` with a `RecordingViewSink` (in `Testing/`, records deliveries, `IsBusy` settable):
- `WhenASessionJoins_ThenItsFirstUpdateHoldsEverythingInView`;
- `WhenAnObjectInViewChanges_ThenOnlyItIsUpserted` (open a door; the next delivery upserts exactly the door);
- `WhenAnObjectOutsideTheViewChanges_ThenNothingIsDelivered` (a door 300 tiles away);
- `WhenThePlayerCrossesAChunkBoundary_ThenTheEnteringStripIsUpsertedAndTheLeavingStripRemoved`;
- `WhenTheSinkIsBusy_ThenChangesAccumulateAndArriveInOneUpdate` (busy during two door toggles and a move, then not busy: one delivery with the final state);
- `WhenAnObjectIsRemoved_ThenItsIdIsRemoved`;
- `WhenAMultiChunkWallChangesInOneChunkOfTheWindow_ThenItStaysKnownOnce` (a 40 tile wall crossing chunks);
- `WhenThePlayerTurns_ThenTheTargetsInThePanelFollow`.

- [ ] **Step 3: Implement the session**

`GameViewSession` (simulation thread only) holds the tracker, the sink, the client id (nullable while the page has no player), the current window (`minimumChunk`, `maximumChunk` from the player's center and `GameViewTracker.ViewRadius`), its accumulated changed chunks (`HashSet<(long, long)>`) and a flag for a full refresh. The tracker gets a partial update:

`GameViewTracker.UpdateChunks(GameWorldIndex index, IReadOnlyCollection<(long X, long Y)> chunks, (long X, long Y) minimumChunk, (long X, long Y) maximumChunk, PlayerEntity? self, ILogger logger)`:
- keeps per known entity the number of window chunks it was seen in at its last update (`Dictionary<GameEntity, int>`) and per window chunk its known entities (`Dictionary<(long, long), List<GameEntity>>`);
- for each given chunk inside the window: queries the chunk's visible entities, upserts the changed ones, updates the chunk's list and the counts (an entity leaving the chunk decrements, entering increments);
- for each chunk that left the window since the last call: removes the chunk's list and decrements;
- removes every entity whose count reached zero (its id goes to `Removed`);
- a full refresh (first update, or a window jump larger than one chunk) treats every chunk of the window as given.

Each tick, after the world changes are applied, the game adds `Index.Changes` to every session's accumulated set, computes the new window from the player's entity, and for each session whose sink is not busy calls `UpdateChunks` with the accumulated chunks plus the chunks that entered the window, delivers the result with a fresh `GamePanelState` (targets recomputed only when the player moved, turned, or a chunk in front of it changed), and clears the accumulated set. Then `Index.Changes.Clear()` (after the snapshot and the sessions used it).

Session API on `Game`: `Task<IDisposable> OpenViewAsync(string? clientId, IGameViewSink sink)` (an action registers the session; disposing posts its removal); `SetViewPlayer(session, clientId)` when the page's player appears or changes.

- [ ] **Step 4: The page uses the session**

`GamePage` implements the sink with a pending flag (`IsBusy` while an `applyUpdate` call is in flight, at most one at a time), queues the delivered update and panel state, and applies them on its renderer with `InvokeAsync`; the full `GameViewTracker` and the subscription to `WorldChanged` disappear. Targets come from the panel state; their operations are read from the subjects as today.

- [ ] **Step 5: Run all game tests including E2E, commit and push**

```bash
git add -A src/HomeBlaze/HomeBlaze.Game src/HomeBlaze/HomeBlaze.Game.Tests
git commit -m "perf: send each page only the changed chunks in its view"
git push
```

Controller: restart the app.

---

### Task 10: Split the page

**Files:**
- Create: `HomeBlaze.Game/Components/GamePage.razor.cs` (code-behind), `HomeBlaze.Game/Components/GameViewConnection.cs`, `HomeBlaze.Game/Components/GameVoiceConnection.cs`
- Modify: `HomeBlaze.Game/Components/GamePage.razor`

- [ ] **Step 1: Extract the view connection**

`GameViewConnection` (internal sealed class, implements `IGameViewSink` and `IAsyncDisposable`) owns the game module reference, the view id, the session from Task 9, the pending update and the busy flag, `Step` forwarding, and raises `PanelChanged` for the page. The page creates it after the module started and passes the canvas, options and `DotNetObjectReference`.

- [ ] **Step 2: Extract the voice connection**

`GameVoiceConnection` (internal sealed class, `IAsyncDisposable`) owns the voice module, the toggles and their state (`IsMicrophoneOn`, `IsAudioOn`, `IsSupported`, `IsBusy`, `Error`, `IsAudioSuspended`), the sink registration and the pending sends, and everything from `StartVoiceAsync` to `StopVoiceInGame` in today's page. It raises `Changed` for re-rendering. The page keeps the `[JSInvokable]` methods (Blazor binds them to the `DotNetObjectReference` of the page) and forwards them.

- [ ] **Step 3: Keep the markup in the `.razor` file, the rest in the code-behind**

`GamePage.razor` keeps directives, injections and markup; `GamePage.razor.cs` holds the partial class with lifecycle, panel refresh, actions, renaming and the records. Target size: no file above about 400 lines.

- [ ] **Step 4: Run all game tests including E2E (the page has no unit tests; the E2E tests cover it), commit and push**

```bash
git add -A src/HomeBlaze/HomeBlaze.Game/Components
git commit -m "refactor: split the game page into view and voice connections"
git push
```

---

### Task 11: One-shot animations in the browser

**Files:**
- Modify: `HomeBlaze.Game/wwwroot/game-entities.js`
- Test: `HomeBlaze.Game.Tests/E2E/GamePageTests.cs` (covered in Task 12's animal test), `View/GameViewTrackerTests.cs` (from Task 1)

- [ ] **Step 1: Play `Once` animations once per change**

In `game-entities.js` where a model part starts its animation group (search `animation`), keep per part the last animation name and mode. For `Loop` (0) keep today's `group.start(true)`. For `Once` (1): when the part is created, or its `animation` value differs from the last one, call `group.start(false)` and, in `onAnimationGroupEndObservable`, `group.goToFrame(group.to)` and `group.pause()` so the last frame holds; an unchanged name does nothing. Stop the previous group of the part before starting another. Expose `data-game-animations-once` on the stats element (count of once-animations started) for tests.

- [ ] **Step 2: Manual check, commit and push**

The E2E test of Task 12 covers it. Commit:

```bash
git add src/HomeBlaze/HomeBlaze.Game/wwwroot/game-entities.js
git commit -m "feat: play model animations once when a visual asks for it"
git push
```

---

### Task 12: The animal sample

**Files:**
- Create: `HomeBlaze.Game.Nature/Animal.cs`, `HomeBlaze.Game.Nature/AnimalVariant.cs`, `HomeBlaze.Game.Nature/wwwroot/objects/nature/animals/*.glb` and `LICENSE.md`
- Modify: the demo world generator and `Game.json` (a few animals in the forest), `HomeBlaze.Game.Tests/DemoWorldTests.cs`, `HomeBlaze.Game.Tests/E2E/GamePageTests.cs`
- Test: `HomeBlaze.Game.Nature.Tests/AnimalTests.cs`

- [ ] **Step 1: Pick the models**

Find one or two CC0 glTF/GLB animals with built-in idle, walk and eat animations (first choice: Quaternius animated animals; check the license text says CC0 and download from the author's official source or poly.pizza). Download into a new empty folder in the scratchpad, inspect the animation names with a small Node or Python script (`pygltflib` or reading the JSON chunk of the GLB), keep files below 1 MB each where possible, and copy them to `HomeBlaze.Game.Nature/wwwroot/objects/nature/animals/` with a `LICENSE.md` naming the author, source URL, pinned version or date, and the CC0 text, like `objects/nature/models/LICENSE.md`. Record the animation names in `AnimalVariant`.

- [ ] **Step 2: Write the failing tests**

`AnimalTests` (Nature tests, `GameTestContext`):
- `WhenAnAnimalIsAddedToTheWorld_ThenItStepsWithinTheStepDelay` (advance 6 s with ticks; its position changed by one tile in one axis or both);
- `WhenEverySideIsBlocked_ThenItStaysAndTriesAgain` (walls around it; position unchanged after 30 s, no exception logged at warning level);
- `WhenFed_ThenTheVisualPlaysEatOnceAndReturnsToIdle` (`Feed()` sets the eating state; `Visual.Parts[0].Animation == "Eat"` with `AnimationMode.Once`; after the eat duration it is `"Idle"` with `Loop`);
- `WhenItStepsEast_ThenItsRotationFacesEast`;
- `WhenRemoved_ThenItStopsStepping` (no schedule runs after removal; assert via position and no errors).

Use a seeded `Random` passed through an internal constructor or a `[Configuration] int Seed` only if needed for determinism; prefer making the tests robust to any direction instead.

- [ ] **Step 3: Implement `Animal`**

```csharp
[Category("Game")]
[Description("An animal that wanders around and eats when fed")]
[DisplayName("Animal")]
[SubjectIcon("Pets")]
[InterceptorSubject]
public partial class Animal : GameObject, ITitleProvider, IIconProvider
{
    private static readonly TimeSpan EatDuration = TimeSpan.FromSeconds(2);
    private readonly GameVisualCache<(AnimalVariant Variant, GameDirection Facing, bool IsEating)> _visual = new();

    [Configuration]
    public partial AnimalVariant Variant { get; set; }

    [State]
    public partial GameDirection Facing { get; set; }

    [State]
    public partial bool IsEating { get; set; }

    public override int Width => 2;

    public override int Depth => 2;

    public override int Height => 3;

    public string? Title => Variant.ToString();

    public string? IconName => "Pets";

    public override GameVisual Visual => _visual.Get((Variant, Facing, IsEating), static state => CreateVisual(state.Variant, state.Facing, state.IsEating));

    [Operation(Title = "Feed", Icon = "Restaurant", Position = 1, Description = "Gives this animal something to eat.")]
    public void Feed()
    {
        IsEating = true;
        World.Schedule(this, EatDuration, () => IsEating = false);
    }

    protected override void OnAddedToWorld() => ScheduleNextStep();

    private void ScheduleNextStep() =>
        World.Schedule(this, TimeSpan.FromSeconds(Random.Shared.Next(2, 7)), Step);

    // Runs on the game's thread; a blocked step is simply skipped.
    private void Step()
    {
        if (!IsEating)
        {
            var direction = (GameDirection)Random.Shared.Next(8);
            if (Area.TryMove(direction.GetOffsetX(), direction.GetOffsetY(), out var next) && World.IsFree(next, this))
            {
                (X, Y, Facing) = (next.X, next.Y, direction);
            }
        }

        ScheduleNextStep();
    }
}
```

`CreateVisual` returns one `GameVisualPart` of shape `Model` with the variant's model URL (`GameAssets.Url(typeof(Animal), "objects/nature/animals/<file>.glb")`), size (2, 3, 2), pivot at the center, rotation from the facing (north 0, clockwise 45 per step), `Animation = isEating ? variant.EatAnimation : variant.IdleAnimation` and `AnimationMode = isEating ? Once : Loop`. Walking animation: the 300 ms glide moves it; play `Walk` only if the model has it and it reads well, otherwise keep idle during the glide (decide when you see the model).

The animal does not block movement for itself checks (`IsFree(next, this)`), and blocks players (default `IsMovementBlocking`).

- [ ] **Step 4: Demo world and E2E**

Add three or four animals in the forest to the generator, regenerate, stage via the index; `DemoWorldTests` asserts their count and that each stands on free tiles. Add an E2E test `WhenTheDemoWorldLoads_ThenTheAnimalModelsLoadWithoutFailures` (walk close to an animal or place one in the test world next to the spawn; `data-game-model-failures` stays 0 and `data-game-models` grows).

- [ ] **Step 5: Run all game tests including E2E, commit and push**

```bash
git add -A src/HomeBlaze/HomeBlaze.Game.Nature src/HomeBlaze/HomeBlaze.Game.Nature.Tests src/HomeBlaze/HomeBlaze.Game.Tests
git update-index --cacheinfo 100644,$(git hash-object -w <scratchpad>/world/Game.json),src/HomeBlaze/HomeBlaze/Data/Files/Demo/Game.json
git commit -m "feat: add wandering animals as a sample of scheduled behavior and animations"
git push
```

Controller: restart the app and tell the user about the animals and the auto-closing door.

---

### Task 13: The benchmark

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Game.Benchmark/HomeBlaze.Game.Benchmark.csproj`, `Program.cs`, `BenchmarkWorld.cs`, `SimulationTickBenchmark.cs`
- Modify: `src/Namotion.Interceptor.slnx`, `HomeBlaze.Game/HomeBlaze.Game.csproj` (`InternalsVisibleTo` for the benchmark, as allowed for benchmark projects)

- [ ] **Step 1: The project**

Model it on `src/Namotion.Interceptor.Benchmark/Namotion.Interceptor.Benchmark.csproj` (BenchmarkDotNet package, `OutputType Exe`, `IsPackable false`), referencing `HomeBlaze.Game`, `HomeBlaze.Game.Buildings`, `HomeBlaze.Game.Nature`, `HomeBlaze.Services` (for `SubjectContextFactory`) and `Microsoft.Extensions.TimeProvider.Testing`. `Program.cs`: `BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);`.

- [ ] **Step 2: The world**

`BenchmarkWorld` builds a game in a root like `GameTestContext` (fake clock, a fake client directory with 200 connected clients), tiles the demo world's walls, doors and trees until 100k objects exist (read `src/HomeBlaze/HomeBlaze/Data/Files/Demo/Game.json` and offset copies by the house size plus a margin on a grid), spreads the 200 players over the tiled area in groups of about 10 near each other, and opens 200 view sessions with a sink that is never busy and discards updates.

- [ ] **Step 3: The benchmarks**

```csharp
[MemoryDiagnoser]
public class SimulationTickBenchmark
{
    private BenchmarkWorld _world = null!;

    [GlobalSetup]
    public void Setup() => _world = BenchmarkWorld.Create(players: 200, objects: 100_000);

    /// <summary>
    /// One tick in which every player takes a step, with all view sessions updating.
    /// </summary>
    [Benchmark]
    public void TickWithAllPlayersWalking() => _world.TickWithAllPlayersStepping();

    /// <summary>
    /// One publish of 200 moved players to their subjects.
    /// </summary>
    [Benchmark]
    public void PublishMovedPlayers() => _world.PublishAllPlayers();

    /// <summary>
    /// One view session update with 10 other players moving in view.
    /// </summary>
    [Benchmark]
    public void ViewSessionWithTenMovingPlayers() => _world.UpdateOneSessionWithTenMovers();
}
```

`TickWithAllPlayersStepping` posts one step per player (alternating east and west so they stay in place over time, advancing the fake clock by `StepInterval` so the rate limiter allows it) and runs one tick.

- [ ] **Step 4: Run it and record the numbers**

Run: `dotnet run -c Release --project src/HomeBlaze/HomeBlaze.Game.Benchmark -- --filter "*SimulationTickBenchmark*"`. This Release build is a separate output folder; it does not touch the app's binaries, but run it only while the user is not testing, or stop the app first and restart it afterwards. Expected: `TickWithAllPlayersWalking` mean under 5 ms. If it is above, profile (`--profiler EP` or `dotnet-trace`), fix the hot spot in the simulation, and run again. Save the summary table for the pull request body.

- [ ] **Step 5: Commit and push**

```bash
git add src/HomeBlaze/HomeBlaze.Game.Benchmark src/Namotion.Interceptor.slnx src/HomeBlaze/HomeBlaze.Game/HomeBlaze.Game.csproj
git commit -m "test: add a benchmark of the game simulation at 200 players and 100k objects"
git push
```

---

### Task 14: Documentation

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Game.Abstractions/README.md`
- Modify: `src/HomeBlaze/HomeBlaze.Game/README.md`
- Delete: `src/HomeBlaze/docs/superpowers/specs/2026-11-01-game-simulation-design.md`, `src/HomeBlaze/docs/superpowers/plans/2026-11-01-game-simulation.md` (and the `docs/superpowers` folders if empty)

- [ ] **Step 1: The author guide**

`HomeBlaze.Game.Abstractions/README.md`, sections:
1. What a game object is (a subject deriving from `GameObject`; tools derive from `GameTool`).
2. The four rules (describe with properties; change by writing properties; everything else through `World`; time-driven behavior in `OnAddedToWorld` with `Schedule`), with the guarantee that writes never wait and are never refused by the game, and the `IsFree` check where a rule matters.
3. A complete door (the real `Door` code trimmed to its essence: properties, blocking overrides, visual, Open, Close with `IsFree`, auto close).
4. A complete tool (`TreeTool`).
5. Visuals: parts, boxes and models, offsets, pivots, rotation, textures, tints, `Wind`, animations with `Loop` and `Once`, caching with `GameVisualCache`, the validation limits (sizes up to 1024 tiles, 16 parts, URLs under `_content/`, no `..` or `%`) and the gray box fallback.
6. Assets and licenses: `GameAssets.Url(typeof(MyObject), "objects/...")`, `wwwroot` of a Razor class library, a `LICENSE.md` next to every external asset.
7. Packaging as a plugin: a Razor class library that references only `HomeBlaze.Game.Abstractions` and `HomeBlaze.Abstractions`; the host registers the assembly; static assets of runtime-loaded plugins need the plugin static assets follow-up.
8. Testing an object against a real game (`GameTestContext`-like setup: a game in a root, ticks with a fake clock).

- [ ] **Step 2: The core README**

Replace the threading section with a simulation section: tick order (from the spec), the action queue, `GameSimulation.Current`, the facade (publishing, the write interceptor and its rules, refused writes to simulation-owned player properties), persistence (10 s, paused publishing, at most 10 s of positions lost on a crash), views (sessions, chunk interest, backpressure), voice (own loop and lock, volume table from the simulation). Update the project overview (four projects), the extension points (link to the author guide instead of repeating it), the asset table paths, the scale section with the benchmark numbers, the known limits, and the follow-ups: plugin static assets (separate pull request on `master`; loading a game extension from a NuGet package is verified once it lands), the 1,000+ players scale (storage other than JSON, several processes), saving schedules, the two-device echo test from #876.

- [ ] **Step 3: Delete the working spec and plan, commit and push**

```bash
git rm -r src/HomeBlaze/docs/superpowers
git add src/HomeBlaze/HomeBlaze.Game.Abstractions/README.md src/HomeBlaze/HomeBlaze.Game/README.md
git commit -m "docs: document the game simulation and how to build game objects"
git push
```

---

### Task 15: Final verification and the draft pull request

- [ ] **Step 1: Full verification**

Run, all with `-c Debug`: `dotnet build src/Namotion.Interceptor.slnx -c Debug`; the unit tests of `HomeBlaze.Game.Tests`, `HomeBlaze.Game.Buildings.Tests`, `HomeBlaze.Game.Nature.Tests`, `HomeBlaze.Game.Abstractions.Tests`, `HomeBlaze.Components.Tests`, `HomeBlaze.Services.Tests`; the E2E tests of `HomeBlaze.Game.Tests`. Expected: all pass. Record the counts.

- [ ] **Step 2: Check the removals and conventions**

Run: `grep -rn "GetGame()\|ResolveActor\|GamePlacement\|UpdateAsync(\|GameObjectSnapshot\|namePrefix" src/HomeBlaze/HomeBlaze.Game* ` (expected: no matches) and `grep -rn "HomeBlaze" src --include=*.cs --include=*.md -l | grep -v "^src/HomeBlaze/"` (expected: no new matches outside `src/HomeBlaze`).

- [ ] **Step 3: Final review**

Dispatch the final code reviewer over `git diff feat/homeblaze-game...HEAD`, fix findings, re-run Step 1.

- [ ] **Step 4: Open the draft pull request**

Read `.github/pull_request_template.md` and fill it into a body file in the scratchpad: summary (simulation owns the state, facade, extension libraries, author API, animals, auto-closing doors), contract (the author rules, outside writes never refused except the simulation-owned player properties, saving every 10 s, schedules not saved), breaking changes (the `$type` names, asset paths, the extension API), diff composition table, verification (counts from Step 1, benchmark table from Task 13). No AI attribution in the body.

```bash
gh pr create --draft --base feat/homeblaze-game --head feat/homeblaze-game-simulation \
  --title "refactor: run the HomeBlaze game on a simulation and split out game object libraries" \
  --body-file <scratchpad>/pr-body-simulation.md --label <area label from gh label list> --label <type label>
```

Controller: restart the app, give the user the PR link and the app URLs.
