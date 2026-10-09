import {createHash} from 'node:crypto';
import type {Timing, TimingBeat} from '../theme/timing';
import {applyLexicon, type LexiconEntry} from './lexicon';
import {allBeats, type Script} from './schema/script';

/** Bump when synthesis settings change so cached audio is regenerated. */
export const engineVersion = 'chatterbox-1';

/** Silence after each narrated line, in seconds. */
export const narrationPadding = 0.4;

export interface NarrationItem {
  beatId: string;
  /** Cache key of the synthesized speech, independent of the tempo. */
  key: string;
  /** Cache key of the tempo-adjusted audio the video plays. */
  audioKey: string;
  text: string;
}

/**
 * Key of the tempo-adjusted copy of synthesized audio. Tempo 1 plays the synthesized file itself; any other
 * tempo gets its own file, so changing the tempo never synthesizes the speech again.
 */
export function tempoKey(key: string, tempo: number): string {
  return tempo === 1 ? key : `${key}-x${tempo}`;
}

export function buildNarration(script: Script, lexicon: LexiconEntry[]): NarrationItem[] {
  return allBeats(script)
    .filter(beat => beat.narration !== undefined)
    .map(beat => {
      const text = applyLexicon(beat.narration!, lexicon);
      const key = createHash('sha256')
        .update(JSON.stringify({text, voice: script.voice, engine: engineVersion}))
        .digest('hex')
        .slice(0, 16);
      return {beatId: beat.id, key, audioKey: tempoKey(key, script.tempo), text};
    });
}

/** Lays the beats out back to back; durations are the seconds of each item's tempo-adjusted audio, by audio key. */
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
