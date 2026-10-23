import {spawnSync} from 'node:child_process';
import {existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync} from 'node:fs';
import {tmpdir} from 'node:os';
import {join} from 'node:path';
import type {Timing} from '../theme/timing';
import {runFfmpeg} from './ffmpeg';
import {loadLexicon} from './lexicon';
import {applyNarrationOptions} from './narration';
import {episodeArgument, episodePaths, lexiconFile, outputDirectory, ttsProjectDirectory, videosRoot} from './paths';
import {loadScript} from './schema/script';
import {transcriptMatches, transcriptReport, type TranscriptResult} from './transcript';

// Usage: npm run transcribe -- <episode> [--voice <voice>] [--tempo <factor>]
// Transcribes every narrated beat with Whisper and lists the beats whose transcript differs from the narration.

const paths = episodePaths(episodeArgument());
const {timingFileName, variant} = applyNarrationOptions(loadScript(paths.episodeDirectory), process.argv.slice(3));
const timingFile = join(paths.generatedDirectory, timingFileName);
if (!existsSync(timingFile)) {
  throw new Error(`No timing for '${paths.episode}'. Run 'npm run tts -- ${paths.episode}' first.`);
}
const timing = JSON.parse(readFileSync(timingFile, 'utf8')) as Timing;
const beats = timing.beats.filter(beat => beat.audio !== null && beat.caption !== null);

const workDirectory = mkdtempSync(join(tmpdir(), 'transcribe-'));
try {
  // Whisper takes 16 kHz mono; the silence around each line stands in for the pause before it in the video,
  // without which Whisper tends to miss a short first word.
  const items = beats.map(beat => {
    const file = join(workDirectory, `${beat.id}.wav`);
    runFfmpeg(['-i', join(videosRoot, 'public', beat.audio!), '-af', 'aformat=channel_layouts=mono,aresample=16000,adelay=500,apad=pad_len=8000', file]);
    return {id: beat.id, file};
  });
  const requestFile = join(workDirectory, 'request.json');
  const resultFile = join(workDirectory, 'result.json');
  writeFileSync(requestFile, JSON.stringify({items, outputFile: resultFile}));
  const result = spawnSync('uv', ['run', 'python', '-m', 'video_tts.transcribe', requestFile], {cwd: ttsProjectDirectory, stdio: 'inherit'});
  if (result.status !== 0) {
    throw new Error(`Transcription failed with exit code ${result.status}`);
  }

  const heardById = new Map((JSON.parse(readFileSync(resultFile, 'utf8')) as Array<{id: string; heard: string}>).map(item => [item.id, item.heard]));
  const lexicon = loadLexicon(lexiconFile);
  const results: TranscriptResult[] = beats.map(beat => {
    const heard = heardById.get(beat.id) ?? '';
    return {id: beat.id, text: beat.caption!, heard, matches: transcriptMatches(beat.caption!, heard, lexicon)};
  });
  const report = transcriptReport(paths.episode, results);
  mkdirSync(outputDirectory, {recursive: true});
  const reportFile = join(outputDirectory, `${paths.episode}${variant ? `-${variant}` : ''}-transcript.md`);
  writeFileSync(reportFile, report);
  console.log(`\n${report}`);
  console.log(`Wrote ${reportFile}`);
} finally {
  rmSync(workDirectory, {recursive: true, force: true});
}
