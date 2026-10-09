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
  | {engine: 'chatterbox'; reference: string | null; exaggeration: number; cfgWeight: number}
  | {engine: 'kokoro'; name: string};

export const defaultVoice = 'chatterbox';

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

export function voiceRequest(voice: Voice): VoiceRequest {
  return voice.engine === 'kokoro'
    ? {engine: 'kokoro', name: voice.name}
    : {engine: 'chatterbox', reference: null, ...chatterboxPresets[voice.preset]};
}

/** What identifies synthesized audio in the cache besides its text: the engine version, the voice and its settings. */
export function voiceIdentity(voice: Voice): {voice: unknown; engine: string} {
  if (voice.engine === 'kokoro') {
    return {voice: {name: voice.name}, engine: engineVersions.kokoro};
  }
  // The built-in voice at default settings keeps the identity it had before voices were configurable, so its cached audio stays valid.
  return {voice: voice.preset === 'default' ? 'default' : chatterboxPresets[voice.preset], engine: engineVersions.chatterbox};
}
