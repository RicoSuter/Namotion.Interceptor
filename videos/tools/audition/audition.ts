import {mkdirSync, writeFileSync} from 'node:fs';
import {join} from 'node:path';
import {probeDuration, runFfmpeg} from '../ffmpeg';
import {applyLexicon, loadLexicon} from '../lexicon';
import {synthesisKey, tempoKey} from '../narration';
import {lexiconFile, outputDirectory} from '../paths';
import {applyTempo, resolveVoice, synthesize} from '../speech';
import {parseVoice} from '../voice';
import {concatWithGapsArgs, countWords, measureLoudnessArgs, normalizeLoudnessArgs, parseLoudnessMeasurement} from './audio';
import {auditionReadme, lineGapSeconds, parseAuditionVoice, voiceGapSeconds, type AuditionLine, type AuditionResult} from './report';

// Usage: npm run audition [-- [<label>=]<voice>[@<tempo>] ...]
// Auditions the built-in voices and then the given ones, for example clone-rico=clone:voices/rico.wav or kokoro:am_michael@1.3.

const builtInVoices = ['chatterbox', 'chatterbox-calm', 'kokoro:af_heart', 'kokoro:am_michael', 'kokoro:bm_george'];

const lines: AuditionLine[] = [
  {id: 'a', purpose: 'intro', text: 'Your coffee machine has a boiler, a pump and a water tank. In this video, the state of that machine leaves the process, and comes back.'},
  {id: 'b', purpose: 'technical', text: 'The client source claims the grind size before it connects, so changes made while the device is offline are queued, and sent once the connection is back.'},
  {id: 'c', purpose: 'calm explanation', text: 'Nothing is lost while the network is down. When the connection returns, the source compares what it queued with the current state, and sends only what still matters.'},
];

const auditionDirectory = join(outputDirectory, 'voice-audition');
const cacheDirectory = join(auditionDirectory, '.cache');
const lexicon = loadLexicon(lexiconFile);
const words = lines.reduce((total, line) => total + countWords(line.text), 0);

const voices = [...builtInVoices, ...process.argv.slice(2)].map(parseAuditionVoice);

mkdirSync(cacheDirectory, {recursive: true});
const results: AuditionResult[] = [];
for (const {label, spec, tempo} of voices) {
  console.log(`\n${label} (${spec} at tempo ${tempo})`);
  const voice = parseVoice(spec);
  const resolved = resolveVoice(voice, tempo);
  const items = lines.map(line => {
    const text = applyLexicon(line.text, lexicon, voice.engine);
    const key = synthesisKey(text, resolved.identity);
    return {line, key, audioKey: tempoKey(key, resolved.atempo), text};
  });
  synthesize(cacheDirectory, resolved.request, items.map(item => ({key: item.key, text: item.text})));

  const clips: string[] = [];
  let peakBeforeNormalization = -Infinity;
  for (const item of items) {
    applyTempo(cacheDirectory, item.key, item.audioKey, resolved.atempo);
    const synthesized = join(cacheDirectory, `${item.audioKey}.wav`);
    const measurement = parseLoudnessMeasurement(runFfmpeg(measureLoudnessArgs(synthesized)));
    peakBeforeNormalization = Math.max(peakBeforeNormalization, Number(measurement.input_tp));
    const clip = join(auditionDirectory, `${label}-${item.line.id}.wav`);
    runFfmpeg(normalizeLoudnessArgs(synthesized, measurement, clip));
    clips.push(clip);
  }
  const all = join(auditionDirectory, `${label}-all.wav`);
  runFfmpeg(concatWithGapsArgs(clips, lineGapSeconds, all));
  results.push({
    label,
    spec,
    tempo,
    allSeconds: probeDuration(all),
    speechSeconds: clips.reduce((total, clip) => total + probeDuration(clip), 0),
    words,
    peakBeforeNormalization,
  });
}

runFfmpeg(concatWithGapsArgs(results.map(result => join(auditionDirectory, `${result.label}-all.wav`)), voiceGapSeconds, join(auditionDirectory, 'audition.wav')));
const readme = auditionReadme(lines, results);
writeFileSync(join(auditionDirectory, 'README.md'), readme);
console.log(`\n${readme}`);
console.log(`Wrote ${join(auditionDirectory, 'audition.wav')}`);
