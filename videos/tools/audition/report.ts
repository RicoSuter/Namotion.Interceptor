import {parseVoice, voiceLabel} from '../voice';
import {targetLoudness, truePeakCeiling, wordsPerMinute} from './audio';

export interface AuditionLine {
  id: string;
  purpose: string;
  text: string;
}

export interface AuditionResult {
  label: string;
  /** The `voice:` value of `script.yaml` that selects this voice. */
  spec: string;
  /** The `tempo:` value of `script.yaml` the clips were made at. */
  tempo: number;
  /** Seconds of the voice's `-all.wav`, gaps included. */
  allSeconds: number;
  /** Seconds of the three clips alone, as the pipeline would play them. */
  speechSeconds: number;
  words: number;
  /** Highest true peak of the synthesized clips before normalization, in dBTP; near 0 hints at clipping. */
  peakBeforeNormalization: number;
}

export const lineGapSeconds = 0.6;
export const voiceGapSeconds = 1.5;

/**
 * Parses an audition argument, `[<label>=]<voice>[@<tempo>]`, for example `kokoro:am_michael@1.3`. The label names
 * the files and defaults to the voice's, with `-x<tempo>` appended for a tempo other than 1.
 */
export function parseAuditionVoice(argument: string): {label: string; spec: string; tempo: number} {
  const labelled = /^([A-Za-z0-9_-]+)=(.+)$/.exec(argument);
  const voiceAndTempo = labelled ? labelled[2] : argument;
  const withTempo = /^(.+)@([0-9.]+)$/.exec(voiceAndTempo);
  const spec = withTempo ? withTempo[1] : voiceAndTempo;
  const tempo = withTempo ? Number(withTempo[2]) : 1;
  if (!Number.isFinite(tempo) || tempo < 0.5 || tempo > 2) {
    throw new Error(`Audition tempo must be a number between 0.5 and 2, got '${withTempo?.[2]}'`);
  }
  const defaultLabel = voiceLabel(parseVoice(spec)) + (tempo === 1 ? '' : `-x${tempo}`);
  return {label: labelled ? labelled[1] : defaultLabel, spec, tempo};
}

export function auditionReadme(lines: AuditionLine[], results: AuditionResult[]): string {
  let start = 0;
  const rows = results.map((result, index) => {
    const row = `| ${index + 1} | ${result.label} | \`${result.spec}\` | ${result.tempo} | ${formatTime(start)} | ${result.allSeconds.toFixed(1)} s | ${Math.round(wordsPerMinute(result.words, result.speechSeconds))} | ${result.peakBeforeNormalization.toFixed(1)} dBTP |`;
    start += result.allSeconds + voiceGapSeconds;
    return row;
  });
  return [
    '# Voice audition',
    '',
    `\`audition.wav\` plays every voice's \`<voice>-all.wav\` in the order below, with ${voiceGapSeconds} s between voices. Each \`-all.wav\` plays lines A, B and C with ${lineGapSeconds} s between them; \`<voice>-<line>.wav\` holds a single line. Every clip is normalized to ${targetLoudness} LUFS integrated with a linear gain (true peak at most ${truePeakCeiling} dBTP), so loudness does not bias the comparison.`,
    '',
    ...lines.map(line => `- ${line.id.toUpperCase()} (${line.purpose}): ${line.text}`),
    '',
    '| # | Voice | `voice:` in script.yaml | `tempo:` | Starts at | Duration | Words per minute | Peak before normalization |',
    '|---|---|---|---|---|---|---|---|',
    ...rows,
    '',
    'Words per minute count the written words of the three lines over the length of their three clips, including the silence the engine leaves at the start and end of a clip, as the pipeline plays them at the listed tempo (Kokoro synthesizes at it natively, other voices are sped up with atempo). An episode runs slower than this, since every narrated beat adds 0.4 s of silence.',
    '',
  ].join('\n');
}

function formatTime(seconds: number): string {
  const minutes = Math.floor(seconds / 60);
  return `${minutes}:${(seconds - minutes * 60).toFixed(1).padStart(4, '0')}`;
}
