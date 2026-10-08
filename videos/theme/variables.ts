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

export function clipUrl(name: string): string {
  return `/generated/${useTiming().episode}/clips/${name}.mp4`;
}
