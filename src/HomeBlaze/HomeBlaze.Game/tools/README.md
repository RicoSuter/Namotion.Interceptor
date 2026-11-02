# Asset tools

Two Node scripts that regenerate the game's people and drawn textures. They are run by hand when an asset changes, not by the build, and the folder is neither served nor packed: only `wwwroot/` becomes static web assets.

Both drive a headless Chromium through Playwright, so they need no npm packages of their own. Point `PLAYWRIGHT_PACKAGE` at the `.playwright/package` folder of any built test project that uses Playwright, for example after `dotnet build src/HomeBlaze/HomeBlaze.Game.Tests -c Debug`:

```bash
export PLAYWRIGHT_PACKAGE=$PWD/src/HomeBlaze/HomeBlaze.Game.Tests/bin/Debug/net10.0/.playwright/package
```

The Chromium of that Playwright version must be installed, which the test project's `playwright.ps1 install chromium` does.

## make-textures.js

```bash
node src/HomeBlaze/HomeBlaze.Game/tools/make-textures.js
```

Draws the 256 x 256 pastel textures on a canvas and writes `grass.png` into `HomeBlaze.Game/wwwroot/textures/` and `bricks.png`, `concrete.png` and `wood.png` into `HomeBlaze.Game.Buildings/wwwroot/textures/`. Lines on a tile edge are centered on it, so repeated tiles join to one line. The grass border is the 1 m grid of the ground.

## convert-people.js

```bash
node src/HomeBlaze/HomeBlaze.Game/tools/convert-people.js <fbx-folder> src/HomeBlaze/HomeBlaze.Game/wwwroot/models/people
```

Converts the six Quaternius FBX models named in `wwwroot/models/people/LICENSE.md` into `<name>.glb` and a 128 x 128 `<name>.png` preview, where the names are those of `GamePeople.Names`. Download the FBX files from the mirror repository and commit given in that license into an empty folder outside the repository. The page loads three.js 0.160.0 from jsDelivr, so the script needs internet access.

For each model the script:

- turns every material into a matte `MeshStandardMaterial` of the same name, undoing the second color space conversion that the FBX loader applies to colors the files already store in linear space;
- keeps the clips `Idle`, `Walk`, `Clapping`, `Sitting` and `Jump` under these exact names, which the game plays, and drops the others;
- keeps the material `Shirt`, which the game tints with the player color;
- shrinks the file below 400 KB: it welds duplicated corners, sorts the faces into one primitive per material, stores skin weights and joints as bytes, drops repeated animation keys, and drops the tracks that hold a bone at its rest transform in every clip;
- renders the preview from the front with a transparent background, in the first frame of `Idle`.

The output is deterministic: the same input files give the same bytes.
