import {spawn, type ChildProcess} from 'node:child_process';
import {resolve} from 'node:path';
import type {CaptureConfig} from './config';

export interface RunningApp {
  baseUrl: string;
  stop(): void;
}

export async function startApp(app: NonNullable<CaptureConfig['app']>, episodeDirectory: string): Promise<RunningApp> {
  const baseUrl = `http://localhost:${app.port}`;
  const child: ChildProcess = spawn(
    'dotnet',
    ['run', '--project', resolve(episodeDirectory, app.project), '--urls', baseUrl],
    // Own process group so stop() also ends the app process that dotnet run starts.
    {detached: true, stdio: ['ignore', 'inherit', 'inherit'], env: {...process.env, ...app.environment}},
  );
  const stop = () => {
    if (child.pid !== undefined && child.exitCode === null) {
      process.kill(-child.pid, 'SIGTERM');
    }
  };

  const deadline = Date.now() + 180_000;
  while (Date.now() < deadline) {
    if (child.exitCode !== null) {
      throw new Error(`App exited with code ${child.exitCode} before it was ready`);
    }
    try {
      const response = await fetch(`${baseUrl}${app.readyPath}`);
      if (response.ok) {
        return {baseUrl, stop};
      }
    } catch {
      // Not listening yet.
    }
    await new Promise(resolveDelay => setTimeout(resolveDelay, 500));
  }
  stop();
  throw new Error(`App did not answer ${baseUrl}${app.readyPath} within 180 s`);
}
