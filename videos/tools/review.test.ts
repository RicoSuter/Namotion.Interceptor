import {describe, expect, it} from 'vitest';
import type {Timing} from '../theme/timing';
import {assignFreezes, beatMidpoints, parseFreezes} from './review';

const timing: Timing = {
  episode: 'smoke',
  totalDuration: 12,
  beats: [
    {id: 'a', chapter: 'one', chapterTitle: 'One', start: 0, duration: 4, audio: null, caption: 'A'},
    {id: 'b', chapter: 'one', chapterTitle: 'One', start: 4, duration: 6, audio: null, caption: 'B'},
    {id: 'c', chapter: 'two', chapterTitle: 'Two', start: 10, duration: 2, audio: null, caption: null},
  ],
};

describe('beatMidpoints', () => {
  it('WhenTimingHasBeats_ThenReturnsTheMiddleOfEach', () => {
    // Act
    const midpoints = beatMidpoints(timing);

    // Assert
    expect(midpoints).toEqual([{id: 'a', time: 2}, {id: 'b', time: 7}, {id: 'c', time: 11}]);
  });
});

describe('parseFreezes', () => {
  it('WhenFreezeHasStartAndEnd_ThenReturnsInterval', () => {
    // Arrange
    const log = [
      '[freezedetect @ 0x1] lavfi.freezedetect.freeze_start: 4.5',
      '[freezedetect @ 0x1] lavfi.freezedetect.freeze_duration: 5',
      '[freezedetect @ 0x1] lavfi.freezedetect.freeze_end: 9.5',
    ].join('\n');

    // Act
    const freezes = parseFreezes(log, 12);

    // Assert
    expect(freezes).toEqual([{start: 4.5, end: 9.5}]);
  });

  it('WhenFreezeRunsToTheEnd_ThenEndIsTheVideoDuration', () => {
    // Act
    const freezes = parseFreezes('lavfi.freezedetect.freeze_start: 10', 12);

    // Assert
    expect(freezes).toEqual([{start: 10, end: 12}]);
  });
});

describe('assignFreezes', () => {
  it('WhenFreezeOverlapsBeats_ThenEachOverlappedBeatIsReported', () => {
    // Act
    const stillBeats = assignFreezes([{start: 4.5, end: 9.5}], timing);

    // Assert
    expect(stillBeats).toEqual([{id: 'b', start: 4.5, end: 9.5}]);
  });

  it('WhenFreezeOnlyTouchesBeatEdge_ThenBeatIsNotReported', () => {
    // Act
    const stillBeats = assignFreezes([{start: 9.95, end: 12}], timing);

    // Assert
    expect(stillBeats).toEqual([{id: 'c', start: 9.95, end: 12}]);
  });
});
