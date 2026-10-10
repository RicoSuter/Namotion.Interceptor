import {createHash} from 'node:crypto';
import type {Timing, TimingBeat} from '../theme/timing';
import {applyLexicon, type LexiconEntry} from './lexicon';
import {allBeats, type Script} from './schema/script';
import {parseVoice, tempoPlan, voiceIdentity, voiceLabel} from './voice';

/** Silence after each narrated line, in seconds. */
export const narrationPadding = 0.4;

export interface NarrationItem {
  beatId: string;
  /** Cache key of the synthesized speech; it includes the tempo only for a voice that reaches it natively. */
  key: string;
  /** Cache key of the audio the video plays: the synthesized speech, or its `atempo` copy. */
  audioKey: string;
  text: string;
}

/** Cache key of a line synthesized by a voice, given the voice's cache identity. */
export function synthesisKey(text: string, identity: {voice: unknown; engine: string}): string {
  return createHash('sha256')
    .update(JSON.stringify({text, voice: identity.voice, engine: identity.engine}))
    .digest('hex')
    .slice(0, 16);
}

/**
 * Key of the `atempo` copy of synthesized audio. Factor 1 plays the synthesized file itself; any other factor gets
 * its own file, so changing the tempo of a voice without native speed never synthesizes the speech again.
 */
export function tempoKey(key: string, atempo: number): string {
  return atempo === 1 ? key : `${key}-x${atempo}`;
}

/** Builds the narrated lines at the script's tempo, see `tempoPlan`. */
export function buildNarration(script: Script, lexicon: LexiconEntry[]): NarrationItem[] {
  const voice = parseVoice(script.voice);
  const {speed, atempo} = tempoPlan(voice, script.tempo);
  const cacheIdentity = voiceIdentity(voice, speed);
  return allBeats(script)
    .filter(beat => beat.narration !== undefined)
    .map(beat => {
      const text = applyLexicon(beat.narration!, lexicon, voice.engine);
      const key = synthesisKey(text, cacheIdentity);
      return {beatId: beat.id, key, audioKey: tempoKey(key, atempo), text};
    });
}

/** Lays the beats out back to back; durations are the seconds of each item's played audio, by audio key. */
export function buildTiming(script: Script, items: NarrationItem[], durations: Record<string, number>): Timing {
  const itemsByBeat = new Map(items.map(item => [item.beatId, item]));
  const beats: TimingBeat[] = [];
  let start = 0;
  for (const beat of allBeats(script)) {
    const item = itemsByBeat.get(beat.id);
    let duration = beat.hold ?? 0;
    if (item) {
      const audioDuration = durations[item.audioKey];
      if (audioDuration === undefined) {
        throw new Error(`No audio duration for beat '${beat.id}' (key ${item.audioKey})`);
      }
      duration += audioDuration + narrationPadding;
    }
    beats.push({
      id: beat.id,
      chapter: beat.chapter,
      chapterTitle: beat.chapterTitle,
      start,
      duration,
      audio: item ? `/generated/${script.episode}/audio/${item.audioKey}.wav` : null,
      caption: beat.narration ?? null,
    });
    start += duration;
  }
  return {episode: script.episode, title: script.title, totalDuration: start, beats};
}

/**
 * Applies the `--voice <voice>` and `--tempo <factor>` options of the tts and render commands to the script. An
 * override names a variant, for example `kokoro-af_heart-x1`, whose timing and renders get their own files, so a trial
 * narration never replaces the episode's regular ones. Without an override the variant is empty.
 */
export function applyNarrationOptions(script: Script, args: string[]): {script: Script; variant: string; timingFileName: string} {
  const voice = optionValue(args, '--voice');
  const tempoText = optionValue(args, '--tempo');
  if (voice === undefined && tempoText === undefined) {
    return {script, variant: '', timingFileName: 'timing.json'};
  }
  const tempo = tempoText === undefined ? script.tempo : Number(tempoText);
  if (!Number.isFinite(tempo) || tempo < 0.5 || tempo > 2) {
    throw new Error(`--tempo must be a number between 0.5 and 2, got '${tempoText}'`);
  }
  const overridden = {...script, voice: voice ?? script.voice, tempo};
  const variant = `${voiceLabel(parseVoice(overridden.voice))}-x${tempo}`;
  return {script: overridden, variant, timingFileName: `timing-${variant}.json`};
}

function optionValue(args: string[], name: string): string | undefined {
  const index = args.indexOf(name);
  if (index < 0) {
    return undefined;
  }
  const value = args[index + 1];
  if (value === undefined || value.startsWith('--')) {
    throw new Error(`${name} needs a value`);
  }
  return value;
}

export function toSrt(timing: Timing): string {
  return timing.beats
    .filter(beat => beat.caption !== null)
    .map((beat, index) => `${index + 1}\n${formatSrtTime(beat.start)} --> ${formatSrtTime(beat.start + beat.duration)}\n${beat.caption}\n`)
    .join('\n');
}

function formatSrtTime(seconds: number): string {
  const totalMilliseconds = Math.round(seconds * 1000);
  const hours = Math.floor(totalMilliseconds / 3_600_000);
  const minutes = Math.floor((totalMilliseconds % 3_600_000) / 60_000);
  const secondsPart = Math.floor((totalMilliseconds % 60_000) / 1000);
  const milliseconds = totalMilliseconds % 1000;
  const pad = (value: number, length = 2) => value.toString().padStart(length, '0');
  return `${pad(hours)}:${pad(minutes)}:${pad(secondsPart)},${pad(milliseconds, 3)}`;
}
