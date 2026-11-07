# HomeBlaze.Game.Abstractions

The extension contract of the HomeBlaze game: everything a library needs to add object types and tools to a game, and nothing more. This README is the author guide; the internals of the game live in the [HomeBlaze.Game README](../HomeBlaze.Game/README.md).

The walls, doors and trees of the game are built on this contract alone: `HomeBlaze.Game.Buildings` (`Wall`, `Door`, `WallTool`) and `HomeBlaze.Game.Nature` (`Tree`, `TreeTool`, `Animal`) reference this library and never `HomeBlaze.Game`. Read them next to this guide; `Animal` is the sample of schedules, configuration driven behavior and animations.

## Game objects and tools

A game object is an `[InterceptorSubject]` that derives from `GameObject`. It stands on a grid of 25 cm tiles, north is +Y and east is +X, and covers the tiles from `X` to `X + Width - 1` and from `Y` to `Y + Depth - 1`. The game keeps it in the region of its south-west corner, one of the 64 by 64 tile groups that `Game.Regions` holds the objects in (see the [core README](../HomeBlaze.Game/README.md#model)), and saves it with its `$type`, so the full name of the class is part of every saved world: renaming the class or its namespace breaks the worlds that hold it.

| Member of `GameObject` | Contract |
|---|---|
| `X`, `Y` | `[Configuration]`, the south-west corner of the area |
| `Width`, `Depth`, `Height` | Virtual, 1 by default, in tiles from 1 to `MaximumSize` (1024). A type with a configurable size overrides them as `[Configuration]` partial properties; a type with a computed size overrides the getters |
| `IsMovementBlocking` | Virtual, `true` by default: no other movement blocking object may enter the area |
| `IsSoundBlocking` | Virtual, `false` by default: whether voices pass through the area. A plant or a chair blocks movement, not sound; a wall or a closed door blocks both |
| `IsVisible` | Virtual, `true` by default |
| `Visual` | Virtual, how the object looks; `DefaultVisual`, a gray box of the area and `Height`, by default. See [Visuals](#visuals) |
| `Area` | `new GameArea(X, Y, Width, Depth)` |
| `World` | Protected, the game the object is part of, as `IGameWorld`; throws `InvalidOperationException` while it is not part of one |
| `TryGetWorld(out world)` | Protected, the game without throwing; false while the object is being loaded or once its removal was published |
| `OnAddedToWorld()` | Protected virtual, called on the game's own thread when the object enters the world. See [The four rules](#the-four-rules) |

A tool is an `[InterceptorSubject]` that derives from `GameTool`. The game lists the operations of the tools in `Game.Tools` for every player, which is how players build, plant and emote. A tool reaches its game through the protected `World` like an object; the game itself reads nothing of a tool but its operations.

Who acts: an operation that needs the acting player takes a `[FromServices] GameCaller? caller` parameter. Only the game page provides one, with the client id of its browser; the subject page, MCP and agents pass none. `World.GetSpotInFront(caller, across, along)` turns the caller into a spot in front of its player and refuses a caller without a player in the game.

Refusals throw `InvalidOperationException` with a message for the player, which the error dialog of the page shows. `GameMessages.Blocked` ("Something is in the way.") is the text the game uses itself, so an object refuses with the same words. Invalid arguments throw `ArgumentException`. Parameters with a default are nullable, so an empty dialog field means the default both in the dialog and over MCP; `[Range]` documents the limits, which the dialog does not enforce, so the operation checks them again.

## The four rules

1. **Describe the object with properties.** `[Configuration]` and `[State]` properties plus overrides of the size, the blocking flags, `IsVisible` and `Visual`. The game reads only these.
2. **Change the object by writing its properties**, from its operations, the subject page, MCP or anywhere else. A write lands right away, never waits for the game and is never refused by it: the game reads the object again at its next tick, or still in the current tick when the write happens on the game's thread. Where a rule matters, the object checks it before the write, typically with `World.IsFree(area, except: this)`, and throws `InvalidOperationException(GameMessages.Blocked)`.
3. **Everything beyond the object's own properties goes through `World`**: `GetSpotInFront`, `PlayAnimation`, `IsFree`, `PlaceAsync`, `RemoveAsync`, `ReplaceAsync` and `Schedule`.
4. **Time-driven behavior is set up in `OnAddedToWorld` with `World.Schedule`.** The game calls `OnAddedToWorld` on its own thread when the object enters the world: on start, restart and load, and when the object is placed, added by a replacement or added by an outside write of the objects. Schedules are not saved, so this is where they come back after a restart.

What follows from them:

- The game never refuses a write, so it never checks one either. A door that an automation closes while a player stands in it closes; the player stays inside and can always step out, because a step is only checked against the blocking objects that do not already overlap the player.
- `IsFree` is callable from any thread. On the game's thread it includes what earlier work of the tick wrote; elsewhere it answers from the state of the last tick, so a player may step into the area between the check and the write.
- `OnAddedToWorld` can run again for the same instance, after a restart, an outside replacement of the objects or when the object is removed and placed again, and nothing is called on removal. Set up only schedules there, which removal and a stop cancel, or keep it idempotent.
- A placed object's `OnAddedToWorld` runs once the game added it to its objects: in the tick that placed it, or once a save in progress completed. Awaiting `PlaceAsync` or `ReplaceAsync` from another thread, as an operation does, returns with the object attached, so its `World` resolves, and its `OnAddedToWorld` ran unless the game stopped meanwhile. Called on the game's thread, from a scheduled action or `OnAddedToWorld`, the command runs at once and the object joins later in that tick or in the next.
- `Schedule(gameObject, delay, action)` runs a synchronous action on the game's thread at the first tick at or after the delay. Disposing the returned handle cancels it, from any thread, and removing the object cancels all its schedules. Pass a synchronous action, not an async lambda, whose continuations would leave the game's thread. An exception it throws is logged and the game goes on.
- Property change hooks (`partial void OnIsOpenChanged`) run inside the write, on the writer's thread, which can be any thread, also while the object is loaded and not yet part of a game. Keep them short and thread-safe, and reach the game with `TryGetWorld`, not `World`, which would throw during a load.
- Placing and replacing name new objects after their type, in lowercase, a dash and 8 random hex digits, such as `door-1a2b3c4d`. Canceling the token before the game processed the command drops it; after that the change stays applied. The one exception is a stop that timed out while a save held back the write of the objects, which may drop a placed object or replacement again.
- `PlaceAsync`, `RemoveAsync` and `ReplaceAsync` fail with `GameStoppedException` ("The game has stopped.") when the game stopped before the command completed. It derives from `InvalidOperationException`, so an operation that awaits them passes it on as a refusal.

| Member of `IGameWorld` | Contract |
|---|---|
| `GetSpotInFront(caller, across, along)` | The `GameSpot` (`X`, `Y`, `Width`, `Depth`, `Facing`, `Area`) of an object `across` tiles wide across the facing and `along` tiles deep along it, on the tiles right in front of the caller's player and centered on it across the facing (an odd remainder goes east or north). Does not check whether the area is free. Refuses a caller without a connected player of the game ("Only a player in the game can place objects."), a diagonal facing ("Face north, east, south or west to place objects.") and a spot that would leave the `int` range ("There is no room in front of you.") |
| `PlayAnimation(caller, animation, mode)` | Plays an animation of the person on the caller's player. See [Emotes](#emotes). Refuses a caller without a connected player of the game ("Only a player in the game can play animations."), an empty name, `Idle` and `Walk` (`ArgumentException`) and an undefined mode |
| `IsFree(area, except)` | Whether no movement blocking object other than `except` overlaps the area |
| `PlaceAsync(gameObject, cancellationToken)` | Adds the object under a new name; refused with `GameMessages.Blocked` when it blocks movement and a movement blocking object overlaps it |
| `RemoveAsync(gameObject, cancellationToken)` | Removes the object |
| `ReplaceAsync(gameObject, replacements, cancellationToken)` | Removes the object and adds the replacements in one step, so no player sees an intermediate state; refused, changing nothing, when a movement blocking replacement overlaps a movement blocking object other than the replaced one or another replacement |
| `Schedule(gameObject, delay, action)` | See above |

`RemoveAsync`, `ReplaceAsync` (for the replaced object) and `Schedule` refuse an object that is not part of this game with "The object is not part of this game."; `IsFree` ignores an `except` that is not part of it, and `PlaceAsync` refuses an object that already is with `ArgumentException`. The XML docs of `IGameWorld` hold the full contract.

## A complete door

The `Door` of `HomeBlaze.Game.Buildings`, trimmed to its essence: the lintel is a plain colored box here, the real one takes the wall's texture. It shows the pattern for behavior that follows a property:

- The property change hook, `OnIsOpenChanged`, drives the behavior, whoever writes `IsOpen`: the door's own operation, the subject page, an automation or a scheduled action. It reaches the game with `TryGetWorld`, because a door loaded open writes `IsOpen` before it is part of a game.
- `OnAddedToWorld` restores the behavior after a load or restart, when no write happens.
- The operations only validate and write.

```csharp
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Game.Abstractions;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace MyCompany.Game;

[Category("Game")]
[Description("A door that fills an opening in a wall and blocks it while it is closed")]
[DisplayName("Door")]
[InterceptorSubject]
public partial class Door : GameObject, ITitleProvider
{
    private const double LeafHeight = 8.4;
    private const double LeafThickness = 0.3;

    private readonly GameVisualCache<(int Width, int Depth, int Height, bool IsOpen)> _visual = new();

    // The latest close schedule; replacing or clearing it disposes the previous one, so at most one is live.
    private IDisposable? _pendingClose;

    [Configuration]
    [Range(1, MaximumSize)]
    public override partial int Width { get; set; }

    [Configuration]
    [Range(1, MaximumSize)]
    public override partial int Depth { get; set; }

    [Configuration]
    [Range(1, MaximumSize)]
    public override partial int Height { get; set; }

    [Configuration]
    public partial bool IsOpen { get; set; }

    /// <summary>
    /// The time after which an opened door closes itself; zero keeps it open.
    /// </summary>
    [Configuration]
    public partial TimeSpan AutoCloseDelay { get; set; }

    public string? Title => "Door";

    public override bool IsMovementBlocking => !IsOpen;

    public override bool IsSoundBlocking => !IsOpen;

    public override GameVisual Visual => _visual.Get((Width, Depth, Height, IsOpen),
        static state => CreateVisual(state.Width, state.Depth, state.Height, state.IsOpen));

    [Derived]
    [PropertyAttribute("Open", KnownAttributes.IsEnabled)]
    public bool Open_IsEnabled => !IsOpen;

    [Derived]
    [PropertyAttribute("Close", KnownAttributes.IsEnabled)]
    public bool Close_IsEnabled => IsOpen;

    public Door()
    {
        Width = 4;
        Depth = 1;
        Height = 10;
        IsOpen = false;
        AutoCloseDelay = TimeSpan.Zero;
    }

    partial void OnIsOpenChanged(bool newValue)
    {
        if (newValue)
        {
            if (TryGetWorld(out var world))
            {
                ScheduleAutoClose(world);
            }

            return;
        }

        // The handle is read before IsOpen: an opening on another thread after this close stores a new handle, and only
        // the handle of this close may be cleared, never the new one.
        var pending = Volatile.Read(ref _pendingClose);
        if (!IsOpen && pending is not null && Interlocked.CompareExchange(ref _pendingClose, null, pending) == pending)
        {
            pending.Dispose();
        }
    }

    protected override void OnAddedToWorld()
    {
        if (IsOpen)
        {
            ScheduleAutoClose(World);
        }
    }

    [Operation(Title = "Open", Icon = "MeetingRoom", Position = 1, Description = "Opens this door, so players can walk through it.")]
    public void Open()
    {
        if (IsOpen)
        {
            throw new InvalidOperationException("The door is already open.");
        }

        IsOpen = true;
    }

    [Operation(Title = "Close", Icon = "DoorFront", Position = 2, Description = "Closes this door, unless something stands in it.")]
    public void Close()
    {
        if (!IsOpen)
        {
            throw new InvalidOperationException("The door is already closed.");
        }

        if (!World.IsFree(Area, this))
        {
            throw new InvalidOperationException(GameMessages.Blocked);
        }

        IsOpen = false;
    }

    private void ScheduleAutoClose(IGameWorld world)
    {
        if (AutoCloseDelay <= TimeSpan.Zero)
        {
            return;
        }

        var schedule = world.Schedule(this, AutoCloseDelay, CloseOrTryAgain);
        Interlocked.Exchange(ref _pendingClose, schedule)?.Dispose();
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
            ScheduleAutoClose(World);
        }
    }

    private static GameVisual CreateVisual(int width, int depth, int height, bool isOpen)
    {
        var leafHeight = Math.Min(LeafHeight, height);
        var isAlongX = width >= depth;
        var leaf = new GameVisualPart
        {
            Shape = GameVisualShape.Box,
            Offset = isAlongX ? new GameVisualVector(0, 0, (depth - LeafThickness) / 2) : new GameVisualVector((width - LeafThickness) / 2, 0, 0),
            Size = isAlongX ? new GameVisualVector(width, leafHeight, LeafThickness) : new GameVisualVector(LeafThickness, leafHeight, depth),
            Pivot = isAlongX ? new GameVisualPoint(0, depth / 2.0) : new GameVisualPoint(width / 2.0, 0),
            Rotation = isOpen ? -90 : 0,
            Color = "#8b5a2b"
        };

        if (height - leafHeight < 0.01)
        {
            return new GameVisual([leaf]);
        }

        var lintel = new GameVisualPart
        {
            Shape = GameVisualShape.Box,
            Offset = new GameVisualVector(0, leafHeight, 0),
            Size = new GameVisualVector(width, height - leafHeight, depth),
            Color = "#9a9a9a"
        };
        return new GameVisual([lintel, leaf]);
    }
}
```

`Open_IsEnabled` and `Close_IsEnabled` disable the operations that do not apply; the game page leaves disabled operations out, so the number keys only reach operations that apply.

## A complete tool

The `TreeTool` of `HomeBlaze.Game.Nature`. It creates the object, asks the game for the spot in front of the caller's player, and places it; the game refuses a caller without a player, a diagonal facing and a blocked spot.

```csharp
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Game.Abstractions;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Game.Nature;

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

    [Operation(Title = "Plant tree", Icon = "Park", Position = 1,
        Description = "Plants a tree in front of you. An empty variant picks one at random.")]
    public Task PlantTreeAsync([Range(0, Tree.VariantCount - 1)] int? variant, [FromServices] GameCaller? caller, CancellationToken cancellationToken)
    {
        if (variant is < 0 or >= Tree.VariantCount)
        {
            throw new ArgumentOutOfRangeException(nameof(variant), variant, $"The variant is from 0 to {Tree.VariantCount - 1}.");
        }

        var tree = new Tree
        {
            Variant = variant ?? _random.Next(Tree.VariantCount),
            Rotation = _random.Next(360),
            Scale = _random.Next(Tree.MinimumScale, Tree.MaximumScale + 1)
        };
        var spot = World.GetSpotInFront(caller, across: tree.Width, along: tree.Depth);
        tree.X = spot.X;
        tree.Y = spot.Y;
        return World.PlaceAsync(tree, cancellationToken);
    }
}
```

`Wall.AddDoorAsync` shows `ReplaceAsync`: it replaces a wall by a wall, a door and a wall in one step, and works without a caller too, centering the door on the wall then.

## Emotes

`World.PlayAnimation(caller, animation, mode)` makes the caller's player play an animation of its person, which everyone who sees the player sees. An emote is a tool operation that calls it; the `EmoteTool` of the core is the example, and another emote is another tool of the same shape. The game owns the players, so this is how an extension reaches them.

```csharp
[Category("Game")]
[Description("Lets players clap, sit and jump")]
[DisplayName("Emote tool")]
[SubjectIcon("SignLanguage")]
[InterceptorSubject]
public partial class EmoteTool : GameTool, ITitleProvider, IIconProvider
{
    public string? Title => "Emote tool";

    public string? IconName => "SignLanguage";

    [Operation(Title = "Clap", Icon = "SignLanguage", Position = 1, Description = "Claps your hands once.")]
    public void Clap([FromServices] GameCaller? caller) =>
        World.PlayAnimation(caller, "Clapping", GameVisualAnimationMode.Once);

    [Operation(Title = "Sit", Icon = "Chair", Position = 2, Description = "Sits down until you walk on.")]
    public void Sit([FromServices] GameCaller? caller) =>
        World.PlayAnimation(caller, "Sitting", GameVisualAnimationMode.Loop);

    [Operation(Title = "Jump", Icon = "Height", Position = 3, Description = "Jumps once.")]
    public void Jump([FromServices] GameCaller? caller) =>
        World.PlayAnimation(caller, "Jump", GameVisualAnimationMode.Once);
}
```

- **`animation`** is the name of a clip in the player's model. The six people of the core have `Clapping`, `Sitting` and `Jump` besides `Idle` and `Walk`, which the game plays itself; those two and an empty name throw `ArgumentException`, and an undefined `mode` throws `ArgumentOutOfRangeException`. A person without the named clip shows nothing, and the browser warns once in its console.
- **`mode`**: `Once` plays the clip to its end and returns to idle; `Loop` repeats it until the player's next step. A step that moves the player or a teleport ends either mode, a new call replaces a running animation, and the same call twice in a row plays twice. The game clears a `Once` animation after 5 seconds at the latest, so a clip longer than that is cut short on the server.
- **Who**: like `GetSpotInFront`, it needs a caller with a connected player, so only the player's own page can make it animate. From the subject browser, MCP and agents the caller is null and the call throws `InvalidOperationException`. It may be called from any thread; the animation starts in the game's next tick.
- **State**: `GamePlayer.Animation` shows the playing animation, or null. Only the game writes it, outside writes are refused, and it is not saved. An object does not write it; it calls `PlayAnimation`.
- Each operation takes only the caller, so a click or a number key plays it without a dialog.

## Visuals

`Visual` returns a `GameVisual`, an immutable record of up to 16 `GameVisualPart`s (`GameVisual.MaximumParts`) that compares by value, parts included. `GameVisual.Box(width, height, depth, color)` creates a visual of one box.

- **Shape**: `GameVisualShape.Box` draws a box, `GameVisualShape.Model` a glTF model from `Model`. A model is scaled uniformly to the largest size whose bounding box fits in `Size`, with its origin at the horizontal center of the part's box and its lowest point at the box's bottom.
- **Offset and size**: `GameVisualVector`s in tiles relative to the south-west corner of the object's area at ground level; X grows to the east, Y upward and Z to the north. As a size, X is the width, Y the height and Z the depth. A part may reach beyond the area, as the head of a deer does; only the area blocks.
- **Pivot and rotation**: `Rotation` turns the part around the vertical axis through `Pivot` (a `GameVisualPoint` of X and Z in tiles), in degrees clockwise seen from above. The door leaf has its pivot at the hinge, and `GameDirection.GetRotation()` turns a facing into degrees.
- **Textures and tints**: a box takes a `Texture` URL, repeated every `TextureScale` tiles horizontally and vertically, so a texture keeps its size on every wall length, and a `Color` as `#rrggbb`, which tints the texture or colors a box without one.
- **Wind**: `Effect = GameVisualEffect.Wind` bends the part sideways in proportion to the square of its height above the object's base, so the top sways and the base stays put, as wind moves a tree.
- **Animations**: `Animation` names an animation of the model. `AnimationMode = GameVisualAnimationMode.Loop` (the default) plays it in a loop; `Once` plays it once whenever the part's animation changes and holds its last frame. `Animal` loops `Walk` or `Idle` and plays `Eating` once.
- **Glides**: a change of a part's offset or rotation glides over 300 ms in the browser, which swings a door leaf or turns an animal; any other change of a part, or of the number of parts, rebuilds the object.

Compute the visual from configuration or state properties only, because the game reads it again exactly when one of the object's properties was written, and cache it per state with `GameVisualCache<TKey>`. The cache returns the same instance for an unchanged key, so the game's comparison stops at the reference and allocates nothing; pass a static lambda, as the door does, so the call allocates nothing either.

The game validates every visual before it sends it to a browser. An object whose visual has more than 16 parts, an offset, size or pivot outside -1024 to 1024 tiles (`GameVisual.MaximumExtent`), a negative size, a rotation that is not finite, an undefined enum value, a texture scale outside 0 (exclusive) to 1024, a color other than `#rrggbb`, an animation name over 64 characters, a model part without a model, or a URL that does not start with `_content/`, is longer than 512 characters or contains `..` or `%`, shows as its gray default box, and the server logs a warning once per object type.

## Look

The core draws the outlines, the shadows and the corner shading of every box and model of every object, so an object needs nothing for them; [Look and rendering](../HomeBlaze.Game/README.md#look-and-rendering) describes them. What the object supplies is shape and surface:

- Use flat pastel colors, or textures that are drawn, not photographed: a pastel fill with faint darker lines, as `HomeBlaze.Game.Buildings/wwwroot/textures/` has for bricks, planks and concrete. The core's own drawing script is `HomeBlaze.Game/tools/make-textures.js`.
- Avoid photo textures.
- Prefer low poly models with vertex colors or flat colored materials, like the trees and animals. Keep the shape simple enough that an outline reads well.
- Fading for the sightline needs nothing from the object either.

## Assets and licenses

Ship textures and models in the `wwwroot/` folder of the library that uses them. The library is then a Razor class library (`Microsoft.NET.Sdk.Razor`), whose static web assets the host serves at `_content/<assembly name>/`. `GameAssets.Url(typeof(MyObject), "models/chair.glb")` builds that URL from the assembly of the type, so it follows the type when it moves to another library:

```csharp
private static readonly string BricksUrl = GameAssets.Url(typeof(WallTextures), "textures/bricks.png");
```

Every external asset needs a free license that allows redistribution, CC0 preferably, and a `LICENSE.md` next to it that names its source and terms and lists every file of the folder, as `HomeBlaze.Game.Nature/wwwroot/models/LICENSE.md` does.

## Packaging as a plugin

An object library is a Razor class library that references `HomeBlaze.Abstractions` and `HomeBlaze.Game.Abstractions`, plus the Namotion.Interceptor source generator as an analyzer, and `Namotion.Interceptor.Registry` when it uses `[PropertyAttribute]`. It never references `HomeBlaze.Game`. The project file of `HomeBlaze.Game.Buildings`, trimmed to its references:

```xml
<Project Sdk="Microsoft.NET.Sdk.Razor">

    <ItemGroup>
        <ProjectReference Include="..\HomeBlaze.Abstractions\HomeBlaze.Abstractions.csproj" />
        <ProjectReference Include="..\HomeBlaze.Game.Abstractions\HomeBlaze.Game.Abstractions.csproj" />
        <ProjectReference Include="..\..\Namotion.Interceptor.Generator\Namotion.Interceptor.Generator.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
        <ProjectReference Include="..\..\Namotion.Interceptor\Namotion.Interceptor.csproj" />
        <ProjectReference Include="..\..\Namotion.Interceptor.Registry\Namotion.Interceptor.Registry.csproj" />
    </ItemGroup>

</Project>
```

The host registers the assembly, so its types show in the subject browser and load from saved worlds: a library the app references at build time with `typeProvider.AddAssembly(typeof(MyObject).Assembly)` in `Program.cs`, a plugin loaded at runtime by the host itself, which registers the assemblies that the plugin loader loaded (`Program.cs` calls `typeProvider.AddAssembly` for each after `LoadPluginsAsync`). The host references `HomeBlaze.Game.Abstractions` like `HomeBlaze.Abstractions`, so the plugin loader shares it with plugins and their objects bind to the same `GameObject` and `IGameWorld` types as the game.

The static web assets of a plugin loaded at runtime are not served yet, so its textures and models do not load and a model part shows as a gray box; serving them is a separate change to the host.

## Testing

Test an object against a real game rather than a mock: the rules above, such as a hook that schedules or a check with `IsFree`, only show their effect in a running simulation. The tests of `HomeBlaze.Game.Buildings` and `HomeBlaze.Game.Nature` do it with `GameTestContext`, a game in a root subject with a fake clock (`FakeTimeProvider`) and fake clients, whose simulation ticks on the test thread, so a test advances the clock and runs ticks instead of waiting:

```csharp
private readonly GameTestContext _context = new();

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
```

| Member of `GameTestContext` | Purpose |
|---|---|
| `Tick(count)` | Advances the clock by one tick interval and runs a tick, per count |
| `RunAsync(task)` | Runs ticks until a world command such as `PlaceAsync` completed |
| `Publish()` | Runs ticks until the players' state is written to their subjects |
| `AddObject(name, gameObject)`, `AddTool(name, tool)` | Adds an object as a load or an outside edit does, which calls its `OnAddedToWorld`, or a tool |
| `AddConnectedPlayer(clientId, x, y)` | Adds a connected player, whose client id an operation receives as `new GameCaller(clientId)` |
| `StepAsync(clientId, direction)` | Moves a player one step |
| `SaveAsync()` | Runs ticks until a requested save was written |
| `HoldSave()`, `ReleaseSaveAsync(blocker)` | Holds a requested save, which pauses publishing, and lets it write |
| `BuildVolumeTable()` | Runs ticks until a change is in the volume table |
| `Reconcile()` | Reconciles the players with the clients and publishes the result |
| `OpenView(clientId, sink)` | Opens a view session and runs the tick that delivers its first update |
| `AddWall(...)`, `AddDoor(...)` | Adds a wall or a door of `HomeBlaze.Game.Buildings` |

A test project references `HomeBlaze.Game` besides the object library, and `HomeBlaze.Game.Buildings` when it links `GameTestContext`, whose `AddWall` and `AddDoor` create its types. It links the helpers it uses from `HomeBlaze.Game.Tests/Testing/`, and needs an `InternalsVisibleTo` entry in `HomeBlaze.Game`, because the helpers use its internals; see `HomeBlaze.Game.Nature.Tests.csproj`. This works only inside this repository. A public `HomeBlaze.Game.Testing` library with a public `GameTestContext`, which replaces the linked files and the `InternalsVisibleTo` entries, is a planned follow-up.
