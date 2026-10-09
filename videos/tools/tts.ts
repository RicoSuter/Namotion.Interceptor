import {writeFileSync} from 'node:fs';
import {join} from 'node:path';
import {probeDuration} from './ffmpeg';
import {loadLexicon} from './lexicon';
import {applyNarrationOptions, buildNarration, buildTiming} from './narration';
import {episodeArgument, episodePaths, lexiconFile} from './paths';
import {loadScript} from './schema/script';
import {applyTempo, resolveVoice, synthesize} from './speech';
import {parseVoice} from './voice';

const paths = episodePaths(episodeArgument());
// Narration needs only the script, so speech can be synthesized before the sample, demos and capture exist.
const {script, timingFileName} = applyNarrationOptions(loadScript(paths.episodeDirectory), process.argv.slice(3));
const voice = resolveVoice(parseVoice(script.voice), script.tempo);
const items = buildNarration(script, loadLexicon(lexiconFile));
synthesize(paths.audioDirectory, voice.request, items.map(item => ({key: item.key, text: item.text})));

// Speech the engine cannot bring to the tempo itself gets an atempo copy (see tempoPlan), so such a tempo change only re-runs this fast step.
const durations: Record<string, number> = {};
for (const item of items) {
  applyTempo(paths.audioDirectory, item.key, item.audioKey, voice.atempo);
  durations[item.audioKey] = probeDuration(join(paths.audioDirectory, `${item.audioKey}.wav`));
}
const timing = buildTiming(script, items, durations);
writeFileSync(join(paths.generatedDirectory, timingFileName), JSON.stringify(timing, null, 2));

for (const chapter of script.chapters) {
  const seconds = timing.beats.filter(beat => beat.chapter === chapter.id).reduce((total, beat) => total + beat.duration, 0);
  console.log(`${chapter.id.padEnd(24)} ${seconds.toFixed(1).padStart(7)} s`);
}
console.log(`${'total'.padEnd(24)} ${timing.totalDuration.toFixed(1).padStart(7)} s at tempo ${script.tempo} with voice ${script.voice}`);
