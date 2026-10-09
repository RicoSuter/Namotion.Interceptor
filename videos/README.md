# Learning videos

Narrated learning videos generated from the library documentation. Episodes are produced with the `learning-video` skill in `.claude/skills/learning-video/`.

## Setup

Requirements: Node 24, [uv](https://docs.astral.sh/uv/), .NET 10, an NVIDIA GPU for fast speech synthesis (CPU works, slowly).

```bash
cd videos
npm install
npx playwright install chromium
(cd tools/tts && uv sync)
```

Chatterbox voices need the optional `chatterbox` extra, which `npm run tts` installs on first use (`uv sync --extra chatterbox` installs it up front).

## Producing an episode

```bash
npm run validate -- <episode>   # duration per chapter, then schema, code regions, demo and terminal references
npm run capture -- <episode>    # browser clips and terminal output (starts the sample app)
npm run tts -- <episode>        # narration audio (cached), sped up to the script's tempo, and beat timing
npm run render -- <episode>     # draft at 15 fps, plus contact sheet and review report
npm run render -- <episode> --final
```

Every scene gets a background layer and a chapter header ("Namotion.Interceptor | <video title> | <chapter title>", top left) from the scene runtime. Choose the background with `background:` in `script.yaml` (`drift`, `chapter-tint`, `edge-aurora` or `follow-light`) or `--background <variant>` on the render command; `--beats <id>,<id> --out <name>` renders only those beats, for quick comparisons.

Choose the narration voice with `voice:` and its speed with `tempo:` in `script.yaml` (1 is the voice's natural pace, 0.5 to 2 allowed). Both are optional: the default is `kokoro:am_michael` at tempo 1.32, the voice with the fewest transcription errors when checked with Whisper, and Kokoro synthesizes a 10 minute episode in about a minute instead of the ten Chatterbox needs. Other voices:

- `kokoro:<name>`: any [Kokoro](https://huggingface.co/hexgrad/Kokoro-82M/blob/main/VOICES.md) English voice, for example `kokoro:af_heart` or `kokoro:bm_george`.
- `chatterbox`: Chatterbox's built-in voice.
- `chatterbox-calm`: the same voice with a slower, more neutral delivery (lower exaggeration and CFG weight).

Audio is cached by text, engine, voice and settings, so switching back to a voice reuses its earlier audio.

`npm run audition` compares voices on three fixed lines: it writes each built-in voice's clips, loudness-normalized to -16 LUFS, plus `audition.wav` (every voice in a row) and a `README.md` with durations and words per minute to `output/voice-audition/`. Append more voices, optionally labelled and with a tempo after `@`, to include them: `npm run audition -- quiet=chatterbox-calm@1.1 kokoro:bf_emma kokoro:am_michael@1.32`. Synthesis is cached, so a rerun only synthesizes new voices.

To try a voice or tempo without changing `script.yaml`, pass `--voice <voice>` and `--tempo <factor>` to both `tts` and `render`, for example `npm run tts -- smoke --voice kokoro:bf_emma --tempo 1.2` and then `npm run render -- smoke --voice kokoro:bf_emma --tempo 1.2 --final`. The trial gets its own timing and render files (named after the voice and tempo, such as `smoke-final-kokoro-bf_emma-x1.2.mp4`), so the regular ones stay as they are.

The tempo maps to the engine like this:

| Voice | Tempo up to 1.25 | Tempo above 1.25 |
|---|---|---|
| `kokoro:<name>` | Kokoro's own `speed` | `speed` 1.25, then ffmpeg `atempo` for the rest (tempo 1.32 is 1.25 times 1.056) |
| `chatterbox`, `chatterbox-calm` | ffmpeg `atempo` on speech at the natural pace | the same |

Kokoro's own speed keeps the prosody natural, but Kokoro rounds every sound to whole frames, and above about 1.3 short sounds collapse (a line's first "a" or "the" goes missing, "claims" becomes "claimed"), so faster tempos continue with the pitch-preserving `atempo`. TTS caches the synthesized speech by Kokoro's speed and the `atempo` copies by their factor, so a tempo change that only changes the `atempo` factor does not synthesize again.

## Length

Episodes run 10 to 12 minutes, 10 unless the user asks for more. With the default voice a minute of finished video holds about 165 words, the pauses between beats included (1608 words in 9.9 minutes and 1881 words in 10.9 minutes on two trial episodes), so budget about 1650 words for 10 minutes. `npm run validate` prints the words and seconds per chapter: estimated from the script's voice and tempo (`speechRates` in `tools/estimate.ts`), or measured from `timing.json` once `npm run tts` has run.

Output goes to `output/`: the MP4 with the narration at -16 LUFS (one gain for the whole track and a peak limiter at -2 dBFS, so every voice plays at the same level), the narration text as a soft subtitle track (English, off by default), the same subtitles as an SRT file, `<episode>-contact.png` (one frame per beat) and `<episode>-review.md` (chapter durations and beats without motion).

## Layout

- `episodes/<episode>/`: `script.yaml` (narration and storyboard), `scenes/`, `demos/`, `capture.ts`, `sample/`
- `theme/`: shared scene runtime, style tokens (`palette.ts`, `style.ts`, `fonts.ts`) and components in `theme/components/`
- `domain/`: the coffee machine model and simulator used by all samples
- `tools/`: validate, TTS, capture and render commands
- `tools/tts/lexicon.yaml`: spoken forms for terms the voice mispronounces

## Demos

Demo pages are recorded at `deviceScaleFactor` 2 by default (set it in `capture.ts`), so a 1280 by 800 page becomes a 2560 by 1600 clip that stays crisp when the camera zooms in. Design pages for video: large type and the theme palette. Capture registers Inter and JetBrains Mono in every demo page, so pages can name those fonts without shipping them. Capture writes clip durations to `clips/clips.json`; `BrowserFrame.play` uses them to fit a clip to its beat (up to 4x faster, otherwise the start is trimmed).

Generated media lives in `public/generated/` and rendered videos in `output/`; both are gitignored.
