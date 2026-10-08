import {readFileSync} from 'node:fs';
import {join} from 'node:path';
import {parse} from 'yaml';
import {z} from 'zod';

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
  'Caption',
] as const;

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
  voice: z.string().min(1).default('default'),
  chapters: z.array(chapterSchema).min(1),
});

export type Script = z.infer<typeof scriptSchema>;
export type Beat = Script['chapters'][number]['beats'][number];
export type ChapterBeat = Beat & {chapter: string};

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
  return script.chapters.flatMap(chapter => chapter.beats.map(beat => ({...beat, chapter: chapter.id})));
}
