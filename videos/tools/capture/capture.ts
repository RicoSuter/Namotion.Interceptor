import {existsSync, mkdirSync, readdirSync, readFileSync, writeFileSync} from 'node:fs';
import {basename, join} from 'node:path';
import {pathToFileURL} from 'node:url';
import {loadCaptureConfig, validateEpisode} from '../episode';
import {probeVideoDuration} from '../ffmpeg';
import {episodeArgument, episodePaths} from '../paths';
import {startApp, type RunningApp} from './app';
import type {Demo} from './config';
import {recordDemo} from './record';
import {runTerminalCapture} from './terminal';

const paths = episodePaths(episodeArgument());
const onlyIndex = process.argv.indexOf('--only');
const only = onlyIndex >= 0 ? process.argv[onlyIndex + 1] : undefined;
await validateEpisode(paths.episodeDirectory);
const config = await loadCaptureConfig(paths.episodeDirectory);

mkdirSync(paths.terminalDirectory, {recursive: true});
for (const capture of config.terminal ?? []) {
  if (only && capture.name !== only) continue;
  console.log(`terminal: ${capture.name}`);
  writeFileSync(join(paths.terminalDirectory, `${capture.name}.txt`), await runTerminalCapture(capture, paths.episodeDirectory));
}

const demosDirectory = join(paths.episodeDirectory, 'demos');
const demoFiles = existsSync(demosDirectory)
  ? readdirSync(demosDirectory).filter(file => file.endsWith('.ts') && (!only || basename(file, '.ts') === only))
  : [];
if (demoFiles.length > 0) {
  if (!config.app) {
    throw new Error('Demos need an app entry in capture.ts');
  }
  // Keep durations of clips that --only did not record again.
  const durations: Record<string, number> = existsSync(paths.clipsFile) ? JSON.parse(readFileSync(paths.clipsFile, 'utf8')) : {};
  let app: RunningApp | undefined;
  try {
    app = await startApp(config.app, paths.episodeDirectory);
    for (const file of demoFiles) {
      const name = basename(file, '.ts');
      console.log(`demo: ${name}`);
      const module = (await import(pathToFileURL(join(demosDirectory, file)).href)) as {default: Demo};
      const clipFile = join(paths.clipsDirectory, `${name}.mp4`);
      await recordDemo(module.default, clipFile, {
        baseUrl: app.baseUrl,
        viewport: config.viewport ?? {width: 1280, height: 800},
        deviceScaleFactor: config.deviceScaleFactor ?? 2,
      });
      durations[name] = probeVideoDuration(clipFile);
      writeFileSync(paths.clipsFile, JSON.stringify(durations, null, 2));
    }
  } finally {
    app?.stop();
  }
}
