/** Chatterbox settings by preset name; lower values give a slower, more neutral delivery. */
export const chatterboxPresets = {
  default: {exaggeration: 0.5, cfgWeight: 0.5},
  calm: {exaggeration: 0.35, cfgWeight: 0.3},
} as const;

export type ChatterboxPreset = keyof typeof chatterboxPresets;

/** Bump an engine's version when its synthesis changes, so its cached audio is regenerated. */
export const engineVersions = {chatterbox: 'chatterbox-1', kokoro: 'kokoro-1'} as const;

/** A narration voice: the speech engine and its settings. */
export type Voice =
  | {engine: 'chatterbox'; preset: ChatterboxPreset}
  | {engine: 'kokoro'; name: string};

/** The settings the Python TTS package receives for a voice. */
export type VoiceRequest =
  | {engine: 'chatterbox'; exaggeration: number; cfgWeight: number}
  | {engine: 'kokoro'; name: string; speed: number};

export const defaultVoice = 'kokoro:am_michael';

const kokoroName = /^[ab][fm]_[a-z]+$/;

/**
 * Parses a voice from `script.yaml`: `chatterbox`, `chatterbox-calm`, or `kokoro:<name>` with an American (`a`) or
 * British (`b`) English Kokoro voice such as `kokoro:af_heart`.
 */
export function parseVoice(spec: string): Voice {
  if (spec === 'chatterbox') {
    return {engine: 'chatterbox', preset: 'default'};
  }
  if (spec === 'chatterbox-calm') {
    return {engine: 'chatterbox', preset: 'calm'};
  }
  if (spec.startsWith('kokoro:')) {
    const name = spec.slice('kokoro:'.length);
    if (!kokoroName.test(name)) {
      throw new Error(`Unknown Kokoro voice '${name}'; use an English voice such as af_heart, am_michael or bm_george`);
    }
    return {engine: 'kokoro', name};
  }
  throw new Error(`Unknown voice '${spec}'; use chatterbox, chatterbox-calm or kokoro:<name>`);
}

/** A short file-name-safe name for the voice, for example `chatterbox-calm` or `kokoro-af_heart`. */
export function voiceLabel(voice: Voice): string {
  return voice.engine === 'kokoro' ? `kokoro-${voice.name}` : `chatterbox-${voice.preset}`;
}

/**
 * Highest speed Kokoro synthesizes at natively. Kokoro rounds every phoneme to whole frames, so above about 1.3 short
 * phonemes collapse: a line's first article or a final consonant goes missing (measured with Whisper on the
 * connectors episode). Faster tempos add `atempo` on top.
 */
export const kokoroMaximumSpeed = 1.25;

/**
 * How a voice reaches a narration tempo: the engine's native `speed` and the factor ffmpeg's pitch-preserving
 * `atempo` applies afterwards. Kokoro synthesizes at the tempo itself, which keeps its prosody natural, up to
 * `kokoroMaximumSpeed`. Chatterbox has no speed control, so its speech is synthesized at the natural pace and sped up.
 */
export function tempoPlan(voice: Voice, tempo: number): {speed: number; atempo: number} {
  if (voice.engine !== 'kokoro') {
    return {speed: 1, atempo: tempo};
  }
  const speed = Math.min(tempo, kokoroMaximumSpeed);
  // Rounded so the factor in cache keys and filter arguments carries no floating point noise.
  return {speed, atempo: Number((tempo / speed).toFixed(4))};
}

/** The request for the TTS package; `speed` is the engine's native speed from `tempoPlan`. */
export function voiceRequest(voice: Voice, speed = 1): VoiceRequest {
  if (voice.engine === 'kokoro') {
    return {engine: 'kokoro', name: voice.name, speed};
  }
  if (speed !== 1) {
    throw new Error(`Voice '${voiceLabel(voice)}' has no native speed; its tempo is applied with atempo`);
  }
  return {engine: 'chatterbox', ...chatterboxPresets[voice.preset]};
}

/**
 * What identifies synthesized audio in the cache besides its text: the engine version, the voice, its settings and
 * the engine's native `speed` from `tempoPlan`.
 */
export function voiceIdentity(voice: Voice, speed = 1): {voice: unknown; engine: string} {
  if (voice.engine === 'kokoro') {
    return {voice: {name: voice.name, speed}, engine: engineVersions.kokoro};
  }
  if (speed !== 1) {
    throw new Error(`Voice '${voiceLabel(voice)}' has no native speed; its tempo is applied with atempo`);
  }
  return {voice: chatterboxPresets[voice.preset], engine: engineVersions.chatterbox};
}
