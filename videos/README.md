# Learning videos

Narrated learning videos generated from the library documentation. Episodes are produced with the `learning-video` skill in `.claude/skills/learning-video/`.

## Series

Each episode covers the docs of one row and lives in `episodes/<folder>/`. Status is `open`, `in progress` or `done`; the next episode is the lowest `open` row.

| # | Folder | Docs | Status |
|---|---|---|---|
| 00 | `00-intro` | `README.md`, `docs/interceptor.md`, `docs/generator.md` | open |
| 01 | `01-tracking` | `docs/tracking.md`, `docs/tracking-transactions.md` | open |
| 02 | `02-subject-design` | `docs/subject-guidelines.md` (mainly the rules and contracts every subject must follow, modeling recommendations second; validation, registry attributes and hosted subjects only briefly, with a pointer to their episodes) | open |
| 03 | `03-validation` | `docs/validation.md` | open |
| 04 | `04-registry` | `docs/registry.md`, `docs/dynamic.md` | open |
| 05 | `05-hosting` | `docs/hosting.md` | open |
| 06 | `06-connectors` | `docs/connectors.md`, `docs/connectors-monitoring.md` | open |
| 07 | `07-opcua` | `docs/connectors-opcua.md`, `docs/connectors-opcua-client.md`, `docs/connectors-opcua-server.md`, `docs/connectors-opcua-mapping.md` | open |
| 08 | `08-mqtt` | `docs/connectors-mqtt.md` | open |
| 09 | `09-websocket` | `docs/connectors-websocket.md`, `docs/connectors-subject-updates.md` | open |
| 10 | `10-modbus` | `docs/connectors-modbus.md` | open |
| 11 | `11-aspnetcore` | `docs/aspnetcore.md` (mention subject updates from `docs/connectors-subject-updates.md` briefly) | open |
| 12 | `12-mcp` | `docs/mcp.md` | open |
| 13 | `13-blazor` | `docs/blazor.md` | open |
| 14 | `14-graphql` | `docs/graphql.md` | open |

`episodes/smoke/` is not part of the series: a short episode that uses every shared component and helper, rendered to check changes to `theme/` and `tools/`.

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
npm run transcribe -- <episode> # Whisper transcript of every narrated beat, lists the beats that differ from the script
```

Every scene gets a background layer and a chapter header ("Namotion.Interceptor | <video title> | <chapter title>", top left) from the scene runtime. Choose the background with `background:` in `script.yaml` or `--background <variant>` on the render command; `--beats <id>,<id> --out <name>` renders only those beats, for quick comparisons.

| Background | Look |
|---|---|
| `drift` (default) | four large, soft blue, purple and teal glows drifting on a one minute loop |
| `chapter-tint` | the same glows with a hue pair per chapter, cross fading when the chapter changes |
| `edge-aurora` | glows along the top and bottom edges that slowly breathe; the center stays neutral |
| `follow-light` | one soft light behind the camera's focus that reaches a new target ahead of the camera |

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

Episodes run 10 to 12 minutes, 10 by default. With the default voice a minute of finished video holds about 165 words, the pauses between beats included (1608 words in 9.9 minutes and 1881 words in 10.9 minutes on two trial episodes), so budget about 1650 words for 10 minutes. `npm run validate` prints the words and seconds per chapter: estimated from the script's voice and tempo (`speechRates` in `tools/estimate.ts`), or measured from `timing.json` once `npm run tts` has run.

## Output and checks

Output goes to `output/`: the MP4 with the narration at -16 LUFS (one gain for the whole track and a peak limiter at -2 dBFS, so every voice plays at the same level), the narration text as a soft subtitle track (English, off by default), the same subtitles as an SRT file, `<episode>-contact.png` (one frame per beat) and `<episode>-review.md`: chapter durations, beats without motion, loudness of the whole video and of every 30 s window, and whether each beat's speech starts in the video where it starts in its narration clip. `npm run transcribe` writes `<episode>-transcript.md`; it runs Whisper (`openai/whisper-small.en`, downloaded on first use) on the narration clips of the last `npm run tts`, and word boundaries, punctuation, number words and the lexicon's spoken forms do not count as differences.

## Layout

- `episodes/<episode>/`: `script.yaml` (narration and storyboard), `scenes/`, `demos/`, `capture.ts`, `sample/`
- `theme/`: shared scene runtime, style tokens (`palette.ts`, `style.ts`, `fonts.ts`) and components in `theme/components/`
- `domain/`: the coffee machine model and simulator used by all samples
- `tools/`: validate, TTS, capture and render commands
- `tools/tts/lexicon.yaml`: spoken forms for terms the voice mispronounces

## Demos

Demo pages are recorded at `deviceScaleFactor` 2 by default (set it in `capture.ts`), so a 1280 by 800 page becomes a 2560 by 1600 clip that stays crisp when the camera zooms in. Design pages for video: large type and the theme palette. Capture registers Inter and JetBrains Mono in every demo page, so pages can name those fonts without shipping them. Capture writes clip durations to `clips/clips.json`; `BrowserFrame.play` uses them to fit a clip to its beat (up to 4x faster, otherwise the start is trimmed).

Generated media lives in `public/generated/` and rendered videos in `output/`; both are gitignored.

## Tests

```bash
npm run typecheck
npx vitest run                  # TypeScript unit tests in theme/ and tools/
(cd tools/tts && uv run pytest) # Python unit tests of the speech and transcription package
dotnet test domain/Coffee.Tests
```
