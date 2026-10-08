import {validateEpisode} from './episode';
import {allBeats} from './schema/script';
import {episodeArgument, episodePaths} from './paths';

const paths = episodePaths(episodeArgument());
const script = await validateEpisode(paths.episodeDirectory);
const words = allBeats(script).reduce((total, beat) => total + (beat.narration?.split(/\s+/).length ?? 0), 0);
// The default voice speaks about 200 words per minute (measured on the smoke and connectors episodes).
console.log(`${script.episode}: ${allBeats(script).length} beats, ${words} words, about ${(words / 200).toFixed(1)} min of narration`);
