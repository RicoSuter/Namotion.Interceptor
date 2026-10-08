import type {Page} from 'playwright';

export interface DemoContext {
  baseUrl: string;
}

/** A demo script is choreography: timed pauses set pacing, condition waits cover app state that varies. */
export type Demo = (page: Page, context: DemoContext) => Promise<void>;

export interface TerminalCapture {
  name: string;
  command: string;
  args: string[];
  /** Relative to the episode directory. */
  cwd?: string;
  /** Stop the process once its output matches this regular expression. */
  until?: string;
  timeoutSeconds?: number;
}

export interface CaptureConfig {
  app?: {
    /** Path to the .csproj, relative to the episode directory. */
    project: string;
    port: number;
    readyPath: string;
    environment?: Record<string, string>;
  };
  viewport?: {width: number; height: number};
  terminal?: TerminalCapture[];
}

export function defineCapture(config: CaptureConfig): CaptureConfig {
  return config;
}
