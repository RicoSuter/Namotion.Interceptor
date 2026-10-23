import {join} from 'node:path';
import type {Timing} from '../theme/timing';

/** Sample rate of the narration track in rendered videos. */
export const narrationSampleRate = 48000;

/**
 * ffmpeg arguments that build the narration track of the given beats, in script order: each beat's audio padded
 * with silence to the beat's duration, back to back, so the track lines up with the beats exactly.
 *
 * The renderer's own mix (`amix` followed by a gain of the input count) raises the level whenever one of its
 * inputs ends before the others, which clips the last minutes of a video; this track replaces it.
 */
export function narrationTrackArgs(timing: Timing, beatIds: ReadonlySet<string> | null, publicDirectory: string, output: string): string[] {
  const beats = timing.beats.filter(beat => beatIds === null || beatIds.has(beat.id));
  if (beats.length === 0) {
    throw new Error('No beats for the narration track');
  }
  const format = `aformat=sample_fmts=s16:sample_rates=${narrationSampleRate}:channel_layouts=stereo`;
  const inputs: string[] = [];
  const filters: string[] = [];
  beats.forEach((beat, index) => {
    const duration = beat.duration.toFixed(6);
    if (beat.audio) {
      inputs.push('-i', join(publicDirectory, beat.audio));
      filters.push(`[${index}:a]${format},apad,atrim=end=${duration},asetpts=PTS-STARTPTS[beat${index}]`);
    } else {
      inputs.push('-f', 'lavfi', '-t', duration, '-i', `anullsrc=r=${narrationSampleRate}:cl=stereo`);
      filters.push(`[${index}:a]${format}[beat${index}]`);
    }
  });
  const segments = beats.map((_, index) => `[beat${index}]`).join('');
  filters.push(`${segments}concat=n=${beats.length}:v=0:a=1[narration]`);
  return [...inputs, '-filter_complex', filters.join(';'), '-map', '[narration]', '-c:a', 'pcm_s16le', output];
}

/** Integrated loudness of the narration track in rendered videos, in LUFS. */
export const narrationLoudness = -16;

/** Sample peak ceiling of the narration track, in dBFS; the headroom keeps AAC encoding from clipping. */
export const narrationPeakCeiling = -2;

/** Lookahead of the peak limiter, in seconds. The limiter delays its output by this much, which is trimmed again. */
const limiterLookahead = 0.005;

/** Below this integrated loudness a track is treated as silent and keeps its level. */
const silenceLoudness = -60;

/** ffmpeg arguments that measure a file's loudness; read the result with `parseIntegratedLoudness`. */
export function measureLoudnessArgs(file: string): string[] {
  return ['-nostats', '-i', file, '-af', 'ebur128', '-f', 'null', '-'];
}

/** Reads the integrated loudness, in LUFS, from the summary ebur128 logs at the end. */
export function parseIntegratedLoudness(log: string): number {
  const summary = log.lastIndexOf('Summary:');
  const match = /I:\s+(-?[\d.]+) LUFS/.exec(summary >= 0 ? log.slice(summary) : '');
  if (!match) {
    throw new Error(`No integrated loudness in ffmpeg output:\n${log.slice(-2000)}`);
  }
  return Number(match[1]);
}

/**
 * ffmpeg arguments that bring a narration track to `narrationLoudness` with one gain for the whole track, so the level
 * stays the same from start to end, and catch the peaks the gain pushes over `narrationPeakCeiling` with a limiter.
 */
export function normalizeNarrationArgs(input: string, integratedLoudness: number, output: string): string[] {
  const gain = integratedLoudness < silenceLoudness ? 0 : narrationLoudness - integratedLoudness;
  const limit = 10 ** (narrationPeakCeiling / 20);
  const filters = [
    `volume=${gain.toFixed(2)}dB`,
    `alimiter=limit=${limit.toFixed(4)}:attack=${limiterLookahead * 1000}:level=false`,
    `atrim=start=${limiterLookahead},asetpts=PTS-STARTPTS`,
  ];
  return ['-i', input, '-af', filters.join(','), '-c:a', 'pcm_s16le', output];
}
