import {spawn} from 'node:child_process';
import {resolve} from 'node:path';
import type {TerminalCapture} from './config';

export function stripAnsi(text: string): string {
  return text.replace(/\u001b\[[0-9;?]*[A-Za-z]/g, '');
}

/** Drops what a stopped process printed after the line that matched, such as its shutdown messages. */
export function cutAfterMatch(output: string, until: RegExp | null): string {
  const match = until?.exec(output);
  if (!match) {
    return output;
  }
  const lineEnd = output.indexOf('\n', match.index + match[0].length);
  return lineEnd < 0 ? output : output.slice(0, lineEnd + 1);
}

/** Runs the command and returns a transcript: the prompt line followed by its combined output. */
export function runTerminalCapture(capture: TerminalCapture, episodeDirectory: string): Promise<string> {
  const cwd = resolve(episodeDirectory, capture.cwd ?? '.');
  const until = capture.until ? new RegExp(capture.until) : null;
  const timeout = (capture.timeoutSeconds ?? 120) * 1000;

  return new Promise((resolvePromise, reject) => {
    const child = spawn(capture.command, capture.args, {cwd, env: {...process.env, NO_COLOR: '1', DOTNET_NOLOGO: '1'}});
    let output = '';
    let stopped = false;
    const stop = () => {
      stopped = true;
      child.kill('SIGTERM');
    };
    const timer = setTimeout(() => {
      stop();
      reject(new Error(`Terminal capture '${capture.name}' timed out after ${timeout / 1000} s`));
    }, timeout);
    const onData = (chunk: Buffer) => {
      output += chunk.toString();
      if (until && until.test(output) && !stopped) {
        stop();
      }
    };
    child.stdout.on('data', onData);
    child.stderr.on('data', onData);
    child.on('error', reject);
    child.on('close', code => {
      clearTimeout(timer);
      if (!stopped && code !== 0) {
        reject(new Error(`Terminal capture '${capture.name}' exited with ${code}:\n${output}`));
        return;
      }
      resolvePromise(`$ ${[capture.command, ...capture.args].join(' ')}\n${cutAfterMatch(stripAnsi(output), until)}`);
    });
  });
}
