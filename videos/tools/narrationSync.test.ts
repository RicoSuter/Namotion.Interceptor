import {describe, expect, it} from 'vitest';
import {narrationSyncReport, speechOnset} from './narrationSync';

function tone(rate: number, silentSeconds: number, loudSeconds: number, amplitude = 0.5): Float32Array {
  const samples = new Float32Array(Math.round(rate * (silentSeconds + loudSeconds)));
  for (let index = Math.round(rate * silentSeconds); index < samples.length; index++) {
    samples[index] = amplitude * Math.sin(index / 3);
  }
  return samples;
}

describe('speechOnset', () => {
  it('WhenSoundStartsAfterSilence_ThenReturnsItsStart', () => {
    // Act & Assert
    expect(speechOnset(tone(16000, 0.35, 1), 16000)).toBeCloseTo(0.35);
  });

  it('WhenTheGainDiffers_ThenTheOnsetStaysTheSame', () => {
    // Act & Assert
    expect(speechOnset(tone(16000, 0.35, 1, 0.05), 16000)).toBeCloseTo(0.35);
  });

  it('WhenThereIsOnlySilence_ThenReturnsNull', () => {
    // Act & Assert
    expect(speechOnset(new Float32Array(16000), 16000)).toBeNull();
  });
});

describe('narrationSyncReport', () => {
  it('WhenEveryBeatIsWithinTolerance_ThenReportsInSync', () => {
    // Act
    const report = narrationSyncReport([{id: 'a', videoOnset: 0.35, clipOnset: 0.34}]).join('\n');

    // Assert
    expect(report).toContain('In sync: in all 1 narrated beats');
    expect(report).toContain('largest difference 10 ms');
  });

  it('WhenABeatDriftsOrHasNoSpeech_ThenItIsListed', () => {
    // Act
    const report = narrationSyncReport([
      {id: 'a', videoOnset: 0.35, clipOnset: 0.35},
      {id: 'b', videoOnset: 0.82, clipOnset: 0.35},
      {id: 'c', videoOnset: Number.NaN, clipOnset: 0.3},
    ]).join('\n');

    // Assert
    expect(report).toContain('b (470 ms), c (no speech found)');
    expect(report).not.toContain('a (');
  });
});
