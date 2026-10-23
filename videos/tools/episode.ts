import {existsSync, readFileSync} from 'node:fs';
import {join, resolve} from 'node:path';
import {pathToFileURL} from 'node:url';
import {extractRegion} from '../theme/regions';
import type {CaptureConfig} from './capture/config';
import {allBeats, loadScript, type Script} from './schema/script';

export async function loadCaptureConfig(episodeDirectory: string): Promise<CaptureConfig> {
  const file = join(episodeDirectory, 'capture.ts');
  if (!existsSync(file)) {
    return {};
  }
  const module = (await import(pathToFileURL(file).href)) as {default: CaptureConfig};
  return module.default;
}

/** Validates the script and every file, region, demo and terminal capture it references. */
export async function validateEpisode(episodeDirectory: string): Promise<Script> {
  const script = loadScript(episodeDirectory);
  const capture = await loadCaptureConfig(episodeDirectory);
  const terminalNames = new Set((capture.terminal ?? []).map(entry => entry.name));
  const problems: string[] = [];

  for (const beat of allBeats(script)) {
    if (beat.code) {
      const file = resolve(episodeDirectory, beat.code.file);
      if (!existsSync(file)) {
        problems.push(`beat '${beat.id}': ${beat.code.file} does not exist`);
      } else {
        try {
          extractRegion(readFileSync(file, 'utf8'), beat.code.region);
        } catch (error) {
          problems.push(`beat '${beat.id}': ${(error as Error).message} in ${beat.code.file}`);
        }
      }
    }
    if (beat.demo && !existsSync(join(episodeDirectory, 'demos', `${beat.demo}.ts`))) {
      problems.push(`beat '${beat.id}': demos/${beat.demo}.ts does not exist`);
    }
    if (beat.terminal && !terminalNames.has(beat.terminal)) {
      problems.push(`beat '${beat.id}': terminal capture '${beat.terminal}' is not defined in capture.ts`);
    }
  }

  if (problems.length > 0) {
    throw new Error(`Episode '${script.episode}' is invalid:\n- ${problems.join('\n- ')}`);
  }
  return script;
}
