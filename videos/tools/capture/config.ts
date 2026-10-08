import type {Page} from 'playwright';

export interface DemoContext {
  /** Base URL of the first app. */
  baseUrl: string;
  /** Base URL of every app by name. */
  baseUrls: Record<string, string>;
}

/** A demo script is choreography: timed pauses set pacing, condition waits cover app state that varies. */
export type Demo = (page: Page, context: DemoContext) => Promise<void>;

/**
 * Optional `prepare` export of a demo module. It runs before the recording starts, for example to wait until
 * an app reaches a state or to reset it, so the clip does not begin with a long wait.
 */
export type DemoPreparation = (context: DemoContext) => Promise<void>;

export interface TerminalCapture {
  name: string;
  command: string;
  args: string[];
  /** Relative to the episode directory. */
  cwd?: string;
  /** Stop the process once its output matches this regular expression. */
  until?: string;
  timeoutSeconds?: number;
  /** Runs while the apps are up, for example a client that connects to one of them. Otherwise it runs before they start. */
  whileAppsRun?: boolean;
}

export interface AppConfig {
  /** Key of the app's base URL in the demo context. Required when several apps are configured. */
  name?: string;
  /** Path to the .csproj, relative to the episode directory. */
  project: string;
  port: number;
  readyPath: string;
  environment?: Record<string, string>;
}

export interface CaptureConfig {
  /** The one app the demos record. */
  app?: AppConfig;
  /** Several apps, started in order and each awaited until ready, for samples with more than one process. */
  apps?: AppConfig[];
  /** Demo page size in CSS pixels. Defaults to 1280 by 800. */
  viewport?: {width: number; height: number};
  /** Device pixels per CSS pixel for demo recordings. Defaults to 2 so text stays crisp when scaled. */
  deviceScaleFactor?: number;
  terminal?: TerminalCapture[];
}

export type NamedAppConfig = AppConfig & {name: string};

export function defineCapture(config: CaptureConfig): CaptureConfig {
  return config;
}

/** The apps to start, in order, each with a name; `app` alone is named 'app'. */
export function resolveApps(config: CaptureConfig): NamedAppConfig[] {
  if (config.app && config.apps) {
    throw new Error('capture.ts sets both app and apps; use apps for several apps');
  }
  if (config.app) {
    return [{...config.app, name: config.app.name ?? 'app'}];
  }
  const apps = config.apps ?? [];
  const names = new Set<string>();
  const ports = new Set<number>();
  return apps.map((app, index) => {
    if (!app.name) {
      throw new Error(`apps[${index}] needs a name when several apps are configured`);
    }
    if (names.has(app.name)) {
      throw new Error(`apps[${index}]: the name '${app.name}' is used twice`);
    }
    if (ports.has(app.port)) {
      throw new Error(`apps[${index}]: the port ${app.port} is used twice`);
    }
    names.add(app.name);
    ports.add(app.port);
    return {...app, name: app.name};
  });
}

/** The demo context for the running apps: the first app's URL and every app's URL by name. */
export function demoContext(apps: Array<{name: string; baseUrl: string}>): DemoContext {
  if (apps.length === 0) {
    throw new Error('Demos need an app or apps entry in capture.ts');
  }
  return {baseUrl: apps[0].baseUrl, baseUrls: Object.fromEntries(apps.map(app => [app.name, app.baseUrl]))};
}
