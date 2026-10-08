# Learning videos

Narrated learning videos generated from the library documentation. Design: [docs/superpowers/specs/2026-10-08-learning-videos-design.md](../docs/superpowers/specs/2026-10-08-learning-videos-design.md).

## Setup

Requirements: Node 24, [uv](https://docs.astral.sh/uv/), .NET 10, an NVIDIA GPU for fast speech synthesis (CPU works, slowly).

```bash
cd videos
npm install
npx playwright install chromium
(cd tools/tts && uv sync)
```

## Producing an episode

```bash
npm run validate -- <episode>   # schema, code regions, demo and terminal references
npm run capture -- <episode>    # browser clips and terminal output (starts the sample app)
npm run tts -- <episode>        # narration audio (cached) and beat timing
npm run render -- <episode>     # draft at 15 fps, plus contact sheet and review report
npm run render -- <episode> --final
```

Output goes to `output/`: the MP4, an SRT subtitle file, `<episode>-contact.png` (one frame per beat) and `<episode>-review.md` (chapter durations and beats without motion).

## Layout

- `episodes/<episode>/`: `script.yaml` (narration and storyboard), `scenes/`, `demos/`, `capture.ts`, `sample/`
- `theme/`: shared scene runtime and components
- `domain/`: the coffee machine model and simulator used by all samples
- `tools/`: validate, TTS, capture and render commands
- `tools/tts/lexicon.yaml`: spoken forms for terms the voice mispronounces

Generated media lives in `public/generated/` and rendered videos in `output/`; both are gitignored.
