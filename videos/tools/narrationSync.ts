import {join} from 'node:path';
import type {Timing} from '../theme/timing';
import {decodeAudio} from './ffmpeg';

/** Sample rate the sync check decodes audio at; onsets are resolved to `onsetWindowSeconds`. */
const sampleRate = 16000;
const onsetWindowSeconds = 0.01;

/** Speech starts at the first window this many dB below the loudest window of the analyzed audio. */
const onsetThreshold = 30;

/** Audio analyzed before a beat starts, to catch speech that starts early, and in total, in seconds. */
const leadSeconds = 0.3;
const analyzedSeconds = 2.3;

/** A beat whose speech starts this much earlier or later in the video than in its clip is reported, in seconds. */
export const syncTolerance = 0.04;

export interface BeatSync {
  id: string;
  /** Seconds from the beat start to the speech onset in the video. */
  videoOnset: number;
  /** Seconds from the clip start to the speech onset in the narration clip. */
  clipOnset: number;
}

/**
 * Seconds from the start of `samples` to the first window whose RMS level comes within `onsetThreshold` dB of the
 * loudest window. The threshold is relative, so the gain the narration track applies does not move the onset.
 */
export function speechOnset(samples: Float32Array, rate: number): number | null {
  const length = Math.round(rate * onsetWindowSeconds);
  const levels: number[] = [];
  for (let start = 0; start + length <= samples.length; start += length) {
    let energy = 0;
    for (let index = start; index < start + length; index++) {
      energy += samples[index] * samples[index];
    }
    levels.push(10 * Math.log10(energy / length + 1e-12));
  }
  const loudest = Math.max(...levels);
  if (levels.length === 0 || loudest < -90) {
    return null;
  }
  return levels.findIndex(level => level >= loudest - onsetThreshold) * onsetWindowSeconds;
}

/** Compares where each narrated beat's speech starts in the video with where it starts in its narration clip. */
export function measureNarrationSync(videoFile: string, timing: Timing, publicDirectory: string): BeatSync[] {
  const video = decodeAudio(videoFile, sampleRate);
  return timing.beats.flatMap(beat => {
    if (!beat.audio) {
      return [];
    }
    const first = Math.max(Math.round((beat.start - leadSeconds) * sampleRate), 0);
    const lead = beat.start - first / sampleRate;
    const videoOnset = speechOnset(video.subarray(first, first + Math.round(analyzedSeconds * sampleRate)), sampleRate);
    const clipOnset = speechOnset(decodeAudio(join(publicDirectory, beat.audio), sampleRate, {start: 0, duration: analyzedSeconds - leadSeconds}), sampleRate);
    if (videoOnset === null || clipOnset === null) {
      return [{id: beat.id, videoOnset: Number.NaN, clipOnset: clipOnset ?? Number.NaN}];
    }
    return [{id: beat.id, videoOnset: videoOnset - lead, clipOnset}];
  });
}

/** The narration sync section of the review report. */
export function narrationSyncReport(syncs: BeatSync[]): string[] {
  const drifts = syncs.map(sync => ({id: sync.id, drift: sync.videoOnset - sync.clipOnset}));
  const offBeats = drifts.filter(beat => !(Math.abs(beat.drift) <= syncTolerance));
  const largest = Math.max(0, ...drifts.filter(beat => Number.isFinite(beat.drift)).map(beat => Math.abs(beat.drift)));
  return [
    '## Narration sync',
    '',
    offBeats.length === 0
      ? `In sync: in all ${syncs.length} narrated beats the speech starts in the video where it starts in its clip (largest difference ${milliseconds(largest)}).`
      : `Speech starts more than ${milliseconds(syncTolerance)} away from its clip in: ${offBeats.map(beat => `${beat.id} (${Number.isFinite(beat.drift) ? milliseconds(beat.drift) : 'no speech found'})`).join(', ')}.`,
    '',
  ];
}

function milliseconds(seconds: number): string {
  return `${Math.round(seconds * 1000)} ms`;
}
