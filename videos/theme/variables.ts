import {useScene} from '@revideo/core';
import {emptyTiming, type Timing} from './timing';

export function useTiming(): Timing {
  return useScene().variables.get('timing', emptyTiming)();
}

export function useTerminal(name: string): string {
  const captures = useScene().variables.get('terminal', {} as Record<string, string>)();
  const text = captures[name];
  if (text === undefined) {
    throw new Error(`Terminal capture '${name}' missing. Run 'npm run capture -- ${useTiming().episode}'.`);
  }
  return text;
}

export interface Clip {
  url: string;
  /** Seconds, probed at capture time. */
  duration: number;
  /** Named moments the demo recorded with mark, in seconds of clip time. */
  marks: Record<string, number>;
}

export function useClip(name: string): Clip {
  const durations = useScene().variables.get('clips', {} as Record<string, number>)();
  const duration = durations[name];
  if (duration === undefined) {
    throw new Error(`Clip '${name}' missing. Run 'npm run capture -- ${useTiming().episode}'.`);
  }
  const marks = useScene().variables.get('marks', {} as Record<string, Record<string, number>>)()[name] ?? {};
  return {url: `/generated/${useTiming().episode}/clips/${name}.mp4`, duration, marks};
}
