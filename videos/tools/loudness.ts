import {runFfmpeg} from './ffmpeg';
import {narrationLoudness} from './narrationTrack';

/** Length of the windows the review measures loudness in, in seconds. */
export const loudnessWindowSeconds = 30;

/** A window whose integrated loudness differs from the whole video's by more than this, in LU, is reported. */
export const loudnessTolerance = 2;

/** One 400 ms block of ebur128's frame log, every 100 ms. */
export interface LoudnessFrame {
  /** End of the block in seconds. */
  time: number;
  /** Momentary loudness of the block in LUFS. */
  momentary: number;
  /** Highest true peak of the frame over all channels, in dBTP. */
  truePeak: number;
}

export interface LoudnessWindow {
  start: number;
  end: number;
  /** Gated integrated loudness in LUFS; null when the window holds only silence. */
  integrated: number | null;
  truePeak: number;
}

export interface LoudnessMeasurement {
  integrated: number | null;
  truePeak: number;
  windows: LoudnessWindow[];
}

/** Absolute gate of ITU-R BS.1770 in LUFS. */
const absoluteGate = -70;

/** Relative gate of ITU-R BS.1770 in LU below the absolute-gated loudness. */
const relativeGate = 10;

const framePattern = /t:\s*([\d.]+)\s+TARGET.*?M:\s*(-?[\d.]+|-inf|nan)\s.*?FTPK:\s*((?:\s*(?:-?[\d.]+|-inf))+)\s+dBFS/;

/** Reads the frame log of ffmpeg's `ebur128=peak=true` filter. */
export function parseLoudnessFrames(log: string): LoudnessFrame[] {
  const frames: LoudnessFrame[] = [];
  for (const line of log.split('\n')) {
    const match = framePattern.exec(line);
    if (match) {
      const peaks = match[3].trim().split(/\s+/).map(toNumber);
      frames.push({time: Number(match[1]), momentary: toNumber(match[2]), truePeak: Math.max(...peaks)});
    }
  }
  return frames;
}

/**
 * Integrated loudness of momentary blocks with the gating of ITU-R BS.1770, as ebur128 computes it for a whole file.
 * Null when no block passes the absolute gate.
 */
export function gatedLoudness(momentary: number[]): number | null {
  const audible = momentary.filter(loudness => loudness > absoluteGate);
  if (audible.length === 0) {
    return null;
  }
  const threshold = meanLoudness(audible) - relativeGate;
  return meanLoudness(audible.filter(loudness => loudness > threshold));
}

/** Integrated loudness and true peak of consecutive windows of `windowSeconds`; the last one may be shorter. */
export function loudnessWindows(frames: LoudnessFrame[], duration: number, windowSeconds = loudnessWindowSeconds): LoudnessWindow[] {
  const windows: LoudnessWindow[] = [];
  for (let start = 0; start < duration; start += windowSeconds) {
    const end = Math.min(start + windowSeconds, duration);
    const inside = frames.filter(frame => frame.time > start && frame.time <= end);
    windows.push({
      start,
      end,
      integrated: gatedLoudness(inside.map(frame => frame.momentary)),
      truePeak: Math.max(-Infinity, ...inside.map(frame => frame.truePeak)),
    });
  }
  return windows;
}

/** Measures the loudness of a video's audio in one ffmpeg pass. */
export function measureVideoLoudness(videoFile: string, duration: number): LoudnessMeasurement {
  const frames = parseLoudnessFrames(runFfmpeg(['-nostats', '-i', videoFile, '-vn', '-af', 'ebur128=peak=true', '-f', 'null', '-']));
  return {
    integrated: gatedLoudness(frames.map(frame => frame.momentary)),
    truePeak: Math.max(-Infinity, ...frames.map(frame => frame.truePeak)),
    windows: loudnessWindows(frames, duration),
  };
}

/** The loudness section of the review report. */
export function loudnessReport(measurement: LoudnessMeasurement): string[] {
  const {integrated} = measurement;
  const uneven = measurement.windows.filter(window =>
    integrated !== null && window.integrated !== null && Math.abs(window.integrated - integrated) > loudnessTolerance);
  return [
    '## Loudness',
    '',
    `Integrated ${formatLoudness(integrated)} (target ${narrationLoudness} LUFS), true peak ${measurement.truePeak.toFixed(1)} dBTP.`,
    '',
    uneven.length === 0
      ? `Flat: every ${loudnessWindowSeconds} s window is within ${loudnessTolerance} LU of the integrated loudness.`
      : `Windows more than ${loudnessTolerance} LU from the integrated loudness: ${uneven.map(window => `${formatTime(window.start)} (${formatLoudness(window.integrated)})`).join(', ')}.`,
    '',
    '| Window | Integrated | True peak |',
    '|---|---|---|',
    ...measurement.windows.map(window =>
      `| ${formatTime(window.start)} to ${formatTime(window.end)} | ${formatLoudness(window.integrated)} | ${window.truePeak.toFixed(1)} dBTP |`),
    '',
  ];
}

function meanLoudness(loudness: number[]): number {
  return 10 * Math.log10(loudness.reduce((total, value) => total + 10 ** (value / 10), 0) / loudness.length);
}

function toNumber(text: string): number {
  return text === '-inf' || text === 'nan' ? -Infinity : Number(text);
}

function formatLoudness(loudness: number | null): string {
  return loudness === null ? 'silent' : `${loudness.toFixed(1)} LUFS`;
}

function formatTime(seconds: number): string {
  const minutes = Math.floor(seconds / 60);
  return `${minutes}:${Math.round(seconds - minutes * 60).toString().padStart(2, '0')}`;
}
