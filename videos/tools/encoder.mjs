#!/usr/bin/env node
// ffmpeg wrapper handed to the renderer. Revideo encodes the rendered frames with ffmpeg's default quality,
// which turns the dark gradients of the theme into visible blocks, so that one encode gets explicit settings.
// Every other invocation passes through unchanged.
import {spawnSync} from 'node:child_process';
import ffmpegInstaller from '@ffmpeg-installer/ffmpeg';

const args = process.argv.slice(2);
const encodesFrames = args.includes('image2pipe') && args.at(-1).endsWith('.mp4');
const quality = ['-c:v', 'libx264', '-preset', 'slow', '-crf', '14', '-tune', 'animation', '-x264-params', 'aq-mode=3'];
const finalArgs = encodesFrames ? [...args.slice(0, -1), ...quality, args.at(-1)] : args;
const result = spawnSync(ffmpegInstaller.path, finalArgs, {stdio: 'inherit'});
process.exit(result.status ?? 1);
