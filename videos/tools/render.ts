import {existsSync, mkdirSync, readdirSync, readFileSync, renameSync, writeFileSync} from 'node:fs';
import {basename, join} from 'node:path';
import {renderVideo} from '@revideo/renderer';
import type {Timing} from '../theme/timing';
import {validateEpisode} from './episode';
import {probeVideoDuration, runFfmpeg} from './ffmpeg';
import {toSrt} from './narration';
import {episodeArgument, episodePaths, outputDirectory, videosRoot} from './paths';
import {writeReview} from './review';

const paths = episodePaths(episodeArgument());
const preset = process.argv.includes('--final') ? 'final' : 'draft';
await validateEpisode(paths.episodeDirectory);

if (!existsSync(paths.timingFile)) {
  throw new Error(`No timing for '${paths.episode}'. Run 'npm run tts -- ${paths.episode}' first.`);
}
const timing = JSON.parse(readFileSync(paths.timingFile, 'utf8')) as Timing;

const terminal: Record<string, string> = {};
if (existsSync(paths.terminalDirectory)) {
  for (const file of readdirSync(paths.terminalDirectory).filter(name => name.endsWith('.txt'))) {
    terminal[basename(file, '.txt')] = readFileSync(join(paths.terminalDirectory, file), 'utf8');
  }
}

// The renderer resolves the project file and the public/ media folder from the working directory.
process.chdir(videosRoot);
mkdirSync(outputDirectory, {recursive: true});
const videoFile = await renderVideo({
  projectFile: `./episodes/${paths.episode}/project.ts`,
  variables: {timing, terminal},
  settings: {
    outFile: `${paths.episode}-${preset}.mp4` as const,
    logProgress: true,
    // Ubuntu blocks Chrome's user namespace sandbox; the browser only loads this local project.
    puppeteer: {args: ['--no-sandbox']},
    viteConfig: {define: {__RENDER_PRESET__: JSON.stringify(preset)}},
  },
});

writeFileSync(join(outputDirectory, `${paths.episode}.srt`), toSrt(timing));
const absoluteVideoFile = join(videosRoot, videoFile);
// The renderer ends the audio track with the last clip; pad it so audio and video streams have the same length.
// The bundled ffmpeg ignores -shortest with apad and copied video, so the length is set explicitly.
const paddedVideoFile = join(outputDirectory, `${paths.episode}-${preset}.padded.mp4`);
runFfmpeg(['-i', absoluteVideoFile, '-c:v', 'copy', '-af', 'apad', '-t', probeVideoDuration(absoluteVideoFile).toString(), paddedVideoFile]);
renameSync(paddedVideoFile, absoluteVideoFile);
writeReview(absoluteVideoFile, timing, outputDirectory);
console.log(`Rendered ${absoluteVideoFile}`);
console.log(`Review ${join(outputDirectory, `${paths.episode}-review.md`)} and ${join(outputDirectory, `${paths.episode}-contact.png`)}`);
