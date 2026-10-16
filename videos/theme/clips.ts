/** Fastest a demo clip is played before its start is trimmed instead. */
export const maximumClipRate = 4;

/** Slowest a demo clip is played when it is shorter than its beat. */
export const minimumClipRate = 0.5;

export interface ClipRange {
  /** Clip time in seconds where playback starts. Defaults to the clip start. */
  from?: number;
  /** Clip time in seconds where playback ends. Defaults to the clip end. */
  to?: number;
}

export interface ClipFit {
  /** Clip time in seconds at the start of the beat. */
  start: number;
  /** Clip time in seconds at the end of the beat. */
  end: number;
  rate: number;
}

/**
 * Chooses where and how fast to play a clip so its range ends with the beat. Ranges that would need
 * more than the maximum rate keep their end and lose their start, so the viewer always sees the end state.
 */
export function fitClip(clipDuration: number, beatDuration: number, range: ClipRange = {}): ClipFit {
  if (beatDuration <= 0) {
    throw new Error('A clip needs a beat duration above zero');
  }
  const end = Math.min(range.to ?? clipDuration, clipDuration);
  const from = Math.max(range.from ?? 0, 0);
  if (end <= from) {
    throw new Error(`Clip range ${from} to ${end} is empty`);
  }
  const rate = (end - from) / beatDuration;
  if (rate > maximumClipRate) {
    return {start: end - beatDuration * maximumClipRate, end, rate: maximumClipRate};
  }
  if (rate < minimumClipRate) {
    return {start: from, end: from + beatDuration * minimumClipRate, rate: minimumClipRate};
  }
  return {start: from, end, rate};
}
