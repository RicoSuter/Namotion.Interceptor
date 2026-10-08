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
npm run tts -- <episode>        # narration audio (cached), sped up to the script's tempo, and beat timing
npm run render -- <episode>     # draft at 15 fps, plus contact sheet and review report
npm run render -- <episode> --final
```

Narration plays at `tempo` 1.1 by default (set `tempo:` in `script.yaml` to change it per episode). TTS caches the synthesized speech and a pitch-preserving copy per tempo (ffmpeg `atempo`), so a tempo change does not synthesize again.

Output goes to `output/`: the MP4 with the narration as a soft subtitle track (English, off by default; nothing is burned into the picture), the same subtitles as an SRT file, `<episode>-contact.png` (one frame per beat) and `<episode>-review.md` (chapter durations and beats without motion).

## Layout

- `episodes/<episode>/`: `script.yaml` (narration and storyboard), `scenes/`, `demos/`, `capture.ts`, `sample/`
- `theme/`: shared scene runtime, style tokens (`palette.ts`, `style.ts`, `fonts.ts`) and components in `theme/components/`
- `domain/`: the coffee machine model and simulator used by all samples
- `tools/`: validate, TTS, capture and render commands
- `tools/tts/lexicon.yaml`: spoken forms for terms the voice mispronounces

## Demos

Demo pages are recorded at `deviceScaleFactor` 2 by default (set it in `capture.ts`), so a 1280 by 800 page becomes a 2560 by 1600 clip that stays crisp when the camera zooms in. Design pages for video: large type and the theme palette. Capture registers Inter and JetBrains Mono in every demo page, so pages can name those fonts without shipping them. Capture writes clip durations to `clips/clips.json`; `BrowserFrame.play` uses them to fit a clip to its beat (up to 4x faster, otherwise the start is trimmed).

Generated media lives in `public/generated/` and rendered videos in `output/`; both are gitignored.
