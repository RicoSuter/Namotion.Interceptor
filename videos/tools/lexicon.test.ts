import {describe, expect, it} from 'vitest';
import {applyLexicon} from './lexicon';

const entries = [
  {match: 'OPC UA', say: 'O P C U A'},
  {match: 'UA', say: 'U A'},
  {match: '°C', say: 'degrees'},
  {match: 'DI', say: 'D I'},
];

describe('applyLexicon', () => {
  it('WhenTermsOverlap_ThenLongestMatchWinsAndReplacementIsNotRescanned', () => {
    // Act
    const spoken = applyLexicon('Connect over OPC UA.', entries);

    // Assert
    expect(spoken).toBe('Connect over O P C U A.');
  });

  it('WhenTermIsPartOfAWord_ThenItIsNotReplaced', () => {
    // Act
    const spoken = applyLexicon('DIRECT DI wiring', entries);

    // Assert
    expect(spoken).toBe('DIRECT D I wiring');
  });

  it('WhenTermStartsWithSymbol_ThenItIsReplaced', () => {
    // Act
    const spoken = applyLexicon('Heating to 93 °C now', entries);

    // Assert
    expect(spoken).toBe('Heating to 93 degrees now');
  });
});
