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

Kokoro voices need the optional `kokoro` extra, which `npm run tts` installs on first use (`uv sync --extra kokoro` installs it up front).

## Producing an episode

```bash
npm run validate -- <episode>   # schema, code regions, demo and terminal references
npm run capture -- <episode>    # browser clips and terminal output (starts the sample app)
npm run tts -- <episode>        # narration audio (cached), sped up to the script's tempo, and beat timing
npm run render -- <episode>     # draft at 15 fps, plus contact sheet and review report
npm run render -- <episode> --final
```

Every scene gets a background layer and a chapter header ("Namotion.Interceptor | <video title> | <chapter title>", top left) from the scene runtime. Choose the background with `background:` in `script.yaml` (`drift`, `chapter-tint`, `edge-aurora` or `follow-light`) or `--background <variant>` on the render command; `--beats <id>,<id> --out <name>` renders only those beats, for quick comparisons.

Choose the narration voice with `voice:` in `script.yaml`:

- `chatterbox` (default): Chatterbox's built-in voice.
- `chatterbox-calm`: the same voice with a slower, more neutral delivery (lower exaggeration and CFG weight).
- `clone:<file>` and `clone-calm:<file>`: Chatterbox cloning the voice of a recording relative to `videos/`, at the default or calm settings. Put 10 to 20 s of clean speech in any audio format into `voices/` (gitignored, so personal recordings are never committed), for example `clone:voices/rico.m4a`. TTS converts it to mono WAV at 24 kHz with edge silence trimmed and loudness normalized, cached in `voices/.prepared/` by content, so a new recording at the same path is synthesized again.
- `kokoro:<name>`: a [Kokoro](https://huggingface.co/hexgrad/Kokoro-82M/blob/main/VOICES.md) English voice, for example `kokoro:af_heart`, `kokoro:am_michael` or `kokoro:bm_george`.

Audio is cached by text, engine, voice and settings, so switching back to a voice reuses its earlier audio.

`npm run audition` compares voices on three fixed lines: it writes each built-in voice's clips, loudness-normalized to -16 LUFS, plus `audition.wav` (every voice in a row) and a `README.md` with durations and words per minute to `output/voice-audition/`. Append more voices, optionally labelled, to include them: `npm run audition -- clone-rico=clone:voices/rico.wav clone-rico-calm=clone-calm:voices/rico.wav kokoro:bf_emma`. Synthesis is cached, so a rerun only synthesizes new voices.

To try a voice or tempo without changing `script.yaml`, pass `--voice <voice>` and `--tempo <factor>` to both `tts` and `render`, for example `npm run tts -- 03-connectors --voice clone:voices/rico.wav --tempo 1` and then `npm run render -- 03-connectors --voice clone:voices/rico.wav --tempo 1 --final`. The trial gets its own timing and render files (named after the voice and tempo, such as `03-connectors-final-clone-rico-x1.mp4`), so the regular ones stay as they are.

Narration plays at the voice's natural pace (`tempo` 1) by default; set `tempo:` in `script.yaml` to speed it up or slow it down per episode. TTS caches the synthesized speech and a pitch-preserving copy per tempo (ffmpeg `atempo`), so a tempo change does not synthesize again.

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
