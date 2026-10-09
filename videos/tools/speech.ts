import {spawnSync} from 'node:child_process';
import {createHash} from 'node:crypto';
import {existsSync, mkdirSync, readFileSync, renameSync, writeFileSync} from 'node:fs';
import {join, resolve} from 'node:path';
import {probeDuration, runFfmpeg} from './ffmpeg';
import {ttsProjectDirectory, videosRoot, voicesDirectory} from './paths';
import {voiceIdentity, voiceRequest, type Voice, type VoiceRequest} from './voice';

/** Bump when the preparation of reference recordings changes, so cloned voices are synthesized again. */
const referencePreparation = 'reference-1';

/** Sample rate of Chatterbox's reference conditioning (S3GEN_SR); its speech prompt uses at most the first 10 s. */
const referenceSampleRate = 24000;

/** Edge silence below this level is trimmed from a reference recording. */
const referenceSilenceThreshold = '-45dB';

export interface ResolvedVoice {
  request: VoiceRequest;
  /** The voice's cache identity, see `voiceIdentity`. */
  identity: {voice: unknown; engine: string};
}

/** Resolves a voice for synthesis; a cloned voice's reference recording is prepared first (cached by content). */
export function resolveVoice(voice: Voice): ResolvedVoice {
  if (voice.engine === 'kokoro' || voice.reference === null) {
    return {request: voiceRequest(voice), identity: voiceIdentity(voice)};
  }
  const reference = prepareReference(resolve(videosRoot, voice.reference));
  return {request: voiceRequest(voice, reference.file), identity: voiceIdentity(voice, reference.digest)};
}

/**
 * Converts a recording in any audio format to what Chatterbox clones best: mono WAV at its sample rate, edge silence
 * trimmed, loudness normalized. The result is cached in `voices/.prepared/` by the recording's content.
 */
export function prepareReference(file: string): {file: string; digest: string} {
  if (!existsSync(file)) {
    throw new Error(`Reference recording ${file} not found`);
  }
  const digest = createHash('sha256').update(referencePreparation).update(readFileSync(file)).digest('hex').slice(0, 16);
  const directory = join(voicesDirectory, '.prepared');
  const prepared = join(directory, `${digest}.wav`);
  if (!existsSync(prepared)) {
    mkdirSync(directory, {recursive: true});
    const trimStart = `silenceremove=start_periods=1:start_threshold=${referenceSilenceThreshold}:detection=peak`;
    // Write then rename so an interrupted run never leaves a truncated file in the cache.
    const temporary = join(directory, `${digest}.tmp.wav`);
    runFfmpeg([
      '-i', file,
      '-af', `highpass=f=60,${trimStart},areverse,${trimStart},areverse,loudnorm=I=-16:TP=-1.5:LRA=11,aresample=${referenceSampleRate}`,
      '-ac', '1', '-ar', `${referenceSampleRate}`, '-c:a', 'pcm_s16le',
      temporary,
    ]);
    renameSync(temporary, prepared);
    const seconds = probeDuration(prepared);
    console.log(`Prepared reference ${file}: ${seconds.toFixed(1)} s${seconds < 8 ? ' (short: Chatterbox clones best from about 10 s of speech)' : ''}`);
  }
  return {file: prepared, digest};
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
