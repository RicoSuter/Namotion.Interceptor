import {existsSync, readFileSync} from 'node:fs';
import type {Timing} from '../theme/timing';
import {validateEpisode} from './episode';
import {durationReport, formatDurationReport} from './estimate';
import {episodeArgument, episodePaths} from './paths';
import {loadScript} from './schema/script';

const paths = episodePaths(episodeArgument());
// The durations need only the script, so they print before the checks of files that may not exist yet.
const script = loadScript(paths.episodeDirectory);
const timing = existsSync(paths.timingFile) ? (JSON.parse(readFileSync(paths.timingFile, 'utf8')) as Timing) : null;
console.log(formatDurationReport(script, durationReport(script, timing)));
await validateEpisode(paths.episodeDirectory);
console.log(`${script.episode} is valid`);
