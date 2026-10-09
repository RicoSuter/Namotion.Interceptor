import {readFileSync} from 'node:fs';
import {join} from 'node:path';
import {parse} from 'yaml';
import {z} from 'zod';
import {backgroundVariants} from '../../theme/backgrounds';
import {defaultVoice, parseVoice} from '../voice';

export const componentNames = [
  'CodeCard',
  'FlowDiagram',
  'SequenceDiagram',
  'ObjectGraph',
  'LiveChart',
  'LayerStack',
  'BrowserFrame',
  'Terminal',
  'ChapterCard',
  'Card',
  'Camera',
] as const;

/** Narration speed when script.yaml sets no tempo. */
export const defaultTempo = 1;

const kebabCase = /^[a-z0-9]+(?:-[a-z0-9]+)*$/;
const identifier = z.string().regex(kebabCase, 'ids are kebab-case');

const beatSchema = z
  .object({
    id: identifier,
    narration: z.string().trim().min(1).optional(),
    hold: z.number().positive().optional(),
    visual: z.string().trim().min(1),
    components: z.array(z.enum(componentNames)).min(1),
    code: z.object({file: z.string().min(1), region: z.string().min(1)}).optional(),
    demo: identifier.optional(),
    terminal: identifier.optional(),
  })
  .refine(beat => beat.narration !== undefined || beat.hold !== undefined, {
    message: 'a beat needs narration, hold, or both',
  });

const chapterSchema = z.object({
  id: identifier,
  title: z.string().trim().min(1),
  beats: z.array(beatSchema).min(1),
});

export const scriptSchema = z.object({
  episode: identifier,
  title: z.string().trim().min(1),
  /** Narration voice, see `parseVoice`. */
  voice: z
    .string()
    .default(defaultVoice)
    .superRefine((spec, context) => {
      try {
        parseVoice(spec);
      } catch (error) {
        context.addIssue({code: 'custom', message: (error as Error).message});
      }
    }),
  /** Speech speed factor applied to the synthesized narration, pitch preserved. */
  tempo: z.number().min(0.5).max(2).default(defaultTempo),
  /** Background variant; the theme default when omitted. */
  background: z.enum(backgroundVariants).optional(),
  chapters: z.array(chapterSchema).min(1),
});

export type Script = z.infer<typeof scriptSchema>;
export type Beat = Script['chapters'][number]['beats'][number];
export type ChapterBeat = Beat & {chapter: string; chapterTitle: string};

export function parseScript(yamlText: string): Script {
  const result = scriptSchema.safeParse(parse(yamlText));
  if (!result.success) {
    throw new Error(`Invalid script.yaml:\n${z.prettifyError(result.error)}`);
  }
  const seen = new Set<string>();
  for (const beat of allBeats(result.data)) {
    if (seen.has(beat.id)) {
      throw new Error(`Duplicate beat id '${beat.id}'`);
    }
    seen.add(beat.id);
  }
  return result.data;
}

export function loadScript(episodeDirectory: string): Script {
  return parseScript(readFileSync(join(episodeDirectory, 'script.yaml'), 'utf8'));
}

export function allBeats(script: Script): ChapterBeat[] {
  return script.chapters.flatMap(chapter => chapter.beats.map(beat => ({...beat, chapter: chapter.id, chapterTitle: chapter.title})));
}
