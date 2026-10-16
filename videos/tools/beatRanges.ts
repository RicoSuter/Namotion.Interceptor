import {findBeat, type Timing} from '../theme/timing';

export interface TimeRange {
  start: number;
  end: number;
}

/**
 * Time ranges that cover the given beats, in script order, with directly consecutive beats merged into one
 * range. Throws on an unknown beat id.
 */
export function beatRanges(timing: Timing, ids: string[]): TimeRange[] {
  const wanted = new Set(ids.map(id => findBeat(timing, id).id));
  const ranges: TimeRange[] = [];
  let previousIndex = -2;
  timing.beats.forEach((beat, index) => {
    if (!wanted.has(beat.id)) {
      return;
    }
    if (index === previousIndex + 1) {
      ranges[ranges.length - 1].end = beat.start + beat.duration;
    } else {
      ranges.push({start: beat.start, end: beat.start + beat.duration});
    }
    previousIndex = index;
  });
  return ranges;
}
