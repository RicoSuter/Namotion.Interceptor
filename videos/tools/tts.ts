import {existsSync, renameSync, writeFileSync} from 'node:fs';
import {join} from 'node:path';
import {probeDuration, runFfmpeg} from './ffmpeg';
import {loadLexicon} from './lexicon';
import {applyNarrationOptions, buildNarration, buildTiming} from './narration';
import {episodeArgument, episodePaths, lexiconFile} from './paths';
import {loadScript} from './schema/script';
import {resolveVoice, synthesize} from './speech';
import {parseVoice} from './voice';

const paths = episodePaths(episodeArgument());
// Narration needs only the script, so speech can be synthesized before the sample, demos and capture exist.
const {script, timingFileName} = applyNarrationOptions(loadScript(paths.episodeDirectory), process.argv.slice(3));
const voice = resolveVoice(parseVoice(script.voice));
const items = buildNarration(script, loadLexicon(lexiconFile), voice.identity);
synthesize(paths.audioDirectory, voice.request, items.map(item => ({key: item.key, text: item.text})));

// The tempo is applied to copies of the synthesized lines, so a tempo change only re-runs this fast step.
const durations: Record<string, number> = {};
for (const item of items) {
  const audioFile = join(paths.audioDirectory, `${item.audioKey}.wav`);
  if (!existsSync(audioFile)) {
    // Write then rename so an interrupted run never leaves a truncated file in the cache.
    const temporaryFile = join(paths.audioDirectory, `${item.audioKey}.tmp.wav`);
    runFfmpeg(['-i', join(paths.audioDirectory, `${item.key}.wav`), '-af', `atempo=${script.tempo}`, temporaryFile]);
    renameSync(temporaryFile, audioFile);
  }
  durations[item.audioKey] = probeDuration(audioFile);
}
const timing = buildTiming(script, items, durations);
writeFileSync(join(paths.generatedDirectory, timingFileName), JSON.stringify(timing, null, 2));

for (const chapter of script.chapters) {
  const seconds = timing.beats.filter(beat => beat.chapter === chapter.id).reduce((total, beat) => total + beat.duration, 0);
  console.log(`${chapter.id.padEnd(24)} ${seconds.toFixed(1).padStart(7)} s`);
}
console.log(`${'total'.padEnd(24)} ${timing.totalDuration.toFixed(1).padStart(7)} s at tempo ${script.tempo} with voice ${script.voice}`);
