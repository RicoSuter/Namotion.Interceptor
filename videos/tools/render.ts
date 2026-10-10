import {existsSync, mkdirSync, readdirSync, readFileSync, renameSync, rmSync, writeFileSync} from 'node:fs';
import {basename, dirname, join} from 'node:path';
import {renderVideo} from '@revideo/renderer';
import {backgroundVariants, defaultBackground, type BackgroundVariant} from '../theme/backgrounds';
import type {Timing} from '../theme/timing';
import {beatRanges} from './beatRanges';
import {validateEpisode} from './episode';
import {probeVideoDuration, runFfmpeg} from './ffmpeg';
import {disableSubtitleTracks} from './mp4';
import {measureLoudnessArgs, narrationTrackArgs, normalizeNarrationArgs, parseIntegratedLoudness} from './narrationTrack';
import {applyNarrationOptions, toSrt} from './narration';
import {episodeArgument, episodePaths, outputDirectory, videosRoot} from './paths';
import {writeReview} from './review';

const paths = episodePaths(episodeArgument());
const preset = process.argv.includes('--final') ? 'final' : 'draft';
const narration = applyNarrationOptions(await validateEpisode(paths.episodeDirectory), process.argv.slice(3));
const script = narration.script;
const timingFile = join(paths.generatedDirectory, narration.timingFileName);
// A narration override (--voice, --tempo) renders to its own files, next to the regular render.
const variant = narration.variant ? `-${narration.variant}` : '';
const background = (option('--background') ?? script.background ?? defaultBackground) as BackgroundVariant;
if (!backgroundVariants.includes(background)) {
  throw new Error(`Unknown background '${background}'. Choose one of ${backgroundVariants.join(', ')}.`);
}
const beats = option('--beats')?.split(',');

if (!existsSync(timingFile)) {
  const options = narration.variant ? ` --voice ${script.voice} --tempo ${script.tempo}` : '';
  throw new Error(`No timing for '${paths.episode}'. Run 'npm run tts -- ${paths.episode}${options}' first.`);
}
const timing = JSON.parse(readFileSync(timingFile, 'utf8')) as Timing;

const terminal: Record<string, string> = {};
if (existsSync(paths.terminalDirectory)) {
  for (const file of readdirSync(paths.terminalDirectory).filter(name => name.endsWith('.txt'))) {
    terminal[basename(file, '.txt')] = readFileSync(join(paths.terminalDirectory, file), 'utf8');
  }
}

const clips: Record<string, number> = existsSync(paths.clipsFile) ? JSON.parse(readFileSync(paths.clipsFile, 'utf8')) : {};
const marks: Record<string, Record<string, number>> = existsSync(paths.marksFile) ? JSON.parse(readFileSync(paths.marksFile, 'utf8')) : {};

// The renderer resolves the project file and the public/ media folder from the working directory.
process.chdir(videosRoot);
mkdirSync(outputDirectory, {recursive: true});

async function render(outFile: string, range?: [number, number]): Promise<string> {
  return renderVideo({
    projectFile: `./episodes/${paths.episode}/project.ts`,
    variables: {timing, terminal, clips, marks, background},
    settings: {
      outFile: outFile as `${string}.mp4`,
      logProgress: true,
      // Ubuntu blocks Chrome's user namespace sandbox; the browser only loads this local project.
      puppeteer: {args: ['--no-sandbox']},
      // The default browser encoder offers no quality control; the ffmpeg exporter goes through tools/encoder.mjs.
      projectSettings: {exporter: {name: '@revideo/core/ffmpeg', options: {format: 'mp4'}}, ...(range ? {range} : {})},
      ffmpeg: {ffmpegPath: join(videosRoot, 'tools', 'encoder.mjs')},
      viteConfig: {define: {__RENDER_PRESET__: JSON.stringify(preset)}},
    },
  });
}

if (beats) {
  // Only the listed beats, for example to compare a theme change: one render per range, joined without subtitles.
  const name = option('--out') ?? `${paths.episode}-${preset}-${background}${variant}`;
  const ranges = beatRanges(timing, beats);
  const parts: string[] = [];
  for (const [index, range] of ranges.entries()) {
    const rendered = join(videosRoot, await render(`${name.replaceAll('/', '-')}-part${index}.mp4`, [range.start, range.end]));
    // Each part gets the narration of its own beats, so a part's frame rounding never shifts the next part's audio.
    const rangeBeats = timing.beats.filter(beat => beats.includes(beat.id) && beat.start >= range.start && beat.start < range.end);
    const narrationFile = rendered.replace(/\.mp4$/, '-narration.wav');
    writeNarrationTrack(new Set(rangeBeats.map(beat => beat.id)), narrationFile);
    const part = rendered.replace(/\.mp4$/, '-narrated.mp4');
    runFfmpeg(['-i', rendered, '-i', narrationFile, '-map', '0:v', '-map', '1:a', '-c:v', 'copy', '-c:a', 'aac', '-b:a', '192k', part]);
    rmSync(rendered);
    rmSync(narrationFile);
    parts.push(part);
  }
  const joinedFile = join(outputDirectory, `${name}.mp4`);
  mkdirSync(dirname(joinedFile), {recursive: true});
  const listFile = join(outputDirectory, `${name}-parts.txt`);
  writeFileSync(listFile, parts.map(part => `file '${part}'`).join('\n'));
  runFfmpeg(['-f', 'concat', '-safe', '0', '-i', listFile, '-c', 'copy', joinedFile]);
  for (const file of [listFile, ...parts]) {
    rmSync(file);
  }
  console.log(`Rendered ${joinedFile} (${ranges.map(range => `${range.start.toFixed(1)} to ${range.end.toFixed(1)} s`).join(', ')})`);
  process.exit(0);
}

const videoFile = await render(`${paths.episode}-${preset}${variant}.mp4`);

const subtitleFile = join(outputDirectory, `${paths.episode}${variant}.srt`);
writeFileSync(subtitleFile, toSrt(timing));
const absoluteVideoFile = join(videosRoot, videoFile);
const narrationFile = join(outputDirectory, `${paths.episode}${variant}-narration.wav`);
writeNarrationTrack(null, narrationFile);
// One pass copies the video, replaces the renderer's audio with the narration track and adds the narration text as
// a soft subtitle track. The bundled ffmpeg ignores -shortest with copied video, so the length is set explicitly.
const finishedVideoFile = join(outputDirectory, `${paths.episode}-${preset}${variant}.finished.mp4`);
runFfmpeg([
  '-i', absoluteVideoFile, '-i', narrationFile, '-i', subtitleFile,
  '-map', '0:v', '-map', '1:a', '-map', '2:s',
  '-c:v', 'copy', '-c:a', 'aac', '-b:a', '192k', '-af', 'apad', '-c:s', 'mov_text',
  '-metadata:s:s:0', 'language=eng', '-disposition:s:0', '0',
  '-t', probeVideoDuration(absoluteVideoFile).toString(), finishedVideoFile,
]);
// The bundled mov muxer enables the first subtitle track despite its disposition.
disableSubtitleTracks(finishedVideoFile);
renameSync(finishedVideoFile, absoluteVideoFile);
rmSync(narrationFile);
writeReview(absoluteVideoFile, timing, outputDirectory, join(videosRoot, 'public'));
console.log(`Rendered ${absoluteVideoFile}`);
console.log(`Review ${join(outputDirectory, `${paths.episode}-review.md`)} and ${join(outputDirectory, `${paths.episode}-contact.png`)}`);

/** Writes the narration track of the given beats (all when null), normalized to the narration loudness. */
function writeNarrationTrack(beatIds: ReadonlySet<string> | null, output: string): void {
  const joined = output.replace(/\.wav$/, '-joined.wav');
  runFfmpeg(narrationTrackArgs(timing, beatIds, join(videosRoot, 'public'), joined));
  runFfmpeg(normalizeNarrationArgs(joined, parseIntegratedLoudness(runFfmpeg(measureLoudnessArgs(joined))), output));
  rmSync(joined);
}

function option(name: string): string | undefined {
  const index = process.argv.indexOf(name);
  return index >= 0 ? process.argv[index + 1] : undefined;
}
