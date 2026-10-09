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
  /** `reference` is the recording, relative to `videos/`, whose voice Chatterbox clones; null for its built-in voice. */
  | {engine: 'chatterbox'; preset: ChatterboxPreset; reference: string | null}
  | {engine: 'kokoro'; name: string};

/** The settings the Python TTS package receives for a voice. */
export type VoiceRequest =
  | {engine: 'chatterbox'; reference: string | null; exaggeration: number; cfgWeight: number}
  | {engine: 'kokoro'; name: string; speed: number};

export const defaultVoice = 'chatterbox';

const kokoroName = /^[ab][fm]_[a-z]+$/;

/**
 * Parses a voice from `script.yaml`: `chatterbox`, `chatterbox-calm`, `clone:<file>` and `clone-calm:<file>` (Chatterbox
 * cloning the voice of an audio file relative to `videos/`), or `kokoro:<name>` with an American (`a`) or British (`b`)
 * English Kokoro voice such as `kokoro:af_heart`.
 */
export function parseVoice(spec: string): Voice {
  if (spec === 'chatterbox') {
    return {engine: 'chatterbox', preset: 'default', reference: null};
  }
  if (spec === 'chatterbox-calm') {
    return {engine: 'chatterbox', preset: 'calm', reference: null};
  }
  const clone = /^clone(-calm)?:(.+)$/.exec(spec);
  if (clone) {
    return {engine: 'chatterbox', preset: clone[1] ? 'calm' : 'default', reference: clone[2]};
  }
  if (spec.startsWith('kokoro:')) {
    const name = spec.slice('kokoro:'.length);
    if (!kokoroName.test(name)) {
      throw new Error(`Unknown Kokoro voice '${name}'; use an English voice such as af_heart, am_michael or bm_george`);
    }
    return {engine: 'kokoro', name};
  }
  throw new Error(`Unknown voice '${spec}'; use chatterbox, chatterbox-calm, clone:<file>, clone-calm:<file> or kokoro:<name>`);
}

/** A short file-name-safe name for the voice, for example `chatterbox-calm`, `kokoro-af_heart` or `clone-rico-calm`. */
export function voiceLabel(voice: Voice): string {
  if (voice.engine === 'kokoro') {
    return `kokoro-${voice.name}`;
  }
  if (voice.reference === null) {
    return `chatterbox-${voice.preset}`;
  }
  const name = voice.reference.replace(/^.*[\\/]/, '').replace(/\.[^.]*$/, '').replace(/[^A-Za-z0-9_-]+/g, '-');
  return voice.preset === 'default' ? `clone-${name}` : `clone-${name}-${voice.preset}`;
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

/**
 * The request for the TTS package; a cloned voice needs the absolute path of its prepared reference recording.
 * `speed` is the engine's native speed from `tempoPlan`.
 */
export function voiceRequest(voice: Voice, preparedReference: string | null = null, speed = 1): VoiceRequest {
  if (voice.engine === 'kokoro') {
    return {engine: 'kokoro', name: voice.name, speed};
  }
  if (speed !== 1) {
    throw new Error(`Voice '${voiceLabel(voice)}' has no native speed; its tempo is applied with atempo`);
  }
  if (voice.reference !== null && preparedReference === null) {
    throw new Error(`Voice '${voiceLabel(voice)}' needs its prepared reference recording`);
  }
  return {engine: 'chatterbox', reference: preparedReference, ...chatterboxPresets[voice.preset]};
}

/**
 * What identifies synthesized audio in the cache besides its text: the engine version, the voice, its settings and
 * the engine's native `speed` from `tempoPlan`. A cloned voice is identified by the digest of its reference recording,
 * so a new recording at the same path is synthesized again.
 */
export function voiceIdentity(voice: Voice, referenceDigest: string | null = null, speed = 1): {voice: unknown; engine: string} {
  if (voice.engine === 'kokoro') {
    // Speed 1 keeps the identity it had before Kokoro took a speed, so its cached audio stays valid.
    return {voice: speed === 1 ? {name: voice.name} : {name: voice.name, speed}, engine: engineVersions.kokoro};
  }
  if (speed !== 1) {
    throw new Error(`Voice '${voiceLabel(voice)}' has no native speed; its tempo is applied with atempo`);
  }
  const settings = chatterboxPresets[voice.preset];
  if (voice.reference !== null) {
    if (referenceDigest === null) {
      throw new Error(`Voice '${voiceLabel(voice)}' needs the digest of its reference recording`);
    }
    return {voice: {reference: referenceDigest, ...settings}, engine: engineVersions.chatterbox};
  }
  // The built-in voice at default settings keeps the identity it had before voices were configurable, so its cached audio stays valid.
  return {voice: voice.preset === 'default' ? 'default' : settings, engine: engineVersions.chatterbox};
}
