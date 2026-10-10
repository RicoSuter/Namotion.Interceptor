import {applyLexicon, type LexiconEntry} from './lexicon';

const units = ['zero', 'one', 'two', 'three', 'four', 'five', 'six', 'seven', 'eight', 'nine', 'ten', 'eleven', 'twelve',
  'thirteen', 'fourteen', 'fifteen', 'sixteen', 'seventeen', 'eighteen', 'nineteen'];
const tens = ['', '', 'twenty', 'thirty', 'forty', 'fifty', 'sixty', 'seventy', 'eighty', 'ninety'];

/** Words a transcription model spells either way; both sides are mapped to the first form. */
const homophones: Array<[string, string]> = [['gray', 'grey'], ['write', 'right'], ['writes', 'rights'], ['base', 'bass']];

export interface TranscriptResult {
  id: string;
  text: string;
  heard: string;
  matches: boolean;
}

/**
 * Normalizes a line for comparison with a transcript: lower case, punctuation dropped (`.NET` reads `dotnet`), number
 * words as digits, common homophones unified.
 */
export function normalizeTranscript(text: string): string {
  const words = text.toLowerCase().replace(/\.net\b/g, ' dotnet').replace(/[-–]/g, ' ').replace(/[^a-z0-9#' ]/g, ' ').split(/\s+/).filter(word => word.length > 0);
  const numbers: string[] = [];
  for (let index = 0; index < words.length; index++) {
    const ten = tens.indexOf(words[index]);
    const unit = units.indexOf(words[index + 1] ?? '');
    if (ten >= 2 && unit >= 1 && unit <= 9) {
      numbers.push(String(ten * 10 + unit));
      index++;
    } else if (ten >= 2) {
      numbers.push(String(ten * 10));
    } else if (units.includes(words[index])) {
      numbers.push(String(units.indexOf(words[index])));
    } else {
      numbers.push(words[index]);
    }
  }
  return numbers.map(word => homophones.find(pair => pair[1] === word)?.[0] ?? word).join(' ');
}

/**
 * Whether a transcript matches a narrated line, as written or as spoken with the lexicon's plain `say` forms (the
 * engine-specific forms can hold phoneme markup). Word boundaries are ignored, so `WebSocket` matches `web socket`
 * and `O P C U A` matches `OPC UA`.
 */
export function transcriptMatches(text: string, heard: string, lexicon: LexiconEntry[]): boolean {
  const spoken = applyLexicon(text, lexicon.map(entry => ({match: entry.match, say: entry.say})), 'kokoro');
  const compact = (line: string) => normalizeTranscript(line).replaceAll(' ', '');
  return compact(text) === compact(heard) || compact(spoken) === compact(heard);
}

export function transcriptReport(episode: string, results: TranscriptResult[]): string {
  const mismatches = results.filter(result => !result.matches);
  return [
    `# Transcript check: ${episode}`,
    '',
    `${results.length - mismatches.length} of ${results.length} narrated beats match their transcript.`,
    '',
    ...mismatches.flatMap(result => [`- ${result.id}`, `  - text: ${result.text}`, `  - heard: ${result.heard}`]),
    '',
  ].join('\n');
}
