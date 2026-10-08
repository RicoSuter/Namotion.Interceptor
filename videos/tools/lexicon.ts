import {readFileSync} from 'node:fs';
import {parse} from 'yaml';
import {z} from 'zod';

const lexiconSchema = z.array(z.object({match: z.string().min(1), say: z.string().min(1)}));

export type LexiconEntry = z.infer<typeof lexiconSchema>[number];

export function loadLexicon(file: string): LexiconEntry[] {
  return lexiconSchema.parse(parse(readFileSync(file, 'utf8')) ?? []);
}

export function applyLexicon(text: string, entries: LexiconEntry[]): string {
  if (entries.length === 0) {
    return text;
  }
  const replacements = new Map(entries.map(entry => [entry.match, entry.say]));
  const alternatives = [...replacements.keys()]
    .sort((left, right) => right.length - left.length)
    .map(match => match.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'));
  // One alternation pass so a replacement is never matched again by a shorter entry.
  const pattern = new RegExp(`(?<!\\w)(?:${alternatives.join('|')})(?!\\w)`, 'g');
  return text.replace(pattern, match => replacements.get(match) ?? match);
}
