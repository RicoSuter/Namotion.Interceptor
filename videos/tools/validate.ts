import {validateEpisode} from './episode';
import {allBeats} from './schema/script';
import {episodeArgument, episodePaths} from './paths';

const paths = episodePaths(episodeArgument());
const script = await validateEpisode(paths.episodeDirectory);
const words = allBeats(script).reduce((total, beat) => total + (beat.narration?.split(/\s+/).length ?? 0), 0);
// About 150 spoken words per minute is a typical narration pace.
console.log(`${script.episode}: ${allBeats(script).length} beats, ${words} words, about ${(words / 150).toFixed(1)} min of narration`);
