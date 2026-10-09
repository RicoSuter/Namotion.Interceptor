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

/** Parses an audition argument, `<voice>` or `<label>=<voice>`; the label names the files and defaults to the voice's. */
export function parseAuditionVoice(argument: string): {label: string; spec: string} {
  const labelled = /^([A-Za-z0-9_-]+)=(.+)$/.exec(argument);
  const spec = labelled ? labelled[2] : argument;
  return {label: labelled ? labelled[1] : voiceLabel(parseVoice(spec)), spec};
}

export function auditionReadme(lines: AuditionLine[], results: AuditionResult[]): string {
  let start = 0;
  const rows = results.map((result, index) => {
    const row = `| ${index + 1} | ${result.label} | \`${result.spec}\` | ${formatTime(start)} | ${result.allSeconds.toFixed(1)} s | ${Math.round(wordsPerMinute(result.words, result.speechSeconds))} | ${result.peakBeforeNormalization.toFixed(1)} dBTP |`;
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
    '| # | Voice | `voice:` in script.yaml | Starts at | Duration | Words per minute | Peak before normalization |',
    '|---|---|---|---|---|---|---|',
    ...rows,
    '',
    'Words per minute count the written words of the three lines over the length of their three clips, including the silence the engine leaves at the start and end of a clip, as the pipeline plays them at tempo 1.',
    '',
  ].join('\n');
}

function formatTime(seconds: number): string {
  const minutes = Math.floor(seconds / 60);
  return `${minutes}:${(seconds - minutes * 60).toFixed(1).padStart(4, '0')}`;
}
