---
name: learning-video
description: Use when asked to create, resume, restart or revise a narrated learning video episode, for example "/learning-video docs/tracking.md", "/learning-video 04" or "make the next learning video". Builds videos/episodes/<nn>-<name>/ through gated stages (outline, script and storyboard, build, produce, review) with the Revideo pipeline in videos/.
argument-hint: "<nn> | <doc.md> [<doc.md> ...] | next [auto-approve]"
---

# Learning video

Turn the docs of one series entry into a self-contained narrated episode. The episode is code: a script, Revideo scenes, a compiled companion sample, demo scripts and capture settings; rendered media is regenerated from them.

`videos/README.md` is the reference for the pipeline: the series table, commands, voices, length and word budget, outputs and tests. The smoke episode `videos/episodes/smoke/` uses every shared component and helper; read its `script.yaml`, `scenes/main.tsx`, `diagrams/flow.ts`, `demos/`, `capture.ts`, `project.ts` and `sample/` before writing anything.

Commands run from `videos/` unless they start with `dotnet` (run those from the repository root).

## Invocation and episode folder

- The series table in `videos/README.md` decides the number, the folder and the docs. Accept an episode number, the docs of an entry (globs are fine), or "next", which is the lowest entry with status `open`. When the given docs belong to no entry, **stop** and ask which entry they belong to; never invent a number.
- Read every doc of the entry. Set its status to `in progress` when stage 1 starts and to `done` when the user accepts the final video.
- The folder `videos/episodes/<nn>-<name>/` is the episode id. `episode:` in `script.yaml` must equal it exactly: audio is written under the folder name but referenced under the script's id, so a mismatch renders silence.
- An existing folder is resumed, see below. To restart an episode from scratch, delete its folder, its project lines in `src/Namotion.Interceptor.slnx`, `videos/public/generated/<episode>/` and `videos/output/<episode>*`, and start again at stage 1.
- Only when the user says to auto-approve gates (for example "auto-approve" in the arguments) continue past a gate without stopping, and record in the gate file that it was auto-approved.

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

For a revision of an earlier stage (changed docs, new chapter), edit that stage's file, set its status back to awaiting approval, and carry the change forward only into the affected beats, scenes and samples.

## Rules for every stage

- **Self-contained.** Assume the viewer has seen no other episode. Recap the coffee machine in one or two beats when the episode relies on it; other episodes are optional pointers only ("the tracking episode goes deeper").
- **Do not say "Namotion".** Only the intro episode names the library aloud; elsewhere say "the library" or the feature name. Identifiers stay on screen and are read aloud only when the viewer needs to hear one.
- **No invented API.** Every type, member and extension method shown or narrated exists in `src/`; the companion sample compiles it.
- **Code on screen comes only from compiled files** via `#region` markers, never typed into scenes.
- **Values on screen come from the running sample**: clips, terminal captures, or numbers matching the domain defaults and simulator constants.
- **Motion language** (checked at gate 2 and in review):
  1. No bullet slides. The voice explains, the screen shows: titles, short labels and key term callouts only. The narration is a soft subtitle track, never drawn.
  2. Something meaningful moves at least every 3 to 4 seconds.
  3. Transformations, not cuts: code morphs into its next version, a node expands into the live UI, a diagram grows by steps.
  4. The camera moves toward the current subject. Hard cuts only at chapter boundaries.
  5. A beat that introduces a key term or number shows it as a short `Pill` next to what it names (`buffer time 8 ms` under the link it configures).
- **Style.** Clean macOS look from theme components and tokens only, soft shadows instead of outlines, Inter and JetBrains Mono, no neon or glow.
- **Writing.** Follow AGENTS.md: no em dashes, no hard wrapping, no application references (describe scenarios with the coffee machine domain).

## Stage 1: Analyze

- [ ] Read each doc in full and list its sections; every section ends up in the outline as a chapter or under "Left out".
- [ ] Read the shared domain `videos/domain/Coffee/*.cs`, its simulator constants and existing regions (`grep -rn '#region' videos/domain`). Defaults: boiler 20 °C rising to a 93 °C target; Espresso 40 ml at 93 °C, Lungo 110 ml at 92 °C; `Brew(recipeName)` throws unless `IsReady`.
- [ ] Reuse the concept colors and node labels of `episodes/smoke/diagrams/flow.ts` (simulator orange, boiler pink, water tank cyan, machine purple, status page green); read other episodes for consistency only.
- [ ] Read the real public API behind the docs: `src/Namotion.Interceptor.<Feature>/`, its `VerifyChecksTests.PublicApi.verified.txt` where it exists, and the samples `src/Namotion.Interceptor.*Sample*/`.
- [ ] List every mismatch between doc and code under "Doc mismatches" in the outline; follow the code.
- [ ] **Test the story's behavior first.** Before the outline, prove every behavior the story depends on with a throwaway test (for example in `videos/domain/Coffee.Tests`, printing the change sequence through an `ImmediateScheduler` subscription), then delete it. Docs can promise more than the code keeps, and a story built on such a promise has to be rewritten once the sample exists.

## Stage 2: Outline (gate 1)

Copy `.claude/skills/learning-video/templates/outline.md` to the episode folder and fill it in.

- Order by importance: hook, setup, core feature, the sample running live, advanced topics, recap. A viewer who stops halfway has learned the most useful half.
- Give every chapter a time budget; the total is the target length in `videos/README.md`. Hook and recap stay under 45 seconds each.
- Per chapter: what the viewer learns, what is on screen, the sample code (files and regions to create), and the API members used with their source files.
- Describe the companion sample (projects, ports, pages, simulator events, demos), the doc sections left out with a reason, and the doc mismatches. Set `Status: awaiting approval`.

**STOP.** Present the chapter table, the left-out list and the doc mismatches, and ask for approval. On approval set `Status: approved` (or `auto-approved`) and commit.

## Stage 3: Script and storyboard (gate 2)

Copy `.claude/skills/learning-video/templates/script.yaml` to the episode folder and replace its content. The schema is `videos/tools/schema/script.ts`:

- `episode` (the folder name), `title`, `chapters`; `voice`, `tempo` and `background` are optional, leave them out for the defaults (see `videos/README.md`).
- Chapter: `id` (kebab-case), `title`, `beats`.
- Beat: `id` (kebab-case, unique, prefixed with the chapter, `setup-context`); `narration` (one spoken line, the beat lasts as long as its audio plus 0.4 s); `hold` (extra seconds, or the length of a silent beat; a beat needs `narration`, `hold` or both); `visual` (the storyboard: what appears, what moves, what the camera does); `components` (`CodeCard`, `FlowDiagram`, `SequenceDiagram`, `BrowserFrame`, `Terminal`, `ChapterCard`, `Card`, `Camera`; arrows and pills are part of the visual); `code: {file, region}` relative to the episode folder; `demo` (`demos/<name>.ts`); `terminal` (a terminal capture in `capture.ts`).

### Narration

- Spoken English: short sentences, one idea per beat, active voice, "you" for the viewer. Open with a hook (the problem and a glimpse of the result); end with a recap of what the viewer can now do.
- 8 to 25 words per beat. Split longer thoughts so the picture can change with them.
- Write identifiers as spoken words when they must be said ("is ready", not `IsReady`); subtitles show the text as written. Better: let a code card show the identifier and narrate what it does.
- Kokoro reads `async` as "a sink" and may turn a line's first "Is" into "As"; say "asynchronous" and start such lines with another word.
- Terms the voice mispronounces go into `videos/tools/tts/lexicon.yaml` (matching rules in `tools/lexicon.ts`). Kokoro also reads phoneme markup in a `kokoro:` override. To find phonemes, print what Kokoro makes of the plain spelling and of a markup candidate, then adjust and listen (from `videos/tools/tts`):

  ```bash
  uv run python -c "
  from video_tts.kokoro_engine import _create_pipeline
  pipeline = _create_pipeline('a')
  for text in ['Over O P C U A.', 'Over [OPC UA](/ˈO pˈi sˈi jˈu ˈeɪ/).']:
      for result in pipeline(text, voice='am_michael', speed=1.25):
          print(text, '->', result.phonemes)
  "
  ```

  The phonemes printed for a markup line must equal the markup, or Kokoro did not take it. In letters spoken together, primary stress `ˈ` on the last letter usually sounds most natural (`[ASP.NET](/ˌAɛspˈi dɑt nˈɛt/)`).

### Storyboard checks

- [ ] No beat shows a list of text; labels are at most a few words, and key terms and numbers get a `Pill`.
- [ ] Every `visual` names a motion; beats over about 8 seconds (20 words) name at least two.
- [ ] Consecutive beats transform the same element where possible instead of replacing it.
- [ ] Each chapter starts with a `ChapterCard` beat; hard cuts happen only there.
- [ ] Every live value comes from a clip or terminal capture of the sample, or matches domain defaults.
- [ ] `grep -n -i namotion episodes/<episode>/script.yaml` finds nothing in narration (intro episode excepted).
- [ ] Every chapter is within about 15 % of its outline budget: `npm run validate -- <episode>` prints words and seconds per chapter before it checks files that do not exist yet at this gate.

A YAML value that starts with a double quote must be quoted as a whole, so start a `visual` with a word. Add `# Status: awaiting approval` as the first line of `script.yaml`. Start `npm run tts -- <episode>` in the background now (it needs only the script) and adjust the script until the measured total lands in the target range; only changed lines are synthesized again.

**STOP.** Present the chapter table (words, seconds, budget, over or under), the total and the storyboard highlights per chapter, and ask for approval. On approval set the first line to `# Status: approved` (or `auto-approved`) and commit.

## Stage 4: Build

### Companion sample

- [ ] Create the sample under `videos/episodes/<episode>/sample/`: one project `sample/<Name>.Sample.csproj`, or one folder per process (`sample/Server/`, `sample/Client/`). Start from the smoke csproj, add the feature library, and add one `..\` per extra folder level:

  ```xml
  <ProjectReference Include="..\..\..\..\src\Namotion.Interceptor.<Feature>\Namotion.Interceptor.<Feature>.csproj" />
  ```

  `videos/Directory.Build.props` imports the repository settings (net10.0, nullable, warnings as errors); packages use central versions (no `Version` attributes).
- [ ] Add every project to `src/Namotion.Interceptor.slnx` inside `<Folder Name="/Videos/">`.
- [ ] Ports: the first app of episode `nn` listens on `5300 + 10 × nn` (episode 04: 5340), further apps and terminal captures on the following ports (5341, 5342, ...).
- [ ] Wrap every piece of code the video shows in `#region <Name>` / `#endregion` (PascalCase, unique per file). Nested region markers are dropped from the shown code, so a region can contain smaller regions for close-ups. The "before" of a morph is compiled too, for example as a static method per version in a `Steps.cs` that is never called.
- [ ] Size regions for the card: at the default 30 px a 1240 by 780 card shows 14 lines of about 60 characters; at 26 px (the smallest) a 1500 wide card fits about 90. Lines are clipped, not wrapped, so break long calls in the sample itself; longer regions need `focus` scrolling.
- [ ] Keep console output short so terminal captures read well (`SuppressStatusMessages` and `AddSimpleConsole(options => options.SingleLine = true)`, as in smoke).
- [ ] Several instances of one hosted service (a simulator per machine) are registered with `AddSingleton<IHostedService>(serviceProvider => ...)`, since `AddHostedService` registers a type once. A before and after comparison is simpler as two machines in one app, each in its own context, routed with `MapGroup("/{machine}")`.
- [ ] Scripted events (a fault, a temperature drop) come from a hosted service running `new CoffeeMachineSimulator(machine, seed, events)` with `SimulatorEvent(TimeSpan At, Action<CoffeeMachine> Apply)`.
- [ ] Extend the shared domain only when the episode needs it: add, never rename, regions and members, and keep `videos/domain/Coffee.Tests` passing.

**Sample code is teaching code.** Viewers copy what the video shows, so review every sample against the contracts the docs state, including the failure and shutdown paths:

- [ ] Samples follow the library's contracts, for example a source claims ownership as soon as the binding is known, before it connects when the binding is local configuration (`SourceOwnershipManager` in `docs/connectors.md`).
- [ ] Every connect or subscribe has its cleanup: a disposable connection, or a cleanup callback passed to `BackgroundTaskLifetime`.
- [ ] Results of claim and try methods are checked, not ignored.
- [ ] `WriteChangesAsync` writes the changes it is given and reads no other subject properties.
- [ ] State shared by a background loop and a write or request handler is synchronized or immutable.

**Library defects found while building a sample** are not worked around: reproduce the defect with a failing test in the library's test project, fix it in its own `fix:` commit separate from the episode commits, keep the sample showing the intended usage, and list the fix in the stage 6 report so the user can split it into its own pull request.

### Demo pages

Browser demos record pages of the sample, designed for video like `episodes/smoke/sample/StatusPage.cs`:

- A static class with a `const string Html = """..."""` page, polling a JSON endpoint every 250 to 500 ms, no scrolling.
- Background `#1c1c1e`, cards `#2c2c2e` with radius 28 and the soft shadow `0 12px 40px rgba(0, 0, 0, 0.45)`, text `#f5f5f7`, secondary `#a1a1a6`, accents from `theme/palette.ts`, no borders. `font-family: Inter` (capture registers Inter and JetBrains Mono in every page); headline around 100 px, values 56 px or more, labels 28 px, nothing under 22 px; `tabular-nums` for changing numbers, CSS transitions for bars and colors.
- Sizes in `vw` with a pixel maximum (`min(104px, 8.2vw)`) let a page also fill one half of a split recording.
- Stable element ids the demo can wait on, large buttons when the demo clicks them, and `document.body.dataset.live = 'true'` after the first successful poll, which `openSplit` waits for.
- A page that lists changes must survive the demo's reset: an asynchronous scheduler delivers changes after the reset endpoint returns, so the clear records its time and drops older changes by `ChangedTimestamp`.

### Demos and capture

- `demos/<name>.ts` default-exports a `Demo` (`tools/capture/config.ts`): choreography with `page.goto`, real clicks, `waitForFunction` for app state that varies, `waitForTimeout` for pacing, and about 1.5 s of hold on the end state. Demos record in name order against the same running apps, so a demo that needs a cold machine must sort first.
- `mark('<name>')` records a moment a scene cuts on; `BrowserFrame.mark` and `SplitWindows.mark` return its clip time, so scenes play ranges such as `{from: windows.mark('click') - 0.2, to: windows.mark('brewing') + 2.5}` that survive a new capture.
- An exported `prepare: DemoPreparation` runs before the recording starts, for example waiting until the machine is ready, so the clip does not begin with a long wait.
- A clip plays at 0.5 to 4 times its speed to fit its beats (`theme/clips.ts`): add up every beat that plays one clip before capturing, and lengthen the holds when the beats are more than twice as long as the clip.
- Two pages in sync (two processes, or two pages of one app) are one split recording: the demo calls `openSplit(page, leftUrl, rightUrl)` from `tools/capture/split.ts`, the viewport holds both pages side by side, and the scene shows the halves with `SplitWindows` (smoke `demos/two-pages.ts`). All demos of an episode share the `viewport` of `capture.ts`.
- To place callouts on a recorded page, extract one frame (`ffmpeg -ss <s> -i clips/<demo>.mp4 -frames:v 1 -vf scale=<viewport width>:-1 frame.png`) and read element positions in CSS pixels, which `SplitWindows.pagePoint` takes.
- `capture.ts` (see smoke) sets `app: {project, port, readyPath}` or, for several processes, `apps: [{name, ...}]`, started in order and each awaited until `readyPath` answers; demos get `baseUrl` (the first app) and `baseUrls` by name. Terminal captures run before the apps start, in their own process on another port; `whileAppsRun: true` runs one against the running apps, and `until` (a regular expression) ends a long-running command at the line it matched.

### Diagrams

Typed definitions in `diagrams/<name>.ts`, as in smoke: a `FlowDefinition` (`theme/flowLayout.ts`, laid out by elkjs; `step` sets the reveal order, edges default to the later step of their nodes) or sequence participants `[{id, label, color}] as const`. Translate the docs' Mermaid diagrams into these, keeping their names and one color per concept. At default sizes at most 4 layers fit left to right or 3 top to bottom; for more, narrow the nodes or scale the diagram.

### Scenes

One scene per chapter is a good default: `scenes/<chapter-id>.tsx`, registered in order in `project.ts` with `episodeProject([...])`. Follow the structure of smoke's `scenes/main.tsx`:

- [ ] `yield* waitForFonts()` first, then one `Narrator` and one `Camera` per scene.
- [ ] `narrator.beat(id, ...animations)` once for every beat id, in script order (the narrator stops the render otherwise). The animations start together; order them with `delay`, `chain`, `sequence`, and size long ones with `narrator.duration(id)`.
- [ ] Between beats do only zero-time work: add or `remove()` nodes, `yield* flow.build()`, `yield browser`. Anything that takes time there shifts the picture against the narration.
- [ ] Add content to `camera` so it zooms and `ChapterCard`s to `view`. Fade an element out inside a beat and `remove()` it after that beat.
- [ ] Frame: 1920 by 1080, origin in the center. The narrator adds the background layer and the chapter header ("Namotion.Interceptor | <title> | <chapter title>", top left); scenes add neither, nor a full-frame backdrop. Center content vertically around y 0 and point camera targets at the subject itself.
- [ ] Keep the header region free once a move has settled: nothing above screen y 120 from the left edge to just past the header text (`ChapterHeader.zone()`, around screen x 700 to 800). Pass the large subjects of a zoom as `clear` to `focusOn` or `focusOnPoint` (a code card, `windows.frames`, `SequenceDiagram.headers`, `FlowDiagram.boxes`), each node on its own.
- [ ] Import code with `import source from '<path>?raw'` and `extractRegion(source, '<Region>')`, and reference the same file and region in the beat's `code:`.
- [ ] Connect elements with an `Arrow` from edge to edge, for two cards from the right side of one to the left side of the other at their vertical center (`messageCurve(fromX, toX, y)`). Arrows default to the neutral `palette.edge`; an accent color carries meaning, and pink means failure or a blocked path. A value in transit rides the arrow (`ride`).

### Components and helpers

Read the file before using one in a new way; smoke uses all of them.

| Name | Use |
|---|---|
| `Camera` | `focusOn(node, world point or () => point, {zoom, duration, clear})`, `focusOnPoint(content point, options)`, `reset(duration)` |
| `ChapterCard` | `{title, kicker?, colors?}`, titles up to about 24 characters; `enter()`, `exit()` |
| `CodeCard` | `{fileName, width, height, codeFontSize?}`; `show`, `morph(code, duration, fileName?)`, `focus(from, to)` (zero-based inclusive lines), `unfocus()`, `linesCenter` |
| `FlowDiagram` | `{definition}`, `yield* build()` once; `reveal(step)`, `pulse(from, to)` along an existing edge, `retext(id, {label?, detail?})`, `node(id)`, `boxes` |
| `SequenceDiagram` | `{participants, width, height, rowHeight}`, about 5 messages at defaults; `appear()`, `message(from, to, label, {reply?})`, `activate(id)`, `headers` |
| `Terminal` | `{transcript: useTerminal(name), width, title?}`; `run(duration)` types the command and streams the output |
| `BrowserFrame` | `{demo, address, width, aspectRatio?, crop?}`, then `yield browser`; `play(duration, range?)`, `mark(name)` |
| `SplitWindows` | `{demo, addresses, pageWidth?, pageHeight, windowWidth?, gap?}`: the halves of an `openSplit` recording; `arrive()`, `play`, `mark`, `pagePoint`, `frames` |
| `Card`, `WindowFrame` | rounded surfaces with a soft shadow; build shadowed surfaces from `Card` |
| `Arrow` | `{curve, color?, lineWidth?, dashed?}` with curves from `theme/geometry.ts`; `grow(duration)` |
| `Pill` | `{text, color?, code?, size?}`: key terms, values and callouts; `retext`, `recolor` |
| `theme/motion.ts` | `arrive`, `leave`, `nudge`, `travel` (a particle between two points), `ride` (a node along an arrow), `toLocal` (a world point in a node's space) |

Tokens live in `theme/palette.ts` and `theme/style.ts`. A beat needing a component that does not exist is composed from these (an object tree as a `'down'` `FlowDiagram`), or the component is added to `theme/components/`, shown in smoke, and committed separately. Change `theme/` or `tools/` only for a fix or a reusable improvement, in its own commit, keeping the tests and the smoke render passing.

### Verify the build

`dotnet build src/Namotion.Interceptor.slnx`, then `npm run typecheck` and `npm run validate -- <episode>`.

## Stage 5: Produce

Run `validate`, `capture`, `tts` and `render` as listed in `videos/README.md`; `capture --only <name>` records one demo or terminal again, and `render --beats <id>,...` renders a few beats for a quick check.

- Capture, TTS and render can each outlast a 10 minute command timeout (a 10 minute draft renders in about 50 minutes). Run them in the background with output to a log file and wait for the completion notice.
- Compare the chapter seconds `tts` prints with the outline budgets. Lengthen a short chapter with substance or a `hold` that lets a demo breathe, not with filler words.
- Fix every `Beat '<id>': an animation ran N s past the end of the beat` the render logs.
- Render the final preset only when the user asks, or when the draft review is clean and gates are auto-approved.

## Stage 6: Review

1. Read `output/<episode>-review.md`: duration per chapter, beats without motion for 4 s or more, loudness (flat within 2 LU per 30 s window), narration sync (speech starts where its clip does), and the contact sheet order.
2. Open `output/<episode>-contact.png` (one frame per beat at its midpoint, 4 per row) and look at full-size frames wherever a thumbnail is unclear and at the start and end of complex beats; beat times are in `public/generated/<episode>/timing.json`:

   ```bash
   FFMPEG=$(node -e "import('@ffmpeg-installer/ffmpeg').then(m => console.log(m.default.path))")
   "$FFMPEG" -hide_banner -loglevel error -y -ss <seconds> -i output/<episode>-draft.mp4 -frames:v 1 /tmp/<episode>-<seconds>.png
   ```

3. Run `npm run transcribe -- <episode>` and fix each listed beat: a lexicon entry for a mispronounced term, or a rephrased line.
4. Check every frame for:
   - [ ] still beats from the report (add a camera move, a focus step or a pulse, or split the beat);
   - [ ] overlaps, clipped text (code cut at the card edge, labels outside their nodes, content outside the frame), sizes under 22 px, low contrast, crowded frames;
   - [ ] empty frames, a picture that contradicts the narration, values that do not match the sample;
   - [ ] framing: the subject centered, nothing important cut off, no large empty band;
   - [ ] the chapter header free and showing the right chapter;
   - [ ] arrows touching what they connect and pointing the way the value travels.
5. Fix and render again until the report lists no still beats (or each has a stated reason), loudness is flat, narration is in sync and the frames are clean. Narration changes need `tts` before `render`; a demo change needs `capture --only <name>`.
6. Present the video path, duration per chapter against the budget, the contact sheet, remaining warnings with reasons, doc mismatches, and library fixes (one commit each, for a separate pull request).

## Known pitfalls

- **Revideo cache.** A node with a shadow, a filter or opacity below 1 is drawn through a cache that misses paint changes of its children. Do not animate children's paint inside a shadowed container; `Card` puts its shadow on a background child for this reason.
- **Media nodes.** `yield` a `BrowserFrame` (or `windows.left` and `windows.right`) after adding it and before the first frame that draws it, or the clip may not show.
- **Fixed beat length.** Animations that outlive their beat run into the next one with a warning; size them with `narrator.duration(id)`.
- **Reactive text.** Set `Txt` text imperatively inside a `tween`, as `Terminal` and `CodeCard.show` do; a reactive text function can create nodes outside the scene.
- **Camera coordinates.** `focusOn` takes world coordinates, whose origin is the top left corner of the frame; use `focusOnPoint` for content coordinates. A target measured before an earlier move in the same `chain` is stale, so pass `() => node.absolutePosition()`.
- **Small motion counts as still.** The review's freeze detection ignores the background and small changes (a particle, a ticking number, a nudge). Give such beats a slow camera move across most of their length (`focusOnPoint` with a zoom of 1.05 to 1.15).
- **Eager arguments.** Helper arguments such as `all(...nodes.map(...))` are evaluated when the beat starts; read a list that fills during the beat inside a generator that runs later.
- **Tree layouts.** Flow layouts keep the definition order within a layer and connect top-down layers vertically. A fixed `position` still takes part in the layout, so a node meant to stand apart is better a separate one-node `FlowDiagram`.

## Commits

- Commit after each approved gate and at the end of build and review, for example `feat: tracking learning video script` or `fix: tracking video overlaps in the setup chapter`.
- Never add AI attribution (AGENTS.md), never commit `videos/public/generated/` or `videos/output/` (check `git status --short`), and do not push unless the user asks.
