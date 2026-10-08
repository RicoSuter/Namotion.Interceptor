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

/** Returns the duration in seconds of the first video stream. */
export function probeVideoDuration(file: string): number {
  const result = spawnSync(ffprobePath, ['-v', 'error', '-select_streams', 'v:0', '-show_entries', 'stream=duration', '-of', 'csv=p=0', file], {encoding: 'utf8'});
  const duration = Number(result.stdout.trim());
  if (result.status !== 0 || !Number.isFinite(duration)) {
    throw new Error(`ffprobe could not read the video duration of ${file}:\n${result.stderr}`);
  }
  return duration;
}

/** Returns the duration in seconds of a media file, for example a WAV file. */
export function probeDuration(file: string): number {
  const result = spawnSync(ffprobePath, ['-v', 'error', '-show_entries', 'format=duration', '-of', 'csv=p=0', file], {encoding: 'utf8'});
  const duration = Number(result.stdout.trim());
  if (result.status !== 0 || !Number.isFinite(duration)) {
    throw new Error(`ffprobe could not read the duration of ${file}:\n${result.stderr}`);
  }
  return duration;
}
