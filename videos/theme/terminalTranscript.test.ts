import {describe, expect, it} from 'vitest';
import {parseTranscript, wrapLines} from './terminalTranscript';

describe('parseTranscript', () => {
  it('WhenTranscriptHasPromptLine_ThenCommandAndOutputAreSplit', () => {
    // Act
    const transcript = parseTranscript('$ dotnet --version\n10.0.100\n\n');

    // Assert
    expect(transcript).toEqual({command: 'dotnet --version', output: ['10.0.100']});
  });

  it('WhenTranscriptUsesCrLf_ThenLinesAreSplitCleanly', () => {
    // Act
    const transcript = parseTranscript('$ run\r\none\r\ntwo\r\n');

    // Assert
    expect(transcript.output).toEqual(['one', 'two']);
  });
});

describe('wrapLines', () => {
  it('WhenLineHasNoSpaces_ThenItIsBrokenAtTheColumn', () => {
    // Act
    const rows = wrapLines(['abcdefgh', 'ab'], 3);

    // Assert
    expect(rows).toEqual(['abc', 'def', 'gh', 'ab']);
  });

  it('WhenLineHasSpaces_ThenItBreaksBetweenWords', () => {
    // Act
    const rows = wrapLines(['info: now listening on http://localhost'], 26);

    // Assert
    expect(rows).toEqual(['info: now listening on', 'http://localhost']);
  });
});
