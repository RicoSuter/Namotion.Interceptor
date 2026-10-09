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
