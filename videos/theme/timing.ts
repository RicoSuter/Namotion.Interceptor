export interface TimingBeat {
  id: string;
  chapter: string;
  /** Title of the chapter from script.yaml, shown in the chapter header. */
  chapterTitle: string;
  start: number;
  duration: number;
  audio: string | null;
  caption: string | null;
}

export interface Timing {
  episode: string;
  /** Title of the video from script.yaml, shown in the chapter header. */
  title: string;
  totalDuration: number;
  beats: TimingBeat[];
}

export const emptyTiming: Timing = {episode: '', title: '', totalDuration: 0, beats: []};

export function findBeat(timing: Timing, id: string): TimingBeat {
  const beat = timing.beats.find(candidate => candidate.id === id);
  if (!beat) {
    throw new Error(`Unknown beat '${id}'. Check script.yaml and run 'npm run tts -- ${timing.episode}'.`);
  }
  return beat;
}
