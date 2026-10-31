# Game World Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement the approved spec `src/HomeBlaze/docs/superpowers/specs/2026-10-31-game-world-design.md`: data-driven object visuals (`GameVisual`), configurable tools (`GameTool`), a public object and placement API, walls with size and material, doors added to walls, trees with wind, remembered dialog parameters, and a new demo world with a house and a forest.

**Architecture:** The core (`HomeBlaze.Game`, `Visuals/`, `Tools/`, `World/`, `View/`, `Voice/`) knows no object type. Objects live in `Objects/Buildings/` and `Objects/Nature/` and use only public members of the core: they describe their look as a value-compared `GameVisual`, act through `Game.RemoveAsync`, `Game.UpdateAsync`, `Game.ReplaceAsync`, `Game.GetPlacementInFront` and `Game.PlaceAsync`, and contribute tools as `GameTool` subjects in `Game.Tools`. The view tracker sends the parts of each visual with short JSON names, and `game-entities.js` renders any object from its parts (boxes with shared materials, glTF models loaded once per URL, a wind material plugin), so walls, doors and trees need no special rendering.

**Tech Stack:** .NET 10, C# 14, Namotion.Interceptor source generator and registry, Blazor Server with MudBlazor 9.11, Babylon.js 9.31 (vendored UMD), xUnit, Moq (Components tests), Playwright (.NET) E2E, Python 3 with Pillow for asset preparation and the world generator (scratchpad only).

---

## Conventions

Every task follows these rules; reviewers check them.

- Run every command from the repository root `/home/rico/GitHub/Namotion.Interceptor/.claude/worktrees/bridge-cse_011ShuL5XgwWZqRmSZFhkZve`. Never `cd` to another checkout.
- The scratchpad is `/tmp/claude-1000/-home-rico-GitHub-Namotion-Interceptor--claude-worktrees-bridge-cse-011ShuL5XgwWZqRmSZFhkZve/3d51e483-5513-56d7-b4a2-5748ff2f1414/scratchpad`. Shell state does not persist between calls, so every command block that uses it starts with `SCRATCH=/tmp/claude-1000/-home-rico-GitHub-Namotion-Interceptor--claude-worktrees-bridge-cse-011ShuL5XgwWZqRmSZFhkZve/3d51e483-5513-56d7-b4a2-5748ff2f1414/scratchpad`.
- Build and test ONLY with `-c Debug`, because a live app runs from the Release output of `src/HomeBlaze/HomeBlaze`:
  - Build: `dotnet build src/HomeBlaze/HomeBlaze.Game.Tests -c Debug`
  - Unit tests: `dotnet test src/HomeBlaze/HomeBlaze.Game.Tests -c Debug --filter "FullyQualifiedName!~E2E"`
  - E2E tests: `dotnet test src/HomeBlaze/HomeBlaze.Game.Tests -c Debug --filter "FullyQualifiedName~E2E"`
  - When touching `HomeBlaze.Components`: `dotnet test src/HomeBlaze/HomeBlaze.Components.Tests -c Debug`
- Sonar warnings are errors (S134, S1541, S1067, S3776, S107, S1200, S2234, S3241 fired before in this branch; `src/.editorconfig` also enables S104 and S138). `Game` is a large partial class, so watch S1200 (class coupling) when the object API and placement files add types to it. Keep methods small, boolean expressions at most 3 operators, nesting at most 3, never pass a variable to a parameter of another parameter's name (S2234: name locals `sizeX`/`sizeY` rather than `width`/`depth` when they go to other positions). Never suppress a rule; if a rule forces a costly change, stop and report.
- Style (AGENTS.md): US English, no em dashes, no hard wrapping in markdown, no abbreviations in names, comments only for the why a reader cannot derive, XML docs state the contract, tests named `When<Condition>_Then<Expected>` with `// Arrange`, `// Act`, `// Assert` (or `// Act & Assert`), no fixed waits in tests (use `AsyncTestHelpers.WaitUntilAsync` or Playwright expectations).
- Object folders (`Objects/Buildings`, `Objects/Nature`) use only public (and protected) members of the core. Their own `internal` members (for example `WallTextures`, `Tree.Variants`, the `TreeTool(Random)` constructor) are fine. Reviewers check this rule on every task that touches those folders.
- Git: stage explicit paths only (`git add <path> ...`, `git mv`, `git rm`), never `git add -A` or `.`. Never stage anything under `src/HomeBlaze/HomeBlaze/Data` EXCEPT `src/HomeBlaze/HomeBlaze/Data/Files/Demo/Game.json` in the tasks that say so. Commit with a `feat:`, `test:`, `docs:` or `refactor:` prefix and the trailer as a second `-m`: `git commit -m "feat: ..." -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"`. Push after every task: `git push origin feat/homeblaze-game`.
- The controller (not implementers) restarts the app after every task marked **Runnable**: first `pkill -f "[u]rls http://192.168.1.201"` in its own call, then from `src/HomeBlaze/HomeBlaze` run in the background `ASPNETCORE_ENVIRONMENT=Development dotnet run -c Release --no-launch-profile --urls "http://192.168.1.201:5280;https://192.168.1.201:5281"`. The page is `/pages/Demo/Game`.

## Decisions taken while planning

These resolve gaps or ambiguities of the spec; the README (Task 10) documents them.

1. `Game.Place` is `Task PlaceAsync(GameCaller? caller, string namePrefix, GameObject gameObject, CancellationToken cancellationToken)` instead of `void Place(...)`, so placing writes immediately like every other action (README persistence rule) and the API matches `RemoveAsync`, `UpdateAsync` and `ReplaceAsync`. `GetPlacementInFront` and `PlaceAsync` take `GameCaller?` so tools pass their `[FromServices] GameCaller? caller` straight through.
2. `GetPlacementInFront(caller, across, along)` takes the extent across the facing direction first and the extent along it second. The footprint starts on the tile right in front of the player along the facing axis and is centered on the player's center across it (for an odd extent the extra half tile goes east or north, as `WallPlacement` did). `WallTool` calls `GetPlacementInFront(caller, across: length, along: 1)`, so a wall stands across the facing as today, a barrier in front of the player: a length of 40 reaches about 20 tiles to each side. Trees call it with `across: 2, along: 2`. (The user chose across; the spec wording was updated accordingly.)
3. Generic refusal messages: "Only a player in the game can place objects.", "Face north, east, south or west to place objects." and "Something is in the way." (also for a footprint outside the `int` range). Closing a door with someone in the doorway now says "Something is in the way." instead of "Someone is in the way of the door.".
4. Tool and door parameters are nullable (`int? length`, `int? height`, `WallMaterial? material`, `int? variant`, `int? width`): an empty dialog field means the default (4, 10, `Bricks`, random, 4). The dialog has no notion of parameter defaults, so this is how "default 4" reaches both the dialog and MCP. `[Range]` documents the limits; `MethodParameterDialog` does not enforce `[Range]`, the operations check again on the server.
5. `OperationInvoker.InvokeAsync` gets a second optional parameter, `Action<IReadOnlyDictionary<string, object?>>? parametersEntered`, besides `initialValues`, because the page cannot see the dialog result otherwise ("the values are taken from the dialog result"). The page keys the remembered values by object type and operation (`$"{subject.GetType().FullName}.{method.PropertyName}"`), so "Add door" remembers its width across walls. Only parameters entered in one field (`InputKind.Simple`) are prefilled.
6. `Door` gets `[Configuration] WallMaterial Material` (default `Bricks`) for its lintel, and "Add door" copies the wall's material, so a door in a concrete wall has a concrete lintel. The spec's "brick lintel" holds for the default.
7. Without an actor (subject page, MCP), "Add door" centers the door on the wall instead of refusing.
8. The game's change observer (`Game.Runtime.cs`) no longer names `Door.IsOpen`: any outside change of an object of this game requests an index rebuild, so the core knows no object type.
9. Model parts: a model is scaled uniformly to the largest size whose bounding box fits in the part's `Size`, with its own origin at the horizontal center of the part's box and its lowest point at the box's bottom. Rotation is in degrees clockwise seen from above (north toward east), like the player facing; the door opens to -90. `Color` tints boxes only.
10. Model parts are cloned meshes sharing geometry and materials (`instantiateModelsToScene(..., false, { doNotInstantiate: true })`) rather than `InstancedMesh`, because instances of a source mesh that is not in the scene are unreliable. Wind uses a per-material clone with the plugin, cached per source material.
11. Tree models come from the CC0 "low poly nature pack" by flo-bit on GitHub, not from Kenney or Quaternius: `kenney.nl` and `opengameart.org` answer automated downloads with 403, and Quaternius distributes through Google Drive. The flo-bit pack is CC0 per its repository and pack README, ships self-contained `.glb` files (no textures, no extensions, no animations) and is pinned to a commit with SHA-256 checksums below.
12. The bricks texture is re-encoded from 1024 x 512 (2 m by 1 m of bricks) to a square 512 x 512 of two stacked copies, so one `TextureScale` (tiles per repeat in both directions) keeps today's look at 8 tiles per repeat.
13. The demo world spawn point is 0, 0, just outside the open entrance door of the house: `ReconcilePlayers` already starts new players at 0, 0.
14. The local private `Game.json` is backed up and released from skip-worktree at the start of Task 3 (not Task 8), because Task 3 changes the `$type` names, so the private file would stop loading, and `git add` does not stage a skip-worktree file.

## Verified assets

| Asset | Source | License | Notes |
|---|---|---|---|
| Concrete wall texture | `https://ambientcg.com/get?file=Concrete046_1K-JPG.zip`, file `Concrete046_1K-JPG_Color.jpg` (redirects to `acg-download.struffelproductions.com`, 200, zip) | CC0 1.0 (ambientCG) | 1024 x 1024, 2.4 m by 2.4 m of cast concrete; re-encoded to 512 x 512, about 35 KB; 10 tiles per repeat |
| Wood wall texture | `https://ambientcg.com/get?file=Planks021_1K-JPG.zip`, file `Planks021_1K-JPG_Color.jpg` | CC0 1.0 (ambientCG) | 1024 x 1024, 1.4 m of horizontal planks; re-encoded to 512 x 512, about 44 KB; 6 tiles per repeat |
| Bricks texture | Existing `wwwroot/textures/bricks.jpg` (ambientCG Bricks097) | CC0 1.0 | Stacked to 512 x 512, about 87 KB; 8 tiles per repeat |
| Tree models | `https://raw.githubusercontent.com/flo-bit/low-poly-asset-packs/61bbdb032e079c7276a6558cbedc3ef1ea8b1575/nature-pack/glb/<name>.glb` | CC0 (repository README "All packs are licensed under CC0", pack README "license cc0") | Self-contained GLB (Blender glTF I/O 4.2), vertex-colored PBR materials, no textures, no animations, origin at the trunk base |

Tree model checksums (SHA-256) and sizes:

| Variant | File | Bytes | SHA-256 | Height in tiles |
|---|---|---:|---|---:|
| 0 | `common_tree_1.glb` | 245300 | `e17c3742e9251af93f03dee12d7d94c3ab15b78d61661e1a86b704f1f725f9ea` | 24 |
| 1 | `common_tree_2.glb` | 226772 | `8451a97982c5e43641dce7cbea4624f68f22f6048c1a13f004d21e1e3c7b9d02` | 21 |
| 2 | `common_tree_3.glb` | 318884 | `129a861f0eb715ff196791f93fe965a46833f945803c52c9fbd8d4df1c2e97a2` | 24 |
| 3 | `common_tree_1_autumn.glb` | 245324 | `c629ed59bafd2ef839850d2f5daddee49367ccb3fa10648568922b7f1ee978c9` | 22 |
| 4 | `pine_tree_1.glb` | 134788 | `d37154637ccd55a5ff0cf53df7645f0503fd0b9bd81900bce8bc92624b2e514a` | 22 |
| 5 | `pine_tree_2.glb` | 149416 | `a8d8e81d34713b79a5e0ee846281f2a4dcc5bcb6181c9b7c821fd188751310b3` | 24 |
| 6 | `pine_tree_3.glb` | 100372 | `06fd84104dcacc73fbf214d8c295263f6e94698b7874bb991693c7173dfa269f` | 16 |

## File structure

```
src/HomeBlaze/HomeBlaze.Game/
├── Game.cs                         (modify: Tools property)
├── Game.Actions.cs                 (modify: only ResolveActor, messages remain)
├── Game.Objects.cs                 (create: RemoveAsync, UpdateAsync, ReplaceAsync, name helpers)
├── Game.Placement.cs               (create: GetPlacementInFront, PlaceAsync)
├── Game.Runtime.cs                 (modify: generic outside-change rule)
├── Game.View.cs                    (modify: passes the logger to the tracker)
├── GameObject.cs                   (modify: Visual, GetGame)
├── GameObjectSnapshot.cs           (create: undo for UpdateAsync)
├── Visuals/                        (create: GameVisual, GameVisualPart, GameVisualVector, GameVisualPoint,
│                                    GameVisualShape, GameVisualEffect, GameVisualCache, GameAssets, GameVisualValidator)
├── Tools/                          (create: GameTool, GamePlacement)
├── Objects/Buildings/              (Wall.cs, Door.cs moved here; WallTextures, WallMaterial, WallTool created)
├── Objects/Nature/                 (create: Tree, TreeTool)
├── World/FrontPlacement.cs         (renamed from WallPlacement.cs, generalized)
├── View/GameViewObject.cs          (modify: Visual, Parts; no IsOpen, no wall/door kinds)
├── View/GameViewPart.cs            (create: short JSON names)
├── View/GameViewTracker.cs         (modify: visual compare, validation)
├── View/GameColors.cs              (modify: player colors only)
├── Components/GamePage.razor       (modify: tool actions, order, remembered parameters)
└── wwwroot/
    ├── game.js                     (modify: stats element)
    ├── game-entities.js            (rewrite: generic parts, models, wind, fading)
    ├── textures/                   (grass only; LICENSE.md updated)
    ├── objects/buildings/textures/ (bricks.jpg moved and restacked, concrete.jpg, wood.jpg, LICENSE.md)
    └── objects/nature/models/      (7 tree .glb files, LICENSE.md)
src/HomeBlaze/HomeBlaze.Components/
├── Operations/OperationInvoker.cs  (modify: initialValues, parametersEntered)
└── Dialogs/MethodParameterDialog.razor (modify: InitialValues, FormatInitialValue)
src/HomeBlaze/HomeBlaze.Components.Tests/
├── MethodParameterDialogTests.cs   (create)
├── OperationInvokerTests.cs        (modify)
└── VerifyChecksTests.PublicApi.verified.txt (accept new snapshot)
src/HomeBlaze/HomeBlaze.Game.Tests/
├── HomeBlaze.Game.Tests.csproj     (modify: global usings)
├── Testing/                        (GameTestContext modified; VisualObject, WarnedVisualObject, RecordingLogger, RepositoryPaths created)
├── Visuals/                        (GameVisualTests, GameAssetsTests, GameVisualValidatorTests)
├── Objects/Buildings/              (WallTests, DoorTests (moved from GameDoorTests), WallToolTests, WallDoorTests)
├── Objects/Nature/                 (TreeTests, TreeToolTests)
├── World/FrontPlacementTests.cs    (renamed from WallPlacementTests.cs)
├── GameObjectsTests.cs, GamePlacementTests.cs, DemoWorldTests.cs (create)
├── GameActionsTests.cs, GameSerializationTests.cs, GameVolumeTableTests.cs, View/GameViewTrackerTests.cs (modify)
└── E2E/GamePageTests.cs            (modify)
src/HomeBlaze/HomeBlaze/Data/Files/Demo/Game.json (modify in Tasks 3, 4, 7; replace in Task 8)
```

## Tasks overview

| Task | Content | Runnable |
|---|---|---|
| 1 | Visuals core and view transport | Yes (no visible change) |
| 2 | Generic rendering, wall and door visuals, bricks texture moved | Yes |
| 3 | Public object API, walls and doors move to `Objects/Buildings` | Yes |
| 4 | Tools, placement API, `WallTool`, wall materials, panel order | Yes |
| 5 | Remembered parameters (`HomeBlaze.Components` and page) | Yes |
| 6 | "Add door" on walls | Yes |
| 7 | Trees and `TreeTool` | Yes |
| 8 | New demo world | Yes |
| 9 | Full verification and screenshot (controller) | No code |
| 10 | README, delete working docs, PR description | No code |

---

## Task 1: Visuals core and view transport

**Runnable:** yes (no visible change; the browser ignores the new `parts` field until Task 2). Controller restarts the app.

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Game/Visuals/GameVisualShape.cs`, `GameVisualEffect.cs`, `GameVisualVector.cs`, `GameVisualPoint.cs`, `GameVisualPart.cs`, `GameVisual.cs`, `GameVisualCache.cs`, `GameAssets.cs`, `GameVisualValidator.cs`
- Create: `src/HomeBlaze/HomeBlaze.Game/View/GameViewPart.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Game/GameObject.cs`, `View/GameViewObject.cs`, `View/GameViewTracker.cs`, `Game.View.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Game.Tests/HomeBlaze.Game.Tests.csproj`, `Testing/GameTestContext.cs`
- Create (tests): `src/HomeBlaze/HomeBlaze.Game.Tests/Testing/VisualObject.cs`, `Testing/WarnedVisualObject.cs`, `Testing/RecordingLogger.cs`, `Visuals/GameVisualTests.cs`, `Visuals/GameAssetsTests.cs`, `Visuals/GameVisualValidatorTests.cs`
- Modify (tests): `src/HomeBlaze/HomeBlaze.Game.Tests/View/GameViewTrackerTests.cs`

- [ ] **Step 1: Add the global using and the test helpers**

In `src/HomeBlaze/HomeBlaze.Game.Tests/HomeBlaze.Game.Tests.csproj` replace

```xml
    <ItemGroup>
        <Using Include="Xunit" />
    </ItemGroup>
```

with

```xml
    <ItemGroup>
        <Using Include="Xunit" />
        <Using Include="HomeBlaze.Game.Visuals" />
    </ItemGroup>
```

Create `src/HomeBlaze/HomeBlaze.Game.Tests/Testing/VisualObject.cs`:

```csharp
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Game.Tests.Testing;

/// <summary>
/// An object whose visual a test sets directly.
/// </summary>
[InterceptorSubject]
public partial class VisualObject : GameObject
{
    public GameVisual CustomVisual { get; set; } = GameVisual.Box(1, 1, 1);

    public override GameVisual Visual => CustomVisual;
}
```

Create `src/HomeBlaze/HomeBlaze.Game.Tests/Testing/WarnedVisualObject.cs`:

```csharp
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Game.Tests.Testing;

/// <summary>
/// Like <see cref="VisualObject"/>, but a type of its own, because the game warns about an invalid visual once per type
/// and process.
/// </summary>
[InterceptorSubject]
public partial class WarnedVisualObject : GameObject
{
    public GameVisual CustomVisual { get; set; } = GameVisual.Box(1, 1, 1);

    public override GameVisual Visual => CustomVisual;
}
```

Create `src/HomeBlaze/HomeBlaze.Game.Tests/Testing/RecordingLogger.cs`:

```csharp
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Game.Tests.Testing;

/// <summary>
/// A thread-safe logger that records every entry.
/// </summary>
internal sealed class RecordingLogger : ILogger
{
    private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

    public IReadOnlyList<(LogLevel Level, string Message)> Entries => _entries.ToArray();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        _entries.Enqueue((logLevel, formatter(state, exception)));
}
```

In `src/HomeBlaze/HomeBlaze.Game.Tests/Testing/GameTestContext.cs` add after the `AddDoor` method (before the closing brace of the class):

```csharp

    /// <summary>
    /// Adds the object to the game under the name.
    /// </summary>
    public T AddObject<T>(string name, T gameObject) where T : GameObject
    {
        Game.Objects = new Dictionary<string, GameObject>(Game.Objects) { [name] = gameObject };
        Game.RequestIndexRebuild();
        return gameObject;
    }
```

- [ ] **Step 2: Write the failing visual tests**

Create `src/HomeBlaze/HomeBlaze.Game.Tests/Visuals/GameVisualTests.cs`:

```csharp
using System.Collections.Immutable;

namespace HomeBlaze.Game.Tests.Visuals;

public class GameVisualTests
{
    [Fact]
    public void WhenTwoVisualsHaveEqualParts_ThenTheyAreEqual()
    {
        // Arrange
        var first = new GameVisual([new GameVisualPart { Size = new GameVisualVector(1, 2, 3), Texture = "_content/A/a.jpg" }]);
        var second = new GameVisual([new GameVisualPart { Size = new GameVisualVector(1, 2, 3), Texture = "_content/A/a.jpg" }]);

        // Act
        var isEqual = first.Equals(second);

        // Assert
        Assert.True(isEqual);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }

    [Fact]
    public void WhenAPartDiffers_ThenTheVisualsDiffer()
    {
        // Arrange
        var first = GameVisual.Box(1, 2, 3);
        var second = GameVisual.Box(1, 2, 4);

        // Act
        var isEqual = first.Equals(second);

        // Assert
        Assert.False(isEqual);
    }

    [Fact]
    public void WhenAVisualIsCreatedFromADefaultArray_ThenItHasNoParts()
    {
        // Act
        var visual = new GameVisual(default(ImmutableArray<GameVisualPart>));

        // Assert
        Assert.Empty(visual.Parts);
    }

    [Fact]
    public void WhenABoxIsCreated_ThenItIsOneBoxOfTheSizeInTheDefaultColor()
    {
        // Act
        var visual = GameVisual.Box(4, 10, 1);

        // Assert
        var part = Assert.Single(visual.Parts);
        Assert.Equal(GameVisualShape.Box, part.Shape);
        Assert.Equal(new GameVisualVector(4, 10, 1), part.Size);
        Assert.Equal(default, part.Offset);
        Assert.Equal(GameVisual.DefaultColor, part.Color);
    }

    [Fact]
    public void WhenTheKeyIsUnchanged_ThenTheCacheReturnsTheSameInstance()
    {
        // Arrange
        var cache = new GameVisualCache<(int Width, int Height)>();
        var first = cache.Get((2, 3), static size => GameVisual.Box(size.Width, size.Height, 1));

        // Act
        var second = cache.Get((2, 3), static size => GameVisual.Box(size.Width, size.Height, 1));
        var third = cache.Get((2, 4), static size => GameVisual.Box(size.Width, size.Height, 1));

        // Assert
        Assert.Same(first, second);
        Assert.NotSame(first, third);
        Assert.Equal(GameVisual.Box(2, 4, 1), third);
    }

    [Fact]
    public void WhenAnObjectHasNoVisualOfItsOwn_ThenItIsAGrayBoxOfItsSize()
    {
        // Arrange
        var gameObject = new Testing.MovementBlocker { Width = 3, Depth = 2, Height = 5 };

        // Act
        var visual = gameObject.Visual;

        // Assert
        Assert.Equal(GameVisual.Box(3, 5, 2), visual);
        Assert.Same(visual, gameObject.Visual);
    }
}
```

Create `src/HomeBlaze/HomeBlaze.Game.Tests/Visuals/GameAssetsTests.cs`:

```csharp
namespace HomeBlaze.Game.Tests.Visuals;

public class GameAssetsTests
{
    [Fact]
    public void WhenAnAssetUrlIsCreated_ThenItPointsIntoTheContentOfTheTypesAssembly()
    {
        // Act
        var gameUrl = GameAssets.Url(typeof(Game), "textures/grass.jpg");
        var testUrl = GameAssets.Url(typeof(GameAssetsTests), "a/b.glb");

        // Assert
        Assert.Equal("_content/HomeBlaze.Game/textures/grass.jpg", gameUrl);
        Assert.Equal("_content/HomeBlaze.Game.Tests/a/b.glb", testUrl);
    }
}
```

Create `src/HomeBlaze/HomeBlaze.Game.Tests/Visuals/GameVisualValidatorTests.cs`:

```csharp
using HomeBlaze.Game.Tests.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HomeBlaze.Game.Tests.Visuals;

public class GameVisualValidatorTests
{
    private static GameVisualPart CreatePart(string kind) => kind switch
    {
        "size" => new GameVisualPart { Size = new GameVisualVector(1025, 1, 1) },
        "negative-size" => new GameVisualPart { Size = new GameVisualVector(1, -1, 1) },
        "offset" => new GameVisualPart { Size = new GameVisualVector(1, 1, 1), Offset = new GameVisualVector(0, 0, -1025) },
        "pivot" => new GameVisualPart { Size = new GameVisualVector(1, 1, 1), Pivot = new GameVisualPoint(2000, 0) },
        "rotation" => new GameVisualPart { Size = new GameVisualVector(1, 1, 1), Rotation = double.NaN },
        "texture" => new GameVisualPart { Size = new GameVisualVector(1, 1, 1), Texture = "https://example.com/bricks.jpg" },
        "parent-texture" => new GameVisualPart { Size = new GameVisualVector(1, 1, 1), Texture = "_content/A/../../secret.jpg" },
        "model" => new GameVisualPart { Shape = GameVisualShape.Model, Size = new GameVisualVector(1, 1, 1), Model = "models/tree.glb" },
        "missing-model" => new GameVisualPart { Shape = GameVisualShape.Model, Size = new GameVisualVector(1, 1, 1) },
        "color" => new GameVisualPart { Size = new GameVisualVector(1, 1, 1), Color = "red" },
        "texture-scale" => new GameVisualPart { Size = new GameVisualVector(1, 1, 1), TextureScale = 0 },
        "shape" => new GameVisualPart { Shape = (GameVisualShape)7, Size = new GameVisualVector(1, 1, 1) },
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    [Fact]
    public void WhenTheVisualIsValid_ThenItIsKept()
    {
        // Arrange
        var gameObject = new VisualObject
        {
            CustomVisual = new GameVisual([new GameVisualPart
            {
                Shape = GameVisualShape.Model, Size = new GameVisualVector(20, 20, 20), Offset = new GameVisualVector(-9, 0, -9),
                Model = "_content/HomeBlaze.Game/objects/nature/models/pine_tree_1.glb", Effect = GameVisualEffect.Wind, Rotation = 270
            }])
        };

        // Act
        var visual = GameVisualValidator.GetValidVisual(gameObject, gameObject.CustomVisual, NullLogger.Instance);

        // Assert
        Assert.Same(gameObject.CustomVisual, visual);
    }

    [Theory]
    [InlineData("size")]
    [InlineData("negative-size")]
    [InlineData("offset")]
    [InlineData("pivot")]
    [InlineData("rotation")]
    [InlineData("texture")]
    [InlineData("parent-texture")]
    [InlineData("model")]
    [InlineData("missing-model")]
    [InlineData("color")]
    [InlineData("texture-scale")]
    [InlineData("shape")]
    public void WhenAPartIsInvalid_ThenTheObjectShowsAsTheDefaultBox(string kind)
    {
        // Arrange
        var gameObject = new VisualObject { Width = 2, Depth = 3, Height = 4, CustomVisual = new GameVisual([CreatePart(kind)]) };

        // Act
        var visual = GameVisualValidator.GetValidVisual(gameObject, gameObject.CustomVisual, NullLogger.Instance);

        // Assert
        Assert.Equal(GameVisual.Box(2, 4, 3), visual);
    }

    [Fact]
    public void WhenTheVisualHasTooManyParts_ThenTheObjectShowsAsTheDefaultBox()
    {
        // Arrange
        var part = new GameVisualPart { Size = new GameVisualVector(1, 1, 1) };
        var allowed = new VisualObject { CustomVisual = new GameVisual([.. Enumerable.Repeat(part, GameVisual.MaximumParts)]) };
        var tooMany = new VisualObject { CustomVisual = new GameVisual([.. Enumerable.Repeat(part, GameVisual.MaximumParts + 1)]) };

        // Act
        var allowedVisual = GameVisualValidator.GetValidVisual(allowed, allowed.CustomVisual, NullLogger.Instance);
        var tooManyVisual = GameVisualValidator.GetValidVisual(tooMany, tooMany.CustomVisual, NullLogger.Instance);

        // Assert
        Assert.Same(allowed.CustomVisual, allowedVisual);
        Assert.Equal(GameVisual.Box(1, 1, 1), tooManyVisual);
    }

    [Fact]
    public void WhenObjectsOfATypeHaveInvalidVisuals_ThenTheGameWarnsOnce()
    {
        // Arrange
        var logger = new RecordingLogger();
        var invalid = new GameVisual([CreatePart("texture")]);
        var first = new WarnedVisualObject { CustomVisual = invalid };
        var second = new WarnedVisualObject { CustomVisual = invalid };

        // Act
        GameVisualValidator.GetValidVisual(first, first.CustomVisual, logger);
        GameVisualValidator.GetValidVisual(second, second.CustomVisual, logger);

        // Assert
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains(typeof(WarnedVisualObject).FullName!, entry.Message);
    }
}
```

- [ ] **Step 3: Write the failing tracker tests**

In `src/HomeBlaze/HomeBlaze.Game.Tests/View/GameViewTrackerTests.cs` add these tests before the last test (`WhenThePlayerColorIsRequested_ThenItIsTheColorOfItsViewObject`):

```csharp
    [Fact]
    public void WhenAnObjectGetsAnEqualVisual_ThenItIsNotSentAgain()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        var gameObject = _context.AddObject("object", new VisualObject { X = 5, Y = 5, CustomVisual = GameVisual.Box(1, 2, 1) });
        _context.Game.UpdateView(_tracker, player);

        // Act
        gameObject.CustomVisual = GameVisual.Box(1, 2, 1);
        var update = _context.Game.UpdateView(_tracker, player);

        // Assert
        Assert.True(update.IsEmpty);
    }

    [Fact]
    public void WhenAnObjectGetsAnotherVisual_ThenItIsUpsertedWithTheNewParts()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        var gameObject = _context.AddObject("object", new VisualObject { X = 5, Y = 5 });
        _context.Game.UpdateView(_tracker, player);

        // Act
        gameObject.CustomVisual = GameVisual.Box(1, 3, 1, "#112233");
        var update = _context.Game.UpdateView(_tracker, player);

        // Assert
        var view = Assert.Single(update.Upserted);
        var part = Assert.Single(view.Parts);
        Assert.Equal(("box", 3d, "#112233"), (part.Shape, part.Height, part.Color));
    }

    [Fact]
    public void WhenAnObjectHasAnInvalidVisual_ThenItIsSentAsTheDefaultBox()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        _context.AddObject("object", new VisualObject { X = 5, Y = 5, CustomVisual = GameVisual.Box(2000, 1, 1) });

        // Act
        var update = _context.Game.UpdateView(_tracker, player);

        // Assert
        var view = Assert.Single(update.Upserted, view => !view.IsSelf);
        var part = Assert.Single(view.Parts);
        Assert.Equal((1d, 1d, 1d, GameVisual.DefaultColor), (part.Width, part.Height, part.Depth, part.Color));
    }

    [Fact]
    public void WhenAPlayerIsInView_ThenItCarriesNoParts()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");

        // Act
        var update = _context.Game.UpdateView(_tracker, player);

        // Assert
        var self = Assert.Single(update.Upserted);
        Assert.Empty(self.Parts);
        Assert.Null(self.Visual);
    }
```

- [ ] **Step 4: Run the tests to see them fail**

Run: `dotnet build src/HomeBlaze/HomeBlaze.Game.Tests -c Debug`
Expected: FAIL with compile errors (`GameVisual`, `GameVisualPart`, `GameVisualCache`, `GameAssets`, `GameVisualValidator`, `Parts` do not exist).

- [ ] **Step 5: Create the visual types**

Create `src/HomeBlaze/HomeBlaze.Game/Visuals/GameVisualShape.cs`:

```csharp
namespace HomeBlaze.Game.Visuals;

/// <summary>
/// The geometry of a <see cref="GameVisualPart"/>.
/// </summary>
public enum GameVisualShape
{
    /// <summary>
    /// A box filling the part's offset and size, textured or colored.
    /// </summary>
    Box,

    /// <summary>
    /// A glTF model from <see cref="GameVisualPart.Model"/>, scaled to fit the part's size.
    /// </summary>
    Model
}
```

Create `src/HomeBlaze/HomeBlaze.Game/Visuals/GameVisualEffect.cs`:

```csharp
namespace HomeBlaze.Game.Visuals;

/// <summary>
/// A motion the browser adds to a <see cref="GameVisualPart"/>.
/// </summary>
public enum GameVisualEffect
{
    None,

    /// <summary>
    /// Bends the part sideways in proportion to the height above the object's base, as wind moves a tree.
    /// </summary>
    Wind
}
```

Create `src/HomeBlaze/HomeBlaze.Game/Visuals/GameVisualVector.cs`:

```csharp
namespace HomeBlaze.Game.Visuals;

/// <summary>
/// A point or a size in tiles: X grows to the east, Y upward and Z to the north. As a size, X is the width, Y the height
/// and Z the depth.
/// </summary>
public readonly record struct GameVisualVector(double X, double Y, double Z);
```

Create `src/HomeBlaze/HomeBlaze.Game/Visuals/GameVisualPoint.cs`:

```csharp
namespace HomeBlaze.Game.Visuals;

/// <summary>
/// A point on the ground in tiles: X grows to the east and Z to the north.
/// </summary>
public readonly record struct GameVisualPoint(double X, double Z);
```

Create `src/HomeBlaze/HomeBlaze.Game/Visuals/GameVisualPart.cs`:

```csharp
namespace HomeBlaze.Game.Visuals;

/// <summary>
/// One box or model of a <see cref="GameVisual"/>. Positions are in tiles relative to the south-west corner of the
/// object's footprint at ground level.
/// </summary>
public sealed record GameVisualPart
{
    public GameVisualShape Shape { get; init; }

    /// <summary>
    /// The south-west bottom corner of the part's box.
    /// </summary>
    public GameVisualVector Offset { get; init; }

    /// <summary>
    /// The width, height and depth of the part's box. A model is scaled uniformly to the largest size whose bounding box
    /// fits in it, with its own origin at the horizontal center of the box and its lowest point at the box's bottom.
    /// </summary>
    public GameVisualVector Size { get; init; }

    /// <summary>
    /// The point around which <see cref="Rotation"/> turns the part.
    /// </summary>
    public GameVisualPoint Pivot { get; init; }

    /// <summary>
    /// The turn around the vertical axis through <see cref="Pivot"/> in degrees, clockwise seen from above (north toward east).
    /// </summary>
    public double Rotation { get; init; }

    /// <summary>
    /// The URL of the texture of a box, starting with "_content/"; see <see cref="GameAssets.Url"/>.
    /// </summary>
    public string? Texture { get; init; }

    /// <summary>
    /// The tiles one repeat of the texture covers, horizontally and vertically.
    /// </summary>
    public double TextureScale { get; init; } = 1;

    /// <summary>
    /// The color of a box as "#rrggbb", which tints its texture; gray for a box without texture and color.
    /// </summary>
    public string? Color { get; init; }

    /// <summary>
    /// The URL of the glTF model of a <see cref="GameVisualShape.Model"/> part, starting with "_content/".
    /// </summary>
    public string? Model { get; init; }

    public GameVisualEffect Effect { get; init; }

    /// <summary>
    /// The name of an animation in the model that plays in a loop, or null.
    /// </summary>
    public string? Animation { get; init; }
}
```

Create `src/HomeBlaze/HomeBlaze.Game/Visuals/GameVisual.cs`:

```csharp
using System.Collections.Immutable;

namespace HomeBlaze.Game.Visuals;

/// <summary>
/// How an object looks: parts placed relative to the south-west corner of its footprint at ground level. Compares by
/// value, including its parts, so an unchanged description is not sent to the browser again.
/// </summary>
public sealed record GameVisual
{
    /// <summary>
    /// The color of a box without texture and color, which is also the default look of an object.
    /// </summary>
    public const string DefaultColor = "#8d8577";

    /// <summary>
    /// The most parts a visual may have; an object whose visual has more shows as its default box.
    /// </summary>
    public const int MaximumParts = 16;

    /// <summary>
    /// The largest absolute coordinate of an offset, size or pivot in tiles; an object whose visual exceeds it shows as
    /// its default box.
    /// </summary>
    public const double MaximumExtent = 1024;

    public GameVisual(ImmutableArray<GameVisualPart> parts)
    {
        Parts = parts.IsDefault ? [] : parts;
    }

    public ImmutableArray<GameVisualPart> Parts { get; }

    /// <summary>
    /// Creates a visual of one box of the size at the corner of the footprint.
    /// </summary>
    public static GameVisual Box(double width, double height, double depth, string color = DefaultColor) =>
        new([new GameVisualPart { Shape = GameVisualShape.Box, Size = new GameVisualVector(width, height, depth), Color = color }]);

    public bool Equals(GameVisual? other) =>
        ReferenceEquals(this, other) || (other is not null && Parts.AsSpan().SequenceEqual(other.Parts.AsSpan()));

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var part in Parts)
        {
            hash.Add(part);
        }

        return hash.ToHashCode();
    }
}
```

Create `src/HomeBlaze/HomeBlaze.Game/Visuals/GameVisualCache.cs`:

```csharp
namespace HomeBlaze.Game.Visuals;

/// <summary>
/// Keeps the visual of the latest state of an object, so that an unchanged state returns the same instance without
/// allocating. Thread-safe: a reader sees the visual of one state, never a mix.
/// </summary>
/// <typeparam name="TKey">The state the visual follows, typically a value tuple of the properties it reads.</typeparam>
public sealed class GameVisualCache<TKey> where TKey : IEquatable<TKey>
{
    private Entry? _entry;

    /// <summary>
    /// Gets the visual of the key, created by <paramref name="create"/> when the key differs from the last one. Pass a
    /// static lambda, so that the call allocates nothing for an unchanged key.
    /// </summary>
    public GameVisual Get(TKey key, Func<TKey, GameVisual> create)
    {
        var entry = Volatile.Read(ref _entry);
        if (entry is null || !entry.Key.Equals(key))
        {
            entry = new Entry(key, create(key));
            Volatile.Write(ref _entry, entry);
        }

        return entry.Visual;
    }

    private sealed record Entry(TKey Key, GameVisual Visual);
}
```

Create `src/HomeBlaze/HomeBlaze.Game/Visuals/GameAssets.cs`:

```csharp
namespace HomeBlaze.Game.Visuals;

/// <summary>
/// Builds the URLs of the static web assets that visuals reference.
/// </summary>
public static class GameAssets
{
    /// <summary>
    /// The start of every asset URL a visual may reference.
    /// </summary>
    public const string ContentPrefix = "_content/";

    /// <summary>
    /// Gets the URL of a file under <c>wwwroot/</c> of the assembly that defines <paramref name="type"/>, so the URL
    /// follows the type when it moves to another library.
    /// </summary>
    /// <param name="type">A type of the assembly that ships the file.</param>
    /// <param name="path">The path below <c>wwwroot/</c>, for example "objects/nature/models/pine_tree_1.glb".</param>
    public static string Url(Type type, string path)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentException.ThrowIfNullOrEmpty(path);
        return $"{ContentPrefix}{type.Assembly.GetName().Name}/{path}";
    }
}
```

Create `src/HomeBlaze/HomeBlaze.Game/Visuals/GameVisualValidator.cs`:

```csharp
using System.Buffers;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Game.Visuals;

/// <summary>
/// Replaces a visual that the browser must not get by the default box of the object.
/// </summary>
internal static class GameVisualValidator
{
    private const int MaximumAnimationLength = 64;
    private static readonly SearchValues<char> HexDigits = SearchValues.Create("0123456789abcdefABCDEF");
    private static readonly ConcurrentDictionary<Type, bool> WarnedTypes = new();

    /// <summary>
    /// Gets the visual when it is valid, else the default box of the object, warning once per object type and process.
    /// </summary>
    public static GameVisual GetValidVisual(GameObject gameObject, GameVisual? visual, ILogger logger)
    {
        if (visual is not null && IsValid(visual))
        {
            return visual;
        }

        if (WarnedTypes.TryAdd(gameObject.GetType(), true))
        {
            logger.LogWarning("The visual of {Type} is invalid, so its objects show as gray boxes", gameObject.GetType().FullName);
        }

        return GameVisual.Box(gameObject.Width, gameObject.Height, gameObject.Depth);
    }

    private static bool IsValid(GameVisual visual)
    {
        if (visual.Parts.Length > GameVisual.MaximumParts)
        {
            return false;
        }

        foreach (var part in visual.Parts)
        {
            if (!IsValid(part))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValid(GameVisualPart? part) => part is not null && IsValidPlacement(part) && IsValidLook(part);

    private static bool IsValidPlacement(GameVisualPart part) =>
        IsInRange(part.Offset) && IsValidSize(part.Size) && IsValidTurn(part);

    private static bool IsValidTurn(GameVisualPart part) =>
        IsInRange(part.Pivot.X) && IsInRange(part.Pivot.Z) && double.IsFinite(part.Rotation);

    private static bool IsValidLook(GameVisualPart part) =>
        Enum.IsDefined(part.Shape) && Enum.IsDefined(part.Effect) && IsValidSurface(part) && IsValidSource(part);

    private static bool IsValidSurface(GameVisualPart part) =>
        IsValidTextureScale(part.TextureScale) && IsValidColor(part.Color) && IsValidAnimation(part.Animation);

    private static bool IsValidSource(GameVisualPart part) =>
        IsAssetUrl(part.Texture) && IsAssetUrl(part.Model) && (part.Shape != GameVisualShape.Model || part.Model is not null);

    private static bool IsValidTextureScale(double scale) => scale is > 0 and <= GameVisual.MaximumExtent;

    private static bool IsValidColor(string? color) =>
        color is null || (color.Length == 7 && color[0] == '#' && !color.AsSpan(1).ContainsAnyExcept(HexDigits));

    private static bool IsValidAnimation(string? animation) => animation is null || animation.Length <= MaximumAnimationLength;

    private static bool IsAssetUrl(string? url) =>
        url is null || (url.StartsWith(GameAssets.ContentPrefix, StringComparison.Ordinal) && !url.Contains("..", StringComparison.Ordinal));

    private static bool IsValidSize(GameVisualVector size) => IsInRange(size) && size.X >= 0 && size.Y >= 0 && size.Z >= 0;

    private static bool IsInRange(GameVisualVector vector) => IsInRange(vector.X) && IsInRange(vector.Y) && IsInRange(vector.Z);

    // NaN fails both comparisons, so it is out of range too.
    private static bool IsInRange(double value) => value is >= -GameVisual.MaximumExtent and <= GameVisual.MaximumExtent;
}
```

If Sonar reports S1067 on `IsValidColor` or `IsValidSize` (it counts `&&` and `||`), split the right side into a helper (`IsHexColor(string color)`, `IsNotNegative(GameVisualVector size)`), keeping the behavior.

- [ ] **Step 6: Give every object a default visual**

In `src/HomeBlaze/HomeBlaze.Game/GameObject.cs` replace the usings

```csharp
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Game.World;
```

with

```csharp
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Game.Visuals;
using HomeBlaze.Game.World;
```

and replace

```csharp
    /// <summary>
    /// Whether players see this object.
    /// </summary>
    public virtual bool IsVisible => true;
```

with

```csharp
    /// <summary>
    /// Whether players see this object.
    /// </summary>
    public virtual bool IsVisible => true;

    /// <summary>
    /// How the object looks; a gray box filling the footprint and <see cref="Height"/> by default. An override computes it
    /// from configuration or state properties only, which the page observes, and caches it per state, for example with
    /// <see cref="GameVisualCache{TKey}"/>. Players are drawn by the page itself and ignore it.
    /// </summary>
    public virtual GameVisual Visual =>
        (_defaultVisual ??= new()).Get((Width, Height, Depth), static size => GameVisual.Box(size.Width, size.Height, size.Depth));
```

and add the field right after the class's opening brace (before the `MaximumSize` constant's XML doc):

```csharp
    // Created on first use, because most object types override Visual.
    private GameVisualCache<(int Width, int Height, int Depth)>? _defaultVisual;

```

- [ ] **Step 7: Carry the visual in the view**

Create `src/HomeBlaze/HomeBlaze.Game/View/GameViewPart.cs`:

```csharp
using System.Text.Json.Serialization;
using HomeBlaze.Game.Visuals;

namespace HomeBlaze.Game.View;

/// <summary>
/// A part of an object's visual as the browser draws it. The names are short because every page receives the parts of
/// every object in view.
/// </summary>
internal sealed class GameViewPart
{
    [JsonPropertyName("k")]
    public required string Shape { get; init; }

    [JsonPropertyName("x")]
    public double X { get; init; }

    [JsonPropertyName("y")]
    public double Y { get; init; }

    [JsonPropertyName("z")]
    public double Z { get; init; }

    [JsonPropertyName("w")]
    public double Width { get; init; }

    [JsonPropertyName("h")]
    public double Height { get; init; }

    [JsonPropertyName("d")]
    public double Depth { get; init; }

    [JsonPropertyName("px")]
    public double PivotX { get; init; }

    [JsonPropertyName("pz")]
    public double PivotZ { get; init; }

    [JsonPropertyName("r")]
    public double Rotation { get; init; }

    [JsonPropertyName("t")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Texture { get; init; }

    [JsonPropertyName("ts")]
    public double TextureScale { get; init; }

    [JsonPropertyName("c")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Color { get; init; }

    [JsonPropertyName("m")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Model { get; init; }

    [JsonPropertyName("e")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Effect { get; init; }

    [JsonPropertyName("a")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Animation { get; init; }

    public static GameViewPart[] FromVisual(GameVisual visual)
    {
        var parts = new GameViewPart[visual.Parts.Length];
        for (var index = 0; index < parts.Length; index++)
        {
            parts[index] = From(visual.Parts[index]);
        }

        return parts;
    }

    private static GameViewPart From(GameVisualPart part) => new()
    {
        Shape = part.Shape == GameVisualShape.Model ? "model" : "box",
        X = part.Offset.X,
        Y = part.Offset.Y,
        Z = part.Offset.Z,
        Width = part.Size.X,
        Height = part.Size.Y,
        Depth = part.Size.Z,
        PivotX = part.Pivot.X,
        PivotZ = part.Pivot.Z,
        Rotation = part.Rotation,
        Texture = part.Texture,
        TextureScale = part.TextureScale,
        Color = part.Color,
        Model = part.Model,
        Effect = part.Effect == GameVisualEffect.Wind ? "wind" : null,
        Animation = part.Animation
    };
}
```

Replace the whole content of `src/HomeBlaze/HomeBlaze.Game/View/GameViewObject.cs` with:

```csharp
using System.Text.Json.Serialization;
using HomeBlaze.Game.Visuals;

namespace HomeBlaze.Game.View;

/// <summary>
/// An object as the browser draws it. Ids are unique within one page. <see cref="Visual"/> is what the tracker compares,
/// <see cref="Parts"/> what the browser draws: the visual, or the default box when the visual is invalid. Players carry
/// neither, because the page draws them itself.
/// </summary>
internal sealed record GameViewObject(
    string Id,
    string Kind,
    int X,
    int Y,
    int Width,
    int Depth,
    int Height,
    int Facing,
    string Title,
    string Color,
    bool IsMovementBlocking,
    bool IsOpen,
    bool IsSelf,
    bool IsMicrophoneOn,
    bool IsAudioOn,
    [property: JsonIgnore] GameVisual? Visual,
    IReadOnlyList<GameViewPart> Parts)
{
    public const string PlayerKind = "player";
    public const string WallKind = "wall";
    public const string DoorKind = "door";
    public const string ObjectKind = "object";
}
```

In `src/HomeBlaze/HomeBlaze.Game/View/GameViewTracker.cs`:

Replace the usings

```csharp
using HomeBlaze.Abstractions;
```

with

```csharp
using HomeBlaze.Abstractions;
using HomeBlaze.Game.Visuals;
using Microsoft.Extensions.Logging;
```

Replace

```csharp
    public GameViewUpdate Update(IReadOnlyList<GameObject> objects, GamePlayer? self)
    {
        var upserted = UpsertChanged(objects, self);
```

with

```csharp
    /// <param name="objects">The visible objects in view.</param>
    /// <param name="self">The page's own player, or null.</param>
    /// <param name="logger">Warns about object types with an invalid visual.</param>
    public GameViewUpdate Update(IReadOnlyList<GameObject> objects, GamePlayer? self, ILogger logger)
    {
        var upserted = UpsertChanged(objects, self, logger);
```

Replace

```csharp
    private List<GameViewObject>? UpsertChanged(IReadOnlyList<GameObject> objects, GamePlayer? self)
```

with

```csharp
    private List<GameViewObject>? UpsertChanged(IReadOnlyList<GameObject> objects, GamePlayer? self, ILogger logger)
```

Replace

```csharp
            var view = CreateView(gameObject, known?.Id ?? $"o{++_nextId}", isSelf);
```

with

```csharp
            var view = CreateView(gameObject, known?.Id ?? $"o{++_nextId}", isSelf, logger);
```

Replace

```csharp
    private bool IsUnchanged(GameViewObject known, GameObject gameObject, bool isSelf) =>
        HasSameFootprint(known, gameObject) && HasSameState(known, gameObject, isSelf) && HasSamePassage(known, gameObject)
            && HasSameLabel(known, gameObject);
```

with

```csharp
    private bool IsUnchanged(GameViewObject known, GameObject gameObject, bool isSelf) =>
        HasSameFootprint(known, gameObject) && HasSameState(known, gameObject, isSelf) && HasSameLook(known, gameObject);

    // A cached visual is the same instance, so the comparison stops at the reference and allocates nothing.
    private bool HasSameLook(GameViewObject known, GameObject gameObject) =>
        HasSamePassage(known, gameObject) && HasSameLabel(known, gameObject) && Equals(known.Visual, GetVisual(gameObject));
```

Replace

```csharp
    private GameViewObject CreateView(GameObject gameObject, string id, bool isSelf) =>
        new(id, GetKind(gameObject), gameObject.X, gameObject.Y, gameObject.Width, gameObject.Depth, gameObject.Height,
            GetFacing(gameObject), GetTitle(gameObject), GetColor(gameObject), gameObject.IsMovementBlocking, IsOpen(gameObject), isSelf,
            IsMicrophoneOn(gameObject), IsAudioOn(gameObject));
```

with

```csharp
    private GameViewObject CreateView(GameObject gameObject, string id, bool isSelf, ILogger logger)
    {
        var visual = GetVisual(gameObject);
        return new(id, GetKind(gameObject), gameObject.X, gameObject.Y, gameObject.Width, gameObject.Depth, gameObject.Height,
            GetFacing(gameObject), GetTitle(gameObject), GetColor(gameObject), gameObject.IsMovementBlocking, IsOpen(gameObject), isSelf,
            IsMicrophoneOn(gameObject), IsAudioOn(gameObject), visual, GetParts(gameObject, visual, logger));
    }

    private static GameVisual? GetVisual(GameObject gameObject) => gameObject is GamePlayer ? null : gameObject.Visual;

    private static IReadOnlyList<GameViewPart> GetParts(GameObject gameObject, GameVisual? visual, ILogger logger) =>
        visual is null ? [] : GameViewPart.FromVisual(GameVisualValidator.GetValidVisual(gameObject, visual, logger));
```

In `src/HomeBlaze/HomeBlaze.Game/Game.View.cs` replace

```csharp
            return tracker.Update(_viewObjects, viewer);
```

with

```csharp
            return tracker.Update(_viewObjects, viewer, _logger);
```

- [ ] **Step 8: Run the unit tests**

Run: `dotnet build src/HomeBlaze/HomeBlaze.Game.Tests -c Debug`
Expected: Build succeeded, 0 warnings, 0 errors.

Run: `dotnet test src/HomeBlaze/HomeBlaze.Game.Tests -c Debug --filter "FullyQualifiedName!~E2E"`
Expected: all tests pass, including `WhenNothingChanged_ThenTheUpdateAllocatesNothing` (a wall's default visual is cached, so the comparison stops at the reference).

- [ ] **Step 9: Commit and push**

```bash
git add src/HomeBlaze/HomeBlaze.Game/Visuals src/HomeBlaze/HomeBlaze.Game/View/GameViewPart.cs src/HomeBlaze/HomeBlaze.Game/View/GameViewObject.cs src/HomeBlaze/HomeBlaze.Game/View/GameViewTracker.cs src/HomeBlaze/HomeBlaze.Game/GameObject.cs src/HomeBlaze/HomeBlaze.Game/Game.View.cs src/HomeBlaze/HomeBlaze.Game.Tests/HomeBlaze.Game.Tests.csproj src/HomeBlaze/HomeBlaze.Game.Tests/Testing/GameTestContext.cs src/HomeBlaze/HomeBlaze.Game.Tests/Testing/VisualObject.cs src/HomeBlaze/HomeBlaze.Game.Tests/Testing/WarnedVisualObject.cs src/HomeBlaze/HomeBlaze.Game.Tests/Testing/RecordingLogger.cs src/HomeBlaze/HomeBlaze.Game.Tests/Visuals src/HomeBlaze/HomeBlaze.Game.Tests/View/GameViewTrackerTests.cs
git commit -m "feat: describe the look of game objects as value-compared visuals" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push origin feat/homeblaze-game
```

---

## Task 2: Generic rendering, wall and door visuals

**Runnable:** yes. Controller restarts the app and checks the demo page: walls show bricks as before, a door opens with its leaf gliding a quarter turn, walls and doors between camera and player fade.

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Game/WallTextures.cs` (moves to `Objects/Buildings` in Task 3)
- Modify: `src/HomeBlaze/HomeBlaze.Game/Wall.cs`, `Door.cs`, `View/GameViewObject.cs`, `View/GameViewTracker.cs`, `View/GameColors.cs`
- Rewrite: `src/HomeBlaze/HomeBlaze.Game/wwwroot/game-entities.js`
- Modify: `src/HomeBlaze/HomeBlaze.Game/wwwroot/game.js`
- Move and re-encode: `wwwroot/textures/bricks.jpg` to `wwwroot/objects/buildings/textures/bricks.jpg`; create `wwwroot/objects/buildings/textures/LICENSE.md`; modify `wwwroot/textures/LICENSE.md`
- Create (tests): `src/HomeBlaze/HomeBlaze.Game.Tests/Objects/Buildings/WallTests.cs`
- Modify (tests): `src/HomeBlaze/HomeBlaze.Game.Tests/GameDoorTests.cs`, `View/GameViewTrackerTests.cs`, `E2E/GamePageTests.cs`
- Scratchpad: `$SCRATCH/scripts/wall_textures.py`

- [ ] **Step 1: Write the failing wall and door visual tests**

Create `src/HomeBlaze/HomeBlaze.Game.Tests/Objects/Buildings/WallTests.cs`:

```csharp
namespace HomeBlaze.Game.Tests.Objects.Buildings;

public class WallTests
{
    [Fact]
    public void WhenAWallIsCreated_ThenItLooksLikeABrickBoxOfItsSize()
    {
        // Arrange
        var wall = new Wall { Width = 6, Depth = 1, Height = 10 };

        // Act
        var part = Assert.Single(wall.Visual.Parts);

        // Assert
        Assert.Equal(GameVisualShape.Box, part.Shape);
        Assert.Equal(new GameVisualVector(6, 10, 1), part.Size);
        Assert.Equal("_content/HomeBlaze.Game/objects/buildings/textures/bricks.jpg", part.Texture);
        Assert.Equal(8, part.TextureScale);
    }

    [Fact]
    public void WhenTheWallIsUnchanged_ThenItsVisualIsTheSameInstance()
    {
        // Arrange
        var wall = new Wall();
        var first = wall.Visual;

        // Act
        var second = wall.Visual;

        // Assert
        Assert.Same(first, second);
    }

    [Fact]
    public void WhenTheWallIsResized_ThenItsVisualFollows()
    {
        // Arrange
        var wall = new Wall();

        // Act
        wall.Width = 1;
        wall.Depth = 12;
        wall.Height = 4;

        // Assert
        Assert.Equal(new GameVisualVector(1, 4, 12), Assert.Single(wall.Visual.Parts).Size);
    }
}
```

In `src/HomeBlaze/HomeBlaze.Game.Tests/GameDoorTests.cs` add before the last test (`WhenTheDoorIsNotPartOfAGame_ThenOpeningIsRefused`):

```csharp
    [Fact]
    public void WhenADoorOpens_ThenItsLeafTurnsAQuarterAroundTheHinge()
    {
        // Arrange
        var door = new Door();
        var closed = door.Visual;

        // Act
        door.IsOpen = true;
        var open = door.Visual;

        // Assert
        Assert.Equal(2, closed.Parts.Length);
        var lintel = closed.Parts[0];
        Assert.Equal(new GameVisualVector(0, Door.LeafHeight, 0), lintel.Offset);
        Assert.Equal(new GameVisualVector(4, 10 - Door.LeafHeight, 1), lintel.Size);
        var leaf = closed.Parts[1];
        Assert.Equal(new GameVisualPoint(0, 0.5), leaf.Pivot);
        Assert.Equal(new GameVisualVector(4, Door.LeafHeight, 0.3), leaf.Size);
        Assert.Equal(0, leaf.Rotation);
        Assert.Equal(-90, open.Parts[1].Rotation);
    }

    [Fact]
    public void WhenADoorRunsNorthToSouth_ThenItsLeafHangsAtTheSouthEnd()
    {
        // Arrange
        var door = new Door { Width = 1, Depth = 4 };

        // Act
        var leaf = door.Visual.Parts[1];

        // Assert
        Assert.Equal(new GameVisualPoint(0.5, 0), leaf.Pivot);
        Assert.Equal(new GameVisualVector(0.3, Door.LeafHeight, 4), leaf.Size);
        Assert.Equal(new GameVisualVector(0.35, 0, 0), leaf.Offset);
    }

    [Fact]
    public void WhenADoorIsNoTallerThanItsLeaf_ThenItHasNoLintel()
    {
        // Arrange
        var door = new Door { Height = 8 };

        // Act
        var part = Assert.Single(door.Visual.Parts);

        // Assert
        Assert.Equal(new GameVisualVector(4, 8, 0.3), part.Size);
    }
```

In `src/HomeBlaze/HomeBlaze.Game.Tests/View/GameViewTrackerTests.cs` replace

```csharp
        var wall = Assert.Single(update.Upserted, view => view.Kind == GameViewObject.WallKind);
        Assert.Equal((-20, 8, 19), (wall.X, wall.Y, wall.Width));
        Assert.True(wall.IsMovementBlocking);
```

with

```csharp
        var wall = Assert.Single(update.Upserted, view => view.Kind == GameViewObject.ObjectKind);
        Assert.Equal((-20, 8, 19), (wall.X, wall.Y, wall.Width));
        Assert.True(wall.IsMovementBlocking);
        Assert.Equal(19d, Assert.Single(wall.Parts).Width);
```

and replace the whole test `WhenADoorOpens_ThenItIsUpsertedOpenAndNotBlocking` with

```csharp
    [Fact]
    public async Task WhenADoorOpens_ThenItIsUpsertedNotBlockingWithItsLeafTurned()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        var door = _context.AddDoor(-1, 2);
        var first = _context.Game.UpdateView(_tracker, player);
        var closed = Assert.Single(first.Upserted, view => !view.IsSelf);

        // Act
        await door.OpenAsync(CancellationToken.None);
        var update = _context.Game.UpdateView(_tracker, player);

        // Assert
        Assert.Equal(("Door", true, 0d), (closed.Title, closed.IsMovementBlocking, closed.Parts[1].Rotation));
        var open = Assert.Single(update.Upserted);
        Assert.Equal((closed.Id, GameViewObject.ObjectKind, false, -90d), (open.Id, open.Kind, open.IsMovementBlocking, open.Parts[1].Rotation));
    }
```

- [ ] **Step 2: Run the tests to see them fail**

Run: `dotnet build src/HomeBlaze/HomeBlaze.Game.Tests -c Debug`
Expected: FAIL to compile (`Door.LeafHeight` does not exist).

- [ ] **Step 3: Prepare the bricks texture**

Create `$SCRATCH/scripts/wall_textures.py` (the scripts folder is outside every download folder):

```python
"""Prepares the wall textures of the game.

Usage:
  python3 -I wall_textures.py bricks <current bricks.jpg> <output bricks.jpg>
  python3 -I wall_textures.py square <ambientCG color map> <output jpg>
"""
import sys

from PIL import Image


def stack_bricks(source, target):
    # The current file shows 2 m by 1 m of bricks; two copies above each other make a square repeat of 2 m by 2 m.
    half = Image.open(source).convert("RGB").resize((512, 256), Image.LANCZOS)
    square = Image.new("RGB", (512, 512))
    square.paste(half, (0, 0))
    square.paste(half, (0, 256))
    square.save(target, quality=85, optimize=True)


def scale(source, target):
    Image.open(source).convert("RGB").resize((512, 512), Image.LANCZOS).save(target, quality=82, optimize=True)


if __name__ == "__main__":
    mode, source, target = sys.argv[1:4]
    if mode == "bricks":
        stack_bricks(source, target)
    elif mode == "square":
        scale(source, target)
    else:
        raise SystemExit(f"Unknown mode {mode}")
```

Run:

```bash
SCRATCH=/tmp/claude-1000/-home-rico-GitHub-Namotion-Interceptor--claude-worktrees-bridge-cse-011ShuL5XgwWZqRmSZFhkZve/3d51e483-5513-56d7-b4a2-5748ff2f1414/scratchpad
mkdir -p src/HomeBlaze/HomeBlaze.Game/wwwroot/objects/buildings/textures
python3 -I "$SCRATCH/scripts/wall_textures.py" bricks src/HomeBlaze/HomeBlaze.Game/wwwroot/textures/bricks.jpg src/HomeBlaze/HomeBlaze.Game/wwwroot/objects/buildings/textures/bricks.jpg
git rm -q src/HomeBlaze/HomeBlaze.Game/wwwroot/textures/bricks.jpg
python3 -I -c "from PIL import Image; import sys; print(Image.open(sys.argv[1]).size)" src/HomeBlaze/HomeBlaze.Game/wwwroot/objects/buildings/textures/bricks.jpg
```

Expected: `(512, 512)`, and the file is about 87 KB.

Create `src/HomeBlaze/HomeBlaze.Game/wwwroot/objects/buildings/textures/LICENSE.md`:

```markdown
# Building textures

`bricks.jpg` is the color map of Bricks097 from ambientCG (https://ambientcg.com), released under CC0 1.0 (https://creativecommons.org/publicdomain/zero/1.0/). It was taken from the 1K JPG download, scaled to 512 x 256 and stacked twice into a square of 512 x 512, so one repeat shows 2 m by 2 m of bricks.
```

Replace the content of `src/HomeBlaze/HomeBlaze.Game/wwwroot/textures/LICENSE.md` with:

```markdown
# Textures

`grass.jpg` is the color map of Grass004 from ambientCG (https://ambientcg.com), released under CC0 1.0 (https://creativecommons.org/publicdomain/zero/1.0/). It was taken from the 1K JPG download, scaled down to 512 x 512 and re-encoded to save bandwidth. The textures of walls and doors live in `objects/buildings/textures/`.
```

- [ ] **Step 4: Give walls and doors their visuals**

Create `src/HomeBlaze/HomeBlaze.Game/WallTextures.cs`:

```csharp
using HomeBlaze.Game.Visuals;

namespace HomeBlaze.Game;

/// <summary>
/// The textured boxes of walls and door lintels.
/// </summary>
internal static class WallTextures
{
    // One repeat of the bricks texture shows 2 m by 2 m.
    private const double BricksScale = 8;
    private static readonly string BricksUrl = GameAssets.Url(typeof(WallTextures), "objects/buildings/textures/bricks.jpg");

    public static GameVisualPart CreateBricks(GameVisualVector offset, GameVisualVector size) => new()
    {
        Shape = GameVisualShape.Box,
        Offset = offset,
        Size = size,
        Texture = BricksUrl,
        TextureScale = BricksScale
    };
}
```

In `src/HomeBlaze/HomeBlaze.Game/Wall.cs` add `using HomeBlaze.Game.Visuals;` after `using HomeBlaze.Abstractions.Attributes;`, and replace

```csharp
    public override bool IsSoundBlocking => true;
```

with

```csharp
    public override bool IsSoundBlocking => true;

    public override GameVisual Visual => _visual.Get((Width, Height, Depth),
        static size => new GameVisual([WallTextures.CreateBricks(default, new GameVisualVector(size.Width, size.Height, size.Depth))]));
```

and add as the first member of the class (before `DefaultLength`):

```csharp
    private readonly GameVisualCache<(int Width, int Height, int Depth)> _visual = new();

```

In `src/HomeBlaze/HomeBlaze.Game/Door.cs` add `using HomeBlaze.Game.Visuals;` after `using HomeBlaze.Abstractions.Attributes;`, add as the first members of the class:

```csharp
    private const double LeafThickness = 0.3;
    private const string LeafColor = "#8b5a2b";
    private const double OpenRotation = -90;

    private readonly GameVisualCache<(int Width, int Depth, int Height, bool IsOpen)> _visual = new();

    /// <summary>
    /// The height of the door leaf in tiles, 2.1 m; a lintel of the wall fills the opening above it.
    /// </summary>
    public const double LeafHeight = 8.4;

```

and replace

```csharp
    public override bool IsSoundBlocking => !IsOpen;
```

with

```csharp
    public override bool IsSoundBlocking => !IsOpen;

    public override GameVisual Visual => _visual.Get((Width, Depth, Height, IsOpen),
        static state => CreateVisual(state.Width, state.Depth, state.Height, state.IsOpen));
```

and add before the closing brace of the class (after `GetGame`):

```csharp

    private static GameVisual CreateVisual(int width, int depth, int height, bool isOpen)
    {
        var leafHeight = Math.Min(LeafHeight, height);
        var leaf = CreateLeaf(width, depth, leafHeight, isOpen ? OpenRotation : 0);
        if (height - leafHeight < 0.01)
        {
            return new GameVisual([leaf]);
        }

        var lintel = WallTextures.CreateBricks(new GameVisualVector(0, leafHeight, 0), new GameVisualVector(width, height - leafHeight, depth));
        return new GameVisual([lintel, leaf]);
    }

    // The leaf hangs on a hinge at the west end of an opening running east to west, or at the south end of one running
    // north to south, and opens toward the north or the west.
    private static GameVisualPart CreateLeaf(int width, int depth, double height, double rotation)
    {
        var isAlongX = width >= depth;
        return new GameVisualPart
        {
            Shape = GameVisualShape.Box,
            Offset = isAlongX ? new GameVisualVector(0, 0, (depth - LeafThickness) / 2) : new GameVisualVector((width - LeafThickness) / 2, 0, 0),
            Size = isAlongX ? new GameVisualVector(width, height, LeafThickness) : new GameVisualVector(LeafThickness, height, depth),
            Pivot = isAlongX ? new GameVisualPoint(0, depth / 2.0) : new GameVisualPoint(width / 2.0, 0),
            Rotation = rotation,
            Color = LeafColor
        };
    }
```

- [ ] **Step 5: Drop the wall and door specifics from the view**

Replace the record declaration in `src/HomeBlaze/HomeBlaze.Game/View/GameViewObject.cs` (keep the usings and the XML doc) so that it reads:

```csharp
internal sealed record GameViewObject(
    string Id,
    string Kind,
    int X,
    int Y,
    int Width,
    int Depth,
    int Height,
    int Facing,
    string Title,
    string Color,
    bool IsMovementBlocking,
    bool IsSelf,
    bool IsMicrophoneOn,
    bool IsAudioOn,
    [property: JsonIgnore] GameVisual? Visual,
    IReadOnlyList<GameViewPart> Parts)
{
    public const string PlayerKind = "player";
    public const string ObjectKind = "object";
}
```

In `src/HomeBlaze/HomeBlaze.Game/View/GameViewTracker.cs`:

Replace

```csharp
    private static bool HasSamePassage(GameViewObject known, GameObject gameObject) =>
        known.IsMovementBlocking == gameObject.IsMovementBlocking && known.IsOpen == IsOpen(gameObject);
```

with

```csharp
    private static bool HasSamePassage(GameViewObject known, GameObject gameObject) =>
        known.IsMovementBlocking == gameObject.IsMovementBlocking;
```

In `CreateView`, replace `gameObject.IsMovementBlocking, IsOpen(gameObject), isSelf,` with `gameObject.IsMovementBlocking, isSelf,`.

Replace

```csharp
    private static string GetKind(GameObject gameObject) => gameObject switch
    {
        GamePlayer => GameViewObject.PlayerKind,
        Wall => GameViewObject.WallKind,
        Door => GameViewObject.DoorKind,
        _ => GameViewObject.ObjectKind
    };
```

with

```csharp
    private static string GetKind(GameObject gameObject) =>
        gameObject is GamePlayer ? GameViewObject.PlayerKind : GameViewObject.ObjectKind;
```

Delete

```csharp
    private static bool IsOpen(GameObject gameObject) => gameObject is Door { IsOpen: true };

```

Replace

```csharp
    private string GetColor(GameObject gameObject) => gameObject switch
    {
        GamePlayer player => GetPlayerColor(player),
        Door => GameColors.Door,
        _ => GameColors.Wall
    };
```

with

```csharp
    // Other objects take their colors from their visual.
    private string GetColor(GameObject gameObject) => gameObject is GamePlayer player ? GetPlayerColor(player) : string.Empty;
```

In `src/HomeBlaze/HomeBlaze.Game/View/GameColors.cs` delete

```csharp
    public const string Wall = "#8d8577";
    public const string Door = "#8b5a2b";

```

- [ ] **Step 6: Run the unit tests**

Run: `dotnet build src/HomeBlaze/HomeBlaze.Game.Tests -c Debug`
Expected: Build succeeded, 0 warnings, 0 errors.

Run: `dotnet test src/HomeBlaze/HomeBlaze.Game.Tests -c Debug --filter "FullyQualifiedName!~E2E"`
Expected: all tests pass.

- [ ] **Step 7: Rewrite the renderer**

Replace the whole content of `src/HomeBlaze/HomeBlaze.Game/wwwroot/game-entities.js` with:

```javascript
// Draws the objects of the view. Every object but a player is built from the parts of its visual description: boxes with
// shared materials and glTF models, each loaded once per URL. Players are animated people. World coordinates are relative
// to an integer origin near the own player, so the GPU's 32-bit floats stay precise anywhere in the int range. One unit
// is one tile of 25 cm.
const REBASE_DISTANCE = 2048;
const TELEPORT_DISTANCE = 6;
// One meter, the size of one grass repeat and the spacing of the ground grid.
const METER = 4;
const FADED_VISIBILITY = 0.3;
const HEAD_HEIGHT = 0.8;
// Keeps a player walking for this part of a step after its move ended, so the next step does not flicker to idle.
const IDLE_DELAY_STEPS = 0.75;
// Lets the feet of the walk cycle keep pace with one tile per step interval, about 1.4 m/s.
const WALKING_SPEED_RATIO = 1.05;
// The names in the model file; the shirt takes the player's color.
const IDLE_ANIMATION = 'Idle_Neutral';
const WALKING_ANIMATION = 'Walk';
const SHIRT_MATERIAL = 'LightBrown';
// A part glides this long to a new offset or rotation, such as a door leaf that swings open.
const GLIDE_MS = 300;
const RADIANS_PER_DEGREE = Math.PI / 180;
// The color of a box without texture or color, and of the box that replaces a model that failed to load.
const FALLBACK_COLOR = '#8d8577';
// Wind bends a part by this many tiles per square tile of height above the object's base, swaying at the first speed and
// gusting at the second, both in radians per second.
const WIND_STRENGTH = 0.0015;
const WIND_SPEED = 1.7;
const GUST_SPEED = 0.37;
// Runs after the vertex is in world space; finalWorld[3] is the mesh's world position, which spreads the phase so that
// neighboring trees do not sway in step.
const WIND_SHADER = `
#ifdef GAME_WIND
    float gameWindHeight = max(worldPos.y - finalWorld[3].y, 0.0);
    float gameWindPhase = finalWorld[3].x * 0.13 + finalWorld[3].z * 0.07;
    float gameWindSway = sin(gameWindTime * ${toGlsl(WIND_SPEED)} + gameWindPhase) * 0.7
        + sin(gameWindTime * ${toGlsl(GUST_SPEED)} + gameWindPhase * 0.5) * 0.3;
    float gameWindBend = gameWindHeight * gameWindHeight * ${toGlsl(WIND_STRENGTH)} * gameWindSway;
    worldPos.x += gameWindBend;
    worldPos.z += gameWindBend * 0.4;
#endif
`;

// Seconds of the render clock, which the wind shader reads; the views of a tab share it.
let windTime = 0;
let WindPlugin = null;

function toGlsl(value) {
    return value.toFixed(6);
}

// Defined on first use, because Babylon is loaded after this module.
function getWindPlugin() {
    const BABYLON = window.BABYLON;
    WindPlugin ??= class extends BABYLON.MaterialPluginBase {
        constructor(material) {
            super(material, 'GameWind', 200, { GAME_WIND: false });
            this._enable(true);
        }

        prepareDefines(defines) {
            defines.GAME_WIND = true;
        }

        getClassName() {
            return 'GameWindPluginMaterial';
        }

        getUniforms() {
            return {
                ubo: [{ name: 'gameWindTime', size: 1, type: 'float' }],
                vertex: '#ifdef GAME_WIND\nuniform float gameWindTime;\n#endif\n',
            };
        }

        bindForSubMesh(uniformBuffer) {
            uniformBuffer.updateFloat('gameWindTime', windTime);
        }

        getCustomCode(shaderType) {
            return shaderType === 'vertex' ? { CUSTOM_VERTEX_UPDATE_WORLDPOS: WIND_SHADER } : null;
        }
    };
    return WindPlugin;
}

export async function createEntities(scene, options) {
    const BABYLON = window.BABYLON;
    const entities = new Map();
    const targetMarkers = [];
    // Shared by the objects of the view: box materials by texture and color, model loads by URL, and the wind variant of
    // each model material.
    const boxMaterials = new Map();
    const models = new Map();
    const windMaterials = new Map();
    // Written to the canvas for tests and diagnosis.
    const stats = { parts: 0, models: 0, failures: 0 };
    // Reused by every frame, so the render loop does not allocate.
    const selfPosition = { x: 0, z: 0 };
    const segment = { enter: 0, exit: 1 };
    const idleDelay = options.stepIntervalMs * IDLE_DELAY_STEPS;
    let originX = null;
    let originY = null;
    let selfId = null;

    const ground = BABYLON.MeshBuilder.CreateGround('ground', { width: 600, height: 600 }, scene);
    ground.material = await createGroundMaterial();
    ground.isPickable = false;

    const targetMaterial = new BABYLON.StandardMaterial('target', scene);
    targetMaterial.emissiveColor = new BABYLON.Color3(1, 0.88, 0.4);
    targetMaterial.disableLighting = true;
    targetMaterial.alpha = 0.4;

    const blobMaterial = new BABYLON.StandardMaterial('blob', scene);
    blobMaterial.diffuseColor = BABYLON.Color3.Black();
    blobMaterial.specularColor = BABYLON.Color3.Black();
    blobMaterial.alpha = 0.28;

    // Not added to the scene; every player instantiates its own copy.
    const character = await BABYLON.LoadAssetContainerAsync(`${options.baseUrl}models/Casual_2.glb`, scene);
    updateStats();

    async function createGroundMaterial() {
        // The border of each repeat draws the meter grid; half of the stroke lies outside, so each edge shows half of it.
        // It stays wide enough to survive the smaller mipmaps that the usual camera distance samples.
        const image = new Image();
        image.src = `${options.baseUrl}textures/grass.jpg`;
        await image.decode();
        const texture = new BABYLON.DynamicTexture('grass', 512, scene, true);
        const context = texture.getContext();
        context.drawImage(image, 0, 0, 512, 512);
        context.strokeStyle = 'rgba(0, 0, 0, 0.16)';
        context.lineWidth = 8;
        context.strokeRect(0, 0, 512, 512);
        texture.update();
        // Dynamic textures clamp by default.
        texture.wrapU = BABYLON.Texture.WRAP_ADDRESSMODE;
        texture.wrapV = BABYLON.Texture.WRAP_ADDRESSMODE;
        texture.anisotropicFilteringLevel = 8;
        texture.uScale = 600 / METER;
        texture.vScale = 600 / METER;
        const material = new BABYLON.StandardMaterial('ground', scene);
        material.diffuseTexture = texture;
        material.specularColor = BABYLON.Color3.Black();
        return material;
    }

    function updateStats() {
        const element = options.statsElement;
        if (element) {
            element.dataset.gameParts = String(stats.parts);
            element.dataset.gameModels = String(stats.models);
            element.dataset.gameModelFailures = String(stats.failures);
        }
    }

    function ensureOrigin(x, y) {
        if (originX === null || Math.abs(x - originX) > REBASE_DISTANCE || Math.abs(y - originY) > REBASE_DISTANCE) {
            originX = x;
            originY = y;
            for (const entity of entities.values()) {
                place(entity, entity.view.x, entity.view.y);
            }
        }
    }

    // Players stand on the center of their footprint; the parts of other objects start at its south-west corner.
    function positionOf(view, x, y) {
        return view.kind === 'player'
            ? new BABYLON.Vector3(x - originX + view.width / 2, 0, y - originY + view.depth / 2)
            : new BABYLON.Vector3(x - originX, 0, y - originY);
    }

    function getBoxMaterial(texture, color) {
        const key = `${texture ?? ''}|${color ?? ''}`;
        let material = boxMaterials.get(key);
        if (!material) {
            material = new BABYLON.StandardMaterial(`box-${boxMaterials.size}`, scene);
            if (texture) {
                material.diffuseTexture = new BABYLON.Texture(texture, scene);
            }

            // The diffuse color tints the texture, so a textured box without a color stays white.
            material.diffuseColor = BABYLON.Color3.FromHexString(color ?? (texture ? '#ffffff' : FALLBACK_COLOR));
            material.specularColor = new BABYLON.Color3(0.04, 0.04, 0.04);
            boxMaterials.set(key, material);
        }

        return material;
    }

    // The UVs follow the size of each face, so the texture keeps its scale on every box.
    function createBox(name, part) {
        const scale = part.ts > 0 ? part.ts : 1;
        const sideUv = new BABYLON.Vector4(0, 0, part.w / scale, part.h / scale);
        const endUv = new BABYLON.Vector4(0, 0, part.d / scale, part.h / scale);
        const topUv = new BABYLON.Vector4(0, 0, part.w / scale, part.d / scale);
        const box = BABYLON.MeshBuilder.CreateBox(name, {
            width: part.w,
            height: part.h,
            depth: part.d,
            faceUV: [sideUv, sideUv, endUv, endUv, topUv, topUv],
            wrap: true,
        }, scene);
        box.material = getBoxMaterial(part.t, part.c);
        return box;
    }

    function loadModel(url) {
        let loading = models.get(url);
        if (!loading) {
            // Logged once per URL, because the failed load stays cached.
            loading = BABYLON.LoadAssetContainerAsync(url, scene).catch(error => {
                console.warn(`The model ${url} could not be loaded: ${error?.message ?? error}`);
                return null;
            });
            models.set(url, loading);
        }

        return loading;
    }

    function getWindMaterial(material) {
        let windMaterial = windMaterials.get(material);
        if (!windMaterial) {
            windMaterial = material.clone(`${material.name}-wind`);
            const Plugin = getWindPlugin();
            windMaterial.gameWind = new Plugin(windMaterial);
            windMaterials.set(material, windMaterial);
        }

        return windMaterial;
    }

    function addMeshes(entity, meshes) {
        const visibility = entity.isFaded ? FADED_VISIBILITY : 1;
        for (const mesh of meshes) {
            mesh.isPickable = false;
            mesh.visibility = visibility;
            entity.meshes.push(mesh);
        }
    }

    // A part hangs on a pivot node that turns it, with an offset node at the south-west bottom corner of its box below,
    // so that gliding moves only these two nodes.
    function createPart(entity, part, index) {
        const name = `${entity.view.id}-${index}`;
        const pivot = new BABYLON.TransformNode(`${name}-pivot`, scene);
        pivot.parent = entity.root;
        pivot.position.copyFromFloats(part.px, 0, part.pz);
        pivot.rotation.y = part.r * RADIANS_PER_DEGREE;
        const offset = new BABYLON.TransformNode(`${name}-offset`, scene);
        offset.parent = pivot;
        offset.position.copyFromFloats(part.x - part.px, part.y, part.z - part.pz);
        const node = { part, pivot, offset, instance: null, state: null, glide: null };
        stats.parts++;
        if (part.k === 'model') {
            addModel(entity, node, name).catch(error => console.warn(`The model ${part.m} could not be shown: ${error?.message ?? error}`));
        } else {
            const box = createBox(name, part);
            box.parent = offset;
            box.position.copyFromFloats(part.w / 2, part.h / 2, part.d / 2);
            addMeshes(entity, [box]);
        }

        return node;
    }

    async function addModel(entity, node, name) {
        const container = await loadModel(node.part.m);
        if (entity.isDisposed) {
            return;
        }

        if (container) {
            showModel(entity, node, name, container);
        } else {
            showFallback(entity, node, name);
        }

        updateStats();
    }

    function showModel(entity, node, name, container) {
        const part = node.part;
        const prefix = `${name}-`;
        const instance = container.instantiateModelsToScene(nodeName => prefix + nodeName, false, { doNotInstantiate: true });
        // The root keeps the loader's handedness conversion; its bounds are measured before it gets a parent or a scale.
        const root = instance.rootNodes[0];
        const { min, max } = root.getHierarchyBoundingVectors(true);
        const scale = Math.min(
            part.w / Math.max(max.x - min.x, 1e-6),
            part.h / Math.max(max.y - min.y, 1e-6),
            part.d / Math.max(max.z - min.z, 1e-6));
        root.scaling.scaleInPlace(scale);
        root.parent = node.offset;
        root.position.copyFromFloats(part.w / 2, -min.y * scale, part.d / 2);
        const meshes = root.getChildMeshes(false);
        if (part.e === 'wind') {
            for (const mesh of meshes) {
                if (mesh.material) {
                    mesh.material = getWindMaterial(mesh.material);
                }
            }
        }

        playAnimation(instance, prefix, part.a);
        addMeshes(entity, meshes);
        node.instance = instance;
        node.state = 'model';
        stats.models++;
    }

    function playAnimation(instance, prefix, animation) {
        for (const group of instance.animationGroups) {
            if (animation && group.name === prefix + animation) {
                group.start(true);
            } else {
                group.stop();
            }
        }
    }

    function showFallback(entity, node, name) {
        const part = node.part;
        const box = createBox(`${name}-fallback`, { ...part, t: undefined, c: FALLBACK_COLOR });
        box.parent = node.offset;
        box.position.copyFromFloats(part.w / 2, part.h / 2, part.d / 2);
        addMeshes(entity, [box]);
        node.state = 'fallback';
        stats.failures++;
    }

    // The box around all parts relative to the object's corner, which fading tests; rotations are left out.
    function getBounds(parts) {
        if (parts.length === 0) {
            return { minX: 0, minY: 0, minZ: 0, maxX: 0, maxY: 0, maxZ: 0 };
        }

        const bounds = { minX: Infinity, minY: Infinity, minZ: Infinity, maxX: -Infinity, maxY: -Infinity, maxZ: -Infinity };
        for (const part of parts) {
            bounds.minX = Math.min(bounds.minX, part.x);
            bounds.minY = Math.min(bounds.minY, part.y);
            bounds.minZ = Math.min(bounds.minZ, part.z);
            bounds.maxX = Math.max(bounds.maxX, part.x + part.w);
            bounds.maxY = Math.max(bounds.maxY, part.y + part.h);
            bounds.maxZ = Math.max(bounds.maxZ, part.z + part.d);
        }

        return bounds;
    }

    function createObject(entity) {
        entity.root = new BABYLON.TransformNode(entity.view.id, scene);
        entity.bounds = getBounds(entity.view.parts);
        entity.partNodes = entity.view.parts.map((part, index) => createPart(entity, part, index));
    }

    function createLabel(view, parent) {
        const texture = new BABYLON.DynamicTexture(`${view.id}-label`, { width: 512, height: 96 }, scene, true);
        texture.hasAlpha = true;
        const context = texture.getContext();
        context.clearRect(0, 0, 512, 96);
        context.fillStyle = 'rgba(0, 0, 0, 0.55)';
        context.beginPath();
        context.roundRect(4, 14, 504, 68, 16);
        context.fill();
        context.fillStyle = '#ffffff';
        context.font = '500 40px Roboto, Arial, sans-serif';
        context.textAlign = 'center';
        context.textBaseline = 'middle';
        context.fillText(view.isSelf ? `${view.title} (you)` : view.title, 256, 50, 480);
        texture.update();
        const material = new BABYLON.StandardMaterial(`${view.id}-label`, scene);
        material.diffuseTexture = texture;
        material.emissiveColor = BABYLON.Color3.White();
        material.disableLighting = true;
        material.useAlphaFromDiffuseTexture = true;
        const plane = BABYLON.MeshBuilder.CreatePlane(`${view.id}-label`, { width: 6, height: 1.125 }, scene);
        plane.material = material;
        plane.billboardMode = BABYLON.Mesh.BILLBOARDMODE_ALL;
        plane.isPickable = false;
        plane.parent = parent;
        plane.position.y = view.height + 1.4;
        return material;
    }

    function createPlayer(entity) {
        const view = entity.view;
        const holder = new BABYLON.TransformNode(view.id, scene);
        const prefix = `${view.id}-`;
        const instance = character.instantiateModelsToScene(name => prefix + name, true);
        // The root keeps the loader's handedness conversion, which leaves the character looking north (+z).
        const model = instance.rootNodes[0];
        model.parent = holder;
        const { min, max } = model.getHierarchyBoundingVectors(true);
        const scale = view.height / (max.y - min.y);
        model.scaling.scaleInPlace(scale);
        model.position.y = -min.y * scale;

        const meshes = model.getChildMeshes(false);
        // Every player owns the materials cloned for it; the blob material is shared.
        const materials = [...new Set(meshes.map(mesh => mesh.material).filter(Boolean))];
        const shirt = materials.find(material => material.name === prefix + SHIRT_MATERIAL);
        if (shirt) {
            const color = BABYLON.Color3.FromHexString(view.color);
            if ('albedoColor' in shirt) {
                shirt.albedoColor = color.toLinearSpace();
            } else {
                shirt.diffuseColor = color;
            }
        }

        for (const mesh of meshes) {
            mesh.isPickable = false;
        }

        const blob = BABYLON.MeshBuilder.CreateDisc(`${view.id}-blob`, { radius: view.width * 0.55, tessellation: 24 }, scene);
        blob.material = blobMaterial;
        blob.rotation.x = Math.PI / 2;
        blob.position.y = 0.02;
        blob.parent = holder;
        blob.isPickable = false;

        materials.push(createLabel(view, holder));

        const find = name => instance.animationGroups.find(group => group.name === prefix + name);
        const idle = find(IDLE_ANIMATION);
        const walking = find(WALKING_ANIMATION);
        for (const group of instance.animationGroups) {
            group.stop();
        }

        idle?.start(true);
        entity.root = holder;
        entity.animations = { idle, walking, isWalking: false };
        entity.instance = instance;
        entity.meshes = meshes;
        entity.materials = materials;
    }

    function createEntity(view) {
        const entity = {
            view, root: null, tween: null, idleAt: 0, isFaded: false, isDisposed: false,
            meshes: [], materials: [], partNodes: [], bounds: null, animations: null, instance: null,
        };
        if (view.kind === 'player') {
            createPlayer(entity);
        } else {
            createObject(entity);
        }

        updateStats();
        return entity;
    }

    function disposeInstance(instance, meshes) {
        for (const group of instance.animationGroups) {
            group.dispose();
        }

        for (const skeleton of instance.skeletons) {
            skeleton.dispose();
        }

        for (const mesh of meshes) {
            mesh.morphTargetManager?.dispose();
        }
    }

    function disposePart(node) {
        stats.parts--;
        if (node.state === 'model') {
            stats.models--;
            disposeInstance(node.instance, []);
        } else if (node.state === 'fallback') {
            stats.failures--;
        }
    }

    // Shared box, model and wind materials stay; the scene disposes them.
    function disposeEntity(entity) {
        entity.isDisposed = true;
        if (entity.instance) {
            disposeInstance(entity.instance, entity.meshes);
        }

        for (const material of entity.materials) {
            material.dispose(true, true);
        }

        for (const node of entity.partNodes) {
            disposePart(node);
        }

        entity.root.dispose(false, false);
        updateStats();
    }

    function place(entity, x, y) {
        entity.root.position = positionOf(entity.view, x, y);
        entity.tween = null;
        setWalking(entity, false);
    }

    function moveTo(entity, x, y) {
        const target = positionOf(entity.view, x, y);
        if (BABYLON.Vector3.Distance(entity.root.position, target) > TELEPORT_DISTANCE) {
            place(entity, x, y);
            return;
        }

        entity.tween = { from: entity.root.position.clone(), to: target, start: performance.now(), duration: options.stepIntervalMs };
        setWalking(entity, true);
    }

    function setWalking(entity, isWalking) {
        entity.idleAt = 0;
        const animations = entity.animations;
        if (!animations || animations.isWalking === isWalking) {
            return;
        }

        animations.isWalking = isWalking;
        if (isWalking) {
            animations.idle?.stop();
            animations.walking?.start(true, WALKING_SPEED_RATIO);
        } else {
            animations.walking?.stop();
            animations.idle?.start(true);
        }
    }

    function setFacing(entity, facing) {
        if (entity.view.kind === 'player') {
            entity.root.rotation.y = facing * Math.PI / 4;
        }
    }

    function hideTarget() {
        for (const marker of targetMarkers) {
            marker.setEnabled(false);
        }
    }

    function setFaded(entity, isFaded) {
        if (entity.isFaded === isFaded) {
            return;
        }

        entity.isFaded = isFaded;
        const visibility = isFaded ? FADED_VISIBILITY : 1;
        for (const mesh of entity.meshes) {
            mesh.visibility = visibility;
        }
    }

    // What a glide cannot animate: a change of any of it rebuilds the object.
    function getPartShape(part) {
        return [part.k, part.w, part.h, part.d, part.px, part.pz, part.t, part.ts, part.c, part.m, part.e, part.a].join('|');
    }

    function haveSameShapes(previous, parts) {
        return previous.length === parts.length && previous.every((part, index) => getPartShape(part) === getPartShape(parts[index]));
    }

    // The own player's label reads "(you)", so becoming or ceasing to be the own player recreates it.
    function hasShapeChanged(previous, view) {
        if (previous.kind !== view.kind) {
            return true;
        }

        if (view.kind !== 'player') {
            return !haveSameShapes(previous.parts, view.parts);
        }

        return previous.width !== view.width || previous.depth !== view.depth || previous.height !== view.height
            || previous.color !== view.color || previous.title !== view.title || previous.isSelf !== view.isSelf;
    }

    function updateParts(entity, parts) {
        entity.partNodes.forEach((node, index) => {
            const part = parts[index];
            const previous = node.part;
            if (part.x !== previous.x || part.y !== previous.y || part.z !== previous.z || part.r !== previous.r) {
                node.glide = {
                    fromOffset: node.offset.position.clone(),
                    toOffset: new BABYLON.Vector3(part.x - part.px, part.y, part.z - part.pz),
                    fromRotation: node.pivot.rotation.y,
                    toRotation: part.r * RADIANS_PER_DEGREE,
                    start: performance.now(),
                };
            }

            node.part = part;
        });
        entity.bounds = getBounds(parts);
    }

    function animateGlide(node, now) {
        const glide = node.glide;
        const progress = Math.min(1, (now - glide.start) / GLIDE_MS);
        // Eases out, so a door leaf slows down before it stops.
        const eased = 1 - (1 - progress) * (1 - progress);
        BABYLON.Vector3.LerpToRef(glide.fromOffset, glide.toOffset, eased, node.offset.position);
        node.pivot.rotation.y = glide.fromRotation + (glide.toRotation - glide.fromRotation) * eased;
        if (progress >= 1) {
            node.glide = null;
        }
    }

    function clearSelf() {
        selfId = null;
        hideTarget();
        for (const other of entities.values()) {
            setFaded(other, false);
        }
    }

    // Whether the segment from the start by the offset crosses the bounds of the entity, clipping the part of the segment
    // inside them one axis at a time; allocation free for the render loop.
    function crosses(entity, startX, startY, startZ, offsetX, offsetY, offsetZ) {
        const position = entity.root.position;
        const bounds = entity.bounds;
        segment.enter = 0;
        segment.exit = 1;
        return clip(startX, offsetX, position.x + bounds.minX, position.x + bounds.maxX)
            && clip(startY, offsetY, bounds.minY, bounds.maxY)
            && clip(startZ, offsetZ, position.z + bounds.minZ, position.z + bounds.maxZ);
    }

    function clip(start, offset, low, high) {
        if (Math.abs(offset) < 1e-6) {
            return start >= low && start <= high;
        }

        let near = (low - start) / offset;
        let far = (high - start) / offset;
        if (near > far) {
            const swap = near;
            near = far;
            far = swap;
        }

        segment.enter = Math.max(segment.enter, near);
        segment.exit = Math.min(segment.exit, far);
        return segment.enter <= segment.exit;
    }

    return {
        /** Adds or updates the object; returns true when it was the own player and is not anymore. */
        upsert(view) {
            const isSelfLost = !view.isSelf && view.id === selfId;
            if (isSelfLost) {
                clearSelf();
            }

            if (view.isSelf) {
                selfId = view.id;
                ensureOrigin(view.x, view.y);
            } else if (originX === null) {
                ensureOrigin(view.x, view.y);
            }

            let entity = entities.get(view.id);
            if (!entity || hasShapeChanged(entity.view, view)) {
                const previous = entity?.view;
                if (entity) {
                    disposeEntity(entity);
                }

                // A recreated own player keeps the predicted position, which the input owns.
                const shown = view.isSelf && previous?.isSelf ? { ...view, x: previous.x, y: previous.y, facing: previous.facing } : view;
                entity = createEntity(shown);
                entities.set(view.id, entity);
                place(entity, shown.x, shown.y);
                setFacing(entity, shown.facing);
                return isSelfLost;
            }

            const hasMoved = entity.view.x !== view.x || entity.view.y !== view.y;
            const previousFacing = entity.view.facing;
            if (view.kind !== 'player') {
                updateParts(entity, view.parts);
            }

            // The own player's position and facing belong to the input, which predicts them.
            entity.view = view.isSelf ? { ...view, x: entity.view.x, y: entity.view.y, facing: previousFacing } : view;
            if (!view.isSelf) {
                if (hasMoved) {
                    moveTo(entity, view.x, view.y);
                }

                setFacing(entity, view.facing);
            }

            return isSelfLost;
        },

        /** Removes the object; returns true when it was the own player. */
        remove(id) {
            const entity = entities.get(id);
            if (entity) {
                disposeEntity(entity);
                entities.delete(id);
            }

            if (id !== selfId) {
                return false;
            }

            clearSelf();
            return true;
        },

        /** Releases what the scene does not own; call before disposing the scene. */
        dispose() {
            character.dispose();
            for (const loading of models.values()) {
                loading.then(container => container?.dispose());
            }
        },

        setSelf(x, y, facing, animate) {
            const entity = entities.get(selfId);
            if (!entity) {
                return;
            }

            const hasMoved = entity.view.x !== x || entity.view.y !== y;
            entity.view = { ...entity.view, x, y, facing };
            ensureOrigin(x, y);
            if (hasMoved) {
                if (animate) {
                    moveTo(entity, x, y);
                } else {
                    place(entity, x, y);
                }
            }

            setFacing(entity, facing);
        },

        getSelf() {
            return entities.get(selfId)?.view ?? null;
        },

        getBlockers() {
            const blockers = [];
            for (const entity of entities.values()) {
                if (entity.view.id !== selfId && entity.view.isMovementBlocking) {
                    blockers.push(entity.view);
                }
            }

            return blockers;
        },

        setTarget(tiles) {
            while (targetMarkers.length < tiles.length) {
                const marker = BABYLON.MeshBuilder.CreateGround(`target-${targetMarkers.length}`, { width: 0.92, height: 0.92 }, scene);
                marker.material = targetMaterial;
                marker.isPickable = false;
                targetMarkers.push(marker);
            }

            targetMarkers.forEach((marker, index) => {
                const tile = tiles[index];
                const isVisible = Boolean(tile) && originX !== null && selfId !== null;
                marker.setEnabled(isVisible);
                if (isVisible) {
                    marker.position.copyFromFloats(tile.x - originX + 0.5, 0.04, tile.y - originY + 0.5);
                }
            });
        },

        animate(now) {
            windTime = now / 1000;
            for (const entity of entities.values()) {
                for (const node of entity.partNodes) {
                    if (node.glide) {
                        animateGlide(node, now);
                    }
                }

                const tween = entity.tween;
                if (tween) {
                    const progress = Math.min(1, (now - tween.start) / tween.duration);
                    BABYLON.Vector3.LerpToRef(tween.from, tween.to, progress, entity.root.position);
                    if (progress >= 1) {
                        entity.tween = null;
                        entity.idleAt = now + idleDelay;
                    }
                } else if (entity.idleAt !== 0 && now >= entity.idleAt) {
                    setWalking(entity, false);
                }
            }

            const self = entities.get(selfId);
            if (!self) {
                return null;
            }

            // The ground follows in whole meters, so its grid stays on the tiles.
            ground.position.x = Math.round((self.root.position.x + originX) / METER) * METER - originX;
            ground.position.z = Math.round((self.root.position.z + originY) / METER) * METER - originY;
            selfPosition.x = self.root.position.x;
            selfPosition.z = self.root.position.z;
            return selfPosition;
        },

        // Fades the movement blocking objects between the camera and the own player's head.
        afterCameraMoved(camera) {
            const self = entities.get(selfId);
            if (!self) {
                return;
            }

            const eye = camera.position;
            const offsetX = self.root.position.x - eye.x;
            const offsetY = self.view.height * HEAD_HEIGHT - eye.y;
            const offsetZ = self.root.position.z - eye.z;
            for (const entity of entities.values()) {
                if (entity.view.kind !== 'player') {
                    setFaded(entity, entity.view.isMovementBlocking && crosses(entity, eye.x, eye.y, eye.z, offsetX, offsetY, offsetZ));
                }
            }
        },
    };
}
```

In `src/HomeBlaze/HomeBlaze.Game/wwwroot/game.js` replace

```javascript
        this.entities = await createEntities(this.scene, this.options);
```

with

```javascript
        // The canvas carries the part and model counts for tests and diagnosis.
        this.entities = await createEntities(this.scene, { ...this.options, statsElement: this.canvas });
```

- [ ] **Step 8: Extend the E2E tests with the part counts**

In `src/HomeBlaze/HomeBlaze.Game.Tests/E2E/GamePageTests.cs`, in `WhenWalkingIntoAWall_ThenThePositionStays`, after

```csharp
        await Expect(page.GetByTestId("game-target-title")).ToHaveTextAsync("Wall", new() { Timeout = Timeout });
```

add

```csharp
        await Expect(page.GetByTestId("game-canvas")).ToHaveAttributeAsync("data-game-parts", "1", new() { Timeout = Timeout });
```

In `WhenAClosedDoorIsOpenedWithItsNumberKey_ThenThePlayerStepsThroughIt`, after

```csharp
        await Expect(page.GetByTestId("game-target-title")).ToHaveTextAsync("Door", new() { Timeout = Timeout });
```

add

```csharp
        // The lintel and the leaf.
        await Expect(page.GetByTestId("game-canvas")).ToHaveAttributeAsync("data-game-parts", "2", new() { Timeout = Timeout });
```

- [ ] **Step 9: Run all tests**

Run: `dotnet build src/HomeBlaze/HomeBlaze.Game.Tests -c Debug`
Expected: Build succeeded, 0 warnings, 0 errors.

Run: `dotnet test src/HomeBlaze/HomeBlaze.Game.Tests -c Debug --filter "FullyQualifiedName!~E2E"`
Expected: all pass.

Run: `dotnet test src/HomeBlaze/HomeBlaze.Game.Tests -c Debug --filter "FullyQualifiedName~E2E"`
Expected: all pass (the door opens and closes, the part counts match, no `JSException` breaks the view).

- [ ] **Step 10: Commit and push**

```bash
git add src/HomeBlaze/HomeBlaze.Game/WallTextures.cs src/HomeBlaze/HomeBlaze.Game/Wall.cs src/HomeBlaze/HomeBlaze.Game/Door.cs src/HomeBlaze/HomeBlaze.Game/View/GameViewObject.cs src/HomeBlaze/HomeBlaze.Game/View/GameViewTracker.cs src/HomeBlaze/HomeBlaze.Game/View/GameColors.cs src/HomeBlaze/HomeBlaze.Game/wwwroot/game-entities.js src/HomeBlaze/HomeBlaze.Game/wwwroot/game.js src/HomeBlaze/HomeBlaze.Game/wwwroot/objects/buildings/textures/bricks.jpg src/HomeBlaze/HomeBlaze.Game/wwwroot/objects/buildings/textures/LICENSE.md src/HomeBlaze/HomeBlaze.Game/wwwroot/textures/LICENSE.md src/HomeBlaze/HomeBlaze.Game.Tests/Objects/Buildings/WallTests.cs src/HomeBlaze/HomeBlaze.Game.Tests/GameDoorTests.cs src/HomeBlaze/HomeBlaze.Game.Tests/View/GameViewTrackerTests.cs src/HomeBlaze/HomeBlaze.Game.Tests/E2E/GamePageTests.cs
git commit -m "feat: render every game object from its visual description" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push origin feat/homeblaze-game
```

---

## Task 3: Public object API, walls and doors move to `Objects/Buildings`

**Runnable:** yes. Controller restarts the app; the demo page loads the demo world with the new `$type` names, and doors open, close and refuse closing while someone stands in them.

**Files:**
- Move: `src/HomeBlaze/HomeBlaze.Game/Wall.cs`, `Door.cs`, `WallTextures.cs` to `src/HomeBlaze/HomeBlaze.Game/Objects/Buildings/`
- Create: `src/HomeBlaze/HomeBlaze.Game/Game.Objects.cs`, `GameObjectSnapshot.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Game/Game.Actions.cs`, `GameObject.cs`, `Game.Runtime.cs`, `World/WallPlacement.cs`
- Modify: `src/HomeBlaze/HomeBlaze/Data/Files/Demo/Game.json` (committed demo world, `$type` names only)
- Move (tests): `src/HomeBlaze/HomeBlaze.Game.Tests/GameDoorTests.cs` to `Objects/Buildings/DoorTests.cs`
- Create (tests): `src/HomeBlaze/HomeBlaze.Game.Tests/GameObjectsTests.cs`
- Modify (tests): `src/HomeBlaze/HomeBlaze.Game.Tests/HomeBlaze.Game.Tests.csproj`, `GameSerializationTests.cs`

- [ ] **Step 0 (controller, before dispatching the task): release the private demo world**

The working copy of the demo world is a private layout with the skip-worktree flag. The user approved replacing it; back it up first, then let the file follow the repository:

```bash
SCRATCH=/tmp/claude-1000/-home-rico-GitHub-Namotion-Interceptor--claude-worktrees-bridge-cse-011ShuL5XgwWZqRmSZFhkZve/3d51e483-5513-56d7-b4a2-5748ff2f1414/scratchpad
mkdir -p "$SCRATCH/world"
cp src/HomeBlaze/HomeBlaze/Data/Files/Demo/Game.json "$SCRATCH/world/Game.local-backup.json"
test -s "$SCRATCH/world/Game.local-backup.json" && echo "backup ok"
```

Expected: `backup ok`. Only after that:

```bash
git update-index --no-skip-worktree src/HomeBlaze/HomeBlaze/Data/Files/Demo/Game.json
git checkout -- src/HomeBlaze/HomeBlaze/Data/Files/Demo/Game.json
git ls-files -v src/HomeBlaze/HomeBlaze/Data/Files/Demo/Game.json
```

Expected: `H src/HomeBlaze/HomeBlaze/Data/Files/Demo/Game.json` (no longer `S`). Leave every other skip-worktree file under `Data` alone.

- [ ] **Step 1: Write the failing object API tests**

Create `src/HomeBlaze/HomeBlaze.Game.Tests/GameObjectsTests.cs`:

```csharp
using HomeBlaze.Game.Tests.Testing;

namespace HomeBlaze.Game.Tests;

public class GameObjectsTests
{
    private readonly GameTestContext _context = new();

    [Fact]
    public async Task WhenAnObjectOfTheGameIsRemoved_ThenItIsGoneAndWritten()
    {
        // Arrange
        var wall = _context.AddWall(5, 5);

        // Act
        await _context.Game.RemoveAsync(wall, CancellationToken.None);

        // Assert
        Assert.Empty(_context.Game.Objects);
        Assert.Equal(1, _context.Root.WriteCount);
    }

    [Fact]
    public async Task WhenAForeignObjectIsRemoved_ThenRemovingIsRefused()
    {
        // Arrange
        _context.AddWall(5, 5);

        // Act
        var exception = await Record.ExceptionAsync(() => _context.Game.RemoveAsync(new Wall(), CancellationToken.None));

        // Assert
        Assert.Equal("The object is not part of this game.", Assert.IsType<InvalidOperationException>(exception).Message);
        Assert.Single(_context.Game.Objects);
        Assert.Equal(0, _context.Root.WriteCount);
    }

    [Fact]
    public async Task WhenAChangeIsApplied_ThenItIsKeptAndWritten()
    {
        // Arrange
        var wall = _context.AddWall(5, 5);

        // Act
        await _context.Game.UpdateAsync(wall, () => wall.Height = 12, CancellationToken.None);

        // Assert
        Assert.Equal(12, wall.Height);
        Assert.Equal(1, _context.Root.WriteCount);
    }

    [Fact]
    public async Task WhenAChangeMakesAnObjectOverlapAnother_ThenItIsUndoneAndRefused()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1", x: 4, y: 9);
        var wall = _context.AddWall(0, 10);
        _context.AddWall(10, 10);

        // Act
        var exception = await Record.ExceptionAsync(() => _context.Game.UpdateAsync(wall, () =>
        {
            wall.X = 8;
            wall.Height = 3;
        }, CancellationToken.None));
        _context.TimeProvider.Advance(Game.StepInterval);
        var step = _context.Game.TryMove(player, GameDirection.West, turnOnly: false);

        // Assert
        Assert.Equal("Something is in the way.", Assert.IsType<InvalidOperationException>(exception).Message);
        Assert.Equal((0, 4, 10), (wall.X, wall.Width, wall.Height));
        Assert.Equal(0, _context.Root.WriteCount);
        Assert.False(step.IsAccepted);
    }

    [Fact]
    public async Task WhenTheChangeThrows_ThenItIsUndone()
    {
        // Arrange
        var wall = _context.AddWall(0, 10);

        // Act
        var exception = await Record.ExceptionAsync(() => _context.Game.UpdateAsync(wall, () =>
        {
            wall.Height = 3;
            throw new InvalidOperationException("Not now.");
        }, CancellationToken.None));

        // Assert
        Assert.Equal("Not now.", Assert.IsType<InvalidOperationException>(exception).Message);
        Assert.Equal(10, wall.Height);
        Assert.Equal(0, _context.Root.WriteCount);
    }

    [Fact]
    public async Task WhenAForeignObjectIsUpdated_ThenUpdatingIsRefused()
    {
        // Arrange
        var wall = new Wall();

        // Act
        var exception = await Record.ExceptionAsync(() => _context.Game.UpdateAsync(wall, () => wall.Height = 3, CancellationToken.None));

        // Assert
        Assert.Equal("The object is not part of this game.", Assert.IsType<InvalidOperationException>(exception).Message);
        Assert.Equal(10, wall.Height);
    }

    [Fact]
    public async Task WhenAnObjectIsReplaced_ThenTheReplacementsTakeItsPlaceUnderNewNames()
    {
        // Arrange
        var wall = _context.AddWall(0, 10, width: 10);
        var west = new Wall { X = 0, Y = 10, Width = 3 };
        var door = new Door { X = 3, Y = 10, Width = 4 };
        var east = new Wall { X = 7, Y = 10, Width = 3 };

        // Act
        await _context.Game.ReplaceAsync(wall, [("wall", west), ("door", door), ("wall", east)], CancellationToken.None);

        // Assert
        var objects = _context.Game.Objects;
        Assert.Equal(3, objects.Count);
        Assert.DoesNotContain(wall, objects.Values);
        Assert.Equal(2, objects.Count(pair => pair.Key.StartsWith("wall-", StringComparison.Ordinal)));
        Assert.Same(door, Assert.Single(objects, pair => pair.Key.StartsWith("door-", StringComparison.Ordinal)).Value);
        Assert.Equal(1, _context.Root.WriteCount);
    }

    [Fact]
    public async Task WhenAReplacementWouldOverlapAnotherObject_ThenNothingChanges()
    {
        // Arrange
        var wall = _context.AddWall(0, 10, width: 10);
        var other = _context.AddWall(12, 10);
        var before = new Dictionary<string, GameObject>(_context.Game.Objects);

        // Act
        var exception = await Record.ExceptionAsync(() =>
            _context.Game.ReplaceAsync(wall, [("wall", new Wall { X = 5, Y = 10, Width = 10 })], CancellationToken.None));

        // Assert
        Assert.Equal("Something is in the way.", Assert.IsType<InvalidOperationException>(exception).Message);
        Assert.Equal(before, _context.Game.Objects);
        Assert.Contains(other, _context.Game.Objects.Values);
        Assert.Equal(0, _context.Root.WriteCount);
    }
}
```

Note: in the `UpdateAsync` overlap test the player stands at 4, 9, right east of the wall (tiles 0 to 3 of row 10). Its step west is refused only when the index holds the wall at its restored place, so the test covers the index as well as the properties.

- [ ] **Step 2: Move the door tests and update them**

```bash
mkdir -p src/HomeBlaze/HomeBlaze.Game.Tests/Objects/Buildings
git mv src/HomeBlaze/HomeBlaze.Game.Tests/GameDoorTests.cs src/HomeBlaze/HomeBlaze.Game.Tests/Objects/Buildings/DoorTests.cs
```

In `src/HomeBlaze/HomeBlaze.Game.Tests/Objects/Buildings/DoorTests.cs` replace

```csharp
namespace HomeBlaze.Game.Tests;

public class GameDoorTests
```

with

```csharp
namespace HomeBlaze.Game.Tests.Objects.Buildings;

public class DoorTests
```

and replace

```csharp
        Assert.Equal("Someone is in the way of the door.", Assert.IsType<InvalidOperationException>(exception).Message);
```

with

```csharp
        Assert.Equal("Something is in the way.", Assert.IsType<InvalidOperationException>(exception).Message);
```

In `src/HomeBlaze/HomeBlaze.Game.Tests/HomeBlaze.Game.Tests.csproj` replace

```xml
        <Using Include="HomeBlaze.Game.Visuals" />
```

with

```xml
        <Using Include="HomeBlaze.Game.Visuals" />
        <Using Include="HomeBlaze.Game.Objects.Buildings" />
```

In `src/HomeBlaze/HomeBlaze.Game.Tests/GameSerializationTests.cs` replace every `HomeBlaze.Game.Wall` with `HomeBlaze.Game.Objects.Buildings.Wall` and every `HomeBlaze.Game.Door` with `HomeBlaze.Game.Objects.Buildings.Door` (three string literals: the `$type` in the JSON of `WhenGameWithWallIsDeserialized_ThenTheWallKeepsItsFootprint` and the two `Assert.Contains`).

- [ ] **Step 3: Run the tests to see them fail**

Run: `dotnet build src/HomeBlaze/HomeBlaze.Game.Tests -c Debug`
Expected: FAIL to compile (`RemoveAsync`, `UpdateAsync`, `ReplaceAsync` and the namespace `HomeBlaze.Game.Objects.Buildings` do not exist).

- [ ] **Step 4: Add the snapshot and the public object API**

Create `src/HomeBlaze/HomeBlaze.Game/GameObjectSnapshot.cs`:

```csharp
using HomeBlaze.Abstractions;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Game;

/// <summary>
/// The values of the writable configuration and state properties of an object, which <see cref="Restore"/> writes back.
/// An object that the registry does not know captures nothing.
/// </summary>
internal sealed class GameObjectSnapshot
{
    private readonly List<(RegisteredSubjectProperty Property, object? Value)> _values = [];

    private GameObjectSnapshot()
    {
    }

    public static GameObjectSnapshot Capture(GameObject gameObject)
    {
        var snapshot = new GameObjectSnapshot();
        if (gameObject.TryGetRegisteredSubject() is { } registered)
        {
            foreach (var property in registered.Properties)
            {
                if (IsRestorable(property))
                {
                    snapshot._values.Add((property, property.GetValue()));
                }
            }
        }

        return snapshot;
    }

    public void Restore()
    {
        foreach (var (property, value) in _values)
        {
            property.SetValue(value);
        }
    }

    private static bool IsRestorable(RegisteredSubjectProperty property) =>
        !property.IsAttribute && property.HasSetter && IsConfigurationOrState(property);

    private static bool IsConfigurationOrState(RegisteredSubjectProperty property) =>
        property.TryGetAttribute(KnownAttributes.Configuration) is not null || property.TryGetAttribute(KnownAttributes.State) is not null;
}
```

Create `src/HomeBlaze/HomeBlaze.Game/Game.Objects.cs`:

```csharp
using HomeBlaze.Game.World;

namespace HomeBlaze.Game;

public partial class Game
{
    /// <summary>
    /// The message with which a change is refused because a movement blocking object stands in the way.
    /// </summary>
    internal const string BlockedMessage = "Something is in the way.";

    private const string NotPartOfGameMessage = "The object is not part of this game.";

    /// <summary>
    /// Removes the object from <see cref="Objects"/> and saves the game.
    /// </summary>
    /// <exception cref="InvalidOperationException">The object is not part of this game.</exception>
    public async Task RemoveAsync(GameObject gameObject, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(gameObject);
        lock (_lock)
        {
            var objects = CurrentObjects;
            var name = FindObjectName(objects, gameObject) ?? throw new InvalidOperationException(NotPartOfGameMessage);
            var next = new Dictionary<string, GameObject>(objects);
            next.Remove(name);
            ReplaceObjects(next);
            GetIndex().Remove(gameObject);
        }

        await WriteOrRequestSaveAsync(cancellationToken);
    }

    /// <summary>
    /// Runs <paramref name="change"/> under the game's lock, updates the index and saves the game. When the change throws,
    /// or the object blocks movement afterwards and overlaps another movement blocking object, its configuration and state
    /// properties get back the values they had before.
    /// </summary>
    /// <remarks>
    /// The change runs under the game's lock: it may only set properties of the object and must not call into the game.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The object is not part of this game, or the change makes it overlap
    /// another movement blocking object.</exception>
    public async Task UpdateAsync(GameObject gameObject, Action change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(gameObject);
        ArgumentNullException.ThrowIfNull(change);
        lock (_lock)
        {
            if (FindObjectName(CurrentObjects, gameObject) is null)
            {
                throw new InvalidOperationException(NotPartOfGameMessage);
            }

            ApplyChange(gameObject, change);
        }

        await WriteOrRequestSaveAsync(cancellationToken);
    }

    /// <summary>
    /// Replaces the object by the replacements in one step under the game's lock, each under a new name of its prefix, a
    /// dash and 8 random hex digits, and saves the game; no other page sees an intermediate state.
    /// </summary>
    /// <exception cref="ArgumentException">A replacement is already part of this game, or its size is outside 1 to
    /// <see cref="GameObject.MaximumSize"/>.</exception>
    /// <exception cref="InvalidOperationException">The object is not part of this game, or a movement blocking replacement
    /// overlaps a movement blocking object other than the replaced one; nothing changes then.</exception>
    public async Task ReplaceAsync(
        GameObject gameObject,
        IReadOnlyList<(string NamePrefix, GameObject Replacement)> replacements,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(gameObject);
        ArgumentNullException.ThrowIfNull(replacements);
        lock (_lock)
        {
            var objects = CurrentObjects;
            var name = FindObjectName(objects, gameObject) ?? throw new InvalidOperationException(NotPartOfGameMessage);
            var index = GetIndex();
            ValidateReplacements(objects, index, gameObject, replacements);
            var next = new Dictionary<string, GameObject>(objects);
            next.Remove(name);
            foreach (var (namePrefix, replacement) in replacements)
            {
                next[CreateObjectName(next, namePrefix)] = replacement;
            }

            ReplaceObjects(next);
            index.Remove(gameObject);
            foreach (var (_, replacement) in replacements)
            {
                index.AddOrUpdate(replacement);
            }
        }

        await WriteOrRequestSaveAsync(cancellationToken);
    }

    // Callers hold _lock.
    private void ApplyChange(GameObject gameObject, Action change)
    {
        var snapshot = GameObjectSnapshot.Capture(gameObject);
        var isApplied = false;
        try
        {
            change();
            isApplied = true;
        }
        finally
        {
            if (!isApplied)
            {
                snapshot.Restore();
            }
        }

        var index = GetIndex();
        index.AddOrUpdate(gameObject);
        if (gameObject.IsMovementBlocking && index.IsBlocked(gameObject.Footprint, gameObject))
        {
            snapshot.Restore();
            index.AddOrUpdate(gameObject);
            throw new InvalidOperationException(BlockedMessage);
        }
    }

    // Callers hold _lock.
    private static void ValidateReplacements(
        Dictionary<string, GameObject> objects,
        GameWorldIndex index,
        GameObject replaced,
        IReadOnlyList<(string NamePrefix, GameObject Replacement)> replacements)
    {
        foreach (var (namePrefix, replacement) in replacements)
        {
            ArgumentException.ThrowIfNullOrEmpty(namePrefix, nameof(replacements));
            ValidateNewObject(objects, replacement);
            if (replacement.IsMovementBlocking && index.IsBlocked(replacement.Footprint, replaced))
            {
                throw new InvalidOperationException(BlockedMessage);
            }
        }
    }

    private static void ValidateNewObject(Dictionary<string, GameObject> objects, GameObject gameObject)
    {
        ArgumentNullException.ThrowIfNull(gameObject);
        if (FindObjectName(objects, gameObject) is not null)
        {
            throw new ArgumentException("The object is already part of this game.", nameof(gameObject));
        }

        if (!GameWorldIndex.HasValidSize(gameObject))
        {
            throw new ArgumentException($"The width, depth and height of an object are from 1 to {GameObject.MaximumSize} tiles.", nameof(gameObject));
        }
    }

    private static string? FindObjectName(Dictionary<string, GameObject> objects, GameObject gameObject)
    {
        foreach (var (name, candidate) in objects)
        {
            if (ReferenceEquals(candidate, gameObject))
            {
                return name;
            }
        }

        return null;
    }

    private static string CreateObjectName(Dictionary<string, GameObject> objects, string prefix)
    {
        string name;
        do
        {
            name = $"{prefix}-{Guid.NewGuid():N}"[..(prefix.Length + 9)];
        }
        while (objects.ContainsKey(name));

        return name;
    }
}
```

In `src/HomeBlaze/HomeBlaze.Game/Game.Actions.cs`:

Delete the methods `RemoveObjectAsync` (with its XML doc), `SetDoorOpenAsync` (with its XML doc), `FindObjectName` and `CreateObjectName` (they now live in `Game.Objects.cs`). Replace the usings

```csharp
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Messaging;
using HomeBlaze.Game.World;
```

with

```csharp
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Messaging;
using HomeBlaze.Game.Objects.Buildings;
using HomeBlaze.Game.World;
```

(`BuildWallAsync` still creates a `Wall` until Task 4 moves it to `WallTool`.)

In `src/HomeBlaze/HomeBlaze.Game/World/WallPlacement.cs` add `using HomeBlaze.Game.Objects.Buildings;` as the first line (Task 4 replaces this file).

In `src/HomeBlaze/HomeBlaze.Game/GameObject.cs` replace

```csharp
    public virtual Task ApplyConfigurationAsync(CancellationToken cancellationToken)
```

with

```csharp
    /// <summary>
    /// Gets the game this object is part of, for example to call <see cref="Game.RemoveAsync"/> from an operation.
    /// </summary>
    /// <exception cref="InvalidOperationException">The object is not part of a game.</exception>
    protected Game GetGame() => ParentGame ?? throw new InvalidOperationException("The object is not part of a game.");

    public virtual Task ApplyConfigurationAsync(CancellationToken cancellationToken)
```

In `src/HomeBlaze/HomeBlaze.Game/Game.Runtime.cs` replace

```csharp
    private bool IsOutsideChange(SubjectPropertyChange change)
    {
        var name = change.Property.Name;
        return change.Property.Subject switch
        {
            GameObject gameObject => (IsFootprintProperty(name) || IsStateProperty(name)) && ReferenceEquals(gameObject.ParentGame, this),
            var subject => ReferenceEquals(subject, this) && IsCollectionProperty(name)
        };
    }

    private static bool IsFootprintProperty(string name) =>
        name is nameof(GameObject.X) or nameof(GameObject.Y) or nameof(GameObject.Width) or nameof(GameObject.Depth);

    // The properties that IsMovementBlocking, IsSoundBlocking or IsVisible follow.
    private static bool IsStateProperty(string name) =>
        name is nameof(GamePlayer.IsConnected) or nameof(Door.IsOpen);
```

with

```csharp
    // The game knows no object type, so any outside change of one of its objects may move it or change its blocking or
    // visibility, which the index holds.
    private bool IsOutsideChange(SubjectPropertyChange change) => change.Property.Subject switch
    {
        GameObject gameObject => ReferenceEquals(gameObject.ParentGame, this),
        var subject => ReferenceEquals(subject, this) && IsCollectionProperty(change.Property.Name)
    };
```

- [ ] **Step 5: Move walls and doors**

```bash
mkdir -p src/HomeBlaze/HomeBlaze.Game/Objects/Buildings
git mv src/HomeBlaze/HomeBlaze.Game/Wall.cs src/HomeBlaze/HomeBlaze.Game/Objects/Buildings/Wall.cs
git mv src/HomeBlaze/HomeBlaze.Game/Door.cs src/HomeBlaze/HomeBlaze.Game/Objects/Buildings/Door.cs
git mv src/HomeBlaze/HomeBlaze.Game/WallTextures.cs src/HomeBlaze/HomeBlaze.Game/Objects/Buildings/WallTextures.cs
```

In each of the three moved files replace `namespace HomeBlaze.Game;` with `namespace HomeBlaze.Game.Objects.Buildings;`.

In `src/HomeBlaze/HomeBlaze.Game/Objects/Buildings/Wall.cs` replace

```csharp
    [Operation(Title = "Demolish", Icon = "Delete", Position = 1, Description = "Removes this wall from the game.")]
    public Task DemolishAsync(CancellationToken cancellationToken) =>
        (ParentGame ?? throw new InvalidOperationException("The wall is not part of a game.")).RemoveObjectAsync(this, cancellationToken);
```

with

```csharp
    /// <exception cref="InvalidOperationException">The wall is not part of a game.</exception>
    [Operation(Title = "Demolish", Icon = "Delete", Position = 1, Description = "Removes this wall from the game.")]
    public Task DemolishAsync(CancellationToken cancellationToken) => GetGame().RemoveAsync(this, cancellationToken);
```

In `src/HomeBlaze/HomeBlaze.Game/Objects/Buildings/Door.cs` replace

```csharp
    /// <exception cref="InvalidOperationException">The door is already open or not part of a game.</exception>
    [Operation(Title = "Open", Icon = "MeetingRoom", Position = 1, Description = "Opens this door, so players can walk through it.")]
    public Task OpenAsync(CancellationToken cancellationToken) =>
        GetGame().SetDoorOpenAsync(this, isOpen: true, cancellationToken);

    /// <exception cref="InvalidOperationException">The door is already closed, someone stands in it, or it is not part of a game.</exception>
    [Operation(Title = "Close", Icon = "DoorFront", Position = 2, Description = "Closes this door, unless someone stands in it.")]
    public Task CloseAsync(CancellationToken cancellationToken) =>
        GetGame().SetDoorOpenAsync(this, isOpen: false, cancellationToken);

    private Game GetGame() => ParentGame ?? throw new InvalidOperationException("The door is not part of a game.");
```

with

```csharp
    /// <exception cref="InvalidOperationException">The door is already open or not part of a game.</exception>
    [Operation(Title = "Open", Icon = "MeetingRoom", Position = 1, Description = "Opens this door, so players can walk through it.")]
    public Task OpenAsync(CancellationToken cancellationToken) =>
        GetGame().UpdateAsync(this, () =>
        {
            if (IsOpen)
            {
                throw new InvalidOperationException("The door is already open.");
            }

            IsOpen = true;
        }, cancellationToken);

    /// <exception cref="InvalidOperationException">The door is already closed, something stands in it, or it is not part of a game.</exception>
    [Operation(Title = "Close", Icon = "DoorFront", Position = 2, Description = "Closes this door, unless something stands in it.")]
    public Task CloseAsync(CancellationToken cancellationToken) =>
        GetGame().UpdateAsync(this, () =>
        {
            if (!IsOpen)
            {
                throw new InvalidOperationException("The door is already closed.");
            }

            IsOpen = false;
        }, cancellationToken);
```

The moved files reference `GameObject`, `GameObject.MaximumSize` and `Game` of the enclosing namespace `HomeBlaze.Game` and need no further using. Check that `Wall.cs`, `Door.cs` and `WallTextures.cs` use no `internal` member of the core (only `GetGame`, `RemoveAsync`, `UpdateAsync`, `GameAssets`, `GameVisual*`).

- [ ] **Step 6: Update the committed demo world**

```bash
sed -i 's/"HomeBlaze\.Game\.Wall"/"HomeBlaze.Game.Objects.Buildings.Wall"/g; s/"HomeBlaze\.Game\.Door"/"HomeBlaze.Game.Objects.Buildings.Door"/g' src/HomeBlaze/HomeBlaze/Data/Files/Demo/Game.json
grep -c "HomeBlaze.Game.Objects.Buildings.Wall" src/HomeBlaze/HomeBlaze/Data/Files/Demo/Game.json
```

Expected: `14`.

- [ ] **Step 7: Run all tests**

Run: `dotnet build src/HomeBlaze/HomeBlaze.Game.Tests -c Debug`
Expected: Build succeeded, 0 warnings, 0 errors.

Run: `dotnet test src/HomeBlaze/HomeBlaze.Game.Tests -c Debug --filter "FullyQualifiedName!~E2E"`
Expected: all pass, including `WhenADoorIsOpenedFromOutside_ThenMovementSeesItOpen` (generic outside-change rule) and `WhenAnObjectIsMovedWhileTheGameHoldsItsLock_ThenTheMoveDoesNotWaitForTheGame`.

Run: `dotnet test src/HomeBlaze/HomeBlaze.Game.Tests -c Debug --filter "FullyQualifiedName~E2E"`
Expected: all pass.

- [ ] **Step 8: Commit and push**

```bash
git add src/HomeBlaze/HomeBlaze.Game/Objects/Buildings src/HomeBlaze/HomeBlaze.Game/Game.Objects.cs src/HomeBlaze/HomeBlaze.Game/GameObjectSnapshot.cs src/HomeBlaze/HomeBlaze.Game/Game.Actions.cs src/HomeBlaze/HomeBlaze.Game/GameObject.cs src/HomeBlaze/HomeBlaze.Game/Game.Runtime.cs src/HomeBlaze/HomeBlaze.Game/World/WallPlacement.cs src/HomeBlaze/HomeBlaze/Data/Files/Demo/Game.json src/HomeBlaze/HomeBlaze.Game.Tests/GameObjectsTests.cs src/HomeBlaze/HomeBlaze.Game.Tests/Objects/Buildings/DoorTests.cs src/HomeBlaze/HomeBlaze.Game.Tests/HomeBlaze.Game.Tests.csproj src/HomeBlaze/HomeBlaze.Game.Tests/GameSerializationTests.cs
git status --short
git commit -m "refactor: move walls and doors onto a public game object API" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push origin feat/homeblaze-game
```

`git status --short` before the commit must show no staged path under `src/HomeBlaze/HomeBlaze/Data` other than `Demo/Game.json`, and no deleted `Wall.cs`, `Door.cs`, `WallTextures.cs` or `GameDoorTests.cs` left unstaged.

---

## Task 4: Tools, placement API, `WallTool` and wall materials

**Runnable:** yes. Controller restarts the app; the demo page lists "Build wall" under Actions (from the wall tool of the demo world), above "Looking at"; building a concrete or wooden wall shows its texture.

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Game/Tools/GameTool.cs`, `Tools/GamePlacement.cs`, `Game.Placement.cs`
- Rename and rewrite: `src/HomeBlaze/HomeBlaze.Game/World/WallPlacement.cs` to `World/FrontPlacement.cs`
- Create: `src/HomeBlaze/HomeBlaze.Game/Objects/Buildings/WallMaterial.cs`, `Objects/Buildings/WallTool.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Game/Game.cs`, `Game.Actions.cs`, `Objects/Buildings/Wall.cs`, `Objects/Buildings/Door.cs`, `Components/GamePage.razor`
- Rewrite: `src/HomeBlaze/HomeBlaze.Game/Objects/Buildings/WallTextures.cs`
- Assets: create `wwwroot/objects/buildings/textures/concrete.jpg`, `wood.jpg`; modify `wwwroot/objects/buildings/textures/LICENSE.md`
- Modify: `src/HomeBlaze/HomeBlaze/Data/Files/Demo/Game.json` (add the wall tool)
- Rename (tests): `src/HomeBlaze/HomeBlaze.Game.Tests/World/WallPlacementTests.cs` to `World/FrontPlacementTests.cs`
- Create (tests): `src/HomeBlaze/HomeBlaze.Game.Tests/GamePlacementTests.cs`, `Objects/Buildings/WallToolTests.cs`
- Modify (tests): `HomeBlaze.Game.Tests.csproj`, `Testing/GameTestContext.cs`, `GameActionsTests.cs`, `GameSerializationTests.cs`, `Objects/Buildings/WallTests.cs`, `E2E/GamePageTests.cs`

- [ ] **Step 1: Write the failing placement tests**

```bash
git mv src/HomeBlaze/HomeBlaze.Game.Tests/World/WallPlacementTests.cs src/HomeBlaze/HomeBlaze.Game.Tests/World/FrontPlacementTests.cs
```

Replace the whole content of `src/HomeBlaze/HomeBlaze.Game.Tests/World/FrontPlacementTests.cs` with:

```csharp
using HomeBlaze.Game.World;

namespace HomeBlaze.Game.Tests.World;

public class FrontPlacementTests
{
    [Theory]
    [InlineData(GameDirection.North, -1, 2, 4, 1)]
    [InlineData(GameDirection.South, -1, -1, 4, 1)]
    [InlineData(GameDirection.East, 2, -1, 1, 4)]
    [InlineData(GameDirection.West, -1, -1, 1, 4)]
    public void WhenFacingStraight_ThenTheFootprintStandsAcrossTheFacingInFront(
        GameDirection facing, int x, int y, int width, int depth)
    {
        // Act
        var isPlaced = FrontPlacement.TryGetFootprint(new Footprint(0, 0, 2, 2), facing, across: 4, along: 1, out var footprint);

        // Assert
        Assert.True(isPlaced);
        Assert.Equal(new Footprint(x, y, width, depth), footprint);
    }

    [Fact]
    public void WhenTheFootprintIsAsWideAsThePlayer_ThenItIsCenteredInFront()
    {
        // Act
        var isPlaced = FrontPlacement.TryGetFootprint(new Footprint(0, 0, 2, 2), GameDirection.East, across: 2, along: 3, out var footprint);

        // Assert
        Assert.True(isPlaced);
        Assert.Equal(new Footprint(2, 0, 3, 2), footprint);
    }

    [Fact]
    public void WhenFacingDiagonally_ThenNothingIsPlaced()
    {
        // Act
        var isPlaced = FrontPlacement.TryGetFootprint(new Footprint(0, 0, 2, 2), GameDirection.NorthEast, across: 4, along: 1, out _);

        // Assert
        Assert.False(isPlaced);
    }

    [Fact]
    public void WhenTheFootprintWouldLeaveTheIntRange_ThenNothingIsPlaced()
    {
        // Act
        var isPlaced = FrontPlacement.TryGetFootprint(new Footprint(0, int.MaxValue - 1, 2, 2), GameDirection.North, across: 1, along: 1, out _);

        // Assert
        Assert.False(isPlaced);
    }
}
```

Create `src/HomeBlaze/HomeBlaze.Game.Tests/GamePlacementTests.cs`:

```csharp
using HomeBlaze.Game.Tests.Testing;

namespace HomeBlaze.Game.Tests;

public class GamePlacementTests
{
    private readonly GameTestContext _context = new();

    [Theory]
    [InlineData(GameDirection.North, -3, 2, 8, 1)]
    [InlineData(GameDirection.East, 2, -3, 1, 8)]
    [InlineData(GameDirection.South, -3, -1, 8, 1)]
    [InlineData(GameDirection.West, -1, -3, 1, 8)]
    public void WhenThePlayerFacesStraight_ThenThePlacementStandsAcrossTheFacingInFront(
        GameDirection facing, int x, int y, int width, int depth)
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        _context.Game.TryMove(player, facing, turnOnly: true);

        // Act
        var placement = _context.Game.GetPlacementInFront(new GameCaller(player), across: 8, along: 1);

        // Assert
        Assert.Equal(new GamePlacement(x, y, width, depth, facing), placement);
    }

    [Fact]
    public void WhenThePlayerFacesDiagonally_ThenThePlacementIsRefused()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        _context.Game.TryMove(player, GameDirection.NorthEast, turnOnly: true);

        // Act
        var exception = Record.Exception(() => _context.Game.GetPlacementInFront(new GameCaller(player), across: 4, along: 1));

        // Assert
        Assert.Equal("Face north, east, south or west to place objects.", Assert.IsType<InvalidOperationException>(exception).Message);
    }

    [Fact]
    public void WhenThereIsNoActor_ThenThePlacementIsRefused()
    {
        // Act
        var exception = Record.Exception(() => _context.Game.GetPlacementInFront(null, across: 4, along: 1));

        // Assert
        Assert.Equal("Only a player in the game can place objects.", Assert.IsType<InvalidOperationException>(exception).Message);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(GameObject.MaximumSize + 1, 1)]
    public void WhenTheSizeIsOutOfRange_ThenThePlacementIsRefused(int across, int along)
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");

        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => _context.Game.GetPlacementInFront(new GameCaller(player), across, along));
    }

    [Fact]
    public async Task WhenAnObjectIsPlaced_ThenItIsAddedUnderANewNameAndWritten()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        var wall = new Wall { X = 0, Y = 2, Width = 1, Depth = 4 };

        // Act
        await _context.Game.PlaceAsync(new GameCaller(player), "wall", wall, CancellationToken.None);

        // Assert
        var (name, placed) = Assert.Single(_context.Game.Objects);
        Assert.Same(wall, placed);
        Assert.StartsWith("wall-", name);
        Assert.Equal("wall-".Length + 8, name.Length);
        Assert.Equal(1, _context.Root.WriteCount);
        Assert.Same(wall, Assert.Single(_context.Game.GetTargetObjects(player)));
    }

    [Fact]
    public async Task WhenSomethingIsInTheWay_ThenPlacingIsRefused()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        _context.AddWall(0, 4, width: 1);

        // Act
        var exception = await Record.ExceptionAsync(() =>
            _context.Game.PlaceAsync(new GameCaller(player), "wall", new Wall { X = 0, Y = 2, Width = 1, Depth = 4 }, CancellationToken.None));

        // Assert
        Assert.Equal("Something is in the way.", Assert.IsType<InvalidOperationException>(exception).Message);
        Assert.Single(_context.Game.Objects);
        Assert.Equal(0, _context.Root.WriteCount);
    }

    [Fact]
    public async Task WhenTheObjectDoesNotBlockMovement_ThenItMayOverlapOthers()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");

        // Act
        await _context.Game.PlaceAsync(new GameCaller(player), "door", new Door { X = 0, Y = 0, IsOpen = true }, CancellationToken.None);

        // Assert
        Assert.Single(_context.Game.Objects);
    }

    [Fact]
    public async Task WhenThereIsNoActor_ThenPlacingIsRefused()
    {
        // Act
        var exception = await Record.ExceptionAsync(() => _context.Game.PlaceAsync(null, "wall", new Wall { X = 50 }, CancellationToken.None));

        // Assert
        Assert.Equal("Only a player in the game can place objects.", Assert.IsType<InvalidOperationException>(exception).Message);
        Assert.Empty(_context.Game.Objects);
    }

    [Fact]
    public async Task WhenTheObjectIsAlreadyPartOfTheGame_ThenPlacingIsRefused()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        var wall = _context.AddWall(50, 50);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => _context.Game.PlaceAsync(new GameCaller(player), "wall", wall, CancellationToken.None));
    }
}
```

- [ ] **Step 2: Write the failing wall tool and material tests**

In `src/HomeBlaze/HomeBlaze.Game.Tests/HomeBlaze.Game.Tests.csproj` replace

```xml
        <Using Include="HomeBlaze.Game.Objects.Buildings" />
```

with

```xml
        <Using Include="HomeBlaze.Game.Objects.Buildings" />
        <Using Include="HomeBlaze.Game.Tools" />
```

In `src/HomeBlaze/HomeBlaze.Game.Tests/Testing/GameTestContext.cs` add after the `AddObject` method:

```csharp

    /// <summary>
    /// Adds the tool to the game under the name.
    /// </summary>
    public T AddTool<T>(string name, T tool) where T : GameTool
    {
        Game.Tools = new Dictionary<string, GameTool>(Game.Tools) { [name] = tool };
        return tool;
    }
```

Create `src/HomeBlaze/HomeBlaze.Game.Tests/Objects/Buildings/WallToolTests.cs`:

```csharp
using HomeBlaze.Game.Tests.Testing;

namespace HomeBlaze.Game.Tests.Objects.Buildings;

public class WallToolTests
{
    private readonly GameTestContext _context = new();
    private readonly WallTool _tool;

    public WallToolTests()
    {
        _tool = _context.AddTool("wall-tool", new WallTool());
    }

    [Fact]
    public async Task WhenAPlayerBuildsAWallWithoutParameters_ThenADefaultBrickWallStandsAcrossInFront()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");

        // Act
        await _tool.BuildWallAsync(null, null, null, new GameCaller(player), CancellationToken.None);

        // Assert
        var wall = Assert.IsType<Wall>(Assert.Single(_context.Game.Objects).Value);
        Assert.Equal((-1, 2, 4, 1, 10, WallMaterial.Bricks), (wall.X, wall.Y, wall.Width, wall.Depth, wall.Height, wall.Material));
        Assert.Equal(1, _context.Root.WriteCount);
        Assert.Same(wall, Assert.Single(_context.Game.GetTargetObjects(player)));
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
        _context.Game.TryMove(player, facing, turnOnly: true);

        // Act
        await _tool.BuildWallAsync(8, null, null, new GameCaller(player), CancellationToken.None);

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
        await _tool.BuildWallAsync(8, 12, WallMaterial.Concrete, new GameCaller(player), CancellationToken.None);

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
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _tool.BuildWallAsync(length, height, null, new GameCaller(player), CancellationToken.None));
        Assert.Empty(_context.Game.Objects);
    }

    [Fact]
    public async Task WhenTheMaterialIsUndefined_ThenBuildingIsRefused()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _tool.BuildWallAsync(null, null, (WallMaterial)7, new GameCaller(player), CancellationToken.None));
    }

    [Fact]
    public async Task WhenSomethingIsInTheWay_ThenBuildingIsRefused()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        _context.AddWall(1, 2, width: 1);

        // Act
        var exception = await Record.ExceptionAsync(() => _tool.BuildWallAsync(null, null, null, new GameCaller(player), CancellationToken.None));

        // Assert
        Assert.Equal("Something is in the way.", Assert.IsType<InvalidOperationException>(exception).Message);
        Assert.Single(_context.Game.Objects);
    }

    [Fact]
    public async Task WhenThePlayerFacesDiagonally_ThenBuildingIsRefused()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        _context.Game.TryMove(player, GameDirection.NorthEast, turnOnly: true);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => _tool.BuildWallAsync(null, null, null, new GameCaller(player), CancellationToken.None));
    }

    [Fact]
    public async Task WhenThereIsNoActor_ThenBuildingIsRefused()
    {
        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => _tool.BuildWallAsync(null, null, null, null, CancellationToken.None));
    }

    [Fact]
    public async Task WhenObjectsWereReplacedFromOutside_ThenBuildingAWallKeepsThem()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        var existing = new Wall { X = 20, Y = 20 };
        _context.Game.Objects = new Dictionary<string, GameObject> { ["loaded"] = existing };

        // Act
        await _tool.BuildWallAsync(null, null, null, new GameCaller(player), CancellationToken.None);

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
        await Assert.ThrowsAsync<InvalidOperationException>(() => new WallTool().BuildWallAsync(null, null, null, new GameCaller(player), CancellationToken.None));
    }
}
```

In `src/HomeBlaze/HomeBlaze.Game.Tests/Objects/Buildings/WallTests.cs` add before the closing brace of the class:

```csharp

    [Theory]
    [InlineData(WallMaterial.Bricks, "bricks.jpg", 8)]
    [InlineData(WallMaterial.Concrete, "concrete.jpg", 10)]
    [InlineData(WallMaterial.Wood, "wood.jpg", 6)]
    public void WhenTheMaterialIsSet_ThenTheWallShowsItsTexture(WallMaterial material, string file, double scale)
    {
        // Arrange
        var wall = new Wall();

        // Act
        wall.Material = material;

        // Assert
        var part = Assert.Single(wall.Visual.Parts);
        Assert.Equal($"_content/HomeBlaze.Game/objects/buildings/textures/{file}", part.Texture);
        Assert.Equal(scale, part.TextureScale);
    }

    [Fact]
    public void WhenADoorHasAMaterial_ThenItsLintelShowsIt()
    {
        // Arrange
        var door = new Door { Material = WallMaterial.Wood };

        // Act
        var lintel = door.Visual.Parts[0];

        // Assert
        Assert.Equal("_content/HomeBlaze.Game/objects/buildings/textures/wood.jpg", lintel.Texture);
    }
```

In `src/HomeBlaze/HomeBlaze.Game.Tests/GameActionsTests.cs` delete the tests `WhenAPlayerBuildsAWall_ThenItStandsInFrontAndIsWritten`, `WhenObjectsWereReplacedFromOutside_ThenBuildingAWallKeepsThem`, `WhenSomethingIsInTheWay_ThenBuildingIsRefused`, `WhenThePlayerFacesDiagonally_ThenBuildingIsRefused` and `WhenThereIsNoActor_ThenBuildingIsRefused` (they moved to `WallToolTests`).

In `src/HomeBlaze/HomeBlaze.Game.Tests/GameSerializationTests.cs`:

Replace

```csharp
        typeProvider.AddTypes([typeof(Game), typeof(Wall), typeof(Door), typeof(GamePlayer)]);
```

with

```csharp
        typeProvider.AddTypes([typeof(Game), typeof(Wall), typeof(Door), typeof(GamePlayer), typeof(WallTool)]);
```

and add before the last test (`WhenAPlayerHasItsVoiceOn_ThenTheTogglesAreNotSaved`):

```csharp
    [Fact]
    public void WhenAWallWithAMaterialRoundTrips_ThenItKeepsTheMaterial()
    {
        // Arrange
        var game = new Game(Microsoft.Extensions.Logging.Abstractions.NullLogger<Game>.Instance)
        {
            Objects = new Dictionary<string, GameObject> { ["wall-1"] = new Wall { Material = WallMaterial.Concrete } }
        };

        // Act
        var json = _serializer.Serialize(game);
        var loaded = Assert.IsType<Game>(_serializer.Deserialize(json));

        // Assert
        Assert.Contains("\"material\": \"Concrete\"", json);
        Assert.Equal(WallMaterial.Concrete, Assert.IsType<Wall>(loaded.Objects["wall-1"]).Material);
    }

    [Fact]
    public void WhenToolsRoundTrip_ThenTheyKeepTheirType()
    {
        // Arrange
        var game = new Game(Microsoft.Extensions.Logging.Abstractions.NullLogger<Game>.Instance)
        {
            Tools = new Dictionary<string, GameTool> { ["wall-tool"] = new WallTool() }
        };

        // Act
        var json = _serializer.Serialize(game);
        var loaded = Assert.IsType<Game>(_serializer.Deserialize(json));

        // Assert
        Assert.Contains("\"$type\": \"HomeBlaze.Game.Objects.Buildings.WallTool\"", json);
        Assert.IsType<WallTool>(loaded.Tools["wall-tool"]);
    }

    [Fact]
    public void WhenAGameWithoutToolsIsLoaded_ThenItHasNone()
    {
        // Act
        var game = Assert.IsType<Game>(_serializer.Deserialize("""{ "$type": "HomeBlaze.Game.Game", "tools": null }"""));

        // Assert
        Assert.Empty(game.Tools);
    }
```

- [ ] **Step 3: Run the tests to see them fail**

Run: `dotnet build src/HomeBlaze/HomeBlaze.Game.Tests -c Debug`
Expected: FAIL to compile (`FrontPlacement`, `GamePlacement`, `GetPlacementInFront`, `PlaceAsync`, `GameTool`, `WallTool`, `WallMaterial`, `Game.Tools` do not exist).

- [ ] **Step 4: Add the tools and the placement API to the core**

Create `src/HomeBlaze/HomeBlaze.Game/Tools/GameTool.cs`:

```csharp
using HomeBlaze.Abstractions;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Tracking.Parent;

namespace HomeBlaze.Game.Tools;

/// <summary>
/// A tool of a game in <see cref="Game.Tools"/>, whose operations the game page lists for every player, for example to
/// build or plant objects. An operation takes a nullable <c>[FromServices] GameCaller</c> parameter for the acting player.
/// </summary>
[InterceptorSubject]
public abstract partial class GameTool : IConfigurable
{
    public virtual Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Gets the game whose <see cref="Game.Tools"/> holds this tool.
    /// </summary>
    /// <exception cref="InvalidOperationException">The tool is not part of a game.</exception>
    protected Game GetGame() => this.TryGetFirstParent<Game>() ?? throw new InvalidOperationException("The tool is not part of a game.");
}
```

Create `src/HomeBlaze/HomeBlaze.Game/Tools/GamePlacement.cs`:

```csharp
namespace HomeBlaze.Game.Tools;

/// <summary>
/// Where an object fits on the grid: the south-west corner of its footprint, its size in tiles along X (width) and Y
/// (depth), and the direction the placing player faces.
/// </summary>
public sealed record GamePlacement(int X, int Y, int Width, int Depth, GameDirection Facing);
```

```bash
git mv src/HomeBlaze/HomeBlaze.Game/World/WallPlacement.cs src/HomeBlaze/HomeBlaze.Game/World/FrontPlacement.cs
```

Replace the whole content of `src/HomeBlaze/HomeBlaze.Game/World/FrontPlacement.cs` with:

```csharp
namespace HomeBlaze.Game.World;

internal static class FrontPlacement
{
    /// <summary>
    /// Gets the footprint of an object <paramref name="across"/> tiles wide across the facing direction and
    /// <paramref name="along"/> tiles deep along it, starting on the tile right in front of the actor and centered on the
    /// actor's center across the facing (an odd remainder goes east or north); returns false for a diagonal facing or a
    /// footprint outside the <see cref="int"/> range.
    /// </summary>
    public static bool TryGetFootprint(Footprint actor, GameDirection facing, int across, int along, out Footprint footprint)
    {
        if (!Enum.IsDefined(facing) || facing.IsDiagonal())
        {
            footprint = default;
            return false;
        }

        var isNorthOrSouth = facing is GameDirection.North or GameDirection.South;
        var sizeX = isNorthOrSouth ? across : along;
        var sizeY = isNorthOrSouth ? along : across;
        return TryCreate(GetX(actor, facing, along, sizeX), GetY(actor, facing, along, sizeY), sizeX, sizeY, out footprint);
    }

    private static long GetX(Footprint actor, GameDirection facing, int along, int sizeX) => facing switch
    {
        GameDirection.East => actor.Right + 1,
        GameDirection.West => (long)actor.X - along,
        _ => (long)actor.X + (actor.Width - sizeX) / 2
    };

    private static long GetY(Footprint actor, GameDirection facing, int along, int sizeY) => facing switch
    {
        GameDirection.North => actor.Top + 1,
        GameDirection.South => (long)actor.Y - along,
        _ => (long)actor.Y + (actor.Depth - sizeY) / 2
    };

    private static bool TryCreate(long x, long y, int sizeX, int sizeY, out Footprint footprint)
    {
        if (x < int.MinValue || y < int.MinValue || x + sizeX - 1 > int.MaxValue || y + sizeY - 1 > int.MaxValue)
        {
            footprint = default;
            return false;
        }

        footprint = new Footprint((int)x, (int)y, sizeX, sizeY);
        return true;
    }
}
```

Create `src/HomeBlaze/HomeBlaze.Game/Game.Placement.cs`:

```csharp
using HomeBlaze.Game.Tools;
using HomeBlaze.Game.World;

namespace HomeBlaze.Game;

public partial class Game
{
    /// <summary>
    /// The message with which placing is refused for a caller that is not a player of this game.
    /// </summary>
    internal const string NoActorMessage = "Only a player in the game can place objects.";

    /// <summary>
    /// The message with which placing in front of a player that faces diagonally is refused.
    /// </summary>
    internal const string DiagonalMessage = "Face north, east, south or west to place objects.";

    /// <summary>
    /// Gets where an object <paramref name="across"/> tiles wide across the facing direction and <paramref name="along"/>
    /// tiles deep along it fits right in front of the caller's player, centered on the player's center across the facing.
    /// Does not check whether the tiles are free; <see cref="PlaceAsync"/> does.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A size is outside 1 to <see cref="GameObject.MaximumSize"/>.</exception>
    /// <exception cref="InvalidOperationException">The caller is not a player of this game, faces diagonally, or the
    /// footprint would leave the <see cref="int"/> range.</exception>
    public GamePlacement GetPlacementInFront(GameCaller? caller, int across, int along)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(across, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(across, GameObject.MaximumSize);
        ArgumentOutOfRangeException.ThrowIfLessThan(along, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(along, GameObject.MaximumSize);
        lock (_lock)
        {
            var actor = ResolveActor(caller) ?? throw new InvalidOperationException(NoActorMessage);
            if (!FrontPlacement.TryGetFootprint(actor.Footprint, actor.Facing, across, along, out var footprint))
            {
                throw new InvalidOperationException(actor.Facing.IsDiagonal() ? DiagonalMessage : BlockedMessage);
            }

            return new GamePlacement(footprint.X, footprint.Y, footprint.Width, footprint.Depth, actor.Facing);
        }
    }

    /// <summary>
    /// Adds the object to <see cref="Objects"/> under a new name of the prefix, a dash and 8 random hex digits, and saves
    /// the game.
    /// </summary>
    /// <exception cref="ArgumentException">The prefix is empty, the object is already part of this game, or its size is
    /// outside 1 to <see cref="GameObject.MaximumSize"/>.</exception>
    /// <exception cref="InvalidOperationException">The caller is not a player of this game, or the object blocks movement
    /// and another movement blocking object covers one of its tiles.</exception>
    public async Task PlaceAsync(GameCaller? caller, string namePrefix, GameObject gameObject, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(namePrefix);
        ArgumentNullException.ThrowIfNull(gameObject);
        lock (_lock)
        {
            if (ResolveActor(caller) is null)
            {
                throw new InvalidOperationException(NoActorMessage);
            }

            var objects = CurrentObjects;
            ValidateNewObject(objects, gameObject);
            var index = GetIndex();
            if (gameObject.IsMovementBlocking && index.IsBlocked(gameObject.Footprint, ignored: null))
            {
                throw new InvalidOperationException(BlockedMessage);
            }

            ReplaceObjects(new Dictionary<string, GameObject>(objects) { [CreateObjectName(objects, namePrefix)] = gameObject });
            index.AddOrUpdate(gameObject);
        }

        await WriteOrRequestSaveAsync(cancellationToken);
    }
}
```

In `src/HomeBlaze/HomeBlaze.Game/Game.cs`:

Replace

```csharp
using HomeBlaze.Game.Clients;
using HomeBlaze.Game.Voice;
```

with

```csharp
using HomeBlaze.Game.Clients;
using HomeBlaze.Game.Tools;
using HomeBlaze.Game.Voice;
```

Replace

```csharp
    [Configuration]
    public partial Dictionary<string, GameObject> Objects { get; set; }
```

with

```csharp
    [Configuration]
    public partial Dictionary<string, GameObject> Objects { get; set; }

    /// <summary>
    /// The tools by name, whose operations the game page offers every player; the game never changes them.
    /// </summary>
    [Configuration]
    public partial Dictionary<string, GameTool> Tools { get; set; }
```

Replace

```csharp
        Objects = new Dictionary<string, GameObject>();
    }
```

with

```csharp
        Objects = new Dictionary<string, GameObject>();
        Tools = new Dictionary<string, GameTool>();
    }

    partial void OnToolsChanging(ref Dictionary<string, GameTool> newValue, ref bool cancel)
    {
        newValue ??= new Dictionary<string, GameTool>();
    }
```

In `src/HomeBlaze/HomeBlaze.Game/Game.Actions.cs` delete the whole `BuildWallAsync` method with its `[Operation]` attribute, and replace the usings

```csharp
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Messaging;
using HomeBlaze.Game.Objects.Buildings;
using HomeBlaze.Game.World;
```

with

```csharp
using HomeBlaze.Abstractions.Messaging;
```

After this step no file of the core outside `Objects/` mentions `Wall`, `Door` or `HomeBlaze.Game.Objects`; check with `grep -rn "Objects.Buildings\|Objects.Nature" src/HomeBlaze/HomeBlaze.Game --include=*.cs --include=*.razor | grep -v "/Objects/"`, which must print nothing.

- [ ] **Step 5: Download and prepare the concrete and wood textures**

```bash
SCRATCH=/tmp/claude-1000/-home-rico-GitHub-Namotion-Interceptor--claude-worktrees-bridge-cse-011ShuL5XgwWZqRmSZFhkZve/3d51e483-5513-56d7-b4a2-5748ff2f1414/scratchpad
mkdir -p "$SCRATCH/downloads/concrete" "$SCRATCH/downloads/wood"
curl -sSfL -o "$SCRATCH/downloads/concrete/Concrete046_1K-JPG.zip" "https://ambientcg.com/get?file=Concrete046_1K-JPG.zip"
curl -sSfL -o "$SCRATCH/downloads/wood/Planks021_1K-JPG.zip" "https://ambientcg.com/get?file=Planks021_1K-JPG.zip"
unzip -o -q "$SCRATCH/downloads/concrete/Concrete046_1K-JPG.zip" Concrete046_1K-JPG_Color.jpg -d "$SCRATCH/downloads/concrete"
unzip -o -q "$SCRATCH/downloads/wood/Planks021_1K-JPG.zip" Planks021_1K-JPG_Color.jpg -d "$SCRATCH/downloads/wood"
python3 -I "$SCRATCH/scripts/wall_textures.py" square "$SCRATCH/downloads/concrete/Concrete046_1K-JPG_Color.jpg" src/HomeBlaze/HomeBlaze.Game/wwwroot/objects/buildings/textures/concrete.jpg
python3 -I "$SCRATCH/scripts/wall_textures.py" square "$SCRATCH/downloads/wood/Planks021_1K-JPG_Color.jpg" src/HomeBlaze/HomeBlaze.Game/wwwroot/objects/buildings/textures/wood.jpg
ls -la src/HomeBlaze/HomeBlaze.Game/wwwroot/objects/buildings/textures/
```

Expected: `concrete.jpg` about 35 KB and `wood.jpg` about 44 KB, both 512 x 512. If `wall_textures.py` is missing (Task 2 ran in another session), recreate it from Task 2 Step 3.

Replace the content of `src/HomeBlaze/HomeBlaze.Game/wwwroot/objects/buildings/textures/LICENSE.md` with:

```markdown
# Building textures

All three are color maps from ambientCG (https://ambientcg.com), released under CC0 1.0 (https://creativecommons.org/publicdomain/zero/1.0/), taken from the 1K JPG downloads and re-encoded to 512 x 512:

- `bricks.jpg`: Bricks097, scaled to 512 x 256 and stacked twice, so one repeat shows 2 m by 2 m of bricks.
- `concrete.jpg`: Concrete046 (https://ambientcg.com/view?id=Concrete046), 2.4 m by 2.4 m of cast concrete.
- `wood.jpg`: Planks021 (https://ambientcg.com/view?id=Planks021), 1.4 m by 1.4 m of planks.
```

- [ ] **Step 6: Add materials and the wall tool**

Create `src/HomeBlaze/HomeBlaze.Game/Objects/Buildings/WallMaterial.cs`:

```csharp
namespace HomeBlaze.Game.Objects.Buildings;

/// <summary>
/// What a wall or a door lintel is made of, which only its look follows.
/// </summary>
public enum WallMaterial
{
    Bricks,
    Concrete,
    Wood
}
```

Replace the whole content of `src/HomeBlaze/HomeBlaze.Game/Objects/Buildings/WallTextures.cs` with:

```csharp
using HomeBlaze.Game.Visuals;

namespace HomeBlaze.Game.Objects.Buildings;

/// <summary>
/// The textured boxes of walls and door lintels.
/// </summary>
internal static class WallTextures
{
    private static readonly string BricksUrl = GameAssets.Url(typeof(WallTextures), "objects/buildings/textures/bricks.jpg");
    private static readonly string ConcreteUrl = GameAssets.Url(typeof(WallTextures), "objects/buildings/textures/concrete.jpg");
    private static readonly string WoodUrl = GameAssets.Url(typeof(WallTextures), "objects/buildings/textures/wood.jpg");

    public static GameVisualPart CreateBox(WallMaterial material, GameVisualVector offset, GameVisualVector size) => new()
    {
        Shape = GameVisualShape.Box,
        Offset = offset,
        Size = size,
        Texture = GetUrl(material),
        TextureScale = GetScale(material)
    };

    // A value outside the enum, possible in a loaded file, looks like bricks.
    private static string GetUrl(WallMaterial material) => material switch
    {
        WallMaterial.Concrete => ConcreteUrl,
        WallMaterial.Wood => WoodUrl,
        _ => BricksUrl
    };

    // Tiles per repeat, so that bricks show 2 m, concrete 2.5 m and planks 1.5 m per repeat.
    private static double GetScale(WallMaterial material) => material switch
    {
        WallMaterial.Concrete => 10,
        WallMaterial.Wood => 6,
        _ => 8
    };
}
```

In `src/HomeBlaze/HomeBlaze.Game/Objects/Buildings/Wall.cs`:

Replace

```csharp
    private readonly GameVisualCache<(int Width, int Height, int Depth)> _visual = new();
```

with

```csharp
    private readonly GameVisualCache<(int Width, int Height, int Depth, WallMaterial Material)> _visual = new();
```

Replace

```csharp
    [Configuration]
    [Range(1, MaximumSize)]
    public override partial int Height { get; set; }
```

with

```csharp
    [Configuration]
    [Range(1, MaximumSize)]
    public override partial int Height { get; set; }

    [Configuration]
    public partial WallMaterial Material { get; set; }
```

Replace

```csharp
    public override GameVisual Visual => _visual.Get((Width, Height, Depth),
        static size => new GameVisual([WallTextures.CreateBricks(default, new GameVisualVector(size.Width, size.Height, size.Depth))]));
```

with

```csharp
    public override GameVisual Visual => _visual.Get((Width, Height, Depth, Material),
        static state => new GameVisual([WallTextures.CreateBox(state.Material, default, new GameVisualVector(state.Width, state.Height, state.Depth))]));
```

Replace

```csharp
        Height = DefaultHeight;
    }
```

with

```csharp
        Height = DefaultHeight;
        Material = WallMaterial.Bricks;
    }
```

In `src/HomeBlaze/HomeBlaze.Game/Objects/Buildings/Door.cs`:

Replace

```csharp
    private readonly GameVisualCache<(int Width, int Depth, int Height, bool IsOpen)> _visual = new();
```

with

```csharp
    private readonly GameVisualCache<(int Width, int Depth, int Height, bool IsOpen, WallMaterial Material)> _visual = new();
```

Replace

```csharp
    [Configuration]
    public partial bool IsOpen { get; set; }
```

with

```csharp
    [Configuration]
    public partial bool IsOpen { get; set; }

    /// <summary>
    /// The material of the lintel above the leaf, the one of the wall around the door.
    /// </summary>
    [Configuration]
    public partial WallMaterial Material { get; set; }
```

Replace

```csharp
    public override GameVisual Visual => _visual.Get((Width, Depth, Height, IsOpen),
        static state => CreateVisual(state.Width, state.Depth, state.Height, state.IsOpen));
```

with

```csharp
    public override GameVisual Visual => _visual.Get((Width, Depth, Height, IsOpen, Material),
        static state => CreateVisual(state.Width, state.Depth, state.Height, state.IsOpen, state.Material));
```

Replace

```csharp
        IsOpen = false;
    }
```

with

```csharp
        IsOpen = false;
        Material = WallMaterial.Bricks;
    }
```

Replace

```csharp
    private static GameVisual CreateVisual(int width, int depth, int height, bool isOpen)
```

with

```csharp
    private static GameVisual CreateVisual(int width, int depth, int height, bool isOpen, WallMaterial material)
```

and inside it replace

```csharp
        var lintel = WallTextures.CreateBricks(new GameVisualVector(0, leafHeight, 0), new GameVisualVector(width, height - leafHeight, depth));
```

with

```csharp
        var lintel = WallTextures.CreateBox(material, new GameVisualVector(0, leafHeight, 0), new GameVisualVector(width, height - leafHeight, depth));
```

Create `src/HomeBlaze/HomeBlaze.Game/Objects/Buildings/WallTool.cs`:

```csharp
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Game.Tools;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Game.Objects.Buildings;

/// <summary>
/// Builds walls in front of the player.
/// </summary>
[Category("Game")]
[Description("Builds walls of a chosen length, height and material in front of the player")]
[DisplayName("Wall tool")]
[SubjectIcon("AddBox")]
[InterceptorSubject]
public partial class WallTool : GameTool, ITitleProvider, IIconProvider
{
    /// <summary>
    /// The longest wall the tool builds in tiles, 10 m.
    /// </summary>
    public const int MaximumLength = 40;

    /// <summary>
    /// The tallest wall the tool builds in tiles, 5 m.
    /// </summary>
    public const int MaximumHeight = 20;

    public string? Title => "Wall tool";

    public string? IconName => "AddBox";

    /// <summary>
    /// Builds a wall one tile thick right in front of the player, across the direction it faces and centered on it.
    /// </summary>
    /// <param name="length">The length in tiles, 1 to <see cref="MaximumLength"/>; null for <see cref="Wall.DefaultLength"/>.</param>
    /// <param name="height">The height in tiles, 1 to <see cref="MaximumHeight"/>; null for <see cref="Wall.DefaultHeight"/>.</param>
    /// <param name="material">The material; null for <see cref="WallMaterial.Bricks"/>.</param>
    /// <param name="caller">The acting player.</param>
    /// <param name="cancellationToken">Cancels saving.</param>
    /// <exception cref="ArgumentOutOfRangeException">A parameter is outside its range.</exception>
    /// <exception cref="InvalidOperationException">There is no player, it faces diagonally, something is in the way, or
    /// the tool is not part of a game.</exception>
    [Operation(Title = "Build wall", Icon = "AddBox", Position = 1,
        Description = "Builds a wall 25 cm thick right in front of you, across the direction you face and centered on you. Empty fields build 1 m long, 2.5 m tall bricks.")]
    public Task BuildWallAsync(
        [Range(1, MaximumLength)] int? length,
        [Range(1, MaximumHeight)] int? height,
        WallMaterial? material,
        [FromServices] GameCaller? caller,
        CancellationToken cancellationToken)
    {
        var wallLength = length ?? Wall.DefaultLength;
        var wallHeight = height ?? Wall.DefaultHeight;
        var wallMaterial = material ?? WallMaterial.Bricks;
        ValidateParameters(wallLength, wallHeight, wallMaterial);

        var game = GetGame();
        var placement = game.GetPlacementInFront(caller, across: wallLength, along: 1);
        var wall = new Wall
        {
            X = placement.X,
            Y = placement.Y,
            Width = placement.Width,
            Depth = placement.Depth,
            Height = wallHeight,
            Material = wallMaterial
        };
        return game.PlaceAsync(caller, "wall", wall, cancellationToken);
    }

    private static void ValidateParameters(int length, int height, WallMaterial material)
    {
        if (length is < 1 or > MaximumLength)
        {
            throw new ArgumentOutOfRangeException(nameof(length), length, $"A wall is 1 to {MaximumLength} tiles long.");
        }

        if (height is < 1 or > MaximumHeight)
        {
            throw new ArgumentOutOfRangeException(nameof(height), height, $"A wall is 1 to {MaximumHeight} tiles tall.");
        }

        if (!Enum.IsDefined(material))
        {
            throw new ArgumentOutOfRangeException(nameof(material), material, "The material is unknown.");
        }
    }
}
```

- [ ] **Step 7: List the tool operations on the page**

In `src/HomeBlaze/HomeBlaze.Game/Components/GamePage.razor` replace this markup block

```razor
        @if (_targets.Count > 0)
        {
            <section data-testid="game-looking-at">
                <h5>Looking at</h5>
                @foreach (var target in _targets)
                {
                    <div class="game-target-title" data-testid="game-target-title">@target.Title</div>
                    @foreach (var action in target.Actions)
                    {
                        <MudButton Size="Size.Small" Variant="Variant.Filled" Color="Color.Primary" Class="game-action"
                                   Disabled="_isInvoking" data-testid="game-target-action"
                                   OnClick="@(() => InvokeActionAsync(target.Subject, action.Method))">
                            @if (action.Key > 0)
                            {
                                <span class="game-key">@action.Key</span>
                            }
                            @action.Method.Title
                        </MudButton>
                    }
                }
            </section>
        }
        <section>
            <h5>Actions</h5>
            @foreach (var method in _gameActions)
            {
                <MudButton Size="Size.Small" Variant="Variant.Outlined" Class="game-action"
                           Disabled="@(_isInvoking || _player is null)" data-testid="game-action"
                           OnClick="@(() => InvokeActionAsync(Game!, method))">@method.Title</MudButton>
            }
        </section>
```

with

```razor
        @* The operations of the game and its tools come first, so they keep their place while targets come and go. *@
        <section>
            <h5>Actions</h5>
            @foreach (var action in _actions)
            {
                <MudButton Size="Size.Small" Variant="Variant.Outlined" Class="game-action"
                           Disabled="@(_isInvoking || _player is null)" data-testid="game-action"
                           OnClick="@(() => InvokeActionAsync(action.Subject, action.Method))">@action.Method.Title</MudButton>
            }
        </section>
        @if (_targets.Count > 0)
        {
            <section data-testid="game-looking-at">
                <h5>Looking at</h5>
                @foreach (var target in _targets)
                {
                    <div class="game-target-title" data-testid="game-target-title">@target.Title</div>
                    @foreach (var action in target.Actions)
                    {
                        <MudButton Size="Size.Small" Variant="Variant.Filled" Color="Color.Primary" Class="game-action"
                                   Disabled="_isInvoking" data-testid="game-target-action"
                                   OnClick="@(() => InvokeActionAsync(target.Subject, action.Method))">
                            @if (action.Key > 0)
                            {
                                <span class="game-key">@action.Key</span>
                            }
                            @action.Method.Title
                        </MudButton>
                    }
                }
            </section>
        }
```

In the `@code` block replace

```csharp
    private IReadOnlyList<MethodMetadata> _gameActions = [];
```

with

```csharp
    private IReadOnlyList<GameAction> _actions = [];
```

replace

```csharp
    private void RefreshActions(Game game)
    {
        _gameActions = GetOperations(game);
```

with

```csharp
    private void RefreshActions(Game game)
    {
        _actions = GetGameActions(game);
```

add right before `// Disabled operations are left out rather than shown disabled, so the number keys only reach operations that apply.`:

```csharp
    // The operations of the game, then those of its tools ordered by name.
    private static List<GameAction> GetGameActions(Game game)
    {
        var actions = new List<GameAction>();
        AddActions(actions, game);
        foreach (var (_, tool) in game.Tools.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            AddActions(actions, tool);
        }

        return actions;
    }

    private static void AddActions(List<GameAction> actions, IInterceptorSubject subject)
    {
        foreach (var method in GetOperations(subject))
        {
            actions.Add(new GameAction(subject, method));
        }
    }

```

and replace

```csharp
    private sealed record ActionEntry(int Key, MethodMetadata Method);
```

with

```csharp
    private sealed record ActionEntry(int Key, MethodMetadata Method);

    private sealed record GameAction(IInterceptorSubject Subject, MethodMetadata Method);
```

- [ ] **Step 8: Add the wall tool to the demo world and update the E2E test**

```bash
sed -i 's|^  "players": {},$|  "players": {},\n  "tools": { "wall-tool": { "$type": "HomeBlaze.Game.Objects.Buildings.WallTool" } },|' src/HomeBlaze/HomeBlaze/Data/Files/Demo/Game.json
python3 -I -c "import json, sys; print(json.load(open(sys.argv[1]))['tools'])" src/HomeBlaze/HomeBlaze/Data/Files/Demo/Game.json
```

Expected: `{'wall-tool': {'$type': 'HomeBlaze.Game.Objects.Buildings.WallTool'}}`.

In `src/HomeBlaze/HomeBlaze.Game.Tests/E2E/GamePageTests.cs` replace the test `WhenAWallIsBuiltAndDemolished_ThenItAppearsAndDisappears` with:

```csharp
    [Fact]
    public async Task WhenAWallIsBuiltAndDemolished_ThenItAppearsAndDisappears()
    {
        // Arrange
        await using var world = await GameWorld.CreateAsync(_fixture);
        world.Game.Tools = new Dictionary<string, GameTool> { ["wall-tool"] = new WallTool() };
        var (page, player) = await world.JoinAsync();

        // Act
        await page.GetByTestId("game-action").Filter(new() { HasText = "Build wall" }).ClickAsync(new() { Timeout = Timeout });
        await page.Locator(".mud-dialog").GetByRole(AriaRole.Button, new() { Name = "Execute" }).ClickAsync(new() { Timeout = Timeout });
        await AsyncTestHelpers.WaitUntilAsync(() => world.Game.Objects.Count == 1, message: "The wall was not built");
        var wall = Assert.IsType<Wall>(Assert.Single(world.Game.Objects.Values));
        await page.GetByTestId("game-target-action").Filter(new() { HasText = "Demolish" }).ClickAsync(new() { Timeout = Timeout });

        // Assert
        // A default wall of 4 tiles stands across the facing, centered on the player.
        Assert.Equal((player.X - 1, player.Y + GamePlayer.Size), (wall.X, wall.Y));
        await AsyncTestHelpers.WaitUntilAsync(() => world.Game.Objects.Count == 0, message: "The wall was not demolished");
        await Expect(page.GetByTestId("game-looking-at")).ToHaveCountAsync(0, new() { Timeout = Timeout });
    }
```

- [ ] **Step 9: Run all tests**

Run: `dotnet build src/HomeBlaze/HomeBlaze.Game.Tests -c Debug`
Expected: Build succeeded, 0 warnings, 0 errors.

Run: `dotnet test src/HomeBlaze/HomeBlaze.Game.Tests -c Debug --filter "FullyQualifiedName!~E2E"`
Expected: all pass.

Run: `dotnet test src/HomeBlaze/HomeBlaze.Game.Tests -c Debug --filter "FullyQualifiedName~E2E"`
Expected: all pass.

- [ ] **Step 10: Commit and push**

```bash
git add src/HomeBlaze/HomeBlaze.Game/Tools src/HomeBlaze/HomeBlaze.Game/Game.Placement.cs src/HomeBlaze/HomeBlaze.Game/World/FrontPlacement.cs src/HomeBlaze/HomeBlaze.Game/Game.cs src/HomeBlaze/HomeBlaze.Game/Game.Actions.cs src/HomeBlaze/HomeBlaze.Game/Objects/Buildings src/HomeBlaze/HomeBlaze.Game/Components/GamePage.razor src/HomeBlaze/HomeBlaze.Game/wwwroot/objects/buildings/textures/concrete.jpg src/HomeBlaze/HomeBlaze.Game/wwwroot/objects/buildings/textures/wood.jpg src/HomeBlaze/HomeBlaze.Game/wwwroot/objects/buildings/textures/LICENSE.md src/HomeBlaze/HomeBlaze/Data/Files/Demo/Game.json src/HomeBlaze/HomeBlaze.Game.Tests/World/FrontPlacementTests.cs src/HomeBlaze/HomeBlaze.Game.Tests/GamePlacementTests.cs src/HomeBlaze/HomeBlaze.Game.Tests/Objects/Buildings/WallToolTests.cs src/HomeBlaze/HomeBlaze.Game.Tests/Objects/Buildings/WallTests.cs src/HomeBlaze/HomeBlaze.Game.Tests/HomeBlaze.Game.Tests.csproj src/HomeBlaze/HomeBlaze.Game.Tests/Testing/GameTestContext.cs src/HomeBlaze/HomeBlaze.Game.Tests/GameActionsTests.cs src/HomeBlaze/HomeBlaze.Game.Tests/GameSerializationTests.cs src/HomeBlaze/HomeBlaze.Game.Tests/E2E/GamePageTests.cs
git commit -m "feat: add game tools, a placement API and walls of a chosen size and material" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push origin feat/homeblaze-game
```

---

## Task 5: Remembered parameters

**Runnable:** yes. Controller restarts the app; on the demo page a second "Build wall" shows the length and material of the first.

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze.Components/Operations/OperationInvoker.cs`, `src/HomeBlaze/HomeBlaze.Components/Dialogs/MethodParameterDialog.razor`
- Create (tests): `src/HomeBlaze/HomeBlaze.Components.Tests/MethodParameterDialogTests.cs`
- Modify (tests): `src/HomeBlaze/HomeBlaze.Components.Tests/OperationInvokerTests.cs`, `src/HomeBlaze/HomeBlaze.Components.Tests/VerifyChecksTests.PublicApi.verified.txt` (accepted snapshot)
- Modify: `src/HomeBlaze/HomeBlaze.Game/Components/GamePage.razor`
- Modify (tests): `src/HomeBlaze/HomeBlaze.Game.Tests/E2E/GamePageTests.cs`

`HomeBlaze.Components.Tests` has no bUnit, so the dialog's prefill logic is a static method the tests call directly, and the invoker tests capture the dialog parameters with Moq. The E2E test covers the rendered dialog.

- [ ] **Step 1: Write the failing Components tests**

Create `src/HomeBlaze/HomeBlaze.Components.Tests/MethodParameterDialogTests.cs`:

```csharp
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Components.Dialogs;
using Xunit;

namespace HomeBlaze.Components.Tests;

public class MethodParameterDialogTests
{
    [Theory]
    [InlineData(8, "8")]
    [InlineData(1.5, "1.5")]
    [InlineData(true, "True")]
    [InlineData("text", "text")]
    [InlineData(DayOfWeek.Friday, "Friday")]
    public void WhenAnInitialValueIsFormatted_ThenTheFieldShowsItAsEntered(object value, string expected)
    {
        // Act
        var text = MethodParameterDialog.FormatInitialValue(value, unit: null);

        // Assert
        Assert.Equal(expected, text);
    }

    [Fact]
    public void WhenAPercentIsFormatted_ThenTheFieldShowsItFrom0To100()
    {
        // Act
        var fromDecimal = MethodParameterDialog.FormatInitialValue(0.5m, StateUnit.Percent);
        var fromDouble = MethodParameterDialog.FormatInitialValue(0.333, StateUnit.Percent);

        // Assert
        Assert.Equal("50", fromDecimal);
        Assert.Equal("33.3", fromDouble);
    }

    [Fact]
    public void WhenTheInitialValueIsNull_ThenTheFieldStaysEmpty()
    {
        // Act
        var text = MethodParameterDialog.FormatInitialValue(null, unit: null);

        // Assert
        Assert.Null(text);
    }
}
```

In `src/HomeBlaze/HomeBlaze.Components.Tests/OperationInvokerTests.cs` add before the closing brace of the class:

```csharp

    [Fact]
    public async Task WhenInitialValuesAreGiven_ThenTheParameterDialogGetsThem()
    {
        // Arrange
        var dialogService = new Mock<IDialogService>();
        DialogParameters? shownParameters = null;
        dialogService
            .Setup(service => service.ShowAsync<MethodParameterDialog>(It.IsAny<string>(), It.IsAny<DialogParameters>(), It.IsAny<DialogOptions>()))
            .Callback<string, DialogParameters, DialogOptions>((_, parameters, _) => shownParameters = parameters)
            .ReturnsAsync(() =>
            {
                var reference = new Mock<IDialogReference>();
                reference.Setup(dialogReference => dialogReference.Result).Returns(Task.FromResult<DialogResult?>(DialogResult.Cancel()));
                return reference.Object;
            });
        var method = new MethodMetadata(_ => null) { Title = "Set Speed", Parameters = [InputParameter("speed", typeof(int))] };
        var initialValues = new Dictionary<string, object?> { ["speed"] = 5 };

        // Act
        await OperationInvoker.InvokeAsync(dialogService.Object, ServiceProvider, method, CancellationToken.None, initialValues: initialValues);

        // Assert
        Assert.NotNull(shownParameters);
        Assert.Same(initialValues, shownParameters.Get<IReadOnlyDictionary<string, object?>>(nameof(MethodParameterDialog.InitialValues)));
    }

    [Fact]
    public async Task WhenParametersAreEntered_ThenTheyAreReportedByName()
    {
        // Arrange
        var dialogService = new Mock<IDialogService>();
        SetupDialog<MethodParameterDialog>(dialogService, () => DialogResult.Ok(new object?[] { 5, "fast" }));
        IReadOnlyDictionary<string, object?>? reported = null;
        var method = new MethodMetadata(_ => null)
        {
            Title = "Set Speed",
            Parameters =
            [
                InputParameter("speed", typeof(int)),
                new MethodParameter { Name = "cancellationToken", Type = typeof(CancellationToken), IsRuntimeProvided = true },
                InputParameter("mode", typeof(string))
            ]
        };

        // Act
        await OperationInvoker.InvokeAsync(dialogService.Object, ServiceProvider, method, CancellationToken.None,
            parametersEntered: values => reported = values);

        // Assert
        Assert.NotNull(reported);
        Assert.Equal(2, reported.Count);
        Assert.Equal(5, reported["speed"]);
        Assert.Equal("fast", reported["mode"]);
    }

    [Fact]
    public async Task WhenTheParameterDialogIsCanceled_ThenNoParametersAreReported()
    {
        // Arrange
        var dialogService = new Mock<IDialogService>();
        SetupDialog<MethodParameterDialog>(dialogService, DialogResult.Cancel);
        var isReported = false;
        var method = new MethodMetadata(_ => null) { Title = "Set Speed", Parameters = [InputParameter("speed", typeof(int))] };

        // Act
        var outcome = await OperationInvoker.InvokeAsync(dialogService.Object, ServiceProvider, method, CancellationToken.None,
            parametersEntered: _ => isReported = true);

        // Assert
        Assert.Equal(OperationOutcome.Canceled, outcome);
        Assert.False(isReported);
    }
```

- [ ] **Step 2: Run the tests to see them fail**

Run: `dotnet build src/HomeBlaze/HomeBlaze.Components.Tests -c Debug`
Expected: FAIL to compile (`FormatInitialValue`, `InitialValues`, `initialValues`, `parametersEntered` do not exist).

- [ ] **Step 3: Implement the initial values**

In `src/HomeBlaze/HomeBlaze.Components/Dialogs/MethodParameterDialog.razor`:

Replace

```razor
@using HomeBlaze.Abstractions.Attributes
@using HomeBlaze.Abstractions.Metadata
@using HomeBlaze.Services.Display
```

with

```razor
@using System.Globalization
@using HomeBlaze.Abstractions.Attributes
@using HomeBlaze.Abstractions.Metadata
@using HomeBlaze.Services.Display
```

Replace

```csharp
    [Parameter] public MethodMetadata? Method { get; set; }
```

with

```csharp
    [Parameter] public MethodMetadata? Method { get; set; }

    /// <summary>
    /// The values that prefill the fields, keyed by parameter name; only parameters entered in one field use them.
    /// </summary>
    [Parameter] public IReadOnlyDictionary<string, object?>? InitialValues { get; set; }
```

Replace

```csharp
        foreach (var input in _inputs.Where(input => input.Kind == InputKind.Simple && input.Parameter.Type == typeof(bool)))
        {
            _inputValues[input.Parameter.Name] = "false";
        }

        ValidateAll();
    }
```

with

```csharp
        foreach (var input in _inputs.Where(input => input.Kind == InputKind.Simple && input.Parameter.Type == typeof(bool)))
        {
            _inputValues[input.Parameter.Name] = "false";
        }

        ApplyInitialValues();
        ValidateAll();
    }

    private void ApplyInitialValues()
    {
        if (InitialValues is null)
        {
            return;
        }

        foreach (var input in _inputs.Where(input => input.Kind == InputKind.Simple))
        {
            if (InitialValues.TryGetValue(input.Parameter.Name, out var value))
            {
                _inputValues[input.Parameter.Name] = FormatInitialValue(value, input.Parameter.Unit);
            }
        }
    }

    /// <summary>
    /// Formats a value as the user would enter it, so that the field converts it back to the same value; a percent shows
    /// from 0 to 100.
    /// </summary>
    internal static string? FormatInitialValue(object? value, StateUnit? unit) => value switch
    {
        null => null,
        // G29 drops the trailing zeros that the decimal multiplication keeps.
        _ when unit == StateUnit.Percent && TryGetFraction(value, out var fraction) => (fraction * 100m).ToString("G29", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString()
    };

    // A fraction beyond 1e15 is no percent value and would overflow the decimal.
    private static bool TryGetFraction(object value, out decimal fraction)
    {
        (var isFraction, fraction) = value switch
        {
            decimal number when Math.Abs(number) < 1e15m => (true, number),
            double number when Math.Abs(number) < 1e15 => (true, (decimal)number),
            float number when Math.Abs(number) < 1e15f => (true, (decimal)number),
            _ => (false, 0m)
        };
        return isFraction;
    }
```

Replace

```csharp
    public static Task<IDialogReference> ShowAsync(
        IDialogService dialogService,
        MethodMetadata method)
    {
        var parameters = new DialogParameters<MethodParameterDialog>
        {
            { x => x.Method, method }
        };
```

with

```csharp
    public static Task<IDialogReference> ShowAsync(
        IDialogService dialogService,
        MethodMetadata method,
        IReadOnlyDictionary<string, object?>? initialValues = null)
    {
        var parameters = new DialogParameters<MethodParameterDialog>
        {
            { x => x.Method, method },
            { x => x.InitialValues, initialValues }
        };
```

In `src/HomeBlaze/HomeBlaze.Components/Operations/OperationInvoker.cs`:

Replace

```csharp
    /// <param name="isRunningChanged">
    /// Called with true right before the method starts and with false once it has returned or thrown, before a result
    /// or error dialog shows. Not called when the method does not start.
    /// </param>
    public static async Task<OperationOutcome> InvokeAsync(
        IDialogService dialogService,
        IServiceProvider serviceProvider,
        MethodMetadata method,
        CancellationToken lifetime,
        Action<bool>? isRunningChanged = null)
    {
```

with

```csharp
    /// <param name="isRunningChanged">
    /// Called with true right before the method starts and with false once it has returned or thrown, before a result
    /// or error dialog shows. Not called when the method does not start.
    /// </param>
    /// <param name="initialValues">
    /// The values that prefill the parameter dialog, keyed by parameter name; only parameters entered in one field use them.
    /// </param>
    /// <param name="parametersEntered">
    /// Called with the entered parameter values keyed by name when the parameter dialog closes with Execute, before the
    /// method starts.
    /// </param>
    public static async Task<OperationOutcome> InvokeAsync(
        IDialogService dialogService,
        IServiceProvider serviceProvider,
        MethodMetadata method,
        CancellationToken lifetime,
        Action<bool>? isRunningChanged = null,
        IReadOnlyDictionary<string, object?>? initialValues = null,
        Action<IReadOnlyDictionary<string, object?>>? parametersEntered = null)
    {
```

Replace

```csharp
                var dialog = await MethodParameterDialog.ShowAsync(dialogService, method);
                var dialogResult = await dialog.Result;
                if (dialogResult is null || dialogResult.Canceled)
                {
                    return OperationOutcome.Canceled;
                }

                parameters = dialogResult.Data as object?[];
                break;
```

with

```csharp
                var dialog = await MethodParameterDialog.ShowAsync(dialogService, method, initialValues);
                var dialogResult = await dialog.Result;
                if (dialogResult is null || dialogResult.Canceled)
                {
                    return OperationOutcome.Canceled;
                }

                parameters = dialogResult.Data as object?[];
                parametersEntered?.Invoke(GetParameterValues(method, parameters));
                break;
```

Add after the `GetPrompt` method:

```csharp

    /// <summary>
    /// Gets the values of the parameters that require input, keyed by name, from the dialog result, which lists them in
    /// their order.
    /// </summary>
    internal static Dictionary<string, object?> GetParameterValues(MethodMetadata method, object?[]? parameters)
    {
        var values = new Dictionary<string, object?>();
        var index = 0;
        foreach (var parameter in method.Parameters)
        {
            if (parameter.RequiresInput)
            {
                values[parameter.Name] = parameters is not null && index < parameters.Length ? parameters[index] : null;
                index++;
            }
        }

        return values;
    }
```

- [ ] **Step 4: Run the Components tests and accept the public API snapshot**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Components.Tests -c Debug`
Expected: only `VerifyChecksTests.PublicApi` fails, because `OperationInvoker.InvokeAsync` and `MethodParameterDialog.ShowAsync` gained parameters and `MethodParameterDialog` gained `InitialValues`.

```bash
diff src/HomeBlaze/HomeBlaze.Components.Tests/VerifyChecksTests.PublicApi.verified.txt src/HomeBlaze/HomeBlaze.Components.Tests/VerifyChecksTests.PublicApi.received.txt
```

Expected: exactly three changes: the new `[Microsoft.AspNetCore.Components.Parameter] public System.Collections.Generic.IReadOnlyDictionary<string, object?>? InitialValues { get; set; }` in `MethodParameterDialog`, the new `ShowAsync` signature with `System.Collections.Generic.IReadOnlyDictionary<string, object?>? initialValues = null`, and the new `InvokeAsync` signature with `initialValues` and `parametersEntered`. If anything else differs, stop and report. Then:

```bash
mv src/HomeBlaze/HomeBlaze.Components.Tests/VerifyChecksTests.PublicApi.received.txt src/HomeBlaze/HomeBlaze.Components.Tests/VerifyChecksTests.PublicApi.verified.txt
```

Run: `dotnet test src/HomeBlaze/HomeBlaze.Components.Tests -c Debug`
Expected: all pass.

- [ ] **Step 5: Remember the parameters on the game page**

In `src/HomeBlaze/HomeBlaze.Game/Components/GamePage.razor` replace

```csharp
    private IReadOnlyList<GameAction> _actions = [];
```

with

```csharp
    private IReadOnlyList<GameAction> _actions = [];
    // The parameters this page entered last per operation, keyed by object type and operation, so that the next dialog
    // of the same operation, also on another object of that type, starts from them.
    private readonly Dictionary<string, IReadOnlyDictionary<string, object?>> _lastParameters = new();
```

and in `InvokeActionAsync` replace

```csharp
            var services = new GameCallerServiceProvider(ServiceProvider, new GameCaller(player));
            await OperationInvoker.InvokeAsync(DialogService, services, method, _lifetime.Token);
```

with

```csharp
            var services = new GameCallerServiceProvider(ServiceProvider, new GameCaller(player));
            var key = $"{owner.GetType().FullName}.{method.PropertyName}";
            await OperationInvoker.InvokeAsync(DialogService, services, method, _lifetime.Token,
                initialValues: _lastParameters.GetValueOrDefault(key),
                parametersEntered: values => _lastParameters[key] = values);
```

- [ ] **Step 6: Write the E2E test for the dialog**

In `src/HomeBlaze/HomeBlaze.Game.Tests/E2E/GamePageTests.cs` add after `WhenAWallIsBuiltAndDemolished_ThenItAppearsAndDisappears`:

```csharp
    [Fact]
    public async Task WhenAWallIsBuiltThroughTheDialog_ThenItHasTheChosenLengthAndMaterialAndTheNextDialogIsPrefilled()
    {
        // Arrange
        await using var world = await GameWorld.CreateAsync(_fixture);
        world.Game.Tools = new Dictionary<string, GameTool> { ["wall-tool"] = new WallTool() };
        var (page, player) = await world.JoinAsync();
        var buildWall = page.GetByTestId("game-action").Filter(new() { HasText = "Build wall" });
        var dialog = page.Locator(".mud-dialog");

        // Act
        await buildWall.ClickAsync(new() { Timeout = Timeout });
        await dialog.GetByLabel("Length", new() { Exact = true }).FillAsync("8", new() { Timeout = Timeout });
        // Material is the only select of the dialog.
        await dialog.Locator(".mud-select").ClickAsync(new() { Timeout = Timeout });
        await page.Locator(".mud-popover-open .mud-list-item").Filter(new() { HasTextString = "Concrete" }).ClickAsync(new() { Timeout = Timeout });
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Execute" }).ClickAsync(new() { Timeout = Timeout });
        await AsyncTestHelpers.WaitUntilAsync(() => world.Game.Objects.Count == 1, message: "The wall was not built");
        await Expect(dialog).ToHaveCountAsync(0, new() { Timeout = Timeout });
        await buildWall.ClickAsync(new() { Timeout = Timeout });

        // Assert
        var wall = Assert.IsType<Wall>(Assert.Single(world.Game.Objects.Values));
        Assert.Equal((player.X - 3, player.Y + GamePlayer.Size, 8, 1, WallMaterial.Concrete), (wall.X, wall.Y, wall.Width, wall.Depth, wall.Material));
        await Expect(dialog.GetByLabel("Length", new() { Exact = true })).ToHaveValueAsync("8", new() { Timeout = Timeout });
        await Expect(dialog.GetByLabel("Material", new() { Exact = true })).ToHaveValueAsync("Concrete", new() { Timeout = Timeout });
    }
```

If `GetByLabel("Material")` does not resolve the select's input in MudBlazor 9.11, use `dialog.Locator(".mud-select input").First` instead; the assertion stays `ToHaveValueAsync("Concrete")`.

- [ ] **Step 7: Run all game tests**

Run: `dotnet build src/HomeBlaze/HomeBlaze.Game.Tests -c Debug`
Expected: Build succeeded, 0 warnings, 0 errors.

Run: `dotnet test src/HomeBlaze/HomeBlaze.Game.Tests -c Debug --filter "FullyQualifiedName!~E2E"`
Expected: all pass.

Run: `dotnet test src/HomeBlaze/HomeBlaze.Game.Tests -c Debug --filter "FullyQualifiedName~E2E"`
Expected: all pass.

- [ ] **Step 8: Commit and push**

```bash
git add src/HomeBlaze/HomeBlaze.Components/Operations/OperationInvoker.cs src/HomeBlaze/HomeBlaze.Components/Dialogs/MethodParameterDialog.razor src/HomeBlaze/HomeBlaze.Components.Tests/MethodParameterDialogTests.cs src/HomeBlaze/HomeBlaze.Components.Tests/OperationInvokerTests.cs src/HomeBlaze/HomeBlaze.Components.Tests/VerifyChecksTests.PublicApi.verified.txt src/HomeBlaze/HomeBlaze.Game/Components/GamePage.razor src/HomeBlaze/HomeBlaze.Game.Tests/E2E/GamePageTests.cs
git commit -m "feat: prefill operation dialogs with the parameters entered last" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push origin feat/homeblaze-game
```

---

## Task 6: "Add door" on walls

**Runnable:** yes. Controller restarts the app; looking at a demo wall of 4 or more tiles offers "Add door", which splits the wall around the player into wall, door, wall.

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze.Game/Objects/Buildings/Wall.cs`, `Objects/Buildings/Door.cs`
- Create (tests): `src/HomeBlaze/HomeBlaze.Game.Tests/Objects/Buildings/WallDoorTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `src/HomeBlaze/HomeBlaze.Game.Tests/Objects/Buildings/WallDoorTests.cs`:

```csharp
using HomeBlaze.Game.Tests.Testing;
using HomeBlaze.Services;
using Namotion.Interceptor.Registry;

namespace HomeBlaze.Game.Tests.Objects.Buildings;

public class WallDoorTests
{
    private readonly GameTestContext _context = new();

    private (Wall Before, Door Door, Wall After) GetPieces()
    {
        var walls = _context.Game.Objects.Values.OfType<Wall>().OrderBy(wall => wall.X).ThenBy(wall => wall.Y).ToArray();
        Assert.Equal(2, walls.Length);
        return (walls[0], Assert.Single(_context.Game.Objects.Values.OfType<Door>()), walls[1]);
    }

    [Fact]
    public async Task WhenADoorIsAddedToAnEastWestWall_ThenTheWallIsSplitAroundThePlayer()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        var wall = _context.AddWall(-4, 2, width: 16);

        // Act
        await wall.AddDoorAsync(null, new GameCaller(player), CancellationToken.None);

        // Assert
        var (before, door, after) = GetPieces();
        Assert.Equal((-4, 2, 3, 1), (before.X, before.Y, before.Width, before.Depth));
        Assert.Equal((-1, 2, 4, 1, false), (door.X, door.Y, door.Width, door.Depth, door.IsOpen));
        Assert.Equal((3, 2, 9, 1), (after.X, after.Y, after.Width, after.Depth));
        Assert.Equal(3, _context.Game.Objects.Count);
        Assert.Equal(1, _context.Root.WriteCount);
    }

    [Fact]
    public async Task WhenADoorIsAddedToANorthSouthWall_ThenTheWallIsSplitAroundThePlayer()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        _context.Game.TryMove(player, GameDirection.East, turnOnly: true);
        var wall = _context.AddWall(2, -4, width: 1, depth: 16);

        // Act
        await wall.AddDoorAsync(6, new GameCaller(player), CancellationToken.None);

        // Assert
        var walls = _context.Game.Objects.Values.OfType<Wall>().OrderBy(piece => piece.Y).ToArray();
        var door = Assert.Single(_context.Game.Objects.Values.OfType<Door>());
        Assert.Equal((2, -4, 1, 2), (walls[0].X, walls[0].Y, walls[0].Width, walls[0].Depth));
        Assert.Equal((2, -2, 1, 6), (door.X, door.Y, door.Width, door.Depth));
        Assert.Equal((2, 4, 1, 8), (walls[1].X, walls[1].Y, walls[1].Width, walls[1].Depth));
    }

    [Fact]
    public async Task WhenThePlayerStandsNearTheEnd_ThenTheDoorMovesInwardKeepingOneWallTile()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1", x: -1);
        var wall = _context.AddWall(0, 2, width: 8);

        // Act
        await wall.AddDoorAsync(4, new GameCaller(player), CancellationToken.None);

        // Assert
        var (before, door, after) = GetPieces();
        Assert.Equal((0, 1), (before.X, before.Width));
        Assert.Equal((1, 4), (door.X, door.Width));
        Assert.Equal((5, 3), (after.X, after.Width));
    }

    [Fact]
    public async Task WhenADoorIsAdded_ThenThePiecesKeepTheMaterialAndHeight()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        var wall = _context.AddWall(-4, 2, width: 16);
        wall.Material = WallMaterial.Concrete;
        wall.Height = 12;

        // Act
        await wall.AddDoorAsync(null, new GameCaller(player), CancellationToken.None);

        // Assert
        var (before, door, after) = GetPieces();
        Assert.All(new[] { before, after }, piece => Assert.Equal((WallMaterial.Concrete, 12), (piece.Material, piece.Height)));
        Assert.Equal((WallMaterial.Concrete, 12), (door.Material, door.Height));
    }

    [Fact]
    public async Task WhenThereIsNoActor_ThenTheDoorIsCenteredOnTheWall()
    {
        // Arrange
        var wall = _context.AddWall(0, 20, width: 10);

        // Act
        await wall.AddDoorAsync(4, null, CancellationToken.None);

        // Assert
        var (before, door, after) = GetPieces();
        Assert.Equal((3, 3, 3), (before.Width, door.X, after.Width));
    }

    [Fact]
    public async Task WhenTheDoorDoesNotFit_ThenAddingIsRefused()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        var wall = _context.AddWall(-1, 2, width: 5);

        // Act
        var exception = await Record.ExceptionAsync(() => wall.AddDoorAsync(4, new GameCaller(player), CancellationToken.None));

        // Assert
        Assert.Equal("This wall is too short for a door of 4 tiles; it fits up to 3.", Assert.IsType<InvalidOperationException>(exception).Message);
        Assert.Same(wall, Assert.Single(_context.Game.Objects).Value);
        Assert.Equal(0, _context.Root.WriteCount);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(9)]
    public async Task WhenTheWidthIsOutOfRange_ThenAddingIsRefused(int width)
    {
        // Arrange
        var wall = _context.AddWall(0, 20, width: 20);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => wall.AddDoorAsync(width, null, CancellationToken.None));
    }

    [Fact]
    public async Task WhenTheWallIsThickerThanOneTile_ThenAddingIsRefused()
    {
        // Arrange
        var wall = _context.AddWall(0, 20, width: 6, depth: 2);

        // Act
        var exception = await Record.ExceptionAsync(() => wall.AddDoorAsync(null, null, CancellationToken.None));

        // Assert
        Assert.Equal("Only a wall one tile thick can take a door.", Assert.IsType<InvalidOperationException>(exception).Message);
    }

    [Fact]
    public void WhenTheWallIsShorterThanFourTiles_ThenAddDoorIsDisabled()
    {
        // Arrange
        var wall = _context.AddWall(0, 20, width: 4);
        var addDoor = wall.TryGetRegisteredSubject()!.TryGetProperty("AddDoor")!;
        var isEnabledAtFour = addDoor.IsMethodEnabled();

        // Act
        wall.Width = 3;

        // Assert
        Assert.True(isEnabledAtFour);
        Assert.False(addDoor.IsMethodEnabled());
    }

    [Fact]
    public async Task WhenAPieceWouldOverlapAnotherObject_ThenNothingChanges()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        var wall = _context.AddWall(-4, 2, width: 16);
        // A hand-edited overlap: another wall on the last tile of the first one.
        _context.AddWall(11, 2, width: 1);

        // Act
        var exception = await Record.ExceptionAsync(() => wall.AddDoorAsync(null, new GameCaller(player), CancellationToken.None));

        // Assert
        Assert.Equal("Something is in the way.", Assert.IsType<InvalidOperationException>(exception).Message);
        Assert.Equal(2, _context.Game.Objects.Count);
        Assert.Contains(wall, _context.Game.Objects.Values);
    }
}
```

- [ ] **Step 2: Run the tests to see them fail**

Run: `dotnet build src/HomeBlaze/HomeBlaze.Game.Tests -c Debug`
Expected: FAIL to compile (`AddDoorAsync` does not exist).

- [ ] **Step 3: Implement "Add door"**

In `src/HomeBlaze/HomeBlaze.Game/Objects/Buildings/Door.cs` add after the `DefaultHeight` constant:

```csharp

    /// <summary>
    /// The narrowest door a wall takes in tiles, 50 cm.
    /// </summary>
    public const int MinimumWidth = 2;

    /// <summary>
    /// The widest door a wall takes in tiles, 2 m.
    /// </summary>
    public const int MaximumWidth = 8;
```

In `src/HomeBlaze/HomeBlaze.Game/Objects/Buildings/Wall.cs`:

Replace the usings

```csharp
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Game.Visuals;
using Namotion.Interceptor.Attributes;
```

with

```csharp
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Game.Visuals;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;
```

Add after the `DefaultHeight` constant:

```csharp

    /// <summary>
    /// The shortest wall that takes a door: one of <see cref="Door.MinimumWidth"/> tiles with a wall tile on each side.
    /// </summary>
    public const int MinimumDoorWallLength = Door.MinimumWidth + 2;
```

Replace

```csharp
    public override bool IsSoundBlocking => true;
```

with

```csharp
    public override bool IsSoundBlocking => true;

    [Derived]
    [PropertyAttribute("AddDoor", KnownAttributes.IsEnabled)]
    public bool AddDoor_IsEnabled => IsLine && Length >= MinimumDoorWallLength;

    // A wall runs along its longer side; one with both sides above 1 tile is no line.
    private bool IsLine => Width == 1 || Depth == 1;

    private int Length => Math.Max(Width, Depth);
```

Add after `DemolishAsync`:

```csharp

    /// <summary>
    /// Replaces this wall by a wall before the door, the door and a wall after it, keeping material and height. The door
    /// is centered on the caller's player along the wall, or on the wall without one, and moved inward so that at least
    /// one wall tile remains on each side.
    /// </summary>
    /// <param name="width">The door width in tiles, <see cref="Door.MinimumWidth"/> to <see cref="Door.MaximumWidth"/>;
    /// null for <see cref="Door.DefaultLength"/>.</param>
    /// <param name="caller">The acting player, or null.</param>
    /// <param name="cancellationToken">Cancels saving.</param>
    /// <exception cref="ArgumentOutOfRangeException">The width is out of range.</exception>
    /// <exception cref="InvalidOperationException">The wall is thicker than one tile, too short for the door, not part of
    /// a game, or a piece would overlap another movement blocking object.</exception>
    [Operation(Title = "Add door", Icon = "DoorFront", Position = 2,
        Description = "Replaces the part of this wall in front of you by a door, keeping a wall tile on each side. An empty width makes a door of 1 m.")]
    public Task AddDoorAsync([Range(Door.MinimumWidth, Door.MaximumWidth)] int? width, [FromServices] GameCaller? caller, CancellationToken cancellationToken)
    {
        var doorWidth = width ?? Door.DefaultLength;
        if (doorWidth is < Door.MinimumWidth or > Door.MaximumWidth)
        {
            throw new ArgumentOutOfRangeException(nameof(width), doorWidth, $"A door is {Door.MinimumWidth} to {Door.MaximumWidth} tiles wide.");
        }

        if (!IsLine)
        {
            throw new InvalidOperationException("Only a wall one tile thick can take a door.");
        }

        var fit = Length - 2;
        if (doorWidth > fit)
        {
            throw new InvalidOperationException($"This wall is too short for a door of {doorWidth} tiles; it fits up to {fit}.");
        }

        var game = GetGame();
        return game.ReplaceAsync(this, CreateDoorPieces(doorWidth, game.ResolveActor(caller)), cancellationToken);
    }

    // Works in doubled coordinates, where the center of a player, on the line between two tiles, is a whole number.
    private List<(string NamePrefix, GameObject Replacement)> CreateDoorPieces(int doorWidth, GamePlayer? actor)
    {
        var isAlongX = Width >= Depth;
        long start = isAlongX ? X : Y;
        var length = Length;
        var doubledCenter = actor is null ? 2 * start + length : 2L * (isAlongX ? actor.X : actor.Y) + GamePlayer.Size;
        var doorStart = Math.Clamp((long)Math.Floor((doubledCenter - doorWidth) / 2.0), start + 1, start + length - 1 - doorWidth);
        return
        [
            ("wall", CreatePiece(isAlongX, start, (int)(doorStart - start))),
            ("door", CreateDoor(isAlongX, doorStart, doorWidth)),
            ("wall", CreatePiece(isAlongX, doorStart + doorWidth, (int)(start + length - doorStart - doorWidth)))
        ];
    }

    private Wall CreatePiece(bool isAlongX, long position, int pieceLength) => new()
    {
        X = isAlongX ? (int)position : X,
        Y = isAlongX ? Y : (int)position,
        Width = isAlongX ? pieceLength : Width,
        Depth = isAlongX ? Depth : pieceLength,
        Height = Height,
        Material = Material
    };

    private Door CreateDoor(bool isAlongX, long position, int doorWidth) => new()
    {
        X = isAlongX ? (int)position : X,
        Y = isAlongX ? Y : (int)position,
        Width = isAlongX ? doorWidth : Width,
        Depth = isAlongX ? Depth : doorWidth,
        Height = Height,
        Material = Material
    };
```

`Wall` uses only public members of the core here (`GetGame`, `ReplaceAsync`, `ResolveActor`, `GamePlayer.X`, `GamePlayer.Y`, `GamePlayer.Size`).

- [ ] **Step 4: Run all tests**

Run: `dotnet build src/HomeBlaze/HomeBlaze.Game.Tests -c Debug`
Expected: Build succeeded, 0 warnings, 0 errors.

Run: `dotnet test src/HomeBlaze/HomeBlaze.Game.Tests -c Debug --filter "FullyQualifiedName!~E2E"`
Expected: all pass.

Run: `dotnet test src/HomeBlaze/HomeBlaze.Game.Tests -c Debug --filter "FullyQualifiedName~E2E"`
Expected: all pass (the wall E2E tests now also see "Add door" among the target actions; `WhenAWallIsBuiltAndDemolished` clicks "Demolish" by text, so it is unaffected).

- [ ] **Step 5: Commit and push**

```bash
git add src/HomeBlaze/HomeBlaze.Game/Objects/Buildings/Wall.cs src/HomeBlaze/HomeBlaze.Game/Objects/Buildings/Door.cs src/HomeBlaze/HomeBlaze.Game.Tests/Objects/Buildings/WallDoorTests.cs
git commit -m "feat: let players add doors to walls" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push origin feat/homeblaze-game
```

---

## Task 7: Trees and `TreeTool`

**Runnable:** yes. Controller restarts the app; "Plant tree" appears under Actions on the demo page, a planted tree sways, the player cannot walk into its trunk, and its canopy fades when it hides the player.

**Files:**
- Assets: create `src/HomeBlaze/HomeBlaze.Game/wwwroot/objects/nature/models/` with 7 `.glb` files and `LICENSE.md`
- Create: `src/HomeBlaze/HomeBlaze.Game/Objects/Nature/TreeVariant.cs`, `Objects/Nature/Tree.cs`, `Objects/Nature/TreeTool.cs`
- Modify: `src/HomeBlaze/HomeBlaze/Data/Files/Demo/Game.json` (add the tree tool)
- Create (tests): `src/HomeBlaze/HomeBlaze.Game.Tests/Testing/RepositoryPaths.cs`, `Objects/Nature/TreeTests.cs`, `Objects/Nature/TreeToolTests.cs`
- Modify (tests): `HomeBlaze.Game.Tests.csproj`, `GameVolumeTableTests.cs`, `GameSerializationTests.cs`, `E2E/GamePageTests.cs`

- [ ] **Step 1: Download the tree models**

```bash
SCRATCH=/tmp/claude-1000/-home-rico-GitHub-Namotion-Interceptor--claude-worktrees-bridge-cse-011ShuL5XgwWZqRmSZFhkZve/3d51e483-5513-56d7-b4a2-5748ff2f1414/scratchpad
mkdir -p "$SCRATCH/downloads/trees"
for model in common_tree_1 common_tree_2 common_tree_3 common_tree_1_autumn pine_tree_1 pine_tree_2 pine_tree_3; do
  curl -sSfL -o "$SCRATCH/downloads/trees/$model.glb" "https://raw.githubusercontent.com/flo-bit/low-poly-asset-packs/61bbdb032e079c7276a6558cbedc3ef1ea8b1575/nature-pack/glb/$model.glb"
done
cat > "$SCRATCH/downloads/trees.sha256" <<'EOF'
e17c3742e9251af93f03dee12d7d94c3ab15b78d61661e1a86b704f1f725f9ea  common_tree_1.glb
8451a97982c5e43641dce7cbea4624f68f22f6048c1a13f004d21e1e3c7b9d02  common_tree_2.glb
129a861f0eb715ff196791f93fe965a46833f945803c52c9fbd8d4df1c2e97a2  common_tree_3.glb
c629ed59bafd2ef839850d2f5daddee49367ccb3fa10648568922b7f1ee978c9  common_tree_1_autumn.glb
d37154637ccd55a5ff0cf53df7645f0503fd0b9bd81900bce8bc92624b2e514a  pine_tree_1.glb
a8d8e81d34713b79a5e0ee846281f2a4dcc5bcb6181c9b7c821fd188751310b3  pine_tree_2.glb
06fd84104dcacc73fbf214d8c295263f6e94698b7874bb991693c7173dfa269f  pine_tree_3.glb
EOF
(cd "$SCRATCH/downloads/trees" && sha256sum -c ../trees.sha256)
mkdir -p src/HomeBlaze/HomeBlaze.Game/wwwroot/objects/nature/models
cp "$SCRATCH"/downloads/trees/*.glb src/HomeBlaze/HomeBlaze.Game/wwwroot/objects/nature/models/
ls src/HomeBlaze/HomeBlaze.Game/wwwroot/objects/nature/models/
```

Expected: seven `OK` lines from `sha256sum -c`, then the seven files listed. Stop if a checksum fails.

Create `src/HomeBlaze/HomeBlaze.Game/wwwroot/objects/nature/models/LICENSE.md`:

```markdown
# Tree models

`common_tree_1.glb`, `common_tree_2.glb`, `common_tree_3.glb`, `common_tree_1_autumn.glb`, `pine_tree_1.glb`, `pine_tree_2.glb` and `pine_tree_3.glb` are from the "low poly nature pack" by flo-bit (https://github.com/flo-bit/low-poly-asset-packs, also at https://flo-bit.itch.io/low-poly-nature-pack), released under CC0 1.0 (https://creativecommons.org/publicdomain/zero/1.0/) as stated by the repository README ("All packs are licensed under CC0") and the pack README. They were taken unchanged from `nature-pack/glb/` at commit 61bbdb032e079c7276a6558cbedc3ef1ea8b1575. Each file is self-contained: vertex-colored materials, no textures, no animations, origin at the trunk base.
```

- [ ] **Step 2: Write the failing tree tests**

In `src/HomeBlaze/HomeBlaze.Game.Tests/HomeBlaze.Game.Tests.csproj` replace

```xml
        <Using Include="HomeBlaze.Game.Tools" />
```

with

```xml
        <Using Include="HomeBlaze.Game.Tools" />
        <Using Include="HomeBlaze.Game.Objects.Nature" />
```

Create `src/HomeBlaze/HomeBlaze.Game.Tests/Testing/RepositoryPaths.cs`:

```csharp
namespace HomeBlaze.Game.Tests.Testing;

/// <summary>
/// Finds files of the repository from the test output folder.
/// </summary>
internal static class RepositoryPaths
{
    /// <summary>
    /// Gets the full path of a file from its path relative to the repository root, searching upward from the test output.
    /// </summary>
    /// <exception cref="FileNotFoundException">No folder above the test output holds the file.</exception>
    public static string Get(string relativePath)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"{relativePath} was not found above {AppContext.BaseDirectory}.");
    }
}
```

Create `src/HomeBlaze/HomeBlaze.Game.Tests/Objects/Nature/TreeTests.cs`:

```csharp
using HomeBlaze.Game.Tests.Testing;

namespace HomeBlaze.Game.Tests.Objects.Nature;

public class TreeTests
{
    private readonly GameTestContext _context = new();

    [Fact]
    public void WhenATreeIsCreated_ThenItsTrunkHasAFixedSizeAndBlocksMovementButNotSound()
    {
        // Arrange
        var tree = new Tree();

        // Act
        ((GameObject)tree).Width = 5;
        ((GameObject)tree).Depth = 6;

        // Assert
        Assert.Equal((Tree.Size, Tree.Size), (tree.Width, tree.Depth));
        Assert.True(tree.IsMovementBlocking);
        Assert.False(tree.IsSoundBlocking);
        Assert.True(tree.IsVisible);
        Assert.Equal(24, tree.Height);
    }

    [Fact]
    public void WhenTheVariantIsOutsideTheTable_ThenTheTreeLooksLikeVariantZero()
    {
        // Arrange
        var unknown = new Tree { Variant = 99 };
        var first = new Tree { Variant = 0 };

        // Act
        var visual = unknown.Visual;

        // Assert
        Assert.Equal(first.Visual, visual);
        Assert.Equal(first.Height, unknown.Height);
    }

    [Fact]
    public void WhenTheTreeIsTurnedAndScaled_ThenItsModelFollows()
    {
        // Arrange
        var tree = new Tree { Variant = 4, Rotation = 90, Scale = 110 };

        // Act
        var part = Assert.Single(tree.Visual.Parts);

        // Assert
        var size = 22 * (110 / 100.0);
        Assert.Equal(GameVisualShape.Model, part.Shape);
        Assert.Equal("_content/HomeBlaze.Game/objects/nature/models/pine_tree_1.glb", part.Model);
        Assert.Equal(new GameVisualVector(size, size, size), part.Size);
        Assert.Equal(new GameVisualVector((Tree.Size - size) / 2, 0, (Tree.Size - size) / 2), part.Offset);
        Assert.Equal(new GameVisualPoint(1, 1), part.Pivot);
        Assert.Equal(90, part.Rotation);
        Assert.Equal(GameVisualEffect.Wind, part.Effect);
        Assert.Equal((int)Math.Ceiling(size), tree.Height);
    }

    [Fact]
    public void WhenTheScaleIsOutsideTheRange_ThenTheTreeKeepsWithinIt()
    {
        // Arrange
        var tiny = new Tree { Scale = 0 };
        var smallest = new Tree { Scale = Tree.MinimumScale };

        // Act
        var visual = tiny.Visual;

        // Assert
        Assert.Equal(smallest.Visual, visual);
    }

    [Fact]
    public void WhenTheTreeIsUnchanged_ThenItsVisualIsTheSameInstance()
    {
        // Arrange
        var tree = new Tree { Variant = 2 };
        var first = tree.Visual;

        // Act
        var second = tree.Visual;

        // Assert
        Assert.Same(first, second);
    }

    [Fact]
    public void WhenTheVariantsAreListed_ThenEveryModelFileExists()
    {
        // Act
        var paths = Tree.Variants.Select(variant => $"src/HomeBlaze/HomeBlaze.Game/wwwroot/objects/nature/models/{variant.File}").ToArray();

        // Assert
        Assert.Equal(Tree.VariantCount, Tree.Variants.Length);
        Assert.All(paths, path => Assert.True(File.Exists(RepositoryPaths.Get(path))));
    }

    [Fact]
    public void WhenATreeStandsInFront_ThenThePlayerCannotStepIntoIt()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        _context.AddObject("tree", new Tree { X = 0, Y = 2 });

        // Act
        _context.TimeProvider.Advance(Game.StepInterval);
        var result = _context.Game.TryMove(player, GameDirection.North, turnOnly: false);

        // Assert
        Assert.False(result.IsAccepted);
        Assert.Equal((0, 0), (player.X, player.Y));
    }

    [Fact]
    public async Task WhenATreeIsDemolished_ThenItIsRemovedAndWritten()
    {
        // Arrange
        var tree = _context.AddObject("tree", new Tree { X = 10, Y = 10 });

        // Act
        await tree.DemolishAsync(CancellationToken.None);

        // Assert
        Assert.Empty(_context.Game.Objects);
        Assert.Equal(1, _context.Root.WriteCount);
    }
}
```

Create `src/HomeBlaze/HomeBlaze.Game.Tests/Objects/Nature/TreeToolTests.cs`:

```csharp
using HomeBlaze.Game.Tests.Testing;

namespace HomeBlaze.Game.Tests.Objects.Nature;

public class TreeToolTests
{
    private readonly GameTestContext _context = new();
    private readonly TreeTool _tool;

    public TreeToolTests()
    {
        _tool = _context.AddTool("tree-tool", new TreeTool(new Random(876)));
    }

    [Fact]
    public async Task WhenAPlayerPlantsATreeWithoutAVariant_ThenARandomTreeStandsInFront()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");

        // Act
        await _tool.PlantTreeAsync(null, new GameCaller(player), CancellationToken.None);

        // Assert
        var tree = Assert.IsType<Tree>(Assert.Single(_context.Game.Objects).Value);
        Assert.Equal((0, 2), (tree.X, tree.Y));
        Assert.InRange(tree.Variant, 0, Tree.VariantCount - 1);
        Assert.InRange(tree.Rotation, 0, 359);
        Assert.InRange(tree.Scale, Tree.MinimumScale, Tree.MaximumScale);
        Assert.Equal(1, _context.Root.WriteCount);
        Assert.Same(tree, Assert.Single(_context.Game.GetTargetObjects(player)));
    }

    [Fact]
    public async Task WhenAVariantIsGiven_ThenTheTreeHasIt()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");

        // Act
        await _tool.PlantTreeAsync(5, new GameCaller(player), CancellationToken.None);

        // Assert
        Assert.Equal(5, Assert.IsType<Tree>(Assert.Single(_context.Game.Objects).Value).Variant);
    }

    [Fact]
    public async Task WhenManyTreesArePlanted_ThenRotationAndScaleVaryWithinTheirRanges()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        var rotations = new HashSet<int>();
        var scales = new HashSet<int>();

        // Act
        for (var index = 0; index < 20; index++)
        {
            await _tool.PlantTreeAsync(null, new GameCaller(player), CancellationToken.None);
            var tree = Assert.IsType<Tree>(Assert.Single(_context.Game.Objects).Value);
            rotations.Add(tree.Rotation);
            scales.Add(tree.Scale);
            await tree.DemolishAsync(CancellationToken.None);
        }

        // Assert
        Assert.All(rotations, rotation => Assert.InRange(rotation, 0, 359));
        Assert.All(scales, scale => Assert.InRange(scale, Tree.MinimumScale, Tree.MaximumScale));
        Assert.True(rotations.Count > 1);
        Assert.True(scales.Count > 1);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(Tree.VariantCount)]
    public async Task WhenTheVariantIsOutOfRange_ThenPlantingIsRefused(int variant)
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _tool.PlantTreeAsync(variant, new GameCaller(player), CancellationToken.None));
        Assert.Empty(_context.Game.Objects);
    }

    [Fact]
    public async Task WhenSomethingIsInTheWay_ThenPlantingIsRefused()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        _context.AddWall(1, 3, width: 1);

        // Act
        var exception = await Record.ExceptionAsync(() => _tool.PlantTreeAsync(null, new GameCaller(player), CancellationToken.None));

        // Assert
        Assert.Equal("Something is in the way.", Assert.IsType<InvalidOperationException>(exception).Message);
        Assert.Single(_context.Game.Objects);
    }

    [Fact]
    public async Task WhenThereIsNoActor_ThenPlantingIsRefused()
    {
        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => _tool.PlantTreeAsync(null, null, CancellationToken.None));
    }
}
```

In `src/HomeBlaze/HomeBlaze.Game.Tests/GameVolumeTableTests.cs` add after `WhenTwoPlayersStandWithinRange_ThenEachHearsTheOther`:

```csharp
    [Fact]
    public void WhenATreeStandsBetweenTwoPlayers_ThenTheyHearEachOtherAsWithoutIt()
    {
        // Arrange
        _context.AddConnectedPlayer("tab-1");
        _context.AddConnectedPlayer("tab-2", x: 10);
        _context.AddObject("tree", new Tree { X = 5, Y = 0 });

        // Act
        _context.Game.UpdateVolumeTable();

        // Assert
        var table = _context.Game.VolumeTable;
        Assert.Equal(0.4f, FindVolume(table, "tab-1", "tab-2"));
        Assert.Equal(0.4f, FindVolume(table, "tab-2", "tab-1"));
    }
```

In `src/HomeBlaze/HomeBlaze.Game.Tests/GameSerializationTests.cs` replace

```csharp
        typeProvider.AddTypes([typeof(Game), typeof(Wall), typeof(Door), typeof(GamePlayer), typeof(WallTool)]);
```

with

```csharp
        typeProvider.AddTypes([typeof(Game), typeof(Wall), typeof(Door), typeof(GamePlayer), typeof(WallTool), typeof(Tree), typeof(TreeTool)]);
```

and add before the last test:

```csharp
    [Fact]
    public void WhenATreeAndTheTreeToolRoundTrip_ThenTheyKeepTheirTypeAndState()
    {
        // Arrange
        var game = new Game(Microsoft.Extensions.Logging.Abstractions.NullLogger<Game>.Instance)
        {
            Objects = new Dictionary<string, GameObject> { ["tree-1"] = new Tree { X = 3, Y = 4, Variant = 5, Rotation = 120, Scale = 90 } },
            Tools = new Dictionary<string, GameTool> { ["tree-tool"] = new TreeTool() }
        };

        // Act
        var json = _serializer.Serialize(game);
        var loaded = Assert.IsType<Game>(_serializer.Deserialize(json));

        // Assert
        Assert.Contains("\"$type\": \"HomeBlaze.Game.Objects.Nature.Tree\"", json);
        Assert.Contains("\"$type\": \"HomeBlaze.Game.Objects.Nature.TreeTool\"", json);
        var tree = Assert.IsType<Tree>(loaded.Objects["tree-1"]);
        Assert.Equal((3, 4, 5, 120, 90), (tree.X, tree.Y, tree.Variant, tree.Rotation, tree.Scale));
        Assert.IsType<TreeTool>(loaded.Tools["tree-tool"]);
    }
```

- [ ] **Step 3: Run the tests to see them fail**

Run: `dotnet build src/HomeBlaze/HomeBlaze.Game.Tests -c Debug`
Expected: FAIL to compile (`Tree`, `TreeTool`, `HomeBlaze.Game.Objects.Nature` do not exist).

- [ ] **Step 4: Implement trees**

Create `src/HomeBlaze/HomeBlaze.Game/Objects/Nature/TreeVariant.cs`:

```csharp
using HomeBlaze.Game.Visuals;

namespace HomeBlaze.Game.Objects.Nature;

/// <summary>
/// A tree model: its file under <c>wwwroot/objects/nature/models/</c> and its height in tiles at a scale of 100 percent.
/// </summary>
internal sealed record TreeVariant(string File, int Height)
{
    public string Url { get; } = GameAssets.Url(typeof(TreeVariant), $"objects/nature/models/{File}");
}
```

Create `src/HomeBlaze/HomeBlaze.Game/Objects/Nature/Tree.cs`:

```csharp
using System.Collections.Immutable;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Game.Visuals;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Game.Objects.Nature;

/// <summary>
/// A tree that sways in the wind. Its trunk covers 2 by 2 tiles, which block movement but not sound.
/// </summary>
[Category("Game")]
[Description("A tree that sways in the wind; its trunk blocks movement but not sound")]
[DisplayName("Tree")]
[SubjectIcon("Park")]
[InterceptorSubject]
public partial class Tree : GameObject, ITitleProvider, IIconProvider
{
    /// <summary>
    /// The width and depth of the trunk's footprint in tiles, 50 cm, like a player.
    /// </summary>
    public const int Size = 2;

    /// <summary>
    /// The number of tree models; <see cref="Variant"/> runs from 0 to one less.
    /// </summary>
    public const int VariantCount = 7;

    /// <summary>
    /// The smallest <see cref="Scale"/> in percent.
    /// </summary>
    public const int MinimumScale = 80;

    /// <summary>
    /// The largest <see cref="Scale"/> in percent.
    /// </summary>
    public const int MaximumScale = 120;

    internal static readonly ImmutableArray<TreeVariant> Variants =
    [
        new("common_tree_1.glb", 24),
        new("common_tree_2.glb", 21),
        new("common_tree_3.glb", 24),
        new("common_tree_1_autumn.glb", 22),
        new("pine_tree_1.glb", 22),
        new("pine_tree_2.glb", 24),
        new("pine_tree_3.glb", 16)
    ];

    private readonly GameVisualCache<(int Variant, int Rotation, int Scale)> _visual = new();

    /// <summary>
    /// The model, from 0 to <see cref="VariantCount"/> - 1; another value, possible in a loaded file, shows variant 0.
    /// </summary>
    [Configuration]
    [Range(0, VariantCount - 1)]
    public partial int Variant { get; set; }

    /// <summary>
    /// The turn in degrees clockwise seen from above.
    /// </summary>
    [Configuration]
    [Range(0, 359)]
    public partial int Rotation { get; set; }

    /// <summary>
    /// The size in percent of the model's size; a value outside <see cref="MinimumScale"/> to <see cref="MaximumScale"/>
    /// shows as the nearest limit.
    /// </summary>
    [Configuration]
    [Range(MinimumScale, MaximumScale)]
    public partial int Scale { get; set; }

    public string? Title => "Tree";

    public string? IconName => "Park";

    public override int Width => Size;

    public override int Depth => Size;

    public override int Height => (int)Math.Ceiling(GetVariant(Variant).Height * GetScaleFactor(Scale));

    public override GameVisual Visual => _visual.Get((Variant, Rotation, Scale),
        static state => CreateVisual(state.Variant, state.Rotation, state.Scale));

    public Tree()
    {
        Variant = 0;
        Rotation = 0;
        Scale = 100;
    }

    /// <exception cref="InvalidOperationException">The tree is not part of a game.</exception>
    [Operation(Title = "Demolish", Icon = "Delete", Position = 1, Description = "Removes this tree from the game.")]
    public Task DemolishAsync(CancellationToken cancellationToken) => GetGame().RemoveAsync(this, cancellationToken);

    private static TreeVariant GetVariant(int variant) => variant is >= 0 and < VariantCount ? Variants[variant] : Variants[0];

    private static double GetScaleFactor(int scale) => Math.Clamp(scale, MinimumScale, MaximumScale) / 100.0;

    // Every model is taller than wide, so its height fills a cube of the tree's height around the trunk.
    private static GameVisual CreateVisual(int variant, int rotation, int scale)
    {
        var model = GetVariant(variant);
        var size = model.Height * GetScaleFactor(scale);
        var offset = (Size - size) / 2;
        return new GameVisual([new GameVisualPart
        {
            Shape = GameVisualShape.Model,
            Model = model.Url,
            Offset = new GameVisualVector(offset, 0, offset),
            Size = new GameVisualVector(size, size, size),
            Pivot = new GameVisualPoint(Size / 2.0, Size / 2.0),
            Rotation = ((rotation % 360) + 360) % 360,
            Effect = GameVisualEffect.Wind
        }]);
    }
}
```

Create `src/HomeBlaze/HomeBlaze.Game/Objects/Nature/TreeTool.cs`:

```csharp
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Game.Tools;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Game.Objects.Nature;

/// <summary>
/// Plants trees in front of the player.
/// </summary>
[Category("Game")]
[Description("Plants trees in front of the player")]
[DisplayName("Tree tool")]
[SubjectIcon("Park")]
[InterceptorSubject]
public partial class TreeTool : GameTool, ITitleProvider, IIconProvider
{
    private readonly Random _random;

    public TreeTool()
        : this(Random.Shared)
    {
    }

    /// <param name="random">Chooses variant, rotation and scale; tests pass a seeded one.</param>
    internal TreeTool(Random random)
    {
        _random = random;
    }

    public string? Title => "Tree tool";

    public string? IconName => "Park";

    /// <summary>
    /// Plants a tree with a random rotation and scale in front of the player.
    /// </summary>
    /// <param name="variant">The model, 0 to <see cref="Tree.VariantCount"/> - 1; null picks one at random.</param>
    /// <param name="caller">The acting player.</param>
    /// <param name="cancellationToken">Cancels saving.</param>
    /// <exception cref="ArgumentOutOfRangeException">The variant is out of range.</exception>
    /// <exception cref="InvalidOperationException">There is no player, it faces diagonally, something is in the way, or
    /// the tool is not part of a game.</exception>
    [Operation(Title = "Plant tree", Icon = "Park", Position = 1,
        Description = "Plants a tree in front of you. An empty variant picks one at random.")]
    public Task PlantTreeAsync([Range(0, Tree.VariantCount - 1)] int? variant, [FromServices] GameCaller? caller, CancellationToken cancellationToken)
    {
        if (variant is < 0 or >= Tree.VariantCount)
        {
            throw new ArgumentOutOfRangeException(nameof(variant), variant, $"The variant is from 0 to {Tree.VariantCount - 1}.");
        }

        var game = GetGame();
        var placement = game.GetPlacementInFront(caller, across: Tree.Size, along: Tree.Size);
        var tree = new Tree
        {
            X = placement.X,
            Y = placement.Y,
            Variant = variant ?? _random.Next(Tree.VariantCount),
            Rotation = _random.Next(360),
            Scale = _random.Next(Tree.MinimumScale, Tree.MaximumScale + 1)
        };
        return game.PlaceAsync(caller, "tree", tree, cancellationToken);
    }
}
```

`Tree.Variants` and the `TreeTool(Random)` constructor are internal members of the object folder itself, not of the core; `Tree` and `TreeTool` use only public members of the core.

- [ ] **Step 5: Add the tree tool to the demo world**

```bash
sed -i 's|"wall-tool": { "$type": "HomeBlaze.Game.Objects.Buildings.WallTool" } },|"wall-tool": { "$type": "HomeBlaze.Game.Objects.Buildings.WallTool" }, "tree-tool": { "$type": "HomeBlaze.Game.Objects.Nature.TreeTool" } },|' src/HomeBlaze/HomeBlaze/Data/Files/Demo/Game.json
python3 -I -c "import json, sys; print(sorted(json.load(open(sys.argv[1]))['tools']))" src/HomeBlaze/HomeBlaze/Data/Files/Demo/Game.json
```

Expected: `['tree-tool', 'wall-tool']`.

- [ ] **Step 6: Write the tree E2E tests**

In `src/HomeBlaze/HomeBlaze.Game.Tests/E2E/GamePageTests.cs` add after `WhenAClosedDoorIsOpenedWithItsNumberKey_ThenThePlayerStepsThroughIt`:

```csharp
    [Fact]
    public async Task WhenATreeIsPlanted_ThenItStandsInFrontAndThePlayerCannotWalkIntoIt()
    {
        // Arrange
        await using var world = await GameWorld.CreateAsync(_fixture);
        world.Game.Tools = new Dictionary<string, GameTool> { ["tree-tool"] = new TreeTool() };
        var (page, player) = await world.JoinAsync();
        var (x, y) = (player.X, player.Y);

        // Act
        await page.GetByTestId("game-action").Filter(new() { HasText = "Plant tree" }).ClickAsync(new() { Timeout = Timeout });
        await page.Locator(".mud-dialog").GetByRole(AriaRole.Button, new() { Name = "Execute" }).ClickAsync(new() { Timeout = Timeout });
        await AsyncTestHelpers.WaitUntilAsync(() => world.Game.Objects.Count == 1, message: "The tree was not planted");
        await Expect(page.GetByTestId("game-target-title")).ToHaveTextAsync("Tree", new() { Timeout = Timeout });
        await page.Keyboard.PressAsync("ArrowUp");
        // The circuit answers the turn after the step, so the turn shows only once the step was decided.
        await page.Keyboard.PressAsync("Shift+ArrowLeft");

        // Assert
        var tree = Assert.IsType<Tree>(Assert.Single(world.Game.Objects.Values));
        Assert.Equal((x, y + GamePlayer.Size), (tree.X, tree.Y));
        await Expect(page.GetByTestId("game-position")).ToHaveTextAsync(FormatPosition(x, y, "W"), new() { Timeout = Timeout });
        var canvas = page.GetByTestId("game-canvas");
        await Expect(canvas).ToHaveAttributeAsync("data-game-models", "1", new() { Timeout = Timeout });
        await Expect(canvas).ToHaveAttributeAsync("data-game-model-failures", "0", new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenATreeStandsBetweenTwoPlayers_ThenTheListenerStillPlaysTheTalkersFrames()
    {
        // Arrange
        await using var browser = await MediaBrowser.LaunchAsync();
        await using var world = await GameWorld.CreateAsync(_fixture);
        var (talker, talkerPlayer) = await world.JoinAsync(CreateMicrophoneContext(), browser.Browser);
        var (listener, listenerPlayer) = await world.JoinAsync(CreateMicrophoneContext(), browser.Browser);

        // Two meters east of the listener, with a tree right between them.
        talkerPlayer.X = listenerPlayer.X + 8;
        talkerPlayer.Y = listenerPlayer.Y;
        world.Game.Objects = new Dictionary<string, GameObject> { ["tree"] = new Tree { X = listenerPlayer.X + 4, Y = listenerPlayer.Y } };

        // Act
        await TurnOnVoiceAsync(talker);
        await TurnOnVoiceAsync(listener);

        // Assert
        await Expect(listener.GetByTestId("game-nearby-player").Filter(new() { HasText = talkerPlayer.Title })).ToHaveCountAsync(1, new() { Timeout = Timeout });
        await WaitForCounterAsync(listener, ReceivedFramesAttribute, 50);
    }
```

- [ ] **Step 7: Run all tests**

Run: `dotnet build src/HomeBlaze/HomeBlaze.Game.Tests -c Debug`
Expected: Build succeeded, 0 warnings, 0 errors.

Run: `dotnet test src/HomeBlaze/HomeBlaze.Game.Tests -c Debug --filter "FullyQualifiedName!~E2E"`
Expected: all pass.

Run: `dotnet test src/HomeBlaze/HomeBlaze.Game.Tests -c Debug --filter "FullyQualifiedName~E2E"`
Expected: all pass; the planted tree's model loads (`data-game-models` 1, no failure).

- [ ] **Step 8: Commit and push**

```bash
git add src/HomeBlaze/HomeBlaze.Game/wwwroot/objects/nature/models src/HomeBlaze/HomeBlaze.Game/Objects/Nature src/HomeBlaze/HomeBlaze/Data/Files/Demo/Game.json src/HomeBlaze/HomeBlaze.Game.Tests/HomeBlaze.Game.Tests.csproj src/HomeBlaze/HomeBlaze.Game.Tests/Testing/RepositoryPaths.cs src/HomeBlaze/HomeBlaze.Game.Tests/Objects/Nature src/HomeBlaze/HomeBlaze.Game.Tests/GameVolumeTableTests.cs src/HomeBlaze/HomeBlaze.Game.Tests/GameSerializationTests.cs src/HomeBlaze/HomeBlaze.Game.Tests/E2E/GamePageTests.cs
git commit -m "feat: add trees that sway in the wind and a tool to plant them" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push origin feat/homeblaze-game
```

---

## Task 8: New demo world

**Runnable:** yes. Controller restarts the app; the demo page shows the house in front of the spawn point, the shed to the west and the forest to the east.

**Files:**
- Scratchpad: `$SCRATCH/world/make_world.py` (not committed)
- Replace: `src/HomeBlaze/HomeBlaze/Data/Files/Demo/Game.json`
- Create (tests): `src/HomeBlaze/HomeBlaze.Game.Tests/DemoWorldTests.cs`

The layout (tiles; north is up, the spawn point is 0, 0 just south of the entrance):

| Area | Tiles inside the walls | Doors (width) |
|---|---|---|
| House outline | Walls from -28, 8 to 27, 47 (56 x 40, bricks) | Entrance in the south wall at x -2 to 3 (6, open) |
| Living room | -27..-8 x 9..28 (20 x 20) | To the hall at y 16 to 20 (5, open), to the bedroom at x -20 to -16 (5, closed) |
| Hall | -6..9 x 9..28 | To the kitchen at y 16 to 20 (5, open), to the second bedroom at x 0 to 4 (5, open) |
| Kitchen | 11..26 x 9..28 | |
| Bedroom | -27..-8 x 30..46 | |
| Second bedroom | -6..9 x 30..46 | To the bathroom at y 36 to 39 (4, closed) |
| Bathroom | 11..26 x 30..46 | |
| Shed (wood, 9 tiles tall) | -47..-38 x 13..20 | East wall at y 15 to 18 (4, closed) |
| Forest | 32 trees in 36..99 x -10..59, every variant | |
| Garden and backyard | 4 trees south of the house, 3 north of it | |

Trees keep 3 tiles between each other, stay out of the house and shed with a margin, out of 6 x 6 tiles around the spawn point and its path to the entrance, and 5 tiles away from both sides of every door.

- [ ] **Step 1: Write the failing demo world tests**

Create `src/HomeBlaze/HomeBlaze.Game.Tests/DemoWorldTests.cs`:

```csharp
using HomeBlaze.Game.Tests.Testing;
using HomeBlaze.Game.World;
using HomeBlaze.Services;
using Microsoft.Extensions.DependencyInjection;

namespace HomeBlaze.Game.Tests;

public class DemoWorldTests
{
    private const string DemoWorldPath = "src/HomeBlaze/HomeBlaze/Data/Files/Demo/Game.json";

    // A position inside each room where a player fits, as the south-west corner of its footprint.
    private static readonly (string Name, int X, int Y)[] Rooms =
    [
        ("living room", -18, 18),
        ("hall", 2, 18),
        ("kitchen", 18, 18),
        ("bedroom", -18, 38),
        ("second bedroom", 2, 38),
        ("bathroom", 18, 38),
        ("shed", -43, 16)
    ];

    private static readonly (int X, int Y)[] Steps = [(1, 0), (-1, 0), (0, 1), (0, -1)];

    private readonly Game _game;

    public DemoWorldTests()
    {
        var typeProvider = new TypeProvider();
        typeProvider.AddTypes([typeof(Game), typeof(GamePlayer), typeof(Wall), typeof(Door), typeof(Tree), typeof(WallTool), typeof(TreeTool)]);
        var serializer = new ConfigurableSubjectSerializer(typeProvider, new ServiceCollection().AddLogging().BuildServiceProvider());
        _game = Assert.IsType<Game>(serializer.Deserialize(File.ReadAllText(RepositoryPaths.Get(DemoWorldPath))));
    }

    [Fact]
    public void WhenTheDemoWorldLoads_ThenItHasAHouseAShedAForestAndTheTools()
    {
        // Act
        var walls = _game.Objects.Values.OfType<Wall>().ToArray();
        var doors = _game.Objects.Values.OfType<Door>().ToArray();
        var forest = _game.Objects
            .Where(pair => pair.Key.StartsWith("forest-", StringComparison.Ordinal))
            .Select(pair => Assert.IsType<Tree>(pair.Value))
            .ToArray();

        // Assert
        Assert.True(walls.Length >= 20);
        Assert.Contains(walls, wall => wall.Material == WallMaterial.Concrete);
        Assert.Contains(walls, wall => wall.Material == WallMaterial.Wood);
        Assert.InRange(doors.Length, 6, 10);
        Assert.All(doors, door => Assert.InRange(Math.Max(door.Width, door.Depth), 4, 6));
        Assert.InRange(forest.Length, 25, 40);
        Assert.All(forest, tree => Assert.True(tree.X > 27, "The forest stands east of the house."));
        Assert.Equal(Enumerable.Range(0, Tree.VariantCount), forest.Select(tree => tree.Variant).Distinct().Order());
        Assert.IsType<WallTool>(_game.Tools["wall-tool"]);
        Assert.IsType<TreeTool>(_game.Tools["tree-tool"]);
    }

    [Fact]
    public void WhenWalkingFromTheSpawnPoint_ThenEveryRoomIsReachableThroughItsDoors()
    {
        // Act
        var reachable = FindReachablePositions();

        // Assert
        Assert.All(Rooms, room => Assert.True(reachable.Contains((room.X, room.Y)), $"The {room.Name} cannot be reached."));
    }

    [Fact]
    public void WhenTheTreesStand_ThenNoneBlocksADoorOrTheSpawnPoint()
    {
        // Arrange
        var keepClear = _game.Objects.Values.OfType<Door>().Select(GetApproach).Append(new Footprint(-2, -2, 6, 6)).ToArray();

        // Act
        var blocking = _game.Objects.Values.OfType<Tree>().Where(tree => keepClear.Any(area => area.Intersects(tree.Footprint))).ToArray();

        // Assert
        Assert.Empty(blocking);
    }

    // The door and 3 tiles on both sides of it, where a player stands to walk through.
    private static Footprint GetApproach(Door door) => door.Width >= door.Depth
        ? new Footprint(door.X, door.Y - 3, door.Width, door.Depth + 6)
        : new Footprint(door.X - 3, door.Y, door.Width + 6, door.Depth);

    // Every position of a player's footprint that walking reaches from the spawn point at 0, 0 with every door passable.
    private HashSet<(int X, int Y)> FindReachablePositions()
    {
        var index = new GameWorldIndex();
        foreach (var gameObject in _game.Objects.Values.Where(gameObject => gameObject is not Door))
        {
            index.AddOrUpdate(gameObject);
        }

        var reachable = new HashSet<(int X, int Y)> { (0, 0) };
        var queue = new Queue<(int X, int Y)>();
        queue.Enqueue((0, 0));
        while (queue.TryDequeue(out var position))
        {
            foreach (var (offsetX, offsetY) in Steps)
            {
                var next = (X: position.X + offsetX, Y: position.Y + offsetY);
                if (IsWithinArea(next) && !index.IsBlocked(new Footprint(next.X, next.Y, GamePlayer.Size, GamePlayer.Size), ignored: null) && reachable.Add(next))
                {
                    queue.Enqueue(next);
                }
            }
        }

        return reachable;
    }

    private static bool IsWithinArea((int X, int Y) position) =>
        position.X is >= -70 and <= 120 && position.Y is >= -40 and <= 80;
}
```

- [ ] **Step 2: Run the tests to see them fail**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Game.Tests -c Debug --filter "FullyQualifiedName~DemoWorldTests"`
Expected: FAIL (the current demo world has no rooms with doors, no trees and no shed).

- [ ] **Step 3: Generate the demo world**

Create `$SCRATCH/world/make_world.py` with this content (a copy made while planning may already be there; overwrite it):

```python
"""Generates the demo world of the game: a house, a wooden shed and a forest east of the house.

Usage: python3 -I make_world.py <output path>
"""
import json
import random
import sys

WALL = "HomeBlaze.Game.Objects.Buildings.Wall"
DOOR = "HomeBlaze.Game.Objects.Buildings.Door"
TREE = "HomeBlaze.Game.Objects.Nature.Tree"
WALL_TOOL = "HomeBlaze.Game.Objects.Buildings.WallTool"
TREE_TOOL = "HomeBlaze.Game.Objects.Nature.TreeTool"
VARIANT_COUNT = 7
TREE_SIZE = 2
HEIGHT = 10
SHED_HEIGHT = 9

objects = {}
doors = []


def wall(name, x, y, width, depth, material, height=HEIGHT):
    objects[name] = {"$type": WALL, "x": x, "y": y, "width": width, "depth": depth, "height": height, "material": material}


def door(name, x, y, width, depth, material, is_open, height=HEIGHT):
    objects[name] = {"$type": DOOR, "x": x, "y": y, "width": width, "depth": depth, "height": height, "material": material, "isOpen": is_open}
    doors.append((x, y, width, depth))


def horizontal(name, y, segments, material, height=HEIGHT):
    """A wall running east to west at y; segments are (start x, end x) of walls or ("door", start x, end x, open)."""
    for index, segment in enumerate(segments):
        if segment[0] == "door":
            _, start, end, is_open = segment
            door(f"{name}-{index + 1}", start, y, end - start + 1, 1, material, is_open, height)
        else:
            start, end = segment
            wall(f"{name}-{index + 1}", start, y, end - start + 1, 1, material, height)


def vertical(name, x, segments, material, height=HEIGHT):
    """A wall running north to south at x; segments as for horizontal, in y."""
    for index, segment in enumerate(segments):
        if segment[0] == "door":
            _, start, end, is_open = segment
            door(f"{name}-{index + 1}", x, start, 1, end - start + 1, material, is_open, height)
        else:
            start, end = segment
            wall(f"{name}-{index + 1}", x, start, 1, end - start + 1, material, height)


# The house: 56 x 40 tiles from -28, 8 to 27, 47. Rooms, inside the outer walls:
# living room -27..-8 x 9..28, hall -6..9 x 9..28, kitchen 11..26 x 9..28,
# bedroom -27..-8 x 30..46, bedroom -6..9 x 30..46, bathroom 11..26 x 30..46.
horizontal("house-south", 8, [(-28, -3), ("door", -2, 3, True), (4, 27)], "Bricks")
horizontal("house-north", 47, [(-28, 27)], "Bricks")
vertical("house-west", -28, [(9, 46)], "Bricks")
vertical("house-east", 27, [(9, 46)], "Bricks")
vertical("living-hall", -7, [(9, 15), ("door", 16, 20, True), (21, 46)], "Concrete")
vertical("hall-kitchen", 10, [(9, 15), ("door", 16, 20, True), (21, 35), ("door", 36, 39, False), (40, 46)], "Concrete")
horizontal("living-bedroom", 29, [(-27, -21), ("door", -20, -16, False), (-15, -8)], "Concrete")
horizontal("hall-bedroom", 29, [(-6, -1), ("door", 0, 4, True), (5, 9)], "Concrete")
horizontal("kitchen-bathroom", 29, [(11, 26)], "Concrete")

# The wooden shed west of the house, inside -47..-38 x 13..20, with its door facing the house.
horizontal("shed-south", 12, [(-48, -37)], "Wood", SHED_HEIGHT)
horizontal("shed-north", 21, [(-48, -37)], "Wood", SHED_HEIGHT)
vertical("shed-west", -48, [(13, 20)], "Wood", SHED_HEIGHT)
vertical("shed-east", -37, [(13, 14), ("door", 15, 18, False), (19, 20)], "Wood", SHED_HEIGHT)

# Areas that trees keep clear of, as (x, y, width, depth): the house and the shed with a margin, the spawn point at 0, 0
# with the path to the entrance, and 4 tiles on both sides of every door.
keep_clear = [(-31, 5, 62, 46), (-51, 9, 18, 16), (-6, -6, 14, 14)]
for x, y, width, depth in doors:
    if width >= depth:
        keep_clear.append((x - 1, y - 5, width + 2, depth + 10))
    else:
        keep_clear.append((x - 5, y - 1, width + 10, depth + 2))


def intersects(first, second):
    return first[0] < second[0] + second[2] and second[0] < first[0] + first[2] and first[1] < second[1] + second[3] and second[1] < first[1] + first[3]


trees = []


def plant(rng, area, count, prefix, variants):
    """Plants count trees in area (x, y, width, depth), at least 3 tiles apart so that players walk between them."""
    planted = 0
    attempts = 0
    while planted < count:
        attempts += 1
        if attempts > 10000:
            raise RuntimeError(f"Could not plant {count} trees in {area}")
        x = rng.randrange(area[0], area[0] + area[2] - TREE_SIZE + 1)
        y = rng.randrange(area[1], area[1] + area[3] - TREE_SIZE + 1)
        footprint = (x, y, TREE_SIZE, TREE_SIZE)
        spaced = (x - 3, y - 3, TREE_SIZE + 6, TREE_SIZE + 6)
        if any(intersects(footprint, clear) for clear in keep_clear) or any(intersects(spaced, tree) for tree in trees):
            continue
        trees.append(footprint)
        planted += 1
        objects[f"{prefix}-{planted:02d}"] = {
            "$type": TREE, "x": x, "y": y,
            "variant": variants[(planted - 1) % len(variants)],
            "rotation": rng.randrange(0, 360),
            "scale": rng.randrange(80, 121),
        }


rng = random.Random(876)
plant(rng, (36, -10, 64, 70), 32, "forest", list(range(VARIANT_COUNT)))
plant(rng, (-40, -24, 80, 14), 4, "garden", [0, 2, 4, 6])
plant(rng, (-40, 52, 80, 12), 3, "backyard", [1, 3, 5])

world = {
    "$type": "HomeBlaze.Game.Game",
    "name": "Demo world",
    "players": {},
    "tools": {
        "wall-tool": {"$type": WALL_TOOL},
        "tree-tool": {"$type": TREE_TOOL},
    },
    "objects": "OBJECTS",
}

lines = [f'    "{name}": {json.dumps(value)}' for name, value in objects.items()]
text = json.dumps(world, indent=2).replace('"OBJECTS"', "{\n" + ",\n".join(lines) + "\n  }")
with open(sys.argv[1], "w", encoding="utf-8", newline="\n") as file:
    file.write(text + "\n")
print(f"{len(objects)} objects, {len(trees)} trees, {len(doors)} doors")
```

Run:

```bash
SCRATCH=/tmp/claude-1000/-home-rico-GitHub-Namotion-Interceptor--claude-worktrees-bridge-cse-011ShuL5XgwWZqRmSZFhkZve/3d51e483-5513-56d7-b4a2-5748ff2f1414/scratchpad
git ls-files -v src/HomeBlaze/HomeBlaze/Data/Files/Demo/Game.json
python3 -I "$SCRATCH/world/make_world.py" src/HomeBlaze/HomeBlaze/Data/Files/Demo/Game.json
```

Expected: the first line starts with `H ` (if it shows `S `, the controller has not run Task 3 Step 0 yet: stop and report), then `66 objects, 39 trees, 7 doors`.

- [ ] **Step 4: Run all tests**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Game.Tests -c Debug --filter "FullyQualifiedName!~E2E"`
Expected: all pass, including the three `DemoWorldTests`.

- [ ] **Step 5: Commit and push**

```bash
git add src/HomeBlaze/HomeBlaze/Data/Files/Demo/Game.json src/HomeBlaze/HomeBlaze.Game.Tests/DemoWorldTests.cs
git status --short src/HomeBlaze/HomeBlaze/Data
git commit -m "feat: replace the demo world by a house with six rooms, a shed and a forest" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push origin feat/homeblaze-game
```

`git status --short src/HomeBlaze/HomeBlaze/Data` must list only `Demo/Game.json` as staged.

---

## Task 9: Full verification and screenshot (controller)

**Runnable:** no code changes. The controller runs this after Task 8, with the app restarted on the new demo world.

- [ ] **Step 1: Run every suite**

```bash
dotnet test src/HomeBlaze/HomeBlaze.Game.Tests -c Debug --filter "FullyQualifiedName!~E2E"
dotnet test src/HomeBlaze/HomeBlaze.Game.Tests -c Debug --filter "FullyQualifiedName~E2E"
dotnet test src/HomeBlaze/HomeBlaze.Components.Tests -c Debug
dotnet test src/Namotion.Interceptor.slnx -c Debug --filter "Category!=Integration"
```

Expected: every run reports 0 failed. Note the passed counts for the PR description (Task 10).

- [ ] **Step 2: Take a screenshot of the demo world**

With the app running (see Conventions), from the repository root:

```bash
SCRATCH=/tmp/claude-1000/-home-rico-GitHub-Namotion-Interceptor--claude-worktrees-bridge-cse-011ShuL5XgwWZqRmSZFhkZve/3d51e483-5513-56d7-b4a2-5748ff2f1414/scratchpad
pwsh src/HomeBlaze/HomeBlaze.Game.Tests/bin/Debug/net10.0/playwright.ps1 screenshot --browser chromium --viewport-size "2400, 1200" --wait-for-selector "canvas[data-game-models]:not([data-game-models='0'])" --wait-for-timeout 3000 "http://192.168.1.201:5280/pages/Demo/Game" "$SCRATCH/world/demo-world.png"
```

The settle time only lets the camera and the remaining models arrive for a manual look; it is not a test. A new anonymous player spawns at 0, 0, so the screenshot shows the house to the north with brick, concrete and wooden walls, the open entrance and the forest at the right edge. Open `$SCRATCH/world/demo-world.png` and check: textures on all walls, door leaves visible, trees as models (no gray boxes), nothing floating. Fix and re-run the affected task if not.

- [ ] **Step 3: Check the browser console**

Optionally rerun the screenshot command with `--save-har "$SCRATCH/world/demo-world.har"` and check in the HAR that every request under `_content/HomeBlaze.Game/objects/` returned 200.

---

## Task 10: README, working documents and PR description

**Runnable:** no (documentation only).

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze.Game/README.md`
- Delete: `src/HomeBlaze/docs/superpowers/` (the spec and this plan)
- Scratchpad: `$SCRATCH/scripts/update_readme.py`, `$SCRATCH/pr-body-876.md`

- [ ] **Step 1: Fold the spec into the README**

Create `$SCRATCH/scripts/update_readme.py` and run it with `python3 -I "$SCRATCH/scripts/update_readme.py" src/HomeBlaze/HomeBlaze.Game/README.md`. Every replacement must match exactly once; the script stops otherwise.

```python
"""Folds the game world design into the README of HomeBlaze.Game. Usage: python3 -I update_readme.py <README.md>"""
import sys

REPLACEMENTS = [
    (
        "invoke the operations of the objects standing there, such as building and demolishing walls, opening doors and sending messages.",
        "invoke the operations of the objects standing there, such as demolishing walls, adding doors to them, opening doors and sending messages, and use the tools of the game to build walls and plant trees. Object types such as walls, doors and trees use only the public API of the core: they describe their look as data and act through public methods of the game, so they can move into libraries of their own.",
    ),
    (
        "`Data/Files/Demo/Game.json` of the app holds a demo world with a house of walls and door openings, served at `/pages/Demo/Game`.",
        "`Data/Files/Demo/Game.json` of the app holds the demo world, served at `/pages/Demo/Game`: a house of six rooms with brick outer walls and concrete inner walls joined by doors 4 to 6 tiles wide, a wooden shed to the west, a forest of 32 trees to the east, a few trees around the house, and the wall and tree tools. New players spawn at 0, 0 in front of the open entrance. A script generated the file once with a fixed random seed; it is edited like any saved world.",
    ),
    (
        "`Game.Actions.cs`, `Game.Persistence.cs`, `Game.View.cs`,",
        "`Game.Actions.cs`, `Game.Objects.cs` (public object API), `Game.Placement.cs` (placement API), `Game.Persistence.cs`, `Game.View.cs`,",
    ),
    (
        "| `GameObject.cs`, `Wall.cs`, `Door.cs`, `GamePlayer.cs` | The objects on the grid |",
        "| `GameObject.cs`, `GamePlayer.cs`, `GameObjectSnapshot.cs` | The base of every object on the grid, the player, and the snapshot with which `UpdateAsync` undoes a refused change |\n"
        "| `Visuals/` | `GameVisual`, `GameVisualPart`, `GameVisualVector`, `GameVisualPoint`, `GameVisualShape`, `GameVisualEffect`, `GameVisualCache`, `GameAssets`, `GameVisualValidator` |\n"
        "| `Tools/` | `GameTool`, `GamePlacement` |\n"
        "| `Objects/Buildings/` | `Wall`, `WallMaterial`, `WallTool`, `Door`, `WallTextures` |\n"
        "| `Objects/Nature/` | `Tree`, `TreeVariant`, `TreeTool` |",
    ),
    ("`SpawnFinder`, `TargetArea`, `WallPlacement` |", "`SpawnFinder`, `TargetArea`, `FrontPlacement` |"),
    (
        "`game-entities.js` (meshes, tweens, ground, target marker)",
        "`game-entities.js` (parts, models, wind, tweens, glides, fading, ground, target marker)",
    ),
    (
        "`lib/`, `models/`, `textures/` |",
        "`lib/`, `models/`, `textures/`, `objects/buildings/textures/`, `objects/nature/models/` |",
    ),
    (
        "| | `Objects` | Configuration | `Dictionary<string, GameObject>` keyed by object name, managed by the user and by player actions |",
        "| | `Objects` | Configuration | `Dictionary<string, GameObject>` keyed by object name, managed by the user and by player actions |\n"
        "| | `Tools` | Configuration | `Dictionary<string, GameTool>` keyed by tool name, managed by the user; the game never changes it |",
    ),
    (
        "| | `Width`, `Depth`, `Height` | Virtual, not persisted | In tiles; `Height` is only used for rendering. Walls and doors override them as configuration |",
        "| | `Width`, `Depth`, `Height` | Virtual, not persisted | In tiles; `Height` is only used for rendering and validation. Walls and doors override them as configuration, trees and players fix them |\n"
        "| | `Visual` | Virtual, not persisted | A `GameVisual`, by default a gray box of the footprint and `Height`; see [Objects and extension points](#objects-and-extension-points) |",
    ),
    (
        "| `Door` | `Width`, `Depth`, `Height` | Configuration, `[Range(1, 1024)]` | Defaults 4 x 1 x 10; use 1 x 4 in a north-south wall |",
        "| | `Material` | Configuration | `Bricks` (default), `Concrete` or `Wood` |\n"
        "| `Door` | `Width`, `Depth`, `Height` | Configuration, `[Range(1, 1024)]` | Defaults 4 x 1 x 10; use 1 x 4 in a north-south wall |\n"
        "| | `Material` | Configuration | The material of the lintel, `Bricks` by default; \"Add door\" copies the wall's |",
    ),
    (
        "| | `IsOpen` | Configuration | Closed by default. `IsMovementBlocking` and `IsSoundBlocking` are `!IsOpen` |",
        "| | `IsOpen` | Configuration | Closed by default. `IsMovementBlocking` and `IsSoundBlocking` are `!IsOpen` |\n"
        "| `Tree` | `Variant`, `Rotation`, `Scale` | Configuration | 0 to 6, degrees clockwise, 80 to 120 percent; defaults 0, 0, 100. A value outside the table or range shows variant 0 or the nearest scale |\n"
        "| | `Width`, `Depth`, `Height` | Fixed | A trunk of 2 x 2 tiles; the height follows variant and scale. Blocks movement, not sound |\n"
        "| `WallTool`, `TreeTool` | none | | Tools in `Game.Tools`; their operations are listed for every player |",
    ),
    (
        "`Objects` is typed as the abstract `GameObject`, which relies on",
        "`Objects` and `Tools` are typed as the abstract `GameObject` and `GameTool`, which relies on",
    ),
    (
        "- Building, demolishing, opening and closing write immediately, outside the lock.",
        "- `PlaceAsync`, `RemoveAsync`, `UpdateAsync` and `ReplaceAsync` (building, planting, demolishing, adding doors, opening and closing) write immediately, outside the lock.",
    ),
    (
        "| `Game.BuildWallAsync` | Required | Places a default wall across the facing, right in front of and centered on the actor (`WallPlacement`), named `wall-` plus 8 random hex digits. Refuses diagonal facing and blocked tiles |\n"
        "| `Wall.DemolishAsync` | None | Removes the wall from its parent game |\n"
        "| `Door.OpenAsync`, `Door.CloseAsync` | None | `Game.SetDoorOpenAsync` under the lock. Refuses the current state, and refuses closing while another blocking object covers a door tile |",
        "| `WallTool.BuildWallAsync` | Required | Builds a wall one tile thick right in front of the actor, across the facing and centered on it (`GetPlacementInFront`, `PlaceAsync`), named `wall-` plus 8 random hex digits. Refuses diagonal facing, blocked tiles and parameters out of range |\n"
        "| `TreeTool.PlantTreeAsync` | Required | Plants a tree of the given or a random variant, with random rotation and scale, on the 2 x 2 tiles in front of the actor, named `tree-` plus 8 random hex digits |\n"
        "| `Wall.DemolishAsync`, `Tree.DemolishAsync` | None | `Game.RemoveAsync` |\n"
        "| `Wall.AddDoorAsync` | Optional | Splits the wall into wall, door, wall through `Game.ReplaceAsync`, centered on the actor along the wall, or on the wall without one |\n"
        "| `Door.OpenAsync`, `Door.CloseAsync` | None | `Game.UpdateAsync`. Refuses the current state, and refuses closing while another blocking object covers a door tile (\"Something is in the way.\") |",
    ),
    (
        "attribute (`Door.Open_IsEnabled`, `Door.Close_IsEnabled`).",
        "attribute (`Door.Open_IsEnabled`, `Door.Close_IsEnabled`, `Wall.AddDoor_IsEnabled`).",
    ),
    (
        "while a dialog is open the browser does not move the player.",
        "while a dialog is open the browser does not move the player. The page remembers the parameters it entered per operation, keyed by object type and operation, for as long as it lives, and prefills the next dialog of that operation through `initialValues` of `OperationInvoker.InvokeAsync`, which reports the entered values through `parametersEntered`.",
    ),
    (
        "`_lock` guards `Players`, `Objects`, the positions and door states of their objects,",
        "`_lock` guards `Players`, `Objects`, the positions of their objects and the states that `UpdateAsync` changes,",
    ),
    ("(move, join, build, demolish, open, close)", "(move, join, place, remove, update, replace)"),
    (
        "(a replaced `Players` or `Objects`, a changed `X`, `Y`, `Width`, `Depth`, `IsConnected` or `IsOpen` of an object of this game)",
        "(a replaced `Players` or `Objects`, or any property change of an object of this game on a thread that does not hold `_lock`, because the core knows no object type and cannot tell which properties its blocking follows)",
    ),
    (
        "Player colors derive from the client id with FNV-1a and are cached per client id; walls and doors have fixed colors.",
        "Player colors derive from the client id with FNV-1a and are cached per client id. Other objects carry their colors and textures in their visual, which the tracker compares by value; players carry no visual, because the page draws them itself.",
    ),
    (
        "- Walls are individual boxes with per-face UVs scaled to their size (one brick repeat per 8 x 4 tiles), not thin instances, so the texture keeps its size on every wall length.\n"
        "- Doors are a brick lintel from 8.4 tiles up to the full height and a wooden panel 8.4 tiles tall and 0.3 thick, hinged at the west end of an east-west opening or the south end of a north-south one. Opening swings it a quarter turn over 300 ms.\n"
        "- Walls and doors that the segment from the camera to the own player's head crosses fade to 30 % visibility.",
        "- Every object but a player is a node at the south-west corner of its footprint with one child per part of its visual. A box part is a box mesh with per-face UVs scaled to its size by `TextureScale`, so the texture keeps its size on every wall length, sharing one material per texture and color. A model part instantiates the glTF of its URL, loaded once per URL, as cloned meshes that share geometry and materials; a model that fails to load shows a gray box of the part's size, and the console logs the URL once.\n"
        "- A change of a part's offset or rotation glides over 300 ms, which swings a door leaf; any other change of a part rebuilds the object.\n"
        "- `Wind` parts use clones of their materials with a material plugin that moves each vertex sideways by the square of its height above the mesh's origin, swaying with a phase from its world position plus a slower gust; strength and speeds are constants in `game-entities.js`. `Animation` plays the named animation group of a model in a loop.\n"
        "- Movement blocking objects whose part bounds the segment from the camera to the own player's head crosses fade to 30 % visibility.\n"
        "- For tests and diagnosis the canvas carries `data-game-parts` (parts of the objects in view), `data-game-models` (model parts showing their model) and `data-game-model-failures` (model parts showing the fallback box).",
    ),
    (
        "**Looking at** (only while targets exist, actions numbered 1 to 9 across all targets), **Actions** (operations of the `Game`, disabled without a player)",
        "**Actions** (operations of the `Game`, then of every tool by name, disabled without a player), **Looking at** (only while targets exist, actions numbered 1 to 9 across all targets)",
    ),
    (
        "| `textures/` | ambientCG Grass004 (`grass.jpg`) and Bricks097 (`bricks.jpg`), re-encoded | CC0 |",
        "| `textures/` | ambientCG Grass004 (`grass.jpg`), re-encoded | CC0 |\n"
        "| `objects/buildings/textures/` | ambientCG Bricks097 (`bricks.jpg`, stacked to a square), Concrete046 (`concrete.jpg`) and Planks021 (`wood.jpg`), re-encoded to 512 x 512 | CC0 |\n"
        "| `objects/nature/models/` | Seven trees of the low poly nature pack by flo-bit, unchanged GLB files | CC0 |",
    ),
    (
        "adds connected players, walls and doors |",
        "adds connected players, walls, doors, other objects and tools |\n"
        "| `VisualObject`, `WarnedVisualObject` | Objects whose visual a test sets; the second is a type of its own for the warning that is logged once per type |\n"
        "| `RecordingLogger` | A thread-safe logger that records its entries |\n"
        "| `RepositoryPaths` | Finds repository files, such as the demo world and the tree models, from the test output |",
    ),
    (
        "- Only one character model, and only walls and doors as object types.",
        "- Only one character model.\n"
        "- A tree blocks only the 2 x 2 tiles of its trunk; its canopy passes through walls and players, and fading tests the bounds of its parts without their rotation.\n"
        "- \"Add door\" reads the wall's geometry before the game takes its lock, so a wall edited from outside in between is replaced by pieces of its former geometry.\n"
        "- Remembered dialog parameters live as long as the page; a reload starts with empty fields.",
    ),
    (
        "  - Every object is its own mesh, so thousands of walls within the view radius mean thousands of draw calls. Instances of identical meshes would batch them.",
        "  - Every box part is its own mesh and every model part one mesh per model node, so thousands of walls and trees within the view radius mean thousands of draw calls. Thin instances of identical meshes would batch them.",
    ),
]

OBJECTS_SECTION = """## Objects and extension points

The core (`Game*.cs`, `GameObject`, `GamePlayer`, `Visuals/`, `Tools/`, `World/`, `View/`, `Voice/`, `Components/`) knows no object type. Every object type lives in a folder of `Objects/` with its own namespace and uses only public and protected members of the core, so that a later split into libraries only moves files and migrates the `$type` names of saved worlds. Reviews check this rule; an object folder may have internal members of its own.

| Folder | Namespace | Types |
|---|---|---|
| `Objects/Buildings/` | `HomeBlaze.Game.Objects.Buildings` | `Wall`, `WallMaterial`, `WallTool`, `Door`, `WallTextures`; walls and doors share a folder because "Add door" creates doors |
| `Objects/Nature/` | `HomeBlaze.Game.Objects.Nature` | `Tree`, `TreeVariant`, `TreeTool` |

Moving `Wall` and `Door` into `Objects/Buildings` changed their `$type` from `HomeBlaze.Game.Wall` and `HomeBlaze.Game.Door` to `HomeBlaze.Game.Objects.Buildings.Wall` and `HomeBlaze.Game.Objects.Buildings.Door`. The loader has no rename mapping, so a world saved before needs those names replaced in its file.

Look (`Visuals/`):

- `GameObject.Visual` returns a `GameVisual`: an immutable record of up to 16 `GameVisualPart`s that compares by value, parts included. The default is a gray box filling the footprint and `Height`.
- A part is a `Box` or a `Model` with `Offset` and `Size` in tiles relative to the south-west corner of the footprint at ground level, a `Pivot` and a `Rotation` in degrees clockwise seen from above, `Texture`, `TextureScale` (tiles per repeat in both directions) and `Color` for boxes, `Model` for models, `Effect` (`None` or `Wind`) and `Animation` (a looping animation of the model). A model is scaled uniformly to the largest size whose bounding box fits in `Size`, with its origin at the horizontal center of the part's box and its lowest point at the bottom.
- `GameAssets.Url(type, path)` returns `_content/<assembly of type>/<path>`, so URLs follow their type into another library.
- An object computes its visual from configuration or state properties only, because the page observes exactly those, and caches it per state with `GameVisualCache<TKey>`, which returns the same instance for an unchanged key, so the tracker's comparison stops at the reference and allocates nothing.
- `GameViewTracker` validates a visual when it builds a view object: sizes, offsets or pivots outside -1024 to 1024 tiles, more than 16 parts, URLs that do not start with `_content/` or contain `..`, colors other than `#rrggbb`, undefined enum values or a model part without a model show the object as its default box, and the server logs a warning once per object type and process. The browser receives the parts with short property names (`GameViewPart`).

Tools (`Tools/`):

- `GameTool` is an abstract configurable subject in `Game.Tools`, saved with `$type` like objects. A tool reaches its game through the subject graph (`GetGame()`), not a constructor, and offers operations with a nullable `[FromServices] GameCaller` parameter.
- Tool parameters are nullable, so an empty dialog field means the default; `[Range]` documents the limits, which the operations check again.

Public object API on `Game`:

| Member | Behavior |
|---|---|
| `GetPlacementInFront(caller, across, along)` | Returns a `GamePlacement(X, Y, Width, Depth, Facing)` for an object `across` tiles wide across the facing direction and `along` tiles deep along it, starting on the tile right in front of the caller's player and centered on the player's center across the facing. Refuses without a player ("Only a player in the game can place objects."), when facing diagonally ("Face north, east, south or west to place objects.") and beyond the `int` range ("Something is in the way.") |
| `PlaceAsync(caller, namePrefix, gameObject, cancellationToken)` | Adds the object under the prefix, a dash and 8 random hex digits and saves; refuses without a player, and with "Something is in the way." when a movement blocking object covers one of its tiles |
| `RemoveAsync(gameObject, cancellationToken)` | Removes an object of this game and saves |
| `UpdateAsync(gameObject, change, cancellationToken)` | Runs `change` under `_lock`, updates the index and saves. When the change throws, or the object then blocks movement and overlaps another movement blocking object, `GameObjectSnapshot` restores its writable configuration and state properties through the registry, and the call throws ("Something is in the way." for the overlap). The change may only set properties of the object |
| `ReplaceAsync(gameObject, replacements, cancellationToken)` | Removes the object and adds the replacements under new names in one step under `_lock`, so no page sees an intermediate state; refuses when a movement blocking replacement overlaps a movement blocking object other than the replaced one, changing nothing |

Objects:

- `Wall`: `Material` picks the texture of its single box: bricks 2 m, concrete 2.5 m and wooden planks 1.5 m per repeat. "Add door" (`width` 2 to 8, default 4) splits a wall one tile thick into wall, door, wall, keeping material and height; the door is centered on the caller's player along the wall (on the wall without a player) and moved inward so that a wall tile remains on each side. It is disabled through `KnownAttributes.IsEnabled` below 4 tiles and refuses a width that does not fit with "This wall is too short for a door of N tiles; it fits up to M.".
- `Door`: its visual is a lintel of its `Material` above 2.1 m and a wooden leaf with its pivot at the hinge, at the west end of an east-west door or the south end of a north-south one, turned by -90 degrees while open.
- `Tree`: one wind-swayed model part from the variant table in `Tree` (model file and height per variant), on a fixed trunk of 2 x 2 tiles.
- `WallTool` ("Build wall": `length` 1 to 40, default 4; `height` 1 to 20, default 10; `material`, default bricks) builds a wall right in front of the player, across the facing and centered on it, so a length of 40 reaches about 20 tiles to each side. `TreeTool` ("Plant tree": `variant`, random when empty) plants a tree with random rotation and scale in front of the player.

"""


def main(path):
    with open(path, encoding="utf-8") as file:
        text = file.read()
    for old, new in REPLACEMENTS:
        count = text.count(old)
        if count != 1:
            raise SystemExit(f"Expected one match, found {count}: {old[:80]}")
        text = text.replace(old, new)
    anchor = "## Clients and players\n"
    if text.count(anchor) != 1:
        raise SystemExit("The Clients and players heading was not found once.")
    text = text.replace(anchor, OBJECTS_SECTION + anchor)
    with open(path, "w", encoding="utf-8", newline="\n") as file:
        file.write(text)


if __name__ == "__main__":
    main(sys.argv[1])
```

Then read the README once from top to bottom and fix anything that still describes the old behavior (for example a mention of `SetDoorOpenAsync`, `BuildWallAsync` on the game or `WallPlacement`): `grep -n "SetDoorOpenAsync\|RemoveObjectAsync\|Game.BuildWallAsync\|WallPlacement\|GameColors.Wall" src/HomeBlaze/HomeBlaze.Game/README.md` must print nothing. Keep the style rules: no em dashes, no hard wrapping, US English.

- [ ] **Step 2: Delete the working documents**

```bash
git rm -r -q src/HomeBlaze/docs/superpowers
git ls-files src/HomeBlaze/docs
```

Expected: no output from `git ls-files` (the folder held only the spec and this plan).

- [ ] **Step 3: Commit and push**

```bash
git add src/HomeBlaze/HomeBlaze.Game/README.md
git commit -m "docs: document extensible game objects, tools, trees and the demo world" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push origin feat/homeblaze-game
```

- [ ] **Step 4: Update the PR description**

Edit `$SCRATCH/pr-body-876.md` (it exists; keep everything not mentioned here):

1. Replace the paragraph starting with `In progress:` by:

   ```markdown
   Game objects are extensible: walls, doors and trees use only the public API of the game core. Each describes its look as a value-compared `GameVisual` (boxes and glTF models with offsets, pivots, rotations, textures and a wind effect), which the browser renders generically, and acts through public methods of the game (`GetPlacementInFront`, `PlaceAsync`, `RemoveAsync`, `UpdateAsync`, `ReplaceAsync`). Tools are configurable subjects in `Game.Tools`: the wall tool builds walls of a chosen length, height and material (bricks, concrete, wood), and the tree tool plants one of seven tree models that sway in the wind. Players add doors to the walls they look at, operation dialogs start from the values entered last, and a new demo world has a house of six rooms, a wooden shed and a forest.
   ```

2. Append to `## Contract`:

   ```markdown
   - Object types under `Objects/` use only public and protected members of the core, and the core names no object type, so object types can move into libraries of their own.
   - `Game.UpdateAsync` undoes a refused change by restoring the object's writable configuration and state properties through the registry; other fields of the object are not restored.
   - A visual that fails validation (sizes beyond 1024 tiles, more than 16 parts, URLs outside `_content/`) shows as a gray box, and the server warns once per object type.
   - `Game.GetPlacementInFront(caller, across, along)` centers a footprint on the player across the facing direction, right in front of it; walls of the wall tool stand across the facing as before.
   ```

3. Replace the `## Breaking changes` body by:

   ```markdown
   - The `$type` of walls and doors moved from `HomeBlaze.Game.Wall` and `HomeBlaze.Game.Door` to `HomeBlaze.Game.Objects.Buildings.Wall` and `HomeBlaze.Game.Objects.Buildings.Door`. Saved worlds need these names replaced; the loader has no mapping, which is acceptable for this unreleased library. The demo world is regenerated.
   - "Build wall" moved from `Game` to `WallTool`; a game offers it only with a wall tool in `Game.Tools`.
   - `OperationInvoker.InvokeAsync` gains the optional parameters `initialValues` and `parametersEntered`, and `MethodParameterDialog.ShowAsync` the optional `initialValues`. Callers compile unchanged; assemblies compiled against the old signatures must be recompiled.
   - `CurrentClientConnection` moves from `HomeBlaze.Host` (internal) to `HomeBlaze.Services.Clients` (public), which only adds API.
   ```

4. Replace the `## Diff composition` body by the output of `pwsh scripts/diff-composition.ps1 -Range "origin/master...HEAD"` (run after `git fetch origin master`), followed by the line `The working spec and plan were added and deleted within the branch, so they do not show up.`

5. In `## Verification`, tick `Documentation updated`, `Unit tests` and `Integration tests` with the passed counts from Task 9, keep the struck-through entries, and replace the closing paragraph by:

   ```markdown
   Voice was tested manually on iPhone Safari against a test talker (range, walls, doors, clicks). The game world was checked on the demo page and on a Playwright screenshot of the house and the forest. Still pending: a voice test with two real devices, including the echo check without headphones.
   ```

Then check the body for AI attribution and em dashes, and apply it:

```bash
SCRATCH=/tmp/claude-1000/-home-rico-GitHub-Namotion-Interceptor--claude-worktrees-bridge-cse-011ShuL5XgwWZqRmSZFhkZve/3d51e483-5513-56d7-b4a2-5748ff2f1414/scratchpad
grep -n "Claude\|Generated with\|Co-Authored-By\|—" "$SCRATCH/pr-body-876.md"
gh pr edit 876 --body-file "$SCRATCH/pr-body-876.md"
```

Expected: `grep` prints nothing; `gh` prints the PR URL.

---

## Self-review checklist (for the controller after the last task)

- Spec goals: doors added to walls (Task 6), trees with random variant, rotation and scale (Task 7), walls with length, height and material (Task 4), wind (Task 2 plugin, Task 7 effect), remembered parameters (Task 5), visuals and tools as public extension points used by walls, doors and trees (Tasks 1 to 4, 7), new demo world (Task 8).
- Spec structure: `Visuals/` and `Tools/` namespaces, `Objects/Buildings` and `Objects/Nature`, assets per object folder with `LICENSE.md`, grass and character unchanged, README states the folder rule and the `$type` change (Task 10).
- Spec tests: visual equality and transport (Task 1), validation fallback and warning (Task 1), `GameAssets.Url` (Task 1), door and wall visuals (Tasks 2 and 4), tree rules (Task 7), add door cases (Task 6), wall and tree tools (Tasks 4 and 7), remove, update and replace (Task 3), persistence of materials, trees and tools (Tasks 4 and 7), demo world reachability and doors (Task 8), dialog prefill (Task 5), E2E for build wall with prefill (Task 5), plant tree, movement and voice (Task 7), door open and close (Task 2), screenshot (Task 9).
