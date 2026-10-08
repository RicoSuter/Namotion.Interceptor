import {mkdirSync, rmSync, writeFileSync} from 'node:fs';
import {join} from 'node:path';
import type {Timing} from '../theme/timing';
import {runFfmpeg} from './ffmpeg';

/** Seconds without visible motion before a beat is flagged. */
export const stillnessBudget = 4;

/**
 * Freeze detection on the picture's detail only: subtracting a blurred copy removes the slow, smooth motion of the
 * background layer, which would otherwise count as motion in every beat, and keeps edges, text and particles.
 */
const stillnessFilter = `format=gray,split[picture][copy];[copy]gblur=sigma=32[blurred];`
  + `[picture][blurred]blend=all_mode=difference,freezedetect=n=0.001:d=${stillnessBudget}`;

/** Seconds a freeze must reach into a beat before the beat is reported, so frame-edge contact does not count. */
const boundaryTolerance = 0.1;

const columns = 4;
const thumbnailWidth = 480;
const thumbnailHeight = 270;

export interface Interval {
  start: number;
  end: number;
}

export function beatMidpoints(timing: Timing): Array<{id: string; time: number}> {
  return timing.beats.map(beat => ({id: beat.id, time: beat.start + beat.duration / 2}));
}

export function parseFreezes(log: string, videoDuration: number): Interval[] {
  const freezes: Interval[] = [];
  let start: number | null = null;
  for (const match of log.matchAll(/lavfi\.freezedetect\.freeze_(start|end): ([\d.]+)/g)) {
    const value = Number(match[2]);
    if (match[1] === 'start') {
      start = value;
    } else if (start !== null) {
      freezes.push({start, end: value});
      start = null;
    }
  }
  if (start !== null) {
    freezes.push({start, end: videoDuration});
  }
  return freezes;
}

export function assignFreezes(freezes: Interval[], timing: Timing): Array<{id: string} & Interval> {
  return freezes.flatMap(freeze =>
    timing.beats
      .filter(beat => beat.start < freeze.end - boundaryTolerance && beat.start + beat.duration > freeze.start + boundaryTolerance)
      .map(beat => ({id: beat.id, start: freeze.start, end: freeze.end})),
  );
}

/** Writes <episode>-contact.png and <episode>-review.md next to the video. */
export function writeReview(videoFile: string, timing: Timing, outputDirectory: string): void {
  const framesDirectory = join(outputDirectory, `${timing.episode}-frames`);
  rmSync(framesDirectory, {recursive: true, force: true});
  mkdirSync(framesDirectory, {recursive: true});

  const midpoints = beatMidpoints(timing);
  midpoints.forEach((midpoint, index) => {
    runFfmpeg(['-ss', midpoint.time.toFixed(3), '-i', videoFile, '-frames:v', '1',
      '-vf', `scale=${thumbnailWidth}:${thumbnailHeight}`, join(framesDirectory, `frame-${pad(index)}.png`)]);
  });
  // Fill the last row with black frames so the tile filter emits a complete grid.
  for (let index = midpoints.length; index % columns !== 0; index++) {
    runFfmpeg(['-f', 'lavfi', '-i', `color=black:s=${thumbnailWidth}x${thumbnailHeight}`, '-frames:v', '1',
      join(framesDirectory, `frame-${pad(index)}.png`)]);
  }
  const rows = Math.ceil(midpoints.length / columns);
  runFfmpeg(['-framerate', '1', '-i', join(framesDirectory, 'frame-%03d.png'),
    '-vf', `tile=${columns}x${rows}:padding=8:color=0x11111b`, '-frames:v', '1',
    join(outputDirectory, `${timing.episode}-contact.png`)]);

  const log = runFfmpeg(['-i', videoFile, '-vf', stillnessFilter, '-map', '0:v:0', '-f', 'null', '-']);
  const stillBeats = assignFreezes(parseFreezes(log, timing.totalDuration), timing);

  const chapters = [...new Set(timing.beats.map(beat => beat.chapter))];
  const report = [
    `# Review: ${timing.episode}`,
    '',
    `Total: ${timing.totalDuration.toFixed(1)} s`,
    '',
    '| Chapter | Seconds |',
    '|---|---|',
    ...chapters.map(chapter => {
      const seconds = timing.beats.filter(beat => beat.chapter === chapter).reduce((total, beat) => total + beat.duration, 0);
      return `| ${chapter} | ${seconds.toFixed(1)} |`;
    }),
    '',
    `## Beats without motion for ${stillnessBudget} s or more`,
    '',
    ...(stillBeats.length === 0
      ? ['None.']
      : stillBeats.map(beat => `- ${beat.id}: still from ${beat.start.toFixed(1)} s to ${beat.end.toFixed(1)} s`)),
    '',
    '## Contact sheet order',
    '',
    ...midpoints.map((midpoint, index) => `${index + 1}. ${midpoint.id} (${midpoint.time.toFixed(1)} s)`),
    '',
  ].join('\n');
  writeFileSync(join(outputDirectory, `${timing.episode}-review.md`), report);
}

function pad(index: number): string {
  return index.toString().padStart(3, '0');
}
