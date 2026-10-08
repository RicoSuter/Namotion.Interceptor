import {existsSync, mkdirSync, readdirSync, readFileSync, renameSync, writeFileSync} from 'node:fs';
import {basename, join} from 'node:path';
import {renderVideo} from '@revideo/renderer';
import type {Timing} from '../theme/timing';
import {validateEpisode} from './episode';
import {probeVideoDuration, runFfmpeg} from './ffmpeg';
import {disableSubtitleTracks} from './mp4';
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

const clips: Record<string, number> = existsSync(paths.clipsFile) ? JSON.parse(readFileSync(paths.clipsFile, 'utf8')) : {};
const marks: Record<string, Record<string, number>> = existsSync(paths.marksFile) ? JSON.parse(readFileSync(paths.marksFile, 'utf8')) : {};

// The renderer resolves the project file and the public/ media folder from the working directory.
process.chdir(videosRoot);
mkdirSync(outputDirectory, {recursive: true});
const videoFile = await renderVideo({
  projectFile: `./episodes/${paths.episode}/project.ts`,
  variables: {timing, terminal, clips, marks},
  settings: {
    outFile: `${paths.episode}-${preset}.mp4` as const,
    logProgress: true,
    // Ubuntu blocks Chrome's user namespace sandbox; the browser only loads this local project.
    puppeteer: {args: ['--no-sandbox']},
    // The default browser encoder offers no quality control; the ffmpeg exporter goes through tools/encoder.mjs.
    projectSettings: {exporter: {name: '@revideo/core/ffmpeg', options: {format: 'mp4'}}},
    ffmpeg: {ffmpegPath: join(videosRoot, 'tools', 'encoder.mjs')},
    viteConfig: {define: {__RENDER_PRESET__: JSON.stringify(preset)}},
  },
});

const subtitleFile = join(outputDirectory, `${paths.episode}.srt`);
writeFileSync(subtitleFile, toSrt(timing));
const absoluteVideoFile = join(videosRoot, videoFile);
// One pass copies the video, pads the audio and adds the narration as a soft subtitle track. The renderer ends
// the audio track with the last clip; the bundled ffmpeg ignores -shortest with apad and copied video, so the
// length is set explicitly.
const finishedVideoFile = join(outputDirectory, `${paths.episode}-${preset}.finished.mp4`);
runFfmpeg([
  '-i', absoluteVideoFile, '-i', subtitleFile,
  '-map', '0:v', '-map', '0:a?', '-map', '1:s',
  '-c:v', 'copy', '-af', 'apad', '-c:s', 'mov_text',
  '-metadata:s:s:0', 'language=eng', '-disposition:s:0', '0',
  '-t', probeVideoDuration(absoluteVideoFile).toString(), finishedVideoFile,
]);
// The bundled mov muxer enables the first subtitle track despite its disposition.
disableSubtitleTracks(finishedVideoFile);
renameSync(finishedVideoFile, absoluteVideoFile);
writeReview(absoluteVideoFile, timing, outputDirectory);
console.log(`Rendered ${absoluteVideoFile}`);
console.log(`Review ${join(outputDirectory, `${paths.episode}-review.md`)} and ${join(outputDirectory, `${paths.episode}-contact.png`)}`);
