import {existsSync} from 'node:fs';
import {dirname, join, resolve} from 'node:path';
import {fileURLToPath} from 'node:url';

export const videosRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
export const ttsProjectDirectory = join(videosRoot, 'tools', 'tts');
export const lexiconFile = join(ttsProjectDirectory, 'lexicon.yaml');
export const outputDirectory = join(videosRoot, 'output');

export interface EpisodePaths {
  episode: string;
  episodeDirectory: string;
  generatedDirectory: string;
  audioDirectory: string;
  clipsDirectory: string;
  /** Clip durations in seconds by clip name, written by capture. */
  clipsFile: string;
  terminalDirectory: string;
  timingFile: string;
}

export function episodePaths(episode: string): EpisodePaths {
  const episodeDirectory = join(videosRoot, 'episodes', episode);
  if (!existsSync(episodeDirectory)) {
    throw new Error(`Episode '${episode}' not found at ${episodeDirectory}`);
  }
  const generatedDirectory = join(videosRoot, 'public', 'generated', episode);
  return {
    episode,
    episodeDirectory,
    generatedDirectory,
    audioDirectory: join(generatedDirectory, 'audio'),
    clipsDirectory: join(generatedDirectory, 'clips'),
    clipsFile: join(generatedDirectory, 'clips', 'clips.json'),
    terminalDirectory: join(generatedDirectory, 'terminal'),
    timingFile: join(generatedDirectory, 'timing.json'),
  };
}

export function episodeArgument(): string {
  const episode = process.argv[2];
  if (!episode || episode.startsWith('-')) {
    throw new Error('Usage: npm run <command> -- <episode> [options]');
  }
  return episode;
}
