# Learning Videos Design

## Goal

Produce one narrated learning video per library, 10 to 15 minutes each, generated from the library's markdown documentation. Each video goes from setup to a real sample, with the most important features first. A README intro video shows the full stack: hosting with dependency injection, a connector, Swagger UI and a GraphQL subscription.

Videos are built as code so they can be regenerated when the docs or the API change. A project skill turns one doc (or a group of docs) into an episode through gated stages, so each episode is brainstormed, reviewed and adjusted with the user.

## Scope of the first deliverable

1. Video pipeline: Revideo project, shared theme, TTS, capture and render tooling.
2. Shared coffee machine domain model and simulator.
3. The `learning-video` project skill.
4. Three episodes produced with the skill: README intro, Tracking, OPC UA.

The remaining libraries (Registry, Validation, Hosting, Dynamic, Connectors, MQTT, WebSocket, Modbus, AspNetCore, GraphQL, Blazor, MCP, Generator) follow as separate skill runs after this deliverable.

## Decisions

| Topic | Decision |
|---|---|
| Video engine | [Revideo](https://github.com/redotvideo/revideo) (MIT, Motion Canvas fork with headless rendering and per-scene audio). Scenes use only the API shared with Motion Canvas so a switch stays cheap. |
| Narration | AI voice, [Chatterbox](https://github.com/resemble-ai/chatterbox) (MIT), run locally on the GPU. Behind a `synthesize(text) -> wav` interface so Kokoro can replace it. |
| Live demos | Hybrid. Browser parts are scripted Playwright recordings of the running companion sample, shown in a styled browser frame. Terminal parts are real output captured as text and animated in the scene. |
| Code on screen | Loaded from compiled companion samples via region markers, never typed into scenes. |
| Visual style | Dark IDE base (calm, Catppuccin-like palette) with bold gradient accents (gradient chapter cards, glow on the active element, slow gradient background on transitions). |
| Location | `videos/` in this repository. Sources are committed; rendered output stays local and is gitignored. |
| Skill flow | Gated: outline approval, then script and storyboard approval, then build, produce and draft review. |

## Repository layout

```
videos/
├── README.md                  # local setup and commands
├── package.json               # Revideo, Playwright, zod
├── theme/                     # shared components and effects
├── tools/
│   ├── tts/                   # Python (uv): Chatterbox, lexicon, cache, timing.json
│   ├── capture/               # starts companion sample, runs Playwright demos and terminal capture
│   ├── schema/                # zod schema for script.yaml
│   └── render.ts              # headless render to MP4 and SRT, draft and final presets
├── domain/                    # shared .NET coffee machine model and simulator
├── episodes/
│   ├── smoke/                 # 20 s episode exercising every theme component
│   ├── 00-intro/
│   ├── 01-tracking/
│   └── 02-opcua/
├── public/generated/          # gitignored: audio cache, clips, terminal captures, timing.json
└── output/                    # gitignored: MP4s, subtitles, contact sheets, review reports
.claude/skills/learning-video/SKILL.md
```

Each episode folder contains:

- `outline.md`: chapters ranked by importance with time budgets and the doc sections deliberately left out (gate 1).
- `script.yaml`: chapters, each with beats; a beat has a narration line, a visual (component and action), an optional code region or diagram, and an optional demo reference (gate 2).
- `scenes/*.tsx`: Revideo scenes, with beat durations read from `timing.json`.
- `diagrams/*.ts`: typed diagram definitions.
- `demos/*.ts`: Playwright demo scripts.
- `sample/`: .NET companion app with C# `#region` markers for code shown on screen.

The .NET projects under `videos/` (`domain/` and each `sample/`) import `src/Directory.Build.props`, reference the libraries via `ProjectReference`, and are added to `src/Namotion.Interceptor.slnx` so CI builds them. A breaking API change then fails the build instead of silently invalidating a video.

## Pipeline

```
script.yaml ──> tools/tts ──> public/generated/<episode>/audio/*.wav + timing.json
sample/ + demos/ ──> tools/capture ──> public/generated/<episode>/clips/*.mp4 + terminal/*.txt
scenes + theme + timing + code regions + clips ──> tools/render ──> output/<episode>.mp4 + .srt
```

- `script.yaml` is validated against the zod schema before TTS and render: unknown components, missing code regions and demo references without a script fail fast.
- The code region loader fails the render when a marker is missing.
- Audio is cached by the hash of text, voice and settings. Only changed lines are regenerated, which also keeps unchanged audio identical across renders.
- `tools/tts/lexicon.yaml` rewrites terms before synthesis (for example `OPC UA` to "O P C U A", `kW` to "kilowatt"). Captions show the original text.
- Narration avoids saying "Namotion". The intro may name the library; elsewhere the voice says "the library" or the feature name ("the tracking package"), and package and namespace identifiers stay on screen only, not read aloud, unless reading one is needed to follow along. Its lexicon pronunciation is set after listening to the intro draft.
- Render presets: draft at 1080p and 15 fps, final at 1080p and 30 fps (the renderer ignores resolution scaling). Every render also writes a contact sheet, one frame per beat in a PNG grid, and a report of actual duration per chapter.

## Theme components

| Component | Purpose | Animation |
|---|---|---|
| `CodeCard` | code regions from the sample | type in, morph between versions, highlight and glow lines, zoom to a line |
| `FlowDiagram` | DI wiring, interceptor chain, connector pipeline, flowcharts | nodes and edges appear per step, particles travel along edges |
| `SequenceDiagram` | interactions such as write, interceptors, tracking, derived update, connector | lifelines appear, one message per beat, the active participant glows |
| `ObjectGraph` | subject trees with live values | values tick, changed nodes flash, attach and detach animate, derived values ripple upward |
| `LiveChart` | values over time | streaming line or sparkline |
| `LayerStack` | package architecture | blocks build up; library episodes open by zooming into their layer |
| `BrowserFrame` | Playwright clips | window chrome around the clip |
| `Terminal` | captured terminal output | typed commands, streamed output |
| `ChapterCard` | chapter transitions | kinetic gradient title over a moving gradient background |
| `Caption` | subtitle synced to narration | line by line |

Diagrams are typed TypeScript objects (nodes, edges, participants, messages, step numbers). Layout comes from elkjs, with optional manual position overrides. Mermaid diagrams in the docs are translated into this format so they can animate step by step.

Prebuilt layouts: code with diagram, code with browser, full-screen diagram.

## Motion language

Rules the skill checks at the storyboard gate:

1. No bullet slides. The voice explains, the screen shows. On-screen text is limited to captions, titles and short labels.
2. Something meaningful moves at least every 3 to 4 seconds. The render report runs ffmpeg freeze detection on the video and lists beats that stay still for 4 seconds or more.
3. Show transformations instead of cuts: code morphs into its next version, a property flies from code into a diagram node, a node expands into the live UI.
4. The camera moves continuously toward the current subject. Hard cuts only at chapter boundaries.
5. Values on screen come from the running sample.

Effects in `theme/`: shared-element transitions, data particles on edges, change ripples, spring physics for arriving elements, an ambient gradient background (subtle in the body, strong on chapter cards), focus pull (blur and dim what is not relevant), kinetic titles.

## Shared domain: coffee machine

One model used by all episodes, so viewers learn it once and each episode adds a layer.

```
CoffeeMachine
  Name, SerialNumber
  State                 Idle | Heating | Brewing | Descaling | Fault
  ActiveRecipe          Recipe?
  Boiler, Pump, WaterTank, BeanHopper
  Recipes               Dictionary<string, Recipe>
  CupsBrewed            int
  [Derived] IsReady     State == Idle && Boiler.IsHot && !WaterTank.IsLow
  [Derived] Status      "Ready", "Heating 78 °C", "Refill water", ...
  Brew(recipeName)      sets State, ActiveRecipe, Boiler.TargetTemperature, Pump.IsRunning

Boiler      Temperature (°C), [Range(85, 96)] TargetTemperature, HeaterOn, [Derived] IsHot
Pump        Pressure (bar), IsRunning
WaterTank   Level (%), [Derived] IsLow
BeanHopper  Level (%), [Range(1, 10)] GrindSize
Recipe      Name, WaterAmount (ml), [Range(85, 96)] Temperature
```

`Brew` is a plain method: the connectors and integrations synchronize properties, not methods, so each host calls it in its own idiom (for example a minimal API endpoint). The Tracking episode evolves it into a transactional `BrewAsync`.

The simulator is a hosted service: the boiler heats toward its target, a brew builds pump pressure to about 9 bar and consumes water and beans, and the machine returns to `Idle`. It takes a fixed seed and a video mode with scripted events (for example a boiler overheat at a given time) so captures are reproducible.

How a subject method reaches its context for the transaction (generated `IInterceptorSubject` members, a parameter, or a small service) is decided during planning by reading the generator output.

## Episodes in this deliverable

**00 Intro (README).** Package layers, install, define the coffee machine with `[InterceptorSubject]`, context setup, hosting with DI, the simulator as a hosted service, `MapSubjectWebApis` and a `POST /brew/{recipe}` endpoint, Swagger UI brewing an espresso, a GraphQL subscription showing temperature drop and recovery, and an OPC UA server exposure as a teaser for the connector episodes.

**01 Tracking.** Derived properties (`IsReady`, `Status`) with ripples, change streams, lifecycle when a recipe is added, then the transaction arc: an observer sees the inconsistent intermediate state of `Brew` and a flickering `IsReady`; the code morphs into `BrewAsync` with a transaction and the flicker disappears.

**02 OPC UA.** Exposing the machine as an OPC UA server, browsing it with a client, then a second process mirroring it as a remote control. Its `BrewAsync` runs with source transactions so all writes reach the server together; a rejected write (for example an out-of-range temperature) rolls back the whole brew. Built from the four OPC UA docs (overview, server, client, mapping).

## The learning-video skill

Invoked with one doc or a group of docs, for example `/learning-video docs/tracking.md` or `/learning-video docs/connectors-opcua*.md`. It creates or resumes `videos/episodes/<nn>-<name>/`, detects the current stage, and continues or revises from there.

1. **Analyze.** Read the docs, the shared domain, existing episodes (continuity, no repeated explanations) and the actual public API (no invented members).
2. **Outline, gate 1.** Write `outline.md`: chapters ranked by importance (setup, core feature, real sample, advanced) with time budgets totaling 10 to 15 minutes, plus the doc sections left out. Stop for approval.
3. **Script and storyboard, gate 2.** Write `script.yaml`, check it against the motion language and the narration rule on "Namotion", estimate duration from word count and flag chapters over budget. Stop for approval.
4. **Build.** Extend the shared domain if needed, write the companion sample with region markers, build it, write scenes, diagrams and demo scripts.
5. **Produce.** Run TTS, capture and a draft render.
6. **Review.** Present the contact sheet, chapter durations and stillness report. Apply feedback by editing only the affected beats and re-render. Final render on request.

Demo scripts are choreography: timed pauses set pacing, and condition waits are used where app state varies (for example waiting until the boiler reaches temperature).

## Local tooling

Node 24 and uv (installed in `~/.local`), ffmpeg from the `@ffmpeg-installer` npm package, Chatterbox with CUDA (RTX 3080, 10 GB), Playwright with its own Chromium. `videos/README.md` documents setup and commands.

## Risks

| Risk | Mitigation |
|---|---|
| Revideo rendering or maintenance problems | Shared Motion Canvas API in scenes; switch engines |
| Chatterbox quality, speed or nondeterminism | Audio cache; swap to Kokoro behind the synthesize interface |
| Long render times | Draft preset and contact sheets for review; final render only on request |
| Video and API drift | Samples and domain build in CI; region loader fails on missing markers |

## Build order

1. Tooling and the smoke episode (validates engine, TTS, capture, render and every theme component).
2. Shared coffee machine domain and simulator.
3. The `learning-video` skill.
4. Intro episode, through both gates.
5. Tracking episode, through both gates.
6. OPC UA episode, through both gates.

## Out of scope

- Rendering in CI.
- Publishing (YouTube, release assets) and committing rendered MP4s.
- Episodes beyond intro, Tracking and OPC UA.
- Exposing subject methods through connectors.
