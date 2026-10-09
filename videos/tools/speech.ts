import {spawnSync} from 'node:child_process';
import {existsSync, mkdirSync, renameSync, writeFileSync} from 'node:fs';
import {join} from 'node:path';
import {runFfmpeg} from './ffmpeg';
import {ttsProjectDirectory} from './paths';
import {tempoPlan, voiceIdentity, voiceRequest, type Voice, type VoiceRequest} from './voice';

export interface ResolvedVoice {
  request: VoiceRequest;
  /** The voice's cache identity at the tempo's native speed, see `voiceIdentity`. */
  identity: {voice: unknown; engine: string};
  /** The factor ffmpeg's `atempo` applies to the synthesized speech; 1 when the engine reaches the tempo itself. */
  atempo: number;
}

/** Resolves a voice for synthesis at a narration tempo, see `tempoPlan`. */
export function resolveVoice(voice: Voice, tempo = 1): ResolvedVoice {
  const {speed, atempo} = tempoPlan(voice, tempo);
  return {request: voiceRequest(voice, speed), identity: voiceIdentity(voice, speed), atempo};
}

/**
 * Writes the copy of `<key>.wav` that plays at `atempo` as `<audioKey>.wav`, unless it exists or the factor is 1
 * (then both keys name the same file).
 */
export function applyTempo(directory: string, key: string, audioKey: string, atempo: number): void {
  const audioFile = join(directory, `${audioKey}.wav`);
  if (atempo === 1 || existsSync(audioFile)) {
    return;
  }
  // Write then rename so an interrupted run never leaves a truncated file in the cache.
  const temporaryFile = join(directory, `${audioKey}.tmp.wav`);
  runFfmpeg(['-i', join(directory, `${key}.wav`), '-af', `atempo=${atempo}`, temporaryFile]);
  renameSync(temporaryFile, audioFile);
}

/** Synthesizes every line whose `<key>.wav` is missing in `outputDirectory`, with one voice. */
export function synthesize(outputDirectory: string, voice: VoiceRequest, items: {key: string; text: string}[]): void {
  mkdirSync(outputDirectory, {recursive: true});
  const requestFile = join(outputDirectory, 'narration-request.json');
  writeFileSync(requestFile, JSON.stringify({outputDirectory, voice, items}, null, 2));

  // Kokoro is an optional extra of the TTS project, installed on first use.
  const extras = voice.engine === 'kokoro' ? ['--extra', 'kokoro'] : [];
  const result = spawnSync('uv', ['run', ...extras, 'python', '-m', 'video_tts', requestFile], {cwd: ttsProjectDirectory, stdio: 'inherit'});
  if (result.status !== 0) {
    throw new Error(`Speech synthesis failed with exit code ${result.status}`);
  }
}
