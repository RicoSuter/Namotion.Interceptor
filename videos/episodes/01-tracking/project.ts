import {episodeProject} from '../../theme/project';
import derived from './scenes/derived';
import hook from './scenes/hook';
import lifecycle from './scenes/lifecycle';
import recap from './scenes/recap';
import setup from './scenes/setup';
import streams from './scenes/streams';
import transactions from './scenes/transactions';

export default episodeProject([hook, setup, derived, streams, lifecycle, transactions, recap]);
