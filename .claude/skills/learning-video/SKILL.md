---
name: learning-video
description: Use when asked to create, resume or revise a narrated learning video episode from one or more markdown docs, for example "/learning-video docs/tracking.md" or "/learning-video docs/connectors-opcua*.md". Builds videos/episodes/<nn>-<name>/ through gated stages (outline, script and storyboard, build, produce, review) with the Revideo pipeline in videos/.
argument-hint: "<doc.md> [<doc.md> ...] [auto-approve]"
---

# Learning video

Turn one doc, or a group of docs about one library, into a self-contained narrated episode of about 10 to 15 minutes. The episode is code: a script, Revideo scenes, a compiled companion sample, demo scripts and capture settings. Rendered media is regenerated from them.

The pipeline lives in `videos/` (see `videos/README.md`). The design is in `docs/superpowers/specs/2026-10-08-learning-videos-design.md`. The reference for how an episode is built is the complete smoke episode in `videos/episodes/smoke/`: read its `script.yaml`, `scenes/main.tsx`, `diagrams/flow.ts`, `demos/status.ts`, `capture.ts`, `project.ts` and `sample/` before writing anything.

All commands below run from `videos/` unless they start with `dotnet` (run those from the repository root).

## Invocation and episode folder

- `/learning-video docs/x.md [docs/y.md ...]`. Globs are fine. Read every listed doc.
- The user may say to auto-approve gates (for example "auto-approve" in the arguments or a spike instruction). Only then continue past a gate without stopping, and record in the gate file that it was auto-approved.
- Folder: `videos/episodes/<nn>-<name>/`, `<nn>` two digits, `<name>` short kebab-case from the doc (`tracking`, `opcua`, `connectors`). Reserved: `00-intro` (README), `01-tracking`, `02-opcua`. Otherwise take the next number above the highest existing or reserved one (`ls videos/episodes`).
- The folder name is the episode id. `episode:` in `script.yaml` must equal it exactly: generated audio is written under the folder name but referenced under the script's id, so a mismatch renders silence.
- If a folder for the same docs already exists, resume it instead of creating a new one.

### Resume: detect the stage

| Found in the episode folder | Stage |
|---|---|
| nothing | 1 Analyze |
| `outline.md` with `Status: awaiting approval` | gate 1: present it again or apply feedback |
| `outline.md` approved, no `script.yaml` | 3 Script |
| `script.yaml` with `# Status: awaiting approval` | gate 2: present it again or apply feedback |
| `script.yaml` approved, no `scenes/` or no `sample/` | 4 Build |
| scenes, sample and `capture.ts` present, no `output/<episode>-draft.mp4` | 5 Produce |
| `output/<episode>-draft.mp4` exists | 6 Review |

When the user asks for a revision of an earlier stage (changed docs, new chapter), edit that stage's file, set its status back to awaiting approval, and carry the change forward only into the affected beats, scenes and samples.

## Rules for every stage

- **Self-contained.** Assume the viewer has seen no other episode. Recap the coffee machine in one or two beats when the episode relies on it. Other episodes may be mentioned only as optional pointers ("the tracking episode goes deeper").
- **Do not say "Namotion".** Only the intro episode names the library aloud. Elsewhere say "the library" or the feature name ("the tracking package"). Package names, namespaces and identifiers stay on screen and are not read aloud unless the viewer needs to hear one to follow along.
- **No invented API.** Every type, member and extension method shown or narrated must exist in `src/`. The companion sample compiles it, so the build proves it.
- **Code on screen comes only from compiled files** via `#region` markers, never typed into scenes.
- **Values on screen come from the running sample**: clips, terminal captures, or numbers that match the domain defaults and simulator constants.
- **Motion language** (checked at gate 2 and in review):
  1. No bullet slides. The voice explains, the screen shows. On-screen text is limited to captions, titles and short labels.
  2. Something meaningful moves at least every 3 to 4 seconds.
  3. Transformations, not cuts: code morphs into its next version, a node expands into the live UI, a diagram grows by steps.
  4. The camera moves toward the current subject. Hard cuts only at chapter boundaries.
  5. Real values from the running sample.
- **Style.** Clean macOS look: theme components and tokens only (`theme/palette.ts`, `theme/style.ts`), soft shadows instead of outlines, Inter and JetBrains Mono, no neon or glow.
- **No application references.** Episodes describe scenarios generically with the coffee machine domain (AGENTS.md).
- **Writing.** No em dashes and no hard wrapping in markdown and narration.

## Stage 1: Analyze

- [ ] Read each doc in full. List its sections; every section ends up in the outline either as a chapter or under "Left out".
- [ ] Read the shared domain `videos/domain/Coffee/*.cs`: the model, `CoffeeMachineSimulator` constants, and the existing regions (`grep -rn '#region' videos/domain`). Defaults worth knowing: boiler starts at 20 °C, target 93 °C; recipes Espresso (40 ml, 93 °C) and Lungo (110 ml, 92 °C); `Brew(recipeName)` throws unless `IsReady`.
- [ ] Read existing episodes (`videos/episodes/*/outline.md`, `script.yaml`, `diagrams/`) only for consistency: chapter title style, node labels, concept colors (smoke: simulator orange, boiler pink, water tank cyan, machine purple, status page green). Do not rely on their content.
- [ ] Read the real public API of the library behind the docs:
  - the projects in `src/Namotion.Interceptor.<Feature>/`, especially the types and extension methods the doc's examples use;
  - `src/Namotion.Interceptor.<Feature>.Tests/VerifyChecksTests.PublicApi.verified.txt` where it exists (the complete public surface);
  - the existing samples `src/Namotion.Interceptor.*Sample*/` for working wiring.
- [ ] Note every mismatch between doc and code. Follow the code; list the mismatch in the outline under "Doc mismatches" so the user can fix the doc.

## Stage 2: Outline (gate 1)

Copy `.claude/skills/learning-video/templates/outline.md` to `videos/episodes/<nn>-<name>/outline.md` and fill it in.

- Order by importance: hook, setup, core feature, the real sample running live, advanced topics, recap. The viewer who stops after half the video should have learned the most useful half.
- Give every chapter a time budget. The total matches the target length (default 10 to 15 minutes; a spike or the user may set another). Hook and recap stay under 45 seconds each.
- Per chapter: what the viewer learns, what is on screen, the sample code (file and region names to create), and the API members used with their source files.
- Describe the companion sample: projects, ports, pages, simulator events, what the demos record.
- List doc sections deliberately left out, each with a reason.
- Set `Status: awaiting approval`.

**STOP.** Present the chapter table, the left-out list and the doc mismatches, and ask for approval. Do not write `script.yaml` until the user approves, unless gates are auto-approved. On approval set `Status: approved` (or `Status: auto-approved`) and commit (see Commits).

## Stage 3: Script and storyboard (gate 2)

Copy `.claude/skills/learning-video/templates/script.yaml` to the episode folder and replace its content. The schema is `videos/tools/schema/script.ts`:

- `episode` (the folder name), `title`, `voice` (`default`, or a reference wav path relative to `videos/`), `chapters`.
- Chapter: `id` (kebab-case), `title`, `beats`.
- Beat:
  - `id`: kebab-case, unique in the episode. Prefix with the chapter (`setup-context`).
  - `narration`: one spoken line. The beat lasts as long as its audio plus 0.4 s.
  - `hold`: extra seconds after the narration, or the whole length of a silent beat. A beat needs `narration`, `hold`, or both.
  - `visual`: the storyboard. What appears, what moves where, what the camera does.
  - `components`: one or more of `CodeCard`, `FlowDiagram`, `SequenceDiagram`, `ObjectGraph`, `LiveChart`, `LayerStack`, `BrowserFrame`, `Terminal`, `ChapterCard`, `Caption`, `Card`, `Camera`. `ObjectGraph`, `LiveChart` and `LayerStack` are allowed by the schema but have no implementation in `theme/components/` yet (see Stage 4).
  - `code: {file, region}`: file relative to the episode folder (`sample/Program.cs`, `../../domain/Coffee/Boiler.cs`).
  - `demo`: name of `demos/<name>.ts`. `terminal`: name of a terminal capture in `capture.ts`.

### Narration

- Spoken English: short sentences, one idea per beat, active voice, "you" for the viewer.
- 8 to 25 words per beat (about 3 to 10 seconds). Split longer thoughts into several beats so the picture can change with them.
- Budget about 150 words per minute: a 10 minute episode is about 1300 to 1500 words. Per chapter, words = budget seconds x 2.5.
- Write identifiers as spoken words when they must be said ("is ready", not `IsReady`); captions show the narration text as written. Better: let the code card show the identifier and narrate what it does.
- Terms the voice mispronounces go into `videos/tools/tts/lexicon.yaml` (`{match: OPC UA, say: O P C U A}`); matching is whole word and case-sensitive, captions keep the original.
- Open the first chapter with a hook: the problem and a glimpse of the result. End with a short recap of what the viewer can now do, plus optional pointers.

### Storyboard checks

Go through every beat and fix the script until all hold:

- [ ] No beat shows a list of text. Labels are at most a few words.
- [ ] Every `visual` names a motion. Beats longer than about 8 seconds (20 words) name at least two motions (for example a camera move after the code types in).
- [ ] Consecutive beats transform the same element where possible (morph, focus, reveal the next step, camera move) instead of replacing it.
- [ ] Each chapter starts with a `ChapterCard` beat; hard cuts happen only there.
- [ ] Every live value comes from a clip or terminal capture of the sample, or matches domain defaults.
- [ ] `grep -n -i namotion episodes/<episode>/script.yaml` finds nothing in narration (intro episode excepted).
- [ ] Every chapter is within about 15 % of its outline budget.

Estimate the duration per chapter. `npm run validate` also checks code files, regions, demos and terminal captures, which do not exist yet at this gate, so use this schema-only estimate now:

```bash
npx tsx -e "import {loadScript} from './tools/schema/script.ts'; const script = loadScript('episodes/<episode>'); let total = 0; for (const chapter of script.chapters) { const words = chapter.beats.reduce((sum, beat) => sum + (beat.narration?.split(/\s+/).length ?? 0), 0); const seconds = words / 2.5 + chapter.beats.reduce((sum, beat) => sum + 0.4 + (beat.hold ?? 0), 0); total += seconds; console.log(chapter.id.padEnd(24), String(words).padStart(5), 'words', seconds.toFixed(0).padStart(5), 's'); } console.log('total'.padEnd(36), (total / 60).toFixed(1), 'min');"
```

A schema error fails this command with the exact path. Add `# Status: awaiting approval` as the first line of `script.yaml`.

**STOP.** Present the chapter table (words, estimated seconds, budget, over or under), the total, and the storyboard highlights per chapter, and ask for approval, unless gates are auto-approved. On approval set the first line to `# Status: approved` (or `# Status: auto-approved`) and commit.

## Stage 4: Build

### Companion sample

- [ ] Create the sample under `videos/episodes/<episode>/sample/`: one project `sample/<Name>.Sample.csproj`, or one folder per process (`sample/Server/`, `sample/Client/`). Use the smoke csproj as the template:

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">

  <ItemGroup>
    <ProjectReference Include="..\..\..\..\src\Namotion.Interceptor.Generator\Namotion.Interceptor.Generator.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
    <ProjectReference Include="..\..\..\..\src\Namotion.Interceptor.<Feature>\Namotion.Interceptor.<Feature>.csproj" />
    <ProjectReference Include="..\..\..\domain\Coffee\Coffee.csproj" />
  </ItemGroup>

</Project>
```

  Add one `..\` per extra folder level. `videos/Directory.Build.props` imports `src/Directory.Build.props` (net10.0, nullable, warnings as errors) and packages use central versions from `src/Directory.Packages.props` (no `Version` attributes).
- [ ] Add every new project to `src/Namotion.Interceptor.slnx` inside `<Folder Name="/Videos/">`, for example `<Project Path="../videos/episodes/<episode>/sample/<Name>.Sample.csproj" />`.
- [ ] Wrap every piece of code the video shows in `#region <Name>` / `#endregion`. Names are PascalCase and unique per file. Nested region marker lines are dropped from the shown code, so a region can contain smaller regions for close-ups.
- [ ] Code for intermediate versions (the "before" of a morph) is compiled too, for example a static method per version in a `Steps.cs` file that builds but is never called.
- [ ] Size regions for the card: at the default code size (30 px) a 1240 by 780 card shows 14 lines of about 60 characters. Lines longer than the card are clipped, not wrapped. Longer regions need `focus` scrolling or a smaller `codeFontSize` (26 at the smallest).
- [ ] Keep console output short so terminal captures read well (smoke `Program.cs`: `SuppressStatusMessages` and `AddSimpleConsole(options => options.SingleLine = true)`).
- [ ] For reproducible scripted events (a fault, a temperature drop) write a hosted service that runs `new CoffeeMachineSimulator(machine, seed, events)` with `SimulatorEvent(TimeSpan At, Action<CoffeeMachine> Apply)` instead of `CoffeeMachineSimulatorService`.
- [ ] Extend the shared domain only when the episode needs it. Add, never rename, regions and members; keep `videos/domain/Coffee.Tests` passing.
- [ ] Build: `dotnet build videos/episodes/<episode>/sample/<Name>.Sample.csproj`, later `dotnet build src/Namotion.Interceptor.slnx`.

### Video-friendly demo pages

Browser demos record pages of the sample. Design them for video, following `episodes/smoke/sample/StatusPage.cs`:

- A static class with a `const string Html = """..."""` page served at `/`, polling a JSON endpoint (for example `/status`) every 500 ms.
- Viewport 1280 by 800, no scrolling. Background `#1c1c1e`, cards `#2c2c2e` with radius 28 and a soft shadow (`0 12px 40px rgba(0, 0, 0, 0.45)`), text `#f5f5f7`, secondary `#a1a1a6`, accents from `theme/palette.ts`. No borders.
- `font-family: Inter`; capture registers Inter and JetBrains Mono in every page. Headline around 100 px, values 56 px or more, labels 28 px, nothing under 22 px. `font-variant-numeric: tabular-nums` for changing numbers, CSS transitions for smooth bars and colors.
- Stable element ids the demo script can wait on, and large buttons when the demo clicks them.

### Demos and capture

- `demos/<name>.ts` default-exports a `Demo` (`tools/capture/config.ts`): `async (page, {baseUrl}) => {...}`. It is choreography: `page.goto`, real clicks, `page.waitForFunction` for app state that varies (temperature reached, status text), `page.waitForTimeout` for pacing, and about 1.5 s of hold on the end state.
- Keep a clip between 0.5 and 4 times its beat length. `BrowserFrame.play` speeds it up to 4x; a longer clip loses its start, a shorter one plays at no less than 0.5x.
- Each demo records one page of one app. To show two processes, build one page that shows both, or record two demos and play two `BrowserFrame`s.
- `capture.ts` (see smoke):

```ts
import {defineCapture} from '../../tools/capture/config';

export default defineCapture({
  app: {project: 'sample/<Name>.Sample.csproj', port: 5300, readyPath: '/status'},
  viewport: {width: 1280, height: 800},
  terminal: [
    {name: 'run-sample', command: 'dotnet', args: ['run', '--project', 'sample', '--urls', 'http://localhost:5301'], until: 'Now listening on', timeoutSeconds: 180},
  ],
});
```

  Capture starts exactly one `app` (with `--urls http://localhost:<port>`, optional `environment`) and records every demo against it. Terminal captures run first, each as its own process, so give them another port. `until` is a regular expression that ends a long-running command. Use ports unique to the episode. If an episode truly needs two running apps, extend `tools/capture` to accept several (with a test) in its own commit.

### Diagrams

Typed definitions in `diagrams/<name>.ts`, as in `episodes/smoke/diagrams/flow.ts`:

- `FlowDefinition` (`theme/flowLayout.ts`): `direction` (`'right'` or `'down'`), `nodes` (`id`, `label`, `detail`, `color`, `step`, `width`, `height`, `position`), `edges` (`from`, `to`, `label`, `step`), `nodeSpacing`, `layerSpacing`. Layout is elkjs. `step` sets the reveal order; edges default to the later step of their nodes.
- Sequence participants: `[{id, label, color}] as const`.
- Translate Mermaid diagrams from the docs into these types; keep the doc's names and give each concept the same color everywhere.
- Fit the frame: at default sizes (nodes 300 by 124, layers 150 apart) at most 4 layers left to right or 3 top to bottom. For more, narrow the nodes or set `scale` on the diagram.

### Scenes

One scene per chapter is a good default: `scenes/<chapter-id>.tsx`, registered in order in `project.ts`:

```ts
import {episodeProject} from '../../theme/project';
import intro from './scenes/intro';
import setup from './scenes/setup';

export default episodeProject([intro, setup]);
```

Scene skeleton:

```tsx
import {makeScene2D} from '@revideo/2d';
import {all, chain, delay, spring} from '@revideo/core';
import programSource from '../sample/Program.cs?raw';
import {Camera} from '../../../theme/components/Camera';
import {ChapterCard} from '../../../theme/components/ChapterCard';
import {CodeCard} from '../../../theme/components/CodeCard';
import {waitForFonts} from '../../../theme/fonts';
import {Narrator} from '../../../theme/narrator';
import {extractRegion} from '../../../theme/regions';
import {arrivalSpring, enterEasing} from '../../../theme/style';
import {useTiming} from '../../../theme/variables';

export default makeScene2D('setup', function* (view) {
  yield* waitForFonts();
  const narrator = new Narrator(view, useTiming());
  const camera = new Camera({});
  view.add(camera);

  const title = new ChapterCard({title: 'Setup', kicker: 'Chapter 1'});
  view.add(title);
  yield* narrator.beat('setup-title', title.enter());

  const code = new CodeCard({fileName: 'Program.cs', width: 1240, height: 780, y: -40, opacity: 0, scale: 0.94});
  camera.add(code);
  yield* narrator.beat('setup-context',
    title.exit(),
    delay(0.2, all(code.opacity(1, 0.5, enterEasing), spring(arrivalSpring, 0.94, 1, value => code.scale(value)))),
    delay(0.5, code.show(extractRegion(programSource, 'Context'), 1.8)),
  );
});
```

Scene rules:

- [ ] `yield* waitForFonts()` first, then one `Narrator` and one `Camera` per scene.
- [ ] Call `narrator.beat(id, ...animations)` for every beat id of `script.yaml` exactly once, in script order. The animations all start with the beat and run in parallel; order them with `delay`, `chain`, `sequence`. Size long animations with `narrator.duration(id)`.
- [ ] Between beats do only zero-time work: add or `remove()` nodes, `yield* flow.build()`, `yield browser`. A `waitFor` or animation outside a beat shifts the picture against the narration, subtitles and contact sheet.
- [ ] Add content to `camera` so it zooms; add `ChapterCard`s to `view` so they stay full screen. Fade an element out inside a beat and `remove()` it after that beat.
- [ ] Frame: 1920 by 1080, origin in the center. The caption pill sits above y 492, so keep content above y 370 (cards centered around y -40 to -70).
- [ ] Import code with `import source from '<path>?raw'` and `extractRegion(source, '<Region>')`; reference the same file and region in the beat's `code:` so validate checks it.
- [ ] Use the domain and sample values in labels (93 °C target, 9 bar, Espresso).

### Component reference

All in `theme/components/`; read the file before using a component in a new way.

| Component | Construct | Animate |
|---|---|---|
| `Camera` | `new Camera({})`, add to `view`, add content to it | `focusOn(target, {zoom = 1.4, duration = 1.2})` where target is a node, a `Vector2`, or `() => Vector2` evaluated when the move starts; `reset(duration)` |
| `ChapterCard` | `{title, kicker?, colors?: [AccentColor, AccentColor, AccentColor]}`; titles up to about 24 characters | `enter()` (about 1.4 s), `exit()` (0.7 s, removes itself) |
| `CodeCard` | `{fileName, width, height, code?, codeFontSize?}` plus Rect props | `show(code, duration)` types in; `morph(code, duration, fileName?)` animates the diff and can rename the tab; `focus(from, to, duration)` highlights zero-based inclusive lines of the region and dims the rest, scrolling if needed; `unfocus()`; `linesCenter(from, to)` world point for the camera |
| `FlowDiagram` | `{definition: FlowDefinition}`; `yield* flow.build()` once before revealing | `reveal(step)`; `pulse(from, to, duration)` sends a particle along an edge; `node(id)` for camera targets |
| `SequenceDiagram` | `{participants, width = 1500, height = 760, rowHeight = 110}`; about 5 messages fit at defaults | `appear()`; `message(from, to, label, {reply?})` draws the next row and highlights the receiver; `activate(id)` |
| `Terminal` | `{transcript: useTerminal('<name>'), width, maximumRows = 12, title?}`; wraps long lines | `run(duration)` types the command and streams the output to end within the duration |
| `BrowserFrame` | `{demo: '<name>', address, width, aspectRatio = 1.6}`; then `yield browser` | `play(duration, {from?, to?})` fits the clip to the duration |
| `Card` | Rect props plus `fill`; rounded, soft shadow | standard node signals (`opacity`, `scale`, `position`) |
| `WindowFrame` | `{width, height, title?, address?, bodyFill?}`; add content to `.body` | standard node signals |
| `Arrow` | `{curve, color?, lineWidth?, headSize?, dashed?, drawn?}`; curves from `theme/geometry.ts` (`sideCurve`, `messageCurve`) | `grow(duration)` |
| `Caption` | owned by `Narrator`; never add one | shown per beat automatically |

Tokens: `palette` (`background`, `card`, `elevated`, `separator`, `text`, `secondaryText`, accents `blue`, `cyan`, `green`, `orange`, `pink`, `purple`); `style.ts` has `radius`, `shadow`, `smallShadow`, `fonts`, `fontSize` (caption 34, label 30, detail 22, code 30, terminal 30, title 112), `spacing`, `durations` (0.3, 0.6, 1.2), `moveEasing` for moves and the camera, `enterEasing` for arrivals, `arrivalSpring` for `spring(arrivalSpring, from, to, setter)`.

`ObjectGraph`, `LiveChart` and `LayerStack` are not implemented. Compose the beat from existing components (an object tree as a `'down'` `FlowDiagram`, values in a `Card`), or implement the component in `theme/components/` following `FlowDiagram` and `Card`, show it in the smoke episode, and commit it separately.

Change `theme/` or `tools/` only for a fix or a reusable improvement, in its own commit, and keep `npm test`, `npm run typecheck` and `npm run render -- smoke` passing.

### Verify the build

```bash
dotnet build src/Namotion.Interceptor.slnx
cd videos
npm run typecheck
npm run validate -- <episode>     # prints beats, words and minutes; fails on missing files, regions, demos, terminal names
```

## Stage 5: Produce

Run in order from `videos/`:

| Command | Output |
|---|---|
| `npm run validate -- <episode>` | `<episode>: N beats, W words, about M min of narration` |
| `npm run capture -- <episode>` | `public/generated/<episode>/terminal/*.txt`, `clips/*.mp4` and `clips/clips.json`. `--only <name>` recaptures one demo or terminal |
| `npm run tts -- <episode>` | `public/generated/<episode>/audio/*.wav` and `timing.json`; prints seconds per chapter and the total |
| `npm run render -- <episode>` | draft at 15 fps: `output/<episode>-draft.mp4`, `output/<episode>.srt`, `output/<episode>-contact.png`, `output/<episode>-review.md`, `output/<episode>-frames/` |
| `npm run render -- <episode> --final` | `output/<episode>-final.mp4` at 30 fps, plus the same review files |

- Speech synthesis takes about 3 s per 1 s of speech on the first run (half an hour or more for a full episode). Audio is cached by text, voice and settings, so later runs only synthesize changed lines.
- Capture, TTS and render can each take longer than a 10 minute command timeout. Run them in the background with output to a log file and poll the log.
- After TTS compare the chapter seconds with the outline budgets. The 150 words per minute estimate is a planning figure; the voice may speak faster (the smoke episode measured about 200). If a chapter is short, add substance or let a demo breathe with `hold`, not filler words.
- Render logs `Beat '<id>': an animation ran N s past the end of the beat` when an animation outlives its beat. Fix every one.
- Render the final preset only when the user asks or the draft review is clean and gates are auto-approved.

## Stage 6: Review

1. Read `output/<episode>-review.md`: total and per chapter seconds, beats without motion for 4 s or more, and the contact sheet order with each beat's midpoint time.
2. Open `output/<episode>-contact.png` with the Read tool: one frame per beat at its midpoint, 4 per row, in the listed order.
3. Look at full-size frames wherever a thumbnail is unclear, and at the start and end of complex beats:

```bash
FFMPEG=$(node -e "import('@ffmpeg-installer/ffmpeg').then(m => console.log(m.default.path))")
"$FFMPEG" -hide_banner -loglevel error -y -ss <seconds> -i output/<episode>-draft.mp4 -frames:v 1 /tmp/<episode>-<seconds>.png
```

   Beat start times and durations are in `public/generated/<episode>/timing.json`.
4. Check every frame for:
   - [ ] still beats from the report (add a camera move, a focus step, a pulse, or split the beat);
   - [ ] overlaps between elements, or with the caption pill;
   - [ ] clipped text: code cut at the card edge, labels outside their nodes, content outside the frame;
   - [ ] unreadable sizes (under 22 px at 1080p), low contrast, crowded frames;
   - [ ] empty or black frames, a picture that contradicts the narration, values that do not match the sample;
   - [ ] camera framing: the subject centered, nothing important cut off.
5. Fix and re-render until the report lists no still beats (or each remaining one has a stated reason) and the frames are clean. Narration changes re-synthesize only the changed beats; rerun `tts` before `render`.
6. Present to the user: video path, duration per chapter against the budget, the contact sheet, remaining warnings with reasons, and any doc mismatches.
7. Apply feedback by editing only the affected beats: narration in `script.yaml` then `tts`; visuals in the scene; a demo with `capture --only <name>`; then render again.

## Known pitfalls

- **Revideo cache.** A node with a shadow, a filter, or opacity below 1 is drawn through a cache that misses paint changes of its children (an output line fading in, a highlight). Do not animate children's paint properties inside a shadowed container. Put the shadow on a background child, as `Card` does, and build shadowed surfaces from `Card`.
- **Media nodes.** `yield` a `Video` or `Audio` node (for example `yield browser` after adding a `BrowserFrame`) before the first frame that draws it, or reading media properties logs an asynchronous property warning and the clip may not show.
- **Fonts.** `yield* waitForFonts()` at the start of every scene. Canvas text does not trigger font loading, so text drawn earlier uses a fallback face.
- **Fixed beat length.** A beat always lasts its timed length. Animations that outlive it keep running into the next beat with a warning; size them with `narrator.duration(id)`.
- **Reactive text.** Set `Txt` text imperatively inside a `tween` (as `Terminal` and `CodeCard.show` do); a reactive text function can create text nodes outside the scene while drawing.
- **Flow layout** is asynchronous: `yield* flow.build()` before the first `reveal`.
- **Camera targets** measured before an earlier move in the same `chain` are stale; pass `() => node.absolutePosition()` to `focusOn`.
- **Clip fitting.** `BrowserFrame.play` fits a clip to its beat between 0.5x and 4x and trims the start beyond that, so the end state is always visible. Record demos with the end state held.
- **Demo resolution.** Demos record at `deviceScaleFactor` 2 (a 1280 by 800 page becomes 2560 by 1600) so zooms stay crisp. Keep the default.
- **Chrome sandbox.** Rendering launches Chrome with `--no-sandbox` (Ubuntu blocks its user namespace sandbox); this is handled in `tools/render.ts`.
- **Episode id.** `episode:` in `script.yaml` must equal the folder name.
- **Validate before files exist.** `npm run validate` fails until the sample regions, demos and `capture.ts` exist; use the schema-only estimate at gate 2.

## Commits

- Commit after each approved gate and at the end of build and review, for example `feat: outline for the tracking learning video`, `feat: tracking learning video script`, `feat: tracking learning video`, `fix: tracking video overlaps in the setup chapter`. Prefixes: `feat:`, `fix:`, `docs:`, `refactor:`, `test:`, `chore:`.
- Never add AI attribution: no agent names, no `Co-Authored-By` trailers, no "Generated with" footers (AGENTS.md).
- Never commit `videos/public/generated/` or `videos/output/`. Both are gitignored; do not force-add them. Check `git status --short` before each commit.
- Do not push unless the user asks.
