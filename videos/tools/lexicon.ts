import {readFileSync} from 'node:fs';
import {parse} from 'yaml';
import {z} from 'zod';
import type {Voice} from './voice';

export type Engine = Voice['engine'];

/** `say` is the spoken form for every engine; `kokoro` and `chatterbox` replace it for that engine only. */
const lexiconSchema = z.array(z.object({
  match: z.string().min(1),
  say: z.string().min(1),
  kokoro: z.string().min(1).optional(),
  chatterbox: z.string().min(1).optional(),
}).strict());

export type LexiconEntry = z.infer<typeof lexiconSchema>[number];

export function loadLexicon(file: string): LexiconEntry[] {
  return lexiconSchema.parse(parse(readFileSync(file, 'utf8')) ?? []);
}

/** Replaces each whole-word match with the entry's spoken form for `engine`, the longest match first. */
export function applyLexicon(text: string, entries: LexiconEntry[], engine: Engine): string {
  if (entries.length === 0) {
    return text;
  }
  const replacements = new Map(entries.map(entry => [entry.match, entry[engine] ?? entry.say]));
  const alternatives = [...replacements.keys()]
    .sort((left, right) => right.length - left.length)
    .map(match => match.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'));
  // One alternation pass so a replacement is never matched again by a shorter entry.
  const pattern = new RegExp(`(?<!\\w)(?:${alternatives.join('|')})(?!\\w)`, 'g');
  return text.replace(pattern, match => replacements.get(match) ?? match);
}
