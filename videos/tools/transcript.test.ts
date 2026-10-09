import {describe, expect, it} from 'vitest';
import {normalizeTranscript, transcriptMatches, transcriptReport} from './transcript';

const lexicon = [
  {match: 'OPC UA', say: 'O P C U A', kokoro: '[OPC UA](/ˈO pˈi sˈi jˈu ˈeɪ/)'},
  {match: '°C', say: 'degrees'},
  {match: 'C#', say: 'C sharp'},
];

describe('normalizeTranscript', () => {
  it('WhenLineHasCasePunctuationAndNumberWords_ThenTheyAreNormalized', () => {
    // Act & Assert
    expect(normalizeTranscript('Heat to Ninety-three, then wait TEN seconds.')).toBe('heat to 93 then wait 10 seconds');
    expect(normalizeTranscript('Twenty cups, one by one')).toBe('20 cups 1 by 1');
  });

  it('WhenLineHasAHomophone_ThenItIsUnified', () => {
    // Act & Assert
    expect(normalizeTranscript('Right the grey value')).toBe(normalizeTranscript('Write the gray value'));
  });
});

describe('transcriptMatches', () => {
  it('WhenTranscriptSplitsOrJoinsWords_ThenItMatches', () => {
    // Act & Assert
    expect(transcriptMatches('Open a WebSocket and call write changes.', 'Open a web socket and call WriteChanges.', lexicon)).toBe(true);
  });

  it('WhenTranscriptHearsTheSpokenForm_ThenItMatches', () => {
    // Act & Assert
    expect(transcriptMatches('Heat to 93 °C in plain C#.', 'Heat to 93 degrees in plain C-sharp.', lexicon)).toBe(true);
    expect(transcriptMatches('Connect over OPC UA.', 'Connect over OPC UA.', lexicon)).toBe(true);
  });

  it('WhenTranscriptHearsAnotherWord_ThenItDoesNotMatch', () => {
    // Act & Assert
    expect(transcriptMatches('The same context and its own machine.', 'The same context in its own machine.', lexicon)).toBe(false);
  });
});

describe('transcriptReport', () => {
  it('WhenSomeBeatsDiffer_ThenOnlyThoseAreListed', () => {
    // Act
    const report = transcriptReport('smoke', [
      {id: 'a', text: 'Hello.', heard: 'Hello.', matches: true},
      {id: 'b', text: 'Set it.', heard: 'Said it.', matches: false},
    ]);

    // Assert
    expect(report).toContain('1 of 2 narrated beats match their transcript.');
    expect(report).toContain('- b\n  - text: Set it.\n  - heard: Said it.');
    expect(report).not.toContain('- a');
  });
});
