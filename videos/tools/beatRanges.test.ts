import {describe, expect, it} from 'vitest';
import type {Timing} from '../theme/timing';
import {beatRanges} from './beatRanges';

const timing: Timing = {
  episode: 'smoke',
  title: 'Smoke',
  totalDuration: 10,
  beats: [
    {id: 'a', chapter: 'one', chapterTitle: 'One', start: 0, duration: 2, audio: null, caption: null},
    {id: 'b', chapter: 'one', chapterTitle: 'One', start: 2, duration: 3, audio: null, caption: null},
    {id: 'c', chapter: 'two', chapterTitle: 'Two', start: 5, duration: 1, audio: null, caption: null},
    {id: 'd', chapter: 'two', chapterTitle: 'Two', start: 6, duration: 4, audio: null, caption: null},
  ],
};

describe('beatRanges', () => {
  it('WhenBeatsAreConsecutive_ThenTheyShareOneRange', () => {
    // Act
    const ranges = beatRanges(timing, ['c', 'b', 'd']);

    // Assert
    expect(ranges).toEqual([{start: 2, end: 10}]);
  });

  it('WhenBeatsAreApart_ThenEachGetsItsRangeInScriptOrder', () => {
    // Act
    const ranges = beatRanges(timing, ['d', 'a']);

    // Assert
    expect(ranges).toEqual([{start: 0, end: 2}, {start: 6, end: 10}]);
  });

  it('WhenBeatIsUnknown_ThenThrows', () => {
    // Act & Assert
    expect(() => beatRanges(timing, ['a', 'missing'])).toThrow(/Unknown beat 'missing'/);
  });
});
