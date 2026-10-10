import {describe, expect, it} from 'vitest';
import {cutAfterMatch, runTerminalCapture, stripAnsi} from './terminal';

describe('stripAnsi', () => {
  it('WhenTextHasColorCodes_ThenTheyAreRemoved', () => {
    // Act
    const text = stripAnsi('\u001b[32minfo\u001b[0m: started');

    // Assert
    expect(text).toBe('info: started');
  });
});

describe('runTerminalCapture', () => {
  it('WhenCommandExits_ThenReturnsPromptAndOutput', async () => {
    // Act
    const text = await runTerminalCapture({name: 'echo', command: 'node', args: ['-e', 'console.log("hello")']}, process.cwd());

    // Assert
    expect(text).toBe('$ node -e console.log("hello")\nhello\n');
  });

  it('WhenUntilMatches_ThenLongRunningProcessIsStopped', async () => {
    // Arrange
    const script = 'console.log("Now listening on: http://localhost:5000"); setInterval(() => {}, 1000)';

    // Act
    const text = await runTerminalCapture({name: 'run', command: 'node', args: ['-e', script], until: 'Now listening on', timeoutSeconds: 10}, process.cwd());

    // Assert
    expect(text).toContain('Now listening on: http://localhost:5000');
  });
});

describe('cutAfterMatch', () => {
  it('WhenOutputContinuesAfterTheMatch_ThenLaterLinesAreDropped', () => {
    // Act
    const text = cutAfterMatch('starting\nready on 5000\nshutting down\n', /ready on \d+/);

    // Assert
    expect(text).toBe('starting\nready on 5000\n');
  });

  it('WhenNothingMatches_ThenOutputIsKept', () => {
    // Act
    const text = cutAfterMatch('starting\n', /ready/);

    // Assert
    expect(text).toBe('starting\n');
  });
});
