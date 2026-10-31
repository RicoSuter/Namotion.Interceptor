# Game World Design

Adds trees with wind, walls with a chosen size and material, and a new demo world with a larger house and a small forest to `HomeBlaze.Game`. The design makes game objects extensible: an object type, its actions and its look use only the public API of the game core, so object types can later move into their own libraries.

This document is a working spec for the game pull request (#876); the voice branch merges it. Before the pull request is ready, its content moves into `src/HomeBlaze/HomeBlaze.Game/README.md` and the file is deleted.

## Goals

- Players plant trees (one of several models, chosen at random unless the player picks one) and build walls with a chosen length, height and material (bricks, concrete, wood).
- Trees sway in the wind.
- The parameter dialog of an action is prefilled with the values the player used last on that page.
- Every object type, its building tool and its look are defined through public extension points of the core: a data-driven visual description (`GameVisual`) and configurable tools (`GameTool`). Walls, doors and trees are built that way.
- A new demo world with a house of large rooms and a small forest replaces the current one.

## Non-goals

- Separate libraries for object types. The code is structured in folders and namespaces so that a later split only moves files and migrates the `$type` names in saved worlds.
- A type rename mapping in the loader. Moving types is a breaking change of saved worlds, which is acceptable for this unreleased library.
- Sending only the objects the camera covers (already a follow-up in the README).

## Structure

- Core, namespace `HomeBlaze.Game` with the folders `World/`, `View/`, `Voice/`, and the new `Visuals/` (namespace `HomeBlaze.Game.Visuals`) and `Tools/` (namespace `HomeBlaze.Game.Tools`): `Game`, `GameObject`, `GamePlayer`, `GameVisual`, `GameVisualPart`, `GameAssets`, `GameTool`, and the placement API.
- Objects, each in its own folder and namespace, using only public members of the core:
  - `Objects/Walls/` (`HomeBlaze.Game.Objects.Walls`): `Wall`, `WallMaterial`, `WallTool`.
  - `Objects/Doors/` (`HomeBlaze.Game.Objects.Doors`): `Door`.
  - `Objects/Nature/` (`HomeBlaze.Game.Objects.Nature`): `Tree`, `TreeTool`.
- Assets per object folder: `wwwroot/objects/walls/textures/` (bricks, moved from `wwwroot/textures/`, concrete, wood) and `wwwroot/objects/nature/models/` (tree models), each with a `LICENSE.md`. The grass texture and the character stay where they are.
- The README states the rule that object folders use no internal members of the core; reviews check it.

Moving `Wall` and `Door` changes their `$type` to `HomeBlaze.Game.Objects.Walls.Wall` and `HomeBlaze.Game.Objects.Doors.Door`. The demo world is rewritten with the new names, and the README notes that worlds saved before need them.

## Visual description

`GameObject` gets `public virtual GameVisual Visual`, which describes how the object looks. The default is a single gray box filling the footprint and `Height`.

- `GameVisual` is an immutable record holding an `ImmutableArray<GameVisualPart>` and compares by value, including its parts.
- `GameVisualPart` is an immutable record with:
  - `Shape`: `Box` or `Model`.
  - `Offset` (x, y, z) and `Size` (width, height, depth) in tiles, relative to the south-west corner of the footprint at ground level. For a `Model`, `Size` scales the model's bounding box to fit.
  - `Pivot` (x, z) and `Rotation` (degrees around the vertical axis), applied around the pivot.
  - `Texture` (asset URL, `Box` only), `TextureScale` (tiles per texture repeat), `Color` (tint, `#rrggbb`), `Model` (asset URL, `Model` only).
  - `Effect`: `None` or `Wind`.
  - `Animation`: the name of a looping animation contained in the model, or null.
- `GameAssets.Url(Type type, string path)` returns `_content/<assembly name of type>/<path>`, so an object's URLs follow its assembly after a split.
- Objects compute `Visual` from their state; a change of a state property that `Visual` depends on must be a property the page already observes (configuration or state properties of the object), so that the next view diff carries the new description. Objects should cache the description per state to avoid allocations on unchanged state.
- Validation when the view is built: sizes and offsets outside -1024 to 1024 tiles, more than 16 parts, and URLs that do not start with `_content/` make the object fall back to the default box, and the server logs a warning once per object type.

`GameViewObject` carries the `Visual`; `GameViewTracker` compares it by value, so an unchanged description is not sent again. The JSON sent to the browser describes each part with short property names.

## Rendering

`game-entities.js` renders every object except players from its `Visual`:

- Each object is a node positioned at its footprint (floating origin as today) with one child per part.
- `Box`: a box mesh with a material per texture, color and texture scale, cached and shared.
- `Model`: the glTF at the URL is loaded once per URL (cached promise); each part is an instance of its meshes, scaled to `Size`, positioned at `Offset`, rotated around `Pivot`. A model that fails to load is replaced by a gray box of the part's size, and the console logs the URL once.
- Changes of a part's offset or rotation glide over 300 ms (constant); other changes apply at once.
- `Wind`: a material plugin bends vertices sideways in proportion to their height above the part's base, with a phase from the object's world position and a slower gust term. Strength and speeds are constants in the module.
- `Animation`: plays the named animation group of the model in a loop.
- Fading of objects between the camera and the own player works for every object that blocks movement, as today for walls.
- Players keep their own rendering (character, prediction, camera, label). The special rendering of walls and doors is removed.

## Tools and placement

- `GameTool` is an abstract `IConfigurable` `[InterceptorSubject]` class. `Game` gets `[Configuration] Dictionary<string, GameTool> Tools`, saved with `$type` like objects. Tools are children of the game and receive the game through the subject graph (the parent), not through a constructor.
- A tool exposes `[Operation]` methods with a `[FromServices] GameCaller? caller` parameter and its own parameters. The game page lists, in this order, the operations of the game, of every tool, and of the object the player looks at.
- Public placement API on `Game`:
  - `GamePlacement GetPlacementInFront(GameCaller caller, int length, int depth)` returns where an object of `length` tiles along the facing direction and `depth` tiles across it fits in front of the caller's player when it faces north, east, south or west: a public record `GamePlacement(int X, int Y, int Width, int Depth, GameDirection Facing)` with the footprint's south-west corner and its size on the grid. It fails with the existing messages ("Only a player in the game can …", "Face north, east, south or west to …"). Public API exposes no internal types such as `Footprint`.
  - `void Place(GameCaller caller, string namePrefix, GameObject gameObject)` adds the object under a new unique name, refusing with "Something is in the way." when its footprint is blocked for movement-blocking objects, under the game's lock.
  - Both resolve the caller as the operations do today and throw `InvalidOperationException` with a message for the player.
- `WallTool` ("Build wall"): parameters `length` (1 to 40, default 4), `height` (1 to 20, default 10), `material` (`WallMaterial`, default `Bricks`), validated with `[Range]` and again on the server. The wall is `length` tiles long in the facing direction and 1 tile thick.
- `TreeTool` ("Plant tree"): parameter `variant` (nullable; null picks a random variant). Rotation (0 to 359) and scale (80 to 120 percent) are random. The tree's footprint is 2 x 2 tiles in front of the player.
- "Demolish" stays an operation of the game and works on every object.
- The current "Build wall" operation of `Game` moves to `WallTool`.

## Objects

- `Wall`: `[Configuration] WallMaterial Material` (`Bricks`, `Concrete`, `Wood`, default `Bricks`), configurable `Width`, `Depth`, `Height` as today, `IsMovementBlocking` true. `Visual`: one box with the material's texture.
- `Door`: behavior as today (`IsOpen`, the "Open" and "Close" operations, refused while the doorway is occupied). `Visual`: a brick lintel above the opening and a wooden leaf with its pivot at the hinge, rotated 0 or 90 degrees by `IsOpen`.
- `Tree`: `[Configuration] int Variant`, `int Rotation`, `int Scale` (percent); fixed footprint 2 x 2 like players (sizes not configurable), `IsMovementBlocking` true. On the voice branch, which adds `IsSoundBlocking`, trees keep its default `false`. `Visual`: one `Model` part with the variant's model, rotation and scale, effect `Wind`, height from the variant table. A variant outside the table falls back to variant 0.
- Tree models: 5 to 8 CC0 models (Quaternius or Kenney nature packs), each a small `.glb`, with a table of model file and height per variant in `Tree`.

## Remembered parameters

- `MethodParameterDialog.ShowAsync` and `OperationInvoker.InvokeAsync` in `HomeBlaze.Components` get an optional `IReadOnlyDictionary<string, object?>? initialValues`, keyed by parameter name, that prefills the fields. Existing callers are unchanged.
- The game page keeps the last parameter values per operation (keyed by the method) for as long as the page lives and passes them in. The values are taken from the dialog result.

## Demo world

- `src/HomeBlaze/HomeBlaze/Data/Files/Demo/Game.json` is replaced by a world generated once with a fixed random seed by a script kept in the scratchpad (not committed):
  - A house of about 56 x 40 tiles (14 x 10 m): a hall with an entrance door, a living room of about 20 x 20 tiles, a kitchen, two bedrooms, a bathroom; doors 4 to 6 tiles wide; outer walls bricks, inner walls concrete, and a wooden shed outside.
  - A forest of 25 to 40 trees east of the house with all variants, random rotations and scales, and a few trees around the house, never blocking a door or the spawn point.
  - Tools `WallTool` and `TreeTool` configured.
- The developer's local `Game.json` (currently skip-worktree with a private layout) is backed up to the scratchpad, replaced by the new demo world, and the skip-worktree flag removed.

## Error handling

- Assets that fail to load fall back to gray boxes; the game keeps running.
- Invalid visual descriptions fall back to the default box on the server, with one warning per type.
- Tools refuse without a player, in a wrong direction, or with a blocked footprint, with messages shown in the existing error dialog.

## Testing

Unit tests (`HomeBlaze.Game.Tests`, `HomeBlaze.Components.Tests`):

- `GameVisual` and parts compare by value; an unchanged description is not sent again; a changed one is.
- Validation fallback for oversized parts, too many parts and foreign URLs.
- `GameAssets.Url` uses the assembly name.
- `Door.Visual` follows `IsOpen`; `Wall.Visual` follows `Material`.
- `Tree` blocks movement, has a fixed size, falls back to variant 0.
- `WallTool` builds the requested wall in the facing direction and refuses blocked or diagonal placement; ranges are enforced on the server.
- `TreeTool` picks a random variant only without one, sets rotation and scale within range, refuses blocked placement.
- Walls with `Material` and trees save and load; tools save and load with `$type`.
- The demo world loads, every room is reachable from the spawn point through open or openable doors, and no tree blocks a door.
- `MethodParameterDialog` prefills initial values.

E2E (`HomeBlaze.Game.Tests`):

- "Build wall" through the dialog with concrete and length 8 creates that wall; the second invocation shows the fields prefilled.
- "Plant tree" creates a tree in front of the player; the player cannot walk into it.
- Opening and closing a door still works.
- The existing game E2E tests keep passing.

A screenshot of the forest and the house taken with Playwright is checked before the work is handed over.
