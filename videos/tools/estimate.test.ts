import {describe, expect, it} from 'vitest';
import type {Timing} from '../theme/timing';
import {countWords, durationReport, speechRates} from './estimate';
import {parseScript} from './schema/script';

const script = parseScript(`
episode: smoke
title: Smoke
voice: chatterbox
tempo: 1
chapters:
  - id: one
    title: One
    beats:
      - id: first
        narration: One two three four five six seven.
        visual: Boiler heats.
        components: [CodeCard]
      - id: second
        hold: 2
        visual: Pause.
        components: [CodeCard]
  - id: two
    title: Two
    beats:
      - id: third
        narration: Done.
        visual: Outro.
        components: [ChapterCard]
`);

function timingFor(ids: string[]): Timing {
  return {
    episode: 'smoke',
    title: 'Smoke',
    totalDuration: 9,
    beats: ids.map((id, index) => ({id, chapter: index < 2 ? 'one' : 'two', chapterTitle: '', start: index * 3, duration: 3, audio: null, caption: null})),
  };
}

describe('countWords', () => {
  it('WhenTextHasPunctuationAndExtraSpacing_ThenCountsWords', () => {
    // Act & Assert
    expect(countWords('  Nothing is lost,  while the network is down. ')).toBe(8);
    expect(countWords('')).toBe(0);
  });
});

describe('durationReport', () => {
  it('WhenThereIsNoTiming_ThenEstimatesFromVoiceTempoPaddingAndHolds', () => {
    // Act
    const report = durationReport(script, null);

    // Assert
    expect(report.measured).toBe(false);
    expect(report.words).toBe(8);
    expect(report.chapters[0].seconds).toBeCloseTo(7 / speechRates.chatterbox + 0.4 + 2);
    expect(report.chapters[1].seconds).toBeCloseTo(1 / speechRates.chatterbox + 0.4);
  });

  it('WhenTempoIsFaster_ThenSpeechIsShorter', () => {
    // Act
    const natural = durationReport(script, null);
    const faster = durationReport({...script, voice: 'kokoro:am_michael', tempo: 1.32}, null);

    // Assert
    expect(faster.chapters[1].seconds).toBeCloseTo(1 / (speechRates.kokoro * 1.32) + 0.4);
    expect(natural.seconds).not.toBe(faster.seconds);
  });

  it('WhenTimingHoldsTheScriptBeats_ThenSecondsAreMeasured', () => {
    // Act
    const report = durationReport(script, timingFor(['first', 'second', 'third']));

    // Assert
    expect(report.measured).toBe(true);
    expect(report.chapters.map(chapter => chapter.seconds)).toEqual([6, 3]);
    expect(report.seconds).toBe(9);
  });

  it('WhenTimingIsStale_ThenSecondsAreEstimated', () => {
    // Act
    const report = durationReport(script, timingFor(['first', 'renamed', 'third']));

    // Assert
    expect(report.measured).toBe(false);
  });
});
