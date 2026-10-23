import type {Timing} from '../theme/timing';
import {narrationPadding} from './narration';
import {allBeats, type Script} from './schema/script';
import {parseVoice, type Voice} from './voice';

/**
 * Words per second of synthesized speech at tempo 1, including the silence the engine leaves around a line.
 * Kokoro: `kokoro:am_michael` on two finished episodes of 1608 and 1881 words. Chatterbox: its built-in voice on a
 * finished episode.
 */
export const speechRates: Record<Voice['engine'], number> = {kokoro: 2.29, chatterbox: 3.5};

export interface ChapterDuration {
  id: string;
  words: number;
  seconds: number;
}

export interface DurationReport {
  chapters: ChapterDuration[];
  words: number;
  seconds: number;
  /** True when the seconds come from the timing of synthesized narration, false when they are estimated. */
  measured: boolean;
}

export function countWords(text: string): number {
  return text.trim().split(/\s+/).filter(word => word.length > 0).length;
}

/**
 * Duration per chapter: measured from `timing` when it holds exactly the script's beats, otherwise estimated from
 * the word count at the script's voice and tempo, plus the padding of every narrated beat and the holds.
 */
export function durationReport(script: Script, timing: Timing | null): DurationReport {
  const beats = allBeats(script);
  const measured = timing !== null && timing.beats.length === beats.length && timing.beats.every((beat, index) => beat.id === beats[index].id);
  const wordsPerSecond = speechRates[parseVoice(script.voice).engine] * script.tempo;
  const chapters = script.chapters.map(chapter => {
    const words = chapter.beats.reduce((total, beat) => total + countWords(beat.narration ?? ''), 0);
    const seconds = measured
      ? timing.beats.filter(beat => beat.chapter === chapter.id).reduce((total, beat) => total + beat.duration, 0)
      : chapter.beats.reduce((total, beat) => total + (beat.narration ? countWords(beat.narration) / wordsPerSecond + narrationPadding : 0) + (beat.hold ?? 0), 0);
    return {id: chapter.id, words, seconds};
  });
  return {
    chapters,
    words: chapters.reduce((total, chapter) => total + chapter.words, 0),
    seconds: chapters.reduce((total, chapter) => total + chapter.seconds, 0),
    measured,
  };
}

export function formatDurationReport(script: Script, report: DurationReport): string {
  const source = report.measured ? 'measured from timing.json' : `estimated for ${script.voice} at tempo ${script.tempo}`;
  return [
    ...report.chapters.map(chapter => `${chapter.id.padEnd(28)} ${String(chapter.words).padStart(5)} words ${chapter.seconds.toFixed(0).padStart(5)} s`),
    `${'total'.padEnd(28)} ${String(report.words).padStart(5)} words ${(report.seconds / 60).toFixed(1).padStart(5)} min (${source})`,
  ].join('\n');
}
