export interface TimingBeat {
  id: string;
  chapter: string;
  start: number;
  duration: number;
  audio: string | null;
  caption: string | null;
}

export interface Timing {
  episode: string;
  totalDuration: number;
  beats: TimingBeat[];
}

export const emptyTiming: Timing = {episode: '', totalDuration: 0, beats: []};

export function findBeat(timing: Timing, id: string): TimingBeat {
  const beat = timing.beats.find(candidate => candidate.id === id);
  if (!beat) {
    throw new Error(`Unknown beat '${id}'. Check script.yaml and run 'npm run tts -- ${timing.episode}'.`);
  }
  return beat;
}
