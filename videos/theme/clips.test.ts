import {describe, expect, it} from 'vitest';
import {fitClip} from './clips';

describe('fitClip', () => {
  it('WhenClipIsLongerThanBeat_ThenWholeClipSpansBeat', () => {
    // Act
    const fit = fitClip(12, 4);

    // Assert
    expect(fit).toEqual({start: 0, end: 12, rate: 3});
  });

  it('WhenFittingNeedsMoreThanMaximumRate_ThenStartIsTrimmed', () => {
    // Act
    const fit = fitClip(20.9, 3.8);

    // Assert
    expect(fit.rate).toBe(4);
    expect(fit.end).toBe(20.9);
    expect(fit.start).toBeCloseTo(20.9 - 15.2);
  });

  it('WhenClipIsShorterThanBeat_ThenItIsSlowedDown', () => {
    // Act
    const fit = fitClip(3, 4);

    // Assert
    expect(fit).toEqual({start: 0, end: 3, rate: 0.75});
  });

  it('WhenClipIsFarShorterThanBeat_ThenRateStopsAtMinimumAndLastFrameHolds', () => {
    // Act
    const fit = fitClip(1, 4);

    // Assert
    expect(fit).toEqual({start: 0, end: 2, rate: 0.5});
  });

  it('WhenRangeIsGiven_ThenOnlyRangeIsFitted', () => {
    // Act
    const fit = fitClip(30, 5, {from: 10, to: 20});

    // Assert
    expect(fit).toEqual({start: 10, end: 20, rate: 2});
  });

  it('WhenRangeEndsAfterClip_ThenClipEndIsUsed', () => {
    // Act
    const fit = fitClip(8, 4, {to: 50});

    // Assert
    expect(fit).toEqual({start: 0, end: 8, rate: 2});
  });

  it('WhenRangeIsEmpty_ThenThrows', () => {
    // Act & Assert
    expect(() => fitClip(8, 4, {from: 6, to: 6})).toThrow('empty');
  });
});
