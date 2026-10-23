import {describe, expect, it} from 'vitest';
import {applyLexicon} from './lexicon';
import {parseVoice} from './voice';

const entries = [
  {match: 'OPC UA', say: 'O P C U A', kokoro: '[OPC UA](/ˈO pˈi sˈi jˈu ˈeɪ/)'},
  {match: 'UA', say: 'U A'},
  {match: '°C', say: 'degrees'},
  {match: 'DI', say: 'D I'},
];

describe('applyLexicon', () => {
  it('WhenTermsOverlap_ThenLongestMatchWinsAndReplacementIsNotRescanned', () => {
    // Act
    const spoken = applyLexicon('Connect over OPC UA.', entries, 'chatterbox');

    // Assert
    expect(spoken).toBe('Connect over O P C U A.');
  });

  it('WhenEntryHasKokoroOverride_ThenKokoroUsesItAndTheOverrideIsNotRescanned', () => {
    // Act
    const spoken = applyLexicon('Connect over OPC UA, then UA alone.', entries, 'kokoro');

    // Assert
    expect(spoken).toBe('Connect over [OPC UA](/ˈO pˈi sˈi jˈu ˈeɪ/), then U A alone.');
  });

  it('WhenEntryHasKokoroOverride_ThenChatterboxUsesThePlainForm', () => {
    // Act
    const spoken = applyLexicon('OPC UA', entries, parseVoice('chatterbox-calm').engine);

    // Assert
    expect(spoken).toBe('O P C U A');
  });

  it('WhenTermIsPartOfAWord_ThenItIsNotReplaced', () => {
    // Act
    const spoken = applyLexicon('DIRECT DI wiring', entries, 'kokoro');

    // Assert
    expect(spoken).toBe('DIRECT D I wiring');
  });

  it('WhenTermStartsWithSymbol_ThenItIsReplaced', () => {
    // Act
    const spoken = applyLexicon('Heating to 93 °C now', entries, 'kokoro');

    // Assert
    expect(spoken).toBe('Heating to 93 degrees now');
  });
});
