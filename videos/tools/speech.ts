import {spawnSync} from 'node:child_process';
import {mkdirSync, writeFileSync} from 'node:fs';
import {join} from 'node:path';
import {ttsProjectDirectory} from './paths';
import type {VoiceRequest} from './voice';

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
