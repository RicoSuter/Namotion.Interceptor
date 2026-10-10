/** Integrated loudness every audition clip is normalized to, in LUFS. */
export const targetLoudness = -16;

/** True peak ceiling of normalized clips, in dBTP. */
export const truePeakCeiling = -1.5;

/** Sample rate of every audition file; both engines synthesize at 24 kHz. */
export const auditionSampleRate = 24000;

const loudnormTargets = `I=${targetLoudness}:TP=${truePeakCeiling}:LRA=11`;

export interface LoudnessMeasurement {
  input_i: string;
  input_tp: string;
  input_lra: string;
  input_thresh: string;
  target_offset: string;
}

/** Arguments of the first loudnorm pass, which only measures. */
export function measureLoudnessArgs(file: string): string[] {
  return ['-i', file, '-af', `loudnorm=${loudnormTargets}:print_format=json`, '-f', 'null', '-'];
}

/** Reads the JSON block loudnorm prints at the end of its log. */
export function parseLoudnessMeasurement(stderr: string): LoudnessMeasurement {
  const start = stderr.lastIndexOf('{');
  const end = stderr.lastIndexOf('}');
  if (start < 0 || end < start) {
    throw new Error(`No loudnorm measurement in ffmpeg output:\n${stderr}`);
  }
  return JSON.parse(stderr.slice(start, end + 1)) as LoudnessMeasurement;
}

/** Arguments of the second loudnorm pass: a linear gain from the measurement, so the delivery itself is unchanged. */
export function normalizeLoudnessArgs(input: string, measurement: LoudnessMeasurement, output: string): string[] {
  const measured = `measured_I=${measurement.input_i}:measured_TP=${measurement.input_tp}:measured_LRA=${measurement.input_lra}:measured_thresh=${measurement.input_thresh}:offset=${measurement.target_offset}`;
  return ['-i', input, '-af', `loudnorm=${loudnormTargets}:${measured}:linear=true,aresample=${auditionSampleRate}`, ...pcmOutput(output)];
}

/** Arguments that join `files` in order with `gapSeconds` of silence between them. */
export function concatWithGapsArgs(files: string[], gapSeconds: number, output: string): string[] {
  if (files.length === 0) {
    throw new Error('Nothing to join');
  }
  const inputs: string[] = [];
  const segments: string[] = [];
  files.forEach((file, index) => {
    if (index > 0) {
      inputs.push('-f', 'lavfi', '-t', `${gapSeconds}`, '-i', `anullsrc=r=${auditionSampleRate}:cl=mono`);
      segments.push(`[${segments.length}:a]`);
    }
    inputs.push('-i', file);
    segments.push(`[${segments.length}:a]`);
  });
  return [...inputs, '-filter_complex', `${segments.join('')}concat=n=${segments.length}:v=0:a=1[out]`, '-map', '[out]', ...pcmOutput(output)];
}

function pcmOutput(output: string): string[] {
  return ['-ar', `${auditionSampleRate}`, '-ac', '1', '-c:a', 'pcm_s16le', output];
}

export function wordsPerMinute(words: number, seconds: number): number {
  return (words / seconds) * 60;
}
