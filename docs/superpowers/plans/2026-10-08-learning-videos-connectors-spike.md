# Learning Videos Connectors Spike

> **For agentic workers:** Runs after [the foundation plan](2026-10-08-learning-videos-foundation.md) is complete. This is a spike: tasks state goals and acceptance criteria instead of full code, and the result is reviewed as a whole afterwards. Run uninterrupted; the skill's gates are auto-approved.

**Goal:** A complete, roughly 10 minute narrated video for `docs/connectors.md`, produced by a first version of the `learning-video` skill with a minimal theme in the clean macOS-like style.

**Spec:** [docs/superpowers/specs/2026-10-08-learning-videos-design.md](../specs/2026-10-08-learning-videos-design.md), in particular "Spike: Connectors episode", "Theme components" and "Motion language".

---

### Task S1: Style foundation

- Add `@fontsource/inter` and `@fontsource/jetbrains-mono` to `videos/package.json` and load them in `theme/project.ts` so the renderer's browser has them before the first frame.
- Replace `theme/palette.ts` with macOS dark system colors: background `#1c1c1e`, card `#2c2c2e`, elevated `#3a3a3c`, separator `#48484a`, text `#f5f5f7`, secondary text `#a1a1a6`, blue `#0a84ff`, cyan `#64d2ff`, green `#30d158`, orange `#ff9f0a`, pink `#ff375f`, purple `#bf5af2`. Keep the code highlighter readable on the card color.
- Add `theme/style.ts` with shared tokens: corner radius 24 for cards and 12 for small elements, shadow (`shadowColor` black at 45 %, `shadowBlur` 40, `shadowOffsetY` 12), font families, spacing scale, and standard easings (`easeInOutCubic` for camera, a spring for arrivals).

**Done when:** a smoke frame shows Inter in the caption and JetBrains Mono in the code card, with no visible card outline and a soft shadow.

### Task S2: Minimal theme components

Each component in `theme/components/`, used in the smoke episode so the contact sheet shows it:

- `Card`: rounded, shadowed, no stroke.
- `CodeCard`: `Card` with a `Code` node, filename tab, `show(code)`, `morph(code)`, `focus(lines)` (others dim), scroll to keep focused lines visible.
- `Caption`: pill at the bottom, fades between lines; replaces the plain caption in `Narrator`.
- `ChapterCard`: full-screen title over a slow, subtle gradient wash, letters rise in with stagger.
- `BrowserFrame`: macOS window with traffic lights and URL field around a `Video` clip.
- `Terminal`: macOS terminal window that types the prompt line and streams captured output.
- `FlowDiagram`: nodes and edges from a typed definition, laid out with elkjs (`yield` the layout promise), edges drawn as smooth cubic curves with rounded caps and small arrowheads, `reveal(step)` and `pulse(edge)` (a particle travels the edge).
- `SequenceDiagram`: participants with lifelines, `message(from, to, label)` animates a curved arrow, active participant highlighted.
- `Camera`: wraps scene content; `focusOn(node)` and `reset()` with eased zoom and pan.

**Done when:** `npm run render -- smoke` shows every component in the contact sheet, edges are curved with no hard corners, and the review report flags no beat as still.

### Task S3: Skill draft

Create `.claude/skills/learning-video/SKILL.md`, following the spec's skill section:

- Inputs: one doc or a group of docs.
- Stages: analyze, outline (gate 1), script and storyboard (gate 2), build, produce, review. A spike run may auto-approve gates when the user said so.
- Rules: self-contained episodes; "Namotion" only in the intro or where an identifier is needed; motion language; no bullet slides; code only from compiled regions; read the public API before writing code.
- Duration budgeting: about 150 words per minute; a 10 minute episode is about 1300 to 1500 narrated words.
- Templates for `outline.md` and `script.yaml` inside the skill folder.
- Commands to run (`validate`, `capture`, `tts`, `render`) and how to read `review.md` and the contact sheet.

**Done when:** the skill file reads as complete instructions a fresh session could follow without this plan.

### Task S4: Connectors episode via the skill

Follow the skill for `docs/connectors.md` into `videos/episodes/03-connectors/`:

- Outline: chapters ranked by importance (what a connector is, sources and servers; data flow inbound and outbound; live sample; write consistency, batching, retry queue; implementing a custom source), about 10 minutes.
- Samples: `sample/Server` (coffee machine, simulator, WebSocket embedded handler, `/status` page) and `sample/Client` (mirrored machine via the WebSocket client, `/status` page, `POST /brew/{recipe}`), both in the solution and building. Code for the custom source chapter lives in a compiled file too.
- Demos: Playwright clips of both status pages, including a brew started on the client that shows up on the server.
- Diagrams for data flow and write paths, translated from the doc's Mermaid flowchart where it fits.
- Run `validate`, `capture`, `tts`, `render --final`.

**Done when:** `output/03-connectors-final.mp4` is between 9 and 12 minutes, the review report flags no still beats (or each remaining one is listed with a reason), and the contact sheet has been inspected frame by frame for overlaps, clipped text and empty frames, with fixes applied and re-rendered.

### Task S5: Report

Summarize for the user: video path, duration per chapter, what worked, what looks weak, and the open questions for the theme plan.
