import {existsSync, mkdirSync, readdirSync, readFileSync, writeFileSync} from 'node:fs';
import {basename, join} from 'node:path';
import {pathToFileURL} from 'node:url';
import {loadCaptureConfig, validateEpisode} from '../episode';
import {probeVideoDuration} from '../ffmpeg';
import {episodeArgument, episodePaths} from '../paths';
import {startApp, type RunningApp} from './app';
import {appUrls, resolveApps, type Demo, type DemoPreparation, type TerminalCapture} from './config';
import {recordDemo} from './record';
import {runTerminalCapture} from './terminal';

const paths = episodePaths(episodeArgument());
const onlyIndex = process.argv.indexOf('--only');
const only = onlyIndex >= 0 ? process.argv[onlyIndex + 1] : undefined;
await validateEpisode(paths.episodeDirectory);
const config = await loadCaptureConfig(paths.episodeDirectory);
const apps = resolveApps(config);

const terminals = (config.terminal ?? []).filter(capture => !only || capture.name === only);
const demosDirectory = join(paths.episodeDirectory, 'demos');
const demoFiles = existsSync(demosDirectory)
  // Demos record in name order against the same running apps, so a demo can rely on the state an earlier one left.
  ? readdirSync(demosDirectory).filter(file => file.endsWith('.ts') && (!only || basename(file, '.ts') === only)).sort()
  : [];

mkdirSync(paths.terminalDirectory, {recursive: true});
async function captureTerminal(capture: TerminalCapture): Promise<void> {
  console.log(`terminal: ${capture.name}`);
  writeFileSync(join(paths.terminalDirectory, `${capture.name}.txt`), await runTerminalCapture(capture, paths.episodeDirectory));
}

for (const capture of terminals.filter(capture => !capture.whileAppsRun)) {
  await captureTerminal(capture);
}

const terminalsWhileAppsRun = terminals.filter(capture => capture.whileAppsRun);
if (demoFiles.length > 0 || terminalsWhileAppsRun.length > 0) {
  if (apps.length === 0) {
    throw new Error('Demos and terminal captures with whileAppsRun need an app or apps entry in capture.ts');
  }
  const running: Array<RunningApp & {name: string}> = [];
  try {
    for (const app of apps) {
      console.log(`app: ${app.name}`);
      running.push({...(await startApp(app, paths.episodeDirectory)), name: app.name});
    }
    for (const capture of terminalsWhileAppsRun) {
      await captureTerminal(capture);
    }

    // Keep durations of clips that --only did not record again.
    const durations: Record<string, number> = existsSync(paths.clipsFile) ? JSON.parse(readFileSync(paths.clipsFile, 'utf8')) : {};
    const marks: Record<string, Record<string, number>> = existsSync(paths.marksFile) ? JSON.parse(readFileSync(paths.marksFile, 'utf8')) : {};
    const urls = appUrls(running);
    for (const file of demoFiles) {
      const name = basename(file, '.ts');
      console.log(`demo: ${name}`);
      const module = (await import(pathToFileURL(join(demosDirectory, file)).href)) as {default: Demo; prepare?: DemoPreparation};
      await module.prepare?.(urls);
      const clipFile = join(paths.clipsDirectory, `${name}.mp4`);
      marks[name] = await recordDemo(module.default, clipFile, {
        apps: urls,
        viewport: config.viewport ?? {width: 1280, height: 800},
        deviceScaleFactor: config.deviceScaleFactor ?? 2,
      });
      durations[name] = probeVideoDuration(clipFile);
      writeFileSync(paths.clipsFile, JSON.stringify(durations, null, 2));
      writeFileSync(paths.marksFile, JSON.stringify(marks, null, 2));
    }
  } finally {
    // Clients first, so a server does not log their disconnects as errors.
    for (const app of running.reverse()) {
      app.stop();
    }
  }
}
