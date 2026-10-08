import {spawnSync} from 'node:child_process';
import {mkdirSync, readFileSync, writeFileSync} from 'node:fs';
import {join, resolve} from 'node:path';
import {validateEpisode} from './episode';
import {loadLexicon} from './lexicon';
import {buildNarration, buildTiming} from './narration';
import {episodeArgument, episodePaths, lexiconFile, ttsProjectDirectory, videosRoot} from './paths';

const paths = episodePaths(episodeArgument());
const script = await validateEpisode(paths.episodeDirectory);
const items = buildNarration(script, loadLexicon(lexiconFile));

mkdirSync(paths.audioDirectory, {recursive: true});
const requestFile = join(paths.generatedDirectory, 'narration-request.json');
writeFileSync(
  requestFile,
  JSON.stringify({
    outputDirectory: paths.audioDirectory,
    voice: script.voice === 'default' ? null : resolve(videosRoot, script.voice),
    items: items.map(item => ({key: item.key, text: item.text})),
  }, null, 2),
);

const result = spawnSync('uv', ['run', 'python', '-m', 'video_tts', requestFile], {cwd: ttsProjectDirectory, stdio: 'inherit'});
if (result.status !== 0) {
  throw new Error(`Speech synthesis failed with exit code ${result.status}`);
}

const durations = JSON.parse(readFileSync(join(paths.audioDirectory, 'durations.json'), 'utf8')) as Record<string, number>;
const timing = buildTiming(script, items, durations);
writeFileSync(paths.timingFile, JSON.stringify(timing, null, 2));

for (const chapter of script.chapters) {
  const seconds = timing.beats.filter(beat => beat.chapter === chapter.id).reduce((total, beat) => total + beat.duration, 0);
  console.log(`${chapter.id.padEnd(24)} ${seconds.toFixed(1).padStart(7)} s`);
}
console.log(`${'total'.padEnd(24)} ${timing.totalDuration.toFixed(1).padStart(7)} s`);
