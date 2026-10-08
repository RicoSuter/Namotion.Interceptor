import {mkdirSync, mkdtempSync, rmSync, writeFileSync} from 'node:fs';
import {tmpdir} from 'node:os';
import {join} from 'node:path';
import {chromium, type CDPSession} from 'playwright';
import {runFfmpeg} from '../ffmpeg';
import type {Demo} from './config';
import {themeFontsScript} from './fonts';

export interface RecordOptions {
  baseUrl: string;
  /** Page size in CSS pixels. */
  viewport: {width: number; height: number};
  /** Device pixels per CSS pixel; the clip is recorded at the device resolution. */
  deviceScaleFactor: number;
}

export interface CapturedFrame {
  file: string;
  /** Milliseconds since the recording started. */
  time: number;
}

/** Output frame rate of recorded clips. */
const clipFps = 30;

/**
 * Records a demo at device resolution. Playwright's own video recording and the CDP screencast both
 * deliver CSS-pixel frames, so the page is captured with scaled screenshots in a loop instead.
 */
export async function recordDemo(demo: Demo, outputFile: string, options: RecordOptions): Promise<void> {
  const framesDirectory = mkdtempSync(join(tmpdir(), 'demo-'));
  const browser = await chromium.launch();
  try {
    const {viewport, deviceScaleFactor} = options;
    const context = await browser.newContext({viewport, deviceScaleFactor, colorScheme: 'dark'});
    await context.addInitScript(themeFontsScript());
    const page = await context.newPage();
    // Dark first frame instead of the white blank page.
    await page.setContent('<html style="background:#1c1c1e"></html>');
    const session = await context.newCDPSession(page);
    const recording = captureFrames(session, framesDirectory, viewport, deviceScaleFactor);
    try {
      await demo(page, {baseUrl: options.baseUrl});
    } finally {
      recording.stop();
    }
    const frames = await recording.frames;
    await context.close();
    if (frames.length === 0) {
      throw new Error('The demo recorded no frames');
    }
    const list = join(framesDirectory, 'frames.txt');
    writeFileSync(list, concatList(frames));
    mkdirSync(join(outputFile, '..'), {recursive: true});
    runFfmpeg(['-f', 'concat', '-safe', '0', '-i', list, '-vf', `fps=${clipFps},format=yuv420p`,
      '-c:v', 'libx264', '-preset', 'medium', '-crf', '16', '-an', outputFile]);
  } finally {
    await browser.close();
    rmSync(framesDirectory, {recursive: true, force: true});
  }
}

/** ffmpeg concat list that shows every frame until the next one was captured. */
export function concatList(frames: CapturedFrame[]): string {
  const lines = frames.flatMap((frame, index) => {
    const next = frames[index + 1]?.time ?? frame.time + 1000 / clipFps;
    return [`file '${frame.file}'`, `duration ${((next - frame.time) / 1000).toFixed(4)}`];
  });
  // The concat demuxer ignores the duration of the last entry, so the last frame is listed twice.
  lines.push(`file '${frames[frames.length - 1].file}'`);
  return `${lines.join('\n')}\n`;
}

function captureFrames(session: CDPSession, directory: string, viewport: {width: number; height: number}, scale: number) {
  let stopped = false;
  const start = performance.now();
  const frames = (async () => {
    const captured: CapturedFrame[] = [];
    while (!stopped) {
      const requested = performance.now();
      try {
        const shot = await withTimeout(session.send('Page.captureScreenshot', {
          format: 'jpeg', quality: 92, optimizeForSpeed: true, clip: {x: 0, y: 0, ...viewport, scale},
        }), 500);
        const file = join(directory, `frame-${captured.length.toString().padStart(5, '0')}.jpg`);
        writeFileSync(file, Buffer.from(shot.data, 'base64'));
        captured.push({file, time: (requested + performance.now()) / 2 - start});
      } catch {
        // No frame while the page navigates; the previous frame stays on screen.
      }
    }
    return captured;
  })();
  return {frames, stop: () => (stopped = true)};
}

/** A screenshot requested during a navigation can stay unanswered, so each one gets a deadline. */
function withTimeout<T>(promise: Promise<T>, milliseconds: number): Promise<T> {
  return Promise.race([
    promise,
    new Promise<T>((_, reject) => setTimeout(() => reject(new Error('Screenshot timed out')), milliseconds)),
  ]);
}
