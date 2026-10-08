import {mkdirSync, mkdtempSync, rmSync} from 'node:fs';
import {tmpdir} from 'node:os';
import {join} from 'node:path';
import {chromium} from 'playwright';
import {runFfmpeg} from '../ffmpeg';
import type {Demo} from './config';

export async function recordDemo(
  demo: Demo,
  outputFile: string,
  baseUrl: string,
  viewport: {width: number; height: number},
): Promise<void> {
  const recordingDirectory = mkdtempSync(join(tmpdir(), 'demo-'));
  const browser = await chromium.launch();
  try {
    const context = await browser.newContext({viewport, colorScheme: 'dark', recordVideo: {dir: recordingDirectory, size: viewport}});
    const page = await context.newPage();
    await demo(page, {baseUrl});
    await context.close();
    const webm = await page.video()!.path();
    mkdirSync(join(outputFile, '..'), {recursive: true});
    runFfmpeg(['-i', webm, '-c:v', 'libx264', '-pix_fmt', 'yuv420p', '-an', outputFile]);
  } finally {
    await browser.close();
    rmSync(recordingDirectory, {recursive: true, force: true});
  }
}
