import {spawnSync} from 'node:child_process';
import ffmpegInstaller from '@ffmpeg-installer/ffmpeg';
import ffprobeInstaller from '@ffprobe-installer/ffprobe';

export const ffmpegPath: string = ffmpegInstaller.path;
export const ffprobePath: string = ffprobeInstaller.path;

/** Runs ffmpeg and returns stderr, where ffmpeg writes its log and filter output. */
export function runFfmpeg(args: string[]): string {
  const result = spawnSync(ffmpegPath, ['-hide_banner', '-y', ...args], {encoding: 'utf8', maxBuffer: 64 * 1024 * 1024});
  if (result.status !== 0) {
    throw new Error(`ffmpeg ${args.join(' ')} failed:\n${result.stderr}`);
  }
  return result.stderr;
}
