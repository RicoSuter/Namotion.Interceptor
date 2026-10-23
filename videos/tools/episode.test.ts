import {cpSync, mkdtempSync, readFileSync, writeFileSync} from 'node:fs';
import {tmpdir} from 'node:os';
import {join} from 'node:path';
import {describe, expect, it} from 'vitest';
import {validateEpisode} from './episode';

const fixture = join(import.meta.dirname, 'fixtures', 'episodes', 'valid');

function copyFixture(change: (script: string) => string): string {
  const directory = mkdtempSync(join(tmpdir(), 'episode-'));
  cpSync(fixture, directory, {recursive: true});
  const scriptFile = join(directory, 'script.yaml');
  writeFileSync(scriptFile, change(readFileSync(scriptFile, 'utf8')));
  return directory;
}

describe('validateEpisode', () => {
  it('WhenAllReferencesExist_ThenReturnsScript', async () => {
    // Act
    const script = await validateEpisode(fixture);

    // Assert
    expect(script.episode).toBe('valid');
  });

  it('WhenRegionIsMissing_ThenThrows', async () => {
    // Arrange
    const directory = copyFixture(script => script.replace('region: Greeting', 'region: Missing'));

    // Act & Assert
    await expect(validateEpisode(directory)).rejects.toThrow(/beat 'code'.*Region 'Missing' not found/);
  });

  it('WhenCodeFileIsMissing_ThenThrows', async () => {
    // Arrange
    const directory = copyFixture(script => script.replace('file: Sample.cs', 'file: Missing.cs'));

    // Act & Assert
    await expect(validateEpisode(directory)).rejects.toThrow(/beat 'code'.*Missing.cs does not exist/);
  });

  it('WhenDemoScriptIsMissing_ThenThrows', async () => {
    // Arrange
    const directory = copyFixture(script => script.replace('demo: home', 'demo: checkout'));

    // Act & Assert
    await expect(validateEpisode(directory)).rejects.toThrow(/beat 'demo'.*demos\/checkout.ts does not exist/);
  });

  it('WhenTerminalCaptureIsNotConfigured_ThenThrows', async () => {
    // Arrange
    const directory = copyFixture(script => script.replace('terminal: version', 'terminal: build'));

    // Act & Assert
    await expect(validateEpisode(directory)).rejects.toThrow(/beat 'shell'.*terminal capture 'build' is not defined in capture.ts/);
  });
});
